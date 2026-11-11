using System.Drawing.Drawing2D;
using System.Reflection;
using ElementsCore;
using ElementsCore.Exchange;
using ElementsCore.Formats;

namespace ElementsEditor;

/// <summary>
/// Domains tab, JD IDE style: tabs for every domain*.data of the folder, the domain list, a map of the
/// outlines with names (zoom / pan / click to select / drag the centre to move it), details, neighbours
/// and battle times on the right. "Table" switches to the generic table editor of the same file.
/// </summary>
public sealed class DomainMapView : Panel, IModuleView
{
    public event Action? Changed;
    public event Action<string>? Status;

    private readonly DomainFile _f;
    private readonly Action<string> _openFile;
    private readonly Panel _mapMode;
    private TableFileView? _table;
    private readonly Button _btnMap, _btnTable;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly Label _listCount;
    private readonly DomainCanvas _canvas;
    private readonly Panel _side;
    private Record? _sel;
    private bool _syncing;
    private List<Record> _shown = new();

    // details
    private readonly Label _selTitle, _selInfo, _selWarn;
    private readonly TextBox _tbId, _tbName, _tbReward, _tbCx, _tbCy;
    private readonly ComboBox _cbType;
    private readonly ListBox _nbList;
    private readonly ComboBox _nbAdd;
    private readonly CheckBox _nbBoth;
    private readonly Panel _detail;
    private readonly DataGridView _times;
    private readonly TextBox _tbPerSlot;

    public static readonly (string File, string Title)[] KnownFiles =
    [
        ("domain.data", L.T("Guild")), ("domain1.data", L.T("Territory")), ("domain2.data", L.T("Cross-server")),
        ("domain3.data", L.T("General")), ("domain4.data", L.T("Ancient")), ("domain5.data", L.T("Domain 5")), ("domainlandwar.data", L.T("Battlefield")),
    ];

    public static Color TypeColor(long t) => t switch
    {
        1 => Color.FromArgb(196, 82, 82),
        2 => Color.FromArgb(206, 150, 60),
        3 => Color.FromArgb(78, 120, 196),
        _ => Color.FromArgb(110, 118, 126),
    };

    public DomainMapView(DomainFile f, Action<string> openFile)
    {
        _f = f; _openFile = openFile;

        // ── top: one tab per domain file of the folder + Map / Table switch
        var top = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 2) };
        top.Resize += (_, _) => top.Height = Math.Max(40, (top.Controls.OfType<FlowLayoutPanel>().FirstOrDefault()?.PreferredSize.Height ?? 32) + 8);
        var files = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        foreach (var path in SiblingFiles())
        {
            var name = Path.GetFileName(path);
            var title = KnownFiles.FirstOrDefault(k => k.File.Equals(name, StringComparison.OrdinalIgnoreCase)).Title ?? "";
            bool cur = string.Equals(path, f.Path, StringComparison.OrdinalIgnoreCase);
            var b = new FileTab(title.Length > 0 ? $"{name} · {title}" : name, cur) { Margin = new Padding(0, 0, 4, 0) };
            if (!cur) b.Click += (_, _) => _openFile(path);
            files.Controls.Add(b);
        }
        var sw = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        _btnMap = new Button { Text = L.T("🗺 Map"), Width = 92, Height = 28 };
        _btnTable = new Button { Text = L.T("▦ Table"), Width = 80, Height = 28 };
        sw.Controls.AddRange([_btnMap, _btnTable]);
        top.Controls.Add(files);
        top.Controls.Add(sw);
        // wrap the file tabs onto a second row instead of hiding them
        files.Layout += (_, _) =>
        {
            int h = files.Controls.Count == 0 ? 28 : files.Controls.Cast<Control>().Max(c => c.Bottom) + 2;
            int want = Math.Max(40, h + top.Padding.Vertical);
            if (top.Height != want) top.Height = want;
        };

        // ── left: search + list
        var left = new Panel { Dock = DockStyle.Left, Width = 250, Padding = new Padding(8, 8, 4, 6) };
        _search = new TextBox { Dock = DockStyle.Top, PlaceholderText = L.T("Name or ID…") };
        _list = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44, IntegralHeight = false, BorderStyle = BorderStyle.None };
        _listCount = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
        var listBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, WrapContents = false };
        var bAdd = new Button { Text = L.T("+ Add"), Width = 72, Height = 28 };
        var bClone = new Button { Text = L.T("Clone"), Width = 80, Height = 28 };
        var bDel = new Button { Text = L.T("Delete"), Width = 56, Height = 28 };
        listBtns.Controls.AddRange([bAdd, bClone, bDel]);
        left.Controls.Add(_list);
        left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
        left.Controls.Add(_search);
        left.Controls.Add(_listCount);
        left.Controls.Add(listBtns);

        // ── centre: map + legend
        var centre = new Panel { Dock = DockStyle.Fill };
        _canvas = new DomainCanvas(f) { Dock = DockStyle.Fill };
        var legend = new LegendBar { Dock = DockStyle.Bottom, Height = 26 };
        centre.Controls.Add(_canvas);
        centre.Controls.Add(legend);

        // ── right: details + neighbours + battle times
        _side = new Panel { Dock = DockStyle.Right, Width = 360, AutoScroll = true, Padding = new Padding(12, 8, 12, 8) };
        _detail = new Panel { Dock = DockStyle.Top, Height = 420 };
        _selTitle = new Label { Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 13F, FontStyle.Bold), UseMnemonic = false };
        _selInfo = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray, UseMnemonic = false };
        _tbId = new TextBox(); _tbName = new TextBox { MaxLength = DomainFile.NameChars - 1 }; _tbReward = new TextBox(); _tbCx = new TextBox(); _tbCy = new TextBox();
        _cbType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        for (int t = 0; t <= 3; t++) _cbType.Items.Add($"{t} · {DomainFile.TypeEnum[t]}");
        // label + box rows (docked panels: the theme's rounded wrapper keeps the Dock, a TableLayoutPanel would not)
        Panel Row(params (string label, Control box)[] cells)
        {
            var row = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 3, 0, 3) };
            var parts = new List<Control>();
            foreach (var (label, box) in cells)
            {
                var part = new Panel { Dock = DockStyle.Left, Width = 100 };
                box.Dock = DockStyle.Fill;
                part.Controls.Add(box);
                part.Controls.Add(new Label { Text = label, Dock = DockStyle.Left, Width = 50, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft });
                parts.Add(part);
            }
            for (int i = parts.Count - 1; i >= 0; i--) row.Controls.Add(parts[i]);
            row.Resize += (_, _) =>
            {
                int w = (row.ClientSize.Width - 6 * (parts.Count - 1)) / parts.Count;
                for (int i = 0; i < parts.Count; i++) { parts[i].Width = w; parts[i].Margin = Padding.Empty; }
            };
            return row;
        }
        var rowName = Row((L.T("Name"), _tbName));
        var rowId = Row(("ID", _tbId), (L.T("Level"), _cbType));
        var rowCenter = Row((L.T("Center X"), _tbCx), (L.T("Center Y"), _tbCy));
        var nbHead = new Label { Text = L.T("Adjacent domains"), Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft };
        _nbList = new ListBox { Dock = DockStyle.Top, Height = 110, IntegralHeight = false };
        var nbRow = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 4, 0, 0) };
        _nbAdd = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        var nbAddBtn = new Button { Text = L.T("+ Add"), Dock = DockStyle.Right, Width = 66 };
        var nbDelBtn = new Button { Text = L.T("Remove"), Dock = DockStyle.Right, Width = 46 };
        nbRow.Controls.Add(_nbAdd);
        nbRow.Controls.Add(nbAddBtn);
        nbRow.Controls.Add(nbDelBtn);
        _nbBoth = new CheckBox { Text = L.T("Add / remove in both directions (recommended)"), Dock = DockStyle.Top, Height = 24, Checked = true };
        _selWarn = new Label { Dock = DockStyle.Top, Height = 38, ForeColor = Color.DarkGoldenrod, UseMnemonic = false };
        var rewardRow = Row((L.T("Reward"), _tbReward));
        _detail.Controls.AddRange([_selWarn, _nbBoth, nbRow, _nbList, nbHead, rewardRow, rowCenter, rowId, rowName, _selInfo, _selTitle]);

        var timesHead = new Label { Text = L.T("Battle times (counted from Monday 00:00)"), Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft };
        _times = TableFileView.MakeGrid();
        _times.Dock = DockStyle.Top;
        _times.Height = 230;
        _times.EditMode = DataGridViewEditMode.EditOnEnter;
        var dayCol = new DataGridViewComboBoxColumn { HeaderText = L.T("Day"), Width = 110, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox };
        foreach (var kv in DomainFile.WeekdayEnum) dayCol.Items.Add(kv.Value);
        _times.Columns.Add(dayCol);
        _times.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Hour"), Width = 60 });
        _times.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Minute"), Width = 60 });
        _times.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Text = "✕", UseColumnTextForButtonValue = true, Width = 40, FlatStyle = FlatStyle.Flat });
        var timesRow = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 6, 0, 0) };
        var addTime = new Button { Text = L.T("+ Add time"), Dock = DockStyle.Left, Width = 96 };
        _tbPerSlot = new TextBox { Dock = DockStyle.Right, Width = 60 };
        timesRow.Controls.Add(new Label { Text = L.T("Battles per slot"), Dock = DockStyle.Right, Width = 120, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.DimGray });
        timesRow.Controls.Add(_tbPerSlot);
        timesRow.Controls.Add(addTime);
        var note = new Label
        {
            Dock = DockStyle.Top, Height = 64, ForeColor = Color.DimGray, UseMnemonic = false,
            Text = L.T("The server reads battle times from domain.sev (exported from domain1.data). Edit outlines / triangles in detail in ▦ Table mode."),
        };
        _side.Controls.AddRange([note, timesRow, _times, timesHead, _detail]);

        _mapMode = new Panel { Dock = DockStyle.Fill };
        _mapMode.Controls.Add(centre);
        _mapMode.Controls.Add(new Splitter { Dock = DockStyle.Right, Width = 4 });
        _mapMode.Controls.Add(_side);
        _mapMode.Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
        _mapMode.Controls.Add(left);
        Controls.Add(_mapMode);
        Controls.Add(top);

        // ── events
        GameFont.Bind(_list);
        GameFont.Bind(_nbList);
        GameFont.Bind(_tbName);
        GameFont.Bind(_canvas);
        _search.TextChanged += (_, _) => FillList();
        _list.DrawItem += DrawListItem;
        _list.SelectedIndexChanged += (_, _) => { if (!_syncing && _list.SelectedIndex >= 0) Select(_shown[_list.SelectedIndex], fromList: true); };
        _canvas.Picked += r => Select(r, fromList: false);
        _canvas.CenterMoved += r => { Changed?.Invoke(); ShowDetail(); _list.Invalidate(); };
        _btnMap.Click += (_, _) => SetMode(table: false);
        _btnTable.Click += (_, _) => SetMode(table: true);
        foreach (var tb in new[] { _tbId, _tbName, _tbReward, _tbCx, _tbCy })
        {
            tb.Leave += (_, _) => ApplyDetail(tb);
            tb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyDetail(tb); } };
        }
        _cbType.SelectedIndexChanged += (_, _) => { if (!_syncing && _sel != null && _cbType.SelectedIndex >= 0) Set(_sel, DomainFile.ColRank, _cbType.SelectedIndex.ToString()); };
        nbAddBtn.Click += (_, _) => AddNeighbour();
        nbDelBtn.Click += (_, _) => RemoveNeighbour();
        _nbList.DoubleClick += (_, _) => { if (_nbList.SelectedItem is NbItem n && Find(n.Id) is { } r) Select(r, fromList: false); };
        bAdd.Click += (_, _) => AddDomain(clone: false);
        bClone.Click += (_, _) => AddDomain(clone: true);
        bDel.Click += (_, _) => DeleteDomain();
        _times.CellValueChanged += OnTimeChanged;
        _times.CurrentCellDirtyStateChanged += (_, _) => { if (_times.IsCurrentCellDirty && _times.CurrentCell is DataGridViewComboBoxCell) _times.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _times.CellContentClick += (_, e) => { if (e.ColumnIndex == 3 && e.RowIndex >= 0 && e.RowIndex < _f.BattleTimes.Rows.Count) { _f.BattleTimes.Rows.RemoveAt(e.RowIndex); Changed?.Invoke(); FillTimes(); } };
        _times.DataError += (_, e) => e.ThrowException = false;
        addTime.Click += (_, _) =>
        {
            var r = _f.BattleTimes.Rows.Count > 0 ? _f.BattleTimes.Rows[^1].Clone() : _f.BattleTimes.NewRecord();
            _f.BattleTimes.Rows.Add(r);
            Changed?.Invoke();
            FillTimes();
        };
        _tbPerSlot.Leave += (_, _) => ApplyPerSlot();
        _tbPerSlot.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyPerSlot(); } };

        FillList();
        FillTimes();
        ShowDetail();
        SetMode(table: false);
    }

    private IEnumerable<string> SiblingFiles()
    {
        var dir = Path.GetDirectoryName(_f.Path);
        if (dir == null || !Directory.Exists(dir)) return [_f.Path];
        var all = Directory.GetFiles(dir, "domain*.data");
        int Order(string p) { var i = Array.FindIndex(KnownFiles, k => k.File.Equals(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)); return i < 0 ? 99 : i; }
        return all.OrderBy(Order).ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    private void SetMode(bool table)
    {
        if (table && _table == null)
        {
            _table = new TableFileView(_f) { Dock = DockStyle.Fill, Visible = false };
            _table.Changed += () => { Changed?.Invoke(); _canvas.Invalidate(); };
            _table.Status += s => Status?.Invoke(s);
            Controls.Add(_table);
            _table.BringToFront();
        }
        if (!table && _table != null) { _table.Commit(); RefreshAll(); }
        if (table) _table!.RefreshAll();
        _mapMode.Visible = !table;
        if (_table != null) _table.Visible = table;
        _btnMap.FlatAppearance.BorderColor = !table ? Theme.Accent : Theme.Border;
        _btnTable.FlatAppearance.BorderColor = table ? Theme.Accent : Theme.Border;
        _btnMap.ForeColor = !table ? Theme.AccentText : Theme.Text;
        _btnTable.ForeColor = table ? Theme.AccentText : Theme.Text;
    }

    // ───────────────────────── list ─────────────────────────

    private RecordTable D => _f.Domains;
    private Record? Find(long id) => D.Rows.FirstOrDefault(r => r.Long(DomainFile.ColId) == id);

    private void FillList()
    {
        var q = _search.Text.Trim();
        _shown = D.Rows.Where(r => q.Length == 0 || r.Text(DomainFile.ColName).Contains(q, StringComparison.OrdinalIgnoreCase) || r.Long(DomainFile.ColId).ToString() == q).ToList();
        _syncing = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _shown) _list.Items.Add(r);
        _list.EndUpdate();
        if (_sel != null && _shown.IndexOf(_sel) is >= 0 and var i) _list.SelectedIndex = i;
        _syncing = false;
        _listCount.Text = L.F("{0} / {1} domains", _shown.Count, D.Rows.Count);
    }

    private void DrawListItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _shown.Count) return;
        var r = _shown[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var g = e.Graphics;
        using (var b = new SolidBrush(sel ? (Theme.Dark ? Theme.AccentDark : SystemColors.Highlight) : _list.BackColor)) g.FillRectangle(b, e.Bounds);
        if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, e.Bounds.Left, e.Bounds.Top + 6, 3, e.Bounds.Height - 12);
        var fg = Theme.Dark ? Theme.Text : SystemColors.ControlText;
        var dim = Theme.Dark ? Theme.TextDim : SystemColors.GrayText;
        using var mono = new Font("Consolas", 9F, FontStyle.Bold);
        TextRenderer.DrawText(g, r.Long(DomainFile.ColId).ToString(), mono, new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, 50, e.Bounds.Height), dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        long type = r.Long(DomainFile.ColRank);
        using (var b = new SolidBrush(TypeColor(type))) g.FillEllipse(b, e.Bounds.Left + 62, e.Bounds.Top + 10, 8, 8);
        TextRenderer.DrawText(g, r.Text(DomainFile.ColName), _list.Font, new Rectangle(e.Bounds.Left + 76, e.Bounds.Top + 3, e.Bounds.Width - 80, 20), fg, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var sub = L.F("{0} · {1} adjacent domains", type == 0 ? L.T("No level") : L.F("Level {0}", type), r.Table(DomainFile.ColNeighbours).Count);
        using var small = new Font(_list.Font.FontFamily, 8F);
        TextRenderer.DrawText(g, sub, small, new Rectangle(e.Bounds.Left + 76, e.Bounds.Top + 23, e.Bounds.Width - 80, 18), dim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private void Select(Record r, bool fromList)
    {
        CommitDetail();
        _sel = r;
        _canvas.Selected = r;
        if (!fromList)
        {
            _syncing = true;
            int i = _shown.IndexOf(r);
            if (i < 0) { _search.Text = ""; FillList(); i = _shown.IndexOf(r); }
            _list.SelectedIndex = i;
            _syncing = false;
        }
        else _canvas.Reveal(r);
        ShowDetail();
    }

    // ───────────────────────── details ─────────────────────────

    private void ShowDetail()
    {
        _syncing = true;
        try
        {
            _detail.Visible = true;
            if (_sel == null || !D.Rows.Contains(_sel))
            {
                _sel = null;
                foreach (Control c in _detail.Controls) c.Visible = c == _selTitle || c == _selInfo;
                _selTitle.Text = L.T("Select a domain");
                _selInfo.Text = L.T("in the list or click it on the map.");
                _detail.Height = 70;
                return;
            }
            foreach (Control c in _detail.Controls) c.Visible = true;
            _detail.Height = _detail.Controls.Cast<Control>().Sum(c => c.Height) + 8;
            var r = _sel;
            long type = r.Long(DomainFile.ColRank);
            _selTitle.Text = r.Text(DomainFile.ColName);
            _selInfo.Text = L.F("ID {0} · {1} · {2} vertices · {3} triangles", r.Long(DomainFile.ColId), DomainFile.TypeEnum.GetValueOrDefault(type, L.F("Level {0}", type)), r.Table(DomainFile.ColVerts).Count, r.Table(DomainFile.ColFaces).Count);
            _tbId.Text = r.Long(DomainFile.ColId).ToString();
            _tbName.Text = r.Text(DomainFile.ColName);
            _tbReward.Text = r.Long(DomainFile.ColReward).ToString();
            _tbCx.Text = r.Long(DomainFile.ColCenterX).ToString();
            _tbCy.Text = r.Long(DomainFile.ColCenterY).ToString();
            _cbType.SelectedIndex = type is >= 0 and <= 3 ? (int)type : -1;
            _nbList.Items.Clear();
            foreach (var n in r.Table(DomainFile.ColNeighbours))
            {
                long id = n.Long(0);
                var other = Find(id);
                bool back = other != null && other.Table(DomainFile.ColNeighbours).Any(x => x.Long(0) == r.Long(DomainFile.ColId));
                _nbList.Items.Add(new NbItem(id, other?.Text(DomainFile.ColName) ?? L.T("(not in the file)"), back));
            }
            _nbAdd.Items.Clear();
            var have = r.Table(DomainFile.ColNeighbours).Select(n => n.Long(0)).ToHashSet();
            foreach (var o in D.Rows.Where(o => o != r && !have.Contains(o.Long(DomainFile.ColId))))
                _nbAdd.Items.Add(new NbItem(o.Long(DomainFile.ColId), o.Text(DomainFile.ColName), true));
            var warns = new List<string>();
            if (r.Table(DomainFile.ColVerts).Count < 3 || r.Table(DomainFile.ColFaces).Count == 0) warns.Add(L.T("No area (fewer than 3 vertices / no triangles) – not visible or clickable on the map."));
            if (_nbList.Items.Cast<NbItem>().Any(n => !n.Back)) warns.Add(L.T("Some adjacent domains are declared in one direction only."));
            if (D.Rows.Count(x => x.Long(DomainFile.ColId) == r.Long(DomainFile.ColId)) > 1) warns.Add(L.T("Duplicate ID – the client only finds the first domain."));
            _selWarn.Text = string.Join("\n", warns);
        }
        finally { _syncing = false; }
    }

    private sealed record NbItem(long Id, string Name, bool Back)
    {
        public override string ToString() => $"{Id}  {Name}{(Back ? "" : "   " + L.T("⚠ one-way"))}";
    }

    private bool Set(Record r, int col, string text)
    {
        var c = D.Columns[col];
        if (!RecordIO.TryParse(c, text, out var v, out var err)) { Status?.Invoke($"{c.Name}: {err}"); return false; }
        if (RecordIO.Format(c, v) == RecordIO.Format(c, r.Values[col])) return true;
        r[col] = v;
        Changed?.Invoke();
        _canvas.Invalidate();
        _list.Invalidate();
        ShowDetail();
        return true;
    }

    private void ApplyDetail(TextBox tb)
    {
        if (_syncing || _sel == null) return;
        int col = tb == _tbId ? DomainFile.ColId : tb == _tbName ? DomainFile.ColName : tb == _tbReward ? DomainFile.ColReward : tb == _tbCx ? DomainFile.ColCenterX : DomainFile.ColCenterY;
        if (col == DomainFile.ColId && long.TryParse(tb.Text, out var nid) && nid != _sel.Long(DomainFile.ColId) && Find(nid) != null)
        { Status?.Invoke(L.F("ID {0} already exists – choose another ID.", nid)); ShowDetail(); return; }
        if (!Set(_sel, col, tb.Text)) ShowDetail();
    }

    private void CommitDetail()
    {
        if (_sel == null) return;
        foreach (var tb in new[] { _tbId, _tbName, _tbReward, _tbCx, _tbCy }) if (tb.Focused) ApplyDetail(tb);
    }

    private void AddNeighbour()
    {
        if (_sel == null || _nbAdd.SelectedItem is not NbItem n) return;
        var me = _sel.Long(DomainFile.ColId);
        Link(_sel, n.Id);
        if (_nbBoth.Checked && Find(n.Id) is { } o) Link(o, me);
        Changed?.Invoke();
        _canvas.Invalidate();
        _list.Invalidate();
        ShowDetail();
    }

    private static void Link(Record r, long id)
    {
        var list = r.Table(DomainFile.ColNeighbours);
        if (list.Any(x => x.Long(0) == id)) return;
        var n = Record.CreateDefault(DomainFile.NeighbourColumns);
        n[0] = (int)id;
        list.Add(n);
    }

    private void RemoveNeighbour()
    {
        if (_sel == null || _nbList.SelectedItem is not NbItem n) return;
        var me = _sel.Long(DomainFile.ColId);
        _sel.Table(DomainFile.ColNeighbours).RemoveAll(x => x.Long(0) == n.Id);
        if (_nbBoth.Checked && Find(n.Id) is { } o) o.Table(DomainFile.ColNeighbours).RemoveAll(x => x.Long(0) == me);
        Changed?.Invoke();
        _canvas.Invalidate();
        _list.Invalidate();
        ShowDetail();
    }

    private void AddDomain(bool clone)
    {
        if (clone && _sel == null) return;
        var r = clone ? _sel!.Clone() : D.NewRecord();
        long next = D.Rows.Count == 0 ? 1 : D.Rows.Max(x => x.Long(DomainFile.ColId)) + 1;
        r[DomainFile.ColId] = (int)next;
        r.Table(DomainFile.ColNeighbours).Clear();
        if (clone)
        {
            r[DomainFile.ColName] = (r.Text(DomainFile.ColName) + " 2") is var nm && nm.Length < DomainFile.NameChars ? nm : nm[..(DomainFile.NameChars - 1)];
            // shift the copy a little so it is visible next to the original
            foreach (var v in r.Table(DomainFile.ColVerts)) { v[0] = (int)(v.Long(0) + 40); v[1] = (int)(v.Long(1) + 40); }
            r[DomainFile.ColCenterX] = (int)(r.Long(DomainFile.ColCenterX) + 40);
            r[DomainFile.ColCenterY] = (int)(r.Long(DomainFile.ColCenterY) + 40);
        }
        else r[DomainFile.ColName] = L.F("Domain {0}", next);
        D.Rows.Add(r);
        Changed?.Invoke();
        _search.Text = "";
        FillList();
        Select(r, fromList: false);
        _canvas.Invalidate();
        Status?.Invoke(clone ? L.T("Domain cloned (shifted by 40px, no adjacent domains yet).") : L.T("New domain added – it has no outline yet: draw vertices / triangles in ▦ Table mode or clone an existing domain."));
    }

    private void DeleteDomain()
    {
        if (_sel == null) return;
        var me = _sel.Long(DomainFile.ColId);
        if (Msg.Show(L.F("Delete domain {0} \"{1}\"? Adjacent domains are unlinked from it as well.", me, _sel.Text(DomainFile.ColName)), L.T("Delete domain"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        D.Rows.Remove(_sel);
        foreach (var o in D.Rows) o.Table(DomainFile.ColNeighbours).RemoveAll(x => x.Long(0) == me);
        _sel = null;
        _canvas.Selected = null;
        Changed?.Invoke();
        FillList();
        ShowDetail();
        _canvas.Invalidate();
    }

    // ───────────────────────── battle times ─────────────────────────

    private void FillTimes()
    {
        _syncing = true;
        _times.Rows.Clear();
        foreach (var t in _f.BattleTimes.Rows)
        {
            int wd = (int)t.Long(0);
            int i = _times.Rows.Add(DomainFile.WeekdayEnum.GetValueOrDefault(wd, DomainFile.WeekdayEnum[0]), t.Long(1).ToString(), t.Long(2).ToString().PadLeft(2, '0'));
            _times.Rows[i].Tag = t;
        }
        _tbPerSlot.Text = _f.Header.Rows.Count > 0 ? _f.Header.Rows[0].Long(DomainFile.HdrBattlesPerSlot).ToString() : "";
        _syncing = false;
    }

    private void OnTimeChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_syncing || e.RowIndex < 0 || _times.Rows[e.RowIndex].Tag is not Record t) return;
        var cell = _times.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString() ?? "";
        int col = e.ColumnIndex;
        string text = col == 0 ? DomainFile.WeekdayEnum.First(kv => kv.Value == cell).Key.ToString() : cell;
        int max = col == 1 ? 23 : col == 2 ? 59 : 6;
        if (!int.TryParse(text, out int v) || v < 0 || v > max) { Status?.Invoke(L.F("Value must be between 0 and {0}.", max)); FillTimes(); return; }
        if (t.Long(col) == v) return;
        t[col] = v;
        Changed?.Invoke();
    }

    private void ApplyPerSlot()
    {
        if (_syncing || _f.Header.Rows.Count == 0) return;
        var h = _f.Header.Rows[0];
        if (!int.TryParse(_tbPerSlot.Text, out int v) || v < 0) { Status?.Invoke(L.T("Battles per slot must be a number ≥ 0.")); FillTimes(); return; }
        if (h.Long(DomainFile.HdrBattlesPerSlot) == v) return;
        h[DomainFile.HdrBattlesPerSlot] = v;
        Changed?.Invoke();
    }

    // ───────────────────────── IModuleView ─────────────────────────

    public bool Commit()
    {
        CommitDetail();
        if (_times.IsCurrentCellInEditMode) _times.EndEdit();
        if (_tbPerSlot.Focused) ApplyPerSlot();
        return _table?.Commit() ?? true;
    }

    public void FocusSearch()
    {
        if (_table?.Visible == true) _table.FocusSearch();
        else { _search.Focus(); _search.SelectAll(); }
    }

    public void RefreshAll()
    {
        FillList();
        FillTimes();
        ShowDetail();
        _canvas.Invalidate();
        if (_table?.Visible == true) _table.RefreshAll();
    }

    public void Locate(ChangeItem c)
    {
        if (c.Group == "Domains" && c.Target is Record r && D.Rows.Contains(r))
        {
            if (_table?.Visible == true) { _table.Locate(c); return; }
            Select(r, fromList: false);
            _canvas.Reveal(r);
        }
        else { SetMode(table: true); _table!.Locate(c); }
    }

    public RecordTable? CurrentTable => _table?.Visible == true ? _table.CurrentTable : D;

    /// <summary>File tab (domain.data Guild …), the open one underlined in green.</summary>
    private sealed class FileTab : Control
    {
        private readonly bool _current;
        private bool _hover;
        public FileTab(string text, bool current)
        {
            Text = text; _current = current;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Font = new Font("Segoe UI", 9F, current ? FontStyle.Bold : FontStyle.Regular);
            Size = new Size(TextRenderer.MeasureText(text, Font).Width + 14, 28);
            Cursor = current ? Cursors.Default : Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(_current ? (Theme.Dark ? Theme.Panel : SystemColors.ControlLight) : _hover ? (Theme.Dark ? Theme.Hover : SystemColors.ControlLight) : Parent?.BackColor ?? BackColor);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, _current ? (Theme.Dark ? Theme.Text : SystemColors.ControlText) : (Theme.Dark ? Theme.TextDim : SystemColors.GrayText),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (_current) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, Height - 2, Width, 2);
        }
    }

    private sealed class LegendBar : Control
    {
        public LegendBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = new Font("Segoe UI", 8.5F);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Dark ? Theme.Panel : SystemColors.Control);
            int x = 10;
            foreach (var t in new long[] { 0, 1, 2, 3 })
            {
                using (var b = new SolidBrush(TypeColor(t))) g.FillRectangle(b, x, Height / 2 - 5, 10, 10);
                x += 14;
                var s = DomainFile.TypeEnum[t];
                TextRenderer.DrawText(g, s, Font, new Point(x, Height / 2 - 8), Theme.Dark ? Theme.Text : SystemColors.ControlText);
                x += TextRenderer.MeasureText(s, Font).Width + 14;
            }
            TextRenderer.DrawText(g, L.T("Mouse wheel: zoom · drag: pan · double-click: fit · drag the centre dot of the selected domain to move it"),
                Font, new Point(x + 10, Height / 2 - 8), Theme.Dark ? Theme.TextDim : SystemColors.GrayText);
        }
    }
}

/// <summary>Map canvas: draws every domain outline filled by class, names at the centres; zoom / pan / pick / drag centre.</summary>
public sealed class DomainCanvas : Control
{
    private readonly DomainFile _f;
    private float _scale = 1;
    private PointF _offset;
    private bool _fitted;
    private Point _down;
    private PointF _downOffset;
    private bool _panning, _dragCenter, _moved;
    private Record? _sel, _hover;

    public event Action<Record>? Picked;
    public event Action<Record>? CenterMoved;

    public DomainCanvas(DomainFile f)
    {
        _f = f;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font("Segoe UI", 10F, FontStyle.Bold);
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Record? Selected { get => _sel; set { _sel = value; Invalidate(); } }

    private IEnumerable<Record> Domains => _f.Domains.Rows;

    private static IEnumerable<PointF> Verts(Record r) => r.Table(DomainFile.ColVerts).Select(v => new PointF(v.Long(0), v.Long(1)));

    private RectangleF WorldBounds()
    {
        var pts = Domains.SelectMany(Verts).Concat(Domains.Select(r => new PointF(r.Long(DomainFile.ColCenterX), r.Long(DomainFile.ColCenterY)))).ToList();
        if (pts.Count == 0) return new RectangleF(0, 0, 1000, 1000);
        float x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y), x1 = pts.Max(p => p.X), y1 = pts.Max(p => p.Y);
        return RectangleF.FromLTRB(x0, y0, Math.Max(x1, x0 + 1), Math.Max(y1, y0 + 1));
    }

    public void Fit()
    {
        var b = WorldBounds();
        if (Width < 20 || Height < 20) return;
        _scale = Math.Min((Width - 60) / b.Width, (Height - 60) / b.Height);
        _offset = new PointF(Width / 2f - (b.X + b.Width / 2) * _scale, Height / 2f - (b.Y + b.Height / 2) * _scale);
        _fitted = true;
        Invalidate();
    }

    /// <summary>Scrolls so the domain is visible (keeps zoom).</summary>
    public void Reveal(Record r)
    {
        var c = ToScreen(new PointF(r.Long(DomainFile.ColCenterX), r.Long(DomainFile.ColCenterY)));
        if (c.X < 40 || c.Y < 40 || c.X > Width - 40 || c.Y > Height - 40)
            _offset = new PointF(_offset.X + Width / 2f - c.X, _offset.Y + Height / 2f - c.Y);
        Invalidate();
    }

    private PointF ToScreen(PointF w) => new(w.X * _scale + _offset.X, w.Y * _scale + _offset.Y);
    private PointF ToWorld(Point s) => new((s.X - _offset.X) / _scale, (s.Y - _offset.Y) / _scale);

    private GraphicsPath Outline(Record r)
    {
        var p = new GraphicsPath();
        var pts = Verts(r).Select(ToScreen).ToArray();
        if (pts.Length >= 3) p.AddPolygon(pts);
        return p;
    }

    private Record? HitTest(Point pt)
    {
        foreach (var r in Domains.Reverse())
        {
            using var path = Outline(r);
            if (path.PointCount > 0 && path.IsVisible(pt)) return r;
        }
        return null;
    }

    private bool _userView; // the user zoomed / panned: stop auto-fitting

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_userView) Fit();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_fitted) Fit();
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Dark ? Color.FromArgb(12, 15, 17) : Color.FromArgb(235, 238, 240));
        var neigh = _sel?.Table(DomainFile.ColNeighbours).Select(n => n.Long(0)).ToHashSet() ?? new HashSet<long>();

        foreach (var r in Domains)
        {
            using var path = Outline(r);
            if (path.PointCount == 0) continue;
            var tc = DomainMapView.TypeColor(r.Long(DomainFile.ColRank));
            bool sel = r == _sel, hov = r == _hover, nb = neigh.Contains(r.Long(DomainFile.ColId));
            int a = sel ? 150 : hov ? 110 : nb ? 95 : 70;
            using (var b = new SolidBrush(Blend(tc, a))) g.FillPath(b, path);
            using (var p = new Pen(sel ? Theme.Accent : nb ? Color.FromArgb(200, Theme.AccentText) : Color.FromArgb(150, 120, 150, 190), sel ? 2.4f : nb ? 1.6f : 1f) { LineJoin = LineJoin.Round })
                g.DrawPath(p, path);
        }
        // dashed links from the selected domain to its neighbours (as JD IDE)
        if (_sel != null)
        {
            var sc = ToScreen(new PointF(_sel.Long(DomainFile.ColCenterX), _sel.Long(DomainFile.ColCenterY)));
            using var dash = new Pen(Color.FromArgb(220, Theme.AccentText), 1.6f) { DashStyle = DashStyle.Dash };
            foreach (var o in Domains.Where(o => o != _sel && neigh.Contains(o.Long(DomainFile.ColId))))
                g.DrawLine(dash, sc, ToScreen(new PointF(o.Long(DomainFile.ColCenterX), o.Long(DomainFile.ColCenterY))));
        }
        foreach (var r in Domains)
        {
            var c = ToScreen(new PointF(r.Long(DomainFile.ColCenterX), r.Long(DomainFile.ColCenterY)));
            bool sel = r == _sel;
            float rad = sel ? 5 : 3.5f;
            using (var b = new SolidBrush(sel ? Theme.Accent : Color.FromArgb(200, 210, 220))) g.FillEllipse(b, c.X - rad, c.Y - rad, rad * 2, rad * 2);
            var name = r.Text(DomainFile.ColName);
            if (name.Length == 0) continue;
            var sz = TextRenderer.MeasureText(name, Font);
            var tp = new Point((int)(c.X - sz.Width / 2f), (int)(c.Y - sz.Height - 4));
            TextRenderer.DrawText(g, name, Font, new Point(tp.X + 1, tp.Y + 1), Color.Black);
            TextRenderer.DrawText(g, name, Font, tp, sel ? Theme.AccentText : Color.FromArgb(235, 240, 245));
        }
        if (!Domains.Any())
            TextRenderer.DrawText(g, L.T("The file has no domains yet."), Font, ClientRectangle, Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static Color Blend(Color c, int alpha) => Color.FromArgb(alpha + 60, (c.R + 30) / 2, (c.G + 40) / 2, (c.B + 60) / 2);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _userView = true;
        float f = e.Delta > 0 ? 1.2f : 1 / 1.2f;
        var w = ToWorld(e.Location);
        _scale = Math.Clamp(_scale * f, 0.02f, 40f);
        _offset = new PointF(e.X - w.X * _scale, e.Y - w.Y * _scale);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        _down = e.Location;
        _downOffset = _offset;
        _moved = false;
        if (e.Button == MouseButtons.Left && _sel != null)
        {
            var c = ToScreen(new PointF(_sel.Long(DomainFile.ColCenterX), _sel.Long(DomainFile.ColCenterY)));
            if (Math.Abs(c.X - e.X) <= 8 && Math.Abs(c.Y - e.Y) <= 8) { _dragCenter = true; Cursor = Cursors.SizeAll; return; }
        }
        _panning = e.Button is MouseButtons.Left or MouseButtons.Middle or MouseButtons.Right;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.None && Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) > 4) _moved = true;
        if (_dragCenter && _sel != null && e.Button == MouseButtons.Left)
        {
            var w = ToWorld(e.Location);
            int x = (int)Math.Round(w.X), y = (int)Math.Round(w.Y);
            if (x != _sel.Long(DomainFile.ColCenterX) || y != _sel.Long(DomainFile.ColCenterY))
            {
                _sel[DomainFile.ColCenterX] = x;
                _sel[DomainFile.ColCenterY] = y;
                Invalidate();
            }
            return;
        }
        if (_panning && e.Button != MouseButtons.None && _moved)
        {
            _offset = new PointF(_downOffset.X + e.X - _down.X, _downOffset.Y + e.Y - _down.Y);
            _userView = true;
            Cursor = Cursors.SizeAll;
            Invalidate();
            return;
        }
        var h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragCenter) { _dragCenter = false; Cursor = Cursors.Default; if (_moved && _sel != null) CenterMoved?.Invoke(_sel); return; }
        _panning = false;
        Cursor = Cursors.Default;
        if (!_moved && e.Button == MouseButtons.Left && HitTest(e.Location) is { } r) Picked?.Invoke(r);
        base.OnMouseUp(e);
    }

    protected override void OnDoubleClick(EventArgs e) { _userView = false; Fit(); base.OnDoubleClick(e); }
}
