namespace ElementsCore.Formats;

/// <summary>
/// path.data and dynamicobjects.data: an ID → resource path table.
/// <code>
/// char[4] "DIMP"; u32 count;
/// count × { u32 id; u32 len; char path[len] (GBK, no terminator) }
/// </code>
/// elements.data refers to model/icon files by these IDs (path.data); dynamicobjects.data lists
/// the doors, portals and other scene objects the server can show or hide.
/// </summary>
public sealed class PathTableFile : DataFile
{
    public const uint Magic = 0x504D4944; // "DIMP"
    public static readonly IReadOnlyList<ColumnDef> Columns =
    [
        new() { Name = "id", Type = ColType.U32, Note = L.T("ID that elements.data / the server uses to refer to it") },
        new() { Name = "path", Type = ColType.Str, Note = L.T("File path inside the .pck packages (GBK)") },
    ];

    public bool IsDynamicObjects { get; private set; }
    public override string FormatName => IsDynamicObjects ? L.T("dynamicobjects.data (ID → model)") : L.T("path.data (ID → path)");
    public RecordTable Entries => Tables[0];

    public static bool Sniff(byte[] d) => d.Length >= 8 && BitConverter.ToUInt32(d, 0) == Magic;

    public static PathTableFile Parse(byte[] data, string path)
    {
        var r = new BinReader(data);
        if (r.U32() != Magic) throw new InvalidDataException(L.T("Not a path table (missing DIMP signature)."));
        int count = r.I32();
        var f = new PathTableFile { Path = path, IsDynamicObjects = System.IO.Path.GetFileName(path).Contains("dynamic", StringComparison.OrdinalIgnoreCase) };
        var t = new RecordTable
        {
            Name = f.IsDynamicObjects ? "Dynamic objects" : "Paths",
            Columns = Columns, KeyColumn = 0, NameColumn = 1,
        };
        for (int i = 0; i < count; i++)
        {
            var rec = new Record(2);
            rec.Values[0] = r.U32();
            int len = r.I32();
            var raw = r.Bytes(len);
            rec.Values[1] = RecordIO.DecodeVar(ColType.Str, raw);
            rec.Raw[1] = raw;
            t.Rows.Add(rec);
        }
        if (!r.AtEnd) f.Warnings.Add(L.F("{0} extra bytes after the table (dropped on save).", r.Remaining));
        var dup = t.Rows.GroupBy(x => x.Long(0)).Where(g => g.Count() > 1).Select(g => g.Key).Take(5).ToList();
        if (dup.Count > 0) f.Warnings.Add(L.F("Duplicate IDs: {0}", string.Join(", ", dup)));
        f.Tables.Add(t);
        return f;
    }

    public override byte[] ToBytes()
    {
        var w = new BinWriter();
        w.U32(Magic);
        w.I32(Entries.Rows.Count);
        foreach (var rec in Entries.Rows)
        {
            w.U32(Convert.ToUInt32(rec.Values[0]));
            var b = rec.Raw[1] ?? RecordIO.EncodeVar(ColType.Str, rec.Text(1));
            w.I32(b.Length);
            w.Bytes(b);
        }
        return w.ToArray();
    }
}
