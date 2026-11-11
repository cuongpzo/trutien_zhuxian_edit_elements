using ElementsCore.Formats;

namespace ElementsCore.Assets;

/// <summary>
/// Game pictures for the editor (read-only): element\surfaces.pck + element\data\path.data of a client.
/// <list type="bullet">
/// <item>Item / skill icons are packed in atlases: surfaces\iconset\iconlist*.dds (a grid of 36×36 icons)
/// with iconlist*.txt (icon size, grid, then one file name per icon, GBK). elements.data stores
/// "file_icon" as a path.data ID → "Surfaces\男物品\天尊宝录·木.dds" → looked up by file name in the atlases.</item>
/// <item>Other pictures (gshop "Surfaces\QShop\…dds", ui .tga) are separate files in the package.</item>
/// </list>
/// </summary>
public sealed class IconLibrary : IDisposable
{
    public string ClientRoot { get; }
    public PckArchive? Surfaces { get; private set; }
    public PathTableFile? Paths { get; private set; }
    public List<string> Messages { get; } = new();

    private sealed class Atlas
    {
        public required string Name { get; init; }
        public required string DdsPath { get; init; }
        public int IconW, IconH, Cols;
        public byte[]? Dds;
        public TextureDecoder.DdsInfo Info;
    }

    private readonly List<Atlas> _atlases = new();
    private readonly Dictionary<string, (Atlas atlas, int index)> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _pathById = new();
    private readonly Dictionary<string, Bgra?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int IconCount => _byName.Count;

    private IconLibrary(string root) { ClientRoot = root; }

    /// <summary>Opens the client's packages (slow part: the file table + icon name lists, ~1 s).</summary>
    public static IconLibrary Open(string clientRoot)
    {
        var lib = new IconLibrary(clientRoot);
        var element = Path.Combine(clientRoot, "element");
        var pathData = Path.Combine(element, "data", "path.data");
        if (File.Exists(pathData))
        {
            lib.Paths = PathTableFile.Parse(File.ReadAllBytes(pathData), pathData);
            foreach (var r in lib.Paths.Entries.Rows) lib._pathById[r.Long(0)] = r.Text(1);
        }
        else lib.Messages.Add("Không thấy element\\data\\path.data.");

        var surf = Path.Combine(element, "surfaces.pck");
        if (File.Exists(surf))
        {
            try
            {
                lib.Surfaces = new PckArchive(surf);
                lib.LoadAtlasLists();
            }
            catch (Exception ex) { lib.Messages.Add("Không đọc được surfaces.pck: " + ex.Message); }
        }
        else lib.Messages.Add("Không thấy element\\surfaces.pck.");
        return lib;
    }

    private void LoadAtlasLists()
    {
        var pck = Surfaces!;
        // iconlist.txt (items) first: a name found there wins over the other sheets
        var lists = pck.Entries.Where(e => e.Path.StartsWith(@"surfaces\iconset\", StringComparison.OrdinalIgnoreCase)
                                          && e.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                                          && e.Path.Count(c => c == '\\') == 2)
                               .OrderBy(e => Path.GetFileName(e.Path).Equals("iconlist.txt", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                               .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var e in lists)
        {
            var dds = Path.ChangeExtension(e.Path, ".dds");
            if (!pck.Contains(dds)) continue;
            var raw = pck.Read(e.Path);
            if (raw == null || raw.Length == 0) continue;
            var lines = Text(raw).Split('\n').Select(l => l.Trim('\r', ' ', '\t', '﻿')).ToList();
            if (lines.Count < 5 || !int.TryParse(lines[0], out int w) || !int.TryParse(lines[1], out int h) || w <= 0 || h <= 0) continue;
            var atlas = new Atlas { Name = Path.GetFileNameWithoutExtension(e.Path), DdsPath = dds, IconW = w, IconH = h };
            _atlases.Add(atlas);
            int index = 0;
            foreach (var name in lines.Skip(4))
            {
                if (name.Length == 0) continue;
                _byName.TryAdd(name, (atlas, index));
                index++;
            }
        }
    }

    private static string Text(byte[] raw)
    {
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) return System.Text.Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) return System.Text.Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
        return FieldCodec.Gbk.GetString(raw);
    }

    /// <summary>Resource path of a path.data ID ("Models\…\x.ecm", "Surfaces\…\icon.dds"), or null.</summary>
    public string? PathOf(long id) => _pathById.TryGetValue(id, out var p) ? p : null;

    /// <summary>Picture for a path.data ID (icons; other resources return null).</summary>
    public Bgra? IconById(long id) => PathOf(id) is { } p ? Icon(p) : null;

    /// <summary>
    /// Picture for a resource path: the file itself when it is in surfaces.pck (dds / tga), otherwise
    /// the icon of that file name in the icon atlases. Null when not found / not a picture.
    /// </summary>
    public Bgra? Icon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var hit)) return hit;
            Bgra? img = null;
            try { img = Resolve(path); } catch { }
            if (_cache.Count > 20000) _cache.Clear();
            _cache[path] = img;
            return img;
        }
    }

    private Bgra? Resolve(string path)
    {
        var pck = Surfaces;
        if (pck == null) return null;
        var norm = PckArchive.Normalize(path);
        if (!norm.StartsWith("surfaces\\", StringComparison.OrdinalIgnoreCase)) norm = "surfaces\\" + norm;
        var name = Path.GetFileName(norm);
        // 1) icon sheet by file name (item / skill icons)
        if (_byName.TryGetValue(name, out var at)) return FromAtlas(at.atlas, at.index);
        // 2) a picture file of its own
        if (pck.Contains(norm) && (name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)))
        {
            if (pck.TryGet(norm, out var e) && e.Size > 8 * 1024 * 1024) return null; // not an icon
            var data = pck.Read(norm);
            return data == null ? null : TextureDecoder.Decode(data);
        }
        return null;
    }

    private Bgra? FromAtlas(Atlas a, int index)
    {
        if (a.Dds == null)
        {
            a.Dds = Surfaces!.Read(a.DdsPath);
            if (a.Dds == null || !TextureDecoder.IsDds(a.Dds)) return null;
            a.Info = TextureDecoder.ReadDdsInfo(a.Dds);
            a.Cols = Math.Max(1, a.Info.Width / a.IconW);
        }
        int col = index % a.Cols, row = index / a.Cols;
        var rect = new TextureDecoder.Rectangle(col * a.IconW, row * a.IconH, a.IconW, a.IconH);
        if (rect.Y + rect.H > a.Info.Height) return null;
        return TextureDecoder.DecodeDds(a.Dds, rect);
    }

    public void Dispose() => Surfaces?.Dispose();
}
