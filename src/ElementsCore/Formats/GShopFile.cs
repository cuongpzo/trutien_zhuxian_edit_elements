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
        [0] = L.T("Normal"), [1] = L.T("New"), [2] = L.T("Recommended"), [3] = L.T("New + Recommended"),
    };

    private static readonly IReadOnlyDictionary<long, string> ValidTypeEnum = new Dictionary<long, string>
    {
        [0] = L.T("Date range (start → end)"), [1] = L.T("From start date, repeating?"),
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
    private static ColumnDef Pad(string n, int len, string? note = null) =>
        new() { Name = n, Type = ColType.Bytes, Length = len, Note = note ?? L.T("Unknown (always 0 in the sample files)"), HideInGrid = true };

    /// <summary>GSHOP_ITEM of the C++ source, packed (2630 B).</summary>
    private static List<ColumnDef> SourceColumns() =>
    [
        U32("id", L.T("Item ID sold (elements.data)")),
        U32("num", L.T("Quantity per purchase")),
        Str("icon", 128, L.T("Shop icon image (GBK)"), hide: true),
        U32("price", L.T("Price (usually in 1/100 yuanbao)")),
        U32("time", L.T("Duration, seconds (0 = permanent)")),
        I32("discount", L.T("Discount % (100 = full price, 50 = half price)")),
        I32("bonus", L.T("Bonus (not seen in use, always 0)"), hide: true),
        U8("props", L.T("Label: 1 = New, 2 = Recommended"), e: PropsEnum),
        U8("props_b1", L.T("Byte 2 of props (always 0)"), hide: true),
        U8("props_b2", L.T("Byte 3 of props (always 255)"), hide: true),
        U8("props_b3", L.T("Byte 4 of props: 128 when 50% off"), hide: true),
        I32("main_type", L.T("Main category (index in the Categories table)")),
        I32("sub_type", L.T("Subcategory (index within the main category)")),
        I32("local_id", L.T("Internal ID / order (different for each item)")),
        WStr("description", 512, L.T("Description (color codes ^RRGGBB, line break \\r\\n)"), hide: true),
        WStr("name", 32, L.T("Shop display name")),
        new() { Name = "has_present", Type = ColType.Bool, Note = L.T("Has a bundled gift (always 0 in the samples)"), HideInGrid = true },
        WStr("present_name", 32, L.T("Bundled gift name"), hide: true),
        U32("present_id", L.T("Bundled gift ID"), hide: true),
        U32("present_count", L.T("Gift quantity"), hide: true),
        U32("present_time", L.T("Gift duration, seconds"), hide: true),
        Str("present_icon", 128, L.T("Gift icon (GBK)"), hide: true),
        new() { Name = "present_bind", Type = ColType.Bool, Note = L.T("Gift is bound"), HideInGrid = true },
        WStr("present_description", 512, L.T("Gift description"), hide: true),
        I32("valid_type", L.T("Sale time type"), hide: true, e: ValidTypeEnum),
        I32("valid_start", L.T("Sale start (Unix time, 0 = always on sale)"), hide: true),
        I32("valid_end", L.T("Sale end (Unix time) / duration for type 1"), hide: true),
        I32("valid_param", L.T("Sale time parameter"), hide: true),
        WStr("search_keys", 64, L.T("Search keywords (comma-separated)"), hide: true),
    ];

    /// <summary>TT3 2660 B: Source + 30 extra bytes.</summary>
    private static List<ColumnDef> Tt3Columns()
    {
        var c = SourceColumns();
        c.AddRange(
        [
            U8("x_flag1", L.T("Unknown: 1 together with x_flag2 (pets, masks…)"), hide: true),
            U32("x_price1", L.T("Unknown: usually equals the price or the original price (12800, 9900, 2500…)"), hide: true),
            U8("x_flag2", L.T("Unknown: second flag"), hide: true),
            U32("x_price2", L.T("Unknown: second price"), hide: true),
            U32("x_value", L.T("Unknown: 1 on a few items (Decade pack)"), hide: true),
            I32("valid2_type", L.T("Sale time (copy 2) – type"), hide: true, e: ValidTypeEnum),
            I32("valid2_start", L.T("Sale time (copy 2) – start (Unix time)"), hide: true),
            I32("valid2_end", L.T("Sale time (copy 2) – end (Unix time)"), hide: true),
            I32("valid2_param", L.T("Sale time (copy 2) – parameter"), hide: true),
        ]);
        return c;
    }

    private static List<ColumnDef> OldClientColumns() =>
    [
        U32("id", L.T("Item ID sold (elements.data)")),
        U32("num", L.T("Quantity per purchase")),
        Str("icon", 128, L.T("Shop icon image (GBK)"), hide: true),
        U32("price", L.T("Price")),
        U32("time", L.T("Duration, seconds (0 = permanent)")),
        I32("unknown_144", L.T("Unknown (always 0; possibly the discount)"), hide: true),
        new() { Name = "props", Type = ColType.U32, Note = L.T("Label: 1 = New, 2 = Recommended"), Enum = PropsEnum },
        I32("main_type", L.T("Main category (index in the Categories table)")),
        I32("sub_type", L.T("Subcategory (index within the main category)")),
        I32("local_id", L.T("Internal ID / order")),
        WStr("description", 512, L.T("Description"), hide: true),
        WStr("name", 32, L.T("Shop display name")),
    ];

    private static List<ColumnDef> LuckyBagColumns() =>
    [
        I32("place", L.T("Position in the shop")),
        I32("main_type", L.T("Main category (index in the Categories table)")),
        I32("sub_type", L.T("Subcategory")),
        Str("name", 128, L.T("Box name (GBK)")),
        U32("id", L.T("Lucky bag box ID sold (elements.data)")),
        U32("num", L.T("Quantity per purchase")),
        U32("price_item", L.T("ID of the item used as payment (token)")),
        U32("price_item_count", L.T("Number of tokens to pay")),
        Pad("unknown_156", 36),
        U8("flag_192", L.T("Flag: 1 on items in the New section (unclear)")),
        Str("icon", 128, L.T("Icon image (GBK)"), hide: true),
        I32("valid_type", L.T("Sale time type"), hide: true, e: ValidTypeEnum),
        I32("valid_start", L.T("Sale start (Unix time)"), hide: true),
        I32("valid_end", L.T("Sale end (Unix time) / duration for type 1"), hide: true),
        I32("valid_param", L.T("Sale time parameter"), hide: true),
        Pad("unknown_337", 20),
    ];

    private static List<ColumnDef> ExchangeColumns() =>
    [
        I32("place", L.T("Position in the shop")),
        I32("main_type", L.T("Main category (index in the Categories table)")),
        I32("sub_type", L.T("Subcategory")),
        Str("name", 128, L.T("Display name (GBK)")),
        U32("id", L.T("Item ID sold (elements.data)")),
        U32("num", L.T("Quantity per purchase")),
        U32("price", L.T("Price")),
        Pad("unknown_152", 20),
        I32("value_172", L.T("Unknown (1–20; possibly a purchase limit)")),
        Pad("unknown_176", 16),
        I32("local_id", L.T("Internal ID / order (uncertain)")),
        I32("value_196", L.T("Unknown (always 1 in the samples)")),
    ];

    /// <summary>Known item layouts, in detection priority.</summary>
    public static readonly IReadOnlyList<Layout> Layouts =
    [
        new() { Key = "tt3", Title = L.T("TT3 shop"), Columns = Tt3Columns() },          // 2660
        new() { Key = "source", Title = L.T("Source"), Columns = SourceColumns() },      // 2630
        new() { Key = "oldclient", Title = L.T("Old client"), Columns = OldClientColumns() }, // 1252
        new() { Key = "luckybag", Title = L.T("Lucky bag"), Columns = LuckyBagColumns() }, // 357
        new() { Key = "exchange", Title = L.T("Point exchange"), Columns = ExchangeColumns() },  // 200
    ];

    public static readonly IReadOnlyList<ColumnDef> HeaderColumns =
    [
        new() { Name = "timestamp", Type = ColType.U32, Note = L.T("Version stamp (Unix time); client and server gshop must match") },
    ];

    public static readonly IReadOnlyList<ColumnDef> SubCategoryColumns =
    [
        WStr("name", 64, L.T("Subcategory name")),
    ];

    public static readonly IReadOnlyList<ColumnDef> CategoryColumns =
    [
        I32("id", L.T("Main category ID (usually = order 0,1,2…)")),
        WStr("name", 64, L.T("Main category name")),
        new() { Name = "subs", Type = ColType.Table, SubColumns = SubCategoryColumns, Note = L.T("Subcategories (an item's main/sub_type = index)") },
    ];

    private const int MaxMain = 256, MaxSub = 4096;

    public Layout ItemLayout { get; private set; } = null!;
    public bool LayoutGuessed { get; private set; }

    public RecordTable Header => Tables[0];
    public RecordTable Items => Tables[1];
    public RecordTable Categories => Tables[2];

    public override string FormatName =>
        L.F("gshop ({0}, {1} B/item{2})", ItemLayout.Title, ItemLayout.Size, LayoutGuessed ? L.T(", no items yet – default layout") : "");

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
        if (size < 0) throw new InvalidDataException(L.T("No category list found at the end of the file for any item size: not a gshop file."));
        var f = new GShopFile { Path = path };
        uint count = BitConverter.ToUInt32(data, 4);
        if (layout == null && count == 0)
        {
            layout = Layouts[0];
            f.LayoutGuessed = true;
            f.Warnings.Add(L.F("The file has no items: the default layout {0} ({1} B/item) is used if you add new items.", layout.Title, layout.Size));
        }
        else if (layout == null)
        {
            layout = new Layout
            {
                Key = "raw" + size, Title = L.T("Unknown"),
                Columns = [new() { Name = "data", Type = ColType.Bytes, Length = size, Note = L.T("Raw item data (layout not decoded)") }],
            };
            f.Warnings.Add(L.F("Item layout of {0} B is not decoded: it can only be edited as hex.", size));
        }
        f.ItemLayout = layout;

        var r = new BinReader(data);
        var header = new RecordTable { Name = "Header", Columns = HeaderColumns, CanAddRemove = false, Note = L.T("File header") };
        var h = new Record(1);
        h.Values[0] = r.U32();
        header.Rows.Add(h);
        r.U32(); // count

        var items = new RecordTable
        {
            Name = "Items", Columns = layout.Columns, KeyColumn = layout.KeyColumn, NameColumn = layout.NameColumn,
            Note = L.F("Items for sale ({0}, {1} bytes/item)", layout.Title, layout.Size),
        };
        for (uint i = 0; i < count; i++) items.Rows.Add(RecordIO.ReadFixed(r, layout.Columns));

        var cats = new RecordTable
        {
            Name = "Categories", Columns = CategoryColumns, KeyColumn = 0, NameColumn = 1,
            Note = L.T("Main categories and subcategories (the shop needs at least 1 main category)"),
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
        if (!r.AtEnd) throw new InvalidDataException(L.F("{0} bytes left after the category list.", r.Remaining));

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
        if (bad.Count > 0) Warnings.Add(L.F("Items pointing to a non-existent category: {0}", string.Join(", ", bad)));
    }

    public override byte[] ToBytes()
    {
        if (Categories.Rows.Count == 0) throw new InvalidDataException(L.T("The shop needs at least one main category."));
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
