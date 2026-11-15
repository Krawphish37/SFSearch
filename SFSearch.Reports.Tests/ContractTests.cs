// The output contract of ARCHITECTURE.md section 7 for the four reports (compact by default, [more] with a cursor,
// [notes], [source], [cost], an empty result that says why, an error as one line), and what each report takes.
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Starfield;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ContractTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static readonly Regex AnyKey = new(@"[0-9A-F]{6}:[^\s()\[\],;|""]+\.es[mpl]", RegexOptions.IgnoreCase);

    static void Contract(string name, ReportAnswer a, string text)
    {
        var lines = text.TrimEnd('\n').Split('\n');
        Check(lines[^1].StartsWith("[cost] ", StringComparison.Ordinal), $"{name}: the last line isn't [cost]: {lines[^1]}");
        Check(lines.Count(l => l.StartsWith("[source] ", StringComparison.Ordinal)) == 1, $"{name}: there isn't exactly one [source] line");
        int source = Array.FindIndex(lines, l => l.StartsWith("[source] ", StringComparison.Ordinal));
        Check(source == lines.Length - 2, $"{name}: [source] isn't the line before [cost]");
        // Notes come before the source, and nowhere else
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("[notes] ", StringComparison.Ordinal)) Check(i < source && lines.Skip(i).Take(source - i).All(l => l.StartsWith("[notes] ", StringComparison.Ordinal)), $"{name}: a [notes] line isn't in the block before [source]");
        // Every record is named with its plugin, in brackets: no bare FormID
        foreach (var line in lines.Take(source))
            foreach (Match m in AnyKey.Matches(line))
            {
                char before = m.Index > 0 ? line[m.Index - 1] : ' ';
                char after = m.Index + m.Length < line.Length ? line[m.Index + m.Length] : ' ';
                Check((before == '(' && after == ')') || (before == '[' && after == ']'), $"{name}: the FormKey {m.Value} isn't in brackets: {line}");
            }
        var json = a.ToJson();
        Check(json["kind"]?.GetValue<string>() == a.Kind && json["notes"] != null && json["source"] != null && json["cost"]?["seconds"] != null, $"{name}: the structured answer lacks kind, notes, source or cost");
    }

    public static string OutputContract(Ctx ctx)
    {
        var s = ctx.Session;
        var check = new CheckReport(s); var leveled = new LeveledReport(s); var dialogue = new DialogueReport(s); var sources = new SourcesReport(s);
        int answers = 0;
        // ---- each report, compact and in detail
        var made = new (string Name, ReportAnswer Answer)[]
        {
            ("check newcombinedflorafauna.esm", check.Check("newcombinedflorafauna.esm")),
            ("check darkstarpatch_crimsonx.esm", check.Check("darkstarpatch_crimsonx.esm")),
            ("leveled 0032EE:Starfield.esm", leveled.Leveled("0032EE:Starfield.esm")),
            ("dialogue CF03", dialogue.Dialogue("CF03")),
            ("sources kgcdoom.esm", sources.Sources("kgcdoom.esm")),
        };
        foreach (var (name, a) in made)
            foreach (bool detail in new[] { false, true })
            {
                string text = a.ToText(new ReportTextOptions { Detail = detail, MaxChars = int.MaxValue });
                Contract(name + (detail ? " (detail)" : ""), a, text);
                answers++;
            }
        // Compact by default: a check lists 25 rows per check and says how many more there are
        var crimson = (CheckAnswer)made[1].Answer;
        string compact = crimson.ToText(), full = crimson.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue });
        Check(compact.Contains("… 359 more (detail lists every one)", StringComparison.Ordinal) && compact.Length < full.Length / 3, "the compact check answer doesn't fold the 384 broken links to 25 and a count");

        // ---- pages: a long answer is cut between rows, says so, and the pages together are the whole
        int pages = 0, rows = 0;
        {
            var uncut = full.TrimEnd('\n').Split('\n').ToList();
            int headEnd = uncut.FindIndex(l => l.StartsWith("ERROR: ", StringComparison.Ordinal) || l.StartsWith("WARN: ", StringComparison.Ordinal));
            var head = uncut.Take(headEnd).ToList();
            var body = uncut.Skip(headEnd).TakeWhile(l => !l.StartsWith("[notes] ", StringComparison.Ordinal) && !l.StartsWith("[source] ", StringComparison.Ordinal)).ToList();
            var got = new List<string>();
            int cursor = 0;
            while (cursor >= 0)
            {
                string page = crimson.ToText(new ReportTextOptions { Detail = true, MaxChars = 6000, Cursor = cursor }, out int next);
                var pl = page.TrimEnd('\n').Split('\n').ToList();
                Check(pl.Take(head.Count).SequenceEqual(head), $"page {pages + 1} doesn't start with the heading");
                Contract($"check page {pages + 1}", crimson, page);
                var pageRows = pl.Skip(head.Count).Where(l => !l.StartsWith("(continued from", StringComparison.Ordinal)).TakeWhile(l => !l.StartsWith("[more] ", StringComparison.Ordinal) && !l.StartsWith("[notes] ", StringComparison.Ordinal) && !l.StartsWith("[source] ", StringComparison.Ordinal)).ToList();
                got.AddRange(pageRows);
                bool more = pl.Any(l => l.StartsWith("[more] ", StringComparison.Ordinal));
                Check(more == (next >= 0), $"page {pages + 1}: [more] and the cursor disagree");
                if (more) Check(pl.First(l => l.StartsWith("[more] ", StringComparison.Ordinal)).Contains($"Next: cursor \"{next}\"", StringComparison.Ordinal), $"page {pages + 1}: [more] doesn't give the cursor {next}");
                Check(string.Join('\n', pageRows).Length <= 6000 + 400, $"page {pages + 1} has {string.Join('\n', pageRows).Length} characters of rows");
                pages++; cursor = next;
                Check(pages < 500, "the pages don't end");
            }
            Check(got.SequenceEqual(body), $"the {pages} pages together have {got.Count} lines of rows; the uncut answer has {body.Count}");
            rows = body.Count;
        }

        // ---- an empty result says why
        var empties = new List<string>();
        {
            var a = check.Check("atom74.esm");
            string t = a.ToText();
            Check(a.Errors == 0 && t.Contains("No check found anything.", StringComparison.Ordinal), $"a clean plugin's check doesn't say that nothing was found:\n{t}");
            Contract("check atom74.esm", a, t);
            empties.Add("a check that finds nothing: \"No check found anything.\"");
        }
        {
            var mod = Made.New("m11a_empty.esm");
            mod.LeveledItems.AddNew("m11a_list_empty");
            mod.Quests.AddNew("m11a_quest_silent");
            string path = Made.Write(ctx, mod, "made-empty");
            var a = leveled.Leveled("m11a_list_empty", new LeveledOptions { In = path });
            string t = a.ToText();
            Check(a.EntryCount == 0 && t.Contains("The list has no entries in m11a_empty.esm.", StringComparison.Ordinal), $"an empty list's answer doesn't say so:\n{t}");
            Contract("leveled empty", a, t);
            empties.Add("a leveled list with no entries: \"The list has no entries in m11a_empty.esm.\"");
            var d = dialogue.Dialogue("m11a_quest_silent", new DialogueOptions { In = path });
            t = d.ToText();
            Check(d.Topics.Count == 0 && t.Contains("No dialogue topic belongs to this quest in", StringComparison.Ordinal) && t.Contains("Another plugin may hold them", StringComparison.Ordinal), $"a quest with no dialogue doesn't say so:\n{t}");
            Contract("dialogue empty", d, t);
            empties.Add("a quest with no dialogue: \"No dialogue topic belongs to this quest in …. Another plugin may hold them: …\"");
            var so = sources.Sources(path);
            t = so.ToText();
            Check(so.Items.Count == 0 && t.Contains("adds no record of the types WEAP, ARMO", StringComparison.Ordinal), $"a plugin with no items doesn't say so:\n{t}");
            Contract("sources empty", so, t);
            empties.Add("a plugin with no weapon or armor: \"… adds no record of the types WEAP, ARMO …\"");
        }
        {
            var a = sources.Sources("atom74.esm", new SourcesOptions { Item = "NoSuchItem", Dependents = false });
            string t = a.ToText();
            Check(a.Items.Count == 0 && t.Contains("adds 3 such records, and none is \"NoSuchItem\"", StringComparison.Ordinal), $"an item that isn't there doesn't say so:\n{t}");
            empties.Add("an item filter that fits nothing: \"atom74.esm adds 3 such records, and none is \"NoSuchItem\".\"");
        }

        // ---- a wrong call is one line: Error: what. Fix: what to do.
        var wrong = new (string What, Action Call)[]
        {
            ("check of a plugin that isn't there", () => check.Check("nosuchplugin.esm")),
            ("leveled of a weapon", () => leveled.Leveled("0476C4:Starfield.esm")),
            ("leveled of a name no record has", () => leveled.Leveled("NoSuchListAnywhere")),
            ("leveled of a FormKey in a plugin that isn't there", () => leveled.Leveled("000800:nosuchplugin.esm")),
            ("leveled of a record its plugin doesn't hold", () => leveled.Leveled("LL_Weapon_ccAtom74", new LeveledOptions { In = "weap_starlight.esm" })),
            ("dialogue of a weapon", () => dialogue.Dialogue("0476C4:Starfield.esm")),
            ("sources of a record type that isn't one", () => sources.Sources("atom74.esm", new SourcesOptions { Signatures = new[] { "Sword" } })),
            ("sources of a plugin that isn't there", () => sources.Sources("nosuchplugin.esm")),
        };
        var errorLines = new List<string>();
        foreach (var (what, call) in wrong)
        {
            try { call(); throw new TestFailed($"{what}: no error"); }
            catch (ReportException e)
            {
                Check(Regex.IsMatch(e.Line, @"^Error: [^\n]+\. Fix: [^\n]+\.$"), $"{what}: the error isn't one line with its fix: {e.Line}");
                errorLines.Add(e.Line);
            }
        }
        ctx.WriteReport("output-contract-errors.txt", string.Join('\n', errorLines) + "\n");
        return $"{answers} answers (5 reports, compact and in detail) each end with [source] and [cost], keep [notes] in one block before them, name every record with its plugin in brackets, and have a structured form. " +
            $"Compact: the 384 broken links of darkstarpatch_crimsonx.esm print as 25 and \"… 359 more\". Cut at 6,000 characters of rows a page, the detailed answer is {pages} pages, each with the heading and the next cursor; together they are the uncut answer's {N(rows)} lines, none lost and none twice. " +
            $"{empties.Count} empty results each say why ({string.Join("; ", empties)}). {wrong.Length} wrong calls each stop with one line \"Error: …. Fix: ….\"";
    }

    // ---------------------------------------------------------------- Time
    public static string Timing(Ctx ctx)
    {
        var s = ctx.Session;
        var check = new CheckReport(s); var leveled = new LeveledReport(s); var dialogue = new DialogueReport(s); var sources = new SourcesReport(s);
        var parts = new List<string>();
        string Time(string what, Func<ReportAnswer> run, int again = 1)
        {
            var sw = Stopwatch.StartNew();
            var a = run();
            double first = sw.Elapsed.TotalSeconds;
            double best = double.MaxValue;
            for (int i = 0; i < again; i++) { sw.Restart(); run(); best = Math.Min(best, sw.Elapsed.TotalSeconds); }
            if (again == 0) best = first;
            sw.Restart();
            int chars = a.ToText(new ReportTextOptions { MaxChars = int.MaxValue }).Length;
            string line = $"{what}: {first:F2} s, again {best:F2} s; text {N(chars)} characters in {sw.Elapsed.TotalMilliseconds:F0} ms";
            parts.Add(line);
            return line;
        }
        Time("check atom74.esm (317 records)", () => check.Check("atom74.esm"));
        Time("check darkstarpatch_crimsonx.esm (122 records)", () => check.Check("darkstarpatch_crimsonx.esm"));
        Time("check kgcdoom.esm, world data left out", () => check.Check("kgcdoom.esm"));
        Time("check kgcdoom.esm with world data", () => check.Check("kgcdoom.esm", new CheckOptions { World = true }));
        Time("check darkstar.esm (47,021 records), world data left out", () => check.Check("darkstar.esm"));
        Time("check darkstar.esm with world data", () => check.Check("darkstar.esm", new CheckOptions { World = true }));
        Time("leveled 0032EE:Starfield.esm (12 lists)", () => leveled.Leveled("0032EE:Starfield.esm"), 3);
        Time("leveled 143BDF:Starfield.esm (2,755 lists)", () => leveled.Leveled("143BDF:Starfield.esm"));
        Time("dialogue MQ101 (411 topics)", () => dialogue.Dialogue("MQ101"));
        Time("dialogue Com_Companion_Barrett (1,829 topics)", () => dialogue.Dialogue("1C7187:Starfield.esm"));
        Time("sources atom74.esm alone", () => sources.Sources("atom74.esm", new SourcesOptions { Dependents = false }));
        Time("sources atom74.esm with its patches", () => sources.Sources("atom74.esm"));
        Time("sources kgcdoom.esm alone", () => sources.Sources("kgcdoom.esm", new SourcesOptions { Dependents = false }));
        Time("sources darkstar.esm alone (1,429 items)", () => sources.Sources("darkstar.esm", new SourcesOptions { Dependents = false }), 0);
        ctx.WriteReport("timing.txt", string.Join('\n', parts) + "\n");
        return string.Join("; ", parts) + $". In this process, with the plugins already open from the tests before; peak working set so far {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB. New-process times and memory are in the note.";
    }
}
