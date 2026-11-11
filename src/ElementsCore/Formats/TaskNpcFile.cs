namespace ElementsCore.Formats;

/// <summary>
/// task_npc.data: where each task NPC stands (used by the client for task auto-pathing / map marks).
/// <code>
/// u32 fileSize;      // = total file length, the client refuses a mismatch
/// u32 timeMark;      // Unix time of the export
/// u16 version;       // 2
/// u16 count;         // ≤ 65 535
/// count × { u32 npcId; u32 mapId; i16 x; i16 y; i16 z; u16 pad }   // 16 B
/// </code>
/// npcId is an NPC_ESSENCE id of elements.data; mapId 0 with (0,0,0) = no known position.
/// Coordinates are whole metres (y = height).
/// </summary>
public sealed class TaskNpcFile : DataFile
{
    public const int HeaderSize = 12;
    public const int RecordSize = 16;
    public const ushort KnownVersion = 2;

    public static readonly IReadOnlyList<ColumnDef> HeaderColumns =
    [
        new() { Name = "fileSize", Type = ColType.U32, ReadOnly = true, Note = L.T("File size (recomputed when saving)") },
        new() { Name = "timeMark", Type = ColType.U32, Note = L.T("Export timestamp (Unix)") },
        new() { Name = "version", Type = ColType.U16, Note = L.T("Format version; the client reads version 2") },
        new() { Name = "count", Type = ColType.U16, ReadOnly = true, Note = L.T("Row count (recomputed when saving)") },
    ];

    public static readonly IReadOnlyList<ColumnDef> Columns =
    [
        new() { Name = "npcId", Type = ColType.U32, Note = L.T("NPC ID (NPC_ESSENCE in elements.data)") },
        new() { Name = "mapId", Type = ColType.U32, Note = L.T("Map ID; 0 = no position yet") },
        new() { Name = "x", Type = ColType.I16, Note = L.T("X coordinate (metres)") },
        new() { Name = "y", Type = ColType.I16, Note = L.T("Height Y (metres)") },
        new() { Name = "z", Type = ColType.I16, Note = L.T("Z coordinate (metres)") },
        new() { Name = "pad", Type = ColType.U16, HideInGrid = true, Note = L.T("Padding, always 0") },
    ];

    private byte[] _trailing = [];

    public override string FormatName => $"task_npc.data (v{HeaderRow.Long(2)}, {RecordSize} B/NPC)";
    public RecordTable Header => Tables[0];
    public RecordTable Entries => Tables[1];
    private Record HeaderRow => Header.Rows[0];

    public static bool Sniff(byte[] d)
    {
        if (d.Length < HeaderSize || BitConverter.ToUInt32(d, 0) != (uint)d.Length) return false;
        int count = BitConverter.ToUInt16(d, 10);
        return HeaderSize + count * RecordSize == d.Length;
    }

    public static TaskNpcFile Parse(byte[] data, string path)
    {
        if (data.Length < HeaderSize) throw new InvalidDataException(L.T("File too short for the task_npc.data header."));
        var r = new BinReader(data);
        var f = new TaskNpcFile { Path = path };
        var head = RecordIO.ReadFixed(r, HeaderColumns);
        uint size = (uint)head.Long(0);
        int ver = (int)head.Long(2);
        int count = (int)head.Long(3);
        if (size != data.Length) f.Warnings.Add(L.F("Header says {0} bytes but the file is {1} bytes long (the client will reject it; fixed when saving).", size, data.Length));
        if (ver != KnownVersion) f.Warnings.Add(L.F("Unknown version {0} (the client reads version {1}).", ver, KnownVersion));
        if (HeaderSize + (long)count * RecordSize > data.Length)
            throw new InvalidDataException(L.F("Header declares {0} rows ({1} B) but only {2} B remain in the file.", count, count * RecordSize, r.Remaining));

        f.Tables.Add(new RecordTable
        {
            Name = "Header", Columns = HeaderColumns, CanAddRemove = false, Rows = [head],
            Note = L.T("File header; fileSize and count are recomputed when saving."),
        });
        var t = new RecordTable
        {
            Name = "Task NPC", Columns = Columns, KeyColumn = 0,
            Note = L.T("Task NPC positions: NPC ID → map + coordinates."),
        };
        for (int i = 0; i < count; i++) t.Rows.Add(RecordIO.ReadFixed(r, Columns));
        f.Tables.Add(t);

        if (!r.AtEnd)
        {
            f._trailing = r.Bytes(r.Remaining);
            f.Warnings.Add(L.F("{0} extra bytes after the table (kept when saving).", f._trailing.Length));
        }
        if (t.Rows.Any(x => x.Long(0) == 0)) f.Warnings.Add(L.T("Some rows have NPC ID 0."));
        var dup = t.Rows.GroupBy(x => x.Long(0)).Where(g => g.Count() > 1).Select(g => g.Key).Take(5).ToList();
        if (dup.Count > 0) f.Warnings.Add(L.F("Duplicate NPC IDs (the game keeps only the last row): {0}", string.Join(", ", dup)));
        return f;
    }

    public override byte[] ToBytes()
    {
        int count = Entries.Rows.Count;
        if (count > ushort.MaxValue) throw new InvalidDataException(L.F("task_npc.data holds at most {0} rows (currently {1}).", ushort.MaxValue, count));
        var h = HeaderRow;
        h.Values[0] = (uint)(HeaderSize + count * RecordSize + _trailing.Length);
        h.Values[3] = (ushort)count;
        var w = new BinWriter();
        RecordIO.WriteFixed(w, HeaderColumns, h);
        foreach (var rec in Entries.Rows) RecordIO.WriteFixed(w, Columns, rec);
        w.Bytes(_trailing);
        return w.ToArray();
    }
}
