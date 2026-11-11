using System.Drawing.Drawing2D;
using ElementsCore.Formats;

namespace ElementsEditor;

/// <summary>Vertical module bar on the left (icon + caption per file family), JD IDE style.</summary>
public sealed class ActivityBar : Control
{
    private const int ItemH = 50;
    private readonly IReadOnlyList<ModuleInfo> _modules;
    private readonly ToolTip _tip = new();
    private readonly HashSet<string> _loaded = new();
    private readonly HashSet<string> _dirty = new();
    private int _hover = -1;
    private int _scroll;
    private string _active;
    private static readonly string IconFont = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public event Action<ModuleInfo>? ModuleClicked;

    public ActivityBar(IReadOnlyList<ModuleInfo> modules)
    {
        _modules = modules;
        _active = modules[0].Key;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Width = 66;
        Dock = DockStyle.Left;
        Font = new Font("Segoe UI", 7.5F);
        Cursor = Cursors.Hand;
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Active
    {
        get => _active;
        set { _active = value; Invalidate(); }
    }

    /// <summary>Small dot under the icon: a file is open in that tab (green) / has unsaved edits (amber).</summary>
    public void SetState(string key, bool loaded, bool dirty)
    {
        if (loaded) _loaded.Add(key); else _loaded.Remove(key);
        if (dirty) _dirty.Add(key); else _dirty.Remove(key);
        Invalidate();
    }

    private int HitTest(Point p)
    {
        int i = (p.Y - 6 + _scroll) / ItemH;
        return p.Y >= 6 && i >= 0 && i < _modules.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hover)
        {
            _hover = h;
            Invalidate();
            if (h >= 0) _tip.SetToolTip(this, $"{_modules[h].Title}\n{_modules[h].Description}");
            else _tip.SetToolTip(this, null);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h >= 0) ModuleClicked?.Invoke(_modules[h]);
        base.OnMouseClick(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int max = Math.Max(0, _modules.Count * ItemH + 12 - Height);
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * ItemH, 0, max);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool dark = Theme.Dark;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(dark ? Theme.Panel : SystemColors.Control);
        using (var p = new Pen(dark ? Theme.Border : SystemColors.ControlDark)) g.DrawLine(p, Width - 1, 0, Width - 1, Height);

        using var iconFont = new Font(IconFont, 13F);
        for (int i = 0; i < _modules.Count; i++)
        {
            var m = _modules[i];
            var r = new Rectangle(5, 6 + i * ItemH - _scroll, Width - 11, ItemH - 4);
            if (r.Bottom < 0 || r.Top > Height) continue;
            bool act = m.Key == _active, hov = i == _hover;
            if (act || hov)
            {
                using var path = Theme.RoundRect(r, 7);
                var fill = act ? (dark ? Theme.AccentDark : Color.FromArgb(214, 238, 220)) : (dark ? Theme.Hover : SystemColors.ControlLight);
                using var b = new SolidBrush(fill);
                g.FillPath(b, path);
            }
            if (act)
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, r.Top + 10, 3, r.Height - 20);

            var fg = act ? (dark ? Theme.AccentText : Color.FromArgb(20, 120, 50)) : (dark ? (hov ? Theme.Text : Theme.TextDim) : SystemColors.ControlText);
            if (m.ServerSide && !act && !hov) fg = dark ? Theme.TextFaint : SystemColors.GrayText;
            TextRenderer.DrawText(g, m.Glyph, iconFont, new Rectangle(r.Left, r.Top + 3, r.Width, 24), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, m.Title, Font, new Rectangle(r.Left - 4, r.Top + 27, r.Width + 8, 16), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (_loaded.Contains(m.Key) || _dirty.Contains(m.Key))
            {
                var dot = _dirty.Contains(m.Key) ? Theme.Amber : Theme.Accent;
                using var b = new SolidBrush(dot);
                g.FillEllipse(b, r.Right - 10, r.Top + 5, 6, 6);
            }
        }
    }
}
