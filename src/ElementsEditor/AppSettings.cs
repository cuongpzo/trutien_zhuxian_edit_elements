using System.Text.Json;

namespace ElementsEditor;

public sealed class AppSettings
{
    public string? LastFile { get; set; }
    public bool AutoOpenLast { get; set; } = true;
    public bool DarkTheme { get; set; } = true;
    /// <summary>Hán-Việt dictionary (ChinesePhienAmWords.txt) used by bulk Việt hoá.</summary>
    public string? HanVietDict { get; set; }

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
