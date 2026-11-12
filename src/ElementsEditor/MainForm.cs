using System.Buffers.Binary;
using System.Reflection;
using ElementsCore;
using ElementsCore.Schema;

namespace ElementsEditor;

public sealed partial class MainForm : ThemedForm
{
    private const string DefaultClientFile = @"E:\Game\TruTien3_2026\TruTien3\Server_Clietnts\Client0619\element\data\elements.data";

    private readonly string[] _startupArgs;
    private readonly AppSettings _settings;
    private readonly SchemaRepository _repo;

    private ElementsFile? _ef;
    private bool _dirty;
    private readonly HashSet<byte[]> _dirtyRecords = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TalkDialog> _dirtyDialogs = new(ReferenceEqualityComparer.Instance);

    // Current selection state
    private bool _inTalkMode;
    private ElementList? _currentList;
    private List<int> _filteredItemIndices = new();
    private int _currentItemIndex = -1;
    private List<FieldDef> _filteredFields = new();
    private List<FieldDef> _textFields = new();
    private bool _syncingUi;

    // Layout & Top Controls
    private readonly ToolStripButton _btnSave;
    private readonly ToolStripStatusLabel _lblStatus;
    private readonly ToolStripProgressBar _progressBar;
    private readonly Panel _mainWorkHost;
    private readonly Panel _listViewHost;
    private readonly Panel _workSplitHost;

    // Left Pane: Lists
    private readonly TextBox _listSearchBox;
    private readonly ComboBox _listFilterCombo;
    private readonly ListView _listsView;
    private readonly List<ListEntry> _visibleLists = new();

    // Center Pane (List Items)
    private readonly Panel _listCenterHost;
    private readonly TextBox _itemSearchBox;
    private readonly CheckBox _chkItemCjkOnly;
    private readonly DataGridView _itemsGrid;
    private readonly Label _lblListSummary;

    // Right Pane (Inspector Tabs)
    private readonly TabControl _inspectorTabs;
    private readonly TextBox _fieldSearchBox;
    private readonly CheckBox _chkFieldTextOnly;
    private readonly Button _btnEnumMask;
    private readonly Button _btnJumpRef;
    private readonly Label _lblCharCount;
    private readonly DataGridView _fieldsGrid;

    // Tab 2: Quick localization text
    private readonly DataGridView _textFieldsGrid;
    private readonly TextBox _quickTextEditor;
    private readonly Label _lblQuickCounter;
    private readonly Button _btnQuickApplyNext;

    // Tab 3: Hex View
    private readonly RichTextBox _hexBox;

    // TalkProc Panel (NPC Dialogs)
    private readonly Panel _talkHost;

    private sealed record ListEntry(bool IsTalk, ElementList? List, string DisplayTitle, int Count, string SchemaTag);

    public MainForm(string[] args)
    {
        _startupArgs = args;
        _settings = AppSettings.Load();
        _repo = new SchemaRepository(SchemaRepository.FindDefaultRoot());

        Text = $"Tru Tiên 3 - Elements Editor ({L.T("Localization & Data Editing")}) {ElementsCore.BuildInfo.Tag}";
        Size = new Size(1480, 900);
        MinimumSize = new Size(1050, 650);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        // 1. MenuStrip
        var menu = new MenuStrip();
        var mFile = new ToolStripMenuItem(L.T("&File")) { Name = "mnuFile" };
        mFile.DropDownItems.Add(L.T("&Open elements.data...\tCtrl+O"), null, (_, _) => OpenFileDialog());
        var mOpenDefault = new ToolStripMenuItem(L.T("Quick open &Client0619\\elements.data"), null, (_, _) => { if (File.Exists(DefaultClientFile)) LoadFileAsync(DefaultClientFile); })
        {
            Enabled = File.Exists(DefaultClientFile)
        };
        mFile.DropDownItems.Add(mOpenDefault);
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add(L.T("&Save (automatic backup)\tCtrl+S"), null, (_, _) => SaveFile(false));
        mFile.DropDownItems.Add(L.T("Save &As...\tCtrl+Shift+S"), null, (_, _) => SaveFile(true));
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add(L.T("E&xit"), null, (_, _) => Close());

        var mEdit = new ToolStripMenuItem(L.T("&Search && Edit")) { Name = "mnuEdit" };
        mEdit.DropDownItems.Add(L.T("&Global search...\tCtrl+F"), null, (_, _) => ShowGlobalSearch());
        mEdit.DropDownItems.Add(L.T("Find && batch &replace...\tCtrl+H"), null, (_, _) => ShowBatchReplace());
        mEdit.DropDownItems.Add(new ToolStripSeparator());
        mEdit.DropDownItems.Add(L.T("&Clone selected item"), null, (_, _) => CloneSelectedItem());
        mEdit.DropDownItems.Add(L.T("&Delete selected item"), null, (_, _) => DeleteSelectedItem());

        var mLoc = new ToolStripMenuItem(L.T("&Localization (CSV)")) { Name = "mnuLoc" };
        mLoc.DropDownItems.Add(L.T("Export &detailed CSV (Chinese only)..."), null, (_, _) => ExportCsv(glossary: false, onlyCjk: true, currentListOnly: false));
        mLoc.DropDownItems.Add(L.T("Export detailed CSV (selected &list)..."), null, (_, _) => ExportCsv(glossary: false, onlyCjk: true, currentListOnly: true));
        mLoc.DropDownItems.Add(L.T("Export detailed CSV (&all strings)..."), null, (_, _) => ExportCsv(glossary: false, onlyCjk: false, currentListOnly: false));
        mLoc.DropDownItems.Add(new ToolStripSeparator());
        mLoc.DropDownItems.Add(L.T("Export CSV &glossary - merge duplicate strings (Chinese only)..."), null, (_, _) => ExportCsv(glossary: true, onlyCjk: true, currentListOnly: false));
        mLoc.DropDownItems.Add(new ToolStripSeparator());
        mLoc.DropDownItems.Add(L.T("&Import translated CSV (auto-detects detailed / glossary)..."), null, (_, _) => ImportCsv());

        var mTools = new ToolStripMenuItem(L.T("&Tools")) { Name = "mnuTools" };
        mTools.DropDownItems.Add(L.T("Export selected list to &JSON..."), null, (_, _) => ExportCurrentListJson());
        mTools.DropDownItems.Add(L.T("&Compare (diff) with original / backup file..."), null, (_, _) => CompareWithBaseline());
        mTools.DropDownItems.Add(L.T("&Verify integrity (round-trip)"), null, (_, _) => VerifyRoundTrip());
        mTools.DropDownItems.Add(new ToolStripSeparator());
        mTools.DropDownItems.Add(Theme.Dark ? L.T("Switch to &light theme") : L.T("Switch to &dark theme"), null, (_, _) => ToggleTheme());

        var mHelp = new ToolStripMenuItem(L.T("&Help")) { Name = "mnuHelp" };
        mHelp.DropDownItems.Add(L.T("&About…"), null, (_, _) => { using var dlg = new AboutDialog(); dlg.ShowDialog(this); });

        menu.Items.AddRange([mFile, mEdit, mLoc, mTools, mHelp]);
        MainMenuStrip = menu;

        // 2. ToolStrip
        var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        var btnOpen = new ToolStripButton(L.T("Open file"));
        btnOpen.Click += (_, _) => OpenFileDialog();
        var btnClient = new ToolStripButton(L.T("Open Client0619")) { Enabled = File.Exists(DefaultClientFile) };
        btnClient.Click += (_, _) => LoadFileAsync(DefaultClientFile);
        _btnSave = new ToolStripButton(L.T("Save (backup)")) { Enabled = false };
        _btnSave.Click += (_, _) => SaveFile(false);
        var btnSearch = new ToolStripButton(L.T("Global search"));
        btnSearch.Click += (_, _) => ShowGlobalSearch();
        var btnReplace = new ToolStripButton(L.T("Batch replace"));
        btnReplace.Click += (_, _) => ShowBatchReplace();
        var btnExpGlossary = new ToolStripButton(L.T("Export localization (glossary)"));
        btnExpGlossary.Click += (_, _) => ExportCsv(glossary: true, onlyCjk: true, currentListOnly: false);
        var btnImpCsv = new ToolStripButton(L.T("Import translated CSV"));
        btnImpCsv.Click += (_, _) => ImportCsv();
        ts.Items.AddRange([btnOpen, btnClient, _btnSave, new ToolStripSeparator(), btnSearch, btnReplace, new ToolStripSeparator(), btnExpGlossary, btnImpCsv]);

        // 3. StatusStrip
        var status = new StatusStrip();
        _lblStatus = new ToolStripStatusLabel(L.T("Ready. Open elements.data.")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _progressBar = new ToolStripProgressBar { Visible = false, Width = 160 };
        status.Items.AddRange([_lblStatus, _progressBar]);

        // 4. Main Workspace (Panel + Splitter avoids WinForms SplitContainer GDI+ layout bugs)
        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 320 };
        var mainSplitter = new Splitter { Dock = DockStyle.Left, Width = 5, BackColor = SystemColors.ControlDark };
        _mainWorkHost = new Panel { Dock = DockStyle.Fill };

        var workLeft = new Panel { Dock = DockStyle.Left, Width = 410 };
        var workSplitter = new Splitter { Dock = DockStyle.Left, Width = 5, BackColor = SystemColors.ControlDark };
        var workRight = new Panel { Dock = DockStyle.Fill };
        _workSplitHost = new Panel { Dock = DockStyle.Fill };
        _workSplitHost.Controls.Add(workRight);
        _workSplitHost.Controls.Add(workSplitter);
        _workSplitHost.Controls.Add(workLeft);
        _mainWorkHost.Controls.Add(_workSplitHost);

        // Left Pane: Lists Explorer
        var leftTop = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(4) };
        _listSearchBox = new TextBox { Dock = DockStyle.Top, PlaceholderText = L.T("Filter lists (number or struct name)...") };
        _listFilterCombo = new ComboBox { Dock = DockStyle.Bottom, DropDownStyle = ComboBoxStyle.DropDownList };
        _listFilterCombo.Items.AddRange([
            L.T("All (318 lists + Dialogs)"),
            L.T("With data (count > 0)"),
            L.T("Still has Chinese (CJK)"),
            "Schema Exact",
            "Schema Borrowed / Auto"
        ]);
        _listFilterCombo.SelectedIndex = 1; // Default: non-empty lists
        leftTop.Controls.AddRange([_listSearchBox, _listFilterCombo]);

        _listsView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            GridLines = true,
        };
        _listsView.Columns.Add("#", 36);
        _listsView.Columns.Add("Struct / List", 150);
        _listsView.Columns.Add(L.T("Items"), 58, HorizontalAlignment.Right);
        _listsView.Columns.Add("Schema", 54);
        leftPanel.Controls.Add(_listsView);
        leftPanel.Controls.Add(leftTop);
        _listViewHost = leftPanel;

        // Center Pane: Items Grid
        _listCenterHost = new Panel { Dock = DockStyle.Fill };
        var centerTop = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(4) };
        _itemSearchBox = new TextBox { Left = 4, Top = 4, Width = 240, PlaceholderText = L.T("Search ID or name in the list...") };
        _chkItemCjkOnly = new CheckBox { Text = L.T("CJK only"), Left = 250, Top = 6, Width = 72 };
        var btnClone = new Button { Text = L.T("+ Clone"), Left = 4, Top = 32, Width = 86, Height = 25 };
        var btnDelete = new Button { Text = L.T("- Delete"), Left = 94, Top = 32, Width = 62, Height = 25 };
        _lblListSummary = new Label { Left = 162, Top = 37, AutoSize = true, ForeColor = Color.DimGray, Text = "" };
        centerTop.Controls.AddRange([_itemSearchBox, _chkItemCjkOnly, btnClone, btnDelete, _lblListSummary]);

        _itemsGrid = CreateFastGrid();
        _itemsGrid.VirtualMode = true;
        _itemsGrid.ReadOnly = true;
        _itemsGrid.MultiSelect = false;
        _itemsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _itemsGrid.Columns.Add("colRow", "#");
        _itemsGrid.Columns.Add("colId", "ID");
        _itemsGrid.Columns.Add("colName", L.T("Name"));
        _itemsGrid.Columns.Add("colDirty", "*");
        _itemsGrid.Columns[0].Width = 48;
        _itemsGrid.Columns[1].Width = 68;
        _itemsGrid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _itemsGrid.Columns[3].Width = 24;
        _listCenterHost.Controls.Add(_itemsGrid);
        _listCenterHost.Controls.Add(centerTop);
        workLeft.Controls.Add(_listCenterHost);

        // Right Pane: Inspector Tabs
        _inspectorTabs = new ThemedTabControl { Dock = DockStyle.Fill };

        // Tab 1: Structured Fields
        var tabFields = new TabPage(L.T("Field details"));
        var fieldBar = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4) };
        _fieldSearchBox = new TextBox { Left = 4, Top = 6, Width = 185, PlaceholderText = L.T("Filter field name...") };
        _chkFieldTextOnly = new CheckBox { Text = L.T("Text only"), Left = 195, Top = 8, Width = 74 };
        _btnEnumMask = new Button { Text = "Enum/Mask...", Left = 274, Top = 5, Width = 92, Height = 25, Enabled = false };
        _btnJumpRef = new Button { Text = L.T("Go to ref ->"), Left = 370, Top = 5, Width = 82, Height = 25, Enabled = false };
        _lblCharCount = new Label { Left = 460, Top = 10, AutoSize = true, ForeColor = Color.DimGray, Text = "" };
        fieldBar.Controls.AddRange([_fieldSearchBox, _chkFieldTextOnly, _btnEnumMask, _btnJumpRef, _lblCharCount]);

        _fieldsGrid = CreateFastGrid();
        _fieldsGrid.VirtualMode = false;
        _fieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "fOff", HeaderText = "Off", Width = 70, ReadOnly = true });
        _fieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "fGrp", HeaderText = L.T("Group"), Width = 105, ReadOnly = true });
        _fieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "fPath", HeaderText = L.T("Field"), Width = 180, ReadOnly = true });
        _fieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "fVal", HeaderText = L.T("Value (edit directly)"), Width = 210, ReadOnly = false });
        _fieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "fType", HeaderText = L.T("Type"), Width = 72, ReadOnly = true });
        _fieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "fNote", HeaderText = L.T("Enum / Note"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        tabFields.Controls.Add(_fieldsGrid);
        tabFields.Controls.Add(fieldBar);

        // Tab 2: Quick localization text
        var tabText = new TabPage(L.T("Localize text"));
        var textTopPanel = new Panel { Dock = DockStyle.Top, Height = 260 };
        var textSplitter = new Splitter { Dock = DockStyle.Top, Height = 5, BackColor = SystemColors.ControlDark };
        _textFieldsGrid = CreateFastGrid();
        _textFieldsGrid.ReadOnly = false;
        _textFieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "tField", HeaderText = L.T("Text field"), Width = 170, ReadOnly = true });
        _textFieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "tLimit", HeaderText = L.T("Limit"), Width = 75, ReadOnly = true });
        _textFieldsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "tVal", HeaderText = L.T("Content"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = false });
        textTopPanel.Controls.Add(_textFieldsGrid);

        var quickPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        var quickTop = new Panel { Dock = DockStyle.Top, Height = 30 };
        _lblQuickCounter = new Label { Left = 0, Top = 6, AutoSize = true, Text = L.T("Select a text field above to translate it quickly.") };
        _btnQuickApplyNext = new Button { Text = L.T("Apply && go to next item (Ctrl+Enter)"), Width = 240, Height = 26, Dock = DockStyle.Right, Enabled = false };
        quickTop.Controls.Add(_lblQuickCounter);
        quickTop.Controls.Add(_btnQuickApplyNext);
        _quickTextEditor = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 11f) };
        quickPanel.Controls.Add(_quickTextEditor);
        quickPanel.Controls.Add(quickTop);
        tabText.Controls.Add(quickPanel);
        tabText.Controls.Add(textSplitter);
        tabText.Controls.Add(textTopPanel);

        // Tab 3: Hex View
        var tabHex = new TabPage("Hex View");
        _hexBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            BackColor = Color.FromArgb(248, 249, 250),
        };
        tabHex.Controls.Add(_hexBox);

        _inspectorTabs.TabPages.AddRange([tabFields, tabText, tabHex]);
        workRight.Controls.Add(_inspectorTabs);

        // TalkProc Host (hidden until [TALK] selected)
        _talkHost = BuildTalkProcPanel();
        _talkHost.Visible = false;
        _mainWorkHost.Controls.Add(_talkHost);

        Controls.Add(_mainWorkHost);
        Controls.Add(mainSplitter);
        Controls.Add(_listViewHost);
        Controls.Add(ts);
        Controls.Add(menu);
        Controls.Add(status);

        // Events wiring
        _listSearchBox.TextChanged += (_, _) => PopulateListsView();
        _listFilterCombo.SelectedIndexChanged += (_, _) => PopulateListsView();
        _listsView.SelectedIndexChanged += (_, _) => OnListSelected();
        // left list panel → JD IDE-style dropdown selector above the items (MainForm.Selector.cs)
        SetupListSelector(mainSplitter, workLeft);

        _itemSearchBox.TextChanged += (_, _) => RefreshFilteredItems();
        _chkItemCjkOnly.CheckedChanged += (_, _) => RefreshFilteredItems();
        btnClone.Click += (_, _) => CloneSelectedItem();
        btnDelete.Click += (_, _) => DeleteSelectedItem();

        _itemsGrid.CellValueNeeded += OnItemsGridCellValueNeeded;
        _itemsGrid.SelectionChanged += (_, _) => OnItemSelected();

        _fieldSearchBox.TextChanged += (_, _) => RefreshInspector();
        _chkFieldTextOnly.CheckedChanged += (_, _) => RefreshInspector();
        _fieldsGrid.CellValueChanged += OnFieldCellValueChanged;
        _fieldsGrid.SelectionChanged += (_, _) => OnFieldSelectionChanged();
        _btnEnumMask.Click += (_, _) => OpenEnumMaskPicker();
        _btnJumpRef.Click += (_, _) => JumpToReference();

        _textFieldsGrid.SelectionChanged += (_, _) => SyncQuickEditorFromSelection();
        _textFieldsGrid.CellValueChanged += OnQuickTextGridCellChanged;
        _quickTextEditor.TextChanged += (_, _) => UpdateQuickCounter();
        _quickTextEditor.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyQuickTextAndNext(); }
        };
        _btnQuickApplyNext.Click += (_, _) => ApplyQuickTextAndNext();

        KeyDown += OnMainFormKeyDown;
        Shown += (_, _) => OnStartupShown();
        FormClosing += OnMainFormClosing;

        // left activity bar: Elements + Path / Objects / Domains / GShop … (MainForm.Modules.cs)
        SetupModules();
        // colour text editor, data export/import, edit history (MainForm.Exchange.cs)
        SetupDataExchange();
        // game icons from surfaces.pck (MainForm.Icons.cs)
        SetupIcons();
        // UI language menu (MainForm.Language.cs)
        SetupLanguageMenu();
    }

    private static DataGridView CreateFastGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        };
        typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(g, true);
        return g;
    }

    private void OnStartupShown()
    {
        string? target = _startupArgs.FirstOrDefault(File.Exists);
        // the elements.data that was open last time (Documents\TruTien Elements Editor), else the old setting
        var lastOpen = _recent.For("elements").Open;
        if (target == null && _settings.AutoOpenLast && lastOpen != null && File.Exists(lastOpen)) target = lastOpen;
        if (target == null && _settings.AutoOpenLast && _recent.Recent("elements").Count == 0 && !string.IsNullOrEmpty(_settings.LastFile) && File.Exists(_settings.LastFile))
            target = _settings.LastFile;
        if (target != null) LoadFileAsync(target);
        RestoreModules();
    }

    private void OnMainFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (RouteModuleKey(e)) return;
        if (e.Control && e.Shift && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; SaveFile(true); }
        else if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; SaveFile(false); }
        else if (e.Control && e.KeyCode == Keys.O) { e.SuppressKeyPress = true; OpenFileDialog(); }
        else if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; ShowGlobalSearch(); }
        else if (e.Control && e.KeyCode == Keys.H) { e.SuppressKeyPress = true; ShowBatchReplace(); }
    }

    /// <summary>
    /// Before closing: one warning listing every tab with unsaved changes (text typed in an edit box
    /// counts too) – Yes = save all then exit, No = exit without saving, Cancel = stay.
    /// </summary>
    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        _fieldEditor?.CommitPending();
        foreach (var p in _pages.Values) p.CommitView();
        var lines = new List<string>();
        if (_dirty && _ef != null)
        {
            int n = _dirtyRecords.Count + _dirtyDialogs.Count;
            lines.Add($"• Elements: {Path.GetFileName(_ef.Path)} – " + (n > 0 ? L.F("{0:N0} records changed", n) : L.T("has changes")));
        }
        foreach (var p in _pages.Values.Where(p => p.Dirty && p.File != null))
            lines.Add($"• {p.Module.Title}: {Path.GetFileName(p.File!.Path)} – " + (p.ChangeCount > 0 ? L.F("{0:N0} changes", p.ChangeCount) : L.T("has changes")));
        if (lines.Count == 0) return;

        var res = Msg.Show(L.T("There are UNSAVED changes – exiting now will lose them:") + "\n\n" + string.Join("\n", lines)
            + "\n\n" + L.T("Yes  = Save all (with backup), then exit\nNo  = Exit and discard the changes\nCancel  = Stay in the program"),
            L.T("Unsaved – confirm exit"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (res == DialogResult.Cancel) { e.Cancel = true; return; }
        if (res == DialogResult.No) { _closingDiscard = true; return; }

        if (_dirty && _ef != null && !SaveFile(false)) { e.Cancel = true; return; }
        foreach (var p in _pages.Values.Where(p => p.Dirty && p.File != null))
        {
            ShowModule(p.Module.Key);
            if (!p.Save(false)) { e.Cancel = true; return; }
        }
        _closingDiscard = true;
    }

    private bool _closingDiscard;

    private void OpenFileDialog()
    {
        if (_dirty && Msg.Show(L.T("Changes are not saved. Opening another file will lose the unsaved changes. Continue?"), L.T("Confirm"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        using var ofd = new OpenFileDialog
        {
            Filter = "elements.data|elements.data;*.data|" + L.T("All files") + "|*.*",
            Title = L.T("Select the elements.data file"),
        };
        if (ofd.ShowDialog(this) == DialogResult.OK) LoadFileAsync(ofd.FileName);
    }

    private async void LoadFileAsync(string path)
    {
        Enabled = false;
        _progressBar.Visible = true;
        _progressBar.Value = 0;
        _lblStatus.Text = L.F("Reading {0}...", path);
        var prog = new Progress<int>(v => _progressBar.Value = Math.Clamp(v, 0, 100));

        try
        {
            var ef = await Task.Run(() => ElementsFile.Load(path, _repo, prog));
            _ef = ef;
            _dirty = false;
            _dirtyRecords.Clear();
            _dirtyDialogs.Clear();
            _currentList = null;
            _currentItemIndex = -1;
            _btnSave.Enabled = true;

            _settings.LastFile = path;
            _settings.Save();
            _baselineEf = null;
            OnElementsOpened(path);

            UpdateWindowTitle();
            PopulateListsView();
            SelectFirstDataList();
            UpdateSummaryStatus();

            if (ef.Warnings.Count > 0)
                if (_activeModule == "elements") _lblStatus.Text += " | " + L.F("Warning: {0}", string.Join("; ", ef.Warnings));
        }
        catch (Exception ex)
        {
            Msg.Show(L.F("Error reading file:\n{0}", ex.Message), L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            _lblStatus.Text = L.T("Failed to read the file.");
        }
        finally
        {
            _progressBar.Visible = false;
            Enabled = true;
        }
    }

    private void UpdateWindowTitle()
    {
        _activity?.SetState("elements", _ef != null, _dirty);
        UpdateChangesLabel();
        if (ModuleWindowTitle() is { } moduleTitle) { Text = moduleTitle; return; }
        string name = _ef?.Path != null ? Path.GetFileName(_ef.Path) : "elements.data";
        string star = _dirty ? " *" : "";
        Text = $"{name}{star} - Tru Tiên 3 Elements Editor {ElementsCore.BuildInfo.Tag}";
    }

    private void UpdateSummaryStatus()
    {
        if (_ef == null || _activeModule != "elements") return; // another tab owns the status bar
        int exact = _ef.Lists.Count(l => l.Schema.Match == SchemaMatch.Exact);
        int borrowed = _ef.Lists.Count(l => l.Schema.Match == SchemaMatch.Borrowed);
        int auto = _ef.Lists.Count(l => l.Schema.Match == SchemaMatch.Auto);
        int mod = _dirtyRecords.Count + _dirtyDialogs.Count;
        _lblStatus.Text = $"{_ef.Path} | " + L.F("Version {0} ({1}) | {2} lists (Exact:{3}, Borrowed:{4}, Auto:{5}) | Dialogs: {6}", _ef.Version, _ef.Layout?.Version ?? "auto", _ef.Lists.Count, exact, borrowed, auto, _ef.Dialogs?.Count ?? 0) + " | " + L.F("Modified: {0}", mod);
    }

    private void PopulateListsView()
    {
        if (_ef == null) return;
        string q = _listSearchBox.Text.Trim();
        int mode = _listFilterCombo.SelectedIndex;

        _visibleLists.Clear();
        if (_ef.Dialogs != null && (mode == 0 || mode == 1 || (mode == 2 && _ef.Dialogs.Any(DialogHasCjk))))
        {
            if (string.IsNullOrEmpty(q) || ("talk_proc npc dialog " + L.T("conversation")).Contains(q, StringComparison.OrdinalIgnoreCase))
                _visibleLists.Add(new ListEntry(true, null, "NPC_DIALOG (TALK_PROC)", _ef.Dialogs.Count, "Tree"));
        }

        foreach (var l in _ef.Lists)
        {
            if (mode == 1 && l.Items.Count == 0) continue;
            if (mode == 2 && !ListHasCjk(l)) continue;
            if (mode == 3 && l.Schema.Match != SchemaMatch.Exact) continue;
            if (mode == 4 && l.Schema.Match == SchemaMatch.Exact) continue;

            if (!string.IsNullOrEmpty(q))
            {
                bool match = l.Index.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)
                          || l.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                          || (l.Schema.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
                if (!match) continue;
            }
            string tag = l.Schema.Match switch
            {
                SchemaMatch.Exact => "Exact",
                SchemaMatch.Borrowed => l.Schema.SourceVersion ?? "Borrow",
                _ => "Auto",
            };
            _visibleLists.Add(new ListEntry(false, l, l.Name, l.Items.Count, tag));
        }

        _listsView.BeginUpdate();
        _listsView.Items.Clear();
        foreach (var e in _visibleLists)
        {
            string idxText = e.IsTalk ? "TALK" : e.List!.Index.ToString();
            var lvi = new ListViewItem([idxText, e.DisplayTitle, e.Count.ToString(), e.SchemaTag]);
            if (e.IsTalk) lvi.ForeColor = Color.DarkBlue;
            else if (e.List!.Schema.Match == SchemaMatch.Auto) lvi.ForeColor = Color.DarkGoldenrod;
            _listsView.Items.Add(lvi);
        }
        _listsView.EndUpdate();
    }

    private static bool ListHasCjk(ElementList l)
    {
        var textFields = l.Schema.Fields.Where(f => f.IsText).ToList();
        if (textFields.Count == 0) return false;
        for (int i = 0; i < l.Items.Count; i++)
        {
            foreach (var f in textFields)
            {
                if (TextExchange.ContainsCjk(FieldCodec.GetText(l.Items[i], f))) return true;
            }
        }
        return false;
    }

    private void OnListSelected()
    {
        if (_listsView.SelectedIndices.Count == 0) return;
        var entry = _visibleLists[_listsView.SelectedIndices[0]];
        if (entry.IsTalk)
        {
            _inTalkMode = true;
            _currentList = null;
            _workSplitHost.Visible = false;
            _talkHost.Visible = true;
            PopulateTalkDialogs();
        }
        else
        {
            _inTalkMode = false;
            _talkHost.Visible = false;
            _workSplitHost.Visible = true;
            _currentList = entry.List;
            RefreshFilteredItems();
            if (_currentList != null && _settings.LastList != _currentList.Index) { _settings.LastList = _currentList.Index; _settings.Save(); }
        }
    }

    private void RefreshFilteredItems()
    {
        _filteredItemIndices.Clear();
        _currentItemIndex = -1;
        if (_currentList == null)
        {
            _itemsGrid.RowCount = 0;
            _lblListSummary.Text = "";
            RefreshInspector();
            return;
        }

        string q = _itemSearchBox.Text.Trim();
        bool cjkOnly = _chkItemCjkOnly.Checked;
        var textFields = cjkOnly ? _currentList.Schema.Fields.Where(f => f.IsText).ToList() : null;

        for (int i = 0; i < _currentList.Items.Count; i++)
        {
            if (cjkOnly && textFields != null)
            {
                bool has = false;
                foreach (var tf in textFields)
                {
                    if (TextExchange.ContainsCjk(FieldCodec.GetText(_currentList.Items[i], tf))) { has = true; break; }
                }
                if (!has) continue;
            }
            if (!string.IsNullOrEmpty(q))
            {
                long id = _currentList.GetId(i);
                string nm = _currentList.GetName(i);
                if (!id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !nm.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            _filteredItemIndices.Add(i);
        }

        _itemsGrid.RowCount = 0;
        _itemsGrid.RowCount = _filteredItemIndices.Count;
        _lblListSummary.Text = L.F("{0:N0} / {1:N0} items (size={2}B)", _filteredItemIndices.Count, _currentList.Items.Count, _currentList.ItemSize);
        if (_filteredItemIndices.Count > 0)
        {
            _itemsGrid.CurrentCell = _itemsGrid.Rows[0].Cells[0];
            _itemsGrid.Rows[0].Selected = true;
            OnItemSelected();
        }
        else RefreshInspector();
    }

    private void OnItemsGridCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (_currentList == null || e.RowIndex < 0 || e.RowIndex >= _filteredItemIndices.Count) return;
        int itemIdx = _filteredItemIndices[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => itemIdx,
            1 => _currentList.GetId(itemIdx),
            2 => _currentList.GetName(itemIdx),
            3 => _dirtyRecords.Contains(_currentList.Items[itemIdx]) ? "*" : "",
            _ => ""
        };
    }

    private void OnItemSelected()
    {
        if (_currentList == null || _itemsGrid.CurrentRow == null)
        {
            _currentItemIndex = -1;
            RefreshInspector();
            return;
        }
        int row = _itemsGrid.CurrentRow.Index;
        if (row < 0 || row >= _filteredItemIndices.Count) return;
        _currentItemIndex = _filteredItemIndices[row];
        RefreshInspector();
    }

    private void RefreshInspector()
    {
        _fieldEditor?.CommitPending();
        _syncingUi = true;
        try
        {
            _fieldsGrid.Rows.Clear();
            _textFieldsGrid.Rows.Clear();
            _filteredFields.Clear();
            _textFields.Clear();
            _btnEnumMask.Enabled = false;
            _btnJumpRef.Enabled = false;
            _lblCharCount.Text = "";

            if (_currentList == null || _currentItemIndex < 0 || _currentItemIndex >= _currentList.Items.Count)
            {
                _hexBox.Clear();
                _quickTextEditor.Text = "";
                _btnQuickApplyNext.Enabled = false;
                return;
            }

            var item = _currentList.Items[_currentItemIndex];
            string fq = _fieldSearchBox.Text.Trim();
            bool textOnly = _chkFieldTextOnly.Checked;

            foreach (var f in _currentList.Schema.Fields)
            {
                if (f.IsText) _textFields.Add(f);
                if (textOnly && !f.IsText) continue;
                if (!string.IsNullOrEmpty(fq) &&
                    !f.Path.Contains(fq, StringComparison.OrdinalIgnoreCase) &&
                    !(f.Comment?.Contains(fq, StringComparison.OrdinalIgnoreCase) ?? false))
                    continue;

                _filteredFields.Add(f);
                string val = FieldCodec.Format(item, f, _currentList.Schema);
                string desc = FieldCodec.Describe(item, f, _currentList.Schema, _repo);
                string note = string.Join(" | ", new[] { desc, f.Comment }.Where(x => !string.IsNullOrWhiteSpace(x)));
                string typeText = f.Kind switch
                {
                    FieldKind.WStr => $"wstr[{f.Length}]",
                    FieldKind.Str => $"str[{f.Length}]",
                    FieldKind.Bytes => $"bytes[{f.Length}]",
                    _ => FieldCodec.EffectiveKind(item, f, _currentList.Schema).ToString().ToLowerInvariant(),
                };
                _fieldsGrid.Rows.Add($"0x{f.Offset:X4}", f.Group ?? "", f.Path, val, typeText, note);
            }

            foreach (var tf in _textFields)
            {
                string val = FieldCodec.GetText(item, tf);
                int max = FieldCodec.MaxTextLength(tf);
                _textFieldsGrid.Rows.Add(tf.Path, $"{val.Length}/{max}", val);
            }

            _hexBox.Text = HexFormatter.Format(item);
            SyncQuickEditorFromSelection();
        }
        finally
        {
            _syncingUi = false;
        }
        AfterInspectorRefresh();
    }

    private void OnFieldSelectionChanged()
    {
        if (_syncingUi || _currentList == null || _currentItemIndex < 0 || _fieldsGrid.CurrentRow == null) return;
        int row = _fieldsGrid.CurrentRow.Index;
        if (row < 0 || row >= _filteredFields.Count) return;

        var f = _filteredFields[row];
        var item = _currentList.Items[_currentItemIndex];
        _btnEnumMask.Enabled = f.Enum != null && !f.IsText && _repo.Enums.ContainsKey(f.Enum);
        _btnJumpRef.Enabled = f.Refs is { Count: > 0 } && !f.IsText;

        if (f.IsText)
        {
            int curLen = FieldCodec.GetText(item, f).Length;
            int max = FieldCodec.MaxTextLength(f);
            _lblCharCount.Text = L.F("Length: {0}/{1} characters", curLen, max);
        }
        else _lblCharCount.Text = $"Byte offset: 0x{f.Offset:X} ({f.ByteSize}B)";

        // Highlight field bytes in Hex View
        if (f.Offset + f.ByteSize <= HexFormatter.MaxBytes)
        {
            _hexBox.SelectAll();
            _hexBox.SelectionBackColor = _hexBox.BackColor;
            int start = HexFormatter.HexPos(f.Offset);
            int end = HexFormatter.HexPos(f.Offset + f.ByteSize - 1) + 2;
            if (start >= 0 && end <= _hexBox.TextLength)
            {
                _hexBox.Select(start, end - start);
                _hexBox.SelectionBackColor = Theme.Back(Color.LightGoldenrodYellow);
                _hexBox.Select(start, 0);
            }
        }
    }

    private void OnFieldCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_syncingUi || _currentList == null || _currentItemIndex < 0) return;
        if (e.RowIndex < 0 || e.RowIndex >= _filteredFields.Count || e.ColumnIndex != 3) return;

        var f = _filteredFields[e.RowIndex];
        var item = _currentList.Items[_currentItemIndex];
        string newVal = _fieldsGrid.Rows[e.RowIndex].Cells[3].Value?.ToString() ?? "";

        try
        {
            if (FieldCodec.SetFromString(item, f, _currentList.Schema, newVal))
            {
                MarkRecordDirty(item);
                _syncingUi = true;
                _fieldsGrid.Rows[e.RowIndex].Cells[5].Value = string.Join(" | ",
                    new[] { FieldCodec.Describe(item, f, _currentList.Schema, _repo), f.Comment }.Where(x => !string.IsNullOrWhiteSpace(x)));
                _hexBox.Text = HexFormatter.Format(item);
                _syncingUi = false;
                if (_itemsGrid.CurrentRow != null) _itemsGrid.InvalidateRow(_itemsGrid.CurrentRow.Index);
            }
        }
        catch (Exception ex)
        {
            Msg.Show(L.F("Invalid value for '{0}':\n{1}", f.Path, ex.Message), L.T("Input error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _syncingUi = true;
            _fieldsGrid.Rows[e.RowIndex].Cells[3].Value = FieldCodec.Format(item, f, _currentList.Schema);
            _syncingUi = false;
        }
    }

    private void SyncQuickEditorFromSelection()
    {
        if (_currentList == null || _currentItemIndex < 0 || _textFieldsGrid.CurrentRow == null ||
            _textFieldsGrid.CurrentRow.Index >= _textFields.Count)
        {
            _quickTextEditor.Text = "";
            _btnQuickApplyNext.Enabled = false;
            _lblQuickCounter.Text = L.T("No text field.");
            return;
        }

        var tf = _textFields[_textFieldsGrid.CurrentRow.Index];
        var item = _currentList.Items[_currentItemIndex];
        _syncingUi = true;
        _quickTextEditor.Text = FieldCodec.GetText(item, tf);
        _syncingUi = false;
        _btnQuickApplyNext.Enabled = true;
        UpdateQuickCounter();
    }

    private void UpdateQuickCounter()
    {
        if (_currentList == null || _currentItemIndex < 0 || _textFieldsGrid.CurrentRow == null ||
            _textFieldsGrid.CurrentRow.Index >= _textFields.Count) return;

        var tf = _textFields[_textFieldsGrid.CurrentRow.Index];
        int max = FieldCodec.MaxTextLength(tf);
        int len = _quickTextEditor.Text.Length;
        _lblQuickCounter.Text = L.F("[{0}] Length: {1} / {2} characters", tf.Path, len, max);
        _lblQuickCounter.ForeColor = len > max ? Color.Firebrick : Color.DarkGreen;
    }

    private void OnQuickTextGridCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_syncingUi || _currentList == null || _currentItemIndex < 0 || e.RowIndex < 0 || e.RowIndex >= _textFields.Count) return;
        var tf = _textFields[e.RowIndex];
        var item = _currentList.Items[_currentItemIndex];
        string txt = _textFieldsGrid.Rows[e.RowIndex].Cells[2].Value?.ToString() ?? "";
        try
        {
            FieldCodec.SetText(item, tf, txt);
            MarkRecordDirty(item);
            _syncingUi = true;
            _textFieldsGrid.Rows[e.RowIndex].Cells[1].Value = $"{txt.Length}/{FieldCodec.MaxTextLength(tf)}";
            _quickTextEditor.Text = txt;
            _syncingUi = false;
            if (_itemsGrid.CurrentRow != null) _itemsGrid.InvalidateRow(_itemsGrid.CurrentRow.Index);
        }
        catch (Exception ex)
        {
            Msg.Show(ex.Message, L.T("Too long"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _syncingUi = true;
            _textFieldsGrid.Rows[e.RowIndex].Cells[2].Value = FieldCodec.GetText(item, tf);
            _syncingUi = false;
        }
    }

    private void ApplyQuickTextAndNext()
    {
        if (_currentList == null || _currentItemIndex < 0 || _textFieldsGrid.CurrentRow == null ||
            _textFieldsGrid.CurrentRow.Index >= _textFields.Count) return;

        int tfIdx = _textFieldsGrid.CurrentRow.Index;
        var tf = _textFields[tfIdx];
        var item = _currentList.Items[_currentItemIndex];
        string newTxt = _quickTextEditor.Text;

        try
        {
            FieldCodec.SetText(item, tf, newTxt);
            MarkRecordDirty(item);
            RefreshInspector();
            if (_itemsGrid.CurrentRow != null) _itemsGrid.InvalidateRow(_itemsGrid.CurrentRow.Index);

            // Move to next item in grid
            if (_itemsGrid.CurrentRow != null && _itemsGrid.CurrentRow.Index + 1 < _itemsGrid.RowCount)
            {
                int nextRow = _itemsGrid.CurrentRow.Index + 1;
                _itemsGrid.CurrentCell = _itemsGrid.Rows[nextRow].Cells[0];
                _itemsGrid.Rows[nextRow].Selected = true;
                _quickTextEditor.Focus();
            }
        }
        catch (Exception ex)
        {
            Msg.Show(ex.Message, L.T("Exceeds the allowed length"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenEnumMaskPicker()
    {
        if (_currentList == null || _currentItemIndex < 0 || _fieldsGrid.CurrentRow == null) return;
        var f = _filteredFields[_fieldsGrid.CurrentRow.Index];
        if (f.Enum == null || !_repo.Enums.TryGetValue(f.Enum, out var enumDef)) return;

        var item = _currentList.Items[_currentItemIndex];
        long cur = FieldCodec.ReadInteger(item, f);
        using var dlg = new BitmaskEnumDialog(f.Path, enumDef, cur);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            if (FieldCodec.SetFromString(item, f, _currentList.Schema, dlg.ResultValue.ToString()))
            {
                MarkRecordDirty(item);
                RefreshInspector();
                if (_itemsGrid.CurrentRow != null) _itemsGrid.InvalidateRow(_itemsGrid.CurrentRow.Index);
            }
        }
    }

    private void JumpToReference()
    {
        if (_ef == null || _currentList == null || _currentItemIndex < 0 || _fieldsGrid.CurrentRow == null) return;
        var f = _filteredFields[_fieldsGrid.CurrentRow.Index];
        if (f.Refs == null || f.Refs.Count == 0) return;

        long targetId = FieldCodec.ReadInteger(_currentList.Items[_currentItemIndex], f);
        if (targetId <= 0) { Msg.Show(L.T("The referenced ID is 0."), L.T("Notice")); return; }

        foreach (var refStruct in f.Refs)
        {
            var targetList = _ef.Lists.FirstOrDefault(l => l.Name.Equals(refStruct, StringComparison.OrdinalIgnoreCase));
            if (targetList == null) continue;
            for (int i = 0; i < targetList.Items.Count; i++)
            {
                if (targetList.GetId(i) == targetId)
                {
                    JumpTo(false, targetList.Index, i, null);
                    return;
                }
            }
        }
        Msg.Show(L.F("ID {0} not found in [{1}].", targetId, string.Join(", ", f.Refs)), L.T("Not found"));
    }

    private void JumpTo(bool isDialog, int listIndex, int itemIndex, string? fieldPath)
    {
        if (_ef == null) return;
        if (isDialog)
        {
            SelectListEntry(e => e.IsTalk);
            SelectTalkDialogIndex(itemIndex);
            return;
        }
        if (!SelectListEntry(e => !e.IsTalk && e.List!.Index == listIndex))
        {
            _listSearchBox.Text = "";
            _listFilterCombo.SelectedIndex = 0;
            SelectListEntry(e => !e.IsTalk && e.List!.Index == listIndex);
        }

        int rowIdx = _filteredItemIndices.IndexOf(itemIndex);
        if (rowIdx < 0)
        {
            _itemSearchBox.Text = "";
            _chkItemCjkOnly.Checked = false;
            rowIdx = _filteredItemIndices.IndexOf(itemIndex);
        }
        if (rowIdx >= 0 && rowIdx < _itemsGrid.RowCount)
        {
            _itemsGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, rowIdx - 3);
            _itemsGrid.CurrentCell = _itemsGrid.Rows[rowIdx].Cells[0];
            _itemsGrid.Rows[rowIdx].Selected = true;
            if (!string.IsNullOrEmpty(fieldPath))
            {
                for (int r = 0; r < _filteredFields.Count; r++)
                {
                    if (_filteredFields[r].Path == fieldPath)
                    {
                        _fieldsGrid.CurrentCell = _fieldsGrid.Rows[r].Cells[3];
                        break;
                    }
                }
            }
        }
    }

    private bool SelectListEntry(Func<ListEntry, bool> pred)
    {
        for (int i = 0; i < _visibleLists.Count; i++)
        {
            if (pred(_visibleLists[i]))
            {
                _listsView.Items[i].Selected = true;
                _listsView.EnsureVisible(i);
                return true;
            }
        }
        return false;
    }

    private void CloneSelectedItem()
    {
        if (_currentList == null || _currentItemIndex < 0) return;
        var src = _currentList.Items[_currentItemIndex];
        var copy = (byte[])src.Clone();

        if (_currentList.Schema.IdField is { IsText: false } idField && idField.ByteSize >= 4)
        {
            long maxId = 0;
            for (int i = 0; i < _currentList.Items.Count; i++)
                maxId = Math.Max(maxId, _currentList.GetId(i));
            FieldCodec.SetFromString(copy, idField, _currentList.Schema, (maxId + 1).ToString());
        }

        _currentList.Items.Add(copy);
        MarkRecordDirty(copy);
        _itemSearchBox.Text = "";
        _chkItemCjkOnly.Checked = false;
        RefreshFilteredItems();
        int newRow = _itemsGrid.RowCount - 1;
        if (newRow >= 0)
        {
            _itemsGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, newRow - 3);
            _itemsGrid.CurrentCell = _itemsGrid.Rows[newRow].Cells[0];
            _itemsGrid.Rows[newRow].Selected = true;
        }
    }

    private void DeleteSelectedItem()
    {
        if (_currentList == null || _currentItemIndex < 0) return;
        long id = _currentList.GetId(_currentItemIndex);
        string name = _currentList.GetName(_currentItemIndex);
        if (Msg.Show(L.F("Delete item ID={0} '{1}' from {2}?", id, name, _currentList.Name), L.T("Confirm delete"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _currentList.Items.RemoveAt(_currentItemIndex);
        _dirty = true;
        UpdateWindowTitle();
        RefreshFilteredItems();
        UpdateSummaryStatus();
    }

    private void MarkRecordDirty(byte[] record)
    {
        _dirty = true;
        _dirtyRecords.Add(record);
        UpdateWindowTitle();
        UpdateSummaryStatus();
    }

    private bool SaveFile(bool saveAs)
    {
        if (_ef == null) return false;
        string? target = _ef.Path;
        if (saveAs || string.IsNullOrEmpty(target))
        {
            using var sfd = new SaveFileDialog
            {
                Filter = "elements.data|elements.data;*.data|" + L.T("All files") + "|*.*",
                FileName = Path.GetFileName(target ?? "elements.data"),
                InitialDirectory = target != null ? Path.GetDirectoryName(target) : null,
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return false;
            target = sfd.FileName;
        }

        try
        {
            var bak = _ef.Save(target, backup: true);
            _baselineEf = null;
            if (_elChanges.Visible) BeginInvoke(() => _elChanges.Reload());
            _recent.Saved("elements", target);
            if (saveAs) _recent.Opened("elements", target);
            _dirty = false;
            _dirtyRecords.Clear();
            _dirtyDialogs.Clear();
            UpdateWindowTitle();
            UpdateSummaryStatus();
            _itemsGrid.Invalidate();
            Msg.Show(L.F("Saved successfully:\n{0}", target) + (bak != null ? "\n\n" + L.F("Old version backed up to:\n{0}", bak) : ""),
                L.T("Saved"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            Msg.Show(L.F("Error saving file:\n{0}", ex.Message), L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void ShowGlobalSearch()
    {
        if (_ef == null) return;
        var dlg = new GlobalSearchDialog(_ef, hit => JumpTo(hit.IsDialog, hit.ListIndex, hit.ItemIndex, hit.FieldPath));
        dlg.Show(this);
    }

    private void ShowBatchReplace()
    {
        if (_ef == null) return;
        int? listIdx = _inTalkMode ? -1 : _currentList?.Index;
        string listLabel = _inTalkMode ? "NPC Dialogs" : (_currentList != null ? $"[{_currentList.Index}] {_currentList.Name}" : "");
        using var dlg = new BatchReplaceDialog(_ef, listIdx, listLabel);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.AppliedCount > 0)
        {
            _dirty = true;
            UpdateWindowTitle();
            UpdateSummaryStatus();
            if (_inTalkMode) PopulateTalkDialogs();
            else { _itemsGrid.Invalidate(); RefreshInspector(); }
        }
    }

    private void ExportCsv(bool glossary, bool onlyCjk, bool currentListOnly)
    {
        if (_ef == null) return;
        ISet<int>? lists = currentListOnly && _currentList != null ? new HashSet<int> { _currentList.Index } : null;
        if (currentListOnly && _currentList == null && !_inTalkMode)
        {
            Msg.Show(L.T("Select a list first."), L.T("Notice"));
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Filter = "CSV (UTF-8 Excel)|*.csv",
            FileName = glossary ? "viethoa_glossary.csv" : (currentListOnly && _currentList != null ? $"list_{_currentList.Index}_{_currentList.Name}.csv" : "viethoa_all.csv"),
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        var entries = TextExchange.Enumerate(_ef, lists, dialogs: !currentListOnly || _inTalkMode);
        int n = glossary
            ? TextExchange.ExportGlossary(entries, sfd.FileName, onlyCjk)
            : TextExchange.ExportDetailed(entries, sfd.FileName, onlyCjk);
        Msg.Show(L.F("Exported {0:N0} rows to:\n{1}\n\nTip: the file is saved as UTF-8 with BOM — it opens directly in Excel or Google Sheets without garbled Vietnamese/Chinese characters.", n, sfd.FileName),
            L.T("CSV exported"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ImportCsv()
    {
        if (_ef == null) return;
        using var ofd = new OpenFileDialog { Filter = "CSV|*.csv|" + L.T("All files") + "|*.*", Title = L.T("Select the translated CSV file") };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        var rep = TextExchange.Import(_ef, ofd.FileName);
        if (rep.Applied > 0)
        {
            _dirty = true;
            UpdateWindowTitle();
            UpdateSummaryStatus();
            if (_inTalkMode) PopulateTalkDialogs();
            else { _itemsGrid.Invalidate(); RefreshInspector(); }
        }
        using var dlg = new ImportReportDialog(rep);
        dlg.ShowDialog(this);
    }

    private void ExportCurrentListJson()
    {
        if (_ef == null || _currentList == null) return;
        using var sfd = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"{_currentList.Name}.json" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        ElementsOps.ExportListToJson(_currentList, _ef, sfd.FileName);
        Msg.Show(L.F("Exported {0:N0} records to {1}.", _currentList.Items.Count, sfd.FileName), L.T("Success"));
    }

    private void CompareWithBaseline()
    {
        if (_ef == null) return;
        using var ofd = new OpenFileDialog { Filter = "elements.data|elements.data;*.data;*.bak_*|" + L.T("All files") + "|*.*", Title = L.T("Select the original elements.data to compare with") };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        var baseline = ElementsFile.Load(ofd.FileName, _repo);
        var diffs = ElementsOps.Diff(_ef, baseline);
        var dlg = new DiffReportDialog(diffs, ofd.FileName, d =>
        {
            if (d.Kind == "-" || _ef.Lists.FirstOrDefault(l => l.Index == d.ListIndex) is not { } lst) return;
            for (int i = 0; i < lst.Items.Count; i++)
            {
                if (lst.GetId(i) == d.Id) { JumpTo(false, d.ListIndex, i, null); break; }
            }
        });
        dlg.Show(this);
    }

    private void ToggleTheme()
    {
        _settings.DarkTheme = !Theme.Dark;
        _settings.Save();
        if (_dirty)
        {
            Msg.Show(L.T("Theme choice saved. Save your file, then restart the program to apply it."), L.T("Theme"));
            return;
        }
        if (Msg.Show(L.T("Restart the program to change the theme?"), L.T("Theme"), MessageBoxButtons.YesNo) == DialogResult.Yes)
            Application.Restart();
    }

    private void VerifyRoundTrip()
    {
        if (_ef == null || string.IsNullOrEmpty(_ef.Path) || !File.Exists(_ef.Path)) return;
        var orig = File.ReadAllBytes(_ef.Path);
        var re = _ef.ToBytes();
        if (orig.Length == re.Length && orig.AsSpan().SequenceEqual(re))
            Msg.Show(L.F("100% MATCH ({0:N0} bytes identical to the original file on disk).", orig.Length), L.T("Verification passed"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            Msg.Show(L.F("The file in memory ({0:N0} bytes) differs from the original on disk ({1:N0} bytes) — normal if you have just edited data.", re.Length, orig.Length), L.T("Information"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
