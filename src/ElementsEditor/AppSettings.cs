using System.Text.Json;

namespace ElementsEditor;

public sealed class AppSettings
{
    public string? LastFile { get; set; }
    public bool AutoOpenLast { get; set; } = true;
    public bool DarkTheme { get; set; } = true;
    /// <summary>Hán-Việt dictionary (ChinesePhienAmWords.txt) used by bulk Việt hoá.</summary>
    public string? HanVietDict { get; set; }
    /// <summary>Index of the elements list open last time (reopened at start).</summary>
    public int? LastList { get; set; }
    /// <summary>UI language code (lang\&lt;code&gt;.json); null / "en" = English.</summary>
    public string? Language { get; set; }
    /// <summary>Font family for game (Chinese) text; null = default.</summary>
    public string? CjkFont { get; set; }
    /// <summary>Size factor for game text (1.0 = normal).</summary>
    public float CjkFontScale { get; set; } = 1f;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElementsEditor", "settings.json");

    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
