using ElementsCore;

namespace ElementsEditor;

public sealed partial class MainForm
{
    private TextBox _talkSearchBox = null!;
    private CheckBox _chkTalkCjkOnly = null!;
    private DataGridView _talkDialogsGrid = null!;
    private TextBox _talkNameBox = null!;
    private Label _talkNameCounter = null!;
    private TreeView _talkWinTree = null!;
    private TextBox _talkWinTextBox = null!;
    private DataGridView _talkOptionsGrid = null!;

    private readonly List<int> _filteredDialogIndices = new();
    private TalkDialog? _currentDialog;
    private TalkWindow? _currentWindow;

    private Panel BuildTalkProcPanel()
    {
        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 360 };
        var leftSplitter = new Splitter { Dock = DockStyle.Left, Width = 5, BackColor = SystemColors.ControlDark };
        var rightPanel = new Panel { Dock = DockStyle.Fill };

        // Left: Dialogs list
        var leftTop = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4) };
        _talkSearchBox = new TextBox { Left = 4, Top = 6, Width = 240, PlaceholderText = L.T("Search ID / name / dialog text...") };
        _chkTalkCjkOnly = new CheckBox { Text = L.T("CJK only"), Left = 250, Top = 8, Width = 75 };
        leftTop.Controls.AddRange([_talkSearchBox, _chkTalkCjkOnly]);

        _talkDialogsGrid = CreateFastGrid();
        _talkDialogsGrid.ReadOnly = true;
        _talkDialogsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _talkDialogsGrid.MultiSelect = false;
        _talkDialogsGrid.Columns.Add("tdRow", "#");
        _talkDialogsGrid.Columns.Add("tdId", "ID");
        _talkDialogsGrid.Columns.Add("tdName", L.T("Dialog name"));
        _talkDialogsGrid.Columns.Add("tdWins", L.T("Windows"));
        _talkDialogsGrid.Columns[0].Width = 42;
        _talkDialogsGrid.Columns[1].Width = 55;
        _talkDialogsGrid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _talkDialogsGrid.Columns[3].Width = 55;
        leftPanel.Controls.Add(_talkDialogsGrid);
        leftPanel.Controls.Add(leftTop);

        // Right: Dialog Name + Windows Tree + Window Text + Options
        var nameBar = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(6) };
        nameBar.Controls.Add(new Label { Text = L.T("Dialog name:"), Left = 6, Top = 10, Width = 78 });
        _talkNameBox = new TextBox { Left = 88, Top = 7, Width = 340 };
        _talkNameCounter = new Label { Left = 436, Top = 10, AutoSize = true, Text = "0/63" };
        nameBar.Controls.AddRange([_talkNameBox, _talkNameCounter]);

        var treePanel = new Panel { Dock = DockStyle.Top, Height = 210 };
        var treeSplitter = new Splitter { Dock = DockStyle.Top, Height = 5, BackColor = SystemColors.ControlDark };
        _talkWinTree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        treePanel.Controls.Add(_talkWinTree);

        var winTextHost = new Panel { Dock = DockStyle.Top, Height = 200, Padding = new Padding(4) };
        var textSplitter = new Splitter { Dock = DockStyle.Top, Height = 5, BackColor = SystemColors.ControlDark };
        winTextHost.Controls.Add(new Label { Dock = DockStyle.Top, Height = 22, Text = L.T("Window text (UTF-16LE, supports colour codes ^ff6000):") });
        _talkWinTextBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 10.5f) };
        winTextHost.Controls.Add(_talkWinTextBox);
        _talkWinTextBox.BringToFront();

        var optHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        optHost.Controls.Add(new Label { Dock = DockStyle.Top, Height = 22, Text = L.T("Options (max 63 characters):") });
        _talkOptionsGrid = CreateFastGrid();
        _talkOptionsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "opId", HeaderText = "ID", Width = 60, ReadOnly = true });
        _talkOptionsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "opText", HeaderText = L.T("Option text"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = false });
        _talkOptionsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "opParam", HeaderText = "Param", Width = 90, ReadOnly = false });
        optHost.Controls.Add(_talkOptionsGrid);
        _talkOptionsGrid.BringToFront();

        rightPanel.Controls.Add(optHost);
        rightPanel.Controls.Add(textSplitter);
        rightPanel.Controls.Add(winTextHost);
        rightPanel.Controls.Add(treeSplitter);
        rightPanel.Controls.Add(treePanel);
        rightPanel.Controls.Add(nameBar);

        _talkSearchBox.TextChanged += (_, _) => PopulateTalkDialogs();
        _chkTalkCjkOnly.CheckedChanged += (_, _) => PopulateTalkDialogs();
        _talkDialogsGrid.SelectionChanged += (_, _) => OnTalkDialogSelected();
        _talkNameBox.Leave += (_, _) => ApplyTalkName();
        _talkNameBox.TextChanged += (_, _) => _talkNameCounter.Text = $"{_talkNameBox.Text.Length}/63";
        _talkWinTree.AfterSelect += (_, _) => OnTalkWindowSelected();
        _talkWinTextBox.Leave += (_, _) => ApplyTalkWindowText();
        _talkOptionsGrid.CellValueChanged += OnTalkOptionChanged;

        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(rightPanel);
        host.Controls.Add(leftSplitter);
        host.Controls.Add(leftPanel);
        return host;
    }

    private static bool DialogHasCjk(TalkDialog d)
    {
        if (TextExchange.ContainsCjk(d.Name)) return true;
        foreach (var w in d.Windows)
        {
            if (TextExchange.ContainsCjk(w.Text)) return true;
            foreach (var o in w.Options)
                if (TextExchange.ContainsCjk(o.Text)) return true;
        }
        return false;
    }

    private void PopulateTalkDialogs()
    {
        _filteredDialogIndices.Clear();
        _talkDialogsGrid.Rows.Clear();
        if (_ef?.Dialogs == null) return;

        string q = _talkSearchBox.Text.Trim();
        bool cjk = _chkTalkCjkOnly.Checked;
        for (int i = 0; i < _ef.Dialogs.Count; i++)
        {
            var d = _ef.Dialogs[i];
            if (cjk && !DialogHasCjk(d)) continue;
            if (!string.IsNullOrEmpty(q))
            {
                bool match = d.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)
                          || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                          || d.Windows.Any(w => w.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                                             || w.Options.Any(o => o.Text.Contains(q, StringComparison.OrdinalIgnoreCase)));
                if (!match) continue;
            }
            _filteredDialogIndices.Add(i);
            _talkDialogsGrid.Rows.Add(i, d.Id, d.Name, d.Windows.Count);
        }

        if (_talkDialogsGrid.Rows.Count > 0)
        {
            _talkDialogsGrid.Rows[0].Selected = true;
            OnTalkDialogSelected();
        }
    }

    private void SelectTalkDialogIndex(int dialogIndex)
    {
        int row = _filteredDialogIndices.IndexOf(dialogIndex);
        if (row < 0)
        {
            _talkSearchBox.Text = "";
            _chkTalkCjkOnly.Checked = false;
            row = _filteredDialogIndices.IndexOf(dialogIndex);
        }
        if (row >= 0 && row < _talkDialogsGrid.Rows.Count)
        {
            _talkDialogsGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, row - 3);
            _talkDialogsGrid.CurrentCell = _talkDialogsGrid.Rows[row].Cells[0];
            _talkDialogsGrid.Rows[row].Selected = true;
        }
    }

    private void OnTalkDialogSelected()
    {
        if (_ef?.Dialogs == null || _talkDialogsGrid.CurrentRow == null) return;
        int row = _talkDialogsGrid.CurrentRow.Index;
        if (row < 0 || row >= _filteredDialogIndices.Count) return;

        _currentDialog = _ef.Dialogs[_filteredDialogIndices[row]];
        _syncingUi = true;
        _talkNameBox.Text = _currentDialog.Name;
        _talkWinTree.BeginUpdate();
        _talkWinTree.Nodes.Clear();

        var nodesById = new Dictionary<int, TreeNode>();
        foreach (var w in _currentDialog.Windows)
        {
            string snip = w.Text.Replace("\r", " ").Replace("\n", " ");
            if (snip.Length > 60) snip = snip[..60] + "...";
            var node = new TreeNode($"[Win {w.Id}] {snip}") { Tag = w };
            nodesById[w.Id] = node;
        }
        foreach (var w in _currentDialog.Windows)
        {
            var node = nodesById[w.Id];
            if (w.ParentId >= 0 && nodesById.TryGetValue(w.ParentId, out var parentNode) && parentNode != node)
                parentNode.Nodes.Add(node);
            else
                _talkWinTree.Nodes.Add(node);
        }
        _talkWinTree.ExpandAll();
        _talkWinTree.EndUpdate();
        _syncingUi = false;

        if (_talkWinTree.Nodes.Count > 0) _talkWinTree.SelectedNode = _talkWinTree.Nodes[0];
        else { _currentWindow = null; _talkWinTextBox.Text = ""; _talkOptionsGrid.Rows.Clear(); }
    }

    private void OnTalkWindowSelected()
    {
        if (_talkWinTree.SelectedNode?.Tag is not TalkWindow w) return;
        _currentWindow = w;
        _syncingUi = true;
        _talkWinTextBox.Text = w.Text;
        _talkOptionsGrid.Rows.Clear();
        foreach (var o in w.Options)
            _talkOptionsGrid.Rows.Add(o.Id, o.Text, o.Param);
        _syncingUi = false;
    }

    private void ApplyTalkName()
    {
        if (_syncingUi || _currentDialog == null) return;
        string val = _talkNameBox.Text;
        if (val == _currentDialog.Name) return;
        try
        {
            _currentDialog.Name = val;
            _dirty = true;
            _dirtyDialogs.Add(_currentDialog);
            UpdateWindowTitle();
            UpdateSummaryStatus();
            if (_talkDialogsGrid.CurrentRow != null) _talkDialogsGrid.CurrentRow.Cells[2].Value = val;
        }
        catch (Exception ex)
        {
            Msg.Show(ex.Message, L.T("Length error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _talkNameBox.Text = _currentDialog.Name;
        }
    }

    private void ApplyTalkWindowText()
    {
        if (_syncingUi || _currentDialog == null || _currentWindow == null) return;
        string val = _talkWinTextBox.Text;
        if (val == _currentWindow.Text) return;
        _currentWindow.Text = val;
        _dirty = true;
        _dirtyDialogs.Add(_currentDialog);
        UpdateWindowTitle();
        UpdateSummaryStatus();
        if (_talkWinTree.SelectedNode != null)
        {
            string snip = val.Replace("\r", " ").Replace("\n", " ");
            if (snip.Length > 60) snip = snip[..60] + "...";
            _talkWinTree.SelectedNode.Text = $"[Win {_currentWindow.Id}] {snip}";
        }
    }

    private void OnTalkOptionChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_syncingUi || _currentDialog == null || _currentWindow == null) return;
        if (e.RowIndex < 0 || e.RowIndex >= _currentWindow.Options.Count) return;

        var opt = _currentWindow.Options[e.RowIndex];
        var row = _talkOptionsGrid.Rows[e.RowIndex];
        try
        {
            if (e.ColumnIndex == 1)
            {
                string txt = row.Cells[1].Value?.ToString() ?? "";
                if (txt == opt.Text) return;
                opt.Text = txt;
            }
            else if (e.ColumnIndex == 2)
            {
                int param = int.Parse(row.Cells[2].Value?.ToString() ?? "0");
                if (param == opt.Param) return;
                opt.Param = param;
            }
            _dirty = true;
            _dirtyDialogs.Add(_currentDialog);
            UpdateWindowTitle();
            UpdateSummaryStatus();
        }
        catch (Exception ex)
        {
            Msg.Show(ex.Message, L.T("Input error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _syncingUi = true;
            row.Cells[1].Value = opt.Text;
            row.Cells[2].Value = opt.Param;
            _syncingUi = false;
        }
    }
}
