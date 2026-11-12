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
          tasks-test  <tasks.data> <empty out folder> Rebuild check + edit/save/re-read test on a copy
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
        if (df is ElementsCore.Formats.TasksFile tfile && !tfile.RebuildEqualsOriginal(out var diff)) { bad++; Console.WriteLine($"    BAD rebuild through the text editor path: {diff}"); }
    }
    return bad == 0 ? 0 : 2;
}

if (args[0].Equals("tasks-dump", StringComparison.OrdinalIgnoreCase) && args.Length >= 3)
{
    // debugging aid: every text found in the tasks, tab separated
    var tf = ElementsCore.Formats.TasksFile.Parse(File.ReadAllBytes(args[1]), args[1]);
    using var sw1 = new StreamWriter(args[2], false, new UTF8Encoding(true));
    foreach (var t in tf.Tables)
        foreach (var r in t.Rows)
            sw1.WriteLine(t.Name + "\t" + string.Join("\t", r.Values.Select(v => (v?.ToString() ?? "").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", " "))));
    return 0;
}

if (args[0].Equals("tasks-test", StringComparison.OrdinalIgnoreCase) && args.Length >= 3)
{
    // edit test on a copy: change name / dialogue / text / option of a few tasks, save into the out folder, read back
    var src = args[1]; var outDir = args[2];
    Directory.CreateDirectory(outDir);
    if (Directory.GetFiles(outDir).Length > 0) { Console.WriteLine("Out folder must be empty."); return 1; }
    var sw0 = Stopwatch.StartNew();
    var tf = ElementsCore.Formats.TasksFile.Parse(File.ReadAllBytes(src), src);
    Console.WriteLine($"Read in {sw0.ElapsedMilliseconds} ms: {tf.FormatName} · {string.Join(", ", tf.Tables.Select(t => $"{t.Name} {t.Rows.Count}"))}");
    foreach (var w in tf.Warnings.Take(5)) Console.WriteLine("  ! " + w);
    Console.WriteLine("Rebuild check: " + (tf.RebuildEqualsOriginal(out var d0) ? "OK" : "BAD " + d0));
    var rnd = new Random(7);
    var edits = new List<(int T, int R, string V)>();
    void Edit(int t, int r, int col, string v) { tf.Tables[t].Rows[r][col] = v; edits.Add((t, r, v)); }
    var tt = tf.Tables;
    for (int i = 0; i < 40; i++)
    {
        int r = rnd.Next(tt[0].Rows.Count); Edit(0, r, 3, "Tên thử " + i);
        r = rnd.Next(tt[1].Rows.Count); Edit(1, r, 5, tt[1].Rows[r].Text(5) + " — Việt hoá thử " + i + " ^ffcb4a màu");
        r = rnd.Next(tt[1].Rows.Count); Edit(1, r, 5, "Ngắn");
        r = rnd.Next(tt[2].Rows.Count); Edit(2, r, 5, "Lựa chọn " + i);
    }
    var target = Path.Combine(outDir, Path.GetFileName(src));
    sw0.Restart();
    tf.Save(target, backup: false);
    Console.WriteLine($"Saved {Directory.GetFiles(outDir).Length} file(s) in {sw0.ElapsedMilliseconds} ms");
    // packs that were not changed are not written to another folder? (save-as writes all) → read back
    var back = ElementsCore.Formats.TasksFile.Parse(File.ReadAllBytes(target), target);
    int fail = 0;
    for (int t = 0; t < 3; t++)
    {
        var a = tf.Tables[t]; var b = back.Tables[t];
        if (t == 0 && a.Rows.Count != b.Rows.Count) { Console.WriteLine("row count differs"); fail++; }
    }
    foreach (var (t, r, v) in edits)
    {
        var row = tf.Tables[t].Rows[r];
        int col = t == 0 ? 3 : 5;
        // find the same item in the re-read file by (pack, slot, offset)
        var match = back.Tables[t].Rows.FirstOrDefault(x => t == 0
            ? x.Long(0) == row.Long(0) && x.Long(1) == row.Long(1)
            : x.Long(0) == row.Long(0) && x.Long(1) == row.Long(1) && x.Long(t == 1 ? 4 : 3) == row.Long(t == 1 ? 4 : 3));
        var got = match?.Text(col);
        var want = row.Text(col);
        if (got != want) { fail++; if (fail < 10) Console.WriteLine($"  MISMATCH table {t} row {r}: want \"{want}\" got \"{got ?? "(not found)"}\""); }
    }
    Console.WriteLine($"Index MD5 check: {(back.Warnings.Any(w => w.Contains("MD5")) ? "BAD" : "OK")}");
    Console.WriteLine(fail == 0 ? $"Edit test OK ({edits.Count} edits)" : $"Edit test: {fail} mismatch(es)");
    return fail == 0 ? 0 : 2;
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
