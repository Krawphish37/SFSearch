using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SFSearch.Service;

namespace SFSearch.ServiceTest;

public static partial class T
{
    static Request Parts(params string[] args) => new() { Op = "run", Args = args, Agent = "test", WaitMs = -1, Structured = true };

    // ================================================================ The message forms, cursors, the scratch folder
    static async Task Protocol()
    {
        // 1. A client from before the change: its request has none of the new fields, its reply none of the new parts
        string raw = await RawCall("{\"op\":\"run\",\"args\":[\"__test\",\"rows\",\"5\"],\"cwd\":null,\"agent\":\"old-client\",\"waitMs\":-1,\"job\":null}");
        using (var doc = JsonDocument.Parse(raw))
        {
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            string[] old = { "done", "job", "out", "err", "code", "ms", "error", "ahead", "state" };
            Check("old form: the reply has exactly the fields it always had", keys.SequenceEqual(old), string.Join(",", keys));
            Check("old form: the answer is in out", doc.RootElement.GetProperty("out").GetString()!.Split('\n').Length == 8, "a 5-row table");
        }

        // 2. The structured form: answer, notes, cost
        var r = await Call(Parts("__test", "lines", "3"));
        Check("structured: answer", r.Answer == "line 1 of 3\nline 2 of 3\nline 3 of 3\n".Replace("\n", Environment.NewLine) && r.Out == "" && r.Err == "", $"{r.Answer?.Length} characters, out and err empty");
        Check("structured: notes", r.Notes is { Length: 1 } && r.Notes[0] == "[test] a note from the command", string.Join(" | ", r.Notes ?? []));
        Check("structured: cost", r.Cost != null && r.Cost.Ms >= 0 && r.Cost.MemMB > 0 && r.More == null, $"{r.Cost?.Ms} ms, worker at {r.Cost?.MemMB} MB, waited {r.Cost?.QueuedMs} ms");

        // 3. Source and what was read: said by a test command, and by a verb
        r = await Call(Parts("__test", "source"));
        Check("structured: source and read given by the command", r.Source is { Length: 1 } && r.Source[0].Plugin == "example.esm" && r.Source[0].Bytes == 26474052 && r.Cost?.Read?[0] == "Weapon (456) from example.esm",
            Wire.Render(r).Replace(Environment.NewLine, "\n").Replace("\n", " / "));
        // A tool by name fills the structured source (a verb on a command line prints its [source] line in its answer)
        r = await Call(new Request { Op = "run", Tool = "sfs_query", Params = new() { ["plugins"] = J("darkstar.esm"), ["sql"] = J("SELECT count(*) AS N FROM Forms;") }, Structured = true, Agent = "test", WaitMs = -1 });
        long size = new FileInfo(Path.Combine(Environment.GetEnvironmentVariable("SFSEARCH_DATA") ?? "", "darkstar.esm")).Length;
        var ds = r.Source?.FirstOrDefault(s => Path.GetFileName(s.Plugin).Equals("darkstar.esm", StringComparison.OrdinalIgnoreCase));
        Check("structured: source of a tool's answer", ds != null && ds.Bytes == size && ds.Key.Length == 8, $"{ds?.Text ?? ds?.Plugin}: {ds?.Bytes:N0} bytes (file: {size:N0}), key {ds?.Key}");
        Say("rendered: " + string.Join(" / ", Wire.Render(r).Replace("\r", "").Split('\n').Where(l => l.StartsWith('['))));

        // 4. An error with its fix
        r = await Call(Parts("__test", "fail"));
        Check("structured: error with its fix", r.Code == 1 && r.Failure?.Fix == "ask for something else." && r.Failure.Message == "Error: the test asked for a failure. Fix: ask for something else.", r.Failure?.Message ?? "(none)");
        var classic = await Call(Cmd("__test", "fail"));
        Check("old form: the same error is one line in out", classic.Out.TrimEnd() == "Error: the test asked for a failure. Fix: ask for something else." && classic.Failure == null && classic.Code == 1);
        r = await Call(Parts("loadorder2", "plugin", "nosuch.esm"));
        Check("structured: the error of a verb", r.Code == 2 && r.Failure != null && r.Failure.Message.StartsWith("Error: ") && r.Failure.Message.Contains("nosuch.esm") && r.Failure.Fix != null, r.Failure?.Message ?? "(none)");
        r = await Call(Parts("Names", "000CF2"));
        Check("structured: an older command's name (removed in 2.0) points at its verb", r.Code == 2 && r.Failure != null && r.Failure.Message.Contains("removed in 2.0") && r.Failure.Message.Contains("Fix: use find"), r.Failure?.Message ?? "(none)");

        // A command's --json is its own: the next command (calls one after another go to the worker idle the shortest
        // time, so the same worker) answers in text again (GAPS S78: the global options outlived their command)
        var asJson = await Call(Cmd("describe", "KYWD", "--json"));
        var asText = await Call(Cmd("describe", "KYWD"));
        Check("a command's --json doesn't carry over to the next command on the worker", asJson.Out.TrimStart().StartsWith("{") && !asText.Out.TrimStart().StartsWith("{") && asText.Code == 0,
            $"with --json: {asJson.Out.TrimStart()[..Math.Min(40, asJson.Out.TrimStart().Length)]}…; then without: {asText.Out.TrimStart()[..Math.Min(60, asText.Out.TrimStart().Length)]}…");

        // 5. A tool name with named arguments
        r = await Call(new Request { Op = "run", Tool = "run", Params = new() { ["args"] = J(new[] { "__test", "echo", "hello", "there" }) }, Agent = "test" });
        Check("tool form: run with args", r.Answer?.TrimEnd() == "hello there" && r.Code == 0, r.Answer?.TrimEnd() ?? "");
        // (sfs_find was the unknown name here until milestone M5 built it)
        r = await Call(new Request { Op = "run", Tool = "sfs_no_such_tool", Params = new() { ["query"] = J("Eon") }, Agent = "test" });
        Check("tool form: an unknown tool says so, with a fix", r.Code == 2 && r.Failure?.Fix != null && r.Failure.Message.Contains("no tool named 'sfs_no_such_tool'"), r.Failure?.Message ?? "(none)");
        r = await Call(new Request { Op = "run", Structured = true });
        Check("structured: a request with no command", r.Failure?.Message == "No command given." && r.Code == 2);

        // 6. Cursors
        var whole = await Call(Cmd("__test", "rows", "1000", "40"));
        var wholeLines = whole.Out.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var first = await Call(new Request { Op = "run", Args = ["__test", "rows", "1000", "40"], Agent = "test", PageChars = 5000 });
        var rows = new List<string>();
        int pages = 0, headerRepeats = 0, biggest = 0;
        var page = first;
        string? firstCursor = first.More?.Cursor;
        while (true)
        {
            pages++;
            var lines = (page.Answer ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2 && lines[0] == wholeLines[0] && lines[1] == wholeLines[1]) headerRepeats++;
            rows.AddRange(lines.Skip(2));
            biggest = Math.Max(biggest, page.Answer?.Length ?? 0);
            if (page.More?.Cursor == null) break;
            page = await Call(new Request { Op = "run", Cursor = page.More.Cursor, PageChars = 5000 });
        }
        Check("cursor: a table in pages", pages > 5 && headerRepeats == pages && rows.SequenceEqual(wholeLines.Skip(2)) && first.More is { Total: 1000, Unit: "rows", From: 1 },
            $"1,000 rows in {pages} pages of at most {biggest:N0} characters, header on every page, rows equal to the unpaged answer; first page says \"{Wire.Render(first).Replace("\r", "").Split('\n').First(l => l.StartsWith("[more]"))}\"");
        Check("cursor: the last page says it is the end", page.More is { Cursor: null } && page.More.From + page.More.Shown - 1 == 1000, $"rows {page.More?.From} to {page.More?.From + page.More?.Shown - 1} of {page.More?.Total}");
        var again = await Call(new Request { Op = "more", Cursor = firstCursor, PageChars = 5000 });
        var second = await Call(new Request { Op = "run", Cursor = firstCursor, PageChars = 5000 });
        Check("cursor: the same cursor gives the same page", again.Answer == second.Answer && again.More?.From == second.More?.From && again.More?.From > 1, $"rows from {again.More?.From}");
        var bad = await Call(new Request { Op = "run", Cursor = "j0-999@40" });
        Check("cursor: one that is gone is a clear error", bad.Code == 2 && bad.Failure?.Fix == "run the command again.", bad.Error ?? "");

        var plainWhole = await Call(Cmd("__test", "lines", "500"));
        var sb = new StringBuilder();
        pages = 0;
        page = await Call(new Request { Op = "run", Args = ["__test", "lines", "500"], Agent = "test", PageChars = 2000 });
        string unit = page.More?.Unit ?? "";
        while (true) { pages++; sb.Append(page.Answer); if (page.More?.Cursor == null) break; page = await Call(new Request { Op = "run", Cursor = page.More.Cursor, PageChars = 2000 }); }
        Check("cursor: plain lines in pages", sb.ToString() == plainWhole.Out && unit == "lines" && pages > 3, $"500 lines in {pages} pages, put together equal to the unpaged answer");

        // A verb's long answer, with the tool's own row and character limits raised so only the service cuts it. The
        // [source] and [cost] lines after the table differ between two runs: the table is compared
        string[] longAsk = ["query2", "darkstar.esm", "SELECT FormKey, EditorID, Sig FROM Forms ORDER BY FormKey LIMIT 1500;", "--rows", "1500", "--max-chars", "1000000"];
        var realWhole = await Call(Cmd(longAsk));
        static string Table(string text) => string.Join("\n", text.Replace("\r", "").Split('\n').Where(l => l.StartsWith('|')));
        sb.Clear(); pages = 0;
        page = await Call(new Request { Op = "run", Args = longAsk, Agent = "test", PageChars = 20000 });
        while (true) { pages++; sb.Append(page.Answer); if (page.More?.Cursor == null) break; page = await Call(new Request { Op = "run", Cursor = page.More.Cursor }); }
        Check("cursor: a verb's long answer in pages", Table(sb.ToString()) == Table(realWhole.Out) && Table(realWhole.Out).Split('\n').Length > 1500 && pages > 2,
            $"{realWhole.Out.Length:N0} characters in {pages} pages of about 20,000, the table put together equal to the unpaged answer's");
        var small = await Call(new Request { Op = "run", Args = ["__test", "rows", "5"], Agent = "test", PageChars = 20000 });
        Check("cursor: an answer that fits has no [more]", small.More == null && small.Answer == (await Call(Cmd("__test", "rows", "5"))).Out);

        // 7. The scratch folder
        string expected = Path.Combine(Home, "scratch");
        r = await Call(Cmd("__test", "scratch", "left-by-test.txt"));
        Check("scratch: a worker can ask where it is", Path.GetFullPath(r.Out.Trim()) == Path.GetFullPath(expected) && File.Exists(Path.Combine(expected, "left-by-test.txt")), r.Out.Trim());
        Directory.CreateDirectory(Path.Combine(expected, "sub", "deeper"));
        File.WriteAllText(Path.Combine(expected, "sub", "deeper", "file.bin"), new string('x', 100000));
        // The SQL layer's tables (scratch\query), and a file beside them
        var sqlAsk = await Call(Cmd("query2", "atom74.esm", "SELECT count(*) FROM Weapon;"));
        string queryDir = Path.Combine(expected, "query");
        int queryFiles = Directory.Exists(queryDir) ? Directory.GetFiles(queryDir, "*", SearchOption.AllDirectories).Length : 0;
        Check("scratch: query2's tables are made in scratch\\query", sqlAsk.Code == 0 && queryFiles > 0, $"exit {sqlAsk.Code}; {queryFiles} file(s) in {queryDir}");
        if (Directory.Exists(queryDir)) File.WriteAllText(Path.Combine(queryDir, "left-by-test.txt"), "x");
        // Outside the scratch folder: what was in it before the restart, for 'scratch-after-restart'
        File.WriteAllLines(Path.Combine(Home, "left-by-test-in-scratch.txt"), Directory.GetFiles(expected, "*", SearchOption.AllDirectories));
        Say("left three files and a table in scratch: run 'scratch-after-restart' after restarting the service");
    }

    static async Task ScratchAfterRestart()
    {
        string dir = Path.Combine(Home, "scratch");
        var left = Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).ToArray() : [];
        string status = await Status();
        string line = status.Split('\n').FirstOrDefault(l => l.StartsWith("Scratch folder:"))?.Trim() ?? "";
        // What was there before the restart is gone. Times are the files' last writes: Windows gives a file made again
        // under the same name within 15 s the old file's creation time (the indexer's material-paths.tsv, written at start).
        // What was there before the restart is gone. The service makes its own folders again at once (the indexes and
        // the like), so the folder isn't empty: no file in it may be older than this service.
        string listFile = Path.Combine(Home, "left-by-test-in-scratch.txt");
        var before = File.Exists(listFile) ? File.ReadAllLines(listFile) : [];
        var pid = System.Text.RegularExpressions.Regex.Match(status, @"\(process (\d+)\)");
        var started = pid.Success ? System.Diagnostics.Process.GetProcessById(int.Parse(pid.Groups[1].Value)).StartTime : DateTime.MaxValue;
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != ".sfsearch-scratch").ToArray() : [];
        var old = files.Where(f => File.GetLastWriteTime(f) < started).ToArray();
        var survived = before.Where(f => Path.GetFileName(f) != ".sfsearch-scratch" && File.Exists(f) && File.GetLastWriteTime(f) < started).ToArray();
        Check("scratch: emptied when the service starts",
            left.Contains(".sfsearch-scratch") && before.Length >= 4 && survived.Length == 0 && old.Length == 0 && line.Contains("emptied at start"),
            $"{before.Length} file(s) were in scratch before the restart, {survived.Length} of them still there; {files.Length} file(s) now, {old.Length} made before this service started; left: {string.Join(", ", left)}; status: {line}");
        var tables = Directory.Exists(Path.Combine(dir, "query")) ? Directory.GetFiles(Path.Combine(dir, "query"), "*", SearchOption.AllDirectories).Where(f => File.GetLastWriteTime(f) < started).ToArray() : [];
        Check("scratch: none of query2's tables outlives a service start", tables.Length == 0, $"{tables.Length} file(s) from before the start in scratch\\query");
    }

    // ================================================================ The heavy lane
    static (DateTime From, DateTime To)? Interval(string text)
    {
        var m = Regex.Match(text, @"from (\d\d:\d\d:\d\d\.\d\d\d) to (\d\d:\d\d:\d\d\.\d\d\d)");
        return m.Success ? (DateTime.Parse(m.Groups[1].Value), DateTime.Parse(m.Groups[2].Value)) : null;
    }

    static async Task Heavy()
    {
        long avail = AvailableMB();
        Say($"{avail} MB available. The jobs here are marked heavy but take no memory; estimates of 3,000 MB need {3000 + 4096} MB available.");
        if (avail < 3000 + 4096 + 2048) { Check("heavy: enough memory to run this test", false, "under 9 GB available"); return; }

        // Not allowed: the answer is the estimate, and how to allow it
        var r = await Call(Cmd("__test", "heavy", "4096", "200"));
        Check("heavy: not allowed, old form", r.Code == 7 && r.Out.StartsWith("Not run.") && r.Out.Contains("about 4 GB") && r.Out.Contains("allow_heavy=true"), r.Out.Trim());
        r = await Call(Parts("__test", "heavy", "4096", "200"));
        Check("heavy: not allowed, structured", r.Heavy is { Reason: "not_allowed", EstimateMB: 4096 } && r.Code == 7 && r.Failure == null, $"heavy: {r.Heavy?.Reason}, {r.Heavy?.EstimateMB} MB");
        // Allowed three ways
        r = await Call(new Request { Op = "run", Args = ["__test", "heavy", "3000", "100"], Agent = "test", AllowHeavy = true });
        Check("heavy: allowed by the request", r.Code == 0 && r.Out.StartsWith("heavy ran"), r.Out.Trim());
        r = await Call(Cmd("__test", "heavy", "3000", "100", "--allow-heavy"));
        Check("heavy: allowed by --allow-heavy on the command line", r.Code == 0 && r.Out.StartsWith("heavy ran"), r.Out.Trim());
        r = await Call(Cmd("__test", "heavy", "1000", "100"));
        Check("heavy: an estimate under the 2 GB limit is an ordinary job", r.Code == 0 && r.Out.StartsWith("heavy ran"), r.Out.Trim());

        // One at a time: 3 commands that find themselves heavy and 3 marked heavy by the caller, all at once, with
        // ordinary commands running through the middle
        var clock = Stopwatch.StartNew();
        var heavy = new List<Task<Reply>>();
        for (int i = 0; i < 3; i++)
        {
            heavy.Add(Call(new Request { Op = "run", Args = ["__test", "heavy", "3000", "1200"], Agent = $"h{i}", AllowHeavy = true }));
            heavy.Add(Call(new Request { Op = "run", Args = ["__test", "sleep", "1200"], Agent = $"c{i}", HeavyMB = 3000 }));
        }
        var ordinary = new List<Task<(Reply R, long Ms)>>();
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(500);
            var sw = Stopwatch.StartNew();
            ordinary.Add(Call(Cmd("__test", "sleep", "200")).ContinueWith(t => (t.Result, sw.ElapsedMilliseconds)));
        }
        string mid = await Status();
        var done = await Task.WhenAll(heavy);
        var ord = await Task.WhenAll(ordinary);
        var spans = done.Select(d => Interval(d.Out)).ToList();
        bool allRan = done.All(d => d.Code == 0) && spans.All(s => s != null);
        var sorted = spans.Where(s => s != null).Select(s => s!.Value).OrderBy(s => s.From).ToList();
        double worstOverlap = 0;
        for (int i = 1; i < sorted.Count; i++) worstOverlap = Math.Max(worstOverlap, (sorted[i - 1].To - sorted[i].From).TotalMilliseconds);
        Check("heavy: two never run at once", allRan && worstOverlap <= 0,
            $"6 heavy jobs of 1.2 s each took {clock.Elapsed.TotalSeconds:0.0} s in all, one after another; the smallest gap between two was {Math.Max(0, -worstOverlap):0.#} ms");
        var ordSpans = ord.Select(o => Interval(o.R.Out)).Where(s => s != null).Select(s => s!.Value).ToList();
        int inside = ordSpans.Count(o => sorted.Any(h => o.From >= h.From && o.To <= h.To));
        Check("heavy: ordinary jobs still run while a heavy one does", ord.All(o => o.R.Code == 0) && inside >= 3 && ord.Max(o => o.Ms) < 1500,
            $"{ord.Length} ordinary 200 ms jobs: {inside} ran entirely inside a heavy job's run; slowest took {ord.Max(o => o.Ms)} ms from asking to answer");
        Say("status in the middle: " + (mid.Split('\n').FirstOrDefault(l => l.StartsWith("Heavy lane:"))?.Trim() ?? ""));
        Say("status at the end:    " + ((await Status()).Split('\n').FirstOrDefault(l => l.StartsWith("Heavy lane:"))?.Trim() ?? ""));
    }

    // ================================================================ Real memory: refused when short, started when there
    static async Task Memory(string bash, string? heavySh, int reserveMB)
    {
        const int spare = 4096;
        long before = AvailableMB();
        Say($"{before} MB available of {TotalMB()} MB.");
        if (before - reserveMB < 6144 + 1024) { Check("memory: enough available to run this test and keep 6 GB", false, $"{before} MB available, the test takes {reserveMB} MB"); return; }
        // An estimate that fits now with half the reservation to spare, so it can't fit once the reservation is taken
        int estimate = (int)(before - spare - reserveMB / 2);
        string Need() => $"estimate {estimate} MB + {spare} MB spare = {estimate + spare} MB";

        var r = await Call(new Request { Op = "run", Args = ["__test", "echo", "ran"], Agent = "test", HeavyMB = estimate });
        Check("memory: started when memory is there (marked heavy by the caller)", r.Code == 0 && r.Out.Trim() == "ran", $"{Need()}; {AvailableMB()} MB available");
        r = await Call(new Request { Op = "run", Args = ["__test", "heavy", estimate.ToString(), "100"], Agent = "test", AllowHeavy = true });
        Check("memory: started when memory is there (the command says it is heavy)", r.Code == 0 && r.Out.StartsWith("heavy ran"), r.Out.Trim());

        // Take real memory with the helper: through a memory-slot script (--heavy-sh, run with bash) when one is named, so
        // it waits its turn on a PC that other jobs share; else directly
        string release = Path.Combine(Path.GetTempPath(), $"sfs_m4_release_{Environment.ProcessId}");
        File.Delete(release);
        string self = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(heavySh != null ? bash : self) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        string[] helperArgs = heavySh != null
            ? [heavySh, "sfs-m4-reserve", self.Replace('\\', '/'), "reserve", reserveMB.ToString(), "120", release.Replace('\\', '/')]
            : ["reserve", reserveMB.ToString(), "120", release];
        foreach (var a in helperArgs) psi.ArgumentList.Add(a);
        using var helper = Process.Start(psi)!;
        var reserved = new TaskCompletionSource<string>();
        helper.OutputDataReceived += (_, e) => { if (e.Data != null) { Say("helper: " + e.Data); if (e.Data.StartsWith("reserved") || e.Data.StartsWith("stopped")) reserved.TrySetResult(e.Data); } };
        helper.ErrorDataReceived += (_, e) => { if (e.Data != null) Say("helper: " + e.Data); };
        helper.BeginOutputReadLine(); helper.BeginErrorReadLine();
        try
        {
            if (await Task.WhenAny(reserved.Task, Task.Delay(TimeSpan.FromMinutes(20))) != reserved.Task) { Check("memory: the helper reserved its memory", false, "the helper didn't start within 20 minutes"); return; }
            long during = AvailableMB();
            Check("memory: the helper reserved its memory", during < estimate + spare, $"{during} MB available now ({before - during} MB less); the job needs {estimate + spare} MB");

            const string pattern = @"^Error: needs about [\d.]+ GB, [\d.]+ GB is free\. Fix: try again later, or close other programs\.\s*$";
            long refusedBefore = RefusedCount(await Status());
            var sw = Stopwatch.StartNew();
            r = await Call(new Request { Op = "run", Args = ["__test", "echo", "ran"], Agent = "test", HeavyMB = estimate });
            long ms = sw.ElapsedMilliseconds;
            Check("memory: refused at once with the clear line (marked heavy by the caller)", r.Code == 8 && Regex.IsMatch(r.Out, pattern) && ms < 1000, $"answered in {ms} ms: {r.Out.Trim()}");
            sw.Restart();
            r = await Call(new Request { Op = "run", Args = ["__test", "heavy", estimate.ToString(), "100"], Agent = "test", AllowHeavy = true });
            ms = sw.ElapsedMilliseconds;
            Check("memory: refused at once with the clear line (the command says it is heavy)", r.Code == 8 && Regex.IsMatch(r.Out, pattern) && ms < 1500, $"answered in {ms} ms: {r.Out.Trim()}");
            var s = await Call(new Request { Op = "run", Args = ["__test", "echo", "ran"], Agent = "test", HeavyMB = estimate, Structured = true });
            Check("memory: the structured refusal", s.Code == 8 && s.Heavy is { Reason: "no_memory" } && s.Heavy.EstimateMB == estimate && s.Failure?.Fix == "try again later, or close other programs.",
                $"heavy: {s.Heavy?.Reason}, estimate {s.Heavy?.EstimateMB} MB, {s.Heavy?.AvailableMB} MB available");
            long refusedAfter = RefusedCount(await Status());
            Check("memory: nothing heavy was started", refusedAfter - refusedBefore == 3 && !(await Status()).Contains("__test echo ran"), $"status counts {refusedAfter - refusedBefore} more refused; the two marked by the caller never reached a worker");
            r = await Call(Cmd("__test", "echo", "an ordinary command"));
            Check("memory: an ordinary command still runs meanwhile", r.Code == 0 && r.Out.Trim() == "an ordinary command");
        }
        finally
        {
            File.WriteAllText(release, "go");
            if (!helper.WaitForExit(15000)) { try { helper.Kill(true); } catch (Exception) { } }
            File.Delete(release);
        }
        // Memory is back: the same job starts
        long back = -1;
        for (int i = 0; i < 100 && (back = AvailableMB()) < estimate + spare; i++) await Task.Delay(100);
        r = await Call(new Request { Op = "run", Args = ["__test", "echo", "ran"], Agent = "test", HeavyMB = estimate });
        Check("memory: started again once the memory was given back", r.Code == 0 && r.Out.Trim() == "ran", $"{back} MB available; {Need()}");
        Say($"lowest the PC went in this test: about {before - reserveMB} MB available.");
    }

    static long RefusedCount(string status)
    {
        var m = Regex.Match(status, @"(\d+) refused \(memory short\)");
        return m.Success ? long.Parse(m.Groups[1].Value) : -1;
    }

    // ================================================================ Cancelling
    static async Task Cancel()
    {
        // A running job
        async Task<long> CancelRunning(string name, Request start, Func<string, bool> running)
        {
            var first = await Call(start);
            if (first.Done) { Check(name, false, "it finished before it could be cancelled: " + first.Out + first.Err); return -1; }
            if (await UntilStatus(running, 5000) < 0) { Check(name, false, "it never showed as running"); return -1; }
            var sw = Stopwatch.StartNew();
            await Call(new Request { Op = "cancel", Job = first.Job });
            var end = await Call(new Request { Op = "job", Job = first.Job, WaitMs = 5000 });
            long answered = sw.ElapsedMilliseconds;
            long gone = await UntilStatus(s => !s.Contains("__test " + start.Args![1] + " " + start.Args[2]), 5000);
            Check(name, end.Done && end.Code == 4 && answered < 2000 && gone >= 0 && answered + gone < 2000, $"answered {answered} ms after the cancel (\"{end.Err.Trim()}\"); its worker was gone {answered + gone} ms after");
            return answered;
        }
        await CancelRunning("cancel: a running job stops within 2 s", new Request { Op = "run", Args = ["__test", "sleep", "60000"], Agent = "test", WaitMs = 300 }, s => s.Contains("running") && s.Contains("__test sleep 60000"));
        await CancelRunning("cancel: a heavy job stops within 2 s", new Request { Op = "run", Args = ["__test", "heavy", "3000", "61000", "256"], Agent = "test", WaitMs = 300, AllowHeavy = true }, s => s.Contains("Heavy lane: job"));
        string st = await Status();
        Check("cancel: the heavy lane is free again", st.Contains("Heavy lane: free"), st.Split('\n').FirstOrDefault(l => l.StartsWith("Heavy lane:"))?.Trim() ?? "");

        // A job waiting its turn for the heavy lane
        var holder = await Call(new Request { Op = "run", Args = ["__test", "heavy", "3000", "62000"], Agent = "test", WaitMs = 300, AllowHeavy = true });
        await UntilStatus(s => s.Contains("Heavy lane: job"), 5000);
        var waiter = await Call(new Request { Op = "run", Args = ["__test", "heavy", "3000", "63000"], Agent = "test2", WaitMs = 800, AllowHeavy = true });
        var sw2 = Stopwatch.StartNew();
        await Call(new Request { Op = "cancel", Job = waiter.Job });
        var wend = await Call(new Request { Op = "job", Job = waiter.Job, WaitMs = 5000 });
        Check("cancel: a job waiting for the heavy lane stops within 2 s", wend.Done && wend.Code == 4 && sw2.ElapsedMilliseconds < 2000, $"{sw2.ElapsedMilliseconds} ms (it was: {waiter.State})");
        sw2.Restart();
        await Call(new Request { Op = "cancel", Job = holder.Job });
        await Call(new Request { Op = "job", Job = holder.Job, WaitMs = 5000 });
        Say($"the job holding the lane, cancelled: {sw2.ElapsedMilliseconds} ms");

        // Cancelled by its key, before the caller has a job number (a call that waits for the whole answer)
        string key = Wire.NewKey();
        var call = Call(new Request { Op = "run", Args = ["__test", "sleep", "64000"], Agent = "test", WaitMs = -1, Key = key });
        await UntilStatus(s => s.Contains("__test sleep 64000"), 5000);
        var sw3 = Stopwatch.StartNew();
        await Call(new Request { Op = "cancel", Key = key });
        var kend = await call;
        Check("cancel: by the call's key, with no job number", kend.Code == 4 && sw3.ElapsedMilliseconds < 2000, $"{sw3.ElapsedMilliseconds} ms");
        key = Wire.NewKey();
        await Call(new Request { Op = "cancel", Key = key });
        var late = await Call(new Request { Op = "run", Args = ["__test", "echo", "should not run"], Agent = "test", Key = key });
        Check("cancel: a key cancelled before its call arrives", late.Code == 4 && late.Out == "", late.Err.Trim());

        // Through the MCP server: the client cancels its call
        if (File.Exists(Mcp))
        {
            foreach (var (label, afterMs, arg) in new[] { ("in the call's first seconds", 1000, "65000"), ("after 6 s", 6000, "66000") })
            {
                using var mcp = await RawMcp.Start(Mcp, Home);
                mcp.Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"sfs_run\",\"arguments\":{\"args\":[\"__test\",\"sleep\",\"" + arg + "\"]}}}");
                // The server lists the 12 new tools since milestone M8: its sfs_run sends the command as a tool call, which the status shows as its arguments
                bool Running(string s) => s.Contains("__test sleep " + arg) || s.Contains("\"sleep\",\"" + arg + "\"");
                if (await UntilStatus(Running, 8000) < 0) { Check($"cancel: an MCP call cancelled {label}", false, "the call never reached the service"); continue; }
                await Task.Delay(afterMs);
                mcp.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":2,\"reason\":\"the test cancelled it\"}}");
                long gone = await UntilStatus(s => !Running(s), 8000);
                Check($"cancel: an MCP call cancelled {label} stops its job within 2 s", gone >= 0 && gone < 2000, gone < 0 ? "the job was still running 8 s later" : $"{gone} ms after the client's notifications/cancelled");
                if (gone < 0) Say("the job is left to end by itself");
            }
        }
        else Say("no --mcp given: the MCP cancel checks were skipped");
        var workers = await Workers();
        Check("cancel: the service is whole afterwards", (await Call(Cmd("__test", "echo", "ok"))).Out.Trim() == "ok" && workers.All(w => w.Idle || w.Line.Contains("starting")), $"{workers.Count} workers, none still on a cancelled job");
    }

    // ================================================================ The indexer lane
    // ================================================================ Worker limits and the service's total
    // Needs a service started with small limits:
    //   SFSEARCH_WORKER_MAX_MB=1024  SFSEARCH_WORKER_RECYCLE_MB=300  SFSEARCH_SERVICE_MAX_MB=1024  SFSEARCH_WORKERS=4
    static async Task Limits()
    {
        string cfg = (await RunExe(Exe, ["Service", "config"])).Out;
        Say("in effect: " + string.Join(" ", cfg.Replace("\r", "").Split('\n').Where(l => l.StartsWith("Workers:") || l.StartsWith("Memory"))));
        if (!cfg.Contains("stopped above 1024 MB") || !cfg.Contains("The workers together: 1 GB (serviceMaxMB, set by SFSEARCH_SERVICE_MAX_MB)")) { Check("limits: the service has the test's small limits", false, "start it with SFSEARCH_WORKER_MAX_MB=1024 SFSEARCH_WORKER_RECYCLE_MB=300 SFSEARCH_SERVICE_MAX_MB=1024"); return; }

        // Stopped above the worker limit
        var sw = Stopwatch.StartNew();
        var r = await Call(Cmd("__test", "alloc", "1500", "8000"));
        Check("limits: a worker above the limit is stopped", r.Code == 5 && r.Err.Contains("it used more than 1 GB of memory"), $"after {sw.ElapsedMilliseconds} ms: {r.Err.Trim()}");

        // A heavy command may go past the worker limit, up to its estimate and half again
        r = await Call(new Request { Op = "run", Args = ["__test", "heavy", "2048", "2500", "1500"], Agent = "test", AllowHeavy = true });
        Check("limits: a heavy job may hold more than the worker limit", r.Code == 0 && r.Out.Contains("held 1536 MB"), r.Out.Trim());

        // Recycled after a command that left it above the recycle limit
        var before = (await Workers()).Select(x => x.Pid).ToHashSet();
        r = await Call(Cmd("__test", "keep", "400"));
        await Task.Delay(1500);
        var after = (await Workers()).Select(x => x.Pid).ToHashSet();
        Check("limits: a worker left above the recycle limit is replaced", r.Code == 0 && before.Except(after).Any(), $"{r.Out.Trim()}; worker process(es) {string.Join(", ", before.Except(after))} gone");

        // The service's total: two jobs that together hold more than it, then a third that must wait for one to end
        // 640 MB each: under one command's 1 GB less the 320 MB a server worker keeps for memory outside its heap (J57)
        var a = Call(Cmd("__test", "alloc", "640", "6000"));
        var b = Call(Cmd("__test", "alloc", "640", "6000"));
        await UntilStatus(s => s.Contains("(over: no new command starts"), 6000);
        string mem = (await Status()).Split('\n').FirstOrDefault(l => l.StartsWith("Memory:"))?.Trim() ?? "";
        sw.Restart();
        var c = await Call(Cmd("__test", "echo", "third"));
        long waited = sw.ElapsedMilliseconds;
        await Task.WhenAll(a, b);
        Check("limits: no new work starts while the workers together are over the service's total", c.Code == 0 && c.Out.Trim() == "third" && waited > 1500 && mem.Contains("(over"),
            $"the third command waited {waited} ms, until one of the two ended. Status meanwhile: {mem}");
        sw.Restart();
        c = await Call(Cmd("__test", "echo", "after"));
        Check("limits: work starts again when memory is back under", c.Out.Trim() == "after" && sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");
    }
}
