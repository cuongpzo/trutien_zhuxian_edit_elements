using System.Reflection;
using ElementsCore.Exchange;

namespace ElementsEditor;

/// <summary>One text cell that bulk Hán-Việt can rewrite.</summary>
public sealed class TextTarget
{
    public required string Group { get; init; }
    public required string Key { get; init; }
    public required string Field { get; init; }
    public required string Text { get; init; }
    /// <summary>Max length as the field counts it (chars / GBK bytes); int.MaxValue = no limit.</summary>
    public int Max { get; init; } = int.MaxValue;
    public Func<string, int> Measure { get; init; } = s => s.Length;
    /// <summary>GBK (str) field: cannot hold Vietnamese letters with diacritics.</summary>
    public bool Gbk { get; init; }
    /// <summary>Writes the new text; returns an error or null.</summary>
    public required Func<string, string?> Set { get; init; }
}

/// <summary>A choice of "where" for bulk Hán-Việt (current list, all lists, current table …).</summary>
public sealed record TextScope(string Title, Func<IEnumerable<TextTarget>> Enumerate);

/// <summary>Shared pieces of the Hán-Việt feature: dictionary cache, one-cell conversion, table targets.</summary>
public static class HanVietTools
{
    private static HanViet? _cached;
    /// <summary>Program settings (dictionary path), set by the main window.</summary>
    public static AppSettings Settings { get; set; } = new();
    public static HanVietCase LastMode { get; set; } = HanVietCase.EveryWord;

    public static HanViet? Dictionary(string? path, out string? error)
    {
        path = string.IsNullOrWhiteSpace(path) ? Settings.HanVietDict ?? HanViet.DefaultPath : path.Trim();
        error = null;
        if (_cached != null && _cached.Source == path) return _cached;
        try
        {
            _cached = HanViet.Load(path);
            if (Settings.HanVietDict != path) { Settings.HanVietDict = path; Settings.Save(); }
            return _cached;
        }
        catch (Exception ex) { error = $"Không đọc được từ điển phiên âm:\n{path}\n{ex.Message}"; return null; }
    }

    /// <summary>Converts one cell; returns a message for the status bar (or an error box text when it was skipped).</summary>
    public static bool ConvertOne(TextTarget t, out string message)
    {
        var hv = Dictionary(null, out var err);
        if (hv == null) { message = err!; return false; }
        if (t.Gbk) { message = $"{t.Field} là field GBK – không chứa được tiếng Việt có dấu."; return false; }
        if (!HanViet.HasCjk(t.Text)) { message = $"{t.Field} của {t.Key} không còn chữ Hán."; return false; }
        var s = hv.Convert(t.Text, LastMode, out int unk);
        int len = t.Measure(s);
        if (len > t.Max) { message = $"Bỏ qua: \"{s}\" dài {len}/{t.Max} – vượt độ dài của {t.Field}. Hãy sửa tay cho ngắn lại."; return false; }
        var e = t.Set(s);
        if (e != null) { message = e; return false; }
        message = $"{t.Key} · {t.Field}: {t.Text} → {s}" + (unk > 0 ? $"  (thiếu {unk} chữ trong từ điển)" : "");
        return true;
    }

    /// <summary>Text cells of a table, nested lists included ("windows.text"); <paramref name="onlyField"/> limits to one field name.</summary>
    public static IEnumerable<TextTarget> TableTargets(ElementsCore.Formats.RecordTable t, string? onlyField = null)
    {
        for (int r = 0; r < t.Rows.Count; r++)
        {
            var rec = t.Rows[r];
            foreach (var x in RecordTargets(t, rec, r))
                if (onlyField == null || x.Field == onlyField) yield return x;
        }
    }

    public static IEnumerable<TextTarget> RecordTargets(ElementsCore.Formats.RecordTable t, ElementsCore.Formats.Record rec, int index)
    {
        string key = t.KeyColumn >= 0 ? $"{t.Columns[t.KeyColumn].Name} {ElementsCore.Formats.RecordIO.Format(t.Columns[t.KeyColumn], rec.Values[t.KeyColumn])}" : $"#{index}";
        return CellTargets(t.Name, key, "", t.Columns, rec);
    }

    private static IEnumerable<TextTarget> CellTargets(string group, string key, string prefix, IReadOnlyList<ElementsCore.Formats.ColumnDef> cols, ElementsCore.Formats.Record rec)
    {
        for (int c = 0; c < cols.Count; c++)
        {
            var col = cols[c];
            if (col.ReadOnly) continue;
            if (col.Type == ElementsCore.Formats.ColType.Table && col.SubColumns != null)
            {
                var list = rec.Table(c);
                for (int i = 0; i < list.Count; i++)
                    foreach (var x in CellTargets(group, $"{key} · {col.Name}[{i}]", prefix + col.Name + ".", col.SubColumns, list[i])) yield return x;
                continue;
            }
            if (!col.IsText || rec.Values[c] is not string text || text.Length == 0) continue;
            int ci = c;
            yield return new TextTarget
            {
                Group = group, Key = key, Field = prefix + col.Name, Text = text,
                Max = col.Length > 0 ? col.MaxChars : int.MaxValue, Gbk = col.Type == ElementsCore.Formats.ColType.Str,
                Measure = s => ElementsCore.Formats.RecordIO.TextLength(col, s),
                Set = s =>
                {
                    if (!ElementsCore.Formats.RecordIO.TryParse(col, s, out var v, out var err)) return err;
                    rec[ci] = v;
                    return null;
                },
            };
        }
    }
}

/// <summary>
/// Bulk Hán-Việt: pick fields (e.g. name), preview every Chinese text converted with the
/// ChinesePhienAmWords dictionary, rows that would exceed the field length are skipped (left as
/// they are), then apply. Applied rows are ordinary edits: visible in "Lịch sử sửa", undoable, saved with Ctrl+S.
/// </summary>
public sealed class HanVietDialog : ThemedForm
{
    private readonly IReadOnlyList<TextScope> _scopes;
    private readonly AppSettings _settings;
    private readonly TextBox _dictPath;
    private readonly Label _dictInfo, _summary;
    private readonly ComboBox _scope;
    private readonly CheckedListBox _fields;
    private readonly RadioButton _rEvery, _rFirst, _rLower;
    private readonly DataGridView _grid;
    private readonly Button _apply, _preview;
    private HanViet? _hv;
    private List<TextTarget> _targets = new();
    private readonly List<Row> _rows = new();
    private bool _loadingFields;
    private readonly string? _onlyField;

    public int Applied { get; private set; }

    private sealed class Row
    {
        public required TextTarget T;
        public required string New;
        public int Len, Unknown;
        public bool Ok;
        public string Status = "";
    }

    public HanVietDialog(IReadOnlyList<TextScope> scopes, AppSettings settings, string? preferField = "name", bool onlyThatField = false)
    {
        _scopes = scopes; _settings = settings; _onlyField = onlyThatField ? preferField : null;
        Text = "Việt hoá Hán Việt hàng loạt";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1180, 760);
        MinimumSize = new Size(900, 560);
        Font = new Font("Segoe UI", 9.5F);
        KeyPreview = true;

        var left = new Panel { Dock = DockStyle.Left, Width = 320, Padding = new Padding(12, 10, 8, 10) };
        var lDict = new Label { Text = "Từ điển phiên âm (字=âm)", Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray };
        var dictRow = new Panel { Dock = DockStyle.Top, Height = 32 };
        _dictPath = new TextBox { Dock = DockStyle.Fill, Text = settings.HanVietDict ?? HanViet.DefaultPath };
        var browse = new Button { Text = "…", Dock = DockStyle.Right, Width = 34 };
        dictRow.Controls.Add(_dictPath);
        dictRow.Controls.Add(browse);
        _dictInfo = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray, UseMnemonic = false };
        var lScope = new Label { Text = "Phạm vi", Dock = DockStyle.Top, Height = 24, ForeColor = Color.DimGray, TextAlign = ContentAlignment.BottomLeft };
        _scope = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var s in scopes) _scope.Items.Add(s.Title);
        var lFields = new Label { Text = "Field cần Việt hoá (số dòng còn chữ Hán)", Dock = DockStyle.Top, Height = 26, ForeColor = Color.DimGray, TextAlign = ContentAlignment.BottomLeft };
        _fields = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        var caseBox = new GroupBox { Text = "Viết hoa", Dock = DockStyle.Bottom, Height = 104, Padding = new Padding(8, 4, 8, 4) };
        _rEvery = new RadioButton { Text = "Mỗi chữ – Tích Thiện Y (tên)", Dock = DockStyle.Top, Checked = HanVietTools.LastMode == HanVietCase.EveryWord, Height = 24 };
        _rFirst = new RadioButton { Text = "Chữ đầu – Tích thiện y (câu)", Dock = DockStyle.Top, Checked = HanVietTools.LastMode == HanVietCase.FirstWord, Height = 24 };
        _rLower = new RadioButton { Text = "Thường – tích thiện y", Dock = DockStyle.Top, Checked = HanVietTools.LastMode == HanVietCase.Lower, Height = 24 };
        caseBox.Controls.AddRange([_rLower, _rFirst, _rEvery]);
        _preview = new Button { Text = "Xem trước", Dock = DockStyle.Bottom, Height = 34 };
        left.Controls.Add(_fields);
        left.Controls.Add(lFields);
        left.Controls.Add(_scope);
        left.Controls.Add(lScope);
        left.Controls.Add(_dictInfo);
        left.Controls.Add(dictRow);
        left.Controls.Add(lDict);
        left.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 8 });
        left.Controls.Add(caseBox);
        left.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 8 });
        left.Controls.Add(_preview);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 10, 12, 10) };
        _summary = new Label { Dock = DockStyle.Top, Height = 44, UseMnemonic = false, Text = "Chọn field rồi bấm Xem trước. Dòng nào sau khi đổi vượt độ dài của field sẽ được bỏ qua (giữ nguyên)." };
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None, BackgroundColor = SystemColors.Window,
        };
        typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(_grid, true);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Bản ghi", Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Field", Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Gốc", Width = 200 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Hán Việt", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 200 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Độ dài", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Trạng thái", Width = 170 });
        _grid.CellValueNeeded += OnValue;
        _grid.CellFormatting += OnFormat;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var close = new Button { Text = "Đóng", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
        _apply = new Button { Text = "Áp dụng", Width = 200, Height = 32, Enabled = false };
        bottom.Controls.AddRange([close, _apply]);
        right.Controls.Add(_grid);
        right.Controls.Add(_summary);
        right.Controls.Add(bottom);

        Controls.Add(right);
        Controls.Add(left);
        CancelButton = close;
        AcceptButton = _preview;

        browse.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog { Filter = "Từ điển (*.txt)|*.txt|Tất cả|*.*", FileName = _dictPath.Text };
            if (ofd.ShowDialog(this) == DialogResult.OK) { _dictPath.Text = ofd.FileName; LoadDict(); }
        };
        _dictPath.Leave += (_, _) => LoadDict();
        _scope.SelectedIndexChanged += (_, _) => LoadFields(preferField);
        _fields.ItemCheck += (_, _) => { if (!_loadingFields) BeginInvoke(ClearPreview); };
        _rEvery.CheckedChanged += (_, _) => ClearPreview();
        _rFirst.CheckedChanged += (_, _) => ClearPreview();
        _preview.Click += (_, _) => Preview();
        _apply.Click += (_, _) => Apply();
        Shown += (_, _) =>
        {
            LoadDict();
            if (_scope.Items.Count > 0) _scope.SelectedIndex = 0;
        };
    }

    private void LoadDict()
    {
        var p = _dictPath.Text.Trim();
        if (_hv != null && _hv.Source == p) return;
        _hv = HanVietTools.Dictionary(p, out var err);
        _dictInfo.Text = _hv != null ? $"✓ {_hv.Count:N0} chữ" : "✗ " + err;
        _dictInfo.ForeColor = _hv != null ? Color.DarkGreen : Color.Firebrick;
        ClearPreview();
    }

    private void LoadFields(string? prefer)
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            // GBK fields (icon / model paths, server strings) cannot hold Vietnamese letters: not offered
            _targets = _scopes[_scope.SelectedIndex].Enumerate().Where(t => !t.Gbk && (_onlyField == null || t.Field == _onlyField) && HanViet.HasCjk(t.Text)).ToList();
        }
        finally { Cursor = Cursors.Default; }
        var checkedNow = _fields.CheckedItems.Cast<FieldItem>().Select(f => f.Name).ToHashSet();
        _loadingFields = true;
        _fields.Items.Clear();
        foreach (var g in _targets.GroupBy(t => t.Field).OrderByDescending(g => g.Key == prefer).ThenByDescending(g => g.Count()))
        {
            int i = _fields.Items.Add(new FieldItem(g.Key, g.Count()));
            if (checkedNow.Contains(g.Key) || checkedNow.Count == 0 && g.Key == prefer) _fields.SetItemChecked(i, true);
        }
        _loadingFields = false;
        ClearPreview();
        if (_targets.Count == 0) _summary.Text = "Phạm vi này không còn chữ Hán nào.";
    }

    private sealed record FieldItem(string Name, int Count)
    {
        public override string ToString() => $"{Name}   ({Count:N0})";
    }

    private HanVietCase Mode => HanVietTools.LastMode = _rEvery.Checked ? HanVietCase.EveryWord : _rFirst.Checked ? HanVietCase.FirstWord : HanVietCase.Lower;

    private void ClearPreview()
    {
        _rows.Clear();
        _grid.RowCount = 0;
        _apply.Enabled = false;
        _apply.Text = "Áp dụng";
    }

    private void Preview()
    {
        LoadDict();
        if (_hv == null) { Msg.Show("Chưa đọc được từ điển phiên âm.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var fields = _fields.CheckedItems.Cast<FieldItem>().Select(f => f.Name).ToHashSet();
        if (fields.Count == 0) { _summary.Text = "Hãy tích ít nhất một field."; return; }
        _rows.Clear();
        var mode = Mode;
        foreach (var t in _targets.Where(t => fields.Contains(t.Field)))
        {
            var s = _hv.Convert(t.Text, mode, out int unk);
            var r = new Row { T = t, New = s, Unknown = unk, Len = t.Measure(s) };
            if (t.Gbk) r.Status = "Bỏ qua: field GBK không chứa được tiếng Việt";
            else if (r.Len > t.Max) r.Status = $"Bỏ qua: quá dài {r.Len}/{t.Max}";
            else { r.Ok = true; r.Status = unk > 0 ? $"Đổi · thiếu {unk} chữ trong từ điển" : "Đổi"; }
            _rows.Add(r);
        }
        _rows.Sort((a, b) => a.Ok == b.Ok ? 0 : a.Ok ? 1 : -1); // skipped rows first, to review them
        int ok = _rows.Count(r => r.Ok), tooLong = _rows.Count(r => !r.Ok && !r.T.Gbk), gbk = _rows.Count(r => r.T.Gbk), unknown = _rows.Count(r => r.Unknown > 0);
        _summary.Text = $"{_rows.Count:N0} dòng có chữ Hán · sẽ đổi {ok:N0} · bỏ qua vì quá dài {tooLong:N0}" + (gbk > 0 ? $" · bỏ qua field GBK {gbk:N0}" : "")
            + (unknown > 0 ? $" · {unknown:N0} dòng còn chữ không có trong từ điển (giữ nguyên chữ đó)" : "")
            + "\nCác dòng bỏ qua giữ nguyên văn bản gốc. Sau khi áp dụng: xem/hoàn tác ở “Lịch sử sửa”, rồi Lưu (Ctrl+S).";
        _grid.RowCount = 0;
        _grid.RowCount = _rows.Count;
        _apply.Enabled = ok > 0;
        _apply.Text = $"Áp dụng {ok:N0} dòng";
        AcceptButton = _apply;
    }

    private void Apply()
    {
        int n = 0, failed = 0;
        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (var r in _rows.Where(r => r.Ok))
            {
                if (r.T.Set(r.New) == null) n++;
                else failed++;
            }
        }
        finally { Cursor = Cursors.Default; }
        Applied += n;
        Msg.Show($"Đã Việt hoá Hán Việt {n:N0} dòng" + (failed > 0 ? $", {failed:N0} dòng không ghi được" : "") + ".\nChưa ghi vào file – bấm Lưu (Ctrl+S) để ghi.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnValue(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex >= _rows.Count) return;
        var r = _rows[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => $"{r.T.Group} · {r.T.Key}",
            1 => r.T.Field,
            2 => r.T.Text,
            3 => r.New,
            4 => r.T.Max == int.MaxValue ? $"{r.Len}" : $"{r.Len}/{r.T.Max}",
            5 => r.Status,
            _ => "",
        };
    }

    private void OnFormat(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex >= _rows.Count || e.CellStyle == null) return;
        var r = _rows[e.RowIndex];
        if (e.ColumnIndex is 4 or 5)
            e.CellStyle.ForeColor = !r.Ok ? (Theme.Dark ? Theme.Red : Color.Firebrick) : r.Unknown > 0 ? (Theme.Dark ? Theme.Amber : Color.DarkGoldenrod) : (Theme.Dark ? Theme.AccentText : Color.DarkGreen);
        if (e.ColumnIndex == 3 && !r.Ok) e.CellStyle.ForeColor = Color.Gray;
    }
}
