using System.Globalization;
using System.Text.Json;

namespace ElementsCore;

/// <summary>
/// UI language. The program is written in English; every visible text goes through
/// <see cref="T"/> (fixed text) or <see cref="F"/> (text with {0} {1}… placeholders). Other languages
/// are JSON files in the "lang" folder next to the exe:
/// <code>
/// { "code": "vi", "name": "Tiếng Việt", "englishName": "Vietnamese",
///   "strings": { "Open file…": "Mở file…", "Saved {0}": "Đã lưu {0}" } }
/// </code>
/// A missing entry falls back to English, so a partial translation still works. Add a language by
/// dropping a new json file into the folder. The language is chosen at start-up (restart to switch).
/// </summary>
public static class L
{
    public sealed record Language(string Code, string Name, string EnglishName, string Path);

    private static Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    /// <summary>Current language code ("en" = built-in English).</summary>
    public static string Code { get; private set; } = "en";

    public static string Folder { get; set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "lang");

    /// <summary>English plus every lang\*.json that can be read, English first.</summary>
    public static List<Language> Available()
    {
        var list = new List<Language> { new("en", "English", "English", "") };
        try
        {
            if (Directory.Exists(Folder))
                foreach (var f in Directory.GetFiles(Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(f), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                        var r = doc.RootElement;
                        var code = r.TryGetProperty("code", out var c) ? c.GetString() : System.IO.Path.GetFileNameWithoutExtension(f);
                        if (string.IsNullOrEmpty(code) || code == "en") continue;
                        var name = r.TryGetProperty("name", out var n) ? n.GetString() : code;
                        var en = r.TryGetProperty("englishName", out var e) ? e.GetString() : name;
                        list.Add(new Language(code, name ?? code, en ?? code, f));
                    }
                    catch { }
                }
        }
        catch { }
        return list;
    }

    /// <summary>Loads a language (unknown / "en" = English). Call once at start-up, before any UI is built.</summary>
    public static void Load(string? code)
    {
        _strings = new(StringComparer.Ordinal);
        Code = "en";
        if (string.IsNullOrEmpty(code) || code == "en") return;
        var lang = Available().FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        if (lang == null || lang.Path.Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(lang.Path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("strings", out var s))
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v)
                        _strings[p.Name] = v;
            Code = lang.Code;
        }
        catch { }
    }

    /// <summary>Translation of an English UI text (the text itself when there is none).</summary>
    public static string T(string english) => _strings.TryGetValue(english, out var t) ? t : english;

    /// <summary>Translated format string ("Saved {0}") filled with the arguments.</summary>
    public static string F(string englishFormat, params object?[] args)
    {
        var fmt = T(englishFormat);
        try { return string.Format(CultureInfo.CurrentCulture, fmt, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, englishFormat, args); }
    }
}
