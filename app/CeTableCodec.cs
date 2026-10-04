using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace MemoryStudio;

public sealed record CeTableImport(IReadOnlyList<TableEntry> Entries, int Skipped);
public static class CeTableCodec
{
    private static readonly Dictionary<string, int> Types = new(StringComparer.OrdinalIgnoreCase)
    { ["Byte"] = 0, ["2 Bytes"] = 1, ["4 Bytes"] = 2, ["8 Bytes"] = 3, ["Float"] = 4, ["Double"] = 5, ["String"] = 6, ["Array of byte"] = 8 };
    public static CeTableImport Load(string path)
    {
        if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new ArgumentException(".CT 表超过 64 MB。");
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 });
        var document = XDocument.Load(reader); var entries = new List<TableEntry>(); int skipped = 0;
        if (document.Root?.Name.LocalName != "CheatTable") throw new ArgumentException("不是有效的 Cheat Engine 表。");
        foreach (var item in document.Descendants("CheatEntry"))
        {
            string typeName = (string?)item.Element("VariableType") ?? "";
            string expression = ((string?)item.Element("Address") ?? "").Trim();
            if (!Types.TryGetValue(typeName, out int type) || expression.Length == 0 || item.Element("AssemblerScript") != null)
            { skipped++; continue; }
            if (type == 6 && ((string?)item.Element("Unicode") == "1")) type = 7;
            int size = ValueCodec.Width(type);
            if (size == 0)
            {
                if (!int.TryParse((string?)item.Element(type == 8 ? "ByteLength" : "Length"), NumberStyles.None, CultureInfo.InvariantCulture, out size)) size = 1;
                if (type == 7) size = checked(size * 2);
            }
            if (size is < 1 or > 4096) { skipped++; continue; }
            int[] offsets;
            try
            {
                offsets = item.Element("Offsets")?.Elements("Offset").Select(e => ParseOffset(e.Value)).Reverse().ToArray() ?? [];
                if (offsets.Length > 32) throw new ArgumentException();
            }
            catch { skipped++; continue; }
            string description = ((string?)item.Element("Description") ?? "导入地址").Trim('"');
            entries.Add(new TableEntry(expression, type, size, description, expression, offsets));
            if (entries.Count > 100000) throw new ArgumentException(".CT 表超过 100,000 条数据记录。");
        }
        return new(entries, skipped);
    }
    private static int ParseOffset(string text)
    {
        string value = text.Trim(); bool negative = value.StartsWith('-');
        if (negative) value = value[1..];
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        uint offset = uint.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return negative ? checked(-(int)offset) : unchecked((int)offset);
    }
    public static void Save(string path, IEnumerable<WatchRow> rows)
    {
        var entries = new XElement("CheatEntries"); int id = 0;
        foreach (var row in rows)
        {
            string typeName = row.Type switch { 0 => "Byte", 1 => "2 Bytes", 2 => "4 Bytes", 3 => "8 Bytes", 4 => "Float", 5 => "Double", 6 or 7 => "String", _ => "Array of byte" };
            var entry = new XElement("CheatEntry", new XElement("ID", id++), new XElement("Description", '"' + row.Description + '"'),
                new XElement("VariableType", typeName), new XElement("Address", string.IsNullOrWhiteSpace(row.AddressExpression) ? row.Address.ToString("X", CultureInfo.InvariantCulture) : row.AddressExpression));
            if (row.Type is 6 or 7) { entry.Add(new XElement("Length", row.Type == 7 ? row.Size / 2 : row.Size)); if (row.Type == 7) entry.Add(new XElement("Unicode", 1)); }
            if (row.Type == 8) entry.Add(new XElement("ByteLength", row.Size));
            if (row.PointerOffsets.Length > 0) entry.Add(new XElement("Offsets", row.PointerOffsets.Reverse().Select(offset =>
                new XElement("Offset", offset < 0 ? "-" + (-(long)offset).ToString("X") : offset.ToString("X")))));
            entries.Add(entry);
        }
        new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("CheatTable", new XAttribute("CheatEngineTableVersion", "45"), entries)).Save(path);
    }
}
