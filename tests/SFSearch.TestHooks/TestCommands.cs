using System.Diagnostics;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Engine;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Service;

namespace SFSearch.TestHooks
{
    /// <summary>
    /// `__test`: the small commands the service's own tests drive it with (tests\SFSearch.ServiceTest, the harness's MCP
    /// part). They read no plugin, except `linkscheck`. A test add-on (J37, 2026-11-15): built only into a test home's
    /// extensions folder, never into SFSearch itself. Each uses only what any add-on may use: the add-on API and
    /// <see cref="JobContext"/> (the heavy lane, a table's header, where an answer came from, an error with its fix, the
    /// scratch folder, the indexer's give-way).
    /// </summary>
    public sealed class TestCommands : ISfsExtension
    {
        public IReadOnlyList<(string Name, string Help)> Commands { get; } =
        [
            ("__test", "  __test echo|sleep|rows|lines|heavy|alloc|keep|yieldable|scratch|source|fail|crash ...   the service tests' commands (no plugin read)\n" +
                       "  __test linkscheck <plugin> [--sample N] [--world]   the link walk held against the record's JSON and Mutagen's own list of links"),
        ];

        public void Run(string command, List<string> args)
        {
            string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "";
            int N(int i, int fallback) => args.Count > i && int.TryParse(args[i], out int v) ? v : fallback;
            switch (sub)
            {
                case "echo":
                    Console.WriteLine(string.Join(' ', args.GetRange(1, args.Count - 1)));
                    break;
                case "sleep":
                    {
                        // sleep <ms>: stays busy for that long
                        int ms = N(1, 1000);
                        var clock = Stopwatch.StartNew();
                        while (clock.ElapsedMilliseconds < ms) Thread.Sleep(Math.Min(50, Math.Max(1, ms - (int)clock.ElapsedMilliseconds)));
                        Console.WriteLine($"slept {ms} ms from {Stamp(DateTime.UtcNow.AddMilliseconds(-clock.ElapsedMilliseconds))} to {Stamp(DateTime.UtcNow)}");
                        break;
                    }
                case "rows":
                    {
                        // rows <n> [width] [header 0|1]: a table of n rows, to page through
                        int n = N(1, 100), width = N(2, 40);
                        bool declare = N(3, 0) == 1;
                        Console.WriteLine("| Row | Text |");
                        Console.WriteLine("|---|---|");
                        if (declare) JobContext.TableHeader(2);
                        for (int i = 1; i <= n; i++) Console.WriteLine($"| {i} | {new string((char)('a' + i % 26), width)} |");
                        break;
                    }
                case "lines":
                    {
                        // lines <n>: plain lines, no table, with a note on stderr
                        int n = N(1, 100);
                        for (int i = 1; i <= n; i++) Console.WriteLine($"line {i} of {n}");
                        Console.Error.WriteLine("[test] a note from the command");
                        break;
                    }
                case "heavy":
                    {
                        // heavy <estimateMB> <holdMs> [allocMB]: asks for the heavy lane, then holds it (and, when asked, real memory)
                        int est = N(1, 4096), hold = N(2, 1000), alloc = N(3, 0);
                        if (!JobContext.RequireHeavy(est, $"The test's heavy part ({est} MB)")) return;
                        var from = DateTime.UtcNow;
                        var blocks = Allocate(alloc);
                        Thread.Sleep(hold);
                        Console.WriteLine($"heavy ran: estimate {est} MB, held {blocks.Count * 64} MB from {Stamp(from)} to {Stamp(DateTime.UtcNow)}");
                        GC.KeepAlive(blocks);
                        break;
                    }
                case "alloc":
                    {
                        // alloc <MB> <holdMs>: takes real memory without asking the heavy lane (for the worker limits)
                        var blocks = Allocate(N(1, 256));
                        Thread.Sleep(N(2, 1000));
                        Console.WriteLine($"held {blocks.Count * 64} MB");
                        GC.KeepAlive(blocks);
                        break;
                    }
                case "keep":
                    {
                        // keep <MB>: takes memory and keeps it after the command, like a worker with plugins open
                        _kept.AddRange(Allocate(N(1, 256)));
                        Console.WriteLine($"keeping {_kept.Count * 64} MB");
                        break;
                    }
                case "yieldable":
                    {
                        // yieldable <ms>: a job that stops early when the coordinator asks it to give way (in the indexer lane)
                        int ms = N(1, 5000);
                        var clock = Stopwatch.StartNew();
                        while (clock.ElapsedMilliseconds < ms && !JobContext.YieldRequested) Thread.Sleep(20);
                        if (JobContext.YieldRequested && clock.ElapsedMilliseconds < ms) { JobContext.Yielded = true; Console.WriteLine($"gave way after {clock.ElapsedMilliseconds} ms"); }
                        else Console.WriteLine($"indexed for {ms} ms, finished at {Stamp(DateTime.UtcNow)}");
                        break;
                    }
                case "scratch":
                    {
                        // scratch [file]: the scratch folder's path; with a name, also leaves a file there
                        string? dir = JobContext.ScratchDir;
                        Console.WriteLine(dir ?? "(no scratch folder outside the service)");
                        if (dir != null && args.Count > 1) File.WriteAllText(Path.Combine(dir, Path.GetFileName(args[1])), "left by a test\n");
                        break;
                    }
                case "source":
                    // source: a command that says where its answer came from and what it read
                    JobContext.AddSource("example.esm", 26474052, new DateTime(2026, 9, 2, 3, 51, 0, DateTimeKind.Utc), "5e1c0000");
                    JobContext.AddRead("Weapon (456) from example.esm");
                    Console.WriteLine("an answer");
                    break;
                case "fail":
                    JobContext.Fail("the test asked for a failure", "ask for something else");
                    break;
                case "crash":
                    Environment.Exit(99);
                    break;
                case "linkscheck":
                    LinksCheck.Run(args.Skip(1).ToList());
                    break;
                default:
                    Console.WriteLine("Usage: __test echo|sleep|rows|lines|heavy|alloc|keep|yieldable|scratch|source|fail|crash ... | linkscheck <plugin> [--sample N] [--world]");
                    break;
            }
        }

        static readonly List<byte[]> _kept = new();

        static string Stamp(DateTime utc) => utc.ToString("HH:mm:ss.fff");

        /// <summary>Takes memory in 64 MB blocks and writes to every page, so Windows really hands it over.</summary>
        static List<byte[]> Allocate(int mb)
        {
            var blocks = new List<byte[]>();
            for (int got = 0; got < mb; got += 64)
            {
                var b = new byte[64 * 1048576];
                for (int i = 0; i < b.Length; i += 4096) b[i] = 1;
                blocks.Add(b);
            }
            return blocks;
        }
    }

    /// <summary>
    /// `__test linkscheck &lt;plugin&gt; [--sample N] [--world]` (was `__links check` in the program until J37): holds the
    /// link walk (SFSearch\Engine\LinkWalker.cs, what the link index is made of) against two other readings of the same
    /// records: the links in the record's JSON (the walk and the JSON must give the same links at the same paths), and
    /// Mutagen's own list of a record's links (EnumerateFormLinks; it has no paths, so the targets are compared). Exit
    /// code 1 when the JSON differs.
    /// </summary>
    static class LinksCheck
    {
        public static void Run(List<string> args)
        {
            int sample = int.TryParse(CommandLine.TakeOption(args, "--sample"), out int s) ? s : 0;
            bool withWorld = CommandLine.TakeFlag(args, "--world");
            if (args.Count == 0) { Console.WriteLine("Usage: __test linkscheck <plugin> [--sample N] [--world]"); return; }
            var eng = EngineIndex.Get();
            eng.Check();
            var ix = eng.Index;
            var p = ix.Plugin(args[0]) ?? ix.Plugin(args[0] + ".esm");
            if (p?.Segment == null) { Console.WriteLine($"Error: {args[0]} isn't a plugin in Data that can be read. Fix: check the name."); Environment.ExitCode = 1; return; }
            var seg = p.Segment;
            var world = WorldTypes.Of(seg);
            var entries = new List<int>();
            for (int i = 0; i < seg.FirstUnresolved; i++) if (withWorld || !world[seg[i].Signature]) entries.Add(i);
            int all = entries.Count;
            if (sample > 0 && entries.Count > sample)
            {
                // Evenly spread, and the same records on every run: so many of each type
                var byType = entries.GroupBy(e => seg[e].Signature).ToList();
                int each = Math.Max(1, sample / byType.Count);
                entries = byType.SelectMany(g => { var l = g.ToList(); return l.Count <= each ? l : Enumerable.Range(0, each).Select(k => l[(int)((long)k * l.Count / each)]); }).OrderBy(e => e).ToList();
            }
            var engine = EngineRecords.For(eng, ix);
            var use = engine.Sessions.Use(p.Name);
            var sink = RecordLinks.Collector(indexed: true);
            long records = 0, links = 0, mutagenLinks = 0, unread = 0, jsonDiffer = 0, mutagenDiffer = 0, onlyWalk = 0, onlyMutagen = 0;
            var examples = new List<string>();
            var clock = Stopwatch.StartNew();
            foreach (int entry in entries)
            {
                var r = new RecordRef(p.Id, entry);
                LoadedRecord rec;
                try { rec = engine.Load(r); }
                catch (RecordException e) { unread++; if (examples.Count < 12) examples.Add($"not read: {e.Message}"); continue; }
                records++;
                RecordLinks.Walk(rec.Record, sink);
                var walk = sink.Links.Select(l => (Path: sink.Paths[l.Path], Target: $"{l.FormKey.ID:X6}:{l.FormKey.ModKey.FileName.String}")).ToList();
                links += walk.Count;

                // The same record as JSON: every text that is a FormKey, but the record's own and the markers of records inside it
                var json = new List<(string Path, string Target)>();
                JsonLinks(engine.Json(rec), "", json, top: true);
                var a = walk.Select(x => x.Path + " = " + x.Target.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var b = json.Select(x => x.Path + " = " + x.Target.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (!a.SequenceEqual(b))
                {
                    jsonDiffer++;
                    if (examples.Count < 12) examples.Add($"{rec.Info.Signature} {ProblemNotes.Label(rec.Info)}: the walk and the JSON differ. Only the walk: {string.Join("; ", a.Except(b).Take(3))}. Only the JSON: {string.Join("; ", b.Except(a).Take(3))}");
                }

                // Mutagen's own list of the record's links (no paths; a record that holds others lists theirs too)
                var mine = walk.Select(x => x.Target.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var theirs = rec.Record.EnumerateFormLinks().Where(l => !l.IsNull).Select(l => $"{l.FormKey.ID:X6}:{l.FormKey.ModKey.FileName.String}".ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
                mutagenLinks += theirs.Count;
                if (!mine.SequenceEqual(theirs))
                {
                    mutagenDiffer++;
                    var mineOnly = Minus(mine, theirs); var theirsOnly = Minus(theirs, mine);
                    onlyWalk += mineOnly.Count; onlyMutagen += theirsOnly.Count;
                    if (examples.Count < 12) examples.Add($"{rec.Info.Signature} {ProblemNotes.Label(rec.Info)}: the walk has {mine.Count} links, EnumerateFormLinks {theirs.Count}. Only the walk: {string.Join("; ", mineOnly.Take(3).Select(t => t + " at " + string.Join(", ", walk.Where(w => w.Target.Equals(t, StringComparison.OrdinalIgnoreCase)).Select(w => w.Path).Distinct().Take(2))))}. Only Mutagen: {string.Join("; ", theirsOnly.Take(3))}");
                }
            }
            engine.Sessions.Confirm(use);
            Console.WriteLine($"{p.Name}: {records:N0} of {all:N0} records{(withWorld ? "" : " outside the world groups")} walked in {clock.Elapsed.TotalSeconds:0.0} s ({unread:N0} couldn't be read): {links:N0} links. "
                + $"Against the record's JSON: {jsonDiffer:N0} record(s) differ. Against Mutagen's EnumerateFormLinks ({mutagenLinks:N0} links): {mutagenDiffer:N0} record(s) differ ({onlyWalk:N0} links only the walk has, {onlyMutagen:N0} only Mutagen has).");
            foreach (var e in examples) Console.WriteLine("  " + e);
            if (jsonDiffer > 0) Environment.ExitCode = 1;
        }

        static List<string> Minus(List<string> a, List<string> b)
        {
            var left = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var x in b) left[x] = left.GetValueOrDefault(x) + 1;
            var only = new List<string>();
            foreach (var x in a) { if (left.TryGetValue(x, out int n) && n > 0) left[x] = n - 1; else only.Add(x); }
            return only;
        }

        static void JsonLinks(JsonNode? node, string path, List<(string, string)> into, bool top = false)
        {
            switch (node)
            {
                case JsonObject o:
                    if (RecordSplit.ChildOf(o) != null) return;
                    foreach (var (k, v) in o)
                    {
                        if (top && k == "FormKey") continue;
                        JsonLinks(v, path.Length == 0 ? k : path + "." + k, into);
                    }
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) JsonLinks(a[i], $"{path}[{i}]", into);
                    break;
                case JsonValue v when v.TryGetValue<string>(out var s) && RecordSplit.IsFormKey(s):
                    into.Add((path, s));
                    break;
            }
        }
    }
}
