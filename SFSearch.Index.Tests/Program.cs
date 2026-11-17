// sfsindex-tests: the tests of the header index (milestone M2 of design\agent-first\PLAN.md).
//
//   sfsindex-tests <Data folder> [--only name,name] [--all-plugins] [--starfield-whole] [--work <folder>] [--runner <sfsindex.dll or .exe>] [--today-db <_loadorder.db>] [--race-seconds 30]
//
// Every test prints one line with its numbers. The Data folder is only read: copies and scratch files go to the work
// folder (default %TEMP%\sfsindex-tests), which is emptied at the start and removed at the end.
// The Mutagen comparison opens plugins through Mutagen, so run the whole set through heavy.sh.
using System.Diagnostics;
using SFSearch.Index;
using SFSearch.Index.Tests;

if (args.Length < 1) { Console.Error.WriteLine("usage: sfsindex-tests <Data folder> [--only name,name] [--all-plugins] [--work <folder>] [--runner <path>] [--today-db <path>]"); return 2; }
var rest = args.Skip(1).ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }

var ctx = new Ctx
{
    Data = Path.GetFullPath(args[0]),
    Work = Path.GetFullPath(Opt("--work") ?? Path.Combine(Path.GetTempPath(), "sfsindex-tests")),
    Runner = Opt("--runner"),
    TodayDb = Opt("--today-db"),
    AllPlugins = rest.Remove("--all-plugins"),
    StarfieldWhole = rest.Remove("--starfield-whole"),
};
int raceSeconds = int.Parse(Opt("--race-seconds") ?? "30");
var only = Opt("--only")?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
if (Path.GetFileName(ctx.Work) != "sfsindex-tests" && !File.Exists(Path.Combine(ctx.Work, ".sfsindex-tests")) && Directory.Exists(ctx.Work) && Directory.EnumerateFileSystemEntries(ctx.Work).Any())
{ Console.Error.WriteLine($"Error: the work folder {ctx.Work} isn't empty and wasn't made by these tests. Fix: name an empty folder."); return 2; }
if (Directory.Exists(ctx.Work)) Directory.Delete(ctx.Work, true);
Directory.CreateDirectory(ctx.Work);
File.WriteAllText(Path.Combine(ctx.Work, ".sfsindex-tests"), "made by sfsindex-tests; safe to delete");

var tests = new (string Name, Func<Ctx, string> Run)[]
{
    ("index-all", Tests.IndexAll),
    ("lookups", Tests.Lookups),
    ("load-order", Tests.LoadOrderMatchesMutagen),
    ("mutagen", Tests.EqualsMutagen),
    ("older-master", Tests.OlderMaster),
    ("today-index", Tests.EqualsTodaysIndex),
    ("date-backwards", Tests.DateBackwards),
    ("content-change-small", Tests.ContentChangeSmall),
    ("content-change-middle", Tests.ContentChangeMiddle),
    ("missing-master", Tests.MissingMaster),
    ("missing-master-chain", ChainTests.MissingMasterChain),
    ("master-size-change", Tests.MasterSizeChange),
    ("one-segment-replaced", Tests.OneSegmentReplaced),
    ("plugins-txt-change", Tests.PluginsTxtChange),
    ("changed-during-read", Tests.ChangedDuringRead),
    ("broken-files", Tests.BrokenFiles),
    ("two-processes", Tests.TwoProcesses),
    ("scratch-guard", Tests.ScratchGuard),
    ("late-header", RaceTests.LateHeader),
    ("folder-race", c => RaceTests.FolderRace(c, raceSeconds)),
    ("pipeline", PipelineTests.Run),
    ("plugin-sets", PluginSetsTests.Run),
    ("formid-forms", FormIdFormsTests.Run),
};
int failed = 0, ran = 0;
var total = Stopwatch.StartNew();
try
{
    foreach (var (name, run) in tests)
    {
        if (only != null && !only.Contains(name)) continue;
        ran++;
        var sw = Stopwatch.StartNew();
        try { string numbers = run(ctx); Console.WriteLine($"PASS  {name} ({sw.Elapsed.TotalSeconds:F1} s): {numbers}"); }
        catch (TestFailed e) { failed++; Console.WriteLine($"FAIL  {name} ({sw.Elapsed.TotalSeconds:F1} s): {e.Message}"); }
        catch (Exception e) { failed++; Console.WriteLine($"FAIL  {name} ({sw.Elapsed.TotalSeconds:F1} s): {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        Console.Out.Flush();
    }
}
finally
{
    ctx.Dispose();
    try { Directory.Delete(ctx.Work, true); } catch (IOException e) { Console.WriteLine($"[notes] the work folder {ctx.Work} couldn't be removed: {e.Message}"); }
}
Console.WriteLine($"{ran - failed} of {ran} tests passed in {total.Elapsed.TotalSeconds:F0} s, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB.");
return failed == 0 ? 0 : 1;

sealed class TestFailed(string message) : Exception(message);

sealed class Ctx : IDisposable
{
    public required string Data, Work;
    public string? Runner, TodayDb;
    public bool AllPlugins, StarfieldWhole;
    public const string Version = "sfsindex-tests";
    HeaderIndex? _dataIndex;
    readonly List<IDisposable> _open = new();

    /// <summary>One scratch folder for every index the tests make, so a plugin read once is never read again.</summary>
    public string Scratch => Path.Combine(Work, "scratch");

    /// <summary>The index of the whole Data folder, made once.</summary>
    public HeaderIndex DataIndex
    {
        get
        {
            if (_dataIndex == null) { _dataIndex = Track(new HeaderIndex(PluginSource.Folder(Data), Options())); _dataIndex.Refresh(); }
            return _dataIndex;
        }
    }

    /// <summary>Tests that replace a segment pass their own version text: their segments are then their own files,
    /// and replacing one doesn't take a file away from the other indexes of the run.</summary>
    public IndexOptions Options(string? pluginsTxt = null, string? version = null) => new() { ScratchFolder = Scratch, VersionText = version ?? Version, PluginsTxt = pluginsTxt };
    public T Track<T>(T item) where T : IDisposable { _open.Add(item); return item; }
    public string Folder(string name) { string f = Path.Combine(Work, name); Directory.CreateDirectory(f); return f; }
    public void Dispose() { foreach (var d in _open) d.Dispose(); _open.Clear(); }

    public static void Check(bool ok, string what) { if (!ok) throw new TestFailed(what); }
}
