namespace ElementsCore.Schema;

public enum SchemaMatch
{
    /// <summary>Schema of the file's own version, size matches.</summary>
    Exact,
    /// <summary>Schema borrowed from another version with the same record size.</summary>
    Borrowed,
    /// <summary>No matching schema: fields were guessed (id / name / raw ints).</summary>
    Auto,
}

public sealed class ListSchema
{
    public required string Struct { get; init; }
    public string? DisplayName { get; init; }
    public required int Size { get; init; }
    public required IReadOnlyList<FieldDef> Fields { get; init; }
    public SchemaMatch Match { get; init; }
    /// <summary>Layout version the schema came from (e.g. "v165"), null for auto.</summary>
    public string? SourceVersion { get; init; }

    public FieldDef? IdField => Fields.FirstOrDefault(f => f.Offset == 0 && f.Path.Equals("id", StringComparison.OrdinalIgnoreCase))
                               ?? Fields.FirstOrDefault(f => f.Offset == 0);

    public FieldDef? NameField => Fields.FirstOrDefault(f => f.IsText && f.Path.Equals("name", StringComparison.OrdinalIgnoreCase))
                                 ?? Fields.FirstOrDefault(f => f.IsText);

    public FieldDef? Find(string path) => Fields.FirstOrDefault(f => f.Path == path);
}

public sealed record EnumValue(long Value, string Label, string? Description);
public sealed record MaskFlag(int Bit, string Label, string? Description);

public sealed class EnumDef
{
    public required string Key { get; init; }
    public string? Label { get; init; }
    public Dictionary<long, EnumValue> Values { get; } = new();
    public List<MaskFlag> Flags { get; } = new();
    public bool IsMask => Flags.Count > 0;

    public string Describe(long v)
    {
        if (IsMask)
        {
            if (v == 0) return "(none)";
            var parts = Flags.Where(f => f.Bit < 64 && (v & (1L << f.Bit)) != 0).Select(f => f.Label).ToList();
            return parts.Count == 0 ? "" : string.Join(", ", parts);
        }
        return Values.TryGetValue(v, out var e) ? e.Label : "";
    }
}
