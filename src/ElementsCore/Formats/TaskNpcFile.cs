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
        new() { Name = "fileSize", Type = ColType.U32, ReadOnly = true, Note = "Kích thước file (tự tính lại khi lưu)" },
        new() { Name = "timeMark", Type = ColType.U32, Note = "Dấu thời gian xuất file (Unix)" },
        new() { Name = "version", Type = ColType.U16, Note = "Phiên bản định dạng; client đọc bản 2" },
        new() { Name = "count", Type = ColType.U16, ReadOnly = true, Note = "Số dòng (tự tính lại khi lưu)" },
    ];

    public static readonly IReadOnlyList<ColumnDef> Columns =
    [
        new() { Name = "npcId", Type = ColType.U32, Note = "ID NPC (NPC_ESSENCE trong elements.data)" },
        new() { Name = "mapId", Type = ColType.U32, Note = "ID bản đồ; 0 = chưa có vị trí" },
        new() { Name = "x", Type = ColType.I16, Note = "Toạ độ X (mét)" },
        new() { Name = "y", Type = ColType.I16, Note = "Độ cao Y (mét)" },
        new() { Name = "z", Type = ColType.I16, Note = "Toạ độ Z (mét)" },
        new() { Name = "pad", Type = ColType.U16, HideInGrid = true, Note = "Đệm, luôn 0" },
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
        if (data.Length < HeaderSize) throw new InvalidDataException("File quá ngắn, không đủ header task_npc.data.");
        var r = new BinReader(data);
        var f = new TaskNpcFile { Path = path };
        var head = RecordIO.ReadFixed(r, HeaderColumns);
        uint size = (uint)head.Long(0);
        int ver = (int)head.Long(2);
        int count = (int)head.Long(3);
        if (size != data.Length) f.Warnings.Add($"Header ghi {size} byte nhưng file dài {data.Length} byte (client sẽ từ chối; sẽ sửa khi lưu).");
        if (ver != KnownVersion) f.Warnings.Add($"Phiên bản {ver} lạ (client đọc bản {KnownVersion}).");
        if (HeaderSize + (long)count * RecordSize > data.Length)
            throw new InvalidDataException($"Header khai {count} dòng ({count * RecordSize} B) nhưng file chỉ còn {r.Remaining} B.");

        f.Tables.Add(new RecordTable
        {
            Name = "Header", Columns = HeaderColumns, CanAddRemove = false, Rows = [head],
            Note = "Header của file; fileSize và count tự tính khi lưu.",
        });
        var t = new RecordTable
        {
            Name = "Task NPC", Columns = Columns, KeyColumn = 0,
            Note = "Vị trí NPC nhiệm vụ: ID NPC → bản đồ + toạ độ.",
        };
        for (int i = 0; i < count; i++) t.Rows.Add(RecordIO.ReadFixed(r, Columns));
        f.Tables.Add(t);

        if (!r.AtEnd)
        {
            f._trailing = r.Bytes(r.Remaining);
            f.Warnings.Add($"Còn {f._trailing.Length} byte thừa sau bảng (giữ nguyên khi lưu).");
        }
        if (t.Rows.Any(x => x.Long(0) == 0)) f.Warnings.Add("Có dòng mang ID NPC 0.");
        var dup = t.Rows.GroupBy(x => x.Long(0)).Where(g => g.Count() > 1).Select(g => g.Key).Take(5).ToList();
        if (dup.Count > 0) f.Warnings.Add("ID NPC trùng (game chỉ giữ dòng sau cùng): " + string.Join(", ", dup));
        return f;
    }

    public override byte[] ToBytes()
    {
        int count = Entries.Rows.Count;
        if (count > ushort.MaxValue) throw new InvalidDataException($"task_npc.data chứa tối đa {ushort.MaxValue} dòng (đang có {count}).");
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
