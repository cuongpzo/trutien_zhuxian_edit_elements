using System.Buffers.Binary;
using System.Text;

namespace ElementsCore;

/// <summary>NPC dialog trees stored after the last list ("talk_proc").</summary>
public sealed class TalkDialog
{
    public int Id { get; set; }
    /// <summary>wchar[64]</summary>
    public byte[] NameRaw { get; set; } = new byte[128];
    public List<TalkWindow> Windows { get; } = new();

    public string Name
    {
        get => FieldCodec.DecodeWString(NameRaw, 0, 64);
        set => NameRaw = TalkProc.EncodeFixed(value, 64, NameRaw);
    }
}

public sealed class TalkWindow
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    /// <summary>Exact characters stored in the file (usually ends with '\0').</summary>
    public string RawText { get; set; } = "";
    public List<TalkOption> Options { get; } = new();

    public string Text
    {
        get { int i = RawText.IndexOf('\0'); return i < 0 ? RawText : RawText[..i]; }
        set => RawText = (value ?? "").Replace("\0", "") + "\0";
    }
}

public sealed class TalkOption
{
    public int Id { get; set; }
    /// <summary>wchar[64]</summary>
    public byte[] TextRaw { get; set; } = new byte[128];
    public int Param { get; set; }

    public string Text
    {
        get => FieldCodec.DecodeWString(TextRaw, 0, 64);
        set => TextRaw = TalkProc.EncodeFixed(value, 64, TextRaw);
    }
}

public static class TalkProc
{
    public const int FixedChars = 64;

    internal static byte[] EncodeFixed(string value, int chars, byte[] previous)
    {
        if (FieldCodec.DecodeWString(previous, 0, chars) == value) return previous;
        if (value.Length > chars - 1) throw new ArgumentException($"Tối đa {chars - 1} ký tự (đang {value.Length})");
        var b = new byte[chars * 2];
        Encoding.Unicode.GetBytes(value).CopyTo(b, 0);
        return b;
    }

    /// <summary>Parses the whole buffer; returns null when it is not a well-formed talk_proc block.</summary>
    public static List<TalkDialog>? TryParse(byte[] data)
    {
        try
        {
            int p = 0;
            int I() { var v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p)); p += 4; return v; }
            byte[] B(int n) { if (n < 0 || p + n > data.Length) throw new FormatException(); var b = data.AsSpan(p, n).ToArray(); p += n; return b; }

            int count = I();
            if (count < 0 || count > 1_000_000) return null;
            var list = new List<TalkDialog>(count);
            for (int d = 0; d < count; d++)
            {
                var dlg = new TalkDialog { Id = I(), NameRaw = B(128) };
                int nw = I();
                if (nw < 0 || nw > 100_000) return null;
                for (int w = 0; w < nw; w++)
                {
                    var win = new TalkWindow { Id = I(), ParentId = I() };
                    int len = I();
                    if (len < 0) return null;
                    win.RawText = Encoding.Unicode.GetString(B(len * 2));
                    int no = I();
                    if (no < 0 || no > 100_000) return null;
                    for (int o = 0; o < no; o++)
                        win.Options.Add(new TalkOption { Id = I(), TextRaw = B(128), Param = I() });
                    dlg.Windows.Add(win);
                }
                list.Add(dlg);
            }
            return p == data.Length ? list : null;
        }
        catch { return null; }
    }

    public static void Write(BinaryWriter bw, List<TalkDialog> dialogs)
    {
        bw.Write(dialogs.Count);
        foreach (var d in dialogs)
        {
            bw.Write(d.Id);
            bw.Write(d.NameRaw);
            bw.Write(d.Windows.Count);
            foreach (var w in d.Windows)
            {
                bw.Write(w.Id);
                bw.Write(w.ParentId);
                bw.Write(w.RawText.Length);
                bw.Write(Encoding.Unicode.GetBytes(w.RawText));
                bw.Write(w.Options.Count);
                foreach (var o in w.Options)
                {
                    bw.Write(o.Id);
                    bw.Write(o.TextRaw);
                    bw.Write(o.Param);
                }
            }
        }
    }
}
