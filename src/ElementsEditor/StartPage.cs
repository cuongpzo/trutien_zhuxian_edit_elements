using ElementsCore;
using ElementsCore.Formats;

namespace ElementsEditor;

/// <summary>
/// "Open a file" landing card of a module tab (JD IDE style): choose a file, reopen the last one,
/// the recent list (Documents\TruTien Elements Editor) and the matching files found in the client.
/// Files can also be dropped anywhere on the page.
/// </summary>
public sealed class StartPage : Panel
{
    private readonly ModuleInfo _m;
    private readonly RecentStore _recent;
    private readonly Func<string?> _clientRoot;
    private readonly Action<string> _open;
    private readonly Action _browse;
    private readonly Panel _card;
    private readonly ListView _recentList;
    private readonly ListView _clientList;
    private readonly TextBox _clientFilter;
    private readonly Button _btnReopen;
    private readonly Label _clientHeader;
    private List<string> _clientFiles = new();

    public StartPage(ModuleInfo m, RecentStore recent, Func<string?> clientRoot, Action<string> open, Action browse)
    {
        _m = m; _recent = recent; _clientRoot = clientRoot; _open = open; _browse = browse;
        Dock = DockStyle.Fill;
        AllowDrop = true;
        AutoScroll = true;

        _card = new Panel { Width = 680, Height = 600 };
        Controls.Add(_card);

        var logo = new PictureBox { Image = AppIcon.Logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64), BackColor = Color.Transparent };
        var title = new Label { Text = L.F("Open {0}", string.Join(" / ", m.Patterns.Take(2))), Font = new Font("Segoe UI", 14F, FontStyle.Bold), AutoSize = false, Height = 32, TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false };
        var desc = new Label
        {
            Text = m.Description + (m.ServerSide ? "\n" + L.T("This is a server-side file (on the server, not in the client) – copy it to this computer, then open it.") : "")
                 + "\n" + L.T("Drag and drop a file onto this window, or choose a file."),
            ForeColor = Color.DimGray, AutoSize = false, Height = 58, TextAlign = ContentAlignment.TopCenter, UseMnemonic = false,
        };
        var btnChoose = new Button { Text = L.T("Choose file…"), Width = 130, Height = 32 };
        btnChoose.Click += (_, _) => _browse();
        _btnReopen = new Button { Height = 32, AutoSize = false, Width = 300, AutoEllipsis = true };
        _btnReopen.Click += (_, _) => { if (_btnReopen.Tag is string p) _open(p); };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Height = 40, WrapContents = false };
        buttons.Controls.Add(btnChoose);
        buttons.Controls.Add(_btnReopen);

        var recentHeader = Header(L.T("RECENT  ·  kept in Documents\\TruTien Elements Editor"));
        _recentList = MakeList();
        _recentList.Columns.Add(L.T("File"), 200);
        _recentList.Columns.Add(L.T("Folder"), 290);
        _recentList.Columns.Add(L.T("Opened"), 120);

        _clientHeader = Header(L.T("IN THE CLIENT FOLDER"));
        _clientFilter = new TextBox { PlaceholderText = L.T("Filter by name / folder…"), Height = 26 };
        _clientFilter.TextChanged += (_, _) => FillClientList();
        _clientList = MakeList();
        _clientList.Columns.Add(L.T("File"), 200);
        _clientList.Columns.Add(L.T("Folder"), 330);
        _clientList.Columns.Add(L.T("Size"), 80, HorizontalAlignment.Right);

        _recentList.Height = 150;
        _clientList.Height = 170;
        _stack = [logo, title, desc, buttons, recentHeader, _recentList, _clientHeader, _clientFilter, _clientList];
        _card.Controls.AddRange(_stack);
        Relayout();

        Resize += (_, _) => Center();
        _clientFilter.ParentChanged += (_, _) => Relayout(); // wrapped into a rounded host by the theme
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) _open(f[0]); };
        VisibleChanged += (_, _) => { if (Visible) Refresh2(); };
        Refresh2();
    }

    private static Label Header(string text) => new()
    {
        Text = text, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Color.DimGray, Height = 20, AutoSize = false,
        TextAlign = ContentAlignment.BottomLeft, UseMnemonic = false,
    };

    private ListView MakeList()
    {
        var lv = new ListView { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.FixedSingle };
        lv.ItemActivate += (_, _) => { if (lv.SelectedItems.Count > 0 && lv.SelectedItems[0].Tag is string p) _open(p); };
        return lv;
    }

    private Control[] _stack = [];
    private bool _showFilter, _showClient = true;

    /// <summary>Stacks the card's rows top-down (the theme may have moved a TextBox into a rounded host).</summary>
    private void Relayout()
    {
        const int pad = 24;
        int inner = _card.Width - 2 * pad, y = 16;
        foreach (var c0 in _stack)
        {
            Control c = c0.Parent is RoundBox rb ? rb : c0;
            if (c0 == _clientFilter && !_showFilter || c0 == _clientList && !_showClient) continue;
            if (c0 is PictureBox) { c.SetBounds(pad + (inner - c.Width) / 2, y, c.Width, c.Height); }
            else if (c0 is FlowLayoutPanel f)
            {
                int w = f.Controls.Cast<Control>().Where(x => x.Visible).Sum(x => x.Width + x.Margin.Horizontal);
                c.SetBounds(pad + Math.Max(0, (inner - w) / 2), y, Math.Min(inner, w + 4), c.Height);
            }
            else c.SetBounds(pad, y, inner, c.Height);
            y = c.Bottom + (c0 is Label ? 4 : 10);
        }
        _card.Height = y + 12;
        Center();
    }

    private void Center()
    {
        _card.Left = Math.Max(10, (ClientSize.Width - _card.Width) / 2);
        _card.Top = Math.Max(10, (ClientSize.Height - _card.Height) / 2);
    }

    /// <summary>Reloads the recent list and the client file list.</summary>
    public void Refresh2()
    {
        var rec = _recent.Recent(_m.Key);
        _recentList.BeginUpdate();
        _recentList.Items.Clear();
        foreach (var e in rec)
        {
            bool exists = File.Exists(e.Path);
            var it = new ListViewItem([Path.GetFileName(e.Path), Path.GetDirectoryName(e.Path) ?? "", e.LastOpened.ToString("dd/MM HH:mm")]) { Tag = e.Path };
            if (!exists) { it.ForeColor = Color.Gray; it.ToolTipText = L.T("The file no longer exists"); }
            _recentList.Items.Add(it);
        }
        if (rec.Count == 0) _recentList.Items.Add(new ListViewItem([L.T("(none yet)"), "", ""]) { ForeColor = Color.Gray });
        _recentList.EndUpdate();

        var last = rec.FirstOrDefault(e => File.Exists(e.Path));
        _btnReopen.Visible = last != null;
        if (last != null)
        {
            _btnReopen.Text = L.F("Reopen {0}", Path.GetFileName(last.Path));
            _btnReopen.Tag = last.Path;
        }

        _clientFiles = FindClientFiles();
        var root = _clientRoot();
        _clientHeader.Text = _m.ClientFolder == null
            ? (_m.ServerSide ? L.T("IN THE CLIENT FOLDER  ·  (server-side file – not in the client)") : L.T("IN THE CLIENT FOLDER  ·  (no separate files – open any file with “Choose file…”)"))
            : L.F("IN THE CLIENT FOLDER  ·  {0}  ·  {1} files", root ?? L.T("client folder unknown – open elements.data first"), _clientFiles.Count);
        FillClientList();
    }

    private void FillClientList()
    {
        var root = _clientRoot() ?? "";
        var q = _clientFilter.Text.Trim();
        _clientList.BeginUpdate();
        _clientList.Items.Clear();
        foreach (var p in _clientFiles)
        {
            if (q.Length > 0 && p.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var dir = Path.GetDirectoryName(p) ?? "";
            if (root.Length > 0 && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)) dir = dir[root.Length..].TrimStart('\\');
            long len = 0;
            try { len = new FileInfo(p).Length; } catch { }
            _clientList.Items.Add(new ListViewItem([Path.GetFileName(p), dir, len.ToString("N0")]) { Tag = p });
            if (_clientList.Items.Count >= 2000) break;
        }
        _clientList.EndUpdate();
        // the theme may have moved the filter box into a rounded host: hide that host instead
        Control filterHost = _clientFilter.Parent is RoundBox rb ? rb : _clientFilter;
        _showFilter = _clientFiles.Count > 12;
        _showClient = _m.ClientFolder != null;
        filterHost.Visible = _showFilter;
        _clientList.Visible = _showClient;
        Relayout();
    }

    private List<string> FindClientFiles()
    {
        var root = _clientRoot();
        var list = new List<string>();
        if (root == null || _m.ClientFolder == null) return list;
        try
        {
            IEnumerable<string> dirs = _m.ClientFolder.EndsWith(@"\*")
                ? Directory.Exists(Path.Combine(root, _m.ClientFolder[..^2])) ? Directory.GetDirectories(Path.Combine(root, _m.ClientFolder[..^2])) : []
                : [Path.Combine(root, _m.ClientFolder)];
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                foreach (var pat in _m.Patterns.Take(2))
                    foreach (var f in Directory.GetFiles(d, pat))
                        if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
            }
        }
        catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}
