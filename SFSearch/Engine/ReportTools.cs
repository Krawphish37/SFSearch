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
        static void Report(EngineAnswer answer, ToolArgs a, string verb, IEnumerable<string> known, Func<ReportSession, ReportAnswer?> work, Action<ReportAnswer>? after = null)
        {
            try
            {
                if (!EngineLibs.Language(a.Text("language"), answer, out var language)) return;
                int maxChars = Math.Max(RecordTool.LeastChars, a.Int("max_chars") ?? RecordTool.DefaultChars);
                string question = verb + "|" + string.Join("|", known.Where(k => k is not ("cursor" or "max_chars" or "allow_heavy")).Select(k => k + "=" + a.Text(k)));
                if (!Cursors.TryRead(a.Text("cursor"), question, out int start))
                { answer.Fail($"the cursor \"{a.Text("cursor")}\" doesn't belong to this question", "pass a cursor together with the same arguments that gave it, or leave it out to start at the top", 2); return; }
                var eng = EngineIndex.Get();
                answer.Checked(eng.Check());
                using var session = Session(eng, language);
                var result = work(session);
                if (result == null) return; // the answer is already said (an error, or "not run")
                EngineLibs.Parts(answer, result.Notes, result.Sources, result.Read);
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
                    string text = result.ToText(new ReportTextOptions { Detail = a.Flag("detail"), MaxChars = maxChars, Cursor = start }, out int next);
                    EngineLibs.Paged(answer, text, start, next, question);
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
            check2 <plugin> [--what records|itm|assets|materials|roundtrip|all] [--world] [--beside] [--detail]
                   [--max-chars N] [--cursor C] [--allow-heavy] [--json]
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
              --allow-heavy  run a check estimated over 10 s or 2 GB. Without it the answer is what it would cost.
              A plugin is a file name in Data or the path of a plugin file.
              Exit code: 0 answered, nothing wrong; 1 answered, and a check found errors (or the round trip differs);
              2 an error (one line, Error: ... Fix: ...); 7 not run: heavy work that wasn't allowed; 8 not enough memory
              free for heavy work that was allowed.
            """;

        static readonly string[] CheckKnown = { "plugin", "what", "world", "beside", "allow_heavy", "detail", "max_chars", "cursor", "language" };
        static readonly VerbSpec CheckSpec = Spec("check2", new[] { "plugin" }, new[] { "what" }, new[] { "world", "beside", "allow_heavy" });
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
            bool allow = a.Flag("allow_heavy"), world = a.Flag("world"), beside = a.Flag("beside"), detail = a.Flag("detail");
            if (parts.Count == 1 && parts[0] is "records" or "assets" or "materials")
            {
                // One report: the library's own pages, with its heading on every page
                string part = parts[0];
                Report(answer, a, "check", CheckKnown, s => One(s, part, plugin, world, beside, allow, answer), r => { if (r is CheckAnswer { Failed: true }) Environment.ExitCode = 1; });
                return;
            }
            // Several parts, or one the engine words itself (itm, roundtrip): every part whole, one after another, cut by lines
            try
            {
                if (!EngineLibs.Language(a.Text("language"), answer, out var language)) return;
                int maxChars = Math.Max(RecordTool.LeastChars, a.Int("max_chars") ?? RecordTool.DefaultChars);
                string question = "check|" + string.Join("|", CheckKnown.Where(k => k is not ("cursor" or "max_chars" or "allow_heavy")).Select(k => k + "=" + a.Text(k)));
                if (!Cursors.TryRead(a.Text("cursor"), question, out int offset))
                { answer.Fail($"the cursor \"{a.Text("cursor")}\" doesn't belong to this question", "pass a cursor together with the same arguments that gave it, or leave it out to start at the top", 2); return; }
                var eng = EngineIndex.Get();
                answer.Checked(eng.Check());
                using var session = Session(eng, language);
                var lines = new List<string>();
                bool failed = false;
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
                    var result = One(session, part == "itm" ? "records" : part, plugin, world, beside, allow, answer);
                    if (result == null) return;
                    EngineLibs.Parts(answer, result.Notes, result.Sources, result.Read);
                    if (part == "itm" && result is CheckAnswer c)
                    {
                        var itm = c.Findings.FirstOrDefault(f => f.Id.Contains("itm", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("identical", StringComparison.OrdinalIgnoreCase));
                        lines.Add($"Overrides of {c.Plugin} that change nothing (ITMs): {itm?.Rows.Count ?? 0:N0} of the {c.OverridesCompared:N0} overrides compared with their master ({c.Overrides:N0} overrides in all).");
                        // World data left out is said right under the count, the same way every tool says it (WorldData.LeftOut)
                        if (c.WorldLeftOut.Values.Sum() is int leftOut and > 0)
                            lines.Add(WorldData.LeftOut("was left out of the ITM check", leftOut, c.WorldBytesLeftOut, 0, $"about {2 + leftOut * CheckReport.WorldSecondsPerRecord:F0} s more", ByType(c.WorldLeftOut)));
                        if (itm?.NotRun != null) lines.Add($"Not run: {itm.NotRun}");
                        else if (itm == null || itm.Rows.Count == 0) lines.Add("None: every override compared changes something.");
                        else { if (itm.Note != null) lines.Add("  " + itm.Note); lines.AddRange(itm.Rows.Select(r => r.Line())); }
                        continue;
                    }
                    failed |= result is CheckAnswer { Failed: true };
                    lines.AddRange(EngineLibs.BodyLines(result.ToText(new ReportTextOptions { Detail = detail, MaxChars = int.MaxValue })));
                }
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

        /// <summary>The largest world types left out: "REFR 30, NAVM 4".</summary>
        static string ByType(Dictionary<string, int> left) =>
            string.Join(", ", left.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {k.Value:N0}")) + (left.Count > 6 ? ", …" : "");

        /// <summary>One part of check that is a report of the library. Null when the answer is already said ("not run").</summary>
        static ReportAnswer? One(ReportSession session, string part, string plugin, bool world, bool beside, bool allow, EngineAnswer answer)
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
                        if (!EngineLibs.Heavy(answer, allow, seconds, memory, $"The checks of {side.Name} read {read:N0} of its {seg.Count:N0} records{(world ? ", world data included" : "")}")) return null;
                    }
                    return new CheckReport(session).Check(plugin, new CheckOptions { World = world });
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
            if (!EngineLibs.Heavy(answer, allow, 2 + mb * 0.05, 200 + (long)(mb * 10), $"The round trip of {side.Name} ({EngineIndex.Mb(p.Stamp.Length)}) reads the whole plugin into memory and writes it again")) return null;
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
                mod.BeginWrite.ToPath(written).WithLoadOrderFromHeaderMasters().WithDataFolder(eng.DataFolder).WriteAsync().GetAwaiter().GetResult();
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
                     [--detail] [--max-chars N] [--cursor C] [--json]
              How the player gets each item a plugin adds: a recipe, a quest, a vendor, a container, a placed copy, an NPC,
              through leveled lists, form lists and outfits. Each item says whether any path ends at something the player
              can reach.
              --types   the item types (default WEAP,ARMO)          --item   one item only
              --with    more plugins to search                      --only   don't search the plugins that have this one as a master
              --depth   how many lists deep (default 6)
              --scan    find "what points at this" by reading every record of the searched plugins, not from the link index.
                        The answer is the same; it is how the two are held against each other.
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...).
            """;
        static readonly string[] SourcesKnown = { "plugin", "types", "item", "with", "only", "depth", "scan" };
        static readonly VerbSpec SourcesSpec = Spec("sources2", new[] { "plugin" }, new[] { "types", "item", "with", "depth" }, new[] { "only", "scan" }, new() { ["sig"] = "types" });

        public static void SourcesVerb(List<string> args) => RunVerb(args, SourcesHelp, SourcesSpec, (a, answer) =>
        {
            if (a.Text("plugin") is not { } plugin) { answer.Fail("sources2 needs a plugin", "give a plugin's file name in Data, or the path of a plugin file", 2); return; }
            bool scan = a.Flag("scan");
            Report(answer, a, "sources", SourcesKnown.Concat(Common), s => new SourcesReport(s).Sources(plugin, new SourcesOptions
            {
                Signatures = a.Text("types") == null ? null : List(a.Text("types")), Item = a.Text("item"), With = List(a.Text("with")),
                Dependents = !a.Flag("only"), Depth = a.Int("depth") is > 0 and var d ? d : 6,
                // "What points at this" from the link index (the same one refs answers from), unless --scan asks for the walk
                BackLinks = scan ? null : (scope, searched) => new LinkIndexBackLinks(s, scope, searched),
            }));
        });

        // ---------------------------------------------------------------- archive, assets, materials, scripts, changes
        public const string ArchiveHelp = """
            archive2 <plugin | file.ba2 | all> [list [<pattern>] | find <text> | show <path> | extract <pattern> --to <folder>]
                     [--beside] [--detail] [--max-chars N] [--cursor C] [--json]
              What is inside BA2 archives. With a plugin: its own archives (<plugin name> - <part>.ba2). "all": every archive in Data.
              (nothing)  a summary per archive: files, size, kinds of file, files that also exist loose in Data
              list       the files whose path fits a pattern. A pattern with * or ? must fit the whole path (*.mat, meshes\*);
                         any other text is looked for anywhere in the path
              find       the same over every archive in Data: archive2 all find <text>
              show       one file's content as text (.mat, .psc, .json, ...)
              extract    copies the files that fit into a folder (--to), never into the game's folders
              --beside   for a plugin file outside Data: the archives that sit beside it, not the ones in Data
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...).
            """;
        static readonly string[] ArchiveKnown = { "target", "action", "pattern", "to", "beside", "list", "find", "show", "extract" };
        static readonly VerbSpec ArchiveSpec = Spec("archive2", new[] { "target", "action", "pattern" }, new[] { "to", "list", "find", "show", "extract" }, new[] { "beside" });

        public static void ArchiveVerb(List<string> args) => RunVerb(args, ArchiveHelp, ArchiveSpec, (a, answer) =>
        {
            if (a.Text("target") is not { } target) { answer.Fail("archive2 needs a plugin, an archive or \"all\"", "give a plugin's name, a .ba2 file, or all", 2); return; }
            string action = (a.Text("action") ?? "").ToLowerInvariant();
            string? pattern = a.Text("pattern");
            // The runner's spelling works too: --list <pattern>, --find <text>, --show <path>, --extract <pattern>
            foreach (string opt in new[] { "list", "find", "show", "extract" })
                if (a.Text(opt) is { } v) { action = opt; pattern = v; }
            bool beside = a.Flag("beside");
            if (action.Length > 0 && action is not ("list" or "find" or "show" or "extract" or "summary"))
            { answer.Fail($"archive2 has no action '{action}'", "after the target comes nothing (a summary), list [<pattern>], find <text>, show <path>, or extract <pattern> --to <folder>", 2); return; }
            if (action is "find" or "show" or "extract" && pattern == null) { answer.Fail($"archive2 {action} needs {(action == "show" ? "a file's path" : "a text or a pattern")}", $"archive2 {target} {action} <{(action == "show" ? "path" : "pattern")}>", 2); return; }
            if (action == "extract" && a.Text("to") == null) { answer.Fail("archive2 extract needs a folder to copy into", "add --to <folder>: any folder outside the game's folders", 2); return; }
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
              in the SFSearch home.
              --find    every line that contains a text      --show   one script's source
              --calls   only the code that reaches records, every call (needs no decompiler)
              --no-records  don't look for the records that carry each script
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...).
            """;
        static readonly VerbSpec ScriptsSpec = Spec("scripts2", new[] { "plugins" }, new[] { "find", "show" }, new[] { "calls", "no_records", "world", "no_world", "beside" }, new() { ["plugin"] = "plugins" });

        public static void ScriptsVerb(List<string> args) => RunVerb(args, ScriptsHelp, ScriptsSpec, (a, answer) =>
        {
            if (a.Text("plugins") is not { } plugins) { answer.Fail("scripts2 needs a plugin", "give one or more plugin names, with commas between them", 2); return; }
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
              What a field holds across many plugins, in one call: for each field, the records and values each plugin has;
              the distinct values, the most common ones and the share of zeros; for 4-byte values (and each 4 bytes of a run
              of bytes) how many read as a float (1e-4 to 1e6), as the FormID of a record in Data (read with the plugin's own
              masters; and which record types), and as a strings ID of the plugin's strings files; samples, each with its
              record (EditorID, FormKey), where in it, and the scalar values beside the field.
              Values are Mutagen's, read through the record layer along the schema's path. Nothing is stored.

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
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...); 7 not run (heavy, not allowed).
            """;
        static readonly string[] ProfileKnown = { "fields", "in", "type", "unknown", "groups", "group", "list", "samples", "allow_heavy" };
        static readonly VerbSpec ProfileSpec = Spec("profile", new[] { "fields" }, new[] { "in", "type", "group", "samples" }, new[] { "unknown", "groups", "list", "allow_heavy" }, new() { ["plugins"] = "in", ["plugin"] = "in" });

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
            bool groups = a.Flag("groups"), unknown = a.Flag("unknown");
            int samples = a.Int("samples") ?? 3;
            if (groups && (words.Count > 0 || unknown)) { answer.Fail("--groups shows the GRUP headers, and fields were named too", "ask for the groups alone, or for fields alone", 2); return; }
            if (!groups && a.Text("group") != null) { answer.Fail("--group names a group of the GRUP-header view", "add --groups", 2); return; }
            if (!groups && !unknown && words.Count == 0) { answer.Fail("profile got no field", "give fields as Type.Path (Weapon.AttackDamage), --unknown with --type, or --groups (profile --help)", 2); return; }
            Report(answer, a, "profile", ProfileKnown.Concat(Common), s =>
            {
                if (groups) return new GroupsReport(s).Groups(new GroupsOptions { In = plugins, Type = a.Text("type"), Group = a.Text("group"), List = a.Flag("list") });
                var report = new ProfileReport(s);
                var plan = report.Plan(new ProfileOptions { Fields = words, Unknown = unknown, Type = a.Text("type"), In = plugins, Samples = samples });
                if (!EngineLibs.Heavy(answer, a.Flag("allow_heavy"), plan.Seconds, plan.MemoryMB, plan.What)) return null;
                return report.Run(plan, samples);
            });
        }

        public const string ChangesHelp = """
            changes2 [<plugin>] [--since 2026-11-01] [--all] [--detail] [--max-chars N] [--cursor C] [--json]
              What changed in Data since last time: plugins added, removed, back and updated, and the patches that are at
              risk because a master of theirs changed. With a plugin: its history, and what its last update added and removed.
              The history is a small log under kept\history in the SFSearch home. It grows by looks: this call, and the
              service by itself at its start and every 10 minutes while idle (historyLook in sfsearch.json). The answer
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
    /// (it leaves world data out): they are read once per command, as the scan reads them. A searched plugin that has
    /// no link file yet is answered by the library's scan.
    /// </summary>
    public sealed class LinkIndexBackLinks : IBackLinks
    {
        readonly ReportSession _s;
        readonly ReadScope _scope;
        readonly List<(RecordEngine Engine, IndexedPlugin Plugin, LinkFile File)> _indexed = new();
        readonly ScanBackLinks? _scan;
        readonly List<string> _plugins;
        readonly SFSearch.Reports.LinkWalker _walker;
        readonly List<string> _notes = new();
        readonly Dictionary<string, IReadOnlyList<BackLink>> _answers = new(StringComparer.OrdinalIgnoreCase);
        // target FormKey to the placed references that name it, each as (place in _indexed) << 32 | entry
        Dictionary<string, List<long>>? _placed;
        int _readForPlaces, _placedRead, _unread, _lookups;
        long _placedLinks;
        double _seconds;
        static readonly HashSet<string> Placed = new(StringComparer.Ordinal) { "REFR", "ACHR" };

        /// <summary>A plugin with more placed references than this isn't searched for placed copies (the note says so): the scan's own limit.</summary>
        public int PlacedLimit { get; init; } = 300_000;

        public LinkIndexBackLinks(ReportSession session, ReadScope scope, IReadOnlyList<(RecordEngine Engine, IndexedPlugin Plugin)> plugins)
        {
            _s = session; _scope = scope; _walker = new SFSearch.Reports.LinkWalker(session.Schema);
            _plugins = plugins.Select(p => p.Plugin.Name).ToList();
            var rest = new List<(RecordEngine Engine, IndexedPlugin Plugin)>();
            foreach (var (engine, p) in plugins)
            {
                var file = p.Segment != null && p.Problems.MissingMasters.Count == 0 ? EngineLinks.For(p) : null;
                if (file != null) _indexed.Add((engine, p, file)); else rest.Add((engine, p));
            }
            if (rest.Count > 0)
            {
                _scan = new ScanBackLinks(session, scope, rest) { PlacedLimit = PlacedLimit };
                _notes.Add($"{rest.Count:N0} of the {plugins.Count:N0} plugins searched have no link file yet ({string.Join(", ", rest.Take(4).Select(r => r.Plugin.Name))}{(rest.Count > 4 ? ", …" : "")}): their records were read one by one instead. The answer is the same.");
            }
        }

        public IReadOnlyList<string> Plugins => _plugins;
        public IReadOnlyList<string> Notes { get { BuildPlaced(); return _scan == null ? _notes : _notes.Concat(_scan.Notes).ToList(); } }
        public string Cost =>
            $"the link index of {_indexed.Count:N0} plugin{(_indexed.Count == 1 ? "" : "s")} ({_lookups:N0} lookup{(_lookups == 1 ? "" : "s")}), {_readForPlaces:N0} record{(_readForPlaces == 1 ? "" : "s")} to place the links, {_placedRead:N0} placed reference{(_placedRead == 1 ? "" : "s")} ({_placedLinks:N0} links, {_seconds:F1} s)"
            + (_scan != null ? ", " + _scan.Cost : "");

        /// <summary>The placed references of the indexed plugins, read once: the link index leaves world data out.</summary>
        void BuildPlaced()
        {
            if (_placed != null) return;
            var clock = Stopwatch.StartNew();
            _placed = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            for (int pi = 0; pi < _indexed.Count; pi++)
            {
                var (engine, p, _) = _indexed[pi];
                var seg = p.Segment!;
                int placed = seg.Signatures.Where(x => Placed.Contains(x.Signature)).Sum(x => x.Count);
                if (placed == 0) continue;
                if (placed > PlacedLimit) { _notes.Add($"{p.Name} has {placed:N0} placed references: they weren't searched for placed copies."); continue; }
                _scope.Touch(engine, p.Name);
                foreach (string sig in Placed)
                    foreach (int i in seg.EntriesOf(sig))
                    {
                        if (i >= seg.FirstUnresolved) continue;
                        RecordView view;
                        try { view = _s.Compare.View(engine, new RecordRef(p.Id, i)); }
                        catch (CompareException) { _unread++; continue; }
                        _placedRead++;
                        long from = (long)pi << 32 | (uint)i;
                        _walker.Walk(view.Type, view.Json, link =>
                        {
                            _placedLinks++;
                            string key = ReportSession.Canon(link.FormKey);
                            if (!_placed.TryGetValue(key, out var list)) _placed[key] = list = new List<long>(1);
                            if (list.Count == 0 || list[^1] != from) list.Add(from);
                        });
                    }
            }
            if (_unread > 0) _notes.Add($"{_unread:N0} placed reference(s) couldn't be read and weren't searched.");
            _seconds += clock.Elapsed.TotalSeconds;
        }

        public IReadOnlyList<BackLink> Into(string formKey)
        {
            string key = ReportSession.Canon(formKey);
            if (_answers.TryGetValue(key, out var have)) return have;
            BuildPlaced();
            var result = new List<BackLink>();
            if (HeaderIndex.TryParseFormKey(key, out var owner, out var localId))
            {
                _lookups++;
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
                    // Records the link walk couldn't read through Mutagen can't be read here either: the scan counts them as not read
                    if (_placed!.TryGetValue(key, out var placed))
                        foreach (long h in placed) if ((int)(h >> 32) == pi) holders.Add((int)(uint)h);
                    if (holders.Count == 0) continue;
                    _scope.Touch(engine, p.Name);
                    foreach (int entry in holders.Distinct().OrderBy(x => x))
                    {
                        var hit = new Hit(engine, new RecordRef(p.Id, entry));
                        RecordView view;
                        try { view = _s.Compare.View(engine, hit.Ref); }
                        catch (CompareException) { continue; }
                        _readForPlaces++;
                        var info = view.Info;
                        _walker.Walk(view.Type, view.Json, link =>
                        {
                            if (!ReportSession.Canon(link.FormKey).Equals(key, StringComparison.OrdinalIgnoreCase)) return;
                            result.Add(new BackLink
                            {
                                From = hit, FromKey = ReportSession.Canon(info.FormKey), Signature = info.Signature, Type = view.Type, EditorId = info.EditorId,
                                Plugin = p.Name, Path = link.Path, Parent = info.Parent == null ? null : ReportSession.Canon(info.Parent),
                            });
                        });
                    }
                }
            }
            if (_scan != null) result.AddRange(_scan.Into(formKey));
            return _answers[key] = result;
        }
    }
}
