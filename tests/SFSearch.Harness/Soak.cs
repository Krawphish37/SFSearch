using System.Text.RegularExpressions;

namespace SFSearch.Harness;

public sealed class SoakResult
{
    public int Seconds;
    public long Commands = -1, Same, Wrong = -1, Hangs = -1, ClearErrors;
    public string Chaos = "", Times = "", Output = "", NewTools = "", Memory = "";
    public string? Problem;
    public bool Pass => Problem == null && Commands > 0 && Wrong == 0 && Hangs == 0;
    public string Line() => Problem != null ? "problem: " + Problem
        : $"{Commands:N0} commands in {Seconds} s, {Wrong} wrong answers, {Hangs} hangs ({Same:N0} the same as the baseline, {ClearErrors} clear error(s)); {(NewTools.Length > 0 ? NewTools + "; " : "")}{Chaos}{(Memory.Length > 0 ? "; " + Memory : "")}";
}

/// <summary>
/// The soak test is tests\SFSearch.ServiceTest's (milestone M4): many clients at once through the command line and
/// MCP servers, workers and an MCP server killed on purpose, junk on the service's pipe, a plugin copy's date changed.
/// The harness gives it a service on a test home and the test Data folder of copies, runs it, and reads its result.
/// </summary>
public static class Soak
{
    public static async Task<SoakResult> Run(int seconds, int cli = 4, int adapters = 2, int perAdapter = 4)
    {
        var res = new SoakResult { Seconds = seconds };
        if (!File.Exists(Paths.ServiceTestExe)) { res.Problem = $"SFSearch.ServiceTest.exe wasn't found at {Paths.ServiceTestExe}: build it (dotnet build tests\\SFSearch.ServiceTest -c Release)"; return res; }
        if (!File.Exists(Paths.McpExe)) { res.Problem = $"SFSearch.Mcp.exe wasn't found at {Paths.McpExe}: build it (dotnet build SFSearch.Mcp -c Release)"; return res; }
        Console.WriteLine("  " + TestData.Make());
        await using var sut = new Sut(Paths.Out("home-testdata"), TestData.Folder, TestData.PluginsTxt);
        // The service's total, as in the M4 run: the PC is shared with other sessions
        sut.Extra["SFSEARCH_SERVICE_MAX_MB"] = "6000";
        await sut.StartService();
        var (o, e, code) = await Sut.RunProcess(Paths.ServiceTestExe,
            ["soak", "--home", sut.Home, "--exe", Paths.Exe, "--mcp", Paths.McpExe, "--seconds", seconds.ToString(), "--cli", cli.ToString(), "--adapters", adapters.ToString(), "--per-adapter", perAdapter.ToString(), "--touch", TestData.TouchFile],
            sut.Env(), timeoutMs: (seconds + 900) * 1000);
        res.Output = o + (e.Trim().Length > 0 ? "\n" + e : "");
        // The whole output is kept: a wrong answer is printed there with what was expected and what came
        File.WriteAllText(Paths.Out("runs", $"soak_{DateTime.Now:yyyyMMdd_HHmmss}.txt"), res.Output, Json.Utf8);
        var m = Regex.Match(o, @"commands: (\d+); same as the baseline: (\d+); clear error[^:]*: (\d+); wrong: (\d+); hung \(over 120 s\): (\d+)");
        if (!m.Success) { res.Problem = $"the soak test gave no result line (exit code {code}): {Compare.Cut(res.Output.Trim().Split('\n').LastOrDefault() ?? "", 200)}"; return res; }
        res.Commands = long.Parse(m.Groups[1].Value); res.Same = long.Parse(m.Groups[2].Value); res.ClearErrors = long.Parse(m.Groups[3].Value);
        res.Wrong = long.Parse(m.Groups[4].Value); res.Hangs = long.Parse(m.Groups[5].Value);
        res.Chaos = Regex.Match(o, @"chaos: ([^\n]+)").Groups[1].Value.Trim();
        res.Times = Regex.Match(o, @"time per command[^\n]+").Value.Trim();
        // The calls of the 12 new tools among them (through an MCP server that lists the new tools), and what the workers hold at the end
        res.NewTools = Regex.Match(o, @"new tools: ([^\n]+)").Groups[1].Value.Trim() is { Length: > 0 } nt ? "of the 12 new tools: " + nt : "";
        var mem = Regex.Match(await sut.ServiceStatus(), @"the workers together hold (\d+) MB");
        if (mem.Success) res.Memory = $"the workers together hold {int.Parse(mem.Groups[1].Value):N0} MB at the end";
        if (code != 0 && res.Wrong == 0 && res.Hangs == 0) res.Problem = "the soak test failed a check: " + string.Join("; ", o.Split('\n').Where(l => l.StartsWith("FAIL")).Select(l => l.Trim()));
        return res;
    }
}
