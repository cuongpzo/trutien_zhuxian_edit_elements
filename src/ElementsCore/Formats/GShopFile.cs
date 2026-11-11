namespace ElementsCore.Formats;

/// <summary>
/// gshop*.data – the in-game item mall (client side; the server reads the same file).
/// <code>
/// u32 timestamp;                 // version stamp, client and server copies must match
/// u32 item_count;
/// item_count × ITEM              // fixed size, one of the layouts below
/// u32 main_count;                // ≥ 1
/// main_count × { i32 id; wchar name[64]; u32 sub_count; sub_count × wchar sub_name[64] }
/// </code>
/// The item layout is not stored in the file: it is found by arithmetic (the only record size
/// for which the category list ends exactly at EOF). Known layouts (all packed, no alignment):
/// 2660 B (TT3 shop: Source GSHOP_ITEM + 30 extra bytes), 2630 B (Source), 1252 B (old client),
/// 357 B (lucky bag, gshop4), 200 B (exchange / bonus shop, gshop5).
/// See research\formats\gshop.md.
/// </summary>
public sealed class GShopFile : DataFile
{
    public sealed class Layout
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required IReadOnlyList<ColumnDef> Columns { get; init; }
        public int Size => RecordIO.FixedSize(Columns);
        public int KeyColumn => IndexOf("id");
        public int NameColumn => IndexOf("name");
        public int IndexOf(string name)
        {
            for (int i = 0; i < Columns.Count; i++) if (Columns[i].Name == name) return i;
            return -1;
        }
    }

    // ---------- shared columns ----------

    private static readonly IReadOnlyDictionary<long, string> PropsEnum = new Dictionary<long, string>
    {
        [0] = "Thường", [1] = "Mới", [2] = "Đề cử", [3] = "Mới + Đề cử",
    };

    private static readonly IReadOnlyDictionary<long, string> ValidTypeEnum = new Dictionary<long, string>
    {
        [0] = "Khoảng ngày (bắt đầu → kết thúc)", [1] = "Từ ngày bắt đầu, lặp lại?",
    };

    private static ColumnDef I32(string n, string? note = null, bool hide = false, IReadOnlyDictionary<long, string>? e = null) =>
        new() { Name = n, Type = ColType.I32, Note = note, HideInGrid = hide, Enum = e };
    private static ColumnDef U32(string n, string? note = null, bool hide = false) =>
        new() { Name = n, Type = ColType.U32, Note = note, HideInGrid = hide };
    private static ColumnDef U8(string n, string? note = null, bool hide = false, IReadOnlyDictionary<long, string>? e = null) =>
        new() { Name = n, Type = ColType.U8, Note = note, HideInGrid = hide, Enum = e };
    private static ColumnDef WStr(string n, int len, string? note = null, bool hide = false) =>
        new() { Name = n, Type = ColType.WStr, Length = len, Note = note, HideInGrid = hide };
    private static ColumnDef Str(string n, int len, string? note = null, bool hide = false) =>
        new() { Name = n, Type = ColType.Str, Length = len, Note = note, HideInGrid = hide };
    private static ColumnDef Pad(string n, int len, string note = "Chưa rõ (luôn 0 trong file mẫu)") =>
        new() { Name = n, Type = ColType.Bytes, Length = len, Note = note, HideInGrid = true };

    /// <summary>GSHOP_ITEM of the C++ source, packed (2630 B).</summary>
    private static List<ColumnDef> SourceColumns() =>
    [
        U32("id", "ID vật phẩm bán (elements.data)"),
        U32("num", "Số lượng mỗi lần mua"),
        Str("icon", 128, "Ảnh icon trong shop (GBK)", hide: true),
        U32("price", "Giá (thường tính theo 1/100 nguyên bảo)"),
        U32("time", "Thời hạn sử dụng, giây (0 = vĩnh viễn)"),
        I32("discount", "Chiết khấu % (100 = giá gốc, 50 = nửa giá)"),
        I32("bonus", "Thưởng (chưa thấy dùng, luôn 0)", hide: true),
        U8("props", "Nhãn: 1 = Mới, 2 = Đề cử", e: PropsEnum),
        U8("props_b1", "Byte 2 của props (luôn 0)", hide: true),
        U8("props_b2", "Byte 3 của props (luôn 255)", hide: true),
        U8("props_b3", "Byte 4 của props: 128 khi đang giảm giá 50%", hide: true),
        I32("main_type", "Danh mục chính (chỉ số trong bảng Categories)"),
        I32("sub_type", "Danh mục con (chỉ số trong danh mục chính)"),
        I32("local_id", "ID nội bộ / thứ tự (mỗi món khác nhau)"),
        WStr("description", 512, "Mô tả (mã màu ^RRGGBB, xuống dòng \\r\\n)", hide: true),
        WStr("name", 32, "Tên hiển thị trong shop"),
        new() { Name = "has_present", Type = ColType.Bool, Note = "Có quà tặng kèm (luôn 0 trong mẫu)", HideInGrid = true },
        WStr("present_name", 32, "Tên quà tặng kèm", hide: true),
        U32("present_id", "ID quà tặng kèm", hide: true),
        U32("present_count", "Số lượng quà", hide: true),
        U32("present_time", "Thời hạn quà, giây", hide: true),
        Str("present_icon", 128, "Icon quà (GBK)", hide: true),
        new() { Name = "present_bind", Type = ColType.Bool, Note = "Quà bị khoá (bind)", HideInGrid = true },
        WStr("present_description", 512, "Mô tả quà", hide: true),
        I32("valid_type", "Kiểu thời gian bán", hide: true, e: ValidTypeEnum),
        I32("valid_start", "Bắt đầu bán (Unix time, 0 = luôn bán)", hide: true),
        I32("valid_end", "Kết thúc bán (Unix time) / độ dài khi kiểu 1", hide: true),
        I32("valid_param", "Tham số thời gian bán", hide: true),
        WStr("search_keys", 64, "Từ khoá tìm kiếm (phân cách bằng dấu phẩy)", hide: true),
    ];

    /// <summary>TT3 2660 B: Source + 30 extra bytes.</summary>
    private static List<ColumnDef> Tt3Columns()
    {
        var c = SourceColumns();
        c.AddRange(
        [
            U8("x_flag1", "Chưa rõ: 1 cùng lúc với x_flag2 (thú cưng, mặt nạ…)", hide: true),
            U32("x_price1", "Chưa rõ: thường bằng giá hoặc giá gốc (12800, 9900, 2500…)", hide: true),
            U8("x_flag2", "Chưa rõ: cờ thứ 2", hide: true),
            U32("x_price2", "Chưa rõ: giá thứ 2", hide: true),
            U32("x_value", "Chưa rõ: 1 ở vài món (gói Thập niên)", hide: true),
            I32("valid2_type", "Thời gian bán (bản 2) – kiểu", hide: true, e: ValidTypeEnum),
            I32("valid2_start", "Thời gian bán (bản 2) – bắt đầu (Unix time)", hide: true),
            I32("valid2_end", "Thời gian bán (bản 2) – kết thúc (Unix time)", hide: true),
            I32("valid2_param", "Thời gian bán (bản 2) – tham số", hide: true),
        ]);
        return c;
    }

    private static List<ColumnDef> OldClientColumns() =>
    [
        U32("id", "ID vật phẩm bán (elements.data)"),
        U32("num", "Số lượng mỗi lần mua"),
        Str("icon", 128, "Ảnh icon trong shop (GBK)", hide: true),
        U32("price", "Giá"),
        U32("time", "Thời hạn sử dụng, giây (0 = vĩnh viễn)"),
        I32("unknown_144", "Chưa rõ (luôn 0; có thể là chiết khấu)", hide: true),
        new() { Name = "props", Type = ColType.U32, Note = "Nhãn: 1 = Mới, 2 = Đề cử", Enum = PropsEnum },
        I32("main_type", "Danh mục chính (chỉ số trong bảng Categories)"),
        I32("sub_type", "Danh mục con (chỉ số trong danh mục chính)"),
        I32("local_id", "ID nội bộ / thứ tự"),
        WStr("description", 512, "Mô tả", hide: true),
        WStr("name", 32, "Tên hiển thị trong shop"),
    ];

    private static List<ColumnDef> LuckyBagColumns() =>
    [
        I32("place", "Vị trí trong shop"),
        I32("main_type", "Danh mục chính (chỉ số trong bảng Categories)"),
        I32("sub_type", "Danh mục con"),
        Str("name", 128, "Tên hộp (GBK)"),
        U32("id", "ID hộp phúc túi bán (elements.data)"),
        U32("num", "Số lượng mỗi lần mua"),
        U32("price_item", "ID vật phẩm dùng để trả (token)"),
        U32("price_item_count", "Số token phải trả"),
        Pad("unknown_156", 36),
        U8("flag_192", "Cờ: 1 ở các món mục Mới (chưa rõ)"),
        Str("icon", 128, "Ảnh icon (GBK)", hide: true),
        I32("valid_type", "Kiểu thời gian bán", hide: true, e: ValidTypeEnum),
        I32("valid_start", "Bắt đầu bán (Unix time)", hide: true),
        I32("valid_end", "Kết thúc bán (Unix time) / độ dài khi kiểu 1", hide: true),
        I32("valid_param", "Tham số thời gian bán", hide: true),
        Pad("unknown_337", 20),
    ];

    private static List<ColumnDef> ExchangeColumns() =>
    [
        I32("place", "Vị trí trong shop"),
        I32("main_type", "Danh mục chính (chỉ số trong bảng Categories)"),
        I32("sub_type", "Danh mục con"),
        Str("name", 128, "Tên hiển thị (GBK)"),
        U32("id", "ID vật phẩm bán (elements.data)"),
        U32("num", "Số lượng mỗi lần mua"),
        U32("price", "Giá"),
        Pad("unknown_152", 20),
        I32("value_172", "Chưa rõ (1–20; có thể là giới hạn mua)"),
        Pad("unknown_176", 16),
        I32("local_id", "ID nội bộ / thứ tự (chưa chắc)"),
        I32("value_196", "Chưa rõ (luôn 1 trong mẫu)"),
    ];

    /// <summary>Known item layouts, in detection priority.</summary>
    public static readonly IReadOnlyList<Layout> Layouts =
    [
        new() { Key = "tt3", Title = "Shop TT3", Columns = Tt3Columns() },          // 2660
        new() { Key = "source", Title = "Source", Columns = SourceColumns() },      // 2630
        new() { Key = "oldclient", Title = "Client cũ", Columns = OldClientColumns() }, // 1252
        new() { Key = "luckybag", Title = "Phúc túi", Columns = LuckyBagColumns() }, // 357
        new() { Key = "exchange", Title = "Đổi điểm", Columns = ExchangeColumns() },  // 200
    ];

    public static readonly IReadOnlyList<ColumnDef> HeaderColumns =
    [
        new() { Name = "timestamp", Type = ColType.U32, Note = "Mốc phiên bản (Unix time); gshop client và server phải khớp" },
    ];

    public static readonly IReadOnlyList<ColumnDef> SubCategoryColumns =
    [
        WStr("name", 64, "Tên danh mục con"),
    ];

    public static readonly IReadOnlyList<ColumnDef> CategoryColumns =
    [
        I32("id", "ID danh mục chính (thường = thứ tự 0,1,2…)"),
        WStr("name", 64, "Tên danh mục chính"),
        new() { Name = "subs", Type = ColType.Table, SubColumns = SubCategoryColumns, Note = "Các danh mục con (main/sub_type của món = chỉ số)" },
    ];

    private const int MaxMain = 256, MaxSub = 4096;

    public Layout ItemLayout { get; private set; } = null!;
    public bool LayoutGuessed { get; private set; }

    public RecordTable Header => Tables[0];
    public RecordTable Items => Tables[1];
    public RecordTable Categories => Tables[2];

    public override string FormatName =>
        $"gshop ({ItemLayout.Title}, {ItemLayout.Size} B/món{(LayoutGuessed ? ", chưa có món – kiểu mặc định" : "")})";

    // ---------- detection ----------

    /// <summary>Walks the category list at <paramref name="off"/>; returns the end offset or -1.</summary>
    private static int CategoryEnd(byte[] d, long off)
    {
        if (off < 0 || off + 4 > d.Length) return -1;
        uint main = BitConverter.ToUInt32(d, (int)off);
        if (main == 0 || main > MaxMain) return -1;
        off += 4;
        for (uint i = 0; i < main; i++)
        {
            off += 4 + 128;
            if (off + 4 > d.Length) return -1;
            uint sub = BitConverter.ToUInt32(d, (int)off);
            if (sub > MaxSub) return -1;
            off += 4 + 128L * sub;
            if (off > d.Length) return -1;
        }
        return (int)off;
    }

    private static bool Fits(byte[] d, uint count, int size) =>
        CategoryEnd(d, 8L + (long)count * size) == d.Length;

    /// <summary>Finds the item size: a known layout first, else any size that ends the category list at EOF.</summary>
    private static int DetectSize(byte[] d, out Layout? layout)
    {
        layout = null;
        if (d.Length < 12) return -1;
        uint count = BitConverter.ToUInt32(d, 4);
        if (count == 0) return Fits(d, 0, 0) ? 0 : -1;
        if (count > (d.Length - 8) / 8) return -1;
        foreach (var l in Layouts)
            if (Fits(d, count, l.Size)) { layout = l; return l.Size; }
        int max = (int)Math.Min(65536, (d.Length - 12) / count);
        for (int s = 8; s <= max; s++)
            if (Fits(d, count, s)) return s;
        return -1;
    }

    /// <summary>Cheap check: header + one of the known item layouts + a category list ending exactly at EOF.</summary>
    public static bool Sniff(byte[] d)
    {
        if (d.Length < 12) return false;
        uint count = BitConverter.ToUInt32(d, 4);
        if (count == 0) return Fits(d, 0, 0);
        if (count > (d.Length - 8) / 8) return false;
        foreach (var l in Layouts) if (Fits(d, count, l.Size)) return true;
        return false;
    }

    // ---------- parse / write ----------

    public static GShopFile Parse(byte[] data, string path)
    {
        int size = DetectSize(data, out var layout);
        if (size < 0) throw new InvalidDataException("Không tìm thấy danh sách danh mục ở cuối file với bất kỳ kích thước món nào: không phải file gshop.");
        var f = new GShopFile { Path = path };
        uint count = BitConverter.ToUInt32(data, 4);
        if (layout == null && count == 0)
        {
            layout = Layouts[0];
            f.LayoutGuessed = true;
            f.Warnings.Add($"File chưa có món nào: dùng kiểu mặc định {layout.Title} ({layout.Size} B/món) nếu thêm món mới.");
        }
        else if (layout == null)
        {
            layout = new Layout
            {
                Key = "raw" + size, Title = "Chưa biết",
                Columns = [new() { Name = "data", Type = ColType.Bytes, Length = size, Note = "Dữ liệu thô của món (kiểu chưa giải mã)" }],
            };
            f.Warnings.Add($"Kiểu món {size} B chưa được giải mã: chỉ sửa được dạng hex.");
        }
        f.ItemLayout = layout;

        var r = new BinReader(data);
        var header = new RecordTable { Name = "Header", Columns = HeaderColumns, CanAddRemove = false, Note = "Đầu file" };
        var h = new Record(1);
        h.Values[0] = r.U32();
        header.Rows.Add(h);
        r.U32(); // count

        var items = new RecordTable
        {
            Name = "Items", Columns = layout.Columns, KeyColumn = layout.KeyColumn, NameColumn = layout.NameColumn,
            Note = $"Các món bán ({layout.Title}, {layout.Size} byte/món)",
        };
        for (uint i = 0; i < count; i++) items.Rows.Add(RecordIO.ReadFixed(r, layout.Columns));

        var cats = new RecordTable
        {
            Name = "Categories", Columns = CategoryColumns, KeyColumn = 0, NameColumn = 1,
            Note = "Danh mục chính và danh mục con (shop cần ít nhất 1 danh mục chính)",
        };
        uint main = r.U32();
        for (uint i = 0; i < main; i++)
        {
            var c = new Record(CategoryColumns.Count);
            RecordIO.ReadCell(r, CategoryColumns[0], c, 0);
            RecordIO.ReadCell(r, CategoryColumns[1], c, 1);
            uint sub = r.U32();
            var subs = new List<Record>((int)sub);
            for (uint j = 0; j < sub; j++) subs.Add(RecordIO.ReadFixed(r, SubCategoryColumns));
            c.Values[2] = subs;
            cats.Rows.Add(c);
        }
        if (!r.AtEnd) throw new InvalidDataException($"Còn {r.Remaining} byte sau danh sách danh mục.");

        f.Tables.Add(header);
        f.Tables.Add(items);
        f.Tables.Add(cats);
        f.CheckReferences();
        return f;
    }

    /// <summary>Warns about items whose main/sub type points outside the category list.</summary>
    private void CheckReferences()
    {
        int mi = Items.IndexOf("main_type"), si = Items.IndexOf("sub_type");
        if (mi < 0 || si < 0) return;
        var bad = new List<string>();
        foreach (var it in Items.Rows)
        {
            long m = it.Long(mi), s = it.Long(si);
            bool ok = m >= 0 && m < Categories.Rows.Count && s >= 0 && s < Categories.Rows[(int)m].Table(2).Count;
            if (!ok && bad.Count < 5) bad.Add($"{(Items.KeyColumn >= 0 ? it.Long(Items.KeyColumn) : 0)} ({m}/{s})");
        }
        if (bad.Count > 0) Warnings.Add("Món trỏ tới danh mục không tồn tại: " + string.Join(", ", bad));
    }

    public override byte[] ToBytes()
    {
        if (Categories.Rows.Count == 0) throw new InvalidDataException("Shop cần ít nhất một danh mục chính.");
        var w = new BinWriter();
        w.U32(Convert.ToUInt32(Header.Rows.Count > 0 ? Header.Rows[0].Values[0] ?? 0u : 0u));
        w.I32(Items.Rows.Count);
        foreach (var it in Items.Rows) RecordIO.WriteFixed(w, ItemLayout.Columns, it);
        w.I32(Categories.Rows.Count);
        foreach (var c in Categories.Rows)
        {
            RecordIO.WriteCell(w, CategoryColumns[0], c, 0);
            RecordIO.WriteCell(w, CategoryColumns[1], c, 1);
            var subs = c.Values[2] as List<Record> ?? [];
            w.I32(subs.Count);
            foreach (var s in subs) RecordIO.WriteFixed(w, SubCategoryColumns, s);
        }
        return w.ToArray();
    }
}
