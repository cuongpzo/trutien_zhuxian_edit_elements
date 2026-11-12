using System.Text.RegularExpressions;
using ElementsCore;

namespace ElementsEditor;

/// <summary>Game colour codes (^RRGGBB) offered by the colour bar.</summary>
public static class GameColors
{
    public static readonly (string Code, string Name)[] Palette =
    [
        ("ffffff", L.T("White")), ("ffcb4a", L.T("Yellow")), ("ff0000", L.T("Red")), ("00ff00", L.T("Green")), ("00b4ff", L.T("Blue")),
        ("c297ff", L.T("Purple")), ("ff9c00", L.T("Orange")), ("ff6ec7", L.T("Pink")), ("00ffff", L.T("Cyan")), ("ffd700", L.T("Gold")),
        ("7cfc00", L.T("Lime green")), ("ff4500", L.T("Red-orange")), ("a0a0a0", L.T("Gray")), ("8b4513", L.T("Brown")),
    ];

    private static readonly Regex Code = new(@"\^([0-9a-fA-F]{6})", RegexOptions.Compiled);

    /// <summary>Splits text into (colour, run) segments; colour null = default.</summary>
    public static IEnumerable<(Color? color, string text)> Segments(string s)
    {
        Color? cur = null;
        int pos = 0;
        foreach (Match m in Code.Matches(s))
        {
            if (m.Index > pos) yield return (cur, s[pos..m.Index]);
            cur = ColorTranslator.FromHtml("#" + m.Groups[1].Value);
            pos = m.Index + m.Length;
        }
        if (pos < s.Length) yield return (cur, s[pos..]);
    }

    /// <summary>Text without colour codes.</summary>
    public static string Plain(string s) => Code.Replace(s, "");

    /// <summary>Renders text with its colour codes into a RichTextBox (preview).</summary>
    public static void Render(RichTextBox rtb, string text, Color defaultColor)
    {
        // set the whole text first, then colour each run by selecting it (reliable on a read-only box)
        var segs = Segments(text.Replace("\r\n", "\n").Replace("\\n", "\n").Replace("\r", "\n")).ToList();
        rtb.SuspendLayout();
        rtb.Text = string.Concat(segs.Select(s => s.text));
        rtb.SelectAll();
        rtb.SelectionColor = defaultColor;
        int pos = 0;
        foreach (var (c, run) in segs)
        {
            if (c != null && run.Length > 0)
            {
                rtb.Select(pos, run.Length);
                rtb.SelectionColor = c.Value;
            }
            pos += run.Length;
        }
        rtb.Select(0, 0);
        rtb.ResumeLayout();
    }
}

/// <summary>
/// In-game look of a text: ^RRGGBB codes colour the following run, line breaks are kept, long lines
/// wrap (character by character, as the game does for Chinese). Drawn by itself – a RichTextBox loses
/// its colours under the dark window theme.
/// </summary>
public sealed class GameTextPreview : Control
{
    private string _text = "";
    private readonly Dictionary<char, int> _widths = new();
    private int _scroll, _contentHeight;

    public GameTextPreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font("Segoe UI", 10.5F);
        BackColor = Color.FromArgb(10, 12, 14);
        Padding = new Padding(8, 6, 8, 6);
    }

    /// <summary>Text with game colour codes (^RRGGBB, \n).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string GameText
    {
        get => _text;
        set { _text = value ?? ""; _scroll = 0; Invalidate(); }
    }

    /// <summary>Colour of text before the first code.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color DefaultTextColor { get; set; } = Color.FromArgb(235, 235, 235);

    protected override void OnFontChanged(EventArgs e) { _widths.Clear(); base.OnFontChanged(e); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int max = Math.Max(0, _contentHeight - Height + Padding.Vertical);
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * Font.Height * 2, 0, max);
        Invalidate();
        base.OnMouseWheel(e);
    }

    private int W(Graphics g, char ch)
    {
        if (!_widths.TryGetValue(ch, out int w))
            _widths[ch] = w = TextRenderer.MeasureText(g, ch.ToString(), Font, new Size(1000, 100), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        return w;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int lineH = Font.Height + 2;
        int x0 = Padding.Left, maxX = Width - Padding.Right - 8;
        int x = x0, y = Padding.Top - _scroll;
        var runChars = new System.Text.StringBuilder();
        int runX = x;
        Color runColor = DefaultTextColor;
        void Flush()
        {
            if (runChars.Length > 0 && y + lineH > 0 && y < Height)
                TextRenderer.DrawText(g, runChars.ToString(), Font, new Point(runX, y), runColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            runChars.Clear();
        }
        var text = _text.Replace("\r\n", "\n").Replace("\\n", "\n").Replace("\r", "\n");
        foreach (var (c, run) in GameColors.Segments(text))
        {
            Flush();
            runColor = c ?? DefaultTextColor;
            runX = x;
            foreach (char ch in run)
            {
                if (ch == '\n') { Flush(); x = x0; y += lineH; runX = x; continue; }
                int w = W(g, ch);
                if (x + w > maxX && x > x0) { Flush(); x = x0; y += lineH; runX = x; }
                runChars.Append(ch);
                x += w;
            }
        }
        Flush();
        _contentHeight = y + _scroll + lineH;
        if (_contentHeight > Height)
        {
            // thin scroll indicator
            float k = (float)Height / _contentHeight;
            int barH = Math.Max(16, (int)(Height * k)), barY = (int)(_scroll * k);
            using var b = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
            g.FillRectangle(b, Width - 5, barY, 3, barH);
        }
    }
}

/// <summary>Row of colour swatches that inserts ^RRGGBB codes into a TextBox (wraps the selection).</summary>
public sealed class ColorCodeBar : FlowLayoutPanel, ILocalizable
{
    private readonly List<(Control Swatch, string Code, string Name)> _swatches = new();
    private readonly TextBox _target;
    private string _last = "ffcb4a";
    private readonly LinkLabel _insertLast;
    private readonly ToolTip _tip = new();

    public ColorCodeBar(TextBox target)
    {
        _target = target;
        WrapContents = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(0, 2, 0, 2);
        Controls.Add(new Label { Text = L.T("Color"), AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 6, 6, 0) });
        foreach (var (code, name) in GameColors.Palette)
        {
            var sw = new Swatch(ColorTranslator.FromHtml("#" + code));
            _swatches.Add((sw, code, name));
            _tip.SetToolTip(sw, SwatchTip(code, name));
            var c = code;
            sw.Click += (_, _) => Insert(c);
            Controls.Add(sw);
        }
        var more = new Button { Text = L.T("Other color…"), AutoSize = true, Height = 24, Margin = new Padding(6, 1, 0, 0), FlatStyle = FlatStyle.Flat };
        more.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { FullOpen = true, Color = ColorTranslator.FromHtml("#" + _last) };
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Insert($"{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}");
        };
        Controls.Add(more);
        _insertLast = new LinkLabel { AutoSize = true, Margin = new Padding(8, 6, 0, 0), LinkColor = Color.FromArgb(63, 185, 80) };
        _insertLast.LinkClicked += (_, _) => Insert(_last);
        Controls.Add(_insertLast);
        UpdateLast();
    }

    private void UpdateLast() => _insertLast.Text = L.F("Insert ^{0}", _last);

    private static string SwatchTip(string code, string name) =>
        $"{L.Retranslate(name)}  ^{code}\n" + L.T("Click to insert; if text is selected, it is coloured and then set back to white.");

    public void OnLanguageChanged()
    {
        foreach (var (sw, code, name) in _swatches) _tip.SetToolTip(sw, SwatchTip(code, name));
        UpdateLast();
    }

    public void Insert(string code)
    {
        _last = code.ToLowerInvariant();
        UpdateLast();
        int st = _target.SelectionStart, len = _target.SelectionLength;
        if (len > 0)
        {
            var sel = _target.SelectedText;
            _target.SelectedText = $"^{_last}{sel}^ffffff";
            _target.Select(st, len + 14);
        }
        else
        {
            _target.SelectedText = "^" + _last;
            _target.SelectionStart = st + 7;
        }
        _target.Focus();
    }

    private sealed class Swatch : Control
    {
        private readonly Color _c;
        private bool _hover;
        public Swatch(Color c)
        {
            _c = c;
            Size = new Size(22, 22);
            Margin = new Padding(2, 2, 2, 2);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? BackColor);
            using var path = Theme.RoundRect(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), 4);
            using (var b = new SolidBrush(_c)) g.FillPath(b, path);
            using var p = new Pen(_hover ? Theme.Accent : Color.FromArgb(90, 255, 255, 255), _hover ? 2f : 1f);
            g.DrawPath(p, path);
        }
    }
}

/// <summary>
/// "Editing &lt;field&gt;" box (JD IDE style): multi-line text, n / max counter, colour code bar,
/// live preview with the colours, Cancel / Save. Ctrl+Enter saves, Esc cancels.
/// </summary>
public sealed class ColorTextEditor : Panel
{
    private readonly Label _title, _counter, _hint;
    private readonly TextBox _text;
    private readonly GameTextPreview _preview;
    private readonly Button _save, _cancel;
    private Func<string, int> _measure = s => s.Length;
    private int _max = int.MaxValue;
    private string _original = "";
    private bool _crlf;

    /// <summary>Saves the text; return an error message to keep the editor open, or null.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, string?>? SaveText { get; set; }
    public event Action? Cancelled;

    public ColorTextEditor()
    {
        Padding = new Padding(10, 8, 10, 8);
        var top = new Panel { Dock = DockStyle.Top, Height = 24 };
        _title = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
        _counter = new Label { Dock = DockStyle.Right, Width = 110, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Consolas", 9F), ForeColor = Color.DimGray };
        top.Controls.Add(_title);
        top.Controls.Add(_counter);

        _text = new TextBox { Dock = DockStyle.Top, Height = 74, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = new Font("Segoe UI", 10.5F) };
        var bar = new ColorCodeBar(_text) { Dock = DockStyle.Top };
        var prevLabel = new Label { Text = L.T("Preview (in game)"), Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, TextAlign = ContentAlignment.BottomLeft };
        _preview = new GameTextPreview { Dock = DockStyle.Fill, TabStop = false };
        GameFont.Bind(_text);
        GameFont.Bind(_preview);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(0, 6, 0, 0) };
        _hint = new Label { Text = L.T("Ctrl+Enter save · Esc cancel"), Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
        _save = new Button { Text = L.T("✓ Save text"), Dock = DockStyle.Right, Width = 96 };
        _cancel = new Button { Text = L.T("Cancel"), Dock = DockStyle.Right, Width = 70 };
        bottom.Controls.Add(_hint);
        bottom.Controls.Add(_cancel);
        bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6 });
        bottom.Controls.Add(_save);

        Controls.Add(_preview);
        Controls.Add(prevLabel);
        Controls.Add(bar);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4 });
        Controls.Add(_text);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4 });
        Controls.Add(top);
        Controls.Add(bottom);

        _text.TextChanged += (_, _) => Changed();
        _text.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSave(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DoCancel(); }
        };
        _save.Click += (_, _) => DoSave();
        _cancel.Click += (_, _) => DoCancel();
    }

    public bool IsModified => Normalize(_text.Text) != _original;

    /// <summary>Shows a field's text. <paramref name="measure"/>: length as the limit counts it (chars / GBK bytes).</summary>
    public void Begin(string field, string text, int maxLength, Func<string, int>? measure = null, string? note = null)
    {
        _measure = measure ?? (s => s.Length);
        _max = maxLength <= 0 ? int.MaxValue : maxLength;
        _crlf = text.Contains("\r\n");
        _original = text;
        _title.Text = "✎  " + L.F("Editing {0}", field) + (note != null ? $"   ·   {note}" : "");
        _text.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _text.SelectionStart = _text.TextLength;
        Changed();
        Visible = true;
    }

    public void FocusText() { _text.Focus(); }

    private string Normalize(string s) => _crlf ? s : s.Replace("\r\n", "\n");

    private void Changed()
    {
        var t = Normalize(_text.Text);
        int len = _measure(t);
        _counter.Text = _max == int.MaxValue ? $"{len}" : $"{len} / {_max}";
        _counter.ForeColor = len > _max ? Theme.Red : Theme.Dark ? Theme.TextDim : Color.DimGray;
        _save.Enabled = len <= _max && t != _original;
        _hint.Text = len > _max ? L.F("Too long by {0} – shorten it", len - _max) : t != _original ? L.T("Unsaved · Ctrl+Enter save · Esc cancel") : L.T("Ctrl+Enter save · Esc cancel");
        _hint.ForeColor = len > _max ? Theme.Red : Color.DimGray;
        _preview.GameText = t;
    }

    private void DoSave()
    {
        var t = Normalize(_text.Text);
        if (t == _original || _measure(t) > _max || SaveText == null) return;
        var err = SaveText(t);
        if (err != null) { _hint.Text = err; _hint.ForeColor = Theme.Red; return; }
        _original = t;
        Changed();
    }

    /// <summary>Applies typed but unsaved text (e.g. before moving to another record).</summary>
    public void CommitPending()
    {
        if (Visible && IsModified) DoSave();
    }

    private void DoCancel()
    {
        _text.Text = _original.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Cancelled?.Invoke();
    }
}
