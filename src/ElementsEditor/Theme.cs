using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ElementsEditor;

/// <summary>
/// Dark theme in the style of JD IDE. Applied automatically to every <see cref="ThemedForm"/>
/// (walks all child controls, including ones added later). Light mode = do nothing.
/// </summary>
public static class Theme
{
    public static bool Dark { get; set; } = true;

    // ── palette (JD IDE-like) ──
    public static readonly Color Bg = Color.FromArgb(14, 18, 20);          // window
    public static readonly Color Panel = Color.FromArgb(19, 24, 27);       // side panels, menus
    public static readonly Color Surface = Color.FromArgb(17, 21, 24);     // lists / grids
    public static readonly Color SurfaceAlt = Color.FromArgb(21, 26, 29);  // alternating rows
    public static readonly Color Input = Color.FromArgb(24, 30, 34);       // text boxes
    public static readonly Color Header = Color.FromArgb(16, 20, 23);      // column headers
    public static readonly Color Border = Color.FromArgb(42, 50, 55);
    public static readonly Color Hover = Color.FromArgb(32, 40, 45);
    public static readonly Color Text = Color.FromArgb(225, 230, 235);
    public static readonly Color TextDim = Color.FromArgb(132, 142, 150);
    public static readonly Color TextFaint = Color.FromArgb(80, 88, 95);
    public static readonly Color Accent = Color.FromArgb(63, 185, 80);     // green
    public static readonly Color AccentDark = Color.FromArgb(24, 58, 38);  // selection background
    public static readonly Color AccentText = Color.FromArgb(126, 226, 160);
    public static readonly Color Amber = Color.FromArgb(214, 160, 60);
    public static readonly Color Red = Color.FromArgb(248, 100, 90);
    public static readonly Color Blue = Color.FromArgb(100, 170, 255);
    public static readonly Color Purple = Color.FromArgb(190, 150, 255);
    public static readonly Color HexHighlight = Color.FromArgb(36, 92, 58);

    private static readonly ConditionalWeakTable<Control, object> Done = new();

    /// <summary>Maps a light-theme foreground colour to its dark-theme counterpart.</summary>
    public static Color Fore(Color c)
    {
        if (!Dark) return c;
        if (c.IsEmpty || c.IsSystemColor || c.ToArgb() == Color.Black.ToArgb()) return Text;
        int a = c.ToArgb();
        bool Is(params Color[] cs) => cs.Any(x => x.ToArgb() == a);
        if (Is(Color.DimGray, Color.Gray, Color.DarkGray)) return TextDim;
        if (Is(Color.Silver, Color.LightGray)) return TextFaint;
        if (Is(Color.DarkGreen, Color.Green, Color.SeaGreen, Color.ForestGreen)) return AccentText;
        if (Is(Color.Firebrick, Color.Red, Color.DarkRed, Color.Crimson)) return Red;
        if (Is(Color.DarkBlue, Color.Blue, Color.Navy, Color.SteelBlue, Color.MediumBlue)) return Blue;
        if (Is(Color.DarkGoldenrod, Color.SaddleBrown, Color.Chocolate, Color.DarkOrange)) return Amber;
        if (Is(Color.MediumPurple, Color.Purple)) return Purple;
        return c;
    }

    /// <summary>Maps a light background/highlight colour (e.g. hex selection) to the dark palette.</summary>
    public static Color Back(Color c)
    {
        if (!Dark) return c;
        int a = c.ToArgb();
        if (a == Color.LightGoldenrodYellow.ToArgb() || a == Color.LightYellow.ToArgb()) return HexHighlight;
        if (c.IsSystemColor || c.GetBrightness() > 0.85f) return Input;
        return c;
    }

    public static void Apply(Control root)
    {
        if (!Dark) return;
        Style(root);
    }

    private static void Style(Control c)
    {
        if (!Done.TryGetValue(c, out _))
        {
            Done.Add(c, new object());
            StyleOne(c);
            c.ControlAdded += (_, e) => { if (e.Control != null) Style(e.Control); };
            if (c is Label or CheckBox or RadioButton or GroupBox)
                c.ForeColorChanged += (_, _) => { var m = Fore(c.ForeColor); if (m != c.ForeColor) c.ForeColor = m; };
        }
        // snapshot: styling may wrap a child (rounded text box host) and change the collection
        foreach (Control child in c.Controls.Cast<Control>().ToArray()) Style(child);
    }

    public static readonly Color HoverRow = Color.FromArgb(25, 32, 36);
    public static readonly Color CurrentRow = Color.FromArgb(19, 36, 28);
    public static readonly Color BorderHover = Color.FromArgb(70, 82, 90);

    public static System.Drawing.Drawing2D.GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static bool CanRound(TextBox tb) =>
        tb is not DataGridViewTextBoxEditingControl &&
        tb.Parent is not null and not DataGridView and not ToolStrip and not UpDownBase and not RoundBox and not ComboBox;

    /// <summary>Moves a TextBox into a rounded, padded host that glows green while focused.</summary>
    private static void WrapRounded(TextBox tb)
    {
        var parent = tb.Parent!;
        bool multi = tb.Multiline;
        int idx = parent.Controls.GetChildIndex(tb);
        var bounds = tb.Bounds;
        if (!multi && tb.Dock is DockStyle.None or DockStyle.Top or DockStyle.Bottom) bounds.Height = Math.Max(bounds.Height + 5, 28);
        var host = new RoundBox(tb, multi)
        {
            Dock = tb.Dock, Anchor = tb.Anchor, Margin = tb.Margin, Bounds = bounds,
            MinimumSize = tb.MinimumSize, TabIndex = tb.TabIndex,
        };
        tb.BorderStyle = BorderStyle.None;
        tb.Dock = DockStyle.None;
        tb.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        parent.SuspendLayout();
        parent.Controls.Add(host);
        parent.Controls.SetChildIndex(host, idx);
        host.Controls.Add(tb);
        parent.ResumeLayout(true);
    }

    /// <summary>Owner-painted rounded button: green when primary (AcceptButton), glow border on hover.</summary>
    private static void RoundButton(Button b)
    {
        bool hover = false, down = false;
        b.MouseEnter += (_, _) => { hover = true; b.Invalidate(); };
        b.MouseLeave += (_, _) => { hover = false; down = false; b.Invalidate(); };
        b.MouseDown += (_, _) => { down = true; b.Invalidate(); };
        b.MouseUp += (_, _) => { down = false; b.Invalidate(); };
        b.GotFocus += (_, _) => b.Invalidate();
        b.LostFocus += (_, _) => b.Invalidate();
        b.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(b.Parent?.BackColor ?? Bg);
            bool primary = b.FindForm()?.AcceptButton == b;
            var r = new RectangleF(0.5f, 0.5f, b.Width - 1.5f, b.Height - 1.5f);
            Color fill = !b.Enabled ? Panel
                : down ? (primary ? Color.FromArgb(30, 110, 55) : AccentDark)
                : primary ? (hover ? Color.FromArgb(46, 160, 75) : Color.FromArgb(35, 134, 62))
                : hover ? Hover : Input;
            Color border = !b.Enabled ? Color.FromArgb(32, 38, 42)
                : primary ? Color.FromArgb(70, 200, 100)
                : hover || b.Focused ? Color.FromArgb(60, 140, 85) : Border;
            using (var path = RoundRect(r, 6))
            {
                using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                using (var pen = new Pen(border)) g.DrawPath(pen, path);
            }
            var fg = !b.Enabled ? TextFaint : primary ? Color.White : Text;
            TextRenderer.DrawText(g, b.Text, primary ? new Font(b.Font, FontStyle.Bold) : b.Font, Rectangle.Round(r), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.HidePrefix | TextFormatFlags.EndEllipsis);
        };
    }

    private static void StyleOne(Control c)
    {
        switch (c)
        {
            case Form f:
                f.BackColor = Bg;
                f.ForeColor = Text;
                break;
            case MenuStrip or ToolStrip or StatusStrip:
                var ts = (ToolStrip)c;
                ts.Renderer = new DarkRenderer();
                ts.BackColor = c is StatusStrip ? Header : Panel;
                ts.ForeColor = Text;
                foreach (ToolStripItem it in ts.Items) StyleItem(it);
                ts.ItemAdded += (_, e) => StyleItem(e.Item!);
                break;
            case DataGridView g:
                StyleGrid(g);
                break;
            case ListView lv:
                StyleListView(lv);
                break;
            case TreeView tv:
                StyleTree(tv);
                break;
            case RichTextBox rtb:
                rtb.BackColor = Surface;
                rtb.ForeColor = Text;
                rtb.BorderStyle = BorderStyle.None;
                DarkScrollbars(rtb);
                break;
            case TextBox tb:
                tb.BackColor = Input;
                tb.ForeColor = Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                if (tb.Multiline) DarkScrollbars(tb);
                if (CanRound(tb)) WrapRounded(tb);
                break;
            case ComboBox cb:
                cb.BackColor = Input;
                cb.ForeColor = Text;
                cb.FlatStyle = FlatStyle.Flat;
                break;
            case ListBox lb:
                lb.BackColor = Input;
                lb.ForeColor = Text;
                lb.BorderStyle = BorderStyle.FixedSingle;
                DarkScrollbars(lb);
                break;
            case NumericUpDown nud:
                nud.BackColor = Input;
                nud.ForeColor = Text;
                nud.BorderStyle = BorderStyle.FixedSingle;
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = Input;
                b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = Hover;
                b.FlatAppearance.MouseDownBackColor = AccentDark;
                b.EnabledChanged += (_, _) => { b.ForeColor = b.Enabled ? Text : TextFaint; b.Invalidate(); };
                if (!b.Enabled) b.ForeColor = TextFaint;
                RoundButton(b);
                break;
            case CheckBox or RadioButton:
                c.ForeColor = Fore(c.ForeColor);
                var bb = (ButtonBase)c;
                bb.FlatStyle = FlatStyle.Flat;
                bb.FlatAppearance.BorderColor = BorderHover;
                bb.FlatAppearance.CheckedBackColor = AccentDark;
                bb.FlatAppearance.MouseOverBackColor = Hover;
                break;
            case LinkLabel ll:
                ll.LinkColor = Blue;
                ll.ActiveLinkColor = AccentText;
                ll.ForeColor = Fore(ll.ForeColor);
                break;
            case Label l:
                l.ForeColor = Fore(l.ForeColor);
                if (!l.BackColor.IsSystemColor && l.BackColor != Color.Transparent) l.BackColor = Back(l.BackColor);
                break;
            case Splitter s:
                s.BackColor = Border;
                break;
            case TabPage tp:
                tp.UseVisualStyleBackColor = false;
                tp.BackColor = Panel;
                tp.ForeColor = Text;
                break;
            case TabControl tc:
                tc.BackColor = Bg;
                tc.ForeColor = Text;
                break;
            case SplitContainer sc:
                sc.BackColor = Border;
                sc.Panel1.BackColor = Bg;
                sc.Panel2.BackColor = Bg;
                break;
            case GroupBox gb:
                gb.ForeColor = TextDim;
                break;
            case ProgressBar:
                break;
            default:
                if (c.BackColor.IsSystemColor) c.BackColor = Bg;
                c.ForeColor = Fore(c.ForeColor);
                break;
        }
    }

    private static void StyleItem(ToolStripItem it)
    {
        it.ForeColor = Text;
        if (it is ToolStripTextBox tb) { tb.BackColor = Input; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.FixedSingle; }
        if (it is ToolStripComboBox cb) { cb.BackColor = Input; cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Flat; }
        if (it is ToolStripDropDownItem dd)
        {
            dd.DropDown.Renderer = new DarkRenderer();
            dd.DropDown.BackColor = Panel;
            foreach (ToolStripItem sub in dd.DropDownItems) StyleItem(sub);
            dd.DropDown.ItemAdded += (_, e) => StyleItem(e.Item!);
        }
    }

    // ───────── DataGridView ─────────
    private static void StyleGrid(DataGridView g)
    {
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = Surface;
        g.GridColor = Color.FromArgb(30, 36, 40);
        g.BorderStyle = BorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;

        var h = g.ColumnHeadersDefaultCellStyle;
        h.BackColor = Header;
        h.ForeColor = TextDim;
        h.SelectionBackColor = Header;
        h.SelectionForeColor = TextDim;
        h.Font = new Font(g.Font.FontFamily, Math.Max(7.5f, g.Font.Size - 0.5f), FontStyle.Bold);
        h.Padding = new Padding(4, 0, 0, 0);
        h.WrapMode = DataGridViewTriState.False;
        if (g.ColumnHeadersHeightSizeMode != DataGridViewColumnHeadersHeightSizeMode.AutoSize)
            g.ColumnHeadersHeight = Math.Max(g.ColumnHeadersHeight, 26);
        g.RowHeadersDefaultCellStyle.BackColor = Header;
        g.RowHeadersDefaultCellStyle.ForeColor = TextDim;
        g.RowHeadersDefaultCellStyle.SelectionBackColor = AccentDark;

        var d = g.DefaultCellStyle;
        d.BackColor = Surface;
        d.ForeColor = Text;
        d.SelectionBackColor = AccentDark;
        d.SelectionForeColor = Color.White;
        g.AlternatingRowsDefaultCellStyle.BackColor = SurfaceAlt;
        g.AlternatingRowsDefaultCellStyle.SelectionBackColor = AccentDark;
        g.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.White;
        g.RowTemplate.Height = Math.Max(g.RowTemplate.Height, 24);

        void StyleColumn(DataGridViewColumn col)
        {
            var s = col.DefaultCellStyle;
            if (!s.BackColor.IsEmpty) s.BackColor = Back(s.BackColor);
            if (!s.ForeColor.IsEmpty) s.ForeColor = Fore(s.ForeColor);
            s.SelectionBackColor = AccentDark;
            s.SelectionForeColor = Color.White;
            // editable value columns: brighter, bold – like JD IDE's value column
            if (!col.ReadOnly && !g.ReadOnly && col.DefaultCellStyle.Font == null)
                s.Font = new Font(g.Font, FontStyle.Bold);
        }
        // ── smoother look: taller rows, padding, small-caps headers, hover + current-row accent ──
        d.Padding = new Padding(6, 0, 6, 0);
        g.RowTemplate.Height = Math.Max(g.RowTemplate.Height, 28);
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 32;
        var headFont = new Font(g.Font.FontFamily, Math.Max(7.25f, g.Font.Size - 1.25f), FontStyle.Bold);
        g.CellPainting += (_, e) =>
        {
            if (e.RowIndex != -1 || e.ColumnIndex < 0 || e.Graphics == null) return;
            var b = e.CellBounds;
            using (var br = new SolidBrush(Header)) e.Graphics.FillRectangle(br, b);
            using (var p = new Pen(Border)) e.Graphics.DrawLine(p, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
            var text = g.Columns[e.ColumnIndex].HeaderText.ToUpper(System.Globalization.CultureInfo.GetCultureInfo("vi-VN"));
            var r = new Rectangle(b.Left + 8, b.Top, b.Width - 12, b.Height);
            TextRenderer.DrawText(e.Graphics, text, headFont, r, TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            e.Handled = true;
        };
        int hoverRow = -1, lastCurrent = -1;
        void InvalidateRowSafe(int r) { if (r >= 0 && r < g.RowCount) g.InvalidateRow(r); }
        g.CellMouseEnter += (_, e) =>
        {
            if (e.RowIndex == hoverRow) return;
            int old = hoverRow; hoverRow = e.RowIndex;
            InvalidateRowSafe(old); InvalidateRowSafe(hoverRow);
        };
        g.MouseLeave += (_, _) => { int old = hoverRow; hoverRow = -1; InvalidateRowSafe(old); };
        g.CurrentCellChanged += (_, _) =>
        {
            int cur = g.CurrentCell?.RowIndex ?? -1;
            if (cur == lastCurrent) return;
            InvalidateRowSafe(lastCurrent); lastCurrent = cur; InvalidateRowSafe(cur);
        };
        g.CellFormatting += (_, e) =>
        {
            if (e.CellStyle == null || e.RowIndex < 0) return;
            if (e.RowIndex == g.CurrentCell?.RowIndex) e.CellStyle.BackColor = CurrentRow;
            else if (e.RowIndex == hoverRow) e.CellStyle.BackColor = HoverRow;
        };
        g.RowPostPaint += (_, e) =>
        {
            if (e.RowIndex != g.CurrentCell?.RowIndex) return;
            int x = g.RowHeadersVisible ? g.RowHeadersWidth : 0;
            using var br = new SolidBrush(Accent);
            e.Graphics.FillRectangle(br, x, e.RowBounds.Top, 3, e.RowBounds.Height - 1);
        };

        foreach (DataGridViewColumn col in g.Columns) StyleColumn(col);
        g.ColumnAdded += (_, e) => StyleColumn(e.Column);
        g.EditingControlShowing += (_, e) =>
        {
            e.Control.BackColor = Input;
            e.Control.ForeColor = Text;
        };
        // per-cell colours set by code (e.g. red for too-long text) get mapped too
        g.CellFormatting += (_, e) =>
        {
            if (e.CellStyle == null) return;
            if (!e.CellStyle.ForeColor.IsEmpty && !e.CellStyle.ForeColor.IsSystemColor) e.CellStyle.ForeColor = Fore(e.CellStyle.ForeColor);
        };
        DarkScrollbars(g);
        foreach (Control sb in g.Controls) DarkScrollbars(sb);
    }

    // ───────── ListView (owner drawn, header included) ─────────
    private static void StyleListView(ListView lv)
    {
        lv.BackColor = Surface;
        lv.ForeColor = Text;
        lv.GridLines = false;
        lv.BorderStyle = BorderStyle.None;
        DarkScrollbars(lv);
        if (lv.View != View.Details) return;

        lv.OwnerDraw = true;
        typeof(ListView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(lv, true);
        // taller rows (row height follows the small image list) + hover highlight
        if (lv.SmallImageList == null) lv.SmallImageList = new ImageList { ImageSize = new Size(1, 26) };
        int hover = -1;
        void Inv(int i) { if (i >= 0 && i < (lv.VirtualMode ? lv.VirtualListSize : lv.Items.Count)) lv.Invalidate(lv.GetItemRect(i)); }
        lv.MouseMove += (_, e) =>
        {
            int i = lv.GetItemAt(e.X, e.Y)?.Index ?? -1;
            if (i == hover) return;
            int old = hover; hover = i; Inv(old); Inv(hover);
        };
        lv.MouseLeave += (_, _) => { int old = hover; hover = -1; Inv(old); };
        var headerFont = new Font(lv.Font.FontFamily, Math.Max(7.5f, lv.Font.Size - 0.5f), FontStyle.Bold);

        lv.DrawColumnHeader += (_, e) =>
        {
            using (var b = new SolidBrush(Header)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Border))
            {
                e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            }
            var r = Rectangle.Inflate(e.Bounds, -6, 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, headerFont, r, TextDim, Align(e.Header?.TextAlign ?? HorizontalAlignment.Left));
        };
        lv.DrawItem += (_, e) => { /* sub-items do the drawing */ };
        lv.DrawSubItem += (_, e) =>
        {
            if (e.Item == null) return;
            bool sel = e.Item.Selected;
            var back = sel ? AccentDark : e.ItemIndex == hover ? HoverRow : (e.ItemIndex % 2 == 1 ? SurfaceAlt : Surface);
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel && e.ColumnIndex == 0)
                using (var b = new SolidBrush(Accent)) e.Graphics.FillRectangle(b, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
            var sub = e.SubItem;
            var fg = Fore(e.Item.UseItemStyleForSubItems || sub == null ? e.Item.ForeColor : sub.ForeColor);
            if (sel && fg == Text) fg = Color.White;
            var r = Rectangle.Inflate(e.Bounds, -6, 0);
            if (e.ColumnIndex == 0) r.X += 2;
            var align = e.Header?.TextAlign ?? HorizontalAlignment.Left;
            TextRenderer.DrawText(e.Graphics, sub?.Text, e.Item.Font ?? lv.Font, r, fg, Align(align));
        };
        // the area right of the last column is painted natively: give the header control the dark theme
        void DarkHeader()
        {
            try
            {
                var header = SendMessage(lv.Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null);
            }
            catch { }
        }
        if (lv.IsHandleCreated) DarkHeader(); else lv.HandleCreated += (_, _) => DarkHeader();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static TextFormatFlags Align(HorizontalAlignment a) =>
        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
        a switch { HorizontalAlignment.Right => TextFormatFlags.Right, HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter, _ => TextFormatFlags.Left };

    // ───────── TreeView ─────────
    private static void StyleTree(TreeView tv)
    {
        tv.BackColor = Surface;
        tv.ForeColor = Text;
        tv.LineColor = Border;
        tv.BorderStyle = BorderStyle.None;
        tv.DrawMode = TreeViewDrawMode.OwnerDrawText;
        tv.DrawNode += (_, e) =>
        {
            if (e.Node == null) return;
            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            var r = e.Bounds;
            if (r.IsEmpty) return;
            using (var b = new SolidBrush(sel ? AccentDark : tv.BackColor)) e.Graphics.FillRectangle(b, r);
            var fg = Fore(e.Node.ForeColor);
            if (sel) fg = Color.White;
            TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont ?? tv.Font, r, fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
        };
        DarkScrollbars(tv);
    }

    // ───────── native bits: dark scrollbars + title bar ─────────
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? appName, string? idList);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void DarkScrollbars(Control c)
    {
        if (!Dark) return;
        void Set() { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
        if (c.IsHandleCreated) Set(); else c.HandleCreated += (_, _) => Set();
    }

    public static void DarkTitleBar(Form f)
    {
        if (!Dark) return;
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
        }
        catch { }
    }

    // ───────── menus / toolbars ─────────
    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Fore(e.Item.ForeColor) : TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = TextDim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
                using (var p = new Pen(Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            else
                using (var p = new Pen(Border)) e.Graphics.DrawLine(p, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public DarkColors() { UseSystemColors = false; }
        public override Color MenuStripGradientBegin => Panel;
        public override Color MenuStripGradientEnd => Panel;
        public override Color ToolStripGradientBegin => Panel;
        public override Color ToolStripGradientMiddle => Panel;
        public override Color ToolStripGradientEnd => Panel;
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ToolStripBorder => Border;
        public override Color ToolStripContentPanelGradientBegin => Bg;
        public override Color ToolStripContentPanelGradientEnd => Bg;
        public override Color StatusStripGradientBegin => Header;
        public override Color StatusStripGradientEnd => Header;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Input;
        public override Color MenuItemPressedGradientMiddle => Input;
        public override Color MenuItemPressedGradientEnd => Input;
        public override Color ButtonSelectedHighlight => Hover;
        public override Color ButtonSelectedHighlightBorder => Border;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientMiddle => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonSelectedBorder => Border;
        public override Color ButtonPressedGradientBegin => AccentDark;
        public override Color ButtonPressedGradientMiddle => AccentDark;
        public override Color ButtonPressedGradientEnd => AccentDark;
        public override Color ButtonPressedBorder => Accent;
        public override Color ButtonCheckedGradientBegin => AccentDark;
        public override Color ButtonCheckedGradientMiddle => AccentDark;
        public override Color ButtonCheckedGradientEnd => AccentDark;
        public override Color CheckBackground => AccentDark;
        public override Color CheckSelectedBackground => AccentDark;
        public override Color CheckPressedBackground => AccentDark;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Panel;
        public override Color GripDark => Border;
        public override Color GripLight => Panel;
        public override Color OverflowButtonGradientBegin => Panel;
        public override Color OverflowButtonGradientMiddle => Panel;
        public override Color OverflowButtonGradientEnd => Panel;
    }
}

/// <summary>Form base class: applies <see cref="Theme"/> before first show and darkens the title bar.</summary>
public class ThemedForm : Form
{
    public ThemedForm()
    {
        // every window and popup of the program carries the Tru Tiên icon
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        Theme.Apply(this);
        base.OnLoad(e);
    }
}

/// <summary>TabControl with flat dark tabs (green underline on the active tab), like JD IDE.</summary>
public sealed class ThemedTabControl : TabControl
{
    public ThemedTabControl()
    {
        if (!Theme.Dark) return;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SizeMode = TabSizeMode.Normal;
        Padding = new Point(16, 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!Theme.Dark) { base.OnPaint(e); return; }
        var g = e.Graphics;
        using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);
        var stripBottom = TabCount > 0 ? GetTabRect(0).Bottom : 0;
        using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, stripBottom, Width, stripBottom);
        var bold = new Font(Font, FontStyle.Bold);
        for (int i = 0; i < TabCount; i++)
        {
            var r = GetTabRect(i);
            bool sel = i == SelectedIndex;
            using (var b = new SolidBrush(sel ? Theme.Panel : Theme.Bg)) g.FillRectangle(b, r);
            if (sel)
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.Left, r.Bottom - 2, r.Width, 2);
            TextRenderer.DrawText(g, TabPages[i].Text, sel ? bold : Font, r, sel ? Theme.Text : Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
        bold.Dispose();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }
}

/// <summary>Rounded, padded host for a borderless TextBox (JD IDE-style input); green glow while focused.</summary>
public sealed class RoundBox : Panel
{
    private readonly TextBox _inner;
    private readonly bool _multi;
    private bool _hover;

    public RoundBox(TextBox inner, bool multiline)
    {
        _inner = inner;
        _multi = multiline;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = multiline ? new Padding(10, 8, 4, 8) : new Padding(10, 0, 8, 0);
        Cursor = Cursors.IBeam;
        inner.GotFocus += (_, _) => Invalidate();
        inner.LostFocus += (_, _) => Invalidate();
        inner.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        inner.MouseLeave += (_, _) => { _hover = ClientRectangle.Contains(PointToClient(MousePosition)); Invalidate(); };
        inner.EnabledChanged += (_, _) => { _inner.BackColor = _inner.Enabled ? Theme.Input : Theme.Panel; Invalidate(); };
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        MouseDown += (_, _) => _inner.Focus();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_inner.Parent != this) return;
        if (_multi)
            _inner.Bounds = new Rectangle(Padding.Left, Padding.Top, Math.Max(10, Width - Padding.Horizontal), Math.Max(10, Height - Padding.Vertical));
        else
        {
            int h = _inner.PreferredHeight;
            _inner.Bounds = new Rectangle(Padding.Left, Math.Max(0, (Height - h) / 2), Math.Max(10, Width - Padding.Horizontal), h);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        bool focused = _inner.Focused;
        var r = new RectangleF(1f, 1f, Width - 2.5f, Height - 2.5f);
        using (var path = Theme.RoundRect(r, 7))
        {
            using (var br = new SolidBrush(_inner.Enabled ? Theme.Input : Theme.Panel)) g.FillPath(br, path);
            if (focused)
            {
                // soft outer glow + crisp accent border
                using (var glow = new Pen(Color.FromArgb(55, Theme.Accent), 3f)) g.DrawPath(glow, path);
                using (var pen = new Pen(Theme.Accent, 1.2f)) g.DrawPath(pen, path);
            }
            else
                using (var pen = new Pen(_hover ? Theme.BorderHover : Theme.Border)) g.DrawPath(pen, path);
        }
    }
}
