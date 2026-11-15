// The output contract for the file reports (archive, assets, materials, scripts, changes), and what each costs in
// a process that is already up.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class FilesContractTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string OutputContract(Ctx ctx)
    {
        var s = ctx.Session;
        var archive = new ArchiveReport(s); var assets = new AssetsReport(s); var materials = new MaterialsReport(s); var scripts = new ScriptsReport(s);
        var changes = new ChangesReport(s, null, Path.Combine(ctx.Folder("contract-history"), "history"));
        bool decompiler = s.Champollion != null;

        var answers = new List<(string Name, ReportAnswer Answer)>
        {
            ("archive summary", archive.Summary("darkstar.esm")),
            ("archive list", archive.List("darkstar.esm", "*.mat")),
            ("archive find", archive.Find("bullpupdummyreceiver")),
            ("archive show", archive.Show("darkstar.esm", archive.List("darkstar.esm", "*.mat").Files[0].Path)),
            ("assets", assets.Assets("kgcdoom.esm")),
            ("materials", materials.Materials("aa50_longwatch.esm")),
            ("changes", changes.Changes()),
            ("changes of one plugin", changes.Changes(new ChangesOptions { Plugin = "atom74.esm" })),
        };
        if (decompiler)
        {
            answers.Add(("scripts", scripts.Scripts("darkstar.esm")));
            answers.Add(("scripts find", scripts.Scripts("darkstar.esm", new ScriptsOptions { Find = "AddItem" })));
        }
        var log = new StringBuilder();
        int texts = 0;
        foreach (var (name, a) in answers)
            foreach (bool detail in new[] { false, true })
            {
                string text = a.ToText(new ReportTextOptions { Detail = detail, MaxChars = int.MaxValue });
                texts++;
                var lines = text.TrimEnd('\n').Split('\n');
                Check(lines[^1].StartsWith("[cost] ", StringComparison.Ordinal) && Regex.IsMatch(lines[^1], @"^\[cost\] \d+\.\d+ s"), $"{name}: the last line isn't the cost: {lines[^1]}");
                Check(lines[^2].StartsWith("[source] ", StringComparison.Ordinal), $"{name}: the line before the cost isn't the source: {lines[^2]}");
                // Notes sit in one block right before the source
                int firstNote = Array.FindIndex(lines, l => l.StartsWith("[notes] ", StringComparison.Ordinal));
                if (firstNote >= 0) for (int i = firstNote; i < lines.Length - 2; i++) Check(lines[i].StartsWith("[notes] ", StringComparison.Ordinal), $"{name}: a line among the notes isn't a note: {lines[i]}");
                Check(a.Notes.Count == lines.Count(l => l.StartsWith("[notes] ", StringComparison.Ordinal)), $"{name}: {a.Notes.Count} notes, {lines.Count(l => l.StartsWith("[notes] ", StringComparison.Ordinal))} printed");
                // No bare FormID: every six-digit ID in a record's name stands before a colon and its plugin
                foreach (Match m in Regex.Matches(text, @"\(([0-9A-F]{6})([:)])")) Check(m.Groups[2].Value == ":", $"{name}: a FormID without its plugin: {m.Value}");
                var json = a.ToJson();
                Check(json["kind"]?.GetValue<string>() == a.Kind && json["notes"] is JsonArray && json["source"] is JsonArray && json["cost"]?["seconds"] != null, $"{name}: the structured answer lacks kind, notes, source or cost");
                if (!detail) log.Append($"=== {name}\n{(text.Length > 6000 ? text[..3000] + "\n…\n" + text[^1500..] : text)}\n");
            }

        // ---- a long answer comes in pages: together they are the uncut rows, none lost and none twice
        var big = archive.List("darkstar.esm");
        string whole = big.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        var wholeRows = whole.Split('\n').Where(l => l.StartsWith("| darkstar - ", StringComparison.Ordinal)).ToList();
        var paged = new List<string>(); int pages = 0, cursor = 0;
        while (cursor >= 0)
        {
            string page = big.ToText(new ReportTextOptions { MaxChars = 20000, Cursor = cursor }, out int next);
            pages++;
            Check(page.StartsWith("Files in darkstar.esm", StringComparison.Ordinal), "a page has no heading");
            Check(next < 0 || page.Contains($"Next: cursor \"{next}\"", StringComparison.Ordinal), "a cut page doesn't give the next cursor");
            paged.AddRange(page.Split('\n').Where(l => l.StartsWith("| darkstar - ", StringComparison.Ordinal)));
            cursor = next;
        }
        Check(pages > 3 && paged.SequenceEqual(wholeRows) && wholeRows.Count == big.Files.Count, $"{pages} pages hold {paged.Count} rows, the uncut answer {wholeRows.Count}, the answer {big.Files.Count} files");
        // The text of a file pages the same way
        var shown = archive.Show("darkstar.esm", archive.List("darkstar.esm", "*.mat").Files[0].Path);
        var shownLines = new List<string>(); int shownPages = 0; cursor = 0;
        while (cursor >= 0)
        {
            string page = shown.ToText(new ReportTextOptions { MaxChars = 1500, Cursor = cursor }, out int next);
            shownPages++;
            var body = page.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("[more] ", StringComparison.Ordinal) && !l.StartsWith("[source] ", StringComparison.Ordinal)).Where(l => !l.StartsWith("(continued from ", StringComparison.Ordinal)).ToList();
            shownLines.AddRange(body);
            cursor = next;
        }
        Check(shownPages > 1 && string.Join("\n", shownLines).TrimEnd() == shown.Text!.Replace("\r\n", "\n").TrimEnd('\n').TrimEnd(), $"the file's text in {shownPages} pages isn't the file's text");

        // ---- an empty answer says why
        var empties = new List<(string What, ReportAnswer Answer, string Says)>
        {
            ("a plugin with no archives", archive.Summary("darkstarpatch_atom74.esm"), "has no archives"),
            ("a pattern that fits nothing", archive.List("darkstar.esm", "*.nosuchkind"), "files in 2 archives were looked at"),
            ("a find that hits nothing in all of Data", archive.Find("zz_no_such_file_zz"), "archives were looked at"),
            ("assets of a plugin whose records name no file", assets.Assets("darkstarpatch_atom74.esm"), ""),
            ("materials of a plugin with no archives", materials.Materials("darkstarpatch_atom74.esm"), "has no archives"),
            ("changes when nothing changed", changes.Changes(), "No plugin was added, removed or changed"),
        };
        if (decompiler) empties.Add(("scripts of a plugin that has none", scripts.Scripts("darkstarpatch_atom74.esm"), "has no compiled scripts"));
        var emptyParts = new List<string>();
        foreach (var (what, a, says) in empties)
        {
            string text = a.ToText();
            if (says.Length == 0)
            {
                // Either the plugin names no file (and says so), or everything it names is there (and says so)
                Check(text.Contains("No record of", StringComparison.Ordinal) || text.Contains("Nothing is missing", StringComparison.Ordinal), $"{what}: the answer doesn't say why it has no rows:\n{text}");
            }
            else Check(text.Contains(says, StringComparison.Ordinal), $"{what}: the answer doesn't say \"{says}\":\n{text}");
            emptyParts.Add(what);
            log.Append($"=== empty: {what}\n{text}\n");
        }

        // ---- a wrong call is one line
        var wrong = new (string What, Action Call)[]
        {
            ("an archive that isn't there", () => archive.Summary("no_such_archive.ba2")),
            ("a plugin file that isn't there", () => archive.Summary(@"C:\no\such\folder\x.esm")),
            ("a file that isn't in the archive", () => archive.Show("darkstar.esm", "materials\\nothere.mat")),
            ("no target folder", () => archive.Extract("darkstar.esm", "*.mat", "")),
            ("a target inside Data", () => archive.Extract("darkstar.esm", "*.mat", Path.Combine(ctx.Data, "Meshes"))),
            ("assets of a plugin that isn't there", () => assets.Assets("no_such_plugin.esm")),
            ("scripts with an empty text to find", () => scripts.Scripts("darkstar.esm", new ScriptsOptions { Find = "" })),
            ("changes of a plugin the history doesn't know", () => changes.Changes(new ChangesOptions { Plugin = "no_such_plugin.esm" })),
            ("a kept folder inside Data", () => { string? keep = s.KeptFolder; try { s.KeptFolder = Path.Combine(ctx.Data, "kept"); s.Kept("scripts"); } finally { s.KeptFolder = keep; } }),
        };
        var errorLines = new List<string>();
        foreach (var (what, call) in wrong)
        {
            try { call(); Check(false, $"{what}: no error"); }
            catch (ReportException e)
            {
                Check(Regex.IsMatch(e.Line, @"^Error: [^\n]+\. Fix: [^\n]+\.$"), $"{what}: the error isn't one line with its fix: {e.Line}");
                errorLines.Add(e.Line);
            }
        }
        Check(!Directory.Exists(Path.Combine(ctx.Data, "kept")), "a kept folder was made in Data");
        ctx.WriteReport("files-contract.txt", log.ToString());
        ctx.WriteReport("files-contract-errors.txt", string.Join('\n', errorLines) + "\n");
        return $"{texts} texts of {answers.Count} answers (archive, assets, materials, scripts, changes; compact and in detail) each end with [source] and [cost], keep [notes] in one block before them, never print a FormID without its plugin, and have a structured form. " +
            $"The {N(big.Files.Count)} files of darkstar.esm's archives, cut at 20,000 characters a page: {pages} pages, each with the heading and the next cursor, together the uncut rows, none lost and none twice; a file's text pages the same way ({shownPages} pages). " +
            $"{empties.Count} empty answers each say why ({string.Join("; ", emptyParts)}). {wrong.Length} wrong calls each stop with one line \"Error: …. Fix: ….\"" + (decompiler ? "" : " Champollion wasn't found: the scripts answers were left out.");
    }

    public static string Timing(Ctx ctx)
    {
        var s = ctx.Session;
        var archive = new ArchiveReport(s); var assets = new AssetsReport(s); var materials = new MaterialsReport(s); var scripts = new ScriptsReport(s);
        var changes = new ChangesReport(s, null, Path.Combine(ctx.Folder("timing-history"), "history"));
        var parts = new List<string>();
        void Time(string what, Func<ReportAnswer> run, int again = 1)
        {
            var sw = Stopwatch.StartNew();
            var a = run();
            double first = sw.Elapsed.TotalSeconds;
            double best = double.MaxValue;
            for (int i = 0; i < again; i++) { sw.Restart(); run(); best = Math.Min(best, sw.Elapsed.TotalSeconds); }
            sw.Restart();
            int chars = a.ToText(new ReportTextOptions { MaxChars = int.MaxValue }).Length;
            parts.Add($"{what}: {first:F2} s{(again > 0 ? $", again {best:F2} s" : "")}; text {N(chars)} characters in {sw.Elapsed.TotalMilliseconds:F0} ms");
        }
        Time("archive darkstar.esm (2 archives, 3,593 files)", () => archive.Summary("darkstar.esm"));
        Time("archive darkstar.esm, every file listed", () => archive.List("darkstar.esm"));
        Time("archive all, summary (457 archives)", () => archive.Summary("all"));
        Time("archive all, find a file", () => archive.Find("bullpupdummyreceiver"));
        Time("assets atom74.esm (317 records)", () => assets.Assets("atom74.esm"));
        Time("assets aa50_longwatch.esm (191 records)", () => assets.Assets("aa50_longwatch.esm"));
        Time("assets kgcdoom.esm (15,328 records, 731 read)", () => assets.Assets("kgcdoom.esm"));
        Time("assets darkstar.esm (47,021 records, 31,429 read)", () => assets.Assets("darkstar.esm"));
        Time("materials aa50_longwatch.esm (31 meshes, 133 materials)", () => materials.Materials("aa50_longwatch.esm"));
        Time("materials apc_galaxysmartcontainers.esm (207 meshes, 212 materials)", () => materials.Materials("apc_galaxysmartcontainers.esm"));
        Time("materials kgcdoom.esm", () => materials.Materials("kgcdoom.esm"));
        Time("materials darkstar.esm (34 meshes, 236 materials)", () => materials.Materials("darkstar.esm"));
        if (s.Champollion != null)
        {
            Time("scripts darkstar.esm (59 scripts), with the records read for their scripts", () => scripts.Scripts("darkstar.esm"));
            Time("scripts darkstar.esm, the list alone", () => scripts.Scripts("darkstar.esm", new ScriptsOptions { Carriers = false }));
            Time("scripts darkstar.esm, find AddItem", () => scripts.Scripts("darkstar.esm", new ScriptsOptions { Find = "AddItem" }));
        }
        Time("changes, the first look at Data (370 plugins remembered)", () => changes.Changes());
        ctx.WriteReport("files-timing.txt", string.Join('\n', parts) + "\n");
        return string.Join("; ", parts) + $". In this process, with the plugins and archive lists already in memory from the tests before; peak working set so far {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB. New-process times and memory are in the note.";
    }
}
