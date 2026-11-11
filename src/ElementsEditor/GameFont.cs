using System.Drawing.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ElementsCore;

namespace ElementsEditor;

/// <summary>
/// Font for game text (Chinese names, descriptions, NPC dialogs …), chosen in the "Font" menu.
/// Fonts shipped with the program live in the "fonts" folder next to the exe (e.g. DFKai-SB.ttf) and
/// are loaded for this process only (not installed into Windows); installed CJK fonts can be picked too.
/// Controls showing game data register with <see cref="Bind"/> and follow every change live.
/// </summary>
public static class GameFont
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);
    private const uint FR_PRIVATE = 0x10;

    private static readonly PrivateFontCollection _private = new();
    private static readonly List<string> _privateFiles = new();
    private static readonly List<WeakReference<object>> _bound = new();
    private static readonly ConditionalWeakTable<object, Font> _original = new();
    private static readonly Dictionary<(string, float, FontStyle), Font> _cache = new();
    private static bool _loaded;

    /// <summary>Family name for game text; null = the controls' own font.</summary>
    public static string? Family { get; private set; }
    /// <summary>Size factor (1.0 = as designed).</summary>
    public static float Scale { get; private set; } = 1f;
    public static event Action? Changed;

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "fonts");

    /// <summary>Fonts shipped in the fonts folder (family names).</summary>
    public static IReadOnlyList<string> ProgramFonts
    {
        get { LoadProgramFonts(); return _private.Families.Select(f => f.Name).Distinct().ToList(); }
    }

    /// <summary>Installed fonts that cover Chinese (only those present on this PC).</summary>
    public static IReadOnlyList<string> InstalledCjkFonts()
    {
        string[] wanted = ["Microsoft YaHei", "Microsoft YaHei UI", "Microsoft JhengHei", "SimSun", "NSimSun", "SimHei", "KaiTi", "FangSong",
                           "DFKai-SB", "MingLiU", "PMingLiU", "Yu Gothic UI", "Meiryo", "MS Gothic", "Malgun Gothic", "Noto Sans SC", "Noto Serif SC", "Source Han Sans SC"];
        using var ifc = new InstalledFontCollection();
        var have = ifc.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.Where(have.Contains).ToList();
    }

    /// <summary>Loads fonts\*.ttf|otf|ttc for this process (GDI and GDI+). Safe to call many times.</summary>
    public static void LoadProgramFonts()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.GetFiles(Folder).Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    AddFontResourceEx(f, FR_PRIVATE, IntPtr.Zero); // text boxes / grids draw with GDI
                    _private.AddFontFile(f);                       // GDI+ needs the family object
                    _privateFiles.Add(f);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Applies the saved choice at start-up.</summary>
    public static void Init(string? family, float scale)
    {
        LoadProgramFonts();
        Family = string.IsNullOrWhiteSpace(family) ? null : family;
        Scale = scale is >= 0.6f and <= 3f ? scale : 1f;
    }

    public static void Set(string? family, float scale)
    {
        Family = string.IsNullOrWhiteSpace(family) ? null : family;
        Scale = scale is >= 0.6f and <= 3f ? scale : 1f;
        _cache.Clear(); // old fonts stay alive: controls may still reference them until repainted
        ApplyAll();
        Changed?.Invoke();
    }

    private static FontFamily? FamilyObject()
    {
        if (Family == null) return null;
        var p = _private.Families.FirstOrDefault(f => string.Equals(f.Name, Family, StringComparison.OrdinalIgnoreCase));
        if (p != null) return p;
        try { return new FontFamily(Family); } catch { return null; }
    }

    /// <summary>A font of a bundled (fonts folder) or installed family, or null.</summary>
    public static Font? Create(string family, float size)
    {
        LoadProgramFonts();
        var p = _private.Families.FirstOrDefault(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));
        try { return p != null ? new Font(p, size) : new Font(new FontFamily(family), size); }
        catch { return null; }
    }

    /// <summary>The game-text version of a control's own font (null = keep the original).</summary>
    public static Font? Get(Font original)
    {
        if (Family == null && Scale == 1f) return null;
        var fam = FamilyObject() ?? original.FontFamily;
        var style = fam.IsStyleAvailable(original.Style) ? original.Style : FontStyle.Regular;
        var key = (fam.Name, original.Size * Scale, style);
        if (!_cache.TryGetValue(key, out var f)) { _cache[key] = f = new Font(fam, original.Size * Scale, style, original.Unit); _made.Add(f); }
        return f;
    }

    /// <summary>A control (or DataGridView cells) that shows game text: follows the chosen font.</summary>
    public static void Bind(object target)
    {
        lock (_bound) _bound.Add(new WeakReference<object>(target));
        Apply(target);
    }

    private static void ApplyAll()
    {
        List<object> alive = new();
        lock (_bound)
        {
            _bound.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in _bound) if (w.TryGetTarget(out var t)) alive.Add(t);
        }
        foreach (var t in alive) Apply(t);
    }

    private static void Apply(object target)
    {
        try
        {
            switch (target)
            {
                case DataGridView g:
                    {
                        // fonts are swapped while each cell is formatted, so every column follows
                        // (also the bold value columns the theme adds later) and nothing is scaled twice
                        if (_hooked.TryAdd(g, true))
                            g.CellFormatting += (_, e) =>
                            {
                                if (e.CellStyle?.Font is { } f && (Family != null || Scale != 1f) && !_made.Contains(f))
                                    e.CellStyle.Font = Get(f) ?? f;
                            };
                        var probe = Get(g.DefaultCellStyle.Font ?? g.Font);
                        if (probe != null) g.RowTemplate.Height = Math.Max(g.RowTemplate.Height, (int)(probe.GetHeight() + 10));
                        g.Invalidate();
                        break;
                    }
                case ListBox lb when lb.DrawMode != DrawMode.Normal:
                    {
                        var orig = Original(lb, lb.Font);
                        lb.Font = Get(orig) ?? orig;
                        lb.Invalidate();
                        break;
                    }
                case Control c:
                    {
                        var orig = Original(c, c.Font);
                        c.Font = Get(orig) ?? orig;
                        c.Invalidate();
                        break;
                    }
            }
        }
        catch { }
    }

    private static readonly ConditionalWeakTable<DataGridView, object> _hooked = new();
    /// <summary>Fonts this class created (never converted again).</summary>
    private static readonly HashSet<Font> _made = new();

    private static Font Original(object key, Font current)
    {
        if (_original.TryGetValue(key, out var f)) return f;
        _original.Add(key, current);
        return current;
    }

    /// <summary>"DFKai-SB · 125%" for menus / status.</summary>
    public static string Describe() => (Family ?? L.T("Default")) + (Scale != 1f ? $" · {Scale:P0}" : "");
}
