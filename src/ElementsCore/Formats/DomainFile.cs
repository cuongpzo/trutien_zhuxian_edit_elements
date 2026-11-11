namespace ElementsCore.Formats;

/// <summary>
/// domain*.data: guild-war / territory map domains (client side: outline drawn on the world map).
/// <code>
/// char[4] "MOXB"; i32 domainCount;
/// domainCount × {
///     wchar name[16];                       // UTF-16LE, NUL-padded
///     i32 id; i32 type; i32 reward;
///     i32 centerX; i32 centerY;             // label / click point on the map image
///     i32 vertCount;  vertCount × { i32 x; i32 y }        // outline polygon
///     i32 faceCount;  faceCount × { i32 a; i32 b; i32 c } // triangulation (indices into verts)
///     i32 neighbourCount; neighbourCount × i32 domainId
/// }
/// i32 battleTimeCount; battleTimeCount × { i32 weekday; i32 hour; i32 minute }
/// i32 battlesPerTimeSlot;
/// </code>
/// Files: domain.data (guild map), domain1.data (territory war), domain2.data (cross-server world map),
/// domain3.data (overall world map), domain4.data (ancient world), domain5.data, domainlandwar.data (land war).
/// </summary>
public sealed class DomainFile : DataFile
{
    public const uint Magic = 0x42584F4D; // "MOXB"
    public const int NameChars = 16;

    public static readonly IReadOnlyDictionary<long, string> TypeEnum = new Dictionary<long, string>
    {
        [0] = "Không",
        [1] = "Cấp 1 (nhỏ)",
        [2] = "Cấp 2 (vừa)",
        [3] = "Cấp 3 (lớn / chủ thành)",
    };

    public static readonly IReadOnlyDictionary<long, string> WeekdayEnum = new Dictionary<long, string>
    {
        // counted from Monday 00:00 (as JD IDE / the server's domain.sev): the samples' slots are
        // Sat/Sun/Mon 20:00, Sun/Mon 14:00, Sun/Mon 09:00
        [0] = "Thứ 2", [1] = "Thứ 3", [2] = "Thứ 4", [3] = "Thứ 5",
        [4] = "Thứ 6", [5] = "Thứ 7", [6] = "Chủ nhật",
    };

    public static readonly IReadOnlyList<ColumnDef> VertColumns =
    [
        new() { Name = "x", Type = ColType.I32, Note = "Toạ độ X trên ảnh bản đồ (pixel)" },
        new() { Name = "y", Type = ColType.I32, Note = "Toạ độ Y trên ảnh bản đồ (pixel)" },
    ];

    public static readonly IReadOnlyList<ColumnDef> FaceColumns =
    [
        new() { Name = "v0", Type = ColType.I32, Note = "Chỉ số đỉnh thứ nhất (đếm từ 0)" },
        new() { Name = "v1", Type = ColType.I32, Note = "Chỉ số đỉnh thứ hai (đếm từ 0)" },
        new() { Name = "v2", Type = ColType.I32, Note = "Chỉ số đỉnh thứ ba (đếm từ 0)" },
    ];

    public static readonly IReadOnlyList<ColumnDef> NeighbourColumns =
    [
        new() { Name = "domain_id", Type = ColType.I32, Note = "ID lãnh địa kề bên (phải có trong file)" },
    ];

    // Column indices of the "Domains" table.
    public const int ColId = 0, ColName = 1, ColRank = 2, ColReward = 3, ColCenterX = 4, ColCenterY = 5,
        ColVerts = 6, ColFaces = 7, ColNeighbours = 8;

    public static readonly IReadOnlyList<ColumnDef> DomainColumns =
    [
        new() { Name = "id", Type = ColType.I32, Note = "ID lãnh địa (server dùng)" },
        new() { Name = "name", Type = ColType.WStr, Length = NameChars, Note = "Tên lãnh địa (tối đa 15 ký tự)" },
        new() { Name = "type", Type = ColType.I32, Enum = TypeEnum, Note = "Cấp lãnh địa (1–3)" },
        new() { Name = "reward", Type = ColType.I32, Note = "Thưởng (mọi file mẫu đều = 0)" },
        new() { Name = "center_x", Type = ColType.I32, Note = "Tâm X: chỗ hiện tên / bấm chọn" },
        new() { Name = "center_y", Type = ColType.I32, Note = "Tâm Y: chỗ hiện tên / bấm chọn" },
        new() { Name = "verts", Type = ColType.Table, SubColumns = VertColumns, HideInGrid = true, Note = "Các đỉnh đường viền (đa giác, ≥ 3 đỉnh)" },
        new() { Name = "faces", Type = ColType.Table, SubColumns = FaceColumns, HideInGrid = true, Note = "Tam giác phủ vùng (thường = số đỉnh − 2)" },
        new() { Name = "neighbours", Type = ColType.Table, SubColumns = NeighbourColumns, Note = "Lãnh địa kề bên (nên khai báo hai chiều)" },
    ];

    public static readonly IReadOnlyList<ColumnDef> BattleTimeColumns =
    [
        new() { Name = "weekday", Type = ColType.I32, Enum = WeekdayEnum, Note = "Thứ trong tuần (0 = Thứ 2 … 6 = Chủ nhật)" },
        new() { Name = "hour", Type = ColType.I32, Note = "Giờ (0–23)" },
        new() { Name = "minute", Type = ColType.I32, Note = "Phút (0–59)" },
    ];

    public const int HdrBattlesPerSlot = 0, HdrTrailing = 1;

    public static readonly IReadOnlyList<ColumnDef> HeaderColumns =
    [
        new() { Name = "battles_per_slot", Type = ColType.I32, Note = "Số trận tối đa mỗi khung giờ" },
        new() { Name = "trailing", Type = ColType.Bytes, ReadOnly = true, HideInGrid = true, Note = "Byte thừa cuối file (giữ nguyên)" },
    ];

    /// <summary>False when the file ended right after the domains (no battle-time section).</summary>
    public bool HasBattleSection { get; private set; } = true;

    public RecordTable Domains => Tables[0];
    public RecordTable BattleTimes => Tables[1];
    public RecordTable Header => Tables[2];

    public override string FormatName
    {
        get
        {
            var n = System.IO.Path.GetFileName(Path).ToLowerInvariant();
            string kind = n switch
            {
                "domain.data" => "bản đồ bang hội",
                "domain1.data" => "chiến lãnh thổ",
                "domain2.data" => "bản đồ liên server",
                "domain3.data" => "bản đồ tổng",
                "domain4.data" => "bản đồ thượng cổ",
                "domainlandwar.data" => "chiến địa",
                _ => "lãnh địa",
            };
            return $"{(n.Length > 0 ? n : "domain.data")} ({kind}, {Domains.Rows.Count} lãnh địa)";
        }
    }

    public static bool Sniff(byte[] d) => d.Length >= 8 && BitConverter.ToUInt32(d, 0) == Magic;

    public static DomainFile Parse(byte[] data, string path)
    {
        var r = new BinReader(data);
        if (r.U32() != Magic) throw new InvalidDataException("Không phải file domain (thiếu chữ ký MOXB).");
        int count = r.I32();
        if (count < 0 || count > 100000) throw new InvalidDataException($"Số lãnh địa không hợp lệ: {count}.");

        var f = new DomainFile { Path = path };
        var domains = new RecordTable
        {
            Name = "Domains", Columns = DomainColumns, KeyColumn = ColId, NameColumn = ColName,
            Note = "Mỗi dòng một lãnh địa: đường viền, tam giác và lãnh địa kề bên.",
        };
        var times = new RecordTable
        {
            Name = "Battle times", Columns = BattleTimeColumns,
            Note = "Các khung giờ khai chiến trong tuần.",
        };
        var header = new RecordTable
        {
            Name = "Header", Columns = HeaderColumns, CanAddRemove = false,
            Note = "Giá trị chung của file.",
        };

        for (int i = 0; i < count; i++)
        {
            var rec = new Record(DomainColumns.Count);
            RecordIO.ReadCell(r, DomainColumns[ColName], rec, ColName);
            rec.Values[ColId] = r.I32();
            rec.Values[ColRank] = r.I32();
            rec.Values[ColReward] = r.I32();
            rec.Values[ColCenterX] = r.I32();
            rec.Values[ColCenterY] = r.I32();
            rec.Values[ColVerts] = ReadList(r, VertColumns, $"lãnh địa #{i}: số đỉnh");
            rec.Values[ColFaces] = ReadList(r, FaceColumns, $"lãnh địa #{i}: số tam giác");
            rec.Values[ColNeighbours] = ReadList(r, NeighbourColumns, $"lãnh địa #{i}: số lãnh địa kề");
            domains.Rows.Add(rec);
        }

        var hdr = new Record(HeaderColumns.Count);
        hdr.Values[HdrBattlesPerSlot] = 0;
        hdr.Values[HdrTrailing] = Array.Empty<byte>();
        if (r.AtEnd)
        {
            f.HasBattleSection = false;
        }
        else
        {
            foreach (var t in ReadList(r, BattleTimeColumns, "số khung giờ khai chiến")) times.Rows.Add(t);
            hdr.Values[HdrBattlesPerSlot] = r.I32();
            if (!r.AtEnd)
            {
                hdr.Values[HdrTrailing] = r.Bytes(r.Remaining);
                f.Warnings.Add($"Có {((byte[])hdr.Values[HdrTrailing]!).Length} byte sau phần giờ khai chiến (giữ nguyên khi lưu).");
            }
        }
        header.Rows.Add(hdr);

        f.Tables.Add(domains);
        f.Tables.Add(times);
        f.Tables.Add(header);
        f.Warnings.AddRange(f.Validate());
        return f;
    }

    private static List<Record> ReadList(BinReader r, IReadOnlyList<ColumnDef> cols, string what)
    {
        int n = r.I32();
        int size = RecordIO.FixedSize(cols);
        if (n < 0 || (long)n * size > r.Remaining)
            throw new InvalidDataException($"{what} không hợp lệ ({n}) ở offset {r.Pos - 4}.");
        var list = new List<Record>(n);
        for (int k = 0; k < n; k++) list.Add(RecordIO.ReadFixed(r, cols));
        return list;
    }

    private static void WriteList(BinWriter w, IReadOnlyList<ColumnDef> cols, List<Record>? list)
    {
        list ??= [];
        w.I32(list.Count);
        foreach (var rec in list) RecordIO.WriteFixed(w, cols, rec);
    }

    public override byte[] ToBytes()
    {
        var w = new BinWriter();
        w.U32(Magic);
        w.I32(Domains.Rows.Count);
        foreach (var rec in Domains.Rows)
        {
            RecordIO.WriteCell(w, DomainColumns[ColName], rec, ColName);
            w.I32(ToI32(rec.Values[ColId]));
            w.I32(ToI32(rec.Values[ColRank]));
            w.I32(ToI32(rec.Values[ColReward]));
            w.I32(ToI32(rec.Values[ColCenterX]));
            w.I32(ToI32(rec.Values[ColCenterY]));
            WriteList(w, VertColumns, rec.Values[ColVerts] as List<Record>);
            WriteList(w, FaceColumns, rec.Values[ColFaces] as List<Record>);
            WriteList(w, NeighbourColumns, rec.Values[ColNeighbours] as List<Record>);
        }

        var hdr = Header.Rows.Count > 0 ? Header.Rows[0] : Record.CreateDefault(HeaderColumns);
        int perSlot = ToI32(hdr.Values[HdrBattlesPerSlot]);
        var trailing = hdr.Values[HdrTrailing] as byte[] ?? [];
        if (HasBattleSection || BattleTimes.Rows.Count > 0 || perSlot != 0 || trailing.Length > 0)
        {
            WriteList(w, BattleTimeColumns, BattleTimes.Rows);
            w.I32(perSlot);
            w.Bytes(trailing);
        }
        return w.ToArray();
    }

    private static int ToI32(object? v) => Convert.ToInt32(v ?? 0, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Consistency checks the client relies on (outline, triangles, neighbours, battle times).</summary>
    public List<string> Validate(int max = 30)
    {
        var res = new List<string>();
        void Add(string s) { if (res.Count < max) res.Add(s); }

        var ids = new Dictionary<long, Record>();
        foreach (var d in Domains.Rows)
        {
            long id = d.Long(ColId);
            if (!ids.TryAdd(id, d)) Add($"ID lãnh địa {id} bị trùng: client chỉ tìm thấy dòng đầu.");
        }

        foreach (var d in Domains.Rows)
        {
            long id = d.Long(ColId);
            string tag = $"Lãnh địa {id} ({d.Text(ColName)})";
            var verts = d.Values[ColVerts] as List<Record> ?? [];
            var faces = d.Values[ColFaces] as List<Record> ?? [];
            var nbs = d.Values[ColNeighbours] as List<Record> ?? [];

            if (verts.Count < 3 || faces.Count == 0)
                Add($"{tag}: không có diện tích (ít hơn 3 đỉnh hoặc không có tam giác) — không thấy/không bấm được trên bản đồ.");
            for (int k = 0; k < faces.Count; k++)
                for (int j = 0; j < 3; j++)
                {
                    long v = faces[k].Long(j);
                    if (v < 0 || v >= verts.Count)
                    {
                        Add($"{tag}: tam giác #{k} dùng đỉnh {v} ngoài danh sách ({verts.Count} đỉnh).");
                        j = 3;
                    }
                }

            var seen = new HashSet<long>();
            foreach (var n in nbs)
            {
                long nid = n.Long(0);
                if (nid == id) { Add($"{tag}: tự liệt kê mình là lãnh địa kề."); continue; }
                if (!seen.Add(nid)) { Add($"{tag}: lãnh địa kề {nid} bị lặp."); continue; }
                if (!ids.TryGetValue(nid, out var other)) { Add($"{tag}: lãnh địa kề {nid} không có trong file."); continue; }
                var back = other.Values[ColNeighbours] as List<Record> ?? [];
                if (!back.Any(x => x.Long(0) == id)) Add($"{tag}: lãnh địa kề {nid} không khai báo ngược lại.");
            }
        }

        foreach (var t in BattleTimes.Rows)
        {
            long wd = t.Long(0), h = t.Long(1), m = t.Long(2);
            if (wd is < 0 or > 6 || h is < 0 or > 23 || m is < 0 or > 59)
                Add($"Giờ khai chiến không hợp lệ: thứ {wd}, {h}:{m:00}.");
        }
        return res;
    }
}
