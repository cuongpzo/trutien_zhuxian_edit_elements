using ElementsCore;
using ElementsCore.Schema;

namespace ElementsEditor;

/// <summary>
/// Elements tab icons (like JD IDE): the item icon next to the name in the record list, the icon in
/// the "file_icon" field row, and the resource path (path.data) of every "file_*" path-ID field.
/// </summary>
public sealed partial class MainForm
{
    private Dictionary<long, long> _itemIcons = new();

    private void SetupIcons()
    {
        GameIcons.Ready += () =>
        {
            _itemsGrid.Invalidate();
            _fieldsGrid.Invalidate();
            if (_activeModule == "elements" && _ef != null) _lblStatus.Text = (_lblStatus.Text ?? "").Split("  ·  icon:")[0] + "  ·  icon: " + GameIcons.Status;
        };
        GameIcons.ItemIcon = id => _itemIcons.TryGetValue(id, out var p) ? p : null;
        GameIcons.EnsureLoaded(ClientRoot());

        _itemsGrid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 2 || e.Graphics == null || _currentList == null) return;
            if (e.RowIndex >= _filteredItemIndices.Count || IconField(_currentList) is not { } f) return;
            var item = _currentList.Items[_filteredItemIndices[e.RowIndex]];
            var img = GameIcons.ForPathId(SafeInt(item, f, _currentList.Schema), 22);
            PaintWithIcon(e, img);
        };
        _fieldsGrid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 3 || e.Graphics == null || _currentList == null || _currentItemIndex < 0) return;
            if (e.RowIndex >= _filteredFields.Count || !IsIconField(_filteredFields[e.RowIndex])) return;
            var item = _currentList.Items[_currentItemIndex];
            PaintWithIcon(e, GameIcons.ForPathId(SafeInt(item, _filteredFields[e.RowIndex], _currentList.Schema), 22));
        };
        // note column: the path.data resource of path-ID fields (file_matter, file_icon, file_model…)
        _fieldsGrid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 5 || _currentList == null || _currentItemIndex < 0 || e.RowIndex >= _filteredFields.Count) return;
            var f = _filteredFields[e.RowIndex];
            if (!IsPathIdField(f)) return;
            long id = SafeInt(_currentList.Items[_currentItemIndex], f, _currentList.Schema);
            if (id <= 0 || GameIcons.PathOf(id) is not { } path) return;
            e.Value = $"📁 {path}" + (e.Value is string s && s.Length > 0 ? $"  |  {s}" : "");
            e.FormattingApplied = true;
        };
    }

    private static string LastSegment(FieldDef f) => f.Path[(f.Path.LastIndexOf('.') + 1)..];
    /// <summary>A path.data ID that points at an icon: file_icon…, or a nested "….icon" (lottery candidates …).</summary>
    private static bool IsIconField(FieldDef f) => !f.IsText && f.Kind != FieldKind.Bytes && f.ByteSize == 4
        && (LastSegment(f).StartsWith("file_icon", StringComparison.OrdinalIgnoreCase) || LastSegment(f).Equals("icon", StringComparison.OrdinalIgnoreCase));
    private static bool IsPathIdField(FieldDef f) => IsIconField(f)
        || !f.IsText && f.Kind != FieldKind.Bytes && f.ByteSize == 4 && LastSegment(f).StartsWith("file_", StringComparison.OrdinalIgnoreCase);
    private static FieldDef? IconField(ElementList l) =>
        l.Schema.Fields.FirstOrDefault(f => IsIconField(f) && f.Path.StartsWith("file_icon", StringComparison.OrdinalIgnoreCase)) ?? l.Schema.Fields.FirstOrDefault(f => IsIconField(f) && !f.Path.Contains('.'));

    private static long SafeInt(byte[] item, FieldDef f, ListSchema s)
    {
        try { return long.TryParse(FieldCodec.Format(item, f, s), out var v) ? v : 0; } catch { return 0; }
    }

    /// <summary>Paints the cell normally with room for the icon at its left.</summary>
    private static void PaintWithIcon(DataGridViewCellPaintingEventArgs e, Image? img)
    {
        if (img == null || e.Graphics == null || e.CellStyle == null) return;
        var pad = e.CellStyle.Padding;
        e.CellStyle.Padding = new Padding(pad.Left + img.Width + 6, pad.Top, pad.Right, pad.Bottom);
        e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
        GameIcons.DrawInCell(e.Graphics, img, e.CellBounds, 4);
        e.Handled = true;
    }

    /// <summary>elements.data ID → icon path ID, for icons in the other tabs (gshop, vip …).</summary>
    private async void BuildItemIcons()
    {
        var ef = _ef;
        if (ef == null) return;
        var map = await Task.Run(() =>
        {
            var d = new Dictionary<long, long>();
            foreach (var l in ef.Lists)
            {
                var f = IconField(l);
                if (f == null || l.Schema.IdField == null) continue;
                for (int i = 0; i < l.Items.Count; i++)
                {
                    long id = l.GetId(i), pid = SafeInt(l.Items[i], f, l.Schema);
                    if (id > 0 && pid > 0) d.TryAdd(id, pid);
                }
            }
            return d;
        });
        if (_ef != ef) return;
        _itemIcons = map;
        GameIcons.EnsureLoaded(ClientRoot());
        _itemsGrid.Invalidate();
    }
}
