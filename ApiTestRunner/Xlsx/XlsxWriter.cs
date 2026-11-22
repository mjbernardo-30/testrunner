using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ApiTestRunner.Xlsx;

public enum CellStyle
{
    Normal = 0,
    Header = 1,
    Pass = 2,
    Fail = 3,
    Skipped = 4,
    Wrap = 5,
    Bold = 6,
    Title = 7,
    Warn = 8,
}

public readonly record struct XCell(string? Text, double? Number, CellStyle Style)
{
    public static implicit operator XCell(string? s) => new(s, null, CellStyle.Normal);
    public static implicit operator XCell(int n) => new(null, n, CellStyle.Normal);
    public static implicit operator XCell(long n) => new(null, n, CellStyle.Normal);
    public static XCell S(string? s, CellStyle style) => new(s, null, style);
    public static XCell N(double n, CellStyle style = CellStyle.Normal) => new(null, n, style);
}

public sealed class XSheet
{
    public string Name { get; }
    internal readonly List<XCell[]> Rows = new();
    internal double[] Widths = Array.Empty<double>();
    internal int FreezeRows;
    internal int? FilterHeaderRow;
    internal int FilterColumns;
    internal double? DataRowHeight;
    internal int? HeightFromRow;

    internal XSheet(string name) => Name = name;

    public XSheet ColumnWidths(params double[] widths) { Widths = widths; return this; }
    public XSheet AddRow(params XCell[] cells) { Rows.Add(cells); return this; }
    public XSheet Freeze(int rows) { FreezeRows = rows; return this; }
    /// <summary>Fixed height (points) for data rows. Applies from <paramref name="fromRow"/>
    /// (default: first row below the frozen header). Null/0 = Excel auto-fit.</summary>
    public XSheet FixedRowHeight(double? points, int? fromRow = null)
    {
        DataRowHeight = points is > 0 ? points : null;
        HeightFromRow = fromRow;
        return this;
    }
    /// <summary>Adds an autofilter on the given (1-based) header row.</summary>
    public XSheet Filter(int headerRow, int columns) { FilterHeaderRow = headerRow; FilterColumns = columns; return this; }
    public int RowCount => Rows.Count;
}

/// <summary>Minimal, dependency-free .xlsx writer with a small fixed style palette.</summary>
public sealed class XlsxWriter
{
    private const int ExcelCellLimit = 32767;
    private readonly List<XSheet> _sheets = new();

    public XSheet AddSheet(string name)
    {
        var s = new XSheet(SafeSheetName(name));
        _sheets.Add(s);
        return s;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        Write(zip, "[Content_Types].xml", ContentTypes());
        Write(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");
        Write(zip, "xl/workbook.xml", WorkbookXml());
        Write(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
        Write(zip, "xl/styles.xml", StylesXml);
        for (int i = 0; i < _sheets.Count; i++)
            Write(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(_sheets[i]));
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
        s.Write(content);
    }

    private string ContentTypes()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (int i = 0; i < _sheets.Count; i++)
            sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        sb.Append("</Types>");
        return sb.ToString();
    }

    private string WorkbookXml()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (int i = 0; i < _sheets.Count; i++)
            sb.Append($"<sheet name=\"{Esc(_sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        sb.Append("</sheets>");
        // Defined names for autofilters (Excel expects _xlnm._FilterDatabase).
        var filters = _sheets.Select((s, i) => (s, i)).Where(x => x.s.FilterHeaderRow is not null && x.s.Rows.Count > 0).ToList();
        if (filters.Count > 0)
        {
            sb.Append("<definedNames>");
            foreach (var (s, i) in filters)
            {
                var lastRow = Math.Max(s.FilterHeaderRow!.Value, s.Rows.Count);
                sb.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">'{Esc(s.Name.Replace("'", "''"))}'!${ColName(1)}${s.FilterHeaderRow}:${ColName(s.FilterColumns)}${lastRow}</definedName>");
            }
            sb.Append("</definedNames>");
        }
        sb.Append("</workbook>");
        return sb.ToString();
    }

    private string WorkbookRels()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 0; i < _sheets.Count; i++)
            sb.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
        sb.Append($"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string SheetXml(XSheet sheet)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        if (sheet.FreezeRows > 0)
        {
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
            sb.Append($"<pane ySplit=\"{sheet.FreezeRows}\" topLeftCell=\"A{sheet.FreezeRows + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            sb.Append("</sheetView></sheetViews>");
        }
        if (sheet.Widths.Length > 0)
        {
            sb.Append("<cols>");
            for (int i = 0; i < sheet.Widths.Length; i++)
                sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{sheet.Widths[i].ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            var rowNum = r + 1;
            if (sheet.DataRowHeight is double h && rowNum >= (sheet.HeightFromRow ?? sheet.FreezeRows + 1))
                sb.Append($"<row r=\"{rowNum}\" ht=\"{h.ToString(CultureInfo.InvariantCulture)}\" customHeight=\"1\">");
            else
                sb.Append($"<row r=\"{rowNum}\">");
            var row = sheet.Rows[r];
            for (int c = 0; c < row.Length; c++)
            {
                var cell = row[c];
                var reference = ColName(c + 1) + rowNum;
                var style = (int)cell.Style;
                if (cell.Number is double n)
                {
                    sb.Append($"<c r=\"{reference}\" s=\"{style}\"><v>{n.ToString(CultureInfo.InvariantCulture)}</v></c>");
                }
                else if (!string.IsNullOrEmpty(cell.Text))
                {
                    var text = cell.Text!;
                    if (text.Length > ExcelCellLimit) text = text[..(ExcelCellLimit - 60)] + "\n...[truncated - see payload files]";
                    sb.Append($"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");
                }
                else if (style != 0)
                {
                    sb.Append($"<c r=\"{reference}\" s=\"{style}\"/>");
                }
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        if (sheet.FilterHeaderRow is int fr && sheet.Rows.Count >= fr)
            sb.Append($"<autoFilter ref=\"A{fr}:{ColName(sheet.FilterColumns)}{Math.Max(fr, sheet.Rows.Count)}\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    public static string ColName(int col)
    {
        var s = "";
        while (col > 0)
        {
            int m = (col - 1) % 26;
            s = (char)('A' + m) + s;
            col = (col - 1) / 26;
        }
        return s;
    }

    private static string Esc(string s)
    {
        // Strip characters that are illegal in XML 1.0, then escape.
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\t' || ch == '\n' || ch == '\r' || (ch >= 0x20 && ch <= 0xD7FF) || (ch >= 0xE000 && ch <= 0xFFFD) || char.IsSurrogate(ch))
                sb.Append(ch);
        }
        return SecurityElement.Escape(sb.ToString()) ?? "";
    }

    private static string SafeSheetName(string name)
    {
        foreach (var bad in new[] { '\\', '/', '?', '*', '[', ']', ':' }) name = name.Replace(bad, '_');
        return name.Length > 31 ? name[..31] : name;
    }

    // Style indices must match the CellStyle enum order.
    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"4\">" +
        "<font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
        "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
        "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
        "<font><b/><sz val=\"14\"/><color rgb=\"FF1F4E78\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
        "</fonts>" +
        "<fills count=\"7\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFC6EFCE\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFC7CE\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEDEDED\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFEB9C\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "</fills>" +
        "<borders count=\"2\">" +
        "<border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left><right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right><top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border>" +
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"9\">" +
        // 0 Normal
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        // 1 Header
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>" +
        // 2 Pass
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        // 3 Fail
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        // 4 Skipped
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"5\" borderId=\"1\" xfId=\"0\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        // 5 Wrap
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" +
        // 6 Bold
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        // 7 Title
        "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        // 8 Warn
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"6\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";
}
