using System.Text.RegularExpressions;

namespace ElementsEditor;

/// <summary>Game colour codes (^RRGGBB) offered by the colour bar.</summary>
public static class GameColors
{
    public static readonly (string Code, string Name)[] Palette =
    [
        ("ffffff", "Trắng"), ("ffcb4a", "Vàng"), ("ff0000", "Đỏ"), ("00ff00", "Xanh lá"), ("00b4ff", "Xanh dương"),
        ("c297ff", "Tím"), ("ff9c00", "Cam"), ("ff6ec7", "Hồng"), ("00ffff", "Xanh ngọc"), ("ffd700", "Vàng kim"),
        ("7cfc00", "Xanh nõn chuối"), ("ff4500", "Đỏ cam"), ("a0a0a0", "Xám"), ("8b4513", "Nâu"),
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
        rtb.SuspendLayout();
        rtb.Clear();
        foreach (var (c, run) in Segments(text.Replace("\r\n", "\n").Replace("\\n", "\n").Replace("\r", "\n")))
        {
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            rtb.SelectionColor = c ?? defaultColor;
            rtb.AppendText(run);
        }
        rtb.SelectionStart = 0;
        rtb.ResumeLayout();
    }
}

/// <summary>Row of colour swatches that inserts ^RRGGBB codes into a TextBox (wraps the selection).</summary>
public sealed class ColorCodeBar : FlowLayoutPanel
{
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
        Controls.Add(new Label { Text = "Màu", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 6, 6, 0) });
        foreach (var (code, name) in GameColors.Palette)
        {
            var sw = new Swatch(ColorTranslator.FromHtml("#" + code));
            _tip.SetToolTip(sw, $"{name}  ^{code}\nBấm để chèn; nếu đang bôi đen, đoạn đó được tô màu rồi trả về trắng.");
            var c = code;
            sw.Click += (_, _) => Insert(c);
            Controls.Add(sw);
        }
        var more = new Button { Text = "Màu khác…", AutoSize = true, Height = 24, Margin = new Padding(6, 1, 0, 0), FlatStyle = FlatStyle.Flat };
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

    private void UpdateLast() => _insertLast.Text = $"Chèn ^{_last}";

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
    private readonly RichTextBox _preview;
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
        var prevLabel = new Label { Text = "Xem trước (trong game)", Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, TextAlign = ContentAlignment.BottomLeft };
        _preview = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10.5F), BackColor = Color.FromArgb(10, 12, 14), ScrollBars = RichTextBoxScrollBars.Vertical, TabStop = false };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(0, 6, 0, 0) };
        _hint = new Label { Text = "Ctrl+Enter lưu · Esc huỷ", Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
        _save = new Button { Text = "✓ Lưu chữ", Dock = DockStyle.Right, Width = 96 };
        _cancel = new Button { Text = "Huỷ", Dock = DockStyle.Right, Width = 70 };
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
        _title.Text = $"✎  Đang sửa {field}" + (note != null ? $"   ·   {note}" : "");
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
        _hint.Text = len > _max ? $"Quá dài {len - _max} – cần rút ngắn" : t != _original ? "Chưa lưu · Ctrl+Enter lưu · Esc huỷ" : "Ctrl+Enter lưu · Esc huỷ";
        _hint.ForeColor = len > _max ? Theme.Red : Color.DimGray;
        GameColors.Render(_preview, t, Theme.Dark ? Theme.Text : Color.White);
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
