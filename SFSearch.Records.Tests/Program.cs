// sfsrecord-tests: the tests of the records layer (milestone M3 of design\agent-first\PLAN.md).
//
//   sfsrecord-tests <Data folder> [--only name,name] [--work <folder>] [--report <folder>] [--heavy] [--heavy-cards]
//
//   sfsrecord-tests --perf <Data folder> …   how direct reads scale with threads (see Perf.cs)
//
// Every test prints one line with its numbers. A test that counts (the one-record verify) prints DONE with its
// numbers and every difference grouped by cause; it fails only when a record gets no verdict at all.
// The Data folder is only read: copies and scratch files go to the work folder (default %TEMP%\sfsrecord-tests),
// which is emptied at the start and removed at the end. --report keeps the full lists (one file per test).
// --heavy adds the checks that read Starfield.esm's cells through the overlay (about 1 GB of groups): run those
// with HEAVY_FULL=1 through heavy.sh. With --heavy the verify also reads Starfield.esm's one heavy record, its NAVI
// (4 GB), and heavy-part reads a field of it in part. --heavy-cards makes a card of it too, which takes far more: see the note.
using System.Diagnostics;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Records.Tests;

if (args.Length >= 2 && args[0] == "--perf") { Console.OutputEncoding = System.Text.Encoding.UTF8; return Perf.Run(args); }
if (args.Length < 1) { Console.Error.WriteLine("usage: sfsrecord-tests <Data folder> [--only name,name] [--work <folder>] [--report <folder>] [--heavy]"); return 2; }
var rest = args.Skip(1).ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
Console.OutputEncoding = System.Text.Encoding.UTF8;

var ctx = new Ctx
{
    Data = Path.GetFullPath(args[0]),
    Work = Path.GetFullPath(Opt("--work") ?? Path.Combine(Path.GetTempPath(), "sfsrecord-tests")),
    Report = Opt("--report") is { } rep ? Path.GetFullPath(rep) : null,
    Heavy = rest.Remove("--heavy"),
    HeavyCards = rest.Remove("--heavy-cards"),
};
var only = Opt("--only")?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
if (Path.GetFileName(ctx.Work) != "sfsrecord-tests" && !File.Exists(Path.Combine(ctx.Work, ".sfsrecord-tests")) && Directory.Exists(ctx.Work) && Directory.EnumerateFileSystemEntries(ctx.Work).Any())
{ Console.Error.WriteLine($"Error: the work folder {ctx.Work} isn't empty and wasn't made by these tests. Fix: name an empty folder."); return 2; }
if (Directory.Exists(ctx.Work)) Directory.Delete(ctx.Work, true);
Directory.CreateDirectory(ctx.Work);
File.WriteAllText(Path.Combine(ctx.Work, ".sfsrecord-tests"), "made by sfsrecord-tests; safe to delete");
if (ctx.Report != null) Directory.CreateDirectory(ctx.Report);

var tests = new (string Name, Func<Ctx, string> Run)[]
{
    ("sizes", Tests.Sizes),
    ("known-weapon", Tests.KnownWeapon),
    ("reflection-card", ReflectionCard.Run),
    ("evidence-items", EvidenceItems.Run),
    ("link-positions", LinkPositions.Run),
    ("value-positions", ValuePositions.Run),
    ("heavy-list", HeavyPart.List),
    ("heavy-part", HeavyPart.Run),
    ("unknown-field", Tests.UnknownField),
    ("formid-reader", Tests.FormIdReaderEqualsIndex),
    ("raw-reader", Tests.RawReader),
    ("plugin-changed", Tests.PluginChanged),
    ("strings-changed", StringsChanged.Run),
    ("missing-master", Tests.MissingMaster),
    ("split-assemble", Tests.SplitAndAssemble),
    ("languages", Tests.LanguagesByName),
    ("languages-every-view", Languages.EveryView),
    ("languages-cost", Languages.Cost),
    ("cards-mods", Tests.CardsOfTheMods),
    ("cards-starfield", Tests.CardsOfStarfield),
    ("names-with-spaces", Tests.NamesWithSpaces),
    ("fields-defaults", Tests.FieldsDefaults),
    ("card-defaults", Tests.CardDefaultsShown),
    ("derived-fields", Tests.DerivedFields),
    ("names-from-bytes", Tests.NamesFromBytes),
    ("names-load-order", Tests.NamesWholeLoadOrder),
    ("text-places", TextSearch.Places),
    ("text-search", TextSearch.Search),
    ("text-plain", TextSearch.Plain),
    ("verify-mods", Tests.VerifyTheMods),
    ("verify-starfield", Tests.VerifyStarfield),
    ("verify-json-notes", JsonNotes.Run),
    ("timing", Tests.Timing),
    ("nested-route", Tests.NestedRoute),
    ("direct-top-level", Tests.DirectTopLevel),
    ("threads-equal", Threads.Equal),
    ("json-writer", Threads.SameWriter),
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
        try
        {
            string numbers = run(ctx);
            string status = numbers.StartsWith("DONE ", StringComparison.Ordinal) ? "DONE " : numbers.StartsWith("SKIP ", StringComparison.Ordinal) ? "SKIP " : "PASS ";
            if (status != "PASS ") numbers = numbers[5..];
            Console.WriteLine($"{status} {name} ({sw.Elapsed.TotalSeconds:F1} s): {numbers}");
        }
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
Console.WriteLine($"{ran - failed} of {ran} tests ran through without a failure in {total.Elapsed.TotalSeconds:F0} s, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB.");
return failed == 0 ? 0 : 1;

sealed class TestFailed(string message) : Exception(message);

sealed class Ctx : IDisposable
{
    public required string Data, Work;
    public string? Report;
    public bool Heavy, HeavyCards;
    HeaderIndex? _dataIndex; RecordSchema? _schema; RecordEngine? _engine;
    readonly List<IDisposable> _open = new();

    public string Scratch => Path.Combine(Work, "scratch");
    public RecordSchema Schema => _schema ??= new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
    public string VersionText => $"sfsrecord-tests / mutagen {Schema.MutagenVersion} / schema {Schema.Version}";

    /// <summary>The header index of the whole Data folder, made once.</summary>
    public HeaderIndex DataIndex
    {
        get
        {
            if (_dataIndex == null) { _dataIndex = Track(new HeaderIndex(PluginSource.Folder(Data), Options())); _dataIndex.Refresh(); }
            return _dataIndex;
        }
    }

    /// <summary>The engine over the whole Data folder, made once: plugins stay open from test to test.</summary>
    public RecordEngine Engine => _engine ??= Track(new RecordEngine(DataIndex, Schema, Path.Combine(Work, "verify"), Data));

    public IndexOptions Options(string? version = null) => new() { ScratchFolder = Path.Combine(Scratch, "index"), VersionText = version ?? VersionText, StringFiles = new StringFiles(Data) };
    public T Track<T>(T item) where T : IDisposable { _open.Add(item); return item; }
    public string Folder(string name) { string f = Path.Combine(Work, name); Directory.CreateDirectory(f); return f; }
    public void Dispose() { for (int i = _open.Count - 1; i >= 0; i--) _open[i].Dispose(); _open.Clear(); }

    public void WriteReport(string name, IEnumerable<string> lines)
    {
        if (Report != null) File.WriteAllLines(Path.Combine(Report, name), lines);
    }

    public static void Check(bool ok, string what) { if (!ok) throw new TestFailed(what); }
}
