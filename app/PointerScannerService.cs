using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MemoryStudio;

public sealed record PointerPath(string RootExpression, ulong RootAddress, IReadOnlyList<int> Offsets, ulong ResolvedTarget)
{
    public string OffsetText => string.Join(" → ", Offsets.Select(o => o >= 0 ? $"0x{o:X}" : $"-0x{-(long)o:X}"));
}
public sealed record PointerScanReport(IReadOnlyList<PointerPath> Paths, bool Truncated, ulong ScannedBytes, string Summary);

/// <summary>Bounded reverse pointer search using independent memory reads; never changes scan history.</summary>
public static class PointerScannerService
{
    private const int BlockBytes = 2 * 1024 * 1024, FrontierLimit = 100000;
    private const ulong LayerBudget = 512UL * 1024 * 1024;
    private sealed record Node(ulong Address, int[] Offsets);
    private sealed record Region(ulong Address, ulong End, bool Module);

    public static Task<PointerScanReport> ScanAsync(NativeEngine engine, int pid, ulong target,
        int maxDepth = 3, int maxOffset = 4096, int maxResults = 1000, CancellationToken token = default,
        IProgress<string>? progress = null)
    {
        Validate(engine, pid, target);
        if (maxDepth is < 1 or > 5 || maxOffset is < 0 or > 1048576 || maxResults is < 1 or > 10000)
            throw new ArgumentException("指针扫描深度须为 1–5，偏移为 0–1 MiB，结果上限为 1–10,000。");
        return Task.Run(() => Scan(pid, target, maxDepth, maxOffset, maxResults, token, progress), token);
    }
    public static Task<PointerScanReport> RecheckAsync(NativeEngine engine, int pid,
        IReadOnlyList<PointerPath> paths, ulong target, CancellationToken token = default)
    {
        Validate(engine, pid, target); ArgumentNullException.ThrowIfNull(paths);
        PointerPath[] snapshot = paths.Take(10001).ToArray();
        if (snapshot.Length > 10000) throw new ArgumentException("重新校验最多支持 10,000 条指针链。");
        return Task.Run(() =>
        {
            using var reader = new NativeEngine(pid); var resolver = new AddressResolver(reader, pid); var kept = new List<PointerPath>();
            foreach (var path in snapshot)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    ulong root = resolver.Resolve(path.RootExpression), resolved = resolver.Resolve(path.RootExpression, path.Offsets);
                    if (resolved == target) kept.Add(path with { RootAddress = root, ResolvedTarget = resolved });
                }
                catch (ObjectDisposedException) { throw; }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException or Win32Exception) { }
            }
            return new PointerScanReport(kept, false, 0, $"已重新校验 {snapshot.Length:N0} 条链，保留 {kept.Count:N0} 条仍指向 0x{target:X} 的结果。");
        }, token);
    }
    private static void Validate(NativeEngine engine, int pid, ulong target)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pid <= 0 || engine.ProcessId != pid || target == 0) throw new ArgumentException("指针扫描需要当前进程的有效目标地址。");
    }
    private static PointerScanReport Scan(int pid, ulong target, int maxDepth, int maxOffset, int maxResults,
        CancellationToken token, IProgress<string>? progress)
    {
        using var reader = new NativeEngine(pid); var resolver = new AddressResolver(reader, pid);
        int width = resolver.PointerSize;
        if (width == 4 && target > uint.MaxValue) throw new ArgumentException("目标地址超出 32 位进程地址空间。");
        using var process = Native.OpenProcess(0x00100400, false, (uint)pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var modules = resolver.Modules;
        var regions = Enumerate(process, width, modules, token).OrderByDescending(r => r.Module).ThenBy(r => r.Address).ToArray();
        var frontier = new List<Node> { new(target, []) }; var paths = new List<PointerPath>(); var seen = new HashSet<string>();
        bool truncated = false; ulong scanned = 0; int skippedPages = 0, layers = 0;
        for (int depth = 1; depth <= maxDepth && frontier.Count > 0; ++depth)
        {
            token.ThrowIfCancellationRequested(); ++layers;
            Node[] sorted = frontier.OrderBy(n => n.Address).ToArray(); var next = new List<Node>(); var nextSeen = new HashSet<string>();
            ulong lower = sorted[0].Address >= (ulong)maxOffset ? sorted[0].Address - (ulong)maxOffset : 0;
            ulong highest = sorted[^1].Address, examined = 0;
            progress?.Report($"第 {depth}/{maxDepth} 层，{frontier.Count:N0} 个候选节点；优先搜索模块，每层读取范围预算 512 MiB。");
            bool stopLayer = false;
            foreach (var region in regions)
            {
                for (ulong address = region.Address; address < region.End;)
                {
                    token.ThrowIfCancellationRequested();
                    if (examined >= LayerBudget) { truncated = true; stopLayer = true; break; }
                    int size = (int)Math.Min((ulong)BlockBytes, Math.Min(region.End - address, LayerBudget - examined));
                    examined += (ulong)size;
                    foreach (var chunk in ReadChunks(reader, process, address, size, token, () => ++skippedPages))
                    {
                        scanned += (ulong)chunk.Bytes.Length;
                        int offset = (int)((ulong)width - chunk.Address % (ulong)width) % width;
                        for (int at = offset; at + width <= chunk.Bytes.Length; at += width)
                        {
                            if ((at & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                            ulong pointed = width == 4 ? BitConverter.ToUInt32(chunk.Bytes, at) : BitConverter.ToUInt64(chunk.Bytes, at);
                            if (pointed == 0 || pointed < lower || pointed > highest) continue;
                            ulong last = pointed <= ulong.MaxValue - (ulong)maxOffset ? pointed + (ulong)maxOffset : ulong.MaxValue;
                            int first = LowerBound(sorted, pointed);
                            for (int hit = first; hit < sorted.Length && sorted[hit].Address <= last; ++hit)
                            {
                                ulong source = chunk.Address + (ulong)at;
                                if (source == sorted[hit].Address) continue; // No direct self-pointer cycles.
                                int[] offsets = [(int)(sorted[hit].Address - pointed), .. sorted[hit].Offsets];
                                string key = source.ToString("X") + ":" + string.Join(',', offsets);
                                var module = modules.FirstOrDefault(m => m.Contains(source));
                                if (module is not null && seen.Add(key))
                                {
                                    string expression = $"\"{module.Name}\"+0x{source - module.BaseAddress:X}";
                                    // Re-read a complete chain before emitting: the target may change during scanning.
                                    try { if (resolver.Resolve(expression, offsets) == target) paths.Add(new(expression, source, offsets, target)); }
                                    catch (InvalidOperationException) { }
                                    if (paths.Count >= maxResults) { truncated = true; stopLayer = true; break; }
                                }
                                if (depth < maxDepth)
                                {
                                    if (next.Count >= FrontierLimit) { truncated = true; stopLayer = true; break; }
                                    if (nextSeen.Add(key)) next.Add(new(source, offsets));
                                }
                            }
                            if (stopLayer) break;
                        }
                        if (stopLayer) break;
                    }
                    address += (ulong)size;
                    if (stopLayer) break;
                }
                if (stopLayer) break;
            }
            if (paths.Count >= maxResults) break;
            frontier = next;
        }
        string limits = truncated ? "结果已截断（结果上限、每层 512 MiB 读取范围预算或 100,000 节点上限）；不能视为穷尽搜索。" : "已完成指定深度内的有界搜索。";
        string summary = $"找到 {paths.Count:N0} 条模块根指针链；搜索 {layers} 层，实际读取 {scanned / 1048576.0:N1} MiB，跳过 {skippedPages:N0} 个不可读/追踪保护页。{limits} 偏移按根到目标排列，只搜索自然对齐指针和非负偏移；进程变化后请重新校验。";
        return new(paths, truncated, scanned, summary);
    }
    private static int LowerBound(Node[] nodes, ulong value)
    {
        int lo = 0, hi = nodes.Length;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (nodes[mid].Address < value) lo = mid + 1; else hi = mid; }
        return lo;
    }
    private static IEnumerable<(ulong Address, byte[] Bytes)> ReadChunks(NativeEngine reader, SafeProcessHandle process,
        ulong address, int size, CancellationToken token, Action skipped)
    {
        byte[]? bytes = null;
        try { bytes = reader.Read(address, size); } catch (InvalidOperationException) { }
        if (bytes is not null) { yield return (address, bytes); yield break; }
        if (Native.WaitForSingleObject(process, 0) != 258) throw new InvalidOperationException("指针扫描期间目标进程已退出。");
        ulong end = address + (ulong)size;
        while (address < end)
        {
            token.ThrowIfCancellationRequested(); int page = Environment.SystemPageSize - (int)(address % (ulong)Environment.SystemPageSize);
            int take = (int)Math.Min((ulong)page, end - address); bytes = null;
            try { bytes = reader.Read(address, take); } catch (InvalidOperationException) { skipped(); }
            if (bytes is not null) yield return (address, bytes);
            address += (ulong)take;
        }
    }
    private static IEnumerable<Region> Enumerate(SafeProcessHandle process, int width, IReadOnlyList<ProcessModuleInfo> modules, CancellationToken token)
    {
        ulong cursor = 0, maximum = width == 4 ? 0x100000000UL : 0x0000800000000000UL;
        while (cursor < maximum)
        {
            token.ThrowIfCancellationRequested();
            if (Native.VirtualQueryEx(process, (nint)cursor, out var info, (nuint)Marshal.SizeOf<MemoryRegion>()) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (Native.WaitForSingleObject(process, 0) != 258) throw new InvalidOperationException("目标进程已经退出。");
                if (error == 87) yield break;
                throw new Win32Exception(error, "无法枚举指针扫描的内存区域。");
            }
            ulong start = (ulong)info.BaseAddress, bytes = (ulong)info.RegionSize;
            if (bytes == 0 || start > ulong.MaxValue - bytes || start + bytes <= cursor) throw new InvalidOperationException("目标返回了无效内存区域。");
            ulong end = Math.Min(maximum, start + bytes);
            if (info.State == 0x1000 && (info.Protect & 0x101) == 0 && (info.Protect & 0xFF) is 2 or 4 or 8 or 0x20 or 0x40 or 0x80)
                yield return new(Math.Max(cursor, start), end, modules.Any(m => m.Contains(start)));
            cursor = end;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryRegion
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect, Padding;
        public nuint RegionSize;
        public uint State, Protect, Type, Padding2;
    }
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryRegion info, nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    }
}
