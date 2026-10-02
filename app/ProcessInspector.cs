using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MemoryStudio;

public sealed record ProcessArchitectureInfo(int? Bitness, string Label, string? Warning);
public sealed record ProcessModuleInfo(string Name, ulong BaseAddress, ulong Size, ulong? EntryPoint)
{
    public string DisplayName => $"{Name}  ·  0x{BaseAddress:X16}  ·  {Size / 1024:N0} KB";
    public bool Contains(ulong address) => address >= BaseAddress && address - BaseAddress < Size;
}
public sealed record ProcessInspection(ProcessArchitectureInfo Architecture, IReadOnlyList<ProcessModuleInfo> Modules, string? Warning);

public static class ProcessInspector
{
    public static ProcessArchitectureInfo DetectArchitecture(int processId)
    {
        using var handle = OpenProcess(0x1000, false, (uint)processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法查询目标进程的位数。");
        try
        {
            if (!IsWow64Process2(handle, out ushort processMachine, out ushort nativeMachine))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "目标进程的架构查询失败。");
            ushort machine = processMachine == 0 ? nativeMachine : processMachine;
            return machine switch
            {
                0x014C => new(32, "x86 · 32 位", null),
                0x8664 => new(64, "x64 · 64 位", null),
                0xAA64 or 0xA641 or 0xA64E => new(null, "ARM64", "目标架构为 ARM64；当前查看器只解码 x86/x64。请确认字节属于 x86/x64 后手动选择模式。"),
                0x01C0 or 0x01C4 => new(null, "ARM · 32 位", "目标是 ARM 指令集，当前查看器只支持 x86/x64。"),
                _ => new(null, $"未知架构 0x{machine:X4}", "无法自动确定 x86/x64 模式，请手动选择位数。")
            };
        }
        catch (EntryPointNotFoundException)
        {
            if (!IsWow64Process(handle, out bool wow64)) throw new Win32Exception(Marshal.GetLastWin32Error(), "目标进程的位数查询失败。");
            if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                return new(null, "旧系统架构查询", "旧版系统无法可靠区分 ARM 与 x86/x64，请手动选择位数。");
            int bitness = wow64 || !Environment.Is64BitOperatingSystem ? 32 : 64;
            return new(bitness, bitness == 32 ? "x86 · 32 位" : "x64 · 64 位", "已使用旧版 IsWow64Process 检测。");
        }
    }

    public static ProcessInspection Inspect(int processId, NativeEngine engine)
    {
        ProcessArchitectureInfo architecture;
        string? warning = null;
        try { architecture = DetectArchitecture(processId); }
        catch (Win32Exception ex) { architecture = new(null, "架构查询失败", ex.Message); }
        var modules = new List<ProcessModuleInfo>();
        using var process = Process.GetProcessById(processId);
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    ulong baseAddress = unchecked((ulong)module.BaseAddress.ToInt64());
                    ulong size = (ulong)Math.Max(0, module.ModuleMemorySize);
                    if (baseAddress == 0 || size == 0) continue;
                    ulong? entry = ReadEntryPoint(engine, baseAddress, size);
                    if (entry == null)
                    {
                        try
                        {
                            ulong candidate = unchecked((ulong)module.EntryPointAddress.ToInt64());
                            if (candidate >= baseAddress && candidate - baseAddress < size) entry = candidate;
                        }
                        catch (Win32Exception) { }
                    }
                    modules.Add(new ProcessModuleInfo(module.ModuleName, baseAddress, size, entry));
                }
            }
        }
        catch (Win32Exception ex) { warning = $"模块列表读取不完整：{ex.Message}。可继续使用地址输入。"; }
        catch (ObjectDisposedException) { throw; }
        catch (InvalidOperationException ex) { warning = $"目标进程或模块已经变化：{ex.Message}"; }
        return new ProcessInspection(architecture, modules, warning);
    }

    private static ulong? ReadEntryPoint(NativeEngine engine, ulong baseAddress, ulong moduleSize)
    {
        try
        {
            byte[] dos = engine.Read(baseAddress, 64);
            if (BitConverter.ToUInt16(dos) != 0x5A4D) return null;
            int peOffset = BitConverter.ToInt32(dos, 0x3C);
            if (peOffset < 64 || peOffset > 1_048_576 || (ulong)peOffset + 44 > moduleSize) return null;
            byte[] header = engine.Read(baseAddress + (ulong)peOffset, 44);
            if (BitConverter.ToUInt32(header) != 0x00004550 || BitConverter.ToUInt16(header, 20) < 20) return null;
            ushort magic = BitConverter.ToUInt16(header, 24);
            if (magic is not (0x10B or 0x20B)) return null;
            uint entryRva = BitConverter.ToUInt32(header, 40);
            return entryRva != 0 && entryRva < moduleSize && baseAddress <= ulong.MaxValue - entryRva ? baseAddress + entryRva : null;
        }
        catch (ObjectDisposedException) { throw; }
        catch (InvalidOperationException) { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);
}
