namespace ElementsCore.Formats;

public enum ModuleView { Elements, Table, Text }

/// <summary>One file family of the game (a tab of the left activity bar).</summary>
public sealed class ModuleInfo
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    /// <summary>Segoe Fluent / MDL2 icon glyph.</summary>
    public required string Glyph { get; init; }
    public required string Description { get; init; }
    /// <summary>File name patterns ("gshop*.data"), used by the open dialog and the client file finder.</summary>
    public required string[] Patterns { get; init; }
    /// <summary>Where the files live relative to the client root ("element\data", "element\maps\*"), or null (server side / anywhere).</summary>
    public string? ClientFolder { get; init; }
    public bool ServerSide { get; init; }
    public ModuleView View { get; init; } = ModuleView.Table;

    public string DialogFilter =>
        $"{Title} ({string.Join(";", Patterns)})|{string.Join(";", Patterns)}|Tất cả|*.*";
}

public static class DataModules
{
    public static readonly IReadOnlyList<ModuleInfo> All =
    [
        new() { Key = "elements", Title = "Elements", Glyph = "", View = ModuleView.Elements,
                Description = "elements.data – toàn bộ vật phẩm, NPC, quái, kỹ năng… (318 list + hội thoại NPC).",
                Patterns = ["elements.data"], ClientFolder = @"element\data" },
        new() { Key = "path", Title = "Path", Glyph = "",
                Description = "path.data – bảng ID → đường dẫn model/icon mà elements.data tham chiếu.",
                Patterns = ["path.data"], ClientFolder = @"element\data" },
        new() { Key = "objects", Title = "Objects", Glyph = "",
                Description = "dynamicobjects.data – cửa, cổng dịch chuyển… server bật/tắt được.",
                Patterns = ["dynamicobjects.data"], ClientFolder = @"element\data" },
        new() { Key = "domains", Title = "Domains", Glyph = "",
                Description = "domain*.data – lãnh địa bang hội, chiến trường, bản đồ thế giới.",
                Patterns = ["domain*.data"], ClientFolder = @"element\data" },
        new() { Key = "drops", Title = "Drops", Glyph = "", ServerSide = true,
                Description = "extra_drops.sev – rơi đồ thêm của quái (file phía server).",
                Patterns = ["extra_drops.sev", "*drop*.sev", "*.sev"] },
        new() { Key = "quiz", Title = "Quiz", Glyph = "", ServerSide = true,
                Description = "Ngân hàng câu hỏi đố vui (file phía server).",
                Patterns = ["*question*", "*quiz*"] },
        new() { Key = "lua", Title = "Lua", Glyph = "", View = ModuleView.Text,
                Description = "Script .lua / file cấu hình dạng văn bản (giữ nguyên bảng mã).",
                Patterns = ["*.lua", "*.txt", "*.ini"] },
        new() { Key = "areas", Title = "Areas", Glyph = "", View = ModuleView.Text,
                Description = "precinct.clt / region.clt của từng bản đồ – khu vực, nhạc nền, điểm hồi sinh.",
                Patterns = ["precinct.clt", "region.clt", "*.clt", "*.sev"], ClientFolder = @"element\maps\*" },
        new() { Key = "tasks", Title = "Tasks", Glyph = "",
                Description = "tasks.data – nhiệm vụ.",
                Patterns = ["tasks.data", "tasks.data*"], ClientFolder = @"element\data" },
        new() { Key = "dyntasks", Title = "Dyn Tasks", Glyph = "",
                Description = "dyn_tasks.data – nhiệm vụ động / hoạt động.",
                Patterns = ["dyn_tasks.data"], ClientFolder = @"element\data" },
        new() { Key = "tasknpc", Title = "Task NPC", Glyph = "",
                Description = "task_npc.data – NPC giao/nhận nhiệm vụ và vị trí.",
                Patterns = ["task_npc.data"], ClientFolder = @"element\data" },
        new() { Key = "npcgen", Title = "NPC Gen", Glyph = "", ServerSide = true,
                Description = "npcgen.data – điểm sinh NPC/quái/mỏ (file phía server).",
                Patterns = ["npcgen.data"] },
        new() { Key = "gshop", Title = "GShop", Glyph = "",
                Description = "gshop*.data – cửa hàng (Shop, Lucky bag, liên server…).",
                Patterns = ["gshop*.data"], ClientFolder = @"element\data" },
        new() { Key = "vip", Title = "VIP", Glyph = "",
                Description = "vipaward.data – quà VIP.",
                Patterns = ["vipaward.data"], ClientFolder = @"element\data" },
        new() { Key = "ai", Title = "AI", Glyph = "", ServerSide = true,
                Description = "aipolicy.data – kịch bản AI của quái (file phía server).",
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
            ? "Định dạng file phía server này chưa được giải mã (chưa có file mẫu để kiểm chứng)."
            : "Định dạng này chưa được giải mã."));
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
                reason = $"Nội dung không giống file {m.Title} đã biết.";
            }
            catch (Exception ex) { reason = $"Đọc theo định dạng {m.Title} thất bại: {ex.Message}"; }
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
