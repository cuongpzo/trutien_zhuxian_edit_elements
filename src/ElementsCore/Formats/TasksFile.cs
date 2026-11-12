using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ElementsCore.Formats;

/// <summary>
/// tasks.data + tasks.data1…N (the task templates). See research/formats/tasks.md.
/// <code>
/// tasks.data (index, 20 + 16·N B):
///   u32 magic 0x69340304; u32 version (184); u32 unknown (1); u32 taskCount (top-level, all packs); u32 packCount N
///   N × md5[16]        MD5 of the whole file tasks.data{k} (k = 1…N), recomputed when saving
/// tasks.data{k} (pack):
///   u32 magic 0x06934554; u32 count (≤ 300); count × u32 offset (from file start); task blobs back to back
/// task blob:
///   u32 id; wchar name[30]; … fixed + variable data (layout not decoded) …
/// </code>
/// The task body is kept as raw bytes. Texts are located by structure:
/// <list type="bullet">
/// <item>NPC dialogue windows (talk_proc): <c>u32 id; u32 parent; u32 len; wchar text[len] (NUL-terminated);
///   u32; u32; u32 nOptions; nOptions × { u32 id; wchar text[64]; u32 param }</c></item>
/// <item>other length-prefixed texts (description, …): <c>u32 len; wchar text[len]</c>.</item>
/// </list>
/// Only those texts and the name can be edited; everything else is written back byte for byte.
/// </summary>
public sealed class TasksFile : DataFile
{
    public const uint IndexMagic = 0x69340304;
    public const uint PackMagic = 0x06934554;
    public const int IndexHeader = 20;
    public const int NameChars = 30;
    public const int OptionChars = 64;

    private static readonly Dictionary<long, string> KindEnum = new() { [0] = L.T("Dialogue"), [1] = L.T("Text") };

    public static readonly IReadOnlyList<ColumnDef> TaskColumns =
    [
        new() { Name = "pack", Type = ColType.U16, ReadOnly = true, Note = L.T("Pack file number (tasks.data{N})") },
        new() { Name = "slot", Type = ColType.U16, ReadOnly = true, Note = L.T("Position inside the pack") },
        new() { Name = "id", Type = ColType.U32, ReadOnly = true, Note = L.T("Task ID") },
        new() { Name = "name", Type = ColType.WStr, Length = NameChars, Note = L.T("Task name (max 29 characters)") },
        new() { Name = "bytes", Type = ColType.I32, ReadOnly = true, Note = L.T("Size of the task data incl. subtasks (bytes)") },
        new() { Name = "dialogues", Type = ColType.I32, ReadOnly = true, Note = L.T("Dialogue windows found in this task") },
        new() { Name = "texts", Type = ColType.I32, ReadOnly = true, Note = L.T("Other texts found in this task (description…)") },
    ];

    public static readonly IReadOnlyList<ColumnDef> TextColumns =
    [
        new() { Name = "pack", Type = ColType.U16, ReadOnly = true, Note = L.T("Pack file number (tasks.data{N})") },
        new() { Name = "slot", Type = ColType.U16, ReadOnly = true, HideInGrid = true, Note = L.T("Position inside the pack") },
        new() { Name = "task", Type = ColType.U32, ReadOnly = true, Note = L.T("ID of the top-level task holding this text") },
        new() { Name = "kind", Type = ColType.U8, ReadOnly = true, Enum = KindEnum, Note = L.T("0 = NPC dialogue window, 1 = other text (description…)") },
        new() { Name = "offset", Type = ColType.I32, ReadOnly = true, HideInGrid = true, Note = L.T("Byte offset inside the task data (as read)") },
        new() { Name = "text", Type = ColType.WStr, Note = L.T("Text (length is stored, no size limit)") },
    ];

    public static readonly IReadOnlyList<ColumnDef> OptionColumns =
    [
        new() { Name = "pack", Type = ColType.U16, ReadOnly = true, Note = L.T("Pack file number (tasks.data{N})") },
        new() { Name = "slot", Type = ColType.U16, ReadOnly = true, HideInGrid = true, Note = L.T("Position inside the pack") },
        new() { Name = "task", Type = ColType.U32, ReadOnly = true, Note = L.T("ID of the top-level task holding this text") },
        new() { Name = "offset", Type = ColType.I32, ReadOnly = true, HideInGrid = true, Note = L.T("Byte offset inside the task data (as read)") },
        new() { Name = "option", Type = ColType.U32, ReadOnly = true, Note = L.T("Option ID (0x80000000+ = built-in action)") },
        new() { Name = "text", Type = ColType.WStr, Length = OptionChars, Note = L.T("Option button text (max 63 characters)") },
    ];

    // ───────────────────────── state ─────────────────────────

    private sealed class Pack
    {
        public int Number;
        public byte[] Data = [];
        public int[] Offsets = [];          // count + 1 (last = file length)
        public byte[]? Rebuilt;             // cached bytes after edits (null = Data)
        public int Count => Offsets.Length - 1;
        public ReadOnlySpan<byte> Blob(int slot) => Data.AsSpan(Offsets[slot], Offsets[slot + 1] - Offsets[slot]);
    }

    private enum ItemKind : byte { Dialogue = 0, Text = 1, Option = 2 }

    /// <summary>One editable text found in a blob (offset of its length prefix / fixed buffer).</summary>
    private readonly record struct Found(ItemKind Kind, int Offset, int Chars, bool Nul, uint OptionId, string Value);

    private sealed class Slot
    {
        public Pack Pack = null!;
        public int Index;
        public Record TaskRow = null!;
        public string OrigName = "";
        public List<(Found F, Record Row)> Items = new();
    }

    private readonly List<Pack> _packs = new();
    private readonly List<Slot> _slots = new();
    private byte[] _index = [];
    private bool _indexMode;

    public override string FormatName => _indexMode
        ? $"tasks.data (v{BinaryPrimitives.ReadUInt32LittleEndian(_index.AsSpan(4))}, {_packs.Count} packs)"
        : $"tasks.data{_packs[0].Number} (pack, {_packs[0].Count} tasks)";

    public override bool SavesSeveralFiles => true;

    public RecordTable Tasks => Tables[0];
    public RecordTable Texts => Tables[1];
    public RecordTable Options => Tables[2];

    // ───────────────────────── sniff / parse ─────────────────────────

    public static bool SniffIndex(byte[] d) =>
        d.Length >= IndexHeader && BinaryPrimitives.ReadUInt32LittleEndian(d) == IndexMagic &&
        d.Length == IndexHeader + 16L * BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16));

    public static bool SniffPack(byte[] d)
    {
        if (d.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(d) != PackMagic) return false;
        long n = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        if (n == 0) return d.Length == 8;
        return 8 + 4 * n <= d.Length && BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8)) == 8 + 4 * n;
    }

    public static bool Sniff(byte[] d) => SniffIndex(d) || SniffPack(d);

    /// <summary>Pack number from "tasks.data17" (0 when the name has no number).</summary>
    public static int PackNumber(string path)
    {
        var n = System.IO.Path.GetFileName(path);
        int i = n.LastIndexOf(".data", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && int.TryParse(n.AsSpan(i + 5), out int k) ? k : 0;
    }

    public static TasksFile Parse(byte[] data, string path)
    {
        var f = new TasksFile { Path = path };
        if (SniffIndex(data))
        {
            f._indexMode = true;
            f._index = data;
            int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
            uint ver = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            if (ver != 184) f.Warnings.Add(L.F("tasks.data version {0}; only version 184 was verified.", ver));
            var dir = System.IO.Path.GetDirectoryName(path) ?? ".";
            var packs = new Pack?[n];
            Parallel.For(1, n + 1, k =>
            {
                var p = System.IO.Path.Combine(dir, "tasks.data" + k);
                if (!File.Exists(p)) throw new FileNotFoundException(L.F("Pack file missing: {0}", p));
                var bytes = File.ReadAllBytes(p);
                var pk = ReadPack(bytes, k, p);
                if (!MD5.HashData(bytes).AsSpan().SequenceEqual(data.AsSpan(IndexHeader + 16 * (k - 1), 16)))
                    lock (f.Warnings) f.Warnings.Add(L.F("tasks.data{0}: MD5 differs from the index (fixed when saving).", k));
                packs[k - 1] = pk;
            });
            f._packs.AddRange(packs!);
            long total = f._packs.Sum(p => (long)p.Count);
            uint declared = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
            if (total != declared) f.Warnings.Add(L.F("Index says {0} tasks, the packs hold {1}.", declared, total));
        }
        else if (SniffPack(data))
        {
            f._packs.Add(ReadPack(data, PackNumber(path), path));
        }
        else throw new InvalidDataException(L.T("Not a tasks.data index or pack file."));

        f.Build();
        return f;
    }

    private static Pack ReadPack(byte[] d, int number, string path)
    {
        if (!SniffPack(d)) throw new InvalidDataException(L.F("{0} is not a task pack (magic 0x{1:X8}).", System.IO.Path.GetFileName(path), d.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(d) : 0));
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        var offs = new int[n + 1];
        for (int i = 0; i < n; i++) offs[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8 + 4 * i));
        offs[n] = d.Length;
        for (int i = 0; i < n; i++)
            if (offs[i] > offs[i + 1] || offs[i + 1] - offs[i] < 4 + NameChars * 2)
                throw new InvalidDataException(L.F("{0}: task {1} has an invalid offset.", System.IO.Path.GetFileName(path), i));
        return new Pack { Number = number, Data = d, Offsets = offs };
    }

    /// <summary>Scans every blob (in parallel) and fills the three tables.</summary>
    private void Build()
    {
        var perPack = new List<Found>[_packs.Count][];
        Parallel.For(0, _packs.Count, i =>
        {
            var pk = _packs[i];
            var arr = new List<Found>[pk.Count];
            for (int s = 0; s < pk.Count; s++) arr[s] = Scan(pk.Blob(s));
            perPack[i] = arr;
        });

        var tasks = new RecordTable
        {
            Name = "Tasks", Columns = TaskColumns, CanAddRemove = false, KeyColumn = 2, NameColumn = 3,
            Note = L.T("Top-level tasks of every pack. Only the name can be edited here; the rest of the task data is kept as is."),
        };
        var texts = new RecordTable
        {
            Name = "Texts", Columns = TextColumns, CanAddRemove = false, KeyColumn = 2,
            Note = L.T("Dialogue windows and other texts found inside the tasks (subtasks included)."),
        };
        var opts = new RecordTable
        {
            Name = "Options", Columns = OptionColumns, CanAddRemove = false, KeyColumn = 2,
            Note = L.T("Buttons of the dialogue windows (fixed 64-character fields)."),
        };
        for (int i = 0; i < _packs.Count; i++)
        {
            var pk = _packs[i];
            for (int s = 0; s < pk.Count; s++)
            {
                var blob = pk.Blob(s);
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(blob);
                var row = new Record(TaskColumns.Count);
                var nameRaw = blob.Slice(4, NameChars * 2).ToArray();
                string name = FieldCodec.DecodeWString(nameRaw, 0, NameChars);
                row.Values[0] = (ushort)pk.Number; row.Values[1] = (ushort)s; row.Values[2] = id;
                row.Values[3] = name; row.Raw[3] = nameRaw;
                row.Values[4] = blob.Length;
                var found = perPack[i][s];
                row.Values[5] = found.Count(x => x.Kind == ItemKind.Dialogue);
                row.Values[6] = found.Count(x => x.Kind == ItemKind.Text);
                tasks.Rows.Add(row);
                var slot = new Slot { Pack = pk, Index = s, TaskRow = row, OrigName = name };
                foreach (var x in found)
                {
                    Record r;
                    if (x.Kind == ItemKind.Option)
                    {
                        r = new Record(OptionColumns.Count);
                        r.Values[0] = (ushort)pk.Number; r.Values[1] = (ushort)s; r.Values[2] = id;
                        r.Values[3] = x.Offset; r.Values[4] = x.OptionId; r.Values[5] = x.Value;
                        r.Raw[5] = blob.Slice(x.Offset, OptionChars * 2).ToArray();
                        opts.Rows.Add(r);
                    }
                    else
                    {
                        r = new Record(TextColumns.Count);
                        r.Values[0] = (ushort)pk.Number; r.Values[1] = (ushort)s; r.Values[2] = id;
                        r.Values[3] = (byte)x.Kind; r.Values[4] = x.Offset; r.Values[5] = x.Value;
                        texts.Rows.Add(r);
                    }
                    slot.Items.Add((x, r));
                }
                _slots.Add(slot);
            }
        }
        Tables.Clear();
        Tables.Add(tasks);
        Tables.Add(texts);
        Tables.Add(opts);
    }

    // ───────────────────────── text detection ─────────────────────────

    private static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(p));
    private static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(p));

    private static bool IsCjk(int c) => c is >= 0x4E00 and <= 0x9FFF;

    /// <summary>
    /// Characters accepted in a text: Chinese, ASCII, Latin-1, Vietnamese letters, punctuation, symbols,
    /// CJK punctuation / kana, full-width forms. Narrow on purpose so that binary data does not pass.
    /// </summary>
    private static bool IsTextChar(int c) =>
        IsCjk(c) || c is >= 0x20 and < 0x7F || c is 9 or 10 or 13 || c is >= 0xA0 and <= 0xFF || IsVietExtra(c) ||
        c is >= 0x2B0 and <= 0x309 || c == 0x323 || c is >= 0x391 and <= 0x3C9 || c is >= 0x2000 and <= 0x206F || c is >= 0xFE30 and <= 0xFE4F ||
        c is >= 0x2100 and <= 0x27BF || c is >= 0x3000 and <= 0x30FF || c is >= 0xFF00 and <= 0xFFEF;

    /// <summary>Vietnamese letters outside Latin-1 (ă đ ĩ ũ ơ ư and ạ…ỹ).</summary>
    private static bool IsVietExtra(int c) =>
        c is 0x102 or 0x103 or 0x110 or 0x111 or 0x128 or 0x129 or 0x168 or 0x169 or 0x1A0 or 0x1A1 or 0x1AF or 0x1B0 ||
        c is >= 0x1EA0 and <= 0x1EF9;

    private static bool IsVietLetter(int c) => c is >= 0xC0 and <= 0xFF and not 0xD7 and not 0xF7 || IsVietExtra(c);

    /// <summary>Fixed NUL-padded UTF-16 buffer: text chars, a NUL, then only zero bytes. Null when not.</summary>
    private static string? FixedText(ReadOnlySpan<byte> b, int p, int chars)
    {
        int n = 0;
        while (n < chars && U16(b, p + 2 * n) != 0)
        {
            if (!IsTextChar(U16(b, p + 2 * n))) return null;
            n++;
        }
        if (n == chars) return null;
        if (b.Slice(p + 2 * n, 2 * (chars - n)).ContainsAnyExcept((byte)0)) return null;
        return Encoding.Unicode.GetString(b.Slice(p, 2 * n));
    }

    /// <summary>Dialogue window at <paramref name="p"/>; returns its end offset or -1.</summary>
    private static int Window(ReadOnlySpan<byte> b, int p, List<Found> into)
    {
        int L = b.Length;
        if (p + 12 > L) return -1;
        uint wid = U32(b, p), par = U32(b, p + 4), n = U32(b, p + 8);
        if (n < 2 || n > 4000) return -1;
        if (wid > 0xFFFF && wid < 0x80000000) return -1;
        if (!(par == 0xFFFFFFFF || par <= 0xFFFF || par >= 0x80000000)) return -1;
        int q = p + 12;
        if (q + 2 * (int)n + 12 > L) return -1;
        if (U16(b, q + 2 * ((int)n - 1)) != 0) return -1;
        int cjk = 0, letters = 0;
        for (int i = 0; i < n - 1; i++)
        {
            int c = U16(b, q + 2 * i);
            if (c == 0 || !IsTextChar(c)) return -1;
            if (IsCjk(c)) cjk++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' || IsVietLetter(c)) letters++;
        }
        if (cjk == 0 && letters == 0 && n < 3) return -1;
        string text = Encoding.Unicode.GetString(b.Slice(q, 2 * ((int)n - 1)));
        q += 2 * (int)n + 8;
        uint no = U32(b, q);
        q += 4;
        if (no > 20 || q + 136L * no > L) return -1;
        int start = into.Count;
        into.Add(new Found(ItemKind.Dialogue, p + 8, (int)n, true, 0, text));
        for (int o = 0; o < no; o++)
        {
            var t = FixedText(b, q + 4, OptionChars);
            if (t == null) { into.RemoveRange(start, into.Count - start); return -1; }
            if (t.Length > 0) into.Add(new Found(ItemKind.Option, q + 4, OptionChars, false, U32(b, q), t));
            q += 136;
        }
        return q;
    }

    /// <summary>Finds the editable texts of one task blob, in file order.</summary>
    private static List<Found> Scan(ReadOnlySpan<byte> b)
    {
        var list = new List<Found>();
        int L = b.Length;
        var covered = new List<(int A, int B)>();
        // 1) dialogue windows (+ options)
        for (int p = 64; p + 12 <= L;)
        {
            if (b[p + 11] == 0 && b[p + 10] < 0x10 && (b[p + 8] | b[p + 9] << 8 | b[p + 10] << 16) >= 2)
            {
                int e = Window(b, p, list);
                if (e > 0) { covered.Add((p, e)); p = e; continue; }
            }
            p++;
        }
        // 2) other length-prefixed texts, outside the windows
        var extra = new List<Found>();
        int ci = 0;
        for (int p = 64; p + 4 <= L;)
        {
            while (ci < covered.Count && covered[ci].B <= p) ci++;
            if (ci < covered.Count && covered[ci].A <= p) { p = covered[ci].B; continue; }
            uint n = U32(b, p);
            if (n >= 2 && n <= 8000 && p + 4 + 2 * n <= L && (ci >= covered.Count || p + 4 + 2 * n <= covered[ci].A))
            {
                int q = p + 4, chars = (int)n;
                bool nul = U16(b, q + 2 * (chars - 1)) == 0;
                int body = nul ? chars - 1 : chars;
                int cjk = 0, letters = 0, ascii = 0, viet = 0;
                bool ok = body >= 2;
                for (int i = 0; ok && i < body; i++)
                {
                    int c = U16(b, q + 2 * i);
                    if (c == 0 || !IsTextChar(c)) ok = false;
                    else if (IsCjk(c)) cjk++;
                    else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') { letters++; ascii++; }
                    else if (IsVietLetter(c)) { letters++; viet++; }
                }
                // a text that is really the head of a longer one (count field + fixed buffer) must not pass
                if (ok && !nul && q + 2 * chars + 2 <= L)
                {
                    int next = U16(b, q + 2 * chars);
                    if (next != 0 && IsTextChar(next)) ok = false;
                }
                if (ok && (cjk >= 2 || (cjk >= 1 && body >= 4) || (body >= 6 && letters >= 4) || (viet >= 1 && ascii >= 1 && letters >= 2)))
                {
                    extra.Add(new Found(ItemKind.Text, p, chars, nul, 0, Encoding.Unicode.GetString(b.Slice(q, 2 * body))));
                    p = q + 2 * chars;
                    continue;
                }
            }
            p++;
        }
        if (extra.Count > 0) { list.AddRange(extra); list.Sort((x, y) => x.Offset.CompareTo(y.Offset)); }
        return list;
    }

    // ───────────────────────── writing ─────────────────────────

    private static string Cur(Record r, int col) => r.Values[col] as string ?? "";

    private static bool SlotDirty(Slot s)
    {
        if (Cur(s.TaskRow, 3) != s.OrigName) return true;
        foreach (var (f, r) in s.Items)
            if (Cur(r, 5) != f.Value) return true;
        return false;
    }

    /// <summary>Task blob with the edited name/texts spliced in (original bytes when nothing changed),
    /// plus the offset of every text in the new blob.</summary>
    private static (byte[] Bytes, int[] Offsets) BuildBlob(Slot s, bool force = false)
    {
        var src = s.Pack.Blob(s.Index);
        if (!force && !SlotDirty(s)) return (src.ToArray(), s.Items.Select(x => x.F.Offset).ToArray());
        var w = new BinWriter();
        var offs = new int[s.Items.Count];
        w.Bytes(src[..4]);
        string name = Cur(s.TaskRow, 3);
        if (name == s.OrigName && s.TaskRow.Raw[3] is { } raw) w.Bytes(raw);
        else w.Bytes(RecordIO.EncodeFixed(TaskColumns[3], name));
        int pos = 4 + NameChars * 2;
        for (int i = 0; i < s.Items.Count; i++)
        {
            var (f, r) = s.Items[i];
            w.Bytes(src[pos..f.Offset]);
            offs[i] = w.Length;
            string v = Cur(r, 5);
            if (f.Kind == ItemKind.Option)
            {
                if (v == f.Value) w.Bytes(src.Slice(f.Offset, OptionChars * 2));
                else w.Bytes(RecordIO.EncodeFixed(OptionColumns[5], v));
                pos = f.Offset + OptionChars * 2;
            }
            else
            {
                if (v == f.Value) w.Bytes(src.Slice(f.Offset, 4 + 2 * f.Chars));
                else
                {
                    if (v.Contains('\0')) throw new InvalidDataException(L.T("A text cannot contain the NUL character."));
                    if (f.Kind == ItemKind.Dialogue && v.Length == 0) throw new InvalidDataException(L.F("Task {0}: a dialogue window text cannot be empty.", s.TaskRow.Long(2)));
                    w.U32((uint)(v.Length + (f.Nul ? 1 : 0)));
                    w.Bytes(Encoding.Unicode.GetBytes(v));
                    if (f.Nul) w.U16(0);
                }
                pos = f.Offset + 4 + 2 * f.Chars;
            }
        }
        w.Bytes(src[pos..]);
        return (w.ToArray(), offs);
    }

    private sealed record PackBuild(byte[] Bytes, bool Changed, List<int[]> Offsets);

    private List<Slot> SlotsOf(Pack pk) => _slots.Where(s => s.Pack == pk).ToList();

    private PackBuild BuildPack(Pack pk, bool force = false)
    {
        var slots = SlotsOf(pk);
        if (!force && !slots.Any(SlotDirty)) return new(pk.Data, false, []);
        var blobs = slots.Select(s => BuildBlob(s, force)).ToList();
        var w = new BinWriter();
        w.U32(PackMagic);
        w.U32((uint)blobs.Count);
        int off = 8 + 4 * blobs.Count;
        foreach (var b in blobs) { w.U32((uint)off); off += b.Bytes.Length; }
        foreach (var b in blobs) w.Bytes(b.Bytes);
        return new(w.ToArray(), true, blobs.Select(b => b.Offsets).ToList());
    }

    private byte[] BuildIndex(IReadOnlyList<byte[]> packBytes)
    {
        var idx = (byte[])_index.Clone();
        for (int i = 0; i < packBytes.Count && IndexHeader + 16 * (i + 1) <= idx.Length; i++)
            MD5.HashData(packBytes[i]).CopyTo(idx, IndexHeader + 16 * i);
        return idx;
    }

    /// <summary>Index bytes (index mode) or the pack bytes (single pack opened).</summary>
    public override byte[] ToBytes()
    {
        var packs = _packs.Select(p => BuildPack(p).Bytes).ToList();
        return _indexMode ? BuildIndex(packs) : packs[0];
    }

    /// <summary>Self-test: rebuilds every task through the editing path (as if all texts were retyped).</summary>
    public bool RebuildEqualsOriginal(out string? firstDiff)
    {
        firstDiff = null;
        foreach (var pk in _packs)
        {
            var b = BuildPack(pk, force: true).Bytes;
            if (!b.AsSpan().SequenceEqual(pk.Data)) { firstDiff = $"tasks.data{pk.Number}"; return false; }
        }
        return true;
    }

    /// <summary>
    /// Writes the changed packs (each with a .bak copy) and the index with the new MD5s. When a single pack
    /// is open, also updates its MD5 in the tasks.data next to it. Every rebuilt pack is read back first:
    /// each text must read back as the value typed, otherwise nothing is written.
    /// </summary>
    public override string? Save(string path, bool backup = true)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        bool sameDir = string.Equals(dir, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)), StringComparison.OrdinalIgnoreCase);

        var builds = _packs.Select(pk => BuildPack(pk)).ToList();
        for (int i = 0; i < _packs.Count; i++)
            if (builds[i].Changed) Verify(_packs[i], builds[i]);

        string? firstBak = null;
        if (_indexMode)
        {
            for (int i = 0; i < _packs.Count; i++)
            {
                if (sameDir && !builds[i].Changed) continue;
                var target = System.IO.Path.Combine(dir, "tasks.data" + _packs[i].Number);
                firstBak ??= WriteFile(target, builds[i].Bytes, backup && builds[i].Changed);
            }
            var idx = BuildIndex(builds.Select(b => b.Bytes).ToList());
            var ib = WriteFile(path, idx, backup && !idx.AsSpan().SequenceEqual(_index));
            firstBak ??= ib;
            _index = idx;
        }
        else
        {
            firstBak = WriteFile(path, builds[0].Bytes, backup && builds[0].Changed);
            // keep the index next to it in step
            var idxPath = System.IO.Path.Combine(dir, "tasks.data");
            int k = PackNumber(path);
            if (k > 0 && File.Exists(idxPath))
            {
                var idx = File.ReadAllBytes(idxPath);
                if (SniffIndex(idx) && IndexHeader + 16 * k <= idx.Length)
                {
                    var md5 = MD5.HashData(builds[0].Bytes);
                    if (!md5.AsSpan().SequenceEqual(idx.AsSpan(IndexHeader + 16 * (k - 1), 16)))
                    {
                        md5.CopyTo(idx, IndexHeader + 16 * (k - 1));
                        WriteFile(idxPath, idx, backup);
                    }
                }
            }
        }

        // the written bytes become the new original
        for (int i = 0; i < _packs.Count; i++)
            if (builds[i].Changed) Rebase(_packs[i], builds[i]);
        Path = path;
        return firstBak;
    }

    /// <summary>Reads every text of a rebuilt pack back at its new offset.</summary>
    private void Verify(Pack pk, PackBuild build)
    {
        var np = ReadPack(build.Bytes, pk.Number, Path);
        int k = 0;
        foreach (var s in SlotsOf(pk))
        {
            var blob = np.Blob(s.Index);
            var offs = build.Offsets[k++];
            if (BinaryPrimitives.ReadUInt32LittleEndian(blob) != (uint)s.TaskRow.Long(2) ||
                FieldCodec.DecodeWString(blob.Slice(4, NameChars * 2), 0, NameChars) != Cur(s.TaskRow, 3))
                throw new InvalidDataException(L.F("Task {0}: the name does not read back the same – nothing was written.", s.TaskRow.Long(2)));
            for (int i = 0; i < s.Items.Count; i++)
            {
                var (f, r) = s.Items[i];
                string got = f.Kind == ItemKind.Option
                    ? FieldCodec.DecodeWString(blob.Slice(offs[i], OptionChars * 2), 0, OptionChars)
                    : ReadLen(blob, offs[i], f.Nul);
                if (got != Cur(r, 5))
                    throw new InvalidDataException(L.F("Task {0}: the text \"{1}\" does not read back the same – nothing was written.", s.TaskRow.Long(2), Cur(r, 5)));
            }
        }
    }

    private static string ReadLen(ReadOnlySpan<byte> b, int p, bool nul)
    {
        int n = (int)U32(b, p);
        return Encoding.Unicode.GetString(b.Slice(p + 4, 2 * (nul ? n - 1 : n)));
    }

    /// <summary>After saving: the pack's new bytes become the reference (offsets, original values).</summary>
    private void Rebase(Pack pk, PackBuild build)
    {
        var np = ReadPack(build.Bytes, pk.Number, Path);
        pk.Data = np.Data;
        pk.Offsets = np.Offsets;
        int k = 0;
        foreach (var s in SlotsOf(pk))
        {
            var blob = pk.Blob(s.Index);
            var offs = build.Offsets[k++];
            s.OrigName = Cur(s.TaskRow, 3);
            s.TaskRow.Raw[3] = blob.Slice(4, NameChars * 2).ToArray();
            s.TaskRow.Values[4] = blob.Length;
            for (int i = 0; i < s.Items.Count; i++)
            {
                var (f, r) = s.Items[i];
                string v = Cur(r, 5);
                int chars = f.Kind == ItemKind.Option ? OptionChars : v.Length + (f.Nul ? 1 : 0);
                if (f.Kind == ItemKind.Option) { r.Values[3] = offs[i]; r.Raw[5] = blob.Slice(offs[i], OptionChars * 2).ToArray(); }
                else r.Values[4] = offs[i];
                s.Items[i] = (f with { Offset = offs[i], Chars = chars, Value = v }, r);
            }
        }
    }

    private static string? WriteFile(string path, byte[] bytes, bool backup)
    {
        string? bak = null;
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path))
        {
            if (backup)
            {
                bak = $"{path}.bak_{DateTime.Now:yyyyMMdd_HHmmss}";
                if (!File.Exists(bak)) File.Copy(path, bak, overwrite: false);
            }
            File.Replace(tmp, path, null);
        }
        else File.Move(tmp, path);
        return bak;
    }

    /// <summary>Snapshot of the values as read / last saved (the edit-history baseline), without re-reading the packs.</summary>
    public override DataFile? OriginalSnapshot()
    {
        var snap = new SnapshotFile(FormatName);
        var tasks = new RecordTable { Name = Tasks.Name, Columns = TaskColumns, CanAddRemove = false, KeyColumn = 2, NameColumn = 3 };
        var texts = new RecordTable { Name = Texts.Name, Columns = TextColumns, CanAddRemove = false, KeyColumn = 2 };
        var opts = new RecordTable { Name = Options.Name, Columns = OptionColumns, CanAddRemove = false, KeyColumn = 2 };
        foreach (var s in _slots)
        {
            var t = s.TaskRow.Clone();
            t.Values[3] = s.OrigName;
            tasks.Rows.Add(t);
        }
        // rows keep their table order; map each current row to its original value
        var orig = new Dictionary<Record, string>(ReferenceEqualityComparer.Instance);
        foreach (var s in _slots) foreach (var (f, r) in s.Items) orig[r] = f.Value;
        foreach (var r in Texts.Rows) { var c = r.Clone(); c.Values[5] = orig[r]; texts.Rows.Add(c); }
        foreach (var r in Options.Rows) { var c = r.Clone(); c.Values[5] = orig[r]; opts.Rows.Add(c); }
        snap.Tables.Add(tasks); snap.Tables.Add(texts); snap.Tables.Add(opts);
        return snap;
    }

    private sealed class SnapshotFile(string name) : DataFile
    {
        public override string FormatName => name;
        public override byte[] ToBytes() => throw new NotSupportedException();
    }
}
