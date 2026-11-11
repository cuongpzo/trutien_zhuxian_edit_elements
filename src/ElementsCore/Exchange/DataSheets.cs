using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;

namespace ElementsCore.Exchange;

/// <summary>Pre-serialised JSON (a nested list) inside a sheet cell.</summary>
public sealed record RawJson(string Json)
{
    public override string ToString() => Json;
}

/// <summary>
/// One table for export/import: a header and rows. Export cells are typed (string, long, double,
/// bool, <see cref="RawJson"/>); CSV/XLSX write them as text, JSON keeps the types and nesting.
/// Imported cells are always text (numbers as written, nested lists as JSON text).
/// </summary>
public sealed class Sheet
{
    public required string Name { get; init; }
    public List<string> Header { get; init; } = new();
    public List<object?[]> Rows { get; init; } = new();

    public int Col(string name) => Header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
    public string? Cell(object?[] row, int col) => col >= 0 && col < row.Length ? CellText(row[col]) : null;

    public static string? CellText(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b ? "1" : "0",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };
}

public enum SheetFormat { Csv, Xlsx, Json }

/// <summary>Reads / writes <see cref="Sheet"/>s as CSV (one sheet), XLSX (one worksheet per sheet) or JSON.</summary>
public static class DataSheets
{
    public static SheetFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xlsx" => SheetFormat.Xlsx,
        ".json" => SheetFormat.Json,
        _ => SheetFormat.Csv,
    };

    public const string FileFilter = "Excel (*.xlsx)|*.xlsx|CSV UTF-8 (*.csv)|*.csv|JSON (*.json)|*.json";
    public static string OpenFilter => L.T("Data") + " (*.xlsx;*.csv;*.json)|*.xlsx;*.csv;*.json|" + L.T("All files") + "|*.*";

    public static void Write(string path, IReadOnlyList<Sheet> sheets, string kind, string source)
    {
        switch (FormatOf(path))
        {
            case SheetFormat.Csv:
                if (sheets.Count != 1) throw new InvalidOperationException(L.T("CSV can hold only one table – use XLSX or JSON for several tables."));
                using (var w = Csv.CreateWriter(path))
                {
                    Csv.WriteRow(w, sheets[0].Header);
                    foreach (var r in sheets[0].Rows) Csv.WriteRow(w, r.Select(Sheet.CellText));
                }
                break;
            case SheetFormat.Xlsx:
                MiniXlsx.Write(path, sheets);
                break;
            case SheetFormat.Json:
                WriteJson(path, sheets, kind, source);
                break;
        }
    }

    public static List<Sheet> Read(string path) => FormatOf(path) switch
    {
        SheetFormat.Xlsx => MiniXlsx.Read(path),
        SheetFormat.Json => ReadJson(path),
        _ => [ReadCsv(path)],
    };

    private static Sheet ReadCsv(string path)
    {
        var rows = Csv.Read(path).ToList();
        if (rows.Count == 0) throw new InvalidDataException(L.T("The CSV file is empty."));
        var s = new Sheet { Name = Path.GetFileNameWithoutExtension(path), Header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToList() };
        foreach (var r in rows.Skip(1))
            if (r.Any(c => c.Length > 0)) s.Rows.Add(r.Cast<object?>().ToArray());
        return s;
    }

    // ───────────────────────── JSON ─────────────────────────

    private static void WriteJson(string path, IReadOnlyList<Sheet> sheets, string kind, string source)
    {
        using var fs = File.Create(path);
        // big tables (e.g. 34k equipment × 200 fields) are written compact: indentation would triple the size
        long cells = sheets.Sum(s => (long)s.Rows.Count * Math.Max(1, s.Header.Count));
        using var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = cells < 300_000, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        jw.WriteStartObject();
        jw.WriteString("format", "TruTien Elements Editor export");
        jw.WriteString("tool_version", BuildInfo.Tag);
        jw.WriteString("kind", kind);
        jw.WriteString("source", source);
        jw.WriteString("exported", DateTime.Now.ToString("s"));
        jw.WriteStartObject("sheets");
        foreach (var s in sheets)
        {
            jw.WriteStartArray(s.Name);
            foreach (var r in s.Rows)
            {
                jw.WriteStartObject();
                for (int c = 0; c < s.Header.Count && c < r.Length; c++)
                {
                    jw.WritePropertyName(s.Header[c]);
                    WriteJsonValue(jw, r[c]);
                }
                jw.WriteEndObject();
            }
            jw.WriteEndArray();
        }
        jw.WriteEndObject();
        jw.WriteEndObject();
    }

    private static void WriteJsonValue(Utf8JsonWriter jw, object? v)
    {
        switch (v)
        {
            case null: jw.WriteNullValue(); break;
            case string s: jw.WriteStringValue(s); break;
            case bool b: jw.WriteBooleanValue(b); break;
            case RawJson j: jw.WriteRawValue(j.Json, skipInputValidation: false); break;
            case float f when float.IsFinite(f): jw.WriteNumberValue(f); break;
            case double d when double.IsFinite(d): jw.WriteNumberValue(d); break;
            case sbyte or byte or short or ushort or int or uint or long:
                jw.WriteNumberValue(Convert.ToInt64(v, CultureInfo.InvariantCulture)); break;
            case ulong u: jw.WriteNumberValue(u); break;
            default: jw.WriteStringValue(Sheet.CellText(v)); break;
        }
    }

    private static List<Sheet> ReadJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var result = new List<Sheet>();
        if (root.ValueKind == JsonValueKind.Array) { result.Add(ToSheet(Path.GetFileNameWithoutExtension(path), root)); return result; }
        JsonElement container = root;
        foreach (var name in new[] { "sheets", "tables", "lists" })
            if (root.TryGetProperty(name, out var c) && c.ValueKind == JsonValueKind.Object) { container = c; break; }
        foreach (var p in container.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Array) result.Add(ToSheet(p.Name, p.Value));
        if (result.Count == 0) throw new InvalidDataException(L.T("The JSON has no table (expected an array of objects, or \"sheets\": { name: [...] })."));
        return result;
    }

    private static Sheet ToSheet(string name, JsonElement arr)
    {
        var s = new Sheet { Name = name };
        var objs = arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        foreach (var o in objs)
            foreach (var p in o.EnumerateObject())
                if (s.Col(p.Name) < 0) s.Header.Add(p.Name);
        foreach (var o in objs)
        {
            var row = new object?[s.Header.Count];
            foreach (var p in o.EnumerateObject())
                row[s.Col(p.Name)] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.True => "1",
                    JsonValueKind.False => "0",
                    _ => p.Value.GetRawText(),
                };
            s.Rows.Add(row);
        }
        return s;
    }
}

/// <summary>Minimal XLSX (Office Open XML) writer/reader – no external package needed.</summary>
public static class MiniXlsx
{
    private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Excel sheet names: ≤31 chars, no []:*?/\ and unique.</summary>
    public static List<string> SafeNames(IEnumerable<string> names)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var n0 in names)
        {
            var n = new string(n0.Select(ch => "[]:*?/\\".Contains(ch) ? '_' : ch).ToArray()).Trim('\'');
            if (n.Length == 0) n = "Sheet";
            if (n.Length > 31) n = n[..31];
            var b = n; int k = 2;
            while (!used.Add(n)) { var suf = $" ({k++})"; n = (b.Length + suf.Length > 31 ? b[..(31 - suf.Length)] : b) + suf; }
            list.Add(n);
        }
        return list;
    }

    public static void Write(string path, IReadOnlyList<Sheet> sheets)
    {
        var names = SafeNames(sheets.Select(s => s.Name));
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, Action<XmlWriter> body)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Fastest);
            using var st = e.Open();
            using var xw = XmlWriter.Create(st, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false });
            xw.WriteStartDocument(true);
            body(xw);
            xw.WriteEndDocument();
        }

        Entry("[Content_Types].xml", x =>
        {
            x.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            Default(x, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(x, "xml", "application/xml");
            Override(x, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Override(x, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            for (int i = 0; i < sheets.Count; i++)
                Override(x, $"/xl/worksheets/sheet{i + 1}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            x.WriteEndElement();
        });
        Entry("_rels/.rels", x =>
        {
            x.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            Rel(x, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            x.WriteEndElement();
        });
        Entry("xl/_rels/workbook.xml.rels", x =>
        {
            x.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            for (int i = 0; i < sheets.Count; i++)
                Rel(x, $"rId{i + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{i + 1}.xml");
            Rel(x, $"rId{sheets.Count + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
            x.WriteEndElement();
        });
        Entry("xl/workbook.xml", x =>
        {
            x.WriteStartElement("workbook", NsMain);
            x.WriteAttributeString("xmlns", "r", null, NsRel);
            x.WriteStartElement("sheets", NsMain);
            for (int i = 0; i < sheets.Count; i++)
            {
                x.WriteStartElement("sheet", NsMain);
                x.WriteAttributeString("name", names[i]);
                x.WriteAttributeString("sheetId", (i + 1).ToString());
                x.WriteAttributeString("id", NsRel, $"rId{i + 1}");
                x.WriteEndElement();
            }
            x.WriteEndElement();
            x.WriteEndElement();
        });
        Entry("xl/styles.xml", x =>
        {
            // style 0 = normal, style 1 = bold header; text cells keep "@" (text) so Excel does not reformat "-5" or "1e5"
            x.WriteStartElement("styleSheet", NsMain);
            x.WriteRaw("<numFmts count=\"0\"/><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
                + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
                + "<borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs>"
                + "<cellXfs count=\"3\"><xf/><xf fontId=\"1\" applyFont=\"1\"/><xf numFmtId=\"49\" applyNumberFormat=\"1\"/></cellXfs>");
            x.WriteEndElement();
        });
        for (int i = 0; i < sheets.Count; i++)
        {
            var s = sheets[i];
            Entry($"xl/worksheets/sheet{i + 1}.xml", x =>
            {
                x.WriteStartElement("worksheet", NsMain);
                x.WriteRaw("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                x.WriteStartElement("sheetData", NsMain);
                WriteRow(x, 1, s.Header.Cast<object?>().ToArray(), header: true);
                for (int r = 0; r < s.Rows.Count; r++) WriteRow(x, r + 2, s.Rows[r], header: false);
                x.WriteEndElement();
                x.WriteEndElement();
            });
        }
    }

    private static void WriteRow(XmlWriter x, int rowNum, object?[] cells, bool header)
    {
        x.WriteStartElement("row", NsMain);
        x.WriteAttributeString("r", rowNum.ToString(CultureInfo.InvariantCulture));
        for (int c = 0; c < cells.Length; c++)
        {
            var v = cells[c];
            if (v == null) continue;
            x.WriteStartElement("c", NsMain);
            x.WriteAttributeString("r", ColName(c) + rowNum.ToString(CultureInfo.InvariantCulture));
            bool number = !header && v is sbyte or byte or short or ushort or int or uint or long or ulong
                || !header && v is float f && float.IsFinite(f) || !header && v is double d && double.IsFinite(d);
            if (header) x.WriteAttributeString("s", "1");
            if (number)
            {
                x.WriteElementString("v", NsMain, Sheet.CellText(v));
            }
            else
            {
                if (!header) x.WriteAttributeString("s", "2");
                x.WriteAttributeString("t", "inlineStr");
                x.WriteStartElement("is", NsMain);
                x.WriteStartElement("t", NsMain);
                var text = Sheet.CellText(v) ?? "";
                if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains('\n')))
                    x.WriteAttributeString("xml", "space", null, "preserve");
                x.WriteString(XmlSafe(text));
                x.WriteEndElement();
                x.WriteEndElement();
            }
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    /// <summary>XML 1.0 cannot hold most control characters: write them as _xHHHH_ (Excel's own escape).</summary>
    private static string XmlSafe(string s)
    {
        if (!s.Any(ch => ch < 0x20 && ch is not '\t' and not '\n' and not '\r' || ch == '_')) return s;
        var sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch < 0x20 && ch is not '\t' and not '\n' and not '\r') sb.Append($"_x{(int)ch:X4}_");
            else if (ch == '_' && i + 6 < s.Length && s[i + 1] == 'x' && s[i + 6] == '_' && s.Substring(i + 2, 4).All(Uri.IsHexDigit)) sb.Append("_x005F_");
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string XmlUnescape(string s)
    {
        if (!s.Contains("_x", StringComparison.Ordinal)) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '_' && i + 6 < s.Length && s[i + 1] == 'x' && s[i + 6] == '_' && s.Substring(i + 2, 4).All(Uri.IsHexDigit))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16));
                i += 6;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static string ColName(int c)
    {
        var s = "";
        for (c++; c > 0; c = (c - 1) / 26) s = (char)('A' + (c - 1) % 26) + s;
        return s;
    }

    private static int ColIndex(string cellRef)
    {
        int c = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsLetter(ch)) break;
            c = c * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return c - 1;
    }

    private static void Default(XmlWriter x, string ext, string type) { x.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types"); x.WriteAttributeString("Extension", ext); x.WriteAttributeString("ContentType", type); x.WriteEndElement(); }
    private static void Override(XmlWriter x, string part, string type) { x.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types"); x.WriteAttributeString("PartName", part); x.WriteAttributeString("ContentType", type); x.WriteEndElement(); }
    private static void Rel(XmlWriter x, string id, string type, string target) { x.WriteStartElement("Relationship", "http://schemas.openxmlformats.org/package/2006/relationships"); x.WriteAttributeString("Id", id); x.WriteAttributeString("Type", type); x.WriteAttributeString("Target", target); x.WriteEndElement(); }

    /// <summary>Reads every worksheet (shared strings, inline strings, numbers, booleans). First row = header.</summary>
    public static List<Sheet> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XmlDocument Load(string name)
        {
            var e = zip.GetEntry(name) ?? throw new InvalidDataException(L.F("The xlsx file is missing {0}.", name));
            using var st = e.Open();
            var d = new XmlDocument();
            d.Load(st);
            return d;
        }
        var ns = new XmlNamespaceManager(new NameTable());
        ns.AddNamespace("m", NsMain);
        ns.AddNamespace("r", NsRel);
        ns.AddNamespace("pr", "http://schemas.openxmlformats.org/package/2006/relationships");

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") != null)
            foreach (XmlNode si in Load("xl/sharedStrings.xml").SelectNodes("//m:si", ns)!)
                shared.Add(XmlUnescape(string.Concat(si.SelectNodes(".//m:t", ns)!.Cast<XmlNode>().Select(t => t.InnerText))));

        var wb = Load("xl/workbook.xml");
        var rels = Load("xl/_rels/workbook.xml.rels");
        var result = new List<Sheet>();
        foreach (XmlElement sh in wb.SelectNodes("//m:sheets/m:sheet", ns)!)
        {
            var rid = sh.GetAttribute("id", NsRel);
            var target = (rels.SelectSingleNode($"//pr:Relationship[@Id='{rid}']", ns) as XmlElement)?.GetAttribute("Target") ?? "";
            target = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            var doc = Load(target);
            var rows = new List<List<string?>>();
            foreach (XmlElement row in doc.SelectNodes("//m:sheetData/m:row", ns)!)
            {
                var cells = new List<string?>();
                int auto = 0;
                foreach (XmlElement c in row.SelectNodes("m:c", ns)!)
                {
                    var r = c.GetAttribute("r");
                    int ci = r.Length > 0 ? ColIndex(r) : auto;
                    auto = ci + 1;
                    var t = c.GetAttribute("t");
                    string? val = t switch
                    {
                        "inlineStr" => XmlUnescape(string.Concat(c.SelectNodes(".//m:t", ns)!.Cast<XmlNode>().Select(n => n.InnerText))),
                        "s" => int.TryParse(c.SelectSingleNode("m:v", ns)?.InnerText, out var si) && si < shared.Count ? shared[si] : "",
                        "b" => c.SelectSingleNode("m:v", ns)?.InnerText == "1" ? "1" : "0",
                        _ => c.SelectSingleNode("m:v", ns)?.InnerText,
                    };
                    while (cells.Count <= ci) cells.Add(null);
                    cells[ci] = val;
                }
                rows.Add(cells);
            }
            if (rows.Count == 0) continue;
            var s = new Sheet { Name = sh.GetAttribute("name"), Header = rows[0].Select(h => h ?? "").ToList() };
            foreach (var r in rows.Skip(1))
            {
                if (r.All(string.IsNullOrEmpty)) continue;
                var arr = new object?[s.Header.Count];
                for (int i = 0; i < arr.Length && i < r.Count; i++) arr[i] = r[i];
                s.Rows.Add(arr);
            }
            result.Add(s);
        }
        return result;
    }
}
