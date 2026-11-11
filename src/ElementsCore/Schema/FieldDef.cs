namespace ElementsCore.Schema;

public enum FieldKind
{
    I8, U8, I16, U16, I32, U32, I64, U64, F32, F64, Bool,
    /// <summary>UTF-16LE fixed buffer, Length = number of characters.</summary>
    WStr,
    /// <summary>GBK fixed buffer, Length = number of bytes.</summary>
    Str,
    /// <summary>Raw bytes, Length = number of bytes.</summary>
    Bytes,
}

/// <summary>Conditional type override ("when" in the JSON schema).</summary>
public sealed record FieldWhen(string Field, HashSet<long> In, FieldKind Kind);

/// <summary>A flattened, leaf field inside one record.</summary>
public sealed class FieldDef
{
    public required string Path { get; init; }
    public required int Offset { get; init; }
    public required FieldKind Kind { get; init; }
    /// <summary>Chars for WStr, bytes for Str/Bytes; unused for scalars.</summary>
    public int Length { get; init; }
    public string? Group { get; init; }
    public string? Comment { get; init; }
    public string? Enum { get; init; }
    public string? Display { get; init; }
    public IReadOnlyList<string>? Refs { get; init; }
    public IReadOnlyList<FieldWhen>? When { get; init; }

    public int ByteSize => SizeOf(Kind, Length);

    public bool IsText => Kind is FieldKind.WStr or FieldKind.Str;

    public static int SizeOf(FieldKind k, int length) => k switch
    {
        FieldKind.I8 or FieldKind.U8 or FieldKind.Bool => 1,
        FieldKind.I16 or FieldKind.U16 => 2,
        FieldKind.I32 or FieldKind.U32 or FieldKind.F32 => 4,
        FieldKind.I64 or FieldKind.U64 or FieldKind.F64 => 8,
        FieldKind.WStr => length * 2,
        FieldKind.Str or FieldKind.Bytes => length,
        _ => 0,
    };

    public override string ToString() => $"{Path} @{Offset} {Kind}";
}
