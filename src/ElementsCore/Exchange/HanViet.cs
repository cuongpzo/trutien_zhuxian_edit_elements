using System.Globalization;
using System.Text;

namespace ElementsCore.Exchange;

public enum HanVietCase
{
    /// <summary>Tích Thiện Y (Nữ) – names of items, NPCs, skills.</summary>
    EveryWord,
    /// <summary>Tích thiện y (nữ) – first letter of the text only.</summary>
    FirstWord,
    /// <summary>tích thiện y (nữ)</summary>
    Lower,
}

/// <summary>
/// Sino-Vietnamese (Hán Việt) transliteration from a QuickTranslator-style dictionary
/// (<c>ChinesePhienAmWords.txt</c>: one "字=âm" per line, UTF-8). Every Chinese character becomes its
/// reading; colour codes (^RRGGBB), digits, Latin letters and symbols are kept; full-width
/// punctuation becomes ASCII. Characters missing from the dictionary are kept as they are.
/// </summary>
public sealed class HanViet
{
    private readonly Dictionary<int, string> _map = new();
    public int Count => _map.Count;
    public string Source { get; }

    public const string DefaultPath = @"E:\Quick Translator 2016 - Revo\Data\ChinesePhienAmWords.txt";

    private static readonly Dictionary<char, string> Punct = new()
    {
        ['（'] = "(", ['）'] = ")", ['【'] = "[", ['】'] = "]", ['〔'] = "[", ['〕'] = "]", ['［'] = "[", ['］'] = "]",
        ['《'] = "«", ['》'] = "»", ['〈'] = "<", ['〉'] = ">", ['「'] = "\"", ['」'] = "\"", ['『'] = "\"", ['』'] = "\"",
        ['“'] = "\"", ['”'] = "\"", ['‘'] = "'", ['’'] = "'",
        ['，'] = ",", ['、'] = ",", ['。'] = ".", ['：'] = ":", ['；'] = ";", ['！'] = "!", ['？'] = "?",
        ['～'] = "~", ['　'] = " ", ['…'] = "…", ['—'] = "-", ['－'] = "-", ['·'] = "·", ['・'] = "·",
        ['＋'] = "+", ['％'] = "%", ['／'] = "/", ['＊'] = "*", ['＝'] = "=", ['＃'] = "#", ['＆'] = "&",
    };

    private HanViet(string source) { Source = source; }

    public static HanViet Load(string path)
    {
        var hv = new HanViet(path);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim('\uFEFF');
            var val = line[(eq + 1)..].Trim();
            // one character (BMP or surrogate pair) per key; first reading when several are given ("ơ,o")
            var e = StringInfo.GetTextElementEnumerator(key);
            if (!e.MoveNext() || e.GetTextElement().Length != key.Length) continue;
            int cp = char.ConvertToUtf32(key, 0);
            int comma = val.IndexOf(',');
            if (comma > 0) val = val[..comma].Trim();
            if (val.Length == 0 || val == ",") continue;
            hv._map.TryAdd(cp, val.ToLowerInvariant());
        }
        if (hv._map.Count == 0) throw new InvalidDataException("Không đọc được mục nào (cần dạng 字=âm, mỗi dòng một chữ).");
        return hv;
    }

    public static bool IsCjk(int cp) =>
        cp is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3134F;

    public static bool HasCjk(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            int cp = char.IsSurrogatePair(s, i) ? char.ConvertToUtf32(s, i++) : s[i];
            if (IsCjk(cp)) return true;
        }
        return false;
    }

    /// <summary>Converts the text; <paramref name="unknown"/> = Chinese characters not in the dictionary (kept).</summary>
    public string Convert(string text, HanVietCase mode, out int unknown)
    {
        unknown = 0;
        var sb = new StringBuilder(text.Length * 3);
        bool prevWord = false;   // previous output was a Vietnamese syllable
        bool firstDone = false;
        bool afterCode = false;  // just wrote ^RRGGBB or a literal \n – no space after it
        for (int i = 0; i < text.Length; i++)
        {
            // keep colour codes ^RRGGBB untouched
            if (text[i] == '^' && IsHex6(text, i + 1))
            {
                // "Tích^ff0000Thiện" would show as "TíchThiện": the space goes before the code
                if (prevWord && i + 7 < text.Length && (char.IsLetterOrDigit(text[i + 7]) || text[i + 7] is '(' or '[' or '（' or '【'))
                    sb.Append(' ');
                sb.Append(text, i, 7);
                i += 6;
                afterCode = true;
                prevWord = false;
                continue;
            }
            int cp = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i];
            int len = cp > 0xFFFF ? 2 : 1;
            if (IsCjk(cp) && _map.TryGetValue(cp, out var reading))
            {
                bool literalBreak = sb.Length >= 2 && sb[^2] == '\\' && sb[^1] is 'n' or 'r' or 't';
                if (sb.Length > 0 && !afterCode && !literalBreak && NeedsSpaceBefore(sb[^1], prevWord)) sb.Append(' ');
                afterCode = false;
                bool cap = mode == HanVietCase.EveryWord || mode == HanVietCase.FirstWord && !firstDone;
                sb.Append(cap ? Capitalize(reading) : reading);
                firstDone = true;
                prevWord = true;
                i += len - 1;
                continue;
            }
            if (IsCjk(cp)) unknown++;
            string piece = Punct.TryGetValue(text[i], out var p) ? p
                : cp is >= 0xFF01 and <= 0xFF5E ? ((char)(cp - 0xFEE0)).ToString()   // full-width ASCII
                : text.Substring(i, len);
            // a word right after a reading gets a space: "Kiếm 2", "Y (Nữ)"
            if (prevWord && piece.Length > 0 && (char.IsLetterOrDigit(piece[0]) || piece[0] is '(' or '[' or '«' or '<'))
                sb.Append(' ');
            sb.Append(piece);
            if (piece.Length > 0 && char.IsLetter(piece[0]) && !IsCjk(cp)) firstDone = true;
            prevWord = false;
            afterCode = false;
            i += len - 1;
        }
        return sb.ToString();
    }

    private static bool NeedsSpaceBefore(char last, bool prevWord) =>
        prevWord || char.IsLetterOrDigit(last) || last is ')' or ']' or '»' or '>' or ',' or '.' or ':' or ';' or '!' or '?';

    private static bool IsHex6(string s, int at)
    {
        if (at + 6 > s.Length) return false;
        for (int k = at; k < at + 6; k++) if (!Uri.IsHexDigit(s[k])) return false;
        return true;
    }

    private static string Capitalize(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..];
}
