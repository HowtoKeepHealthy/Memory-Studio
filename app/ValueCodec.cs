using System.Globalization;
using System.Text;

namespace MemoryStudio;

public static class ValueCodec
{
    public static readonly Option[] Types = [new("4 字节整数", 2), new("8 字节整数", 3), new("单精度浮点", 4), new("双精度浮点", 5), new("1 字节无符号", 0), new("2 字节整数", 1), new("UTF-8 字符串", 6), new("UTF-16 字符串", 7), new("字节序列", 8)];
    public static string TypeLabel(int type) => Types.FirstOrDefault(t => t.Value == type)?.Label ?? "未知";
    public static int Width(int type) => type switch { 0 => 1, 1 => 2, 2 or 4 => 4, 3 or 5 => 8, _ => 0 };
    public static byte[] Parse(int type, string text)
    {
        var culture = CultureInfo.InvariantCulture;
        var value = text.Trim();
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
    public static string Format(int type, byte[] bytes)
    {
        if (bytes.Length == 0) return "不可读取";
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
    public static ulong Address(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
