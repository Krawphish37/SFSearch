using System.Diagnostics;
using System.Text;

namespace SFSearch.Harness;

/// <summary>
/// sfsharness: the test harness of the agent-first design (design\agent-first\PLAN.md, milestone M12).
///   sfsharness all                     everything, and one table
///   sfsharness replay | mapping | traps | roundtrip | soak | mcp | testdata     one part
/// It drives the SFSearch build in this repo on a test home under a work folder outside the repo. The game's Data
/// folder is only read. Nothing it writes goes into the repo.
/// The author's agent benchmark (a dev tool, not in the public repository) adds its part through the partial methods
/// at the end of this class; without it they do nothing.
/// </summary>
public static partial class Program
{
    static List<string> _rest = [];

    static string? Opt(string name)
    {
        int i = _rest.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= _rest.Count) return null;
        string v = _rest[i + 1];
        _rest.RemoveRange(i, 2);
        return v;
    }

    static bool Flag(string name)
    {
        int i = _rest.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        _rest.RemoveAt(i);
        return true;
    }

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Json.Utf8;
        ThreadPool.SetMinThreads(64, 64);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Usage(); return args.Length == 0 ? 2 : 0; }
        _rest = args.Skip(1).ToList();

        // Where things are
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SFSearch", "SFSearch.csproj")) && Directory.Exists(Path.Combine(d.FullName, "tests", "SFSearch.Harness"))) { Paths.Repo = d.FullName; break; }
        Paths.Repo = Opt("--repo") ?? Paths.Repo;
        if (Paths.Repo.Length == 0) { Console.WriteLine("Error: the SFSearch repo wasn't found above this program. Fix: give it with --repo <folder>."); return 2; }
        Paths.Cases = Path.Combine(Paths.Repo, "tests", "SFSearch.Harness");
        string? work = Opt("--work") ?? Environment.GetEnvironmentVariable("SFSHARNESS_WORK");
        Paths.Work = work == null ? "" : Path.GetFullPath(work);
        Paths.Data = Opt("--data") ?? Environment.GetEnvironmentVariable("SFSEARCH_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";
        Paths.Exe = Opt("--exe") ?? Path.Combine(Paths.Repo, "SFSearch", "bin", "Release", "net10.0", "SFSearch.exe");
        Paths.McpExe = Opt("--mcp") ?? Path.Combine(Paths.Repo, "SFSearch.Mcp", "bin", "Release", "net10.0", "SFSearch.Mcp.exe");
        Paths.ServiceTestExe = Opt("--servicetest") ?? Path.Combine(Paths.Repo, "tests", "SFSearch.ServiceTest", "bin", "Release", "net10.0", "SFSearch.ServiceTest.exe");
        Paths.TestHooksDll = Opt("--testhooks") ?? Path.Combine(Paths.Repo, "tests", "SFSearch.TestHooks", "bin", "Release", "net10.0", "SFSearch.TestHooks.dll");
        Paths.Workers = int.Parse(Opt("--workers") ?? "4");

        string command = args[0].ToLowerInvariant();
        string sub = _rest.Count > 0 && !_rest[0].StartsWith("--") ? _rest[0].ToLowerInvariant() : "";
        try
        {
            // The parts that need no SFSearch and no work folder
            if (command == "replay" && sub == "list") return ReplayList();
            if (command == "benchmark")
            {
                Task<int>? benchmark = null;
                BenchmarkCommand(sub, ref benchmark);
                if (benchmark == null) { Console.WriteLine("Error: 'benchmark' is the author's agent benchmark, a dev tool that isn't in this checkout. Fix: sfsharness help lists the parts there are."); return 2; }
                return await benchmark;
            }
            if (command == "rawdiff") return RawDiff(_rest[0], _rest[1]);
            if (command == "filetimes") return FileTimes(_rest);

            if (Paths.Problem() is { } problem) { Console.WriteLine("Error: " + problem); return 2; }
            Directory.CreateDirectory(Paths.Work);
            return command switch
            {
                "all" => await All(),
                "replay" => await ReplayCommand(sub),
                "mapping" => await MappingCommand(),
                "call" => await CallCommand(),
                "traps" => await TrapsCommand(),
                "polish" => await PolishCommand(),
                "tables" => TablesCommand(),
                "roundtrip" => await RoundTripCommand(),
                "soak" => await SoakCommand(),
                "mcp" => await McpCommand(sub),
                "testdata" => TestDataCommand(sub),
                _ => UsageError(),
            };
        }
        catch (InvalidOperationException e) { Console.WriteLine("Error: " + e.Message); return 2; }
    }

    static int UsageError() { Usage(); return 2; }

    /// <summary>
    /// Every file of the given folders with its size, write time and access time, from the folder's own listing: no
    /// file is opened, so asking doesn't change an access time (a shell's stat does). For the test that the databases
    /// of earlier versions are no longer read.
    /// </summary>
    static int FileTimes(List<string> folders)
    {
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"{f.FullName}\t{f.Length}\t{f.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss.fffffff}\t{f.LastAccessTimeUtc:yyyy-MM-dd HH:mm:ss.fffffff}");
        }
        return 0;
    }

    static void Usage()
    {
        string more = "";
        BenchmarkUsage(ref more);
        Console.Write(UsageText + more);
    }

    const string UsageText = """
        sfsharness <part> --work <folder outside the repo> [--data <Data folder>] [--exe SFSearch.exe] [--mcp SFSearch.Mcp.exe] [--testhooks SFSearch.TestHooks.dll] [--workers 4]
          (--work can also come from SFSHARNESS_WORK, --data from SFSEARCH_DATA. The Data folder is only read.)

          all [--soak-seconds 180] [--with-starfield] [--no-soak] [--no-mcp] [--no-heavy]
                                    everything, then one table: the tools' goldens, the mapping of the recorded old
                                    commands, the 16 traps, the polish cases, round trips, soak, the MCP server
          replay baseline [--passes 2]   run the tools' goldens (replay\*.jsonl) and keep the answers as the baseline (in
                                    the work folder); two passes: the first warms the test home up, the second is kept
          replay compare            run them again and compare with the baseline
              [--set <set>|all] [--via cli|service|mcp] [--no-service] [--only 12,40-60]
              [--testdata]              on the small test Data folder of copies (its own home and baseline)
              [--baseline <folder>]     compare with another baseline folder
              [--against cli|service|mcp]   compare with the baseline taken by another route
          replay list               the goldens, and which name files that aren't there
          mapping                   the 455 recorded old commands and 18 SQL files (mapping\, removed in 2.0 with the
                                    older commands): each must map to the call that answers it now, and each old name's
                                    pointer must name that verb. No plugin is read; seconds
          call <tool> <json arguments> [--home home] [--times 1] [--page-chars N]
                                    one call of a tool through the service's structured form, printed as the contract's
                                    text, with the time it took (--times N: N calls, the fastest, typical and slowest)
          traps [--only 3,7] [--verbose]            the 16-trap acceptance test (traps\traps.json)
          polish [--only 3,7] [--verbose]           the polish cases (polish\*.json): places where the tools once fell short
          tables [--baseline <folder>]              every kept golden of the new tools: no table without its separator row
          roundtrip <plugin or path> ...            read and write through Mutagen, compare byte for byte
          rawdiff <a.esm> <b.esm>                   name the first records that differ between two plugin files
          filetimes <folder> ...                    every file's size, write time and access time, with no file opened
          soak [--seconds 180]                      tests\SFSearch.ServiceTest's soak test, on the test Data folder
          mcp describe              the tools the MCP server lists, with the length of every description (under 15,000 for the 12)
          mcp test [--no-heavy] [--verbose]         the MCP server's own test: the 12 tools listed, sfs_status while
                                    other calls run, a long answer paged, a heavy call refused and run, a job cancelled,
                                    --legacy-tools (removed in 2.0) still starting the server with the 12. (Every tool
                                    through the server, with its goldens: replay compare --via mcp)
          mcp call <tool> <json arguments>          one call of a new tool through the MCP server, as an agent sees it
          testdata make                             the test Data folder of plugin copies (and undo a deliberate change)
          testdata alter <plugin> <text> <other text of the same length>    the deliberate change: overwrite a text in the copy

        """;

    // ---------------------------------------------------------------- Which commands
    static Func<string, bool>? Only()
    {
        string? spec = Opt("--only");
        if (spec == null) return null;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out int a) && int.TryParse(range[1], out int b)) for (int i = a; i <= b; i++) ids.Add(i.ToString());
            else ids.Add(part);
        }
        return ids.Contains;
    }

    static string[] SetsAsked()
    {
        string set = (Opt("--set") ?? "all").ToLowerInvariant();
        return set == "all" ? Replay.Sets : [set];
    }

    // ---------------------------------------------------------------- replay
    static int ReplayList()
    {
        var only = Only();
        foreach (var set in SetsAsked())
        {
            var all = Replay.Load(set);
            int missing = 0;
            foreach (var r in all.Where(r => only?.Invoke(r.Id) ?? true))
            {
                var m = Directory.Exists(Paths.Data) ? Replay.MissingFiles(r.Args, Paths.Data) : [];
                if (m.Count > 0) missing++;
                Console.WriteLine($"{r.Id,4}  {Compare.Cut(r.AsShown().Show(), 200)}{(m.Count > 0 ? "\n        names a file that isn't there: " + string.Join(", ", m) : "")}");
            }
            Console.WriteLine($"{set}: {all.Count} commands ({string.Join(", ", all.GroupBy(r => (r.Tool ?? r.Args[0]).ToLowerInvariant()).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))}); {missing} name files that aren't there.\n");
        }
        return 0;
    }

    static async Task<int> ReplayCommand(string sub)
    {
        if (sub is not ("baseline" or "compare")) return UsageError();
        string via = (Opt("--via") ?? "cli").ToLowerInvariant();
        bool service = !Flag("--no-service"), testdata = Flag("--testdata");
        string? against = Opt("--against");
        int passes = int.Parse(Opt("--passes") ?? "2");
        string baseRoot = Opt("--baseline") ?? Paths.Out(testdata ? "baseline-testdata" : "baseline");
        var only = Only();
        var sets = SetsAsked();
        if (testdata) Console.WriteLine(TestData.Make());
        await using var sut = testdata ? new Sut(Paths.Out("home-testdata"), TestData.Folder, TestData.PluginsTxt) : Sut.Real();
        Func<Recorded, bool> pick = r => (only?.Invoke(r.Id) ?? true) && (!testdata || TestData.Answerable(r));
        int bad = 0;
        string runRoot = sub == "baseline" ? baseRoot : Paths.Out("runs", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        foreach (var set in sets)
        {
            // A baseline is taken on a warmed-up home. The first pass builds what today's SFSearch keeps (its databases,
            // the load-order index), and some of its answers differ until that exists. The last pass is the one kept.
            for (int pass = 1; sub == "baseline" && pass < passes; pass++)
                Console.WriteLine($"pass {pass} of {passes} (not kept): " + Replay.Summary(await Replay.Run(sut, set, via, service, Paths.Out("runs", "warmup"), pick)));
            var info = await Replay.Run(sut, set, via, service, runRoot, pick);
            Console.WriteLine(Replay.Summary(info));
            foreach (var s in info.Skipped) Console.WriteLine($"  skipped #{s.Id}: {Compare.Cut(s.Command, 140)}\n      missing: {string.Join(", ", s.Missing)}");
            if (sub == "baseline") { Console.WriteLine($"Baseline kept in {Replay.Dir(runRoot, via, set)}\n"); continue; }
            var res = Replay.CompareRuns(set, Replay.Dir(baseRoot, against ?? via, set), Replay.Dir(runRoot, via, set));
            if (res.Problem != null) { Console.WriteLine("Error: " + res.Problem); bad++; continue; }
            Console.WriteLine(res.Report);
            bad += res.Different + res.NoBaseline;
        }
        return Math.Min(bad, 100);
    }

    // ---------------------------------------------------------------- mapping, call
    static async Task<int> MappingCommand()
    {
        await using var sut = Sut.Real();
        var (problems, report) = await Mapping.Run(sut);
        Console.Write(report);
        return Math.Min(problems, 100);
    }

    static async Task<int> CallCommand()
    {
        string home = Opt("--home") ?? "home";
        int times = int.Parse(Opt("--times") ?? "1"), pageChars = int.Parse(Opt("--page-chars") ?? "0");
        if (_rest.Count < 1) return UsageError();
        string tool = _rest[0];
        var pars = _rest.Count > 1 ? System.Text.Json.Nodes.JsonNode.Parse(_rest[1]) as System.Text.Json.Nodes.JsonObject : [];
        if (pars == null) { Console.WriteLine("Error: the arguments must be a JSON object. Fix: e.g. {\"query\": \"Eon\"}."); return 2; }
        await using var sut = new Sut(Paths.Out(home), Paths.Data);
        await sut.StartService();
        var took = new List<(long Service, long Trip)>();
        var checks = new List<long>();
        SFSearch.Service.Reply? reply = null;
        for (int i = 0; i < times; i++)
        {
            var clock = Stopwatch.StartNew();
            reply = await sut.Tool(tool, pars, pageChars);
            took.Add((reply.Ms, clock.ElapsedMilliseconds));
            // The new tools say what their check of the header index cost: "(checked in 25 ms)"
            foreach (var read in reply.Cost?.Read ?? [])
                if (System.Text.RegularExpressions.Regex.Match(read, @"checked in (\d+) ms") is { Success: true } m) checks.Add(long.Parse(m.Groups[1].Value));
        }
        Console.Write(SFSearch.Service.Wire.Render(reply!));
        if (reply!.Error != null) Console.WriteLine(reply.Error);
        var s = took.Select(t => t.Service).OrderBy(x => x).ToList(); var t2 = took.Select(t => t.Trip).OrderBy(x => x).ToList();
        // The first call after the service started pays for what later calls find ready (a plugin opened, a type's reader compiled, a table filled)
        Console.WriteLine($"[call] The first call: {took[0].Service} ms in the worker, {took[0].Trip} ms asking to answer. Worker memory after the last call: {reply.Cost?.MemMB} MB.");
        Console.WriteLine($"[call] {times} call(s). In the worker: fastest {s[0]} ms, typical {s[s.Count / 2]} ms, slowest {s[^1]} ms. Asking to answer: fastest {t2[0]} ms, typical {t2[t2.Count / 2]} ms, slowest {t2[^1]} ms. Worker memory {reply.Cost?.MemMB} MB.");
        if (checks.Count > 0)
        {
            checks.Sort();
            Console.WriteLine($"[call] Of that, the check of the header index before the answer: fastest {checks[0]} ms, typical {checks[checks.Count / 2]} ms, slowest {checks[^1]} ms.");
        }
        return reply.Code;
    }

    // ---------------------------------------------------------------- traps
    static async Task<int> TrapsCommand()
    {
        var only = Only();
        bool verbose = Flag("--verbose");
        await using var sut = Sut.ForTraps();
        await sut.StartService();
        var results = await Traps.Run(sut, only == null ? null : t => only(t.N.ToString()), verbose);
        Console.WriteLine();
        Console.Write(Table(TrapRows(results)));
        Console.WriteLine($"\ntraps: {results.Count(r => r.Status == "pass")} pass, {results.Count(r => r.Status == "fail")} fail, {results.Count(r => r.Status == "not built yet")} not built yet, {results.Count(r => r.Status == "problem")} couldn't be run. " +
            $"New-tool calls not built yet: {results.Count(r => r.New is { Status: "not built yet" })} of {results.Count(r => r.Trap.New != null)}.");
        return results.Count(r => r.Status is "fail" or "problem");
    }

    static async Task<int> PolishCommand()
    {
        var only = Only();
        bool verbose = Flag("--verbose");
        await using var sut = Sut.ForTraps();
        await sut.StartService();
        var results = await Polish.Run(sut, only == null ? null : c => only(c.N.ToString()), verbose);
        Console.WriteLine();
        Console.Write(Table(Polish.Rows(results)));
        int bad = results.Count(r => r.Result.Status != "pass");
        Console.WriteLine($"\npolish: {results.Count - bad} of {results.Count} pass.");
        return bad;
    }

    /// <summary>The sets whose commands are calls of the new tools: their goldens are text an agent reads as Markdown.</summary>
    static IEnumerable<string> NewToolSets() => Replay.Sets.Where(s => Replay.Load(s).Any(r => r.Tool != null));

    /// <summary>Every kept golden of the new tools, looked at for a table that has no separator row.</summary>
    static int TablesCommand()
    {
        string root = Opt("--baseline") ?? Paths.Out("baseline");
        var rows = new List<string[]>();
        int bad = 0;
        foreach (var set in NewToolSets())
        {
            var (answers, tables, problems) = Tables.CheckRun(Replay.Dir(root, "cli", set));
            foreach (var p in problems) Console.WriteLine(p);
            rows.Add([$"Tables: {set}", answers == 0 ? "NO BASELINE" : problems.Count == 0 ? "PASS" : "FAIL", $"{answers} answers, {tables} tables, {problems.Count} without a separator row"]);
            if (answers == 0 || problems.Count > 0) bad++;
        }
        Console.Write(Table(rows));
        return bad;
    }

    /// <summary>One row per trap, in plain words. The patterns that decided it are in the lines printed above the table.</summary>
    static List<string[]> TrapRows(List<TrapResult> results) =>
        results.Select(r =>
        {
            bool newDecides = r.New != null && r.New.Status != "not built yet";
            string text = newDecides ? $"New tools: {r.New!.Status}{(r.New.Status == "pass" ? " (" + r.New.Detail + ")" : "")}."
                : r.Today == null ? "No case through today's commands."
                : r.Today.Status == "pass" ? $"Today: caught ({r.Today.Detail}). {r.Trap.TodayShows}"
                : r.Today.Status == "fail" ? $"Today: still open. {r.Trap.TodayShows}"
                : $"Today: the case couldn't be run ({r.Today.Detail}).";
            string tools = r.Trap.New == null ? "" : string.Join(", ", r.Trap.New.Calls.Select(c => c.Tool).Concat(r.Trap.New.Needs).Where(t => t != null).Distinct());
            return new[]
            {
                $"Trap {r.Trap.N}: {r.Trap.Title}", Traps.Label(r.Status),
                $"{text} Design: {r.Trap.Design}." + (r.New is { Status: "not built yet" } ? $" New tool call ({tools}): not built yet." : ""),
            };
        }).ToList();

    // ---------------------------------------------------------------- roundtrip
    static async Task<int> RoundTripCommand()
    {
        if (_rest.Count == 0) return UsageError();
        int bad = 0;
        foreach (var p in _rest)
        {
            var r = await RoundTrip.Run(p);
            Console.WriteLine($"{(r.Identical ? "PASS" : "FAIL")}  round trip {r.Plugin} (Mutagen {r.Mutagen}): {r.Line()}");
            foreach (var l in r.First) Console.WriteLine("      " + l);
            if (!r.Identical) bad++;
        }
        return bad;
    }

    static int RawDiff(string a, string b)
    {
        long at = RoundTrip.FirstDifference(a, b);
        var d = RawPlugin.Diff(a, b, 10, "second");
        Console.WriteLine(at < 0 ? $"The two files are the same, byte for byte ({d.RecordsA:N0} records)."
            : $"First different byte at {at:N0}. Records: {d.RecordsA:N0} and {d.RecordsB:N0}; {d.Differ:N0} differ, {d.OnlyA:N0} only in the first, {d.OnlyB:N0} only in the second.");
        foreach (var l in d.First) Console.WriteLine("  " + l);
        return at < 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- soak
    static async Task<int> SoakCommand()
    {
        var r = await Soak.Run(int.Parse(Opt("--seconds") ?? "180"));
        Console.WriteLine(r.Output.TrimEnd());
        Console.WriteLine($"\n{(r.Pass ? "PASS" : "FAIL")}  soak: {r.Line()}");
        return r.Pass ? 0 : 1;
    }

    // ---------------------------------------------------------------- mcp
    static async Task<int> McpCommand(string sub)
    {
        bool verbose = Flag("--verbose"), noHeavy = Flag("--no-heavy");
        string home = Opt("--home") ?? "home-mcp";
        await using var sut = new Sut(Paths.Out(home), Paths.Data);
        switch (sub)
        {
            case "describe":
            {
                await sut.StartService();
                var (n, report) = await McpTest.Describe(sut);
                Console.Write(report);
                return n < McpTest.DefinitionsBudget ? 0 : 1;
            }
            case "call":
            {
                if (_rest.Count < 2) return UsageError();
                var pars = _rest.Count > 2 ? System.Text.Json.Nodes.JsonNode.Parse(_rest[2]) as System.Text.Json.Nodes.JsonObject : [];
                var clock = Stopwatch.StartNew();
                var (text, isError) = await sut.McpText(_rest[1], pars);
                Console.Write(text.EndsWith('\n') ? text : text + "\n");
                Console.WriteLine($"[mcp] {(isError ? "marked as an error; " : "")}{text.Length:N0} characters, {clock.ElapsedMilliseconds} ms asking to answer");
                return isError ? 1 : 0;
            }
            case "test":
            {
                var r = await McpTest.Run(sut, verbose, !noHeavy);
                Console.WriteLine($"\n{(r.Pass ? "PASS" : "FAIL")}  mcp: {r.Line}");
                return r.Failed;
            }
            default: return UsageError();
        }
    }

    // ---------------------------------------------------------------- testdata
    static int TestDataCommand(string sub)
    {
        switch (sub)
        {
            case "make": Console.WriteLine(TestData.Make(restore: true)); return 0;   // also undoes a deliberate change
            case "alter" when _rest.Count == 4: Console.WriteLine(TestData.Make()); Console.WriteLine(TestData.Alter(_rest[1], _rest[2], _rest[3])); return 0;
            default: return UsageError();
        }
    }

    // ---------------------------------------------------------------- all
    static async Task<int> All()
    {
        int soakSeconds = int.Parse(Opt("--soak-seconds") ?? "180");
        bool withStarfield = Flag("--with-starfield"), noSoak = Flag("--no-soak");
        var rows = new List<string[]>();
        var clock = Stopwatch.StartNew();
        int failed = 0;

        // 1. The tools' goldens, against the kept baseline
        Console.WriteLine("== Golden replay");
        var trapResults = new List<TrapResult>();
        await using (var sut = Sut.Real())
        {
            string runRoot = Paths.Out("runs", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            foreach (var set in Replay.Sets)
            {
                string name = $"Replay: {set}";
                string baseDir = Replay.Dir(Paths.Out("baseline"), "cli", set);
                if (!File.Exists(Path.Combine(baseDir, "run.json")))
                {
                    rows.Add([name, "NO BASELINE", "take one first: sfsharness replay baseline"]);
                    failed++;
                    continue;
                }
                var info = await Replay.Run(sut, set, "cli", true, runRoot);
                var res = Replay.CompareRuns(set, baseDir, Replay.Dir(runRoot, "cli", set));
                Console.WriteLine(res.Report);
                rows.Add([name, res.Pass ? "PASS" : "FAIL",
                    $"{res.Commands} commands: ran {res.Ran}, equal {res.Same + res.SameAsSet} ({res.Same} line for line, {res.SameAsSet} as sets), {(res.Explained > 0 ? $"different as replay\\explained.json says {res.Explained}, " : "")}different {res.Different}, skipped {res.Skipped} (they name files that aren't there); {info.Seconds:0} s"]);
                if (!res.Pass) failed++;
            }
            // 1a. Every table the new tools printed in this run has its separator row (a header alone doesn't render)
            int tableAnswers = 0, tableCount = 0;
            var unruled = new List<string>();
            foreach (var set in NewToolSets())
            {
                var (answers, tables, problems) = Tables.CheckRun(Replay.Dir(runRoot, "cli", set));
                tableAnswers += answers; tableCount += tables; unruled.AddRange(problems);
            }
            foreach (var p in unruled) Console.WriteLine(p);
            rows.Add(["Tables: every table of the new tools' goldens has its separator row", unruled.Count == 0 && tableAnswers > 0 ? "PASS" : "FAIL", $"{tableAnswers} answers of {NewToolSets().Count()} sets, {tableCount} tables, {unruled.Count} without a separator row"]);
            if (unruled.Count > 0 || tableAnswers == 0) failed++;
        }

        // 1b. The recorded old commands: each mapped to the call that answers it now, and each old name's pointer
        Console.WriteLine("== The recorded old commands, mapped");
        await using (var sut = Sut.Real())
        {
            var (problems, report) = await Mapping.Run(sut);
            Console.Write(report);
            rows.Add(["Mapping: the recorded old commands", problems == 0 ? "PASS" : "FAIL", report.TrimEnd().Split('\n')[^1][(report.TrimEnd().Split('\n')[^1].IndexOf("mapping: ") + 9)..]]);
            if (problems > 0) failed++;
        }

        // 2. The 16 traps, on a home of their own
        Console.WriteLine("== The 16 traps");
        await using (var sut = Sut.ForTraps())
        {
            await sut.StartService();
            trapResults = await Traps.Run(sut);
            rows.AddRange(TrapRows(trapResults));
        }
        int trapsOpen = trapResults.Count(r => r.Status is "fail" or "problem");
        if (trapsOpen > 0) failed++;

        // 2b. The polish pass's cases, on the traps' home
        Console.WriteLine("\n== The polish cases");
        await using (var sut = Sut.ForTraps())
        {
            await sut.StartService();
            var polish = await Polish.Run(sut);
            rows.AddRange(Polish.Rows(polish));
            if (polish.Any(r => r.Result.Status != "pass")) failed++;
        }

        // 3. Round trips
        Console.WriteLine("\n== Round trips");
        var kept = RoundTrip.Remembered();
        foreach (var p in new[] { "weap_starlight.esm", "kgcdoom.esm", "darkstar.esm", "Starfield.esm" })
        {
            RoundTripResult? r;
            string note = "";
            if (p == "Starfield.esm" && !withStarfield)
            {
                if (!kept.TryGetValue(p.ToLowerInvariant(), out r)) { rows.Add([$"Round trip: {p}", "NOT RUN", "about 55 s and 11 GB: run 'sfsharness roundtrip Starfield.esm' alone on a PC with memory to spare, or add --with-starfield"]); continue; }
                note = $" (not run now: the result of {r.When}, Mutagen {r.Mutagen})";
            }
            else r = await RoundTrip.Run(p);
            Console.WriteLine($"{(r.Identical ? "PASS" : "FAIL")}  round trip {r.Plugin}: {r.Line()}{note}");
            foreach (var l in r.First) Console.WriteLine("      " + l);
            rows.Add([$"Round trip: {p}", r.Identical ? "PASS" : "FAIL", r.Line() + (r.Identical ? "" : " First: " + string.Join("; ", r.First.Take(2))) + note]);
            if (!r.Identical) failed++;
        }

        // 4. The soak test
        if (noSoak) rows.Add(["Soak", "NOT RUN", "--no-soak was given"]);
        else
        {
            Console.WriteLine($"\n== Soak ({soakSeconds} s)");
            var s = await Soak.Run(soakSeconds);
            Console.WriteLine(string.Join("\n", s.Output.Split('\n').Where(l => l.TrimStart().StartsWith("commands:") || l.StartsWith("PASS") || l.StartsWith("FAIL") || l.StartsWith("WRONG"))));
            rows.Add(["Soak", s.Pass ? "PASS" : "FAIL", s.Line()]);
            if (!s.Pass) failed++;
        }

        // 5. The author's agent benchmark (a dev tool, not in the public repository) adds its rows here; without it, none
        Task<int>? benchmarkPart = null;
        BenchmarkAll(rows, ref benchmarkPart);
        if (benchmarkPart != null) failed += await benchmarkPart;

        // 6. The MCP server: the 12 tools through the harness's MCP client, on a home of its own
        if (Flag("--no-mcp")) rows.Add(["MCP server", "NOT RUN", "--no-mcp was given"]);
        else
        {
            Console.WriteLine("\n== The MCP server");
            await using var sut = new Sut(Paths.Out("home-mcp"), Paths.Data);
            var m = await McpTest.Run(sut, false, !Flag("--no-heavy"));
            rows.Add(["MCP server: the 12 tools", m.Pass ? "PASS" : "FAIL", m.Line]);
            if (!m.Pass) failed++;
        }

        Console.WriteLine($"\n== sfsharness all: SFSearch at {Paths.Exe}, Mutagen {Replay.MutagenVersion()}, {clock.Elapsed.TotalMinutes:0.0} min");
        Console.Write(Table(rows));
        Console.WriteLine($"\nTraps: {trapResults.Count(r => r.Status == "pass")} pass, {trapResults.Count(r => r.Status == "fail")} fail, {trapResults.Count(r => r.Status == "not built yet")} not built yet" +
            $"{(trapResults.Any(r => r.Status == "problem") ? $", {trapResults.Count(r => r.Status == "problem")} couldn't be run" : "")}; the new tools' own calls: {trapResults.Count(r => r.New is { Status: "not built yet" })} of {trapResults.Count(r => r.Trap.New != null)} not built yet.");
        Console.WriteLine(failed == 0 ? "Everything passes." : $"{failed} part(s) don't pass (the traps count as one part while any is open).");
        return failed;
    }

    static string Table(List<string[]> rows)
    {
        var sb = new StringBuilder("| Part | Result | Numbers |\n|---|---|---|\n");
        foreach (var r in rows) sb.Append("| ").Append(string.Join(" | ", r.Select(c => c.Replace("|", "\\|").Replace("\n", " ")))).Append(" |\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- the author's agent benchmark (a dev tool)
    // Filled in by the benchmark's own code when it is in the checkout; in the public repository these do nothing.
    /// <summary>The "benchmark" part: sets run to its work, or leaves it null when the benchmark isn't here.</summary>
    static partial void BenchmarkCommand(string sub, ref Task<int>? run);
    /// <summary>The benchmark's rows of "all": sets run to the work that adds them, which returns the parts that failed.</summary>
    static partial void BenchmarkAll(List<string[]> rows, ref Task<int>? run);
    /// <summary>The benchmark's lines of the help.</summary>
    static partial void BenchmarkUsage(ref string lines);
}
