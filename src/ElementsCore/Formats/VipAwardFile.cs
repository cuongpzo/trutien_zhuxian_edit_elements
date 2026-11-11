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
        [0] = L.T("Normal"), [1] = "VIP", [2] = L.T("VIP shop"),
    };
    public static readonly IReadOnlyDictionary<long, string> ObtainTypes = new Dictionary<long, string>
    {
        [0] = L.T("Daily"), [1] = L.T("Special (once)"), [2] = L.T("Bought in the shop"),
    };

    public static readonly IReadOnlyList<ColumnDef> HeaderColumns =
    [
        new() { Name = "timeMark", Type = ColType.U32, Note = L.T("File export timestamp (Unix)") },
        new() { Name = "count", Type = ColType.U32, ReadOnly = true, Note = L.T("Number of awards (recalculated on save)") },
    ];

    private static readonly ColumnDef[] BaseColumns =
    [
        new() { Name = "id", Type = ColType.U32, Note = L.T("Award ID (>0, unique)") },
        new() { Name = "name", Type = ColType.WStr, Length = 64, Note = L.T("Display name (max 63 characters)") },
        new() { Name = "itemId", Type = ColType.U32, Note = L.T("Item ID in elements.data") },
        new() { Name = "count", Type = ColType.U32, Note = L.T("Quantity (must be 1 for non-stackable items)") },
        new() { Name = "awardType", Type = ColType.U32, Enum = AwardTypes, Note = L.T("Award type: 0 normal, 1 VIP, 2 VIP shop") },
        new() { Name = "level", Type = ColType.U32, Note = L.T("VIP level / level bracket 1–8") },
        new() { Name = "obtainType", Type = ColType.U32, Enum = ObtainTypes, Note = L.T("How it is obtained: 0 daily, 1 special, 2 shop") },
        new() { Name = "expireTime", Type = ColType.I32, Note = L.T("Item duration (seconds); 0 = permanent") },
    ];

    private static readonly ColumnDef[] CurrentTail =
    [
        new() { Name = "price", Type = ColType.F32, Note = L.T("VIP shop price (0 if not sold)") },
        new() { Name = "shopParam", Type = ColType.U32, Note = L.T("VIP shop only (1–3), meaning unknown") },
        new() { Name = "reserved", Type = ColType.Bytes, Length = 8, HideInGrid = true, Note = L.T("Reserved, always 0") },
    ];

    /// <summary>Columns of the current 172-byte layout.</summary>
    public static readonly IReadOnlyList<ColumnDef> Columns = [.. BaseColumns, .. CurrentTail];

    public int AwardSize { get; private set; } = CurrentSize;
    public override string FormatName => L.F("vipaward.data ({0} B/award)", AwardSize);
    public RecordTable Header => Tables[0];
    public RecordTable Awards => Tables[1];

    public static IReadOnlyList<ColumnDef> ColumnsFor(int size)
    {
        if (size == CurrentSize) return Columns;
        if (size == BaseSize) return BaseColumns;
        if (size > BaseSize)
            return [.. BaseColumns, new() { Name = "extra", Type = ColType.Bytes, Length = size - BaseSize, Note = L.T("Undecoded extension") }];
        throw new InvalidDataException(L.F("An award of {0} bytes is not a known vipaward.data layout ({1} or {2}).", size, BaseSize, CurrentSize));
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
        if (data.Length < HeaderSize) throw new InvalidDataException(L.T("File too short for the vipaward.data header."));
        var r = new BinReader(data);
        var head = RecordIO.ReadFixed(r, HeaderColumns);
        long count = head.Long(1);
        var f = new VipAwardFile { Path = path };
        int size;
        if (count == 0)
        {
            if (r.Remaining > 0) throw new InvalidDataException(L.F("No awards, but {0} bytes remain after the header.", r.Remaining));
            size = CurrentSize;
        }
        else
        {
            if (count > MaxAwards) throw new InvalidDataException(L.F("Invalid award count {0} (max {1}).", count, MaxAwards));
            if (r.Remaining % count != 0) throw new InvalidDataException(L.F("{0} bytes do not divide evenly into {1} awards.", r.Remaining, count));
            size = (int)(r.Remaining / count);
        }
        var cols = ColumnsFor(size);
        f.AwardSize = size;
        if (size != CurrentSize) f.Warnings.Add(L.F("Layout {0} B/award (the current version is {1} B); the part after {2} B is kept as raw bytes.", size, CurrentSize, BaseSize));

        f.Tables.Add(new RecordTable
        {
            Name = "Header", Columns = HeaderColumns, CanAddRemove = false, Rows = [head],
            Note = L.T("File header; count is calculated on save."),
        });
        var t = new RecordTable
        {
            Name = "VIP awards", Columns = cols, KeyColumn = 0, NameColumn = 1,
            Note = L.T("VIP awards / daily awards / VIP shop."),
        };
        for (long i = 0; i < count; i++) t.Rows.Add(RecordIO.ReadFixed(r, cols));
        f.Tables.Add(t);

        if (t.Rows.Any(x => x.Long(0) == 0)) f.Warnings.Add(L.T("An award has ID 0 (the server will not start)."));
        var dup = t.Rows.GroupBy(x => x.Long(0)).Where(g => g.Count() > 1).Select(g => g.Key).Take(5).ToList();
        if (dup.Count > 0) f.Warnings.Add(L.F("Duplicate award IDs (the server will not start): {0}", string.Join(", ", dup)));
        return f;
    }

    public override byte[] ToBytes()
    {
        int count = Awards.Rows.Count;
        if (count > MaxAwards) throw new InvalidDataException(L.F("vipaward.data holds at most {0} awards (currently {1}).", MaxAwards, count));
        var h = Header.Rows[0];
        h.Values[1] = (uint)count;
        var w = new BinWriter();
        RecordIO.WriteFixed(w, HeaderColumns, h);
        foreach (var rec in Awards.Rows) RecordIO.WriteFixed(w, Awards.Columns, rec);
        return w.ToArray();
    }
}
