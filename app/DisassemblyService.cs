using Iced.Intel;

namespace MemoryStudio;

public sealed record DisassemblyRow(ulong Address, int Length, byte[] Bytes, string Instruction,
    ulong? BranchTarget, bool IsInvalid, bool IsTruncated, string Note, bool IsBoundaryUncertain = false)
{
    public string AddressText => $"0x{Address:X16}";
    public string BytesText => string.Join(" ", Bytes.Select(b => b.ToString("X2")));
    public string BranchTargetText => BranchTarget is ulong address ? $"0x{address:X16}" : "—";
    public ulong NextAddress => Address + (ulong)Length;
    public string BoundaryText => IsBoundaryUncertain ? "边界未确定" : "已知起点";
}

public sealed record DisassemblyReadResult(ulong StartAddress, ulong NextAddress,
    IReadOnlyList<DisassemblyRow> Rows, int ReadBytes, int RequestedBytes, string? BoundaryMessage);

/// <summary>Decode a contiguous snapshot of target memory. This service never writes to the process.</summary>
public static class DisassemblyService
{
    /// <summary>
    /// There is no unique inverse x86 decoder. A known earlier instruction start is required
    /// for a reliable backward region; merely landing on endAddress is not proof of alignment.
    /// </summary>
    public static DisassemblyReadResult ReadBefore(NativeEngine engine, ulong endAddress, int bitness,
        int byteCount = 512, ulong? knownAnchor = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (bitness is not (32 or 64)) throw new ArgumentOutOfRangeException(nameof(bitness));
        if (bitness == 32 && endAddress > 0x1_0000_0000UL) throw new ArgumentException("32 位指令地址不能超过 0xFFFFFFFF。");
        if (byteCount is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(byteCount));
        if (endAddress == 0) return new(0, 0, [], 0, 0, "已到达地址空间起点。");
        ulong start = endAddress > (ulong)byteCount ? endAddress - (ulong)byteCount : 0;
        bool anchored = knownAnchor is ulong anchor && anchor < endAddress && endAddress - anchor <= 65536;
        if (anchored) start = knownAnchor!.Value;
        int requested = checked((int)(endAddress - start));
        // Read backward, one page at a time. An inaccessible earlier page must not
        // hide the readable suffix immediately before IP (e.g. IP is pageStart+64).
        // Never skip an unreadable gap or concatenate bytes across it.
        var chunks = new List<byte[]>();
        ulong cursor = endAddress;
        string? boundary = null;
        while (cursor > start)
        {
            ulong pageStart = ((cursor - 1) / (ulong)Environment.SystemPageSize) * (ulong)Environment.SystemPageSize;
            ulong chunkStart = Math.Max(start, pageStart);
            int size = checked((int)(cursor - chunkStart));
            try { chunks.Add(engine.Read(chunkStart, size)); cursor = chunkStart; }
            catch (ObjectDisposedException) { throw; }
            catch (InvalidOperationException ex)
            {
                boundary = $"在 0x{cursor:X16} 到达前方不可读边界：{ex.Message}";
                break;
            }
        }
        if (chunks.Count == 0) throw new InvalidOperationException(boundary ?? "当前指令前方没有连续可读字节。");
        chunks.Reverse();
        byte[] bytes = chunks.SelectMany(chunk => chunk).ToArray();
        bool reliable = anchored && cursor == start;
        var rows = DecodeBefore(bytes, cursor, endAddress, bitness, reliable);
        string? note = reliable && rows.All(r => !r.IsBoundaryUncertain) ? boundary :
            "反向区域的指令边界未确定：已尝试对齐至当前起点，可能是数据或另一种合法拆分。请选择已知入口/指令起点确认；黄色行不能直接作为可靠补丁边界。";
        if (boundary != null && note != boundary) note = boundary + " " + note;
        return new(cursor, endAddress, rows, bytes.Length, requested, note);
    }

    public static IReadOnlyList<DisassemblyRow> DecodeBefore(byte[] bytes, ulong startAddress,
        ulong endAddress, int bitness, bool reliableStart)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (endAddress < startAddress || endAddress - startAddress != (ulong)bytes.Length)
            throw new ArgumentException("反向字节范围必须恰好结束于已知指令起点。");
        var direct = Decode(bytes, startAddress, bitness);
        if (reliableStart && direct.All(r => !r.IsTruncated) && (direct.Count == 0 || direct[^1].NextAddress == endAddress))
            return direct;
        // Try possible initial alignments. Every inferred row remains explicitly uncertain,
        // even when the stream converges on the known ending boundary without invalid opcodes.
        IReadOnlyList<DisassemblyRow>? best = null;
        int bestOffset = 0, bestInvalid = int.MaxValue;
        for (int offset = 0; offset < Math.Min(15, bytes.Length); ++offset)
        {
            var candidate = Decode(bytes.AsSpan(offset).ToArray(), startAddress + (ulong)offset, bitness);
            if (candidate.Count == 0 || candidate.Any(r => r.IsTruncated) || candidate[^1].NextAddress != endAddress) continue;
            int invalid = candidate.Count(r => r.IsInvalid);
            if (invalid >= bestInvalid) continue;
            best = candidate; bestOffset = offset; bestInvalid = invalid;
        }
        var result = new List<DisassemblyRow>();
        int rawPrefix = best == null ? bytes.Length : bestOffset;
        for (int i = 0; i < rawPrefix; ++i)
            result.Add(new(startAddress + (ulong)i, 1, [bytes[i]], $"db 0x{bytes[i]:X2}", null, true, false, "边界未确定 · 原始字节", true));
        if (best != null)
            result.AddRange(best.Select(r => r with { IsBoundaryUncertain = true, Note = "边界未确定" + (r.Note.Length > 0 ? " · " + r.Note : "") }));
        return result;
    }

    public static IReadOnlyList<DisassemblyRow> Decode(byte[] bytes, ulong ip, int bitness)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bitness is not (32 or 64)) throw new ArgumentOutOfRangeException(nameof(bitness), "请选择 32 或 64 位指令模式。");
        if (bitness == 32 && ip > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(ip), "32 位指令地址不能超过 0xFFFFFFFF。");
        if (bitness == 32 && (ulong)bytes.Length > 0x1_0000_0000UL - ip)
            throw new ArgumentOutOfRangeException(nameof(bytes), "输入字节不能跨越 32 位地址空间末尾。");
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
            catch (ObjectDisposedException) { throw; }
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
            catch (ObjectDisposedException) { throw; }
            catch (InvalidOperationException) { high = size - 1; }
        }
        return best;
    }
}
