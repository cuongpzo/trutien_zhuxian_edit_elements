using System.Buffers.Binary;
using System.IO.Compression;

namespace ElementsCore.Assets;

/// <summary>
/// Read-only Angelica engine package (*.pck, with *.pkx continuation for files over 2 GB).
/// Layout (version 0x00020002 / 0x00020003, as Client0619's surfaces.pck):
/// <code>
/// u32 0x4DCA23EF; u32/u64 size; file data (each zlib-compressed unless stored)…
/// file table: per entry { u32 len ^ KEY1; u32 len ^ KEY2; entry bytes (zlib if len &lt; entry size) }
///   entry: char path[260] (GBK); [u32 pad – 0x20003 only]; offset (u32 / u64); u32 size; u32 compressedSize; u32 reserved
/// tail: u32 0xFDFDFEEE; u32 version; table offset ^ KEY1 (u32 / u64); … ; u32 entryCount; u32 version
/// </code>
/// The game files are only read here, never written.
/// </summary>
public sealed class PckArchive : IDisposable
{
    public const uint Key1 = 0xA8937462, Key2 = 0xF1A43653;

    public readonly record struct Entry(string Path, long Offset, int Size, int CompressedSize);

    private readonly FileStream _pck;
    private readonly FileStream? _pkx;
    private readonly long _pckLength;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public string PathOnDisk { get; }
    public uint Version { get; }
    public int Count => _entries.Count;
    public IEnumerable<Entry> Entries => _entries.Values;

    public PckArchive(string path)
    {
        PathOnDisk = path;
        _pck = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        _pckLength = _pck.Length;
        var pkx = System.IO.Path.ChangeExtension(path, ".pkx");
        if (File.Exists(pkx)) _pkx = new FileStream(pkx, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);

        Span<byte> tail = stackalloc byte[8];
        ReadAt(_pckLength - 8, tail);
        int count = BinaryPrimitives.ReadInt32LittleEndian(tail);
        Version = BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
        bool v64 = Version == 0x00020003;
        if (Version is not (0x00020002 or 0x00020003)) throw new InvalidDataException($"Gói pck phiên bản lạ 0x{Version:X8}.");

        // table offset sits right after the 0xFDFDFEEE tag + version
        long tableOffset;
        if (v64)
        {
            Span<byte> b = stackalloc byte[8];
            ReadAt(_pckLength - 280, b);
            tableOffset = (long)(BinaryPrimitives.ReadUInt64LittleEndian(b) ^ unchecked((ulong)(long)(int)Key1));
        }
        else
        {
            Span<byte> b = stackalloc byte[4];
            ReadAt(_pckLength - 272, b);
            tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(b) ^ Key1;
        }
        if (tableOffset <= 0 || tableOffset >= _pckLength) throw new InvalidDataException("Không tìm thấy bảng file của gói pck (khoá khác bản PW?).");

        var table = new byte[_pckLength - tableOffset];
        ReadAt(tableOffset, table);
        int entrySize = v64 ? 284 : 276;
        int p = 0;
        for (int i = 0; i < count && p + 8 <= table.Length; i++)
        {
            int len = (int)(BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(p)) ^ Key1);
            p += 8;
            if (len <= 0 || p + len > table.Length) break;
            var raw = table.AsSpan(p, len);
            p += len;
            byte[] e = len < entrySize ? Inflate(raw.ToArray(), entrySize) : raw.ToArray();
            int z = Array.IndexOf(e, (byte)0, 0, Math.Min(260, e.Length));
            var name = FieldCodec.Gbk.GetString(e, 0, z < 0 ? 260 : z).Replace('/', '\\');
            long off = v64 ? BinaryPrimitives.ReadInt64LittleEndian(e.AsSpan(264)) : BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(260));
            int at = v64 ? 272 : 264;
            int size = BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(at));
            int csize = BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(at + 4));
            _entries[name] = new Entry(name, off, size, csize);
        }
    }

    public bool Contains(string path) => _entries.ContainsKey(Normalize(path));

    public bool TryGet(string path, out Entry e) => _entries.TryGetValue(Normalize(path), out e);

    public static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');

    /// <summary>File content, or null when the path is not in the package.</summary>
    public byte[]? Read(string path)
    {
        if (!TryGet(path, out var e)) return null;
        var buf = new byte[e.CompressedSize];
        ReadAt(e.Offset, buf);
        return e.CompressedSize < e.Size ? Inflate(buf, e.Size) : buf;
    }

    private void ReadAt(long offset, Span<byte> dest)
    {
        lock (_lock)
        {
            int done = 0;
            while (done < dest.Length)
            {
                long pos = offset + done;
                // offsets past the .pck size continue in the .pkx
                var fs = pos < _pckLength ? _pck : _pkx ?? throw new InvalidDataException("Thiếu file .pkx đi kèm gói pck.");
                long local = pos < _pckLength ? pos : pos - _pckLength;
                int want = (int)Math.Min(dest.Length - done, pos < _pckLength ? _pckLength - pos : long.MaxValue);
                fs.Position = local;
                int n = fs.Read(dest.Slice(done, want));
                if (n <= 0) throw new EndOfStreamException();
                done += n;
            }
        }
    }

    private static byte[] Inflate(byte[] data, int size)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        var outp = new byte[size];
        int done = 0;
        while (done < size)
        {
            int n = z.Read(outp, done, size - done);
            if (n <= 0) break;
            done += n;
        }
        return done == size ? outp : outp[..done];
    }

    public void Dispose()
    {
        _pck.Dispose();
        _pkx?.Dispose();
    }
}
