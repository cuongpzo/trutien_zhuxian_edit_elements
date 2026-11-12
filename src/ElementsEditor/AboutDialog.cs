using System.Diagnostics;
using ElementsCore;

namespace ElementsEditor;

/// <summary>Help ▸ About: program, version, author, features and the terms for using the source code.</summary>
public sealed class AboutDialog : ThemedForm
{
    public const string Author = "cuongpzo";
    public const string Location = "Việt Nam";
    public const string ForumUrl = "https://forum.ragezone.com/members/cuongpzo11.2000582524/";
    public const string RepoUrl = "https://github.com/cuongpzo/trutien_zhuxian_edit_elements";

    public AboutDialog()
    {
        Text = L.T("About");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 640);
        Padding = new Padding(18, 14, 18, 14);

        var head = new Panel { Dock = DockStyle.Top, Height = 84 };
        var logo = new PictureBox { Image = AppIcon.Logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64), Location = new Point(0, 6), BackColor = Color.Transparent };
        var name = new Label
        {
            Text = "Tru Tiên 3 Elements Editor", Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            AutoSize = true, Location = new Point(78, 6), UseMnemonic = false,
        };
        var ver = new Label
        {
            Text = L.T("Localization & Data Editing") + "  ·  " + BuildInfo.Tag, ForeColor = Theme.Dark ? Theme.TextDim : SystemColors.GrayText,
            AutoSize = true, Location = new Point(80, 42), UseMnemonic = false,
        };
        head.Controls.AddRange([logo, name, ver]);

        var info = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 8) };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control value)
        {
            info.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 4, 12, 4), ForeColor = Theme.Dark ? Theme.TextDim : SystemColors.GrayText, UseMnemonic = false });
            value.Margin = new Padding(0, 4, 0, 4);
            info.Controls.Add(value);
        }
        Row(L.T("Author"), new Label { Text = Author, AutoSize = true, Font = new Font(Font, FontStyle.Bold), UseMnemonic = false });
        Row(L.T("Country"), new Label { Text = Location, AutoSize = true, UseMnemonic = false });
        Row(L.T("Forum"), Link(ForumUrl));
        Row(L.T("Source code"), Link(RepoUrl));

        var body = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            BorderStyle = BorderStyle.None, Text = BodyText(),
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var ok = new Button { Text = L.T("Close"), Width = 110, Height = 32, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
        var copy = new Button { Text = L.T("Copy info"), Width = 110, Height = 32, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText($"Tru Tiên 3 Elements Editor {BuildInfo.Tag}\r\n{L.T("Author")}: {Author} ({Location})\r\n{ForumUrl}\r\n{RepoUrl}"); } catch { }
        };
        bottom.Controls.Add(ok);
        bottom.Controls.Add(copy);
        bottom.Layout += (_, _) =>
        {
            ok.Location = new Point(bottom.Width - ok.Width, 8);
            copy.Location = new Point(0, 8);
        };
        AcceptButton = ok;
        CancelButton = ok;
        // the read-only text box must not open with everything selected
        Shown += (_, _) => { body.Select(0, 0); ActiveControl = ok; };

        Controls.Add(body);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
        Controls.Add(info);
        Controls.Add(head);
        Controls.Add(bottom);
    }

    private static LinkLabel Link(string url)
    {
        var l = new LinkLabel { Text = url, AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentText, UseMnemonic = false };
        l.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };
        return l;
    }

    private static string BodyText()
    {
        string nl = Environment.NewLine;
        var lines = new List<string>
        {
            L.T("FEATURES"),
            "• " + L.T("elements.data: every list (items, NPCs, monsters, skills…) and the NPC dialogues, edited by schema."),
            "• " + L.T("Other client files: path, dynamic objects, domains (map view), GShop (shop editor like JD IDE), tasks (names, NPC dialogues, options), dynamic tasks, task NPCs, VIP awards, Lua / text files."),
            "• " + L.T("Export / import CSV, XLSX and JSON with key columns, so a round trip changes nothing."),
            "• " + L.T("Vietnamese (Hán Việt) conversion that keeps colour codes and never exceeds the field length."),
            "• " + L.T("Colour-code bar ^RRGGBB with an in-game style preview."),
            "• " + L.T("Edit history: every change listed, jump to it, undo one or all."),
            "• " + L.T("Game icons, Chinese game fonts, dark theme, 7 UI languages switched instantly."),
            "• " + L.T("Every file is written back byte for byte when nothing was edited; a .bak copy is made before saving."),
            "",
            L.T("TERMS OF USE"),
            "• " + L.T("Free for personal and non-commercial use, learning and private game servers."),
            "• " + L.T("You may read, change and share the source code, as long as the author credit (cuongpzo) and a link to the original repository are kept."),
            "• " + L.T("Selling this program or its source code, or using it in a paid product, needs the author's written permission."),
            "• " + L.T("Game data, names, icons and fonts belong to their owners; this tool is not affiliated with the game publisher."),
            "• " + L.T("Provided as is, without any warranty. Always keep a backup of your game files."),
        };
        return string.Join(nl, lines);
    }
}
