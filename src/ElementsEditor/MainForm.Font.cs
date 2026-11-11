using ElementsCore;

namespace ElementsEditor;

/// <summary>
/// "Font" menu (next to Language): the font used for game text – Chinese item names, descriptions,
/// NPC dialogs, data grids – and its size. Fonts in the fonts folder next to the exe (e.g. DFKai-SB)
/// are offered first; changes apply at once and are saved.
/// </summary>
public sealed partial class MainForm
{
    private void SetupFontMenu()
    {
        // controls of the main window that show game text
        foreach (var c in new object?[] { _itemsGrid, _fieldsGrid, _textFieldsGrid, _quickTextEditor, _talkDialogsGrid, _talkOptionsGrid, _talkWinTree, _talkWinTextBox, _talkNameBox })
            if (c != null) GameFont.Bind(c);

        var mFont = new ToolStripMenuItem("Aa " + L.T("Font")) { Name = "mnuFont", Alignment = ToolStripItemAlignment.Right };
        mFont.ToolTipText = L.T("Font for Chinese / game text");
        mFont.DropDownOpening += (_, _) =>
        {
            mFont.DropDownItems.Clear();
            mFont.DropDownItems.Add(new ToolStripMenuItem(L.T("Font for Chinese / game text")) { Enabled = false });
            AddFontItem(mFont, null, L.T("Default (program font)"));
            var bundled = GameFont.ProgramFonts;
            if (bundled.Count > 0)
            {
                mFont.DropDownItems.Add(new ToolStripSeparator());
                foreach (var f in bundled) AddFontItem(mFont, f, L.F("{0}  (bundled)", f));
            }
            var installed = GameFont.InstalledCjkFonts().Where(f => !bundled.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            if (installed.Count > 0)
            {
                mFont.DropDownItems.Add(new ToolStripSeparator());
                foreach (var f in installed) AddFontItem(mFont, f, f);
            }
            if (GameFont.Family != null && !bundled.Concat(installed).Contains(GameFont.Family, StringComparer.OrdinalIgnoreCase))
                AddFontItem(mFont, GameFont.Family, GameFont.Family);
            mFont.DropDownItems.Add(L.T("Other installed font…"), null, (_, _) =>
            {
                using var dlg = new FontDialog { ShowEffects = false, ShowColor = false, AllowVerticalFonts = false, FontMustExist = true };
                if (GameFont.Family != null) try { dlg.Font = new Font(GameFont.Family, 10f); } catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK) ChooseGameFont(dlg.Font.FontFamily.Name, GameFont.Scale);
            });

            mFont.DropDownItems.Add(new ToolStripSeparator());
            var size = new ToolStripMenuItem(L.T("Size"));
            foreach (var k in new[] { 1f, 1.1f, 1.25f, 1.5f, 1.75f })
            {
                var it = new ToolStripMenuItem($"{k:P0}") { Checked = Math.Abs(GameFont.Scale - k) < 0.01f };
                var kk = k;
                it.Click += (_, _) => ChooseGameFont(GameFont.Family, kk);
                size.DropDownItems.Add(it);
            }
            mFont.DropDownItems.Add(size);
            mFont.DropDownItems.Add(new ToolStripSeparator());
            mFont.DropDownItems.Add(L.T("Open fonts folder…"), null, (_, _) =>
            {
                Directory.CreateDirectory(GameFont.Folder);
                System.Diagnostics.Process.Start("explorer.exe", GameFont.Folder);
            });
            Theme.Apply(mFont.DropDown);
        };
        mFont.DropDownItems.Add("…");
        _menu.Items.Add(mFont);
    }

    private void AddFontItem(ToolStripMenuItem menu, string? family, string label)
    {
        var it = new ToolStripMenuItem(label) { Checked = string.Equals(family, GameFont.Family, StringComparison.OrdinalIgnoreCase) };
        // show the entry in its own font (a sample of the Chinese glyphs)
        if (family != null && GameFont.Create(family, 10f) is { } sample) { it.Font = sample; it.Text = $"{label}   天尊寶錄 字體"; }
        it.Click += (_, _) => ChooseGameFont(family, GameFont.Scale);
        menu.DropDownItems.Add(it);
    }

    private void ChooseGameFont(string? family, float scale)
    {
        GameFont.Set(family, scale);
        _settings.CjkFont = family;
        _settings.CjkFontScale = scale;
        _settings.Save();
        _lblStatus.Text = L.F("Game text font: {0}", GameFont.Describe());
    }
}
