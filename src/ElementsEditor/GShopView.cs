using System.Globalization;
using ElementsCore;
using ElementsCore.Exchange;
using ElementsCore.Formats;

namespace ElementsEditor;

/// <summary>
/// GShop tab, JD IDE style: category tree (with item counts) · item list (icon, name, ID, price) ·
/// item form grouped as Item / Price / Category / On sale / Description / Free gift / Other fields.
/// Works on the field names of any gshop layout (TT3 2660 B, source 2630 B, old client, lucky bag,
/// exchange); fields a layout does not have are simply not shown. "▦ Table" switches to the grid.
/// </summary>
public sealed class GShopView : Panel, IModuleView
{
    public event Action? Changed;
    public event Action<string>? Status;

    private readonly GShopFile _f;
    private readonly RecordTable _items, _cats;
    private readonly Panel _shopMode;
    private TableFileView? _table;
    private readonly Button _btnShop, _btnTable;
    private readonly TreeView _tree;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly Label _listCount;
    private readonly Panel _form;
    private List<Record> _shown = new();
    private Record? _sel;
    private bool _building;
    private (int main, int sub)? _filter; // null = all; sub -1 = whole main category

    public GShopView(GShopFile f)
    {
        _f = f;
        _items = f.Tables.First(t => t.Name == "Items");
        _cats = f.Tables.First(t => t.Name == "Categories");

        // ── top: info + Shop / Table switch
        var top = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 6, 8, 4) };
        var info = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray, UseMnemonic = false };
        var stamp = f.Tables.FirstOrDefault(t => t.Name == "Header")?.Rows.FirstOrDefault()?.Long(0) ?? 0;
        info.Text = L.F("Items: {0}   ·   Timestamp: {1}   ·   Layout: {2} ({3:N0} bytes)", _items.Rows.Count, stamp, f.ItemLayout.Title, f.ItemLayout.Size);
        var sw = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        _btnShop = new Button { Text = L.T("🛒 Shop"), Width = 92, Height = 28 };
        _btnTable = new Button { Text = L.T("▦ Table"), Width = 84, Height = 28 };
        sw.Controls.AddRange([_btnShop, _btnTable]);
        top.Controls.Add(info);
        top.Controls.Add(sw);

        // ── left: categories
        var left = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(8, 6, 4, 6) };
        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, ShowLines = false, BorderStyle = BorderStyle.None, ItemHeight = 26 };
        var editCats = new Button { Text = L.T("Edit categories…"), Dock = DockStyle.Bottom, Height = 30 };
        left.Controls.Add(_tree);
        left.Controls.Add(editCats);

        // ── middle: item list
        var mid = new Panel { Dock = DockStyle.Left, Width = 330, Padding = new Padding(4, 6, 4, 6) };
        _search = new TextBox { Dock = DockStyle.Top, PlaceholderText = L.T("Name, item ID or position…") };
        _list = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44, IntegralHeight = false, BorderStyle = BorderStyle.None };
        var listBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, WrapContents = false };
        var bClone = new Button { Text = L.T("Duplicate"), Width = 82, Height = 28 };
        var bNew = new Button { Text = L.T("+ New"), Width = 64, Height = 28 };
        var bDel = new Button { Text = L.T("Delete"), Width = 62, Height = 28 };
        var bUp = new Button { Text = "↑", Width = 32, Height = 28 };
        var bDown = new Button { Text = "↓", Width = 32, Height = 28 };
        listBar.Controls.AddRange([bNew, bClone, bDel, bUp, bDown]);
        _listCount = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleRight };
        mid.Controls.Add(_list);
        mid.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
        mid.Controls.Add(_search);
        mid.Controls.Add(_listCount);
        mid.Controls.Add(listBar);

        // ── right: form
        _form = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 6, 14, 10) };
        _form.Resize += (_, _) => Relayout();

        _shopMode = new Panel { Dock = DockStyle.Fill };
        _shopMode.Controls.Add(_form);
        _shopMode.Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
        _shopMode.Controls.Add(mid);
        _shopMode.Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
        _shopMode.Controls.Add(left);
        Controls.Add(_shopMode);
        Controls.Add(top);

        _tree.AfterSelect += (_, e) => { _filter = e.Node?.Tag as (int, int)?; FillList(); };
        _search.TextChanged += (_, _) => FillList();
        _list.DrawItem += DrawItem;
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count) Select(_shown[_list.SelectedIndex]); };
        _btnShop.Click += (_, _) => SetMode(false);
        _btnTable.Click += (_, _) => SetMode(true);
        editCats.Click += (_, _) => { SetMode(true); _table?.Locate(new ChangeItem { Group = "Categories" }); };
        bNew.Click += (_, _) => AddItem(clone: false);
        bClone.Click += (_, _) => AddItem(clone: true);
        bDel.Click += (_, _) => DeleteItem();
        bUp.Click += (_, _) => MoveItem(-1);
        bDown.Click += (_, _) => MoveItem(+1);
        Action refresh = () => { if (IsHandleCreated) BeginInvoke(() => { _list.Invalidate(); if (_sel != null) BuildForm(); }); };
        GameIcons.Ready += refresh;
        IdNames.Changed += refresh;
        Disposed += (_, _) => { GameIcons.Ready -= refresh; IdNames.Changed -= refresh; };

        FillTree();
        FillList();
        SetMode(false);
    }

    // ───────────────────────── columns ─────────────────────────

    private int Col(string name) => _items.IndexOf(name);
    private long Num(Record r, string name) => Col(name) is var c and >= 0 && r.Values[c] != null ? r.Long(c) : 0;
    private string Txt(Record r, string name) => Col(name) is var c and >= 0 ? r.Text(c) : "";

    private bool Set(Record r, string name, string text)
    {
        int c = Col(name);
        if (c < 0) return false;
        var col = _items.Columns[c];
        if (!RecordIO.TryParse(col, text, out var v, out var err)) { Status?.Invoke($"{name}: {err}"); return false; }
        if (RecordIO.Format(col, v) == RecordIO.Format(col, r.Values[c])) return true;
        r[c] = v;
        Changed?.Invoke();
        _list.Invalidate();
        return true;
    }

    // ───────────────────────── categories ─────────────────────────

    private string MainName(int m) => m >= 0 && m < _cats.Rows.Count ? _cats.Rows[m].Text(1) : $"#{m}";
    private string SubName(int m, int s) => m >= 0 && m < _cats.Rows.Count && s >= 0 && s < _cats.Rows[m].Table(2).Count ? _cats.Rows[m].Table(2)[s].Text(0) : $"#{s}";

    private void FillTree()
    {
        var keep = _filter;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var all = _tree.Nodes.Add(L.F("All items   ({0})", _items.Rows.Count));
        all.Tag = null;
        TreeNode? select = all;
        for (int m = 0; m < _cats.Rows.Count; m++)
        {
            int n = _items.Rows.Count(r => Num(r, "main_type") == m);
            var node = _tree.Nodes.Add($"{MainName(m)}   ({n})");
            node.Tag = ((int, int)?)(m, -1);
            if (keep is { } k && k.main == m && k.sub == -1) select = node;
            var subs = _cats.Rows[m].Table(2);
            for (int s = 0; s < subs.Count; s++)
            {
                int ns = _items.Rows.Count(r => Num(r, "main_type") == m && Num(r, "sub_type") == s);
                var sn = node.Nodes.Add($"{subs[s].Text(0)}   ({ns})");
                sn.Tag = ((int, int)?)(m, s);
                if (keep is { } k2 && k2.main == m && k2.sub == s) select = sn;
            }
        }
        _tree.EndUpdate();
        _tree.SelectedNode = select;
    }

    // ───────────────────────── item list ─────────────────────────

    private void FillList()
    {
        var q = _search.Text.Trim();
        _shown = _items.Rows.Where((r, i) =>
        {
            if (_filter is { } f && (Num(r, "main_type") != f.main || f.sub >= 0 && Num(r, "sub_type") != f.sub)) return false;
            if (q.Length == 0) return true;
            return Txt(r, "name").Contains(q, StringComparison.OrdinalIgnoreCase) || Num(r, "id").ToString() == q || (i + 1).ToString() == q;
        }).ToList();
        _building = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _shown) _list.Items.Add(r);
        _list.EndUpdate();
        _building = false;
        _listCount.Text = $"{_shown.Count:N0} / {_items.Rows.Count:N0}";
        int idx = _sel != null ? _shown.IndexOf(_sel) : -1;
        if (idx < 0 && _shown.Count > 0) idx = 0;
        if (idx >= 0) _list.SelectedIndex = idx;
        else { _sel = null; BuildForm(); }
    }

    private static string Price(long hundredths) => (hundredths / 100.0).ToString("N2", CultureInfo.CurrentCulture);

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _shown.Count) return;
        var r = _shown[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var g = e.Graphics;
        using (var b = new SolidBrush(sel ? (Theme.Dark ? Theme.AccentDark : SystemColors.Highlight) : _list.BackColor)) g.FillRectangle(b, e.Bounds);
        if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, e.Bounds.Left, e.Bounds.Top + 6, 3, e.Bounds.Height - 12);
        var fg = Theme.Dark ? Theme.Text : SystemColors.ControlText;
        var dim = Theme.Dark ? Theme.TextDim : SystemColors.GrayText;
        int pos = _items.Rows.IndexOf(r) + 1;
        TextRenderer.DrawText(g, pos.ToString(), _list.Font, new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, 30, e.Bounds.Height), dim, TextFormatFlags.VerticalCenter);
        var icon = GameIcons.ForPath(Txt(r, "icon"), 32) ?? GameIcons.ForItem(Num(r, "id"), 32);
        var box = new Rectangle(e.Bounds.Left + 38, e.Bounds.Top + 6, 32, 32);
        using (var p = new Pen(Theme.Dark ? Theme.Border : SystemColors.ControlDark)) g.DrawRectangle(p, box);
        if (icon != null) g.DrawImage(icon, box.Left + (32 - icon.Width) / 2, box.Top + (32 - icon.Height) / 2, icon.Width, icon.Height);
        bool hasPrice = Col("price") >= 0;
        int right = e.Bounds.Right - (hasPrice ? 80 : 8);
        TextRenderer.DrawText(g, Txt(r, "name"), _list.Font, new Rectangle(box.Right + 8, e.Bounds.Top + 4, right - box.Right - 8, 18), fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var mono = new Font("Consolas", 8.5F);
        TextRenderer.DrawText(g, Num(r, "id").ToString(), mono, new Rectangle(box.Right + 8, e.Bounds.Top + 23, 120, 16), dim, TextFormatFlags.Left);
        if (hasPrice)
        {
            using var bold = new Font(_list.Font, FontStyle.Bold);
            TextRenderer.DrawText(g, Price(Num(r, "price")), bold, new Rectangle(right, e.Bounds.Top, 72, e.Bounds.Height), fg, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    private void Select(Record r)
    {
        if (_building) return;
        _sel = r;
        BuildForm();
    }

    private void AddItem(bool clone)
    {
        if (clone && _sel == null) return;
        var r = clone ? _sel!.Clone() : _items.NewRecord();
        if (!clone && _filter is { } f)
        {
            if (Col("main_type") >= 0) r[Col("main_type")] = f.main;
            if (Col("sub_type") >= 0 && f.sub >= 0) r[Col("sub_type")] = f.sub;
        }
        if (Col("local_id") >= 0) r[Col("local_id")] = (int)(_items.Rows.Count == 0 ? 1 : _items.Rows.Max(x => Num(x, "local_id")) + 1);
        int at = _sel != null ? _items.Rows.IndexOf(_sel) + 1 : _items.Rows.Count;
        _items.Rows.Insert(at, r);
        _sel = r;
        Changed?.Invoke();
        _search.Text = "";
        FillTree();
        FillList();
        Status?.Invoke(clone ? L.T("Item duplicated (placed after the original).") : L.T("New item added."));
    }

    private void DeleteItem()
    {
        if (_sel == null) return;
        if (Msg.Show(L.F("Delete item \"{0}\" (ID {1}) from the shop?", Txt(_sel, "name"), Num(_sel, "id")), L.T("Delete"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        int i = _shown.IndexOf(_sel);
        _items.Rows.Remove(_sel);
        _sel = i >= 0 && i + 1 < _shown.Count ? _shown[i + 1] : i > 0 ? _shown[i - 1] : null;
        Changed?.Invoke();
        FillTree();
        FillList();
    }

    private void MoveItem(int d)
    {
        if (_sel == null) return;
        int i = _items.Rows.IndexOf(_sel), j = i + d;
        if (j < 0 || j >= _items.Rows.Count) return;
        (_items.Rows[i], _items.Rows[j]) = (_items.Rows[j], _items.Rows[i]);
        Changed?.Invoke();
        FillList();
    }

    // ───────────────────────── form ─────────────────────────

    private void BuildForm()
    {
        _form.SuspendLayout();
        foreach (Control c in _form.Controls.Cast<Control>().ToList()) { _form.Controls.Remove(c); c.Dispose(); }
        if (_sel == null)
        {
            _form.Controls.Add(new Label { Text = L.T("Select an item in the list."), Dock = DockStyle.Top, Height = 40, ForeColor = Color.DimGray });
            _form.ResumeLayout();
            return;
        }
        var r = _sel;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var title = new Label
        {
            Text = L.F("Item {0}   {1}", _items.Rows.IndexOf(r) + 1, Txt(r, "name")), Dock = DockStyle.Top, Height = 34,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold), UseMnemonic = false, AutoSize = false, AutoEllipsis = true,
        };
        _groups.Clear();
        _groups.Add([title]);
        Section? priceSec = null, catSec = null, saleSec = null, descSec = null, giftSec = null;

        // Item
        var item = new Section(L.T("Item"));
        Field(item, r, "id", L.T("Sells"), used, lookup: true);
        Field(item, r, "num", L.T("Count"), used);
        Field(item, r, "place", L.T("Position"), used);
        Field(item, r, "name", L.T("Name"), used, wide: true);
        IconField(item, r, "icon", L.T("Icon"), used);
        Field(item, r, "local_id", L.T("Local ID"), used);

        // Price
        if (Col("price") >= 0 || Col("price_item") >= 0)
        {
            var price = new Section(L.T("Price"), Col("price") >= 0 ? L.T("In hundredths: 100 = 1.00") : null);
            Field(price, r, "price", L.T("Price"), used, hint: v => long.TryParse(v, out var p) ? Price(p) : "");
            Field(price, r, "price_item", L.T("Paid with item"), used, lookup: true);
            Field(price, r, "price_item_count", L.T("Item count"), used);
            Field(price, r, "time", L.T("Lasts"), used, hint: v => long.TryParse(v, out var s) ? Duration(s) : "");
            Field(price, r, "discount", L.T("Discount"), used, hint: _ => "%");
            Field(price, r, "bonus", L.T("Bonus"), used, hint: _ => "%");
            if (Col("props") >= 0)
            {
                Bits(price, r, "props", L.T("Flags"), [(0, L.T("New")), (1, L.T("Recommended")), (2, L.T("Promotion"))], used);
                if (Col("props_b2") >= 0) Bits(price, r, "props_b2", L.T("Sale schemes"), Enumerable.Range(0, 8).Select(b => (b, (b + 1).ToString())).ToArray(), used);
                if (Col("props_b3") >= 0) Bits(price, r, "props_b3", L.T("Discount schemes"), Enumerable.Range(0, 8).Select(b => (b, (b + 1).ToString())).ToArray(), used);
            }
            priceSec = price;
        }

        // Category
        if (Col("main_type") >= 0)
        {
            var cat = new Section(L.T("Category"));
            CategoryFields(cat, r, used);
            Field(cat, r, "search_keys", L.T("Keywords"), used, wide: true);
            catSec = cat;
        }

        // On sale
        if (Col("valid_type") >= 0)
        {
            var sale = new Section(L.T("On sale"), L.T("When the server sells it"));
            SaleFields(sale, r, "valid", used);
            if (Col("valid2_type") >= 0) { sale.Add(new Label { Text = L.T("Second sale window (copy)"), ForeColor = Color.DimGray, AutoSize = true, Margin = new Padding(0, 8, 0, 2) }, full: true); SaleFields(sale, r, "valid2", used); }
            saleSec = sale;
        }

        // Description
        if (Col("description") >= 0)
        {
            var desc = new Section(L.T("Description"), L.T("^RRGGBB colours a run; line breaks are kept"));
            DescriptionField(desc, r, "description");
            used.Add("description");
            descSec = desc;
        }

        // Free gift
        if (Col("has_present") >= 0)
        {
            var gift = new Section(L.T("Free gift"), L.T("Given with every purchase"));
            Check(gift, r, "has_present", L.T("Gives a gift"), used);
            Field(gift, r, "present_id", L.T("Gift item"), used, lookup: true);
            Field(gift, r, "present_name", L.T("Gift name"), used, wide: true);
            Field(gift, r, "present_count", L.T("Gift count"), used);
            Field(gift, r, "present_time", L.T("Gift lasts"), used, hint: v => long.TryParse(v, out var s) ? Duration(s) : "");
            IconField(gift, r, "present_icon", L.T("Gift icon"), used);
            Check(gift, r, "present_bind", L.T("Bound"), used);
            Field(gift, r, "present_description", L.T("Gift description"), used, wide: true);
            giftSec = gift;
        }

        // Other fields
        var other = new Section(L.T("Other fields"), L.F("Fields of the layout {0} ({1:N0} bytes) without a meaning; edited by their type", _f.ItemLayout.Title, _f.ItemLayout.Size));
        foreach (var c in _items.Columns)
            if (!used.Contains(c.Name) && c.Type != ColType.Table) Field(other, r, c.Name, c.Name, used, typeHint: true);

        // JD IDE arrangement: Item | Price, Category | On sale side by side when the window is wide
        _groups.Add(priceSec != null ? [item, priceSec] : [item]);
        var pair = new Control?[] { catSec, saleSec }.Where(x => x != null).Cast<Control>().ToArray();
        if (pair.Length > 0) _groups.Add(pair);
        if (descSec != null) _groups.Add([descSec]);
        if (giftSec != null) _groups.Add([giftSec]);
        if (other.Count > 0) _groups.Add([other]);
        foreach (var g in _groups) foreach (var c in g) _form.Controls.Add(c);
        _form.ResumeLayout(true);
        Theme.Apply(_form);
        Relayout();
    }

    private readonly List<Control[]> _groups = new();

    /// <summary>Places the form cards: two columns when there is room, one column otherwise; long boxes follow the width.</summary>
    private void Relayout()
    {
        if (_groups.Count == 0) return;
        int W = _form.ClientSize.Width - _form.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        if (W < 320) return;
        const int gap = 10;
        bool two = W >= 1080;
        int colW = two ? (W - gap) / 2 : W;
        int x0 = _form.AutoScrollPosition.X + _form.Padding.Left, y = _form.AutoScrollPosition.Y + _form.Padding.Top;
        _form.SuspendLayout();
        foreach (var g in _groups)
        {
            if (two && g.Length == 2)
            {
                int h = 0;
                for (int i = 0; i < 2; i++) { Fit(g[i], colW); g[i].Location = new Point(x0 + i * (colW + gap), y); h = Math.Max(h, g[i].Height); }
                foreach (var c in g) c.Height = h; // equal card heights, like JD IDE
                y += h + gap;
            }
            else
                foreach (var c in g) { Fit(c, W); c.Location = new Point(x0, y); y += c.Height + gap; }
        }
        _form.ResumeLayout(true);
    }

    private static void Fit(Control c, int width)
    {
        if (c is Section s) s.Fit(width);
        else c.Width = width;
    }

    private static string Duration(long s) =>
        s <= 0 ? L.T("forever")
        : s % 86400 == 0 ? L.F("{0} days", s / 86400)
        : s % 3600 == 0 ? L.F("{0} hours", s / 3600)
        : L.F("{0} seconds", s);

    /// <summary>A labelled input bound to a column (commit on Enter / leaving the box).</summary>
    private void Field(Section s, Record r, string name, string label, HashSet<string> used, bool wide = false, bool lookup = false, Func<string, string>? hint = null, bool typeHint = false)
    {
        int c = Col(name);
        if (c < 0 || !used.Add(name)) return;
        var col = _items.Columns[c];
        var tb = new TextBox { Text = col.Type == ColType.Bytes ? Convert.ToHexString(r.Values[c] as byte[] ?? []) : RecordIO.Format(col, r.Values[c]), Width = wide ? 360 : 130, ReadOnly = col.ReadOnly };
        var after = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 6, 0, 0), UseMnemonic = false };
        void UpdateHint()
        {
            var parts = new List<string>();
            if (hint != null) parts.Add(hint(tb.Text));
            if (typeHint) parts.Add(col.TypeLabel);
            if (lookup && long.TryParse(tb.Text, out var id) && IdNames.Lookup?.Invoke(id) is { } nm) parts.Add(nm);
            if (col.IsText && col.Length > 0) parts.Add($"{RecordIO.TextLength(col, tb.Text)}/{col.MaxChars}");
            after.Text = string.Join("   ", parts.Where(p => p.Length > 0));
        }
        UpdateHint();
        void Commit()
        {
            if (Set(r, name, tb.Text)) { UpdateHint(); if (name is "name" or "id" or "price" or "icon") _list.Invalidate(); }
            else tb.Text = RecordIO.Format(col, r.Values[c]);
        }
        tb.Leave += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); } };
        tb.TextChanged += (_, _) => UpdateHint();
        var tip = new ToolTip();
        if (col.Note != null) tip.SetToolTip(tb, col.Note);
        s.AddRow(label, col.Note, tb, after);
        if (wide) s.Stretch.Add((tb, 230));
    }

    private void Check(Section s, Record r, string name, string label, HashSet<string> used)
    {
        int c = Col(name);
        if (c < 0 || !used.Add(name)) return;
        var cb = new CheckBox { Text = label, AutoSize = true, Checked = r.Values[c] is true };
        cb.CheckedChanged += (_, _) => { if (!_building) Set(r, name, cb.Checked ? "1" : "0"); };
        s.AddRow("", _items.Columns[c].Note, cb);
    }

    /// <summary>Checkboxes for the bits of a byte (flags / scheme masks).</summary>
    private void Bits(Section s, Record r, string name, string label, (int bit, string text)[] bits, HashSet<string> used)
    {
        int c = Col(name);
        if (c < 0 || !used.Add(name)) return;
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        long v = r.Long(c);
        foreach (var (bit, text) in bits)
        {
            var cb = new CheckBox { Text = text, AutoSize = true, Checked = (v & (1L << bit)) != 0, Margin = new Padding(0, 3, 10, 0) };
            int b = bit;
            cb.CheckedChanged += (_, _) =>
            {
                long cur = r.Long(c);
                long nv = cb.Checked ? cur | (1L << b) : cur & ~(1L << b);
                Set(r, name, nv.ToString(CultureInfo.InvariantCulture));
            };
            flow.Controls.Add(cb);
        }
        s.AddRow(label, _items.Columns[c].Note, flow);
    }

    private void IconField(Section s, Record r, string name, string label, HashSet<string> used)
    {
        int c = Col(name);
        if (c < 0 || !used.Add(name)) return;
        var col = _items.Columns[c];
        var pic = new PictureBox { Size = new Size(36, 36), SizeMode = PictureBoxSizeMode.CenterImage, BorderStyle = BorderStyle.FixedSingle, Image = GameIcons.ForPath(r.Text(c), 32), Margin = new Padding(0, 0, 6, 0) };
        var tb = new TextBox { Text = r.Text(c), Width = 330 };
        var choose = new Button { Text = L.T("Choose…"), AutoSize = true, Height = 28 };
        void Commit()
        {
            if (Set(r, name, tb.Text)) { pic.Image = GameIcons.ForPath(tb.Text, 32); _list.Invalidate(); }
            else tb.Text = r.Text(c);
        }
        tb.Leave += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); } };
        choose.Click += (_, _) =>
        {
            using var dlg = new IconPickerDialog(tb.Text);
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK && dlg.Picked != null) { tb.Text = dlg.Picked; Commit(); }
        };
        s.AddRow(label, col.Note, pic, tb, choose);
        s.Stretch.Add((tb, 300));
    }

    private void CategoryFields(Section s, Record r, HashSet<string> used)
    {
        int cm = Col("main_type"), cs = Col("sub_type");
        used.Add("main_type"); used.Add("sub_type");
        var main = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        var sub = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        for (int m = 0; m < _cats.Rows.Count; m++) main.Items.Add($"{m} · {MainName(m)}");
        void FillSubs(int m)
        {
            sub.Items.Clear();
            if (m >= 0 && m < _cats.Rows.Count)
                for (int i = 0; i < _cats.Rows[m].Table(2).Count; i++) sub.Items.Add($"{i} · {SubName(m, i)}");
        }
        int mv = (int)Num(r, "main_type"), sv = (int)Num(r, "sub_type");
        _building = true;
        if (mv >= 0 && mv < main.Items.Count) main.SelectedIndex = mv;
        FillSubs(mv);
        if (cs >= 0 && sv >= 0 && sv < sub.Items.Count) sub.SelectedIndex = sv;
        _building = false;
        main.SelectedIndexChanged += (_, _) =>
        {
            if (_building || main.SelectedIndex < 0) return;
            Set(r, "main_type", main.SelectedIndex.ToString());
            FillSubs(main.SelectedIndex);
            if (cs >= 0 && sub.Items.Count > 0) { sub.SelectedIndex = 0; }
            FillTree();
        };
        sub.SelectedIndexChanged += (_, _) => { if (!_building && sub.SelectedIndex >= 0) { Set(r, "sub_type", sub.SelectedIndex.ToString()); FillTree(); } };
        s.AddRow(L.T("Main"), _items.Columns[cm].Note, main);
        if (cs >= 0) s.AddRow(L.T("Sub"), _items.Columns[cs].Note, sub);
    }

    private void SaleFields(Section s, Record r, string prefix, HashSet<string> used)
    {
        string tn = prefix + "_type", sn = prefix + "_start", en = prefix + "_end", pn = prefix + "_param";
        if (Col(tn) < 0) return;
        foreach (var n in new[] { tn, sn, en, pn }) used.Add(n);
        var when = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
        when.Items.AddRange([L.T("Always"), L.T("Date range (start → end)"), L.T("Repeating (start + duration)")]);
        long type = Num(r, tn), start = Num(r, sn), end = Num(r, en);
        _building = true;
        when.SelectedIndex = type == 1 ? 2 : start == 0 && end == 0 ? 0 : 1;
        _building = false;
        s.AddRow(L.T("When"), _items.Columns[Col(tn)].Note, when);
        var startTb = new TextBox { Width = 130, Text = start.ToString() };
        var endTb = new TextBox { Width = 130, Text = end.ToString() };
        var paramTb = new TextBox { Width = 80, Text = Num(r, pn).ToString() };
        var startHint = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 6, 0, 0) };
        var endHint = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 6, 0, 0) };
        void Hints()
        {
            long.TryParse(startTb.Text, out var a); long.TryParse(endTb.Text, out var b);
            startHint.Text = a > 100000 ? DateTimeOffset.FromUnixTimeSeconds(a).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
            endHint.Text = when.SelectedIndex == 2 ? Duration(b) : b > 100000 ? DateTimeOffset.FromUnixTimeSeconds(b).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
        }
        Hints();
        void Bind(TextBox tb, string col)
        {
            void Commit() { if (!Set(r, col, tb.Text)) tb.Text = Num(r, col).ToString(); Hints(); }
            tb.Leave += (_, _) => Commit();
            tb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); } };
        }
        Bind(startTb, sn); Bind(endTb, en); Bind(paramTb, pn);
        var rowStart = s.AddRow(L.T("Start"), L.T("Unix time (seconds since 1970)"), startTb, startHint);
        var rowEnd = s.AddRow(L.T("End / duration"), L.T("Unix time, or seconds for a repeating sale"), endTb, endHint);
        var rowParam = s.AddRow(L.T("Parameter"), null, paramTb);
        void Vis() { bool on = when.SelectedIndex > 0; rowStart.Visible = rowEnd.Visible = rowParam.Visible = on; }
        Vis();
        when.SelectedIndexChanged += (_, _) =>
        {
            if (_building) return;
            switch (when.SelectedIndex)
            {
                case 0: Set(r, tn, "0"); Set(r, sn, "0"); Set(r, en, "0"); Set(r, pn, "0"); startTb.Text = endTb.Text = paramTb.Text = "0"; break;
                case 1: Set(r, tn, "0"); break;
                case 2: Set(r, tn, "1"); break;
            }
            Vis(); Hints();
        };
    }

    private void DescriptionField(Section s, Record r, string name)
    {
        int c = Col(name);
        var col = _items.Columns[c];
        var text = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Width = 600, Height = 96, Font = new Font("Segoe UI", 10F), Text = r.Text(c).Replace("\r\n", "\n").Replace("\n", "\r\n") };
        var preview = new GameTextPreview { Width = 600, Height = 96, Font = new Font("Segoe UI", 10F) };
        var counter = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 0) };
        bool crlf = r.Text(c).Contains("\r\n");
        string Norm() => crlf ? text.Text : text.Text.Replace("\r\n", "\n");
        void Refresh2()
        {
            preview.GameText = Norm();
            counter.Text = $"{RecordIO.TextLength(col, Norm())}/{col.MaxChars}";
            counter.ForeColor = RecordIO.TextLength(col, Norm()) > col.MaxChars ? Color.Firebrick : Color.DimGray;
        }
        Refresh2();
        text.TextChanged += (_, _) => Refresh2();
        void Commit() { if (!Set(r, name, Norm())) { text.Text = r.Text(c).Replace("\r\n", "\n").Replace("\n", "\r\n"); } }
        text.Leave += (_, _) => Commit();
        var bar = new ColorCodeBar(text);
        var prevLabel = new Label { Text = L.T("Preview (in game)"), AutoSize = true, ForeColor = Color.DimGray };
        var host = new DescriptionHost(text, bar, counter, prevLabel, preview);
        s.AddRow(L.T("Text"), col.Note, host);
        s.Stretch.Add((host, 150));
    }

    // ───────────────────────── mode / IModuleView ─────────────────────────

    private void SetMode(bool table)
    {
        if (table && _table == null)
        {
            _table = new TableFileView(_f) { Dock = DockStyle.Fill, Visible = false };
            _table.Changed += () => Changed?.Invoke();
            _table.Status += s => Status?.Invoke(s);
            Controls.Add(_table);
            _table.BringToFront();
        }
        if (!table && _table?.Visible == true) { _table.Commit(); RefreshAll(); }
        if (table) _table!.RefreshAll();
        _shopMode.Visible = !table;
        if (_table != null) _table.Visible = table;
        _btnShop.ForeColor = !table ? Theme.AccentText : Theme.Text;
        _btnTable.ForeColor = table ? Theme.AccentText : Theme.Text;
    }

    public bool Commit()
    {
        // leaving the focused box commits it
        if (_form.ContainsFocus) _list.Focus();
        return _table?.Commit() ?? true;
    }

    public void FocusSearch()
    {
        if (_table?.Visible == true) _table.FocusSearch();
        else { _search.Focus(); _search.SelectAll(); }
    }

    public void RefreshAll()
    {
        if (_sel != null && !_items.Rows.Contains(_sel)) _sel = null;
        FillTree();
        FillList();
        if (_table?.Visible == true) _table.RefreshAll();
    }

    public void Locate(ChangeItem c)
    {
        if (c.Group == "Items" && c.Target is Record r && _items.Rows.Contains(r) && _table?.Visible != true)
        {
            _sel = r;
            _filter = null;
            _search.Text = "";
            FillTree();
            FillList();
            return;
        }
        SetMode(true);
        _table!.Locate(c);
    }

    public RecordTable? CurrentTable => _table?.Visible == true ? _table.CurrentTable : _items;

    /// <summary>Titled group box of the form (JD IDE card): rows of label + controls.</summary>
    private sealed class Section : Panel
    {
        private readonly FlowLayoutPanel _rows;
        private readonly Label _head;
        public int Count { get; private set; }
        /// <summary>Controls that follow the card width: (control, width reserved for label / extras).</summary>
        public List<(Control c, int reserve)> Stretch { get; } = new();

        /// <summary>Sets the card width, stretches the long boxes and takes the height its rows need.</summary>
        public void Fit(int width)
        {
            Width = width;
            foreach (var (c, reserve) in Stretch)
            {
                int w = Math.Max(140, width - reserve);
                if (c is DescriptionHost d) d.Fit(w);
                else (c.Parent is RoundBox rb ? rb : c).Width = w; // the theme may host a TextBox in a rounded box
            }
            _rows.PerformLayout();
            Height = _head.Height + _rows.PreferredSize.Height + Padding.Vertical + 4;
        }

        public Section(string title, string? subtitle = null)
        {
            AutoSize = false;
            Padding = new Padding(10, 6, 10, 8);
            BorderStyle = BorderStyle.FixedSingle;
            var head = _head = new Label
            {
                Text = title + (subtitle != null ? "    " + subtitle : ""), Dock = DockStyle.Top, Height = 26,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), UseMnemonic = false,
            };
            _rows = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            Controls.Add(_rows);
            Controls.Add(head);
        }

        public FlowLayoutPanel AddRow(string label, string? tip, params Control[] controls)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            var l = new Label { Text = label, Width = 120, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 8, 0), Height = 22, UseMnemonic = false };
            if (tip != null) new ToolTip().SetToolTip(l, tip);
            row.Controls.Add(l);
            row.Controls.AddRange(controls);
            _rows.Controls.Add(row);
            Count++;
            return row;
        }

        public void Add(Control c, bool full) { _rows.Controls.Add(c); }
    }
}

/// <summary>
/// Description block: text box + colour bar + counter and the in-game preview – side by side when there
/// is room (as JD IDE), stacked otherwise.
/// </summary>
internal sealed class DescriptionHost : Panel
{
    private readonly TextBox _text;
    private readonly Control _bar, _counter, _label, _preview;

    public DescriptionHost(TextBox text, Control bar, Control counter, Control label, Control preview)
    {
        _text = text; _bar = bar; _counter = counter; _label = label; _preview = preview;
        Margin = Padding.Empty;
        Controls.AddRange([text, bar, counter, label, preview]);
        Fit(600);
    }

    private Control Host(Control c) => c.Parent is RoundBox rb ? rb : c;

    public void Fit(int width)
    {
        Width = width;
        var text = Host(_text);
        if (width >= 760)
        {
            int half = (width - 12) / 2;
            _label.Location = new Point(half + 12, 0);
            text.SetBounds(0, 20, half, 120);
            _preview.SetBounds(half + 12, 20, width - half - 12, 120);
            _bar.MaximumSize = new Size(half, 0);
            _bar.Location = new Point(0, text.Bottom + 6);
            _counter.Location = new Point(0, _bar.Bottom + 2);
            Height = Math.Max(_counter.Bottom, _preview.Bottom) + 4;
        }
        else
        {
            text.SetBounds(0, 0, width, 100);
            _bar.MaximumSize = new Size(width, 0);
            _bar.Location = new Point(0, text.Bottom + 6);
            _counter.Location = new Point(0, _bar.Bottom + 2);
            _label.Location = new Point(0, _counter.Bottom + 6);
            _preview.SetBounds(0, _label.Bottom + 2, width, 100);
            Height = _preview.Bottom + 4;
        }
    }
}

/// <summary>Pick a picture from surfaces.pck (shop icons under surfaces\qshop by default), with previews.</summary>
public sealed class IconPickerDialog : ThemedForm
{
    public string? Picked { get; private set; }
    private readonly TextBox _filter;
    private readonly ListView _list;
    private readonly ImageList _images = new() { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };
    private readonly List<string> _all;

    public IconPickerDialog(string current)
    {
        Text = L.T("Choose an icon");
        Size = new Size(760, 560);
        StartPosition = FormStartPosition.CenterParent;
        _filter = new TextBox { Dock = DockStyle.Top, PlaceholderText = L.T("Filter by path (e.g. qshop, 男物品)…") };
        _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, SmallImageList = _images, MultiSelect = false };
        _list.Columns.Add(L.T("Path"), 680);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var ok = new Button { Text = L.T("OK"), Width = 90, Height = 30 };
        var cancel = new Button { Text = L.T("Cancel"), Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        bar.Controls.AddRange([cancel, ok]);
        Controls.Add(_list);
        Controls.Add(_filter);
        Controls.Add(bar);
        CancelButton = cancel;
        AcceptButton = ok;
        _all = GameIcons.PicturePaths().ToList();
        var dir = Path.GetDirectoryName(current.Replace('/', '\\')) ?? "";
        _filter.Text = dir.Length > 0 ? dir : @"qshop";
        _filter.TextChanged += (_, _) => Fill();
        ok.Click += (_, _) => { if (_list.SelectedItems.Count > 0) { Picked = (string)_list.SelectedItems[0].Tag!; DialogResult = DialogResult.OK; } };
        _list.ItemActivate += (_, _) => ok.PerformClick();
        Fill();
    }

    private void Fill()
    {
        var q = _filter.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        _images.Images.Clear();
        foreach (var p in _all.Where(p => q.Length == 0 || p.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(400))
        {
            var img = GameIcons.ForPath(p, 32);
            int idx = -1;
            if (img != null) { _images.Images.Add(img); idx = _images.Images.Count - 1; }
            _list.Items.Add(new ListViewItem(p, idx) { Tag = p });
        }
        _list.EndUpdate();
    }
}
