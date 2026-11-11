using System.Text;

namespace ElementsEditor;

/// <summary>Fixed-width hex dump so a byte offset maps directly to a text position.</summary>
public static class HexFormatter
{
    public const int BytesPerLine = 16;
    private const int OffsetWidth = 6;                 // "0000  "
    private const int HexWidth = BytesPerLine * 3;     // "xx " * 16
    public const int LineLength = OffsetWidth + HexWidth + 1 + BytesPerLine + 1; // + ' ' + ascii + '\n'
    public const int MaxBytes = 128 * 1024;

    public static string Format(byte[] data)
    {
        int len = Math.Min(data.Length, MaxBytes);
        var sb = new StringBuilder((len / BytesPerLine + 1) * LineLength);
        for (int line = 0; line * BytesPerLine < len; line++)
        {
            int start = line * BytesPerLine;
            sb.Append(start.ToString("X4")).Append("  ");
            for (int i = 0; i < BytesPerLine; i++)
                sb.Append(start + i < len ? data[start + i].ToString("x2") + " " : "   ");
            sb.Append(' ');
            for (int i = 0; i < BytesPerLine; i++)
            {
                if (start + i >= len) { sb.Append(' '); continue; }
                byte b = data[start + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '·');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Text position of the hex digits of byte <paramref name="offset"/>.</summary>
    public static int HexPos(int offset) => offset / BytesPerLine * LineLength + OffsetWidth + offset % BytesPerLine * 3;
}
