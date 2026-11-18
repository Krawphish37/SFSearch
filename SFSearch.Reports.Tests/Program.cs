// sfsreport-tests: the tests of the checks and reports (part of milestone M11 of design\agent-first\PLAN.md).
//
//   sfsreport-tests <Data folder> [--only name,name] [--work <folder>] [--report <folder>]
//                   [--today <folder>] [--sfsearch <SFSearch.exe> --home <test home>] [--baseline <folder>]
//
// Every test prints one line with its numbers. The Data folder is only read: test plugins (written by Mutagen) go
// to the work folder (default %TEMP%\sfsreport-tests), which is emptied at the start.
// --report keeps the full lists and the answers' texts (one file per test).
// --today: a folder with the answers of today's SFSearch commands, one file per command (<name>.txt). An answer
//   that isn't there is made now with --sfsearch (this worktree's build of today's SFSearch) on --home (a test home,
//   never the user's own), with no service, and kept there. Without either, the tests that hold the new reports
//   against today's commands print SKIP.
// --champollion: Champollion.exe (the Papyrus decompiler), for the scripts tests. --champollion-home: an SFSearch home
//   folder whose sfsearch.json or toolsampollion folder says where it is (only read). Without either, and with no
//   SFSEARCH_CHAMPOLLION variable, the scripts tests print SKIP with the reason.
// --baseline: the folder with the recorded commands' answers of today's SFSearch, as the M12 harness kept them
//   (<work>\baseline\cli\recorded). It is only read.
using System.Diagnostics;
using System.Text.Json.Nodes;
using SFSearch.Records;
using SFSearch.Reports;
using SFSearch.Reports.Tests;

if (args.Length < 1) { Console.Error.WriteLine("usage: sfsreport-tests <Data folder> [--only name,name] [--work <folder>] [--report <folder>] [--today <folder>] [--sfsearch <exe> --home <folder>] [--baseline <folder>] [--champollion <Champollion.exe> | --champollion-home <SFSearch home>]"); return 2; }
var rest = args.Skip(1).ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
Console.OutputEncoding = System.Text.Encoding.UTF8;

var ctx = new Ctx
{
    Data = Path.GetFullPath(args[0]),
    Work = Path.GetFullPath(Opt("--work") ?? Path.Combine(Path.GetTempPath(), "sfsreport-tests")),
    Report = Opt("--report") is { } rep ? Path.GetFullPath(rep) : null,
    TodayFolder = Opt("--today") is { } t ? Path.GetFullPath(t) : null,
    TodayExe = Opt("--sfsearch") is { } exe ? Path.GetFullPath(exe) : null,
    TodayHome = Opt("--home") is { } home ? Path.GetFullPath(home) : null,
    Baseline = Opt("--baseline") is { } b ? Path.GetFullPath(b) : null,
    Champollion = ScriptsReport.FindChampollion(Opt("--champollion"), Opt("--champollion-home")),
};
var only = Opt("--only")?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
if (ctx.Work.StartsWith(ctx.Data, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("Error: the work folder is inside the Data folder. Fix: name a folder outside it."); return 2; }
if (Path.GetFileName(ctx.Work) != "sfsreport-tests" && !File.Exists(Path.Combine(ctx.Work, ".sfsreport-tests")) && Directory.Exists(ctx.Work) && Directory.EnumerateFileSystemEntries(ctx.Work).Any())
{ Console.Error.WriteLine($"Error: the work folder {ctx.Work} isn't empty and wasn't made by these tests. Fix: name an empty folder."); return 2; }
if (Directory.Exists(ctx.Work)) Directory.Delete(ctx.Work, true);
Directory.CreateDirectory(ctx.Work);
File.WriteAllText(Path.Combine(ctx.Work, ".sfsreport-tests"), "made by sfsreport-tests; safe to delete");
if (ctx.Report != null) Directory.CreateDirectory(ctx.Report);

var tests = new (string Name, Func<Ctx, string> Run)[]
{
    ("link-rules", LinkTests.Rules),
    ("link-walk", LinkTests.Walk),
    ("check-today", CheckTests.Today),
    ("check-made", CheckTests.Made),
    ("check-index", CheckTests.FromIndex),
    ("check-world", CheckTests.World),
    ("check-itm-headers", CheckTests.ItmHeaders),
    ("check-itm-paging", CheckTests.ItmPaging),
    ("check-itm-partial", CheckTests.ItmPartial),
    ("check-itm-heavy", CheckTests.ItmHeavy),
    ("check-itm-xedit", CheckTests.ItmLikeXEdit),
    ("check-stored-text", CheckTests.StoredText),
    ("check-formid-forms", CheckTests.StoredForms),
    ("refused-plugin", RefusedTests.MissionsGalore),
    ("leveled-today", LeveledTests.Today),
    ("leveled-made", LeveledTests.Made),
    ("dialogue-today", DialogueTests.Today),
    ("dialogue-winners", DialogueTests.Winners),
    ("dialogue-language", DialogueTests.Language),
    ("sources-today", SourcesTests.Today),
    ("sources-dependents", SourcesTests.Dependents),
    ("sources-vendors", SourcesTests.Vendors),
    ("sources-injectors", SourcesTests.Injectors),
    ("sources-formlists", SourcesTests.FormListInjectors),
    ("sources-addtolist", SourcesTests.AddToList),
    ("sources-speed", SourcesTests.Speed),
    ("sources-injector-speed", SourcesTests.InjectorSpeed),
    ("output-contract", ContractTests.OutputContract),
    ("timing", ContractTests.Timing),
    ("archive-today", ArchiveTests.Today),
    ("archive-extract", ArchiveTests.Extract),
    ("archive-contents", ArchiveTests.Contents),
    ("archive-name-table", ArchiveTests.NameTable),
    ("archive-grep", ArchiveGrepTests.Grep),
    ("assets-today", AssetsTests.Today),
    ("assets-paths", AssetsTests.Paths),
    ("assets-armor-links", AssetsTests.ArmorLinks),
    ("assets-fields", AssetFieldsTests.Parity),
    ("materials-today", MaterialsTests.Today),
    ("materials-cdb", MaterialDatabaseTests.Databases),
    ("materials-mesh", MaterialsMeshTests.Names),
    ("materials-files", MaterialFileTests.Files),
    ("materials-shaders", MeshShaderTests.Shaders),
    ("scripts-today", ScriptsTests.Today),
    ("scripts-kept", ScriptsTests.Kept),
    ("scripts-code", ScriptsTests.Code),
    ("scripts-speed", ScriptsTests.Speed),
    ("changes-made", ChangesTests.Made),
    ("changes-master-size", ChangesTests.MasterSize),
    ("changes-strings", ChangesTests.Strings),
    ("files-contract", FilesContractTests.OutputContract),
    ("files-timing", FilesContractTests.Timing),
    ("profile-fields", ProfileTests.Fields),
    ("profile-groups", ProfileTests.Groups),
    ("profile-conditions", ProfileCensusTests.Conditions),
    ("profile-links", ProfileLinkTests.Links),
    ("profile-subrecords", ProfileLinkTests.Subrecords),
    ("profile-order", ProfileOrderTests.Order),
    ("profile-stats", ProfileStatsTests.Stats),
    ("profile-values", ProfileValuesTests.Values),
    ("profile-formids", ProfileFormIdsTests.FormIds),
    ("profile-lengths", ProfileLengthsTests.Lengths),
    ("profile-values-data", ProfileValuesTests.Data),
    ("profile-formids-data", ProfileFormIdsTests.Data),
    ("profile-bytes", LinkCensusTests.Profile),
    ("link-census", LinkCensusTests.Run),
    ("link-census-mods", LinkCensusTests.Mods),
    ("profile-bytes-records", LinkCensusTests.ProfileRecords),
    ("link-census-official", LinkCensusTests.Official),
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
    public string? Report, TodayFolder, TodayExe, TodayHome, Baseline, Champollion;
    RecordSchema? _schema; ReportSession? _session;

    public RecordSchema Schema => _schema ??= new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
    /// <summary>One session over the whole Data folder for all tests: plugins stay open from test to test.</summary>
    public ReportSession Session => _session ??= new ReportSession(Data, Path.Combine(Work, "scratch"), Schema, "sfsreport-tests / mutagen " + Schema.MutagenVersion + " / schema " + Schema.Version) { KeptFolder = Path.Combine(Work, "kept"), Champollion = Champollion };
    public string Folder(string name) { string f = Path.Combine(Work, name); Directory.CreateDirectory(f); return f; }
    public void Dispose() { _session?.Dispose(); _session = null; }

    public void WriteReport(string name, string text)
    {
        if (Report != null) File.WriteAllText(Path.Combine(Report, name), text);
    }

    public static void Check(bool ok, string what) { if (!ok) throw new TestFailed(what); }

    public void MustBeInWork(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refusing to write {path}: it isn't inside the tests' work folder");
    }

    // ---------------------------------------------------------------- Today's SFSearch
    public bool CanRunToday => TodayExe != null && TodayHome != null && File.Exists(TodayExe);
    public bool HasToday => TodayFolder != null || CanRunToday;

    /// <summary>
    /// What today's SFSearch prints for a command: the kept answer in the --today folder under this name, or, when
    /// there is none, the answer made now (this worktree's build, the test home, no service), which is then kept.
    /// Null when there is neither.
    /// </summary>
    public string? Today(string name, params string[] arguments)
    {
        string? kept = TodayFolder == null ? null : Path.Combine(TodayFolder, name + ".txt");
        if (kept != null && File.Exists(kept)) return File.ReadAllText(kept);
        if (!CanRunToday) return null;
        Directory.CreateDirectory(TodayHome!);
        var psi = new ProcessStartInfo(TodayExe!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["SFSEARCH_HOME"] = TodayHome!;
        psi.Environment["SFSEARCH_DATA"] = Data;
        psi.Environment["SFSEARCH_WARMUP"] = "0";
        if (Champollion != null) psi.Environment["SFSEARCH_CHAMPOLLION"] = Champollion;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30 * 60 * 1000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new TestFailed($"today's SFSearch didn't finish in 30 minutes: {string.Join(' ', arguments)}"); }
        _ = err.Result;
        if (kept != null) { Directory.CreateDirectory(TodayFolder!); File.WriteAllText(kept, o); }
        return o;
    }

    /// <summary>Today's answer for a recorded command of the M12 harness, from the kept baseline. Null when it isn't there.</summary>
    public string? Recorded(string id)
    {
        if (Baseline == null) return null;
        string kept = Path.Combine(Baseline, id + ".json");
        return File.Exists(kept) ? JsonNode.Parse(File.ReadAllText(kept))!["answer"]?["out"]?.GetValue<string>() : null;
    }

    // ---------------------------------------------------------------- Reading today's text
    /// <summary>The rows of every Markdown table in a text, each as its cells. The header and the ruler rows are left out.</summary>
    public static List<string[]> Table(string text, string? firstHeaderCell = null)
    {
        var rows = new List<string[]>();
        bool inTable = false, wanted = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (!line.StartsWith("| ", StringComparison.Ordinal)) { inTable = false; continue; }
            var cells = line.Trim().Trim('|').Split(" | ").Select(c => c.Trim()).ToArray();
            if (!inTable) { inTable = true; wanted = firstHeaderCell == null || cells[0] == firstHeaderCell; continue; }
            if (cells[0].StartsWith(":---", StringComparison.Ordinal) || cells[0].StartsWith("---", StringComparison.Ordinal)) continue;
            if (wanted) rows.Add(cells);
        }
        return rows;
    }

    static readonly System.Text.RegularExpressions.Regex FormKeyRx = new(@"\b([0-9A-Fa-f]{6}):([^\s\[\]|:,;()]+\.es[mpl])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The first FormKey in a text, as "0003c2:darkstar.esm" (lower case), or null.</summary>
    public static string? FirstKey(string text)
    {
        var m = FormKeyRx.Match(text);
        return m.Success ? Key(m.Value) : null;
    }

    public static List<string> Keys(string text) => FormKeyRx.Matches(text).Select(m => Key(m.Value)).ToList();

    /// <summary>A FormKey in the one spelling the tests compare by.</summary>
    public static string Key(string formKey) => formKey.Trim().ToLowerInvariant();
}
