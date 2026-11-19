// sfsquery-tests: the tests of the SQL layer (milestone M6 of design\agent-first\PLAN.md).
//
//   sfsquery-tests <Data folder> [--only name,name] [--work <folder>] [--report <folder>] [--heavy]
//
// Every test prints one line with its numbers. The Data folder is only read: scratch tables and the header index go
// to the work folder (default %TEMP%\sfsquery-tests), which is emptied at the start. --report keeps the full lists.
// --heavy adds what reads Starfield.esm's world data (its 3.29 million placed objects): run that with HEAVY_FULL=1.
//   sfsquery-tests --perf <Data folder> …   what one read takes, cold and warm, and where the time goes (see Perf.cs)
// A second form, used by the two-processes test to start its children:
//   sfsquery-tests --child <Data folder> <work folder> <name> <start file>
using System.Diagnostics;
using SFSearch.Query;
using SFSearch.Query.Tests;

Console.OutputEncoding = System.Text.Encoding.UTF8;
SqliteTuning.NoMemoryStatistics();
if (args.Length >= 5 && args[0] == "--child") return Tests.Child(args[1], args[2], args[3], args[4]);
if (args.Length >= 2 && args[0] == "--perf") return Perf.Run(args);
if (args.Length < 1) { Console.Error.WriteLine("usage: sfsquery-tests <Data folder> [--only name,name] [--work <folder>] [--report <folder>] [--heavy]"); return 2; }
var rest = args.Skip(1).ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }

string work = Path.GetFullPath(Opt("--work") ?? Path.Combine(Path.GetTempPath(), "sfsquery-tests"));
string? report = Opt("--report") is { } rep ? Path.GetFullPath(rep) : null;
bool heavy = rest.Remove("--heavy");
// --tool <SFSearch.exe> --tool-home <test home>: the SQL questions go through SFSearch's own query tool (milestone M8)
Questions.ToolExe = Opt("--tool") is { } toolExe ? Path.GetFullPath(toolExe) : null; Questions.ToolHome = Opt("--tool-home") is { } toolHome ? Path.GetFullPath(toolHome) : null;
var only = Opt("--only")?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
if (Path.GetFileName(work) != "sfsquery-tests" && !File.Exists(Path.Combine(work, ".sfsquery-tests")) && Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any())
{ Console.Error.WriteLine($"Error: the work folder {work} isn't empty and wasn't made by these tests. Fix: name an empty folder."); return 2; }
// The header index's segments are kept between runs (they are keyed by content); the scratch tables never are
foreach (string sub in new[] { "sql", "sql-timing", "sql-shared", "sql-alone", "sql-stored-timing", "verify" })
    if (Directory.Exists(Path.Combine(work, sub))) Directory.Delete(Path.Combine(work, sub), true);
Directory.CreateDirectory(work);
File.WriteAllText(Path.Combine(work, ".sfsquery-tests"), "made by sfsquery-tests; safe to delete");
if (report != null) Directory.CreateDirectory(report);

using var ctx = new Ctx(Path.GetFullPath(args[0]), work, report, heavy);
var tests = new (string Name, Func<Ctx, string> Run)[]
{
    ("readme-queries", Tests.ReadmeQueries),
    ("sql-questions", Questions.Run),
    ("scopes", Tests.Scopes),
    ("winners", Tests.WinnersEqualMutagen),
    ("links", Tests.LinksEqualMutagen),
    ("wrong-names", Tests.WrongNames),
    ("checks", Tests.ChecksBeforeAQuery),
    ("empty-answers", Tests.EmptyAnswers),
    ("read-only", Tests.UserSqlCantWrite),
    ("answer", Tests.AnswerObject),
    ("functions", Tests.FunctionsEqualMutagen),
    ("text-languages", TextTests.Languages),
    ("auto-reads", AutoReadTests.Run),
    ("cost-notice", Tests.CostNotice),
    ("timing", Tests.Timing),
    ("views-all", Tests.EveryViewRuns),
    ("plugins-view", PluginsTests.Run),
    ("two-processes", Tests.TwoProcesses),
    ("world-columns", Tests.WorldColumns),
    ("direct-columns", GSqlTests.DirectColumns),
    ("world-reads", WorldReadTests.Run),
    ("planned-columns", PlannedColumnTests.Run),
    ("condition-lists", PlannedColumnTests.Conditions),
    ("typed-all", TypedAllTests.Run),
    ("lists-all", TypedAllTests.Lists),
    ("varying-columns", TypedAllTests.Varying),
    ("shared-signatures", TypedAllTests.SharedSignatures),
    ("component-fallback", ComponentFallbackTests.Run),
    ("traversal-fluff", TraversalFluffTests.Run),
    ("read-census", ReadCensusTests.Run),
    ("text-bytes", TextBytesTests.Run),
    ("tag-texts", TagTextTests.Run),
    ("json-default-note", JsonDefaultNoteTests.Run),
    ("with-names", WithNameTests.Run),
    ("forms-by-key", FormsByKeyTests.Run),
    ("old-queries", OldQueryTests.Run),
    ("rough-spots", RoughSpotTests.Run),
    ("wwise-names", WwiseTests.Run),
    ("forms-sizes", GSqlTests.FormsSizes),
    ("forms-flags", FlagsTests.Run),
    ("flag-bits", FlagBitsTests.Run),
    ("flag-raw", FlagRawTests.Run),
    ("keyed-reads", GSqlTests.KeyedReads),
    ("links-by-bytes", GSqlTests.LinksByBytes),
    ("conditions", ConditionTests.Run),
    ("list-reads", ListReadTests.Run),
    ("query-memory", MemoryTests.Run),
    ("strings-stamp", GSqlTests.StringsStamp),
    ("scratch-build-key", GSqlTests.ScratchBuildKey),
    ("stored-format", Stored.Format),
    ("stored-shipped", Stored.Shipped),
    ("stored-mutagen", Stored.AgainstMutagen),
    ("stored-timing", Stored.Timing),
};
int failed = 0, ran = 0;
var total = Stopwatch.StartNew();
foreach (var (name, run) in tests)
{
    if (only != null && !only.Contains(name)) continue;
    ran++;
    var clock = Stopwatch.StartNew();
    try
    {
        string line = run(ctx);
        Console.WriteLine($"PASS  {name}  ({clock.Elapsed.TotalSeconds:F1} s)  {line}");
    }
    catch (TestFailed e)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}  ({clock.Elapsed.TotalSeconds:F1} s)  {e.Message}");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}  ({clock.Elapsed.TotalSeconds:F1} s)  {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
    }
}
Console.WriteLine($"{ran - failed} of {ran} passed in {total.Elapsed.TotalSeconds:F0} s; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 >> 20:N0} MB");
return failed == 0 ? 0 : 1;
