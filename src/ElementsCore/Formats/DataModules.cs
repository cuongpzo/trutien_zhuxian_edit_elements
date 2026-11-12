namespace ElementsCore.Formats;

public enum ModuleView { Elements, Table, Text }

/// <summary>One file family of the game (a tab of the left activity bar).</summary>
public sealed class ModuleInfo
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    /// <summary>Segoe Fluent / MDL2 icon glyph.</summary>
    public required string Glyph { get; init; }
    private readonly string _description = "";
    /// <summary>One-line explanation (in the current UI language).</summary>
    public required string Description { get => L.Retranslate(_description) ?? ""; init => _description = value; }
    /// <summary>File name patterns ("gshop*.data"), used by the open dialog and the client file finder.</summary>
    public required string[] Patterns { get; init; }
    /// <summary>Where the files live relative to the client root ("element\data", "element\maps\*"), or null (server side / anywhere).</summary>
    public string? ClientFolder { get; init; }
    public bool ServerSide { get; init; }
    public ModuleView View { get; init; } = ModuleView.Table;

    public string DialogFilter =>
        $"{Title} ({string.Join(";", Patterns)})|{string.Join(";", Patterns)}|{L.T("All files")}|*.*";
}

public static class DataModules
{
    public static readonly IReadOnlyList<ModuleInfo> All =
    [
        new() { Key = "elements", Title = "Elements", Glyph = "", View = ModuleView.Elements,
                Description = L.T("elements.data – all items, NPCs, monsters, skills… (318 lists + NPC dialogs)."),
                Patterns = ["elements.data"], ClientFolder = @"element\data" },
        new() { Key = "path", Title = "Path", Glyph = "",
                Description = L.T("path.data – the ID → model/icon path table that elements.data refers to."),
                Patterns = ["path.data"], ClientFolder = @"element\data" },
        new() { Key = "objects", Title = "Objects", Glyph = "",
                Description = L.T("dynamicobjects.data – doors, teleport gates… that the server can toggle."),
                Patterns = ["dynamicobjects.data"], ClientFolder = @"element\data" },
        new() { Key = "domains", Title = "Domains", Glyph = "",
                Description = L.T("domain*.data – guild territories, battlefields, world map."),
                Patterns = ["domain*.data"], ClientFolder = @"element\data" },
        new() { Key = "drops", Title = "Drops", Glyph = "", ServerSide = true,
                Description = L.T("extra_drops.sev – extra monster drops (server-side file)."),
                Patterns = ["extra_drops.sev", "*drop*.sev", "*.sev"] },
        new() { Key = "quiz", Title = "Quiz", Glyph = "", ServerSide = true,
                Description = L.T("Quiz question bank (server-side file)."),
                Patterns = ["*question*", "*quiz*"] },
        new() { Key = "lua", Title = "Lua", Glyph = "", View = ModuleView.Text,
                Description = L.T("Lua scripts / text config files (encoding preserved)."),
                Patterns = ["*.lua", "*.txt", "*.ini"] },
        new() { Key = "areas", Title = "Areas", Glyph = "", View = ModuleView.Text,
                Description = L.T("precinct.clt / region.clt of each map – areas, background music, respawn points."),
                Patterns = ["precinct.clt", "region.clt", "*.clt", "*.sev"], ClientFolder = @"element\maps\*" },
        new() { Key = "tasks", Title = "Tasks", Glyph = "",
                Description = L.T("tasks.data + tasks.data1…N – task names, NPC dialogues and option texts (task logic kept as is)."),
                Patterns = ["tasks.data", "tasks.data*"], ClientFolder = @"element\data" },
        new() { Key = "dyntasks", Title = "Dyn Tasks", Glyph = "",
                Description = L.T("dyn_tasks.data – dynamic tasks / events."),
                Patterns = ["dyn_tasks.data"], ClientFolder = @"element\data" },
        new() { Key = "tasknpc", Title = "Task NPC", Glyph = "",
                Description = L.T("task_npc.data – NPCs that give/accept tasks and their locations."),
                Patterns = ["task_npc.data"], ClientFolder = @"element\data" },
        new() { Key = "npcgen", Title = "NPC Gen", Glyph = "", ServerSide = true,
                Description = L.T("npcgen.data – NPC/monster/mine spawn points (server-side file)."),
                Patterns = ["npcgen.data"] },
        new() { Key = "gshop", Title = "GShop", Glyph = "",
                Description = L.T("gshop*.data – shops (Shop, Lucky bag, cross-server…)."),
                Patterns = ["gshop*.data"], ClientFolder = @"element\data" },
        new() { Key = "vip", Title = "VIP", Glyph = "",
                Description = L.T("vipaward.data – VIP awards."),
                Patterns = ["vipaward.data"], ClientFolder = @"element\data" },
        new() { Key = "ai", Title = "AI", Glyph = "", ServerSide = true,
                Description = L.T("aipolicy.data – monster AI scripts (server-side file)."),
                Patterns = ["aipolicy.data"] },
    ];

    public static ModuleInfo Get(string key) => All.First(m => m.Key == key);

    private sealed record Format(string Module, Func<byte[], bool> Sniff, Func<byte[], string, DataFile> Parse);

    /// <summary>Decoded formats, in content-sniffing order (each verified byte-exact on the Client0619 files).</summary>
    private static readonly Format[] Formats =
    [
        new("path", PathTableFile.Sniff, PathTableFile.Parse),        // also dynamicobjects.data (same layout)
        new("domains", DomainFile.Sniff, DomainFile.Parse),
        new("tasknpc", TaskNpcFile.Sniff, TaskNpcFile.Parse),
        new("vip", VipAwardFile.Sniff, VipAwardFile.Parse),
        new("gshop", GShopFile.Sniff, GShopFile.Parse),
        new("dyntasks", DynTasksFile.Sniff, DynTasksFile.Parse),
        new("tasks", TasksFile.Sniff, TasksFile.Parse),
    ];

    private static string FormatKey(string module) => module == "objects" ? "path" : module;

    /// <summary>Opens a file for a module: the decoded format when known, otherwise text or raw hex.</summary>
    public static DataFile Load(ModuleInfo m, string path) => Parse(m, File.ReadAllBytes(path), path);

    /// <summary>Same as <see cref="Load"/> from bytes already read (also used for the "as opened" baseline).</summary>
    public static DataFile Parse(ModuleInfo m, byte[] data, string path)
    {
        if (m.View == ModuleView.Text) return TextDataFile.Parse(data, path);

        DataFile? f = TryParse(m, data, path, out var why);
        if (f != null) return f;
        return RawDataFile.Parse(data, path, why ?? (m.ServerSide
            ? L.T("This server-side file format is not decoded yet (no sample file to verify against).")
            : L.T("This format is not decoded yet.")));
    }

    /// <summary>
    /// Parses with the module's own format first; if the content does not match, tries the other known
    /// formats (a file opened in the wrong tab still shows correctly). Null + reason when nothing fits.
    /// </summary>
    public static DataFile? TryParse(ModuleInfo m, byte[] data, string path, out string? reason)
    {
        reason = null;
        var own = Formats.FirstOrDefault(x => x.Module == FormatKey(m.Key));
        if (own != null)
        {
            try
            {
                if (own.Sniff(data)) return own.Parse(data, path);
                reason = L.F("The content does not look like a known {0} file.", m.Title);
            }
            catch (Exception ex) { reason = L.F("Reading as the {0} format failed: {1}", m.Title, ex.Message); }
        }
        foreach (var x in Formats)
        {
            if (x == own) continue;
            try { if (x.Sniff(data)) return x.Parse(data, path); }
            catch { }
        }
        return null;
    }
}
