using System.Diagnostics;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SFSearch.Service;

namespace SFSearch.Mcp;

/// <summary>Passes tool calls to the SFSearch service and shapes the answers for an agent.</summary>
public static class Bridge
{
    public static string Home = "";
    static readonly string Agent = $"mcp-{Environment.ProcessId}";
    static readonly SemaphoreSlim StartGate = new(1, 1);

    /// <summary>How long one tool call waits before handing back a job number.</summary>
    const int CallBudgetMs = 45000;

    const string StoppedMark = "the service stopped before this command finished";
    const string LostMark = "was lost. Ask again.";

    static bool LostToRestart(CallToolResult r) =>
        r.IsError == true && r.Content.OfType<TextContentBlock>().Any(t => t.Text.Contains(StoppedMark) || t.Text.Contains(LostMark) || t.Text.StartsWith("No job "));

    // ---------------------------------------------------------------- The 12 tools: the service's structured form
    /// <summary>An answer comes back in pages of this many characters (about 6,000 tokens) unless the call's max_chars says more.</summary>
    const int PageChars = 24000;
    /// <summary>Room on top of a tool's own budget, so the service cuts only what a tool didn't already cut itself.</summary>
    const int PageSlack = 8000;
    static readonly System.Text.RegularExpressions.Regex ServiceCursor = new(@"^j[0-9a-f]+-\d+@\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// One call of a new tool: its name and named arguments go to the service as they are, and the structured answer
    /// is rendered as the output contract's text (or, for format json, as one JSON object). There is one cursor for
    /// the caller: whether a [more] line's cursor is the service's (a page of a long answer it keeps) or the tool's
    /// own (the next rows), it is passed back the same way, and this method tells them apart.
    /// </summary>
    public static async Task<CallToolResult> Tool(string tool, Dictionary<string, object?> pars, string? cursor, string? format, int? maxChars, bool allowHeavy,
        IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        if (!TryFormat(format, out bool json, out var bad)) return bad!;
        int page = Math.Max(maxChars ?? 0, PageChars) + PageSlack;
        var p = pars.ToDictionary(kv => kv.Key, kv => System.Text.Json.JsonSerializer.SerializeToElement(kv.Value));
        if (json) p["format"] = System.Text.Json.JsonSerializer.SerializeToElement("json");
        cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim().Trim('"');
        bool servicePage = cursor != null && ServiceCursor.IsMatch(cursor);
        if (cursor != null && !servicePage) p["cursor"] = System.Text.Json.JsonSerializer.SerializeToElement(cursor);
        for (int attempt = 0; ; attempt++)
        {
            string key = Wire.NewKey();
            var request = servicePage
                ? new Request { Op = "run", Cursor = cursor, PageChars = page, Agent = Agent }
                : new Request { Op = "run", Tool = tool, Params = p, Structured = true, PageChars = page, AllowHeavy = allowHeavy, Cwd = Environment.CurrentDirectory, Agent = Agent, WaitMs = 4000, Key = key };
            Reply first;
            try { first = await Call(request, ct); }
            catch (OperationCanceledException)
            {
                try { await Wire.CallAsync(Home, new Request { Op = "cancel", Key = key }, 2000, CancellationToken.None); }
                catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { }
                throw;
            }
            var result = await FollowParts(first, json, page, progress, ct);
            // A service restart loses a job; commands only read, so the call is sent again once
            if (attempt > 0 || servicePage || !LostToRestart(result)) return result;
            progress?.Report(new ProgressNotificationValue { Progress = 0, Message = "the service restarted; asking again" });
        }
    }

    static bool TryFormat(string? format, out bool json, out CallToolResult? bad)
    {
        json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        bad = json || string.IsNullOrWhiteSpace(format) || string.Equals(format, "text", StringComparison.OrdinalIgnoreCase) ? null
            : Error($"Error: there is no format '{format}'. Fix: use text (the default) or json.");
        return bad == null;
    }

    /// <summary>sfs_job for the new tools: collect a job's answer in the same parts, or cancel it.</summary>
    public static async Task<CallToolResult> Job(string job, int waitSeconds, bool cancel, string? format, IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        if (!TryFormat(format, out bool json, out var bad)) return bad!;
        job = job.Trim().Trim('"');
        if (cancel)
        {
            var c = await Call(new Request { Op = "cancel", Job = job }, ct);
            if (c.Error != null) return Error($"Error: {c.Error.TrimEnd('.')}. Fix: check the job number; answers are kept for an hour, and not across a service restart.");
            return Text(c.Out.Trim().Length > 0 ? c.Out : $"Job {job} was cancelled.\n");
        }
        var first = await Call(new Request { Op = "job", Job = job, WaitMs = 4000, Structured = true, PageChars = PageChars + PageSlack }, ct);
        return await FollowParts(first, json, PageChars + PageSlack, progress, ct, Math.Clamp(waitSeconds, 0, 45) * 1000);
    }

    /// <summary>
    /// sfs_status for the new tools. The detailed part (the indexes, the scratch folder, what is open) is made by a
    /// worker; when every worker is busy for 8 s, the service's own lines are the answer and say so.
    /// </summary>
    public static async Task<CallToolResult> StatusNew(string? format, CancellationToken ct)
    {
        if (!TryFormat(format, out bool json, out var bad)) return bad!;
        string key = Wire.NewKey();
        var p = new Dictionary<string, System.Text.Json.JsonElement>();
        if (json) p["format"] = System.Text.Json.JsonSerializer.SerializeToElement("json");
        var r = await Call(new Request { Op = "run", Tool = "sfs_status", Params = p, Structured = true, Agent = Agent, WaitMs = 8000, Key = key }, ct);
        if (r.Done && r.Error == null) return Shape(r, json);
        if (!r.Done)
        {
            try { await Wire.CallAsync(Home, new Request { Op = "cancel", Key = key }, 2000, CancellationToken.None); }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { }
        }
        var s = await Call(new Request { Op = "status", Structured = true }, ct);
        if (s.Error != null) return Error(s.Error);
        return Text(s.Out + "[notes] No worker was free within 8 s, so the indexes, the scratch folder's size and the open plugins aren't shown. Ask again.\n");
    }

    static async Task<CallToolResult> FollowParts(Reply r, bool json, int page, IProgress<ProgressNotificationValue>? progress, CancellationToken ct, int budgetMs = CallBudgetMs)
    {
        var clock = Stopwatch.StartNew();
        int tick = 0;
        while (!r.Done)
        {
            if (r.Error != null) return Error(ErrorLine(r.Error));
            progress?.Report(new ProgressNotificationValue { Progress = ++tick, Message = $"{r.State ?? "running"} ({clock.Elapsed.TotalSeconds:0} s)" });
            if (ct.IsCancellationRequested)
            {
                try { await Call(new Request { Op = "cancel", Job = r.Job }, CancellationToken.None); } catch (Exception) { }
                return Error($"Cancelled (job {r.Job}).");
            }
            if (clock.ElapsedMilliseconds >= budgetMs)
                return Text($"Still working on it (job {r.Job}: {r.State ?? "running"}, {r.Ms / 1000} s so far). The service keeps going: call sfs_job with job \"{r.Job}\" to collect the answer, or with cancel=true to stop it.\n");
            try { r = await Call(new Request { Op = "job", Job = r.Job, WaitMs = 4000, Structured = true, PageChars = page }, ct); }
            catch (OperationCanceledException) { continue; } // the loop sends the cancel
        }
        // The service couldn't run the request at all (no such job, busy, a cursor it no longer keeps): one line, with what to do
        if (r.Error != null) return Error(ErrorLine(r.Error));
        return Shape(r, json);
    }

    static string ErrorLine(string error) =>
        error.StartsWith("Error:", StringComparison.Ordinal) ? error
        : $"Error: {error.TrimEnd('.', ' ')}. Fix: {(error.StartsWith("No job", StringComparison.Ordinal) ? "check the job number: answers are kept for an hour, and not across a service restart" : error.Contains("busy", StringComparison.OrdinalIgnoreCase) ? "ask again in a moment" : "ask again; sfs_status shows whether the service is well")}.";

    /// <summary>A structured reply as the tool's result: the contract's text, or one JSON object.</summary>
    static CallToolResult Shape(Reply r, bool json)
    {
        string text = json ? Wire.RenderJson(r) : Wire.Render(r);
        if (text.Trim().Length == 0) text = r.Code == 0 ? "(no output)\n" : $"(no output, exit code {r.Code})\n";
        // "Not run" (a heavy call that wasn't allowed) is an answer, not an error: it says the cost and how to allow it
        bool failed = r.Failure != null || r.Code != 0 && r.Heavy == null && (r.Answer ?? r.Out).Trim().Length == 0;
        return new CallToolResult { Content = [new TextContentBlock { Text = text }], IsError = failed };
    }

    static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };
    static CallToolResult Error(string text) => new() { Content = [new TextContentBlock { Text = text }], IsError = true };

    /// <summary>One request to the service, starting it first when it isn't running.</summary>
    static async Task<Reply> Call(Request req, CancellationToken ct)
    {
        // A service that is stopping (it lets running commands finish, up to 15 s) or starting doesn't answer for a while:
        // keep trying for up to 90 s while its process is alive, and start one when none is.
        var deadline = DateTime.Now.AddSeconds(90);
        bool started = false;
        for (int attempt = 0; ; attempt++)
        {
            try { return await Wire.CallAsync(Home, req, 5000, ct); }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                if (DateTime.Now > deadline) return new Reply { Done = true, Code = 2, Error = $"The SFSearch service couldn't be reached ({e.Message}). Check 'sfs.cmd Service status' and the log in {Path.Combine(Home, "Service", "logs")}." };
                if (Wire.RunningService(Home) != null || started) { await Task.Delay(500, ct); continue; }
                started = true;
                string? err = await EnsureService(ct);
                if (err != null) return new Reply { Done = true, Code = 2, Error = err };
                if (req.Op == "job") return new Reply { Done = true, Code = 2, Error = $"The service restarted, so job {req.Job} was lost. Ask again." };
            }
        }
    }

    /// <summary>Starts the service through sfs.ps1, which rebuilds SFSearch first when its code changed. Null when it runs.</summary>
    static async Task<string?> EnsureService(CancellationToken ct)
    {
        await StartGate.WaitAsync(ct);
        try
        {
            if (Wire.RunningService(Home) != null)
            {
                try { await Wire.CallAsync(Home, new Request { Op = "ping" }, 3000, ct); return null; }
                catch (Exception e) when (e is TimeoutException or IOException) { }
            }
            string? script = StartScript();
            if (script == null) return $"sfs.ps1 wasn't found (looked in {Home} and beside this adapter). Start the service with 'sfs.cmd Service start'.";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                WorkingDirectory = Home,
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "Service", "start" }) psi.ArgumentList.Add(a);
            psi.Environment["SFSEARCH_HOME"] = Home;
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (Exception) { } return "Starting the SFSearch service took longer than 5 minutes."; }
            string output = (await outTask) + (await errTask);
            if (p.ExitCode != 0) return $"The SFSearch service didn't start (exit code {p.ExitCode}): {output.Trim()}";
            for (int i = 0; i < 50; i++)
            {
                try { await Wire.CallAsync(Home, new Request { Op = "ping" }, 1000, ct); return null; }
                catch (Exception e) when (e is TimeoutException or IOException) { await Task.Delay(200, ct); }
            }
            return "The SFSearch service didn't start: " + output.Trim();
        }
        finally { StartGate.Release(); }
    }

    /// <summary>sfs.ps1 (it rebuilds SFSearch when its code changed, then runs it): in the home folder, or in the SFSearch
    /// project beside this adapter's project (the data home can be elsewhere, through SFSEARCH_HOME).</summary>
    static string? StartScript()
    {
        string inHome = Path.Combine(Home, "sfs.ps1");
        if (File.Exists(inHome)) return inHome;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "SFSearch", "sfs.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
