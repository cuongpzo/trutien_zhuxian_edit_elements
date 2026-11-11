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
        [0] = L.T("None"),
        [1] = L.T("Level 1 (small)"),
        [2] = L.T("Level 2 (medium)"),
        [3] = L.T("Level 3 (large / capital)"),
    };

    public static readonly IReadOnlyDictionary<long, string> WeekdayEnum = new Dictionary<long, string>
    {
        // counted from Monday 00:00 (as JD IDE / the server's domain.sev): the samples' slots are
        // Sat/Sun/Mon 20:00, Sun/Mon 14:00, Sun/Mon 09:00
        [0] = L.T("Monday"), [1] = L.T("Tuesday"), [2] = L.T("Wednesday"), [3] = L.T("Thursday"),
        [4] = L.T("Friday"), [5] = L.T("Saturday"), [6] = L.T("Sunday"),
    };

    public static readonly IReadOnlyList<ColumnDef> VertColumns =
    [
        new() { Name = "x", Type = ColType.I32, Note = L.T("X on the map image (pixels)") },
        new() { Name = "y", Type = ColType.I32, Note = L.T("Y on the map image (pixels)") },
    ];

    public static readonly IReadOnlyList<ColumnDef> FaceColumns =
    [
        new() { Name = "v0", Type = ColType.I32, Note = L.T("First vertex index (from 0)") },
        new() { Name = "v1", Type = ColType.I32, Note = L.T("Second vertex index (from 0)") },
        new() { Name = "v2", Type = ColType.I32, Note = L.T("Third vertex index (from 0)") },
    ];

    public static readonly IReadOnlyList<ColumnDef> NeighbourColumns =
    [
        new() { Name = "domain_id", Type = ColType.I32, Note = L.T("Adjacent domain ID (must exist in the file)") },
    ];

    // Column indices of the "Domains" table.
    public const int ColId = 0, ColName = 1, ColRank = 2, ColReward = 3, ColCenterX = 4, ColCenterY = 5,
        ColVerts = 6, ColFaces = 7, ColNeighbours = 8;

    public static readonly IReadOnlyList<ColumnDef> DomainColumns =
    [
        new() { Name = "id", Type = ColType.I32, Note = L.T("Domain ID (used by the server)") },
        new() { Name = "name", Type = ColType.WStr, Length = NameChars, Note = L.T("Domain name (max 15 characters)") },
        new() { Name = "type", Type = ColType.I32, Enum = TypeEnum, Note = L.T("Domain level (1–3)") },
        new() { Name = "reward", Type = ColType.I32, Note = L.T("Reward (0 in every sample file)") },
        new() { Name = "center_x", Type = ColType.I32, Note = L.T("Center X: where the name is shown / click point") },
        new() { Name = "center_y", Type = ColType.I32, Note = L.T("Center Y: where the name is shown / click point") },
        new() { Name = "verts", Type = ColType.Table, SubColumns = VertColumns, HideInGrid = true, Note = L.T("Outline vertices (polygon, ≥ 3 vertices)") },
        new() { Name = "faces", Type = ColType.Table, SubColumns = FaceColumns, HideInGrid = true, Note = L.T("Triangles covering the area (usually vertex count − 2)") },
        new() { Name = "neighbours", Type = ColType.Table, SubColumns = NeighbourColumns, Note = L.T("Adjacent domains (should be declared both ways)") },
    ];

    public static readonly IReadOnlyList<ColumnDef> BattleTimeColumns =
    [
        new() { Name = "weekday", Type = ColType.I32, Enum = WeekdayEnum, Note = L.T("Day of week (0 = Monday … 6 = Sunday)") },
        new() { Name = "hour", Type = ColType.I32, Note = L.T("Hour (0–23)") },
        new() { Name = "minute", Type = ColType.I32, Note = L.T("Minute (0–59)") },
    ];

    public const int HdrBattlesPerSlot = 0, HdrTrailing = 1;

    public static readonly IReadOnlyList<ColumnDef> HeaderColumns =
    [
        new() { Name = "battles_per_slot", Type = ColType.I32, Note = L.T("Max battles per time slot") },
        new() { Name = "trailing", Type = ColType.Bytes, ReadOnly = true, HideInGrid = true, Note = L.T("Extra bytes at the end of the file (kept as is)") },
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
                "domain.data" => L.T("guild map"),
                "domain1.data" => L.T("territory war"),
                "domain2.data" => L.T("cross-server map"),
                "domain3.data" => L.T("world map"),
                "domain4.data" => L.T("ancient world map"),
                "domainlandwar.data" => L.T("land war"),
                _ => L.T("domain"),
            };
            return L.F("{0} ({1}, {2} domains)", n.Length > 0 ? n : "domain.data", kind, Domains.Rows.Count);
        }
    }

    public static bool Sniff(byte[] d) => d.Length >= 8 && BitConverter.ToUInt32(d, 0) == Magic;

    public static DomainFile Parse(byte[] data, string path)
    {
        var r = new BinReader(data);
        if (r.U32() != Magic) throw new InvalidDataException(L.T("Not a domain file (missing MOXB signature)."));
        int count = r.I32();
        if (count < 0 || count > 100000) throw new InvalidDataException(L.F("Invalid domain count: {0}.", count));

        var f = new DomainFile { Path = path };
        var domains = new RecordTable
        {
            Name = "Domains", Columns = DomainColumns, KeyColumn = ColId, NameColumn = ColName,
            Note = L.T("One domain per row: outline, triangles and adjacent domains."),
        };
        var times = new RecordTable
        {
            Name = "Battle times", Columns = BattleTimeColumns,
            Note = L.T("Weekly battle time slots."),
        };
        var header = new RecordTable
        {
            Name = "Header", Columns = HeaderColumns, CanAddRemove = false,
            Note = L.T("File-wide values."),
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
            rec.Values[ColVerts] = ReadList(r, VertColumns, L.F("domain #{0}: vertex count", i));
            rec.Values[ColFaces] = ReadList(r, FaceColumns, L.F("domain #{0}: triangle count", i));
            rec.Values[ColNeighbours] = ReadList(r, NeighbourColumns, L.F("domain #{0}: adjacent domain count", i));
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
            foreach (var t in ReadList(r, BattleTimeColumns, L.T("battle time slot count"))) times.Rows.Add(t);
            hdr.Values[HdrBattlesPerSlot] = r.I32();
            if (!r.AtEnd)
            {
                hdr.Values[HdrTrailing] = r.Bytes(r.Remaining);
                f.Warnings.Add(L.F("{0} bytes after the battle-time section (kept when saving).", ((byte[])hdr.Values[HdrTrailing]!).Length));
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
            throw new InvalidDataException(L.F("{0} is invalid ({1}) at offset {2}.", what, n, r.Pos - 4));
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
            if (!ids.TryAdd(id, d)) Add(L.F("Domain ID {0} is duplicated: the client only finds the first row.", id));
        }

        foreach (var d in Domains.Rows)
        {
            long id = d.Long(ColId);
            string tag = L.F("Domain {0} ({1})", id, d.Text(ColName));
            var verts = d.Values[ColVerts] as List<Record> ?? [];
            var faces = d.Values[ColFaces] as List<Record> ?? [];
            var nbs = d.Values[ColNeighbours] as List<Record> ?? [];

            if (verts.Count < 3 || faces.Count == 0)
                Add(L.F("{0}: has no area (fewer than 3 vertices or no triangles) — not visible / not clickable on the map.", tag));
            for (int k = 0; k < faces.Count; k++)
                for (int j = 0; j < 3; j++)
                {
                    long v = faces[k].Long(j);
                    if (v < 0 || v >= verts.Count)
                    {
                        Add(L.F("{0}: triangle #{1} uses vertex {2}, outside the list ({3} vertices).", tag, k, v, verts.Count));
                        j = 3;
                    }
                }

            var seen = new HashSet<long>();
            foreach (var n in nbs)
            {
                long nid = n.Long(0);
                if (nid == id) { Add(L.F("{0}: lists itself as an adjacent domain.", tag)); continue; }
                if (!seen.Add(nid)) { Add(L.F("{0}: adjacent domain {1} is repeated.", tag, nid)); continue; }
                if (!ids.TryGetValue(nid, out var other)) { Add(L.F("{0}: adjacent domain {1} is not in the file.", tag, nid)); continue; }
                var back = other.Values[ColNeighbours] as List<Record> ?? [];
                if (!back.Any(x => x.Long(0) == id)) Add(L.F("{0}: adjacent domain {1} does not list it back.", tag, nid));
            }
        }

        foreach (var t in BattleTimes.Rows)
        {
            long wd = t.Long(0), h = t.Long(1), m = t.Long(2);
            if (wd is < 0 or > 6 || h is < 0 or > 23 || m is < 0 or > 59)
                Add(L.F("Invalid battle time: day {0}, {1}:{2:00}.", wd, h, m));
        }
        return res;
    }
}
