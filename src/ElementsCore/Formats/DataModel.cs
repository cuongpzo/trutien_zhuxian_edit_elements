using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ElementsCore.Formats;

/// <summary>
/// Generic, format-independent model used by every non-elements file (gshop, path, domain, …):
/// a <see cref="DataFile"/> holds one or more <see cref="RecordTable"/>s; a table has typed
/// <see cref="ColumnDef"/>s and <see cref="Record"/> rows. A column of type <see cref="ColType.Table"/>
/// holds a nested list of records (e.g. a domain's outline points), shown as a sub-grid in the UI.
///
/// Byte-exact rule: every parser must write back exactly the bytes it read when nothing was edited.
/// Fixed text cells keep their original bytes in <see cref="Record.Raw"/> (garbage after the NUL is
/// common in these files); <see cref="RecordIO"/> re-emits them unless the cell was changed.
/// </summary>
public enum ColType { I8, U8, I16, U16, I32, U32, I64, U64, F32, F64, Bool, WStr, Str, Bytes, Table }

public sealed class ColumnDef
{
    public required string Name { get; init; }
    public required ColType Type { get; init; }
    /// <summary>WStr: UTF-16 chars (bytes = 2×Length); Str: GBK bytes; Bytes: byte count. 0 = variable (format writes the length itself).</summary>
    public int Length { get; init; }
    private readonly string? _note;
    /// <summary>Short explanation shown as tooltip / in the detail panel (in the current UI language).</summary>
    public string? Note { get => L.Retranslate(_note); init => _note = value; }
    public bool ReadOnly { get; init; }
    /// <summary>Not shown as a column of the main grid (still in the detail panel).</summary>
    public bool HideInGrid { get; init; }
    private readonly IReadOnlyDictionary<long, string>? _enum;
    private IReadOnlyDictionary<long, string>? _enumShown;
    private int _enumVersion = -1;
    /// <summary>Optional value → meaning map (display only, in the current UI language).</summary>
    public IReadOnlyDictionary<long, string>? Enum
    {
        get
        {
            if (_enum == null || L.Version == 0) return _enum;
            if (_enumVersion != L.Version)
            {
                _enumShown = _enum.ToDictionary(kv => kv.Key, kv => L.Retranslate(kv.Value) ?? "");
                _enumVersion = L.Version;
            }
            return _enumShown;
        }
        init => _enum = value;
    }
    /// <summary>Columns of the nested records when <see cref="Type"/> is <see cref="ColType.Table"/>.</summary>
    public IReadOnlyList<ColumnDef>? SubColumns { get; init; }
    /// <summary>For nested tables: whether rows may be added/removed.</summary>
    public bool SubCanAddRemove { get; init; } = true;

    public bool IsText => Type is ColType.WStr or ColType.Str;
    public bool IsInteger => Type is ColType.I8 or ColType.U8 or ColType.I16 or ColType.U16 or ColType.I32 or ColType.U32 or ColType.I64 or ColType.U64;

    /// <summary>Byte size of a fixed cell, or -1 when variable / nested.</summary>
    public int ByteSize => Type switch
    {
        ColType.I8 or ColType.U8 or ColType.Bool => 1,
        ColType.I16 or ColType.U16 => 2,
        ColType.I32 or ColType.U32 or ColType.F32 => 4,
        ColType.I64 or ColType.U64 or ColType.F64 => 8,
        ColType.WStr => Length > 0 ? Length * 2 : -1,
        ColType.Str or ColType.Bytes => Length > 0 ? Length : -1,
        _ => -1,
    };

    /// <summary>Max characters a fixed text cell can take (one slot kept for the terminating zero).</summary>
    public int MaxChars => Length > 0 ? Length - 1 : int.MaxValue;

    public string TypeLabel => Type switch
    {
        ColType.WStr => Length > 0 ? $"wstr[{Length}]" : "wstr",
        ColType.Str => Length > 0 ? $"str[{Length}]" : "str",
        ColType.Bytes => Length > 0 ? $"bytes[{Length}]" : "bytes",
        ColType.Table => "list",
        _ => Type.ToString().ToLowerInvariant(),
    };

    public object? DefaultValue() => Type switch
    {
        ColType.I8 => (sbyte)0, ColType.U8 => (byte)0, ColType.I16 => (short)0, ColType.U16 => (ushort)0,
        ColType.I32 => 0, ColType.U32 => 0u, ColType.I64 => 0L, ColType.U64 => 0UL,
        ColType.F32 => 0f, ColType.F64 => 0d, ColType.Bool => false,
        ColType.WStr or ColType.Str => "",
        ColType.Bytes => new byte[Math.Max(0, Length)],
        ColType.Table => new List<Record>(),
        _ => null,
    };
}

public sealed class Record
{
    public object?[] Values { get; }
    /// <summary>Original bytes of fixed text/bytes cells as read; cleared when the cell is set.</summary>
    public byte[]?[] Raw { get; }

    public Record(int columns)
    {
        Values = new object?[columns];
        Raw = new byte[]?[columns];
    }

    public object? this[int i]
    {
        get => Values[i];
        set { Values[i] = value; Raw[i] = null; }
    }

    public static Record CreateDefault(IReadOnlyList<ColumnDef> cols)
    {
        var r = new Record(cols.Count);
        for (int i = 0; i < cols.Count; i++) r.Values[i] = cols[i].DefaultValue();
        return r;
    }

    public Record Clone()
    {
        var r = new Record(Values.Length);
        for (int i = 0; i < Values.Length; i++)
        {
            r.Values[i] = Values[i] switch
            {
                byte[] b => (byte[])b.Clone(),
                List<Record> l => l.Select(x => x.Clone()).ToList(),
                var v => v,
            };
            r.Raw[i] = (byte[]?)Raw[i]?.Clone();
        }
        return r;
    }

    public List<Record> Table(int i) => (List<Record>)Values[i]!;
    public long Long(int i) => Convert.ToInt64(Values[i] ?? 0, CultureInfo.InvariantCulture);
    public string Text(int i) => Values[i] as string ?? "";
}

public sealed class RecordTable
{
    public required string Name { get; init; }
    public required IReadOnlyList<ColumnDef> Columns { get; init; }
    public List<Record> Rows { get; init; } = new();
    public bool CanAddRemove { get; init; } = true;
    /// <summary>Column holding the record ID (for search / display), or -1.</summary>
    public int KeyColumn { get; init; } = -1;
    /// <summary>Column holding the display name, or -1.</summary>
    public int NameColumn { get; init; } = -1;
    private readonly string? _note;
    public string? Note { get => L.Retranslate(_note); init => _note = value; }

    public int IndexOf(string column)
    {
        for (int i = 0; i < Columns.Count; i++) if (Columns[i].Name == column) return i;
        return -1;
    }

    public Record NewRecord() => Record.CreateDefault(Columns);
}

public abstract class DataFile
{
    public string Path { get; set; } = "";
    /// <summary>Human readable format, e.g. "gshop.data (Source, 2630 B/item)".</summary>
    public abstract string FormatName { get; }
    public List<RecordTable> Tables { get; } = new();
    public List<string> Warnings { get; } = new();

    public abstract byte[] ToBytes();

    /// <summary>True when <see cref="Save"/> writes more than one file and checks the result itself (tasks.data + packs).</summary>
    public virtual bool SavesSeveralFiles => false;

    /// <summary>Values as read / last saved, for the edit history; null = re-parse the original bytes.</summary>
    public virtual DataFile? OriginalSnapshot() => null;

    /// <summary>Saves via a temp file; backs up the existing file first. Returns the backup path (or null).</summary>
    public virtual string? Save(string path, bool backup = true)
    {
        var bytes = ToBytes();
        string? bak = null;
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path))
        {
            if (backup)
            {
                bak = $"{path}.bak_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Copy(path, bak, overwrite: false);
            }
            File.Replace(tmp, path, null);
        }
        else File.Move(tmp, path);
        Path = path;
        return bak;
    }
}

/// <summary>Little-endian cursor over a byte array.</summary>
public sealed class BinReader
{
    public byte[] Data { get; }
    public int Pos { get; set; }
    public BinReader(byte[] data, int pos = 0) { Data = data; Pos = pos; }
    public int Remaining => Data.Length - Pos;
    public bool AtEnd => Pos >= Data.Length;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || Pos + n > Data.Length) throw new InvalidDataException(L.F("Out of data at offset {0} (need {1} bytes, {2} left).", Pos, n, Remaining));
        var s = Data.AsSpan(Pos, n);
        Pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public sbyte I8() => (sbyte)Take(1)[0];
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public byte[] Bytes(int n) => Take(n).ToArray();
    public int PeekI32(int at) => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(at, 4));
}

/// <summary>Little-endian writer into a growable buffer.</summary>
public sealed class BinWriter
{
    private readonly MemoryStream _ms = new();
    private readonly byte[] _b = new byte[8];
    public int Length => (int)_ms.Length;

    public void U8(byte v) => _ms.WriteByte(v);
    public void I8(sbyte v) => _ms.WriteByte((byte)v);
    public void I16(short v) { BinaryPrimitives.WriteInt16LittleEndian(_b, v); _ms.Write(_b, 0, 2); }
    public void U16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(_b, v); _ms.Write(_b, 0, 2); }
    public void I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(_b, v); _ms.Write(_b, 0, 4); }
    public void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(_b, v); _ms.Write(_b, 0, 4); }
    public void I64(long v) { BinaryPrimitives.WriteInt64LittleEndian(_b, v); _ms.Write(_b, 0, 8); }
    public void U64(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(_b, v); _ms.Write(_b, 0, 8); }
    public void F32(float v) { BinaryPrimitives.WriteSingleLittleEndian(_b, v); _ms.Write(_b, 0, 4); }
    public void F64(double v) { BinaryPrimitives.WriteDoubleLittleEndian(_b, v); _ms.Write(_b, 0, 8); }
    public void Bytes(ReadOnlySpan<byte> v) => _ms.Write(v);
    /// <summary>Overwrites 4 bytes at an earlier position (e.g. a size/count written after the data).</summary>
    public void PatchI32(int at, int v)
    {
        long p = _ms.Position;
        _ms.Position = at;
        I32(v);
        _ms.Position = p;
    }
    public byte[] ToArray() => _ms.ToArray();
}

/// <summary>Generic reading/writing/formatting of typed cells.</summary>
public static class RecordIO
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Reads one fixed-size cell (not Table, not variable-length text).</summary>
    public static void ReadCell(BinReader r, ColumnDef c, Record rec, int i)
    {
        switch (c.Type)
        {
            case ColType.I8: rec.Values[i] = r.I8(); break;
            case ColType.U8: rec.Values[i] = r.U8(); break;
            case ColType.I16: rec.Values[i] = r.I16(); break;
            case ColType.U16: rec.Values[i] = r.U16(); break;
            case ColType.I32: rec.Values[i] = r.I32(); break;
            case ColType.U32: rec.Values[i] = r.U32(); break;
            case ColType.I64: rec.Values[i] = r.I64(); break;
            case ColType.U64: rec.Values[i] = r.U64(); break;
            case ColType.F32: rec.Values[i] = r.F32(); break;
            case ColType.F64: rec.Values[i] = r.F64(); break;
            case ColType.Bool:
                {
                    byte b = r.U8();
                    rec.Values[i] = b != 0;
                    if (b > 1) rec.Raw[i] = [b]; // keep odd values byte-exact
                    break;
                }
            case ColType.WStr:
                {
                    var raw = r.Bytes(c.Length * 2);
                    rec.Values[i] = FieldCodec.DecodeWString(raw, 0, c.Length);
                    rec.Raw[i] = raw;
                    break;
                }
            case ColType.Str:
                {
                    var raw = r.Bytes(c.Length);
                    rec.Values[i] = FieldCodec.DecodeGbk(raw, 0, c.Length);
                    rec.Raw[i] = raw;
                    break;
                }
            case ColType.Bytes:
                rec.Values[i] = r.Bytes(c.Length);
                break;
            default:
                throw new InvalidOperationException(L.F("Column {0}: type {1} cannot be read automatically.", c.Name, c.Type));
        }
    }

    public static void WriteCell(BinWriter w, ColumnDef c, Record rec, int i)
    {
        object? v = rec.Values[i];
        var raw = rec.Raw[i];
        switch (c.Type)
        {
            case ColType.I8: w.I8(Convert.ToSByte(v, Inv)); break;
            case ColType.U8: w.U8(Convert.ToByte(v, Inv)); break;
            case ColType.I16: w.I16(Convert.ToInt16(v, Inv)); break;
            case ColType.U16: w.U16(Convert.ToUInt16(v, Inv)); break;
            case ColType.I32: w.I32(Convert.ToInt32(v, Inv)); break;
            case ColType.U32: w.U32(Convert.ToUInt32(v, Inv)); break;
            case ColType.I64: w.I64(Convert.ToInt64(v, Inv)); break;
            case ColType.U64: w.U64(Convert.ToUInt64(v, Inv)); break;
            case ColType.F32: w.F32(Convert.ToSingle(v, Inv)); break;
            case ColType.F64: w.F64(Convert.ToDouble(v, Inv)); break;
            case ColType.Bool:
                if (raw != null) w.Bytes(raw); else w.U8((bool)(v ?? false) ? (byte)1 : (byte)0);
                break;
            case ColType.WStr:
                w.Bytes(raw ?? EncodeFixed(c, (string?)v ?? ""));
                break;
            case ColType.Str:
                w.Bytes(raw ?? EncodeFixed(c, (string?)v ?? ""));
                break;
            case ColType.Bytes:
                {
                    var b = (byte[]?)v ?? [];
                    if (c.Length > 0 && b.Length != c.Length) Array.Resize(ref b, c.Length);
                    w.Bytes(b);
                    break;
                }
            default:
                throw new InvalidOperationException(L.F("Column {0}: type {1} cannot be written automatically.", c.Name, c.Type));
        }
    }

    /// <summary>Reads a record whose columns are all fixed-size, in order.</summary>
    public static Record ReadFixed(BinReader r, IReadOnlyList<ColumnDef> cols)
    {
        var rec = new Record(cols.Count);
        for (int i = 0; i < cols.Count; i++) ReadCell(r, cols[i], rec, i);
        return rec;
    }

    public static void WriteFixed(BinWriter w, IReadOnlyList<ColumnDef> cols, Record rec)
    {
        for (int i = 0; i < cols.Count; i++) WriteCell(w, cols[i], rec, i);
    }

    public static int FixedSize(IReadOnlyList<ColumnDef> cols) => cols.Sum(c => c.ByteSize);

    /// <summary>Encodes text into a zero-padded fixed buffer (throws if it does not fit).</summary>
    public static byte[] EncodeFixed(ColumnDef c, string text)
    {
        var enc = c.Type == ColType.WStr ? Encoding.Unicode : FieldCodec.Gbk;
        var bytes = enc.GetBytes(text);
        int size = c.ByteSize;
        int room = size - (c.Type == ColType.WStr ? 2 : 1);
        if (bytes.Length > room) throw new InvalidDataException(L.F("\"{0}\": text too long ({1}/{2}).", c.Name, TextLength(c, text), c.MaxChars));
        var buf = new byte[size];
        bytes.CopyTo(buf, 0);
        return buf;
    }

    /// <summary>Length as the limit counts it: UTF-16 units for WStr, GBK bytes for Str.</summary>
    public static int TextLength(ColumnDef c, string text) =>
        c.Type == ColType.Str ? FieldCodec.Gbk.GetByteCount(text) : text.Length;

    /// <summary>Encodes a length-prefixed / variable text value.</summary>
    public static byte[] EncodeVar(ColType t, string text) => t == ColType.WStr ? Encoding.Unicode.GetBytes(text) : FieldCodec.Gbk.GetBytes(text);
    public static string DecodeVar(ColType t, byte[] raw) => t == ColType.WStr ? Encoding.Unicode.GetString(raw) : FieldCodec.Gbk.GetString(raw);

    public static string Format(ColumnDef c, object? v)
    {
        switch (v)
        {
            case null: return "";
            case string s: return s;
            case bool b: return b ? "1" : "0";
            case float f: return f.ToString("R", Inv);
            case double d: return d.ToString("R", Inv);
            case byte[] bytes: return Convert.ToHexString(bytes);
            case List<Record> list: return $"[{list.Count}]";
            case IFormattable fm: return fm.ToString(null, Inv);
            default: return v.ToString() ?? "";
        }
    }

    /// <summary>Value plus enum meaning, e.g. "3 (New item)".</summary>
    public static string Describe(ColumnDef c, object? v)
    {
        var s = Format(c, v);
        if (c.Enum != null && c.IsInteger && v != null && c.Enum.TryGetValue(Convert.ToInt64(v, Inv), out var name)) s += $" ({name})";
        return s;
    }

    public static bool TryParse(ColumnDef c, string text, out object? value, out string? error)
    {
        value = null;
        error = null;
        text = text ?? "";
        var t = text.Trim();
        try
        {
            switch (c.Type)
            {
                case ColType.I8: value = sbyte.Parse(t, NumberStyles.Integer, Inv); return true;
                case ColType.U8: value = byte.Parse(t, NumberStyles.Integer, Inv); return true;
                case ColType.I16: value = short.Parse(t, NumberStyles.Integer, Inv); return true;
                case ColType.U16: value = ushort.Parse(t, NumberStyles.Integer, Inv); return true;
                case ColType.I32: value = int.Parse(t, NumberStyles.Integer, Inv); return true;
                case ColType.U32: value = uint.Parse(t, NumberStyles.Integer, Inv); return true;
                case ColType.I64: value = long.Parse(t, NumberStyles.Integer, Inv); return true;
                case ColType.U64: value = ulong.Parse(t, NumberStyles.Integer, Inv); return true;
                case ColType.F32: value = float.Parse(t, NumberStyles.Float, Inv); return true;
                case ColType.F64: value = double.Parse(t, NumberStyles.Float, Inv); return true;
                case ColType.Bool:
                    if (t is "1" or "true" or "True" or "yes" or "có") { value = true; return true; }
                    if (t is "0" or "false" or "False" or "no" or "không" or "") { value = false; return true; }
                    error = L.T("Only 0 or 1 is accepted."); return false;
                case ColType.WStr:
                case ColType.Str:
                    if (c.Length > 0 && TextLength(c, text) > c.MaxChars)
                    {
                        error = L.F("Too long: {0}/{1} {2}.", TextLength(c, text), c.MaxChars, c.Type == ColType.Str ? L.T("GBK bytes") : L.T("characters"));
                        return false;
                    }
                    if (c.Type == ColType.Str && FieldCodec.Gbk.GetString(FieldCodec.Gbk.GetBytes(text)) != text)
                    {
                        error = L.T("Contains characters that cannot be encoded in GBK (this field cannot hold accented Vietnamese).");
                        return false;
                    }
                    value = text; return true;
                case ColType.Bytes:
                    {
                        var hex = new string(t.Where(Uri.IsHexDigit).ToArray());
                        if (hex.Length % 2 != 0) { error = L.T("The number of hex digits must be even."); return false; }
                        var b = Convert.FromHexString(hex);
                        if (c.Length > 0 && b.Length != c.Length) { error = L.F("Exactly {0} bytes required.", c.Length); return false; }
                        value = b; return true;
                    }
                default:
                    error = L.T("This type cannot be edited directly."); return false;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            error = L.F("Invalid value for {0}.", c.TypeLabel);
            return false;
        }
    }
}
