// leveled, held against today's Leveled on real lists (the same entries, levels, counts and chances), against
// Mutagen's own records for every list read, and on a made plugin whose two lists lead back to each other.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class LeveledTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    sealed record Case(string Name, string List, string What, string? In = null, int Depth = 8);

    static readonly Case[] Cases =
    {
        new("leveled__0032EE", "0032EE:Starfield.esm", "nested, entries with conditions"),
        new("leveled__003EAE", "003EAE:Starfield.esm", "nested, conditions, filter keywords"),
        new("leveled__034C77", "034C77:Starfield.esm", "a list with conditions of its own"),
        new("leveled__003362", "003362:Starfield.esm", "a chance of nothing"),
        new("leveled__0B28B1", "0B28B1:Starfield.esm", "a ship list (LVLB), nested"),
        new("leveled__055FC7", "055FC7:Starfield.esm", "a ship list a loaded mod overrides"),
        new("leveled__00CF97", "00CF97:Starfield.esm", "an NPC list (LVLN), nested"),
        new("leveled__026B0F", "026B0F:Starfield.esm", "a list a loaded mod overrides"),
        new("leveled__026B0F__in_Starfield.esm", "026B0F:Starfield.esm", "the same, read from Starfield.esm", In: "Starfield.esm"),
        new("leveled__02C607", "02C607:Starfield.esm", "a list three plugins hold"),
        new("leveled__0008BF_atom74", "0008BF:atom74.esm", "a mod's list"),
        new("leveled__143BDF", "143BDF:Starfield.esm", "the largest: over 2,700 lists"),
        new("leveled__depth2_0032EE", "0032EE:Starfield.esm", "cut at depth 2", Depth: 2),
    };

    sealed record Row(int Depth, string Level, string Count, double ChanceNone, string Target, string Type, List<(string Function, string Op, double Value, string? Key)> Conditions, string InList, string? Special);
    sealed record Head(int Depth, string Key, string Type, string Plugin, double ChanceNone, List<string> Flags, int MaxCount, bool ListConditions, int FilterKeywords);

    static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;

    internal static List<(string Function, string Op, double Value, string? Key)> ParseConditionsOf(string text)
    {
        var list = new List<(string, string, double, string?)>();
        if (text.Trim().Length == 0) return list;
        foreach (string part in Regex.Split(text.Trim(), @" (?:AND|OR) (?=[A-Za-z_]\w*[ (])"))
        {
            string p = Regex.Replace(part.Trim(), @" (?:AND|OR)$", "");
            var m = Regex.Match(p, @"^(\w+).* ([=≠<>≥≤]) (\S+)$");
            if (!m.Success) { list.Add((p, "?", double.NaN, null)); continue; }
            list.Add((m.Groups[1].Value, m.Groups[2].Value, Num(m.Groups[3].Value), Ctx.FirstKey(p)));
        }
        return list;
    }

    static (List<Head> Heads, List<Row> Rows, string Summary) ParseToday(string text)
    {
        var heads = new List<Head>(); var rows = new List<Row>(); string summary = "";
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            var h = Regex.Match(line, @"^(\s*)- \*\*(.+?)\*\* \((\w+), from (.+?)\): chance of nothing ([^%]*)%(.*)$");
            if (h.Success)
            {
                string rest = h.Groups[6].Value;
                var flags = Regex.Match(rest, @", flags (.*?)(?=, at most |, list conditions: |, filter keywords: |$)");
                var most = Regex.Match(rest, @", at most (\d+)");
                var kws = Regex.Match(rest, @", filter keywords: (.*)$");
                heads.Add(new Head(h.Groups[1].Value.Length / 2, Ctx.FirstKey(h.Groups[2].Value) ?? "", h.Groups[3].Value, h.Groups[4].Value, Num(h.Groups[5].Value),
                    flags.Success ? flags.Groups[1].Value.Split(", ").ToList() : new List<string>(), most.Success ? int.Parse(most.Groups[1].Value) : 0,
                    rest.Contains(", list conditions: ", StringComparison.Ordinal), kws.Success ? kws.Groups[1].Value.Split("; ").Length : 0));
                continue;
            }
            if (Regex.IsMatch(line, @"^\d+ list\(s\)")) { summary = line; continue; }
        }
        foreach (var c in Ctx.Table(text, "Level"))
        {
            if (c.Length < 7) continue;
            string entry = c[3];
            int dots = entry.TakeWhile(ch => ch == '·').Count();
            string label = entry[dots..].Trim();
            string? special = label.StartsWith("(loops back", StringComparison.Ordinal) ? "loop" : label.StartsWith("(deeper than", StringComparison.Ordinal) ? "depth" : null;
            rows.Add(new Row(dots / 2, c[0], c[1], special != null ? 0 : Num(c[2]), special != null ? "" : Ctx.FirstKey(label) ?? "", c[4], ParseConditionsOf(c[5]), Ctx.FirstKey(c[6]) ?? "", special));
        }
        return (heads, rows, summary);
    }

    static string Sign(string op) => op;

    /// <summary>The entries of a leveled list as Mutagen's own record has them.</summary>
    static List<(int Level, int Count, string Target, double Chance)>? MutagenEntries(IMajorRecordGetter r)
    {
        static string K(FormKey k) => k.IsNull ? "" : Ctx.Key($"{k.ID:X6}:{k.ModKey.FileName}");
        return r switch
        {
            ILeveledItemGetter x => (x.Entries ?? Array.Empty<ILeveledItemEntryGetter>()).Select(e => ((int)e.Level, (int)e.Count, K(e.Reference.FormKey), e.ChanceNone.Value * 100)).ToList(),
            ILeveledNpcGetter x => (x.Entries ?? Array.Empty<ILeveledNpcEntryGetter>()).Select(e => ((int)e.Level, (int)e.Count, K(e.Reference.FormKey), e.ChanceNone.Value * 100)).ToList(),
            ILeveledBaseFormGetter x => (x.Entries ?? Array.Empty<ILeveledBaseFormEntryGetter>()).Select(e => ((int)e.Level, (int)e.Count, K(e.Reference.FormKey), e.ChanceNone.Value * 100)).ToList(),
            ILeveledPackInGetter x => (x.Entries ?? Array.Empty<ILeveledPackInEntryGetter>()).Select(e => ((int)e.Level, (int)e.Count, K(e.Reference.FormKey), e.ChanceNone.Value * 100)).ToList(),
            ILeveledSpaceCellGetter x => (x.Entries ?? Array.Empty<ILeveledSpaceCellEntryGetter>()).Select(e => ((int)e.Level, (int)e.Count, K(e.Reference.FormKey), e.ChanceNone.Value * 100)).ToList(),
            _ => null,
        };
    }

    /// <summary>Every list of an answer against Mutagen's record of it in the plugin it was read from.</summary>
    static int AgainstMutagen(Ctx ctx, LeveledAnswer a, string what, SFSearch.Compare.PluginSide? side = null)
    {
        var s = ctx.Session;
        int lists = 0;
        foreach (var l in a.Lists().DistinctBy(l => l.FormKey + "|" + l.Plugin))
        {
            HeaderIndex.TryParseFormKey(l.FormKey, out var owner, out var id);
            var engine = side != null && l.Plugin.Equals(side.Name, StringComparison.OrdinalIgnoreCase) ? side.Engine : s.Engine;
            var hit = ReportSession.In(engine, l.Plugin, owner, id) ?? throw new TestFailed($"{what}: {l.Label} isn't in {l.Plugin}");
            engine.Sessions.Use(l.Plugin);
            var rec = engine.Load(hit.Ref);
            var entries = MutagenEntries(rec.Record) ?? throw new TestFailed($"{what}: {l.Label} is a {rec.Type} in Mutagen");
            Check(entries.Count == l.Entries.Count, $"{what}: {l.Label} has {l.Entries.Count} entries in the answer and {entries.Count} in Mutagen's record");
            for (int i = 0; i < entries.Count; i++)
                Check(entries[i].Level == l.Entries[i].Level && entries[i].Count == l.Entries[i].Count && entries[i].Target == Ctx.Key(l.Entries[i].Target) && Math.Abs(entries[i].Chance - l.Entries[i].ChanceNone) < 1e-4,
                    $"{what}: {l.Label} entry {i}: the answer has level {l.Entries[i].Level}, count {l.Entries[i].Count}, {l.Entries[i].Target}; Mutagen has {entries[i]}");
            lists++;
        }
        return lists;
    }

    public static string Today(Ctx ctx)
    {
        if (!ctx.HasToday) return "SKIP today's Leveled wasn't given (--today, or --sfsearch and --home)";
        var s = ctx.Session;
        var report = new LeveledReport(s);
        var lines = new List<string>(); var sb = new StringBuilder();
        long allRows = 0, allLists = 0, notStored = 0, mutagenLists = 0, ownChance = 0;
        // Today's Leveled needs its load-order index to tell which version wins; without one it reads the record's own plugin
        ctx.Today("loadorder", "LoadOrder");
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ctx.Today("stats__Starfield.esm", "Stats", "Starfield.esm") is { } stats)
            foreach (var row in Ctx.Table(stats, "Signature")) if (row.Length >= 5) stored[row[0]] = row[4];

        foreach (var c in Cases)
        {
            var args = new List<string> { "Leveled", c.List };
            if (c.In != null) { args.Add("--in"); args.Add(c.In); }
            if (c.Depth != 8) { args.Add("--depth"); args.Add(c.Depth.ToString()); }
            string? text = ctx.Today(c.Name, args.ToArray());
            if (text == null) return "SKIP today's Leveled couldn't be run";
            var (heads, rows, summary) = ParseToday(text);
            Check(heads.Count > 0 && rows.Count > 0, $"{c.Name}: today's answer has no list or no entry:\n{text[..Math.Min(400, text.Length)]}");
            var a = report.Leveled(c.List, new LeveledOptions { In = c.In, Depth = c.Depth });
            ctx.WriteReport(c.Name + ".txt", a.ToText(new ReportTextOptions { MaxChars = int.MaxValue }));

            // ---- the lists: the same ones in the same order, read from the same plugin, with the same settings
            var mine = a.Lists().ToList();
            Check(mine.Count == heads.Count, $"{c.Name}: {mine.Count} lists, today {heads.Count}");
            for (int i = 0; i < mine.Count; i++)
            {
                var l = mine[i]; var h = heads[i];
                Check(Ctx.Key(l.FormKey) == h.Key && l.Depth == h.Depth, $"{c.Name}: list {i} is {l.Label} at depth {l.Depth}; today {h.Key} at depth {h.Depth}");
                Check(l.Plugin.Equals(h.Plugin, StringComparison.OrdinalIgnoreCase), $"{c.Name}: {l.Label} read from {l.Plugin}; today from {h.Plugin}");
                Check(l.ChanceNone == h.ChanceNone, $"{c.Name}: {l.Label} chance of nothing {l.ChanceNone}; today {h.ChanceNone}");
                Check(l.Flags.OrderBy(x => x).SequenceEqual(h.Flags.Where(f => f is not ("" or "0")).OrderBy(x => x)), $"{c.Name}: {l.Label} flags {string.Join(",", l.Flags)}; today {string.Join(",", h.Flags)}");
                Check(l.MaxCount == h.MaxCount, $"{c.Name}: {l.Label} at most {l.MaxCount}; today {h.MaxCount}");
                Check((l.Conditions.Count > 0) == h.ListConditions, $"{c.Name}: {l.Label} has {l.Conditions.Count} conditions of its own; today {(h.ListConditions ? "some" : "none")}");
                Check(l.FilterKeywords.Count == h.FilterKeywords, $"{c.Name}: {l.Label} has {l.FilterKeywords.Count} filter keywords; today {h.FilterKeywords}");
            }

            // ---- the entries: the same ones in the same order with the same level, count, chance and conditions
            var expect = new List<(LeveledList List, LeveledEntry? Entry, string? Special)>();
            foreach (var (list, entry) in a.Entries())
            {
                expect.Add((list, entry, null));
                if (entry.Stopped != null) expect.Add((list, null, entry.Stopped));
            }
            Check(expect.Count == rows.Count, $"{c.Name}: {expect.Count} rows, today {rows.Count}");
            int caseNotStored = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i]; var (list, e, special) = expect[i];
                string at = $"{c.Name} row {i}";
                Check(r.Special == special, $"{at}: {(special ?? "an entry")}; today {(r.Special ?? "an entry")}");
                if (special != null) continue;
                Check(Ctx.Key(e!.Target) == r.Target, $"{at}: {e.TargetLabel}; today {r.Target}");
                Check(e.Level.ToString() == r.Level && e.Count.ToString() == r.Count, $"{at}: {e.TargetLabel} level {e.Level} count {e.Count}; today level {r.Level} count {r.Count}");
                // Today's prints an entry's chance of nothing as the fraction Mutagen gives (0.15); the file holds percent (15)
                Check(Math.Abs(e.ChanceNone - r.ChanceNone * 100) < 1e-4, $"{at}: {e.TargetLabel} chance of nothing {e.ChanceNone}%; today {r.ChanceNone} (a fraction)");
                if (e.ChanceNone != 0) ownChance++;
                Check(Ctx.Key(list.FormKey) == r.InList && list.Depth == r.Depth, $"{at}: in list {list.Label} at depth {list.Depth}; today {r.InList} at depth {r.Depth}");
                Check(e.Conditions.Count == r.Conditions.Count, $"{at}: {e.Conditions.Count} conditions ({ConditionReader.Text(e.Conditions)}); today {r.Conditions.Count}");
                for (int k = 0; k < e.Conditions.Count; k++)
                {
                    var mc = e.Conditions[k]; var tc = r.Conditions[k];
                    Check(mc.Function == tc.Function && mc.OperatorSign == tc.Op && (mc.Value ?? double.NaN).Equals(tc.Value), $"{at}: condition {k} is {mc.Text}; today {tc.Function} {tc.Op} {tc.Value}");
                    string? firstKey = mc.Parameters.FirstOrDefault()?.FormKey;
                    if (tc.Key != null || firstKey != null) Check(firstKey != null && Ctx.Key(firstKey) == tc.Key, $"{at}: condition {k} names {firstKey}; today {tc.Key}");
                }
                // The record type: today's says "(not found)" for a type its database doesn't store
                if (r.Type == "(not found)")
                {
                    Check(e.Found, $"{at}: today's says (not found) and so does the new one: pick a list whose entries exist");
                    string type = e.TargetType!;
                    Check(stored.TryGetValue(type, out var how) && how != "Yes", $"{at}: today's says (not found) for {e.TargetLabel}, a {type} that its database {(stored.TryGetValue(type, out var h2) ? $"stores ({h2})" : "isn't known to store")}");
                    caseNotStored++;
                }
                else Check(e.TargetType == r.Type, $"{at}: {e.TargetLabel} is a {e.TargetType}; today {r.Type}");
            }
            // ---- the summary line
            var sm = Regex.Match(summary, @"^(\d+) list\(s\), (\d+) entries, (\d+) of them items or forms \(not lists\), (\d+) not found");
            Check(sm.Success && int.Parse(sm.Groups[1].Value) == a.ListCount && int.Parse(sm.Groups[2].Value) == a.EntryCount, $"{c.Name}: {a.ListCount} lists and {a.EntryCount} entries; today's summary: {summary}");
            Check(int.Parse(sm.Groups[4].Value) == caseNotStored + a.NotFound, $"{c.Name}: today counts {sm.Groups[4].Value} not found; {caseNotStored} are of types its database doesn't store, {a.NotFound} really aren't there");
            mutagenLists += AgainstMutagen(ctx, a, c.Name);
            allRows += rows.Count; allLists += mine.Count; notStored += caseNotStored;
            lines.Add($"{a.Top.Label} ({c.What}{(c.In != null ? ", --in " + c.In : "")}): {N(mine.Count)} lists, {N(a.EntryCount)} entries, {N(a.Entries().Count(x => x.Entry.Conditions.Count > 0))} with conditions, read from {string.Join("/", mine.Select(l => l.Plugin).Distinct())}" +
                (caseNotStored > 0 ? $", {caseNotStored} entries today calls \"(not found)\"" : "") + (a.TooDeep > 0 ? $", {a.TooDeep} not unfolded" : ""));
            sb.AppendLine(lines[^1]);
        }

        // ---- the two list types today's database doesn't store: today's can't answer; the new one is held against Mutagen
        var cant = new List<string>();
        foreach (var (name, key) in new[] { ("leveled__054B1B", "054B1B:Starfield.esm"), ("leveled__2239D9", "2239D9:Starfield.esm") })
        {
            string? text = ctx.Today(name, "Leveled", key);
            Check(text != null && text.Contains("wasn't found in any plugin's database", StringComparison.Ordinal), $"{name}: today's Leveled was expected to say it can't find the list:\n{text}");
            var a = report.Leveled(key);
            Check(a.EntryCount > 0 && a.NotFound == 0, $"{name}: {a.EntryCount} entries, {a.NotFound} not found");
            mutagenLists += AgainstMutagen(ctx, a, name);
            cant.Add($"{a.Top.Label} {a.Top.Signature}: {a.ListCount} lists, {a.EntryCount} entries, {a.Entries().Count(x => x.Entry.Conditions.Count > 0)} with conditions");
        }

        // ---- with no load-order index, today's Leveled reads a record's own plugin and says nothing
        string fault = "";
        if (ctx.TodayFolder != null && File.Exists(Path.Combine(Path.GetDirectoryName(ctx.TodayFolder)!, "today-noindex", "leveled__026B0F.txt")))
        {
            var (heads, rows, _) = ParseToday(File.ReadAllText(Path.Combine(Path.GetDirectoryName(ctx.TodayFolder)!, "today-noindex", "leveled__026B0F.txt")));
            var winner = report.Leveled("026B0F:Starfield.esm");
            Check(heads[0].Plugin == "Starfield.esm" && !winner.Top.Plugin.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase), "the kept answer of today's Leveled made before its load-order index existed no longer shows the fault");
            fault = $" Before today's load-order index was built on the test home, today's Leveled read {winner.Top.Label} from Starfield.esm ({rows.Count} entries) with no warning; the version that wins is {winner.Top.Plugin}'s ({winner.EntryCount} entries in {winner.ListCount} lists).";
        }
        ctx.WriteReport("leveled-today.txt", sb + string.Join('\n', cant) + "\n" + fault + "\n");
        return $"{Cases.Length} runs of today's Leveled: the same {N(allLists)} lists and {N(allRows)} rows in the same order, each with the same level, count, chance of nothing, conditions, record type and plugin read from; " +
            $"{N(notStored)} entries today's calls \"(not found)\". {N(ownChance)} entries have a chance of nothing of their own: today's prints it as a fraction (0.15), the new report as the percent the file holds (15). {N(mutagenLists)} lists held against Mutagen's own records: the same entries. " +
            $"Lists: {string.Join("; ", lines)}. Two list types today's Leveled can't read (\"wasn't found in any plugin's database\"): {string.Join("; ", cant)}.{fault}";
    }

    // ---------------------------------------------------------------- A loop, made with Mutagen
    public static string Made(Ctx ctx)
    {
        var s = ctx.Session;
        const string weaponKey = "0476C4:Starfield.esm";
        Check(s.Winner(weaponKey)?.Signature == "WEAP", $"{weaponKey} isn't a weapon any more");
        var mod = Tests.Made.New("m11a_loop.esm");
        var a = mod.LeveledItems.AddNew("m11a_list_a");
        var b = mod.LeveledItems.AddNew("m11a_list_b");
        a.Entries = new();
        a.Entries.Add(new LeveledItemEntry { Level = 3, Count = 2, Reference = new FormLink<IItemGetter>(Tests.Made.Key(weaponKey)) });
        a.Entries.Add(new LeveledItemEntry { Level = 1, Count = 1, Reference = b.ToLink<IItemGetter>() });
        b.Entries = new();
        b.Entries.Add(new LeveledItemEntry { Level = 5, Count = 1, Reference = a.ToLink<IItemGetter>() });
        b.Entries.Add(new LeveledItemEntry { Level = 7, Count = 4, Reference = new FormLink<IItemGetter>(Tests.Made.Key(weaponKey)) });
        string path = Tests.Made.Write(ctx, mod, "made-loop");
        var report = new LeveledReport(s);

        var answer = report.Leveled("m11a_list_a", new LeveledOptions { In = path });
        string text = answer.ToText();
        ctx.WriteReport("leveled-made-loop.txt", text);
        Check(answer.ListCount == 2 && answer.EntryCount == 4 && answer.Loops == 1 && answer.Leaves == 2 && answer.NotFound == 0, $"the loop: {answer.ListCount} lists, {answer.EntryCount} entries, {answer.Loops} loops, {answer.Leaves} leaves\n{text}");
        var entries = answer.Entries().Select(x => x.Entry).ToList();
        Check(entries[0].Level == 3 && entries[0].Count == 2 && entries[0].TargetSignature == "WEAP" && entries[1].Nested != null && entries[2].Stopped == "loop" && entries[2].Nested == null && entries[3].Level == 7 && entries[3].Count == 4,
            $"the loop's entries aren't as written:\n{text}");
        Check(answer.Notes.Any(n => n.Contains("back to a list above", StringComparison.Ordinal)) && text.Contains("not followed", StringComparison.Ordinal), $"nothing says the loop was reported and not followed:\n{text}");
        AgainstMutagen(ctx, answer, "the made loop", s.Side(path));

        // Depth 1: the nested list is named, not unfolded
        var shallow = report.Leveled("m11a_list_a", new LeveledOptions { In = path, Depth = 1 });
        Check(shallow.ListCount == 1 && shallow.EntryCount == 2 && shallow.TooDeep == 1, $"depth 1: {shallow.ListCount} lists, {shallow.EntryCount} entries, {shallow.TooDeep} not unfolded");
        return $"two lists written by Mutagen that name each other: 2 lists, 4 entries, 1 reported as leading back to a list above it and not followed, with a note; at depth 1: 1 list, 2 entries, 1 named and not unfolded.";
    }
}
