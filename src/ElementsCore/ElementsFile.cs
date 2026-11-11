using System.Buffers.Binary;
using ElementsCore.Schema;

namespace ElementsCore;

public sealed class ElementList
{
    public int Index { get; init; }
    /// <summary>Raw marker bytes stored right before this list (checksum / exporter / tag), may be empty.</summary>
    public byte[] Prefix { get; init; } = Array.Empty<byte>();
    public MarkerKind? PrefixKind { get; init; }
    public long FileOffset { get; init; }
    public int ItemSize { get; init; }
    public List<byte[]> Items { get; init; } = new();
    public ListSchema Schema { get; set; } = null!;

    public string Name => Schema.Struct;

    public long GetId(int item)
    {
        var f = Schema.IdField;
        if (f == null || f.IsText) return item;
        if (f.Kind == FieldKind.Bytes) return f.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(Items[item].AsSpan(f.Offset)) : item;
        return FieldCodec.ReadInteger(Items[item], f);
    }

    public string GetName(int item)
    {
        var f = Schema.NameField;
        return f == null ? "" : FieldCodec.GetText(Items[item], f);
    }

    /// <summary>File offset of item <paramref name="item"/> as loaded (approximate after edits).</summary>
    public long ItemFileOffset(int item) => FileOffset + 8 + (long)item * ItemSize;
}

public sealed class ElementsFile
{
    public byte[] Header { get; private set; } = new byte[8];
    public ushort Version => BinaryPrimitives.ReadUInt16LittleEndian(Header);
    public ushort Signature => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(2));
    public uint Timestamp => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(4));

    public List<ElementList> Lists { get; } = new();
    /// <summary>Parsed NPC dialogs, or null if the tail could not be parsed (then <see cref="RawTail"/> is kept).</summary>
    public List<TalkDialog>? Dialogs { get; private set; }
    public byte[] RawTail { get; private set; } = Array.Empty<byte>();
    public LayoutInfo? Layout { get; private set; }
    public string? Path { get; private set; }
    public List<string> Warnings { get; } = new();

    public static ElementsFile Load(string path, SchemaRepository repo, IProgress<int>? progress = null)
    {
        var data = File.ReadAllBytes(path);
        var f = Parse(data, repo, progress);
        f.Path = path;
        return f;
    }

    public static ElementsFile Parse(byte[] d, SchemaRepository repo, IProgress<int>? progress = null)
    {
        if (d.Length < 8) throw new InvalidDataException(L.T("File too small"));
        var ef = new ElementsFile { Header = d.AsSpan(0, 8).ToArray() };
        ef.Layout = repo.GetLayout(ef.Version);
        if (ef.Layout == null) ef.Warnings.Add(L.F("No layout for version {0}: detecting lists automatically.", ef.Version));
        else if (ef.Layout.Version != "v" + ef.Version) ef.Warnings.Add(L.F("No layout v{0}, using {1} for now.", ef.Version, ef.Layout.Version));

        long p = 8;
        int expected = ef.Layout?.ListCount ?? int.MaxValue;
        for (int i = 0; i < expected; i++)
        {
            long start = p;
            MarkerKind? mk = null;
            if (ef.Layout != null && ef.Layout.Markers.TryGetValue(i, out var kind)) mk = kind;
            else if (IsHexTag(d, p)) mk = MarkerKind.Checksum;

            if (mk != null)
            {
                p += mk switch
                {
                    MarkerKind.Checksum => 8,
                    MarkerKind.Exporter => 12 + (long)ReadI32(d, p + 4),
                    MarkerKind.Tag => 8 + (long)ReadI32(d, p + 4),
                    _ => 0,
                };
            }
            if (p + 8 > d.Length) { p = start; break; }
            int size = ReadI32(d, p), count = ReadI32(d, p + 4);
            bool ok = size > 0 && size <= 1_000_000 && count >= 0 && p + 8 + (long)size * count <= d.Length;
            if (!ok)
            {
                if (ef.Layout == null || !ef.Layout.ListCountVerified) { p = start; break; }
                throw new InvalidDataException(L.F("List {0} is broken at offset 0x{1:X} (size={2}, count={3})", i, p, size, count));
            }
            var prefix = d.AsSpan((int)start, (int)(p - start)).ToArray();
            var items = new List<byte[]>(count);
            long q = p + 8;
            for (int k = 0; k < count; k++, q += size) items.Add(d.AsSpan((int)q, size).ToArray());
            var list = new ElementList { Index = i, Prefix = prefix, PrefixKind = mk, FileOffset = p, ItemSize = size, Items = items };
            list.Schema = repo.Resolve(ef.Version, i, size, items.Take(30).ToList());
            ef.Lists.Add(list);
            p = q;
            progress?.Report((int)(p * 100 / d.Length));
        }

        var tail = d.AsSpan((int)p).ToArray();
        ef.Dialogs = tail.Length > 0 ? TalkProc.TryParse(tail) : null;
        if (ef.Dialogs == null)
        {
            ef.RawTail = tail;
            if (tail.Length > 0) ef.Warnings.Add(L.F("Cannot read NPC Dialogs ({0} bytes) – kept as raw data.", tail.Length));
        }
        return ef;
    }

    private static int ReadI32(byte[] d, long p) => p + 4 <= d.Length ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)p)) : -1;

    private static bool IsHexTag(byte[] d, long p)
    {
        if (p + 8 > d.Length) return false;
        for (int i = 0; i < 8; i++) if (!Uri.IsHexDigit((char)d[p + i])) return false;
        return true;
    }

    public void Write(Stream s)
    {
        using var bw = new BinaryWriter(s, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write(Header);
        foreach (var l in Lists)
        {
            bw.Write(l.Prefix);
            bw.Write(l.ItemSize);
            bw.Write(l.Items.Count);
            foreach (var it in l.Items) bw.Write(it);
        }
        if (Dialogs != null) TalkProc.Write(bw, Dialogs);
        else bw.Write(RawTail);
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        Write(ms);
        return ms.ToArray();
    }

    /// <summary>Saves via a temp file; backs up the existing file first. Returns the backup path (or null).</summary>
    public string? Save(string path, bool backup = true)
    {
        string? bak = null;
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)) Write(fs);
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
