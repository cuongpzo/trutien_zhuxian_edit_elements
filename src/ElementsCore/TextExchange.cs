using ElementsCore.Schema;

namespace ElementsCore;

/// <summary>One translatable string inside the file.</summary>
public sealed class TextEntry
{
    /// <summary>Stable key: "L{list}:{item}:{field}" or "D{dialog}:name" / "D{d}:W{w}" / "D{d}:W{w}:O{o}".</summary>
    public required string Key { get; init; }
    public required string Group { get; init; }
    public long Id { get; init; }
    public required string Field { get; init; }
    /// <summary>Max characters, or -1 when unlimited (dialog window text).</summary>
    public int MaxLength { get; init; }
    public required string Value { get; init; }
    public required Action<string> Setter { get; init; }

    public void Set(string text) => Setter(text);
}

public sealed class ImportReport
{
    public int Applied, Unchanged, NotFound, TooLong, IdMismatch, SourceChanged;
    public List<string> Log { get; } = new();
    public override string ToString() =>
        $"Áp dụng: {Applied}, không đổi: {Unchanged}, không tìm thấy: {NotFound}, quá dài: {TooLong}, sai ID: {IdMismatch}, nguồn đã khác: {SourceChanged}";
}

public static class TextExchange
{
    public static bool ContainsCjk(string s) =>
        s.Any(c => (c >= 0x3400 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0x3000 && c <= 0x303F) || (c >= 0xFF00 && c <= 0xFFEF));

    /// <summary>All non-empty strings of the selected lists (null = all) and optionally NPC dialogs.</summary>
    public static IEnumerable<TextEntry> Enumerate(ElementsFile ef, ISet<int>? lists = null, bool dialogs = true)
    {
        foreach (var l in ef.Lists)
        {
            if (lists != null && !lists.Contains(l.Index)) continue;
            var textFields = l.Schema.Fields.Where(f => f.IsText).ToList();
            if (textFields.Count == 0) continue;
            for (int i = 0; i < l.Items.Count; i++)
            {
                var item = l.Items[i];
                long id = l.GetId(i);
                foreach (var f in textFields)
                {
                    var v = FieldCodec.GetText(item, f);
                    if (v.Length == 0) continue;
                    var it = item; var fd = f;
                    yield return new TextEntry
                    {
                        Key = $"L{l.Index}:{i}:{f.Path}",
                        Group = $"{l.Index} {l.Name}",
                        Id = id,
                        Field = f.Path,
                        MaxLength = FieldCodec.MaxTextLength(f),
                        Value = v,
                        Setter = t => FieldCodec.SetText(it, fd, t),
                    };
                }
            }
        }

        if (!dialogs || ef.Dialogs == null) yield break;
        for (int d = 0; d < ef.Dialogs.Count; d++)
        {
            var dlg = ef.Dialogs[d];
            if (dlg.Name.Length > 0)
                yield return new TextEntry { Key = $"D{d}:name", Group = "NPC_DIALOG", Id = dlg.Id, Field = "name", MaxLength = TalkProc.FixedChars - 1, Value = dlg.Name, Setter = t => dlg.Name = t };
            for (int w = 0; w < dlg.Windows.Count; w++)
            {
                var win = dlg.Windows[w];
                if (win.Text.Length > 0)
                    yield return new TextEntry { Key = $"D{d}:W{w}", Group = "NPC_DIALOG", Id = dlg.Id, Field = $"window[{win.Id}].text", MaxLength = -1, Value = win.Text, Setter = t => win.Text = t };
                for (int o = 0; o < win.Options.Count; o++)
                {
                    var opt = win.Options[o];
                    if (opt.Text.Length > 0)
                        yield return new TextEntry { Key = $"D{d}:W{w}:O{o}", Group = "NPC_DIALOG", Id = dlg.Id, Field = $"window[{win.Id}].option[{o}]", MaxLength = TalkProc.FixedChars - 1, Value = opt.Text, Setter = t => opt.Text = t };
                }
            }
        }
    }

    public static readonly string[] DetailedHeader = { "Key", "List", "ID", "Field", "MaxLen", "Source", "Translation" };
    public static readonly string[] GlossaryHeader = { "Source", "Translation", "Count", "MaxLen", "Example" };

    /// <summary>One row per string occurrence.</summary>
    public static int ExportDetailed(IEnumerable<TextEntry> entries, string path, bool onlyCjk)
    {
        int n = 0;
        using var w = Csv.CreateWriter(path);
        Csv.WriteRow(w, DetailedHeader);
        foreach (var e in entries)
        {
            if (onlyCjk && !ContainsCjk(e.Value)) continue;
            Csv.WriteRow(w, new[] { e.Key, e.Group, e.Id.ToString(), e.Field, e.MaxLength.ToString(), e.Value, "" });
            n++;
        }
        return n;
    }

    /// <summary>One row per distinct source string (translate once, apply everywhere).</summary>
    public static int ExportGlossary(IEnumerable<TextEntry> entries, string path, bool onlyCjk)
    {
        var map = new Dictionary<string, (int count, int max, string example)>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (onlyCjk && !ContainsCjk(e.Value)) continue;
            if (map.TryGetValue(e.Value, out var x))
                map[e.Value] = (x.count + 1, Min(x.max, e.MaxLength), x.example);
            else map[e.Value] = (1, e.MaxLength, e.Key);
        }
        using var w = Csv.CreateWriter(path);
        Csv.WriteRow(w, GlossaryHeader);
        foreach (var kv in map.OrderByDescending(k => k.Value.count).ThenBy(k => k.Key, StringComparer.Ordinal))
            Csv.WriteRow(w, new[] { kv.Key, "", kv.Value.count.ToString(), kv.Value.max.ToString(), kv.Value.example });
        return map.Count;
    }

    private static int Min(int a, int b) => a < 0 ? b : b < 0 ? a : Math.Min(a, b);

    /// <summary>Imports a CSV produced by ExportDetailed or ExportGlossary (auto-detected by header).</summary>
    public static ImportReport Import(ElementsFile ef, string path)
    {
        var rows = Csv.Read(path).ToList();
        var rep = new ImportReport();
        if (rows.Count == 0) { rep.Log.Add("File rỗng"); return rep; }
        var header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        int Col(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        int cSrc = Col("Source"), cTr = Col("Translation"), cKey = Col("Key"), cId = Col("ID");
        if (cSrc < 0 || cTr < 0) { rep.Log.Add("Thiếu cột Source/Translation"); return rep; }
        string Cell(List<string> r, int c) => c >= 0 && c < r.Count ? r[c] : "";

        var all = Enumerate(ef).ToList();
        if (cKey >= 0)
        {
            var byKey = new Dictionary<string, TextEntry>(all.Count, StringComparer.Ordinal);
            foreach (var item in all) byKey.TryAdd(item.Key, item);
            foreach (var r in rows.Skip(1))
            {
                var tr = Cell(r, cTr);
                if (tr.Length == 0) continue;
                var key = Cell(r, cKey);
                if (!byKey.TryGetValue(key, out var e)) { rep.NotFound++; rep.Log.Add($"{key}: không tìm thấy"); continue; }
                if (cId >= 0 && long.TryParse(Cell(r, cId), out var id) && id != e.Id)
                { rep.IdMismatch++; rep.Log.Add($"{key}: ID {id} ≠ {e.Id} (file đã thay đổi?) – bỏ qua"); continue; }
                if (Cell(r, cSrc) != e.Value && e.Value != tr)
                { rep.SourceChanged++; rep.Log.Add($"{key}: nguồn hiện tại '{e.Value}' khác CSV – vẫn áp dụng"); }
                Apply(e, tr, rep);
            }
        }
        else
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in rows.Skip(1))
            {
                var tr = Cell(r, cTr);
                if (tr.Length > 0) dict[Cell(r, cSrc)] = tr;
            }
            foreach (var e in all)
                if (dict.TryGetValue(e.Value, out var tr)) Apply(e, tr, rep);
        }
        return rep;
    }

    private static void Apply(TextEntry e, string tr, ImportReport rep)
    {
        if (tr == e.Value) { rep.Unchanged++; return; }
        if (e.MaxLength >= 0 && tr.Length > e.MaxLength)
        {
            rep.TooLong++;
            rep.Log.Add($"{e.Key} [{e.Field}]: '{tr}' dài {tr.Length} > {e.MaxLength} – bỏ qua");
            return;
        }
        try { e.Set(tr); rep.Applied++; }
        catch (Exception ex) { rep.TooLong++; rep.Log.Add($"{e.Key}: {ex.Message}"); }
    }
}
