using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace MemoryStudio;

public static class Program
{
    static string Root = "";
    static string OutputDirectory = "";
    static int checks;
    static readonly List<string> Lines = new();
    [DllImport("kernel32.dll",SetLastError=true)] static extern nint VirtualAlloc(nint address,nuint bytes,uint allocation,uint protection);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool VirtualFree(nint address,nuint bytes,uint type);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool VirtualProtect(nint address,nuint bytes,uint protection,out uint old);
    [DllImport("kernel32.dll",SetLastError=true)] static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll",SetLastError=true)] static extern nint VirtualAllocEx(nint process,nint address,nuint bytes,uint allocation,uint protection);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool VirtualFreeEx(nint process,nint address,nuint bytes,uint type);
    static void Check(bool value,string text) { if(!value) throw new Exception("FAIL: "+text); ++checks; Lines.Add("PASS "+text); Console.WriteLine(Lines[^1]); }
    static T Field<T>(object value,string name) => (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    static async Task Wait(Func<bool> predicate,string reason,int ms=6000) { var t=Stopwatch.StartNew(); while(!predicate()) { if(t.ElapsedMilliseconds>ms) throw new Exception("TIMEOUT "+reason); await Task.Delay(30); } }
    static void Save(Window window,string name)
    {
        window.UpdateLayout(); var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(Path.Combine(OutputDirectory,name)); png.Save(stream);
    }
    [STAThread] public static void Main(string[] args)
    {
        Root=Path.GetFullPath(args.Length>0?args[0]:Directory.GetCurrentDirectory());
        OutputDirectory=Path.GetFullPath(args.Length>1?args[1]:Path.Combine(Root,"artifacts","browser-windows"));
        if(!File.Exists(Path.Combine(Root,"app","MemoryStudio.csproj"))) throw new ArgumentException("Pass the MemoryStudio repository root as the first argument.");
        Directory.CreateDirectory(OutputDirectory);
        if(args.Contains("--history-overlap-only"))
        {
            try { RunHistoryOverlap(); Lines.Add($"ALL {checks} PASSED"); File.WriteAllLines(Path.Combine(OutputDirectory,"browser-window-results.txt"),Lines); }
            catch(Exception ex) { Lines.Add(ex.ToString()); Console.Error.WriteLine(ex); File.WriteAllLines(Path.Combine(OutputDirectory,"browser-window-results.txt"),Lines); Environment.ExitCode=1; }
            return;
        }
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("Theme.xaml",UriKind.Relative) });
        app.Startup+=async (_,_)=>
        {
            try { await Run(); Lines.Add($"ALL {checks} PASSED"); File.WriteAllLines(Path.Combine(OutputDirectory,"browser-window-results.txt"),Lines); app.Shutdown(0); }
            catch(Exception ex) { Lines.Add(ex.ToString()); Console.Error.WriteLine(ex); File.WriteAllLines(Path.Combine(OutputDirectory,"browser-window-results.txt"),Lines); app.Shutdown(1); }
        };
        app.Run();
    }
    static async Task CompleteHexDialog() { await Task.Delay(100); var editor=Application.Current.Windows.OfType<MemoryBytesEditor>().Single(); Field<TextBox>(editor,"_input").Text="7F"; var actions=((Grid)editor.Content).Children.OfType<StackPanel>().Last(); actions.Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    static void RunHistoryOverlap()
    {
        nint block=VirtualAlloc(0,8192,0x3000,4); if(block==0) throw new Exception("VirtualAlloc failed");
        var history=MemoryEditHistory.ForProcess(Environment.ProcessId);
        using var browser=new NativeEngine(Environment.ProcessId);
        using var main=new NativeEngine(Environment.ProcessId);
        ulong address=(ulong)block.ToInt64();
        var watch=new WatchRow { Address=address,Type=8,Size=6,FrozenValue=new byte[6],ValueText="00 00 00 00 00 00",IsFrozen=true };
        history.TrackWatch(watch);
        try
        {
            using(history.BeginBatch("overlapping byte patches"))
            {
                history.Write(browser,address,[0x11,0x11,0x11,0x11],"earlier overlapping patch");
                history.Write(browser,address+2,[0x22,0x22,0x22,0x22],"later overlapping patch");
                history.Write(browser,address+4096,[0x33,0x33,0x33,0x33],"independent patch");
            }
            Check(watch.FrozenValue.SequenceEqual(new byte[]{0x11,0x11,0x22,0x22,0x22,0x22}),"overlapping browser writes update entire frozen watch span");
            if(!VirtualProtect(block,4096,2,out _)) throw new Exception("VirtualProtect readonly failed");
            var partial=history.Undo(main);
            Check(partial.Attempted==3&&partial.Restored==1&&partial.Errors.Count==2&&history.CanUndo&&main.Read(address+4096,4).All(b=>b==0),"failed overlap undo restores independent range and retains dependent steps");
            Check(main.Read(address,6).SequenceEqual(new byte[]{0x11,0x11,0x22,0x22,0x22,0x22}),"earlier overlap does not overwrite later patch whose undo failed");
            if(!VirtualProtect(block,4096,4,out _)) throw new Exception("VirtualProtect writable failed");
            var retry=history.Undo(browser);
            Marshal.WriteByte(block,0x66); history.MaintainFrozen(main,watch);
            Check(retry.Attempted==2&&retry.Restored==2&&retry.Errors.Count==0&&!history.CanUndo&&main.Read(address,6).All(b=>b==0)&&watch.IsFrozen&&watch.FrozenValue.All(b=>b==0),"retry restores original overlap bytes and serialized freeze retains restored baseline");
        }
        finally { history.UntrackWatch(watch); VirtualFree(block,0,0x8000); }
    }
    static async Task Run()
    {
        var uncertain=DisassemblyService.DecodeBefore([0x90,0xB8,0x2A,0,0,0,0xC3],0x1000,0x1007,64,false);
        Check(uncertain.All(r=>r.IsBoundaryUncertain),"inferred reverse rows are explicitly uncertain");
        var anchored=DisassemblyService.DecodeBefore([0x90,0xB8,0x2A,0,0,0,0xC3],0x1000,0x1007,64,true);
        Check(anchored.Select(r=>r.Length).SequenceEqual(new[]{1,5,1}) && anchored.All(r=>!r.IsBoundaryUncertain),"known reverse anchor uses real variable length boundaries");
        Check(MemoryBytesEditor.ParseBytes("0x90\n90").SequenceEqual(new byte[]{0x90,0x90}),"hex editor parses bytes directly");
        bool invalid=false; try { MemoryBytesEditor.ParseBytes("90 & calc"); } catch(ArgumentException) { invalid=true; } Check(invalid,"hex editor rejects commands and malformed bytes");
        Check(AssemblyService.Assemble("mov eax, 42\nret",0x100000,64).SequenceEqual(new byte[]{0xB8,42,0,0,0,0xC3}),"NASM assembles real instruction bytes");
        var jump=AssemblyService.Assemble("jmp 0x100010",0x100000,64); Check(DisassemblyService.Decode(jump,0x100000,64)[0].BranchTarget==0x100010,"NASM ORG preserves relative branch target");
        nint memory=VirtualAlloc(0,65536,0x3000,0x40); if(memory==0) throw new Exception("VirtualAlloc failed");
        try
        {
            byte[] block=Enumerable.Repeat((byte)0x90,65536).ToArray(); Marshal.Copy(block,0,memory,block.Length);
            ulong address=(ulong)memory.ToInt64()+32768;
            Marshal.Copy(new byte[]{0xB8,42,0,0,0,0xC3},0,(nint)(long)address,6);
            using var engine=new NativeEngine(Environment.ProcessId);
            var window=new DisassemblyWindow(engine,Environment.ProcessId,address); window.Show();
            await Wait(()=>Field<System.Collections.ObjectModel.ObservableCollection<DisassemblyRow>>(window,"_rows").Count>0 && !Field<bool>(window,"_loading"),"initial disassembly");
            var rows=Field<System.Collections.ObjectModel.ObservableCollection<DisassemblyRow>>(window,"_rows");
            var grid=Field<DataGrid>(window,"InstructionGrid"); var selected=(DisassemblyRow)grid.SelectedItem;
            Check(selected.Address==address && selected.Instruction.Contains("mov"),"specified IP selected in centered real disassembly window");
            Check(rows.Any(r=>r.Address<address && r.IsBoundaryUncertain),"initial reverse area visibly marked uncertain");
            var scroll=Field<ScrollViewer>(window,"_scroll"); double initialOffset=scroll.VerticalOffset;
            int initialCount=rows.Count; ulong initialStart=rows[0].Address;
            await window.LoadAdjacentAsync(true);
            Check(rows.Count>initialCount && rows[0].Address<initialStart && ReferenceEquals(grid.SelectedItem,selected),"prepend preserves selected instruction");
            await Task.Delay(200); Lines.Add($"OFFSET old={initialOffset} new={scroll.VerticalOffset} inserted={rows.Count-initialCount} viewport={scroll.ViewportHeight} extent={scroll.ExtentHeight}"); Check(Math.Abs(scroll.VerticalOffset-initialOffset-(rows.Count-initialCount))<1.1,"prepend preserves visible scroll anchor");
            ulong priorEnd=rows[^1].NextAddress; initialCount=rows.Count;
            await window.LoadAdjacentAsync(false);
            Check(rows.Count>initialCount && rows.Any(r=>r.Address==priorEnd) && ReferenceEquals(grid.SelectedItem,selected),"append starts at exact prior instruction end and preserves selection");
            Check(rows.Zip(rows.Skip(1)).All(pair=>pair.First.NextAddress==pair.Second.Address),"all appended and prepended rows remain contiguous");
            var suppress=typeof(DisassemblyWindow).GetField("_suppressScroll",BindingFlags.Instance|BindingFlags.NonPublic)!; suppress.SetValue(window,true); scroll.ScrollToTop(); await Task.Delay(100); suppress.SetValue(window,false); ulong wheelStart=rows[0].Address; grid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120) { RoutedEvent=UIElement.PreviewMouseWheelEvent }); await Wait(()=>rows[0].Address<wheelStart&&!Field<bool>(window,"_loading"),"up wheel edge loading"); Check(rows[0].Address<wheelStart,"real upward wheel event prepends adjacent code"); suppress.SetValue(window,true); scroll.ScrollToBottom(); await Task.Delay(100); suppress.SetValue(window,false); ulong wheelEnd=rows[^1].NextAddress; grid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120) { RoutedEvent=UIElement.PreviewMouseWheelEvent }); await Wait(()=>rows[^1].NextAddress>wheelEnd&&!Field<bool>(window,"_loading"),"down wheel edge loading"); Check(rows[^1].NextAddress>wheelEnd,"real downward wheel event appends adjacent code"); grid.ScrollIntoView(selected); await Task.Delay(100); Save(window,"disassembly-browser-window.png");
            // Select a real row, change its machine code on an RX page through the actual NOP button.
            VirtualProtect((nint)(long)address,4096,0x20,out _);
            grid.SelectedItem=selected; Field<Button>(window,"NopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200); Console.WriteLine("PATCH_STATUS "+Field<TextBlock>(window,"StatusLabel").Text); await Wait(()=>!Field<bool>(window,"_loading") && engine.Read(address,5).All(b=>b==0x90),"NOP patch");
            Check(MemoryEditHistory.ForProcess(Environment.ProcessId).CanUndo,"NOP uses shared edit history on read execute page");
            Field<Button>(window,"UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>!Field<bool>(window,"_loading") && engine.Read(address,5)[0]==0xB8,"undo NOP");
            Check(engine.Read(address,6).SequenceEqual(new byte[]{0xB8,42,0,0,0,0xC3}),"code undo restores original machine code");
            var hexEngine=new NativeEngine(Environment.ProcessId);
            var hex=new HexViewerWindow(hexEngine,address+4096,Environment.ProcessId,ownsEngine:true); hex.Show();
            await Wait(()=>Field<DataGrid>(hex,"HexGrid").Items.Count==64 && !Field<bool>(hex,"_reading"),"hex real read");
            var hexGrid=Field<DataGrid>(hex,"HexGrid"); var first=(HexViewerWindow.HexRow)hexGrid.Items[0];
            Check(first.RawAddress==address+4096 && first.RawBytes![0]==0x90 && Field<Button>(hex,"EditByteButton").IsEnabled,"HEX byte selection permits editing on live process");
            Save(hex,"hex-browser-window.png"); var editDialogTask=CompleteHexDialog(); Field<Button>(hex,"EditByteButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await editDialogTask; await Wait(()=>!Field<bool>(hex,"_reading") && engine.Read(address+4096,1)[0]==0x7F,"HEX real write"); Check(engine.Read(address+4096,1)[0]==0x7F,"HEX dialog writes actual selected byte"); Field<Button>(hex,"UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(()=>!Field<bool>(hex,"_reading") && engine.Read(address+4096,1)[0]==0x90,"HEX real undo"); Check(engine.Read(address+4096,1)[0]==0x90,"HEX undo restores actual byte through shared history"); hex.Close();
            bool disposed=false; try { hexEngine.Read(address,1); } catch(ObjectDisposedException) { disposed=true; } Check(disposed,"owned HEX session disposed when real window closes");
            window.Close();
        }
        finally { VirtualFree(memory,0,0x8000); }
        var start=new ProcessStartInfo(Path.Combine(Root,"artifacts","native","DemoTarget.exe")) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true }; start.ArgumentList.Add("--trace-test");
        using var demo=Process.Start(start)!;
        try
        {
            string line=await demo.StandardOutput.ReadLineAsync()??throw new Exception("Missing target info"); string[] info=line.Split(' '); int pid=int.Parse(info[1]); ulong watch=ulong.Parse(info[2],NumberStyles.HexNumber);
            _=await demo.StandardOutput.ReadLineAsync();
            var trace=new AccessTraceWindow(pid,watch,4,false); trace.Show();
            Field<Button>(trace,"StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var hits=Field<DataGrid>(trace,"HitGrid"); await Wait(()=>hits.Items.Count>0,"trace actual hits");
            hits.SelectedIndex=0; var hit=(AccessTraceHit)hits.SelectedItem; ulong hitIP=hit.InstructionPointer;
            hits.ScrollIntoView(hit); hits.UpdateLayout();
            var row=(DataGridRow)hits.ItemContainerGenerator.ContainerFromItem(hit);
            hits.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left) { RoutedEvent=Control.MouseDoubleClickEvent,Source=row });
            await Wait(()=>trace.OwnedWindows.OfType<DisassemblyWindow>().Any() && trace.OwnedWindows.OfType<HexViewerWindow>().Any(),"double click opens both browsers");
            var service=Field<AccessTraceService>(trace,"_service");
            Check(!service.State.IsAttached && !service.State.IsRunning && hits.Items.Count>0,"source double click safely detaches and preserves captured hits");
            var code=trace.OwnedWindows.OfType<DisassemblyWindow>().Single(); var data=trace.OwnedWindows.OfType<HexViewerWindow>().Single();
            await Wait(()=>Field<DataGrid>(code,"InstructionGrid").SelectedItem is DisassemblyRow r && r.Address==hitIP && !Field<bool>(code,"_loading"),"source exact IP decode");
            await Wait(()=>Field<DataGrid>(data,"HexGrid").Items.Count>0 && !Field<bool>(data,"_reading"),"source data memory");
            Check(((DisassemblyRow)Field<DataGrid>(code,"InstructionGrid").SelectedItem).Address==hitIP,"source captured IP is exact selected real instruction");
            Check(((HexViewerWindow.HexRow)Field<DataGrid>(data,"HexGrid").Items[0]).RawAddress==(hit.Latest.MemoryAddress&~15UL),"source data browser opens real operand memory");
            Check(!Field<Button>(trace,"StartButton").IsEnabled,"source debugger cannot restart over its own live memory browsers");
            Save(trace,"trace-source-window.png");
            var codeEngine=Field<NativeEngine>(code,"_engine"); var dataEngine=Field<NativeEngine>(data,"_engine"); code.Close(); data.Close();
            bool codeClosed=false,dataClosed=false; try {codeEngine.Read(hitIP,1);} catch(ObjectDisposedException){codeClosed=true;} try{dataEngine.Read(watch,1);}catch(ObjectDisposedException){dataClosed=true;}
            Check(codeClosed&&dataClosed&&Field<Button>(trace,"StartButton").IsEnabled,"source browser sessions close and restart becomes available");
            trace.Close(); await Wait(()=>!trace.IsVisible,"safe trace close");
            var neverStarted=new AccessTraceWindow(pid,watch,4,true); neverStarted.Show(); Check(await neverStarted.CloseSafelyAsync()&&!neverStarted.IsVisible,"never started trace closes safely without reentrant WPF Close");
            using var vm=new MainViewModel(); var main=new MainWindow { DataContext=vm }; Application.Current.MainWindow=main; main.Show(); await vm.AttachToProcessAsync(pid);
            Check(vm.IsAttached&&!vm.IsBusy,"MainVM attaches actual demo target");
            Field<DispatcherTimer>(vm,"_watchTimer").Stop(); var runningJob=Field<Task?>(vm,"_watchJob"); if(runningJob!=null) await runningJob;
            var vmEngine=Field<NativeEngine>(vm,"_engine"); var process=OpenProcess(0x438,false,(uint)pid); if(process==0) throw new Exception("OpenProcess remote allocation failed"); var remote=VirtualAllocEx(process,0,4096,0x3000,4); if(remote==0) throw new Exception("VirtualAllocEx failed");
            try
            {
                ulong unrelatedAddress=(ulong)remote.ToInt64(); vmEngine.Write(unrelatedAddress,BitConverter.GetBytes(111));
                var guarded=new WatchRow { Address=watch,Type=2,Size=4,FrozenValue=BitConverter.GetBytes(-777),ValueText="-777",IsFrozen=true,Description="guard freeze test" };
                var unrelated=new WatchRow { Address=unrelatedAddress,Type=2,Size=4,FrozenValue=BitConverter.GetBytes(111),ValueText="111",Description="other page" };
                vm.Watches.Add(guarded); vm.Watches.Add(unrelated);
                var openTask=vm.HandleRecordActionAsync(guarded,"trace-write"); Check(openTask.IsCompleted&&!vm.IsBusy&&main.IsEnabled,"MainVM trace action immediately returns and keeps main window enabled"); await openTask;
                var modeless=main.OwnedWindows.OfType<AccessTraceWindow>().Single(); Field<Button>(modeless,"StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(()=>modeless.HasProtectedPages&&!Field<bool>(modeless,"_transition"),"modeless trace starts");
                await Wait(()=>Field<DataGrid>(modeless,"HitGrid").Items.Count>0,"modeless write hit");
                await vm.RefreshLiveValuesAsync(); Check(guarded.IsFrozen&&guarded.ValueText=="-777","guarded freeze watch is skipped without being cleared");
                await vm.WriteRecordsValueAsync(new object[]{unrelated},"4321"); await vm.RefreshLiveValuesAsync(); Check(unrelated.ValueText=="4321"&&BitConverter.ToInt32(vmEngine.Read(unrelatedAddress,4))==4321&&main.IsEnabled&&!vm.IsBusy,"main edits and refreshes unrelated watch while trace stays active");
                bool guardedRead=false; try {vmEngine.Read(watch,4);}catch(InvalidOperationException){guardedRead=true;} Check(guardedRead,"ordinary engine refuses protected trace page without consuming guard");
                Field<Button>(modeless,"StopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(()=>!modeless.HasProtectedPages,"modeless stop releases page");
                int afterStop=BitConverter.ToInt32(vmEngine.Read(watch,4)); Check(afterStop!=-777,"skipped guarded freeze never writes sentinel during active capture");
                guarded.IsFrozen=false; await vm.RefreshLiveValuesAsync(); Check(guarded.ValueText!="-777"&&guarded.ValueText!="不可读取","Stop with retained results resumes guarded watch refresh");
                Check(await modeless.CloseSafelyAsync(),"modeless close stops and releases trace"); await vm.RefreshLiveValuesAsync(); Check(!Field<List<(Window,ulong,ulong)>>(vm,"_traces").Any()&&guarded.ValueText!="不可读取","MainVM removes closed trace and continues watch refresh");
                await vm.HandleRecordActionAsync(guarded,"trace-write"); var exitTrace=main.OwnedWindows.OfType<AccessTraceWindow>().Single(); Field<Button>(exitTrace,"StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Wait(()=>exitTrace.HasProtectedPages&&!Field<bool>(exitTrace,"_transition"),"exit trace starts"); main.Close(); await Wait(()=>!main.IsVisible&&!exitTrace.IsVisible,"main owner safe close"); Check(!exitTrace.HasProtectedPages&&vmEngine.Read(watch,4).Length==4,"closing main owner awaits safe trace detach and page restoration");
            }
            finally { VirtualFreeEx(process,remote,0,0x8000); CloseHandle(process); if(main.IsVisible) main.Close(); }
        }
        finally { if(!demo.HasExited) demo.Kill(true); }
    }
}
