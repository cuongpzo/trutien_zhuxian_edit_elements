using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ElementsCore.Schema;

public enum MarkerKind { Checksum, Exporter, Tag }

public sealed class LayoutInfo
{
    public required string Version { get; init; }
    public int ListCount { get; init; }
    public bool ListCountVerified { get; init; } = true;
    public Dictionary<int, MarkerKind> Markers { get; } = new();
}

/// <summary>
/// Loads the JSON schema folder:
///   schemas/layouts/vNNN/layout.json   – list count + markers for that elements version
///   schemas/layouts/vNNN/list_N.json   – record layout of list N
///   schemas/enums/*.json, schemas/masks/*.json
/// </summary>
public sealed class SchemaRepository
{
    private readonly string _root;
    private readonly Dictionary<int, string> _versionDirs = new();
    private readonly Dictionary<string, JsonDocument?> _docCache = new();
    public Dictionary<string, EnumDef> Enums { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Root => _root;
    public IReadOnlyCollection<int> Versions => _versionDirs.Keys;

    /// <summary>Locate the schemas directory next to the executable or in ancestor folders.</summary>
    public static string FindDefaultRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (int depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
            {
                var cand = System.IO.Path.Combine(dir.FullName, "schemas");
                if (Directory.Exists(System.IO.Path.Combine(cand, "layouts"))) return cand;
            }
        }
        return System.IO.Path.Combine(AppContext.BaseDirectory, "schemas");
    }

    public SchemaRepository(string root)
    {
        _root = root;
        var layouts = System.IO.Path.Combine(root, "layouts");
        if (Directory.Exists(layouts))
        {
            foreach (var dir in Directory.GetDirectories(layouts))
            {
                var m = Regex.Match(System.IO.Path.GetFileName(dir), @"^v(\d+)$");
                if (m.Success) _versionDirs[int.Parse(m.Groups[1].Value)] = dir;
            }
        }
        LoadEnums(System.IO.Path.Combine(root, "enums"));
        LoadEnums(System.IO.Path.Combine(root, "masks"));
    }

    private void LoadEnums(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                var r = doc.RootElement;
                var key = r.TryGetProperty("key", out var k) ? k.GetString()! : System.IO.Path.GetFileNameWithoutExtension(file);
                var e = new EnumDef { Key = key, Label = r.TryGetProperty("label", out var l) ? l.GetString() : null };
                if (r.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                    foreach (var v in vals.EnumerateArray())
                    {
                        if (!v.TryGetProperty("value", out var vv) || vv.ValueKind != JsonValueKind.Number) continue;
                        var val = vv.GetInt64();
                        e.Values[val] = new EnumValue(val, Str(v, "label") ?? val.ToString(), Str(v, "description"));
                    }
                if (r.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Array)
                    foreach (var f in flags.EnumerateArray())
                        if (f.TryGetProperty("bit", out var b) && b.ValueKind == JsonValueKind.Number)
                            e.Flags.Add(new MaskFlag(b.GetInt32(), Str(f, "label") ?? "bit" + b.GetInt32(), Str(f, "description")));
                Enums[key] = e;
            }
            catch { /* ignore malformed enum */ }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>Versions ordered by closeness to the requested one (exact first).</summary>
    private IEnumerable<int> CandidateVersions(int version) =>
        _versionDirs.Keys.OrderBy(v => v == version ? -1 : Math.Abs(v - version)).ThenByDescending(v => v);

    public LayoutInfo? GetLayout(int version)
    {
        foreach (var v in CandidateVersions(version))
        {
            var doc = LoadDoc(System.IO.Path.Combine(_versionDirs[v], "layout.json"));
            if (doc == null) continue;
            var r = doc.RootElement;
            var info = new LayoutInfo
            {
                Version = "v" + v,
                ListCount = r.TryGetProperty("listCount", out var lc) ? lc.GetInt32() : 0,
                ListCountVerified = !(r.TryGetProperty("listCountUnverified", out var u) && u.ValueKind == JsonValueKind.True),
            };
            if (r.TryGetProperty("markers", out var ms))
                foreach (var m in ms.EnumerateArray())
                {
                    var kind = Str(m, "kind");
                    var mk = kind switch { "checksum" => MarkerKind.Checksum, "exporter" => MarkerKind.Exporter, "tag" => MarkerKind.Tag, _ => (MarkerKind?)null };
                    if (mk != null) info.Markers[m.GetProperty("before").GetInt32()] = mk.Value;
                }
            return info;
        }
        return null;
    }

    private JsonDocument? LoadDoc(string path)
    {
        if (_docCache.TryGetValue(path, out var d)) return d;
        JsonDocument? doc = null;
        if (File.Exists(path))
        {
            try { doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)); } catch { doc = null; }
        }
        _docCache[path] = doc;
        return doc;
    }

    /// <summary>Best human name for a list (struct name) from any version.</summary>
    public string? GuessListName(int version, int listIndex)
    {
        foreach (var v in CandidateVersions(version))
        {
            var doc = LoadDoc(System.IO.Path.Combine(_versionDirs[v], $"list_{listIndex}.json"));
            if (doc == null) continue;
            var s = Str(doc.RootElement, "struct") ?? Str(doc.RootElement, "name");
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    /// <summary>
    /// Finds the schema of a list: the file's own version first, then other versions
    /// whose record size matches, shifted-index fallback for inserted lists, and finally smart auto-detection.
    /// </summary>
    public ListSchema Resolve(int version, int listIndex, int itemSize, IReadOnlyList<byte[]> samples)
    {
        foreach (var v in CandidateVersions(version))
        {
            if (TryBuildSchema(v, listIndex, itemSize, v == version ? SchemaMatch.Exact : SchemaMatch.Borrowed, "v" + v, out var exact))
                return exact!;
        }

        // Handle inserted/removed lists between client versions (e.g. v165 vs v160 shifted +1 around 184, 211..225)
        if (itemSize > 0)
        {
            foreach (int delta in new[] { -1, 1, -2, 2 })
            {
                int candIdx = listIndex + delta;
                if (candIdx < 0) continue;
                foreach (var v in CandidateVersions(version))
                {
                    if (v == version) continue;
                    if (!TryBuildSchema(v, candIdx, itemSize, SchemaMatch.Borrowed, $"v{v}#{candIdx}", out var shifted))
                        continue;
                    if (samples.Count == 0 || shifted!.NameField == null || LooksLikeTextField(samples, shifted.NameField))
                        return shifted!;
                }
            }
        }

        return AutoSchema(GuessListName(version, listIndex) ?? $"LIST_{listIndex}", itemSize, samples);
    }

    private bool TryBuildSchema(int layoutVersion, int fileListIndex, int itemSize, SchemaMatch match, string sourceVer, out ListSchema? schema)
    {
        schema = null;
        var doc = LoadDoc(System.IO.Path.Combine(_versionDirs[layoutVersion], $"list_{fileListIndex}.json"));
        if (doc == null) return false;
        var r = doc.RootElement;
        if (!r.TryGetProperty("fields", out var fields) || !r.TryGetProperty("size", out var sz)) return false;
        if (sz.GetInt32() != itemSize) return false;

        var list = new List<FieldDef>();
        foreach (var f in fields.EnumerateArray()) ParseField(f, "", 0, null, list);
        list.RemoveAll(f => f.Offset < 0 || f.Offset + f.ByteSize > itemSize);
        list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        schema = new ListSchema
        {
            Struct = Str(r, "struct") ?? $"LIST_{fileListIndex}",
            DisplayName = Str(r, "name"),
            Size = itemSize,
            Fields = list,
            Match = match,
            SourceVersion = sourceVer,
        };
        return true;
    }

    private static bool LooksLikeTextField(IReadOnlyList<byte[]> samples, FieldDef f) =>
        f.Kind == FieldKind.WStr ? LooksLikeWString(samples, f.Offset, f.Length) : false;

    private static void ParseField(JsonElement f, string prefix, int baseOff, string? group, List<FieldDef> output)
    {
        var name = Str(f, "name") ?? "?";
        var off = baseOff + (f.TryGetProperty("off", out var o) ? o.GetInt32() : 0);
        group = Str(f, "g") ?? group;
        var meta = new Meta(
            group,
            Str(f, "c"),
            Str(f, "e"),
            Str(f, "display"),
            f.TryGetProperty("refs", out var refs) && refs.ValueKind == JsonValueKind.Array
                ? refs.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : null,
            ParseWhen(f));
        if (f.TryGetProperty("t", out var t)) AddType(t, prefix + name, off, meta, output);
    }

    private sealed record Meta(string? Group, string? Comment, string? Enum, string? Display, List<string>? Refs, List<FieldWhen>? When);

    private static List<FieldWhen>? ParseWhen(JsonElement f)
    {
        if (!f.TryGetProperty("when", out var w) || w.ValueKind != JsonValueKind.Array) return null;
        var list = new List<FieldWhen>();
        foreach (var c in w.EnumerateArray())
        {
            var field = Str(c, "field");
            if (field == null || !c.TryGetProperty("t", out var t)) continue;
            var kind = MapKind(Str(t, "k"));
            if (kind == null) continue;
            var set = new HashSet<long>();
            if (c.TryGetProperty("in", out var inArr))
                foreach (var x in inArr.EnumerateArray()) if (x.ValueKind == JsonValueKind.Number) set.Add(x.GetInt64());
            list.Add(new FieldWhen(field, set, kind.Value));
        }
        return list.Count > 0 ? list : null;
    }

    private static void AddType(JsonElement t, string path, int off, Meta meta, List<FieldDef> output)
    {
        var k = Str(t, "k");
        if (k == "struct" && t.TryGetProperty("fields", out var sf))
        {
            foreach (var sub in sf.EnumerateArray()) ParseField(sub, path + ".", off, meta.Group, output);
            return;
        }
        if (k == "array" && t.TryGetProperty("t", out var et))
        {
            var n = t.TryGetProperty("n", out var nn) ? nn.GetInt32() : 0;
            var stride = t.TryGetProperty("stride", out var st) ? st.GetInt32() : 0;
            for (int i = 0; i < n; i++)
                AddType(et, $"{path}[{i}]", off + i * stride, meta, output);
            return;
        }
        var kind = MapKind(k);
        if (kind == null) return;
        output.Add(new FieldDef
        {
            Path = path,
            Offset = off,
            Kind = kind.Value,
            Length = t.TryGetProperty("n", out var len) ? len.GetInt32() : 0,
            Group = meta.Group,
            Comment = meta.Comment,
            Enum = meta.Enum,
            Display = meta.Display,
            Refs = meta.Refs,
            When = meta.When,
        });
    }

    private static FieldKind? MapKind(string? k) => k switch
    {
        "i8" => FieldKind.I8, "u8" => FieldKind.U8,
        "i16" => FieldKind.I16, "u16" => FieldKind.U16,
        "i32" => FieldKind.I32, "u32" => FieldKind.U32,
        "i64" => FieldKind.I64, "u64" => FieldKind.U64,
        "f32" => FieldKind.F32, "f64" => FieldKind.F64,
        "bool" => FieldKind.Bool,
        "wstr" => FieldKind.WStr, "str" => FieldKind.Str, "bytes" => FieldKind.Bytes,
        _ => null,
    };

    /// <summary>Guess: int id at 0, aligned UTF-16LE strings across samples, and raw int32 / byte chunks.</summary>
    public static ListSchema AutoSchema(string structName, int size, IReadOnlyList<byte[]> samples)
    {
        var fields = new List<FieldDef>();
        int pos = 0;
        if (size >= 4)
        {
            fields.Add(new FieldDef { Path = "id", Offset = 0, Kind = FieldKind.I32, Group = "Auto" });
            pos = 4;
        }
        if (samples.Count == 0 && size > 2048)
        {
            if (size >= 68)
            {
                fields.Add(new FieldDef { Path = "name", Offset = 4, Kind = FieldKind.WStr, Length = 32, Group = "Auto" });
                pos = 68;
            }
            fields.Add(new FieldDef { Path = $"raw_{pos:X4}", Offset = pos, Kind = FieldKind.Bytes, Length = size - pos, Group = "Unknown" });
            return new ListSchema { Struct = structName, Size = size, Fields = fields, Match = SchemaMatch.Auto };
        }

        // Detect all 4-byte aligned null-padded UTF-16LE text fields (prioritizing name @ 4 -> 32 chars, then 32 or 16 chars)
        var used = new bool[size];
        for (int b = 0; b < Math.Min(4, size); b++) used[b] = true;

        int textIdx = 0;
        for (int off = 4; off + 16 <= size; off += 4)
        {
            if (used[off]) continue;
            int charLen = DetectWStringChars(samples, off, size);
            if (charLen <= 0) continue;

            string name = off == 4 ? "name" : $"text_{++textIdx}_{off:X4}";
            fields.Add(new FieldDef { Path = name, Offset = off, Kind = FieldKind.WStr, Length = charLen, Group = "Auto" });
            int byteLen = charLen * 2;
            for (int b = off; b < off + byteLen && b < size; b++) used[b] = true;
            off += byteLen - 4; // loop increments by 4
        }

        pos = 4;
        while (pos < size)
        {
            if (used[pos]) { pos++; continue; }
            int end = pos;
            while (end < size && !used[end]) end++;
            int p = pos;
            while (p + 4 <= end)
            {
                fields.Add(new FieldDef { Path = $"unk_{p:X4}", Offset = p, Kind = FieldKind.I32, Group = "Unknown" });
                p += 4;
            }
            if (p < end)
                fields.Add(new FieldDef { Path = $"unk_{p:X4}", Offset = p, Kind = FieldKind.Bytes, Length = end - p, Group = "Unknown" });
            pos = end;
        }

        fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return new ListSchema { Struct = structName, Size = size, Fields = fields, Match = SchemaMatch.Auto };
    }

    private static int DetectWStringChars(IReadOnlyList<byte[]> samples, int off, int recordSize)
    {
        if (samples.Count == 0) return 0;
        // Prefer standard JD wstr buffer sizes: 64B (32 chars) or 32B (16 chars)
        int maxAllowedBytes = recordSize - off;
        if (maxAllowedBytes < 16) return 0;

        int hasText = 0, valid = 0, maxContentBytes = 0;
        foreach (var s in samples.Take(30))
        {
            if (s.Length < off + 16) return 0;
            // Read UTF-16LE chars until 0x0000
            int k = 0;
            bool bad = false;
            bool sawCjkOrAscii = false;
            while (off + k + 1 < s.Length)
            {
                ushort ch = (ushort)(s[off + k] | (s[off + k + 1] << 8));
                if (ch == 0) break;
                if ((ch < 0x20 && ch != '\r' && ch != '\n' && ch != '\t') || (ch >= 0xD800 && ch <= 0xDFFF) || ch == 0xFFFD)
                {
                    bad = true;
                    break;
                }
                // Filter false positives from small integers like 0x0001..0x001F (already rejected) or lone ASCII punct
                if ((ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
                    sawCjkOrAscii = true;
                k += 2;
                if (k > 512) { bad = true; break; }
            }
            if (bad || off + k + 1 >= s.Length) return 0;
            if (k >= 4 && sawCjkOrAscii)
            {
                hasText++;
                if (k + 2 > maxContentBytes) maxContentBytes = k + 2;
            }
            else if (k > 0) return 0; // 1-char suspicious junk (likely an integer like 50/100)
            valid++;
        }

        if (valid == 0 || hasText == 0) return 0;
        // Try standard sizes (64B if off==4 or fits well with zero tail across samples, else 32B)
        foreach (int candBytes in new[] { 64, 32 })
        {
            if (candBytes > maxAllowedBytes || candBytes < maxContentBytes) continue;
            if (off != 4 && candBytes == 64 && !AllZeroAfterNull(samples, off, candBytes)) continue;
            if (AllZeroAfterNull(samples, off, candBytes)) return candBytes / 2;
        }
        return 0;
    }

    private static bool AllZeroAfterNull(IReadOnlyList<byte[]> samples, int off, int byteLen)
    {
        foreach (var s in samples.Take(30))
        {
            int k = 0;
            while (k + 1 < byteLen && (s[off + k] != 0 || s[off + k + 1] != 0)) k += 2;
            if (k + 1 >= byteLen) return false; // didn't see null terminator inside buffer
            for (int z = k; z < byteLen; z++)
                if (s[off + z] != 0) return false;
        }
        return true;
    }

    private static bool LooksLikeWString(IReadOnlyList<byte[]> samples, int off, int chars)
    {
        if (samples.Count == 0) return false;
        int good = 0, total = 0;
        foreach (var s in samples.Take(30))
        {
            if (s.Length < off + chars * 2) return false;
            total++;
            var str = FieldCodec.DecodeWString(s, off, chars);
            if (str.Length > 0 && str.All(c => c >= 0x20 && !char.IsSurrogate(c) && c != 0xFFFD)) good++;
        }
        return total > 0 && good * 10 >= total * 6;
    }
}
