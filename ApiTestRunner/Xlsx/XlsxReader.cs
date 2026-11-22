using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace ApiTestRunner.Xlsx;

/// <summary>
/// Minimal, dependency-free .xlsx reader. Reads every sheet as a grid of strings
/// (what the user typed). Good enough for test-case workbooks; no formulas evaluated
/// beyond their cached values.
/// </summary>
public static class XlsxReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Returns sheet name -> rows (row number -> (column index -> value)).</summary>
    public static Dictionary<string, SortedDictionary<int, Dictionary<int, string>>> ReadAll(string path)
    {
        // FileShare.ReadWrite so it still works while the SA has the workbook open in Excel.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var sharedStrings = ReadSharedStrings(zip);
        var workbook = LoadXml(zip, "xl/workbook.xml") ?? throw new InvalidDataException("Not a valid .xlsx file (xl/workbook.xml missing).");
        var rels = LoadXml(zip, "xl/_rels/workbook.xml.rels");

        var relTargets = rels?.Root?.Elements(PkgRel + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!)
            ?? new Dictionary<string, string>();

        var result = new Dictionary<string, SortedDictionary<int, Dictionary<int, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Root!.Element(Main + "sheets")!.Elements(Main + "sheet"))
        {
            var name = (string)sheet.Attribute("name")!;
            var rid = (string?)sheet.Attribute(RelNs + "id");
            if (rid is null || !relTargets.TryGetValue(rid, out var target)) continue;
            var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            var sheetXml = LoadXml(zip, sheetPath);
            if (sheetXml is null) continue;
            result[name] = ReadSheet(sheetXml, sharedStrings);
        }
        return result;
    }

    private static SortedDictionary<int, Dictionary<int, string>> ReadSheet(XDocument doc, List<string> sharedStrings)
    {
        var rows = new SortedDictionary<int, Dictionary<int, string>>();
        var sheetData = doc.Root!.Element(Main + "sheetData");
        if (sheetData is null) return rows;

        int implicitRow = 0;
        foreach (var row in sheetData.Elements(Main + "row"))
        {
            int rowNum = int.TryParse((string?)row.Attribute("r"), out var rn) ? rn : implicitRow + 1;
            implicitRow = rowNum;
            var cells = new Dictionary<int, string>();
            int implicitCol = 0;
            foreach (var c in row.Elements(Main + "c"))
            {
                var r = (string?)c.Attribute("r");
                int col = r is null ? implicitCol + 1 : ColumnIndex(r);
                implicitCol = col;
                var value = CellValue(c, sharedStrings);
                if (!string.IsNullOrEmpty(value)) cells[col] = value;
            }
            if (cells.Count > 0) rows[rowNum] = cells;
        }
        return rows;
    }

    private static string CellValue(XElement c, List<string> sharedStrings)
    {
        var type = (string?)c.Attribute("t");
        var v = c.Element(Main + "v")?.Value;
        switch (type)
        {
            case "s":
                return int.TryParse(v, out var idx) && idx < sharedStrings.Count ? sharedStrings[idx] : "";
            case "inlineStr":
                return string.Concat(c.Element(Main + "is")?.Descendants(Main + "t").Select(t => t.Value) ?? Array.Empty<string>());
            case "b":
                return v == "1" ? "TRUE" : "FALSE";
            case "str":
            case "e":
                return v ?? "";
            default:
                // Numbers: keep as typed where possible (avoid 1E+15 style output).
                if (v is null) return "";
                if (decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d.ToString(CultureInfo.InvariantCulture);
                return v;
        }
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var doc = LoadXml(zip, "xl/sharedStrings.xml");
        if (doc is null) return list;
        foreach (var si in doc.Root!.Elements(Main + "si"))
        {
            // Skip phonetic runs (rPh); concatenate rich-text runs.
            var texts = si.Descendants(Main + "t").Where(t => t.Parent?.Name != Main + "rPh").Select(t => t.Value);
            list.Add(string.Concat(texts));
        }
        return list;
    }

    private static XDocument? LoadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    public static int ColumnIndex(string cellRef)
    {
        int col = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsLetter(ch)) break;
            col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return col;
    }
}
