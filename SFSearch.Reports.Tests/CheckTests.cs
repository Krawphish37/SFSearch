// check, held against today's Check on real plugins, on three made plugins with one known fault each, and against
// the header index and Mutagen for the checks today's Check doesn't make.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Reports;
using MutagenStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.Reports.Tests;

static class CheckTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The six plugins the task names, then ten more.</summary>
    public static readonly string[] Plugins =
    {
        "darkstarpatch_crimsonx.esm", "darkstar.esm", "atom74.esm", "darkstarpatch_atom74.esm", "kgcdoom.esm", "MissionsGalore.esm",
        "darkstarpatch_bountyhunteroutfitz.esm", "newcombinedflorafauna.esm", "weap_starlight.esm", "sfta04.esm", "darkstarpatch_cryomancer.esm",
        "svc3holsters.esm", "mr_atom74.esm", "economy.esm", "aa50_longwatch.esm", "apc_galaxysmartcontainers.esm", "dstgo_ui_declutter.esm",
    };

    /// <summary>Today's check by name, and the new check it is held against. Keyed by record, or by record and target for links.</summary>
    static readonly (string Today, string New, bool Link)[] Pairs =
    {
        ("Links point at records that exist", "links-exist", true),
        ("Links point at the right kind of record", "links-kind", true),
        ("Links stay inside the plugin and its masters", "links-masters", true),
        ("Overrides keep their base record's type", "override-type", false),
        ("No deleted records", "deleted", false),
        ("EditorIDs are unique in the plugin", "editorid-unique", false),
        ("New EditorIDs don't repeat a master's", "editorid-master", false),
        ("No identical-to-master overrides (ITMs)", "itm", false),
    };

    sealed class TodayCheck
    {
        public Dictionary<string, (string Result, int Found)> Table = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> Rows = new(StringComparer.Ordinal);
        public int Records, Links, BuiltIn, Navi;
    }

    static TodayCheck Parse(string text)
    {
        var t = new TodayCheck();
        if (Regex.Match(text, @"([\d,]+) records and ([\d,]+) links read") is { Success: true } m)
        { t.Records = int.Parse(m.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture); t.Links = int.Parse(m.Groups[2].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture); }
        if (Regex.Match(text, @"Note: ([\d,]+) link\(s\) to the engine's built-in") is { Success: true } b) t.BuiltIn = int.Parse(b.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        if (Regex.Match(text, @"Note: ([\d,]+) link\(s\) inside the navmesh info map") is { Success: true } n) t.Navi = int.Parse(n.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        foreach (var row in Ctx.Table(text, "Result")) if (row.Length >= 3) t.Table[row[1]] = (row[0], int.Parse(row[2], NumberStyles.AllowThousands, CultureInfo.InvariantCulture));
        string? current = null; bool link = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            var h = Regex.Match(line, @"^#### (?:ERROR|WARN): (.*) \(([\d,]+)\)$");
            if (h.Success) { current = h.Groups[1].Value; t.Rows[current] = new(); link = Pairs.Any(p => p.Today == current && p.Link); continue; }
            if (current == null) continue;
            if (line.StartsWith("- ", StringComparison.Ordinal)) { t.Rows[current].Add(line[2..]); continue; }
            if (!line.StartsWith("| ", StringComparison.Ordinal) || line.StartsWith("| Record", StringComparison.Ordinal) || line.StartsWith("| :---", StringComparison.Ordinal)) continue;
            var cells = line.Trim().Trim('|').Split(" | ").Select(c => c.Trim()).ToArray();
            string? record = Ctx.FirstKey(cells[0]);
            if (record == null) continue;
            if (!link) { t.Rows[current].Add(record); continue; }
            string? target = Ctx.FirstKey(cells[^1][(cells[^1].IndexOf('→') + 1)..]);
            t.Rows[current].Add($"{record}→{target}");
        }
        return t;
    }

    static List<string> NewKeys(CheckFinding f, bool link) =>
        f.Rows.Select(r => link ? $"{Ctx.Key(r.FormKey!)}→{Ctx.Key(r.Target!)}" : Ctx.Key(r.FormKey!)).ToList();

    /// <summary>What two lists of keys have that the other hasn't, each key counted.</summary>
    static (List<string> OnlyA, List<string> OnlyB, int Both) Split(List<string> a, List<string> b)
    {
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var k in b) count[k] = count.GetValueOrDefault(k) + 1;
        var onlyA = new List<string>(); int both = 0;
        foreach (var k in a)
        {
            if (count.TryGetValue(k, out int n) && n > 0) { count[k] = n - 1; both++; }
            else onlyA.Add(k);
        }
        var onlyB = count.Where(kv => kv.Value > 0).SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).ToList();
        return (onlyA, onlyB, both);
    }

    /// <summary>The record types today's database of a plugin stores in full, from today's own Stats ("CachedInDB").</summary>
    static Dictionary<string, string> Stored(Ctx ctx, string plugin)
    {
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        string? text = ctx.Today($"stats__{plugin}", "Stats", plugin);
        if (text == null) return stored;
        foreach (var row in Ctx.Table(text, "Signature")) if (row.Length >= 5) stored[row[0]] = row[4];
        return stored;
    }

    public static string Today(Ctx ctx)
    {
        if (!ctx.HasToday) return "SKIP today's Check wasn't given (--today, or --sfsearch and --home)";
        var s = ctx.Session;
        var report = new CheckReport(s);
        var sb = new StringBuilder();
        var lines = new List<string>();
        int plugins = 0, unexplained = 0;
        long same = 0, todayOnly = 0, newOnly = 0, extra = 0, countedTwice = 0;
        var causes = new Dictionary<string, int>(StringComparer.Ordinal);
        void Cause(string c, int n = 1) => causes[c] = causes.GetValueOrDefault(c) + n;

        foreach (string plugin in Plugins)
        {
            string? text = ctx.Today($"check__{plugin}", "Check", plugin, "--all");
            if (text == null) return "SKIP today's Check couldn't be run";
            var today = Parse(text);
            Check(today.Table.Count == 10, $"{plugin}: today's Check printed {today.Table.Count} checks; 10 were expected");
            var answer = report.Check(plugin, new CheckOptions { World = true });
            plugins++;
            sb.AppendLine($"== {plugin}: today {N(today.Records)} records, {N(today.Links)} links; new {N(answer.Records)} records ({N(answer.RecordsRead)} read), {N(answer.Links)} links");
            var side = s.Side(plugin);
            var stored = new Lazy<Dictionary<string, string>>(() => Stored(ctx, plugin));
            int pSame = 0, pToday = 0, pNew = 0;
            var pCauses = new List<string>();

            // The two that are about the plugin, not about a record
            Check((today.Table["Masters present"].Found > 0) == (answer.Finding("masters-present").Rows.Count > 0), $"{plugin}: missing masters differ");
            Check((today.Table["FormIDs fit the plugin's size"].Found > 0) == (answer.Finding("formid-size").Rows.Count > 0), $"{plugin}: FormIDs too big differ");

            foreach (var (todayName, newId, link) in Pairs)
            {
                var a = today.Rows.GetValueOrDefault(todayName) ?? new List<string>();
                Check(a.Count == today.Table[todayName].Found, $"{plugin}: today's \"{todayName}\" says {today.Table[todayName].Found} and lists {a.Count} (was it run with --all?)");
                var b = NewKeys(answer.Finding(newId), link);
                var (onlyToday, onlyNew, both) = Split(a, b);
                pSame += both;
                // Today's Check takes a record's links from Mutagen's EnumerateFormLinks, which for a record that holds
                // others (a cell, a worldspace, a quest, a topic) gives the links of every record nested in it too.
                // So a nested record's link is listed again under each record it is nested in.
                var again = new Dictionary<string, int>(StringComparer.Ordinal);
                if (link)
                    foreach (var r in answer.Finding(newId).Rows)
                        foreach (string up in Ancestors(side, r.FormKey!))
                        {
                            string k = $"{up}→{Ctx.Key(r.Target!)}";
                            again[k] = again.GetValueOrDefault(k) + 1;
                        }
                foreach (string key in onlyToday)
                {
                    string? why = null;
                    if (again.TryGetValue(key, out int n) && n > 0) { again[key] = n - 1; why = "a nested record's link, listed a second time under the record that holds it (today's Check counts those links twice)"; }
                    why ??= ExplainTodayOnly(ctx, side, newId, key, answer);
                    if (why == null) { unexplained++; sb.AppendLine($"  UNEXPLAINED only today, {todayName}: {key}"); }
                    else { pToday++; Cause(why); pCauses.Add(why); sb.AppendLine($"  only today, {todayName}: {key}: {why}"); }
                }
                foreach (string key in onlyNew)
                {
                    string? why = ExplainNewOnly(ctx, side, newId, key, stored.Value);
                    if (why == null) { unexplained++; sb.AppendLine($"  UNEXPLAINED only new, {todayName}: {key}"); }
                    else { pNew++; Cause(why); pCauses.Add(why); sb.AppendLine($"  only new, {todayName}: {key}: {why}"); }
                }
            }
            // Counts both give. Today's counts a nested record's links once more for each record it is nested in.
            // Mutagen keeps one record of a FormKey that one record type holds twice, so today's reads that many records fewer
            int dropped = answer.Finding("formkey-repeat").Rows.GroupBy(r => Ctx.Key(r.FormKey!)).Sum(g => g.Count() - g.Select(r => r.Signature).Distinct().Count());
            Check(today.Records == answer.Records - dropped, $"{plugin}: today read {today.Records} records, the header index has {answer.Records} ({dropped} of them a second record of one type with a FormKey the plugin already holds)");
            var (asToday, builtInAsToday, nestedLinks) = CountedAsToday(ctx, side);
            Check(today.Navi == answer.NavmeshInfoLinks, $"{plugin}: links inside the navmesh info map: today {today.Navi}, new {answer.NavmeshInfoLinks}");
            Check(today.BuiltIn == builtInAsToday, $"{plugin}: links to built-in forms: today {today.BuiltIn}; the new check has {answer.BuiltInLinks}, {builtInAsToday} with nested records' links counted again");
            Check(today.Links == asToday + answer.NavmeshInfoLinks, $"{plugin}: today counts {N(today.Links)} links; the new check has {N(answer.Links)} + {N(answer.NavmeshInfoLinks)} in the navmesh info map, {N(asToday + answer.NavmeshInfoLinks)} with nested records' links counted again");
            countedTwice += today.Links - answer.Links - answer.NavmeshInfoLinks;
            sb.AppendLine($"  links: today {N(today.Links)}; new {N(answer.Links)} + {N(answer.NavmeshInfoLinks)} in the navmesh info map; {N(nestedLinks)} links sit in nested records and are counted {N(today.Links - answer.Links - answer.NavmeshInfoLinks)} more times by today's");

            // The checks today's Check doesn't make: each row held against the plugin
            int pExtra = 0;
            foreach (var r in answer.Finding("override-nothing").Rows) { VerifyOfNothing(ctx, side, r); pExtra++; }
            pExtra += VerifyRepeats(ctx, side, answer.Finding("formkey-repeat"));
            pExtra += VerifyPastMasters(ctx, side, answer.Finding("formid-master"));
            foreach (string id in new[] { "masters-order", "size-flags", "readable" })
            {
                var f = answer.Finding(id);
                if (f.Rows.Count > 0) sb.AppendLine($"  {id}: {string.Join(" | ", f.Rows.Select(r => (r.Record ?? "") + " " + r.Problem))}");
                pExtra += f.Rows.Count;
            }
            same += pSame; todayOnly += pToday; newOnly += pNew; extra += pExtra;
            string causeText = pCauses.Count == 0 ? "" : " (" + string.Join("; ", pCauses.GroupBy(c => c).Select(g => $"{g.Count()} {g.Key}")) + ")";
            lines.Add($"{plugin}: {N(pSame)} the same, {pToday} only today, {pNew} only new{causeText}; {pExtra} from checks today doesn't make" +
                $" [links-exist {answer.Finding("links-exist").Rows.Count}, links-kind {answer.Finding("links-kind").Rows.Count}, type {answer.Finding("override-type").Rows.Count}, of nothing {answer.Finding("override-nothing").Rows.Count}, repeats {answer.Finding("formkey-repeat").Rows.Count}, ITM today {today.Table["No identical-to-master overrides (ITMs)"].Found} new {answer.Finding("itm").Rows.Count}]");
            ctx.WriteReport($"check-{plugin}.txt", answer.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));

            // The numbers the task names
            if (plugin == "darkstarpatch_crimsonx.esm")
            {
                Check(today.Table["Overrides keep their base record's type"].Found == 105 && today.Table["Links point at records that exist"].Found == 384, "today's Check on darkstarpatch_crimsonx.esm no longer gives 105 type changes and 384 broken links");
                Check(answer.Finding("override-type").Rows.Count == 105 && answer.Finding("links-exist").Rows.Count == 384, $"the new check gives {answer.Finding("override-type").Rows.Count} type changes and {answer.Finding("links-exist").Rows.Count} broken links; 105 and 384 were expected");
            }
            if (plugin == "darkstar.esm")
                Check(today.Table["No identical-to-master overrides (ITMs)"].Found == 20, $"today's Check on darkstar.esm lists {today.Table["No identical-to-master overrides (ITMs)"].Found} ITMs; 20 were expected");
            if (plugin == "MissionsGalore.esm")
                Check(answer.Finding("formkey-repeat").Rows.Count >= 2, "the repeated FormKey of MissionsGalore.esm wasn't found");
        }
        ctx.WriteReport("check-today.txt", string.Join('\n', lines) + "\n\n" + string.Join('\n', causes.OrderByDescending(c => c.Value).Select(c => $"{c.Value}\t{c.Key}")) + "\n\n" + sb);
        Check(unexplained == 0, $"{unexplained} differences from today's Check have no cause the test can show in the plugin (see check-today.txt): {string.Join(" | ", sb.ToString().Split('\n').Where(l => l.Contains("UNEXPLAINED")).Take(6))}");
        return $"{plugins} plugins, world data included (today's Check reads it): {N(same)} findings the same in both; {N(todayOnly)} only today's, {N(newOnly)} only the new one's, every one with its cause shown in the plugin ({string.Join("; ", causes.OrderByDescending(c => c.Value).Select(c => $"{c.Value} {c.Key}"))}); " +
            $"{N(extra)} rows from checks today doesn't make (overrides of nothing, repeated FormKeys, load order), each held against the plugin. " + string.Join(" || ", lines);
    }

    static int Repeats(CheckAnswer a) => a.Finding("formkey-repeat").Rows.Count(r => r.Problem.StartsWith("repeats", StringComparison.Ordinal));

    /// <summary>The records a record is nested in (its cell, the cell's worldspace; its topic, the topic's quest), nearest first.</summary>
    static List<string> Ancestors(PluginSide side, string formKey)
    {
        var list = new List<string>();
        var seg = side.Segment;
        HeaderIndex.TryParseFormKey(formKey, out var owner, out var id);
        int o = seg.OwnerOf(owner);
        int at = o < 0 ? -1 : seg.Find(o, id);
        for (int guard = 0; at >= 0 && guard < 6; guard++)
        {
            if (!seg.TryGetParent(seg[at], out int po, out uint pl)) break;
            list.Add(Ctx.Key($"{pl:X6}:{seg.Names[po]}"));
            at = seg.Find(po, pl);
        }
        return list;
    }

    /// <summary>The plugin's links counted the way today's Check counts them: each link once for its record and once
    /// more for every record that record is nested in. Worked out here from the records, apart from the check.</summary>
    static (long Links, long BuiltIn, long Nested) CountedAsToday(Ctx ctx, PluginSide side)
    {
        var s = ctx.Session;
        var walker = new LinkWalker(s.Schema);
        var seg = side.Segment; var p = side.Plugin;
        long links = 0, builtIn = 0, nested = 0;
        for (int i = 0; i < seg.FirstUnresolved; i++)
        {
            var e = seg[i];
            if (seg.SignatureOf(e) == "NAVI") continue;
            // Of two records of one type with one FormKey, Mutagen gives the later one
            if (i + 1 < seg.Count && seg[i + 1].Key == e.Key && seg[i + 1].Signature == e.Signature) continue;
            RecordView view;
            try { view = s.Compare.View(side.Engine, new RecordRef(p.Id, i)); } catch (CompareException) { continue; }
            int depth = 0;
            for (int at = i, guard = 0; guard < 6 && seg.TryGetParent(seg[at], out int po, out uint pl); guard++)
            {
                depth++;
                at = seg.Find(po, pl);
                if (at < 0) break;
            }
            walker.Walk(view.Type, view.Json, l =>
            {
                // Mutagen's EnumerateFormLinks leaves out an armor addon's bone data modifiers (the link-walk test), so today's never sees them
                if (view.Type == "ArmorAddon" && l.Top == "BoneDataModifiers") return;
                links += 1 + depth;
                if (depth > 0) nested++;
                if (HeaderIndex.TryParseFormKey(l.FormKey, out var owner, out var id) && owner.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase) && id < 0x800 && s.Seen(side, owner, id) == null) builtIn += 1 + depth;
            });
        }
        return (links, builtIn, nested);
    }


    // ---------------------------------------------------------------- Why the two differ
    static Hit Own(PluginSide side, string key)
    {
        HeaderIndex.TryParseFormKey(key, out var owner, out var id);
        return ReportSession.In(side.Engine, side.Name, owner, id) ?? throw new TestFailed($"{side.Name} doesn't hold {key}");
    }

    static bool SameBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceEqual(b);

    /// <summary>The subrecords of two versions of a record that aren't byte for byte the same, by signature.</summary>
    static List<string> DifferingSubrecords(RawRecord a, RawRecord b)
    {
        var differ = new List<string>();
        var sigs = a.Subrecords.Select(x => x.Signature).Concat(b.Subrecords.Select(x => x.Signature)).Distinct().ToList();
        foreach (string sig in sigs)
        {
            var sa = a.Subrecords.Where(x => x.Signature == sig).ToList(); var sb = b.Subrecords.Where(x => x.Signature == sig).ToList();
            if (sa.Count != sb.Count) { differ.Add(sig); continue; }
            for (int i = 0; i < sa.Count; i++)
                if (!SameBytes(a.Content(sa[i]), b.Content(sb[i]))) { differ.Add(sig); break; }
        }
        return differ;
    }

    static string? ExplainTodayOnly(Ctx ctx, PluginSide side, string id, string key, CheckAnswer answer)
    {
        var s = ctx.Session;
        if (id == "links-kind")
        {
            // The plugin holds the target FormKey twice, as records of different types: the link can mean either
            string target = key[(key.IndexOf('→') + 1)..];
            var seg = side.Segment;
            if (HeaderIndex.TryParseFormKey(target, out var to, out var tid) && seg.OwnerOf(to) is >= 0 and var o && seg.Find(o, tid) is >= 0 and var at)
            {
                var sigs = new List<string>();
                for (int i = at; i < seg.Count && seg[i].Key == seg[at].Key; i++) sigs.Add(seg.SignatureOf(seg[i]));
                if (sigs.Distinct().Count() > 1) return $"the plugin holds the target FormKey twice, as a {string.Join(" and a ", sigs)} (reported as a repeated FormKey); today's Check judged the link by one of them, and the other fits";
            }
        }
        if (id == "itm")
        {
            // Today says the override changes nothing; the new check says it changes something. What, and is it in the files?
            var mine = Own(side, key);
            HeaderIndex.TryParseFormKey(key, out var owner, out var localId);
            if (s.Seen(side, owner, localId, mastersOnly: true) is not { } before) return null;
            var a = s.Compare.View(before.Engine, before.Ref); var b = s.Compare.View(mine.Engine, mine.Ref);
            var diff = s.Compare.Differ.Diff(a.Type, a.Json, b.Type, b.Json, new DiffOptions { Label = s.Engine.Label });
            var fields = diff.TopFields;
            if (fields.Count == 0) return null;
            var rawA = before.Engine.Raw(before.Ref); var rawB = mine.Engine.Raw(mine.Ref);
            var differ = DifferingSubrecords(rawA, rawB);
            if (fields.SequenceEqual(new[] { "EditorID" }))
            {
                // In the files: the EDID subrecord is there on one side only, or holds another name; nothing else differs
                Check(differ.Contains("EDID"), $"{key}: the diff engine says the EditorID differs, but the EDID subrecords have the same bytes (others that differ: {string.Join(", ", differ)})");
                bool masterHas = rawA.Subrecords.Any(x => x.Signature == "EDID");
                return masterHas ? "the override gives the record another EditorID (today's Check leaves the EditorID out)" : "the override gives a record with no EditorID one (today's Check leaves the EditorID out)";
            }
            return null;
        }
        return null;
    }

    static readonly HashSet<string> TextSubrecords = new(StringComparer.Ordinal) { "FULL", "DESC", "NNAM", "ONAM", "ITXT", "RNAM", "SHRT", "DNAM", "CNAM", "TNAM", "ATTX", "NAM1", "BTXT", "UNAM", "EPF2" };

    static string? ExplainNewOnly(Ctx ctx, PluginSide side, string id, string key, Dictionary<string, string> stored)
    {
        var s = ctx.Session;
        // Of two records of one type with one FormKey, Mutagen gives the later one: today's Check never saw the first
        if (id is not ("itm" or "links-exist" or "links-kind" or "links-masters"))
        {
            var seg = side.Segment; var hit = Own(side, key);
            int i = hit.Ref.Entry;
            if (i + 1 < seg.Count && seg[i + 1].Key == seg[i].Key && seg[i + 1].Signature == seg[i].Signature)
                return "the record is the first of two records of one type with one FormKey; Mutagen gives only the later one, so today's Check never saw it";
        }
        if (id == "itm")
        {
            var mine = Own(side, key);
            var info = mine.Info;
            string type = s.Compare.View(mine.Engine, mine.Ref).Type;
            // Today's Check compares only the record types its database stores in full
            if (stored.TryGetValue(type, out var how) && how != "Yes") return $"today's database doesn't store {info.Signature} records in full, so today's Check never compares them";
            if (!stored.ContainsKey(type)) return null;
            HeaderIndex.TryParseFormKey(key, out var owner, out var localId);
            if (s.Seen(side, owner, localId, mastersOnly: true) is not { } before) return null;
            // Today's Check needs the base version in its database of the master too
            var masterStored = Stored(ctx, before.Plugin.Name);
            if (masterStored.TryGetValue(type, out var mhow) && mhow != "Yes") return $"today's database of {before.Plugin.Name} doesn't store {info.Signature} records in full, so today's Check has no base version to compare with";
            var rawA = before.Engine.Raw(before.Ref); var rawB = mine.Engine.Raw(mine.Ref);
            var differ = DifferingSubrecords(rawA, rawB);
            // The same record, byte for byte in every subrecord: today's Check can only differ by what it stores
            if (differ.Count == 0) return "every subrecord has the same bytes in both files; today's stored JSON of the two differs all the same";
            // A text with no content, written as string ID 0 in a localized master and as an empty text in the override
            bool emptyTexts = differ.All(sig => TextSubrecords.Contains(sig) && Empty(rawA, sig) && Empty(rawB, sig));
            if (emptyTexts) return $"an empty text written two ways ({string.Join(", ", differ)}: string ID 0 in the master, an empty text in the override); today's Check compares the JSON form";
            // The same values, with something written another way (a text held another way, a data block length)
            var a = s.Compare.View(before.Engine, before.Ref); var b = s.Compare.View(mine.Engine, mine.Ref);
            var diff = s.Compare.Differ.Diff(a.Type, a.Json, b.Type, b.Json, new DiffOptions { Label = s.Engine.Label });
            if (diff.Same && diff.NotValues.Any())
            {
                var classes = diff.NotValues.Select(d => d.Class).Distinct().ToList();
                return "only " + string.Join(" and ", classes.Select(c => c switch
                {
                    DiffClass.WrittenAnotherWay => "a text every language shows the same is held another way (localized strings in the master, the text itself in the override)",
                    DiffClass.Layout => "the length of a data block differs (the same values)",
                    _ => "the record header's own numbers differ",
                })) + $"; subrecords with other bytes: {string.Join(", ", differ)}";
            }
            return null;
        }
        return null;

        static bool Empty(RawRecord raw, string sig) =>
            raw.Subrecords.Where(x => x.Signature == sig).All(x => { var c = raw.Content(x); return c.Length <= 4 && !c.ToArray().Any(v => v != 0); });
    }

    /// <summary>An override of nothing, held against Mutagen: the master's group of that record type holds no record with the FormKey.</summary>
    static void VerifyOfNothing(Ctx ctx, PluginSide side, CheckRow row)
    {
        var s = ctx.Session;
        HeaderIndex.TryParseFormKey(row.FormKey!, out var owner, out var id);
        var engine = s.Compare.EngineFor(side, owner);
        engine.Sessions.Use(owner);
        var types = s.Schema.TypesOf(row.Signature!);
        var key = new FormKey(ModKey.FromFileName(owner), id);
        foreach (string type in types)
        {
            var group = engine.Sessions.Get(owner).TopLevelGroup(type);
            if (group == null) continue;
            Check(group.RecordCache.TryGetValue(key) == null, $"{row.Record}: reported as an override of nothing, but Mutagen finds it in {owner}'s {type} group");
        }
    }

    /// <summary>FormIDs past the plugin's masters, held against the files: the record's own bytes, and each master's own header for its size.</summary>
    static int VerifyPastMasters(Ctx ctx, PluginSide side, CheckFinding f)
    {
        if (f.Rows.Count == 0) return 0;
        var p = side.Plugin; var seg = side.Segment;
        int full = 0;
        foreach (string m in SFSearch.Index.PluginHeader.Read(side.ReadPath)!.Masters)
        {
            var mp = ctx.Session.Compare.EngineFor(side, m).Index.Plugin(m) ?? throw new TestFailed($"{m} isn't there");
            if (SFSearch.Index.PluginHeader.Read(mp.Path)!.Style == SFSearch.Index.MasterStyle.Full) full++;
        }
        foreach (var r in f.Rows)
        {
            HeaderIndex.TryParseFormKey(r.FormKey!, out var owner, out var id);
            int o = seg.OwnerOf(owner);
            bool found = false;
            for (int i = seg.Find(o, id); i >= 0 && i < seg.Count && seg[i].Owner == o && seg[i].LocalId == id; i++)
            {
                uint raw = side.Engine.Raw(new RecordRef(p.Id, i)).FormId;
                if (r.Problem.Contains(raw.ToString("X8"), StringComparison.Ordinal) && raw >> 24 is not (0xFE or 0xFD) && raw >> 24 > full) found = true;
            }
            Check(found, $"{r.Record}: reported as written past the plugin's {full} Full masters, but the file doesn't hold such a FormID for it ({r.Problem})");
        }
        return f.Rows.Count;
    }

    /// <summary>Repeated FormKeys, held against the file: the FormIDs written at the two places, read with the
    /// records layer's own FormID reader, name the same record.</summary>
    static int VerifyRepeats(Ctx ctx, PluginSide side, CheckFinding f)
    {
        var p = side.Plugin; var seg = side.Segment;
        foreach (var group in f.Rows.GroupBy(r => Ctx.Key(r.FormKey!)))
        {
            HeaderIndex.TryParseFormKey(group.Key, out var owner, out var id);
            int o = seg.OwnerOf(owner);
            int at = seg.Find(o, id);
            var ids = new List<uint>();
            for (int i = at; i < seg.Count && seg[i].Owner == o && seg[i].LocalId == id; i++) ids.Add(side.Engine.Raw(new RecordRef(p.Id, i)).FormId);
            var reader = side.Engine.Sessions.Get(side.Name).FormIds;
            var keys = ids.Select(x => Ctx.Key(reader.FormKey(x))).Distinct().ToList();
            Check(ids.Count == group.Count() && ids.Count >= 2 && keys.Count == 1 && keys[0] == group.Key, $"{group.Key}: {group.Count()} rows, {ids.Count} records in the file with FormIDs {string.Join(", ", ids.Select(x => x.ToString("X8")))}, which read as {string.Join(", ", keys)}");
            // When the written FormIDs differ, the check says why they read as one record
            if (ids.Distinct().Count() > 1) Check(f.Note != null && f.Note.Contains("past the plugin's masters", StringComparison.Ordinal) && group.All(r => r.Problem.Contains("the file writes its FormID as", StringComparison.Ordinal)), $"{group.Key}: the FormIDs written differ ({string.Join(", ", ids.Select(x => x.ToString("X8")))}) and the check doesn't say why they read as one record");
        }
        return f.Rows.Count;
    }

    // ---------------------------------------------------------------- Made plugins: one known fault each
    public static string Made(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new CheckReport(s);
        var parts = new List<string>();

        string Others(CheckAnswer a, params string[] but) => string.Join(", ", a.Findings.Where(f => !but.Contains(f.Id) && f.Result != "PASS").Select(f => $"{f.Id} {f.Result} {f.Rows.Count}"));

        // 1. A link to a record that doesn't exist
        {
            const string missing = "FFFF00:Starfield.esm", real = "0476C4:Starfield.esm";
            Check(s.Index.Versions(missing) == null && s.Index.Versions(real) != null, "the FormKeys picked for the test aren't what they were");
            var mod = Tests.Made.New("m11a_missing_link.esm");
            var keyword = mod.Keywords.AddNew("m11a_keyword");
            var list = mod.FormLists.AddNew("m11a_list");
            list.Items.Add(keyword.ToLink<IStarfieldMajorRecordGetter>());
            list.Items.Add(new FormLink<IStarfieldMajorRecordGetter>(Tests.Made.Key(real)));
            list.Items.Add(new FormLink<IStarfieldMajorRecordGetter>(Tests.Made.Key(missing)));
            string path = Tests.Made.Write(ctx, mod, "made-missing-link");
            var a = report.Check(path);
            var f = a.Finding("links-exist");
            Check(f.Rows.Count == 1 && Ctx.Key(f.Rows[0].Target!) == Ctx.Key(missing) && f.Rows[0].EditorId == "m11a_list" && f.Rows[0].Field == "Items[2]",
                $"a link to a record that doesn't exist: {f.Rows.Count} rows ({string.Join("; ", f.Rows.Select(r => r.Line()))})");
            Check(Others(a, "links-exist").Length == 0, $"a link to a record that doesn't exist: other checks found something: {Others(a, "links-exist")}");
            Check(a.Records == 2 && a.RecordsRead == 2 && a.Links == 3 && a.Failed, $"{a.Records} records, {a.RecordsRead} read, {a.Links} links");
            ctx.WriteReport("check-made-missing-link.txt", a.ToText());
            parts.Add($"a link to a record that doesn't exist (a form list of 3 links, one to {missing}): 1 row, {f.Rows[0].Record} {f.Rows[0].Field}, and nothing else");
        }
        // 2. A link to a record of the wrong kind
        {
            const string weaponKey = "0476C4:Starfield.esm";
            var target = s.Winner(weaponKey) ?? throw new TestFailed($"{weaponKey} isn't there");
            Check(target.Signature == "WEAP", $"{weaponKey} is a {target.Signature}; a weapon was expected");
            var mod = Tests.Made.New("m11a_wrong_kind.esm");
            var keyword = mod.Keywords.AddNew("m11a_keyword");
            var weapon = mod.Weapons.AddNew("m11a_weapon");
            weapon.Keywords = new();
            weapon.Keywords.Add(keyword.ToLink<IKeywordGetter>());
            weapon.Keywords.Add(new FormLink<IKeywordGetter>(Tests.Made.Key(weaponKey)));   // a weapon where a keyword belongs
            string path = Tests.Made.Write(ctx, mod, "made-wrong-kind");
            var a = report.Check(path);
            var f = a.Finding("links-kind");
            Check(f.Rows.Count == 1 && Ctx.Key(f.Rows[0].Target!) == Ctx.Key(weaponKey) && f.Rows[0].EditorId == "m11a_weapon" && f.Rows[0].Field == "Keywords[1]" && f.Rows[0].Problem.Contains("WEAP") && f.Rows[0].Problem.Contains("Keyword"),
                $"a link to a record of the wrong kind: {f.Rows.Count} rows ({string.Join("; ", f.Rows.Select(r => r.Line()))})");
            Check(Others(a, "links-kind").Length == 0, $"a link to a record of the wrong kind: other checks found something: {Others(a, "links-kind")}");
            ctx.WriteReport("check-made-wrong-kind.txt", a.ToText());
            parts.Add($"a link to a record of the wrong kind (a weapon's keyword list naming the weapon {s.Engine.Label(weaponKey)}): 1 row, {f.Rows[0].Field}: \"{f.Rows[0].Problem}\", and nothing else");
        }
        // 3. A FormID too big for a Small plugin
        {
            const uint big = 0x1234;
            StarfieldMod Small()
            {
                var small = Tests.Made.New("m11a_big_formid.esm");
                small.IsSmallMaster = true;
                small.Keywords.Add(new Keyword(new FormKey(small.ModKey, big), StarfieldRelease.Starfield) { EditorID = "m11a_big" });
                small.Keywords.Add(new Keyword(new FormKey(small.ModKey, 0x801), StarfieldRelease.Starfield) { EditorID = "m11a_fits" });
                return small;
            }
            // Mutagen's own check of the IDs against the plugin's size, when it isn't switched off
            string guarded;
            try { Tests.Made.Write(ctx, Small(), "made-big-formid-guarded", MutagenStyle.Small, checkCompaction: true); guarded = "writes it all the same"; }
            catch (Exception e) { guarded = $"refuses ({e.GetType().Name}: {e.Message.Split('\n')[0].Trim()})"; }
            // With that check switched off, Mutagen writes the plugin: the Small flag in the header, the ID 0x1234 in the record
            string path = Tests.Made.Write(ctx, Small(), "made-big-formid", MutagenStyle.Small);
            Check((Tests.Made.HeaderFlags(path) & 0x100) != 0, "Mutagen wrote the made plugin without the Small flag");
            var a = report.Check(path);
            var f = a.Finding("formid-size");
            Check(a.Size == "Small", $"the made plugin reads as {a.Size}");
            Check(f.Rows.Count == 1 && f.Rows[0].EditorId == "m11a_big" && f.Rows[0].Problem.Contains("0x1234") && f.Rows[0].Problem.Contains("0xFFF"),
                $"a FormID too big for a Small plugin: {f.Rows.Count} rows ({string.Join("; ", f.Rows.Select(r => r.Line()))})");
            // The Small form holds 3 hex digits: Mutagen writes 0x1234 as the full index (00001234: the plugin has no master)
            // and 0x801 in the Small
            // form (FE000801), so the file mixes the two forms (GAPS S39): the header's own form wins the tie
            var forms = a.Finding("formid-forms");
            Check(forms.Rows.Count == 1 && forms.Rows[0].EditorId == "m11a_big" && forms.Rows[0].Problem.StartsWith($"its header stores {big:X8} (the full index)", StringComparison.Ordinal),
                $"a FormID too big: the forms rule gives {string.Join("; ", forms.Rows.Select(r => r.Line()))}");
            Check(Others(a, "formid-size", "formid-forms").Length == 0, $"a FormID too big: other checks found something: {Others(a, "formid-size", "formid-forms")}");
            ctx.WriteReport("check-made-big-formid.txt", a.ToText());
            parts.Add($"a FormID too big for a Small plugin (0x{big:X}, limit 0xFFF; a second record, 0x801, fits): 1 row, {f.Rows[0].Record}, and nothing else. Mutagen with its own check of the IDs left on {guarded}; with it switched off (FormIDCompaction = NoCheck) it writes the plugin, and that file was checked");
        }
        // Two more, beyond the three the task names: a master that isn't there, and a header with both size flags
        var more = new List<string>();
        {
            const string absent = "m11a_absent_master.esm";
            Check(s.Index.Plugin(absent) == null, $"{absent} exists");
            var mod = Tests.Made.New("m11a_no_master.esm");
            var list = mod.FormLists.AddNew("m11a_list");
            list.Items.Add(new FormLink<IStarfieldMajorRecordGetter>(Tests.Made.Key("0476C4:Starfield.esm")));
            list.Items.Add(new FormLink<IStarfieldMajorRecordGetter>(new FormKey(ModKey.FromFileName(absent), 0x800)));
            string path = Tests.Made.Write(ctx, mod, "made-no-master", extraMaster: absent);
            var a = report.Check(path);
            string text = a.ToText();
            ctx.WriteReport("check-made-no-master.txt", text);
            var f = a.Finding("masters-present");
            Check(f.Result == "ERROR" && f.Rows.Count == 1 && f.Rows[0].Problem.Contains(absent), $"a master that isn't there: {f.Result}, {string.Join("; ", f.Rows.Select(r => r.Problem))}");
            foreach (string id in new[] { "links-exist", "links-kind", "links-masters", "itm" })
                Check(a.Finding(id).Result == "NOT RUN" && a.Finding(id).NotRun!.Contains(absent), $"a master that isn't there: {id} is {a.Finding(id).Result}");
            Check(a.Failed && a.Notes.Any(n => n.Contains("Put the master back", StringComparison.Ordinal)) && text.Contains("| NOT RUN | Links point at records that exist |", StringComparison.Ordinal), $"a master that isn't there:\n{text}");
            more.Add($"a master that isn't there ({absent}): ERROR for the master, NOT RUN for the 3 link checks and the ITM check, each with the reason, and a note");
        }
        {
            var mod = Tests.Made.New("m11a_both_flags.esm");
            mod.IsSmallMaster = true;
            mod.Keywords.AddNew("m11a_keyword");
            string path = Tests.Made.Write(ctx, mod, "made-both-flags", MutagenStyle.Small);
            Tests.Made.SetHeaderFlags(ctx, path, Tests.Made.HeaderFlags(path) | 0x400);   // Mutagen writes no such header: one bit set in the written file
            var a = report.Check(path);
            string text = a.ToText();
            ctx.WriteReport("check-made-both-flags.txt", text);
            var f = a.Finding("size-flags");
            Check(f.Result == "ERROR" && f.Rows.Count == 1, $"both size flags: {f.Result}, {f.Rows.Count} rows\n{text}");
            string rest = string.Join(", ", a.Findings.Where(x => x.Id != "size-flags" && x.Result is not ("PASS" or "NOT RUN")).Select(x => $"{x.Id} {x.Result}"));
            Check(rest.Length == 0, $"both size flags: other checks found something: {rest}");
            int notRun = a.Findings.Count(x => x.Result == "NOT RUN");
            more.Add($"a header with both size flags (set in a file Mutagen wrote as Small): ERROR for the header, and nothing else" + (notRun > 0 ? $"; Mutagen refuses such a file, so the {notRun} checks that read records are NOT RUN, with the reason" : "; its records were read and checked"));
        }
        {
            // A FormID past the plugin's masters. Mutagen writes no such FormID: one byte is changed in a file it wrote.
            var mod = Tests.Made.New("m11a_past_master.esm");
            var first = mod.Keywords.AddNew("m11a_keyword_a");
            var second = mod.Keywords.AddNew("m11a_keyword_b");
            var list = mod.FormLists.AddNew("m11a_list");
            list.Items.Add(new FormLink<IStarfieldMajorRecordGetter>(Tests.Made.Key("0476C4:Starfield.esm")));   // makes Starfield.esm a master
            string path = Tests.Made.Write(ctx, mod, "made-past-master");
            // The second keyword's FormID is 01xxxxxx (one Full master, so 01 is the plugin itself): its top byte becomes 02
            var bytes = File.ReadAllBytes(path);
            uint want = 0x01000000u | second.FormKey.ID;
            int at = -1;
            for (int i = 0; i + 16 <= bytes.Length; i++)
                if (bytes[i] == 'K' && bytes[i + 1] == 'Y' && bytes[i + 2] == 'W' && bytes[i + 3] == 'D' && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 12)) == want) { at = i + 15; break; }
            Check(at > 0, $"the made plugin has no keyword written with the FormID {want:X8}");
            ctx.MustBeInWork(path);
            bytes[at] = 0x02;
            File.WriteAllBytes(path, bytes);
            var a = report.Check(path);
            string text = a.ToText();
            ctx.WriteReport("check-made-past-master.txt", text);
            var f = a.Finding("formid-master");
            Check(f.Result == "ERROR" && f.Rows.Count == 1 && f.Rows[0].EditorId == "m11a_keyword_b" && f.Rows[0].Problem.Contains($"{0x02000000u | second.FormKey.ID:X8}"), $"a FormID past the masters: {f.Result}, {string.Join("; ", f.Rows.Select(r => r.Line()))}\n{text}");
            string rest = string.Join(", ", a.Findings.Where(x => x.Id != "formid-master" && x.Result != "PASS").Select(x => $"{x.Id} {x.Result} {x.Rows.Count}"));
            Check(rest.Length == 0, $"a FormID past the masters: other checks found something: {rest}");
            more.Add($"a FormID past the plugin's masters ({0x02000000u | second.FormKey.ID:X8} in a plugin with one Full master; one byte changed in a file Mutagen wrote): ERROR, 1 row, {f.Rows[0].Record}, and nothing else");
        }
        {
            // An override that changes only a localized text's languages is not an ITM (fix A2/A4, 2026-11-12). Copied
            // unchanged by Mutagen into a plugin that isn't localized, MissionCargoType01 (0642A4:Starfield.esm) keeps its
            // English name and loses the other 8 languages' texts: German players see the English name. The control, a
            // material swap with no text copied the same way, is an ITM.
            const string misc = "0642A4:Starfield.esm", swap = "03FD14:Starfield.esm";
            const string Game = Tests.Made.Game;
            s.Engine.Sessions.Use(Game);
            var miscRec = (IMiscItemGetter)s.Engine.Load(Game, misc).Record;
            var swapRec = (ILayeredMaterialSwapGetter)s.Engine.Load(Game, swap).Record;
            var mod = Tests.Made.New("zz_fix_languages_only.esm");
            mod.MiscItems.GetOrAddAsOverride(miscRec);
            mod.LayeredMaterialSwaps.GetOrAddAsOverride(swapRec);
            string path = Tests.Made.Write(ctx, mod, "made-languages-only");
            var a = report.Check(path);
            string text = a.ToText();
            ctx.WriteReport("check-made-languages-only.txt", text);
            var f = a.Finding("itm");
            Check(f.Rows.Count == 1 && Ctx.Key(f.Rows[0].FormKey!) == Ctx.Key(swap), $"an override that changes only a text's languages: the ITM rows are {string.Join("; ", f.Rows.Select(r => r.Line()))}; only the material swap {swap} was expected\n{text}");
            // The diff engine says what changed: the name, with the languages
            var side = s.Side(path);
            HeaderIndex.TryParseFormKey(misc, out var owner, out var id);
            var mine = ReportSession.In(side.Engine, side.Name, owner, id) ?? throw new TestFailed($"{misc} isn't in the made plugin");
            var theirs = ReportSession.In(s.Engine, Game, owner, id) ?? throw new TestFailed($"{misc} isn't in {Game}");
            var va = s.Compare.View(theirs.Engine, theirs.Ref); var vb = s.Compare.View(mine.Engine, mine.Ref);
            var diff = s.Compare.Differ.Diff(va.Type, va.Json, vb.Type, vb.Json, new DiffOptions { Label = s.Engine.Label });
            Check(!diff.Same && diff.TopFields.SequenceEqual(new[] { "Name" }) && diff.Values.Single().Note is { } note && note.Contains("German", StringComparison.Ordinal),
                $"the copied MissionCargoType01's differences: {string.Join("; ", diff.All.Select(d => $"{d.Class} {d.Path} {d.A} → {d.B} [{d.Note}]"))}");
            more.Add($"an override that changes only a localized text's languages (MissionCargoType01 ({misc}) copied unchanged by Mutagen into a plugin that isn't localized: its English name kept, the 8 other languages' texts gone): not an ITM, a change of Name ({diff.Values.Single().Note}); the control, a material swap copied the same way, is the 1 ITM");
        }
        return "3 made plugins, each with exactly its one fault found: " + string.Join("; ", parts) + ". Four more: " + string.Join("; ", more) + ".";
    }

    // ---------------------------------------------------------------- From the header index
    public static string FromIndex(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new CheckReport(s);
        var parts = new List<string>();
        foreach (string plugin in new[] { "darkstarpatch_crimsonx.esm", "darkstarpatch_bountyhunteroutfitz.esm", "MissionsGalore.esm", "darkstar.esm" })
        {
            var p = s.Index.Plugin(plugin) ?? throw new TestFailed($"{plugin} isn't in the Data folder");
            var a = report.Check(plugin);
            // The rows are the header index's own findings, one row each
            Check(a.Finding("override-type").Rows.Count == p.Problems.TypeMismatches.Count, $"{plugin}: {a.Finding("override-type").Rows.Count} type rows, the index has {p.Problems.TypeMismatches.Count}");
            Check(a.Finding("override-nothing").Rows.Count == p.Problems.OverridesOfNothing.Count, $"{plugin}: {a.Finding("override-nothing").Rows.Count} rows of overrides of nothing, the index has {p.Problems.OverridesOfNothing.Count}");
            Check(a.Finding("formkey-repeat").Rows.Count(r => r.Problem.StartsWith("repeats", StringComparison.Ordinal)) == p.Problems.Duplicates, $"{plugin}: repeated FormKeys: {a.Finding("formkey-repeat").Rows.Count} rows, the index counts {p.Problems.Duplicates}");
            // Each type row names the base's type, and it is another one
            foreach (var r in a.Finding("override-type").Rows)
            {
                var m = Regex.Match(r.Problem, @"^is a (\w+) in (\S+)$");
                Check(m.Success && m.Groups[1].Value != r.Signature, $"{plugin}: {r.Record} {r.Signature}: \"{r.Problem}\"");
            }
            parts.Add($"{plugin}: {a.Finding("override-type").Rows.Count} overrides of another type, {a.Finding("override-nothing").Rows.Count} of nothing, {p.Problems.Duplicates} repeated FormKey(s), {a.Finding("deleted").Rows.Count} deleted, {a.Finding("editorid-unique").Rows.Count} EditorIDs used twice, {a.Finding("editorid-master").Rows.Count} that repeat a master's");
        }
        var bh = report.Check("darkstarpatch_bountyhunteroutfitz.esm");
        Check(bh.Finding("override-type").Rows.Count == 92 && bh.Finding("override-nothing").Rows.Count == 55, $"darkstarpatch_bountyhunteroutfitz.esm: {bh.Finding("override-type").Rows.Count} and {bh.Finding("override-nothing").Rows.Count}; 92 and 55 were expected (M7's note)");
        return string.Join("; ", parts) + ".";
    }

    // ---------------------------------------------------------------- World data left out, and included
    /// <summary>
    /// darkstar.esm's 20 ITMs (FORMID-TODO item 6, 2026-11-13) hold their master's data under other record header
    /// numbers (form version 582 vs 581, version control zeroed). check2 keeps the 20 and names each one's header
    /// difference, as the raw reader reads both headers; the note explains it once.
    /// </summary>
    public static string ItmHeaders(Ctx ctx)
    {
        var s = ctx.Session;
        var answer = new CheckReport(s).Check("darkstar.esm");
        var itm = answer.Finding("itm");
        Check(itm.Rows.Count == 20, $"darkstar.esm: {itm.Rows.Count} ITMs; 20 were expected");
        foreach (var r in itm.Rows)
        {
            HeaderIndex.TryParseFormKey(r.FormKey!, out var owner, out var id);
            var mine = ReportSession.In(s.Engine, "darkstar.esm", owner, id)!.Value;
            var master = ReportSession.In(s.Engine, owner, owner, id)!.Value;
            var a = mine.Engine.Raw(mine.Ref); var b = master.Engine.Raw(master.Ref);
            string? expected = HeaderNumbers.Differences(new HeaderNumbers(a.VersionControl, a.FormVersion, a.Version2), new HeaderNumbers(b.VersionControl, b.FormVersion, b.Version2));
            Check(expected != null && r.Problem == $"identical to {owner} in data; header differs: {expected}", $"{r.Record}: the row says \"{r.Problem}\"; the raw headers give \"{expected}\"");
        }
        Check(itm.Note != null && itm.Note.Split(HeaderNumbers.ItmExplanation).Length == 2, $"darkstar.esm: the ITM note doesn't explain the header difference once: {itm.Note}");
        return $"darkstar.esm: {itm.Rows.Count} ITMs, each row names its header difference as the raw reader reads both headers ({itm.Rows.Count(r => r.Problem.Contains("form version 582 vs 581", StringComparison.Ordinal))} with form version 582 vs 581); the explanation once in the note";
    }

    public static string World(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new CheckReport(s);
        var parts = new List<string>();
        foreach (string plugin in new[] { "kgcdoom.esm", "apc_galaxysmartcontainers.esm", "sfta04.esm" })
        {
            var plain = report.Check(plugin);
            var world = report.Check(plugin, new CheckOptions { World = true });
            int left = plain.WorldLeftOut.Values.Sum();
            Check(left > 0, $"{plugin} has no world data: pick another plugin for this test");
            Check(plain.RecordsRead + left == world.RecordsRead + world.Finding("readable").Rows.Count(r => !r.Problem.StartsWith("its version", StringComparison.Ordinal)) - plain.Finding("readable").Rows.Count(r => !r.Problem.StartsWith("its version", StringComparison.Ordinal)) || plain.RecordsRead + left == world.RecordsRead,
                $"{plugin}: {plain.RecordsRead} read + {left} left out, and {world.RecordsRead} read with world data");
            Check(world.WorldLeftOut.Count == 0, $"{plugin}: world data was asked for and {world.WorldLeftOut.Values.Sum()} records were still left out");
            string text = plain.ToText();
            Check(plain.Notes.Any(n => n.StartsWith("World data was left out", StringComparison.Ordinal) && n.Contains("Add world: true (command line: --world) to include it", StringComparison.Ordinal) && n.Contains(N(left))), $"{plugin}: no line says that world data was left out and what including it costs:\n{text}");
            Check(!world.Notes.Any(n => n.StartsWith("World data was left out", StringComparison.Ordinal)), $"{plugin}: the world answer still says world data was left out");
            // The checks made from record headers cover world data either way
            foreach (string id in new[] { "deleted", "editorid-unique", "editorid-master", "override-type", "override-nothing", "formkey-repeat", "formid-size" })
                Check(plain.Finding(id).Rows.Count == world.Finding(id).Rows.Count, $"{plugin}: {id} gives {plain.Finding(id).Rows.Count} rows without world data and {world.Finding(id).Rows.Count} with");
            // Whatever the plain check finds, the world check finds too
            foreach (string id in new[] { "links-exist", "links-kind", "links-masters", "itm" })
            {
                var w = world.Finding(id).Rows.Select(r => r.FormKey + "→" + r.Target).ToHashSet();
                Check(plain.Finding(id).Rows.All(r => w.Contains(r.FormKey + "→" + r.Target)), $"{plugin}: {id} finds something without world data that it doesn't find with it");
            }
            parts.Add($"{plugin}: {N(plain.Records)} records; by default {N(plain.RecordsRead)} read and {N(left)} world records left out ({string.Join(", ", plain.WorldLeftOut.OrderByDescending(k => k.Value).Take(4).Select(k => $"{k.Key} {N(k.Value)}"))}), {N(plain.Links)} links; with world {N(world.RecordsRead)} read, {N(world.Links)} links, " +
                $"{world.Finding("links-exist").Rows.Count - plain.Finding("links-exist").Rows.Count} more broken links, {world.Finding("itm").Rows.Count - plain.Finding("itm").Rows.Count} more ITMs, {world.Finding("deleted").Rows.Count} deleted records either way");
        }
        return string.Join("; ", parts) + ". Each default answer says how many records were left out and what including them costs.";
    }

    /// <summary>
    /// The stored-text rule (GAPS S19): economy.esm's Samson’s Art and apc_galaxysmartcontainers.esm's em dashes are
    /// found, each with how Mutagen reads it and how UTF-8 reads it; plugins whose stored text is plain ASCII pass.
    /// GAPS S32: stlazarusawakened.esm's quest log entries are rows; economy.esm still has Samson’s Art alone.
    /// Every text the find search finds outside ASCII in those two plugins is a row of the rule.
    /// </summary>
    public static string StoredText(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new CheckReport(s);
        var parts = new List<string>();
        var nonAscii = new Regex(@"[^\x00-\x7F]", RegexOptions.CultureInvariant);
        foreach (var plugin in new[] { "economy.esm", "apc_galaxysmartcontainers.esm", "stlazarusawakened.esm" })
        {
            var answer = report.Check(plugin);
            var f = answer.Finding("stored-text");
            Check(f.Result == "WARN" && f.Rows.Count > 0, $"{plugin}: stored-text gives {f.Result} with {f.Rows.Count} rows");
            // The same texts the search finds (translated fields; the rule also reads fields that are never translated)
            var side = s.Engine.Index.Plugin(plugin)!;
            var mask = Enumerable.Repeat(true, side.Segment!.Signatures.Count).ToArray();
            using var records = RecordTexts.Reader(side, Mutagen.Bethesda.Strings.Language.English, ctx.Data);
            var hits = RecordTexts.Search(side, mask, s.Schema, nonAscii, Mutagen.Bethesda.Strings.Language.English, ctx.Data, RecordTexts.From.Stored, records.Read).Hits;
            foreach (var h in hits)
            {
                string fk = s.Engine.Index.Describe(new RecordRef(side.Id, h.Entry)).FormKey;
                Check(f.Rows.Any(r => r.FormKey == fk && r.Field == h.Field && r.Problem.Contains(CutText(h.Text), StringComparison.Ordinal)), $"{plugin}: the search finds \"{h.Text}\" in {fk} {h.Field}; the rule has no such row");
            }
            Check(f.Rows.Count >= hits.Count, $"{plugin}: {f.Rows.Count} rows, the search finds {hits.Count} texts");
            parts.Add($"{plugin}: WARN, {f.Rows.Count} texts of {N(answer.StoredTexts)} stored ({hits.Count} found by the search too)");
            if (plugin == "economy.esm")
            {
                var samson = f.Rows.SingleOrDefault(r => r.FormKey == "111EA8:Starfield.esm");
                Check(samson != null && samson.Field == "Name" && samson.Problem.StartsWith("\"Samson’s Art\" as Mutagen reads it (code page 1252; byte 0x92 at 6)", StringComparison.Ordinal)
                    && samson.Problem.Contains("its bytes aren't UTF-8 (0x92 at 6), so a reader that takes UTF-8 shows \"Samson\uFFFDs Art\"", StringComparison.Ordinal)
                    && !samson.Problem.Contains("differently in", StringComparison.Ordinal),
                    $"economy.esm: Samson’s Art's row: {samson?.Field}: {samson?.Problem}");
                Check(samson!.Line().Contains(": Name: \"Samson’s Art\"", StringComparison.Ordinal), $"the row's line: {samson.Line()}");
                // GAPS S32 adds no row here: its game settings, perk entry points and other shared subrecords are plain ASCII
                Check(f.Rows.Count == 1, $"economy.esm: {f.Rows.Count} rows, Samson’s Art alone before");
                parts.Add($"Samson’s Art: {samson.Line().Trim()}");
            }
            else if (plugin == "stlazarusawakened.esm")
            {
                // GAPS S32: a quest's log entries (QUST CNAM) are read now; before, its dialogue's CNAM hid them
                int log = f.Rows.Count(r => r.FormKey == "024A2D:stlazarusawakened.esm" && r.Field == "Stages[*].LogEntries[*].Entry" && r.Problem.Contains("0x92", StringComparison.Ordinal));
                Check(log == 4 && f.Rows.Count == 6, $"stlazarusawakened.esm: {f.Rows.Count} rows, {log} of them 001STLazarusAwakened's log entries (4 and 6 expected)");
                parts.Add($"stlazarusawakened.esm: {log} quest log entries with ’ among {f.Rows.Count} rows");
            }
            else
            {
                int dashes = f.Rows.Count(r => r.Problem.Contains('—') && r.Problem.Contains("0x97", StringComparison.Ordinal) && r.Problem.Contains("aren't UTF-8", StringComparison.Ordinal));
                Check(dashes >= 6 && f.Rows.Any(r => r.FormKey == "000A27:apc_galaxysmartcontainers.esm" && r.Field == "Name"), $"apc_galaxysmartcontainers.esm: {dashes} em-dash rows, APCG_BookVol1's name {(f.Rows.Any(r => r.FormKey == "000A27:apc_galaxysmartcontainers.esm") ? "listed" : "not listed")}");
                parts.Add($"{dashes} em dashes (0x97)");
            }
        }
        // Plugins whose stored text is plain ASCII: localized (atom74.esm, Starfield's DLC) and not (df_firearms_modifications.esm, kgcdoom.esm)
        foreach (var plugin in new[] { "atom74.esm", "df_firearms_modifications.esm", "kgcdoom.esm" })
        {
            var answer = report.Check(plugin);
            var f = answer.Finding("stored-text");
            Check(f.Result == "PASS" && answer.StoredTexts > 0, $"{plugin}: stored-text gives {f.Result} ({string.Join("; ", f.Rows.Take(3).Select(r => r.Line().Trim()))}) after reading {answer.StoredTexts} stored texts");
            parts.Add($"{plugin}: PASS, {N(answer.StoredTexts)} stored texts read");
        }
        return string.Join("; ", parts);
    }

    // ---------------------------------------------------------------- How own FormIDs are stored (GAPS S39, fix F27)
    /// <summary>
    /// The "FormID forms" rules. A Small plugin Mutagen writes stores its own FormIDs in the Small form (FE) alone: both
    /// rules pass. One header and one link changed to the full index make it mixed: formid-forms names exactly those two
    /// records. A Full plugin with an FE header is a row of formid-form-size. On real plugins: darkstarpatch_bublumi.esm
    /// mixes (102 FE / 116 full-index headers, as the Mutagen session's raw count found for C158), and every FE word in
    /// its subrecords that names one of its records is a link the walk found; darkstar.esm stores all of its own
    /// FormIDs as its full index, every link placed; sfta04.esm (Medium) has no false FE link where its reflection data
    /// holds the bytes of one. The raw view says what a header's FormID means. Since fixes.16 the links come from Mutagen's
    /// GetStoredFormIds (C203): on darkstarpatch_bublumi.esm it gives every header as the file stores it, and as many own
    /// links as the rule counts.
    /// </summary>
    public static string StoredForms(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new CheckReport(s);
        var parts = new List<string>();
        string Others(CheckAnswer a, params string[] but) => string.Join(", ", a.Findings.Where(f => !but.Contains(f.Id) && f.Result != "PASS").Select(f => $"{f.Id} {f.Result} {f.Rows.Count}"));
        string Counts(OwnFormCounts f) => $"headers {string.Join("/", f.Headers)}, links {string.Join("/", f.Links)}, not found {f.LinksNotFound}";

        StarfieldMod SmallMod(string name)
        {
            var mod = Tests.Made.New(name);
            mod.IsSmallMaster = true;
            var a = mod.Keywords.AddNew("f27_keyword_a");
            var b = mod.Keywords.AddNew("f27_keyword_b");
            var list = mod.FormLists.AddNew("f27_list");
            list.Items.Add(a.ToLink<IStarfieldMajorRecordGetter>());
            list.Items.Add(b.ToLink<IStarfieldMajorRecordGetter>());
            list.Items.Add(new FormLink<IStarfieldMajorRecordGetter>(Tests.Made.Key("0476C4:Starfield.esm")));
            return mod;
        }
        // Where a FormID sits in a file: in a record header of a signature (12 bytes after it), or elsewhere (a link)
        static (List<int> Headers, List<int> Links) Places(byte[] bytes, uint raw, string sig)
        {
            var headers = new List<int>(); var links = new List<int>();
            for (int i = 12; i + 4 <= bytes.Length; i++)
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == raw)
                {
                    string at = Encoding.ASCII.GetString(bytes, i - 12, 4);
                    if (at == sig) headers.Add(i);
                    else if (at is not ("KYWD" or "FLST")) links.Add(i);
                }
            return (headers, links);
        }

        // 1. As Mutagen writes a Small plugin: every own FormID in the Small form
        {
            string path = Tests.Made.Write(ctx, SmallMod("f27_small.esm"), "made-forms-small", MutagenStyle.Small);
            var a = report.Check(path);
            var f = a.OwnForms!;
            ctx.WriteReport("check-forms-small.txt", a.ToText());
            Check(a.Finding("formid-forms").Result == "PASS" && a.Finding("formid-form-size").Result == "PASS" && Others(a).Length == 0, $"a Small plugin Mutagen wrote: {Others(a)}");
            Check(f.Headers[1] == 3 && f.Headers[0] == 0 && f.Links[1] == 2 && f.Links[0] == 0 && f.LinksNotFound == 0 && f.Patterns[1] == "FE000xxx", $"a Small plugin Mutagen wrote: {Counts(f)}, {f.Patterns[1]}");
            Check(a.ToText().Contains("Own FormIDs as the file stores them: 3 record headers (3 FE000xxx); 2 links to its own records (2 FE000xxx). Its header (Small) gives FE000xxx, the Small (light) form.", StringComparison.Ordinal), $"the line:\n{a.ToText()}");
            parts.Add($"a Small plugin Mutagen wrote (2 keywords, a list of both): PASS, 3 FE headers, 2 FE links");
        }
        // 2. The same with one header and one link in the full index (01: after Starfield.esm): mixed
        {
            string path = Tests.Made.Write(ctx, SmallMod("f27_mixed.esm"), "made-forms-mixed", MutagenStyle.Small);
            ctx.MustBeInWork(path);
            var bytes = File.ReadAllBytes(path);
            var (kwb, _) = Places(bytes, 0xFE000801, "KYWD");
            var (_, link) = Places(bytes, 0xFE000800, "FLST");
            Check(kwb.Count == 1 && link.Count == 1, $"the made plugin: f27_keyword_b's header found {kwb.Count} times, the list's link to f27_keyword_a {link.Count} times");
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(kwb[0]), 0x01000801);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(link[0]), 0x01000800);
            File.WriteAllBytes(path, bytes);
            var a = report.Check(path);
            string text = a.ToText();
            ctx.WriteReport("check-forms-mixed.txt", text);
            var f = a.OwnForms!; var rows = a.Finding("formid-forms").Rows;
            Check(f.Headers[1] == 2 && f.Headers[0] == 1 && f.Links[1] == 1 && f.Links[0] == 1 && f.LinksNotFound == 0, $"mixed: {Counts(f)}");
            Check(a.Finding("formid-forms").Result == "WARN" && rows.Count == 2
                && rows.Any(r => r.EditorId == "f27_keyword_b" && r.Problem.StartsWith("its header stores 01000801 (the full index)", StringComparison.Ordinal) && r.Problem.EndsWith("most of the plugin's own FormIDs are FE000xxx", StringComparison.Ordinal))
                && rows.Any(r => r.EditorId == "f27_list" && r.Problem.StartsWith("1 link to its own records stored as 01xxxxxx (LNAM at 0: 01000800)", StringComparison.Ordinal)),
                $"mixed: {a.Finding("formid-forms").Result}, {string.Join("; ", rows.Select(r => r.Line()))}");
            Check(Others(a, "formid-forms").Length == 0, $"mixed: other checks found something: {Others(a, "formid-forms")}");
            Check(text.Contains("3 record headers (1 01xxxxxx, 2 FE000xxx); 2 links to its own records (1 01xxxxxx, 1 FE000xxx). Its header (Small) gives FE000xxx, the Small (light) form; 01xxxxxx is the full index.", StringComparison.Ordinal), $"mixed, the line:\n{text}");
            // The raw view says what each header means
            var side = s.Side(path);
            string raw = side.Engine.RawText(new RecordRef(side.Plugin.Id, Enumerable.Range(0, side.Segment.Count).First(i => side.Segment.EditorId(i) == "f27_keyword_b")));
            Check(raw.Contains("FormID as stored: 01000801: the full index 01, own record 0x801, as the Creation Kit writes a Small plugin; its header gives FE000xxx, and the game reads both alike", StringComparison.Ordinal), $"the raw view of f27_keyword_b:\n{raw}");
            parts.Add($"one header and one link changed to the full index: WARN, 2 rows ({string.Join("; ", rows.Select(r => r.Line().Trim()))})");
        }
        // 3. A Full plugin with its own record's header in the Small form
        {
            var mod = Tests.Made.New("f27_full.esm");
            mod.Keywords.AddNew("f27_keyword_a");
            mod.Keywords.AddNew("f27_keyword_b");
            // A link into Starfield.esm makes it the master, so the plugin's own number is 01
            mod.FormLists.AddNew("f27_list").Items.Add(new FormLink<IStarfieldMajorRecordGetter>(Tests.Made.Key("0476C4:Starfield.esm")));
            string path = Tests.Made.Write(ctx, mod, "made-forms-full");
            ctx.MustBeInWork(path);
            var bytes = File.ReadAllBytes(path);
            var (kwb, _) = Places(bytes, 0x01000801, "KYWD");
            Check(kwb.Count == 1, $"the made Full plugin: f27_keyword_b's header found {kwb.Count} times");
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(kwb[0]), 0xFE000801);
            File.WriteAllBytes(path, bytes);
            var a = report.Check(path);
            ctx.WriteReport("check-forms-full.txt", a.ToText());
            var size = a.Finding("formid-form-size").Rows;
            Check(a.Finding("formid-form-size").Result == "WARN" && size.Count == 1 && size[0].EditorId == "f27_keyword_b"
                && size[0].Problem == "its header stores FE000801 (the Small (light) form); a Full plugin's header doesn't give that form (its own records are 01xxxxxx)",
                $"an FE header in a Full plugin: {a.Finding("formid-form-size").Result}, {string.Join("; ", size.Select(r => r.Line()))}");
            Check(Others(a, "formid-form-size", "formid-forms").Length == 0, $"an FE header in a Full plugin: other checks found something: {Others(a, "formid-form-size", "formid-forms")}");
            var side = s.Side(path);
            string raw = side.Engine.RawText(new RecordRef(side.Plugin.Id, Enumerable.Range(0, side.Segment.Count).First(i => side.Segment.EditorId(i) == "f27_keyword_b")));
            Check(raw.Contains("FormID as stored: FE000801: the Small (light) form, Small number 000, own record 0x801; the plugin's header (Full) doesn't give this form: it gives 01xxxxxx", StringComparison.Ordinal), $"the raw view of the FE header:\n{raw}");
            parts.Add($"a Full plugin with one header stored FE000801: WARN, {size[0].Line().Trim()}");
        }
        // 4. Real plugins
        {
            const string plugin = "darkstarpatch_bublumi.esm";
            var a = report.Check(plugin);
            var f = a.OwnForms!;
            ctx.WriteReport("check-forms-bublumi.txt", a.ToText(new ReportTextOptions { Detail = true }));
            // The Mutagen session's raw count of the record headers (C158, agent report b4): 102 FE, 116 full index
            Check(f.Headers[1] == 102 && f.Headers[0] == 116 && f.Headers[2] == 0 && f.Patterns[0] == "03xxxxxx" && f.Patterns[1] == "FE001xxx", $"{plugin}: {Counts(f)}");
            Check(a.Finding("formid-forms").Result == "WARN" && a.Finding("formid-form-size").Result == "PASS" && f.LinksNotFound == 0, $"{plugin}: {a.Finding("formid-forms").Result}, {a.Finding("formid-form-size").Result}, {f.LinksNotFound} links not found");
            // Every 4-byte word in its subrecords (at any offset) that names one of its records in the Small form is a
            // link the walk found; a word in the full index can also be another value (a float, a count), so there are at least as many
            var side = s.Side(plugin);
            var ids = side.Plugin.FormIds; var seg = side.Segment;
            var own = new HashSet<uint>();
            for (int i = 0; i < seg.Count; i++) if (seg[i].Owner == seg.SelfOwner) own.Add(seg[i].LocalId);
            long fe = 0, full = 0;
            for (int i = 0; i < seg.Count; i++)
            {
                var r = side.Engine.Raw(new RecordRef(side.Plugin.Id, i));
                foreach (var sub in r.Subrecords)
                {
                    if (sub.Signature is "EDID" or "FULL" or "DESC") continue;
                    var c = r.Content(sub);
                    for (int at = 0; at + 4 <= c.Length; at++)
                    {
                        uint w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(c[at..]);
                        if (!FormIdForms.IsOwn(w, ids, out uint local, out _) || !own.Contains(local) || (w >> 24) == 0xFF) continue;
                        if (FormIdForms.FormOf(w) == StoredForm.Small) fe++; else if (FormIdForms.FormOf(w) == StoredForm.FullIndex) full++;
                    }
                }
            }
            Check(fe == f.Links[1] && full >= f.Links[0], $"{plugin}: the byte scan finds {fe} FE words and {full} full-index words naming its records; the rule placed {f.Links[1]} and {f.Links[0]} links");
            // Mutagen's own account, which the rule counts from since fixes.16 (GetStoredFormIds, C203; GAPS Part 2 row 35):
            // each record's header as stored is the file's 4 bytes at 12, and its own links are where its write puts them
            int headersSame = 0, ownRecords = 0;
            var how = new Dictionary<Mutagen.Bethesda.Plugins.Records.StoredFormIdLocation, int>();
            for (int i = 0; i < seg.Count; i++)
            {
                var rr = new RecordRef(side.Plugin.Id, i);
                var rec = side.Engine.Load(rr);
                var stored = Mutagen.Bethesda.Plugins.Records.StoredFormIdMixIn.GetStoredFormIds(rec.Record, rec.Session.Mod, rec.Route == ReadRoute.Direct ? rec.Session.MasterFlags : null);
                if (stored[0].Location == Mutagen.Bethesda.Plugins.Records.StoredFormIdLocation.Header && stored[0].Raw == side.Engine.Raw(rr).FormId) headersSame++;
                if (seg[i].Owner == seg.SelfOwner) ownRecords++;
                foreach (var x in stored.Skip(1).Where(x => x.IsOwn)) how[x.Location] = how.GetValueOrDefault(x.Location) + 1;
            }
            Check(headersSame == seg.Count && ownRecords == 218 && how.Values.Sum() == f.Links[0] + f.Links[1] && !how.ContainsKey(Mutagen.Bethesda.Plugins.Records.StoredFormIdLocation.NotFound),
                $"{plugin}: GetStoredFormIds gives {headersSame} of {seg.Count} headers as the file stores them, and {how.Values.Sum()} own links ({string.Join(", ", how.Select(x => $"{x.Key} {x.Value}"))}); the rule counted {f.Links[0] + f.Links[1]}");
            // WEAP 000866 holds both forms in its keywords (the Mutagen session's example)
            var weap = a.Finding("formid-forms").Rows.FirstOrDefault(r => r.FormKey == "000866:darkstarpatch_bublumi.esm");
            Check(weap != null && weap.Signature == "WEAP", $"{plugin}: no row for its weapon 000866");
            var r863 = ReportSession.In(s.Engine, plugin, plugin, 0x863)!.Value;
            string raw = r863.Engine.RawText(r863.Ref);
            Check(Regex.IsMatch(raw, @"FormID as stored: (FE001863: the Small \(light\) form, Small number 001, own record 0x863$|03000863: the full index 03, own record 0x863, as the Creation Kit writes a Small plugin)", RegexOptions.Multiline), $"{plugin} 000863's raw view:\n{raw}");
            parts.Add($"{plugin}: headers 102 FE / 116 full index (Mutagen's GetStoredFormIds gives all {seg.Count} headers as stored), links {f.Links[1]} FE (the byte scan's {fe}) / {f.Links[0]} full index (the byte scan's {full}; GetStoredFormIds: {string.Join(", ", how.OrderBy(x => x.Key).Select(x => $"{x.Value} {x.Key}"))}), WARN with {a.Finding("formid-forms").Rows.Count} rows; raw view of 000863: {raw.Split('\n').First(l => l.Contains("FormID as stored", StringComparison.Ordinal)).Trim()}");
        }
        {
            const string plugin = "darkstar.esm";
            var a = report.Check(plugin);
            var f = a.OwnForms!;
            var seg = s.Side(plugin).Segment;
            int own = Enumerable.Range(0, seg.Count).Count(i => !seg[i].IsUnresolved && seg[i].Owner == seg.SelfOwner);
            Check(f.Headers[0] == own && f.Headers[1] + f.Headers[2] == 0 && f.Links[1] + f.Links[2] == 0 && f.Links[0] > 0 && f.LinksNotFound == 0 && f.HeadersPast == 0 && f.LinksPast == 0,
                $"{plugin}: {Counts(f)}, {own} own records");
            Check(a.Finding("formid-forms").Result == "PASS" && a.Finding("formid-form-size").Result == "PASS", $"{plugin}: {a.Finding("formid-forms").Result}, {a.Finding("formid-form-size").Result}");
            parts.Add($"{plugin}: PASS, {N(own)} headers and {N(f.Links[0])} links all {f.Patterns[0]}, every link placed");
        }
        {
            // WeapFX_AphelionAmmoCounter (006CFE:sfta04.esm) links to its own 000001 once (FD010001 in REFL), and its REFL
            // holds the bytes 01 00 00 FE six more times inside other values: the link is the FD one, not a false FE link
            const string plugin = "sfta04.esm";
            var a = report.Check(plugin);
            var f = a.OwnForms!;
            Check(a.Finding("formid-forms").Result == "PASS" && a.Finding("formid-form-size").Result == "PASS" && f.Links[1] == 0 && f.Headers[1] == 0 && f.Links[2] > 0 && f.LinksNotFound == 0,
                $"{plugin}: {a.Finding("formid-forms").Result}, {a.Finding("formid-form-size").Result}, {Counts(f)}; {string.Join("; ", a.Finding("formid-form-size").Rows.Select(r => r.Line()))}");
            parts.Add($"{plugin} (Medium): PASS, {N(f.Headers[2])} headers and {N(f.Links[2])} links all {f.Patterns[2]} (no false FE link in WeapFX_AphelionAmmoCounter's reflection data)");
        }
        return string.Join("; ", parts) + ".";
    }

    /// <summary>The part of a text the rule's row quotes for sure: from its first character outside ASCII, 40 characters.</summary>
    static string CutText(string t)
    {
        int i = t.IndexOf(t.First(c => c > 0x7F));
        string part = t[i..Math.Min(t.Length, i + 40)];
        return part.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
    }
}

static class RefusedTests
{
    /// <summary>
    /// fixes.17 (C204, GAPS Part 2 row 6, the user's decision): Mutagen refuses MissionsGalore.esm (GLOB 000867 twice,
    /// GLOB/TMLM 000869). The header index takes Mutagen's error, leaves the plugin out of the winners (Starfield.esm's
    /// MB_MaxTargetLevel wins again), changes lists it, and check2 and the raw view still read it (KeepLater).
    /// </summary>
    public static string MissionsGalore(Ctx ctx)
    {
        const string Name = "MissionsGalore.esm";
        var s = ctx.Session;
        var p = s.Index.Plugin(Name);
        if (p == null) return $"SKIP {Name} isn't in Data";
        string? refused = p.Problems.Refused;
        Ctx.Check(refused != null && refused.StartsWith("DuplicateFormKeyException: ", StringComparison.Ordinal) && refused.Contains("2 pairs", StringComparison.Ordinal)
            && refused.Contains("000867:MissionsGalore.esm", StringComparison.Ordinal) && refused.Contains("000869:MissionsGalore.esm", StringComparison.Ordinal),
            $"{Name} isn't refused with Mutagen's error: {refused ?? "(not refused)"}");
        Ctx.Check(p.LeftOut != null && p.Problems.Lines().Any(l => l.StartsWith("refused by Mutagen", StringComparison.Ordinal)), $"{Name} isn't left out: {p.LeftOut}");

        // The winners: its 184 overrides no longer win; the version before each does
        var v = s.Index.Versions("028FBC:Starfield.esm")!;
        string winner = v.Winner < 0 ? "none" : s.Index.Plugins[v.Holders[v.Winner].Plugin].Name;
        Ctx.Check(winner == "Starfield.esm" && v.Holders.Any(h => s.Index.Plugins[h.Plugin].Name == Name), $"028FBC:Starfield.esm is won by {winner}");
        var own = s.Index.Versions("000801:MissionsGalore.esm")!;
        Ctx.Check(own.Winner < 0, "000801:MissionsGalore.esm still has a winner");
        int overrides = p.Segment!.FirstOwn, stillWin = 0;
        for (int i = 0; i < overrides; i++) if (s.Index.IsWinner(new RecordRef(p.Id, i))) stillWin++;
        Ctx.Check(stillWin == 0, $"{stillWin} of {Name}'s {overrides} overrides still win");

        // changes2 lists it with Mutagen's error
        var changes = new ChangesReport(s, historyFolder: ctx.Folder("refused-history")).Changes();
        Ctx.Check(changes.Refused.Any(r => r.Plugin == Name && r.Why == refused), $"changes doesn't list {Name} as refused ({changes.Refused.Count} listed)");

        // check2 reads it all the same (KeepLater): the repeated FormKeys and the FormIDs past its masters, as before fixes.17
        var check = new CheckReport(s).Check(Name);
        var repeat = check.Finding("formkey-repeat"); var prefix = check.Finding("formid-master");
        Ctx.Check(repeat.Rows.Count == 4 && repeat.Note != null && repeat.Note.Contains("Mutagen refuses the plugin", StringComparison.Ordinal), $"formkey-repeat: {repeat.Rows.Count} rows, note {repeat.Note}");
        Ctx.Check(prefix.Rows.Count == 219 && check.RecordsRead == 405 && check.Findings.All(f => f.NotRun == null), $"formid-master {prefix.Rows.Count} rows, {check.RecordsRead} records read, {check.NotRun} not run");

        // The raw view of each record of a pair, read with Mutagen's KeepLater read
        var a = s.Engine.Find(Name, "ShipTerminalMod_Show_ModSettings_MissionsGalore");
        var raw = s.Engine.Raw(a);
        Ctx.Check(raw.Signature == "GLOB", $"the raw view of 000867 reads {raw.Signature}");
        return $"{Name}: refused ({refused!.Length} characters of Mutagen's error: 2 pairs, 000867 GLOB/GLOB and 000869 GLOB/TMLM), left out; 028FBC:Starfield.esm won by {winner}, none of its {overrides} overrides wins; changes lists it; check2 read {check.RecordsRead} records (formkey-repeat {repeat.Rows.Count} rows, formid-master {prefix.Rows.Count}); the raw view reads 000867 ({raw.Signature})";
    }
}
