using ElementsCore;
using ElementsCore.Exchange;
using ElementsCore.Formats;

namespace ElementsEditor;

/// <summary>
/// One activity-bar tab for a non-elements file family: header (open / recent / save / close,
/// export / import, edit history), the start page while no file is open, otherwise a table, text
/// or hex view of the file. Changes are measured against the bytes as opened (or last saved).
/// </summary>
public sealed class ModulePage : Panel
{
    public ModuleInfo Module { get; }
    public DataFile? File { get; private set; }
    public bool Dirty { get; private set; }
    public string StatusText { get; private set; } = "";
    /// <summary>Number of field / row differences from the file as opened (updated shortly after each edit).</summary>
    public int ChangeCount { get; private set; }
    public event Action? StateChanged;

    private readonly RecentStore _recent;
    private readonly AppSettings _settings;
    private readonly Func<string?> _clientRoot;
    private readonly Label _lblTitle, _lblPath;
    private readonly Button _btnOpen, _btnRecent, _btnSave, _btnSaveAs, _btnClose, _btnExport, _btnImport, _btnHistory, _btnHanViet;
    private readonly Panel _body;
    private readonly StartPage _start;
    private readonly ChangesPanel _changes;
    private readonly Splitter _changesSplit;
    private readonly System.Windows.Forms.Timer _countTimer = new() { Interval = 450 };
    private Control? _view;
    private IModuleView? _iview;
    private bool _loading;
    private byte[]? _origBytes;
    private DataFile? _baseline;

    public ModulePage(ModuleInfo m, RecentStore recent, Func<string?> clientRoot, AppSettings settings)
    {
        Module = m; _recent = recent; _clientRoot = clientRoot; _settings = settings;
        Dock = DockStyle.Fill;

        var header = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(16, 8, 12, 8) };
        _lblTitle = new Label { Text = m.Title, Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Location = new Point(14, 6), UseMnemonic = false };
        _lblPath = new Label { Text = m.Description, ForeColor = Color.DimGray, AutoSize = false, AutoEllipsis = true, Location = new Point(16, 32), Height = 18, Width = 600, UseMnemonic = false };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        _btnOpen = Btn(L.T("Open file…"), 90);
        _btnRecent = Btn(L.T("Recent ▾"), 90);
        _btnHistory = Btn(L.T("Edit history"), 98);
        _btnExport = Btn(L.T("Export ▾"), 74);
        _btnImport = Btn(L.T("Import…"), 70);
        _btnHanViet = Btn(L.T("Hán Việt…"), 84);
        _btnSave = Btn(L.T("Save"), 64);
        _btnSaveAs = Btn(L.T("Save as…"), 92);
        _btnClose = Btn(L.T("Close file"), 80);
        buttons.Controls.AddRange([_btnOpen, _btnRecent, _btnHistory, _btnHanViet, _btnExport, _btnImport, _btnSave, _btnSaveAs, _btnClose]);
        header.Controls.Add(_lblPath);
        header.Controls.Add(_lblTitle);
        header.Controls.Add(buttons);
        header.Resize += (_, _) => _lblPath.Width = Math.Max(100, header.Width - buttons.Width - 40);

        _body = new Panel { Dock = DockStyle.Fill };
        _start = new StartPage(m, recent, clientRoot, p => _ = OpenAsync(p), OpenDialog);
        _changes = new ChangesPanel(ComputeChanges, c => _iview?.Locate(c), AfterRevert) { Dock = DockStyle.Left, Visible = false };
        _changesSplit = new Splitter { Dock = DockStyle.Left, Width = 5, Visible = false };
        _changes.VisibleChanged += (_, _) => _changesSplit.Visible = _changes.Visible;
        _body.Controls.Add(_start);
        _body.Controls.Add(_changesSplit);
        _body.Controls.Add(_changes);

        Controls.Add(_body);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(42, 50, 55) });
        Controls.Add(header);

        _btnOpen.Click += (_, _) => OpenDialog();
        _btnRecent.Click += (_, _) => ShowRecentMenu();
        _btnSave.Click += (_, _) => Save(false);
        _btnSaveAs.Click += (_, _) => Save(true);
        _btnClose.Click += (_, _) => CloseFile();
        _btnHistory.Click += (_, _) => ToggleChanges();
        _btnExport.Click += (_, _) => ShowExportMenu();
        _btnImport.Click += (_, _) => Import();
        _btnHanViet.Click += (_, _) => ShowHanViet();
        _countTimer.Tick += (_, _) => { _countTimer.Stop(); Recount(); };
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) _ = OpenAsync(f[0]); };
        UpdateHeader();
    }

    private static Button Btn(string text, int w) => new() { Text = text, Width = w, Height = 30, Margin = new Padding(4, 0, 0, 0) };

    // ───────────────────────── open / save / close ─────────────────────────

    public void OpenDialog()
    {
        if (!ConfirmDiscard()) return;
        using var ofd = new OpenFileDialog { Filter = Module.DialogFilter, Title = L.F("{0} – choose a file", Module.Title) };
        var last = _recent.Recent(Module.Key).FirstOrDefault()?.Path;
        var root = _clientRoot();
        if (last != null && Directory.Exists(Path.GetDirectoryName(last))) ofd.InitialDirectory = Path.GetDirectoryName(last);
        else if (root != null && Module.ClientFolder != null && !Module.ClientFolder.Contains('*') && Directory.Exists(Path.Combine(root, Module.ClientFolder)))
            ofd.InitialDirectory = Path.Combine(root, Module.ClientFolder);
        if (ofd.ShowDialog(FindForm()) == DialogResult.OK) _ = OpenAsync(ofd.FileName);
    }

    /// <summary>Opens a file in this tab. <paramref name="quiet"/>: no error box (start-up restore).</summary>
    public async Task OpenAsync(string path, bool quiet = false)
    {
        if (_loading) return;
        if (!quiet && !ConfirmDiscard()) return;
        if (!System.IO.File.Exists(path))
        {
            if (!quiet && Msg.Show(L.F("File not found:\n{0}\n\nRemove it from the recent list?", path), Module.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _recent.Forget(Module.Key, path);
                _start.Refresh2();
            }
            return;
        }
        _loading = true;
        SetStatus(L.F("Reading {0}…", path));
        try
        {
            var bytes = await Task.Run(() => System.IO.File.ReadAllBytes(path));
            var f = await Task.Run(() => DataModules.Parse(Module, bytes, path));
            _origBytes = bytes;
            _baseline = null;
            ShowFile(f);
            _recent.Opened(Module.Key, path);
            var s = $"{Path.GetFileName(path)} · {f.FormatName}";
            if (f.Tables.Count > 0) s += " · " + string.Join(", ", f.Tables.Select(t => $"{t.Name}: {t.Rows.Count:N0}"));
            if (f.Warnings.Count > 0) s += " · " + L.T("Warning: ") + string.Join("; ", f.Warnings.Take(3)) + (f.Warnings.Count > 3 ? $" (+{f.Warnings.Count - 3})" : "");
            SetStatus(s);
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Reading the file failed: {0}", ex.Message));
            RecentStore.Log("ERROR", Module.Key, $"{path} :: {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            if (!quiet) Msg.Show(L.F("Could not read the file:\n{0}\n\n{1}", path, ex.Message), Module.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _loading = false; }
    }

    private void ShowFile(DataFile f)
    {
        File = f;
        Dirty = false;
        ChangeCount = 0;
        _view?.Dispose();
        _iview = f switch
        {
            TextDataFile t => new TextFileView(t),
            RawDataFile r => new RawFileView(r),
            DomainFile d => new DomainMapView(d, p => _ = OpenAsync(p)),
            GShopFile g when g.Tables.FirstOrDefault(t => t.Name == "Items")?.IndexOf("id") >= 0 => new GShopView(g),
            _ => new TableFileView(f),
        };
        _iview.Changed += OnViewChanged;
        _iview.Status += SetStatus;
        _view = (Control)_iview;
        _view.Dock = DockStyle.Fill;
        _start.Visible = false;
        _body.Controls.Add(_view);
        _view.BringToFront();
        if (_changes.Visible) _changes.Reload();
        UpdateHeader();
        StateChanged?.Invoke();
    }

    private void OnViewChanged()
    {
        Dirty = true;
        UpdateHeader();
        StateChanged?.Invoke();
        _countTimer.Stop();
        _countTimer.Start();
    }

    public bool Save(bool saveAs)
    {
        if (File == null || File is RawDataFile) return false;
        if (_iview?.Commit() == false) return false;
        string target = File.Path;
        if (saveAs)
        {
            using var sfd = new SaveFileDialog
            {
                Filter = Module.DialogFilter, FileName = Path.GetFileName(target),
                InitialDirectory = Path.GetDirectoryName(target),
            };
            if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return false;
            target = sfd.FileName;
        }
        try
        {
            // safety: the bytes we are about to write must read back as the same format
            var bytes = File.ToBytes();
            if (File is not TextDataFile && DataModules.TryParse(Module, bytes, target, out var why)?.GetType() != File.GetType())
                throw new InvalidDataException(L.T("The edited data cannot be read back – nothing was written.") + " " + why);
            var bak = File.Save(target, backup: true);
            _origBytes = bytes;
            _baseline = null;
            if (File is TextDataFile tf) tf.MarkSaved();
            Dirty = false;
            ChangeCount = 0;
            _recent.Saved(Module.Key, target);
            if (saveAs) _recent.Opened(Module.Key, target);
            if (_changes.Visible) _changes.Reload();
            UpdateHeader();
            StateChanged?.Invoke();
            SetStatus(L.F("Saved {0}", target) + (bak != null ? $" · backup: {Path.GetFileName(bak)}" : ""));
            return true;
        }
        catch (Exception ex)
        {
            Msg.Show(L.F("Save failed:\n{0}", ex.Message), Module.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public void CloseFile()
    {
        if (File == null || !ConfirmDiscard()) return;
        _view?.Dispose();
        _view = null; _iview = null; File = null; Dirty = false; ChangeCount = 0;
        _origBytes = null; _baseline = null;
        _changes.Visible = false;
        _recent.Closed(Module.Key);
        _start.Visible = true;
        _start.Refresh2();
        SetStatus("");
        UpdateHeader();
        StateChanged?.Invoke();
    }

    /// <summary>True when it is OK to drop the current content (saved, discarded, or nothing to lose).</summary>
    public bool ConfirmDiscard()
    {
        if (!Dirty) return true;
        var r = Msg.Show(L.F("{0}: {1} has unsaved changes. Save first?", Module.Title, Path.GetFileName(File?.Path)), L.T("Not saved"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return Save(false);
        Dirty = false;
        return true;
    }

    /// <summary>Reopens the file that was open in this tab when the program was closed.</summary>
    public Task RestoreAsync()
    {
        var p = _recent.For(Module.Key).Open;
        return p != null && System.IO.File.Exists(p) ? OpenAsync(p, quiet: true) : Task.CompletedTask;
    }

    // ───────────────────────── changes (edit history) ─────────────────────────

    /// <summary>Pushes text typed in the view's edit box into the data (before closing / saving).</summary>
    public void CommitView() => _iview?.Commit();

    public void ToggleChanges()
    {
        if (File == null || File is RawDataFile) { SetStatus(L.T("Open a file to see the changes.")); return; }
        _changes.Visible = !_changes.Visible;
    }

    private List<ChangeItem> ComputeChanges()
    {
        if (File == null || _origBytes == null) return new();
        if (File is TextDataFile tf)
        {
            var cur = _iview is TextFileView tv ? tv.CurrentText : tf.Text;
            return TableDiff.TextDiff(tf, cur, tf.OriginalText, () => (_iview as TextFileView)?.ResetText(tf.OriginalText));
        }
        _baseline ??= DataModules.Parse(Module, _origBytes, File.Path);
        return TableDiff.Compute(File, _baseline);
    }

    private void Recount()
    {
        if (File == null || File is RawDataFile) return;
        try { ChangeCount = ComputeChanges().Count; }
        catch { ChangeCount = Dirty ? 1 : 0; }
        if (ChangeCount == 0 && Dirty && File is not TextDataFile) Dirty = false; // edited back to the original values
        UpdateHeader();
        StateChanged?.Invoke();
    }

    private void AfterRevert()
    {
        _iview?.RefreshAll();
        Dirty = true;
        Recount();
    }

    // ───────────────────────── export / import ─────────────────────────

    private void ShowExportMenu()
    {
        if (File == null || File.Tables.Count == 0) { SetStatus(L.T("This tab has no table to export (text file / not decoded).")); return; }
        var cur = _iview?.CurrentTable ?? File.Tables[0];
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.F("Table \"{0}\" → Excel (.xlsx)…", cur.Name), null, (_, _) => Export([cur], "xlsx"));
        menu.Items.Add(L.F("Table \"{0}\" → CSV…", cur.Name), null, (_, _) => Export([cur], "csv"));
        menu.Items.Add(L.F("Table \"{0}\" → JSON…", cur.Name), null, (_, _) => Export([cur], "json"));
        if (File.Tables.Count > 1)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.F("All {0} tables → Excel (.xlsx, one sheet per table)…", File.Tables.Count), null, (_, _) => Export(File.Tables, "xlsx"));
            menu.Items.Add(L.F("All {0} tables → JSON…", File.Tables.Count), null, (_, _) => Export(File.Tables, "json"));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Key columns: \"#\" (position) + the table's ID column – keep them unchanged so the import finds the right rows")).Enabled = false;
        Theme.Apply(menu);
        menu.Show(_btnExport, new Point(0, _btnExport.Height));
    }

    private void Export(IReadOnlyList<RecordTable> tables, string ext)
    {
        if (File == null || _iview?.Commit() == false) return;
        var baseName = Path.GetFileNameWithoutExtension(File.Path) + (tables.Count == 1 && File.Tables.Count > 1 ? "_" + tables[0].Name.Replace(' ', '_') : "");
        using var sfd = new SaveFileDialog { Filter = DataSheets.FileFilter, FilterIndex = ext switch { "csv" => 2, "json" => 3, _ => 1 }, FileName = $"{baseName}.{ext}", InitialDirectory = Path.GetDirectoryName(File.Path) };
        if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            TableExchange.Export(File, tables, sfd.FileName, Module.Key);
            int rows = tables.Sum(t => t.Rows.Count);
            SetStatus(L.F("Exported {0:N0} rows ({1} tables) → {2}", rows, tables.Count, sfd.FileName));
            RecentStore.Log("EXPORT", Module.Key, sfd.FileName);
        }
        catch (Exception ex) { Msg.Show(L.F("Export failed:\n{0}", ex.Message), Module.Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void Import()
    {
        if (File == null || File.Tables.Count == 0) { SetStatus(L.T("This tab has no table to import into.")); return; }
        if (_iview?.Commit() == false) return;
        using var ofd = new OpenFileDialog { Filter = DataSheets.OpenFilter, Title = L.F("{0} – import data (CSV / XLSX / JSON)", Module.Title), InitialDirectory = Path.GetDirectoryName(File.Path) };
        if (ofd.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            var res = TableExchange.Import(File, ofd.FileName, _iview?.CurrentTable);
            RecentStore.Log("IMPORT", Module.Key, ofd.FileName);
            _iview?.RefreshAll();
            if (res.CellsChanged > 0 || res.RowsAdded > 0) OnViewChanged();
            Msg.Show(res.Summary(L.F("Import {0} into {1}", Path.GetFileName(ofd.FileName), Path.GetFileName(File.Path))), L.T("Import data"), MessageBoxButtons.OK,
                res.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            if (res.CellsChanged > 0 || res.RowsAdded > 0) _changes.Visible = true;
        }
        catch (Exception ex) { Msg.Show(L.F("Import failed:\n{0}", ex.Message), Module.Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ───────────────────────── bulk Hán Việt ─────────────────────────

    private void ShowHanViet()
    {
        if (File == null || File.Tables.Count == 0) { SetStatus(L.T("This tab has no table to localize.")); return; }
        if (_iview?.Commit() == false) return;
        var cur = _iview?.CurrentTable ?? File.Tables[0];
        var scopes = new List<TextScope> { new(L.F("Table {0} ({1:N0})", cur.Name, cur.Rows.Count), () => HanVietTools.TableTargets(cur)) };
        if (File.Tables.Count > 1) scopes.Add(new(L.F("All {0} tables", File.Tables.Count), () => File.Tables.SelectMany(t => HanVietTools.TableTargets(t))));
        using var dlg = new HanVietDialog(scopes, _settings, cur.NameColumn >= 0 ? cur.Columns[cur.NameColumn].Name : "name");
        dlg.ShowDialog(FindForm());
        if (dlg.Applied == 0) return;
        _iview?.RefreshAll();
        OnViewChanged();
        _changes.Visible = true;
        _changes.Reload();
    }

    // ───────────────────────── misc ─────────────────────────

    private void ShowRecentMenu()
    {
        var menu = new ContextMenuStrip();
        foreach (var e in _recent.Recent(Module.Key))
        {
            var it = menu.Items.Add($"{Path.GetFileName(e.Path)}   —   {Path.GetDirectoryName(e.Path)}");
            it.ToolTipText = L.F("Opened {0:dd/MM/yyyy HH:mm}", e.LastOpened);
            if (!System.IO.File.Exists(e.Path)) it.ForeColor = Color.Gray;
            var p = e.Path;
            it.Click += (_, _) => _ = OpenAsync(p);
        }
        if (menu.Items.Count == 0) menu.Items.Add(L.T("(no files yet)")).Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Open the history folder…"), null, (_, _) =>
        {
            Directory.CreateDirectory(RecentStore.Folder);
            System.Diagnostics.Process.Start("explorer.exe", RecentStore.Folder);
        });
        Theme.Apply(menu);
        menu.Show(_btnRecent, new Point(0, _btnRecent.Height));
    }

    /// <summary>Ctrl+S / Ctrl+Shift+S / Ctrl+O / Ctrl+F / Ctrl+W / Ctrl+E / Ctrl+I / Ctrl+H for this tab. Returns true if handled.</summary>
    public bool HandleKey(Keys keys)
    {
        switch (keys)
        {
            case Keys.Control | Keys.S: Save(false); return true;
            case Keys.Control | Keys.Shift | Keys.S: Save(true); return true;
            case Keys.Control | Keys.O: OpenDialog(); return true;
            case Keys.Control | Keys.W: CloseFile(); return true;
            case Keys.Control | Keys.F: _iview?.FocusSearch(); return true;
            case Keys.Control | Keys.E: ShowExportMenu(); return true;
            case Keys.Control | Keys.I: Import(); return true;
            case Keys.Control | Keys.H: ToggleChanges(); return true;
        }
        return false;
    }

    private void UpdateHeader()
    {
        bool has = File != null;
        _lblTitle.Text = Module.Title + (has ? $"  ·  {Path.GetFileName(File!.Path)}{(Dirty ? " *" : "")}" : "");
        _lblPath.Text = has ? $"{File!.Path}   ·   {File.FormatName}" : Module.Description;
        bool editable = has && File is not RawDataFile;
        bool tables = has && File!.Tables.Count > 0;
        _btnSave.Enabled = editable;
        _btnSaveAs.Enabled = editable;
        _btnClose.Enabled = has;
        _btnExport.Enabled = tables;
        _btnImport.Enabled = tables;
        _btnHistory.Enabled = editable;
        _btnHanViet.Enabled = tables;
        _btnHistory.Text = ChangeCount > 0 ? L.F("History ({0})", ChangeCount) : L.T("Edit history");
    }

    private void SetStatus(string s)
    {
        StatusText = s;
        StateChanged?.Invoke();
    }

    public void RefreshStart() { if (File == null) _start.Refresh2(); }
}

/// <summary>What a module page needs from its content view.</summary>
public interface IModuleView
{
    event Action? Changed;
    event Action<string>? Status;
    /// <summary>Pushes pending edits into the model; false = invalid, do not save.</summary>
    bool Commit();
    void FocusSearch();
    /// <summary>Reloads everything from the model (after import / undo).</summary>
    void RefreshAll() { }
    /// <summary>Shows the record / field of a change.</summary>
    void Locate(ChangeItem c) { }
    RecordTable? CurrentTable => null;
}
