using ElementsCore;

namespace ElementsEditor;

/// <summary>
/// "Language" menu on the main menu bar (every tab): English (built in) plus every
/// lang\&lt;code&gt;.json next to the exe. Choosing one saves it and restarts the program
/// (the usual unsaved-changes warning comes first).
/// </summary>
public sealed partial class MainForm
{
    private void SetupLanguageMenu()
    {
        var menu = _menu;
        var mLang = new ToolStripMenuItem("🌐 Language") { Name = "mnuLanguage", Alignment = ToolStripItemAlignment.Right };
        mLang.DropDownOpening += (_, _) =>
        {
            mLang.DropDownItems.Clear();
            foreach (var lang in L.Available())
            {
                var label = lang.Code == "en" ? "English" : lang.Name == lang.EnglishName ? lang.Name : $"{lang.Name}  ({lang.EnglishName})";
                var it = new ToolStripMenuItem(label) { Checked = string.Equals(lang.Code, L.Code, StringComparison.OrdinalIgnoreCase), Tag = lang.Code };
                it.Click += (_, _) => SwitchLanguage((string)it.Tag!);
                mLang.DropDownItems.Add(it);
            }
            mLang.DropDownItems.Add(new ToolStripSeparator());
            mLang.DropDownItems.Add(L.T("Open language folder…"), null, (_, _) =>
            {
                Directory.CreateDirectory(L.Folder);
                System.Diagnostics.Process.Start("explorer.exe", L.Folder);
            });
        };
        mLang.DropDownItems.Add("English"); // placeholder so the arrow shows; filled on opening
        menu.Items.Add(mLang);
        SetupFontMenu(); // right-aligned: left of Language
    }

    private void SwitchLanguage(string code)
    {
        if (string.Equals(code, L.Code, StringComparison.OrdinalIgnoreCase)) return;
        _settings.Language = code;
        _settings.Save();
        var name = L.Available().FirstOrDefault(l => l.Code == code)?.Name ?? code;
        if (Msg.Show(L.F("The language is now {0}. Restart the program to apply it?", name), "Language",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            Application.Restart(); // closing still asks about unsaved changes
    }
}
