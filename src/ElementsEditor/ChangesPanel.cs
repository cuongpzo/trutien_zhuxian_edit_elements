using System.Reflection;
using ElementsCore;
using ElementsCore.Exchange;

namespace ElementsEditor;

/// <summary>
/// "Edit history" panel: every difference between the open document and the file as it was opened,
/// grouped by table / list then by record, with old → new values, undo per change and "go to".
/// </summary>
public sealed class ChangesPanel : Panel
{
    private readonly Func<List<ChangeItem>> _compute;
    private readonly Action<ChangeItem> _locate;
    private readonly Action _afterRevert;
    private readonly DataGridView _grid;
    private readonly TextBox _filter;
    private readonly Label _summary;
    private List<ChangeItem> _all = new();
    private readonly List<object> _rows = new(); // string (group header) or ChangeItem
    private Font? _bold;

    public ChangesPanel(Func<List<ChangeItem>> compute, Action<ChangeItem> locate, Action afterRevert)
    {
        _compute = compute; _locate = locate; _afterRevert = afterRevert;
        Width = 640;
        Padding = new Padding(10, 8, 10, 8);

        var head = new Panel { Dock = DockStyle.Top, Height = 30 };
        var title = new Label { Text = L.T("⟲  EDIT HISTORY  ·  changes compared to the file as opened"), Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        var btnClose = new Button { Text = "✕", Dock = DockStyle.Right, Width = 34 };
        var btnRefresh = new Button { Text = L.T("Refresh"), Dock = DockStyle.Right, Width = 78 };
        var btnUndoAll = new Button { Text = L.T("Undo all"), Dock = DockStyle.Right, Width = 100 };
        head.Controls.Add(title);
        head.Controls.Add(btnUndoAll);
        head.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 4 });
        head.Controls.Add(btnRefresh);
        head.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 4 });
        head.Controls.Add(btnClose);
        _filter = new TextBox { Dock = DockStyle.Top, PlaceholderText = L.T("Filter by table, ID, name, field, value…") };
        _summary = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, ReadOnly = true,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BorderStyle = BorderStyle.None,
            VirtualMode = true, BackgroundColor = SystemColors.Window,
        };
        typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(_grid, true);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Record"), Width = 170 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Field"), Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Old"), Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("New"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Width = 44, Text = "↺", UseColumnTextForButtonValue = false, FlatStyle = FlatStyle.Flat });
        GameFont.Bind(_grid);
        _grid.CellValueNeeded += OnValueNeeded;
        _grid.CellFormatting += OnFormatting;
        _grid.CellContentClick += (_, e) => { if (e.ColumnIndex == 4 && e.RowIndex >= 0) RevertRow(e.RowIndex); };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != 4 && _rows[e.RowIndex] is ChangeItem c) _locate(c); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _grid.CurrentRow != null && _rows[_grid.CurrentRow.Index] is ChangeItem c) { e.Handled = true; _locate(c); } };

        Controls.Add(_grid);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4 });
        Controls.Add(_summary);
        Controls.Add(_filter);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
        Controls.Add(head);

        btnClose.Click += (_, _) => Visible = false;
        btnRefresh.Click += (_, _) => Reload();
        btnUndoAll.Click += (_, _) => RevertAll();
        _filter.TextChanged += (_, _) => Fill();
        VisibleChanged += (_, _) => { if (Visible) Reload(); };
    }

    public int Count => _all.Count;

    public void Reload()
    {
        try { _all = _compute(); }
        catch (Exception ex) { _all = new(); _summary.Text = L.F("Could not compute the changes: {0}", ex.Message); }
        Fill();
    }

    private void Fill()
    {
        var q = _filter.Text.Trim();
        _rows.Clear();
        var items = _all.Where(c => q.Length == 0 || $"{c.Group}|{c.RecordKey}|{c.RecordName}|{c.Field}|{c.Old}|{c.New}".Contains(q, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(c => c.GroupOrder).ThenBy(c => c.Group).ToList();
        string? group = null;
        foreach (var c in items)
        {
            if (c.Group != group)
            {
                group = c.Group;
                int n = items.Where(x => x.Group == group).Select(x => x.RecordKey + "|" + x.RecordName).Distinct().Count();
                _rows.Add(L.F("{0}   ·   {1} records", group, n));
            }
            _rows.Add(c);
        }
        int recs = _all.Select(c => c.Group + "|" + c.RecordKey + "|" + c.RecordName).Distinct().Count();
        _summary.Text = _all.Count == 0 ? L.T("No changes – same as the file as opened.")
            : L.F("{0:N0} changes in {1:N0} records, {2} tables · kept in memory until you Save · double-click to go to the edit", _all.Count, recs, _all.Select(c => c.Group).Distinct().Count());
        _grid.RowCount = 0;
        _grid.RowCount = _rows.Count;
        _grid.Invalidate();
    }

    private void OnValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex >= _rows.Count) return;
        if (_rows[e.RowIndex] is string g) { e.Value = e.ColumnIndex == 0 ? g : ""; return; }
        var c = (ChangeItem)_rows[e.RowIndex];
        bool repeat = e.RowIndex > 0 && _rows[e.RowIndex - 1] is ChangeItem p && p.RecordKey == c.RecordKey && p.RecordName == c.RecordName && p.Group == c.Group;
        e.Value = e.ColumnIndex switch
        {
            0 => repeat ? "" : (c.Kind switch { ChangeKind.Added => "＋ ", ChangeKind.Deleted => "－ ", _ => "" }) + $"{c.RecordKey}  {c.RecordName}".Trim(),
            1 => c.Field ?? (c.Kind == ChangeKind.Added ? L.T("(new row)") : L.T("(deleted)")),
            2 => c.Old ?? "",
            3 => c.New ?? "",
            4 => c.Revert != null ? "↺" : "",
            _ => "",
        };
    }

    private void OnFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count || e.CellStyle == null) return;
        if (_rows[e.RowIndex] is string)
        {
            e.CellStyle.Font = _bold ??= new Font(_grid.Font, FontStyle.Bold);
            e.CellStyle.ForeColor = Theme.Dark ? Theme.AccentText : Color.DarkGreen;
            e.CellStyle.BackColor = Theme.Dark ? Theme.Header : SystemColors.ControlLight;
            return;
        }
        var c = (ChangeItem)_rows[e.RowIndex];
        if (e.ColumnIndex == 2) e.CellStyle.ForeColor = Theme.Dark ? Theme.Red : Color.Firebrick;
        if (e.ColumnIndex == 3) e.CellStyle.ForeColor = Theme.Dark ? Theme.AccentText : Color.DarkGreen;
        if (e.ColumnIndex == 0 && c.Kind != ChangeKind.Modified) e.CellStyle.ForeColor = c.Kind == ChangeKind.Added ? (Theme.Dark ? Theme.AccentText : Color.DarkGreen) : (Theme.Dark ? Theme.Red : Color.Firebrick);
        if (e.ColumnIndex == 4 && c.Revert != null) _grid.Rows[e.RowIndex].Cells[4].ToolTipText = c.RevertLabel;
        if (e.ColumnIndex is 2 or 3) _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText = e.Value?.ToString();
    }

    private void RevertRow(int row)
    {
        if (_rows[row] is not ChangeItem c || c.Revert == null) return;
        if (c.RevertLabel != L.T("Undo") && Msg.Show($"{c.RevertLabel}: {c.RecordKey} {c.RecordName}?", L.T("Undo"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        c.Revert();
        _afterRevert();
        Reload();
    }

    private void RevertAll()
    {
        if (_all.Count == 0) return;
        if (Msg.Show(L.F("Undo all {0:N0} changes and restore the data to the file as opened?", _all.Count), L.T("Undo all"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        // field changes first, then deletions/additions; one revert per distinct action (nested lists share one)
        var done = new HashSet<Action>();
        foreach (var c in _all.OrderBy(c => c.Kind == ChangeKind.Modified ? 0 : 1))
            if (c.Revert != null && done.Add(c.Revert)) c.Revert();
        _afterRevert();
        Reload();
    }
}
