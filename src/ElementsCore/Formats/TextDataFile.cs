using System.Text;

namespace ElementsCore.Formats;

/// <summary>
/// A text file (precinct.clt / region.clt, *.lua, *.txt …) edited as text. The original encoding,
/// byte-order mark and line endings are detected and kept, so an unedited file saves byte-exact.
/// </summary>
public sealed class TextDataFile : DataFile
{
    public Encoding Encoding { get; private set; } = Encoding.UTF8;
    public byte[] Bom { get; private set; } = [];
    public string EncodingName { get; private set; } = "UTF-8";
    /// <summary>Text with the file's own line endings.</summary>
    public string Text { get; set; } = "";
    public string NewLine { get; private set; } = "\r\n";
    private byte[]? _original;
    private string? _originalText;

    public override string FormatName => L.F("Text ({0}{1})", EncodingName, Bom.Length > 0 ? ", BOM" : "");

    public static TextDataFile Parse(byte[] data, string path)
    {
        var f = new TextDataFile { Path = path, _original = data };
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) { f.Encoding = Encoding.Unicode; f.Bom = [0xFF, 0xFE]; f.EncodingName = "UTF-16 LE"; }
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) { f.Encoding = Encoding.BigEndianUnicode; f.Bom = [0xFE, 0xFF]; f.EncodingName = "UTF-16 BE"; }
        else if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) { f.Encoding = new UTF8Encoding(false); f.Bom = [0xEF, 0xBB, 0xBF]; f.EncodingName = "UTF-8"; }
        else if (IsValidUtf8(data)) { f.Encoding = new UTF8Encoding(false); f.EncodingName = "UTF-8"; }
        else { f.Encoding = FieldCodec.Gbk; f.EncodingName = "GBK"; }
        f.Text = f.Encoding.GetString(data, f.Bom.Length, data.Length - f.Bom.Length);
        f._originalText = f.Text;
        int crlf = CountOf(f.Text, "\r\n"), lf = CountOf(f.Text, "\n") - crlf;
        f.NewLine = lf > crlf ? "\n" : "\r\n";
        if (!f.Encoding.GetBytes(f.Text).AsSpan().SequenceEqual(data.AsSpan(f.Bom.Length)))
            f.Warnings.Add(L.F("Some bytes cannot be decoded as {0}; if you edit and save, those bytes will be replaced.", f.EncodingName));
        return f;
    }

    /// <summary>Text as in the file on disk (as opened / last saved).</summary>
    public string OriginalText => _originalText ?? "";

    /// <summary>After a save: the current text becomes the "as on disk" text.</summary>
    public void MarkSaved()
    {
        _original = ToBytes();
        _originalText = Text;
    }

    public override byte[] ToBytes()
    {
        if (_original != null && Text == _originalText) return _original;
        var body = Encoding.GetBytes(Text);
        var all = new byte[Bom.Length + body.Length];
        Bom.CopyTo(all, 0);
        body.CopyTo(all, Bom.Length);
        return all;
    }

    /// <summary>Characters this encoding cannot store (e.g. Vietnamese in a GBK file).</summary>
    public string? CheckEncodable(string text)
    {
        if (Encoding.CodePage is 1200 or 1201 or 65001) return null;
        var round = Encoding.GetString(Encoding.GetBytes(text));
        if (round == text) return null;
        for (int i = 0; i < Math.Min(round.Length, text.Length); i++)
            if (round[i] != text[i]) return L.F("Character '{0}' (position {1}) cannot be saved in {2}.", text[i], i, EncodingName);
        return L.F("Some characters cannot be saved in {0}.", EncodingName);
    }

    private static int CountOf(string s, string what)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
        return n;
    }

    private static bool IsValidUtf8(byte[] d)
    {
        try { new UTF8Encoding(false, true).GetCharCount(d); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}

/// <summary>A file whose layout is not decoded yet: shown as hex + strings, saved unchanged.</summary>
public sealed class RawDataFile : DataFile
{
    public byte[] Data { get; set; } = [];
    public string Reason { get; init; } = "";
    public override string FormatName => L.T("Not decoded (hex view)");
    public static RawDataFile Parse(byte[] data, string path, string reason) => new() { Path = path, Data = data, Reason = reason };
    public override byte[] ToBytes() => Data;
}
