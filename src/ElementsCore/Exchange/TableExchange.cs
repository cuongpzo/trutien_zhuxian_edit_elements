using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ElementsCore.Formats;

namespace ElementsCore.Exchange;

/// <summary>Result of an import: what changed and what was refused (with the sheet row).</summary>
public sealed class ImportResult
{
    public int RowsRead { get; set; }
    public int RowsUpdated { get; set; }
    public int RowsAdded { get; set; }
    public int CellsChanged { get; set; }
    public List<string> Errors { get; } = new();
    public List<string> Notes { get; } = new();

    public string Summary(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{title}");
        sb.AppendLine(L.F("Read {0:N0} rows · updated {1:N0} rows ({2:N0} cells) · added {3:N0} rows · errors {4:N0}.", RowsRead, RowsUpdated, CellsChanged, RowsAdded, Errors.Count));
        foreach (var n in Notes) sb.AppendLine("• " + n);
        if (Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(L.T("Skipped cells / rows:"));
            foreach (var e in Errors.Take(300)) sb.AppendLine("  " + e);
            if (Errors.Count > 300) sb.AppendLine("  " + L.F("… and {0:N0} more errors", Errors.Count - 300));
        }
        sb.AppendLine();
        sb.AppendLine(L.T("The changes are not written to the file yet – review them in “Edit history”, then Save (Ctrl+S)."));
        return sb.ToString();
    }
}

/// <summary>
/// Export / import of the generic tables (gshop, domain, path, task_npc, vipaward, dyn_tasks …).
/// Every exported row carries "#" (row position) and, when the table has one, its key column (e.g. id):
/// import finds the row by the key (or "#" when the key is missing / not unique) so edits made in
/// Excel go back to exactly the same record. Nested lists travel as JSON text (CSV/XLSX) or arrays (JSON).
/// </summary>
public static class TableExchange
{
    public const string RowCol = "#";
    private static readonly JsonSerializerOptions JsonOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Sheet ToSheet(RecordTable t)
    {
        var s = new Sheet { Name = t.Name };
        s.Header.Add(RowCol);
        s.Header.AddRange(t.Columns.Select(c => c.Name));
        for (int r = 0; r < t.Rows.Count; r++)
        {
            var row = new object?[t.Columns.Count + 1];
            row[0] = r;
            for (int c = 0; c < t.Columns.Count; c++) row[c + 1] = CellValue(t.Columns[c], t.Rows[r].Values[c]);
            s.Rows.Add(row);
        }
        return s;
    }

    private static object? CellValue(ColumnDef c, object? v) => v switch
    {
        byte[] b => Convert.ToHexString(b),
        List<Record> list => new RawJson(NestedJson(c, list)),
        _ => v,
    };

    private static string NestedJson(ColumnDef c, List<Record> list)
    {
        var sub = c.SubColumns ?? [];
        using var ms = new MemoryStream();
        using (var jw = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            jw.WriteStartArray();
            foreach (var rec in list)
            {
                jw.WriteStartObject();
                for (int i = 0; i < sub.Count; i++)
                {
                    jw.WritePropertyName(sub[i].Name);
                    switch (rec.Values[i])
                    {
                        case List<Record> inner: jw.WriteRawValue(NestedJson(sub[i], inner)); break;
                        case byte[] b: jw.WriteStringValue(Convert.ToHexString(b)); break;
                        case string str: jw.WriteStringValue(str); break;
                        case bool bo: jw.WriteBooleanValue(bo); break;
                        case float f when float.IsFinite(f): jw.WriteNumberValue(f); break;
                        case double d when double.IsFinite(d): jw.WriteNumberValue(d); break;
                        case null: jw.WriteNullValue(); break;
                        case ulong u: jw.WriteNumberValue(u); break;
                        case var n when sub[i].IsInteger: jw.WriteNumberValue(Convert.ToInt64(n)); break;
                        case var o: jw.WriteStringValue(RecordIO.Format(sub[i], o)); break;
                    }
                }
                jw.WriteEndObject();
            }
            jw.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void Export(DataFile f, IEnumerable<RecordTable> tables, string path, string kind) =>
        DataSheets.Write(path, tables.Select(ToSheet).ToList(), kind, f.Path);

    /// <summary>
    /// Imports sheets into the file's tables (matched by sheet name; a single sheet goes into
    /// <paramref name="fallback"/> when its name matches nothing). Only cells that differ are written.
    /// </summary>
    public static ImportResult Import(DataFile f, string path, RecordTable? fallback)
    {
        var res = new ImportResult();
        var sheets = DataSheets.Read(path);
        var safe = MiniXlsx.SafeNames(f.Tables.Select(t => t.Name));
        foreach (var s in sheets)
        {
            int ti = f.Tables.FindIndex(t => t.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
            if (ti < 0) ti = safe.FindIndex(n => n.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
            var table = ti >= 0 ? f.Tables[ti] : sheets.Count == 1 ? fallback : null;
            if (table == null) { res.Notes.Add(L.F("Skipped sheet \"{0}\": no table with that name.", s.Name)); continue; }
            ImportSheet(table, s, res);
        }
        return res;
    }

    private static void ImportSheet(RecordTable t, Sheet s, ImportResult res)
    {
        int rowCol = s.Col(RowCol);
        var colMap = t.Columns.Select((c, i) => (c, i, sc: s.Col(c.Name))).Where(x => x.sc >= 0).ToList();
        if (colMap.Count == 0) { res.Notes.Add(L.F("Sheet \"{0}\": no column matches a column name of table {1}.", s.Name, t.Name)); return; }

        // key: the table's key column when present in the sheet and unique in the table
        int key = t.KeyColumn;
        int keySc = key >= 0 ? s.Col(t.Columns[key].Name) : -1;
        Dictionary<string, Record>? byKey = null;
        if (keySc >= 0)
        {
            var groups = t.Rows.GroupBy(r => RecordIO.Format(t.Columns[key], r.Values[key])).ToList();
            if (groups.All(g => g.Count() == 1)) byKey = groups.ToDictionary(g => g.Key, g => g.First());
            else res.Notes.Add(L.F("{0}: key column \"{1}\" has duplicate values – matching rows by column \"#\".", t.Name, t.Columns[key].Name));
        }
        if (byKey == null && rowCol < 0) { res.Notes.Add(L.F("{0}: missing column \"#\" and the key column – no row can be matched.", t.Name)); return; }
        res.Notes.Add(L.F("{0}: matching rows by {1}.", t.Name, byKey != null ? L.F("key column \"{0}\"", t.Columns[key].Name) : L.T("column \"#\"")));

        for (int r = 0; r < s.Rows.Count; r++)
        {
            var row = s.Rows[r];
            string where = "[" + L.F("{0} row {1}", s.Name, r + 2) + "]";
            res.RowsRead++;
            Record? target = null;
            if (byKey != null)
            {
                var k = s.Cell(row, keySc)?.Trim() ?? "";
                if (RecordIO.TryParse(t.Columns[key], k, out var kv, out _)) k = RecordIO.Format(t.Columns[key], kv);
                byKey.TryGetValue(k, out target);
                if (target == null)
                {
                    if (!t.CanAddRemove || k.Length == 0) { res.Errors.Add(where + " " + L.F("no {0} = {1} in the table.", t.Columns[key].Name, k)); continue; }
                    target = t.NewRecord();
                    t.Rows.Add(target);
                    byKey[k] = target;
                    res.RowsAdded++;
                }
            }
            else
            {
                if (!int.TryParse(s.Cell(row, rowCol), out int ri) || ri < 0 || ri >= t.Rows.Count) { res.Errors.Add(where + " " + L.T("invalid column \"#\".")); continue; }
                target = t.Rows[ri];
                // safety: when a key column is in the sheet, it must still match this row
                if (keySc >= 0 && s.Cell(row, keySc) is { } kk && RecordIO.TryParse(t.Columns[key], kk, out var kv2, out _)
                    && RecordIO.Format(t.Columns[key], kv2) != RecordIO.Format(t.Columns[key], target.Values[key]))
                { res.Errors.Add(where + " " + L.F("row #{0} now has a different {1} ({2}) – skipped to avoid overwriting the wrong row.", ri, t.Columns[key].Name, RecordIO.Format(t.Columns[key], target.Values[key]))); continue; }
            }

            int changed = 0;
            foreach (var (c, ci, sc) in colMap)
            {
                if (c.ReadOnly) continue;
                var text = s.Cell(row, sc);
                if (text == null) continue; // empty cell = keep
                if (c.Type == ColType.Table)
                {
                    if (!TryParseNested(c, text, out var list, out var err)) { res.Errors.Add($"{where} {c.Name}: {err}"); continue; }
                    if (NestedJson(c, target.Table(ci)) == NestedJson(c, list!)) continue;
                    target[ci] = list;
                    changed++;
                    continue;
                }
                if (!RecordIO.TryParse(c, text, out var v, out var e)) { res.Errors.Add($"{where} {c.Name} = \"{Clip(text)}\": {e}"); continue; }
                if (RecordIO.Format(c, v) == RecordIO.Format(c, target.Values[ci])) continue;
                target[ci] = v;
                changed++;
            }
            if (changed > 0) { res.RowsUpdated++; res.CellsChanged += changed; }
        }
    }

    private static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>Parses a nested list from JSON text (array of objects keyed by sub-column name).</summary>
    public static bool TryParseNested(ColumnDef c, string json, out List<Record>? list, out string? error)
    {
        list = null; error = null;
        var sub = c.SubColumns ?? [];
        try
        {
            using var doc = JsonDocument.Parse(json.Length == 0 ? "[]" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { error = L.T("expected a JSON array [ {...}, … ]"); return false; }
            var result = new List<Record>();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                var rec = Record.CreateDefault(sub);
                if (o.ValueKind != JsonValueKind.Object) { error = L.T("every element must be an object"); return false; }
                foreach (var p in o.EnumerateObject())
                {
                    int i = -1;
                    for (int k = 0; k < sub.Count; k++) if (sub[k].Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
                    if (i < 0) continue;
                    if (sub[i].Type == ColType.Table)
                    {
                        if (!TryParseNested(sub[i], p.Value.GetRawText(), out var inner, out error)) return false;
                        rec.Values[i] = inner;
                        continue;
                    }
                    var text = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString() ?? "",
                        JsonValueKind.True => "1",
                        JsonValueKind.False => "0",
                        _ => p.Value.GetRawText(),
                    };
                    if (!RecordIO.TryParse(sub[i], text, out var v, out var e)) { error = $"{sub[i].Name}: {e}"; return false; }
                    rec.Values[i] = v;
                }
                result.Add(rec);
            }
            list = result;
            return true;
        }
        catch (JsonException ex) { error = L.T("JSON error:") + " " + ex.Message; return false; }
    }
}
