using ElementsCore.Formats;
using ElementsCore.Schema;

namespace ElementsCore.Exchange;

public enum ChangeKind { Modified, Added, Deleted }

/// <summary>One difference between the open document and the file as it was opened (or last saved).</summary>
public sealed class ChangeItem
{
    /// <summary>Table / list name, e.g. "Items" or "3 EQUIPMENT_ESSENCE".</summary>
    public required string Group { get; init; }
    public int GroupOrder { get; init; }
    public ChangeKind Kind { get; init; }
    /// <summary>"ID 24172" / "#12".</summary>
    public string RecordKey { get; init; } = "";
    public string RecordName { get; init; } = "";
    /// <summary>Field path; null for whole-record add/delete.</summary>
    public string? Field { get; init; }
    public string? Old { get; init; }
    public string? New { get; init; }
    /// <summary>Puts this change back as it was in the file.</summary>
    public Action? Revert { get; init; }
    public string RevertLabel { get; init; } = "Hoàn tác";
    /// <summary>Navigation target (Record, byte[] item, TalkWindow …) for "go to".</summary>
    public object? Target { get; init; }
    public int ListIndex { get; init; } = -1;
}

/// <summary>Field-level diff of the generic tables against a baseline parse of the original bytes.</summary>
public static class TableDiff
{
    public static List<ChangeItem> Compute(DataFile cur, DataFile baseline)
    {
        var list = new List<ChangeItem>();
        for (int t = 0; t < cur.Tables.Count && t < baseline.Tables.Count; t++)
            CompareTable(cur.Tables[t], baseline.Tables[t], t, list);
        return list;
    }

    private static string KeyText(RecordTable t, Record r, int index) =>
        t.KeyColumn >= 0 && r.Values[t.KeyColumn] != null ? $"{t.Columns[t.KeyColumn].Name} {RecordIO.Format(t.Columns[t.KeyColumn], r.Values[t.KeyColumn])}" : $"#{index}";

    private static string NameText(RecordTable t, Record r) => t.NameColumn >= 0 ? r.Text(t.NameColumn) : "";

    private static void CompareTable(RecordTable cur, RecordTable bas, int order, List<ChangeItem> outp)
    {
        // pair rows by unique key, else by position
        var pairs = new List<(Record? c, Record? b, int bi)>();
        int k = cur.KeyColumn;
        bool keyed = false;
        if (k >= 0)
        {
            var bk = new Dictionary<string, int>();
            bool unique = true;
            for (int i = 0; i < bas.Rows.Count && unique; i++) unique = bk.TryAdd(RecordIO.Format(bas.Columns[k], bas.Rows[i].Values[k]), i);
            var ck = new HashSet<string>();
            for (int i = 0; i < cur.Rows.Count && unique; i++) unique = ck.Add(RecordIO.Format(cur.Columns[k], cur.Rows[i].Values[k]));
            if (unique)
            {
                keyed = true;
                var seen = new HashSet<int>();
                foreach (var c in cur.Rows)
                {
                    if (bk.TryGetValue(RecordIO.Format(cur.Columns[k], c.Values[k]), out int bi)) { pairs.Add((c, bas.Rows[bi], bi)); seen.Add(bi); }
                    else pairs.Add((c, null, -1));
                }
                for (int i = 0; i < bas.Rows.Count; i++) if (!seen.Contains(i)) pairs.Add((null, bas.Rows[i], i));
            }
        }
        if (!keyed)
        {
            int n = Math.Max(cur.Rows.Count, bas.Rows.Count);
            for (int i = 0; i < n; i++)
                pairs.Add((i < cur.Rows.Count ? cur.Rows[i] : null, i < bas.Rows.Count ? bas.Rows[i] : null, i < bas.Rows.Count ? i : -1));
        }

        foreach (var (c, b, bi) in pairs)
        {
            if (c != null && b == null)
            {
                outp.Add(new ChangeItem
                {
                    Group = cur.Name, GroupOrder = order, Kind = ChangeKind.Added, RecordKey = KeyText(cur, c, cur.Rows.IndexOf(c)), RecordName = NameText(cur, c),
                    Revert = cur.CanAddRemove ? () => cur.Rows.Remove(c) : null, RevertLabel = "Bỏ dòng thêm", Target = c,
                });
                continue;
            }
            if (c == null && b != null)
            {
                outp.Add(new ChangeItem
                {
                    Group = cur.Name, GroupOrder = order, Kind = ChangeKind.Deleted, RecordKey = KeyText(bas, b, bi), RecordName = NameText(bas, b),
                    Revert = cur.CanAddRemove ? () => cur.Rows.Insert(Math.Min(bi, cur.Rows.Count), b.Clone()) : null, RevertLabel = "Khôi phục dòng",
                });
                continue;
            }
            if (c == null || b == null) continue;
            for (int col = 0; col < cur.Columns.Count; col++)
            {
                var cd = cur.Columns[col];
                if (cd.ReadOnly) continue;
                if (cd.Type == ColType.Table)
                {
                    var cl = c.Table(col); var bl = b.Table(col);
                    var diffs = NestedDiff(cd, cl, bl);
                    if (diffs.Count == 0) continue;
                    int colCopy = col;
                    Action revert = () => { c.Values[colCopy] = bl.Select(x => x.Clone()).ToList(); c.Raw[colCopy] = null; };
                    foreach (var (field, o, n) in diffs)
                        outp.Add(new ChangeItem
                        {
                            Group = cur.Name, GroupOrder = order, Kind = ChangeKind.Modified, RecordKey = KeyText(cur, c, cur.Rows.IndexOf(c)), RecordName = NameText(cur, c),
                            Field = field, Old = o, New = n, Revert = revert, RevertLabel = $"Hoàn tác cả {cd.Name}", Target = c,
                        });
                    continue;
                }
                var ov = RecordIO.Format(cd, b.Values[col]);
                var nv = RecordIO.Format(cd, c.Values[col]);
                if (ov == nv) continue;
                int cc = col;
                outp.Add(new ChangeItem
                {
                    Group = cur.Name, GroupOrder = order, Kind = ChangeKind.Modified, RecordKey = KeyText(cur, c, cur.Rows.IndexOf(c)), RecordName = NameText(cur, c),
                    Field = cd.Name, Old = ov, New = nv, Target = c,
                    Revert = () => { c.Values[cc] = b.Values[cc] is byte[] bb ? (byte[])bb.Clone() : b.Values[cc]; c.Raw[cc] = (byte[]?)b.Raw[cc]?.Clone(); },
                });
            }
        }
    }

    private static List<(string field, string? o, string? n)> NestedDiff(ColumnDef cd, List<Record> cl, List<Record> bl)
    {
        var res = new List<(string, string?, string?)>();
        var sub = cd.SubColumns ?? [];
        int n = Math.Max(cl.Count, bl.Count);
        for (int i = 0; i < n; i++)
        {
            if (i >= bl.Count) { res.Add(($"{cd.Name}[{i}]", null, "+ " + RowSummary(sub, cl[i]))); continue; }
            if (i >= cl.Count) { res.Add(($"{cd.Name}[{i}]", "− " + RowSummary(sub, bl[i]), null)); continue; }
            for (int s = 0; s < sub.Count; s++)
            {
                string o = sub[s].Type == ColType.Table ? $"[{bl[i].Table(s).Count}]" : RecordIO.Format(sub[s], bl[i].Values[s]);
                string v = sub[s].Type == ColType.Table ? $"[{cl[i].Table(s).Count}]" : RecordIO.Format(sub[s], cl[i].Values[s]);
                if (sub[s].Type == ColType.Table && o == v && NestedDiff(sub[s], cl[i].Table(s), bl[i].Table(s)).Count > 0) v += " (đã sửa)";
                if (o != v) res.Add(($"{cd.Name}[{i}].{sub[s].Name}", o, v));
            }
            if (res.Count > 200) break;
        }
        return res;
    }

    private static string RowSummary(IReadOnlyList<ColumnDef> sub, Record r) =>
        string.Join(", ", sub.Select((c, i) => c.Type == ColType.Table ? $"{c.Name}[{r.Table(i).Count}]" : $"{c.Name}={RecordIO.Format(c, r.Values[i])}").Take(6));

    /// <summary>Changed line blocks of a text file (common prefix / suffix trimmed).</summary>
    public static List<ChangeItem> TextDiff(TextDataFile cur, string currentText, string originalText, Action revertAll)
    {
        var a = originalText.Replace("\r\n", "\n").Split('\n');
        var b = currentText.Replace("\r\n", "\n").Split('\n');
        int p = 0;
        while (p < a.Length && p < b.Length && a[p] == b[p]) p++;
        int s = 0;
        while (s < a.Length - p && s < b.Length - p && a[^(s + 1)] == b[^(s + 1)]) s++;
        var res = new List<ChangeItem>();
        if (p == a.Length && p == b.Length) return res;
        int n = Math.Max(a.Length - p - s, b.Length - p - s);
        for (int i = 0; i < n && i < 500; i++)
        {
            string? o = p + i < a.Length - s ? a[p + i] : null;
            string? v = p + i < b.Length - s ? b[p + i] : null;
            res.Add(new ChangeItem
            {
                Group = System.IO.Path.GetFileName(cur.Path), Kind = o == null ? ChangeKind.Added : v == null ? ChangeKind.Deleted : ChangeKind.Modified,
                RecordKey = $"dòng {p + i + 1}", Field = "", Old = o, New = v, Revert = revertAll, RevertLabel = "Hoàn tác cả file", Target = p + i,
            });
        }
        return res;
    }
}

/// <summary>Field-level diff of elements.data (lists + NPC dialogs) against the file as opened.</summary>
public static class ElementsDiff
{
    public static List<ChangeItem> Compute(ElementsFile cur, ElementsFile bas, Action<ElementList, byte[]> touched)
    {
        var outp = new List<ChangeItem>();
        for (int li = 0; li < cur.Lists.Count && li < bas.Lists.Count; li++)
        {
            var cl = cur.Lists[li]; var bl = bas.Lists[li];
            string group = $"{cl.Index} {cl.Name}";
            if (cl.Items.Count == bl.Items.Count)
            {
                for (int i = 0; i < cl.Items.Count; i++)
                    if (!cl.Items[i].AsSpan().SequenceEqual(bl.Items[i])) FieldDiff(cl, cl.Items[i], bl.Items[i], group, li, outp, touched);
                continue;
            }
            // different count: pair by ID
            var bmap = new Dictionary<long, int>();
            for (int i = 0; i < bl.Items.Count; i++) bmap.TryAdd(bl.GetId(i), i);
            var seen = new HashSet<int>();
            for (int i = 0; i < cl.Items.Count; i++)
            {
                var item = cl.Items[i];
                if (bmap.TryGetValue(cl.GetId(i), out int bi) && seen.Add(bi))
                {
                    if (!item.AsSpan().SequenceEqual(bl.Items[bi])) FieldDiff(cl, item, bl.Items[bi], group, li, outp, touched);
                }
                else
                {
                    long id = cl.GetId(i);
                    outp.Add(new ChangeItem
                    {
                        Group = group, GroupOrder = li, Kind = ChangeKind.Added, RecordKey = $"ID {id}", RecordName = cl.GetName(i), Target = item, ListIndex = cl.Index,
                        Revert = () => cl.Items.Remove(item), RevertLabel = "Bỏ bản ghi thêm",
                    });
                }
            }
            for (int bi = 0; bi < bl.Items.Count; bi++)
            {
                if (seen.Contains(bi)) continue;
                var orig = bl.Items[bi];
                int at = bi;
                outp.Add(new ChangeItem
                {
                    Group = group, GroupOrder = li, Kind = ChangeKind.Deleted, RecordKey = $"ID {bl.GetId(bi)}", RecordName = bl.GetName(bi), ListIndex = cl.Index,
                    Revert = () => { var copy = (byte[])orig.Clone(); cl.Items.Insert(Math.Min(at, cl.Items.Count), copy); touched(cl, copy); }, RevertLabel = "Khôi phục bản ghi",
                });
            }
        }
        DialogDiff(cur, bas, outp);
        return outp;
    }

    private static void FieldDiff(ElementList l, byte[] item, byte[] orig, string group, int order, List<ChangeItem> outp, Action<ElementList, byte[]> touched)
    {
        long id = l.Schema.IdField != null ? (l.Schema.IdField.Kind == FieldKind.Bytes ? BitConverter.ToInt32(item, l.Schema.IdField.Offset) : FieldCodec.ReadInteger(item, l.Schema.IdField)) : -1;
        string key = id >= 0 ? $"ID {id}" : "";
        string name = l.Schema.NameField != null ? FieldCodec.GetText(item, l.Schema.NameField) : "";
        int before = outp.Count;
        var covered = new bool[item.Length];
        foreach (var f in l.Schema.Fields)
        {
            int end = Math.Min(f.Offset + f.ByteSize, item.Length);
            for (int k = f.Offset; k < end; k++) covered[k] = true;
            if (f.Offset >= item.Length || item.AsSpan(f.Offset, end - f.Offset).SequenceEqual(orig.AsSpan(f.Offset, end - f.Offset))) continue;
            var fd = f;
            outp.Add(new ChangeItem
            {
                Group = group, GroupOrder = order, Kind = ChangeKind.Modified, RecordKey = key, RecordName = name, ListIndex = l.Index,
                Field = f.Path, Old = SafeFormat(orig, f, l.Schema), New = SafeFormat(item, f, l.Schema), Target = item,
                Revert = () => { Array.Copy(orig, fd.Offset, item, fd.Offset, end - fd.Offset); touched(l, item); },
            });
        }
        bool uncovered = false;
        for (int k = 0; k < item.Length && !uncovered; k++) uncovered = !covered[k] && item[k] != orig[k];
        if (uncovered || outp.Count == before)
            outp.Add(new ChangeItem
            {
                Group = group, GroupOrder = order, Kind = ChangeKind.Modified, RecordKey = key, RecordName = name, ListIndex = l.Index,
                Field = "(byte ngoài schema)", Old = "…", New = "…", Target = item, RevertLabel = "Hoàn tác cả bản ghi",
                Revert = () => { Array.Copy(orig, item, item.Length); touched(l, item); },
            });
    }

    private static string SafeFormat(byte[] item, FieldDef f, ListSchema s)
    {
        try { return FieldCodec.Format(item, f, s); } catch { return "?"; }
    }

    private static void DialogDiff(ElementsFile cur, ElementsFile bas, List<ChangeItem> outp)
    {
        if (cur.Dialogs == null || bas.Dialogs == null) return;
        const string group = "NPC Dialogs";
        int order = cur.Lists.Count;
        for (int d = 0; d < Math.Min(cur.Dialogs.Count, bas.Dialogs.Count); d++)
        {
            var cd = cur.Dialogs[d]; var bd = bas.Dialogs[d];
            string key = $"Dialog {cd.Id}";
            if (cd.Name != bd.Name)
            {
                var raw = (byte[])bd.NameRaw.Clone();
                outp.Add(new ChangeItem { Group = group, GroupOrder = order, RecordKey = key, RecordName = cd.Name, Field = "name", Old = bd.Name, New = cd.Name, Target = cd, Revert = () => cd.NameRaw = raw });
            }
            for (int w = 0; w < Math.Min(cd.Windows.Count, bd.Windows.Count); w++)
            {
                var cw = cd.Windows[w]; var bw = bd.Windows[w];
                if (cw.RawText != bw.RawText)
                {
                    var orig = bw.RawText;
                    outp.Add(new ChangeItem { Group = group, GroupOrder = order, RecordKey = key, RecordName = cd.Name, Field = $"window {cw.Id}", Old = bw.Text, New = cw.Text, Target = cw, Revert = () => cw.RawText = orig });
                }
                for (int o = 0; o < Math.Min(cw.Options.Count, bw.Options.Count); o++)
                {
                    var co = cw.Options[o]; var bo = bw.Options[o];
                    if (co.Text == bo.Text) continue;
                    var raw = (byte[])bo.TextRaw.Clone();
                    outp.Add(new ChangeItem { Group = group, GroupOrder = order, RecordKey = key, RecordName = cd.Name, Field = $"window {cw.Id} · option {co.Id}", Old = bo.Text, New = co.Text, Target = cw, Revert = () => co.TextRaw = raw });
                }
            }
        }
    }
}
