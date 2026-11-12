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
/// dropping a new json file into the folder. The language can be switched while the program runs:
/// <see cref="Load"/> raises <see cref="Changed"/>, and texts built earlier are mapped to the new language
/// with <see cref="Retranslate"/> (reverse lookup over every language loaded so far).
/// </summary>
public static class L
{
    public sealed record Language(string Code, string Name, string EnglishName, string Path);

    private static Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    /// <summary>Translation → English, for every language loaded in this run (to re-translate texts built earlier).</summary>
    private static readonly Dictionary<string, string> _reverse = new(StringComparer.Ordinal);

    /// <summary>Raised after <see cref="Load"/> switched the language.</summary>
    public static event Action? Changed;

    /// <summary>Bumped on every language switch (for caches of translated text).</summary>
    public static int Version { get; private set; }

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

    /// <summary>Loads a language (unknown / "en" = English). Can be called again at any time to switch.</summary>
    public static void Load(string? code)
    {
        LoadCore(code);
        Version++;
        Changed?.Invoke();
    }

    private static void LoadCore(string? code)
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
                    {
                        _strings[p.Name] = v;
                        _reverse.TryAdd(v, p.Name);
                    }
            Code = lang.Code;
        }
        catch { }
    }

    /// <summary>Translation of an English UI text (the text itself when there is none).</summary>
    public static string T(string english) => _strings.TryGetValue(english, out var t) ? t : english;

    /// <summary>English source of a text shown in any language loaded so far (the text itself when unknown).</summary>
    public static string English(string shown) => _reverse.TryGetValue(shown, out var en) ? en : shown;

    /// <summary>
    /// A text that was translated earlier (maybe in another language) in the current language.
    /// Multi-line texts are mapped line by line; unknown text (data, numbers…) is returned unchanged.
    /// </summary>
    public static string? Retranslate(string? shown)
    {
        if (string.IsNullOrEmpty(shown)) return shown;
        return Map(shown) ?? Parts(shown, 0) ?? shown;
    }

    /// <summary>Separators used to join translated pieces into one label (tried in this order).</summary>
    private static readonly string[] Joins = ["\n", "   ·   ", "  ·  ", "    "];

    /// <summary>Maps a joined text piece by piece; null when no piece is known.</summary>
    private static string? Parts(string s, int level)
    {
        for (int j = level; j < Joins.Length; j++)
        {
            var sep = Joins[j];
            if (!s.Contains(sep)) continue;
            var parts = s.Split(sep);
            bool any = false;
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                bool cr = sep == "\n" && p.EndsWith('\r');
                var core = cr ? p[..^1] : p;
                if (core.Trim().Length == 0) continue;
                var t = Map(core) ?? Parts(core, j + 1);
                if (t != null && t != core) { parts[i] = cr ? t + "\r" : t; any = true; }
            }
            return any ? string.Join(sep, parts) : null;
        }
        return null;
    }

    /// <summary>Current-language form of a whole text known as English or as an earlier translation; null = unknown.</summary>
    private static string? Map(string s)
    {
        if (_strings.ContainsKey(s)) return T(s);                 // English source
        if (_reverse.TryGetValue(s, out var en)) return T(en);    // translated earlier
        return null;
    }

    /// <summary>Translated format string ("Saved {0}") filled with the arguments.</summary>
    public static string F(string englishFormat, params object?[] args)
    {
        var fmt = T(englishFormat);
        try { return string.Format(CultureInfo.CurrentCulture, fmt, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, englishFormat, args); }
    }
}
