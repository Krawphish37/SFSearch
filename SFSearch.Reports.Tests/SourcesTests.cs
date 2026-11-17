// sources, held against today's Sources on real mods (the same items found the same ways), and the default mode
// (the plugin's patches searched too) against the header index.
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

static class SourcesTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    /// <summary>An injection in one line: the call, its function, the lists and the records.</summary>
    static string Shown(Injection i) => $"{i.Call} in {i.Function}: {string.Join(",", i.Lists.Select(l => l.Property + (l.Member != null ? "." + l.Member : "") + (l.Element ? "[]" : "")))} <- {string.Join(",", i.Items.Select(l => l.Property + (l.Member != null ? "." + l.Member : "") + (l.Element ? "[]" : "")))}";

    sealed record Case(string Name, string Plugin, string[] With, string? Item = null, string? Recorded = null);

    static readonly Case[] Cases =
    {
        new("sources__atom74.esm", "atom74.esm", Array.Empty<string>()),
        new("sources__weap_starlight.esm", "weap_starlight.esm", Array.Empty<string>()),
        new("sources__kgcdoom.esm", "kgcdoom.esm", Array.Empty<string>()),
        new("sources__aa50_longwatch.esm", "aa50_longwatch.esm", Array.Empty<string>()),
        new("sources__sfta04.esm", "sfta04.esm", Array.Empty<string>()),
        new("sources__sfta04.esm__with_sfbgs003.esm", "sfta04.esm", new[] { "sfbgs003.esm" }, Recorded: "451"),
        new("sources__sfta04.esm__with_sfbgs003.esm__item", "sfta04.esm", new[] { "sfbgs003.esm" }, Item: "SFTA04_AphelionLaserRifle", Recorded: "452"),
        new("sources__darkstar.esm", "darkstar.esm", Array.Empty<string>()),
    };

    sealed class TodayItem
    {
        public string EditorId = "", Type = "", Verdict = "", Gate = "", Key = "";
        public List<(string Kind, string Text)> Sources = new();
        public List<string> Checks = new();
        public bool NoSource;
    }

    /// <summary>"0008df:atom74": a FormKey the way today's Sources prints it (no extension), lower case.</summary>
    static string Stem(string formKey) => Regex.Replace(formKey.Trim().ToLowerInvariant(), @"\.(esm|esp|esl)$", "");
    static readonly Regex StemKey = new(@"\b[0-9A-Fa-f]{6}:[\w\-.+]+", RegexOptions.Compiled);
    static List<string> StemKeys(string text) => StemKey.Matches(text).Select(m => Stem(m.Value.TrimEnd('.'))).ToList();

    static List<TodayItem> ParseToday(string text, out List<string> searched)
    {
        var items = new List<TodayItem>();
        var s = Regex.Match(text, @"Searched: (.*?)\. Leveled-list depth");
        searched = s.Success ? s.Groups[1].Value.Split(", ").ToList() : new List<string>();
        foreach (var row in Ctx.Table(text, "Item name"))
        {
            if (row.Length < 4) continue;
            var e = Regex.Match(row[1], @"<sub>\*(.*)\*</sub>");
            items.Add(new TodayItem { EditorId = e.Success ? e.Groups[1].Value : row[1], Type = row[2], Verdict = row[3], Gate = row.Length > 4 ? row[4] : "" });
        }
        var byEdid = items.GroupBy(i => i.EditorId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        TodayItem? current = null; bool details = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("#### Details", StringComparison.Ordinal)) { details = true; continue; }
            if (line.StartsWith("#### What this can't see", StringComparison.Ordinal)) break;
            if (!details) continue;
            var h = Regex.Match(line, @"^\*\*.*\*\*: (.+?) \(([0-9A-Fa-f]{6}:[^)]+)\), (Weapon|Armor)");
            if (h.Success) { current = byEdid.GetValueOrDefault(h.Groups[1].Value); if (current != null) current.Key = Stem(h.Groups[2].Value); continue; }
            if (current == null || !line.StartsWith("- ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("- No source found", StringComparison.Ordinal)) { current.NoSource = true; continue; }
            var c = Regex.Match(line, @"^- \*Checked by \(not a source\):\* (.*)$");
            if (c.Success) { current.Checks.Add(c.Groups[1].Value); continue; }
            var k = Regex.Match(line, @"^- \*\*(.+?):\*\* (.*)$");
            if (k.Success) current.Sources.Add((k.Groups[1].Value, k.Groups[2].Value));
        }
        return items;
    }

    /// <summary>What names a source whichever side printed it: its kind, the record it is, what it adds to or which stage, the lists on the way, the other routes.</summary>
    static string Describe(string kind, string firstKey, string extra, IEnumerable<string> via, int otherRoutes) =>
        $"{kind}|{firstKey}|{extra}|via:{string.Join(">", via)}|routes:{otherRoutes}";

    static (List<string> Descriptors, Dictionary<string, int> Folded) TodayDescriptors(TodayItem item)
    {
        var list = new List<string>(); var folded = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (kind, text0) in item.Sources)
        {
            var f = Regex.Match(text0, @"^(\d+) sources, e\.g\. ");
            if (f.Success) { folded[kind] = int.Parse(f.Groups[1].Value); continue; }
            string text = text0; int routes = 0;
            var r = Regex.Match(text, @" \(\+(\d+) other routes?\)$");
            if (r.Success) { routes = int.Parse(r.Groups[1].Value); text = text[..r.Index]; }
            var via = new List<string>();
            int v = text.LastIndexOf(" · via ", StringComparison.Ordinal);
            if (v >= 0) { via = StemKeys(text[(v + 7)..]); text = text[..v]; }
            var keys = StemKeys(text);
            string first = keys.Count > 0 ? keys[0] : "";
            string extra = "";
            var stage = Regex.Match(text, @"reward at stage (\d+)");
            if (stage.Success) extra = "stage " + stage.Groups[1].Value;
            int adds = text.IndexOf(" adds it to ", StringComparison.Ordinal);
            if (adds >= 0) extra = "adds to " + string.Join(",", StemKeys(text[adds..]).OrderBy(x => x, StringComparer.Ordinal));
            if (kind == "Recipe") extra = "bench " + (keys.Count > 1 && text.Contains(" at ", StringComparison.Ordinal) && !text.Contains(" at no bench", StringComparison.Ordinal) ? keys[1] : "none") + $" conditions {RecipeConditions(text)}";
            list.Add(Describe(kind, first, extra, via, routes));
        }
        return (list, folded);
    }

    static int RecipeConditions(string text)
    {
        int at = text.IndexOf("conditions: ", StringComparison.Ordinal);
        if (at < 0) return 0;
        string conds = text[(at + 12)..];
        return conds == "none" ? 0 : conds.Split(" · ").Length;
    }

    static string MyDescriptor(ItemSource s)
    {
        string extra = "";
        if (s.Stage != null) extra = "stage " + s.Stage;
        if (s.Targets != null) extra = "adds to " + string.Join(",", s.Targets.Select(Stem).OrderBy(x => x, StringComparer.Ordinal));
        if (s.Kind == "Recipe") extra = $"bench {(s.Workbench == null ? "none" : StemKeys(s.Workbench).FirstOrDefault() ?? "none")} conditions {s.Conditions?.Count ?? 0}";
        return Describe(s.Kind, s.Record == null ? "" : Stem(s.Record), extra, s.Via.Select(Stem), s.Routes - 1);
    }

    static readonly HashSet<string> FoldedKinds = new(StringComparer.Ordinal) { "NPC", "Placed", "Container" };

    public static string Today(Ctx ctx)
    {
        if (!ctx.HasToday && ctx.Baseline == null) return "SKIP today's Sources wasn't given (--today, --baseline, or --sfsearch and --home)";
        var s = ctx.Session;
        var report = new SourcesReport(s);
        var parts = new List<string>(); var sb = new StringBuilder();
        long itemsAll = 0, sourcesSame = 0, onlyTodayAll = 0, onlyNewAll = 0, verdictSame = 0, otherVerdicts = 0; int unexplained = 0, recorded = 0;
        var causes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in Cases)
        {
            var args = new List<string> { "Sources", c.Plugin };
            if (c.With.Length > 0) { args.Add("--with"); args.Add(string.Join(",", c.With)); }
            if (c.Item != null) { args.Add("--item"); args.Add(c.Item); }
            string? text = c.Recorded != null ? ctx.Recorded(c.Recorded) : null;
            string from = "run on the test home";
            if (text != null) { recorded++; from = $"recorded run {c.Recorded}"; }
            else text = ctx.Today(c.Name, args.ToArray());
            if (text == null) { if (c.Recorded != null && c.Item != null) continue; return "SKIP today's Sources couldn't be run"; }
            var today = ParseToday(text, out var searched);
            var a = report.Sources(c.Plugin, new SourcesOptions { With = c.With, Item = c.Item, Dependents = false });
            ctx.WriteReport(c.Name + ".txt", a.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
            Check(searched.Count == a.Searched.Count && searched.All(p => a.Searched.Contains(p, StringComparer.OrdinalIgnoreCase)), $"{c.Name}: searched {string.Join(", ", a.Searched)}; today {string.Join(", ", searched)}");
            Check(today.Count == a.Items.Count, $"{c.Name}: {a.Items.Count} items, today {today.Count}");
            var stored = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> Stored(string plugin)
            {
                if (stored.TryGetValue(plugin, out var have)) return have;
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                if (ctx.Today($"stats__{plugin}", "Stats", plugin) is { } stats)
                    foreach (var row in Ctx.Table(stats, "Signature")) if (row.Length >= 5) d[row[0]] = row[4];
                return stored[plugin] = d;
            }
            int same = 0, onlyToday = 0, onlyNew = 0, verdicts = 0, gates = 0, verdictOther = 0;
            foreach (var item in a.Items)
            {
                var t = today.FirstOrDefault(x => x.EditorId.Equals(item.EditorId, StringComparison.OrdinalIgnoreCase) && (x.Key.Length == 0 || x.Key == Stem(item.FormKey)))
                    ?? throw new TestFailed($"{c.Name}: today's answer has no item {item.Label}");
                string at = $"{c.Name} {item.Label}";
                Check((t.Type == "Weapon" ? "WEAP" : "ARMO") == item.Signature, $"{at}: a {item.Signature}; today {t.Type}");
                var (theirs, folded) = TodayDescriptors(t);
                var mine = item.Sources.Where(x => !folded.ContainsKey(x.Kind)).Select(MyDescriptor).ToList();
                // Kinds today's folds into one line give a count only
                var foldedDiffer = new List<string>();
                foreach (var (kind, n) in folded)
                {
                    int have = item.Sources.Count(x => x.Kind == kind);
                    if (have == n) same += n; else foldedDiffer.Add($"{kind}: {have} sources, today {n}");
                }
                var count = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var d in theirs) count[d] = count.GetValueOrDefault(d) + 1;
                var extraMine = new List<string>();
                foreach (var d in mine)
                {
                    if (count.TryGetValue(d, out int n) && n > 0) { count[d] = n - 1; same++; }
                    else extraMine.Add(d);
                }
                var extraToday = count.Where(kv => kv.Value > 0).SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).ToList();
                // A source both have, by another number of routes, is one difference, not two
                foreach (var d in extraMine.ToList())
                {
                    string bare = d[..d.LastIndexOf("|routes:", StringComparison.Ordinal)];
                    var twin = extraToday.FirstOrDefault(x => x[..x.LastIndexOf("|routes:", StringComparison.Ordinal)] == bare);
                    if (twin == null) continue;
                    extraMine.Remove(d); extraToday.Remove(twin);
                    sb.AppendLine($"  {at}: other routes: new {d[(d.LastIndexOf(':') + 1)..]}, today {twin[(twin.LastIndexOf(':') + 1)..]} for {bare}");
                    unexplained++;
                }
                // GAPS S10: a container a vendor faction names, or a master's list a vendor's container holds, was a
                // "Container" or an "Unused list" today; it is now a vendor found by its faction
                int vendorsFound = 0;
                foreach (var d in extraToday.ToList())
                {
                    var p = d.Split('|');
                    if (p.Length < 5 || p[0] is not ("Container" or "Unused list")) continue;
                    string via = p[3][4..];
                    string viaNew = p[0] == "Unused list" ? (via.Length == 0 ? p[1] : via + ">" + p[1]) : via;
                    var twin = extraMine.FirstOrDefault(m => { var q = m.Split('|'); return q[0] == "Vendor" && q[1] == p[1] && q[3][4..] == viaNew; });
                    if (twin == null || item.Sources.First(x => MyDescriptor(x) == twin).FoundBy != "faction") continue;
                    extraToday.Remove(d); extraMine.Remove(twin); vendorsFound++;
                    const string why = "a vendor found by its faction's merchant container, which today's Sources called a container or an unused list (GAPS S10)";
                    sb.AppendLine($"  {at}: today {d}, new {twin}: {why}");
                    causes[why] = causes.GetValueOrDefault(why) + 1;
                }
                foreach (var d in extraToday) { sb.AppendLine($"  UNEXPLAINED only today {at}: {d}"); unexplained++; onlyToday++; }
                foreach (var d in extraMine)
                {
                    // The record that is the source: is it of a type today's database stores?
                    var source = item.Sources.First(x => MyDescriptor(x) == d);
                    string? why = null;
                    if (source.Record != null && s.Winner(source.Record) is { } hit)
                    {
                        string type = s.Compare.View(hit.Engine, hit.Ref).Type;
                        var st = Stored(hit.Plugin.Name);
                        if (st.TryGetValue(type, out var how) && how != "Yes") why = $"the source is a {hit.Signature}, a record type today's database doesn't store in full";
                        else if (hit.Signature is "DIAL" or "INFO" or "SCEN") why = $"the source is a {hit.Signature}, a record type today's database doesn't store";
                    }
                    if (why == null) { sb.AppendLine($"  UNEXPLAINED only new {at}: {d}"); unexplained++; }
                    else { sb.AppendLine($"  only new {at}: {d}: {why}"); causes[why] = causes.GetValueOrDefault(why) + 1; }
                    onlyNew++;
                }
                foreach (var d in foldedDiffer) { sb.AppendLine($"  UNEXPLAINED {at}: {d}"); unexplained++; }
                // Verdict, gate, flags, checks
                bool sameSources = extraMine.Count == 0 && extraToday.Count == 0 && foldedDiffer.Count == 0 && vendorsFound == 0;
                string todayNone = t.Verdict.StartsWith("No source found", StringComparison.Ordinal) ? "No source found" : t.Verdict.StartsWith("Unused list only", StringComparison.Ordinal) ? "Unused list only" : "";
                if (item.Verdict == t.Verdict) verdicts++;
                // Today's verdict leaves sources of the kind Other out, and says "No source found" beside them
                else if (todayNone.Length > 0 && item.Sources.Any(x => x.Kind == "Other") && item.Verdict == "Used by other records" + t.Verdict[todayNone.Length..]) { otherVerdicts++; verdictOther++; }
                else if (sameSources) { sb.AppendLine($"  UNEXPLAINED {at}: verdict \"{item.Verdict}\"; today \"{t.Verdict}\""); unexplained++; }
                else sb.AppendLine($"  {at}: verdict \"{item.Verdict}\"; today \"{t.Verdict}\" (the sources differ, above)");
                var gateToday = Regex.Matches(t.Gate, @"(\w+)\(").Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
                var gateMine = item.Sources.Where(x => x.Kind == "Recipe" && x.Gated).SelectMany(x => x.Conditions!.Where(k => k.Function != "GetLevel").Select(k => k.Function)).ToList();
                if ((t.Gate.Length == 0) == (item.Gate.Length == 0) && gateToday.Distinct().SequenceEqual(gateMine.Distinct().OrderBy(x => x))) gates++;
                else { sb.AppendLine($"  UNEXPLAINED {at}: gate \"{item.Gate}\"; today \"{t.Gate}\""); unexplained++; }
                Check(t.NoSource == (item.Sources.Count == 0) || !sameSources, $"{at}: {(item.Sources.Count == 0 ? "no source" : "sources")}; today {(t.NoSource ? "no source" : "sources")}");
                var checksToday = t.Checks.SelectMany(StemKeys).OrderBy(x => x).ToList();
                var checksMine = item.CheckedBy.Distinct().Select(x => StemKeys(x).FirstOrDefault() ?? "").OrderBy(x => x).ToList();
                if (!checksToday.Distinct().SequenceEqual(checksMine.Distinct())) { sb.AppendLine($"  UNEXPLAINED {at}: checked by {string.Join(",", checksMine)}; today {string.Join(",", checksToday)}"); unexplained++; }
            }
            itemsAll += a.Items.Count; sourcesSame += same; onlyTodayAll += onlyToday; onlyNewAll += onlyNew; verdictSame += verdicts;
            parts.Add($"{c.Plugin}{(c.With.Length > 0 ? " with " + string.Join(",", c.With) : "")}{(c.Item != null ? " item " + c.Item : "")} ({from}): {N(a.Items.Count)} items, {N(verdicts)} with the same verdict{(verdictOther > 0 ? $" and {N(verdictOther)} \"Used by other records\" where today says \"No source found\" or \"Unused list only\"" : "")}, {N(gates)} the same gate, {N(same)} sources the same, {onlyToday} only today, {onlyNew} only new; verdicts: {string.Join(", ", a.Items.GroupBy(i => i.Verdict.Split(',')[0].Trim()).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}; the player can get {a.Items.Count(i => i.Reach == Reach.Yes)}, unclear {a.Items.Count(i => i.Reach == Reach.Unclear)}, no path {a.Items.Count(i => i.Reach == Reach.No)}");
            sb.AppendLine(parts[^1]);
        }
        ctx.WriteReport("sources-today.txt", sb.ToString());
        Check(unexplained == 0, $"{unexplained} differences from today's Sources have no cause the test can show (see sources-today.txt): {string.Join(" | ", sb.ToString().Split('\n').Where(l => l.Contains("UNEXPLAINED") || l.Contains("other routes")).Take(8))}");
        return $"{Cases.Length} runs of today's Sources ({recorded} from the recorded baseline), the same plugins searched: {N(itemsAll)} items, {N(verdictSame)} with the same verdict, {N(otherVerdicts)} more where the new verdict is \"Used by other records\" and today's is \"No source found\" or \"Unused list only\" (sources of the kind Other: today's verdict doesn't count that kind, and can't see records of types its database doesn't store); {N(sourcesSame)} sources the same (kind, record, lists on the way, stage or lists added to, recipe bench and number of conditions), {N(onlyTodayAll)} only today's, {N(onlyNewAll)} only the new one's" +
            (causes.Count > 0 ? $" ({string.Join("; ", causes.Select(k => $"{k.Value} {k.Key}"))})" : "") + $". {string.Join(" || ", parts)}";
    }

    // ---------------------------------------------------------------- A form list's AddToList (ANAM), GAPS S61
    /// <summary>
    /// GAPS S61: a form list that names another in AddToList (ANAM) has its entries added to that list by the game.
    /// A made plugin: a gun in list A whose AddToList is B, which a quest holds (the gun reaches the quest through A, then
    /// B); a gun in B only (it doesn't reach A's quest: A adds to B, it doesn't hold B); a chain C, D, E; a cycle X, Y;
    /// an AddToList that names a weapon (no hop). Then every form list that wins in the load order, read through
    /// Mutagen: the lists SFSearch reads from the bytes as adding to each other are Mutagen's, and every list added to
    /// has the entries the game gives it (its own, then its adders', followed on). Then sources2 on a real plugin whose
    /// items reach a list through AddToList.
    /// </summary>
    public static string AddToList(Ctx ctx)
    {
        var s = ctx.Session;
        var parts = new List<string>();
        static string Key(FormKey k) => ReportSession.Canon(k.ToString());
        static string KeyOf(IMajorRecordGetter r) => ReportSession.Canon(r.FormKey.ToString());

        // ---- The made plugin
        var mod = Made.New("SourcesAddToListTest.esm");
        Weapon Gun(string edid) { var w = new Weapon(mod) { EditorID = edid }; mod.Weapons.Add(w); return w; }
        FormList List(string edid, params IMajorRecordGetter[] items)
        {
            var l = new FormList(mod) { EditorID = edid };
            foreach (var i in items) l.Items.Add(i.FormKey.ToLink<IStarfieldMajorRecordGetter>());
            mod.FormLists.Add(l); return l;
        }
        static void Adds(FormList from, IMajorRecordGetter to) => from.AddToList.SetTo(to.FormKey);
        Quest QuestHolding(string edid, FormList list)
        {
            var q = new Quest(mod)
            {
                EditorID = edid,
                VirtualMachineAdapter = new QuestAdapter { Scripts = new() { new ScriptEntry { Name = "SrcAdd_NoSuchScript", Properties = new() { new ScriptObjectProperty { Name = "Things", Object = list.FormKey.ToLink<IStarfieldMajorRecordGetter>() } } } } },
            };
            mod.Quests.Add(q); return q;
        }
        var gunA = Gun("SrcAdd_GunA"); var gunB = Gun("SrcAdd_GunB"); var gunC = Gun("SrcAdd_GunC"); var gunX = Gun("SrcAdd_GunX"); var gunN = Gun("SrcAdd_GunN");
        var a = List("SrcAdd_ListA", gunA); var b = List("SrcAdd_ListB", gunB);
        Adds(a, b); var questB = QuestHolding("SrcAdd_QuestB", b); var questA = QuestHolding("SrcAdd_QuestA", a);
        var c = List("SrcAdd_ListC", gunC); var d = List("SrcAdd_ListD"); var e = List("SrcAdd_ListE");
        Adds(c, d); Adds(d, e); var questE = QuestHolding("SrcAdd_QuestE", e);
        var x = List("SrcAdd_ListX", gunX); var y = List("SrcAdd_ListY");
        Adds(x, y); Adds(y, x); var questY = QuestHolding("SrcAdd_QuestY", y);
        var n = List("SrcAdd_ListN", gunN); Adds(n, gunA);
        // A list whose AddToList names a master's form list that nothing searched holds: the walk ends there, said
        var gunM = Gun("SrcAdd_GunM"); var m = List("SrcAdd_ListM", gunM); m.AddToList.SetTo(Made.Key(GameFormList));
        string path = Made.Write(ctx, mod, "sources-addtolist");
        var answer = new SourcesReport(s).Sources(path);
        ctx.WriteReport("sources-addtolist.txt", answer.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
        SourcedItem Item(Weapon w) => answer.Items.FirstOrDefault(i => i.EditorId == w.EditorID) ?? throw new TestFailed($"no item {w.EditorID} in the answer");
        static string All(SourcedItem i) => string.Join(" | ", i.Sources.Select(z => $"{z.Kind}: {z.Text} via {string.Join(" > ", z.ViaLabels)}"));
        static bool Quest(SourcedItem i, IMajorRecordGetter quest, params IMajorRecordGetter[] via) =>
            i.Sources.Any(z => z.Kind == "Quest" && z.Record == KeyOf(quest) && z.Via.SequenceEqual(via.Select(KeyOf), StringComparer.OrdinalIgnoreCase));
        var ia = Item(gunA);
        Check(Quest(ia, questB, a, b) && Quest(ia, questA, a) && ia.Sources.Count == 2, $"SrcAdd_GunA (in A, A's AddToList is B, quests hold A and B): {All(ia)}");
        var viaB = ia.Sources.First(z => z.Record == KeyOf(questB));
        Check(viaB.ViaLabels.Count == 2 && viaB.ViaLabels[1].Contains("(by SrcAdd_ListA's AddToList)", StringComparison.Ordinal), $"SrcAdd_GunA: the hop through AddToList isn't said: {string.Join(" > ", viaB.ViaLabels)}");
        var ib = Item(gunB);
        Check(ib.Sources.Count == 1 && Quest(ib, questB, b), $"SrcAdd_GunB (in B only; A adds to B): {All(ib)}");
        var ic = Item(gunC);
        Check(ic.Sources.Count == 1 && Quest(ic, questE, c, d, e), $"SrcAdd_GunC (C to D to E, a quest holds E): {All(ic)}");
        var ix = Item(gunX);
        Check(ix.Sources.Count == 1 && Quest(ix, questY, x, y), $"SrcAdd_GunX (X and Y name each other, a quest holds Y): {All(ix)}");
        var inn = Item(gunN);
        Check(inn.Sources.Count == 1 && inn.Sources[0].Kind == "Unused list" && inn.Sources[0].Record == KeyOf(n), $"SrcAdd_GunN (N's AddToList names a weapon): {All(inn)}");
        var im = Item(gunM);
        Check(im.Sources.Count == 1 && im.Sources[0].Kind == "Unused list" && im.Sources[0].Record == GameFormList && im.Sources[0].Via.SequenceEqual(new[] { KeyOf(m) }, StringComparer.OrdinalIgnoreCase) && im.Sources[0].Text.StartsWith("HelpManualPC (000163:Starfield.esm) (by SrcAdd_ListM's AddToList) is used by nothing", StringComparison.Ordinal),
            $"SrcAdd_GunM (M's AddToList names {GameFormList}, which nothing searched holds): {All(im)}");
        parts.Add("made plugin: A to B reaches B's quest (the hop said), B's own gun doesn't reach A's quest, C to D to E, the cycle X, Y ends, an AddToList naming a weapon adds nothing, M to a Starfield.esm list ends there as an unused list");

        // ---- Every winning form list of the load order, read through Mutagen
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var adds = FormListAdds.Read(s.Index);
        double bytesMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var mutagenAdds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var own = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int read = 0;
        foreach (var p in s.Index.Plugins.Where(p => p.Order.Enabled && p.LeftOut == null && p.Segment != null))
            foreach (int i in p.Segment!.EntriesOf("FLST"))
            {
                var r = new RecordRef(p.Id, i);
                if (i >= p.Segment.FirstUnresolved || !s.Index.IsWinner(r)) continue;
                var fl = (IFormListGetter)s.Engine.Load(r).Record;
                read++;
                own[Key(fl.FormKey)] = fl.Items.Where(l => !l.IsNull).Select(l => Key(l.FormKey)).ToList();
                if (fl.AddToList.FormKeyNullable is { IsNull: false } to && s.Winner(Key(to))?.Info.Signature == "FLST") mutagenAdds[Key(fl.FormKey)] = Key(to);
            }
        double mutagenMs = clock.Elapsed.TotalMilliseconds;
        Check(adds.Records == read && adds.Unreadable == 0, $"form lists read: {adds.Records} from the bytes, {read} through Mutagen ({adds.Unreadable} unreadable)");
        var wrong = mutagenAdds.Where(kv => !string.Equals(adds.AddsTo(kv.Key), kv.Value, StringComparison.OrdinalIgnoreCase)).Select(kv => $"{kv.Key} to {kv.Value} (bytes: {adds.AddsTo(kv.Key) ?? "none"})").ToList();
        Check(wrong.Count == 0 && adds.Adding == mutagenAdds.Count, $"AddToList from the bytes against Mutagen: {adds.Adding} against {mutagenAdds.Count}; differ: {string.Join("; ", wrong.Take(5))}");
        // Each list added to: its entries as the game gives them, worked out here from Mutagen's reading
        var into = mutagenAdds.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.OrdinalIgnoreCase);
        var scope = new ReadScope();
        int targets = 0, cycles = 0, deeper = 0, moreEntries = 0, added = 0;
        foreach (var target in into.Keys)
        {
            var want = new HashSet<string>(own.GetValueOrDefault(target) ?? new(), StringComparer.OrdinalIgnoreCase);
            int ownCount = want.Count;
            var by = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(into[target]);
            while (stack.Count > 0)
            {
                string f = stack.Pop();
                if (f.Equals(target, StringComparison.OrdinalIgnoreCase)) { cycles++; continue; }
                if (!by.Add(f)) continue;
                want.UnionWith(own.GetValueOrDefault(f) ?? new());
                foreach (var g in into.GetValueOrDefault(f) ?? new()) stack.Push(g);
            }
            if (by.Count > into[target].Count) deeper++;
            var got = s.FormListEntries(scope, target);
            Check(got != null && got.Entries.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(want) && got.AddedBy.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(by)
                && got.Entries.Skip(own.GetValueOrDefault(target)?.Count ?? 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() == got.Entries.Count - (own.GetValueOrDefault(target)?.Count ?? 0),
                $"{target}: {got?.Entries.Count} entries, added by [{string.Join(", ", got?.AddedBy ?? Array.Empty<string>())}]; Mutagen: {want.Count} entries, added by [{string.Join(", ", by)}]");
            if (want.Count > ownCount) { moreEntries++; added += want.Count - ownCount; }
            targets++;
        }
        parts.Add($"{read:N0} winning form lists: {adds.Adding:N0} name a list in AddToList (bytes {bytesMs:N0} ms, Mutagen {mutagenMs:N0} ms; the same), {targets:N0} lists added to, {moreEntries:N0} of them get {added:N0} entries they don't hold, {deeper:N0} through more than one step, {cycles:N0} cycle steps met");

        // ---- The injector paths (J2, J9, J16): a form list of target lists that another form list adds to. Data has no
        // such list (none of the injectors' form lists is named by an AddToList), so bubhorizon.esm's template quest is
        // read with an entries callback that gives one of its target form lists an adder holding one more list.
        if (s.Index.Plugin("bubhorizon.esm") is { Order.Enabled: true })
        {
            var code = new ScriptInjections(s.Archives);
            var qh = s.Winner("000920:bubhorizon.esm") ?? throw new TestFailed("000920:bubhorizon.esm isn't in Data");
            var qjson = qh.Engine.Json(qh.Engine.Load(qh.Ref));
            string targetsList = "000904:bubhorizon.esm", adder = "0017E5:darkstar.esm";
            var game = new VendorIndex(s, new ReadScope(), new[] { (s.Engine, s.Index.Plugin(Made.Game)!) });
            string extra = ReportSession.Canon((FirstHeldList(game) ?? throw new TestFailed("no Starfield.esm vendor's container holds a leveled list")).List);
            string? Sig(string k) => s.Winner(k)?.Info.Signature;
            ListEntries? Faked(string k)
            {
                var real = s.FormListEntries(scope, k);
                if (real == null || !k.Equals(targetsList, StringComparison.OrdinalIgnoreCase)) return real;
                return new ListEntries(real.Entries.Append(extra).ToList(), new[] { adder });
            }
            var plain = ScriptLinks.Into(qjson, "000902:bubhorizon.esm", Sig, name => code.Of(name, "bubhorizon.esm"), ScriptLinks.LeveledItemList, k => s.FormListEntries(scope, k)).Single();
            var faked = ScriptLinks.Into(qjson, "000902:bubhorizon.esm", Sig, name => code.Of(name, "bubhorizon.esm"), ScriptLinks.LeveledItemList, Faked).Single();
            Check(plain.AddedBy is not { Count: > 0 } && !plain.Targets.Contains(extra, StringComparer.OrdinalIgnoreCase) && plain.How!.Contains("AddToList", StringComparison.Ordinal) == false,
                $"bubhorizon.esm: with Data's form lists, AddToList shows up: [{string.Join(", ", plain.Targets)}] {plain.How}");
            Check(faked.FoundBy == "code" && faked.Targets.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(plain.Targets.Append(extra)) && faked.Targets.Count == plain.Targets.Count + 1 && faked.AddedBy is { Count: 1 } ab && ab[0] == adder
                && faked.How!.Contains("with the entries another form list adds to them through AddToList", StringComparison.Ordinal),
                $"bubhorizon.esm with an adder to {targetsList}: [{string.Join(", ", faked.Targets)}] added by [{string.Join(", ", faked.AddedBy ?? Array.Empty<string>())}]: {faked.How}");
            parts.Add($"injector paths: bubhorizon.esm's quest with an adder to {targetsList}: {faked.Targets.Count} target lists (one more, {extra}), \"{faked.How}\"");
        }

        // ---- sources2 on a real plugin whose weapons reach a list through AddToList
        var real = Real.FirstOrDefault(r => s.Index.Plugin(r.Plugin) is { Order.Enabled: true });
        if (real.Plugin == null) parts.Add("no real plugin of the list in the load order: skipped");
        else
        {
            var r = new SourcesReport(s).Sources(real.Plugin, new SourcesOptions { Dependents = false, Item = real.Item, With = real.With });
            ctx.WriteReport("sources-addtolist-real.txt", r.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
            var hops = r.Items.SelectMany(i => i.Sources).Where(z => z.Via.Count >= 2 && Enumerable.Range(1, z.Via.Count - 1).Any(k => string.Equals(mutagenAdds.GetValueOrDefault(z.Via[k - 1]), z.Via[k], StringComparison.OrdinalIgnoreCase))).ToList();
            Check(hops.Count > 0 && hops.All(z => z.ViaLabels.Any(l => l.Contains("'s AddToList)", StringComparison.Ordinal))),
                $"{real.Plugin} {real.Item}: no source through a list's AddToList: {string.Join(" | ", r.Items.SelectMany(i => i.Sources).Select(z => $"{z.Kind}: {z.Text} via {string.Join(" > ", z.ViaLabels)}"))}");
            parts.Add($"{real.Plugin} {real.Item}: {hops.Count} source(s) through AddToList, e.g. {hops[0].Kind}: {(hops[0].Compact ?? hops[0].Text)[..Math.Min(120, (hops[0].Compact ?? hops[0].Text).Length)]} via {string.Join(" > ", hops[0].ViaLabels)}");
        }
        return string.Join("; ", parts);
    }

    /// <summary>A form list of Starfield.esm (HelpManualPC), for a made list's AddToList.</summary>
    const string GameFormList = "000163:Starfield.esm";

    /// <summary>A real plugin whose item sits in a form list that names another in AddToList, and the plugins searched with it.</summary>
    static readonly (string Plugin, string Item, string[] With)[] Real = { ("darkstar.esm", "DarkStar_Mk2_AA99", Array.Empty<string>()) };

    // ---------------------------------------------------------------- Vendors found the way the game finds them
    /// <summary>
    /// GAPS S10: a vendor's chest is the placed container a faction names as its merchant container, whatever its
    /// EditorID. A made plugin holds: a gun in a list in a chest named "ShopChest" that a vendor faction names (with an
    /// NPC in the faction); a gun in a container named "VendorBox" that no faction names (the EditorID fallback); a gun
    /// in a plain loot crate; and a gun added to an override of a Starfield.esm list that a Starfield.esm vendor's chest
    /// holds (nothing in the plugin holds it). Then real mods whose vendor chests have no "Vendor" in their EditorID.
    /// </summary>
    public static string Vendors(Ctx ctx)
    {
        var s = ctx.Session;
        var mod = Made.New("SourcesVendorsTest.esm");
        Weapon Gun(string edid) { var w = new Weapon(mod) { EditorID = edid }; mod.Weapons.Add(w); return w; }
        Mutagen.Bethesda.Starfield.Container Box(string edid, IFormLinkGetter<IItemGetter> item)
        {
            var c = new Mutagen.Bethesda.Starfield.Container(mod) { EditorID = edid, Items = new() { new ContainerEntry { Item = new ContainerItem { Item = item.FormKey.ToLink<IItemGetter>(), Count = 1 } } } };
            mod.Containers.Add(c); return c;
        }
        var gun1 = Gun("SrcVend_ShopGun"); var gun2 = Gun("SrcVend_NamedGun"); var gun3 = Gun("SrcVend_LootGun"); var gun4 = Gun("SrcVend_MasterListGun");
        var list = new LeveledItem(mod) { EditorID = "SrcVend_ShopList", Entries = new() { new LeveledItemEntry { Reference = gun1.ToLink<IItemGetter>(), Level = 1, Count = 1 } } };
        mod.LeveledItems.Add(list);
        var shopChest = Box("SrcVend_ShopChest", list.ToLink<IItemGetter>());
        Box("SrcVend_VendorBox", gun2.ToLink<IItemGetter>());
        Box("SrcVend_LootCrate", gun3.ToLink<IItemGetter>());
        var chestRef = new PlacedObject(mod) { Base = shopChest.ToLink<IPlaceableObjectGetter>() };
        var cell = new Cell(mod) { EditorID = "SrcVend_ShopCell", Flags = Cell.Flag.IsInteriorCell };
        cell.Persistent.Add(chestRef);
        mod.Cells.Records.Add(new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = new() { new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = new() { cell } } } });
        var faction = new Faction(mod) { EditorID = "SrcVend_ShopFaction", Flags = Faction.FactionFlag.Vendor, MerchantContainer = chestRef.ToNullableLink<IPlacedObjectGetter>() };
        mod.Factions.Add(faction);
        var npc = new Npc(mod) { EditorID = "SrcVend_Shopkeeper", Factions = new() { new RankPlacement { Faction = faction.ToLink<IFactionGetter>(), Rank = 0 } } };
        mod.Npcs.Add(npc);

        // A Starfield.esm list a Starfield.esm vendor's chest holds, found from the game's own factions
        var game = new VendorIndex(s, new ReadScope(), new[] { (s.Engine, s.Index.Plugin(Made.Game)!) });
        var (vanillaList, vanillaVendor) = FirstHeldList(game) ?? throw new TestFailed("no Starfield.esm vendor's container holds a leveled list");
        var over = new LeveledItem(Made.Key(vanillaList), StarfieldRelease.Starfield) { EditorID = "SrcVend_VanillaListOverride", Entries = new() { new LeveledItemEntry { Reference = gun4.ToLink<IItemGetter>(), Level = 1, Count = 1 } } };
        mod.LeveledItems.Add(over);
        string path = Made.Write(ctx, mod, "sources-vendors");

        var a = new SourcesReport(s).Sources(path);
        ctx.WriteReport("sources-vendors.txt", a.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
        SourcedItem Item(string edid) => a.Items.FirstOrDefault(i => i.EditorId == edid) ?? throw new TestFailed($"no item {edid} in the answer");
        string Key(IMajorRecordGetter r) => ReportSession.Canon(r.FormKey.ToString());

        var shop = Item("SrcVend_ShopGun").Sources.SingleOrDefault(x => x.Kind == "Vendor");
        Check(shop != null && Item("SrcVend_ShopGun").Sources.All(x => x.Kind != "Container"), $"SrcVend_ShopGun: a chest named \"ShopChest\" that a vendor faction names isn't a vendor: {string.Join(" | ", Item("SrcVend_ShopGun").Sources.Select(x => x.Kind + ": " + x.Text))}");
        Check(shop!.FoundBy == "faction" && shop.Record == Key(shopChest) && shop.Vendors is { Count: 1 } && shop.Vendors[0].Faction == Key(faction) && shop.Vendors[0].Chest == Key(chestRef)
            && shop.Vendors[0].VendorFlag && shop.Vendors[0].Npcs.SequenceEqual(new[] { Key(npc) }) && shop.Reach == Reach.Yes && shop.Via.SequenceEqual(new[] { Key(list) }),
            $"SrcVend_ShopGun: the vendor is {shop.Text} (found by {shop.FoundBy}, via {string.Join(">", shop.Via)}, npcs {string.Join(",", shop.Vendors?.SelectMany(v => v.Npcs) ?? Array.Empty<string>())})");
        Check(shop.Text.StartsWith("SrcVend_ShopFaction", StringComparison.Ordinal) && shop.Text.Contains("(SrcVend_Shopkeeper", StringComparison.Ordinal) && shop.Text.Contains("via chest", StringComparison.Ordinal) && shop.Text.Contains("SrcVend_ShopCell", StringComparison.Ordinal),
            $"SrcVend_ShopGun: the text doesn't say faction (NPC) via chest in cell: {shop.Text}");
        var named = Item("SrcVend_NamedGun").Sources.Single();
        Check(named.Kind == "Vendor" && named.FoundBy == "editorId" && named.Text.Contains("found by its EditorID only", StringComparison.Ordinal), $"SrcVend_NamedGun: {named.Kind} ({named.FoundBy}): {named.Text}");
        var loot = Item("SrcVend_LootGun").Sources.Single();
        Check(loot.Kind == "Container" && loot.FoundBy == null, $"SrcVend_LootGun: {loot.Kind} ({loot.FoundBy}): {loot.Text}");
        var held = Item("SrcVend_MasterListGun").Sources;
        Check(held.Count == 1 && held[0].Kind == "Vendor" && held[0].FoundBy == "faction" && held[0].Record == ReportSession.Canon(vanillaList) && held[0].Vendors!.Any(v => v.Faction == vanillaVendor.Faction)
            && held[0].Via.SequenceEqual(new[] { ReportSession.Canon(vanillaList) }) && held[0].Text.Contains("is in the stock of", StringComparison.Ordinal),
            $"SrcVend_MasterListGun (in an override of {vanillaList}, which {vanillaVendor.Faction}'s chest holds): {string.Join(" | ", held.Select(x => x.Kind + " (" + x.FoundBy + "): " + x.Text))}");
        Check(a.Notes.Any(n => n.StartsWith("Vendors are found the way the game finds them", StringComparison.Ordinal) && n.Contains("Read for vendors:", StringComparison.Ordinal)), "the answer's note doesn't say how vendors are found and what it read");

        // Real mods: a vendor chest named without "Vendor", and one named with it (the old rule was right there)
        var real = new List<string>();
        foreach (var (plugin, item, factionKey, record) in RealVendors)
        {
            if (s.Index.Plugin(plugin) is not { Order.Enabled: true }) { real.Add($"{plugin}: not in the load order, skipped"); continue; }
            var r = new SourcesReport(s).Sources(plugin, new SourcesOptions { Item = item, Dependents = false, Signatures = new[] { "WEAP", "ARMO", "MISC", "ALCH", "BOOK", "AMMO" } });
            var src = r.Items.SelectMany(i => i.Sources).Where(x => x.Kind == "Vendor" && x.FoundBy == "faction" && x.Record == record).ToList();
            Check(src.Count > 0 && src.Any(x => x.Vendors!.Any(v => v.Faction == factionKey)), $"{plugin} {item}: no vendor {factionKey} through {record}: {string.Join(" | ", r.Items.SelectMany(i => i.Sources).Select(x => x.Kind + ": " + x.Text))}");
            Check(r.Items.SelectMany(i => i.Sources).All(x => !(x.Record == record && x.Kind is "Container" or "Unused list")), $"{plugin} {item}: {record} is still shown as a container or an unused list");
            real.Add($"{plugin} {item}: {(src[0].Compact ?? src[0].Text)[..Math.Min(160, (src[0].Compact ?? src[0].Text).Length)]}…");
        }
        return $"made plugin: a chest named ShopChest is a vendor by its faction (SrcVend_ShopFaction, NPC SrcVend_Shopkeeper); VendorBox by its EditorID only; LootCrate a container; an override of {vanillaList} is the stock of {held[0].Vendors!.Count} vendor(s), {vanillaVendor.Faction} among them. Real: {string.Join("; ", real)}";
    }

    /// <summary>
    /// Real vendors the old EditorID rule missed, and one it got right. Record is the source's record: the container,
    /// or for a master's list that nothing searched holds, the list.
    /// </summary>
    static readonly (string Plugin, string Item, string Faction, string Record)[] RealVendors =
    {
        // Groat Meat is in a list SFBGS001_MS01_JandarRestContainer holds: a chest SFBGS001_MS01_JandarRestBarFaction
        // names, with no "Vendor" in its EditorID. The old answer said "Container"
        ("ShatteredSpace.esm", "SFBGS001_Food_Meat_Groat", "035261:ShatteredSpace.esm", "035260:ShatteredSpace.esm"),
        // Longwatch is in aa50_longwatch.esm's override of LL_Weapon_Ranged_Allied (269373:Starfield.esm), which
        // Vendor_City_AC_BelleRowlandFaction's container holds: the old answer said "Unused list"
        ("aa50_longwatch.esm", "Longwatch", "1B2637:Starfield.esm", "269373:Starfield.esm"),
        // A chest named "ASE2Vendor_…": the old rule was right, the faction says the same
        ("aseeverbright.esm", "ASE2Clothes_Service_Uniform_01", "0168BB:aseeverbright.esm", "001FA7:aseeverbright.esm"),
    };

    // ---------------------------------------------------------------- The plugin's patches searched too
    public static string Dependents(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new SourcesReport(s);
        var parts = new List<string>();
        foreach (string plugin in new[] { "atom74.esm", "sfta04.esm" })
        {
            var alone = report.Sources(plugin, new SourcesOptions { Dependents = false });
            var all = report.Sources(plugin);
            var expect = s.Index.Plugins.Where(p => p.Order.Enabled && p.Header != null && p.Header.Masters.Any(m => m.Equals(plugin, StringComparison.OrdinalIgnoreCase))).Select(p => p.Name).ToList();
            Check(all.Searched.Count == expect.Count + 1 && expect.All(e => all.Searched.Contains(e, StringComparer.OrdinalIgnoreCase)), $"{plugin}: searched {string.Join(", ", all.Searched)}; the loaded plugins that master it are {string.Join(", ", expect)}");
            Check(alone.Searched.Count == 1 && alone.Notes.Any(n => n.StartsWith("Only ", StringComparison.Ordinal)), $"{plugin}: the answer for the plugin alone doesn't say that only it was searched");
            int more = 0, lost = 0;
            foreach (var item in all.Items)
            {
                var before = alone.Items.First(i => i.FormKey == item.FormKey);
                var have = item.Sources.Select(x => x.Kind + "|" + x.Record + "|" + string.Join(">", x.Via)).ToHashSet();
                // A list that nothing used in the plugin alone may be used by a patch: that source goes, others come
                lost += before.Sources.Count(x => x.Kind != "Unused list" && !have.Contains(x.Kind + "|" + x.Record + "|" + string.Join(">", x.Via)));
                more += item.Sources.Count(x => !before.Sources.Any(y => y.Kind == x.Kind && y.Record == x.Record && y.Via.SequenceEqual(x.Via)));
            }
            Check(lost == 0, $"{plugin}: {lost} sources found in the plugin alone are gone when its patches are searched too");
            string Verdicts(SourcesAnswer x) => string.Join(", ", x.Items.GroupBy(i => i.Verdict.Split(',')[0].Trim()).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
            parts.Add($"{plugin}: alone {N(alone.Items.Sum(i => i.Sources.Count))} sources for {alone.Items.Count} items ({Verdicts(alone)}; the player can get {alone.Items.Count(i => i.Reach == Reach.Yes)}); with the {expect.Count} loaded plugins that master it ({string.Join(", ", expect.Take(6))}{(expect.Count > 6 ? ", …" : "")}) {N(all.Items.Sum(i => i.Sources.Count))} sources, {more} of them new ({Verdicts(all)}; the player can get {all.Items.Count(i => i.Reach == Reach.Yes)})");
        }
        return string.Join("; ", parts) + ". No source found in the plugin alone is lost.";
    }

    // ---------------------------------------------------------------- Injectors told by their code
    /// <summary>
    /// GAPS S27: a leveled-list injector script is told by what its compiled code does (AddForm on a leveled list held
    /// in a property, with the item held in a property), whatever its name; the older rules (a struct that pairs the item
    /// with a list, a script whose name says it injects) are the fallback, and each source says which way it was found.
    /// The code alone: DefaultAddItemToLeveledList (q59's script) and SFTA03_MiscSupportQuestScript (no injector name).
    /// A made plugin: the game's script on a made quest and a made NPC (by its code), a script that is nowhere (by its
    /// name, by a struct), the game's script holding the list in a property its code doesn't use (by its name, and why).
    /// Real plugins: q59's Aphelion (the vendor, now by the code), the Volk gun's list in sfta03.esm (a plain quest
    /// script property before, an injector now), g36credux.esm when it is loaded (its weapons sold by vendors).
    /// </summary>
    public static string Injectors(Ctx ctx)
    {
        var s = ctx.Session;
        var code = new ScriptInjections(s.Archives);
        static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ---- The code alone
        var dal = code.Of("DefaultAddItemToLeveledList");
        var dalAdd = dal.Injections.FirstOrDefault(i => i.Call == "LeveledItem.AddForm" && i.Items.Any(h => Is(h.Property, "LeveledItemData") && Is(h.Member, "ItemToAdd") && h.Element));
        // LeveledList[I].AddForm(LeveledItemData[k].ItemToAdd, …): each item to each list of the array property LeveledList
        Check(dal.Problem == null && dalAdd != null && dalAdd.Lists.Count == 1 && Is(dalAdd.Lists[0].Property, "LeveledList") && dalAdd.Lists[0].Member == null && dalAdd.Lists[0].Element,
            $"DefaultAddItemToLeveledList ({dal.Where ?? dal.Problem}): no LeveledItem.AddForm of LeveledItemData's ItemToAdd on an element of LeveledList: {string.Join(" | ", dal.Injections.Select(Shown))}");
        var misc = code.Of("SFTA03:SFTA03_MiscSupportQuestScript", "sfta03.esm");
        Check(misc.Problem == null && misc.Injections.Any(i => i.Function == "AddVolkGun" && i.Call == "LeveledItem.AddForm" && i.Items.Any(h => Is(h.Property, "LL_VolkGun") && h.Member == null) && i.Lists.Any(l => Is(l.Property, "LL_Trackers"))),
            $"SFTA03:SFTA03_MiscSupportQuestScript ({misc.Where ?? misc.Problem}): no AddForm of property LL_VolkGun in AddVolkGun: {string.Join(" | ", misc.Injections.Select(Shown))}");
        var none = code.Of("SrcInj_NoSuchLLInjector");
        Check(none.Problem != null && none.Problem.Contains("in no loose file or archive in Data", StringComparison.Ordinal) && none.Injections.Count == 0, $"a script that is nowhere: {none.Problem}");

        // ---- A made plugin
        var mod = Made.New("SourcesInjectorsTest.esm");
        var game = new VendorIndex(s, new ReadScope(), new[] { (s.Engine, s.Index.Plugin(Made.Game)!) });
        var (vendorList, vendor) = FirstHeldList(game) ?? throw new TestFailed("no Starfield.esm vendor's container holds a leveled list");
        var vendorLink = new FormLink<IStarfieldMajorRecordGetter>(Made.Key(vendorList));
        LeveledItem ListOf(string edid)
        {
            var gun = new Weapon(mod) { EditorID = edid };
            mod.Weapons.Add(gun);
            var l = new LeveledItem(mod) { EditorID = edid + "_List", Entries = new() { new LeveledItemEntry { Reference = gun.ToLink<IItemGetter>(), Level = 1, Count = 1 } } };
            mod.LeveledItems.Add(l);
            return l;
        }
        ScriptObjectProperty Obj(string name, IFormLinkGetter<IStarfieldMajorRecordGetter> to) => new() { Name = name, Object = to.FormKey.ToLink<IStarfieldMajorRecordGetter>() };
        ScriptStructListProperty Pairs(string name, string itemMember, LeveledItem item, string? listMember) => new()
        {
            Name = name,
            Structs = new() { new ScriptEntryStructs { Members = listMember == null ? new() { Obj(itemMember, item.ToLink<IStarfieldMajorRecordGetter>()) } : new() { Obj(listMember, vendorLink), Obj(itemMember, item.ToLink<IStarfieldMajorRecordGetter>()) } } },
        };
        // The game's script as the quests that use it set it: the lists, and the items with their levels
        Noggog.ExtendedList<ScriptProperty> Default(LeveledItem item) => new() { new ScriptObjectListProperty { Name = "LeveledList", Objects = new() { Obj("", vendorLink) } }, Pairs("LeveledItemData", "ItemToAdd", item, null) };
        void QuestWith(string edid, ScriptEntry script) => mod.Quests.Add(new Quest(mod) { EditorID = edid, VirtualMachineAdapter = new QuestAdapter { Scripts = new() { script } } });
        var byCode = ListOf("SrcInj_ByCodeGun");
        QuestWith("SrcInj_ByCodeQuest", new ScriptEntry { Name = "DefaultAddItemToLeveledList", Properties = Default(byCode) });
        var byName = ListOf("SrcInj_ByNameGun");
        QuestWith("SrcInj_ByNameQuest", new ScriptEntry { Name = "SrcInj_NoSuchLLInjector", Properties = new() { Obj("ItemList", byName.ToLink<IStarfieldMajorRecordGetter>()), Obj("Target", vendorLink) } });
        var byStruct = ListOf("SrcInj_ByStructGun");
        QuestWith("SrcInj_ByStructQuest", new ScriptEntry { Name = "SrcInj_NoSuchPlain", Properties = new() { Pairs("Pairs", "Item", byStruct, "List") } });
        var unused = ListOf("SrcInj_UnusedPropertyGun");
        QuestWith("SrcInj_UnusedPropertyQuest", new ScriptEntry { Name = "DefaultAddItemToLeveledList", Properties = new() { Obj("SomethingElse", unused.ToLink<IStarfieldMajorRecordGetter>()), Obj("OtherList", vendorLink) } });
        var onNpc = ListOf("SrcInj_OnNpcGun");
        mod.Npcs.Add(new Npc(mod) { EditorID = "SrcInj_InjectingNpc", VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = new() { new ScriptEntry { Name = "DefaultAddItemToLeveledList", Properties = Default(onNpc) } } } });
        string path = Made.Write(ctx, mod, "sources-injectors");

        var a = new SourcesReport(s).Sources(path);
        ctx.WriteReport("sources-injectors.txt", a.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
        ItemSource One(string edid, string foundBy)
        {
            var item = a.Items.FirstOrDefault(i => i.EditorId == edid) ?? throw new TestFailed($"no item {edid} in the answer");
            var src = item.Sources.FirstOrDefault(x => x.InjectorFoundBy == foundBy);
            Check(src != null && src.Kind == "Vendor" && src.Targets!.Contains(ReportSession.Canon(vendorList), StringComparer.OrdinalIgnoreCase) && src.Vendors!.Any(v => v.Faction == vendor.Faction),
                $"{edid}: no vendor by {foundBy} through {vendorList}: {string.Join(" | ", item.Sources.Select(x => $"{x.Kind} ({x.InjectorFoundBy}): {x.Text}"))}");
            return src!;
        }
        var c1 = One("SrcInj_ByCodeGun", "code");
        Check(c1.Text.Contains("script DefaultAddItemToLeveledList adds it to", StringComparison.Ordinal) && c1.Text.Contains("(found in its code: LeveledItem.AddForm in ", StringComparison.Ordinal), $"by its code, the text: {c1.Text}");
        var c2 = One("SrcInj_ByNameGun", "name");
        Check(c2.Text.Contains("(found by the script's name (its compiled script scripts\\SrcInj_NoSuchLLInjector.pex is in no loose file or archive in Data))", StringComparison.Ordinal), $"by its name, the text: {c2.Text}");
        var c3 = One("SrcInj_ByStructGun", "struct");
        Check(c3.Text.Contains("(found by a struct that pairs it with the list (its compiled script scripts\\SrcInj_NoSuchPlain.pex is in no loose file", StringComparison.Ordinal), $"by a struct, the text: {c3.Text}");
        var c4 = One("SrcInj_UnusedPropertyGun", "name");
        Check(c4.Text.Contains("(found by the script's name (its code adds nothing held in SomethingElse to a list))", StringComparison.Ordinal), $"by its name although the code was read, the text: {c4.Text}");
        var c5 = One("SrcInj_OnNpcGun", "code");
        Check(c5.Record == ReportSession.Canon(mod.Npcs.First().FormKey.ToString()), $"the NPC's script: {c5.Text}");
        Check(a.Read.Any(r => r.StartsWith("the compiled code of ", StringComparison.Ordinal)) && a.Notes.Any(n => n.StartsWith("Papyrus code is read only to tell an injector", StringComparison.Ordinal)), "the answer doesn't say the code was read, or what for");

        // ---- Real plugins
        var real = new List<string>();
        var aph = new SourcesReport(s).Sources("sfta04.esm", new SourcesOptions { Item = "SFTA04_AphelionLaserRifle" }).Items.SelectMany(i => i.Sources).FirstOrDefault(x => x.Kind == "Vendor" && x.InjectorFoundBy != null);
        Check(aph != null && aph.InjectorFoundBy == "code" && aph.Targets!.Any(t => Is(t, "00E301:SFBGS003.esm")) && aph.Text.Contains("script DefaultAddItemToLeveledList adds it to SFBGS003_LLC_Vendor_TAHQ_TwoShotContainer (00E301:SFBGS003.esm) (found in its code: LeveledItem.AddForm in ", StringComparison.Ordinal),
            $"q59, sfta04.esm SFTA04_AphelionLaserRifle: {aph?.InjectorFoundBy}: {aph?.Text}");
        real.Add($"q59 Aphelion: {aph!.Kind} by {aph.InjectorFoundBy}");
        var volk = new SourcesReport(s).Sources("sfta03.esm", new SourcesOptions { Item = "LL_SFTA03_Astra_VolkGun", Signatures = new[] { "LVLI" }, Dependents = false }).Items.SelectMany(i => i.Sources).ToList();
        var vi = volk.FirstOrDefault(x => x.InjectorFoundBy == "code");
        Check(vi != null && vi.Kind == "Injected" && vi.Targets!.Any(t => Is(t, "2F09A2:Starfield.esm")) && vi.Text.Contains("script SFTA03:SFTA03_MiscSupportQuestScript adds it to", StringComparison.Ordinal) && !volk.Any(x => x.Text.Contains("script property", StringComparison.Ordinal)),
            $"sfta03.esm LL_SFTA03_Astra_VolkGun: {string.Join(" | ", volk.Select(x => $"{x.Kind} ({x.InjectorFoundBy}): {x.Text}"))}");
        real.Add($"sfta03 Volk gun list: {vi!.Kind} by {vi.InjectorFoundBy}");
        if (s.Index.Plugin("g36credux.esm") != null)
        {
            var g = new SourcesReport(s).Sources("g36credux.esm", new SourcesOptions { Item = "G36C_Custom1", Dependents = false }).Items.SelectMany(i => i.Sources).ToList();
            Check(g.Any(x => x.Kind == "Vendor" && x.InjectorFoundBy == "code" && x.Text.Contains("script HcklerKockG36Script adds it to LLC_Vendor_Any_TradeAuthority (08E207:Starfield.esm)", StringComparison.Ordinal)),
                $"g36credux.esm G36C_Custom1: {string.Join(" | ", g.Select(x => $"{x.Kind} ({x.InjectorFoundBy}): {x.Text}"))}");
            real.Add("g36credux G36C_Custom1: Vendor by code");
        }
        else real.Add("g36credux.esm not in Data, skipped");
        // An injector that adds each entry of a form list held in a property (FormList.GetAt): the record links the form list
        if (s.Index.Plugin("ClothingPack_Standalone_Z.esm") != null)
        {
            var cp = new SourcesReport(s).Sources("ClothingPack_Standalone_Z.esm", new SourcesOptions { Dependents = false, Signatures = new[] { "ARMO" } }).Items.SelectMany(i => i.Sources).Where(x => x.Text.Contains(" script Conner_Template_LLInjectorScript ", StringComparison.Ordinal)).ToList();
            Check(cp.Count > 0 && cp.All(x => x.InjectorFoundBy == "code" && x.Text.Contains("(found in its code: LeveledItem.AddForm in InjectLists, for each entry of the form list in Conner_TemplateFormlist)", StringComparison.Ordinal)),
                $"ClothingPack_Standalone_Z.esm Conner_Template_LLInjectorScript: {string.Join(" | ", cp.Take(3).Select(x => $"{x.Kind} ({x.InjectorFoundBy}): {x.Text}"))}");
            real.Add($"ClothingPack_Standalone_Z Conner_Template_LLInjectorScript: {cp.Count} sources by the code (form list entries)");
        }
        else real.Add("ClothingPack_Standalone_Z.esm not in Data, skipped");
        return $"code: DefaultAddItemToLeveledList ({dal.Where}) {Shown(dalAdd!)}; SFTA03_MiscSupportQuestScript ({misc.Where}) {misc.Injections.Count} AddForm calls. Made: by code (quest and NPC), by name, by struct, by name with the code read. Real: {string.Join("; ", real)}";
    }

    // ---------------------------------------------------------------- Injectors whose lists are form lists' entries
    /// <summary>The template's OnQuestInit, as its compiled code reads (decompiled): which item form list each call of
    /// ProcessItemList pairs with which form list of target lists.</summary>
    static readonly (string Script, (string Items, string Lists)[] Pairs)[] Templates =
    {
        ("_TMP_InjectionQuestScript_20", new[]
        {
            ("_TMP_MyNewArmorList", "_TMP_ListOfArmorLeveledLists"), ("_TMP_MyNewArmorList", "_TMP_ListOfArmorVendorLists"),
            ("_TMP_MyNewWeaponList", "_TMP_ListOfWeaponLeveledLists"), ("_TMP_MyNewWeaponList", "_TMP_ListOfWeaponVendorLists"),
            ("_TMP_MyNewFoodAidList", "_TMP_ListOfFoodAidLeveledLists"), ("_TMP_MyNewFoodAidList", "_TMP_ListOfFoodAidVendorLists"),
            ("_TMP_MyNewOtherItemList", "_TMP_ListOfOtherItemLeveledLists"), ("_TMP_MyNewOtherItemList", "_TMP_ListOfOtherItemsVendorLists"),
        }),
        ("_TMP_InjectionQuestScript_30", new[] { "ARMORS", "APPAREL", "HELMS", "PACKS", "WEAPONS", "FOODAID", "OTHERITEMS" }
            .SelectMany(k => new[] { ($"_TMP_MyNew{k}List", $"_TMP_ListOf{k}LeveledLists"), ($"_TMP_MyNew{k}List", $"_TMP_ListOf{k}VendorLists") }).ToArray()),
    };

    /// <summary>
    /// GAPS S53: an injector whose items and target lists are the entries of form lists held in properties, passed through
    /// a function's parameters (ProcessItemList(items, lists, …) → InjectThisItem(item, lists, …) →
    /// lists.GetAt(i).AddForm(item, …)), paired per call: the template _TMP_InjectionQuestScript_20/_30 of the bub*.esm
    /// plugins and pxc_aa_pdw27_redux.esm, and PXC:FormInjection_PX2 (an array of structs pairing an item form list with a
    /// form list of target lists). On every quest of those plugins that attaches one of these scripts, for each item form
    /// list it holds: the lists ScriptLinks names (what refs and sources use) are exactly the lists in the paired form
    /// lists, read through Mutagen on each record as the game uses it; and every source sources2 gives through such a
    /// quest names those lists, found in the code. Then made scripts: an AddForm in another script's function.
    /// </summary>
    public static string FormListInjectors(Ctx ctx)
    {
        var s = ctx.Session;
        var code = new ScriptInjections(s.Archives);
        var scope = new ReadScope();
        var parts = new List<string>();
        object Winner(string formKey) { var h = s.Winner(formKey) ?? throw new TestFailed($"{formKey} isn't in Data"); return h.Engine.Load(h.Ref).Record; }
        static string Key(FormKey k) => ReportSession.Canon(k.ToString());
        string? Sig(string formKey) => s.Winner(formKey)?.Info.Signature;
        // A form list's entries as Mutagen gives them on the version the game uses
        List<string> Entries(string formList) => ((IFormListGetter)Winner(formList)).Items.Where(l => !l.IsNull).Select(l => Key(l.FormKey)).ToList();
        HashSet<string> ListsIn(IEnumerable<string> formLists, Func<string?, bool> isList) =>
            formLists.SelectMany(Entries).Where(e => isList(Sig(e))).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string plugin in new[] { "bubhorizon.esm", "bubleveract.esm", "bublumi.esm", "bubrailgun.esm", "bubrevoelite.esm", "bubvaruunrifles.esm", "pxc_aa_pdw27_redux.esm" })
        {
            var p = s.Index.Plugin(plugin);
            if (p?.Segment == null) { parts.Add($"{plugin} not in Data, skipped"); continue; }
            int quests = 0;
            // The plugin's quests that attach one of the scripts, as the game uses them
            for (int i = p.Segment.FirstOwn; i < p.Segment.FirstUnresolved; i++)
            {
                if (p.Segment.SignatureOf(p.Segment[i]) != "QUST") continue;
                string questKey = ReportSession.Canon(s.Index.Describe(new RecordRef(p.Id, i)).FormKey);
                if (Winner(questKey) is not IQuestGetter quest || quest.VirtualMachineAdapter == null) continue;
                // item form list → the form lists of target lists the code pairs it with
                var expected = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var script in quest.VirtualMachineAdapter.Scripts)
                {
                    var props = script.Properties.ToList();
                    string? Flst(string name) => props.OfType<IScriptObjectPropertyGetter>().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } o && !o.Object.IsNull ? Key(o.Object.FormKey) : null;
                    void Pair(string? items, string? to) { if (items == null || to == null) return; (expected.TryGetValue(items, out var l) ? l : expected[items] = new()).Add(to); }
                    var template = Templates.FirstOrDefault(t => t.Script.Equals(script.Name, StringComparison.OrdinalIgnoreCase));
                    if (template.Pairs != null)
                        foreach (var (items, to) in template.Pairs) Pair(Flst(items), Flst(to));
                    else if (script.Name.Equals("PXC:FormInjection_PX2", StringComparison.OrdinalIgnoreCase)
                        && props.OfType<IScriptStructListPropertyGetter>().FirstOrDefault(x => x.Name.Equals("InjectionList", StringComparison.OrdinalIgnoreCase)) is { } array)
                        foreach (var element in array.Structs)
                        {
                            string? Member(string name) => element.Members.OfType<IScriptObjectPropertyGetter>().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } m && !m.Object.IsNull ? Key(m.Object.FormKey) : null;
                            Pair(Member("InjectFromList"), Member("InjectToList"));
                        }
                }
                if (expected.Count == 0) continue;
                quests++;
                var h = s.Winner(questKey)!.Value;
                var json = h.Engine.Json(h.Engine.Load(h.Ref));
                int lists = 0;
                foreach (var (items, to) in expected)
                {
                    // refs' words (any list type) and sources' (leveled item lists): exactly the lists in the paired form lists
                    foreach (var (isList, what) in new (Func<string?, bool>, string)[] { (ScriptLinks.AnyList, "refs"), (ScriptLinks.LeveledItemList, "sources") })
                    {
                        var want = ListsIn(to, isList);
                        var found = ScriptLinks.Into(json, items, Sig, name => code.Of(name, plugin), isList, k => s.FormListEntries(scope, k));
                        Check(found.Count > 0, $"{plugin} {questKey}: no script property holds {items}");
                        // Form lists that hold no list: the code's answer is no list (not the older rules' guess)
                        foreach (var f in found)
                            Check(f.FoundBy == "code" && f.Entries && f.Targets.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(want) && (want.Count > 0 || f.How!.EndsWith("hold no list)", StringComparison.Ordinal) || f.How.EndsWith("holds no list)", StringComparison.Ordinal)),
                                $"{plugin} {questKey} {f.Script}.{f.Property}{(f.Member != null ? "." + f.Member : "")} ({what}): found by {f.FoundBy} ({f.How}): [{string.Join(", ", f.Targets)}]; Mutagen: the form lists {string.Join(", ", to)} hold [{string.Join(", ", want)}]");
                        if (what == "sources") lists += want.Count;
                    }
                }
                // sources2: every source through this quest is the code's, and names the lists of the item form list it comes through
                var answer = new SourcesReport(s).Sources(plugin, new SourcesOptions { Dependents = false, Signatures = new[] { "WEAP", "ARMO", "ALCH", "MISC", "BOOK", "AMMO" } });
                ctx.WriteReport($"sources-formlists-{plugin}.txt", answer.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
                // (the quest's other scripts' properties that hold an item aren't these injectors': passed over)
                var through = answer.Items.SelectMany(it => it.Sources.Select(x => (Item: it, Source: x))).Where(x => x.Source.Record == questKey && x.Source.Via.Any(expected.ContainsKey)).ToList();
                Check(through.Count > 0, $"{plugin}: no source through {questKey}");
                foreach (var (it, src) in through)
                {
                    string? viaList = src.Via.LastOrDefault(expected.ContainsKey);
                    var want = viaList == null ? null : ListsIn(expected[viaList], ScriptLinks.LeveledItemList);
                    // An item form list paired only with form lists that hold no list is added to nothing: no injector source
                    Check(want != null && (want.Count == 0 ? src.InjectorFoundBy == null : src.InjectorFoundBy == "code" && src.Targets!.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(want)),
                        $"{plugin} {it.Label}: {src.Kind} ({src.InjectorFoundBy}) via {string.Join(" > ", src.Via)}: {src.Text}; Mutagen: [{string.Join(", ", want ?? new())}]");
                }
                parts.Add($"{plugin} {questKey}: {expected.Count} item form lists, {lists} target lists, {through.Count} sources through it");
            }
            Check(quests > 0, $"{plugin}: no quest attaches the template or PXC:FormInjection_PX2");
        }

        // ---- An AddForm in another script's function: Caller passes its properties to Lib.Inject(akItem, akList)
        string data = ctx.Folder("sources-formlists-lib");
        var I = PexValue.Identifier;
        PexValue Id(string n) => new(I, n, 0, 0, false);
        PexValue One() => new(PexValue.Integer, null, 1, 0, false);
        var lib = new ScriptsTests.MadeScript("SrcLib:Lib", "ScriptObject");
        lib.Function("Inject", "none", new[] { ("akItem", "Form"), ("akList", "LeveledItem") }, new[] { ("::nonevar", "none") },
            (PexFile.OpCallMethod, new[] { Id("AddForm"), Id("akList"), Id("::nonevar") }, new[] { Id("akItem"), One(), One() }));
        var caller = new ScriptsTests.MadeScript("SrcLib:Caller", "Quest");
        caller.Property("Item", "Form", "::Item_var");
        caller.Property("Target", "LeveledItem", "::Target_var");
        caller.Property("Other", "LeveledItem", "::Other_var");
        caller.Function("OnQuestInit", "none", Array.Empty<(string, string)>(), new[] { ("::nonevar", "none") },
            (PexFile.OpCallStatic, new[] { Id("SrcLib:Lib"), Id("Inject"), Id("::nonevar") }, new[] { Id("::Item_var"), Id("::Target_var") }));
        Directory.CreateDirectory(Path.Combine(data, "Scripts", "SrcLib"));
        File.WriteAllBytes(Path.Combine(data, "Scripts", "SrcLib", "Lib.pex"), lib.Bytes());
        File.WriteAllBytes(Path.Combine(data, "Scripts", "SrcLib", "Caller.pex"), caller.Bytes());
        var libCode = ScriptInjections.For(data).Of("SrcLib:Caller");
        Check(libCode.Problem == null && libCode.Injections.Count == 1 && libCode.Injections[0] is { Function: "Lib.Inject", Call: "LeveledItem.AddForm" } li
            && li.Lists.Count == 1 && li.Lists[0].Property == "Target" && li.Items.Count == 1 && li.Items[0].Property == "Item",
            $"SrcLib:Caller ({libCode.Where ?? libCode.Problem}): {string.Join(" | ", libCode.Injections.Select(i => $"{i.Call} in {i.Function}: {string.Join(",", i.Lists.Select(l => l.Property))} <- {string.Join(",", i.Items.Select(l => l.Property))}"))}");
        parts.Add("made scripts: Caller's OnQuestInit passes its properties Item and Target to Lib.Inject, whose AddForm is Caller's injection (LeveledItem.AddForm in Lib.Inject: Target <- Item; Other isn't taken)");
        return string.Join("; ", parts);
    }

    // ---------------------------------------------------------------- Speed: only what the walk asks for (GAPS S70)
    /// <summary>
    /// GAPS S70: "what points at this" comes from a byte search for each record the walk asks about, and only the
    /// records that hold its FormID are read through Mutagen (<see cref="ByteBackLinks"/>, the default); the vendors are
    /// told from the factions' and chests' bytes first, and Mutagen reads only the ones a container needs. Each call is
    /// asked twice: with the scan of every record (<see cref="ScanBackLinks"/>, what sources2 --scan uses) and with the
    /// default. The items' answers must be the same, line for line and in JSON; the default must read a small part of
    /// what the scan reads, and DarkStar_Mk2_AA99's container (no vendor's) must cost no read of a faction through Mutagen.
    /// </summary>
    public static string Speed(Ctx ctx)
    {
        var s = ctx.Session;
        var parts = new List<string>();
        var calls = new (string Name, string Plugin, string? Item)[]
        {
            ("aa99", "darkstar.esm", "DarkStar_Mk2_AA99"),
            ("ccatom74", "atom74.esm", "ccAtom74"),
            ("atom74", "atom74.esm", null),
        };
        static string Items(SourcesAnswer a) => string.Join("\n", a.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }).Split('\n')
            .Where(l => !l.StartsWith("[", StringComparison.Ordinal)));
        static string ItemsJson(SourcesAnswer a) => string.Join("\n", a.Items.Select(i => i.ToJson().ToJsonString()));
        static long Number(string text, string pattern) => Regex.Match(text, pattern) is { Success: true } m ? long.Parse(m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
        foreach (var (name, plugin, item) in calls)
        {
            if (s.Index.Plugin(plugin) is not { Order.Enabled: true }) { parts.Add($"{plugin} isn't in the load order: skipped"); continue; }
            var report = new SourcesReport(s);
            // The byte search first, so it doesn't find the records the scan read already read
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // The scan's limit on both sides: the scan leaves out a plugin of over 300,000 placed references (it reads every
            // record through Mutagen); the byte search's default searches it too (GAPS S100), checked on Eon below
            var bytes = report.Sources(plugin, new SourcesOptions { Item = item, BackLinks = (scope, searched) => new ByteBackLinks(s, scope, searched) { PlacedLimit = 300_000 } });
            double byteSeconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            var scan = report.Sources(plugin, new SourcesOptions { Item = item, BackLinks = (scope, searched) => new ScanBackLinks(s, scope, searched) });
            double scanSeconds = clock.Elapsed.TotalSeconds;
            ctx.WriteReport($"sources-speed-{name}-scan.txt", scan.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
            ctx.WriteReport($"sources-speed-{name}-bytes.txt", bytes.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
            Check(Items(scan) == Items(bytes), $"{name}: the answer by the byte search differs from the scan's (sources-speed-{name}-*.txt)");
            Check(ItemsJson(scan) == ItemsJson(bytes), $"{name}: the JSON of the items by the byte search differs from the scan's");
            string scanCost = string.Join("; ", scan.Read), byteCost = string.Join("; ", bytes.Read);
            long scanRead = Number(scanCost, @"([\d,]+) records of \d+ plugins? for their links");
            long byteRead = Number(byteCost, @"([\d,]+) of them read for their links");
            Check(scanRead > 0 && byteRead >= 0 && byteRead * 20 <= scanRead, $"{name}: the byte search read {N(byteRead)} records through Mutagen, the scan {N(scanRead)}: {byteCost}");
            string vendorNote = bytes.Notes.FirstOrDefault(n => n.StartsWith("Vendors are found", StringComparison.Ordinal)) ?? "";
            long factionsRead = Number(vendorNote, @"; ([\d,]+) factions and [\d,]+ chests read through Mutagen");
            if (name == "aa99")
                Check(factionsRead == 0, $"aa99: its container is no vendor's, yet {N(factionsRead)} factions were read through Mutagen: {vendorNote}");
            parts.Add($"{name}: same answer; scan {scanSeconds:F1} s ({N(scanRead)} records through Mutagen), bytes {byteSeconds:F1} s ({N(byteRead)} records; {N(factionsRead)} factions read for vendors)");
        }
        // GAPS S100 (J79): by default the byte search takes in a plugin of over 300,000 placed references: Eon
        // (0476C4:Starfield.esm) in Starfield.esm alone is placed there, and none of its notes says a plugin was left out
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var eon = new SourcesReport(s).Sources("Starfield.esm", new SourcesOptions { Item = "0476C4:Starfield.esm", Dependents = false });
            var placed = eon.Items.SelectMany(i => i.Sources).Where(x => x.Kind == "Placed").ToList();
            Check(placed.Count > 0 && placed.Any(x => x.Record != null && x.Record.EndsWith(":Starfield.esm", StringComparison.OrdinalIgnoreCase)), $"Eon: no placed copy in Starfield.esm by default ({placed.Count} placed sources): {string.Join(" / ", eon.Notes)}");
            Check(!eon.Notes.Any(n => n.Contains("weren't searched for placed copies", StringComparison.Ordinal)), $"Eon: a plugin was left out by default: {string.Join(" / ", eon.Notes)}");
            parts.Add($"Eon in Starfield.esm by default: {placed.Count} placed sources, {placed.Sum(x => Math.Max(1, x.Count)):N0} placed copies ({clock.Elapsed.TotalSeconds:F1} s; {string.Join("; ", eon.Read)})");
        }
        return string.Join("; ", parts);
    }

    /// <summary>
    /// The injector check reads a script's code only when it can matter (GAPS S83). (1) The rule it stands on, held
    /// against the whole reading on every compiled script in Data's archives: each AddForm call the code reading finds
    /// is one the instructions alone show (<see cref="ScriptLinks.Shape"/>), and every script it follows a value into
    /// (<see cref="ScriptLinks.CalledScripts(PexFile)"/>) is among the scripts the instructions call. (2) The scripts on
    /// Starfield.esm's records that hold Eon (0476C4:Starfield.esm), their parents, and the injectors of the other tests,
    /// each looked up as sources does (<see cref="ScriptInjections.Of"/>), give the same injections as the whole reading
    /// of the script, its parents and the scripts they call, as it was before. (3) sources2 of Eon: its script code takes
    /// under 1 s (4.0 s before, on a quiet PC).
    /// </summary>
    public static string InjectorSpeed(Ctx ctx)
    {
        var s = ctx.Session;
        // ---- (3) Eon in sources2, first: no script read yet in this process
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var eon = new SourcesReport(s).Sources(Made.Game, new SourcesOptions { Item = "0476C4:Starfield.esm", Dependents = false });
        string cost = string.Join("; ", eon.Read);
        var m = Regex.Match(cost, @"the compiled code of ([\d,]+) scripts? on records that hold an item in a property, for AddForm calls \(([\d,]+) read from their files, ([\d,]+) ms\)");
        Check(m.Success, $"Eon: no script code in the cost line: {cost}");
        long ms = long.Parse(m.Groups[3].Value.Replace(",", ""), CultureInfo.InvariantCulture);
        Check(ms < 1000, $"Eon: the script code took {N(ms)} ms (4,000 before S83): {m.Value}");
        string eonPart = $"Eon (sources2 Starfield.esm --only, {clock.Elapsed.TotalSeconds:F1} s, first in the process): the code of {m.Groups[1].Value} scripts in {N(ms)} ms ({m.Groups[2].Value} read from their files)";

        // ---- (1) the rule, on every compiled script of every archive that holds one
        int scripts = 0, addForms = 0, follows = 0, unreadable = 0;
        clock.Restart();
        foreach (var archive in s.Archives.AllInData().Where(a => s.Archives.Names(a).Scripts > 0))
            foreach (var f in s.Archives.Open(archive).Files.Where(f => f.Extension == ".pex"))
            {
                PexFile file;
                try { file = PexFile.Read(f.Bytes()); }
                catch (Exception e) when (e is InvalidDataException or IOException) { unreadable++; continue; }
                scripts++;
                var shape = ScriptLinks.Shape(file);
                var result = new ScriptCodeResult();
                ScriptCode.Read(file, "", result);
                bool calls = result.Calls.Any(c => c.Kind == "give" && c.Call.Equals("AddForm", StringComparison.OrdinalIgnoreCase));
                if (calls) addForms++;
                Check(!calls || shape.AddForm, $"{Path.GetFileName(archive)} {f.Path}: the code reading finds an AddForm call the instructions don't show");
                var followed = ScriptLinks.CalledScripts(file, result.Sites);
                follows += followed.Count;
                var missing = followed.Where(c => !shape.Calls.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                Check(missing.Count == 0, $"{Path.GetFileName(archive)} {f.Path}: the code reading follows values into {string.Join(", ", missing)}, which the instructions don't call");
            }
        Check(scripts > 4000 && addForms > 0, $"{scripts} compiled scripts, {addForms} with an AddForm call");
        double ruleSeconds = clock.Elapsed.TotalSeconds;

        // ---- (2) the lookups against the whole reading
        var archives = s.Archives.AllInData();
        byte[]? BytesOf(string script, string plugin)
        {
            string rel = ScriptsReport.PathOfScript(script);
            string loose = Path.Combine(ctx.Data, rel);
            if (File.Exists(loose)) return File.ReadAllBytes(loose);
            string stem = Path.GetFileNameWithoutExtension(plugin) + " - ";
            int Rank(string a)
            {
                string n = Path.GetFileName(a);
                int r = n.StartsWith(stem, StringComparison.OrdinalIgnoreCase) ? 0 : n.StartsWith("Starfield - ", StringComparison.OrdinalIgnoreCase) ? 2 : 4;
                return n.Contains(" - Main", StringComparison.OrdinalIgnoreCase) || n.Contains(" - Misc", StringComparison.OrdinalIgnoreCase) ? r - 1 : r;
            }
            foreach (var a in archives.OrderBy(Rank))
                if (s.Archives.Names(a).Scripts > 0 && s.Archives.Names(a).Has(rel)) return s.Archives.Open(a).Find(rel)?.Bytes();
            return null;
        }
        // As ScriptInjections read a script before S83: its code and every script it follows a value into, up to 3
        // calls deep, each read whole and the script's code read again with them; then the same for its parent, up to 8 deep
        (List<string> Injections, List<string> Parents)? Whole(string script, string plugin)
        {
            var injections = new List<string>();
            var parents = new List<string>();
            string current = script;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int depth = 0; depth < 8 && current.Length > 0 && seen.Add(current); depth++)
            {
                if (BytesOf(current, plugin) is not { } bytes) { if (depth == 0) return null; break; }
                var file = PexFile.Read(bytes);
                var calledFiles = new List<PexFile>();
                var followed = new HashSet<string>(file.Objects.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
                var next = ScriptLinks.CalledScripts(file);
                for (int d = 0; d < 3 && next.Count > 0; d++)
                {
                    var more = new List<string>();
                    foreach (var name in next)
                    {
                        if (!followed.Add(name) || BytesOf(name, plugin) is not { } cb) continue;
                        try { var cf = PexFile.Read(cb); calledFiles.Add(cf); more.AddRange(ScriptLinks.CalledScripts(cf)); }
                        catch (InvalidDataException) { }
                    }
                    next = more;
                }
                var by = ScriptLinks.Injections(file, calledFiles);
                var mine = by.TryGetValue(current, out var x) ? x : by.Values.FirstOrDefault();
                injections.AddRange((mine.Injections ?? new List<Injection>()).Select(Shown));
                current = mine.Parent ?? "";
                if (current.Length > 0) parents.Add(current);
            }
            return (injections, parents);
        }
        // Eon's scripts (the 24 on the records that hold it, and their parents), the game's and mods' injectors
        var cases = new (string Script, string Plugin)[]
        {
            ("Fragments:Quests:QF_MQ101_00003448", "Starfield.esm"), ("Fragments:Quests:QF_MQ103_000114C9", "Starfield.esm"),
            ("Fragments:Quests:QF_UC02_002B1808", "Starfield.esm"), ("Fragments:Quests:QF_UC03_0029D04C", "Starfield.esm"),
            ("Fragments:Quests:QF_UC04_002AAE8D", "Starfield.esm"), ("Fragments:Quests:QF_UC07_00215E93", "Starfield.esm"),
            ("Fragments:Quests:QF_UC08_002062C3", "Starfield.esm"), ("Fragments:Quests:QF_BE_CF02_Ragana_BoardingQu_0010CB89", "Starfield.esm"),
            ("Fragments:Quests:QF_OE_KT_Execution_00082C9A", "Starfield.esm"), ("Fragments:Quests:QF_OE_KT_FalseContact_0007E715", "Starfield.esm"),
            ("Fragments:Quests:QF_OE_KT_RepoMen_0007D4A7", "Starfield.esm"), ("Fragments:Quests:QF_OE_MP_AcelesDropoff_000478A2", "Starfield.esm"),
            ("Fragments:Quests:QF_FFCydoniaR02_00224FE8", "Starfield.esm"), ("Fragments:Quests:QF_FFRedMileR01_002DFEDB", "Starfield.esm"),
            ("FFCydoniaR02QuestScript", "Starfield.esm"), ("DefaultAliasParent", "Starfield.esm"), ("DefaultAliasOnItemAddedScript", "Starfield.esm"),
            ("REScript", "Starfield.esm"), ("oescript", "Starfield.esm"), ("RL082Spawner", "Starfield.esm"), ("SQ_TreasureMapScript", "Starfield.esm"),
            ("sq_groupoccupationscript", "Starfield.esm"), ("ReferenceAlias", "Starfield.esm"), ("ObjectReference", "Starfield.esm"),
            ("DefaultAddItemToLeveledList", "Starfield.esm"), ("SFTA03:SFTA03_MiscSupportQuestScript", "sfta03.esm"),
            ("_TMP_InjectionQuestScript_20", "bubhorizon.esm"), ("_TMP_InjectionQuestScript_30", "bubrevoelite.esm"),
            ("PXC:FormInjection_PX2", "pxc_aa_pdw27_redux.esm"), ("HcklerKockG36Script", "g36credux.esm"),
            ("Fragments:Quests:QF_ccAtom74_LLInjection_010008DF", "atom74.esm"),
        };
        int compared = 0, injecting = 0;
        var lookups = new ScriptInjections(s.Archives);
        foreach (var (script, plugin) in cases)
        {
            if (s.Index.Plugin(plugin) is not { Order.Enabled: true }) continue;
            var whole = Whole(script, plugin);
            var lookup = lookups.Of(script, plugin);
            if (lookup.Problem != null) { Check(whole == null, $"{script}: {lookup.Problem}, yet the whole reading found it"); continue; }
            Check(whole != null, $"{script} ({plugin}): the lookup found it ({lookup.Where}), the whole reading didn't");
            string looked = string.Join("\n", lookup.Injections.Select(Shown)), read = string.Join("\n", whole!.Value.Injections);
            Check(looked == read, $"{script} ({plugin}): the lookup gives\n{looked}\nthe whole reading\n{read}");
            var parents = new List<string>();
            for (string? p = lookups.ParentOf(script, plugin); p is { Length: > 0 } && parents.Count < 8; p = lookups.ParentOf(p, plugin)) parents.Add(p);
            Check(parents.SequenceEqual(whole.Value.Parents, StringComparer.OrdinalIgnoreCase),
                $"{script}: its parents are {string.Join(" > ", parents)} by the lookup, {string.Join(" > ", whole.Value.Parents)} by the whole reading");
            compared++;
            if (lookup.Injections.Count > 0) injecting++;
        }
        Check(compared >= 25 && injecting >= 2, $"{compared} scripts compared, {injecting} of them injectors");

        return $"the rule held on {N(scripts)} compiled scripts ({N(addForms)} with AddForm calls, {N(follows)} scripts followed into; {unreadable} unreadable; {ruleSeconds:F1} s); "
            + $"{compared} lookups the same as the whole reading ({injecting} injectors); {eonPart}";
    }

    /// <summary>A leveled list a vendor's container holds directly, and that vendor: the first by the lists' FormKeys.
    /// (Was VendorIndex.FirstHeldList, a test-only member of SFSearch.Reports, until J81; it reads the index's lists through
    /// InternalsVisibleTo.)</summary>
    static (string List, VendorChest Vendor)? FirstHeldList(VendorIndex vendors)
    {
        vendors.BuildLists();
        return vendors._byList!.Where(kv => kv.Value.Any(h => h.Path.Count == 0)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => ((string List, VendorChest Vendor)?)(kv.Key, kv.Value.First(h => h.Path.Count == 0).Vendor)).FirstOrDefault();
    }
}
