namespace ElementsCore.Formats;

/// <summary>
/// vipaward.data: VIP / daily awards and the VIP shop.
/// <code>
/// u32 timeMark;   // Unix time of the export
/// u32 count;      // ≤ 65 535 (server limit)
/// count × award (fixed size, = (fileLength − 8) / count; 172 B in current builds):
///   u32     id;
///   wchar_t name[64];
///   u32     itemId;      // elements.data item
///   u32     count;       // stack size given
///   u32     awardType;   // 0 normal, 1 VIP, 2 VIP shop
///   u32     level;       // 1..8 (VIP level / level bracket)
///   u32     obtainType;  // 0 daily, 1 special (once), 2 shop
///   i32     expireTime;  // seconds the item lasts, 0 = permanent
///   --- 156 B: older source layout ends here ---
///   f32     price;       // VIP shop price
///   u32     shopParam;   // VIP shop only (1..3), meaning unknown
///   u8      reserved[8]; // always 0
/// </code>
/// Records of another size keep the 156-byte prefix as columns and the rest as raw bytes.
/// </summary>
public sealed class VipAwardFile : DataFile
{
    public const int HeaderSize = 8;
    public const int BaseSize = 156;
    public const int CurrentSize = 172;
    public const int MaxAwards = 65535;

    public static readonly IReadOnlyDictionary<long, string> AwardTypes = new Dictionary<long, string>
    {
        [0] = "Thường", [1] = "VIP", [2] = "VIP shop",
    };
    public static readonly IReadOnlyDictionary<long, string> ObtainTypes = new Dictionary<long, string>
    {
        [0] = "Hằng ngày", [1] = "Đặc biệt (1 lần)", [2] = "Mua ở shop",
    };

    public static readonly IReadOnlyList<ColumnDef> HeaderColumns =
    [
        new() { Name = "timeMark", Type = ColType.U32, Note = "Dấu thời gian xuất file (Unix)" },
        new() { Name = "count", Type = ColType.U32, ReadOnly = true, Note = "Số phần quà (tự tính lại khi lưu)" },
    ];

    private static readonly ColumnDef[] BaseColumns =
    [
        new() { Name = "id", Type = ColType.U32, Note = "ID phần quà (>0, không trùng)" },
        new() { Name = "name", Type = ColType.WStr, Length = 64, Note = "Tên hiển thị (tối đa 63 ký tự)" },
        new() { Name = "itemId", Type = ColType.U32, Note = "ID vật phẩm trong elements.data" },
        new() { Name = "count", Type = ColType.U32, Note = "Số lượng (vật phẩm không chồng được thì phải là 1)" },
        new() { Name = "awardType", Type = ColType.U32, Enum = AwardTypes, Note = "Loại quà: 0 thường, 1 VIP, 2 VIP shop" },
        new() { Name = "level", Type = ColType.U32, Note = "Cấp VIP / bậc cấp 1–8" },
        new() { Name = "obtainType", Type = ColType.U32, Enum = ObtainTypes, Note = "Cách nhận: 0 hằng ngày, 1 đặc biệt, 2 shop" },
        new() { Name = "expireTime", Type = ColType.I32, Note = "Thời hạn vật phẩm (giây); 0 = vĩnh viễn" },
    ];

    private static readonly ColumnDef[] CurrentTail =
    [
        new() { Name = "price", Type = ColType.F32, Note = "Giá ở VIP shop (0 nếu không bán)" },
        new() { Name = "shopParam", Type = ColType.U32, Note = "Chỉ dùng cho VIP shop (1–3), chưa rõ ý nghĩa" },
        new() { Name = "reserved", Type = ColType.Bytes, Length = 8, HideInGrid = true, Note = "Dự trữ, luôn 0" },
    ];

    /// <summary>Columns of the current 172-byte layout.</summary>
    public static readonly IReadOnlyList<ColumnDef> Columns = [.. BaseColumns, .. CurrentTail];

    public int AwardSize { get; private set; } = CurrentSize;
    public override string FormatName => $"vipaward.data ({AwardSize} B/quà)";
    public RecordTable Header => Tables[0];
    public RecordTable Awards => Tables[1];

    public static IReadOnlyList<ColumnDef> ColumnsFor(int size)
    {
        if (size == CurrentSize) return Columns;
        if (size == BaseSize) return BaseColumns;
        if (size > BaseSize)
            return [.. BaseColumns, new() { Name = "extra", Type = ColType.Bytes, Length = size - BaseSize, Note = "Phần mở rộng chưa giải mã" }];
        throw new InvalidDataException($"Phần quà {size} byte không phải bố cục vipaward.data đã biết ({BaseSize} hoặc {CurrentSize}).");
    }

    public static bool Sniff(byte[] d)
    {
        if (d.Length < HeaderSize + BaseSize) return false;
        uint count = BitConverter.ToUInt32(d, 4);
        if (count == 0 || count > MaxAwards || (d.Length - HeaderSize) % count != 0) return false;
        int size = (int)((d.Length - HeaderSize) / count);
        if (size < BaseSize || size > 1024 || size % 4 != 0) return false;
        for (int i = 0; i < count; i++)
        {
            int o = HeaderSize + i * size;
            // awardType (offset 140) and obtainType (offset 148) are 0..2 in every known file
            if (BitConverter.ToUInt32(d, o + 140) > 2 || BitConverter.ToUInt32(d, o + 148) > 2) return false;
            bool nul = false;
            for (int c = 0; c < 64 && !nul; c++) nul = d[o + 4 + c * 2] == 0 && d[o + 5 + c * 2] == 0;
            if (!nul) return false;
        }
        return true;
    }

    public static VipAwardFile Parse(byte[] data, string path)
    {
        if (data.Length < HeaderSize) throw new InvalidDataException("File quá ngắn, không đủ header vipaward.data.");
        var r = new BinReader(data);
        var head = RecordIO.ReadFixed(r, HeaderColumns);
        long count = head.Long(1);
        var f = new VipAwardFile { Path = path };
        int size;
        if (count == 0)
        {
            if (r.Remaining > 0) throw new InvalidDataException($"Không có phần quà nhưng sau header còn {r.Remaining} byte.");
            size = CurrentSize;
        }
        else
        {
            if (count > MaxAwards) throw new InvalidDataException($"Số phần quà {count} không hợp lệ (tối đa {MaxAwards}).");
            if (r.Remaining % count != 0) throw new InvalidDataException($"{r.Remaining} byte không chia đều cho {count} phần quà.");
            size = (int)(r.Remaining / count);
        }
        var cols = ColumnsFor(size);
        f.AwardSize = size;
        if (size != CurrentSize) f.Warnings.Add($"Bố cục {size} B/quà (bản hiện hành là {CurrentSize} B); phần sau {BaseSize} B giữ dạng byte thô.");

        f.Tables.Add(new RecordTable
        {
            Name = "Header", Columns = HeaderColumns, CanAddRemove = false, Rows = [head],
            Note = "Header của file; count tự tính khi lưu.",
        });
        var t = new RecordTable
        {
            Name = "VIP awards", Columns = cols, KeyColumn = 0, NameColumn = 1,
            Note = "Quà VIP / quà hằng ngày / VIP shop.",
        };
        for (long i = 0; i < count; i++) t.Rows.Add(RecordIO.ReadFixed(r, cols));
        f.Tables.Add(t);

        if (t.Rows.Any(x => x.Long(0) == 0)) f.Warnings.Add("Có phần quà ID 0 (server không khởi động).");
        var dup = t.Rows.GroupBy(x => x.Long(0)).Where(g => g.Count() > 1).Select(g => g.Key).Take(5).ToList();
        if (dup.Count > 0) f.Warnings.Add("ID phần quà trùng (server không khởi động): " + string.Join(", ", dup));
        return f;
    }

    public override byte[] ToBytes()
    {
        int count = Awards.Rows.Count;
        if (count > MaxAwards) throw new InvalidDataException($"vipaward.data chứa tối đa {MaxAwards} phần quà (đang có {count}).");
        var h = Header.Rows[0];
        h.Values[1] = (uint)count;
        var w = new BinWriter();
        RecordIO.WriteFixed(w, HeaderColumns, h);
        foreach (var rec in Awards.Rows) RecordIO.WriteFixed(w, Awards.Columns, rec);
        return w.ToArray();
    }
}
