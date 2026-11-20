using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SFSearch.Service;

namespace SFSearch.ServiceTest;

/// <summary>
/// Entry point and the helpers every test uses. Each test prints PASS or FAIL lines with the numbers it measured, and
/// the program's exit code is the number of FAIL lines.
/// </summary>
public static partial class T
{
    public static string Home = "";
    public static string Exe = "";      // SFSearch.exe: the command-line client
    public static string Mcp = "";      // SFSearch.Mcp.exe
    static int _pass, _fail;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        ThreadPool.SetMinThreads(64, 64);
        if (args.Length == 0) { Usage(); return 2; }
        var rest = args.Skip(1).ToList();
        string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
        Home = Opt("--home") ?? Environment.GetEnvironmentVariable("SFSEARCH_HOME") ?? "";
        Exe = Opt("--exe") ?? "";
        Mcp = Opt("--mcp") ?? "";
        switch (args[0].ToLowerInvariant())
        {
            case "reserve": return Reserve(rest);
            case "avail": Console.WriteLine($"{AvailableMB()} MB available of {TotalMB()} MB"); return 0;
            case "compare": return Compare(rest);
            case "giveway": GiveWayRule(); Console.WriteLine($"\ngiveway: {_pass} passed, {_fail} failed."); return _fail;
        }
        if (Home.Length == 0 || !Directory.Exists(Home)) { Console.WriteLine("Give the test home with --home (or SFSEARCH_HOME). Never the user's own SFSearch folder."); return 2; }
        Home = Path.GetFullPath(Home);
        AddOn = Opt("--addon") ?? DefaultAddOn();
        var clock = Stopwatch.StartNew();
        // The tests that drive the service with `__test` put the test add-on into the test home's extensions folder, and
        // take it out again at the end (so --help and the add-on lines of other tests stay as a user's home has them)
        bool hooks = args[0].ToLowerInvariant() is "protocol" or "scratch-after-restart" or "heavy" or "memory" or "cancel" or "indexer" or "limits" or "idleworkers" or "smallpc";
        if (hooks && !InstallAddOn(Home)) { Console.WriteLine($"The test add-on wasn't found at {AddOn}. Build it first: dotnet build tests\\SFSearch.TestHooks -c Release -p:SfsExtensionsDir=<any folder outside the repo> (or name the .dll with --addon)."); return 2; }
        try
        {
        switch (args[0].ToLowerInvariant())
        {
            case "capture": await Capture(Opt("--out") ?? "capture"); break;
            case "protocol": await Protocol(); break;
            case "scratch-after-restart": await ScratchAfterRestart(); break;
            case "heavy": await Heavy(); break;
            case "memory": await Memory(Opt("--bash") ?? "bash", Opt("--heavy-sh"), int.Parse(Opt("--reserve-mb") ?? "3072")); break;
            case "cancel": await Cancel(); break;
            case "indexer": await Indexer(int.Parse(Opt("--records") ?? "50000")); break;
            case "limits": await Limits(); break;
            case "smallpc": await SmallPc(int.Parse(Opt("--records") ?? "50000")); break;
            case "olderfile": await OlderFile(Opt("--older")); break;
            case "idleworkers": await IdleWorkers(); break;
            case "historylook": await HistoryLook(Opt("--data")); break;
            case "keptindexes": await KeptIndexes(); break;
            case "letgo": await LetGo(); break;
            case "keptremoved": await KeptRemoved(); break;
            case "worldindex": await WorldIndex(); break;
            case "saveindex": await SaveIndex(); break;
            case "settingstext": await SettingsTextTest(); break;
            case "gccollector": await GcCollector(); break;
            case "smallgc": await SmallGc(Opt("--data")); break;
            case "keptrestart": await KeptRestart(Opt("--data")); break;
            case "wording": await Wording(); break;
            case "inplace": await InPlace(Opt("--copy")); break;
            case "addoncrash": await AddOnCrash(); break;
            case "addon201": await AddOn201(); break;
            case "soak": await Soak(int.Parse(Opt("--seconds") ?? "180"), int.Parse(Opt("--cli") ?? "4"), int.Parse(Opt("--adapters") ?? "2"), int.Parse(Opt("--per-adapter") ?? "4"), Opt("--touch")); break;
            default: Usage(); return 2;
        }
        }
        finally { if (hooks) RemoveAddOn(Home); }
        Console.WriteLine($"\n{args[0]}: {_pass} passed, {_fail} failed, {clock.Elapsed.TotalSeconds:0.0} s.");
        return _fail;
    }

    static void Usage() => Console.WriteLine(
        "SFSearch.ServiceTest <test> --home <test home> [--exe SFSearch.exe] [--mcp SFSearch.Mcp.exe] [--addon SFSearch.TestHooks.dll]\n" +
        "  The tests that drive the service with __test put the test add-on (tests\\SFSearch.TestHooks, built with -p:SfsExtensionsDir=<a folder>)\n" +
        "  into the test home's extensions folder for the run, and take it out at the end. The service is the normal Release build.\n" +
        "  capture --out <folder>   run the fixed command lines through the command line, and their tool calls through the MCP server\n" +
        "  compare <a> <b>          compare two capture folders\n" +
        "  protocol | scratch-after-restart | heavy | cancel | limits | soak | idleworkers   (a service must run on the test home)\n" +
        "  memory [--heavy-sh <memory-slot script>] [--bash <bash>] [--reserve-mb N]   refused when short, started when there; the memory helper runs through the script (with bash) when one is named, else directly\n" +
        "  indexer --exe <SFSearch.exe> [--records N]   the indexer lane with the real link walk: low priority, gives way to agents at once, agents first, (its own service and test Data folder)\n" +
        "  giveway                  the coordinator's rule for an indexer job that doesn't give way: stopped 2 s after it was asked; not one that answered, not an agent's (plain code, no service)\n" +
        "  smallpc --exe <SFSearch.exe> [--records N]   the memory defaults of other PCs from their numbers; on this PC with real settings: the limit for one command, the floor (its own service and test Data folder)\n" +
        "  olderfile --exe <SFSearch.exe> [--older <an older sfsearch.json>]   its fixed memory numbers taken from this PC; the file rewritten once, or kept when it can't be (its own service and test Data folder)\n" +
        "  historylook --exe <SFSearch.exe> [--data <Data folder>]   the service looks for changed plugins by itself (pluginCheckSeconds 10); changes2 says which look it compares with (its own service and test Data folder)\n" +
        "  keptrestart --exe <SFSearch.exe> [--data <Data folder>]   start to link index complete: first start, kept restart (under 6 s), setting off (its own home; through heavy.sh)\n" +
        "  keptindexes --exe <SFSearch.exe>   the header and link indexes kept between starts: reused, one plugin rebuilt, a master's size change, a change in a file's middle, drop and build, the setting off (its own service and test Data folder)\n" +
        "  settingstext --exe <SFSearch.exe>   every setting explained in the sfsearch.json a fresh service writes (what, values, default, this PC's number), and Service config's value in effect and source for each (its own service and test Data folder)\n" +
        "  keptremoved --exe <SFSearch.exe>   keepRemovedPluginIndexMB: a plugin taken out of Data loses its parts (0, the default) or keeps them (a number), put back it isn't indexed again, the oldest go first over the size (its own service and test Data folder)\n" +
        "  worldindex --exe <SFSearch.exe>   the world link index made only when asked: build, show, refs and query2 from it, kept across a restart, a changed plugin's made again, drop forgets it (its own service and test Data folder)\n" +
        "  saveindex --exe <SFSearch.exe>    the third answer (save_index): refs world \"save_index\" and query2 --allow-heavy=save-index answer now and have the indexer lane make the world link index, the same question then answers from it; where no index helps it says so (its own service and test Data folder)\n" +
        "  gccollector --exe <SFSearch.exe>   the commands' garbage collector: auto (server in the workers; without the service server from 2.5 GB a command), workstation or server when set; the service itself on workstation; a big link walk on a workstation worker (its own service and test Data folder)\n" +
        "  smallgc --exe <SFSearch.exe> --data <Data folder>   an 8 GB PC's settings on 8 cores: every cell of Starfield.esm on a server worker finishes under 1.5 GB (its own home; through heavy.sh)\n" +
        "  wording                  --help, a mistyped verb, sfs_run [\"help\"]: the texts each gives (a service must run)\n" +
        "  addoncrash --exe <SFSearch.exe>   an add-on command that throws exits 2 with its error text (the CrashTest add-on built into the test home; no service)\n" +
        "  addon201 --exe <SFSearch.exe>   an add-on built for SFSearch 2.0.1 that calls JobContext's RequireHeavy, NotRun and NotAllowedLine runs (the AddOn201 add-on built into the test home; no service)\n" +
        "  inplace --copy <kgcdoom.esm in the test Data folder SFSEARCH_DATA names>   a change that keeps size and date (no service)\n" +
        "  reserve <MB> <seconds>   helper: take that much memory, touch it, hold it");

    // ---------------------------------------------------------------- The test add-on (tests\SFSearch.TestHooks)
    /// <summary>SFSearch.TestHooks.dll: the `__test` commands (J37: an add-on built only for test homes, never in the program).</summary>
    public static string AddOn = "";

    /// <summary>The add-on as the repo builds it: tests\SFSearch.TestHooks\bin\Release\net10.0, found from this program's own folder.</summary>
    static string DefaultAddOn()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string f = Path.Combine(d.FullName, "tests", "SFSearch.TestHooks", "bin", "Release", "net10.0", "SFSearch.TestHooks.dll");
            if (File.Exists(f)) return f;
        }
        return Path.Combine(AppContext.BaseDirectory, "SFSearch.TestHooks.dll");
    }

    /// <summary>Copies the test add-on into a home's extensions folder. A running service there loads it at its next command.</summary>
    public static bool InstallAddOn(string home)
    {
        if (!File.Exists(AddOn)) return false;
        string dir = Path.Combine(home, "extensions");
        Directory.CreateDirectory(dir);
        string to = Path.Combine(dir, Path.GetFileName(AddOn));
        if (!File.Exists(to) || !File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(AddOn))) File.Copy(AddOn, to, true);
        return true;
    }

    /// <summary>Takes the test add-on out of a home's extensions folder again.</summary>
    public static void RemoveAddOn(string home)
    {
        try { File.Delete(Path.Combine(home, "extensions", Path.GetFileName(AddOn))); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- Results
    public static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    public static void Say(string text) => Console.WriteLine("      " + text);

    // ---------------------------------------------------------------- The service
    public static Task<Reply> Call(Request r, CancellationToken ct = default) => Wire.CallAsync(Home, r, 5000, ct);

    public static Request Cmd(params string[] args) => new() { Op = "run", Args = args, Agent = "test", WaitMs = -1 };

    public static async Task<string> Status() => (await Call(new Request { Op = "status" })).Out;

    /// <summary>The worker processes, from the status text: id, whether it is idle, and its line.</summary>
    public static async Task<List<(int Pid, bool Idle, string Line)>> Workers()
    {
        var list = new List<(int, bool, string)>();
        foreach (var line in (await Status()).Split('\n'))
        {
            var m = Regex.Match(line, @"^\s+worker \d+ \(process (\d+)\): (\w+)");
            if (m.Success) list.Add((int.Parse(m.Groups[1].Value), m.Groups[2].Value == "idle", line.Trim()));
        }
        return list;
    }

    /// <summary>Waits until the status text matches (or doesn't), up to the time given. Returns the milliseconds it took, -1 on timeout.</summary>
    public static async Task<long> UntilStatus(Func<string, bool> when, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (when(await Status())) return clock.ElapsedMilliseconds;
            await Task.Delay(40);
        }
        return -1;
    }

    /// <summary>One request as raw JSON, the way a client built before the structured form would send it. Returns the raw reply line.</summary>
    public static async Task<string> RawCall(string json)
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", Wire.PipeName(Home), System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var w = Wire.Writer(pipe);
        await w.WriteLineAsync(json);
        await w.FlushAsync();
        using var r = Wire.Reader(pipe);
        return await r.ReadLineAsync() ?? "";
    }

    // ---------------------------------------------------------------- The command line and the MCP server
    public static async Task<(string Out, string Err, int Code)> RunExe(string exe, IEnumerable<string> args, int timeoutMs = 300000)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["SFSEARCH_HOME"] = Home;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (Exception) { } return ("", "TIMEOUT: the command line didn't answer", -99); }
        return (await o, await e, p.ExitCode);
    }

    /// <summary>An MCP server on the test home: the 12 tools.</summary>
    public static async Task<McpClient> StartMcp(string mcpExe)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sfsearch", Command = mcpExe, Arguments = ["--home", Home],
            EnvironmentVariables = new Dictionary<string, string?> { ["SFSEARCH_HOME"] = Home },
        });
        return await McpClient.CreateAsync(transport);
    }

    public static async Task<(string Text, bool IsError)> Tool(McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct = default)
    {
        var r = await client.CallToolAsync(tool, args, cancellationToken: ct);
        return (string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text)), r.IsError == true);
    }

    // ---------------------------------------------------------------- Memory (the same figure the service uses)
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

    public static long AvailableMB() { var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() }; GlobalMemoryStatusEx(ref m); return (long)(m.AvailPhys / 1048576); }
    public static long TotalMB() { var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() }; GlobalMemoryStatusEx(ref m); return (long)(m.TotalPhys / 1048576); }

    /// <summary>reserve MB seconds: takes memory in 64 MB blocks, writes to every page, says so, holds it, exits.</summary>
    static int Reserve(List<string> rest)
    {
        int mb = int.Parse(rest[0]), seconds = int.Parse(rest[1]);
        var blocks = new List<byte[]>();
        for (int got = 0; got < mb; got += 64)
        {
            if (AvailableMB() < 6144 + 64) { Console.WriteLine($"stopped at {got} MB: going on would leave under 6 GB available"); break; }
            var b = new byte[64 * 1048576];
            for (int i = 0; i < b.Length; i += 4096) b[i] = 1;
            blocks.Add(b);
        }
        Console.WriteLine($"reserved {blocks.Count * 64} MB; {AvailableMB()} MB available now");
        Console.Out.Flush();
        var clock = Stopwatch.StartNew();
        // Held until the time is up or the file named by the third argument appears (the test's "let go" signal)
        while (clock.Elapsed.TotalSeconds < seconds && !(rest.Count > 2 && File.Exists(rest[2]))) Thread.Sleep(100);
        GC.KeepAlive(blocks);
        Console.WriteLine("released");
        return 0;
    }

    // ---------------------------------------------------------------- Normalising answers before comparing them
    /// <summary>
    /// Takes out what legitimately differs between two runs of the same command: progress notes with timings, the file
    /// name of a saved long answer, job numbers.
    /// </summary>
    public static string Normal(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("[loadorder]") || line.StartsWith("[session]") || line.StartsWith("[live]")) continue;
            if (line == "[notes]" || line.Length == 0) continue;
            line = Regex.Replace(line, @"[A-Za-z]:\\[^\n]*\\Service\\results\\\d{8}_\d{6}_j[0-9a-f]+-\d+\.md", "<saved file>");
            line = Regex.Replace(line, @"\bj[0-9a-f]+-\d+\b", "<job>");
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    public static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
}
