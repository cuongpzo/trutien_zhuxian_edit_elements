using System.Reflection;
using ElementsCore.Formats;

namespace ElementsEditor;

/// <summary>
/// Optional "ID → name" lookup filled from the open elements.data, used to show the name of the
/// item / NPC an ID column refers to (gshop item id, task_npc npc id, vip award item …).
/// </summary>
public static class IdNames
{
    private static Func<long, string?>? _lookup;
    public static Func<long, string?>? Lookup
    {
        get => _lookup;
        set { _lookup = value; Changed?.Invoke(); }
    }
    /// <summary>Raised when elements.data is (re)loaded so open grids can show the names.</summary>
    public static event Action? Changed;
    /// <summary>Only columns that really hold an elements.data ID (domain / path IDs are their own numbering).</summary>
    public static bool IsRefColumn(DataFile f, ColumnDef c)
    {
        if (!c.IsInteger) return false;
        var n = c.Name.ToLowerInvariant();
        return f switch
        {
            GShopFile => n is "id" or "present_id" or "price_item",
            TaskNpcFile => n is "npcid" or "npc_id",
            VipAwardFile => n is "itemid" or "item_id",
            _ => false,
        };
    }
}

/// <summary>Grid of a decoded file: table picker, search, add/clone/delete, row details and nested lists.</summary>
public sealed class TableFileView : Panel, IModuleView
{
    public event Action? Changed;
    public event Action<string>? Status;

    private readonly DataFile _file;
    private RecordTable _table;
    private readonly ComboBox _tablePick;
    private readonly TextBox _search;
    private readonly Label _count;
    private readonly Button _btnAdd, _btnClone, _btnDel;
    private readonly DataGridView _grid;
    private readonly ThemedTabControl _detailTabs;
    private readonly DataGridView _fields;
    private List<int> _view = new();
    private readonly List<int> _gridCols = new();   // grid column → table column (-1 = "#", -2 = name lookup)
    private int _refCol = -1;
    private bool _syncing;
    private readonly ColorTextEditor _editor;
    private readonly Splitter _editorSplit;
    private string? _selField;

    public TableFileView(DataFile file)
    {
        _file = file;
        _table = file.Tables[0];

        var top = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 8, 10, 6) };
        _tablePick = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Dock = DockStyle.Left, Visible = file.Tables.Count > 1 };
        foreach (var t in file.Tables) _tablePick.Items.Add($"{t.Name}  ({t.Rows.Count:N0})");
        _search = new TextBox { Dock = DockStyle.Left, Width = 300, PlaceholderText = "Tìm (ID, tên, giá trị bất kỳ)…   Ctrl+F" };
        _count = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray, Padding = new Padding(12, 0, 0, 0), UseMnemonic = false };
        var btns = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        _btnAdd = new Button { Text = "+ Thêm dòng", Width = 104, Height = 30 };
        _btnClone = new Button { Text = "Nhân bản", Width = 86, Height = 30 };
        _btnDel = new Button { Text = "Xoá", Width = 64, Height = 30 };
        btns.Controls.AddRange([_btnAdd, _btnClone, _btnDel]);
        top.Controls.Add(_count);
        top.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 });
        top.Controls.Add(_search);
        top.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 });
        top.Controls.Add(_tablePick);
        top.Controls.Add(btns);

        _grid = MakeGrid();
        _grid.VirtualMode = true;
        _grid.ReadOnly = false;
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _grid.CellValueNeeded += OnCellValueNeeded;
        _grid.CellValuePushed += OnCellValuePushed;
        _grid.CellValidating += OnCellValidating;
        _grid.SelectionChanged += (_, _) => ShowDetails();

        var right = new Panel { Dock = DockStyle.Right, Width = 470 };
        var splitter = new Splitter { Dock = DockStyle.Right, Width = 5 };
        _detailTabs = new ThemedTabControl { Dock = DockStyle.Fill };
        _fields = MakeGrid();
        _fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = "Field", ReadOnly = true, Width = 130 });
        _fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Giá trị", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Kiểu", ReadOnly = true, Width = 74 });
        _fields.CellValidating += OnFieldValidating;
        _fields.CellEndEdit += (_, e) => { if (e.RowIndex >= 0) _fields.Rows[e.RowIndex].ErrorText = ""; };
        _fields.SelectionChanged += (_, _) => OnFieldSelected();
        // text fields are edited in this box (colour codes + preview), JD IDE style
        _editor = new ColorTextEditor { Dock = DockStyle.Bottom, Height = 330, Visible = false };
        _editor.Cancelled += () => _editor.Visible = false;
        _editorSplit = new Splitter { Dock = DockStyle.Bottom, Height = 5, Visible = false };
        _editor.VisibleChanged += (_, _) => _editorSplit.Visible = _editor.Visible;
        right.Controls.Add(_detailTabs);
        right.Controls.Add(_editorSplit);
        right.Controls.Add(_editor);

        Controls.Add(_grid);
        Controls.Add(splitter);
        Controls.Add(right);
        Controls.Add(top);

        _tablePick.SelectedIndexChanged += (_, _) => { if (_tablePick.SelectedIndex >= 0) SetTable(_file.Tables[_tablePick.SelectedIndex]); };
        _search.TextChanged += (_, _) => ApplyFilter();
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _grid.RowCount > 0) { e.SuppressKeyPress = true; _grid.Focus(); } };
        _btnAdd.Click += (_, _) => AddRow(clone: false);
        _btnClone.Click += (_, _) => AddRow(clone: true);
        _btnDel.Click += (_, _) => DeleteRows();
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete && !_grid.IsCurrentCellInEditMode) { e.Handled = true; DeleteRows(); } };
        // right-click: Hán Việt this cell / the same field in every row of this table
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            int c = _gridCols[e.ColumnIndex];
            if (!_grid.Rows[e.RowIndex].Selected) { _grid.ClearSelection(); _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex]; _grid.Rows[e.RowIndex].Selected = true; }
            if (c >= 0) ShowFieldMenu(c);
        };
        _fields.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            _fields.CurrentCell = _fields.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            if (_fields.Rows[e.RowIndex].Tag is int c) ShowFieldMenu(c);
        };
        // double-click a text cell → edit it in the colour editor instead of in the cell
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            int c = _gridCols[e.ColumnIndex];
            if (c < 0 || !_table.Columns[c].IsText) return;
            _grid.CancelEdit();
            _grid.EndEdit();
            SelectField(_table.Columns[c].Name);
            _editor.FocusText();
        };

        Action refresh = () => { if (IsHandleCreated) BeginInvoke(() => { _grid.Invalidate(); ShowDetails(); }); };
        IdNames.Changed += refresh;
        Disposed += (_, _) => IdNames.Changed -= refresh;

        // open the biggest editable table first (e.g. Items, not the one-row Header)
        var first = file.Tables.OrderByDescending(t => t.Rows.Count > 1 || t.CanAddRemove).ThenBy(t => file.Tables.IndexOf(t)).First();
        if (_tablePick.Visible) _tablePick.SelectedIndex = file.Tables.IndexOf(first);
        else SetTable(first);
    }

    internal static DataGridView MakeGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, BorderStyle = BorderStyle.None, BackgroundColor = SystemColors.Window,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        };
        typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(g, true);
        return g;
    }

    // ───────────────────────── main grid ─────────────────────────

    private void SetTable(RecordTable t)
    {
        _table = t;
        _grid.CancelEdit();
        _grid.Rows.Clear();
        _grid.Columns.Clear();
        _gridCols.Clear();

        AddCol("#", -1, 56, true);
        _refCol = Enumerable.Range(0, t.Columns.Count).FirstOrDefault(i => IdNames.IsRefColumn(_file, t.Columns[i]) && i != t.NameColumn && !t.Columns[i].HideInGrid, -1);
        for (int i = 0; i < t.Columns.Count; i++)
        {
            var c = t.Columns[i];
            if (c.HideInGrid) continue;
            int w = c.Type switch
            {
                ColType.WStr or ColType.Str => i == t.NameColumn ? 220 : 180,
                ColType.Bytes => 120,
                ColType.Table => 64,
                ColType.F32 or ColType.F64 => 90,
                _ => Math.Max(64, Math.Min(120, c.Name.Length * 9 + 16)),
            };
            AddCol(c.Name, i, w, c.ReadOnly || c.Type == ColType.Table || c.Type == ColType.Bytes && c.Length == 0, c.Note);
            if (i == _refCol) AddCol("→ tên (elements)", -2, 180, true, "Tên tra từ elements.data đang mở");
        }
        bool canEdit = t.CanAddRemove;
        _btnAdd.Enabled = _btnClone.Enabled = _btnDel.Enabled = canEdit;
        ApplyFilter();
    }

    private void AddCol(string name, int col, int width, bool ro, string? tip = null)
    {
        var dc = new DataGridViewTextBoxColumn { HeaderText = name, Width = width, ReadOnly = ro, SortMode = DataGridViewColumnSortMode.NotSortable, ToolTipText = tip };
        if (col >= 0 && _table.Columns[col].IsInteger || col == -1) dc.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns.Add(dc);
        _gridCols.Add(col);
    }

    private void ApplyFilter()
    {
        var q = _search.Text.Trim();
        _view.Clear();
        var rows = _table.Rows;
        if (q.Length == 0) _view.AddRange(Enumerable.Range(0, rows.Count));
        else
        {
            bool num = long.TryParse(q, out long id);
            for (int r = 0; r < rows.Count; r++)
            {
                var rec = rows[r];
                if (num && _table.KeyColumn >= 0 && rec.Values[_table.KeyColumn] != null && rec.Long(_table.KeyColumn) == id) { _view.Add(r); continue; }
                for (int c = 0; c < _table.Columns.Count; c++)
                {
                    var col = _table.Columns[c];
                    if (col.Type is ColType.Table or ColType.Bytes) continue;
                    if (RecordIO.Format(col, rec.Values[c]).Contains(q, StringComparison.OrdinalIgnoreCase)) { _view.Add(r); break; }
                }
            }
        }
        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
        _count.Text = q.Length == 0 ? $"{rows.Count:N0} dòng" : $"{_view.Count:N0} / {rows.Count:N0} dòng";
        if (_table.Note != null) _count.Text += "   ·   " + _table.Note;
        if (_view.Count > 0 && _grid.CurrentCell == null && _grid.Columns.Count > 1)
        {
            _grid.CurrentCell = _grid.Rows[0].Cells[1];
            _grid.Rows[0].Selected = true;
        }
        ShowDetails();
    }

    private Record? RowAt(int gridRow) => gridRow >= 0 && gridRow < _view.Count ? _table.Rows[_view[gridRow]] : null;

    private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        var rec = RowAt(e.RowIndex);
        if (rec == null) return;
        int c = _gridCols[e.ColumnIndex];
        if (c == -1) { e.Value = _view[e.RowIndex]; return; }
        if (c == -2) { e.Value = _refCol >= 0 && rec.Values[_refCol] != null ? IdNames.Lookup?.Invoke(rec.Long(_refCol)) : null; return; }
        var col = _table.Columns[c];
        e.Value = col.Type == ColType.Bytes && rec.Values[c] is byte[] { Length: > 48 } b ? Convert.ToHexString(b, 0, 48) + "…" : RecordIO.Describe(col, rec.Values[c]);
    }

    private void OnCellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (!_grid.IsCurrentCellInEditMode) return;
        int c = _gridCols[e.ColumnIndex];
        if (c < 0) return;
        var col = _table.Columns[c];
        if (!RecordIO.TryParse(col, StripMeaning(col, e.FormattedValue?.ToString() ?? ""), out _, out var err))
        {
            e.Cancel = true;
            Status?.Invoke($"{col.Name}: {err}");
            _grid.Rows[e.RowIndex].ErrorText = err ?? "";
        }
        else _grid.Rows[e.RowIndex].ErrorText = "";
    }

    private void OnCellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
    {
        var rec = RowAt(e.RowIndex);
        int c = _gridCols[e.ColumnIndex];
        if (rec == null || c < 0) return;
        SetValue(_table.Columns[c], rec, c, e.Value?.ToString() ?? "");
        ShowDetails();
    }

    /// <summary>"3 (Vật phẩm mới)" → "3" when the user leaves the enum meaning in place.</summary>
    private static string StripMeaning(ColumnDef c, string s)
    {
        if (c.Enum == null || !c.IsInteger) return s;
        int p = s.IndexOf(" (", StringComparison.Ordinal);
        return p > 0 ? s[..p] : s;
    }

    private bool SetValue(ColumnDef col, Record rec, int c, string text)
    {
        text = StripMeaning(col, text);
        if (!RecordIO.TryParse(col, text, out var v, out var err)) { Status?.Invoke($"{col.Name}: {err}"); return false; }
        if (Equals(v, rec.Values[c]) || v is string s && s == rec.Values[c] as string) return true;
        if (v is byte[] nb && rec.Values[c] is byte[] ob && nb.AsSpan().SequenceEqual(ob)) return true;
        rec[c] = v;
        Changed?.Invoke();
        return true;
    }

    private void AddRow(bool clone)
    {
        if (!_table.CanAddRemove) return;
        var cur = RowAt(_grid.CurrentCell?.RowIndex ?? -1);
        if (clone && cur == null) return;
        var rec = clone ? cur!.Clone() : _table.NewRecord();
        int k = _table.KeyColumn;
        if (k >= 0 && _table.Columns[k].IsInteger && _table.Rows.Count > 0)
        {
            long next = _table.Rows.Max(r => r.Values[k] == null ? 0 : r.Long(k)) + 1;
            RecordIO.TryParse(_table.Columns[k], next.ToString(), out var v, out _);
            rec[k] = v;
        }
        int at = cur != null ? _table.Rows.IndexOf(cur) + 1 : _table.Rows.Count;
        _table.Rows.Insert(at, rec);
        Changed?.Invoke();
        _search.Text = "";
        ApplyFilter();
        SelectModelRow(at);
        Status?.Invoke(clone ? "Đã nhân bản dòng (ID mới = lớn nhất + 1)." : "Đã thêm dòng mới.");
    }

    private void DeleteRows()
    {
        if (!_table.CanAddRemove) return;
        var sel = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => RowAt(r.Index)).Where(r => r != null).Cast<Record>().ToList();
        if (sel.Count == 0) return;
        if (Msg.Show($"Xoá {sel.Count} dòng khỏi \"{_table.Name}\"?", "Xoá", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        int first = _table.Rows.IndexOf(sel[0]);
        foreach (var r in sel) _table.Rows.Remove(r);
        Changed?.Invoke();
        ApplyFilter();
        SelectModelRow(Math.Min(first, _table.Rows.Count - 1));
    }

    private void SelectModelRow(int modelRow)
    {
        int g = _view.IndexOf(modelRow);
        if (g < 0 || _grid.Columns.Count == 0) return;
        _grid.ClearSelection();
        _grid.CurrentCell = _grid.Rows[g].Cells[Math.Min(1, _grid.Columns.Count - 1)];
        _grid.Rows[g].Selected = true;
    }

    // ───────────────────────── details panel ─────────────────────────

    private void ShowDetails()
    {
        if (_syncing) return;
        _editor.CommitPending(); // typed text of the previous record is kept, not lost
        _syncing = true;
        try
        {
            var rec = RowAt(_grid.CurrentCell?.RowIndex ?? -1);
            int keepTab = _detailTabs.SelectedIndex;
            _fields.Rows.Clear();
            while (_detailTabs.TabPages.Count > 1) { var p = _detailTabs.TabPages[1]; _detailTabs.TabPages.RemoveAt(1); p.Dispose(); }
            if (_detailTabs.TabPages.Count == 0)
            {
                var tp = new TabPage("Chi tiết");
                tp.Controls.Add(_fields);
                _detailTabs.TabPages.Add(tp);
            }
            if (rec == null) return;
            for (int i = 0; i < _table.Columns.Count; i++)
            {
                var c = _table.Columns[i];
                string val = c.Type == ColType.Table ? $"{rec.Table(i).Count} dòng → tab \"{c.Name}\"" : RecordIO.Describe(c, rec.Values[i]);
                int r = _fields.Rows.Add(c.Name, val, c.TypeLabel);
                var row = _fields.Rows[r];
                row.Tag = i;
                row.Cells[0].ToolTipText = c.Note ?? "";
                row.Cells[1].ToolTipText = c.Note ?? "";
                if (c.ReadOnly || c.Type == ColType.Table) { row.Cells[1].ReadOnly = true; row.Cells[1].Style.ForeColor = Color.Gray; }
                if (c.IsText && c.Length > 0) row.Cells[2].Value = $"{c.TypeLabel} ({RecordIO.TextLength(c, rec.Text(i))}/{c.MaxChars})";
                if (i == _refCol && IdNames.Lookup?.Invoke(rec.Long(i)) is { } nm) row.Cells[1].ToolTipText = $"{nm}\n{c.Note}";

                if (c.Type == ColType.Table && c.SubColumns != null)
                {
                    var page = new TabPage($"{c.Name} ({rec.Table(i).Count})");
                    page.Controls.Add(new SubTableEditor(c, rec.Table(i), () =>
                    {
                        Changed?.Invoke();
                        page.Text = $"{c.Name} ({rec.Table(i).Count})";
                        _grid.InvalidateRow(_grid.CurrentCell?.RowIndex ?? 0);
                    }, s => Status?.Invoke(s)) { Dock = DockStyle.Fill });
                    _detailTabs.TabPages.Add(page);
                }
            }
            if (keepTab > 0 && keepTab < _detailTabs.TabCount) _detailTabs.SelectedIndex = keepTab;
            // keep the same field selected from record to record (translate one field down the list)
            var keep = _selField;
            if (keep != null)
                foreach (DataGridViewRow r in _fields.Rows)
                    if (r.Tag is int ci && _table.Columns[ci].Name == keep) { _fields.CurrentCell = r.Cells[1]; break; }
        }
        finally { _syncing = false; }
        OnFieldSelected();
    }

    /// <summary>Selected field row: a text field opens the colour editor below.</summary>
    private void OnFieldSelected()
    {
        if (_syncing) return;
        var rec = RowAt(_grid.CurrentCell?.RowIndex ?? -1);
        if (rec == null || _fields.CurrentRow?.Tag is not int i) { _editor.Visible = false; return; }
        var c = _table.Columns[i];
        _selField = c.Name;
        if (!c.IsText || c.ReadOnly) { _editor.Visible = false; return; }
        var key = _table.KeyColumn >= 0 ? $"{_table.Columns[_table.KeyColumn].Name} {RecordIO.Format(_table.Columns[_table.KeyColumn], rec.Values[_table.KeyColumn])}" : $"#{_table.Rows.IndexOf(rec)}";
        _editor.SaveText = text =>
        {
            if (!RecordIO.TryParse(c, text, out _, out var err)) return err;
            SetValue(c, rec, i, text);
            if (_fields.CurrentRow?.Tag is int ci && ci == i)
            {
                _syncing = true;
                _fields.CurrentRow.Cells[1].Value = RecordIO.Describe(c, rec.Values[i]);
                if (c.Length > 0) _fields.CurrentRow.Cells[2].Value = $"{c.TypeLabel} ({RecordIO.TextLength(c, text)}/{c.MaxChars})";
                _syncing = false;
            }
            _grid.InvalidateRow(_grid.CurrentCell?.RowIndex ?? 0);
            Status?.Invoke($"Đã sửa {c.Name} của {key} (chưa lưu file).");
            return null;
        };
        _editor.Begin(c.Name, rec.Text(i), c.Length > 0 ? c.MaxChars : 0, s => RecordIO.TextLength(c, s),
            $"{key}{(c.Type == ColType.Str ? " · GBK (không chứa được tiếng Việt có dấu)" : "")}");
    }

    /// <summary>Right-click menu for a column of the current table.</summary>
    private void ShowFieldMenu(int col)
    {
        var rec = RowAt(_grid.CurrentCell?.RowIndex ?? -1);
        if (rec == null) return;
        var c = _table.Columns[col];
        var table = _table;
        bool text = c.IsText && !c.ReadOnly, gbk = c.Type == ColType.Str;
        int cjk = text && !gbk ? table.Rows.Count(r => r.Values[col] is string s && ElementsCore.Exchange.HanViet.HasCjk(s)) : 0;
        var menu = new ContextMenuStrip();
        var one = menu.Items.Add($"Việt hoá Hán Việt field \"{c.Name}\" của dòng này", null, (_, _) =>
        {
            var t = HanVietTools.RecordTargets(table, rec, table.Rows.IndexOf(rec)).FirstOrDefault(x => x.Field == c.Name);
            if (t == null) return;
            if (HanVietTools.ConvertOne(t, out var msg))
            {
                Changed?.Invoke();
                _grid.InvalidateRow(_grid.CurrentCell?.RowIndex ?? 0);
                ShowDetails();
                Status?.Invoke("Hán Việt: " + msg);
            }
            else Msg.Show(msg, "Việt hoá Hán Việt", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        one.Enabled = text && !gbk && rec.Values[col] is string cur && ElementsCore.Exchange.HanViet.HasCjk(cur);
        var all = menu.Items.Add($"Việt hoá Hán Việt field \"{c.Name}\" cho cả bảng {table.Name} ({cjk:N0} dòng còn chữ Hán)…", null, (_, _) =>
        {
            using var dlg = new HanVietDialog([new TextScope($"Bảng {table.Name} · field {c.Name}", () => HanVietTools.TableTargets(table, c.Name))], HanVietTools.Settings, c.Name, onlyThatField: true);
            dlg.ShowDialog(FindForm());
            if (dlg.Applied == 0) return;
            RefreshAll();
            Changed?.Invoke();
        });
        all.Enabled = cjk > 0;
        if (gbk) menu.Items.Add("(field GBK – không chứa được tiếng Việt có dấu)").Enabled = false;
        else if (!c.IsText) menu.Items.Add("(chỉ field chữ mới Việt hoá được)").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sửa trong khung soạn (màu, xem trước)", null, (_, _) => { SelectField(c.Name); _editor.FocusText(); }).Enabled = text;
        menu.Items.Add("Sao chép giá trị", null, (_, _) => { try { Clipboard.SetText(RecordIO.Format(c, rec.Values[col])); } catch { } });
        Theme.Apply(menu);
        menu.Show(Cursor.Position);
    }

    private void SelectField(string name)
    {
        _selField = name;
        foreach (DataGridViewRow r in _fields.Rows)
            if (r.Tag is int ci && _table.Columns[ci].Name == name) { _fields.CurrentCell = r.Cells[1]; break; }
        OnFieldSelected();
    }

    public RecordTable? CurrentTable => _table;

    public void RefreshAll()
    {
        _grid.CancelEdit();
        for (int i = 0; i < _file.Tables.Count && i < _tablePick.Items.Count; i++)
            _tablePick.Items[i] = $"{_file.Tables[i].Name}  ({_file.Tables[i].Rows.Count:N0})";
        ApplyFilter();
        _grid.Invalidate();
    }

    /// <summary>Goes to the record / field of a change (switches table, clears the search).</summary>
    public void Locate(ElementsCore.Exchange.ChangeItem c)
    {
        var t = _file.Tables.FirstOrDefault(x => x.Name == c.Group);
        if (t == null) return;
        if (t != _table)
        {
            if (_tablePick.Visible) _tablePick.SelectedIndex = _file.Tables.IndexOf(t);
            else SetTable(t);
        }
        if (_search.Text.Length > 0) _search.Text = "";
        if (c.Target is Record rec)
        {
            int mi = _table.Rows.IndexOf(rec);
            if (mi >= 0)
            {
                SelectModelRow(mi);
                int g = _view.IndexOf(mi);
                if (g >= 0) _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, g - 5);
            }
        }
        if (c.Field is { Length: > 0 } f)
        {
            var colName = f.Split('[', '.')[0];
            SelectField(colName);
            // nested list: open its tab
            foreach (TabPage p in _detailTabs.TabPages)
                if (f.Contains('[') && p.Text.StartsWith(colName + " (")) { _detailTabs.SelectedTab = p; break; }
        }
    }

    private void OnFieldValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (e.ColumnIndex != 1 || !_fields.IsCurrentCellInEditMode) return;
        var rec = RowAt(_grid.CurrentCell?.RowIndex ?? -1);
        if (rec == null || _fields.Rows[e.RowIndex].Tag is not int i) return;
        var c = _table.Columns[i];
        if (!SetValue(c, rec, i, e.FormattedValue?.ToString() ?? ""))
        {
            RecordIO.TryParse(c, StripMeaning(c, e.FormattedValue?.ToString() ?? ""), out _, out var err);
            _fields.Rows[e.RowIndex].ErrorText = err ?? "";
            e.Cancel = true;
            return;
        }
        _grid.InvalidateRow(_grid.CurrentCell!.RowIndex);
        BeginInvoke(() => { if (!_fields.IsCurrentCellInEditMode) ShowDetails(); });
    }

    public bool Commit()
    {
        _editor.CommitPending();
        if (_grid.IsCurrentCellInEditMode && !_grid.EndEdit()) return false;
        if (_fields.IsCurrentCellInEditMode && !_fields.EndEdit()) return false;
        foreach (var sub in _detailTabs.TabPages.Cast<TabPage>().SelectMany(p => p.Controls.OfType<SubTableEditor>()))
            if (!sub.Commit()) return false;
        return true;
    }

    public void FocusSearch() { _search.Focus(); _search.SelectAll(); }
}

/// <summary>Editable grid for a nested list (e.g. a domain's outline points), with add/remove.</summary>
public sealed class SubTableEditor : Panel
{
    private readonly ColumnDef _col;
    private readonly List<Record> _rows;
    private readonly Action _changed;
    private readonly Action<string> _status;
    private readonly DataGridView _grid;
    private readonly IReadOnlyList<ColumnDef> _sub;

    public SubTableEditor(ColumnDef col, List<Record> rows, Action changed, Action<string> status)
    {
        _col = col; _rows = rows; _changed = changed; _status = status;
        _sub = col.SubColumns!;
        _grid = TableFileView.MakeGrid();
        _grid.VirtualMode = true;
        _grid.ReadOnly = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", Width = 44, ReadOnly = true });
        foreach (var c in _sub)
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = c.Name, ToolTipText = c.Note, ReadOnly = c.ReadOnly || c.Type == ColType.Table,
                AutoSizeMode = _sub.Count <= 4 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None, Width = 90,
            });
        _grid.RowCount = rows.Count;
        _grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex >= _rows.Count) return;
            e.Value = e.ColumnIndex == 0 ? e.RowIndex : RecordIO.Describe(_sub[e.ColumnIndex - 1], _rows[e.RowIndex].Values[e.ColumnIndex - 1]);
        };
        _grid.CellValidating += (_, e) =>
        {
            if (!_grid.IsCurrentCellInEditMode || e.ColumnIndex == 0) return;
            var c = _sub[e.ColumnIndex - 1];
            var text = e.FormattedValue?.ToString() ?? "";
            int p = c.Enum != null ? text.IndexOf(" (", StringComparison.Ordinal) : -1;
            if (p > 0) text = text[..p];
            if (!RecordIO.TryParse(c, text, out var v, out var err)) { e.Cancel = true; _status($"{c.Name}: {err}"); return; }
            var rec = _rows[e.RowIndex];
            if (!Equals(v, rec.Values[e.ColumnIndex - 1])) { rec[e.ColumnIndex - 1] = v; _changed(); }
        };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4, 4, 4, 2), WrapContents = false };
        var add = new Button { Text = "+ Thêm", Width = 80, Height = 30, Enabled = col.SubCanAddRemove };
        var ins = new Button { Text = "Chèn", Width = 64, Height = 30, Enabled = col.SubCanAddRemove };
        var del = new Button { Text = "Xoá", Width = 64, Height = 30, Enabled = col.SubCanAddRemove };
        var info = new Label { Text = col.Note ?? "", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 8, 0, 0), UseMnemonic = false };
        bar.Controls.AddRange([add, ins, del, info]);
        add.Click += (_, _) => Insert(_rows.Count);
        ins.Click += (_, _) => Insert(Math.Max(0, _grid.CurrentCell?.RowIndex ?? 0));
        del.Click += (_, _) =>
        {
            var idx = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i < _rows.Count).OrderByDescending(i => i).ToList();
            if (idx.Count == 0 && _grid.CurrentCell != null) idx.Add(_grid.CurrentCell.RowIndex);
            foreach (var i in idx) _rows.RemoveAt(i);
            if (idx.Count > 0) { Reload(); _changed(); }
        };
        Controls.Add(_grid);
        Controls.Add(bar);
    }

    private void Insert(int at)
    {
        var rec = Record.CreateDefault(_sub);
        // copying the neighbour row is usually what you want for coordinates / times
        if (_rows.Count > 0) rec = _rows[Math.Min(at, _rows.Count) - (at >= _rows.Count ? 1 : 0)].Clone();
        _rows.Insert(Math.Min(at, _rows.Count), rec);
        Reload();
        _changed();
    }

    private void Reload()
    {
        _grid.RowCount = 0;
        _grid.RowCount = _rows.Count;
    }

    public bool Commit() => !_grid.IsCurrentCellInEditMode || _grid.EndEdit();
}

/// <summary>Plain text editor that keeps the file's encoding (UTF-16 / UTF-8 / GBK) and line endings.</summary>
public sealed class TextFileView : Panel, IModuleView
{
    public event Action? Changed;
    public event Action<string>? Status;
    private readonly TextDataFile _file;
    private readonly TextBox _text;
    private readonly TextBox _find;
    private readonly Label _info;
    private bool _loading;

    public TextFileView(TextDataFile f)
    {
        _file = f;
        var top = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 8, 10, 6) };
        _find = new TextBox { Dock = DockStyle.Left, Width = 300, PlaceholderText = "Tìm… (Enter = tiếp, Ctrl+F)" };
        _info = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray, Padding = new Padding(12, 0, 0, 0), UseMnemonic = false };
        top.Controls.Add(_info);
        top.Controls.Add(_find);
        _text = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsTab = true,
            Font = new Font("Consolas", 10.5F), MaxLength = 0, HideSelection = false,
        };
        _loading = true;
        _text.Text = f.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _loading = false;
        _text.TextChanged += (_, _) => { if (!_loading) { Changed?.Invoke(); UpdateInfo(); } };
        _text.KeyUp += (_, _) => UpdateInfo();
        _text.MouseUp += (_, _) => UpdateInfo();
        _find.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FindNext(); } };
        Controls.Add(_text);
        Controls.Add(top);
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        int line = _text.GetLineFromCharIndex(_text.SelectionStart) + 1;
        _info.Text = $"{_file.FormatName}   ·   {_text.Lines.Length:N0} dòng   ·   dòng {line}   ·   xuống dòng {(_file.NewLine == "\n" ? "LF" : "CRLF")}";
    }

    private void FindNext()
    {
        var q = _find.Text;
        if (q.Length == 0) return;
        int from = _text.SelectionStart + _text.SelectionLength;
        int i = _text.Text.IndexOf(q, from, StringComparison.OrdinalIgnoreCase);
        if (i < 0) i = _text.Text.IndexOf(q, 0, StringComparison.OrdinalIgnoreCase);
        if (i < 0) { Status?.Invoke($"Không thấy \"{q}\"."); return; }
        _text.Select(i, q.Length);
        _text.ScrollToCaret();
        UpdateInfo();
    }

    /// <summary>Editor text in the file's line-ending style.</summary>
    public string CurrentText => _file.NewLine == "\n" ? _text.Text.Replace("\r\n", "\n") : _text.Text;

    public void ResetText(string text)
    {
        _loading = true;
        _text.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _loading = false;
        _file.Text = text;
        UpdateInfo();
    }

    public void Locate(ElementsCore.Exchange.ChangeItem c)
    {
        if (c.Target is not int line || line >= _text.Lines.Length) return;
        int pos = _text.GetFirstCharIndexFromLine(line);
        if (pos < 0) return;
        _text.Focus();
        _text.Select(pos, _text.Lines[line].Length);
        _text.ScrollToCaret();
        UpdateInfo();
    }

    public bool Commit()
    {
        var t = _text.Text;
        if (_file.NewLine == "\n") t = t.Replace("\r\n", "\n");
        if (_file.CheckEncodable(t) is { } err)
        {
            Msg.Show(err + "\nHãy bỏ ký tự đó, hoặc đổi file sang UTF-16 trước.", "Bảng mã", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        _file.Text = t;
        return true;
    }

    public void FocusSearch() { _find.Focus(); _find.SelectAll(); }
}

/// <summary>Read-only hex view for files whose layout is not decoded yet.</summary>
public sealed class RawFileView : Panel, IModuleView
{
    public event Action? Changed { add { } remove { } }
    public event Action<string>? Status { add { } remove { } }

    public RawFileView(RawDataFile f)
    {
        var banner = new Label
        {
            Dock = DockStyle.Top, Height = 64, Padding = new Padding(14, 8, 14, 8), ForeColor = Color.DarkGoldenrod,
            Text = $"{f.Reason}\nĐang hiển thị dạng hex ({f.Data.Length:N0} byte{(f.Data.Length > HexFormatter.MaxBytes ? $", {HexFormatter.MaxBytes / 1024} KB đầu" : "")}). File không bị thay đổi.",
            UseMnemonic = false,
        };
        var hex = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font("Consolas", 10F), Text = HexFormatter.Format(f.Data).Replace("\n", "\r\n"),
        };
        Controls.Add(hex);
        Controls.Add(banner);
    }

    public bool Commit() => true;
    public void FocusSearch() { }
}
