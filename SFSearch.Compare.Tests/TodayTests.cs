// The new comparisons held against today's commands (Conflicts, Diff, Delta, PluginDiff, Check's ITM list).
// Today's commands compare a record's top-level fields as text, in the JSON of the stored databases. The new engine
// compares full-profile JSON beside the schema. Field names and values are spelled differently, so findings are
// compared, not text: the same records, and for each record the same top-level fields flagged.
// Every disagreement must have a cause the test can name from the evidence; one it can't name fails the test.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SFSearch.Compare;
using SFSearch.Records;

namespace SFSearch.Compare.Tests;

static class TodayTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- Reading today's answers
    /// <summary>Every Markdown table of an answer: its header and its rows, cell by cell.</summary>
    public static List<(List<string> Header, List<List<string>> Rows)> Tables(string text)
    {
        var tables = new List<(List<string>, List<List<string>>)>();
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i + 1 < lines.Length; i++)
        {
            if (!lines[i].StartsWith('|') || !Regex.IsMatch(lines[i + 1], @"^\|\s*:?-{3,}")) continue;
            var header = Cells(lines[i]);
            var rows = new List<List<string>>();
            int j = i + 2;
            for (; j < lines.Length && lines[j].StartsWith('|'); j++) rows.Add(Cells(lines[j]));
            tables.Add((header, rows));
            i = j - 1;
        }
        return tables;

        static List<string> Cells(string line)
        {
            var c = line.Split('|').Select(x => x.Trim()).ToList();
            if (c.Count > 0 && c[0].Length == 0) c.RemoveAt(0);
            if (c.Count > 0 && c[^1].Length == 0) c.RemoveAt(c.Count - 1);
            return c;
        }
    }

    static HashSet<string> FieldSet(string cell) =>
        cell is "NONE (ITM)" or "(first)" or "(baseline)" or "-" or "" ? new(StringComparer.Ordinal)
        : cell.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>"0009B7:atom74.esm [ccAtom74Mk2]" as its FormKey, upper case.</summary>
    static string KeyOf(string cell)
    {
        int space = cell.IndexOf(' ');
        return (space > 0 ? cell[..space] : cell).Trim('`').ToUpperInvariant();
    }

    /// <summary>The findings of one pair of records, today's and the new engine's, and the cause of each disagreement.</summary>
    sealed class Tally
    {
        public int Records, SameFields, FieldsBoth, Unexplained;
        public readonly Dictionary<string, int> Causes = new(StringComparer.Ordinal);
        public readonly List<string> Lines = new();
        public readonly List<string> Open = new();

        /// <param name="today">The top-level fields today's command flags.</param>
        /// <param name="mine">The new engine's comparison of the same two records.</param>
        /// <param name="values">Gives the two records' own values of a top-level field, for naming a cause from the records themselves.</param>
        /// <param name="sameValueCause">The cause to name when today flags a field whose value is the same JSON in both records (it must be known why today's command does that).</param>
        public void Hold(string what, HashSet<string> today, RecordDiff mine, Func<string, (JsonNode? A, JsonNode? B)>? values = null, string? sameValueCause = null)
        {
            Records++;
            // The record's flags are "MajorFlags" in today's stored JSON and MajorRecordFlagsRaw in the new one
            var now = mine.TopFields.Select(f => f == "MajorRecordFlagsRaw" ? "MajorFlags" : f).ToHashSet(StringComparer.Ordinal);
            if (today.SetEquals(now)) { SameFields++; FieldsBoth += now.Count; return; }
            FieldsBoth += today.Intersect(now).Count();
            foreach (var f in today.Except(now))
            {
                // Today flags it, the new engine doesn't call it a value difference: what does the new engine see there?
                var seen = mine.NotValues.Where(d => d.Top == f).ToList();
                string? cause = seen.Any(d => d.Class == DiffClass.WrittenAnotherWay) ? "a translated text every language shows the same, held as localized strings on one side and as the text itself on the other (today compares the JSON form)"
                    : seen.Any(d => d.Class == DiffClass.Header) ? "a number of the record header, not a value of the record"
                    : seen.Any(d => d.Class == DiffClass.Layout) ? "the length a data block is written at, with the same values"
                    : values != null && EmptyTexts(values(f)) ? "an empty text written two ways: string ID 0 in a localized plugin, an empty text in one that isn't (the new engine reads both as no text)"
                    : values != null && sameValueCause != null && values(f) is var (va, vb) && va != null && JsonNode.DeepEquals(va, vb) ? sameValueCause
                    : null;
                Note(what, f, "only today", cause);
            }
            foreach (var f in now.Except(today)) Note(what, f, "only new", KnownOnlyNew(f, mine));
        }

        /// <summary>Causes for a field only the new engine flags, each checked against the evidence in the new comparison.</summary>
        static string? KnownOnlyNew(string field, RecordDiff mine)
        {
            // Today's stored JSON holds the EditorID under the key "EDID - Editor ID", which its comparison leaves out
            // (InspectCommands.InternalMetaKeys), so today can never flag it. The new engine compares it like any field.
            if (field == "EditorID" && mine.Values.Any(d => d.Path == "EditorID" && d.A != d.B)) return "the EditorID differs; today's comparison leaves the EditorID out";
            return null;
        }

        /// <summary>True when both values are translated texts with no text, or an empty one, in every language they hold.</summary>
        static bool EmptyTexts((JsonNode? A, JsonNode? B) v)
        {
            static bool Empty(JsonNode? n) => n is JsonObject o && o.ContainsKey("TargetLanguage")
                && (o["Value"] == null || o["Value"]!.GetValue<string>().Length == 0)
                && (o["Values"] is not JsonArray list || list.All(x => (x!["String"]?.GetValue<string>() ?? "").Length == 0));
            return Empty(v.A) && Empty(v.B);
        }

        void Note(string what, string field, string side, string? cause)
        {
            string line = $"{what}: {field} flagged {side}" + (cause == null ? "" : $" ({cause})");
            Lines.Add(line);
            if (cause == null) { Unexplained++; Open.Add(line); }
            else Causes[side + ": " + cause] = Causes.GetValueOrDefault(side + ": " + cause) + 1;
        }

        public string CausesText() => Causes.Count == 0 ? "no disagreement" : string.Join("; ", Causes.OrderByDescending(c => c.Value).Select(c => $"{N(c.Value)} × {c.Key}"));
    }

    /// <summary>The two records' own values of a top-level field, read when a cause has to be named.</summary>
    static Func<string, (JsonNode? A, JsonNode? B)> Values(Ctx ctx, string pluginA, string pluginB, string recordA, string? recordB = null) => field =>
    {
        var s = ctx.Session;
        var a = s.Side(pluginA); var b = s.Side(pluginB);
        s.Use(a, b);
        var ra = s.FindIn(a, recordA);
        var rb = recordB != null ? s.FindIn(b, recordB) : CompareSession.Find(b.Index, b.Name, a.Index.Describe(ra).FormKey) ?? s.FindIn(b, recordA);
        return (s.View(a.Engine, ra).Json[field], s.View(b.Engine, rb).Json[field]);
    };

    static string Finish(Ctx ctx, string name, Tally t, string head)
    {
        ctx.WriteReport(name + ".txt", head + "\n" + string.Join('\n', t.Lines) + "\n");
        Check(t.Unexplained == 0, $"{head} {t.Unexplained} disagreement(s) have no named cause: {string.Join(" | ", t.Open.Take(12))}");
        return $"{head} Records with the same fields flagged: {N(t.SameFields)} of {N(t.Records)}; {N(t.FieldsBoth)} fields flagged by both. Disagreements: {t.CausesText()}";
    }

    static string? Skip(Ctx ctx, List<(string Id, List<string> Args, string? Out, string From)> runs) =>
        runs.Count == 0 ? "SKIP no recorded commands of this kind were found"
        : runs.All(r => r.Out == null) ? "SKIP today's answers aren't there: give --baseline (the M12 harness's kept answers) or --sfsearch and --home" : null;

    static List<string> Positional(List<string> args, params string[] withValue)
    {
        var list = new List<string>();
        for (int i = 1; i < args.Count; i++)
        {
            if (withValue.Contains(args[i], StringComparer.OrdinalIgnoreCase)) { i++; continue; }
            if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            list.Add(args[i]);
        }
        return list;
    }

    // ---------------------------------------------------------------- Delta against plugin_vs_masters
    public static string Delta(Ctx ctx)
    {
        var runs = ctx.Recorded("delta");
        if (Skip(ctx, runs) is { } skip) return skip;
        var t = new Tally();
        int commands = 0, notComparisons = 0, itmBoth = 0, itmToday = 0, itmNew = 0, baseSame = 0, rows = 0;
        foreach (var run in runs)
        {
            if (run.Out == null) continue;
            var tables = Tables(run.Out);
            if (tables.Count == 0 || !tables[0].Header.Contains("ChangedFields")) { notComparisons++; continue; } // a usage line (delta --bogus)
            commands++;
            var pos = Positional(run.Args, "--out", "--maxwidth");
            var answer = ctx.Compare.PluginVsMasters(pos[0], pos.Count > 1 ? pos[1] : null);
            var mine = answer.Overrides.ToDictionary(r => r.FormKey.ToUpperInvariant());
            var today = tables[0].Rows.ToDictionary(r => KeyOf(r[0]), r => r);
            Check(today.Keys.ToHashSet().SetEquals(mine.Keys), $"#{run.Id} delta {string.Join(' ', pos)}: today lists {today.Count} overrides, the new comparison {mine.Count}; only today: {string.Join(", ", today.Keys.Except(mine.Keys).Take(5))}; only new: {string.Join(", ", mine.Keys.Except(today.Keys).Take(5))}");
            foreach (var (key, row) in today)
            {
                var m = mine[key];
                rows++;
                Check(m.Diff != null, $"#{run.Id}: {m.Label} wasn't compared: {m.Class} {m.Problem}");
                if (row[3].Equals(m.Base, StringComparison.OrdinalIgnoreCase)) baseSame++;
                else t.Lines.Add($"#{run.Id} {m.Label}: today's base is {row[3]}, the new one {m.Base}");
                bool todayItm = row[5] == "NONE (ITM)";
                if (todayItm && m.Class == OverrideClass.Itm) itmBoth++; else if (todayItm) itmToday++; else if (m.Class == OverrideClass.Itm) itmNew++;
                t.Hold($"#{run.Id} delta {pos[0]}: {m.Label}", FieldSet(row[5]), m.Diff!, Values(ctx, m.Base!, pos[0], m.FormKey));
            }
        }
        Check(baseSame == rows, $"{rows - baseSame} of {rows} overrides are held against another master than today's Delta picks");
        return Finish(ctx, "today-delta", t, $"{commands} recorded Delta commands ({notComparisons} more is a usage line, not a comparison), {N(rows)} overrides: the same overrides listed, each against the same master as today. ITM in both: {itmBoth}; ITM only today: {itmToday}; ITM only new: {itmNew}.");
    }

    // ---------------------------------------------------------------- Diff against records
    public static string Diff(Ctx ctx)
    {
        var runs = ctx.Recorded("diff");
        if (Skip(ctx, runs) is { } skip) return skip;
        var t = new Tally();
        int commands = 0, missing = 0;
        foreach (var run in runs)
        {
            var pos = Positional(run.Args, "--maxwidth", "--ignore", "--expect");
            if (run.Out == null || pos.Take(2).Any(p => p.Contains('\\') && !File.Exists(p))) { missing++; continue; }
            if (run.Out.StartsWith("File not found", StringComparison.Ordinal)) { missing++; continue; }
            commands++;
            // Today's fields: the summary table's first column, or the top-level lines of the long form
            var today = new HashSet<string>(StringComparer.Ordinal);
            var tables = Tables(run.Out);
            if (tables.Count > 0 && tables[0].Header.Count > 1 && tables[0].Header[0] == "Field") foreach (var r in tables[0].Rows) today.Add(r[0]);
            else foreach (Match m in Regex.Matches(run.Out.Replace("\r", ""), @"(?m)^- \*\*\[(?:MODIFIED|ADDED|REMOVED)\]\*\* \*\*(.+?)\*\*")) today.Add(m.Groups[1].Value);
            var answer = ctx.Compare.Records(pos[0], pos[2], pos[1], pos.Count > 3 ? pos[3] : null);
            Check(!answer.Diff.DifferentTypes, $"#{run.Id}: the two records are of different types in the new comparison");
            t.Hold($"#{run.Id} diff {answer.A} / {answer.B}", today, answer.Diff, Values(ctx, pos[0], pos[1], pos[2], pos.Count > 3 ? pos[3] : null));
        }
        return Finish(ctx, "today-diff", t, $"{commands} recorded Diff commands ({missing} more name a file that isn't there).");
    }

    // ---------------------------------------------------------------- Conflicts against versions
    static (int Versions, string? Winner) HoldConflicts(Ctx ctx, string id, string todayOut, VersionsAnswer answer, Tally t)
    {
        var tables = Tables(todayOut);
        Check(tables.Count > 0 && tables[0].Header.Contains("Role"), $"#{id}: today's Conflicts printed no table:\n{todayOut}");
        var rows = tables[0].Rows;
        var todayLoaded = rows.Where(r => !r[2].Contains("NOT LOADED", StringComparison.Ordinal)).ToList();
        var todayNot = rows.Where(r => r[2].Contains("NOT LOADED", StringComparison.Ordinal)).ToList();
        string? todayWinner = rows.FirstOrDefault(r => r[2].Contains("WINNER", StringComparison.Ordinal))?[1];
        Check(todayLoaded.Select(r => r[1].ToLowerInvariant()).SequenceEqual(answer.Loaded.Select(r => r.Plugin.ToLowerInvariant())),
            $"#{id}: today's loaded holders are {string.Join(" > ", todayLoaded.Select(r => r[1]))}; the new ones {string.Join(" > ", answer.Loaded.Select(r => r.Plugin))}");
        Check(todayNot.Select(r => r[1].ToLowerInvariant()).ToHashSet().SetEquals(answer.NotLoaded.Select(r => r.Plugin.ToLowerInvariant())),
            $"#{id}: today's holders that aren't loaded are {string.Join(", ", todayNot.Select(r => r[1]))}; the new ones {string.Join(", ", answer.NotLoaded.Select(r => r.Plugin))}");
        Check(string.Equals(todayWinner, answer.Winner, StringComparison.OrdinalIgnoreCase), $"#{id}: today's winner is {todayWinner ?? "(none)"}, the new one {answer.Winner ?? "(none)"}");
        for (int i = 0; i < todayLoaded.Count; i++)
        {
            Check(todayLoaded[i][0] == answer.Loaded[i].Position.ToString(CultureInfo.InvariantCulture), $"#{id}: {answer.Loaded[i].Plugin} is at {todayLoaded[i][0]} in today's load order, at {answer.Loaded[i].Position} in the new one");
            bool origin = todayLoaded[i][2].StartsWith("ORIGIN", StringComparison.Ordinal);
            Check(origin == answer.Loaded[i].IsOwner, $"#{id}: {answer.Loaded[i].Plugin} is {(origin ? "" : "not ")}the origin today");
            if (i == 0 || answer.Loaded[i].Diff == null) continue;
            // What the version changes against the one before it
            if (todayLoaded[i][3] is not ("-" or "(first)")) t.Hold($"#{id} conflicts: {answer.Loaded[i].Plugin} against {answer.Loaded[i].Against}", FieldSet(todayLoaded[i][3]), answer.Loaded[i].Diff!, Values(ctx, answer.Loaded[i].Against!, answer.Loaded[i].Plugin, answer.FormKey));
        }
        return (rows.Count, answer.Winner);
    }

    public static string Conflicts(Ctx ctx)
    {
        var runs = ctx.Recorded("conflicts");
        if (Skip(ctx, runs) is { } skip) return skip;
        var t = new Tally();
        int commands = 0, versions = 0, withWinner = 0;
        var said = new List<string>();
        foreach (var run in runs)
        {
            if (run.Out == null) continue;
            commands++;
            var answer = ctx.Compare.Versions(Positional(run.Args, "--in", "--maxwidth")[0]);
            var (n, winner) = HoldConflicts(ctx, run.Id, run.Out, answer, t);
            versions += n; if (winner != null) withWinner++;
            said.Add($"{answer.Label}: {(winner == null ? "no loaded plugin holds it" : "winner " + winner)}");
        }
        return Finish(ctx, "today-conflicts", t, $"{commands} recorded Conflicts commands, {versions} versions: the same holders in the same load order, the same places, the same winner ({withWinner} have one; {string.Join("; ", said)}).");
    }

    public static string KnownWinners(Ctx ctx)
    {
        if (!ctx.HasToday) return "SKIP today's SFSearch wasn't given (--sfsearch and --home)";
        var t = new Tally();
        var said = new List<string>();
        foreach (var key in new[] { "231B24:Starfield.esm", "2377CF:Starfield.esm" })
        {
            var (o, _, code) = ctx.Today("Conflicts", key);
            Check(code == 0, $"today's Conflicts {key} ended with exit code {code}");
            var answer = ctx.Compare.Versions(key);
            var (n, winner) = HoldConflicts(ctx, key, o, answer, t);
            Check(winner != null, $"{answer.Label} has no winner");
            ctx.WriteReport($"known-winners-{key[..6]}.txt", "TODAY\n" + o + "\nNEW\n" + answer.ToText(new TextOptions { Detail = true }));
            said.Add($"{answer.Label}: {n} versions ({answer.Loaded.Count} loaded: {string.Join(" > ", answer.Loaded.Select(r => r.Plugin))}{(answer.NotLoaded.Count > 0 ? $"; not loaded: {string.Join(", ", answer.NotLoaded.Select(r => r.Plugin))}" : "")}), winner {winner} in both");
        }
        return Finish(ctx, "known-winners", t, $"today's Conflicts run now and the new versions, in the current load order. {string.Join(". ", said)}.");
    }

    // ---------------------------------------------------------------- Check's ITM list against plugin_vs_masters
    public static string KnownItms(Ctx ctx)
    {
        if (!ctx.HasToday) return "SKIP today's SFSearch wasn't given (--sfsearch and --home)";
        const string plugin = "darkstar.esm";
        var (o, _, _) = ctx.Today("Check", plugin, "--all");
        var table = Tables(o).FirstOrDefault(x => x.Header.SequenceEqual(new[] { "Record", "Type", "EditorID", "Problem" }) && x.Rows.Count > 0 && x.Rows[0][3].StartsWith("identical to", StringComparison.Ordinal));
        Check(table.Rows != null, $"today's Check printed no ITM table:\n{o}");
        var today = table.Rows!.ToDictionary(r => KeyOf(r[0]), r => r);
        Check(today.Count == 20, $"today's Check lists {today.Count} ITMs in {plugin}; 20 were expected");

        var answer = ctx.Compare.PluginVsMasters(plugin);
        ctx.WriteReport("known-itms.txt", "TODAY\n" + o + "\nNEW\n" + answer.ToText(new TextOptions { MaxChars = 200000 }));
        var mine = answer.Itms.ToDictionary(r => r.FormKey.ToUpperInvariant());
        var all = answer.Overrides.ToDictionary(r => r.FormKey.ToUpperInvariant());
        // Today's ITMs that aren't ITMs in the new comparison. The one cause allowed: the override gives the record an
        // EditorID its master's record doesn't have. Today's comparison leaves the EditorID out. Checked in the files:
        // the master's record has no EDID subrecord, the override's has one, and nothing else differs.
        var missed = today.Keys.Except(mine.Keys).ToList();
        var editorIdOnly = new List<string>();
        var s = ctx.Session;
        foreach (var k in missed)
        {
            Check(all.TryGetValue(k, out var r) && r.Diff != null, $"{k}: an ITM today, not an override that could be compared in the new comparison");
            Check(r!.Class == OverrideClass.Changed && r.Diff!.Values.Count() == 1 && r.Diff.Values.Single() is { Path: "EditorID", Change: Change.OnlyB },
                $"{r.Label}: an ITM today; the new comparison finds {AnswerText(r.Diff!)}");
            var rawBase = s.Engine.Raw(CompareSession.Find(s.Index, r.Base!, r.FormKey)!.Value); var rawMine = s.Engine.Raw(CompareSession.Find(s.Index, plugin, r.FormKey)!.Value);
            Check(!rawBase.Subrecords.Any(x => x.Signature == "EDID") && rawMine.Subrecords.Any(x => x.Signature == "EDID"), $"{r.Label}: EDID subrecords in {r.Base}: {rawBase.Subrecords.Count(x => x.Signature == "EDID")}, in {plugin}: {rawMine.Subrecords.Count(x => x.Signature == "EDID")}");
            var others = rawBase.Subrecords.Select(x => x.Signature + " " + RecordHex(rawBase, x)).SequenceEqual(rawMine.Subrecords.Where(x => x.Signature != "EDID").Select(x => x.Signature + " " + RecordHex(rawMine, x)));
            Check(others, $"{r.Label}: the subrecords other than EDID aren't the same bytes in {r.Base} and {plugin}");
            editorIdOnly.Add($"{r.Label} ({r.Diff!.Values.Single().B})");
        }
        foreach (var (key, row) in today) Check(row[3].EndsWith(all[key].Base!, StringComparison.OrdinalIgnoreCase), $"{all[key].Label}: today \"{row[3]}\", the new base is {all[key].Base}");

        // Every ITM of the new comparison, held against the files (fix A4, 2026-11-12): the subrecords, decompressed, are
        // the same signatures in the same order with the same bytes, the EDID included, and the record flags are the
        // same but for the compressed bit. Only the record header's own numbers (form version, version control, the
        // second version number) may differ: they aren't record data.
        int headerOnly = 0;
        foreach (var r in mine.Values)
        {
            var rawBase = s.Engine.Raw(CompareSession.Find(s.Index, r.Base!, r.FormKey)!.Value); var rawMine = s.Engine.Raw(CompareSession.Find(s.Index, plugin, r.FormKey)!.Value);
            var subsBase = rawBase.Subrecords.Select(x => x.Signature + " " + RecordHex(rawBase, x)).ToList();
            var subsMine = rawMine.Subrecords.Select(x => x.Signature + " " + RecordHex(rawMine, x)).ToList();
            Check(subsBase.SequenceEqual(subsMine), $"{r.Label}: an ITM in the new comparison, but its subrecords aren't the same bytes in {r.Base} and {plugin}: " +
                string.Join(", ", subsBase.Except(subsMine).Concat(subsMine.Except(subsBase)).Select(x => x.Split(' ')[0]).Distinct()));
            Check((rawBase.Flags & ~0x40000u) == (rawMine.Flags & ~0x40000u), $"{r.Label}: an ITM in the new comparison, but the record flags differ: {rawBase.Flags:X8} and {rawMine.Flags:X8}");
            headerOnly++;
        }

        // ITMs only the new comparison finds: why didn't today's Check list them?
        // Today's Check looks only at the record types its stored database holds in full. Ask today's SFSearch which those are.
        var (stored, _, _) = ctx.Today("Query", plugin, "SELECT DISTINCT Signature FROM Records ORDER BY 1");
        var storedTypes = Tables(stored).SelectMany(x => x.Rows).Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
        Check(storedTypes.Contains("Npc") && storedTypes.Count > 20, $"today's stored record types of {plugin} couldn't be read: {stored}");
        var extra = mine.Keys.Except(today.Keys).Select(k => mine[k]).ToList();
        var notStored = extra.Where(r => !storedTypes.Contains(r.Type!)).ToList();
        var storedExtra = extra.Where(r => storedTypes.Contains(r.Type!)).ToList();
        // A stored type that today doesn't call ITM: today's Delta must flag a field the new engine sees as "not a value"
        var t = new Tally();
        if (storedExtra.Count > 0)
        {
            var (delta, _, _) = ctx.Today("Delta", plugin);
            var rows = Tables(delta).First().Rows.ToDictionary(r => KeyOf(r[0]), r => r);
            foreach (var r in storedExtra)
            {
                Check(rows.ContainsKey(r.FormKey.ToUpperInvariant()), $"{r.Label}: an ITM only in the new comparison, of a type today stores, and today's Delta doesn't list it");
                t.Hold($"{r.Label}", FieldSet(rows[r.FormKey.ToUpperInvariant()][5]), r.Diff!, Values(ctx, r.Base!, plugin, r.FormKey));
            }
        }
        var byType = notStored.GroupBy(r => r.Signature).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}").ToList();
        string head = $"{plugin}: today's Check lists 20 ITMs ({string.Join(", ", today.Values.GroupBy(r => r[1]).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))}), each against the same master as the new comparison. " +
            $"{20 - missed.Count} of the 20 are ITMs in the new comparison. " +
            (missed.Count > 0 ? $"The other {missed.Count} change one thing today's comparison leaves out: the override has an EditorID and its master's record has none ({string.Join("; ", editorIdOnly)}). Checked in the files: Starfield.esm's record has no EDID subrecord, darkstar.esm's has one, every other subrecord has the same bytes. " : "") +
            $"The new comparison looks at all {N(answer.Overrides.Count)} overrides and finds {N(mine.Count)} ITMs; each of the {N(headerOnly)} holds the same subrecords as its master's version, in the same order with the same bytes (decompressed, the EDID included), and the same record flags; only the header's own numbers differ. Of the {N(extra.Count)} that today's Check doesn't list, {N(notStored.Count)} are of record types today's stored database doesn't hold ({string.Join(", ", byType)}), so today's Check never looked at them" +
            (storedExtra.Count > 0 ? $"; {N(storedExtra.Count)} are of stored types, where today's Delta flags a field:" : ".");
        return Finish(ctx, "known-itms-causes", t, head);

        static string AnswerText(RecordDiff d) => d.DifferentTypes ? "another type" : string.Join(", ", d.Values.Take(4).Select(v => $"{v.Path}: {v.A} → {v.B}"));
        static string RecordHex(RawRecord raw, RawSubrecord sub) => RawRecord.Hex(raw.Content(sub));
    }

    // ---------------------------------------------------------------- PluginDiff against plugins
    public static string PluginDiff(Ctx ctx)
    {
        if (!ctx.HasToday) return "SKIP today's SFSearch wasn't given (--sfsearch and --home). The one recorded PluginDiff names a file that isn't there, so today's command has to be run on pairs made here";
        var t = new Tally();
        var said = new List<string>();
        // 1. Two versions of one plugin: weap_starlight.esm and the copy with 3 edited, 1 removed, 1 added
        string changed = MadeTests.ChangedCopy(ctx, "plugindiff-new");
        {
            var (o, _, _) = ctx.Today("PluginDiff", "weap_starlight.esm", changed);
            var answer = ctx.Compare.Plugins("weap_starlight.esm", changed);
            ctx.WriteReport("today-plugindiff-versions.txt", "TODAY\n" + o + "\nNEW\n" + answer.ToText(new TextOptions { Detail = true }));
            var rows = Tables(o).First(x => x.Header.Contains("ChangedFields")).Rows;
            var todayAdded = rows.Where(r => r[0] == "ADDED").Select(r => r[4].ToUpperInvariant()).ToHashSet();
            var todayRemoved = rows.Where(r => r[0] == "REMOVED").Select(r => r[3].ToUpperInvariant()).ToHashSet();
            var todayChanged = rows.Where(r => r[0].StartsWith("MODIFIED", StringComparison.Ordinal)).ToDictionary(r => r[4].ToUpperInvariant(), r => r);
            static string KeyIn(string? label) => Regex.Match(label ?? "", @"\(([0-9A-F]{6}:[^)]+)\)$").Groups[1].Value.ToUpperInvariant();
            Check(todayAdded.SetEquals(answer.Rows.Where(r => r.Class == PairClass.Added).Select(r => KeyIn(r.B))), $"added today: {string.Join(", ", todayAdded)}; new: {string.Join(", ", answer.Rows.Where(r => r.Class == PairClass.Added).Select(r => r.B))}");
            Check(todayRemoved.SetEquals(answer.Rows.Where(r => r.Class == PairClass.Removed).Select(r => KeyIn(r.A))), $"removed today: {string.Join(", ", todayRemoved)}; new: {string.Join(", ", answer.Rows.Where(r => r.Class == PairClass.Removed).Select(r => r.A))}");
            var mineChanged = answer.Rows.Where(r => r.Class == PairClass.Changed).ToDictionary(r => KeyIn(r.B));
            Check(mineChanged.Keys.All(todayChanged.ContainsKey), $"changed today: {string.Join(", ", todayChanged.Keys)}; new: {string.Join(", ", mineChanged.Keys)}");
            // Today's PluginDiff writes a link to one of the plugin's own records as that record's EditorID before it
            // compares. For a record the other file doesn't hold (the one that was removed) it can only do that on one
            // side, so every record that links to the removed record shows that field as changed. The field's value is
            // the same in both files; the test checks that from the two records' JSON.
            const string sameValue = "the two records hold the same value there; today's PluginDiff rewrites a link to one of the plugin's own records as its EditorID, and for a record the other file doesn't hold it can do that on one side only";
            int onlyToday = 0;
            var a = ctx.Session.Side("weap_starlight.esm"); var b = ctx.Session.Side(changed);
            foreach (var (key, row) in todayChanged)
            {
                var values = Values(ctx, "weap_starlight.esm", changed, key);
                if (mineChanged.TryGetValue(key, out var m)) { t.Hold($"plugindiff versions: {m.Label}", FieldSet(row[5]), m.Diff!, values, sameValue); continue; }
                // A record only today calls changed: it must have the same bytes in both files
                onlyToday++;
                var ra = CompareSession.Find(a.Index, a.Name, key)!.Value; var rb = CompareSession.Find(b.Index, b.Name, key)!.Value;
                Check(CompareSession.RawBytes(a.Engine, ra).AsSpan().SequenceEqual(CompareSession.RawBytes(b.Engine, rb)), $"{key}: changed today, unchanged in the new comparison, and its bytes differ between the two files");
                var va = ctx.Session.View(a.Engine, ra); var vb = ctx.Session.View(b.Engine, rb);
                t.Hold($"plugindiff versions: {va.Label} (the same bytes in both files)", FieldSet(row[5]), ctx.Session.Differ.Diff(va.Type, va.Json, vb.Type, vb.Json, new DiffOptions { Label = ctx.Session.Labeller(a, b) }), values, sameValue);
            }
            said.Add($"weap_starlight.esm against its changed copy: {todayAdded.Count} added and {todayRemoved.Count} removed in both, the same records; {mineChanged.Count} changed in the new comparison, {todayChanged.Count} today ({onlyToday} more today: a record with the same bytes in both files)");
        }
        // 2. A patch against the plugin it masters
        {
            var (o, _, _) = ctx.Today("PluginDiff", "atom74.esm", "darkstarpatch_atom74.esm");
            var answer = ctx.Compare.Plugins("atom74.esm", "darkstarpatch_atom74.esm");
            ctx.WriteReport("today-plugindiff-patch.txt", "TODAY\n" + o + "\nNEW\n" + answer.ToText(new TextOptions { Detail = true }));
            var rows = Tables(o).First(x => x.Header.Contains("ChangedFields")).Rows;
            var todayChanged = rows.Where(r => r[0].StartsWith("MODIFIED", StringComparison.Ordinal)).ToDictionary(r => r[4].ToUpperInvariant(), r => r);
            static string KeyIn(string? label) => Regex.Match(label ?? "", @"\(([0-9A-F]{6}:[^)]+)\)$").Groups[1].Value.ToUpperInvariant();
            var mineChanged = answer.Rows.Where(r => r.Class == PairClass.Changed).ToDictionary(r => KeyIn(r.B));
            // Today's patch mode lists the patch's own records as ADDED ("no such record in the old file"); the new one leaves them out and counts them
            int todayAdded = rows.Count(r => r[0] == "ADDED");
            Check(rows.All(r => r[0] != "REMOVED") && answer.Removed == 0 && answer.Added == 0, "a patch against its master lists removals");
            Check(todayChanged.Keys.ToHashSet().SetEquals(mineChanged.Keys) || mineChanged.Keys.All(todayChanged.ContainsKey), $"changed today: {string.Join(", ", todayChanged.Keys)}; new: {string.Join(", ", mineChanged.Keys)}");
            foreach (var (key, row) in todayChanged)
                if (mineChanged.TryGetValue(key, out var m)) t.Hold($"plugindiff patch: {m.Label}", FieldSet(row[5]), m.Diff!, Values(ctx, "atom74.esm", "darkstarpatch_atom74.esm", key));
            int notIn = int.Parse(Regex.Match(o, @"(\d+) record\(s\) the patch doesn't contain").Groups[1].Value);
            // Today counts only the records its stored database holds; the new comparison counts every record of the plugin
            var (count, _, _) = ctx.Today("Query", "atom74.esm", "SELECT count(*) AS N FROM Records");
            int storedToday = int.Parse(Tables(count).First().Rows[0][0], NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            Check(answer.NotInPatch - notIn == answer.RecordsA - storedToday, $"today leaves out {notIn} records of atom74.esm that the patch doesn't hold, the new comparison {answer.NotInPatch}; today's database stores {storedToday} of atom74.esm's {answer.RecordsA} records");
            said.Add($"atom74.esm against darkstarpatch_atom74.esm (a patch and its master): {todayChanged.Count} changed in both; records of atom74.esm that the patch doesn't hold, left out: {notIn} today (of the {storedToday} its database stores), {answer.NotInPatch} in the new comparison (of all {answer.RecordsA}); today lists the patch's {todayAdded} other records as ADDED, the new one counts {answer.NotOfPlugin} as not atom74.esm's and lists none");
            Check(todayAdded == answer.NotOfPlugin, $"today lists {todayAdded} records as added, the new comparison counts {answer.NotOfPlugin} records of the patch that aren't the plugin's");
        }
        // 3. Trap 8: a repeated EditorID. Today's command pairs a plugin's own records by EditorID and keeps one of a repeated pair.
        {
            const string from = "kgcDoom_mod_Crucible_Quality_03", to = "kgcDoom_mod_Crucible_Quality_02";
            string old = MadeTests.RenamedEditorId(ctx, "plugindiff-trap8-old", from, to), fresh = ctx.Copy("kgcdoom.esm", "plugindiff-trap8-new", "zz_trap8.esm");
            var (o, _, _) = ctx.Today("PluginDiff", old, fresh);
            var answer = ctx.Compare.Plugins(old, fresh);
            ctx.WriteReport("today-plugindiff-trap8.txt", "TODAY\n" + o + "\nNEW\n" + answer.ToText(new TextOptions { Detail = true }));
            var m = Regex.Match(o, @"\*\*Added\*\*: (\d+) \| \*\*Removed\*\*: (\d+) \| \*\*Modified\*\*: (\d+) \| \*\*FormID-Shifted Only\*\*: (\d+) \| \*\*Unchanged\*\*: (\d+)");
            Check(m.Success, $"today's PluginDiff summary line wasn't found:\n{o[..Math.Min(600, o.Length)]}");
            int tAdded = int.Parse(m.Groups[1].Value), tRemoved = int.Parse(m.Groups[2].Value), tModified = int.Parse(m.Groups[3].Value), tShifted = int.Parse(m.Groups[4].Value), tUnchanged = int.Parse(m.Groups[5].Value);
            int todayTotal = tRemoved + tModified + tShifted + tUnchanged;
            Check(answer.Unchanged + answer.Changed + answer.Removed == answer.RecordsA, $"the new comparison accounts for {answer.Unchanged + answer.Changed + answer.Removed} of {answer.RecordsA} records of the old file");
            // How many records today's database stores of the old file, and how many pairs of (type, EditorID) they make
            var (count, _, _) = ctx.Today("Query", old, "SELECT count(*) AS N, count(DISTINCT CASE WHEN IsOverride = 1 THEN 'O' || FormKey ELSE Signature || '|' || ifnull(EditorID, '') END) AS Keys FROM Records");
            var cells = Tables(count).First().Rows[0];
            int storedToday = int.Parse(cells[0], NumberStyles.AllowThousands, CultureInfo.InvariantCulture), keysToday = int.Parse(cells[1], NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            Check(todayTotal == keysToday, $"today's answer accounts for {todayTotal} records; its database has {keysToday} different pairing keys for {storedToday} records");
            said.Add($"the trap-8 pair (a copy of kgcdoom.esm with one EditorID overwritten by another, {N(answer.RecordsA)} records, of which today's database stores {N(storedToday)}): today reports {tAdded} added, {tRemoved} removed, {tModified} modified, {tShifted} shifted, {N(tUnchanged)} unchanged, which accounts for {N(todayTotal)} of the {N(storedToday)}" +
                (todayTotal == storedToday ? "" : $": {N(storedToday - todayTotal)} dropped without a word, because today's PluginDiff keeps one record for each record type and EditorID ({N(keysToday)} different ones; today's fault, trap 8)") +
                $"; the new comparison, by FormKey, reports {answer.Added} added, {answer.Removed} removed, {answer.Changed} changed, {N(answer.Unchanged)} unchanged, which accounts for all {N(answer.RecordsA)}");
        }
        return Finish(ctx, "today-plugindiff", t, $"today's PluginDiff run now on three pairs. {string.Join(". ", said)}.");
    }
}
