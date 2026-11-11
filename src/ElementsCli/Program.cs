using System.Diagnostics;
using System.Text;
using ElementsCore;
using ElementsCore.Schema;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 1 && args[0] is "--version" or "-v" or "version")
{
    Console.WriteLine(BuildInfo.Tag);
    return 0;
}
if (args.Length < 2)
{
    Console.WriteLine($"ElementsCli {BuildInfo.Tag}");
    Console.WriteLine("""
        ElementsCli <command> <elements.data> [arguments]
          --version                               Print the version
          info        <file>                      List the lists + schema status
          verify      <file>                      Read, rewrite in memory, compare byte by byte
          search      <file> <keyword / ID>       Find entries by ID or text across all lists
          export-json <file> <listIndex> <out.json> Export one list to JSON
          export      <file> <out.csv> [--glossary] [--all] [--lists 0,3,9]
                                                  Export strings for translation (default: only strings with Chinese characters)
          import      <file> <in.csv> [--out <file>]  Import a translation (detects detailed / glossary CSV)
          [--schemas <dir>]                       Schema folder (default: auto-detect next to the exe)
          data        <file|folder> [module]      Other files (gshop, domain, path, task_npc, vipaward…):
                                                  format, row count, byte-identical rewrite check (read-only)
        """);
    return 1;
}

if (args[0].Equals("data", StringComparison.OrdinalIgnoreCase))
{
    // read-only check of the non-elements formats: parse → ToBytes → compare
    var target = args[1];
    var files = Directory.Exists(target) ? Directory.GetFiles(target).Where(f => !f.EndsWith("elements.data", StringComparison.OrdinalIgnoreCase)).ToArray() : [target];
    int bad = 0;
    foreach (var f in files.Order())
    {
        var data = File.ReadAllBytes(f);
        var module = args.Length > 2
            ? ElementsCore.Formats.DataModules.Get(args[2])
            : ElementsCore.Formats.DataModules.All.FirstOrDefault(m => m.Patterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, Path.GetFileName(f))))
              ?? ElementsCore.Formats.DataModules.Get("path");
        var df = ElementsCore.Formats.DataModules.TryParse(module, data, f, out var why);
        if (df == null) { Console.WriteLine($"{Path.GetFileName(f),-24} [{module.Title}] not decoded: {why}"); continue; }
        bool same = df.ToBytes().AsSpan().SequenceEqual(data);
        if (!same) bad++;
        Console.WriteLine($"{Path.GetFileName(f),-24} {(same ? "OK  " : "BAD ")} {df.FormatName} · {string.Join(", ", df.Tables.Select(t => $"{t.Name} {t.Rows.Count}"))}");
        foreach (var w in df.Warnings.Take(5)) Console.WriteLine("    ! " + w);
    }
    return bad == 0 ? 0 : 2;
}

string Opt(string name, string? def = null) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def!; }
bool Flag(string name) => args.Contains(name);

var repo = new SchemaRepository(Opt("--schemas", SchemaRepository.FindDefaultRoot()));
var cmd = args[0].ToLowerInvariant();
var file = args[1];
var sw = Stopwatch.StartNew();
var ef = ElementsFile.Load(file, repo);
Console.WriteLine($"Read {file} in {sw.ElapsedMilliseconds} ms: version {ef.Version}, {ef.Lists.Count} list, layout {ef.Layout?.Version ?? "-"}, dialogs {ef.Dialogs?.Count.ToString() ?? "raw"}");
foreach (var w in ef.Warnings) Console.WriteLine("! " + w);

switch (cmd)
{
    case "info":
        foreach (var l in ef.Lists)
            Console.WriteLine($"{l.Index,3} {l.Name,-36} size={l.ItemSize,6} count={l.Items.Count,6} schema={l.Schema.Match}{(l.Schema.SourceVersion is { } v ? "/" + v : "")} fields={l.Schema.Fields.Count}{(l.PrefixKind is { } k ? " [" + k + "]" : "")}");
        var byMatch = ef.Lists.GroupBy(l => l.Schema.Match).Select(g => $"{g.Key}={g.Count()}");
        Console.WriteLine(string.Join(", ", byMatch));
        break;

    case "verify":
        var orig = File.ReadAllBytes(file);
        var re = ef.ToBytes();
        int diff = -1;
        for (int i = 0; i < Math.Min(orig.Length, re.Length); i++) if (orig[i] != re[i]) { diff = i; break; }
        if (diff < 0 && orig.Length == re.Length) { Console.WriteLine($"OK: rewrite is identical, {orig.Length:N0} bytes."); return 0; }
        Console.WriteLine($"DIFFERENT: original {orig.Length} bytes, rewritten {re.Length} bytes, first difference at 0x{diff:X}");
        return 2;

    case "export":
    {
        var outPath = args[2];
        ISet<int>? lists = args.Contains("--lists") ? Opt("--lists").Split(',').Select(int.Parse).ToHashSet() : null;
        var entries = TextExchange.Enumerate(ef, lists, dialogs: lists == null);
        int n = Flag("--glossary") ? TextExchange.ExportGlossary(entries, outPath, !Flag("--all")) : TextExchange.ExportDetailed(entries, outPath, !Flag("--all"));
        Console.WriteLine($"Exported {n:N0} rows -> {outPath}");
        break;
    }

    case "import":
    {
        var rep = TextExchange.Import(ef, args[2]);
        Console.WriteLine(rep);
        foreach (var line in rep.Log.Take(50)) Console.WriteLine("  " + line);
        if (rep.Log.Count > 50) Console.WriteLine($"  ... and {rep.Log.Count - 50} more lines");
        var outFile = Opt("--out", file);
        var bak = ef.Save(outFile);
        Console.WriteLine($"Saved {outFile}" + (bak != null ? $" (backup: {bak})" : ""));
        break;
    }

    case "search":
    {
        if (args.Length < 3) { Console.WriteLine("Missing search keyword."); return 1; }
        var hits = ElementsOps.Search(ef, args[2], maxHits: 200);
        Console.WriteLine($"Found {hits.Count} results:");
        foreach (var h in hits)
            Console.WriteLine($"  [{(h.IsDialog ? "TALK" : h.ListIndex.ToString())}] {h.ListName} | ID={h.Id} | {h.FieldPath}: {h.MatchedText}");
        break;
    }

    case "export-json":
    {
        if (args.Length < 4) { Console.WriteLine("Missing listIndex or out.json."); return 1; }
        int idx = int.Parse(args[2]);
        var list = ef.Lists.FirstOrDefault(l => l.Index == idx);
        if (list == null) { Console.WriteLine($"List {idx} not found"); return 1; }
        ElementsOps.ExportListToJson(list, ef, args[3]);
        Console.WriteLine($"Exported {list.Items.Count} records of {list.Name} -> {args[3]}");
        break;
    }

    default:
        Console.WriteLine("Invalid command: " + cmd);
        return 1;
}
return 0;
