using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SFSearch.Index;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// indexes (through sfs_run; J31, 2026-11-15): the indexes SFSearch keeps on disk, in indexes\ under its home. `show`
    /// (the default) tells each one's size, how many plugins it covers, when it was built, what making it again from
    /// nothing would cost, what it speeds up, and how to drop it; `build` makes now what is missing; `drop` deletes an
    /// index, or the parts of some plugins. Everything kept is indexes made from the plugin files, never record data
    /// (DEVELOPING.md, "Indexes are kept, and the user decides which"). The two the service builds at its start are kept
    /// between starts unless keepStartupIndexes is false in sfsearch.json. The opt-in world link index (J32) is added to
    /// <see cref="Kinds"/> and shown, built and dropped the same way.
    /// </summary>
    public static class IndexesTool
    {
        public const string Help = """
            indexes [show]
            indexes build [header|links|world|all] [--in <plugins>] [--allow-heavy]
            indexes drop header|links|world|all [--in <plugins>]
              <plugins>: names parted by commas, a name with * (kept as a pattern: a plugin added later that fits it
              counts too), or a set: @official, @loaded, @all (its plugins now).
              The indexes SFSearch keeps on disk (indexes\ in its home): each one's size, how many plugins it covers, when
              it was built, what it speeds up, and how to delete it. They are made from the plugin files, never hold record
              data, and each plugin's part is named by its content: a changed plugin's part is never read.
              header  every record's FormID, EditorID and type, and who holds it: every command uses it
              links   what points at what outside world data, and in-game names: refs and find use it
              world   what points at a record from world data (cells, placed objects, navmeshes, terrain). Made only when
                      you ask for it (here, or by answering save_index where a slow question offers it); refs then answers links from world data in a fraction of a second, where without it
                      it searches the world data's bytes (Starfield.esm: 1.2 GB, several seconds a question)
              show    (the default) what is kept, its size and age, and what isn't covered yet
              build   makes now what is missing (the service does this by itself at its start) of the header index, the
                      link index, and the world link index of the plugins you asked for. --in with links: the link files
                      of those plugins only. build world [--in <plugins>]: asks for the world link index of every plugin
                      with world data (or of those named) and makes it; SFSearch then keeps it, and makes a plugin's part
                      again when the plugin changes, until you drop it. Over 10 s or 2 GB it answers "Not run" with the
                      cost, unless --allow-heavy
              drop    deletes an index (all: every one), or with --in the parts of those plugins only. The service makes the
                      header and link indexes again (the header index at the next command, the link index at its next
                      start or idle check). drop world also forgets that you asked for it (with --in: for those plugins)
              keepStartupIndexes in sfsearch.json (default true) keeps them between service starts; false empties them at
              every start, as SFSearch 2.0 did. keepRemovedPluginIndexMB (default 0) is for the parts of plugins no longer
              in Data (taken out, or the copy a changed plugin replaced): 0 deletes them at the next start, a number keeps
              them up to that many MB (the plugin out of Data longest deleted first), so a plugin put back isn't
              indexed again. show lists them, each plugin with when it was last in Data.
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...); 7 not run (a build over 10 s or 2 GB).
            """;

        /// <summary>One kind of kept index: its name, its folder, and what it speeds up.</summary>
        public sealed record Kind(string Name, string Folder, string SpeedsUp);

        /// <summary>The kept indexes. world (J32) is made only when asked: indexes build world, or (J33) an agent's
        /// "save an index" answer, which calls <see cref="WorldLinks.Want"/> and has the indexer lane run `__links world`.</summary>
        public static IReadOnlyList<Kind> Kinds => new[]
        {
            new Kind("header", EngineIndex.IndexFolder(), "every command: what a FormID or EditorID is, who holds a record and who wins, the cost of a read"),
            new Kind("links", EngineLinks.Folder, "sfs_refs (what points at a record, outside world data) and in-game names in sfs_find"),
            new Kind("world", WorldLinks.Folder, "sfs_refs into world data (cells, placed objects, navmeshes, terrain) in a fraction of a second, and sfs_query's Links WHERE ToKey = '…' rows from world records"),
        };

        /// <summary>What making the link index costs: records walked on about 8 threads, about 4 µs each (M5b: 3,465,077 records, 13.9 to 14.5 s).</summary>
        const double LinkSecondsPerRecord = 4.0e-6;
        /// <summary>What making the header index costs: one pass over each plugin's record headers and a hash of it (STEP1 4.2: 2.8 s for 4.25 GB).</summary>
        const double HeaderSecondsPerGB = 0.66;

        static readonly string[] Actions = { "show", "build", "drop" };

        public static void Verb(List<string> args)
        {
            if (args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            var answer = new EngineAnswer();
            Run(args, answer);
            answer.Emit();
        }

        static void Run(List<string> args, EngineAnswer answer)
        {
            var words = new List<string>();
            string? only = null;
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                if (a.Equals("--in", StringComparison.OrdinalIgnoreCase) || a.Equals("--plugins", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Count) { answer.Fail("--in needs plugin names", "give them after it: --in atom74.esm,weap_*.esm", 2); return; }
                    only = args[++i];
                    continue;
                }
                if (a.StartsWith("--", StringComparison.Ordinal)) { answer.Fail($"indexes has no option {a}", "its options are --in <plugins> and --allow-heavy (indexes --help)", 2); return; }
                words.Add(a);
            }
            string action = words.Count > 0 ? words[0].ToLowerInvariant() : "show";
            if (!Actions.Contains(action))
            {
                answer.Fail($"indexes has no action '{words[0]}'", "use indexes (show), indexes build [header|links|world|all], or indexes drop header|links|world|all", 2);
                return;
            }
            string? kindWord = words.Count > 1 ? words[1].ToLowerInvariant() : null;
            if (words.Count > 2) { answer.Fail($"indexes {action} got more words than it takes ({string.Join(" ", words.Skip(2))})", "name plugins with --in: indexes " + action + " links --in atom74.esm", 2); return; }
            var kinds = Kinds;
            if (action == "show")
            {
                if (kindWord != null || only != null) { answer.Fail("indexes show takes no index name or --in", "run it bare: indexes", 2); return; }
            }
            else
            {
                if (kindWord == null && action == "drop") { answer.Fail("indexes drop needs the index to delete", $"name it: indexes drop {string.Join("|", kinds.Select(k => k.Name))}|all (indexes shows what each holds)", 2); return; }
                kindWord ??= "all";
                if (kindWord != "all" && !kinds.Any(k => k.Name == kindWord)) { answer.Fail($"there is no index named '{kindWord}'", $"the indexes are {string.Join(", ", kinds.Select(k => k.Name))}; all is every one", 2); return; }
            }

            EngineIndex eng;
            Validity v;
            try { eng = EngineIndex.Get(); v = eng.Check(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                answer.Fail($"the header index couldn't be brought up to date ({e.Message.Split('\n')[0].TrimEnd('.')})", "check the Data folder (SetData) and that the home's indexes folder can be written", 2);
                return;
            }
            var ix = eng.Index;
            Func<IndexedPlugin, bool>? pick = null;
            List<string>? asked = null;
            if (only != null)
            {
                // Names, patterns and sets (@official) through the one resolver every verb uses (GAPS S117)
                PluginNaming naming;
                try { naming = PluginSets.Resolve(ix, Home.SplitPluginList(only), winnersAllowed: false, verb: "indexes"); }
                catch (PluginSetException e) { answer.Fail(e.Message, e.Fix, 2); return; }
                if (naming.Picks.FirstOrDefault(p => p.Plugin == null) is { } other)
                {
                    var near = EngineIndex.NearPlugins(ix, other.Item);
                    answer.Fail($"--in names {other.Item}, which isn't a plugin in Data", near.Count > 0 ? SFSearch.SchemaGen.NearNames.Fix(near) : "check the names: loadorder2 lists the plugins", 2);
                    return;
                }
                var chosen = naming.Plugins.ToHashSet();
                pick = chosen.Contains;
                // What is kept as asked for (the world link index): a pattern stays a pattern, so that a plugin added later
                // that fits it is indexed too; a set is its plugins now
                asked = naming.Items.SelectMany(i => PluginSets.IsSetWord(i) ? naming.Picks.Where(p => p.Item == i).Select(p => p.Plugin!.Name) : new[] { i }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            // Before a drop lets go of the index
            answer.Source(eng.AllSource());
            switch (action)
            {
                case "show": Show(answer, eng); break;
                case "build": Build(answer, eng, v, kindWord!, pick, only, asked); break;
                case "drop": Drop(answer, eng, kindWord!, pick, only, asked); break;
            }
        }

        // ---------------------------------------------------------------- What is kept
        sealed class Part
        {
            public required Kind Kind;
            public long Bytes; public int Files;
            public int Covered, Could, Asked;
            public DateTime? Oldest, Newest;
            public string Remake = "";
        }

        static Part Look(Kind kind, EngineIndex eng)
        {
            var ix = eng.Index;
            var part = new Part { Kind = kind };
            HashSet<string> used;
            if (kind.Name == "header")
            {
                used = ix.Plugins.Where(p => p.Segment != null).Select(p => Path.GetFileName(p.Segment!.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                part.Could = ix.Plugins.Count;
                long bytes = ix.Plugins.Sum(p => p.Stamp.Length);
                part.Remake = $"about {Seconds(bytes / 1073741824.0 * HeaderSecondsPerGB)} ({EngineIndex.Mb(bytes)} of plugins read and hashed)";
            }
            else if (kind.Name == "world")
            {
                var with = ix.Plugins.Where(p => EngineLinks.Walkable(p) && !p.ReadingKey.IsZero && WorldLinks.HasWorld(p)).ToList();
                used = with.Select(p => Path.GetFileName(WorldLinks.PathOf(p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                part.Could = with.Count;
                var wanted = WorldLinks.Wanted();
                var asked = with.Where(p => WorldLinks.IsWanted(p, wanted)).ToList();
                part.Asked = asked.Count;
                var (records, _, seconds, mb) = WorldLinks.Cost(asked.Count > 0 ? asked : with);
                part.Remake = $"about {Seconds(seconds)} on {LinkThreads()} cores and up to {MemoryInfo.GB(mb)} GB ({records:N0} world records of {(asked.Count > 0 ? $"the {asked.Count:N0} plugin(s) asked for" : $"all {with.Count:N0} plugins with world data")})";
            }
            else
            {
                used = ix.Plugins.Where(p => EngineLinks.Walkable(p) && !p.ReadingKey.IsZero).Select(p => Path.GetFileName(EngineLinks.PathOf(p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                part.Could = ix.Plugins.Count(EngineLinks.Walkable);
                var (records, seconds, mb) = LinkWalk(ix.Plugins.Where(EngineLinks.Walkable));
                part.Remake = $"about {Seconds(seconds)} on {LinkThreads()} cores and up to {MemoryInfo.GB(mb)} GB ({records:N0} records walked)";
            }
            try
            {
                if (Directory.Exists(kind.Folder))
                    foreach (var f in new DirectoryInfo(kind.Folder).EnumerateFiles())
                    {
                        part.Bytes += f.Length; part.Files++;
                        bool isPart = f.Extension.Equals(".seg", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".links", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".wlinks", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase);
                        if (!isPart) continue;
                        if (used.Contains(f.Name))
                        {
                            part.Covered++;
                            var t = f.LastWriteTimeUtc;
                            if (part.Oldest == null || t < part.Oldest) part.Oldest = t;
                            if (part.Newest == null || t > part.Newest) part.Newest = t;
                        }
                    }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return part;
        }

        /// <summary>The records a link walk of these plugins reads (outside world data), its time and its memory.</summary>
        static (long Records, double Seconds, long MB) LinkWalk(IEnumerable<IndexedPlugin> plugins)
        {
            long records = 0;
            foreach (var p in plugins)
            {
                if (p.Segment == null) continue;
                records += Math.Max(0, p.Segment.FirstUnresolved - WorldTypes.Totals(p.Segment).Records);
            }
            double seconds = records * LinkSecondsPerRecord * 8.0 / LinkThreads();
            // M5b: 1.8 to 2.2 GB at the peak for every plugin of a 370-plugin Data folder, Starfield.esm's walk the biggest part
            long mb = Math.Min(2300, 300 + records / 1700);
            return (records, seconds, mb);
        }

        static int LinkThreads() => Math.Clamp(Environment.ProcessorCount - 2, 2, 8);

        static string Seconds(double s) => s < 1 ? "1 s" : s < 90 ? $"{Math.Ceiling(s):0} s" : $"{s / 60:0.#} min";

        static string Age(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            string ago = d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{d.TotalMinutes:0} min ago" : d.TotalDays < 2 ? $"{d.TotalHours:0} h ago" : $"{d.TotalDays:0} days ago";
            return $"{utc.ToLocalTime():yyyy-MM-dd HH:mm} ({ago})";
        }

        static bool Keep => ServiceConfig.Load(Home.Dir).KeepStartupIndexes;

        static void Show(EngineAnswer answer, EngineIndex eng)
        {
            var parts = Kinds.Select(k => Look(k, eng)).ToList();
            long all = parts.Sum(p => p.Bytes);
            answer.Line($"SFSearch keeps {parts.Count} indexes in {Home.Indexes}: {EngineIndex.Mb(all)} in all. They are made from the plugin files and hold no record data; each plugin's part is named by its content, so a changed plugin's part is never read.");
            answer.Line(Keep
                ? "The header and link indexes are kept between service starts (keepStartupIndexes is on in sfsearch.json): a start brings them up to date plugin by plugin (a new or changed plugin's part is made, the rest is reused) and then checks every plugin by a full hash, so they are ready in seconds instead of being made again."
                : "The header and link indexes are emptied at every service start and made again (keepStartupIndexes is false in sfsearch.json). Set it to true to keep them between starts: a start then reuses them and makes only the parts of new or changed plugins.");
            answer.Line("The world link index is made only when you ask for it (indexes build world, or the answer save_index where a question offers it: allow_heavy=\"save_index\", refs' world \"save_index\"), then kept whatever keepStartupIndexes says: a plugin you asked for gets a new part when it changes, until you drop it.");
            answer.Line();
            answer.Line("| Index | Size | Covers | Built | Made again from nothing | Speeds up |");
            answer.Line("|---|---|---|---|---|---|");
            foreach (var p in parts)
            {
                string covers = p.Kind.Name == "links" ? $"{p.Covered:N0} of {p.Could:N0} plugins that can be walked"
                    : p.Kind.Name == "world" ? $"{p.Covered:N0} of {p.Could:N0} plugins with world data ({(p.Asked == 0 ? "not asked for" : p.Asked == p.Could ? "asked for: all" : $"asked for: {p.Asked:N0}")})"
                    : $"{p.Covered:N0} of {p.Could:N0} plugins";
                string built = p.Newest == null ? "not built" : p.Oldest!.Value.ToLocalTime().Date == p.Newest.Value.ToLocalTime().Date && (p.Newest.Value - p.Oldest.Value).TotalMinutes < 1 ? Age(p.Newest.Value) : $"{p.Oldest.Value.ToLocalTime():yyyy-MM-dd HH:mm} to {Age(p.Newest.Value)}";
                answer.Line($"| {p.Kind.Name} | {EngineIndex.Mb(p.Bytes)} | {covers} | {built} | {p.Remake} | {p.Kind.SpeedsUp} |");
            }
            answer.Line();
            foreach (var p in parts.Where(p => p.Kind.Name != "world" && p.Covered < p.Could))
                answer.Line(p.Kind.Name == "links"
                    ? $"Not covered yet: {p.Could - p.Covered:N0} plugin(s) have no link file. Until they do, refs searches their bytes and find knows no in-game names of theirs. The service makes them in the background; indexes build links makes them now."
                    : $"Not covered yet: {p.Could - p.Covered:N0} plugin(s) have no segment (sfs_loadorder problems says why a plugin can't be read).");
            if (parts.FirstOrDefault(p => p.Kind.Name == "world") is { } w)
            {
                if (w.Asked == 0 && w.Covered == 0)
                    answer.Line($"World link index: not asked for. Without it, refs searches world data by its bytes (Starfield.esm: 1.2 GB, several seconds a question, and world data is left out by default when there is more than 256 MB of it). indexes build world makes it for every plugin with world data ({w.Remake}); indexes build world --in <plugins> for some.");
                else if (w.Covered < w.Asked)
                    answer.Line($"World link index: {w.Asked - w.Covered:N0} plugin(s) you asked for have no world link file yet (new or changed). Until they do, refs searches their world data by its bytes. The service makes them at its next link job; indexes build world makes them now.");
            }
            // Parts no plugin in Data uses now: deleted at the next start, or kept up to keepRemovedPluginIndexMB (J43)
            if (KeptParts.Describe(eng, ServiceConfig.Load(Home.Dir, makeFile: false)) is { } unused) answer.Line(unused);
            answer.Line($"To delete: indexes drop header, indexes drop links, indexes drop world or indexes drop all (sfs_run [\"indexes\", \"drop\", \"all\"]); add --in <plugins> to drop only those plugins' parts. The service makes the header and link indexes again{(Keep ? " (the header index at the next command, the link index at its next start), and keeps them; to have them emptied at every start instead, set \"keepStartupIndexes\": false in sfsearch.json" : "")}. A dropped world link index isn't made again until you ask for it.");
        }

        // ---------------------------------------------------------------- Build
        static void Build(EngineAnswer answer, EngineIndex eng, Validity v, string kind, Func<IndexedPlugin, bool>? pick, string? only, List<string>? words)
        {
            var ix = eng.Index;
            if (kind == "header" && only != null) { answer.Fail("the header index covers every plugin in Data at once: --in doesn't apply to it", "run indexes build header, or indexes build links --in " + only, 2); return; }
            if (kind == "world") { BuildWorld(answer, eng, pick, only, words, asked: true); return; }
            if (kind is "header" or "all")
            {
                var r = v.Report;
                answer.Line(r.PassesRun.Count == 0 && r.Hashed.Count == 0
                    ? $"Header index: up to date, nothing to make: {ix.Plugins.Count(p => p.Segment != null):N0} of {ix.Plugins.Count:N0} plugins, {EngineIndex.Mb(ix.TotalSegmentBytes)} (checked against the files in {v.Ms:N0} ms)."
                    : $"Header index: brought up to date in {v.Ms / 1000:0.0} s: {r.PassesRun.Count:N0} plugin(s) read, {r.Hashed.Count:N0} hashed, {r.SegmentsReused.Count:N0} part(s) already there. {ix.Plugins.Count(p => p.Segment != null):N0} of {ix.Plugins.Count:N0} plugins, {EngineIndex.Mb(ix.TotalSegmentBytes)}.");
                foreach (var n in v.Notes) answer.Note(n);
            }
            if (kind is "links" or "all")
            {
                var want = ix.Plugins.Where(p => (pick == null || pick(p)) && EngineLinks.Walkable(p)).ToList();
                var missing = want.Where(p => !File.Exists(EngineLinks.PathOf(p))).ToList();
                int cannot = ix.Plugins.Count(p => (pick == null || pick(p)) && !EngineLinks.Walkable(p));
                string scope = only != null ? $" of the plugins --in {only} names" : "";
                if (missing.Count == 0)
                    answer.Line($"Link index: up to date, nothing to make: {want.Count:N0} plugin(s){scope} have their link file.{(cannot > 0 ? $" {cannot:N0} can't be walked (a master is missing or the plugin can't be read: sfs_loadorder problems)." : "")}");
                else
                {
                    var (records, seconds, mb) = LinkWalk(missing);
                    if (!EngineLibs.Heavy(answer, false, seconds, mb, $"Making the link files of {missing.Count:N0} plugin(s){scope} ({records:N0} records to walk)", noIndex: "this makes the index")) return;
                    SFSearch.Records.MutagenStart.Run();
                    var set = missing.ToHashSet();
                    var r = EngineLinks.Build(eng, () => false, only: set.Contains);
                    answer.Line($"Link index: {r.Built:N0} plugin(s){scope} walked in {r.Seconds:0.0} s ({r.Records:N0} records, {r.Links:N0} links, {r.Names:N0} in-game names, {EngineIndex.Mb(r.Bytes)} written), {r.AlreadyThere:N0} already there{(r.Left > 0 ? $", {r.Left:N0} left" : "")}.");
                    foreach (var p in r.Problems.Take(12)) answer.Note(p);
                    if (r.Problems.Count > 12) answer.Note($"… {r.Problems.Count - 12} more");
                }
            }
            if (kind == "all") BuildWorld(answer, eng, pick, only, words, asked: false);
            answer.Line(Keep ? "They are kept between service starts (keepStartupIndexes). indexes shows them and their size." : "keepStartupIndexes is false: the header and link indexes are emptied at the next service start. indexes shows them and their size.");
        }

        /// <summary>
        /// The world link index (J32). Asked for (indexes build world [--in]): the plugins named, or every plugin with
        /// world data, are added to what the user asked to keep (only once the cost is allowed) and their files made.
        /// Not asked (build all): the files missing of the plugins already asked for.
        /// </summary>
        static void BuildWorld(EngineAnswer answer, EngineIndex eng, Func<IndexedPlugin, bool>? pick, string? only, List<string>? words, bool asked)
        {
            var ix = eng.Index;
            var wanted = WorldLinks.Wanted();
            var with = ix.Plugins.Where(p => (pick == null || pick(p)) && EngineLinks.Walkable(p) && WorldLinks.HasWorld(p)).ToList();
            var want = asked ? with : with.Where(p => WorldLinks.IsWanted(p, wanted)).ToList();
            string scope = only != null ? $" of the plugins --in {only} names" : "";
            if (!asked && want.Count == 0)
            {
                answer.Line($"World link index: not asked for{scope}, so not made (indexes build world makes it, and SFSearch then keeps it).");
                return;
            }
            if (asked && with.Count == 0)
            {
                answer.Line($"World link index: no plugin{scope} holds world data (cells, placed objects, navmeshes, terrain), so there is nothing to index.");
                return;
            }
            var missing = want.Where(p => !File.Exists(WorldLinks.PathOf(p))).ToList();
            if (missing.Count > 0)
            {
                var (records, bytes, seconds, mb) = WorldLinks.Cost(missing);
                if (!EngineLibs.Heavy(answer, false, seconds, mb, $"Making the world link index of {missing.Count:N0} plugin(s){scope} ({records:N0} world records, {EngineIndex.Mb(bytes)})", noIndex: "this makes the index")) return;
            }
            if (asked) WorldLinks.Want(words ?? new List<string> { "*" });
            if (missing.Count == 0)
            {
                answer.Line($"World link index: up to date, nothing to make: {want.Count:N0} plugin(s){scope} have their world link file.");
            }
            else
            {
                SFSearch.Records.MutagenStart.Run();
                var set = missing.ToHashSet();
                var r = WorldLinks.Build(eng, () => false, set.Contains);
                answer.Line($"World link index: {r.Built:N0} plugin(s){scope} made in {r.Seconds:0.0} s ({r.Records:N0} world records, {r.ByBytes:N0} of them partly by their bytes, {r.Links:N0} links in {r.Postings:N0} rows, {EngineIndex.Mb(r.Bytes)} written), {want.Count - missing.Count:N0} already there{(r.Left > 0 ? $", {r.Left:N0} left" : "")}.");
                foreach (var p in r.Problems.Take(12)) answer.Note(p);
                if (r.Problems.Count > 12) answer.Note($"… {r.Problems.Count - 12} more");
            }
            if (asked) answer.Line($"SFSearch keeps it for {(only != null ? "these plugins" : "every plugin with world data, and adds new ones")}: a plugin that changes gets a new part at the service's next link job. refs answers links from world data from it; indexes drop world{(only != null ? " --in " + only : "")} deletes it.");
        }

        // ---------------------------------------------------------------- Drop
        static void Drop(EngineAnswer answer, EngineIndex eng, string kind, Func<IndexedPlugin, bool>? pick, string? only, List<string>? words)
        {
            var ix = eng.Index;
            var dropped = new List<string>();
            int failed = 0;
            // The files to delete, before anything is let go: the named plugins' parts, or everything in the index's folder
            var chosen = Kinds.Where(k => kind == "all" || k.Name == kind).Select(k => (Kind: k, Files: pick != null
                ? ix.Plugins.Where(pick).Select(p => k.Name == "header" ? p.Segment?.Path : p.ReadingKey.IsZero ? null : k.Name == "world" ? WorldLinks.PathOf(p) : EngineLinks.PathOf(p)).Where(f => f != null && File.Exists(f)).Select(f => f!).ToList()
                : Directory.Exists(k.Folder) ? Directory.GetFiles(k.Folder).ToList() : new List<string>())).ToList();
            bool world = chosen.Any(c => c.Kind.Name == "world");
            // The world link index is forgotten as asked for: it isn't made again until asked
            if (world) WorldLinks.Unwant(words);
            foreach (var (k, files) in chosen)
            {
                // Let go of what this program has open first, so that its files go at once
                if (k.Name == "header") EngineIndex.Forget(); else if (k.Name == "world") WorldLinks.CloseAll(); else EngineLinks.CloseAll();
                long bytes = 0; int n = 0;
                foreach (var f in files)
                {
                    if (!File.Exists(f)) continue; // wanted.txt, gone with the choice
                    try { long size = new FileInfo(f).Length; File.Delete(f); bytes += size; n++; }
                    catch (IOException) { failed++; }
                    catch (UnauthorizedAccessException) { failed++; }
                }
                dropped.Add($"the {(k.Name == "header" ? "header index" : k.Name == "world" ? "world link index" : "link index")}{(pick != null ? $" of the plugins --in {only} names" : "")}: {n:N0} file(s), {EngineIndex.Mb(bytes)}");
            }
            answer.Line($"Deleted {string.Join("; ", dropped)}.{(failed > 0 ? $" {failed:N0} file(s) couldn't be deleted (in use by another program): the next indexes drop or the service's next start removes them." : "")}");
            if (chosen.Any(c => c.Kind.Name != "world"))
            {
                answer.Line(Keep
                    ? "The service makes what it needs again: the header index at the next command that needs it, the link index at its next start or idle check (pluginCheckSeconds in sfsearch.json, 10 minutes by default), and keeps them between starts (keepStartupIndexes). To have them emptied at every start instead, set \"keepStartupIndexes\": false in sfsearch.json."
                    : "The service makes what it needs again: the header index at the next command that needs it, the link index at its next start or idle check. keepStartupIndexes is false: they are emptied at every start anyway.");
                if (chosen.Any(c => c.Kind.Name == "links")) answer.Line("Until the link index is back, refs searches the plugins' bytes (slower) and find knows fewer in-game names.");
            }
            if (world) answer.Line($"The world link index{(pick != null ? " of those plugins" : "")} isn't made again until you ask for it (indexes build world{(pick != null ? " --in " + only : "")}). Until then refs searches world data by its bytes.");
        }

        // ---------------------------------------------------------------- sfs_status's line
        /// <summary>The one line sfs_status gives about the kept indexes: where, how big, whether kept, how to see and drop them.</summary>
        public static string StatusLine()
        {
            var sizes = Kinds.Select(k => (k.Name, Size: EngineLibs.FolderSize(k.Folder).Bytes)).ToList();
            string list = string.Join(", ", sizes.Select(s => s.Name == "world" && s.Size == 0 ? "world none (made only when asked: indexes build world)" : $"{s.Name} {EngineIndex.Mb(s.Size)}"));
            return Keep
                ? $"Kept indexes: {list} in {Home.Indexes}; kept between service starts (keepStartupIndexes), so a start reuses them. indexes shows them; indexes drop all deletes them (sfs_run)."
                : $"Indexes: {list} in {Home.Indexes}; emptied at every service start (keepStartupIndexes is false in sfsearch.json). indexes shows them (sfs_run).";
        }
    }
}
