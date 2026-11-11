using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ElementsCore.Schema;

namespace ElementsCore;

/// <summary>Reads and writes field values inside a raw record buffer.</summary>
public static class FieldCodec
{
    public static Encoding Gbk { get; } = InitGbk();
    private static Encoding InitGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string DecodeWString(ReadOnlySpan<byte> buf, int off, int chars)
    {
        var span = buf.Slice(off, Math.Min(chars * 2, buf.Length - off));
        int n = 0;
        while (n + 1 < span.Length && (span[n] != 0 || span[n + 1] != 0)) n += 2;
        return Encoding.Unicode.GetString(span[..n]);
    }

    public static string DecodeGbk(ReadOnlySpan<byte> buf, int off, int len)
    {
        var span = buf.Slice(off, Math.Min(len, buf.Length - off));
        int n = span.IndexOf((byte)0);
        if (n < 0) n = span.Length;
        return Gbk.GetString(span[..n]);
    }

    /// <summary>Max characters a text field can hold (one slot kept for the terminating zero).</summary>
    public static int MaxTextLength(FieldDef f) => f.Kind switch
    {
        FieldKind.WStr => Math.Max(0, f.Length - 1),
        FieldKind.Str => Math.Max(0, f.Length - 1),
        _ => 0,
    };

    /// <summary>Kind after applying "when" overrides (e.g. addon param stored as float).</summary>
    public static FieldKind EffectiveKind(byte[] item, FieldDef f, ListSchema schema)
    {
        if (f.When == null) return f.Kind;
        foreach (var w in f.When)
        {
            var cond = schema.Find(w.Field);
            if (cond == null || cond.IsText || cond.Kind == FieldKind.Bytes) continue;
            if (w.In.Contains(ReadInteger(item, cond, cond.Kind))) return w.Kind;
        }
        return f.Kind;
    }

    public static long ReadInteger(byte[] item, FieldDef f) => ReadInteger(item, f, f.Kind);

    public static long ReadInteger(byte[] item, FieldDef f, FieldKind kind)
    {
        var s = item.AsSpan(f.Offset);
        return kind switch
        {
            FieldKind.I8 => (sbyte)s[0],
            FieldKind.U8 or FieldKind.Bool => s[0],
            FieldKind.I16 => BinaryPrimitives.ReadInt16LittleEndian(s),
            FieldKind.U16 => BinaryPrimitives.ReadUInt16LittleEndian(s),
            FieldKind.I32 => BinaryPrimitives.ReadInt32LittleEndian(s),
            FieldKind.U32 => BinaryPrimitives.ReadUInt32LittleEndian(s),
            FieldKind.I64 => BinaryPrimitives.ReadInt64LittleEndian(s),
            FieldKind.U64 => (long)BinaryPrimitives.ReadUInt64LittleEndian(s),
            FieldKind.F32 => BinaryPrimitives.ReadInt32LittleEndian(s),
            FieldKind.F64 => BinaryPrimitives.ReadInt64LittleEndian(s),
            _ => 0,
        };
    }

    public static string GetText(byte[] item, FieldDef f) => f.Kind switch
    {
        FieldKind.WStr => DecodeWString(item, f.Offset, f.Length),
        FieldKind.Str => DecodeGbk(item, f.Offset, f.Length),
        _ => throw new InvalidOperationException($"{f.Path} is not a text field"),
    };

    public static string Format(byte[] item, FieldDef f, ListSchema schema)
    {
        var kind = EffectiveKind(item, f, schema);
        var s = item.AsSpan(f.Offset);
        return kind switch
        {
            FieldKind.WStr or FieldKind.Str => GetText(item, f),
            FieldKind.Bytes => Convert.ToHexString(item, f.Offset, f.Length),
            FieldKind.F32 => BinaryPrimitives.ReadSingleLittleEndian(s).ToString("R", Inv),
            FieldKind.F64 => BinaryPrimitives.ReadDoubleLittleEndian(s).ToString("R", Inv),
            FieldKind.U64 => BinaryPrimitives.ReadUInt64LittleEndian(s).ToString(Inv),
            _ => ReadInteger(item, f, kind).ToString(Inv),
        };
    }

    /// <summary>Parses <paramref name="text"/> and writes it into the record. Returns true if bytes changed.</summary>
    public static bool SetFromString(byte[] item, FieldDef f, ListSchema schema, string text)
    {
        var kind = EffectiveKind(item, f, schema);
        var before = item.AsSpan(f.Offset, f.ByteSize).ToArray();
        var s = item.AsSpan(f.Offset, f.ByteSize);
        text = text.Trim(kind is FieldKind.WStr or FieldKind.Str ? Array.Empty<char>() : new[] { ' ', '\t' });
        switch (kind)
        {
            case FieldKind.WStr:
            case FieldKind.Str:
                if (GetText(item, f) == text) return false;
                SetText(item, f, text);
                break;
            case FieldKind.Bytes:
                var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
                var bytes = Convert.FromHexString(hex);
                if (bytes.Length != f.Length) throw new FormatException(L.F("Exactly {0} bytes (hex) required", f.Length));
                bytes.CopyTo(s);
                break;
            case FieldKind.F32:
                BinaryPrimitives.WriteSingleLittleEndian(s, float.Parse(text.Replace(',', '.'), NumberStyles.Float, Inv));
                break;
            case FieldKind.F64:
                BinaryPrimitives.WriteDoubleLittleEndian(s, double.Parse(text.Replace(',', '.'), NumberStyles.Float, Inv));
                break;
            case FieldKind.Bool:
                s[0] = text.ToLowerInvariant() switch { "true" or "1" => 1, "false" or "0" => 0, _ => byte.Parse(text, Inv) };
                break;
            case FieldKind.I8: s[0] = (byte)sbyte.Parse(text, Inv); break;
            case FieldKind.U8: s[0] = byte.Parse(text, Inv); break;
            case FieldKind.I16: BinaryPrimitives.WriteInt16LittleEndian(s, short.Parse(text, Inv)); break;
            case FieldKind.U16: BinaryPrimitives.WriteUInt16LittleEndian(s, ushort.Parse(text, Inv)); break;
            case FieldKind.I32: BinaryPrimitives.WriteInt32LittleEndian(s, ParseInt32(text)); break;
            case FieldKind.U32: BinaryPrimitives.WriteUInt32LittleEndian(s, ParseUInt32(text)); break;
            case FieldKind.I64: BinaryPrimitives.WriteInt64LittleEndian(s, long.Parse(text, Inv)); break;
            case FieldKind.U64: BinaryPrimitives.WriteUInt64LittleEndian(s, ulong.Parse(text, Inv)); break;
        }
        return !s.SequenceEqual(before);
    }

    private static int ParseInt32(string t) =>
        t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? unchecked((int)Convert.ToUInt32(t[2..], 16)) : int.Parse(t, Inv);
    private static uint ParseUInt32(string t) =>
        t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(t[2..], 16) : uint.Parse(t, Inv);

    /// <summary>Writes a text field, zero-filling the rest of the buffer. Throws if too long.</summary>
    public static void SetText(byte[] item, FieldDef f, string text)
    {
        var max = MaxTextLength(f);
        byte[] enc;
        if (f.Kind == FieldKind.WStr)
        {
            enc = Encoding.Unicode.GetBytes(text);
            if (text.Length > max || enc.Length > max * 2) throw new TextTooLongException(f, text.Length, max);
        }
        else if (f.Kind == FieldKind.Str)
        {
            enc = Gbk.GetBytes(text);
            if (enc.Length > max) throw new TextTooLongException(f, enc.Length, max);
        }
        else throw new InvalidOperationException($"{f.Path} is not a text field");
        var s = item.AsSpan(f.Offset, f.ByteSize);
        s.Clear();
        enc.CopyTo(s);
    }

    /// <summary>Short info for UI: enum label / mask flags.</summary>
    public static string Describe(byte[] item, FieldDef f, ListSchema schema, SchemaRepository repo)
    {
        if (f.Enum != null && !f.IsText && f.Kind != FieldKind.Bytes && repo.Enums.TryGetValue(f.Enum, out var e))
            return e.Describe(ReadInteger(item, f));
        var k = EffectiveKind(item, f, schema);
        if (k != f.Kind) return $"({k.ToString().ToLowerInvariant()})";
        return "";
    }
}

public sealed class TextTooLongException(FieldDef field, int length, int max)
    : Exception(L.F("'{0}' is {1} characters long, maximum {2}", field.Path, length, max))
{
    public FieldDef Field { get; } = field;
    public int TextLength { get; } = length;
    public int Max { get; } = max;
}
