using ElementsCore;
using ElementsCore.Schema;

namespace ElementsEditor;

/// <summary>Interactive picker for Enum values and Bitmask flags.</summary>
public sealed class BitmaskEnumDialog : ThemedForm
{
    private readonly EnumDef _enumDef;
    private readonly long _initialValue;
    private readonly CheckedListBox? _maskList;
    private readonly ListView? _enumList;
    private readonly TextBox _valueBox;
    private bool _syncing;

    public long ResultValue { get; private set; }

    public BitmaskEnumDialog(string fieldName, EnumDef enumDef, long currentValue)
    {
        _enumDef = enumDef;
        _initialValue = currentValue;
        ResultValue = currentValue;

        Text = $"Chọn giá trị: {fieldName} ({enumDef.Key})";
        Size = new Size(560, 500);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 8, 10, 6) };
        var lblVal = new Label { Text = "Giá trị số:", AutoSize = true, Left = 10, Top = 13 };
        _valueBox = new TextBox { Left = 85, Top = 10, Width = 180, Text = currentValue.ToString() };
        var lblHex = new Label { Left = 275, Top = 13, AutoSize = true, ForeColor = Color.DimGray, Text = $"0x{currentValue:X}" };
        topPanel.Controls.AddRange([lblVal, _valueBox, lblHex]);

        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        var btnOk = new Button { Text = "Áp dụng", DialogResult = DialogResult.OK, Width = 95, Height = 28 };
        var btnCancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Width = 80, Height = 28 };
        bottomPanel.Controls.AddRange([btnCancel, btnOk]);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        if (enumDef.IsMask)
        {
            var btnAll = new Button { Text = "Chọn hết", Width = 85, Height = 28 };
            var btnNone = new Button { Text = "Bỏ hết", Width = 80, Height = 28 };
            btnAll.Click += (_, _) => SetAllMask(true);
            btnNone.Click += (_, _) => SetAllMask(false);
            bottomPanel.Controls.AddRange([btnNone, btnAll]);

            _maskList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
            foreach (var f in enumDef.Flags.OrderBy(x => x.Bit))
            {
                bool on = f.Bit >= 0 && f.Bit < 64 && (currentValue & (1L << f.Bit)) != 0;
                string desc = string.IsNullOrWhiteSpace(f.Description) ? "" : $" — {f.Description}";
                _maskList.Items.Add(new FlagItem(f, $"[Bit {f.Bit,2}] {f.Label}{desc}"), on);
            }
            _maskList.ItemCheck += (_, e) =>
            {
                BeginInvoke(() =>
                {
                    if (_syncing) return;
                    long val = 0;
                    for (int i = 0; i < _maskList.Items.Count; i++)
                    {
                        if (_maskList.GetItemChecked(i) && _maskList.Items[i] is FlagItem fi && fi.Flag.Bit < 64)
                            val |= 1L << fi.Flag.Bit;
                    }
                    _syncing = true;
                    ResultValue = val;
                    _valueBox.Text = val.ToString();
                    lblHex.Text = $"0x{val:X}";
                    _syncing = false;
                });
            };
            _valueBox.TextChanged += (_, _) =>
            {
                if (_syncing || !long.TryParse(_valueBox.Text.Trim(), out long val)) return;
                _syncing = true;
                ResultValue = val;
                lblHex.Text = $"0x{val:X}";
                for (int i = 0; i < _maskList.Items.Count; i++)
                {
                    if (_maskList.Items[i] is FlagItem fi && fi.Flag.Bit < 64)
                        _maskList.SetItemChecked(i, (val & (1L << fi.Flag.Bit)) != 0);
                }
                _syncing = false;
            };
            Controls.Add(_maskList);
        }
        else
        {
            _enumList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
            };
            _enumList.Columns.Add("Giá trị", 85);
            _enumList.Columns.Add("Nhãn", 220);
            _enumList.Columns.Add("Mô tả", 220);
            foreach (var kv in enumDef.Values.OrderBy(x => x.Key))
            {
                var lvi = new ListViewItem([kv.Key.ToString(), kv.Value.Label, kv.Value.Description ?? ""]) { Tag = kv.Key };
                _enumList.Items.Add(lvi);
                if (kv.Key == currentValue) lvi.Selected = true;
            }
            _enumList.SelectedIndexChanged += (_, _) =>
            {
                if (_syncing || _enumList.SelectedItems.Count == 0) return;
                long val = (long)_enumList.SelectedItems[0].Tag!;
                _syncing = true;
                ResultValue = val;
                _valueBox.Text = val.ToString();
                lblHex.Text = $"0x{val:X}";
                _syncing = false;
            };
            _enumList.DoubleClick += (_, _) => { if (_enumList.SelectedItems.Count > 0) { DialogResult = DialogResult.OK; Close(); } };
            _valueBox.TextChanged += (_, _) =>
            {
                if (long.TryParse(_valueBox.Text.Trim(), out long val))
                {
                    ResultValue = val;
                    lblHex.Text = $"0x{val:X}";
                }
            };
            Controls.Add(_enumList);
        }

        Controls.Add(topPanel);
        Controls.Add(bottomPanel);
    }

    private void SetAllMask(bool check)
    {
        if (_maskList == null) return;
        for (int i = 0; i < _maskList.Items.Count; i++) _maskList.SetItemChecked(i, check);
    }

    private sealed record FlagItem(MaskFlag Flag, string Display)
    {
        public override string ToString() => Display;
    }
}

/// <summary>Global search across all 318 lists + NPC Dialogs.</summary>
public sealed class GlobalSearchDialog : ThemedForm
{
    private readonly ElementsFile _ef;
    private readonly Action<SearchHit> _onJump;
    private readonly TextBox _queryBox;
    private readonly ListView _results;
    private readonly Label _status;
    private List<SearchHit> _hits = new();

    public GlobalSearchDialog(ElementsFile ef, Action<SearchHit> onJump)
    {
        _ef = ef;
        _onJump = onJump;

        Text = "Tìm kiếm toàn cục (ID hoặc Tên / Văn bản)";
        Size = new Size(820, 520);
        StartPosition = FormStartPosition.CenterParent;

        var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
        _queryBox = new TextBox { Left = 10, Top = 10, Width = 580, PlaceholderText = "Nhập ID (vd: 55) hoặc tên TQ/Việt (vd: 木剑, Chân Tiên)..." };
        var btnFind = new Button { Text = "Tìm kiếm", Left = 600, Top = 8, Width = 95, Height = 27 };
        _status = new Label { Left = 705, Top = 13, AutoSize = true, Text = "" };
        top.Controls.AddRange([_queryBox, btnFind, _status]);

        _results = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        _results.Columns.Add("List", 200);
        _results.Columns.Add("ID", 75);
        _results.Columns.Add("Trường (Field)", 140);
        _results.Columns.Add("Nội dung khớp", 360);

        btnFind.Click += (_, _) => RunSearch();
        _queryBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RunSearch(); } };
        _results.DoubleClick += (_, _) =>
        {
            if (_results.SelectedIndices.Count > 0)
                _onJump(_hits[_results.SelectedIndices[0]]);
        };

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Text = "  Mẹo: Nhấp đúp vào kết quả để nhảy thẳng tới mục đó (tối đa 2.000 kết quả).",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
        };

        Controls.Add(_results);
        Controls.Add(top);
        Controls.Add(hint);
    }

    private void RunSearch()
    {
        string q = _queryBox.Text.Trim();
        if (q.Length == 0) return;
        _hits = ElementsOps.Search(_ef, q, maxHits: 2000);
        _results.BeginUpdate();
        _results.Items.Clear();
        foreach (var h in _hits)
        {
            string listLabel = h.IsDialog ? "[TALK] NPC Dialog" : $"[{h.ListIndex}] {h.ListName}";
            string snippet = h.MatchedText.Replace("\r", " ").Replace("\n", " ");
            if (snippet.Length > 120) snippet = snippet[..120] + "...";
            _results.Items.Add(new ListViewItem([listLabel, h.Id.ToString(), h.FieldPath, snippet]));
        }
        _results.EndUpdate();
        _status.Text = $"{_hits.Count} kết quả";
    }
}

/// <summary>Batch Find & Replace with MaxLength validation and preview.</summary>
public sealed class BatchReplaceDialog : ThemedForm
{
    private readonly ElementsFile _ef;
    private readonly int? _currentListIndex;
    private readonly TextBox _findBox;
    private readonly TextBox _replaceBox;
    private readonly RadioButton _rbCurrent;
    private readonly RadioButton _rbAll;
    private readonly CheckBox _chkIgnoreCase;
    private readonly ListView _preview;
    private readonly Label _summary;
    private readonly Button _btnApply;
    private List<ReplacePreview> _items = new();

    public int AppliedCount { get; private set; }

    public BatchReplaceDialog(ElementsFile ef, int? currentListIndex, string currentListLabel)
    {
        _ef = ef;
        _currentListIndex = currentListIndex;

        Text = "Tìm & Thay thế hàng loạt (Việt hóa nhanh)";
        Size = new Size(900, 580);
        StartPosition = FormStartPosition.CenterParent;

        var top = new Panel { Dock = DockStyle.Top, Height = 108, Padding = new Padding(10) };
        top.Controls.Add(new Label { Text = "Tìm chuỗi:", Left = 10, Top = 14, Width = 90 });
        _findBox = new TextBox { Left = 105, Top = 11, Width = 320 };
        top.Controls.Add(new Label { Text = "Thay bằng:", Left = 445, Top = 14, Width = 80 });
        _replaceBox = new TextBox { Left = 530, Top = 11, Width = 330 };

        _rbCurrent = new RadioButton
        {
            Text = currentListIndex.HasValue ? $"Chỉ list hiện tại ({currentListLabel})" : "Chỉ list hiện tại",
            Left = 105,
            Top = 42,
            Width = 360,
            Checked = currentListIndex.HasValue,
            Enabled = currentListIndex.HasValue,
        };
        _rbAll = new RadioButton
        {
            Text = "Toàn bộ file (tất cả 318 list + NPC Dialogs)",
            Left = 470,
            Top = 42,
            Width = 340,
            Checked = !currentListIndex.HasValue,
        };
        _chkIgnoreCase = new CheckBox { Text = "Không phân biệt hoa/thường", Left = 105, Top = 70, Width = 220 };
        var btnPreview = new Button { Text = "Xem trước (Preview)", Left = 350, Top = 67, Width = 160, Height = 28 };
        _summary = new Label { Left = 530, Top = 72, AutoSize = true, Text = "Nhập chuỗi rồi bấm Xem trước." };

        top.Controls.AddRange([_findBox, _replaceBox, _rbCurrent, _rbAll, _chkIgnoreCase, btnPreview, _summary]);

        _preview = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        _preview.Columns.Add("Vị trí", 170);
        _preview.Columns.Add("ID", 70);
        _preview.Columns.Add("Field", 120);
        _preview.Columns.Add("Trước", 220);
        _preview.Columns.Add("Sau", 220);
        _preview.Columns.Add("Giới hạn", 80);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var btnClose = new Button { Text = "Đóng", DialogResult = DialogResult.Cancel, Width = 85, Height = 28 };
        _btnApply = new Button { Text = "Áp dụng thay thế", Width = 140, Height = 28, Enabled = false };
        bottom.Controls.AddRange([btnClose, _btnApply]);

        btnPreview.Click += (_, _) => RunPreview();
        _btnApply.Click += (_, _) =>
        {
            var (applied, skipped) = ElementsOps.ApplyReplace(_items);
            AppliedCount += applied;
            Msg.Show($"Đã áp dụng {applied:N0} thay đổi.\nBỏ qua do quá độ dài: {skipped:N0}.",
                "Hoàn tất", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.Add(_preview);
        Controls.Add(top);
        Controls.Add(bottom);
    }

    private void RunPreview()
    {
        var cmp = _chkIgnoreCase.Checked ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        ISet<int>? lists = _rbCurrent.Checked && _currentListIndex is int idx && idx >= 0 ? new HashSet<int> { idx } : null;
        bool includeDialogs = !_rbCurrent.Checked || _currentListIndex == -1;
        _items = ElementsOps.PreviewReplace(_ef, _findBox.Text, _replaceBox.Text, lists, includeDialogs, cmp);

        _preview.BeginUpdate();
        _preview.Items.Clear();
        int tooLong = 0;
        int shown = 0;
        foreach (var p in _items)
        {
            if (p.TooLong) tooLong++;
            if (shown++ >= 2000) continue;
            string limit = p.TooLong
                ? $"QUÁ ({p.After.Length}/{p.Entry.MaxLength})"
                : p.Entry.MaxLength < 0 ? "OK" : $"{p.After.Length}/{p.Entry.MaxLength}";
            var lvi = new ListViewItem([p.Entry.Group, p.Entry.Id.ToString(), p.Entry.Field, p.Entry.Value, p.After, limit]);
            if (p.TooLong) lvi.ForeColor = Color.Firebrick;
            _preview.Items.Add(lvi);
        }
        _preview.EndUpdate();
        int valid = _items.Count - tooLong;
        _summary.Text = $"Tìm thấy {_items.Count:N0} mục ({valid:N0} hợp lệ, {tooLong:N0} quá dài)" + (_items.Count > 2000 ? " [hiển thị 2000 dòng đầu]" : "");
        _btnApply.Enabled = valid > 0;
    }
}

/// <summary>Shows a detailed import report after applying CSV translations.</summary>
public sealed class ImportReportDialog : ThemedForm
{
    public ImportReportDialog(ImportReport report)
    {
        Text = "Kết quả nhập bản dịch CSV";
        Size = new Size(720, 460);
        StartPosition = FormStartPosition.CenterParent;

        var lbl = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(10),
            Text = report.ToString(),
            Font = new Font(Font, FontStyle.Bold),
        };
        var txt = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            Text = report.Log.Count == 0
                ? "Không có cảnh báo hay lỗi nào. Tất cả bản dịch hợp lệ."
                : string.Join(Environment.NewLine, report.Log),
        };
        var btnOk = new Button { Text = "Đóng", Dock = DockStyle.Bottom, Height = 36, DialogResult = DialogResult.OK };
        Controls.Add(txt);
        Controls.Add(lbl);
        Controls.Add(btnOk);
        AcceptButton = btnOk;
    }
}

/// <summary>Displays Diff comparison between loaded elements.data and another baseline file.</summary>
public sealed class DiffReportDialog : ThemedForm
{
    public DiffReportDialog(List<DiffEntry> diffs, string baseFile, Action<DiffEntry> onJump)
    {
        Text = $"So sánh thay đổi với: {Path.GetFileName(baseFile)} ({diffs.Count} mục khác biệt)";
        Size = new Size(800, 500);
        StartPosition = FormStartPosition.CenterParent;

        var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        lv.Columns.Add("Loại", 55);
        lv.Columns.Add("List", 240);
        lv.Columns.Add("ID", 85);
        lv.Columns.Add("Tên mục", 380);

        lv.BeginUpdate();
        foreach (var d in diffs)
        {
            var lvi = new ListViewItem([d.Kind, $"[{d.ListIndex}] {d.ListName}", d.Id.ToString(), d.Name]);
            lvi.ForeColor = d.Kind switch { "+" => Color.DarkGreen, "-" => Color.Firebrick, _ => Color.DarkBlue };
            lv.Items.Add(lvi);
        }
        lv.EndUpdate();
        lv.DoubleClick += (_, _) => { if (lv.SelectedIndices.Count > 0) onJump(diffs[lv.SelectedIndices[0]]); };

        var lbl = new Label { Dock = DockStyle.Bottom, Height = 26, Text = "  (+) Thêm mới   (-) Đã xóa   (~) Đã chỉnh sửa. Nhấp đúp vào dòng (+) hoặc (~) để nhảy tới.", ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
        Controls.Add(lv);
        Controls.Add(lbl);
    }
}
