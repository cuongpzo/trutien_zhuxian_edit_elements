using System.Text.Json;

namespace ElementsEditor;

/// <summary>
/// Per-module history of opened files, kept in the logged-in user's Documents folder:
/// <c>Documents\TruTien Elements Editor\recent-files.json</c> (state: recent list + the file
/// open in each tab, restored at start-up) and <c>open-history.log</c> (append-only log).
/// </summary>
public sealed class RecentStore
{
    public const int MaxPerModule = 20;

    public string? ActiveModule { get; set; }
    public Dictionary<string, ModuleState> Modules { get; set; } = new();

    public sealed class ModuleState
    {
        public List<RecentEntry> Files { get; set; } = new();
        /// <summary>The file open in this tab when the program was closed (reopened at start).</summary>
        public string? Open { get; set; }
    }

    public sealed class RecentEntry
    {
        public string Path { get; set; } = "";
        public DateTime LastOpened { get; set; }
    }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TruTien Elements Editor");
    public static string StatePath => Path.Combine(Folder, "recent-files.json");
    public static string LogPath => Path.Combine(Folder, "open-history.log");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static RecentStore Load()
    {
        try { if (File.Exists(StatePath)) return JsonSerializer.Deserialize<RecentStore>(File.ReadAllText(StatePath), Json) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch { }
    }

    public ModuleState For(string module)
    {
        if (!Modules.TryGetValue(module, out var s)) Modules[module] = s = new ModuleState();
        return s;
    }

    public IReadOnlyList<RecentEntry> Recent(string module) => For(module).Files;

    /// <summary>Records that a file was opened in a tab (moves it to the top) and saves.</summary>
    public void Opened(string module, string path)
    {
        var s = For(module);
        s.Files.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        s.Files.Insert(0, new RecentEntry { Path = path, LastOpened = DateTime.Now });
        if (s.Files.Count > MaxPerModule) s.Files.RemoveRange(MaxPerModule, s.Files.Count - MaxPerModule);
        s.Open = path;
        Save();
        Log("OPEN", module, path);
    }

    public void Closed(string module)
    {
        var s = For(module);
        if (s.Open != null) Log("CLOSE", module, s.Open);
        s.Open = null;
        Save();
    }

    public void Saved(string module, string path) => Log("SAVE", module, path);

    public void Forget(string module, string path)
    {
        For(module).Files.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void SetActive(string module)
    {
        if (ActiveModule == module) return;
        ActiveModule = module;
        Save();
    }

    public static void Log(string action, string module, string path)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{action}\t{module}\t{path}{Environment.NewLine}");
        }
        catch { }
    }
}
