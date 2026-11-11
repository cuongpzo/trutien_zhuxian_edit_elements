using System.Globalization;
using ElementsCore.Schema;

namespace ElementsCore.Exchange;

/// <summary>
/// Export / import of whole elements.data lists (every schema field) to CSV / XLSX / JSON.
/// Key columns: "_list" (list index), "ID" (the record id) and "_index" (position) – import finds the
/// record by ID (by _index when IDs repeat) inside the list named by _list (or by the sheet name).
/// </summary>
public static class ElementsExchange
{
    public const string ListCol = "_list", IndexCol = "_index";

    public static string SheetName(ElementList l) => $"{l.Index} {l.Name}";

    /// <summary>Column names = field paths (made unique), in schema order.</summary>
    public static List<(FieldDef f, string name)> Columns(ElementList l)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ListCol, IndexCol };
        var cols = new List<(FieldDef, string)>();
        foreach (var f in l.Schema.Fields)
        {
            var n = f.Path;
            if (!used.Add(n)) { n = $"{f.Path}@{f.Offset}"; used.Add(n); }
            cols.Add((f, n));
        }
        return cols;
    }

    public static Sheet ToSheet(ElementList l)
    {
        var cols = Columns(l);
        var s = new Sheet { Name = SheetName(l) };
        s.Header.Add(ListCol);
        s.Header.Add(IndexCol);
        s.Header.AddRange(cols.Select(c => c.name));
        for (int i = 0; i < l.Items.Count; i++)
        {
            var item = l.Items[i];
            var row = new object?[cols.Count + 2];
            row[0] = l.Index;
            row[1] = i;
            for (int c = 0; c < cols.Count; c++) row[c + 2] = Typed(item, cols[c].f, l.Schema);
            s.Rows.Add(row);
        }
        return s;
    }

    private static object Typed(byte[] item, FieldDef f, ListSchema schema)
    {
        var text = FieldCodec.Format(item, f, schema);
        var kind = FieldCodec.EffectiveKind(item, f, schema);
        if (kind is FieldKind.WStr or FieldKind.Str or FieldKind.Bytes or FieldKind.U64) return text;
        if (kind is FieldKind.F32 or FieldKind.F64) return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : text;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : text;
    }

    public static void Export(ElementsFile ef, IEnumerable<ElementList> lists, string path) =>
        DataSheets.Write(path, lists.Select(ToSheet).ToList(), "elements", ef.Path ?? "");

    /// <summary>Imports rows; <paramref name="changed"/> is called for every record written (to mark it dirty).</summary>
    public static ImportResult Import(ElementsFile ef, string path, ElementList? fallback, Action<ElementList, byte[]> changed)
    {
        var res = new ImportResult();
        foreach (var s in DataSheets.Read(path))
        {
            int listCol = s.Col(ListCol);
            ElementList? sheetList = null;
            var head = s.Name.Split(' ', 2)[0];
            if (int.TryParse(head, out int li)) sheetList = ef.Lists.FirstOrDefault(l => l.Index == li);
            sheetList ??= ef.Lists.FirstOrDefault(l => l.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
            if (listCol < 0 && sheetList == null) sheetList = fallback;
            if (listCol < 0 && sheetList == null) { res.Notes.Add(L.F("Skipped sheet \"{0}\": unknown list (missing column {1}).", s.Name, ListCol)); continue; }

            var states = new Dictionary<ElementList, ListState>();
            for (int r = 0; r < s.Rows.Count; r++)
            {
                var row = s.Rows[r];
                string where = "[" + L.F("{0} row {1}", s.Name, r + 2) + "]";
                res.RowsRead++;
                var list = sheetList;
                if (listCol >= 0 && int.TryParse(s.Cell(row, listCol), out int lix)) list = ef.Lists.FirstOrDefault(l => l.Index == lix);
                if (list == null) { res.Errors.Add(where + " " + L.F("{0} is invalid.", ListCol)); continue; }
                if (!states.TryGetValue(list, out var st)) states[list] = st = new ListState(list, s, res);
                st.Apply(row, where, changed);
            }
        }
        return res;
    }

    private sealed class ListState
    {
        private readonly ElementList _l;
        private readonly List<(FieldDef f, int sc, string name)> _cols;
        private readonly int _idSc, _indexSc;
        private readonly Dictionary<long, int>? _byId;
        private readonly ImportResult _res;

        public ListState(ElementList l, Sheet s, ImportResult res)
        {
            _l = l; _res = res;
            _cols = Columns(l).Select(c => (c.f, sc: s.Col(c.name), c.name)).Where(c => c.sc >= 0).ToList();
            var idf = l.Schema.IdField;
            _idSc = idf != null ? s.Col(Columns(l).First(c => c.f == idf).name) : -1;
            _indexSc = s.Col(IndexCol);
            if (_idSc >= 0)
            {
                var d = new Dictionary<long, int>();
                bool unique = true;
                for (int i = 0; i < l.Items.Count; i++) if (!d.TryAdd(l.GetId(i), i)) { unique = false; break; }
                if (unique) _byId = d;
                else res.Notes.Add(L.F("{0}: duplicate IDs in the list – matching by {1}.", SheetName(l), IndexCol));
            }
            res.Notes.Add(L.F("{0}: matching records by {1}, {2} columns match field names.", SheetName(l), _byId != null ? "ID" : IndexCol, _cols.Count));
        }

        public void Apply(object?[] row, string where, Action<ElementList, byte[]> changed)
        {
            int idx = -1;
            bool added = false;
            if (_byId != null && long.TryParse(Sheet.CellText(row[_idSc])?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            {
                if (!_byId.TryGetValue(id, out idx))
                {
                    // new record: start from zeros, the ID column is written below like any other field
                    _l.Items.Add(new byte[_l.ItemSize]);
                    idx = _l.Items.Count - 1;
                    _byId[id] = idx;
                    added = true;
                }
            }
            else if (_indexSc >= 0 && int.TryParse(Sheet.CellText(row[_indexSc]), out int ix) && ix >= 0 && ix < _l.Items.Count)
            {
                idx = ix;
                if (_idSc >= 0 && long.TryParse(Sheet.CellText(row[_idSc]), out long want) && _l.GetId(ix) != want)
                { _res.Errors.Add(where + " " + L.F("position {0} now has ID {1} ≠ {2} – skipped to avoid overwriting the wrong record.", ix, _l.GetId(ix), want)); return; }
            }
            if (idx < 0) { _res.Errors.Add(where + " " + L.F("cannot identify the record (missing ID / {0}).", IndexCol)); return; }

            var item = _l.Items[idx];
            int n = 0;
            foreach (var (f, sc, name) in _cols)
            {
                var text = sc < row.Length ? Sheet.CellText(row[sc]) : null;
                if (text == null) continue;
                try
                {
                    if (FieldCodec.Format(item, f, _l.Schema) == text) continue;
                    if (FieldCodec.SetFromString(item, f, _l.Schema, text)) n++;
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or TextTooLongException or InvalidOperationException)
                {
                    _res.Errors.Add($"{where} {name} = \"{(text.Length > 40 ? text[..40] + "…" : text)}\": {ex.Message}");
                }
            }
            if (added) { _res.RowsAdded++; changed(_l, item); }
            else if (n > 0) { _res.RowsUpdated++; changed(_l, item); }
            _res.CellsChanged += n;
        }
    }
}
