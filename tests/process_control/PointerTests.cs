using MemoryStudio;
using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length != 3)
    throw new ArgumentException("Usage: PointerTests <native DLL> <x64 pointer fixture> <x86 pointer fixture>");
string library = Path.GetFullPath(args[0]);
NativeLibrary.SetDllImportResolver(typeof(NativeEngine).Assembly,
    (name, _, _) => name == "memory_core.dll" ? NativeLibrary.Load(library) : 0);
int checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    Console.WriteLine("PASS " + label);
    ++checks;
}
foreach (string fixture in args.Skip(1))
{
    string path = Path.GetFullPath(fixture);
    using var target = Process.Start(new ProcessStartInfo(path)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
    }) ?? throw new InvalidOperationException("Cannot start owned fixture " + path);
    try
    {
        string line = await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException("Fixture did not report its pointer chain.");
        string[] fields = line.Split(' ');
        ulong root = Convert.ToUInt64(fields[2], 16), first = Convert.ToUInt64(fields[3], 16), final = Convert.ToUInt64(fields[4], 16);
        using var engine = new NativeEngine(target.Id);
        var resolver = new AddressResolver(engine, target.Id);
        string expression = resolver.ExpressionFor(root);
        Check(expression.StartsWith('"'), "static root has module expression " + Path.GetFileName(path));
        Check(resolver.Resolve(expression) == root && resolver.Resolve(expression, [32, 64]) == final,
            "quoted module expression and root-first chain " + Path.GetFileName(path));
        Check(resolver.Resolve($"0x{root:X}", [32, 64]) == final, "absolute pointer root " + Path.GetFileName(path));
        var module = resolver.Modules.Single(m => m.Contains(root));
        Check(resolver.Resolve(module.Name.ToUpperInvariant() + "+" + (root - module.BaseAddress).ToString("X")) == root,
            "unquoted case-insensitive module offset");
        Check(resolver.Resolve($"\"{module.Name}\"-0x10") == module.BaseAddress - 16, "module negative offset");
        bool invalid = false;
        try { resolver.Resolve("0xFFFFFFFFFFFFFFFF+1"); }
        catch (OverflowException) { invalid = true; }
        Check(invalid, "address arithmetic overflow rejected");
        var initialHistory = engine.History;
        var report = await PointerScannerService.ScanAsync(engine, target.Id, final, 2, 128, 100, CancellationToken.None);
        Console.WriteLine(report.Summary);
        var found = report.Paths.Single(p => p.RootAddress == root && p.Offsets.SequenceEqual([32, 64]));
        Check(found.ResolvedTarget == final, "reverse scan finds static two-level path");
        Check(engine.History.Generation == initialHistory.Generation && engine.History.UndoCount == initialHistory.UndoCount,
            "pointer scan leaves normal history untouched");
        var kept = await PointerScannerService.RecheckAsync(engine, target.Id, [found], final);
        Check(kept.Paths.Count == 1, "recheck retains valid chain");
        var limited = await PointerScannerService.ScanAsync(engine, target.Id, final, 2, 128, 1, CancellationToken.None);
        Check(limited.Paths.Count == 1 && limited.Truncated && limited.Summary.Contains("截断"), "result cap reports truncation");
        engine.Write(root, resolver.PointerSize == 4 ? BitConverter.GetBytes((uint)(first + 8)) : BitConverter.GetBytes(first + 8));
        kept = await PointerScannerService.RecheckAsync(engine, target.Id, [found], final);
        Check(kept.Paths.Count == 0, "recheck removes invalidated chain");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        bool cancelled = false;
        try { await PointerScannerService.ScanAsync(engine, target.Id, final, 2, 128, 100, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "pointer scan cancellation");
    }
    finally
    {
        if (!target.HasExited) target.Kill();
        await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
}
Console.WriteLine($"{checks} pointer checks passed");
