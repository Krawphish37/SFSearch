// sfsquery: a small command line over SFSearch.Query, for trying SQL against the real Data folder and for the
// numbers in design\agent-first\notes\M6.md. It is not wired into SFSearch's commands (a later milestone does that).
//
//   sfsquery "<sql>" --plugins a.esm,b.esm | --winners | --versions [--allow-heavy] [--json]
//   sfsquery --file query.sql --plugins Starfield.esm
//   sfsquery --views [<record type>]          every view SQL can name (or one type's columns and list views)
//   sfsquery --calibrate <plugin> [--types A,B] [--world]   read each record type once and print what it took
//   sfsquery --saved list | show <name> | check [<name>] | run <name> [parameter=value …] | save <name> --file q.sql [--overwrite]
//            stored queries (milestone M10). The shipped ones are in queries\ beside the program (or --queries <folder>),
//            the user's in <home>\queries and add-ons' in <home>\extensions\X.queries beside X.dll, with the home from
//            --home or SFSEARCH_HOME. "run" takes the scope from the query's header, or from --plugins, --winners, --versions.
//
// Options: --cursor <c> (the next page of a former answer)   --rows <n> (rows a page, default 40)
//          --lang <language> (text columns: English, de, …)   --again <n> (run the query n more times, to time it warm)
//          --data <Data folder> (or SFSEARCH_DATA)   --scratch <folder> (or SFSQUERY_SCRATCH; default %TEMP%\sfsquery-scratch)
//          --fresh (empty the scratch tables first, as a service start does)   --time (print what each step took)
//          --memory-mb <n> (the memory one command may hold, as the service's workerMaxMB gives it: a statement past it
//          is stopped, and big sorts keep a share of it in memory; default none)
// Read only: nothing is ever written to the Data folder. The scratch folder holds the header index's segments and
// the scratch tables; it can be deleted at any time.
using System.Diagnostics;
using SFSearch.Index;
using SFSearch.Query;
using SFSearch.Records;

var rest = args.ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
bool Flag(string name) => rest.Remove(name);

string data = Opt("--data") ?? Environment.GetEnvironmentVariable("SFSEARCH_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";
string scratch = Opt("--scratch") ?? Environment.GetEnvironmentVariable("SFSQUERY_SCRATCH") ?? Path.Combine(Path.GetTempPath(), "sfsquery-scratch");
string? plugins = Opt("--plugins"), file = Opt("--file"), cursor = Opt("--cursor"), lang = Opt("--lang"), calibrate = Opt("--calibrate"), typesOpt = Opt("--types"), stats = Opt("--stats");
string? saved = Opt("--saved"), queriesFolder = Opt("--queries"), home = Opt("--home") ?? Environment.GetEnvironmentVariable("SFSEARCH_HOME");
bool overwrite = Flag("--overwrite");
long memoryMB = long.Parse(Opt("--memory-mb") ?? "0");
int rows = int.Parse(Opt("--rows") ?? "40"), again = int.Parse(Opt("--again") ?? "0"), threads = int.Parse(Opt("--threads") ?? "0");
bool winners = Flag("--winners"), versions = Flag("--versions"), allowHeavy = Flag("--allow-heavy"), asJson = Flag("--json"), fresh = Flag("--fresh"),
    time = Flag("--time"), views = Flag("--views"), world = Flag("--world");
Console.OutputEncoding = System.Text.Encoding.UTF8;

SqliteTuning.NoMemoryStatistics();
var clock = Stopwatch.StartNew();
var steps = new List<string>();
void Step(string what) { steps.Add($"{what} {clock.Elapsed.TotalMilliseconds:F0} ms"); clock.Restart(); }

try
{
    var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
    Step("schema");
    if (views) return Runner.ListViews(new ViewBuilder(schema), rest.FirstOrDefault());

    string sqlScratch = Path.Combine(scratch, "sql");
    if (fresh && Directory.Exists(sqlScratch))
        foreach (string f in Directory.EnumerateFiles(sqlScratch)) { try { File.Delete(f); } catch (IOException) { } }
    using var index = new HeaderIndex(PluginSource.Folder(data), new IndexOptions { ScratchFolder = Path.Combine(scratch, "index"), VersionText = $"sfsquery 1 / mutagen {schema.MutagenVersion} / schema {schema.Version}", Refusals = new SFSearch.Records.MutagenRefusals() });
    index.Refresh();
    Step("header index");
    // Mutagen's one-time start-up (Warmup.Init, about 0.9 s on fixes.13), before the first read rather than in it
    MutagenStart.Run();
    Step("Mutagen start");
    using var records = new RecordEngine(index, schema, Path.Combine(scratch, "verify"), data);
    using var engine = new QueryEngine(records, new QueryOptions { ScratchFolder = sqlScratch, Language = lang ?? "English", Threads = threads > 0 ? threads : Math.Min(Environment.ProcessorCount, 8),
    MemoryLimitMB = memoryMB > 0 ? () => memoryMB : null, MemoryLimitSetting = memoryMB > 0 ? "--memory-mb" : null });
    Step("engine");

    if (calibrate != null) return Runner.Calibrate(engine, index, schema, calibrate, typesOpt, world);
    if (stats != null) return Runner.Stats(engine, index, schema, stats);

    if (saved != null)
    {
        var stored = new StoredQueries(engine, new StoredOptions
        {
            ShippedFolder = queriesFolder ?? Path.Combine(AppContext.BaseDirectory, "queries"),
            UserFolder = string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, "queries"),
            AddOnFolders = string.IsNullOrWhiteSpace(home) ? Array.Empty<string>() : new[] { Path.Combine(home, "extensions") },
        });
        switch (saved.ToLowerInvariant())
        {
            case "list":
                Console.Write(stored.ListText());
                return 0;
            case "show":
                if (rest.Count == 0) { Console.Error.WriteLine("usage: sfsquery --saved show <name>"); return 2; }
                Console.Write(stored.Find(rest[0]).Describe(withSql: true));
                return 0;
            case "check":
            {
                var which = rest.Count > 0 ? new List<StoredQuery> { stored.Find(rest[0]) } : stored.All();
                int faulty = 0;
                foreach (var q in which)
                {
                    var check = stored.Check(q);
                    if (check.Ok) { Console.WriteLine($"ok      {q.FullName}"); continue; }
                    faulty++;
                    Console.WriteLine($"FAULTY  {q.FullName}");
                    foreach (string p in check.Problems) Console.WriteLine($"        {p}");
                }
                Console.WriteLine($"{which.Count - faulty:N0} of {which.Count:N0} stored queries are in order.");
                return faulty == 0 ? 0 : 1;
            }
            case "save":
            {
                if (rest.Count == 0 || file == null) { Console.Error.WriteLine("usage: sfsquery --saved save <name> --file query.sql [--overwrite] --home <SFSearch home>"); return 2; }
                var done = stored.Save(rest[0], File.ReadAllText(file), overwrite);
                if (!done.Ok)
                {
                    Console.WriteLine($"Not saved. {done.Problems.Count:N0} problem(s):");
                    foreach (string p in done.Problems) Console.WriteLine($"  {p}");
                    return 1;
                }
                Console.WriteLine($"Saved as user.{rest[0]} in {done.File}.");
                if (done.Note != null) Console.WriteLine($"[notes] {done.Note}");
                return 0;
            }
            case "run":
            {
                if (rest.Count == 0) { Console.Error.WriteLine("usage: sfsquery --saved run <name> [parameter=value …] [--plugins a.esm | --winners | --versions]"); return 2; }
                var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string a in rest.Skip(1))
                {
                    int eq = a.IndexOf('=');
                    if (eq <= 0) { Console.Error.WriteLine($"Error: \"{a}\" isn't parameter=value."); return 2; }
                    arguments[a[..eq]] = a[(eq + 1)..];
                }
                var run = new StoredRunRequest
                {
                    Name = rest[0], Arguments = arguments,
                    Scope = winners ? ScopeKind.Winners : versions ? ScopeKind.Versions : plugins != null ? ScopeKind.Plugins : null,
                    Plugins = plugins?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>(),
                    AllowHeavy = allowHeavy, PageRows = rows, Cursor = cursor, Language = lang,
                };
                var a1 = stored.Run(run);
                Step("stored query");
                Console.Write(asJson ? a1.ToJsonText() + "\n" : a1.ToText(run.MaxChars));
                for (int i = 0; i < again; i++)
                {
                    var t = Stopwatch.StartNew();
                    var a2 = stored.Run(run);
                    Console.WriteLine($"[again {i + 1}] {a2.TotalRows:N0} rows; {t.Elapsed.TotalMilliseconds:F0} ms in all, {a2.Seconds * 1000:F0} ms in the engine");
                }
                if (time) Console.Error.WriteLine("[time] " + string.Join("; ", steps) + $"; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
                return a1.Error != null ? 1 : a1.NotRun != null ? 7 : 0;
            }
            default:
                Console.Error.WriteLine($"Error: --saved takes list, show, check, run or save; not \"{saved}\".");
                return 2;
        }
    }

    string? sql = file != null ? File.ReadAllText(file) : rest.FirstOrDefault();
    if (sql == null || (plugins == null && !winners && !versions))
    {
        Console.Error.WriteLine("usage: sfsquery \"<sql>\" --plugins a.esm,b.esm | --winners | --versions [--allow-heavy] [--json]   (see the top of Program.cs)");
        return 2;
    }
    var request = new QueryRequest
    {
        Sql = sql, Scope = winners ? ScopeKind.Winners : versions ? ScopeKind.Versions : ScopeKind.Plugins,
        Plugins = plugins?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>(),
        AllowHeavy = allowHeavy, PageRows = rows, Cursor = cursor, Language = lang,
    };
    var answer = engine.Run(request);
    Step("query");
    Console.Write(asJson ? answer.ToJsonText() + "\n" : answer.ToText(request.MaxChars));
    for (int i = 0; i < again; i++)
    {
        var a = engine.Run(request);
        Console.WriteLine($"[again {i + 1}] {a.TotalRows:N0} rows; {a.Cost.Text}{(time ? " (" + string.Join(", ", a.Cost.Steps) + ")" : "")}");
    }
    if (time) { using var me = Process.GetCurrentProcess(); Console.Error.WriteLine($"[time] processor time of the whole run: {me.UserProcessorTime.TotalSeconds:F2} s in the program, {me.PrivilegedProcessorTime.TotalSeconds:F2} s in Windows"); }
    if (time) Console.Error.WriteLine($"[time] garbage collection: {GC.GetTotalPauseDuration().TotalSeconds:F2} s of pauses in {GC.CollectionCount(0):N0} collections ({GC.CollectionCount(1):N0} of generation 1, {GC.CollectionCount(2):N0} of generation 2), {GC.GetTotalAllocatedBytes() / 1048576.0:N0} MB allocated");
    if (time) Console.Error.WriteLine("[time] query steps: " + string.Join(", ", answer.Cost.Steps));
    if (time) Console.Error.WriteLine("[time] " + string.Join("; ", steps) + $"; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
    return answer.Error != null ? 1 : answer.NotRun != null ? 7 : 0;
}
catch (Exception e) when (e is QueryException or RecordException or SFSearch.SchemaGen.SchemaException)
{
    Console.Error.WriteLine($"Error: {e.Message.TrimEnd('.')}.{(e is QueryException { Fix: { } fix } ? $" Fix: {fix.TrimEnd('.')}." : "")}");
    return 1;
}

static class Runner
{
    public static int ListViews(ViewBuilder builder, string? type)
    {
        if (type != null)
        {
            var v = builder.Find(type);
            if (v == null) { Console.Error.WriteLine($"Error: there is no view named {type}."); return 1; }
            var cat = builder.Catalog(v.Root);
            Console.WriteLine($"{v.Name}: {string.Join(", ", v.FixedColumns)} and {v.Columns.Count:N0} field columns{(v.IsList ? "" : ", Json")}");
            foreach (var c in v.Columns) Console.WriteLine($"  {c.Name}\t{c.Kind}\t{c.Subrecord}\t{c.Readable}\t{c.Expr}");
            if (!v.IsList)
            {
                Console.WriteLine($"list views ({cat.Lists.Count:N0}):");
                foreach (var l in cat.Lists.Values.OrderBy(l => l.Name, StringComparer.Ordinal))
                    Console.WriteLine($"  {l.Name} ({string.Join(", ", l.FixedColumns.Concat(l.Columns.Select(c => c.Name)).Take(14))}{(l.Columns.Count + l.FixedColumns.Count > 14 ? ", …" : "")})");
                if (cat.ColumnsCut > 0) Console.WriteLine($"{cat.ColumnsCut:N0} fields have no column (the view would pass SQLite's column limit): read them from Json");
            }
            return 0;
        }
        int typed = 0, lists = 0, columns = 0, widest = 0, cut = 0; string widestName = "";
        Console.WriteLine("| View | Record types | Columns | List views |");
        Console.WriteLine("|---|---|---|---|");
        foreach (string root in builder.RootNames.OrderBy(r => r, StringComparer.Ordinal))
        {
            var cat = builder.Catalog(root);
            int n = cat.Typed.Columns.Count + cat.Typed.FixedColumns.Count + 1;
            Console.WriteLine($"| {root} | {(cat.Typed.IsUnion ? string.Join(" ", cat.Typed.Types) : "")} | {n:N0} | {cat.Lists.Count:N0} |");
            typed++; lists += cat.Lists.Count; columns += n; cut += cat.ColumnsCut;
            if (n > widest) { widest = n; widestName = root; }
        }
        Console.WriteLine($"{typed:N0} typed views ({builder.UnionNames.Count:N0} of them union views), {lists:N0} list views, {columns:N0} typed columns; the widest is {widestName} with {widest:N0}; {cut:N0} fields cut for SQLite's column limit");
        return 0;
    }

    /// <summary>What the header index knows of each record type of a plugin, and what a read of it is estimated to cost. Nothing is read.</summary>
    public static int Stats(QueryEngine engine, HeaderIndex index, RecordSchema schema, string plugin)
    {
        var p = index.Plugin(plugin);
        if (p?.Segment == null) { Console.Error.WriteLine($"Error: {plugin} isn't in the Data folder."); return 1; }
        Console.WriteLine("Sig\tType\tRecords\tFileMB\tCompressed\tLargestKB\tSeconds\tMemoryMB\tScratchMB\tOverTheLimit");
        foreach (var s in p.Segment.Signatures.OrderByDescending(s => s.Bytes))
        {
            var load = CostModel.Load(p, s.Signature);
            var est = CostModel.Full(load);
            bool heavy = est.Seconds > engine.Options.HeavySeconds || est.MemoryMB > engine.Options.HeavyMemoryMB;
            Console.WriteLine($"{s.Signature}\t{(schema.TypesOf(s.Signature).Count == 0 ? "(no Mutagen type)" : engine.LabelOf(s.Signature))}\t{s.Count}\t{s.Bytes / 1048576.0:F2}\t{s.Compressed}\t{load.LargestRecord / 1024.0:F0}\t{est.Seconds:F2}\t{est.MemoryMB}\t{est.ScratchMB}\t{(heavy ? "yes" : "")}");
        }
        return 0;
    }

    /// <summary>Reads each record type of a plugin once, on fresh scratch tables, and prints what the header index
    /// knew beforehand beside what the read took: the numbers the cost estimates are made from.</summary>
    public static int Calibrate(QueryEngine engine, HeaderIndex index, RecordSchema schema, string plugin, string? only, bool world)
    {
        var p = index.Plugin(plugin);
        if (p?.Segment == null) { Console.Error.WriteLine($"Error: {plugin} isn't in the Data folder."); return 1; }
        var worldSigs = new HashSet<string> { "CELL", "WRLD", "REFR", "ACHR", "PGRE", "PHZD", "PARW", "PBAR", "PBEA", "PCON", "PFLA", "PMIS", "NAVM", "NAVI", "LAND", "SFTR" };
        var want = only?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine("Sig\tType\tRecords\tFileMB\tCompressed\tLargestKB\tJsonMB\tSeconds\tEstimate\tWarmMs\tNote");
        foreach (var s in p.Segment.Signatures.OrderBy(s => s.Signature, StringComparer.Ordinal))
        {
            var types = schema.TypesOf(s.Signature);
            if (types.Count == 0) { Console.WriteLine($"{s.Signature}\t(no Mutagen type)\t{s.Count}\t{s.Bytes / 1048576.0:F2}"); continue; }
            string label = engine.LabelOf(s.Signature);
            if (want != null ? !want.Contains(label) && !want.Contains(s.Signature) : worldSigs.Contains(s.Signature) != world) continue;
            var load = CostModel.Load(p, s.Signature);
            var est = CostModel.Full(load);
            var req = new QueryRequest { Sql = $"SELECT count(*), sum(length(Json)) FROM {label}", Plugins = new[] { plugin }, AllowHeavy = true };
            var a = engine.Run(req);
            if (!a.Ok) { Console.WriteLine($"{s.Signature}\t{label}\t{s.Count}\t{s.Bytes / 1048576.0:F2}\t\t\t\t\t\t\t{a.Error ?? a.NotRun?.What}"); continue; }
            double jsonMb = Convert.ToDouble(a.Rows[0][1] ?? 0L) / 1048576.0;
            var warm = engine.Run(new QueryRequest { Sql = $"SELECT count(*) FROM {label} WHERE EditorID LIKE 'a%'", Plugins = new[] { plugin } });
            Console.WriteLine($"{s.Signature}\t{label}\t{s.Count}\t{s.Bytes / 1048576.0:F2}\t{s.Compressed}\t{load.LargestRecord / 1024.0:F0}\t{jsonMb:F1}\t{a.Cost.Seconds:F3}\t{est.Seconds:F2} s / {est.MemoryMB} MB\t{warm.Cost.Seconds * 1000:F1}\t{string.Join(" | ", a.Notes)}");
        }
        Console.WriteLine($"peak working set {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
        return 0;
    }
}

