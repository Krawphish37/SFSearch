// sfscompare-tests: the tests of the comparisons (milestone M7 of design\agent-first\PLAN.md).
//
//   sfscompare-tests <Data folder> [--only name,name] [--work <folder>] [--report <folder>]
//                    [--sfsearch <SFSearch.exe> --home <test home>] [--baseline <folder>]
//
// Every test prints one line with its numbers. The Data folder is only read: test plugins (copies, and plugins
// written by Mutagen) go to the work folder (default %TEMP%\sfscompare-tests), which is emptied at the start.
// --report keeps the full lists and the answers' texts (one file per test).
// --sfsearch and --home: today's SFSearch (this worktree's build) and a test home for it (never the user's own).
//   The tests that hold the new comparisons against today's commands run it there, with no service; without the
//   two options those tests print SKIP.
// --baseline: the folder with the recorded commands' answers of today's SFSearch, as the M12 harness kept them
//   (<work>\baseline\cli\recorded). It is only read. Without it the recorded commands are run through --sfsearch.
using System.Diagnostics;
using System.Text.Json.Nodes;
using SFSearch.Compare;
using SFSearch.Compare.Tests;
using SFSearch.Records;

if (args.Length < 1) { Console.Error.WriteLine("usage: sfscompare-tests <Data folder> [--only name,name] [--work <folder>] [--report <folder>] [--sfsearch <exe> --home <folder>] [--baseline <folder>]"); return 2; }
var rest = args.Skip(1).ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
Console.OutputEncoding = System.Text.Encoding.UTF8;

var ctx = new Ctx
{
    Data = Path.GetFullPath(args[0]),
    Work = Path.GetFullPath(Opt("--work") ?? Path.Combine(Path.GetTempPath(), "sfscompare-tests")),
    Report = Opt("--report") is { } rep ? Path.GetFullPath(rep) : null,
    TodayExe = Opt("--sfsearch") is { } exe ? Path.GetFullPath(exe) : null,
    TodayHome = Opt("--home") is { } home ? Path.GetFullPath(home) : null,
    Baseline = Opt("--baseline") is { } b ? Path.GetFullPath(b) : null,
    Commands = Opt("--commands") is { } c ? Path.GetFullPath(c) : null,
};
var only = Opt("--only")?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
if (ctx.Work.StartsWith(ctx.Data, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("Error: the work folder is inside the Data folder. Fix: name a folder outside it."); return 2; }
if (Path.GetFileName(ctx.Work) != "sfscompare-tests" && !File.Exists(Path.Combine(ctx.Work, ".sfscompare-tests")) && Directory.Exists(ctx.Work) && Directory.EnumerateFileSystemEntries(ctx.Work).Any())
{ Console.Error.WriteLine($"Error: the work folder {ctx.Work} isn't empty and wasn't made by these tests. Fix: name an empty folder."); return 2; }
if (Directory.Exists(ctx.Work)) Directory.Delete(ctx.Work, true);
Directory.CreateDirectory(ctx.Work);
File.WriteAllText(Path.Combine(ctx.Work, ".sfscompare-tests"), "made by sfscompare-tests; safe to delete");
if (ctx.Report != null) Directory.CreateDirectory(ctx.Report);

var tests = new List<(string Name, Func<Ctx, string> Run)>
{
    ("engine-defaults", EngineTests.Defaults),
    ("engine-lists", EngineTests.Lists),
    ("engine-types-children", EngineTests.TypesAndChildren),
    ("defaults-pair", MadeTests.DefaultsPair),
    ("today-delta", TodayTests.Delta),
    ("today-diff", TodayTests.Diff),
    ("today-conflicts", TodayTests.Conflicts),
    ("today-plugindiff", TodayTests.PluginDiff),
    ("known-itms", TodayTests.KnownItms),
    ("itm-headers", RealTests.ItmHeaders),
    ("partial-forms", RealTests.PartialForms),
    ("heavy-bytes", RealTests.HeavyBytes),
    ("like-xedit", RealTests.LikeXEdit),
    ("world-included", RealTests.WorldIncluded),
    ("known-winners", TodayTests.KnownWinners),
    ("plugins-made", MadeTests.PluginsMade),
    ("plugins-editorid", MadeTests.RepeatedEditorIds),
    ("patch-made", MadeTests.PatchMade),
    ("patch-real", RealTests.PatchReal),
    ("trap12", MadeTests.Trap12),
    ("languages-only", MadeTests.LanguagesOnly),
    ("output-contract", RealTests.OutputContract),
    ("timing", RealTests.Timing),
};
// The tests the author keeps with the agent benchmark (a dev tool, not in the public repository), before the output contract
tests.InsertRange(tests.FindIndex(t => t.Name == "output-contract"), MadeTests.Benchmark());
int failed = 0, ran = 0;
var total = Stopwatch.StartNew();
try
{
    foreach (var (name, run) in tests)
    {
        if (only != null && !only.Contains(name)) continue;
        ran++;
        var sw = Stopwatch.StartNew();
        try
        {
            string numbers = run(ctx);
            string status = numbers.StartsWith("SKIP ", StringComparison.Ordinal) ? "SKIP " : "PASS ";
            if (status != "PASS ") numbers = numbers[5..];
            Console.WriteLine($"{status} {name} ({sw.Elapsed.TotalSeconds:F1} s): {numbers}");
        }
        catch (TestFailed e) { failed++; Console.WriteLine($"FAIL  {name} ({sw.Elapsed.TotalSeconds:F1} s): {e.Message}"); }
        catch (Exception e) { failed++; Console.WriteLine($"FAIL  {name} ({sw.Elapsed.TotalSeconds:F1} s): {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        Console.Out.Flush();
    }
}
finally { ctx.Dispose(); }
Console.WriteLine($"{ran - failed} of {ran} tests ran through without a failure in {total.Elapsed.TotalSeconds:F0} s, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB.");
return failed == 0 ? 0 : 1;

sealed class TestFailed(string message) : Exception(message);

sealed class Ctx : IDisposable
{
    public required string Data, Work;
    public string? Report, TodayExe, TodayHome, Baseline, Commands;
    RecordSchema? _schema; CompareSession? _session; Comparer? _compare;

    public RecordSchema Schema => _schema ??= new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
    /// <summary>One session over the whole Data folder for all tests: plugins stay open from test to test.</summary>
    public CompareSession Session => _session ??= new CompareSession(Data, Path.Combine(Work, "scratch"), Schema, "sfscompare-tests / mutagen " + Schema.MutagenVersion + " / schema " + Schema.Version);
    public Comparer Compare => _compare ??= new Comparer(Session);
    public string Folder(string name) { string f = Path.Combine(Work, name); Directory.CreateDirectory(f); return f; }
    public void Dispose() { _session?.Dispose(); _session = null; }

    public void WriteReport(string name, string text)
    {
        if (Report != null) File.WriteAllText(Path.Combine(Report, name), text);
    }

    public static void Check(bool ok, string what) { if (!ok) throw new TestFailed(what); }

    /// <summary>Plugins the tests never copy or change, by the user's rule. They are only read where they are.</summary>
    public static bool HandsOff(string name) => name.StartsWith("darkstarpatch_", StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(name, @"^sfta0[1-6]\.esm$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A copy of a plugin of the Data folder in the work folder.</summary>
    public string Copy(string plugin, string toFolder, string? asName = null)
    {
        if (HandsOff(plugin)) throw new InvalidOperationException($"{plugin} is never copied");
        string to = Path.Combine(Folder(toFolder), asName ?? plugin);
        File.Copy(Path.Combine(Data, plugin), to, true);
        return to;
    }

    public void MustBeInWork(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refusing to write {path}: it isn't inside the tests' work folder");
    }

    // ---------------------------------------------------------------- Today's SFSearch
    public bool HasToday => TodayExe != null && TodayHome != null && File.Exists(TodayExe);

    /// <summary>Runs today's SFSearch on the test home, with no service, and gives what it printed.</summary>
    public (string Out, string Err, int Code) Today(params string[] arguments)
    {
        if (!HasToday) throw new InvalidOperationException("today's SFSearch wasn't given (--sfsearch and --home)");
        Directory.CreateDirectory(TodayHome!);
        var psi = new ProcessStartInfo(TodayExe!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["SFSEARCH_HOME"] = TodayHome!;
        psi.Environment["SFSEARCH_DATA"] = Data;
        psi.Environment["SFSEARCH_WARMUP"] = "0";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(20 * 60 * 1000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new TestFailed($"today's SFSearch didn't finish in 20 minutes: {string.Join(' ', arguments)}"); }
        return (o, err.Result, p.ExitCode);
    }

    /// <summary>The recorded commands of the harness whose first argument is one of these, with today's answer for
    /// each: from the kept baseline when it has one, else by running the command now.</summary>
    public List<(string Id, List<string> Args, string? Out, string From)> Recorded(params string[] commands)
    {
        string file = Commands ?? FindUp(Path.Combine("tests", "SFSearch.Harness", "replay", "commands.jsonl"))
            ?? throw new TestFailed("the recorded commands (tests\\SFSearch.Harness\\replay\\commands.jsonl) weren't found; name the file with --commands");
        var list = new List<(string, List<string>, string?, string)>();
        foreach (var line in File.ReadLines(file))
        {
            if (line.Trim().Length == 0) continue;
            var o = JsonNode.Parse(line)!.AsObject();
            var a = o["args"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            if (a.Count == 0 || !commands.Contains(a[0], StringComparer.OrdinalIgnoreCase)) continue;
            string id = o["id"]!.GetValue<string>();
            string? answer = null; string from = "not run";
            string kept = Baseline == null ? "" : Path.Combine(Baseline, id + ".json");
            if (File.Exists(kept)) { answer = JsonNode.Parse(File.ReadAllText(kept))!["answer"]?["out"]?.GetValue<string>(); from = "the kept baseline"; }
            else if (HasToday && a.Skip(1).Where(x => x.Contains('\\')).All(File.Exists)) { answer = Today(a.ToArray()).Out; from = "run now"; }
            list.Add((id, a, answer, from));
        }
        return list;
    }

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }
}
