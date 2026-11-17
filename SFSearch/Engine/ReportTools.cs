using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Starfield;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Reports;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// The reports of SFSearch.Reports inside SFSearch (milestones M11 and M8): check (tool sfs_check, verb check2),
    /// and the verbs reached through sfs_run: leveled2, dialogue2, sources2, archive2, assets2, materials2, scripts2,
    /// changes2. Each stands on the engine's own header index and record engine. No record data is stored. The small
    /// files two reports keep (decompiled scripts, the history log) go under kept\ in the SFSearch home, because the
    /// scratch folder is emptied at every service start.
    /// </summary>
    public static class ReportTools
    {
        /// <summary>What every report takes besides its own arguments.</summary>
        static readonly string[] Common = { "detail", "max_chars", "cursor", "language" };

        // ---------------------------------------------------------------- The frame every report runs in
        /// <summary>
        /// Runs one report and puts its answer into the engine's: the text in pages with one cursor, or the structured
        /// object when json was asked for; the notes with a plugin's problems first; the sources; what was read.
        /// </summary>
        static void Report(EngineAnswer answer, ToolArgs a, string verb, IEnumerable<string> known, Func<ReportSession, ReportAnswer?> work, Action<ReportAnswer>? after = null) =>
            Report(answer, a, verb, known, (s, _, _) => work(s), after);

        /// <summary>
        /// The same, for a report that pages by what it searched (<see cref="ISearchPage"/>: archive2 grep): the work
        /// gets the cursor's start and the page's characters, and stops once its page is full; its answer is printed
        /// whole, and the [more] line counts what was searched.
        /// </summary>
        static void Report(EngineAnswer answer, ToolArgs a, string verb, IEnumerable<string> known, Func<ReportSession, int, int, ReportAnswer?> work, Action<ReportAnswer>? after = null)
        {
            try
            {
                if (!EngineLibs.Language(a.Text("language"), answer, out var language)) return;
                int maxChars = a.Flag("all") ? AllChars : Math.Max(RecordTool.LeastChars, a.Int("max_chars") ?? RecordTool.DefaultChars);
                string question = verb + "|" + string.Join("|", known.Where(k => k is not ("cursor" or "max_chars" or "allow_heavy")).Select(k => k + "=" + a.Text(k)));
                if (!Cursors.TryRead(a.Text("cursor"), question, out int start))
                { answer.Fail($"the cursor \"{a.Text("cursor")}\" doesn't belong to this question", "pass a cursor together with the same arguments that gave it, or leave it out to start at the top", 2); return; }
                var eng = EngineIndex.Get();
                answer.Checked(eng.Check());
                using var session = Session(eng, language);
                var result = work(session, start, maxChars);
                if (result == null) return; // the answer is already said (an error, or "not run")
                EngineLibs.Parts(answer, result.Notes, result.Sources, result.Read);
                var page = result as ISearchPage;
                if (page != null && (page.NextItem != null || page.FirstItem > 0))
                    answer.More = new MoreInfo
                    {
                        From = (int)page.FirstItem + 1, Shown = (int)page.ItemsSearched, Total = (int)page.ItemsTotal, Unit = page.ItemNoun,
                        Cursor = page.NextItem is { } next ? Cursors.Make((int)next, question) : null,
                    };
                if (EngineAnswer.JsonMode)
                {
                    var o = result.ToJson();
                    o.Remove("notes"); o.Remove("source"); o.Remove("cost");
                    string json = o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    int most = Math.Max(maxChars, 200_000);
                    if (json.Length > most)
                    { answer.Fail($"the structured form of this report is {json.Length:N0} characters, more than the {most:N0} allowed", "ask for the text form, which comes in pages; or narrow the question; or raise max_chars", 2); return; }
                    answer.Line(json);
                }
                else
                {
                    if (page != null)
                    {
                        // The page is what was searched: printed whole
                        foreach (var l in EngineLibs.BodyLines(result.ToText(new ReportTextOptions { Detail = a.Flag("detail"), MaxChars = int.MaxValue }))) answer.Line(l);
                    }
                    else
                    {
                        string text = result.ToText(new ReportTextOptions { Detail = a.Flag("detail"), MaxChars = maxChars, Cursor = start }, out int next);
                        EngineLibs.Paged(answer, text, start, next, question);
                    }
                }
                after?.Invoke(result);
            }
            catch (ReportException e) { answer.Fail(e.Message.Replace("\r", "").Replace("\n", "; "), e.Fix ?? "check the names: find shows what a FormID or EditorID is, loadorder2 lists the plugins"); }
            catch (CompareException e) { answer.Fail(e.Message.Replace("\r", "").Replace("\n", "; "), e.Fix ?? "check the names: find shows what a FormID or EditorID is, loadorder2 lists the plugins"); }
            catch (RecordException e) { answer.Fail(e.Message.Split('\n')[0], "ask again; loadorder2 problems lists what is wrong with a plugin"); }
            catch (FileNotFoundException e) { answer.Fail(e.Message.Split('\n')[0], "check the name or the path; set the Data folder with SetData, or the SFSEARCH_DATA environment variable"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                answer.Fail($"the report couldn't be made ({e.Message.Split('\n')[0].TrimEnd('.')})", "ask again; if it stays, loadorder2 problems shows which plugin is at fault");
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException and not System.Threading.ThreadAbortException)
            {
                RecordTool.FailMutagenReport(answer, e);
            }
            finally
            {
                if (EngineRecords.TrimIfLarge() is { } trimmed) answer.After(trimmed);
            }
        }

        /// <summary>For the service's indexer lane, once per service (fix-s33h): the material paths materials2 tells from what Data names,
        /// gathered and kept in the scratch folder for every worker, unless they are there already. A line for the log.</summary>
        public static string WarmMaterialPaths()
        {
            try
            {
                using var session = Session(EngineIndex.Get(), Mutagen.Bethesda.Strings.Language.English);
                return new MaterialsView(session).WarmPaths();
            }
            catch (ReportException e) { return "material paths: not gathered: " + e.Message; }
        }

        /// <summary>A report session on the engine's index, with the home's kept folder and the Papyrus decompiler when it is there.</summary>
        static ReportSession Session(EngineIndex eng, Mutagen.Bethesda.Strings.Language language)
        {
            var session = EngineLibs.Reports(eng, language);
            session.KeptFolder = EngineLibs.KeptRoot;
            session.Champollion = ScriptsReport.FindChampollion(null, Home.Dir);
            return session;
        }

        static void RunVerb(List<string> args, string help, VerbSpec spec, Action<ToolArgs, EngineAnswer> run)
        {
            if (args.Count == 0 && spec.Positional.Length > 0 && spec.Verb != "changes2" || args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(help); return; }
            var answer = new EngineAnswer();
            var given = spec.Parse(args, answer);
            if (given != null) run(new ToolArgs(given), answer);
            answer.Emit();
        }

        static VerbSpec Spec(string verb, string[] positional, string[] options, string[] flags, Dictionary<string, string>? aliases = null, bool joinRest = true)
        {
            var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["lang"] = "language" };
            foreach (var kv in aliases ?? new()) all[kv.Key] = kv.Value;
            return new VerbSpec { Verb = verb, Positional = positional, Options = options.Concat(new[] { "max_chars", "cursor", "language" }).ToArray(), Flags = flags.Concat(new[] { "detail" }).ToArray(), Aliases = all, JoinRest = joinRest };
        }

        static string[] List(string? text) => text?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();

        // ---------------------------------------------------------------- check
        public const string CheckHelp = """
            check2 <plugin> [--what records|itm|assets|materials|roundtrip|all] [--world] [--beside] [--like-xedit] [--detail]
                   [--max-chars N] [--cursor C] [--all] [--allow-heavy] [--json]
              Is this plugin built right? Read live from the plugin file. No database is read or built.

              --what    records    (default) 19 checks, one PASS, WARN or ERROR line each, then the records each found:
                                   masters, sizes, FormIDs and how the plugin stores its own (the full index 03xxxxxx, the
                                   Small form FE001xxx, the Medium form FD01xxxx: mixed, or a form its size hasn't), links that point at nothing or at the wrong kind of record,
                                   overrides of another type or of nothing, repeated FormKeys and EditorIDs, deleted
                                   records, overrides that change nothing (ITMs), records that can't be read, text stored
                                   in the plugin outside plain ASCII (it reads one way as code page 1252, Mutagen's
                                   reading, and another as UTF-8)
                        itm        only the overrides that change nothing, every one; a row names the record header's numbers
                                   that differ from the master's (form version, version control): xEdit may not call it an ITM
                        assets     every file path on the plugin's records, held against the loose files and the archives in Data
                        materials  the plugin's meshes, their materials and those materials' textures: what is missing
                        roundtrip  read the plugin and write it through Mutagen to the scratch folder, then compare with the file byte for byte.
                                   Nothing is kept after an identical result; on a difference the written copy stays, and the answer says where
                        all        records, assets, materials and roundtrip, one after another
                        (several: --what records,assets)
              --world   include world data (cells, placed references, navmeshes, terrain) in the checks that read records
              --beside  for a plugin file outside Data: read the archives that sit beside it, not the ones in Data
              --like-xedit  count ITMs as SF1Edit does: an override that differs only in what xEdit's "identical to
                        master" leaves aside (unknown or unused bytes, the order of lists the game sorts, a worldspace's
                        OFST/CLSZ, an added XLRL, the same English text) is listed too, and its row names the rule
              --all     every row on one page, no cursor (for scripts). Without it a long answer comes in pages, and a
                        count line says when its page holds only some of the rows it counts
              --allow-heavy  run a check estimated over 10 s or 2 GB. Without it the answer is what it would cost.
              A plugin is a file name in Data or the path of a plugin file.
              Exit code: 0 answered, nothing wrong; 1 answered, and a check found errors (or the round trip differs);
              2 an error (one line, Error: ... Fix: ...); 7 not run: heavy work that wasn't allowed; 8 not enough memory
              free for heavy work that was allowed.
            """;

        static readonly string[] CheckKnown = { "plugin", "what", "world", "beside", "allow_heavy", "all", "like_xedit", "detail", "max_chars", "cursor", "language" };
        static readonly VerbSpec CheckSpec = Spec("check2", new[] { "plugin" }, new[] { "what" }, new[] { "world", "beside", "allow_heavy", "all", "like_xedit" });
        static readonly string[] CheckParts = { "records", "itm", "assets", "materials", "roundtrip" };

        public static void CheckTool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            var answer = new EngineAnswer { AsTool = true };
            if (a.Unknown(CheckKnown) is { Count: > 0 } wrong)
                answer.Fail($"check has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", $"its arguments are: {string.Join(", ", CheckKnown)}", 2);
            else Check(a, answer);
            answer.Emit();
        }

        public static void CheckVerb(List<string> args) => RunVerb(args, CheckHelp, CheckSpec, Check);

        static void Check(ToolArgs a, EngineAnswer answer)
        {
            string? plugin = a.Text("plugin");
            if (plugin == null) { answer.Fail("check needs a plugin", "give plugin: a file name in Data, or the path of a plugin file", 2); return; }
            var parts = new List<string>();
            foreach (string w in List((a.Text("what") ?? "records").ToLowerInvariant()))
            {
                string part = w switch { "checks" or "errors" or "record" => "records", "itms" => "itm", "asset" => "assets", "material" or "textures" => "materials", "round_trip" or "round-trip" => "roundtrip", _ => w };
                if (part == "all") { parts.AddRange(new[] { "records", "assets", "materials", "roundtrip" }); continue; }
                if (!CheckParts.Contains(part)) { answer.Fail($"check has no part named '{w}'", $"what takes: {string.Join(", ", CheckParts)}, all; or several with commas", 2); return; }
                parts.Add(part);
            }
            parts = parts.Distinct().ToList();
            bool allow = a.Flag("allow_heavy"), world = a.Flag("world"), beside = a.Flag("beside"), detail = a.Flag("detail"), xedit = a.Flag("like_xedit");
            if (parts.Count == 1 && parts[0] is "records" or "assets" or "materials")
            {
                // One report: the library's own pages, with its heading on every page
                string part = parts[0];
                Report(answer, a, "check", CheckKnown, s => One(s, part, plugin, world, beside, allow, answer, xedit), r => { if (r is CheckAnswer { Failed: true }) Environment.ExitCode = 1; });
                return;
            }
            // Several parts, or one the engine words itself (itm, roundtrip): every part whole, one after another, cut by lines
            try
            {
                if (!EngineLibs.Language(a.Text("language"), answer, out var language)) return;
                int maxChars = a.Flag("all") ? AllChars : Math.Max(RecordTool.LeastChars, a.Int("max_chars") ?? RecordTool.DefaultChars);
                string question = "check|" + string.Join("|", CheckKnown.Where(k => k is not ("cursor" or "max_chars" or "allow_heavy")).Select(k => k + "=" + a.Text(k)));
                if (!Cursors.TryRead(a.Text("cursor"), question, out int offset))
                { answer.Fail($"the cursor \"{a.Text("cursor")}\" doesn't belong to this question", "pass a cursor together with the same arguments that gave it, or leave it out to start at the top", 2); return; }
                var eng = EngineIndex.Get();
                answer.Checked(eng.Check());
                using var session = Session(eng, language);
                var lines = new List<string>();
                bool failed = false;
                int itmCount = -1; (int First, int Count) itmRows = (-1, 0);
                foreach (string part in parts)
                {
                    if (parts.Count > 1) lines.Add($"## {part}");
                    if (part == "roundtrip")
                    {
                        var side = session.Side(plugin);
                        var rt = RoundTrip(eng, side, allow, answer);
                        if (rt == null) return;
                        lines.AddRange(rt.Value.Lines);
                        failed |= !rt.Value.Identical;
                        answer.Source(CompareSession.SourceLine(side));
                        continue;
                    }
                    var result = One(session, part == "itm" ? "records" : part, plugin, world, beside, allow, answer, xedit);
                    if (result == null) return;
                    EngineLibs.Parts(answer, result.Notes, result.Sources, result.Read);
                    if (part == "itm" && result is CheckAnswer c)
                    {
                        var itm = c.Findings.FirstOrDefault(f => f.Id.Contains("itm", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("identical", StringComparison.OrdinalIgnoreCase));
                        itmCount = lines.Count;
                        lines.Add($"Overrides of {c.Plugin} that change nothing (ITMs{(xedit ? ", as SF1Edit counts them" : "")}): {itm?.Rows.Count ?? 0:N0} of the {c.OverridesCompared:N0} overrides compared with their master ({c.Overrides:N0} overrides in all).");
                        // World data left out is said right under the count, the same way every tool says it (WorldData.LeftOut)
                        if (c.WorldLeftOut.Values.Sum() is int leftOut and > 0)
                            lines.Add(WorldData.LeftOut("was left out of the ITM check", leftOut, c.WorldBytesLeftOut, 0, $"about {2 + leftOut * CheckReport.WorldSecondsPerRecord:F0} s more", ByType(c.WorldLeftOut), "the checks read each record"));
                        if (itm?.NotRun != null) lines.Add($"Not run: {itm.NotRun}");
                        else if (itm == null || itm.Rows.Count == 0) lines.Add("None: every override compared changes something.");
                        else { if (itm.Note != null) lines.Add("  " + itm.Note); itmRows = (lines.Count, itm.Rows.Count); lines.AddRange(itm.Rows.Select(r => r.Line())); }
                        continue;
                    }
                    failed |= result is CheckAnswer { Failed: true };
                    lines.AddRange(EngineLibs.BodyLines(result.ToText(new ReportTextOptions { Detail = detail, MaxChars = int.MaxValue })));
                }
                // The ITM count line says when this page holds only some of its rows (GAPS S48: a count above 121 rows read as the rows shown)
                if (itmCount >= offset && itmRows.Count > 0) SayRowsShown(lines, itmCount, itmRows.First, itmRows.Count, offset, maxChars);
                answer.Lines(lines, 0, offset, maxChars, n => Cursors.Make(n, question));
                if (failed) Environment.ExitCode = 1;
            }
            catch (ReportException e) { answer.Fail(e.Message.Replace("\r", "").Replace("\n", "; "), e.Fix ?? "check the plugin's name: loadorder2 lists the plugins"); }
            catch (CompareException e) { answer.Fail(e.Message.Replace("\r", "").Replace("\n", "; "), e.Fix ?? "check the plugin's name: loadorder2 lists the plugins"); }
            catch (RecordException e) { answer.Fail(e.Message.Split('\n')[0], "ask again; loadorder2 problems lists what is wrong with a plugin"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                answer.Fail($"the check couldn't be made ({e.Message.Split('\n')[0].TrimEnd('.')})", "ask again; if it stays, loadorder2 problems shows which plugin is at fault");
            }
            finally
            {
                if (EngineRecords.TrimIfLarge() is { } trimmed) answer.After(trimmed);
            }
        }

        /// <summary>What max_chars becomes with all (command line --all): every row on one page.</summary>
        const int AllChars = int.MaxValue / 4;

        /// <summary>How many lines from offset one page of EngineAnswer.Lines holds (its own rule: whole lines up to maxChars, at least one).</summary>
        static int LinesOnPage(IReadOnlyList<string> lines, int offset, int maxChars)
        {
            int used = 0, shown = 0;
            for (int i = offset; i < lines.Count; i++)
            {
                if (shown > 0 && used + lines[i].Length + 1 > maxChars) break;
                used += Math.Min(lines[i].Length, maxChars) + 1; shown++;
            }
            return shown;
        }

        /// <summary>
        /// When the page that holds a count line doesn't hold all the rows it counts, the count line says so: "This page
        /// shows 118 of the 199 rows; …". Without it, "199 ITMs" above 118 rows reads like the rows shown (GAPS S48).
        /// </summary>
        static void SayRowsShown(List<string> lines, int countLine, int firstRow, int rows, int offset, int maxChars)
        {
            string plain = lines[countLine];
            for (int round = 0; round < 3; round++)
            {
                int onPage = LinesOnPage(lines, offset, maxChars);
                int shown = Math.Clamp(offset + onPage - firstRow, 0, rows);
                string said = shown >= rows ? plain
                    : plain + $" This page shows {shown:N0} of the {rows:N0} rows; the cursor on the [more] line gives the rest (command line: --all prints every row on one page).";
                if (said == lines[countLine]) return;
                lines[countLine] = said;
            }
        }

        /// <summary>The largest world types left out: "REFR 30, NAVM 4".</summary>
        static string ByType(Dictionary<string, int> left) =>
            string.Join(", ", left.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {k.Value:N0}")) + (left.Count > 6 ? ", …" : "");

        /// <summary>One part of check that is a report of the library. Null when the answer is already said ("not run").</summary>
        static ReportAnswer? One(ReportSession session, string part, string plugin, bool world, bool beside, bool allow, EngineAnswer answer, bool likeXEdit = false)
        {
            switch (part)
            {
                case "assets": return new AssetsReport(session).Assets(plugin, new AssetsOptions { World = world, Beside = beside });
                case "materials": return new MaterialsReport(session).Materials(plugin, new MaterialsOptions { Beside = beside });
                default:
                {
                    // What reading the plugin's records costs, from the header index: measured on Starfield.esm (notes M11a)
                    var side = session.Side(plugin);
                    var seg = side.Plugin.Segment;
                    if (seg != null)
                    {
                        var (worldRecords, _) = WorldTypes.Totals(seg);
                        long read = seg.Count - worldRecords + (world ? worldRecords : 0);
                        double seconds = read * CheckReport.WorldSecondsPerRecord;
                        long memory = 400 + (long)((seg.Count - worldRecords) * 0.003) + (world ? (long)(worldRecords * 0.0003) : 0);
                        if (!EngineLibs.Heavy(answer, allow, seconds, memory, $"The checks of {side.Name} read {read:N0} of its {seg.Count:N0} records{(world ? ", world data included" : "")}", noIndex: "the checks read every record they check")) return null;
                    }
                    return new CheckReport(session).Check(plugin, new CheckOptions { World = world, LikeXEdit = likeXEdit });
                }
            }
        }

        // ---------------------------------------------------------------- The round trip
        /// <summary>
        /// Reads a plugin and writes it through Mutagen into the scratch folder, then compares the two files byte for
        /// byte. Mutagen reads the whole plugin into memory for this, so a big plugin goes through the heavy lane.
        /// Null when the answer is already said ("not run").
        /// </summary>
        static (List<string> Lines, bool Identical)? RoundTrip(EngineIndex eng, PluginSide side, bool allow, EngineAnswer answer)
        {
            var p = side.Plugin;
            double mb = p.Stamp.Length / 1048576.0;
            // Measured (notes M12): 26 MB took 2 s and 362 MB; 1,390 MB took 68 s and 13,650 MB
            if (!EngineLibs.Heavy(answer, allow, 2 + mb * 0.05, 200 + (long)(mb * 10), $"The round trip of {side.Name} ({EngineIndex.Mb(p.Stamp.Length)}) reads the whole plugin into memory and writes it again", noIndex: "a round trip reads and writes the whole plugin")) return null;
            // A folder of this program's own: two workers may make the round trip of one plugin at the same time
            string folder = Path.Combine(EngineLibs.ScratchRoot, "roundtrip", $"{Path.GetFileNameWithoutExtension(side.Name)}-{p.ContentKey.Short}-{Environment.ProcessId}");
            string written = Path.Combine(folder, side.Name);
            Directory.CreateDirectory(folder);
            if (File.Exists(written)) File.Delete(written);
            var clock = Stopwatch.StartNew();
            var lines = new List<string>();
            string mutagen = (typeof(StarfieldMod).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault()
                as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "?";
            try
            {
                // Masters from the plugin's own header, their sizes from the Data folder: how a patcher opens it
                using var mod = StarfieldMod.Create(StarfieldRelease.Starfield).FromPath(side.ReadPath).WithLoadOrderFromHeaderMasters().WithDataFolder(eng.DataFolder).Construct();
                // Written as read: the header's counts (records, interior cells) and next FormID are the file's own, not
                // recounted, so a plugin whose header count is stale still compares equal when Mutagen holds all of it
                mod.BeginWrite.ToPath(written).WithLoadOrderFromHeaderMasters().WithDataFolder(eng.DataFolder)
                    .WithRecordCount(Mutagen.Bethesda.Plugins.Binary.Parameters.RecordCountOption.NoCheck)
                    .NoNextFormIDProcessing()
                    .WriteAsync().GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                var cause = e; while (cause.InnerException != null) cause = cause.InnerException;
                lines.Add($"Round trip of {side.Name}: NOT DONE. Mutagen {mutagen} couldn't read and write it: {cause.GetType().Name}: {cause.Message.Split('\n')[0].TrimEnd('.')}.");
                Remove(folder);
                answer.Read($"{side.Name} whole, through Mutagen {mutagen} ({clock.Elapsed.TotalSeconds:0.0} s)");
                return (lines, false);
            }
            // The plugin was in memory whole: give that back before anything else is read
            if (mb > 16) EngineLibs.GiveBackMemory();
            long bytes = new FileInfo(side.ReadPath).Length, writtenBytes = new FileInfo(written).Length;
            long at = FirstDifference(side.ReadPath, written);
            long peak;
            using (var me = Process.GetCurrentProcess()) peak = me.PeakWorkingSet64 / 1048576;
            answer.Read($"{side.Name} whole, then its copy as Mutagen {mutagen} wrote it ({clock.Elapsed.TotalSeconds:0.0} s; this program's peak {peak:N0} MB)");
            if (at < 0)
            {
                lines.Add($"Round trip of {side.Name}: IDENTICAL. Read and written through Mutagen {mutagen}, the copy equals the file byte for byte: {bytes:N0} bytes, {p.Segment?.Count ?? 0:N0} records.");
                // Nothing is kept after an identical result: the copy, and the string files Mutagen writes beside a localized plugin
                Remove(folder);
                return (lines, true);
            }
            lines.Add($"Round trip of {side.Name}: DIFFERENT. Read and written through Mutagen {mutagen}, the copy differs from the file: the first different byte is at {at:N0} (0x{at:X}). The file has {bytes:N0} bytes, the copy {writtenBytes:N0}.");
            // Which record the first different byte is in, from the header index
            var seg = p.Segment;
            if (seg != null)
            {
                int inside = -1;
                for (int i = 0; i < seg.Count; i++)
                {
                    var e = seg[i];
                    if (at >= e.Offset && at < e.Offset + RawRecord.HeaderLength + e.Size) { inside = i; break; }
                }
                if (inside >= 0)
                {
                    var info = side.Index.Describe(new RecordRef(p.Id, inside));
                    lines.Add($"  The first difference is in {ProblemNotes.Label(info)}, a {info.Signature}, {at - info.Offset:N0} bytes into the record. record with view raw shows its bytes, view verify checks that one record.");
                }
                else lines.Add("  The first difference is outside every record: in the plugin's header, or in a group's header.");
            }
            bool strings = Directory.Exists(Path.Combine(folder, "Strings"));
            // Kept only on a difference: the copy, and the string files Mutagen writes beside a localized plugin
            lines.Add(strings
                ? $"  The written copy and its string files (in Strings\\ beside it) are kept for a closer look until the service starts again: {written}"
                : $"  The written copy is kept for a closer look until the service starts again: {written}");
            return (lines, false);

            // The folder of this round trip with everything in it, and the roundtrip folder itself when that leaves it empty
            static void Remove(string folder)
            {
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                    string? parent = Path.GetDirectoryName(folder);
                    if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>The first byte position where two files differ (their common length when one is the start of the other), or -1.</summary>
        static long FirstDifference(string a, string b)
        {
            using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
            byte[] ba = new byte[1 << 20], bb = new byte[1 << 20];
            long pos = 0;
            while (true)
            {
                int na = fa.ReadAtLeast(ba, ba.Length, false), nb = fb.ReadAtLeast(bb, bb.Length, false);
                int n = Math.Min(na, nb);
                int at = ba.AsSpan(0, n).CommonPrefixLength(bb.AsSpan(0, n));
                if (at < n) return pos + at;
                if (na != nb) return pos + n;
                if (n == 0) return -1;
                pos += n;
            }
        }

        // ---------------------------------------------------------------- leveled, dialogue, sources
        public const string LeveledHelp = """
            leveled2 <list> [--in <plugin>] [--depth N] [--detail] [--max-chars N] [--cursor C] [--json]
              What a leveled list can give: every entry with its level, count, chance of nothing and conditions, and the
              lists under it. Each list is read from the version the game uses; with --in, from that plugin.
              <list> is a FormKey (0032EE:Starfield.esm) or an EditorID. --depth: how many lists deep (default 8).
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...).
            """;
        static readonly string[] LeveledKnown = { "list", "in", "depth" };
        static readonly VerbSpec LeveledSpec = Spec("leveled2", new[] { "list" }, new[] { "in", "depth" }, Array.Empty<string>(), new() { ["plugin"] = "in" });

        public static void LeveledVerb(List<string> args) => RunVerb(args, LeveledHelp, LeveledSpec, (a, answer) =>
        {
            if (a.Text("list") is not { } list) { answer.Fail("leveled2 needs a leveled list", "give its FormKey or EditorID", 2); return; }
            Report(answer, a, "leveled", LeveledKnown.Concat(Common), s => new LeveledReport(s).Leveled(list, new LeveledOptions { In = a.Text("in"), Depth = a.Int("depth") is > 0 and var d ? d : 8 }));
        });

        public const string DialogueHelp = """
            dialogue2 <quest or topic> [--in <plugin>] [--language <language>] [--detail] [--max-chars N] [--cursor C] [--json]
              What a quest's (or one topic's) characters say: topics and lines with the speaker, the text, the conditions,
              the scene a line starts and the quest stage it sets. Each line is read from the version the game uses; with
              --in, only that plugin's topics and lines. <quest or topic> is a FormKey or an EditorID.
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...).
            """;
        static readonly string[] DialogueKnown = { "quest", "in" };
        static readonly VerbSpec DialogueSpec = Spec("dialogue2", new[] { "quest" }, new[] { "in" }, Array.Empty<string>(), new() { ["plugin"] = "in", ["topic"] = "quest" });

        public static void DialogueVerb(List<string> args) => RunVerb(args, DialogueHelp, DialogueSpec, (a, answer) =>
        {
            if (a.Text("quest") is not { } quest) { answer.Fail("dialogue2 needs a quest or a topic", "give its FormKey or EditorID", 2); return; }
            Report(answer, a, "dialogue", DialogueKnown.Concat(Common), s => new DialogueReport(s).Dialogue(quest, new DialogueOptions { In = a.Text("in"), Language = a.Text("language") }));
        });

        public const string SourcesHelp = """
            sources2 <plugin> [--types WEAP,ARMO] [--item <record>] [--with <plugin,...>] [--only] [--depth N] [--scan]
                     [--allow-heavy | --allow-heavy=save-index] [--detail] [--max-chars N] [--cursor C] [--json]
              How the player gets each item a plugin adds: a recipe, a quest, a vendor, a container, a placed copy, an NPC,
              through leveled lists, form lists and outfits. Each item says whether any path ends at something the player
              can reach.
              --types   the item types (default WEAP,ARMO)          --item   one item only
              --with    more plugins to search                      --only   don't search the plugins that have this one as a master
              --depth   how many lists deep (default 6)
              --scan    find "what points at this" by reading every record of the searched plugins, not from the link index.
                        The answer is the same; it is how the two are held against each other.
              Placed copies come from a plugin's world link index when it has one (indexes build world), and from a
              search of its placed references' bytes when it hasn't: seconds for Starfield.esm's 3.29 million. The answer
              names a plugin with more than 300,000 placed references searched so, and --allow-heavy=save-index searches
              the same way and then saves the world link index of those plugins (made after the answer), so later answers
              take their placed copies from it. Only when that search would take over 10 s (deeper lists, more plugins)
              does the answer leave them out and say so; --allow-heavy then searches them.
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...).
            """;
        static readonly string[] SourcesKnown = { "plugin", "types", "item", "with", "only", "depth", "scan" };
        static readonly VerbSpec SourcesSpec = Spec("sources2", new[] { "plugin" }, new[] { "types", "item", "with", "depth" }, new[] { "only", "scan" }, new() { ["sig"] = "types" });

        public static void SourcesVerb(List<string> args) => RunVerb(args, SourcesHelp, SourcesSpec, (a, answer) =>
        {
            if (a.Text("plugin") is not { } plugin) { answer.Fail("sources2 needs a plugin", "give a plugin's file name in Data, or the path of a plugin file", 2); return; }
            bool scan = a.Flag("scan");
            LinkIndexBackLinks? links = null;
            Report(answer, a, "sources", SourcesKnown.Concat(Common), s => new SourcesReport(s).Sources(plugin, new SourcesOptions
            {
                Signatures = a.Text("types") == null ? null : List(a.Text("types")), Item = a.Text("item"), With = List(a.Text("with")),
                Dependents = !a.Flag("only"), Depth = a.Int("depth") is > 0 and var d ? d : 6,
                // "What points at this" from the link index (the same one refs answers from), unless --scan asks for the walk
                BackLinks = scan ? (scope, searched) => new ScanBackLinks(s, scope, searched) : (scope, searched) => links = new LinkIndexBackLinks(s, scope, searched) { Passes = (a.Int("depth") is > 0 and var dp ? dp : 6) + 3 },
            }), after: _ =>
            {
                // allow_heavy="save_index" (J33, GAPS S96): the world link index of the big plugins whose placed references were searched by their bytes
                if (!JobContext.SaveIndexAsked) return;
                if (links?.SaveOffer() is { } offer) IndexOffers.Take(answer, offer);
                else IndexOffers.NothingToSave(answer, scan ? "--scan reads every record" : "every plugin searched has its world link index already, or none has more than 300,000 placed references");
            });
        });

        // ---------------------------------------------------------------- archive, assets, materials, scripts, changes
        public const string ArchiveHelp = """
            archive2 <plugin | file.ba2 | all> [list [<pattern>] | find <text> | show <path> | extract <pattern> --to <folder>
                     | grep <text> [--regex] [--ext mat,nif] [--folder materials] [--count] [--allow-heavy]
                     | encoding [--ext strings,txt] [--folder strings] [--count] [--allow-heavy]]
                     [--beside] [--detail] [--all] [--max-chars N] [--cursor C] [--json]
              What is inside BA2 archives. With a plugin: its own archives (<plugin name> - <part>.ba2). "all": every archive in Data.
              (nothing)  a summary per archive: files, size, kinds of file, files that also exist loose in Data
              list       the files whose path fits a pattern. A pattern with * or ? must fit the whole path (*.mat, meshes\*);
                         any other text is looked for anywhere in the path
              find       the same over every archive in Data: archive2 all find <text>
              show       one file's content as text (.mat, .psc, .json, ...)
              extract    copies the files that fit into a folder (--to), never into the game's folders
              grep       which files hold a text: the contents of the archives' files, and of the loose files in Data
                         the game reads in their place (all: every loose file in Data's folders), searched for the
                         text (letter case doesn't count) or, with --regex, a .NET regular expression (^ and $: a
                         line's start and end; \A: the file's first byte). Each hit with its archive, file, byte
                         offset (and line, in a text file) and the text around it. Use it to find which mod's file
                         names a material, texture, mesh, sound or script string. A text file that starts with a
                         UTF-16 byte-order mark (the interface's translate_*.txt) is searched as UTF-16 text. A text
                         or regex with a character outside ASCII reads the other files as UTF-8 text (a byte that
                         isn't UTF-8 as Windows-1252, as some string tables hold), so GRÖSSE also finds grösse.
                         An ASCII regex matches the bytes. A character written as a code matches only itself:
                         \xE9 is the byte E9, not C9 (its capital in Latin-1); the letters around it ignore case.
                         Textures, sounds and video are passed over unless --ext names them (--ext * searches all).
                         --ext     only these kinds        --folder  only files under these folders (both take commas)
                         --count   no hits listed: the hits and the files that hold them per kind of file, over the
                                   whole scope (with --detail, per archive too): "how many meshes name this?"
                         The files and inflated bytes it covers, and the time that takes, are worked out before
                         anything is read; over 10 s it says so ("Not run") and runs with --allow-heavy. It stops
                         once the page is full: the cursor goes on from the next file. Nothing is kept between calls.
              encoding   how each file's text is written: ASCII only, UTF-8, Windows-1252, mixed UTF-8 and
                         Windows-1252, UTF-16 (by its byte-order mark) or binary (a control byte); a string table
                         (.strings, .dlstrings, .ilstrings) string by string, with the strings that aren't UTF-8.
                         Use it before editing a string table or a text file. Lists the files with text outside
                         ASCII (--detail: every file and string); --ext, --folder, --count and the cost as for grep.
              --beside   for a plugin file outside Data: the archives that sit beside it, not the ones in Data
              --all      every row on one page, no cursor (for scripts); grep then searches the whole scope at once
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...); 7 not run (heavy, not allowed).
            """;
        static readonly string[] ArchiveKnown = { "target", "action", "pattern", "to", "beside", "list", "find", "show", "extract", "grep", "regex", "ext", "folder", "allow_heavy", "count", "all" };
        static readonly VerbSpec ArchiveSpec = Spec("archive2", new[] { "target", "action", "pattern" }, new[] { "to", "list", "find", "show", "extract", "grep", "ext", "folder" }, new[] { "beside", "regex", "allow_heavy", "count", "all" });

        public static void ArchiveVerb(List<string> args) => RunVerb(args, ArchiveHelp, ArchiveSpec, (a, answer) =>
        {
            if (a.Text("target") is not { } target) { answer.Fail("archive2 needs a plugin, an archive or \"all\"", "give a plugin's name, a .ba2 file, or all", 2); return; }
            string action = (a.Text("action") ?? "").ToLowerInvariant();
            string? pattern = a.Text("pattern");
            // The runner's spelling works too: --list <pattern>, --find <text>, --show <path>, --extract <pattern>
            foreach (string opt in new[] { "list", "find", "show", "extract", "grep" })
                if (a.Text(opt) is { } v) { action = opt; pattern = v; }
            bool beside = a.Flag("beside");
            if (action.Length > 0 && action is not ("list" or "find" or "show" or "extract" or "grep" or "encoding" or "summary"))
            { answer.Fail($"archive2 has no action '{action}'", "after the target comes nothing (a summary), list [<pattern>], find <text>, show <path>, extract <pattern> --to <folder>, grep <text>, or encoding", 2); return; }
            if (action is "find" or "show" or "extract" or "grep" && string.IsNullOrEmpty(pattern)) { answer.Fail($"archive2 {action} needs {(action == "show" ? "a file's path" : action == "grep" ? "a text to look for in the files" : "a text or a pattern")}", $"archive2 {target} {action} <{(action == "show" ? "path" : action == "grep" ? "text" : "pattern")}>", 2); return; }
            if (action == "encoding" && !string.IsNullOrEmpty(pattern)) { answer.Fail("archive2 encoding takes no text", $"name the files with --ext and --folder: archive2 {target} encoding --ext strings", 2); return; }
            if (action == "extract" && a.Text("to") == null) { answer.Fail("archive2 extract needs a folder to copy into", "add --to <folder>: any folder outside the game's folders", 2); return; }
            if (action != "grep" && a.Flag("regex")) { answer.Fail("--regex belongs to archive2 grep", $"archive2 {target} grep <text> --regex; or leave it out", 2); return; }
            if (action is not ("grep" or "encoding") && (a.Text("ext") != null || a.Text("folder") != null || a.Flag("count")))
            { answer.Fail("--ext, --folder and --count belong to archive2 grep and encoding", $"archive2 {target} grep <text> --ext mat; or leave them out", 2); return; }
            if (action == "encoding")
            {
                Report(answer, a, "archive", ArchiveKnown.Concat(Common), (s, start, maxChars) =>
                {
                    var report = new ArchiveReport(s);
                    bool count = a.Flag("count");
                    using var plan = report.PlanEncodings(target, new GrepOptions
                    {
                        Text = "", Extensions = List(a.Text("ext")), Folders = List(a.Text("folder")), Beside = beside, Start = count ? 0 : start, Count = count,
                    });
                    if (!EngineLibs.Heavy(answer, a.Flag("allow_heavy"), plan.Seconds, plan.MemoryMB, plan.What)) return null;
                    return count ? report.EncodingsCount(plan) : report.Encodings(plan, maxChars, a.Flag("detail"));
                });
                return;
            }
            if (action == "grep")
            {
                Report(answer, a, "archive", ArchiveKnown.Concat(Common), (s, start, maxChars) =>
                {
                    var report = new ArchiveReport(s);
                    // A count reads the whole scope and pages its rows (the cursor is a row); a list pages by the files searched
                    bool count = a.Flag("count");
                    using var plan = report.PlanGrep(target, new GrepOptions
                    {
                        Text = pattern!, Regex = a.Flag("regex"), Extensions = List(a.Text("ext")), Folders = List(a.Text("folder")), Beside = beside, Start = count ? 0 : start, Count = count,
                    });
                    // The cost first: over the heavy limits the answer is the estimate, and nothing is read
                    if (!EngineLibs.Heavy(answer, a.Flag("allow_heavy"), plan.Seconds, plan.MemoryMB, plan.What, noIndex: "it reads the archives' files, and SFSearch indexes plugins, not archives")) return null;
                    return count ? report.GrepCount(plan) : report.Grep(plan, maxChars, a.Flag("detail"));
                });
                return;
            }
            Report(answer, a, "archive", ArchiveKnown.Concat(Common), s =>
            {
                var report = new ArchiveReport(s);
                return action switch
                {
                    "list" => report.List(target, pattern, beside),
                    "find" => report.List(target, pattern, beside),
                    "show" => report.Show(target, pattern!, beside),
                    "extract" => report.Extract(target, pattern!, Home.UserPath(a.Text("to")!), beside),
                    _ => report.Summary(target, beside),
                };
            });
        });

        public const string AssetsHelp = """
            assets2 <plugin> [--world] [--beside] [--detail] [--max-chars N] [--cursor C] [--json]
              Every file path on a plugin's records (meshes, materials, textures, sounds, ...), held against the loose files
              and the archives in Data: what is missing. The same report is check2 <plugin> --what assets.
              --world   include world data's records     --beside  for a plugin file outside Data: the archives beside it
              Exit code: 0 answered (missing files are in the answer, not in the exit code); 2 an error (one line, Error: ... Fix: ...).
            """;
        static readonly VerbSpec AssetsSpec = Spec("assets2", new[] { "plugin" }, Array.Empty<string>(), new[] { "world", "beside" });

        public static void AssetsVerb(List<string> args) => RunVerb(args, AssetsHelp, AssetsSpec, (a, answer) =>
        {
            if (a.Text("plugin") is not { } plugin) { answer.Fail("assets2 needs a plugin", "give a plugin's file name in Data, or the path of a plugin file", 2); return; }
            // In a new process: what the report needs first is made on other cores while Mutagen starts (GAPS S30)
            if (!SessionServer.Active)
            {
                string? data = null;
                try { data = Home.DataFolder(); } catch (FileNotFoundException) { }
                if (data != null) AssetsReport.PrepareInBackground(() => EngineRecords.Schema, data, plugin);
            }
            Report(answer, a, "assets", new[] { "plugin", "world", "beside" }.Concat(Common), s => new AssetsReport(s).Assets(plugin, new AssetsOptions { World = a.Flag("world"), Beside = a.Flag("beside") }));
        });

        public const string MaterialsHelp = """
            materials2 <plugin> [--beside] [--detail] [--max-chars N] [--cursor C] [--json]
              The materials a plugin's meshes (the .nif files in its own archives) and records name, and those materials'
              textures: what is missing. A material that is no .mat file is looked up in the compiled material databases
              (materialsbeta.cdb: the game's, the plugin's, other mods'); one in none of them is missing. A compiled
              material's textures are read from its database (read whole once per process) and checked too. The databases
              that count are the game's own and any .cdb in a plugin's " - Main" archives.
              The same report is check2 <plugin> --what materials.
            materials2 --material <path.mat> [<plugin>] [--all-paths]      materials2 --texture <path or text> [<plugin>] [--all-paths]
              --material  one material by its path: what holds it (a .mat file, loose or in an archive; each compiled
                          database, and whether the game reads it), and what it is made of, as a tree: its layers, each
                          layer's material and texture set with every texture (found or missing), its blenders, UV
                          streams and LOD materials, and its parent material, with the names the format gives them. A
                          .mat file is shown the same way; what it takes from its parent is marked. When more than
                          one place holds the path, it says which copy the game most likely uses, and that only a
                          test in the game proves it.
              --texture   the materials that use a texture: the compiled ones, in every database, and the .mat files
                          (loose or in any archive) that name it themselves. A path (matched whole), a pattern with
                          * or ? (fits the whole path), or a text looked for anywhere in the path.
              --all-paths a compiled database knows a material only by hashes of its path. Without this, a path is told
                          from the database's own texture folders, or from what the service gathered while idle;
                          the rest are shown by name and ID. With it, the material paths every .mat file, mesh
                          header, archive folder and plugin record in Data names are gathered now if they aren't
                          kept (several seconds), and kept in the scratch folder for every later call (gathered
                          again when an archive or a plugin changes).
              With a plugin, files are looked for in its archives first.
              Exit code: 0 answered (missing files are in the answer, not in the exit code); 2 an error (one line, Error: ... Fix: ...).
            """;
        static readonly VerbSpec MaterialsSpec = Spec("materials2", new[] { "plugin" }, new[] { "material", "texture" }, new[] { "beside", "all_paths" });

        public static void MaterialsVerb(List<string> args) => RunVerb(args, MaterialsHelp, MaterialsSpec, (a, answer) =>
        {
            string? plugin = a.Text("plugin");
            var known = new[] { "plugin", "material", "texture", "beside", "all_paths" }.Concat(Common);
            string? material = a.Text("material"), texture = a.Text("texture");
            if (material != null && texture != null) { answer.Fail("materials2 takes --material or --texture, not both", "ask about one material, or about the materials that use one texture", 2); return; }
            if (material != null) { Report(answer, a, "materials", known, s => new MaterialsView(s) { AllPaths = a.Flag("all_paths") }.Material(material, plugin, a.Flag("beside"))); return; }
            if (texture != null) { Report(answer, a, "materials", known, s => new MaterialsView(s) { AllPaths = a.Flag("all_paths") }.Texture(texture, plugin, a.Flag("beside"))); return; }
            if (plugin == null) { answer.Fail("materials2 needs a plugin", "give a plugin's file name in Data, or the path of a plugin file; or --material <path> for one material, --texture <path> for the materials that use a texture", 2); return; }
            Report(answer, a, "materials", new[] { "plugin", "beside" }.Concat(Common), s => new MaterialsReport(s).Materials(plugin, new MaterialsOptions { Beside = a.Flag("beside") }));
        });

        public const string ScriptsHelp = """
            scripts2 <plugin[,plugin]> [--find <text>] [--show <script>] [--calls] [--no-records] [--world | --no-world] [--beside]
                     [--detail] [--max-chars N] [--cursor C] [--json]
              A plugin's Papyrus scripts as source: each attached by which records, or attached to nothing; the scripts
              its records attach that are missing, and those its scripts use (a parent, a library they call, a type
              they name) that are missing; and the code that reaches records without a link in a record
              (GetFormFromFile, GetForm, IsPluginInstalled, and AddItem, AddForm, PlaceAtMe ... on what they return).
              Its scripts are those in its archives, loose ones with its name in their path, and loose ones its records
              attach. The compiled scripts are decompiled once with Champollion; the sources are kept under kept\scripts
              in the SFSearch home. Champollion.exe is found by SFSEARCH_CHAMPOLLION, else "champollion" in sfsearch.json,
              else SFSearch's tools\champollion folder ('sfs.cmd Service config' shows which is set).
              <plugin>  names parted by commas, a name with * (sfta*.esm), a set (@official, @loaded, @all), or the path
                        of a plugin file
              --find    every line that contains a text      --show   one script's source
              --calls   only the code that reaches records, every call (needs no decompiler)
              --no-records  don't look for the records that carry each script
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...).
            """;
        static readonly VerbSpec ScriptsSpec = Spec("scripts2", new[] { "plugins" }, new[] { "find", "show" }, new[] { "calls", "no_records", "world", "no_world", "beside" }, new() { ["plugin"] = "plugins" });

        public static void ScriptsVerb(List<string> args) => RunVerb(args, ScriptsHelp, ScriptsSpec, (a, answer) =>
        {
            if (a.Text("plugins") is not { } plugins) { answer.Fail("scripts2 needs a plugin", "give one or more plugin names, with commas between them, or a set such as @official", 2); return; }
            Report(answer, a, "scripts", new[] { "plugins", "find", "show", "calls", "no_records", "world", "no_world", "beside" }.Concat(Common), s => new ScriptsReport(s).Scripts(plugins, new ScriptsOptions
            {
                Find = a.Text("find"), Show = a.Text("show"), Calls = a.Flag("calls"), Carriers = !a.Flag("no_records"), World = a.Flag("world") ? true : a.Flag("no_world") ? false : null, Beside = a.Flag("beside"),
            }));
        });

        // ---------------------------------------------------------------- profile (GAPS S40)
        public const string ProfileHelp = """
            profile <Type.Path ...> [--in <plugins>] [--samples N] [--allow-heavy] [--detail] [--max-chars N] [--cursor C] [--json]
            profile --unknown [--type <type>] [--in <plugins>] ...
            profile --groups [--in <plugins>] [--type CELL] [--group 0x2A3F0] [--list] ...
            profile --conditions [--in <plugins>] [--type <type>] [--function <index or name>] ...
            profile --subrecords STRD[,XTV2] [--in <plugins>] [--type <type>] ...
            profile --subrecords UNAM,BNAM,PNAM|* --order [--type PACK] [--in <plugins>] ...
            profile --subrecords * [--type CELL] [--in <plugins>] ...
            profile --subrecords BFCB --values [--type <type>] [--in <plugins>] ...
            profile --subrecords X,Y|* --lengths [--type <type>] [--in <plugins>] ...
            profile --formids [--type <type>] [--in <plugins>] ...
              What a field holds across many plugins, in one call: for each field, the records and values each plugin has;
              the distinct values, the most common ones and the share of zeros; for 4-byte values (and each 4 bytes of a run
              of bytes) how many read as a float (1e-4 to 1e6), as the FormID of a record in Data (read with the plugin's own
              masters; and which record types), and as a strings ID of the plugin's strings files; samples, each with its
              record (EditorID, FormKey), where in it, and the scalar values beside the field.
              Values are Mutagen's, read through the record layer along the schema's path. Nothing is stored.
              Link fields are read from the files' bytes on all cores where the bytes give them (the answer is the
              same; only the samples' records are read through Mutagen), and a world type's link field from the
              world link index when every plugin read has one (indexes build world; null links aren't kept there).

              fields    Type.Path, parted by spaces or commas: the record type (Mutagen name or signature) and the schema
                        path (describe <type> lists them). Nested paths work: Weapon.Components[*]{AdaptiveTriggerDataComponent}.AdsEffect.Unknown;
                        left without its {Kind}, a path gives each kind that has the field. A point's part: Npc.Something.X.
              --unknown every field the schema marks unknown: of --type, or of every record type (heavy over Starfield.esm)
              --in      the plugins: names parted by commas, a name with *, @official (the game's own plugins and Bethesda's
                        Creations: 33 here), @loaded, @winners (each record only in the version the game uses; the default),
                        @all (every plugin in Data). A plugin's name or an @set among the fields is taken as --in.
              --samples samples shown per field (default 3; the JSON has up to 12)
              Over 10 fields, one table row each; --detail gives each in full (every plugin, 25 values, 12 samples).
              Heavy work (over 10 s or 2 GB) says its cost first; --allow-heavy runs it.

              --groups  the GRUP headers instead: each group's label (record type, FormID with its record, block number,
                        grid), group type, stamp, version, the Unknown at bytes 20-23, size, and the records and groups right
                        inside it. For a set of plugins: each group type's counts, the Unknown's values and the plugins that
                        set it (default --in @loaded); for one plugin (or --list) one row per group.
                        --type CELL: only that top group. --group 0x2A3F0: that group (its position) and the groups inside it.

              --conditions  the condition census: every condition (CTDA) of every record that can hold one (or of --type),
                        counted by its function (index and name): conditions, records, plugins, record types, samples.
                        --function 961 (or a name) gives that function alone: its plugins, record types and samples.
                        The rows themselves, one per condition, are SQL's Conditions view (query2).

              --subrecords  which record types hold these subrecords, from the files' bytes (default --in @official): for
                        each, the records that hold it (of the type's records), how many times, the plugins, a sample;
                        beside the record types whose fields the schema puts in it.
              --order   the orders these subrecords come in instead, per record type: each record's subrecords in file
                        order, the ones named ("*" for all) with any others between as …, a unit repeated in a row as
                        (A B)+; records grouped by their order, with plugins, a sample, and how many times each unit
                        repeats (least-most in a row, in all). From the files' bytes, on all cores.
              --subrecords * (no --order): what a type's records hold, from the files' bytes on all cores: records,
                        bytes in the file and as read; every subrecord (records that hold it, times, bytes, largest);
                        every link field (links, distinct per record, distinct targets, records with one), at its
                        fixed place or walked as Mutagen lays it out (traversals, navmeshes, VMAD, components, …);
                        a link field the walk can't reach is named, with the profile call that counts it.
              --values  the distinct values of the subrecords named, from the files' bytes on all cores (which component
                        kinds exist: --subrecords BFCB --values; which values a subrecord no field covers takes): per
                        subrecord and where it stands (the record, or the component, BFCB ... BFCE, by its BFCB text),
                        each value as text ("...") when it is one, else hex, with uses, records, record types, plugins,
                        a sample record.
              --lengths how long the subrecords named (or * for all) are, by record type and signature, from the files'
                        bytes on all cores (inside components too): always empty (end markers, marker fields), one
                        length, varying, or sometimes empty and sometimes with data; the empty ones with records,
                        plugins, where they stand (the record or a component) and records with an empty one and with data.
              --formids how the records' FormIDs are stored, from the files' bytes on all cores: each record header's
                        FormID and every link, as a master's record (full index, FD, FE), the plugin's own record (full
                        index, FD, FE), past the master list, or a form its size doesn't give (FE outside a Small plugin,
                        FD outside a Medium one); by plugin and record type, and where the unusual ones are, with sample
                        records. Finds the plugins that store FormIDs oddly before you patch or merge them.
              A link field's values (fields) come with what they point at: the distinct records, their record types (by
              distinct records and by uses), null links, links to records no plugin in Data holds, the most used targets;
              several link fields asked together are counted together too.
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...); 7 not run (heavy, not allowed).
            """;
        /// <summary>Why no saved index makes a profile faster (J33): it counts values read from the records themselves. A world
        /// type's link field is the exception (GAPS S99): the world link index holds its links, and profile counts them from it.</summary>
        const string ProfileNoIndex = "a profile counts the values it reads from the records themselves";

        /// <summary>A world type's link field counted from the world link index (GAPS S99): Starfield.esm's placed objects'
        /// Base, 3,291,860 records, in about half a second.</summary>
        const double ProfileWorldLaterSeconds = 0.5;
        const string ProfileWorldLater = "the census of a world type's link field then comes from the index, the records' postings, in about half a second";

        static readonly string[] ProfileKnown = { "fields", "in", "type", "unknown", "groups", "group", "list", "samples", "allow_heavy", "conditions", "function", "subrecords", "order", "values", "lengths", "formids" };
        static readonly VerbSpec ProfileSpec = Spec("profile", new[] { "fields" }, new[] { "in", "type", "group", "samples", "function", "subrecords" }, new[] { "unknown", "groups", "list", "allow_heavy", "conditions", "order", "values", "lengths", "formids" }, new() { ["plugins"] = "in", ["plugin"] = "in" });

        public static void ProfileVerb(List<string> args) => RunVerb(args, ProfileHelp, ProfileSpec, Profile);

        /// <summary>profile: fields across plugins (ProfileReport), or the GRUP headers (GroupsReport).</summary>
        public static void Profile(ToolArgs a, EngineAnswer answer)
        {
            var words = (a.Text("fields") ?? "").Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            string? plugins = a.Text("in");
            // A plugin's name or a set among the fields: the plugins to read
            var named = words.Where(w => w.StartsWith('@') || w.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || w.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || w.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count > 0)
            {
                if (plugins != null) { answer.Fail($"plugins were named twice: {string.Join(", ", named)} among the fields, and --in {plugins}", "name them once, with --in", 2); return; }
                plugins = string.Join(",", named);
                words.RemoveAll(named.Contains);
            }
            bool groups = a.Flag("groups"), unknown = a.Flag("unknown"), conditions = a.Flag("conditions");
            int samples = a.Int("samples") ?? 3;
            if (groups && (words.Count > 0 || unknown)) { answer.Fail("--groups shows the GRUP headers, and fields were named too", "ask for the groups alone, or for fields alone", 2); return; }
            if (!groups && a.Text("group") != null) { answer.Fail("--group names a group of the GRUP-header view", "add --groups", 2); return; }
            string? subrecords = a.Text("subrecords");
            bool formids = a.Flag("formids");
            if ((groups ? 1 : 0) + (conditions ? 1 : 0) + (subrecords != null ? 1 : 0) + (formids ? 1 : 0) > 1) { answer.Fail("more than one of --groups, --conditions, --subrecords and --formids was given", "ask for one of them", 2); return; }
            if ((subrecords != null || formids) && (words.Count > 0 || unknown)) { answer.Fail($"{(formids ? "--formids" : "--subrecords")} was given with fields", $"ask for {(formids ? "the FormIDs" : "the subrecords")} alone, or for fields alone", 2); return; }
            bool order = a.Flag("order"), values = a.Flag("values"), lengths = a.Flag("lengths");
            if (order && subrecords == null) { answer.Fail("--order gives the order of subrecords, and no --subrecords was given", "name them: --subrecords UNAM,BNAM,PNAM --order --type PACK (or --subrecords * for every subrecord)", 2); return; }
            if (values && subrecords == null) { answer.Fail("--values gives the values of subrecords, and no --subrecords was given", "name them: --subrecords BFCB --values", 2); return; }
            if (values && order) { answer.Fail("--values and --order were both given", "ask for the values or for the order of the subrecords", 2); return; }
            if (lengths && subrecords == null) { answer.Fail("--lengths gives the lengths of subrecords, and no --subrecords was given", "name them, or every one: --subrecords * --lengths", 2); return; }
            if (lengths && (order || values)) { answer.Fail($"--lengths and {(order ? "--order" : "--values")} were both given", "ask for one of them", 2); return; }
            if (!groups && !unknown && !conditions && !formids && subrecords == null && words.Count == 0) { answer.Fail("profile got no field", "give fields as Type.Path (Weapon.AttackDamage), --unknown with --type, or --groups (profile --help)", 2); return; }
            IndexOffer? worldOffer = null;
            Report(answer, a, "profile", ProfileKnown.Concat(Common), s =>
            {
                if (groups) return new GroupsReport(s).Groups(new GroupsOptions { In = plugins, Type = a.Text("type"), Group = a.Text("group"), List = a.Flag("list") });
                if (formids)
                {
                    var fr = new FormIdsReport(s);
                    var fo = new SubrecordsOptions { Signatures = "", In = plugins, Type = a.Text("type") };
                    var (fsec, fmem, fwhat) = fr.Estimate(fo);
                    if (!EngineLibs.Heavy(answer, a.Flag("allow_heavy"), fsec, fmem, fwhat, noIndex: ProfileNoIndex)) return null;
                    return fr.Census(fo);
                }
                if (subrecords != null)
                {
                    var sr = new SubrecordsReport(s);
                    var so = new SubrecordsOptions { Signatures = subrecords, In = plugins, Type = a.Text("type") };
                    if (lengths)
                    {
                        var lr = new SubrecordLengthsReport(s);
                        var (lsec, lmem, lwhat) = lr.Estimate(so);
                        if (!EngineLibs.Heavy(answer, a.Flag("allow_heavy"), lsec, lmem, lwhat, noIndex: ProfileNoIndex)) return null;
                        return lr.Lengths(so);
                    }
                    if (values)
                    {
                        var vr = new SubrecordValuesReport(s);
                        var (vsec, vmem, vwhat) = vr.Estimate(so);
                        if (!EngineLibs.Heavy(answer, a.Flag("allow_heavy"), vsec, vmem, vwhat, noIndex: ProfileNoIndex)) return null;
                        return vr.Values(so);
                    }
                    if (!order && (subrecords.Trim() == "*" || subrecords.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)))
                    {
                        var stats = new TypeStatsReport(s);
                        var (ssec, smem, swhat) = stats.Estimate(so);
                        if (!EngineLibs.Heavy(answer, a.Flag("allow_heavy"), ssec, smem, swhat, noIndex: ProfileNoIndex)) return null;
                        return stats.Stats(so);
                    }
                    if (order)
                    {
                        var orders = new SubrecordOrderReport(s);
                        var (osec, omem, owhat) = orders.Estimate(so);
                        if (!EngineLibs.Heavy(answer, a.Flag("allow_heavy"), osec, omem, owhat, noIndex: ProfileNoIndex)) return null;
                        return orders.Orders(so);
                    }
                    var (sec, mem, what) = sr.Estimate(so);
                    if (!EngineLibs.Heavy(answer, a.Flag("allow_heavy"), sec, mem, what, noIndex: ProfileNoIndex)) return null;
                    return sr.Census(so);
                }
                var report = new ProfileReport(s);
                var plan = report.Plan(new ProfileOptions { Fields = words, Unknown = unknown, Type = a.Text("type"), In = plugins, Samples = samples, Conditions = conditions, Function = a.Text("function"), World = new WorldLinkCensus() });
                // A world type's link field the world link index would answer (GAPS S99): the third answer offers to save it
                worldOffer = plan.WorldPlugins is { } wp ? IndexOffers.World(wp, ProfileWorldLaterSeconds, ProfileWorldLater) : null;
                if (!EngineLibs.Heavy(answer, a.Flag("allow_heavy"), plan.Seconds, plan.MemoryMB, plan.What, worldOffer, noIndex: worldOffer == null ? ProfileNoIndex : null)) return null;
                return report.Run(plan, samples);
            }, after: _ =>
            {
                // allow_heavy="save_index" (J33): the world link index of the plugins the fields were read from
                if (JobContext.SaveIndexAsked && worldOffer != null) IndexOffers.Take(answer, worldOffer);
            });
        }

        public const string ChangesHelp = """
            changes2 [<plugin>] [--since 2026-11-01] [--all] [--detail] [--max-chars N] [--cursor C] [--json]
              What changed in Data since last time: plugins added, removed, back and updated, and the patches that are at
              risk because a master of theirs changed. With a plugin: its history, and what its last update added and removed.
              The history is a small log under kept\history in the SFSearch home. It grows by looks: this call, and the
              service by itself at its start and at each idle check (historyLook and pluginCheckSeconds in sfsearch.json:
              on, every 10 minutes, by default). The answer
              says which look it compares with; a change is dated by the look that first saw it, "now" by this call.
              The first look begins the history and reports nothing.
              --since   changes from this day on (default: the last 30 days)     --all   every change in the log
              The same report is loadorder2 changes.
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...).
            """;
        static readonly VerbSpec ChangesSpec = Spec("changes2", new[] { "plugin" }, new[] { "since" }, new[] { "all" });

        public static void ChangesVerb(List<string> args) => RunVerb(args, ChangesHelp, ChangesSpec, Changes);

        /// <summary>The changes report: changes2, and loadorder's view "changes".</summary>
        public static void Changes(ToolArgs a, EngineAnswer answer)
        {
            DateTime? since = null;
            if (a.Text("since") is { } text)
            {
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day))
                { answer.Fail($"since takes a day, not '{text}'", "write it as 2026-11-01", 2); return; }
                since = day;
            }
            Report(answer, a, "changes", new[] { "plugin", "since", "all" }.Concat(Common), s => new ChangesReport(s).Changes(new ChangesOptions { Plugin = a.Text("plugin"), Since = since, All = a.Flag("all") }));
            // What is wrong now (missing masters, load order, overrides of another type) is in the problems view
            if (!answer.Failed) answer.Note("Every problem the header index sees in Data now (a missing master, a master that loads late, overrides of a different type than their base) is in the problems view: sfs_loadorder view \"problems\" (command line: loadorder2 problems).");
        }
    }

    /// <summary>
    /// "What points at this record" for the sources report, from the link index (milestone M8). The link index says
    /// which records of a plugin point at a target; each is then read and walked by the report library's own walker,
    /// so the places are exactly the ones a scan of every record gives. Placed references aren't in the link index
    /// (it leaves world data out): for each record asked about, the placed references whose bytes hold its FormID are
    /// found by a byte search and only those are read (GAPS S70). A searched plugin that has no link file yet is
    /// answered by the library's search of its bytes (<see cref="ByteBackLinks"/>).
    /// A plugin with a world link index (J32) gives its placed copies from it: the placed references that link to the
    /// record, whatever their number (GAPS S96). One without it has its placed references searched by their bytes, the
    /// big ones (more than <see cref="PlacedLimit"/>) too, by the user's decision (GAPS S100, 2026-11-15: "Yes, include
    /// them"): the note says how long that took and offers to save their world link index (allow_heavy="save_index").
    /// Only when that search would itself be heavy (over 10 s by its estimate: a pass over their bytes for each level of
    /// lists) are they left out unless the caller allowed it, with the three answers (J33).
    /// </summary>
    public sealed class LinkIndexBackLinks : IBackLinks
    {
        readonly ReadScope _scope;
        readonly List<(RecordEngine Engine, IndexedPlugin Plugin, LinkFile File)> _indexed = new();
        readonly ByteBackLinks? _bytes;
        readonly List<string> _plugins;
        readonly HolderLinks _links;
        readonly List<string> _notes = new();
        readonly Dictionary<string, IReadOnlyList<BackLink>> _answers = new(StringComparer.OrdinalIgnoreCase);
        // The placed references of the indexed plugins that hold a record's FormID, by their bytes
        ByteHolders? _placed;
        int _placedHits, _unread, _lookups;
        double _seconds;
        static readonly HashSet<string> Placed = new(StringComparer.Ordinal) { "REFR", "ACHR" };
        // The world link index of each indexed plugin that has one (J32): its placed copies come from it
        readonly WorldLinkFile?[] _world;
        int _worldPlugins, _worldHits;
        // Plugins over the limit with no world link index: left out (not allowed), or searched by their bytes (allowed)
        readonly List<(IndexedPlugin P, int Placed, long Bytes)> _bigLeft = new(), _bigSearched = new();

        /// <summary>Placed copies with the world link index: the lookup, then the few placed copies read (J32 note: refs
        /// from it in 0.1 to 0.2 s).</summary>
        const double LaterSeconds = 0.2;
        const string Later = "placed copies of an item then come from it in a fraction of a second, whatever the plugin's size";

        /// <summary>A plugin with more placed references than this and no world link index is named in the note: its bytes
        /// are searched for placed copies (seconds), and its world link index would answer in a fraction of a second.</summary>
        public int PlacedLimit { get; init; } = 300_000;
        /// <summary>The passes over the big plugins' bytes the question needs at most: one for the items, one for each level
        /// of lists and one for the containers of the last (the report asks about each level of every item together). The
        /// default is the report's: depth 6 (Eon: 9 passes).</summary>
        public int Passes { get; init; } = 9;

        public LinkIndexBackLinks(ReportSession session, ReadScope scope, IReadOnlyList<(RecordEngine Engine, IndexedPlugin Plugin)> plugins)
        {
            _scope = scope; _links = new HolderLinks(session);
            _plugins = plugins.Select(p => p.Plugin.Name).ToList();
            var rest = new List<(RecordEngine Engine, IndexedPlugin Plugin)>();
            foreach (var (engine, p) in plugins)
            {
                var file = p.Segment != null && p.Problems.MissingMasters.Count == 0 ? EngineLinks.For(p) : null;
                if (file != null) _indexed.Add((engine, p, file)); else rest.Add((engine, p));
            }
            _world = _indexed.Select(x => WorldLinks.For(x.Plugin)).ToArray();
            if (rest.Count > 0)
            {
                _bytes = new ByteBackLinks(session, scope, rest);
                _notes.Add($"{rest.Count:N0} of the {plugins.Count:N0} plugins searched have no link file yet ({string.Join(", ", rest.Take(4).Select(r => r.Plugin.Name))}{(rest.Count > 4 ? ", …" : "")}): their bytes were searched for each record asked about instead, and the records that hold its FormID read. The answer is the same.");
            }
        }

        public IReadOnlyList<string> Plugins => _plugins;
        public IReadOnlyList<string> Notes
        {
            get
            {
                var placed = PreparePlaced();
                var notes = new List<string>(_notes);
                if (_worldPlugins > 0) notes.Add($"Placed copies came from the world link index of {_worldPlugins:N0} plugin{(_worldPlugins == 1 ? "" : "s")} ({_worldHits:N0} placed reference(s) found), not from a search of their bytes.");
                if (_bigLeft.Count > 0) notes.Add(BigLeftNote());
                if (_bigSearched.Count > 0) notes.Add(BigSearchedNote(placed));
                if (_unread > 0) notes.Add($"{_unread:N0} placed reference(s) hold the FormID of a record asked about but couldn't be read: their links weren't taken.");
                if (placed.NotInflated > 0) notes.Add($"{placed.NotInflated:N0} compressed placed reference(s) couldn't be inflated and weren't searched.");
                return _bytes == null ? notes : notes.Concat(_bytes.Notes).ToList();
            }
        }
        public string Cost =>
            $"the link index of {_indexed.Count:N0} plugin{(_indexed.Count == 1 ? "" : "s")} ({_lookups:N0} lookup{(_lookups == 1 ? "" : "s")}), {_links.Read:N0} record{(_links.Read == 1 ? "" : "s")} to place the links, {_placed?.Records ?? 0:N0} placed reference{(_placed?.Records == 1 ? "" : "s")} (their bytes searched in {_placed?.Passes ?? 0:N0} pass{(_placed?.Passes == 1 ? "" : "es")}, {_placedHits:N0} hits, {_seconds + (_placed?.Seconds ?? 0):F1} s)"
            + (_worldPlugins > 0 ? $", the world link index of {_worldPlugins:N0} plugin{(_worldPlugins == 1 ? "" : "s")} for placed copies ({_worldHits:N0} found)" : "")
            + (_bytes != null ? ", " + _bytes.Cost : "");

        /// <summary>The placed references of the indexed plugins, from the header index: the link index leaves world data out.</summary>
        ByteHolders PreparePlaced()
        {
            if (_placed != null) return _placed;
            var entries = new int[]?[_indexed.Count];
            for (int pi = 0; pi < _indexed.Count; pi++)
            {
                var (_, p, _) = _indexed[pi];
                var seg = p.Segment!;
                int placed = seg.Signatures.Where(x => Placed.Contains(x.Signature)).Sum(x => x.Count);
                if (placed == 0) continue;
                if (_world[pi] is { } wf)
                {
                    // From the world link index (Into); only the placed references its build couldn't read are searched by their bytes
                    _worldPlugins++;
                    var unread = wf.Unread.ToArray().Where(i => i < seg.FirstUnresolved && Placed.Contains(seg.SignatureOf(seg[i]))).OrderBy(i => i).ToArray();
                    if (unread.Length > 0) entries[pi] = unread;
                    continue;
                }
                if (placed > PlacedLimit) _bigSearched.Add((p, placed, seg.Signatures.Where(x => Placed.Contains(x.Signature)).Sum(x => x.Bytes)));
                var list = new List<int>(placed);
                foreach (string sig in Placed)
                    foreach (int i in seg.EntriesOf(sig))
                        if (i < seg.FirstUnresolved) list.Add(i);
                list.Sort();
                entries[pi] = list.ToArray();
            }
            // The big ones' search is heavy only when its estimate is (a pass over their bytes for each level of lists):
            // then they are left out unless allowed, with the three answers
            if (_bigSearched.Count > 0 && BigSeconds(_bigSearched) > EngineLibs.HeavySeconds && !JobContext.HeavyAllowed)
            {
                _bigLeft.AddRange(_bigSearched);
                foreach (var (p, _, _) in _bigSearched) entries[_indexed.FindIndex(x => x.Plugin == p)] = null;
                _bigSearched.Clear();
            }
            return _placed = new ByteHolders(_indexed.Select(x => x.Plugin).ToList(), entries, _scope);
        }

        /// <summary>Seconds a pass over these plugins' placed references takes by their bytes: about 1 GB a second on 8
        /// threads (S100: 770 MB in 0.7 s a pass on a quiet PC).</summary>
        static double PassSeconds(List<(IndexedPlugin P, int Placed, long Bytes)> big) => Math.Max(0.5, big.Sum(b => b.Bytes) / 1.0e9);
        double BigSeconds(List<(IndexedPlugin P, int Placed, long Bytes)> big) => PassSeconds(big) * Passes;

        /// <summary>The big plugins searched by their bytes (the default, S100): what that took, and the index that would
        /// answer it in a fraction of a second next time (J33's save_index).</summary>
        string BigSearchedNote(ByteHolders placed)
        {
            long n = _bigSearched.Sum(b => (long)b.Placed), bytes = _bigSearched.Sum(b => b.Bytes);
            string head = $"{Names(_bigSearched)} {(_bigSearched.Count == 1 ? "has" : "have")} more than {PlacedLimit:N0} placed references ({n:N0}, {EngineIndex.Mb(bytes)}) and no world link index: "
                + $"their bytes were searched for placed copies with the other plugins' ({placed.Passes:N0} pass{(placed.Passes == 1 ? "" : "es")} over {placed.Records:N0} placed references, {placed.Seconds:F1} s).";
            if (JobContext.SaveIndexAsked) return head;
            var offer = IndexOffers.World(_bigSearched.Select(b => b.P), LaterSeconds, Later);
            return offer == null ? head : head + " " + HeavyAnswers.SaveLine(offer, "allow_heavy=\"save_index\"", "--allow-heavy=save-index", "search them the same way", "To make this faster next time, call again with");
        }

        static string Names(List<(IndexedPlugin P, int Placed, long Bytes)> big) =>
            string.Join(", ", big.Take(6).Select(b => b.P.Name)) + (big.Count > 6 ? $" and {big.Count - 6} more" : "");

        /// <summary>
        /// The big plugins left out because searching them would be heavy, with the three answers (J33): search their
        /// placed references now by their bytes (about a second a pass over them; a pass for each level of lists), save
        /// the world link index of them too, or leave them out.
        /// </summary>
        string BigLeftNote()
        {
            long placed = _bigLeft.Sum(b => (long)b.Placed), bytes = _bigLeft.Sum(b => b.Bytes);
            double pass = PassSeconds(_bigLeft);
            string head = $"{Names(_bigLeft)} {(_bigLeft.Count == 1 ? "has" : "have")} more than {PlacedLimit:N0} placed references ({placed:N0}, {EngineIndex.Mb(bytes)}) and no world link index: "
                + $"searching their bytes for placed copies would take about {Math.Ceiling(BigSeconds(_bigLeft)):0} s here (up to {Passes:N0} passes, one for each level of lists), over {EngineLibs.HeavySeconds:0} s, so they weren't searched, and an item placed in {(_bigLeft.Count == 1 ? "it" : "them")} isn't listed as one.";
            var offer = IndexOffers.World(_bigLeft.Select(b => b.P), LaterSeconds, Later);
            return head + HeavyAnswers.Options(offer, "placed copies are found by their bytes", "allow_heavy=true", "--allow-heavy",
                "allow_heavy=\"save_index\"", "--allow-heavy=save-index", $"search them now by their bytes (about {pass:0.#} s a pass over them, a pass for each level of lists)", "search them now the same way");
        }

        /// <summary>The world link index of the big plugins whose placed references were searched by their bytes (null when
        /// none was): what allow_heavy="save_index" saves after the answer.</summary>
        public IndexOffer? SaveOffer()
        {
            PreparePlaced();
            return IndexOffers.World(_bigSearched.Select(b => b.P), LaterSeconds, Later);
        }

        /// <summary>The records asked about next: their placed references, and the plugins with no link file, are searched for together.</summary>
        public void Prefetch(IEnumerable<string> formKeys)
        {
            var keys = formKeys.Where(k => !_answers.ContainsKey(ReportSession.Canon(k))).ToList();
            PreparePlaced().Prefetch(keys);
            _bytes?.Prefetch(keys);
        }

        public IReadOnlyList<BackLink> Into(string formKey)
        {
            string key = ReportSession.Canon(formKey);
            if (_answers.TryGetValue(key, out var have)) return have;
            var placedHolders = PreparePlaced();
            var result = new List<BackLink>();
            if (HeaderIndex.TryParseFormKey(key, out var owner, out var localId))
            {
                var clock = Stopwatch.StartNew();
                _lookups++;
                // The placed references that hold the target's FormID, by their bytes
                var placedHits = placedHolders.Of(key);
                for (int pi = 0; pi < _indexed.Count; pi++)
                {
                    var (engine, p, file) = _indexed[pi];
                    var seg = p.Segment!;
                    int o = seg.OwnerOf(owner);
                    if (o < 0) continue; // the plugin doesn't know the target's plugin, so it can't point at it
                    var holders = new List<int>();
                    var (first, end) = file.Range((ulong)o << 24 | localId);
                    var links = file.Links;
                    for (int i = first; i < end; i++) { int entry = (int)links[i].From; if (holders.Count == 0 || holders[^1] != entry) holders.Add(entry); }
                    // Placed copies from the plugin's world link index (J32, GAPS S96): the placed references that link to the record
                    if (_world[pi] is { } wf)
                    {
                        var (wfirst, wend) = wf.Range((ulong)o << 24 | localId);
                        var postings = wf.Postings;
                        int before = holders.Count;
                        for (int i = wfirst; i < wend; i++)
                        {
                            int entry = (int)postings[i].From;
                            if (Placed.Contains(seg.SignatureOf(seg[entry]))) holders.Add(entry);
                        }
                        _worldHits += holders.Count - before;
                    }
                    // Records the link walk couldn't read through Mutagen can't be read here either: the scan counts them as not read
                    var placed = placedHits[pi];
                    if (placed != null) { holders.AddRange(placed); _placedHits += placed.Count; }
                    if (holders.Count == 0) continue;
                    _scope.Touch(engine, p.Name);
                    _links.ReadAll(engine, p, holders);
                    foreach (int entry in holders.Distinct().OrderBy(x => x))
                        if (!_links.Into(engine, p, entry, key, result) && placed != null && placed.BinarySearch(entry) >= 0) _unread++;
                }
                _seconds += clock.Elapsed.TotalSeconds;
            }
            if (_bytes != null) result.AddRange(_bytes.Into(formKey));
            return _answers[key] = result;
        }
    }
}
