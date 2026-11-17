// sfscompare-tests: the tests of the comparisons (milestone M7 of design\agent-first\PLAN.md).
//
//   sfscompare-tests <Data folder> [--only name,name] [--work <folder>] [--report <folder>]
//
// Every test prints one line with its numbers. The Data folder is only read: test plugins (copies, and plugins
// written by Mutagen) go to the work folder (default %TEMP%\sfscompare-tests), which is emptied at the start.
// --report keeps the full lists and the answers' texts (one file per test).
// The tests that held the comparisons against SFSearch 1.x's commands (today-delta, today-diff, today-conflicts,
// today-plugindiff, known-itms, known-winners) were retired on 2026-11-15: 2.0 removed those commands (1f14d46).
// known-itms' check of every ITM against the files lives on in itm-headers.
using System.Diagnostics;
using System.Text.Json.Nodes;
using SFSearch.Compare;
using SFSearch.Compare.Tests;
using SFSearch.Records;

if (args.Length < 1) { Console.Error.WriteLine("usage: sfscompare-tests <Data folder> [--only name,name] [--work <folder>] [--report <folder>]"); return 2; }
var rest = args.Skip(1).ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
Console.OutputEncoding = System.Text.Encoding.UTF8;

var ctx = new Ctx
{
    Data = Path.GetFullPath(args[0]),
    Work = Path.GetFullPath(Opt("--work") ?? Path.Combine(Path.GetTempPath(), "sfscompare-tests")),
    Report = Opt("--report") is { } rep ? Path.GetFullPath(rep) : null,
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
    ("itm-headers", RealTests.ItmHeaders),
    ("partial-forms", RealTests.PartialForms),
    ("heavy-bytes", RealTests.HeavyBytes),
    ("like-xedit", RealTests.LikeXEdit),
    ("world-included", RealTests.WorldIncluded),
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
    public string? Report;
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
}
