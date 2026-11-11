using ElementsCore;
using ElementsCore.Formats;

namespace ElementsEditor;

/// <summary>
/// JD IDE-style left activity bar: Elements (the original editor) plus one tab per other game file
/// (Path, Objects, Domains, GShop …). Each tab remembers its recent files and the file that was open,
/// in Documents\TruTien Elements Editor, and reopens it at start-up.
/// </summary>
public sealed partial class MainForm
{
    private ActivityBar _activity = null!;
    private Panel _elementsHost = null!;
    private StartPage _elementsStart = null!;
    private readonly Dictionary<string, ModulePage> _pages = new();
    private RecentStore _recent = null!;
    private string _activeModule = "elements";
    private MenuStrip _menu = null!;

    private void SetupModules()
    {
        _recent = RecentStore.Load();
        _menu = Controls.OfType<MenuStrip>().First();
        var ts = Controls.OfType<ToolStrip>().First(t => t is not MenuStrip && t is not StatusStrip);

        // the original elements editor (toolbar + work area) becomes the "Elements" tab
        Controls.Remove(_mainWorkHost);
        Controls.Remove(ts);
        _elementsHost = new Panel { Dock = DockStyle.Fill };
        var elementsModule = DataModules.Get("elements");
        _elementsStart = new StartPage(elementsModule, _recent, ClientRoot, p => LoadFileAsync(p), OpenFileDialog) { Visible = false };
        _elementsHost.Controls.Add(_mainWorkHost);
        _elementsHost.Controls.Add(_elementsStart);
        _elementsHost.Controls.Add(ts);

        _activity = new ActivityBar(DataModules.All);
        Controls.Add(_activity);
        Controls.SetChildIndex(_activity, 0);
        Controls.Add(_elementsHost);
        Controls.SetChildIndex(_elementsHost, 0);

        foreach (var m in DataModules.All.Where(m => m.View != ModuleView.Elements))
        {
            var page = new ModulePage(m, _recent, ClientRoot, _settings) { Visible = false };
            page.StateChanged += () => OnPageStateChanged(page);
            _pages[m.Key] = page;
            Controls.Add(page);
            Controls.SetChildIndex(page, 0);
        }
        _activity.ModuleClicked += m => ShowModule(m.Key);

        AddRecentMenu();
        FormClosing += OnModulesClosing;
        UpdateElementsStart();
    }

    /// <summary>Client root (the folder that contains "element"), from the open / last elements.data.</summary>
    private string? ClientRoot()
    {
        IEnumerable<string?> candidates = [_ef?.Path, _recent?.For("elements").Open, _recent?.Recent("elements").FirstOrDefault()?.Path, DefaultClientFile];
        foreach (var p in candidates)
        {
            if (string.IsNullOrEmpty(p)) continue;
            var dir = Path.GetDirectoryName(p);
            // ...\Client0619\element\data\elements.data → ...\Client0619
            for (var d = dir; d != null; d = Path.GetDirectoryName(d))
                if (string.Equals(Path.GetFileName(d), "element", StringComparison.OrdinalIgnoreCase) && Directory.Exists(d))
                    return Path.GetDirectoryName(d);
        }
        return null;
    }

    private void ShowModule(string key)
    {
        _activeModule = key;
        _activity.Active = key;
        bool el = key == "elements";
        _elementsHost.Visible = el;
        foreach (var (k, p) in _pages) p.Visible = k == key;
        // the Search/Replace and Việt hóa menus act on elements.data only
        foreach (ToolStripItem it in _menu.Items)
            if (it.Text is { } t && (t.StartsWith("&Tìm kiếm") || t.StartsWith("&Việt hóa"))) it.Visible = el;
        if (!el) _pages[key].RefreshStart();
        UpdateChangesLabel();
        _recent.SetActive(key);
        UpdateWindowTitle();
        if (el)
        {
            if (_ef != null) UpdateSummaryStatus();
            else _lblStatus.Text = "Sẵn sàng. Hãy mở elements.data.";
        }
        else _lblStatus.Text = _pages[key].StatusText.Length > 0 ? _pages[key].StatusText : _pages[key].Module.Description;
    }

    private void OnPageStateChanged(ModulePage page)
    {
        _activity.SetState(page.Module.Key, page.File != null, page.Dirty);
        if (page.Module.Key == _activeModule) UpdateChangesLabel();
        if (page.Module.Key == _activeModule)
        {
            _lblStatus.Text = page.StatusText.Length > 0 ? page.StatusText : page.Module.Description;
            UpdateWindowTitle();
        }
    }

    /// <summary>Elements tab: start page while no elements.data is open; dot on the bar.</summary>
    private void UpdateElementsStart()
    {
        bool has = _ef != null;
        _elementsStart.Visible = !has;
        _mainWorkHost.Visible = has;
        if (!has) _elementsStart.Refresh2();
        _activity.SetState("elements", has, _dirty);
    }

    /// <summary>Title of the active tab: "file * – Tab – Tru Tiên 3 Elements Editor vX".</summary>
    private string? ModuleWindowTitle()
    {
        if (_activeModule == "elements" || !_pages.TryGetValue(_activeModule, out var p)) return null;
        var name = p.File != null ? Path.GetFileName(p.File.Path) + (p.Dirty ? " *" : "") + " - " : "";
        return $"{name}{p.Module.Title} - Tru Tiên 3 Elements Editor {BuildInfo.Tag}";
    }

    /// <summary>Routes Ctrl+S/O/F/W to the active non-elements tab. True = handled.</summary>
    private bool RouteModuleKey(KeyEventArgs e)
    {
        if (_activeModule == "elements" || !_pages.TryGetValue(_activeModule, out var p)) return false;
        if (p.HandleKey(e.KeyData)) { e.SuppressKeyPress = true; e.Handled = true; }
        return true; // never let elements shortcuts act while another tab is shown
    }

    /// <summary>Start-up: reopen the file of every tab, then show the tab that was active.</summary>
    private async void RestoreModules()
    {
        var active = _recent.ActiveModule;
        ShowModule(active != null && (active == "elements" || _pages.ContainsKey(active)) ? active : "elements");
        foreach (var p in _pages.Values)
            await p.RestoreAsync();
    }

    private void OnElementsOpened(string path)
    {
        _recent.Opened("elements", path);
        UpdateElementsStart();
        BuildIdNames();
        BuildItemIcons();
    }

    /// <summary>ID → name of every record in elements.data, for the "→ tên" column of other tabs.</summary>
    private async void BuildIdNames()
    {
        var ef = _ef;
        if (ef == null) { IdNames.Lookup = null; return; }
        var map = await Task.Run(() =>
        {
            var d = new Dictionary<long, string>();
            foreach (var l in ef.Lists)
            {
                if (l.Schema?.IdField == null || l.Schema.NameField == null) continue;
                for (int i = 0; i < l.Items.Count; i++)
                {
                    long id = l.GetId(i);
                    if (id > 0 && !d.ContainsKey(id)) d[id] = $"{l.GetName(i)}  [{l.Name}]";
                }
            }
            return d;
        });
        if (_ef != ef) return;
        IdNames.Lookup = id => map.TryGetValue(id, out var s) ? s : null;
    }

    private void AddRecentMenu()
    {
        if (_menu.Items.Count == 0 || _menu.Items[0] is not ToolStripMenuItem file) return;
        var recentMenu = new ToolStripMenuItem("Mở &gần đây (elements.data)");
        recentMenu.DropDownItems.Add("(trống)");
        recentMenu.DropDownOpening += (_, _) =>
        {
            recentMenu.DropDownItems.Clear();
            foreach (var e in _recent.Recent("elements"))
            {
                var p = e.Path;
                var it = recentMenu.DropDownItems.Add($"{Path.GetFileName(p)}   —   {Path.GetDirectoryName(p)}", null, (_, _) =>
                {
                    if (_dirty && Msg.Show("Chưa lưu thay đổi. Mở file khác sẽ mất các thay đổi chưa lưu. Tiếp tục?", "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    if (File.Exists(p)) LoadFileAsync(p);
                    else Msg.Show($"Không tìm thấy file:\n{p}", "Mở gần đây", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
                it.ToolTipText = $"Mở lúc {e.LastOpened:dd/MM/yyyy HH:mm}";
                if (!File.Exists(p)) it.ForeColor = Color.Gray;
            }
            if (recentMenu.DropDownItems.Count == 0) recentMenu.DropDownItems.Add("(chưa có file nào)").Enabled = false;
            recentMenu.DropDownItems.Add(new ToolStripSeparator());
            recentMenu.DropDownItems.Add("Mở thư mục lưu lịch sử…", null, (_, _) =>
            {
                Directory.CreateDirectory(RecentStore.Folder);
                System.Diagnostics.Process.Start("explorer.exe", RecentStore.Folder);
            });
        };
        file.DropDownItems.Insert(Math.Min(2, file.DropDownItems.Count), recentMenu);
    }

    private void OnModulesClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.Cancel) return;
        // the combined warning in OnMainFormClosing already asked; this only covers a page saved there
        if (!_closingDiscard)
            foreach (var p in _pages.Values)
            {
                if (!p.Dirty) continue;
                ShowModule(p.Module.Key);
                if (!p.ConfirmDiscard()) { e.Cancel = true; return; }
            }
        _recent.Save();
    }
}
