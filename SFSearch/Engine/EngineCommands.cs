using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SFSearch.Engine
{
    /// <summary>
    /// The command-line verbs of the engine (design\agent-first, milestones M5 and M8), and __index and __links, the jobs
    /// the service's indexer lane runs.
    /// </summary>
    public static class EngineCommands
    {
        /// <summary>One verb of the new engine: its name on the command line, the tool it is (when it is one of the 12), and what it answers in one line.</summary>
        public sealed record VerbInfo(string Verb, string? Tool, string What, Action<List<string>> Run);

        /// <summary>
        /// Every verb of the engine (milestones M5 and M8). A verb whose natural name an older command had keeps a 2 at its
        /// end: query2, check2, refs2, loadorder2 and the reports. The older commands were removed in 2.0 and their plain
        /// names answer with a pointer to these (Program.Retired), so an old script gets a clear line, not a new tool's
        /// answer to arguments it wasn't written for (design\agent-first\notes\remove-old-commands.md).
        /// </summary>
        public static readonly IReadOnlyList<VerbInfo> Verbs = new List<VerbInfo>
        {
            new("find", "sfs_find", "what a FormID, EditorID or in-game name is; records by pattern or type; who holds each and who wins", FindTool.Verb),
            new("record", "sfs_record", "one record: card, full, fields, json, raw, versions, evidence, verify", RecordTool.Verb),
            new("describe", "sfs_describe", "a record type's fields, one field, an enum, or a search over field names (no plugin read)", DescribeTool.Verb),
            new("query2", "sfs_query", "SQL over typed views; scope plugins, winners or versions", QueryTool.Verb),
            new("saved", "sfs_saved", "the stored queries: list, show, check, run with named arguments, save", SavedTool.Verb),
            new("refs2", "sfs_refs", "what points at a record, and what it points at", RefsTool.Verb),
            new("compare", "sfs_compare", "versions, records, plugin_vs_masters, plugins, patch_vs_reference", CompareTool.Verb),
            new("loadorder2", "sfs_loadorder", "the load order, one plugin, the problems the index found, what changed", LoadOrderTool.Verb),
            new("check2", "sfs_check", "is a plugin built right: 19 checks, ITMs, assets, materials, the round trip", ReportTools.CheckVerb),
            new("leveled2", null, "what a leveled list can give, with the lists under it", ReportTools.LeveledVerb),
            new("dialogue2", null, "what a quest's (or one topic's) characters say", ReportTools.DialogueVerb),
            new("sources2", null, "how the player gets each item a plugin adds", ReportTools.SourcesVerb),
            new("archive2", null, "what is inside BA2 archives: summary, list, find, show, extract; grep: which files hold a text; encoding: how text files are written", ReportTools.ArchiveVerb),
            new("assets2", null, "every file path on a plugin's records: what is missing", ReportTools.AssetsVerb),
            new("materials2", null, "a plugin's meshes, materials and textures: what is missing", ReportTools.MaterialsVerb),
            new("scripts2", null, "a plugin's Papyrus scripts as source, and the records that carry them", ReportTools.ScriptsVerb),
            new("changes2", null, "what changed in Data since last time; patches at risk (also loadorder2 changes)", ReportTools.ChangesVerb),
            new("profile", null, "what a field holds across plugins: counts, values, zeros, FormID/strings/float reads, samples; or the GRUP headers; --conditions: condition functions; --subrecords: which types hold a subrecord, their orders, a type's layout", ReportTools.ProfileVerb),
            new("indexes", null, "the indexes SFSearch keeps on disk: what each speeds up, its size and age; build or drop them", IndexesTool.Verb),
            new("status", "sfs_status", "the service's health, the indexes, what is open, the scratch folder, memory", StatusTool.Verb),
            new("job", "sfs_job", "collect or cancel a command the service is still running", JobTool.Verb),
        };

        /// <summary>Runs the verb when it is one of the engine's. False when it isn't.</summary>
        /// <summary>The verb being run, for an error that should name an add-on's command of the same name (GAPS S212).</summary>
        internal static string? CurrentVerb;

        public static bool TryRun(string command, List<string> args)
        {
            if (command == "__index") { IndexJob(args); return true; }
            if (command == "__links") { LinksJob.Run(args); return true; }
            if (command == "__materialpaths") { Console.WriteLine(ReportTools.WarmMaterialPaths()); return true; }
            var verb = Verbs.FirstOrDefault(v => v.Verb == command);
            if (verb == null) return false;
            CurrentVerb = command;
            // Before this program's first SQLite connection (the schema is one)
            EngineLibs.TuneSqlite();
            // --json, taken off the command line with the other global options: the answer's parts as one JSON object
            if (CommandLine.Format == OutputFormat.Json) EngineAnswer.JsonMode = true;
            // Mutagen's one-time start-up, for a verb that reads records (in a worker it ran when the worker started).
            // assets2 reads most records from their bytes: the start-up runs beside its work (GAPS S30). refs2 reads the
            // link index and searches bytes before its first Mutagen read (J29)
            if (verb.Verb is "assets2" or "refs2") SFSearch.Records.MutagenStart.Begin();
            else if (verb.Verb is not ("describe" or "status" or "job" or "indexes")) SFSearch.Records.MutagenStart.Run(); // indexes starts Mutagen itself when it walks
            verb.Run(args);
            return true;
        }

        /// <summary>The exit codes of every new verb, in one sentence (EngineAnswer has the numbers).</summary>
        public const string ExitCodes = "Exit codes of every verb here (\"exitCode\" in the JSON form): 0 answered; 1 answered, and the answer is a failure of what was asked (check2 found errors, a round trip differs, compare records with expect found another difference); 2 no answer: one line, Error: ... Fix: ...; 7 not run: heavy work that wasn't allowed (the line says what it would cost); 8 not enough memory free for heavy work that was allowed.";

        /// <summary>What sfs_run's help prints: every new verb with one line, then what else it runs.</summary>
        public static string HelpText()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("The verbs of the new engine. Each reads the plugin files live: no database is read or built.\n");
            sb.Append("| Verb | Tool | Answers |\n|---|---|---|\n");
            foreach (var v in Verbs) sb.Append($"| {v.Verb} | {v.Tool ?? "(through sfs_run)"} | {v.What} |\n");
            sb.Append("Each verb takes its tool's arguments as options: record Eon --view versions is sfs_record {record: \"Eon\", view: \"versions\"}. <verb> --help shows them; --json gives the answer's parts as one JSON object.\n");
            sb.Append(ExitCodes).Append('\n');
            sb.Append("A verb whose name ends in 2 replaced an older command of the plain name (Query, Check, Refs, LoadOrder, Leveled, Dialogue, Sources, Archive, Assets, Materials, Scripts, Changes). The older commands were removed in SFSearch 2.0: each old name answers with the verb to use.\n");
            if (Extensions.Summary() is { } addOns) sb.Append("Add-on commands run by their name too. ").Append(addOns.TrimEnd()).Append('\n');
            else sb.Append("Add-on commands (extensions\\*.dll in the SFSearch home) run by their name too; none is installed.\n");
            return sb.ToString();
        }

        /// <summary>
        /// __index build | check | sweep: brings the header index up to date and says what it did in one line.
        /// "build" is what the service queues in its indexer lane at start. "check" is the same thing asked again while
        /// the service is idle (every plugin's quick stamp: about 30 ms when nothing changed). "sweep" also hashes every
        /// file again, which is what notices a change in the middle of a file that kept its size and date; it then
        /// removes segment files no plugin uses any more. (How far the indexes are, by hand: the indexes verb.) The service runs one sweep
        /// right after its start's build when the indexes are kept (J31), with --unused-before (its start time, in UTC
        /// ticks): the segments and link files no plugin uses that were written before then are deleted whatever their age. With --history (the service
        /// adds it unless historyLook is off), build, check and sweep then take a look for changes2's history log
        /// (fix S11) and say so on a second line that begins "history:".
        /// </summary>
        public static void IndexJob(List<string> args)
        {
            string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "build";
            if (sub is not ("build" or "check" or "sweep")) { Console.WriteLine("Usage: SFSearch __index build | check | sweep   (the header index's jobs; the service runs them by itself. What is kept and how far it is: indexes)"); return; }
            try
            {
                var eng = EngineIndex.Get();
                var v = eng.Check();
                string removed = "";
                if (sub == "sweep")
                {
                    // Map what is there first: hashing then costs about half a second, with no header pass
                    var full = eng.Check(verifyContent: true);
                    full.Ms += v.Ms;
                    v = full;
                    DateTime? before = null;
                    int at = args.FindIndex(a => a.Equals("--unused-before", StringComparison.OrdinalIgnoreCase));
                    if (at >= 0 && at + 1 < args.Count && long.TryParse(args[at + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long ticks)) before = new DateTime(ticks, DateTimeKind.Utc);
                    // A plugin found changed: this program lets go of its old link file at once (the next link job makes the new one)
                    if (v.Report.AnythingChanged) { EngineLinks.Trim(eng.Index); WorldLinks.Trim(eng.Index); }
                    // keepRemovedPluginIndexMB (J43): the parts of plugins no longer in Data are kept up to that size; over it a
                    // plugin's parts go together, the plugin out of Data longest first (J60: the check notes when it saw each)
                    var cfg = Service.ServiceConfig.Load(Home.Dir, makeFile: false);
                    if (cfg.KeepsRemovedParts)
                    {
                        KeptParts.Record(eng, DateTime.UtcNow);
                        removed = KeptParts.Trim(eng, before, cfg.KeepRemovedPluginIndexMB);
                    }
                    else
                    {
                        KeptParts.Forget(eng);
                        var (segs, segBytes) = eng.RemoveUnusedSegments(before);
                        var (links, linkBytes) = before != null ? EngineLinks.RemoveOldFiles(eng.Index, before) : (0, 0L);
                        // World link files (J32) of plugins that changed or left
                        var (worlds, worldBytes) = before != null ? WorldLinks.RemoveOldFiles(eng.Index, before) : (0, 0L);
                        if (segs + links + worlds > 0) removed = $" Removed as no plugin uses them any more: {segs} segment file(s), {links} link file(s), {(worlds > 0 ? $"{worlds} world link file(s), " : "")}{EngineIndex.Mb(segBytes + linkBytes + worldBytes)}.";
                    }
                }
                var ix = eng.Index;
                long mem;
                using (var me = Process.GetCurrentProcess()) mem = me.PrivateMemorySize64 / 1048576;
                var r = v.Report;
                Console.WriteLine($"header index: {ix.Plugins.Count} plugins, {ix.TotalRecords:N0} records, {EngineIndex.Mb(ix.TotalSegmentBytes)} of segments in {eng.Folder}. "
                    + $"This {sub}: {v.Ms:N0} ms, {r.PassesRun.Count} header pass(es), {r.Hashed.Count} file(s) hashed, {r.SegmentsReused.Count} segment(s) already there, "
                    + $"{r.ContentChanged.Count} changed, {r.Added.Count} added, {r.Removed.Count} removed, {r.Rekeyed.Count} re-keyed{(r.LoadOrderChanged ? ", load order changed" : "")}. "
                    + $"{ix.LoadOrderEntries.Count(e => e.Enabled)} loaded; {ix.Plugins.Count(p => p.Problems.Any)} plugin(s) with problems.{removed} This program holds {mem} MB.");
                foreach (var n in v.Notes) Console.WriteLine("  " + n);
                // The service asks for a look at the history too (2026-11-13, fix S11): changes2 can then say what changed
                // since the service last looked, each change dated near when it happened, not only since the last call
                if (args.Any(a => a.Equals("--history", StringComparison.OrdinalIgnoreCase)) && sub is "build" or "check" or "sweep")
                {
                    try { Console.WriteLine(SFSearch.Reports.ChangesReport.ServiceLook(ix, Path.Combine(EngineLibs.KeptRoot, "history"), eng.DataFolder, "service-" + sub)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or SFSearch.Reports.ReportException)
                    {
                        Console.WriteLine($"history: not looked at this time ({e.Message.Split('\n')[0].TrimEnd('.')}); the next idle check tries again.");
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                Console.WriteLine($"Error: the header index couldn't be brought up to date ({e.Message.TrimEnd('.')}). Fix: check the Data folder and the indexes folder, then run it again.");
                Environment.ExitCode = 1;
            }
        }
    }
}
