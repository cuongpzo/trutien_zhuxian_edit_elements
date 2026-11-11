namespace ElementsEditor;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Theme.Dark = AppSettings.Load().DarkTheme;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            TryLogError(e.Exception);
            ShowError(e.Exception);
        };
        try
        {
            Application.Run(new MainForm(args));
        }
        catch (Exception ex)
        {
            TryLogError(ex);
            ShowError(ex);
        }
    }

    private static void ShowError(Exception ex)
    {
        try { Msg.Show(ex.ToString(), "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { MessageBox.Show(ex.ToString(), "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static void TryLogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElementsEditor");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "last_error.log"), ex.ToString());
        }
        catch { }
    }
}
