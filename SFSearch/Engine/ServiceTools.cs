using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// run (tool sfs_run): any verb of the engine by name, or an add-on's command, with its answer in the parts of the
    /// output contract. "help" lists the verbs with one line each. An older command's name (removed in 2.0) answers with
    /// the verb to use.
    /// (design\agent-first\ARCHITECTURE.md section 7; milestone M8.)
    /// </summary>
    public static class RunTool
    {
        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            if (a.Unknown("args", "allow_heavy") is { Count: > 0 } wrong)
            {
                JobContext.Fail($"run has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", "its one argument is args: the command line as a list, one item per word ([\"leveled2\", \"0032EE:Starfield.esm\", \"--depth\", \"4\"]), or as one text. args [\"help\"] lists the verbs", 2);
                return;
            }
            List<string> words;
            var given = a.Element("args");
            if (given is { ValueKind: JsonValueKind.Array } list) words = list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()).ToList();
            else if (given is { ValueKind: JsonValueKind.String } text) words = Words.Split(text.GetString()!);
            else words = new List<string>();
            if (words.Count == 0 || words[0].Equals("help", StringComparison.OrdinalIgnoreCase)) { Console.Write(EngineCommands.HelpText()); return; }
            // A verb answers in parts, as a tool does; an add-on's text is the answer and what it wrote beside it the notes
            EngineAnswer.ToolMode = true;
            if (EngineCommands.Verbs.Any(v => v.Verb == words[0]) || words[0].StartsWith("__", StringComparison.Ordinal)) { Program.Main(words.ToArray()); return; }
            // Anything else is an add-on's command, a retired older command's name (it answers with the verb to use), or
            // Service, SetData, Version, Extensions. A name that is none of them gets the one line with its fix.
            var shown = Console.Out;
            var captured = new StringWriter();
            Console.SetOut(captured);
            try { Program.Main(words.ToArray()); }
            finally { Console.SetOut(shown); }
            string said = captured.ToString();
            if (said.StartsWith("Unknown command '", StringComparison.Ordinal) && said.TrimEnd().IndexOf('\n') < 0)
            {
                var near = SFSearch.SchemaGen.NearNames.Pick(words[0], EngineCommands.Verbs.Select(v => v.Verb), v => new[] { v, v.TrimEnd('2') }, 3, always: false);
                JobContext.Fail($"there is no verb or command named '{words[0]}'",
                    (near.Count > 0 ? SFSearch.SchemaGen.NearNames.Fix(near) + ". " : "") + "args [\"help\"] lists the verbs; an add-on's command runs by its own name", 2);
            }
            else shown.Write(said);
        }
    }

    /// <summary>
    /// status (tool sfs_status, command-line verb status): the service's health, what is open, how far the header
    /// index and the link index are, the scratch folder's size, and memory.
    /// </summary>
    public static class StatusTool
    {
        public const string Help = """
            status [--json]
              The SFSearch service's health: workers, the queue, the heavy lane, the indexer; how far the header index and
              the link index are; what is open; the scratch folder's size; memory. With no service running it says so, and
              shows what a plain run has in the scratch folder.
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...).
            """;

        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            var answer = new EngineAnswer { AsTool = true };
            if (a.Unknown() is { Count: > 0 } wrong) answer.Fail($"status has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", "it takes no arguments", 2);
            else Run(answer);
            answer.Emit();
        }

        public static void Verb(List<string> args)
        {
            if (args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            var answer = new EngineAnswer();
            if (args.Count > 0) answer.Fail($"status takes no arguments (it got {string.Join(" ", args)})", "run it bare: status", 2);
            else Run(answer);
            answer.Emit();
        }

        static string Mb(long bytes) => EngineIndex.Mb(bytes);

        static void Run(EngineAnswer answer)
        {
            // ---- The coordinator's own lines: workers, queue, heavy lane, indexer, memory
            bool service = Wire.RunningService(Home.Dir) != null;
            if (service)
            {
                try
                {
                    using var cts = new CancellationTokenSource(8000);
                    var r = Wire.CallAsync(Home.Dir, new Request { Op = "status", Structured = true }, 4000, cts.Token).GetAwaiter().GetResult();
                    answer.Text(r.Error ?? r.Out);
                }
                catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
                {
                    answer.Line($"The service runs, but didn't answer a status request in 8 s ({e.Message.TrimEnd('.')}).");
                }
            }
            else answer.Line("The service isn't running: this is a plain run of SFSearch. Each command then maps the header index again (about 0.2 s) and reads what it needs. 'Service start' keeps plugins open between commands.");

            // ---- What this program can see itself: the indexes, the scratch folder, what it holds open
            try
            {
                var eng = EngineIndex.Get();
                var v = eng.Check();
                var ix = eng.Index;
                answer.Line($"Header index: complete. {ix.Plugins.Count:N0} plugins in Data ({ix.LoadOrderEntries.Count(e => e.Enabled):N0} loaded), {ix.TotalRecords:N0} records, {Mb(ix.TotalSegmentBytes)} of segments; checked against the files in {v.Ms:N0} ms. {ix.Plugins.Count(p => p.Problems.Any):N0} plugin(s) have problems (loadorder2 problems lists them).");
                var (have, could, links, bytes, names) = EngineLinks.Totals(ix);
                string linksLine = have >= could
                    ? $"Link index: complete. {have:N0} of {could:N0} plugins have a link file: {links:N0} links, {names:N0} in-game names, {Mb(bytes)}."
                    : $"Link index: {have:N0} of {could:N0} plugins have a link file so far ({links:N0} links, {names:N0} in-game names, {Mb(bytes)}). Until the rest are made, refs searches those plugins' bytes, and find knows no in-game names of theirs.";
                answer.Line(linksLine);
                string root = EngineLibs.ScratchRoot;
                var parts = new List<string>();
                long all = 0; int files = 0;
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        var (b, f) = EngineLibs.FolderSize(dir);
                        all += b; files += f;
                        if (f > 0) parts.Add($"{Path.GetFileName(dir)} {Mb(b)} in {f:N0} file(s)");
                    }
                }
                answer.Line($"Scratch folder: {root}, {Mb(all)} in {files:N0} file(s){(parts.Count > 0 ? ": " + string.Join("; ", parts) : "")}. The service empties it at every start; everything in it is made again when needed.");
                var (keptBytes, keptFiles) = EngineLibs.FolderSize(EngineLibs.KeptRoot);
                answer.Line(keptFiles == 0 ? $"Kept files: none yet ({EngineLibs.KeptRoot} holds decompiled scripts and the history log once a report makes them)."
                    : $"Kept files: {EngineLibs.KeptRoot}, {Mb(keptBytes)} in {keptFiles:N0} file(s) (decompiled scripts, the history log). They outlive a service start, and can be deleted.");
                // The history log is written by changes2, loadorder's view changes and the service's own looks (fix S11): status names it
                string history = Path.Combine(EngineLibs.KeptRoot, "history");
                var (historyBytes, historyFiles) = EngineLibs.FolderSize(history);
                var lastLook = Directory.Exists(history) ? Directory.GetDirectories(history).Select(SFSearch.Reports.HistoryLook.Read).Where(l => l != null).OrderByDescending(l => l!.Seen).FirstOrDefault() : null;
                string looks = ServiceConfig.Load(Home.Dir).HistoryLook ? "the service looks by itself at its start and every 10 minutes while idle; changes2 looks when asked" : "only changes2 looks (historyLook is off)";
                answer.Line(historyFiles == 0 ? $"History log: none yet. The first look writes one under {history} ({looks}): one line per plugin copy seen and a list of its FormKeys, no record contents."
                    : $"History log: {history}, {Mb(historyBytes)} in {historyFiles:N0} file(s) (one line per plugin copy seen and a list of its FormKeys, no record contents); {looks}.{(lastLook != null ? $" The last look: {lastLook.Seen:yyyy-MM-dd HH:mm:ss}, by {lastLook.ByWords}." : "")} Deleting it only forgets the history.");
                // The names of the plugins this program holds open. Their number is the one the service's line gives
                // for this worker: both count each plugin once (EngineRecords.OpenNames).
                var open = EngineRecords.OpenNames();
                long mem;
                using (var me = Process.GetCurrentProcess()) mem = me.PrivateMemorySize64 / 1048576;
                string who = service ? "the worker that answered this" : "this program";
                answer.Line(open.Count == 0 ? $"Open in {who}: no plugin. It holds {mem:N0} MB."
                    : $"Open in {who}, as Mutagen overlays: {string.Join(", ", open.Take(12))}{(open.Count > 12 ? $" and {open.Count - 12:N0} more" : "")} ({open.Count:N0} plugin{(open.Count == 1 ? "" : "s")}). It holds {mem:N0} MB.");
                answer.Line($"Memory: Windows has {MemoryInfo.AvailableMB():N0} MB available of {MemoryInfo.TotalMB():N0} MB.");
                // The service's own lines above already say what limits it uses and where each came from; with no service, say what one would use
                if (!service) foreach (var line in ServiceConfig.Load(Home.Dir).MemoryLines().Split('\n')) answer.Line(line);
                answer.Source(eng.AllSource());
                answer.Source(eng.LoadOrderSource());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or FileNotFoundException)
            {
                answer.Line($"The header index couldn't be looked at: {e.Message.Split('\n')[0].TrimEnd('.')}.");
            }
        }
    }

    /// <summary>
    /// job (tool sfs_job, command-line verb job): collect the answer of a command the service is still running, or
    /// cancel it. An agent normally reaches this through the MCP server, which asks the service itself.
    /// </summary>
    public static class JobTool
    {
        public const string Help = """
            job <job> [--wait-seconds N] [--cancel]
              Collects the answer of a command the service is still running (a tool call that came back with a job number),
              or cancels it. Answers are kept for an hour.
              Exit code: the collected command's own; 2 an error (one line, Error: ... Fix: ...), such as a job that doesn't exist.
            """;

        static readonly string[] Known = { "job", "wait_seconds", "cancel" };
        static readonly VerbSpec Spec = new() { Verb = "job", Positional = new[] { "job" }, Options = new[] { "wait_seconds" }, Flags = new[] { "cancel" } };

        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            if (a.Unknown(Known) is { Count: > 0 } wrong) { JobContext.Fail($"job has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", $"its arguments are: {string.Join(", ", Known)}", 2); return; }
            Run(a);
        }

        public static void Verb(List<string> args)
        {
            if (args.Count == 0 || args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            var answer = new EngineAnswer();
            var given = Spec.Parse(args, answer);
            if (given == null) { answer.Emit(); return; }
            Run(new ToolArgs(given));
        }

        static void Run(ToolArgs a)
        {
            string? job = a.Text("job");
            if (job == null) { JobContext.Fail("job needs the job's number", "give the number a tool call came back with, e.g. job j1a2b-17", 2); return; }
            if (Wire.RunningService(Home.Dir) == null) { JobContext.Fail("the service isn't running, so there are no jobs", "a plain run answers every command at once; start the service with 'Service start'", 2); return; }
            bool cancel = a.Flag("cancel");
            int wait = Math.Clamp(a.Int("wait_seconds") ?? 10, 0, 20);
            try
            {
                using var cts = new CancellationTokenSource((wait + 10) * 1000);
                var r = Wire.CallAsync(Home.Dir, cancel ? new Request { Op = "cancel", Job = job } : new Request { Op = "job", Job = job, WaitMs = wait * 1000, Structured = true }, 4000, cts.Token).GetAwaiter().GetResult();
                if (r.Error != null)
                {
                    string error = r.Error.StartsWith("Error: ", StringComparison.Ordinal) ? r.Error[7..] : r.Error;
                    JobContext.Fail(error.StartsWith("No job ", StringComparison.Ordinal) ? $"there is no job {job}" : error, "check the job number: answers are kept for an hour, and not across a service restart", 2);
                    return;
                }
                if (cancel) { Console.Write(r.Out.Length > 0 ? r.Out : $"Job {job} was cancelled.\n"); return; }
                if (!r.Done) { Console.WriteLine($"Job {job} is still running ({r.State ?? "running"}, {r.Ms / 1000} s so far{(r.Ahead > 0 ? $", {r.Ahead} ahead of it" : "")}). Ask again, or cancel it with cancel: true."); return; }
                // The finished job's answer, passed on in its parts
                Console.Write(r.Answer ?? r.Out);
                foreach (var n in r.Notes ?? Array.Empty<string>()) Console.Error.WriteLine(n);
                foreach (var s in r.Source ?? Array.Empty<SourceInfo>()) JobContext.AddSource(s);
                foreach (var x in r.Cost?.Read ?? Array.Empty<string>()) JobContext.AddRead(x);
                if (r.More != null) JobContext.SetMore(r.More);
                Environment.ExitCode = r.Code;
            }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                JobContext.Fail($"the service didn't answer ({e.Message.TrimEnd('.')})", "ask again; 'Service status' tells whether it runs", 2);
            }
        }
    }
}
