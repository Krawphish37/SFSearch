// sfsreport: a small command line over SFSearch.Reports, for running the checks and reports against the real Data
// folder and for the numbers in design\agent-first\notes\M11a.md and M11b.md. It is not wired into SFSearch's commands.
//
//   sfsreport check <plugin> [--world]
//       is this plugin built right: one PASS, WARN or ERROR line per check, then the records
//   sfsreport leveled <list> [--in <plugin>] [--depth N]
//       what a leveled list can give: every entry with level, count, chance of nothing and conditions, nested lists under it
//   sfsreport dialogue <quest|topic> [--in <plugin>]
//       what a quest's (or one topic's) characters say: topics and lines with speaker, text, conditions, what a line starts
//   sfsreport sources <plugin> [--sig WEAP,ARMO] [--item <record>] [--with p1,p2] [--only] [--depth N]
//       how the player gets each item a plugin adds
//   sfsreport links <plugin> <record>
//       every link of one record: where it sits and what the schema allows there (for looking into a check's finding)
//   sfsreport archive <plugin|file.ba2|all> [--list [pattern]] [--find <text|pattern>] [--show <path>] [--extract <path|pattern> --to <folder>] [--beside]
//       what is inside BA2 archives: a summary per archive; the files that fit a pattern; one file's text; files copied
//       out to a folder (never the game's folders; a file whose path leads outside the folder is refused)
//   sfsreport assets <plugin> [--world] [--beside]
//       every file path on a plugin's records, held against the loose files and the archives in Data: what is missing
//   sfsreport materials <plugin> [--beside]
//       a plugin's meshes, the materials they use, and those materials' textures: what is missing
//   sfsreport scripts <plugin[,plugin]> [--find <text>] [--show <script>] [--calls] [--no-records] [--world | --no-world] [--beside]
//       a plugin's Papyrus scripts as source (decompiled once with Champollion), and which records carry which script
//       (attached by which records, or to nothing; missing ones), and the code that reaches records without a link
//       (GetFormFromFile, GetForm, and AddItem … on what they return); --calls lists only that, with no decompiler
//   sfsreport changes [<plugin>] [--since 2026-11-01] [--all]
//       what changed in Data since last time: plugins added, removed, updated; patches at risk. With a plugin: its history
//   sfsreport pathfields [<plugin>]
//       the fields the schema types as file paths; with a plugin, every text of its records that ends in a file extension
//
// <plugin> is a file name in Data or the path of a plugin file. <list>, <quest>, <record> are a FormKey
// (0003C2:darkstar.esm), or an EditorID. A pattern with * or ? must fit a file's whole path; any other text is
// looked for anywhere in it.
// Options: --detail (every row)   --json (the structured answer)   --max-chars <n> (rows per page, default 24000)
//          --cursor <n> (continue a cut answer)   --lang <language>   --out <file> (write the answer there)
//          --data <Data folder> (or SFSEARCH_DATA)   --scratch <folder> (or SFSREPORT_SCRATCH; default %TEMP%\sfsreport-scratch)
//          --kept <folder> (or SFSREPORT_KEPT; default %LOCALAPPDATA%\SFSearch\kept): the small kept files, which hold
//            no record contents: the decompiled scripts (scripts\) and the history log (history\)
//          --champollion <Champollion.exe> (or SFSEARCH_CHAMPOLLION, or "champollion" in sfsearch.json or
//            tools\champollion\ in the SFSearch home: --home <folder> or SFSEARCH_HOME; the home is only read)
//          --time (what each step took and the peak memory, on stderr)
// Read only: nothing is ever written to the Data folder or the game's folder. The scratch folder holds the header
// index's segments. scripts and changes write to the kept folder; archive --extract writes to the folder it is given.
// Exit code: 0; 1 for an error, for a check that found errors, and for assets or materials that found something
// missing; 2 for a wrong call.
using System.Diagnostics;
using System.Text.Json;
using Mutagen.Bethesda.Strings;
using SFSearch.Records;
using SFSearch.Reports;

var rest = args.ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
bool Flag(string name) => rest.Remove(name);

string data = Opt("--data") ?? Environment.GetEnvironmentVariable("SFSEARCH_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";
string scratch = Opt("--scratch") ?? Environment.GetEnvironmentVariable("SFSREPORT_SCRATCH") ?? Path.Combine(Path.GetTempPath(), "sfsreport-scratch");
string langText = Opt("--lang") ?? "English";
string? outFile = Opt("--out"), inPlugin = Opt("--in"), sigs = Opt("--sig"), item = Opt("--item"), with = Opt("--with");
int maxChars = int.Parse(Opt("--max-chars") ?? "24000"), cursor = int.Parse((Opt("--cursor") ?? "0").Trim('"'));
int depth = int.Parse(Opt("--depth") ?? "0");
bool detail = Flag("--detail"), asJson = Flag("--json"), time = Flag("--time"), world = Flag("--world"), only = Flag("--only");
string? kept = Opt("--kept") ?? Environment.GetEnvironmentVariable("SFSREPORT_KEPT");
string? find = Opt("--find"), show = Opt("--show"), extract = Opt("--extract"), to = Opt("--to"), since = Opt("--since");
bool beside = Flag("--beside"), calls = Flag("--calls"), noRecords = Flag("--no-records"), noWorld = Flag("--no-world"), all = Flag("--all"), list = false;
string? champollion = Opt("--champollion"), home = Opt("--home") ?? Environment.GetEnvironmentVariable("SFSEARCH_HOME");
string? listPattern = null;
{
    int i = rest.IndexOf("--list");
    if (i >= 0)
    {
        list = true;
        if (i + 1 < rest.Count && !rest[i + 1].StartsWith("--", StringComparison.Ordinal)) { listPattern = rest[i + 1]; rest.RemoveAt(i + 1); }
        rest.RemoveAt(i);
    }
}
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (rest.Count < 2 && !(rest.Count == 1 && rest[0].ToLowerInvariant() is "changes" or "pathfields"))
{
    Console.Error.WriteLine("usage: sfsreport check|leveled|dialogue|sources|links|archive|assets|materials|scripts|changes <plugin or record> …   (see the top of Program.cs)");
    return 2;
}
if (!Enum.TryParse<Language>(langText, true, out var language))
{
    Console.Error.WriteLine($"Error: \"{langText}\" isn't a language Mutagen knows. Fix: use one of {string.Join(", ", Enum.GetNames<Language>())}.");
    return 1;
}
string kind = rest[0].ToLowerInvariant();
var clock = Stopwatch.StartNew();
var steps = new List<string>();
void Step(string what) { steps.Add($"{what} {clock.Elapsed.TotalMilliseconds:F0} ms"); clock.Restart(); }
int exit = 0;

try
{
    var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
    Step("schema");
    using var session = new ReportSession(data, scratch, schema, language: language);
    session.KeptFolder = kept ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SFSearch", "kept");
    session.Champollion = ScriptsReport.FindChampollion(champollion, home, AppContext.BaseDirectory);
    Step("header index");
    ReportAnswer? answer = null;
    switch (kind)
    {
        case "check":
        {
            var check = new CheckReport(session).Check(rest[1], new CheckOptions
            {
                World = world,
                // With --time: a line now and then, so a long check shows it is alive
                Progress = time ? (done, all) => Console.Error.WriteLine($"[progress] {done:N0} records read of {all:N0}, {clock.Elapsed.TotalSeconds:F0} s") : null,
            });
            if (check.Failed) exit = 1;
            answer = check;
            break;
        }
        case "leveled":
            answer = new LeveledReport(session).Leveled(rest[1], new LeveledOptions { In = inPlugin, Depth = depth > 0 ? depth : 8 });
            break;
        case "dialogue":
            answer = new DialogueReport(session).Dialogue(rest[1], new DialogueOptions { In = inPlugin });
            break;
        case "sources":
            answer = new SourcesReport(session).Sources(rest[1], new SourcesOptions
            {
                Signatures = sigs?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Item = item, With = with?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>(),
                Dependents = !only, Depth = depth > 0 ? depth : 6,
            });
            break;
        case "archive":
        {
            var report = new ArchiveReport(session);
            if (show != null) answer = report.Show(rest[1], show, beside);
            else if (extract != null) answer = report.Extract(rest[1], extract, to ?? "", beside);
            else if (find != null) answer = report.List(rest[1], find, beside);
            else if (list) answer = report.List(rest[1], listPattern, beside);
            else answer = report.Summary(rest[1], beside);
            break;
        }
        case "assets":
        {
            var assets = new AssetsReport(session).Assets(rest[1], new AssetsOptions { World = world, Beside = beside });
            if (assets.Failed) exit = 1;
            answer = assets;
            break;
        }
        case "materials":
        {
            var materials = new MaterialsReport(session).Materials(rest[1], new MaterialsOptions { Beside = beside });
            if (materials.Failed) exit = 1;
            answer = materials;
            break;
        }
        case "scripts":
            answer = new ScriptsReport(session).Scripts(rest[1], new ScriptsOptions { Find = find, Show = show, Calls = calls, Carriers = !noRecords, World = world ? true : noWorld ? false : null, Beside = beside });
            break;
        case "changes":
        {
            DateTime? from = null;
            if (since != null)
            {
                if (!DateTime.TryParse(since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var d)) { Console.Error.WriteLine($"Error: \"{since}\" isn't a date. Fix: write it as 2026-11-01."); return 2; }
                from = d;
            }
            answer = new ChangesReport(session).Changes(new ChangesOptions { Plugin = rest.Count > 1 ? rest[1] : null, Since = from, All = all });
            break;
        }
        case "pathfields":
        {
            // pathfields: the fields the schema types as file paths. pathfields <plugin>: every text of the plugin's
            // records that ends in a file extension, counted by field and extension (for looking into what assets reads).
            var walker = new PathWalker(schema, new[] { ".nif", ".mesh", ".mat", ".dds", ".rig", ".af", ".agx", ".swf", ".txt", ".bin", ".wem", ".bk2", ".json", ".psc", ".pex", ".tri", ".morph", ".biom", ".cdb", ".hkx", ".btd", ".bto", ".btr", ".tga", ".png", ".ffxml", ".bnk", ".csv", ".mp4" });
            Console.WriteLine($"The schema types {walker.TypedFields.Count} fields as file paths:");
            foreach (var d in walker.TypedFields) Console.WriteLine($"  {d.Object}.{d.Path}  [{d.Subrecord}]");
            var places = walker.TypedPlaces();
            Console.WriteLine($"They can be reached at {places.Values.Sum():N0} places in {places.Count} of {schema.Model.RecordTypes.Count} record types.");
            if (rest.Count < 2) break;
            var side = session.Side(rest[1]);
            side.Engine.Sessions.Use(side.Name);
            var seg = side.Segment;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sample = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < seg.FirstUnresolved; i++)
            {
                if (!world && ReportSession.IsWorld(seg, seg[i])) continue;
                SFSearch.Compare.RecordView view;
                try { view = session.Compare.View(side.Engine, new SFSearch.Index.RecordRef(side.Plugin.Id, i)); } catch (SFSearch.Compare.CompareException) { continue; }
                walker.Walk(view.Type, view.Json, p =>
                {
                    string key = $"{view.Info.Signature}\t{PathWalker.Plain(p.Path)}\t{(p.Typed ? "typed" : "by value")}\t{PathWalker.ExtensionOf(p.Value)}";
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                    sample.TryAdd(key, p.Value);
                });
            }
            foreach (var (k, c) in counts.OrderBy(k => k.Key, StringComparer.Ordinal)) Console.WriteLine($"{c}\t{k}\t{sample[k]}");
            break;
        }
        case "links":
        {
            if (rest.Count < 3) { Console.Error.WriteLine("usage: sfsreport links <plugin> <record>"); return 2; }
            var side = session.Side(rest[1]);
            side.Engine.Sessions.Use(side.Name);
            var rec = side.Engine.Load(side.Engine.Find(side.Name, rest[2]));
            var json = side.Engine.Json(rec);
            Console.WriteLine($"{rec.Label} {rec.Info.Signature} {rec.Type}");
            var label = session.Compare.Labeller(side);
            new LinkWalker(schema).Walk(rec.Type, json, l => Console.WriteLine($"  {l.Path} → {label(l.FormKey)}   [{(l.LinkTo == null ? "the schema doesn't know this field" : session.Links.Describe(l.LinkTo))}]"));
            break;
        }
        case "survey":
        {
            // survey leveled <plugin>: every leveled list of a plugin with what its unfolding holds, to pick lists to look at.
            // survey dialogue <plugin|*>: the quests with the most topics, per plugin.
            if (rest.Count < 3) { Console.Error.WriteLine("usage: sfsreport survey leveled|dialogue <plugin>"); return 2; }
            var plugins = rest[2] == "*" ? session.Index.Plugins.Where(p => p.Order.Enabled && p.Segment != null).ToList() : new[] { session.Side(rest[2]).Plugin }.ToList();
            if (rest[1] == "leveled")
            {
                var report = new LeveledReport(session);
                Console.WriteLine("sig\tlist\tread from\tlists\tentries\tnot found\tloops\tentry conditions\tlist conditions\tfilter keywords\tchance none\tholders");
                foreach (var p in plugins)
                    foreach (string sig in report.LeveledSignatures)
                        foreach (var r in session.Index.RecordsOfType(p.Name, sig))
                        {
                            var info = session.Index.Describe(r);
                            if (info.IsOverride) continue;
                            var a = report.Leveled(info.FormKey, new LeveledOptions { Depth = 8 });
                            Console.WriteLine($"{sig}\t{info.Label}\t{a.Top.Plugin}\t{a.ListCount}\t{a.EntryCount}\t{a.NotFound}\t{a.Loops}\t{a.Entries().Count(x => x.Entry.Conditions.Count > 0)}\t{a.Lists().Count(l => l.Conditions.Count > 0)}\t{a.Lists().Count(l => l.FilterKeywords.Count > 0)}\t{a.Top.ChanceNone}\t{session.Index.Versions(info.FormKey)?.Holders.Count}");
                        }
            }
            else
            {
                Console.WriteLine("plugin\tquest\ttopics\tlines");
                foreach (var p in plugins)
                {
                    var seg = p.Segment!;
                    var topics = new Dictionary<string, int>(); var lines = new Dictionary<string, int>(); var questOfTopic = new Dictionary<string, string>();
                    foreach (int i in seg.EntriesOf("DIAL"))
                        if (seg.TryGetParent(seg[i], out int po, out uint pl))
                        {
                            string q = $"{pl:X6}:{seg.Names[po]}";
                            topics[q] = topics.GetValueOrDefault(q) + 1;
                            questOfTopic[$"{seg[i].LocalId:X6}:{seg.Names[seg[i].Owner]}"] = q;
                        }
                    foreach (int i in seg.EntriesOf("INFO"))
                        if (seg.TryGetParent(seg[i], out int po, out uint pl) && questOfTopic.TryGetValue($"{pl:X6}:{seg.Names[po]}", out var q))
                            lines[q] = lines.GetValueOrDefault(q) + 1;
                    foreach (var (q, n) in topics.OrderByDescending(t => lines.GetValueOrDefault(t.Key)).Take(rest[2] == "*" ? 3 : 60))
                        Console.WriteLine($"{p.Name}\t{session.Engine.Label(q)}\t{n}\t{lines.GetValueOrDefault(q)}");
                }
            }
            break;
        }
        default:
            Console.Error.WriteLine($"Error: there is no report named \"{rest[0]}\". Fix: use check, leveled, dialogue, sources, links, archive, assets, materials, scripts or changes.");
            return 2;
    }
    Step(kind);
    if (answer != null)
    {
        string text;
        if (asJson) text = answer.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        else text = answer.ToText(new ReportTextOptions { Detail = detail, MaxChars = maxChars, Cursor = cursor });
        if (outFile != null) File.WriteAllText(outFile, text); else Console.Write(text.EndsWith('\n') ? text : text + "\n");
        Step("text");
    }
}
catch (ReportException e) { Console.Error.WriteLine(e.Line); exit = 1; }
catch (RecordException e) { Console.Error.WriteLine("Error: " + e.Message); exit = 1; }
catch (IOException e) { Console.Error.WriteLine("Error: " + e.Message.TrimEnd('.') + ". Fix: check that the file is there and can be read or written, then ask again."); exit = 1; }
catch (UnauthorizedAccessException e) { Console.Error.WriteLine("Error: " + e.Message.TrimEnd('.') + ". Fix: check that the file is there and can be read or written, then ask again."); exit = 1; }
if (time) Console.Error.WriteLine($"[time] {string.Join("; ", steps)}; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB");
return exit;
