using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;

namespace SFSearch.ServiceTest;

public static partial class T
{
    /// <summary>
    /// A small version of the soak test in service_test.md. Clients ask at random, all at once: some through the command
    /// line (the verbs), some through MCP servers (the 12 tools). Meanwhile workers are killed, an MCP server is killed, junk
    /// is sent to the service's pipe, and a plugin copy's date is changed. Every answer is compared with a baseline
    /// taken first, one command at a time. It passes when no answer is wrong and nothing hangs.
    /// </summary>
    static async Task Soak(int seconds, int cliClients, int adapters, int perAdapter, string? touch)
    {
        if (!File.Exists(Exe) || !File.Exists(Mcp)) { Console.WriteLine("soak needs --exe and --mcp"); _fail++; return; }
        // Baseline: twice, so first reads are done; the second is kept. The command lines, and the 12 tools through an MCP server.
        var baseCli = new Dictionary<string, string>();
        var baseNew = new Dictionary<string, string>();
        await using (var one = await StartMcp(Mcp))
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var c in Cases)
                {
                    var (o, _, code) = await RunExe(Exe, c.Cli);
                    baseCli[c.Name] = $"exit {code}\n{NormalNew(o)}";
                }
                foreach (var c in NewCases)
                {
                    var (text, isError) = await Tool(one, c.Tool, c.Args);
                    baseNew[c.Name] = $"isError {isError}\n{NormalNew(text)}";
                }
            }
        Say($"baseline: {Cases.Length} command lines and {NewCases.Length} tool calls, one at a time");
        long totalNew = 0, sameNew = 0;

        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        long total = 0, same = 0, wrong = 0, clearError = 0, hangs = 0, lostToAdapterKill = 0;
        int workersKilled = 0, adaptersKilled = 0, junk = 0, touched = 0;
        var wrongs = new ConcurrentBag<string>();
        var times = new ConcurrentBag<long>();

        // A killed worker's command is run again once. Killed on both tries, it ends with one of these clear errors.
        static bool ClearError(string text) =>
            text.Contains("crashed on this command twice") || text.Contains("the command was stopped") || text.Contains("couldn't be reached") || text.Contains("The service is busy");

        void Judge(string route, Case c, string got, string expected, long ms)
        {
            Interlocked.Increment(ref total);
            if (route == "new") { Interlocked.Increment(ref totalNew); if (got == expected) Interlocked.Increment(ref sameNew); }
            times.Add(ms);
            if (ms > 120000) Interlocked.Increment(ref hangs);
            if (got == expected) { Interlocked.Increment(ref same); return; }
            if (ClearError(got)) { Interlocked.Increment(ref clearError); return; }
            Interlocked.Increment(ref wrong);
            if (wrongs.Count < 12) wrongs.Add($"{route} {c.Name} at {DateTime.Now:HH:mm:ss.fff}:\n{FirstDifference(expected, got)}\n--- expected, whole\n{expected}\n--- got, whole\n{got}\n--- end");
        }

        // Where two answers part: the first line that differs, with the line before it and the three after, from each.
        // (The first 200 characters of each, as before, showed nothing when the difference came later.)
        static string FirstDifference(string expected, string got)
        {
            var a = expected.Split('\n'); var b = got.Split('\n');
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            string Around(string[] lines) => string.Join("\n", lines.Skip(Math.Max(0, i - 1)).Take(5).Select(l => "    " + (l.Length > 600 ? l[..600] + "…" : l)));
            return $"the first line that differs is line {i + 1} (expected has {a.Length} lines, got has {b.Length})\n--- expected, from line {Math.Max(1, i)}\n{Around(a)}\n--- got, from line {Math.Max(1, i)}\n{Around(b)}";
        }

        var tasks = new List<Task>();
        for (int i = 0; i < cliClients; i++)
        {
            var rnd = new Random(1000 + i);
            tasks.Add(Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var c = Cases[rnd.Next(Cases.Length)];
                    var sw = Stopwatch.StartNew();
                    var (o, e, code) = await RunExe(Exe, c.Cli, 150000);
                    string got = code == -99 ? e : $"exit {code}\n{NormalNew(o)}";
                    if (got != baseCli[c.Name] && ClearError(e)) got = e; // the error line is on stderr
                    Judge("cli", c, got, baseCli[c.Name], sw.ElapsedMilliseconds);
                }
            }));
        }
        // Every MCP server lists the 12 tools (the 25 older ones went with the older commands in 2.0)
        int servers = adapters;
        var clients = new McpClient?[servers];
        var clientLocks = Enumerable.Range(0, servers).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
        for (int a = 0; a < servers; a++) clients[a] = await StartMcp(Mcp);
        for (int a = 0; a < servers; a++)
            for (int k = 0; k < perAdapter; k++)
            {
                int slot = a;
                var rnd = new Random(2000 + a * 100 + k);
                tasks.Add(Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var c = NewCases[rnd.Next(NewCases.Length)];
                        var sw = Stopwatch.StartNew();
                        var client = clients[slot];
                        try
                        {
                            if (client == null) throw new IOException("no adapter");
                            using var limit = new CancellationTokenSource(150000);
                            var (text, isError) = await Tool(client, c.Tool, c.Args, limit.Token);
                            Judge("new", c, $"isError {isError}\n{NormalNew(text)}", baseNew[c.Name], sw.ElapsedMilliseconds);
                        }
                        catch (OperationCanceledException) { Interlocked.Increment(ref total); Interlocked.Increment(ref hangs); }
                        catch (Exception)
                        {
                            // The test killed this MCP server: its calls are lost. Start another and go on.
                            Interlocked.Increment(ref lostToAdapterKill);
                            await clientLocks[slot].WaitAsync();
                            try { if (ReferenceEquals(clients[slot], client)) clients[slot] = await StartMcp(Mcp); }
                            finally { clientLocks[slot].Release(); }
                        }
                    }
                }));
            }

        // Chaos
        var chaos = Task.Run(async () =>
        {
            var rnd = new Random(7);
            var mcpPidsAtStart = Process.GetProcessesByName("SFSearch.Mcp").Select(p => p.Id).ToHashSet();
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(rnd.Next(3000, 7000), stop.Token); } catch (OperationCanceledException) { break; }
                try
                {
                    switch (rnd.Next(10))
                    {
                        case < 6:
                            {
                                var ws = await Workers();
                                if (ws.Count == 0) break;
                                var victim = ws[rnd.Next(ws.Count)];
                                try { Process.GetProcessById(victim.Pid).Kill(); workersKilled++; } catch (Exception) { }
                                break;
                            }
                        case 6:
                            {
                                // One of this test's own MCP servers (found by its command line's --home)
                                foreach (var p in Process.GetProcessesByName("SFSearch.Mcp"))
                                {
                                    try
                                    {
                                        if (!string.Equals(Path.GetFullPath(p.MainModule!.FileName), Path.GetFullPath(Mcp), StringComparison.OrdinalIgnoreCase)) continue;
                                        if (!MineByHome(p.Id)) continue;
                                        p.Kill(); adaptersKilled++;
                                        break;
                                    }
                                    catch (Exception) { }
                                }
                                break;
                            }
                        case 7:
                        case 8:
                            foreach (var bad in new[] { "{not json", "{\"op\":\"nonsense\"}", "{\"op\":\"run\"}", "{\"op\":\"job\",\"job\":\"j0-1\"}", "", "{\"op\":\"run\",\"cursor\":\"x@y\"}" })
                            {
                                try { await RawCall(bad); } catch (Exception) { }
                                junk++;
                            }
                            break;
                        default:
                            if (touch != null && File.Exists(touch)) { File.SetLastWriteTime(touch, DateTime.Now); touched++; }
                            break;
                    }
                }
                catch (Exception e) { Say("chaos: " + e.Message); }
            }
        });

        await Task.WhenAll(tasks);
        await chaos;
        foreach (var c in clients) if (c != null) try { await c.DisposeAsync(); } catch (Exception) { }

        string status = await Status();
        var sorted = times.OrderBy(t => t).ToArray();
        long P(double q) => sorted.Length == 0 ? 0 : sorted[(int)Math.Min(sorted.Length - 1, q * sorted.Length)];
        Say($"clients: {cliClients} through the command line, {adapters * perAdapter} through {adapters} MCP servers with the 12 tools ({cliClients + servers * perAdapter} in all), for {seconds} s");
        Say($"tools: {totalNew} calls of {NewCases.Length} kinds, {sameNew} the same as the baseline");
        Say($"chaos: {workersKilled} workers killed, {adaptersKilled} MCP servers killed, {junk} junk requests, the plugin copy's date changed {touched} time(s)");
        Say($"commands: {total}; same as the baseline: {same}; clear error (a command whose two tries were both killed): {clearError}; wrong: {wrong}; hung (over 120 s): {hangs}; calls lost with a killed MCP server and asked again: {lostToAdapterKill}");
        Say($"time per command, asking to answer: median {P(0.5)} ms, 90% under {P(0.9)} ms, slowest {P(1)} ms");
        Say("service: " + status.Split('\n')[0].Trim());
        foreach (var wr in wrongs) Console.WriteLine("WRONG " + wr);
        var after = await Call(Cmd("find", "000808", "--in", "atom74.esm"));
        Check("soak: no wrong answer", wrong == 0 && total > 0, $"{same} of {total} the same as the baseline, {clearError} clear errors");
        Check("soak: no hang", hangs == 0, $"slowest answer {P(1)} ms");
        Check("soak: the service still answers afterwards", after.Code == 0 && after.Out.Contains("mod_ccAtom74_Barrel_Standard"));
    }

    /// <summary>True when that MCP server process was started for this test's home (so the user's own are never touched).</summary>
    static bool MineByHome(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine");
            using var p = Process.Start(psi)!;
            string cmd = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return cmd.Contains(Home, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
