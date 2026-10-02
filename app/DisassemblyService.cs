using Iced.Intel;

namespace MemoryStudio;

public sealed record DisassemblyRow(ulong Address, int Length, byte[] Bytes, string Instruction,
    ulong? BranchTarget, bool IsInvalid, bool IsTruncated, string Note)
{
    public string AddressText => $"0x{Address:X16}";
    public string BytesText => string.Join(" ", Bytes.Select(b => b.ToString("X2")));
    public string BranchTargetText => BranchTarget is ulong address ? $"0x{address:X16}" : "—";
    public ulong NextAddress => Address + (ulong)Length;
}

public sealed record DisassemblyReadResult(ulong StartAddress, ulong NextAddress,
    IReadOnlyList<DisassemblyRow> Rows, int ReadBytes, int RequestedBytes, string? BoundaryMessage);

/// <summary>Decode a contiguous snapshot of target memory. This service never writes to the process.</summary>
public static class DisassemblyService
{
    public static IReadOnlyList<DisassemblyRow> Decode(byte[] bytes, ulong ip, int bitness)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bitness is not (32 or 64)) throw new ArgumentOutOfRangeException(nameof(bitness), "请选择 32 或 64 位指令模式。");
        if (bitness == 32 && ip > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(ip), "32 位指令地址不能超过 0xFFFFFFFF。");
        if (ip > ulong.MaxValue - (ulong)bytes.Length) throw new ArgumentOutOfRangeException(nameof(ip));
        var reader = new ByteArrayCodeReader(bytes);
        var decoder = Decoder.Create(bitness, reader, ip);
        var formatter = new IntelFormatter();
        formatter.Options.HexPrefix = "0x";
        formatter.Options.HexSuffix = "";
        formatter.Options.UppercaseHex = true;
        formatter.Options.SpaceAfterOperandSeparator = true;
        formatter.Options.RipRelativeAddresses = true;
        var output = new StringOutput();
        var rows = new List<DisassemblyRow>();
        while (reader.CanReadByte)
        {
            int offset = reader.Position;
            decoder.Decode(out var instruction);
            int length = reader.Position - offset;
            if (length <= 0) break;
            byte[] raw = bytes.AsSpan(offset, length).ToArray();
            bool truncated = decoder.LastError == DecoderError.NoMoreBytes;
            bool invalid = instruction.IsInvalid;
            string text, note = "";
            ulong? target = null;
            if (invalid)
            {
                text = "db " + string.Join(", ", raw.Select(b => $"0x{b:X2}"));
                note = truncated ? "字节不足，指令截断" : "无效指令编码，显示原始字节";
            }
            else
            {
                formatter.Format(in instruction, output);
                text = output.ToStringAndReset();
                if (instruction.OpCount > 0 && instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                    target = instruction.NearBranchTarget;
                note = instruction.FlowControl switch
                {
                    FlowControl.UnconditionalBranch => "直接跳转",
                    FlowControl.ConditionalBranch => "条件跳转",
                    FlowControl.Call => "直接调用",
                    FlowControl.IndirectBranch => "间接跳转：目标需要运行时寄存器或内存",
                    FlowControl.IndirectCall => "间接调用：目标需要运行时寄存器或内存",
                    FlowControl.Return => "返回",
                    _ => ""
                };
            }
            rows.Add(new DisassemblyRow(ip + (ulong)offset, length, raw, text, target, invalid, truncated, note));
            if (truncated) break;
        }
        return rows;
    }

    public static DisassemblyReadResult ReadWindow(NativeEngine engine, ulong address, int bitness, int byteCount = 512)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (bitness is not (32 or 64)) throw new ArgumentOutOfRangeException(nameof(bitness));
        if (byteCount is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(byteCount));
        if (bitness == 32 && address > uint.MaxValue) throw new ArgumentException("32 位指令地址不能超过 0xFFFFFFFF。");
        if (address > ulong.MaxValue - (ulong)byteCount - 14) throw new ArgumentException("此地址过于接近地址空间末尾。");
        // Up to 14 look-ahead bytes complete an instruction that begins at the displayed window's end.
        int desired = byteCount + 14;
        if (bitness == 32) desired = (int)Math.Min((ulong)desired, 0x1_0000_0000UL - address);
        var collected = new List<byte>(desired);
        string? boundary = null;
        while (collected.Count < desired)
        {
            ulong cursor = address + (ulong)collected.Count;
            int pageRemaining = Environment.SystemPageSize - (int)(cursor % (ulong)Environment.SystemPageSize);
            int size = Math.Min(pageRemaining, desired - collected.Count);
            try { collected.AddRange(engine.Read(cursor, size)); }
            catch (InvalidOperationException ex)
            {
                // Never join bytes across an unreadable gap. Find only a readable prefix of this same chunk.
                byte[] prefix = ReadPrefix(engine, cursor, size);
                collected.AddRange(prefix);
                boundary = $"在 0x{cursor + (ulong)prefix.Length:X16} 到达不可读边界：{ex.Message}";
                break;
            }
        }
        if (collected.Count == 0) throw new InvalidOperationException(boundary ?? $"无法读取起点 0x{address:X16}。");
        var rows = Decode(collected.ToArray(), address, bitness)
            .TakeWhile(row => row.Address - address < (ulong)byteCount).ToArray();
        ulong next = rows.Length > 0 ? rows[^1].NextAddress : address;
        return new DisassemblyReadResult(address, next, rows, collected.Count, desired, boundary);
    }

    private static byte[] ReadPrefix(NativeEngine engine, ulong address, int failedSize)
    {
        int low = 0, high = failedSize - 1;
        byte[] best = [];
        while (low < high)
        {
            int size = low + (high - low + 1) / 2;
            try { best = engine.Read(address, size); low = size; }
            catch (InvalidOperationException) { high = size - 1; }
        }
        return best;
    }
}
