using System.Drawing.Drawing2D;
using System.Media;

namespace ElementsEditor;

/// <summary>The Tru Tiên icon (Assets/app.ico), shared by every window of the program.</summary>
public static class AppIcon
{
    private static readonly Lazy<Icon?> _icon = new(() => Load(s => new Icon(s)));
    private static readonly Lazy<Image?> _logo = new(() => Load(s => Image.FromStream(s)));

    /// <summary>Multi-size icon (16…256 px); null only if the resource is missing.</summary>
    public static Icon? Icon => _icon.Value;
    /// <summary>256 px artwork for in-window use.</summary>
    public static Image? Logo => _logo.Value;

    private static T? Load<T>(Func<Stream, T> make) where T : class
    {
        var name = typeof(T) == typeof(Icon) ? "ElementsEditor.app.ico" : "ElementsEditor.app_256.png";
        try
        {
            using var s = typeof(AppIcon).Assembly.GetManifestResourceStream(name);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return make(ms);
        }
        catch { return null; }
    }
}

/// <summary>
/// Drop-in replacement for <see cref="MessageBox.Show(string)"/>: same arguments, but a themed
/// window that carries the program icon (system message boxes cannot show a custom icon).
/// </summary>
public static class Msg
{
    public static DialogResult Show(string text) => Show(null, text, "", MessageBoxButtons.OK, MessageBoxIcon.None);
    public static DialogResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) => Show(null, text, caption, buttons, MessageBoxIcon.None);
    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) => Show(null, text, caption, buttons, icon);
    public static DialogResult Show(IWin32Window? owner, string text) => Show(owner, text, "", MessageBoxButtons.OK, MessageBoxIcon.None);
    public static DialogResult Show(IWin32Window? owner, string text, string caption) => Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
    public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons) => Show(owner, text, caption, buttons, MessageBoxIcon.None);

    public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        owner ??= Form.ActiveForm;
        using var f = new MessageForm(text, string.IsNullOrEmpty(caption) ? "Tru Tiên Elements Editor" : caption, buttons, icon);
        if (owner == null) f.StartPosition = FormStartPosition.CenterScreen;
        return f.ShowDialog(owner);
    }

    private sealed class MessageForm : ThemedForm
    {
        private readonly MessageBoxIcon _icon;

        public MessageForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            _icon = icon;
            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            KeyPreview = true;
            Font = new Font("Segoe UI", 9.75F);
            Padding = new Padding(0);

            // left: status glyph (warning/error/info) or the Tru Tiên artwork
            var glyph = new PictureBox { Width = 56, Height = 56, Left = 20, Top = 22, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            glyph.Paint += PaintGlyph;

            bool longText = text.Length > 700 || text.Count(c => c == '\n') > 14;
            Control body;
            if (longText)
            {
                body = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                    Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                    Left = 92, Top = 22, Width = 620, Height = 320, Font = new Font("Consolas", 9F),
                };
            }
            else
            {
                body = new Label
                {
                    Text = text, AutoSize = true, MaximumSize = new Size(480, 0),
                    Left = 92, Top = 24, UseMnemonic = false,
                };
            }
            Controls.Add(glyph);
            Controls.Add(body);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56,
                Padding = new Padding(12, 12, 12, 12), BackColor = Theme.Dark ? Theme.Panel : SystemColors.Control,
            };
            var defs = ButtonsFor(buttons);
            Button? first = null;
            foreach (var (label, result) in defs.Reverse())
            {
                var b = new Button { Text = label, DialogResult = result, Width = 96, Height = 32, Margin = new Padding(6, 0, 0, 0) };
                bar.Controls.Add(b);
                if (CancelButton == null && (result is DialogResult.Cancel or DialogResult.No || defs.Length == 1)) CancelButton = b;
            }
            first = (Button)bar.Controls[bar.Controls.Count - 1]; // left-most = default (OK / Có)
            AcceptButton = first;
            Controls.Add(bar);

            Load += (_, _) =>
            {
                int bodyBottom = body.Bottom;
                int w = Math.Max(360, body.Right + 28);
                int h = Math.Max(glyph.Bottom, bodyBottom) + 24 + bar.Height;
                ClientSize = new Size(w, h);
                ActiveControl = first;
            };
            Shown += (_, _) => Sound();
            KeyDown += (_, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) { try { Clipboard.SetText($"{caption}\r\n\r\n{text}"); } catch { } e.Handled = true; }
            };
        }

        private static (string, DialogResult)[] ButtonsFor(MessageBoxButtons b) => b switch
        {
            MessageBoxButtons.OKCancel => new[] { ("OK", DialogResult.OK), ("Hủy", DialogResult.Cancel) },
            MessageBoxButtons.YesNo => new[] { ("Có", DialogResult.Yes), ("Không", DialogResult.No) },
            MessageBoxButtons.YesNoCancel => new[] { ("Có", DialogResult.Yes), ("Không", DialogResult.No), ("Hủy", DialogResult.Cancel) },
            MessageBoxButtons.RetryCancel => new[] { ("Thử lại", DialogResult.Retry), ("Hủy", DialogResult.Cancel) },
            MessageBoxButtons.AbortRetryIgnore => new[] { ("Hủy bỏ", DialogResult.Abort), ("Thử lại", DialogResult.Retry), ("Bỏ qua", DialogResult.Ignore) },
            _ => new[] { ("OK", DialogResult.OK) },
        };

        private void Sound()
        {
            switch (_icon)
            {
                case MessageBoxIcon.Error: SystemSounds.Hand.Play(); break;
                case MessageBoxIcon.Warning: SystemSounds.Exclamation.Play(); break;
                case MessageBoxIcon.Question: SystemSounds.Question.Play(); break;
                case MessageBoxIcon.Information: SystemSounds.Asterisk.Play(); break;
            }
        }

        private void PaintGlyph(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var r = new Rectangle(2, 2, 52, 52);
            // the Tru Tiên artwork is always shown; a small status badge marks warnings/errors
            if (AppIcon.Logo != null) g.DrawImage(AppIcon.Logo, r);
            Color? badge = _icon switch
            {
                MessageBoxIcon.Error => Color.FromArgb(220, 70, 60),
                MessageBoxIcon.Warning => Color.FromArgb(220, 160, 40),
                MessageBoxIcon.Question => Color.FromArgb(70, 140, 230),
                MessageBoxIcon.Information => Color.FromArgb(50, 170, 90),
                _ => null,
            };
            if (badge == null) return;
            var br = new Rectangle(34, 34, 21, 21);
            using (var bg = new SolidBrush(badge.Value)) g.FillEllipse(bg, br);
            using (var pen = new Pen(Theme.Dark ? Theme.Bg : Color.White, 2)) g.DrawEllipse(pen, br);
            string mark = _icon switch { MessageBoxIcon.Error => "×", MessageBoxIcon.Warning => "!", MessageBoxIcon.Question => "?", _ => "i" };
            using var f = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            TextRenderer.DrawText(g, mark, f, br, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
