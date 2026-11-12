using System.Runtime.InteropServices;
using ElementsCore;

namespace ElementsEditor;

/// <summary>A control that rebuilds texts it composed itself (counts, joined strings…) when the language changes.</summary>
public interface ILocalizable
{
    void OnLanguageChanged();
}

/// <summary>
/// Live language switch: walks every open window and maps the texts already shown to the new language
/// (<see cref="L.Retranslate"/>), without rebuilding any control, so nothing flickers and open files,
/// selections and unsaved edits stay as they are. Only UI chrome is touched (labels, buttons, menus,
/// tabs, column headers, tooltips, placeholders); text boxes and grid cells hold game data and are left alone.
/// </summary>
public static class UiText
{
    /// <summary>Shared tooltip for controls whose tooltip should follow the language.</summary>
    public static readonly ToolTip Tips = new();

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rc, IntPtr rgn, uint flags);
    private const int WM_SETREDRAW = 0x000B;
    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_FRAME = 0x400;

    /// <summary>Re-translates every open form (painting suspended while it runs).</summary>
    public static void RelocalizeAll()
    {
        foreach (Form f in Application.OpenForms.Cast<Form>().ToList())
        {
            bool h = f.IsHandleCreated && f.Visible;
            if (h) SendMessage(f.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            f.SuspendLayout();
            try { Relocalize(f); }
            finally
            {
                f.ResumeLayout(true);
                if (h)
                {
                    SendMessage(f.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                    RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME);
                }
            }
        }
    }

    private static string? Tr(string? s) => L.Retranslate(s);

    private static void Set(Control c, string? text)
    {
        if (text != null && text != c.Text) c.Text = text;
    }

    public static void Relocalize(Control c)
    {
        switch (c)
        {
            case TextBoxBase tb:
                if (tb is TextBox t && !string.IsNullOrEmpty(t.PlaceholderText)) t.PlaceholderText = Tr(t.PlaceholderText) ?? "";
                break; // the text itself is data
            case ComboBox or NumericUpDown or DateTimePicker:
                break;
            case DataGridView g:
                foreach (DataGridViewColumn col in g.Columns)
                {
                    var h = Tr(col.HeaderText);
                    if (h != col.HeaderText) col.HeaderText = h;
                    if (!string.IsNullOrEmpty(col.ToolTipText)) col.ToolTipText = Tr(col.ToolTipText) ?? "";
                }
                g.Invalidate();
                break;
            case ListView lv:
                foreach (ColumnHeader ch in lv.Columns) { var h = Tr(ch.Text); if (h != ch.Text) ch.Text = h; }
                foreach (ListViewGroup gr in lv.Groups) gr.Header = Tr(gr.Header) ?? "";
                break;
            case ToolStrip ts:
                foreach (ToolStripItem it in ts.Items) Item(it);
                break;
            case TreeView:
                break;
            case Form or GameTextPreview or WebBrowser:
                break; // window titles are composed by the form itself; previews show game text
            default:
                if (!string.IsNullOrEmpty(c.Text)) Set(c, Tr(c.Text));
                break;
        }
        var tip = Tips.GetToolTip(c);
        if (!string.IsNullOrEmpty(tip)) { var n = Tr(tip); if (n != tip) Tips.SetToolTip(c, n); }
        if (c.ContextMenuStrip != null) foreach (ToolStripItem it in c.ContextMenuStrip.Items) Item(it);
        foreach (Control child in c.Controls) Relocalize(child);
        if (c is ILocalizable loc) loc.OnLanguageChanged();
    }

    private static void Item(ToolStripItem it)
    {
        if (it is not ToolStripTextBox and not ToolStripComboBox)
        {
            var t = Tr(it.Text);
            if (t != it.Text) it.Text = t;
        }
        if (!string.IsNullOrEmpty(it.ToolTipText)) it.ToolTipText = Tr(it.ToolTipText);
        if (it is ToolStripDropDownItem dd)
            foreach (ToolStripItem sub in dd.DropDownItems) Item(sub);
    }
}
