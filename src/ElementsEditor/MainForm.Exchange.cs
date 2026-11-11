using ElementsCore;
using ElementsCore.Exchange;

namespace ElementsEditor;

/// <summary>
/// Elements tab additions: colour-code text editor under the Fields grid, colour bar on the
/// Việt hoá quick editor, full-data export/import (CSV / XLSX / JSON) of a list, the "N changed ·
/// not saved" status label and the edit-history panel (diff against the file as opened).
/// </summary>
public sealed partial class MainForm
{
    private ColorTextEditor _fieldEditor = null!;
    private ChangesPanel _elChanges = null!;
    private ToolStripStatusLabel _lblChanges = null!;
    private ElementsFile? _baselineEf;
    private string? _editorFieldPath;

    private void SetupDataExchange()
    {
        HanVietTools.Settings = _settings;
        // right-click a field: Hán Việt this cell / the same field in every record of the open list
        _fieldsGrid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.RowIndex >= _filteredFields.Count) return;
            _fieldsGrid.CurrentCell = _fieldsGrid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            ShowFieldMenu(_filteredFields[e.RowIndex]);
        };
        // 1. colour editor below the Fields grid (opens when a text field is selected)
        var tabFields = _fieldsGrid.Parent!;
        _fieldEditor = new ColorTextEditor { Dock = DockStyle.Bottom, Height = 320, Visible = false };
        var split = new Splitter { Dock = DockStyle.Bottom, Height = 5, Visible = false };
        _fieldEditor.VisibleChanged += (_, _) => split.Visible = _fieldEditor.Visible;
        _fieldEditor.Cancelled += () => _fieldEditor.Visible = false;
        tabFields.Controls.Add(split);
        tabFields.Controls.Add(_fieldEditor);
        split.SendToBack();
        _fieldEditor.SendToBack(); // docks first → bottom edge
        _fieldsGrid.SelectionChanged += (_, _) => UpdateFieldEditor();
        _fieldsGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < _filteredFields.Count && _filteredFields[e.RowIndex].IsText)
            { _fieldsGrid.CancelEdit(); _fieldEditor.FocusText(); }
        };

        // 2. colour bar on the Việt hoá quick editor
        if (_quickTextEditor.Parent is { } qp)
        {
            var bar = new ColorCodeBar(_quickTextEditor) { Dock = DockStyle.Top };
            qp.Controls.Add(bar);
            qp.Controls.SetChildIndex(bar, 1);
        }

        // 3. status label "● N đã sửa · chưa lưu" (click = edit history)
        var status = Controls.OfType<StatusStrip>().First();
        _lblChanges = new ToolStripStatusLabel { Visible = false, ForeColor = Theme.Amber, IsLink = false, Margin = new Padding(12, 3, 12, 2) };
        _lblChanges.Click += (_, _) => ToggleChangesPanel();
        _lblChanges.MouseEnter += (_, _) => Cursor = Cursors.Hand;
        _lblChanges.MouseLeave += (_, _) => Cursor = Cursors.Default;
        status.Items.Insert(1, _lblChanges);

        // 4. edit-history panel on the left of the elements work area
        _elChanges = new ChangesPanel(ComputeElementChanges, LocateElementChange, AfterElementRevert) { Dock = DockStyle.Left, Visible = false };
        var csplit = new Splitter { Dock = DockStyle.Left, Width = 5, Visible = false };
        _elChanges.VisibleChanged += (_, _) => csplit.Visible = _elChanges.Visible;
        _mainWorkHost.Controls.Add(csplit);
        _mainWorkHost.Controls.Add(_elChanges);

        // 5. menu + toolbar entries
        var tools = _menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Text?.StartsWith("&Công cụ") == true);
        if (tools != null)
        {
            int at = 0;
            tools.DropDownItems.Insert(at++, new ToolStripMenuItem("Xuất &dữ liệu list đang chọn → Excel (.xlsx)…", null, (_, _) => ExportListData("xlsx")));
            tools.DropDownItems.Insert(at++, new ToolStripMenuItem("Xuất dữ liệu list đang chọn → CSV…", null, (_, _) => ExportListData("csv")));
            tools.DropDownItems.Insert(at++, new ToolStripMenuItem("Xuất dữ liệu list đang chọn → JSON (có cột khoá)…", null, (_, _) => ExportListData("json")));
            tools.DropDownItems.Insert(at++, new ToolStripMenuItem("&Nhập dữ liệu (CSV / XLSX / JSON)…", null, (_, _) => ImportListData()));
            tools.DropDownItems.Insert(at++, new ToolStripMenuItem("&Lịch sử sửa (các thay đổi chưa lưu)…", null, (_, _) => ToggleChangesPanel()));
            tools.DropDownItems.Insert(at, new ToolStripSeparator());
        }
        var ts = _elementsHost.Controls.OfType<ToolStrip>().First();
        var exp = new ToolStripDropDownButton("Xuất dữ liệu");
        exp.DropDownItems.Add("List đang chọn → Excel (.xlsx)…", null, (_, _) => ExportListData("xlsx"));
        exp.DropDownItems.Add("List đang chọn → CSV…", null, (_, _) => ExportListData("csv"));
        exp.DropDownItems.Add("List đang chọn → JSON…", null, (_, _) => ExportListData("json"));
        var imp = new ToolStripButton("Nhập dữ liệu");
        imp.Click += (_, _) => ImportListData();
        var hist = new ToolStripButton("Lịch sử sửa");
        hist.Click += (_, _) => ToggleChangesPanel();
        var hanviet = new ToolStripButton("Việt hoá Hán Việt…") { ToolTipText = "Đổi chữ Hán của các field đã chọn (vd name) sang Hán Việt hàng loạt; dòng nào vượt độ dài thì bỏ qua." };
        hanviet.Click += (_, _) => ShowHanViet();
        ts.Items.AddRange([new ToolStripSeparator(), exp, imp, hist, new ToolStripSeparator(), hanviet]);
        var loc = _menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Text?.StartsWith("&Việt hóa") == true);
        loc?.DropDownItems.Insert(0, new ToolStripMenuItem("Việt hoá &Hán Việt hàng loạt (từ điển phiên âm)…", null, (_, _) => ShowHanViet()));
        loc?.DropDownItems.Insert(1, new ToolStripSeparator());
    }

    // ───────────────────────── bulk Hán Việt ─────────────────────────

    private void ShowHanViet()
    {
        if (_ef == null) { Msg.Show("Hãy mở elements.data trước.", "Việt hoá Hán Việt"); return; }
        var ef = _ef;
        var scopes = new List<TextScope>();
        if (_currentList != null)
        {
            var cl = _currentList;
            scopes.Add(new TextScope($"List đang chọn: {cl.Index} {cl.Name} ({cl.Items.Count:N0})", () => ListTargets(cl)));
        }
        scopes.Add(new TextScope($"Tất cả {ef.Lists.Count} list", () => ef.Lists.SelectMany(ListTargets)));
        using var dlg = new HanVietDialog(scopes, _settings);
        dlg.ShowDialog(this);
        if (dlg.Applied == 0) return;
        _dirty = true;
        RefreshFilteredItems();
        RefreshInspector();
        UpdateWindowTitle();
        UpdateSummaryStatus();
        _elChanges.Visible = true;
        _elChanges.Reload();
    }

    private IEnumerable<TextTarget> ListTargets(ElementList l) => ListTargets(l, null);

    private IEnumerable<TextTarget> ListTargets(ElementList l, ElementsCore.Schema.FieldDef? only)
    {
        var texts = l.Schema.Fields.Where(f => f.IsText && (only == null || f == only)).ToList();
        if (texts.Count == 0) yield break;
        for (int i = 0; i < l.Items.Count; i++)
            foreach (var f in texts)
                if (ElementTarget(l, i, f) is { } t) yield return t;
    }

    private TextTarget? ElementTarget(ElementList l, int i, ElementsCore.Schema.FieldDef f)
    {
        var item = l.Items[i];
        var text = FieldCodec.GetText(item, f);
        if (text.Length == 0) return null;
        bool gbk = f.Kind == ElementsCore.Schema.FieldKind.Str;
        return new TextTarget
        {
            Group = $"{l.Index} {l.Name}", Key = $"ID {l.GetId(i)}", Field = f.Path, Text = text,
            Max = FieldCodec.MaxTextLength(f), Gbk = gbk,
            Measure = gbk ? s => FieldCodec.Gbk.GetByteCount(s) : s => s.Length,
            Set = s =>
            {
                try { FieldCodec.SetText(item, f, s); }
                catch (Exception ex) { return ex.Message; }
                _dirtyRecords.Add(item);
                return null;
            },
        };
    }

    /// <summary>Right-click menu of a field row (elements tab).</summary>
    private void ShowFieldMenu(ElementsCore.Schema.FieldDef f)
    {
        if (_currentList == null || _currentItemIndex < 0) return;
        var l = _currentList;
        int idx = _currentItemIndex;
        var item = l.Items[idx];
        var menu = new ContextMenuStrip();
        bool text = f.IsText, gbk = f.Kind == ElementsCore.Schema.FieldKind.Str;
        string cur = text ? FieldCodec.GetText(item, f) : "";
        int cjkRows = 0;
        if (text && !gbk) for (int i = 0; i < l.Items.Count; i++) if (ElementsCore.Exchange.HanViet.HasCjk(FieldCodec.GetText(l.Items[i], f))) cjkRows++;

        var one = menu.Items.Add($"Việt hoá Hán Việt field \"{f.Path}\" của bản ghi này (ID {l.GetId(idx)})", null, (_, _) =>
        {
            if (ElementTarget(l, idx, f) is not { } t) return;
            if (HanVietTools.ConvertOne(t, out var msg))
            {
                _dirty = true;
                MarkRecordDirty(item);
                RefreshInspector();
                if (_itemsGrid.CurrentRow != null) _itemsGrid.InvalidateRow(_itemsGrid.CurrentRow.Index);
                _lblStatus.Text = "Hán Việt: " + msg;
            }
            else Msg.Show(msg, "Việt hoá Hán Việt", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        one.Enabled = text && !gbk && ElementsCore.Exchange.HanViet.HasCjk(cur);
        var all = menu.Items.Add($"Việt hoá Hán Việt field \"{f.Path}\" cho cả list {l.Name} ({cjkRows:N0} dòng còn chữ Hán)…", null, (_, _) =>
        {
            using var dlg = new HanVietDialog([new TextScope($"List {l.Index} {l.Name} · field {f.Path}", () => ListTargets(l, f))], _settings, f.Path, onlyThatField: true);
            dlg.ShowDialog(this);
            if (dlg.Applied == 0) return;
            _dirty = true;
            RefreshFilteredItems();
            RefreshInspector();
            UpdateWindowTitle();
            UpdateSummaryStatus();
            _elChanges.Visible = true;
            _elChanges.Reload();
        });
        all.Enabled = cjkRows > 0;
        if (gbk) menu.Items.Add("(field GBK – không chứa được tiếng Việt có dấu)").Enabled = false;
        else if (!text) menu.Items.Add("(chỉ field chữ mới Việt hoá được)").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sửa trong khung soạn (màu, xem trước)", null, (_, _) => { UpdateFieldEditor(); _fieldEditor.FocusText(); }).Enabled = text;
        menu.Items.Add("Sao chép giá trị", null, (_, _) =>
        {
            try { Clipboard.SetText(FieldCodec.Format(item, f, l.Schema)); } catch { }
        });
        Theme.Apply(menu);
        menu.Show(Cursor.Position);
    }

    // ───────────────────────── text field editor ─────────────────────────

    private void UpdateFieldEditor()
    {
        if (_syncingUi || _currentList == null || _currentItemIndex < 0 || _fieldsGrid.CurrentRow == null || _fieldsGrid.CurrentRow.Index >= _filteredFields.Count)
        { if (!_syncingUi) _fieldEditor.Visible = false; return; }
        var list = _currentList;
        var f = _filteredFields[_fieldsGrid.CurrentRow.Index];
        _editorFieldPath = f.Path;
        if (!f.IsText) { _fieldEditor.Visible = false; return; }
        var item = list.Items[_currentItemIndex];
        int row = _fieldsGrid.CurrentRow.Index;
        long id = list.GetId(_currentItemIndex);
        _fieldEditor.SaveText = text =>
        {
            try { FieldCodec.SetText(item, f, text); }
            catch (Exception ex) { return ex.Message; }
            MarkRecordDirty(item);
            if (_currentList == list && _currentItemIndex >= 0 && list.Items[_currentItemIndex] == item && row < _filteredFields.Count && _filteredFields[row] == f)
            {
                _syncingUi = true;
                _fieldsGrid.Rows[row].Cells[3].Value = FieldCodec.Format(item, f, list.Schema);
                _hexBox.Text = HexFormatter.Format(item);
                _syncingUi = false;
            }
            if (_itemsGrid.CurrentRow != null) _itemsGrid.InvalidateRow(_itemsGrid.CurrentRow.Index);
            _lblStatus.Text = $"Đã sửa {f.Path} của ID {id} (chưa lưu file).";
            return null;
        };
        _fieldEditor.Begin(f.Path, FieldCodec.GetText(item, f), FieldCodec.MaxTextLength(f),
            f.Kind == ElementsCore.Schema.FieldKind.Str ? s => FieldCodec.Gbk.GetByteCount(s) : null,
            $"ID {id}{(f.Kind == ElementsCore.Schema.FieldKind.Str ? " · GBK (không chứa được tiếng Việt có dấu)" : "")}");
    }

    /// <summary>After the inspector is rebuilt (another record): keep editing the same field.</summary>
    private void AfterInspectorRefresh()
    {
        if (_fieldEditor == null) return;
        if (_editorFieldPath != null && _fieldEditor.Visible)
            for (int r = 0; r < _filteredFields.Count; r++)
                if (_filteredFields[r].Path == _editorFieldPath) { _fieldsGrid.CurrentCell = _fieldsGrid.Rows[r].Cells[3]; break; }
        UpdateFieldEditor();
    }

    // ───────────────────────── changes ─────────────────────────

    private void UpdateChangesLabel()
    {
        if (_lblChanges == null) return;
        string? text = null;
        if (_activeModule == "elements")
        {
            int n = _dirtyRecords.Count + _dirtyDialogs.Count;
            if (_dirty) text = n > 0 ? $"● {n:N0} bản ghi đã sửa · chưa lưu" : "● Có thay đổi · chưa lưu";
        }
        else if (_pages.TryGetValue(_activeModule, out var p) && p.Dirty)
            text = p.ChangeCount > 0 ? $"● {p.ChangeCount:N0} thay đổi · chưa lưu" : "● Có thay đổi · chưa lưu";
        _lblChanges.Visible = text != null;
        if (text != null) { _lblChanges.Text = text; _lblChanges.ToolTipText = "Bấm để xem lịch sử sửa (Ctrl+H ở các tab khác)"; }
    }

    private void ToggleChangesPanel()
    {
        if (_activeModule != "elements") { if (_pages.TryGetValue(_activeModule, out var p)) p.ToggleChanges(); return; }
        if (_ef == null) return;
        _elChanges.Visible = !_elChanges.Visible;
    }

    private List<ChangeItem> ComputeElementChanges()
    {
        if (_ef == null || string.IsNullOrEmpty(_ef.Path) || !File.Exists(_ef.Path)) return new();
        if (_baselineEf == null)
        {
            Cursor = Cursors.WaitCursor;
            try { _baselineEf = ElementsFile.Load(_ef.Path, _repo); }
            finally { Cursor = Cursors.Default; }
        }
        return ElementsDiff.Compute(_ef, _baselineEf, (_, item) => _dirtyRecords.Add(item));
    }

    private void LocateElementChange(ChangeItem c)
    {
        if (_ef == null) return;
        if (c.Target is byte[] item && _ef.Lists.FirstOrDefault(l => l.Index == c.ListIndex) is { } list)
        {
            int idx = list.Items.IndexOf(item);
            if (idx >= 0) JumpTo(false, list.Index, idx, c.Field);
            return;
        }
        if (_ef.Dialogs != null)
        {
            int d = c.Target switch
            {
                ElementsCore.TalkDialog td => _ef.Dialogs.IndexOf(td),
                ElementsCore.TalkWindow tw => _ef.Dialogs.FindIndex(x => x.Windows.Contains(tw)),
                _ => -1,
            };
            if (d >= 0) JumpTo(true, -1, d, null);
        }
    }

    private void AfterElementRevert()
    {
        _dirty = true;
        RefreshFilteredItems();
        RefreshInspector();
        UpdateWindowTitle();
        UpdateSummaryStatus();
    }

    // ───────────────────────── export / import ─────────────────────────

    private void ExportListData(string ext)
    {
        if (_ef == null || _currentList == null) { Msg.Show("Hãy mở elements.data và chọn một list trước.", "Xuất dữ liệu"); return; }
        var l = _currentList;
        using var sfd = new SaveFileDialog
        {
            Filter = DataSheets.FileFilter, FilterIndex = ext switch { "csv" => 2, "json" => 3, _ => 1 },
            FileName = $"{l.Index}_{l.Name}.{ext}", InitialDirectory = Path.GetDirectoryName(_ef.Path),
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            ElementsExchange.Export(_ef, [l], sfd.FileName);
            RecentStore.Log("EXPORT", "elements", sfd.FileName);
            _lblStatus.Text = $"Đã xuất {l.Items.Count:N0} bản ghi × {l.Schema.Fields.Count} field của {l.Name} → {sfd.FileName}";
            Msg.Show($"Đã xuất {l.Items.Count:N0} bản ghi ({l.Schema.Fields.Count} cột) của list {l.Index} {l.Name}.\n\n"
                + $"Cột khoá: {ElementsExchange.ListCol} (số list), ID, {ElementsExchange.IndexCol} (vị trí) – giữ nguyên khi sửa để nhập lại đúng bản ghi.\n"
                + "Ô để trống khi nhập = giữ nguyên giá trị cũ. ID chưa có trong list = thêm bản ghi mới.",
                "Xuất dữ liệu", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Msg.Show($"Xuất thất bại:\n{ex.Message}", "Xuất dữ liệu", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }
    }

    private void ImportListData()
    {
        if (_ef == null) { Msg.Show("Hãy mở elements.data trước.", "Nhập dữ liệu"); return; }
        using var ofd = new OpenFileDialog { Filter = DataSheets.OpenFilter, Title = "Nhập dữ liệu vào elements.data (CSV / XLSX / JSON)", InitialDirectory = Path.GetDirectoryName(_ef.Path) };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            var res = ElementsExchange.Import(_ef, ofd.FileName, _currentList, (_, item) => _dirtyRecords.Add(item));
            Cursor = Cursors.Default;
            RecentStore.Log("IMPORT", "elements", ofd.FileName);
            if (res.CellsChanged > 0 || res.RowsAdded > 0)
            {
                _dirty = true;
                RefreshFilteredItems();
                RefreshInspector();
                UpdateWindowTitle();
                UpdateSummaryStatus();
            }
            Msg.Show(res.Summary($"Nhập {Path.GetFileName(ofd.FileName)} vào elements.data"), "Nhập dữ liệu", MessageBoxButtons.OK,
                res.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            if (res.CellsChanged > 0 || res.RowsAdded > 0) { _elChanges.Visible = true; _elChanges.Reload(); }
        }
        catch (Exception ex) { Cursor = Cursors.Default; Msg.Show($"Nhập thất bại:\n{ex.Message}", "Nhập dữ liệu", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
