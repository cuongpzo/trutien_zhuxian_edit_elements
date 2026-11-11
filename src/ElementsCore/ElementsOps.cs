using System.Text.Encodings.Web;
using System.Text.Json;

namespace ElementsCore;

public sealed record SearchHit(
    bool IsDialog,
    int ListIndex,
    string ListName,
    int ItemIndex,
    long Id,
    string FieldPath,
    string MatchedText);

public sealed record ReplacePreview(
    TextEntry Entry,
    string After,
    bool TooLong);

public sealed record DiffEntry(
    string Kind, // "+", "-", "~"
    int ListIndex,
    string ListName,
    long Id,
    string Name);

public static class ElementsOps
{
    public static List<SearchHit> Search(ElementsFile ef, string query, bool onlyTextFields = true, int maxHits = 2000)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        bool isNumeric = long.TryParse(query.Trim(), out long targetId);

        foreach (var l in ef.Lists)
        {
            var textFields = l.Schema.Fields.Where(f => f.IsText).ToList();
            for (int i = 0; i < l.Items.Count; i++)
            {
                long id = l.GetId(i);
                if (isNumeric && id == targetId)
                {
                    hits.Add(new SearchHit(false, l.Index, l.Name, i, id, "id", l.GetName(i)));
                    if (hits.Count >= maxHits) return hits;
                }
                foreach (var f in textFields)
                {
                    var val = FieldCodec.GetText(l.Items[i], f);
                    if (val.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(new SearchHit(false, l.Index, l.Name, i, id, f.Path, val));
                        if (hits.Count >= maxHits) return hits;
                        break; // one hit per item keeps list readable
                    }
                }
                if (!onlyTextFields && !isNumeric)
                {
                    // optional non-text search disabled by default for speed
                }
            }
        }

        if (ef.Dialogs != null)
        {
            for (int d = 0; d < ef.Dialogs.Count; d++)
            {
                var dlg = ef.Dialogs[d];
                if ((isNumeric && dlg.Id == targetId) || dlg.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new SearchHit(true, -1, "NPC_DIALOG", d, dlg.Id, "name", dlg.Name));
                    if (hits.Count >= maxHits) return hits;
                }
                for (int w = 0; w < dlg.Windows.Count; w++)
                {
                    var win = dlg.Windows[w];
                    if (win.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(new SearchHit(true, -1, "NPC_DIALOG", d, dlg.Id, $"win[{w}]", win.Text));
                        if (hits.Count >= maxHits) return hits;
                    }
                    for (int o = 0; o < win.Options.Count; o++)
                    {
                        if (win.Options[o].Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            hits.Add(new SearchHit(true, -1, "NPC_DIALOG", d, dlg.Id, $"win[{w}].opt[{o}]", win.Options[o].Text));
                            if (hits.Count >= maxHits) return hits;
                        }
                    }
                }
            }
        }
        return hits;
    }

    public static List<ReplacePreview> PreviewReplace(
        ElementsFile ef,
        string find,
        string replace,
        ISet<int>? lists = null,
        bool includeDialogs = true,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var list = new List<ReplacePreview>();
        if (string.IsNullOrEmpty(find)) return list;
        foreach (var e in TextExchange.Enumerate(ef, lists, includeDialogs))
        {
            if (!e.Value.Contains(find, comparison)) continue;
            var after = e.Value.Replace(find, replace, comparison);
            if (after == e.Value) continue;
            bool tooLong = e.MaxLength >= 0 && after.Length > e.MaxLength;
            list.Add(new ReplacePreview(e, after, tooLong));
        }
        return list;
    }

    public static (int applied, int skippedTooLong) ApplyReplace(IEnumerable<ReplacePreview> previews)
    {
        int applied = 0, skipped = 0;
        foreach (var p in previews)
        {
            if (p.TooLong) { skipped++; continue; }
            try { p.Entry.Set(p.After); applied++; }
            catch { skipped++; }
        }
        return (applied, skipped);
    }

    public static void ExportListToJson(ElementList list, ElementsFile ef, string outPath)
    {
        using var fs = File.Create(outPath);
        using var jw = new Utf8JsonWriter(fs, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        jw.WriteStartObject();
        jw.WriteNumber("elementsVersion", ef.Version);
        jw.WriteNumber("list", list.Index);
        jw.WriteString("structName", list.Name);
        jw.WriteNumber("recordSize", list.ItemSize);
        jw.WriteNumber("count", list.Items.Count);
        jw.WriteStartArray("records");
        for (int i = 0; i < list.Items.Count; i++)
        {
            var raw = list.Items[i];
            jw.WriteStartObject();
            jw.WriteNumber("_row", i);
            foreach (var f in list.Schema.Fields)
            {
                var k = FieldCodec.EffectiveKind(raw, f, list.Schema);
                switch (k)
                {
                    case Schema.FieldKind.F32:
                        jw.WriteNumber(f.Path, System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(f.Offset)));
                        break;
                    case Schema.FieldKind.F64:
                        jw.WriteNumber(f.Path, System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(f.Offset)));
                        break;
                    case Schema.FieldKind.I8 or Schema.FieldKind.U8 or Schema.FieldKind.I16 or Schema.FieldKind.U16 or Schema.FieldKind.I32 or Schema.FieldKind.U32 or Schema.FieldKind.I64:
                        jw.WriteNumber(f.Path, FieldCodec.ReadInteger(raw, f, k));
                        break;
                    case Schema.FieldKind.U64:
                        jw.WriteNumber(f.Path, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(f.Offset)));
                        break;
                    case Schema.FieldKind.Bool:
                        jw.WriteBoolean(f.Path, raw[f.Offset] != 0);
                        break;
                    default:
                        jw.WriteString(f.Path, FieldCodec.Format(raw, f, list.Schema));
                        break;
                }
            }
            jw.WriteEndObject();
        }
        jw.WriteEndArray();
        jw.WriteEndObject();
    }

    public static List<DiffEntry> Diff(ElementsFile current, ElementsFile baseline, int maxEntries = 5000)
    {
        var diffs = new List<DiffEntry>();
        int count = Math.Min(current.Lists.Count, baseline.Lists.Count);
        for (int li = 0; li < count; li++)
        {
            var cur = current.Lists[li];
            var bas = baseline.Lists[li];

            var baseById = new Dictionary<long, List<byte[]>>();
            for (int i = 0; i < bas.Items.Count; i++)
            {
                long id = bas.GetId(i);
                if (!baseById.TryGetValue(id, out var arr)) { arr = new List<byte[]>(); baseById[id] = arr; }
                arr.Add(bas.Items[i]);
            }

            var curMatched = new HashSet<long>();
            for (int i = 0; i < cur.Items.Count; i++)
            {
                long id = cur.GetId(i);
                curMatched.Add(id);
                if (!baseById.TryGetValue(id, out var arr))
                {
                    diffs.Add(new DiffEntry("+", cur.Index, cur.Name, id, cur.GetName(i)));
                }
                else if (arr.Count == 1 && !cur.Items[i].AsSpan().SequenceEqual(arr[0]))
                {
                    diffs.Add(new DiffEntry("~", cur.Index, cur.Name, id, cur.GetName(i)));
                }
                if (diffs.Count >= maxEntries) return diffs;
            }

            for (int i = 0; i < bas.Items.Count; i++)
            {
                long id = bas.GetId(i);
                if (!curMatched.Contains(id))
                {
                    diffs.Add(new DiffEntry("-", cur.Index, cur.Name, id, bas.GetName(i)));
                    if (diffs.Count >= maxEntries) return diffs;
                }
            }
        }
        return diffs;
    }
}
