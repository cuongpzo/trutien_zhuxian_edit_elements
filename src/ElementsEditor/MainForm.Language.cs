using ElementsCore;

namespace ElementsEditor;

/// <summary>
/// "Language" menu on the main menu bar (every tab): English (built in) plus every
/// lang\&lt;code&gt;.json next to the exe. Choosing one switches the whole program at once – no restart:
/// open files, selections and unsaved edits stay; only the UI texts change (see <see cref="UiText"/>).
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Cursor = Cursors.WaitCursor;
        try
        {
            L.Load(code);
            _settings.Language = L.Code;
            _settings.Save();
            UiText.RelocalizeAll();
            // texts composed by the main window itself
            if (_menu.Items["mnuFont"] is ToolStripItem font) font.Text = "Aa " + L.T("Font");
            ShowModule(_activeModule);
            UpdateWindowTitle();
        }
        finally { Cursor = Cursors.Default; }
        var name = L.Available().FirstOrDefault(l => string.Equals(l.Code, L.Code, StringComparison.OrdinalIgnoreCase))?.Name ?? "English";
        _lblStatus.Text = L.F("Language: {0} ({1} ms)", name, sw.ElapsedMilliseconds);
    }
}
