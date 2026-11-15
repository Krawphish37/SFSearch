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
        }
        if (Home.Length == 0 || !Directory.Exists(Home)) { Console.WriteLine("Give the test home with --home (or SFSEARCH_HOME). Never the user's own SFSearch folder."); return 2; }
        Home = Path.GetFullPath(Home);
        var clock = Stopwatch.StartNew();
        switch (args[0].ToLowerInvariant())
        {
            case "capture": await Capture(Opt("--out") ?? "capture"); break;
            case "protocol": await Protocol(); break;
            case "scratch-after-restart": await ScratchAfterRestart(); break;
            case "heavy": await Heavy(); break;
            case "memory": await Memory(Opt("--bash") ?? "bash", Opt("--heavy-sh"), int.Parse(Opt("--reserve-mb") ?? "3072")); break;
            case "cancel": await Cancel(); break;
            case "indexer": await Indexer(); break;
            case "limits": await Limits(); break;
            case "smallpc": await SmallPc(); break;
            case "olderfile": await OlderFile(Opt("--older")); break;
            case "idleworkers": await IdleWorkers(); break;
            case "historylook": await HistoryLook(Opt("--data")); break;
            case "wording": await Wording(); break;
            case "inplace": await InPlace(Opt("--copy")); break;
            case "addoncrash": await AddOnCrash(); break;
            case "soak": await Soak(int.Parse(Opt("--seconds") ?? "180"), int.Parse(Opt("--cli") ?? "4"), int.Parse(Opt("--adapters") ?? "2"), int.Parse(Opt("--per-adapter") ?? "4"), Opt("--touch")); break;
            default: Usage(); return 2;
        }
        Console.WriteLine($"\n{args[0]}: {_pass} passed, {_fail} failed, {clock.Elapsed.TotalSeconds:0.0} s.");
        return _fail;
    }

    static void Usage() => Console.WriteLine(
        "SFSearch.ServiceTest <test> --home <test home> [--exe SFSearch.exe] [--mcp SFSearch.Mcp.exe]\n" +
        "  capture --out <folder>   run the fixed command lines through the command line, and their tool calls through the MCP server\n" +
        "  compare <a> <b>          compare two capture folders\n" +
        "  protocol | scratch-after-restart | heavy | cancel | indexer | limits | soak | idleworkers\n" +
        "  memory [--heavy-sh <memory-slot script>] [--bash <bash>] [--reserve-mb N]   refused when short, started when there; the memory helper runs through the script (with bash) when one is named, else directly\n" +
        "  smallpc --exe <SFSearch.exe>   a pretend 8 GB and 16 GB PC: the defaults, the limit for one command, the floor (starts and stops its own service)\n" +
        "  olderfile --exe <SFSearch.exe> --older <an older sfsearch.json>   its fixed memory numbers taken from a pretend 8 GB PC; the file rewritten once (starts and stops its own service)\n" +
        "  historylook --exe <SFSearch.exe> [--data <Data folder>]   the service looks for changed plugins by itself; changes2 says which look it compares with (its own service and test Data folder)\n" +
        "  wording                  --help, a mistyped verb, sfs_run [\"help\"]: the texts each gives (a service must run)\n" +
        "  addoncrash --exe <SFSearch.exe>   an add-on command that throws exits 2 with its error text (the CrashTest add-on built into the test home; no service)\n" +
        "  inplace --copy <kgcdoom.esm in the test Data folder SFSEARCH_DATA names>   a change that keeps size and date (no service)\n" +
        "  reserve <MB> <seconds>   helper: take that much memory, touch it, hold it");

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
