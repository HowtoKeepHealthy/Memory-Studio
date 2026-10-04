using System.Globalization;
using System.Text;

namespace MemoryStudio;

public static class ValueCodec
{
    public static readonly Option[] Types = [new("4 字节整数", 2), new("8 字节整数", 3), new("单精度浮点", 4), new("双精度浮点", 5), new("1 字节无符号", 0), new("2 字节整数", 1), new("UTF-8 字符串", 6), new("UTF-16 字符串", 7), new("字节序列", 8)];
    public static string TypeLabel(int type) => Types.FirstOrDefault(t => t.Value == type)?.Label ?? "未知";
    public static int Width(int type) => type switch { 0 => 1, 1 => 2, 2 or 4 => 4, 3 or 5 => 8, _ => 0 };
    public static byte[] Parse(int type, string text, bool hexadecimal = false)
    {
        var culture = CultureInfo.InvariantCulture;
        var value = text.Trim();
        if (hexadecimal && type <= 5)
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
            int width = Width(type);
            if (value.Length > width * 2 || value.Length == 0) throw new ArgumentException($"此类型需要 1–{width * 2} 位十六进制数字。");
            ulong bits = ulong.Parse(value, NumberStyles.AllowHexSpecifier, culture);
            byte[] raw = BitConverter.GetBytes(bits)[..width];
            if (type == 4 && !float.IsFinite(BitConverter.ToSingle(raw)) || type == 5 && !double.IsFinite(BitConverter.ToDouble(raw)))
                throw new ArgumentException("请输入有限浮点数的位模式。");
            return raw;
        }
        byte[] bytes = type switch
        {
            0 => [byte.Parse(value, culture)],
            1 => BitConverter.GetBytes(short.Parse(value, culture)),
            2 => BitConverter.GetBytes(int.Parse(value, culture)),
            3 => BitConverter.GetBytes(long.Parse(value, culture)),
            4 => BitConverter.GetBytes(float.Parse(value, culture)),
            5 => BitConverter.GetBytes(double.Parse(value, culture)),
            6 => Encoding.UTF8.GetBytes(text),
            7 => Encoding.Unicode.GetBytes(text),
            8 => Convert.FromHexString(value.Replace(" ", "").Replace("-", "").Replace("\t", "").Replace("\r", "").Replace("\n", "")),
            _ => throw new ArgumentException("不支持此数据类型")
        };
        if (bytes.Length == 0 || bytes.Length > 4096) throw new ArgumentException("请输入 1–4096 字节的搜索内容");
        if (type == 4 && !float.IsFinite(BitConverter.ToSingle(bytes))) throw new ArgumentException("请输入有限浮点数");
        if (type == 5 && !double.IsFinite(BitConverter.ToDouble(bytes))) throw new ArgumentException("请输入有限浮点数");
        return bytes;
    }
    public static string Format(int type, byte[] bytes, bool hexadecimal = false)
    {
        if (bytes.Length == 0) return "不可读取";
        if (hexadecimal && type <= 5)
        {
            int width = Width(type);
            if (bytes.Length < width) return "不可读取";
            ulong bits = 0;
            for (int i = 0; i < width; i++) bits |= (ulong)bytes[i] << (i * 8);
            return "0x" + bits.ToString("X" + width * 2, CultureInfo.InvariantCulture);
        }
        return type switch
        {
            0 => bytes[0].ToString(CultureInfo.InvariantCulture),
            1 => BitConverter.ToInt16(bytes).ToString(CultureInfo.InvariantCulture),
            2 => BitConverter.ToInt32(bytes).ToString(CultureInfo.InvariantCulture),
            3 => BitConverter.ToInt64(bytes).ToString(CultureInfo.InvariantCulture),
            4 => BitConverter.ToSingle(bytes).ToString("G9", CultureInfo.InvariantCulture),
            5 => BitConverter.ToDouble(bytes).ToString("G17", CultureInfo.InvariantCulture),
            6 => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
            7 => Encoding.Unicode.GetString(bytes).TrimEnd('\0'),
            _ => Convert.ToHexString(bytes)
        };
    }
    public static (byte[] Value, byte[] Mask) ParsePattern(string text)
    {
        string clean = string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '-'));
        if (clean.Length == 0 || clean.Length % 2 != 0 || clean.Length > 8192) throw new ArgumentException("字节模式需要 1–4096 个字节，例如 48 8B ?? ?F。");
        byte[] bytes = new byte[clean.Length / 2], mask = new byte[bytes.Length];
        for (int i = 0; i < clean.Length; i++)
        {
            char c = clean[i];
            if (c is '?' or '*') continue;
            int digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
            if (digit < 0) throw new FormatException("字节模式只接受十六进制数字、? 和 *。");
            int shift = i % 2 == 0 ? 4 : 0;
            bytes[i / 2] |= (byte)(digit << shift); mask[i / 2] |= (byte)(15 << shift);
        }
        return (bytes, mask);
    }
    public static ulong Address(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
