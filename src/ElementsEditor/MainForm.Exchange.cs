using ElementsCore;
using ElementsCore.Exchange;

namespace ElementsEditor;

/// <summary>
/// Elements tab additions: colour-code text editor under the Fields grid, colour bar on the
/// localization quick editor, full-data export/import (CSV / XLSX / JSON) of a list, the "N changed ·
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

        // 2. colour bar on the localization quick editor
        if (_quickTextEditor.Parent is { } qp)
        {
            var bar = new ColorCodeBar(_quickTextEditor) { Dock = DockStyle.Top };
            qp.Controls.Add(bar);
            qp.Controls.SetChildIndex(bar, 1);
        }

        // 3. status label "● N edited · not saved" (click = edit history)
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
        var tools = _menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Name == "mnuTools");
        if (tools != null)
        {
            int at = 0;
            tools.DropDownItems.Insert(at++, new ToolStripMenuItem(L.T("Export &data of the selected list → Excel (.xlsx)…"), null, (_, _) => ExportListData("xlsx")));
            tools.DropDownItems.Insert(at++, new ToolStripMenuItem(L.T("Export data of the selected list → CSV…"), null, (_, _) => ExportListData("csv")));
            tools.DropDownItems.Insert(at++, new ToolStripMenuItem(L.T("Export data of the selected list → JSON (with key columns)…"), null, (_, _) => ExportListData("json")));
            tools.DropDownItems.Insert(at++, new ToolStripMenuItem(L.T("&Import data (CSV / XLSX / JSON)…"), null, (_, _) => ImportListData()));
            tools.DropDownItems.Insert(at++, new ToolStripMenuItem(L.T("Edit &history (unsaved changes)…"), null, (_, _) => ToggleChangesPanel()));
            tools.DropDownItems.Insert(at, new ToolStripSeparator());
        }
        var ts = _elementsHost.Controls.OfType<ToolStrip>().First();
        var exp = new ToolStripDropDownButton(L.T("Export data"));
        exp.DropDownItems.Add(L.T("Selected list → Excel (.xlsx)…"), null, (_, _) => ExportListData("xlsx"));
        exp.DropDownItems.Add(L.T("Selected list → CSV…"), null, (_, _) => ExportListData("csv"));
        exp.DropDownItems.Add(L.T("Selected list → JSON…"), null, (_, _) => ExportListData("json"));
        var imp = new ToolStripButton(L.T("Import data"));
        imp.Click += (_, _) => ImportListData();
        var hist = new ToolStripButton(L.T("Edit history"));
        hist.Click += (_, _) => ToggleChangesPanel();
        var hanviet = new ToolStripButton(L.T("Hán Việt conversion…")) { ToolTipText = L.T("Convert the Chinese characters of the chosen fields (e.g. name) to Sino-Vietnamese (Hán Việt) in bulk; rows that would exceed the length limit are skipped.") };
        hanviet.Click += (_, _) => ShowHanViet();
        ts.Items.AddRange([new ToolStripSeparator(), exp, imp, hist, new ToolStripSeparator(), hanviet]);
        var loc = _menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Name == "mnuLoc");
        loc?.DropDownItems.Insert(0, new ToolStripMenuItem(L.T("Bulk Sino-Vietnamese (&Hán Việt) conversion (phonetic dictionary)…"), null, (_, _) => ShowHanViet()));
        loc?.DropDownItems.Insert(1, new ToolStripSeparator());
    }

    // ───────────────────────── bulk Hán Việt ─────────────────────────

    private void ShowHanViet()
    {
        if (_ef == null) { Msg.Show(L.T("Open elements.data first."), L.T("Hán Việt conversion")); return; }
        var ef = _ef;
        var scopes = new List<TextScope>();
        if (_currentList != null)
        {
            var cl = _currentList;
            scopes.Add(new TextScope(L.F("Selected list: {0} {1} ({2:N0})", cl.Index, cl.Name, cl.Items.Count), () => ListTargets(cl)));
        }
        scopes.Add(new TextScope(L.F("All {0} lists", ef.Lists.Count), () => ef.Lists.SelectMany(ListTargets)));
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

        var one = menu.Items.Add(L.F("Hán Việt conversion of field \"{0}\" in this record (ID {1})", f.Path, l.GetId(idx)), null, (_, _) =>
        {
            if (ElementTarget(l, idx, f) is not { } t) return;
            if (HanVietTools.ConvertOne(t, out var msg))
            {
                _dirty = true;
                MarkRecordDirty(item);
                RefreshInspector();
                if (_itemsGrid.CurrentRow != null) _itemsGrid.InvalidateRow(_itemsGrid.CurrentRow.Index);
                _lblStatus.Text = L.F("Hán Việt: {0}", msg);
            }
            else Msg.Show(msg, L.T("Hán Việt conversion"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        one.Enabled = text && !gbk && ElementsCore.Exchange.HanViet.HasCjk(cur);
        var all = menu.Items.Add(L.F("Hán Việt conversion of field \"{0}\" in the whole list {1} ({2:N0} rows still with Chinese characters)…", f.Path, l.Name, cjkRows), null, (_, _) =>
        {
            using var dlg = new HanVietDialog([new TextScope(L.F("List {0} {1} · field {2}", l.Index, l.Name, f.Path), () => ListTargets(l, f))], _settings, f.Path, onlyThatField: true);
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
        if (gbk) menu.Items.Add(L.T("(GBK field – cannot hold Vietnamese diacritics)")).Enabled = false;
        else if (!text) menu.Items.Add(L.T("(only text fields can be localized)")).Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Edit in the text editor (colours, preview)"), null, (_, _) => { UpdateFieldEditor(); _fieldEditor.FocusText(); }).Enabled = text;
        menu.Items.Add(L.T("Copy value"), null, (_, _) =>
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
            _lblStatus.Text = L.F("Edited {0} of ID {1} (file not saved yet).", f.Path, id);
            return null;
        };
        _fieldEditor.Begin(f.Path, FieldCodec.GetText(item, f), FieldCodec.MaxTextLength(f),
            f.Kind == ElementsCore.Schema.FieldKind.Str ? s => FieldCodec.Gbk.GetByteCount(s) : null,
            $"ID {id}{(f.Kind == ElementsCore.Schema.FieldKind.Str ? L.T(" · GBK (cannot hold Vietnamese diacritics)") : "")}");
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
            if (_dirty) text = n > 0 ? L.F("● {0:N0} records edited · not saved", n) : L.T("● Changed · not saved");
        }
        else if (_pages.TryGetValue(_activeModule, out var p) && p.Dirty)
            text = p.ChangeCount > 0 ? L.F("● {0:N0} changes · not saved", p.ChangeCount) : L.T("● Changed · not saved");
        _lblChanges.Visible = text != null;
        if (text != null) { _lblChanges.Text = text; _lblChanges.ToolTipText = L.T("Click to see the edit history (Ctrl+H in the other tabs)"); }
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
        if (_ef == null || _currentList == null) { Msg.Show(L.T("Open elements.data and select a list first."), L.T("Export data")); return; }
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
            _lblStatus.Text = L.F("Exported {0:N0} records × {1} fields of {2} → {3}", l.Items.Count, l.Schema.Fields.Count, l.Name, sfd.FileName);
            Msg.Show(L.F("Exported {0:N0} records ({1} columns) of list {2} {3}.", l.Items.Count, l.Schema.Fields.Count, l.Index, l.Name) + "\n\n"
                + L.F("Key columns: {0} (list number), ID, {1} (position) – keep them unchanged so the import finds the right records.", ElementsExchange.ListCol, ElementsExchange.IndexCol) + "\n"
                + L.T("Empty cells on import = keep the old value. An ID not yet in the list = a new record is added."),
                L.T("Export data"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Msg.Show(L.F("Export failed:\n{0}", ex.Message), L.T("Export data"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { Cursor = Cursors.Default; }
    }

    private void ImportListData()
    {
        if (_ef == null) { Msg.Show(L.T("Open elements.data first."), L.T("Import data")); return; }
        using var ofd = new OpenFileDialog { Filter = DataSheets.OpenFilter, Title = L.T("Import data into elements.data (CSV / XLSX / JSON)"), InitialDirectory = Path.GetDirectoryName(_ef.Path) };
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
            Msg.Show(res.Summary(L.F("Import {0} into {1}", Path.GetFileName(ofd.FileName), "elements.data")), L.T("Import data"), MessageBoxButtons.OK,
                res.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            if (res.CellsChanged > 0 || res.RowsAdded > 0) { _elChanges.Visible = true; _elChanges.Reload(); }
        }
        catch (Exception ex) { Cursor = Cursors.Default; Msg.Show(L.F("Import failed:\n{0}", ex.Message), L.T("Import data"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
