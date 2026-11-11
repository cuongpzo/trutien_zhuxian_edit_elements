using ElementsCore.Schema;

namespace ElementsEditor;

/// <summary>
/// JD IDE-style list picker: the former left "lists" panel (filter box, filter combo, ListView)
/// now lives in a drop-down popup opened from a selector button above the items grid.
/// All list logic (PopulateListsView / OnListSelected / SelectListEntry) is unchanged.
/// </summary>
public sealed partial class MainForm
{
    private ListSelectorButton _listSelector = null!;
    private ToolStripDropDown _listPopup = null!;
    private Label _selectorInfo = null!;

    private void SetupListSelector(Control mainSplitter, Control workLeft)
    {
        // 1. take the old left column out of the window
        Controls.Remove(mainSplitter);
        Controls.Remove(_listViewHost);
        _listViewHost.Dock = DockStyle.Fill;

        // wider columns now that the list has its own popup
        if (_listsView.Columns.Count >= 4)
        {
            _listsView.Columns[0].Width = 46;
            _listsView.Columns[1].Width = 250;
            _listsView.Columns[2].Width = 70;
            _listsView.Columns[3].Width = 70;
        }

        // 2. popup that hosts it
        var host = new ToolStripControlHost(_listViewHost)
        {
            AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = new Size(470, 580),
        };
        _listPopup = new ToolStripDropDown { Padding = new Padding(1), AutoClose = true, DropShadowEnabled = true };
        _listPopup.Items.Add(host);
        _listPopup.Opened += (_, _) => { _listSearchBox.Focus(); _listSearchBox.SelectAll(); EnsureSelectedVisible(); };
        _listPopup.Closed += (_, _) => _listSelector.Open = false;

        // force the ListView handle so programmatic selection (JumpTo → SelectListEntry) fires events
        _ = _listsView.Handle;

        // close after picking with the mouse or Enter (arrow keys keep it open while browsing)
        _listsView.MouseUp += (_, e) => { if (_listsView.GetItemAt(e.X, e.Y) != null) _listPopup.Close(); };
        _listsView.KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Escape) { e.Handled = true; _listPopup.Close(); } };
        _listSearchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && _listsView.Items.Count > 0)
            {
                e.Handled = e.SuppressKeyPress = true;
                _listsView.Focus();
                if (_listsView.SelectedIndices.Count == 0) _listsView.Items[0].Selected = true;
            }
            else if (e.KeyCode == Keys.Enter && _listsView.Items.Count > 0)
            {
                e.Handled = e.SuppressKeyPress = true;
                _listsView.Items[0].Selected = true;
                _listPopup.Close();
            }
            else if (e.KeyCode == Keys.Escape) { e.Handled = true; _listPopup.Close(); }
        };

        // 3. selector bar on top of the work area (stays visible in NPC dialog mode too)
        var bar = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(8, 8, 8, 6) };
        _listSelector = new ListSelectorButton { Dock = DockStyle.Left, Width = workLeft.Width - 8 };
        _selectorInfo = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0), ForeColor = Color.DimGray, UseMnemonic = false };
        bar.Controls.Add(_selectorInfo);
        bar.Controls.Add(_listSelector);
        _mainWorkHost.Controls.Add(bar); // added last → docked first → sits on top
        workLeft.SizeChanged += (_, _) => _listSelector.Width = Math.Max(220, workLeft.Width - 8);

        _listSelector.Click += (_, _) => ToggleListPopup();
        _listsView.SelectedIndexChanged += (_, _) => UpdateListSelector();
        Load += (_, _) => { Theme.Apply(_listPopup); Theme.Apply(_listViewHost); };
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.L) { e.SuppressKeyPress = true; ToggleListPopup(); }
        };
    }

    private void ToggleListPopup()
    {
        if (_listPopup.Visible) { _listPopup.Close(); return; }
        if (!_listSelector.Visible) return; // another tab of the activity bar is shown
        _listPopup.Items[0].Size = new Size(Math.Max(_listSelector.Width, 470), Math.Min(620, Math.Max(360, ClientSize.Height - 160)));
        _listSelector.Open = true;
        _listPopup.Show(_listSelector, new Point(0, _listSelector.Height + 2));
    }

    /// <summary>After opening a file: show the first list that has records (JD IDE opens EQUIPMENT_ADDON).</summary>
    private void SelectFirstDataList()
    {
        // reopen the list that was open last time; else the first list with records
        if (_settings.LastList is int last && SelectListEntry(e => !e.IsTalk && e.List!.Index == last)) return;
        SelectListEntry(e => !e.IsTalk && e.Count > 0);
    }

    private void EnsureSelectedVisible()
    {
        if (_listsView.SelectedIndices.Count > 0) _listsView.EnsureVisible(_listsView.SelectedIndices[0]);
    }

    private void UpdateListSelector()
    {
        if (_listsView.SelectedIndices.Count == 0) return;
        int i = _listsView.SelectedIndices[0];
        if (i < 0 || i >= _visibleLists.Count) return;
        var e = _visibleLists[i];
        if (e.IsTalk)
        {
            _listSelector.SetEntry("TALK", e.DisplayTitle, e.Count, Theme.Purple);
            _selectorInfo.Text = $"Hội thoại NPC · {e.Count:N0} dialog";
            return;
        }
        var l = e.List!;
        var dot = l.Items.Count == 0 ? Theme.TextFaint
            : l.Schema.Match switch { SchemaMatch.Exact => Theme.Accent, SchemaMatch.Borrowed => Theme.Amber, _ => Theme.TextDim };
        _listSelector.SetEntry(l.Index.ToString(), l.Name, l.Items.Count, dot);
        string schema = l.Schema.Match switch
        {
            SchemaMatch.Exact => $"Exact · {l.Schema.SourceVersion}",
            SchemaMatch.Borrowed => $"Mượn {l.Schema.SourceVersion}",
            _ => "Tự dò",
        };
        _selectorInfo.Text = $"{l.Schema.DisplayName ?? l.Name}   ·   {l.Items.Count:N0} records × {l.ItemSize} B   ·   {schema}   ·   Ctrl+L đổi list";
    }
}

/// <summary>Rounded selector button: ● index NAME ……… count ⌄ (JD IDE look).</summary>
public sealed class ListSelectorButton : Control
{
    private string _index = "", _name = "Chọn list…  (Ctrl+L)";
    private int _count = -1;
    private Color _dot = Color.Gray;
    private bool _hover, _open;

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Open { get => _open; set { _open = value; Invalidate(); } }

    public ListSelectorButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        TabStop = true;
    }

    public void SetEntry(string index, string name, int count, Color dot)
    {
        _index = index; _name = name; _count = count; _dot = dot;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.F4 || (e.Alt && e.KeyCode == Keys.Down)) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool dark = Theme.Dark;
        var bg = Parent?.BackColor ?? (dark ? Theme.Bg : SystemColors.Control);
        var fill = dark ? (_hover || _open ? Theme.Hover : Theme.Input) : (_hover ? SystemColors.ControlLight : SystemColors.Window);
        var border = _open || Focused ? Theme.Accent : (dark ? (_hover ? Theme.BorderHover : Theme.Border) : SystemColors.ControlDark);
        var text = dark ? Theme.Text : SystemColors.ControlText;
        var dim = dark ? Theme.TextDim : SystemColors.GrayText;

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(bg);
        var r = new RectangleF(1f, 1f, Width - 2.5f, Height - 2.5f);
        using (var path = Theme.RoundRect(r, 7))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (_open || Focused)
                using (var glow = new Pen(Color.FromArgb(50, Theme.Accent), 3f)) g.DrawPath(glow, path);
            using (var p = new Pen(border, _open ? 1.3f : 1f)) g.DrawPath(p, path);
        }

        int x = 14, cy = Height / 2;
        using (var b = new SolidBrush(_dot)) g.FillEllipse(b, x, cy - 4, 8, 8);
        x += 18;
        using var small = new Font("Consolas", 8.5F);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (_index.Length > 0)
        {
            var sz = TextRenderer.MeasureText(_index, small);
            TextRenderer.DrawText(g, _index, small, new Rectangle(x, 0, sz.Width, Height), dim, flags);
            x += sz.Width + 4;
        }
        // chevron
        int cx = Width - 20;
        using (var p = new Pen(dim, 1.6f))
        {
            if (_open) g.DrawLines(p, new[] { new Point(cx - 5, cy + 2), new Point(cx, cy - 3), new Point(cx + 5, cy + 2) });
            else g.DrawLines(p, new[] { new Point(cx - 5, cy - 2), new Point(cx, cy + 3), new Point(cx + 5, cy - 2) });
        }
        int right = cx - 14;
        if (_count >= 0)
        {
            var cs = _count.ToString("N0");
            var csz = TextRenderer.MeasureText(cs, small);
            TextRenderer.DrawText(g, cs, small, new Rectangle(right - csz.Width, 0, csz.Width, Height), dim, flags);
            right -= csz.Width + 10;
        }
        TextRenderer.DrawText(g, _name, Font, new Rectangle(x, 0, Math.Max(10, right - x), Height), text, flags | TextFormatFlags.EndEllipsis);
    }
}
