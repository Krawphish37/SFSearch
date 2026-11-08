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
    /// <summary>Answers longer than this go to a file; the call returns the start and the path.</summary>
    const int InlineLimit = 20000;

    public static async Task<CallToolResult> Run(string[] args, IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        if (args.Length == 0) return Error("No command given.");
        // Commands only read, so one cut off by a service restart is simply sent again (once).
        for (int attempt = 0; ; attempt++)
        {
            var first = await Call(new Request { Op = "run", Args = args, Cwd = Environment.CurrentDirectory, Agent = Agent, WaitMs = 4000 }, ct);
            if (first.Job != null) JobArgs[first.Job] = args;
            var result = await Follow(first, progress, ct);
            if (attempt > 0 || !LostToRestart(result)) return result;
            progress?.Report(new ProgressNotificationValue { Progress = 0, Message = "the service restarted; asking again" });
        }
    }

    const string StoppedMark = "the service stopped before this command finished";
    const string LostMark = "was lost. Ask again.";

    static bool LostToRestart(CallToolResult r) =>
        r.IsError == true && r.Content.OfType<TextContentBlock>().Any(t => t.Text.Contains(StoppedMark) || t.Text.Contains(LostMark) || t.Text.StartsWith("No job "));

    public static async Task<CallToolResult> Collect(string job, int waitSeconds, IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        var first = await Call(new Request { Op = "job", Job = job, WaitMs = 4000 }, ct);
        var result = await Follow(first, progress, ct, Math.Clamp(waitSeconds, 0, 45) * 1000);
        // A job cut off by a service restart: this adapter remembers what it was, so it asks again
        if (LostToRestart(result) && JobArgs.TryGetValue(job, out var args))
        {
            progress?.Report(new ProgressNotificationValue { Progress = 0, Message = "the service restarted; asking again" });
            return await Run(args, progress, ct);
        }
        return result;
    }

    /// <summary>The command behind each job number this adapter handed out (jobs are kept an hour by the service).</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> JobArgs = new();

    public static async Task<CallToolResult> Status(CancellationToken ct)
    {
        var r = await Call(new Request { Op = "status" }, ct);
        return r.Error != null ? Error(r.Error) : Text(r.Out);
    }

    static async Task<CallToolResult> Follow(Reply r, IProgress<ProgressNotificationValue>? progress, CancellationToken ct, int budgetMs = CallBudgetMs)
    {
        var clock = Stopwatch.StartNew();
        int tick = 0;
        while (!r.Done)
        {
            if (r.Error != null) return Error(r.Error);
            progress?.Report(new ProgressNotificationValue { Progress = ++tick, Message = $"{r.State ?? "running"} ({clock.Elapsed.TotalSeconds:0} s)" });
            if (ct.IsCancellationRequested)
            {
                try { await Call(new Request { Op = "cancel", Job = r.Job }, CancellationToken.None); } catch (Exception) { }
                return Error($"Cancelled (job {r.Job}).");
            }
            if (clock.ElapsedMilliseconds >= budgetMs)
                return Text($"Still working on it (job {r.Job}: {r.State}, {r.Ms / 1000} s so far). The service keeps going: call sfs_job with job \"{r.Job}\" to get the answer. "
                    + "A first read of a big plugin (Starfield.esm) can take a minute; later questions about it are fast.");
            try { r = await Call(new Request { Op = "job", Job = r.Job, WaitMs = 4000 }, ct); }
            catch (OperationCanceledException) { continue; } // loop sends the cancel
        }
        if (r.Error != null) return Error(r.Error);
        return Shape(r);
    }

    static CallToolResult Shape(Reply r)
    {
        var sb = new StringBuilder(r.Out);
        if (!string.IsNullOrWhiteSpace(r.Err))
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            sb.Append("\n[notes]\n").Append(r.Err.TrimEnd()).Append('\n');
        }
        string text = sb.ToString();
        if (text.Trim().Length == 0) text = r.Code == 0 ? "(no output)" : $"(no output, exit code {r.Code})";
        if (text.Length > InlineLimit)
        {
            string dir = Path.Combine(Home, "Service", "results");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{r.Job}.md");
            File.WriteAllText(file, text, new UTF8Encoding(false));
            var lines = text.Split('\n');
            var head = new StringBuilder();
            foreach (var l in lines) { if (head.Length + l.Length > 6000) break; head.Append(l).Append('\n'); }
            text = head + $"\n… The full answer is {lines.Length:N0} lines ({text.Length / 1024:N0} KB), saved to:\n{file}\nRead that file for the rest, or narrow the question (a WHERE, a LIMIT, fewer columns).\n";
        }
        bool failed = r.Code != 0 && (r.Out.Trim().Length == 0 || r.Out.TrimStart().StartsWith("Error:")); // a command that only printed an error
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
