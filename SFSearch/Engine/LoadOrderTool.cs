using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Engine
{
    /// <summary>
    /// loadorder (tool sfs_loadorder; on the command line loadorder2: the plain name LoadOrder was the older command,
    /// removed in 2.0, and answers with a pointer here): the load order with runtime slots, one plugin's masters, types and room for new records, and every
    /// problem the header index found. It reads the header index only.
    /// (design\agent-first\ARCHITECTURE.md sections 5 to 7; milestone M5, first half.)
    /// </summary>
    public static class LoadOrderTool
    {
        public const int DefaultLimit = 40, MostLimit = 2000;

        public const string Help = """
            loadorder2 [order | plugin <plugin> | problems [<plugin>] | changes [<plugin>]] [--all] [--since <day>] [--limit N] [--cursor C]
              (the load-order tool, sfs_loadorder. The older LoadOrder command was removed in 2.0.)

              order      the load order: position, loaded, runtime slot (0A, FD:03, FE:09A), plugin, size (Full, Medium, Light (Small)),
                         file size, records, new records, overrides, problems. --all adds the plugins that aren't loaded.
              plugin     one plugin (a name in Data, or the path of a plugin file elsewhere): its masters with their sizes,
                         its records per type, and the room left for new records
              problems   every problem the header index found, plugin by plugin: a missing master, a master that isn't
                         loaded or loads late, overrides of a different type than their base (a patch built for an older
                         master), overrides of nothing, a repeated FormKey. Patches at risk because a master changed size
                         since they were built are in the view changes.
              changes    what changed in Data since last time: plugins added, removed, back and updated, and the patches at
                         risk because a master of theirs changed (what is wrong now is in the view problems). With a plugin: its history. --since 2026-11-01 sets the first
                         day (default: the last 30 days); --all gives every change. The first call begins the history (a small
                         log under kept\history in the SFSearch home) and reports nothing.
              --limit    rows per answer (default 40). The answer's [more] line gives the cursor for the next rows.

              A plugin's name alone means "plugin": loadorder2 darkstar.esm
              The load order is Starfield's own plugins.txt (in your Windows user's AppData), or the one "pluginsTxt" in
              sfsearch.json (or SFSEARCH_PLUGINS_TXT) names: 'sfs.cmd Service config' shows which.
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...).
            """;

        static readonly string[] Known = { "view", "plugin", "all", "limit", "cursor", "since" };

        sealed class Ask
        {
            public string? View, Plugin, Cursor, Since;
            public bool All;
            public int Limit = DefaultLimit;
        }

        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            var answer = new EngineAnswer { AsTool = true };
            if (a.Unknown(Known) is { Count: > 0 } wrong)
                answer.Fail($"loadorder has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", $"its arguments are: {string.Join(", ", Known)}", 2);
            else
                Guarded(new Ask { View = a.Text("view"), Plugin = a.Text("plugin"), All = a.Flag("all"), Cursor = a.Text("cursor"), Limit = a.Int("limit") ?? DefaultLimit, Since = a.Text("since") }, answer);
            answer.Emit();
        }

        public static void Verb(List<string> args)
        {
            if (args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            var ask = new Ask { All = CommandLine.TakeFlag(args, "--all"), Cursor = CommandLine.TakeOption(args, "--cursor"), Since = CommandLine.TakeOption(args, "--since") };
            var answer = new EngineAnswer();
            string? limit = CommandLine.TakeOption(args, "--limit");
            var stray = args.Where(x => x.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (limit != null && !int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out ask.Limit))
                answer.Fail($"--limit takes a number, not '{limit}'", "give the number of rows, e.g. --limit 100", 2);
            else if (stray.Count > 0)
                answer.Fail($"loadorder2 has no option {string.Join(", ", stray)}", "its options are --all, --since, --limit and --cursor (loadorder2 --help shows them)", 2);
            else
            {
                if (args.Count > 0 && args[0].ToLowerInvariant() is "order" or "plugin" or "problems" or "changes") { ask.View = args[0].ToLowerInvariant(); args.RemoveAt(0); }
                if (args.Count > 0) ask.Plugin = string.Join(" ", args);
                Guarded(ask, answer);
            }
            answer.Emit();
        }

        static void Guarded(Ask ask, EngineAnswer answer)
        {
            try { Run(ask, answer); }
            catch (FileNotFoundException e) { answer.Fail(e.Message.Split('\n')[0], "set the Data folder with SetData, or the SFSEARCH_DATA environment variable"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                answer.Fail($"the header index couldn't be brought up to date ({e.Message.TrimEnd('.')})", "ask again; if it stays, check that the Data folder can be read");
            }
        }

        static void Run(Ask ask, EngineAnswer answer)
        {
            string view = (ask.View ?? (ask.Plugin != null ? "plugin" : "order")).ToLowerInvariant();
            if (view == "changes")
            {
                // What changed since last time: the changes report of SFSearch.Reports, with its history log under kept\ in the home
                var given = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                if (ask.Plugin != null) given["plugin"] = JsonSerializer.SerializeToElement(ask.Plugin);
                if (ask.Since != null) given["since"] = JsonSerializer.SerializeToElement(ask.Since);
                if (ask.All) given["all"] = JsonSerializer.SerializeToElement(true);
                if (ask.Cursor != null) given["cursor"] = JsonSerializer.SerializeToElement(ask.Cursor);
                ReportTools.Changes(new ToolArgs(given), answer);
                return;
            }
            if (ask.Since != null) { answer.Fail("since goes with the view changes only", "ask for view changes, or leave since out", 2); return; }
            var eng = EngineIndex.Get();
            var validity = eng.Check();
            answer.Checked(validity);
            answer.Read($"the header index of {eng.Index.Plugins.Count:N0} plugins (checked in {validity.Ms:N0} ms)");

            int limit = ask.Limit <= 0 ? DefaultLimit : Math.Min(ask.Limit, MostLimit);
            string question = $"loadorder|{view}|{ask.Plugin?.Trim()}|{ask.All}".ToLowerInvariant();
            if (!Cursors.TryRead(ask.Cursor, question, out int offset))
            {
                answer.Fail($"the cursor \"{ask.Cursor}\" doesn't belong to this question", "ask again with the same view, plugin and all as the answer that gave the cursor, or leave the cursor out to start over", 2);
                return;
            }
            switch (view)
            {
                case "order": Order(eng, ask, answer, limit, offset, question); break;
                case "plugin": Plugin(eng, ask, answer); break;
                case "problems": Problems(eng, ask, answer, limit, offset, question); break;
                default: answer.Fail($"loadorder has no view '{ask.View}'", "the views are: order, plugin, problems, changes", 2); break;
            }
        }

        static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- order
        static void Order(EngineIndex eng, Ask ask, EngineAnswer answer, int limit, int offset, string question)
        {
            var ix = eng.Index;
            if (ask.Plugin != null) { answer.Fail("the order view takes no plugin", $"for one plugin ask for the view \"plugin\" with plugin \"{ask.Plugin}\"", 2); return; }
            var entries = ix.LoadOrderEntries.Where(e => ask.All || e.Enabled).ToList();
            var loaded = ix.LoadOrderEntries.Where(e => e.Enabled).ToList();
            int notInData = ix.LoadOrderEntries.Count(e => !e.InData);
            // An enabled plugin with a missing master (or built on one) can't be loaded: Mutagen's load order drops it and xEdit
            // doesn't load it; the game won't either. It is counted apart, its row says so, and it has no slot.
            var cantLoad = loaded.Select(e => ix.Plugin(e.Name)).Where(p => p is { CantLoad: true }).Select(p => p!.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sizes = new SizeWords();
            answer.Line((cantLoad.Count == 0 ? "" : $"{loaded.Count} plugins enabled; {cantLoad.Count} of them can't be loaded (a master isn't in Data, or one of its masters can't be loaded): Mutagen's load order drops them and xEdit doesn't load them, so they are left out of queries and of the winners. ")
                + $"{loaded.Count - cantLoad.Count} plugins loaded ({loaded.Count(e => e.Size == MasterStyle.Full && !cantLoad.Contains(e.Name))} Full, {loaded.Count(e => e.Size == MasterStyle.Medium && !cantLoad.Contains(e.Name))} Medium, {loaded.Count(e => e.Size == MasterStyle.Small && !cantLoad.Contains(e.Name))} {sizes.Name("Small")}), "
                + $"{ix.Plugins.Count} in Data{(notInData > 0 ? $", {notInData} listed in plugins.txt but not in Data" : "")}."
                + $"{(ask.All ? "" : " The plugins that aren't loaded are left out (all: true adds them).")} Slot is the runtime prefix of a plugin's own records: FE:09A means FormIDs FE09Axxx in the console.");
            answer.Line("| Pos | Loaded | Slot | Plugin | Size | File | Records | New | Overrides | Problems |");
            answer.Line("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
            answer.HeaderLines = 3;
            var page = entries.Skip(offset).Take(limit).ToList();
            if (offset > 0 && page.Count == 0) { answer.Fail($"the cursor points past the end ({entries.Count} rows)", "leave the cursor out to start over", 2); return; }
            var shown = new List<IndexedPlugin>();
            foreach (var e in page)
            {
                var p = ix.Plugin(e.Name);
                string pos = e.Position < 0 ? "-" : e.Position.ToString(CultureInfo.InvariantCulture);
                if (p == null) { answer.Line($"| {pos} | no |  | {e.Name} |  |  |  |  |  | listed in plugins.txt but not in Data |"); continue; }
                shown.Add(p);
                var seg = p.Segment;
                string records = seg == null ? "" : N(seg.Count), news = seg == null ? "" : N(seg.FirstUnresolved - seg.FirstOwn), overrides = seg == null ? "" : N(seg.FirstOwn);
                bool cant = e.Enabled && cantLoad.Contains(p.Name);
                answer.Line($"| {pos} | {(cant ? "no: can't be loaded" : e.Enabled ? "yes" : "no")} | {(cant ? "" : e.Slot)} | {p.Name} | {(p.Header == null ? "" : sizes.Name(EngineIndex.SizeName(e.Size)))} | {EngineIndex.Mb(p.Stamp.Length)} | {records} | {news} | {overrides} | {ProblemNotes.Short(p)} |");
            }
            int next = offset + page.Count;
            answer.Page(offset + 1, page.Count, entries.Count, next < entries.Count ? Cursors.Make(next, question) : null);
            ProblemNotes.Add(answer, shown.Select(p => (ix, p, (string?)null)), required: false, leftOut: false);
            foreach (var bad in ix.BadLoadOrderLines) answer.Note($"A line of plugins.txt names no plugin: {bad}");
            answer.Source(eng.LoadOrderSource());
            answer.Source(eng.AllSource());
        }

        // ---------------------------------------------------------------- plugin
        static void Plugin(EngineIndex eng, Ask ask, EngineAnswer answer)
        {
            var main = eng.Index;
            if (string.IsNullOrWhiteSpace(ask.Plugin)) { answer.Fail("the plugin view needs a plugin", "name one: plugin \"darkstar.esm\" (a name in Data, or the path of a plugin file)", 2); return; }
            string item = ask.Plugin!.Trim().Trim('"');
            HeaderIndex ix = main;
            IndexedPlugin? p = main.Plugin(item);
            bool byPath = false;
            if (p == null && (item.Contains('\\') || item.Contains('/') || Path.IsPathRooted(item)))
            {
                string full = Home.UserPath(item);
                if (!File.Exists(full)) { answer.Fail($"there is no file {full}", "check the path, or name a plugin that is in Data"); return; }
                if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(full) ?? ""), Path.TrimEndingDirectorySeparator(eng.DataFolder), StringComparison.OrdinalIgnoreCase))
                    p = main.Plugin(Path.GetFileName(full));
                else
                {
                    var side = eng.ByPath(full);
                    if (side == null) { answer.Fail($"{full} isn't a Starfield plugin (it doesn't start with a TES4 record)", "give a .esm, .esp or .esl file"); return; }
                    (ix, p) = side.Value; byPath = true;
                }
            }
            if (p == null)
            {
                bool listed = main.LoadOrderEntries.Any(e => e.Name.Equals(item, StringComparison.OrdinalIgnoreCase));
                var near = EngineIndex.NearPlugins(main, item);
                answer.Fail(listed ? $"{item} is in the load order (plugins.txt) but its file isn't in Data" : $"{item} isn't in Data ({main.Plugins.Count} plugins are there)",
                    near.Count > 0 ? $"the nearest real names: {string.Join("; ", near)}" : "check the name (the order view lists the plugins), or give the full path of a plugin file outside Data");
                return;
            }

            // Problems first. A plugin that can't be read is still described here: this is where one looks for what is wrong.
            ProblemNotes.Add(answer, new[] { (ix, p, (string?)null) }, required: false, leftOut: false);
            if (byPath) answer.Note($"{p.Name} was read from {p.Path}, outside Data: it isn't part of the load order.");

            var schema = SchemaTypes.Default;
            var seg = p.Segment;
            var sizes = new SizeWords();
            string size = p.Header == null ? "not a plugin" : sizes.Name(EngineIndex.SizeName(p.Header.Style));
            string where = byPath ? "a file outside Data, not in the load order"
                : p.Order.Enabled && p.CantLoad ? $"enabled at position {p.Order.Position}, but it can't be loaded: {(p.Problems.MissingMasters.Count > 0 ? $"its master {string.Join(", ", p.Problems.MissingMasters)} isn't in Data" : $"its master {p.Problems.MasterUnreadable} can't be loaded (a master of it isn't in Data)")}. Mutagen's load order drops it and xEdit doesn't load it, so it is left out of queries and of the winners"
                : p.Order.Enabled ? $"loaded at position {p.Order.Position}, slot {p.Order.Slot}{(p.LeftOut != null ? $", but left out of queries and of the winners: {(p.Problems.Refused != null ? "Mutagen refuses it (its error is in the notes)" : p.LeftOut)}" : "")}"
                : p.Order.Position >= 0 ? $"in the load order at position {p.Order.Position} but not enabled" : "in Data but not in the load order";
            answer.Line($"{p.Name}: {size}, {EngineIndex.Mb(p.Stamp.Length)} ({N(p.Stamp.Length)} bytes), {where}.");
            if (seg == null) { answer.Line($"It has no record index: {p.Problems.ReadError ?? "it couldn't be read"}."); answer.Source(EngineIndex.Source(p, byPath ? p.Path : null)); return; }

            int own = seg.FirstUnresolved - seg.FirstOwn;
            answer.Line($"{N(seg.Count)} records in {N(seg.Groups)} groups: {N(own)} new, {N(seg.FirstOwn)} overrides{(seg.Unresolved > 0 ? $", {N(seg.Unresolved)} whose FormID can't be read (a master is missing)" : "")}.");
            uint most = EngineIndex.MaxLocalId(p.Header!.Style);
            if (own > 0)
            {
                uint top = seg[seg.FirstUnresolved - 1].LocalId;
                answer.Line($"Room for new records: its highest own ID is {top:X6}. A {sizes.Name(EngineIndex.SizeName(p.Header.Style))} plugin's IDs go up to {most:X6}, so {N(Math.Max(0, (long)most - top))} are left above it"
                    + $"{(top > most ? $" (it is already past the limit by {N((long)top - most)})" : "")}.");
            }
            else answer.Line($"Room for new records: it has no record of its own. A {sizes.Name(EngineIndex.SizeName(p.Header.Style))} plugin's IDs go up to {most:X6}.");

            // One pass over its entries: overrides per master, new and overriding records per type
            var perOwner = new int[seg.Names.Count];
            var sigNew = new int[seg.Signatures.Count]; var sigOver = new int[seg.Signatures.Count];
            var entries = seg.Entries;
            for (int i = 0; i < seg.FirstUnresolved; i++)
            {
                var e = entries[i];
                if (e.Owner < perOwner.Length) perOwner[e.Owner]++;
                if (i < seg.FirstOwn) sigOver[e.Signature]++; else sigNew[e.Signature]++;
            }

            var masters = p.Header.Masters;
            answer.Line();
            if (masters.Count == 0) answer.Line("Masters: none.");
            else
            {
                answer.Line($"Masters ({masters.Count}), in the header's order:");
                answer.Line("| # | Master | Size | Loaded | Slot | File | Overridden here |");
                answer.Line("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
                for (int m = 0; m < masters.Count; m++)
                {
                    var mp = main.Plugin(masters[m]);
                    string msize = mp?.Header == null ? "missing" : sizes.Name(EngineIndex.SizeName(mp.Header.Style));
                    bool cant = mp is { CantLoad: true } && mp.Order.Enabled;
                    string loaded = mp == null ? "not in Data" : cant ? "no: can't be loaded" : mp.Order.Enabled ? "yes" : "no";
                    answer.Line($"| {m} | {mp?.Name ?? masters[m]} | {msize} | {loaded} | {(cant ? "" : mp?.Order.Slot)} | {(mp == null ? "" : EngineIndex.Mb(mp.Stamp.Length))} | {(m < perOwner.Length ? N(perOwner[m]) : "")} |");
                    if (mp != null) answer.Source(EngineIndex.Source(mp));
                }
            }

            answer.Line();
            answer.Line($"Records by type ({seg.Signatures.Count} types):");
            answer.Line("| Type | Records | New | Overrides | Bytes |");
            answer.Line("| :--- | :--- | :--- | :--- | :--- |");
            bool star = false;
            foreach (var (s, i) in seg.Signatures.Select((s, i) => (s, i)).OrderByDescending(x => x.s.Count).ThenBy(x => x.s.Signature, StringComparer.Ordinal))
            {
                string label = schema.Label(s.Signature);
                star |= label.EndsWith('*');
                answer.Line($"| {label} | {N(s.Count)} | {N(sigNew[i])} | {N(sigOver[i])} | {N(s.Bytes)} |");
            }
            if (star)
                foreach (var sig in seg.Signatures.Select(s => s.Signature).Where(s => schema.TypesOf(s).Count > 1))
                    answer.Note($"* {sig} holds {schema.TypesOf(sig).Count} Mutagen types ({string.Join(", ", schema.TypesOf(sig))}). The header index counts them together.");
            if (seg.Unresolved > 0) answer.Note($"The counts per type and per master leave out the {N(seg.Unresolved)} records whose FormID can't be read.");
            answer.Source(EngineIndex.Source(p, byPath ? p.Path : null));
            if (!byPath) answer.Source(eng.LoadOrderSource());
        }

        // ---------------------------------------------------------------- problems
        static void Problems(EngineIndex eng, Ask ask, EngineAnswer answer, int limit, int offset, string question)
        {
            var ix = eng.Index;
            IEnumerable<IndexedPlugin> plugins = ix.Plugins.OrderBy(EngineIndex.Rank);
            if (ask.Plugin != null)
            {
                var one = ix.Plugin(ask.Plugin.Trim().Trim('"'));
                if (one == null) { answer.Fail($"{ask.Plugin} isn't in Data ({ix.Plugins.Count} plugins are there)", "check the name; the order view lists the plugins"); return; }
                plugins = new[] { one };
            }
            var rows = new List<string>();
            var sizes = new SizeWords();
            int with = 0;
            foreach (var p in plugins)
            {
                var pr = p.Problems;
                if (!pr.Any) continue;
                with++;
                void Row(string problem, string examples = "") => rows.Add($"| {p.Name} | {problem} | {examples.Replace("|", "\\|")} |");
                if (pr.ReadError != null) Row("can't be read", pr.ReadError);
                if (pr.Refused != null) Row("refused by Mutagen: left out of queries and of the winners (record and check2 still read it)", pr.Refused);
                if (pr.MasterRefused != null) Row($"its master {pr.MasterRefused} is refused by Mutagen: left out of queries and of the winners");
                if (pr.MasterUnreadable != null) Row($"can't be loaded: its master {pr.MasterUnreadable} can't be (a master of it isn't in Data); left out of queries and of the winners");
                if (pr.MissingMasters.Count > 0) Row($"{(p.Order.Enabled ? "can't be loaded: " : "")}missing master(s): {string.Join(", ", pr.MissingMasters)}", $"{N(pr.Unresolved)} of its {N(p.Segment?.Count ?? 0)} records have a FormID that can't be read without {(pr.MissingMasters.Count == 1 ? "it" : "them")}");
                if (pr.MastersNotLoaded.Count > 0) Row($"master(s) not loaded: {string.Join(", ", pr.MastersNotLoaded)}");
                if (pr.MastersLoadedAfter.Count > 0) Row($"master(s) load after it: {string.Join(", ", pr.MastersLoadedAfter)}");
                if (pr.TypeMismatches.Count > 0)
                {
                    var ex = new List<string>();
                    foreach (int e in pr.TypeMismatches.Take(3))
                    {
                        var r = ix.Describe(new RecordRef(p.Id, e));
                        var v = ix.Versions(r.FormKey);
                        var b = v == null ? null : ix.Describe(v.Holders.FirstOrDefault(h => r.FormKey.EndsWith(":" + ix.Plugins[h.Plugin].Name, StringComparison.OrdinalIgnoreCase), v.Holders[0]));
                        ex.Add($"{r.Signature} {ProblemNotes.Label(r)} over {b?.Signature} {(b == null ? "?" : ProblemNotes.Label(b))}");
                    }
                    Row($"{N(pr.TypeMismatches.Count)} override(s) of a different type than their base: built for an older version of a master", string.Join("; ", ex));
                }
                if (pr.OverridesOfNothing.Count > 0)
                    Row($"{N(pr.OverridesOfNothing.Count)} override(s) of a record their master doesn't have",
                        string.Join("; ", pr.OverridesOfNothing.Take(3).Select(e => { var r = ix.Describe(new RecordRef(p.Id, e)); return $"{r.Signature} {ProblemNotes.Label(r)}"; })));
                if (pr.Duplicates > 0 && p.Segment is { } seg)
                {
                    var ex = new List<string>();
                    for (int i = 1; i < seg.Count && ex.Count < 3; i++)
                        if (seg[i].Key == seg[i - 1].Key)
                        {
                            var a = ix.Describe(new RecordRef(p.Id, i - 1)); var b = ix.Describe(new RecordRef(p.Id, i));
                            ex.Add($"{a.Signature} {ProblemNotes.Label(a)} and {b.Signature} {ProblemNotes.Label(b)}");
                        }
                    Row($"{N(pr.Duplicates)} record(s) repeat a FormKey the plugin already holds", string.Join("; ", ex));
                }
                if (pr.BothSizeFlags) Row($"the header has both the {sizes.Name("Small")} and the Medium flag; read as {sizes.Name("Small")}");
            }
            if (ask.Plugin == null)
            {
                foreach (var e in ix.LoadOrderEntries.Where(e => !e.InData)) rows.Add($"| {e.Name} | listed in plugins.txt but not in Data |  |");
                foreach (var bad in ix.BadLoadOrderLines) rows.Add($"| (plugins.txt) | a line names no plugin | {bad.Replace("|", "\\|")} |");
            }
            answer.Source(eng.LoadOrderSource());
            answer.Source(ask.Plugin == null ? eng.AllSource() : EngineIndex.Source(plugins.First()));
            // What is wrong now is here; what went wrong since a patch was built is in the changes view
            answer.Note("Patches at risk because a master changed size (Full, Medium, Light) after they were built are in the changes view: sfs_loadorder view \"changes\" (command line: loadorder2 changes). It compares with the history log, so its first call only begins that log.");
            if (rows.Count == 0)
            {
                answer.Line(ask.Plugin == null
                    ? $"No plugin has a problem the header index can see ({ix.Plugins.Count} plugins, {N(ix.TotalRecords)} records checked)."
                    : $"{plugins.First().Name} has no problem the header index can see ({N(plugins.First().Segment?.Count ?? 0)} records checked: masters, load order, the type of each override's base, repeated FormKeys).");
                return;
            }
            answer.Line(ask.Plugin == null
                ? $"{with} of {ix.Plugins.Count} plugins have problems the header index can see ({rows.Count} rows)."
                : $"{plugins.First().Name}: {rows.Count} problem(s) the header index can see.");
            answer.Line("| Plugin | Problem | Examples |");
            answer.Line("| :--- | :--- | :--- |");
            answer.HeaderLines = 3;
            var page = rows.Skip(offset).Take(limit).ToList();
            if (offset > 0 && page.Count == 0) { answer.Fail($"the cursor points past the end ({rows.Count} rows)", "leave the cursor out to start over", 2); return; }
            foreach (var r in page) answer.Line(r);
            int next = offset + page.Count;
            answer.Page(offset + 1, page.Count, rows.Count, next < rows.Count ? Cursors.Make(next, question) : null);
        }
    }
}
