using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MemoryStudio;

/// <summary>Resolves absolute/module addresses and CE chains with root-first offsets.</summary>
public sealed class AddressResolver
{
    private readonly NativeEngine _engine;
    private readonly int _processId;
    private readonly object _gate = new();
    private IReadOnlyList<ProcessModuleInfo>? _modules;
    private long _moduleTick;
    public int PointerSize { get; }
    public AddressResolver(NativeEngine engine, int pid)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pid <= 0 || engine.ProcessId != pid) throw new ArgumentException("地址解析器与内存会话必须属于同一进程。");
        _engine = engine; _processId = pid;
        int? bitness = ProcessInspector.DetectArchitecture(pid).Bitness;
        PointerSize = bitness switch { 32 => 4, 64 => 8, _ => throw new InvalidOperationException("无法确定目标的 x86/x64 指针宽度。") };
    }
    public IReadOnlyList<ProcessModuleInfo> Modules
    {
        get
        {
            lock (_gate)
            {
                if (_modules is null || Stopwatch.GetElapsedTime(_moduleTick).TotalSeconds >= 1)
                {
                    _modules = ProcessInspector.Inspect(_processId, _engine).Modules;
                    _moduleTick = Stopwatch.GetTimestamp();
                }
                return _modules;
            }
        }
    }
    public ulong Resolve(string expr, IReadOnlyList<int>? offsets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expr);
        ulong address = ResolveRoot(expr.Trim());
        ValidateAddress(address);
        if (offsets is not null)
        {
            if (offsets.Count > 32) throw new ArgumentException("指针链最多支持 32 层偏移。");
            foreach (int offset in offsets)
            {
                byte[] bytes = _engine.Read(address, PointerSize);
                ulong pointer = PointerSize == 4 ? BitConverter.ToUInt32(bytes) : BitConverter.ToUInt64(bytes);
                if (pointer == 0) throw new InvalidOperationException($"指针链在 0x{address:X} 读取到空指针。");
                address = Add(pointer, offset); ValidateAddress(address);
            }
        }
        return address;
    }
    public string ExpressionFor(ulong address)
    {
        var module = Modules.FirstOrDefault(m => m.Contains(address));
        return module is null ? $"0x{address:X}" : $"\"{module.Name}\"+0x{address - module.BaseAddress:X}";
    }
    private ulong ResolveRoot(string text)
    {
        if (Regex.IsMatch(text, @"\A(?:0x)?[0-9A-Fa-f]+\z", RegexOptions.IgnoreCase)) return ParseHex(text);
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            if (close < 0) throw new FormatException("模块名的引号未闭合。");
            string name = text[1..close];
            var module = Modules.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("目标未加载模块：" + name);
            return ApplySuffix(module.BaseAddress, text[(close + 1)..]);
        }
        foreach (var module in Modules.OrderByDescending(m => m.Name.Length))
        {
            if (!text.StartsWith(module.Name, StringComparison.OrdinalIgnoreCase)) continue;
            string suffix = text[module.Name.Length..].Trim();
            if (suffix.Length == 0 || suffix[0] is '+' or '-') return ApplySuffix(module.BaseAddress, suffix);
        }
        var absolute = Regex.Match(text, @"\A(?<root>(?:0x)?[0-9A-Fa-f]+)\s*(?<suffix>[+-].+)\z", RegexOptions.IgnoreCase);
        if (absolute.Success) return ApplySuffix(ParseHex(absolute.Groups["root"].Value), absolute.Groups["suffix"].Value);
        throw new FormatException("请输入十六进制地址或已加载模块加偏移，例如 \"game.exe\"+0x1234。");
    }
    private static ulong ApplySuffix(ulong root, string text)
    {
        text = text.Trim(); if (text.Length == 0) return root;
        if (text[0] is not ('+' or '-')) throw new FormatException("模块偏移必须以 + 或 - 开头。");
        ulong offset = ParseHex(text[1..].Trim());
        if (text[0] == '+') { if (root > ulong.MaxValue - offset) throw new OverflowException("地址相加溢出。"); return root + offset; }
        if (root < offset) throw new OverflowException("地址相减超出地址空间。");
        return root - offset;
    }
    private static ulong ParseHex(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (text.Length == 0) throw new FormatException("缺少十六进制地址或偏移。");
        return ulong.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }
    internal static ulong Add(ulong pointer, int offset)
    {
        if (offset >= 0) { if (pointer > ulong.MaxValue - (uint)offset) throw new OverflowException("指针偏移相加溢出。"); return pointer + (uint)offset; }
        ulong magnitude = (ulong)-(long)offset;
        if (pointer < magnitude) throw new OverflowException("指针偏移相减溢出。");
        return pointer - magnitude;
    }
    private void ValidateAddress(ulong address)
    {
        if (address == 0 || PointerSize == 4 && address > uint.MaxValue) throw new ArgumentException("解析结果不在目标进程的有效地址空间内。");
    }
}
