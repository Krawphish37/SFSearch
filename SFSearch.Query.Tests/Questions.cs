// The SQL questions with known answers. Each has the SQL an agent would write and the answer worked out through
// Mutagen directly (the overlays the records layer holds open), never from the scratch tables. They are this
// milestone's own (s01…); the author's agent benchmark (a dev tool, not in the public repository) adds those of its
// questions that SQL can answer, through the partial method Benchmark.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using SFSearch.Query;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

sealed class Question
{
    public required string Id { get; init; }
    public required string Ask { get; init; }
    public required string Sql { get; init; }
    public string[] Plugins { get; init; } = Array.Empty<string>();
    public ScopeKind Scope { get; init; } = ScopeKind.Plugins;
    /// <summary>The rows Mutagen gives, each as the text a row of the answer has.</summary>
    public required Func<Ctx, IEnumerable<string>> Truth { get; init; }
    public bool Ordered { get; init; }
    /// <summary>Pieces of the known answer that the rows must hold.</summary>
    public string[] Known { get; init; } = Array.Empty<string>();
}

static partial class Questions
{
    const string SF = "Starfield.esm";

    static IEnumerable<string> One(params object?[] cells) => new[] { Row(cells) };

    /// <summary>EditorID, Mutagen type and name of the records of a plugin with these local IDs, found by walking the plugin.</summary>
    static IEnumerable<string> Named(Ctx c, string plugin, params uint[] ids)
    {
        var mod = c.Mod(plugin);
        var found = mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == mod.ModKey && ids.Contains(r.FormKey.ID)).ToList();
        return found.OrderBy(r => r.FormKey.ID).Select(r => Row(r.EditorID, c.Engine.LabelOf(c.Schema.SignatureOf(Type(r))!), HasName(r) ? P(r, "Name") : null));
    }

    static bool HasName(object r) => r.GetType().GetProperty("Name") != null || r.GetType().GetInterfaces().Any(i => i.GetProperty("Name") != null);

    /// <summary>The same for one record of a big plugin, looked for among the records of its own type only.</summary>
    static IEnumerable<string> NamedTyped<T>(Ctx c, string plugin, string formKey) where T : class, IMajorRecordGetter
    {
        var r = c.Find<T>(plugin, formKey);
        return One(r.EditorID, c.Engine.LabelOf(c.Schema.SignatureOf(Type(r))!), HasName(r) ? P(r, "Name") : null);
    }

    static string FindSql(params string[] formKeys) =>
        $"SELECT EditorID, Type, name(FormKey) FROM Forms WHERE FormKey IN ({string.Join(", ", formKeys.Select(k => $"'{k}'"))}) ORDER BY FormKey";

    static IEnumerable<IFormLinkGetter> Links(object? record, string path) => List(record, path).OfType<IFormLinkGetter>();

    /// <summary>The questions: the agent benchmark's that SQL can answer, when they are in the checkout, then this milestone's own.</summary>
    public static List<Question> All()
    {
        var all = new List<Question>();
        Benchmark(all);
        all.AddRange(Own());
        return all;
    }

    /// <summary>
    /// The questions of the author's agent benchmark (a dev tool, not in the public repository) that SQL can answer,
    /// under their own ids and with their known answers held too. Filled in by the benchmark's own file when it is in
    /// the checkout; without it there are none.
    /// </summary>
    static partial void Benchmark(List<Question> list);

    /// <summary>This milestone's own questions (s01…).</summary>
    static List<Question> Own() => new()
    {
        new() { Id = "s01", Ask = "Which pistols of Starfield.esm do more than 20 damage? (the prototype's query)", Plugins = new[] { SF },
            Sql = "SELECT w.EditorID, w.AttackDamage FROM Weapon w JOIN Weapon_Keywords k ON k.FormKey = w.FormKey WHERE w.AttackDamage > 20 AND k.Value = '04A0A0:Starfield.esm' ORDER BY 2 DESC, 1",
            Truth = c => c.Mod(SF).Weapons.Where(w => (float)P(w, "AttackDamage")! > 20 && w.Keywords != null && w.Keywords.Any(k => k.FormKey == FormKey.Factory("04A0A0:Starfield.esm")))
                .OrderByDescending(w => (float)P(w, "AttackDamage")!).ThenBy(w => w.EditorID, StringComparer.OrdinalIgnoreCase).Select(w => Row(w.EditorID, P(w, "AttackDamage"))),
            Ordered = true, Known = new[] { "MagShot | 53", "Razorback | 51", "UrbanEagle | 39" } },
        new() { Id = "s02", Ask = "How many weapons of Starfield.esm have no scope? (a value the JSON leaves out)", Plugins = new[] { SF },
            Sql = "SELECT count(*) FROM Weapon WHERE HasScope = 0",
            Truth = c => One(c.Mod(SF).Weapons.Count(w => !(bool)P(w, "HasScope")!)), Known = new[] { "393" } },
        new() { Id = "s03", Ask = "The five heaviest armors of Starfield.esm.", Plugins = new[] { SF },
            Sql = "SELECT EditorID, Weight FROM Armor ORDER BY Weight DESC, EditorID LIMIT 5",
            Truth = c => c.Mod(SF).Armors.OrderByDescending(a => (float)P(a, "Weight")!).ThenBy(a => a.EditorID, StringComparer.OrdinalIgnoreCase).Take(5).Select(a => Row(a.EditorID, P(a, "Weight"))), Ordered = true },
        new() { Id = "s04", Ask = "How many NPCs of Starfield.esm are flagged Unique?", Plugins = new[] { SF },
            Sql = "SELECT count(*) FROM Npc_Flags WHERE Value = 'Unique'",
            Truth = c => One(c.Mod(SF).Npcs.Count(n => n.Flags.HasFlag(Npc.Flag.Unique))) },
        new() { Id = "s05", Ask = "The five most used races among Starfield.esm's NPCs.", Plugins = new[] { SF },
            Sql = "SELECT edid(Race), count(*) FROM Npc WHERE Race IS NOT NULL GROUP BY Race ORDER BY 2 DESC, 1 LIMIT 5",
            Truth = c => c.Mod(SF).Npcs.Where(n => !n.Race.IsNull).GroupBy(n => n.Race.FormKey).Select(g => (Name: c.EdidOf<IRaceGetter>(g.Key), N: g.Count()))
                .OrderByDescending(x => x.N).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(5).Select(x => Row(x.Name, x.N)), Ordered = true },
        new() { Id = "s06", Ask = "What is the Eon called in Japanese and in German?", Plugins = new[] { SF },
            Sql = "SELECT text(Json -> '$.Name', 'ja'), text(Json -> '$.Name', 'German'), Name FROM Weapon WHERE EditorID = 'Eon'",
            Truth = c => { var n = c.Mod(SF).Weapons.First(w => w.EditorID == "Eon").Name!; n.TryLookup(Language.Japanese, out var ja); n.TryLookup(Language.German, out var de); n.TryLookup(Language.English, out var en); return One(ja, de, en); },
            Known = new[] { "イーオン | Eon | Eon" } },
        new() { Id = "s07", Ask = "How many recipes of Starfield.esm make more than one item?", Plugins = new[] { SF },
            Sql = "SELECT count(*) FROM ConstructibleObject WHERE AmountProduced > 1",
            Truth = c => One(c.Mod(SF).ConstructibleObjects.Count(r => Convert.ToInt64(P(r, "AmountProduced")) > 1)) },
        new() { Id = "s08", Ask = "Which five ammunitions of Starfield.esm do the most damage?", Plugins = new[] { SF },
            Sql = "SELECT EditorID, Damage FROM Ammunition ORDER BY Damage DESC, EditorID LIMIT 5",
            Truth = c => c.Mod(SF).Ammunitions.OrderByDescending(a => (float)P(a, "Damage")!).ThenBy(a => a.EditorID, StringComparer.OrdinalIgnoreCase).Take(5).Select(a => Row(a.EditorID, P(a, "Damage"))), Ordered = true },
        new() { Id = "s09", Ask = "Which ammunition do the most weapons of Starfield.esm use?", Plugins = new[] { SF },
            Sql = "SELECT edid(AmmoType), count(*) FROM Weapon WHERE AmmoType IS NOT NULL GROUP BY AmmoType ORDER BY 2 DESC, 1 LIMIT 5",
            Truth = c => c.Mod(SF).Weapons.Select(w => (IFormLinkGetter)P(w, "AmmoType")!).Where(l => !l.IsNull).GroupBy(l => l.FormKey).Select(g => (Name: c.EdidOf<IAmmunitionGetter>(g.Key), N: g.Count()))
                .OrderByDescending(x => x.N).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(5).Select(x => Row(x.Name, x.N)), Ordered = true },
        new() { Id = "s10", Ask = "How many game settings of each kind does Starfield.esm have?", Plugins = new[] { SF },
            Sql = "SELECT Type, count(*) FROM GameSetting GROUP BY Type ORDER BY Type",
            Truth = c => c.Mod(SF).GameSettings.GroupBy(Type).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Row(g.Key, g.Count())), Ordered = true },
        new() { Id = "s11", Ask = "The first twenty float game settings of Starfield.esm, by name, with their values.", Plugins = new[] { SF },
            Sql = "SELECT EditorID, Data FROM GameSettingFloat ORDER BY EditorID COLLATE BINARY LIMIT 20",
            Truth = c => c.Mod(SF).GameSettings.OfType<IGameSettingFloatGetter>().OrderBy(g => g.EditorID, StringComparer.Ordinal).Take(20).Select(g => Row(g.EditorID, P(g, "Data"))), Ordered = true },
        new() { Id = "s12", Ask = "How many weapons does the game use, counting each once (the winners of the load order)?", Scope = ScopeKind.Winners,
            Sql = "SELECT count(*), count(DISTINCT FormKey) FROM Weapon",
            Truth = c => { int n = MutagenOrder.WinningWeapons(c).Count; return One(n, n); } },
        new() { Id = "s13", Ask = "Which loaded plugins hold the Eon (0476C4:Starfield.esm), and which wins?", Scope = ScopeKind.Versions,
            Sql = "SELECT Plugin, IsWinner FROM Weapon WHERE FormKey = '0476C4:Starfield.esm' ORDER BY LoadOrder",
            Truth = c => { var key = FormKey.Factory("0476C4:Starfield.esm"); var holders = MutagenOrder.Loaded(c).Where(m => m.Weapons.RecordCache.ContainsKey(key)).ToList(); return holders.Select((m, i) => Row(m.ModKey.FileName.String, i == holders.Count - 1)); },
            Ordered = true },
        new() { Id = "s14", Ask = "How many weapons do kgcdoom.esm and darkstar.esm hold each?", Plugins = new[] { "kgcdoom.esm", "darkstar.esm" },
            Sql = "SELECT Plugin, count(*), sum(IsOverride) FROM Weapon GROUP BY Plugin ORDER BY Plugin",
            Truth = c => new[] { "darkstar.esm", "kgcdoom.esm" }.Select(p => { var m = c.Mod(p); return Row(p, m.Weapons.Count, m.Weapons.Count(w => w.FormKey.ModKey != m.ModKey)); }), Ordered = true },
        new() { Id = "s15", Ask = "How many keyword entries do Starfield.esm's weapons have in all?", Plugins = new[] { SF },
            Sql = "SELECT count(*), count(DISTINCT Value) FROM Weapon_Keywords",
            Truth = c => { var all = c.Mod(SF).Weapons.SelectMany(w => w.Keywords ?? Enumerable.Empty<IFormLinkGetter<IKeywordGetter>>()).ToList(); return One(all.Count, all.Select(k => k.FormKey).Distinct().Count()); } },
        new() { Id = "s16", Ask = "How many entries do Starfield.esm's leveled items have, and how many with a count over 1?", Plugins = new[] { SF },
            Sql = "SELECT count(*), sum(Count > 1) FROM LeveledItem_Entries",
            Truth = c => { var e = c.Mod(SF).LeveledItems.SelectMany(l => List(l, "Entries")).ToList(); return One(e.Count, e.Count(x => Convert.ToInt64(P(x, "Count")) > 1)); } },
        new() { Id = "s17", Ask = "Which five form lists of Starfield.esm are the longest?", Plugins = new[] { SF },
            Sql = "SELECT EditorID, count(*) FROM FormList_Items GROUP BY FormKey ORDER BY 2 DESC, 1 LIMIT 5",
            Truth = c => c.Mod(SF).FormLists.Select(f => (f.EditorID, N: List(f, "Items").Count())).Where(x => x.N > 0).OrderByDescending(x => x.N).ThenBy(x => x.EditorID, StringComparer.OrdinalIgnoreCase).Take(5).Select(x => Row(x.EditorID, x.N)), Ordered = true },
        new() { Id = "s18", Ask = "The five most valuable misc items of Starfield.esm.", Plugins = new[] { SF },
            Sql = "SELECT EditorID, Value, Weight FROM MiscItem ORDER BY Value DESC, EditorID LIMIT 5",
            Truth = c => c.Mod(SF).MiscItems.OrderByDescending(m => Convert.ToInt64(P(m, "Value"))).ThenBy(m => m.EditorID, StringComparer.OrdinalIgnoreCase).Take(5).Select(m => Row(m.EditorID, P(m, "Value"), P(m, "Weight"))), Ordered = true },
        new() { Id = "s19", Ask = "How many weapons does darkstar.esm hold, and how many override a master's?", Plugins = new[] { "darkstar.esm" },
            Sql = "SELECT count(*), sum(IsOverride) FROM Weapon",
            Truth = c => { var m = c.Mod("darkstar.esm"); return One(m.Weapons.Count, m.Weapons.Count(w => w.FormKey.ModKey != m.ModKey)); } },
        new() { Id = "s20", Ask = "How many weapons of Starfield.esm do more than 30 damage? (through a temporary table)", Plugins = new[] { SF },
            Sql = "CREATE TEMP TABLE strong AS SELECT FormKey, AttackDamage FROM Weapon WHERE AttackDamage > 30; CREATE INDEX temp.strong_fk ON strong(FormKey); SELECT count(*), max(AttackDamage) FROM strong",
            Truth = c => { var w = c.Mod(SF).Weapons.Where(x => (float)P(x, "AttackDamage")! > 30).ToList(); return One(w.Count, w.Max(x => (float)P(x, "AttackDamage")!)); } },
        new() { Id = "s21", Ask = "How many links from Starfield.esm's weapons point at WeaponTypePistol (04A0A0:Starfield.esm)?", Plugins = new[] { SF },
            Sql = "SELECT count(*) FROM Links l WHERE l.ToKey = '04A0A0:Starfield.esm' AND l.FromKey IN (SELECT FormKey FROM Weapon)",
            Truth = c => One(c.Mod(SF).Weapons.Sum(w => w.EnumerateFormLinks().Count(l => l.FormKey == FormKey.Factory("04A0A0:Starfield.esm")))) },
        new() { Id = "s22", Ask = "What is the record 0476C4:Starfield.esm called, and what is its EditorID?", Plugins = new[] { SF },
            Sql = "SELECT edid('0476C4:Starfield.esm'), name('0476C4:Starfield.esm')",
            Truth = c => { var w = MutagenOrder.WinningWeapons(c)[FormKey.Factory("0476C4:Starfield.esm")]; return One(w.EditorID, w.Name); } },
        new() { Id = "s23", Ask = "Which bases are placed most often in kgcdoom.esm?", Plugins = new[] { "kgcdoom.esm" },
            Sql = "SELECT Base, count(*) FROM PlacedObject GROUP BY Base ORDER BY 2 DESC, 1 LIMIT 5",
            Truth = c => c.Mod("kgcdoom.esm").EnumerateMajorRecords<IPlacedObjectGetter>().GroupBy(r => r.Base.FormKey.ToString()).Select(g => (g.Key, N: g.Count()))
                .OrderByDescending(x => x.N).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Take(5).Select(x => Row(x.Key, x.N)), Ordered = true },
        new() { Id = "s24", Ask = "How many cells and how many quests does darkstar.esm hold?", Plugins = new[] { "darkstar.esm" },
            Sql = "SELECT (SELECT count(*) FROM Cell), (SELECT count(*) FROM Quest)",
            Truth = c => { var m = c.Mod("darkstar.esm"); return One(m.EnumerateMajorRecords<ICellGetter>().Count(), m.Quests.Count); } },
        new() { Id = "s25", Ask = "How many NPCs of Starfield.esm are essential or protected?", Plugins = new[] { SF },
            Sql = "SELECT count(*) FROM Npc WHERE EXISTS (SELECT 1 FROM json_each(Npc.Flags) f WHERE f.value IN ('Essential', 'Protected'))",
            Truth = c => One(c.Mod(SF).Npcs.Count(n => n.Flags.HasFlag(Npc.Flag.Essential) || n.Flags.HasFlag(Npc.Flag.Protected))) },
        new() { Id = "s26", Ask = "How many perks of Starfield.esm have ranks, and how many ranks in all?", Plugins = new[] { SF },
            Sql = "SELECT count(DISTINCT FormKey), count(*) FROM Perk_Ranks",
            Truth = c => { var p = c.Mod(SF).Perks.Select(x => List(x, "Ranks").Count()).Where(n => n > 0).ToList(); return One(p.Count, p.Sum()); } },
        new() { Id = "s27", Ask = "Which weapons of darkstar.esm use the ammunition ammo_DarkStar_VolatileMIArray50?", Plugins = new[] { "darkstar.esm" },
            Sql = "SELECT EditorID FROM Weapon WHERE edid(AmmoType) = 'ammo_DarkStar_VolatileMIArray50' ORDER BY EditorID",
            Truth = c => { var m = c.Mod("darkstar.esm"); var ammo = m.Ammunitions.First(a => a.EditorID == "ammo_DarkStar_VolatileMIArray50").FormKey; return m.Weapons.Where(w => ((IFormLinkGetter)P(w, "AmmoType")!).FormKey == ammo).OrderBy(w => w.EditorID, StringComparer.OrdinalIgnoreCase).Select(w => Row(w.EditorID)); },
            Ordered = true },
        new() { Id = "s28", Ask = "The object bounds of the Eon.", Plugins = new[] { SF },
            Sql = "SELECT ObjectBounds_First, ObjectBounds_Second FROM Weapon WHERE EditorID = 'Eon'",
            Truth = c => { var w = c.Mod(SF).Weapons.First(x => x.EditorID == "Eon"); return One(Point(P(w, "ObjectBounds.First")), Point(P(w, "ObjectBounds.Second"))); } },
        new() { Id = "s29", Ask = "How many records of each type does kgcdoom.esm override?", Plugins = new[] { "kgcdoom.esm" },
            Sql = "SELECT Type, count(*) FROM Forms WHERE IsOverride = 1 GROUP BY Type ORDER BY 2 DESC, 1",
            Truth = c => { var m = c.Mod("kgcdoom.esm"); return m.EnumerateMajorRecords().Where(r => r.FormKey.ModKey != m.ModKey).GroupBy(r => c.Engine.LabelOf(c.Schema.SignatureOf(Type(r))!)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => Row(g.Key, g.Count())); },
            Ordered = true },
        new() { Id = "s30", Ask = "Which keywords do the most armors of darkstar.esm carry?", Plugins = new[] { "darkstar.esm" },
            Sql = "SELECT edid(Value), count(*) FROM Armor_Keywords GROUP BY Value ORDER BY 2 DESC, 1 LIMIT 5",
            Truth = c => c.Mod("darkstar.esm").Armors.SelectMany(a => a.Keywords ?? Enumerable.Empty<IFormLinkGetter<IKeywordGetter>>()).GroupBy(k => k.FormKey).Select(g => (Name: c.EdidOf<IKeywordGetter>(g.Key), N: g.Count()))
                .OrderByDescending(x => x.N).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(5).Select(x => Row(x.Name, x.N)), Ordered = true },
        new() { Id = "s31", Ask = "How many dialogue responses does darkstar.esm hold, and in how many topics?", Plugins = new[] { "darkstar.esm" },
            Sql = "SELECT (SELECT count(*) FROM DialogResponses), (SELECT count(*) FROM DialogTopic)",
            Truth = c => { var m = c.Mod("darkstar.esm"); return One(m.EnumerateMajorRecords<IDialogResponsesGetter>().Count(), m.EnumerateMajorRecords<IDialogTopicGetter>().Count()); } },
        new() { Id = "s32", Ask = "Which weapons of the load order's winners do the most damage?", Scope = ScopeKind.Winners,
            Sql = "SELECT EditorID, FormKey, AttackDamage FROM Weapon ORDER BY AttackDamage DESC, FormKey LIMIT 10",
            Truth = c => MutagenOrder.WinningWeapons(c).Values.OrderByDescending(w => (float)P(w, "AttackDamage")!).ThenBy(w => w.FormKey.ToString(), StringComparer.OrdinalIgnoreCase).Take(10).Select(w => Row(w.EditorID, w.FormKey, P(w, "AttackDamage"))),
            Ordered = true },
    };

    /// <summary>A point the way Mutagen's JSON writes it: "x, y, z".</summary>
    static string Point(object? p) => string.Join(", ", new[] { "X", "Y", "Z" }.Select(n => Fmt(P(p, n))));

    /// <summary>
    /// Set by --tool and --tool-home (milestone M8): the questions then go through SFSearch's own query tool
    /// (SFSearch.exe query2 --stdin --json, on a test home whose service runs), and the rows it gives are held
    /// against Mutagen exactly as the library's are.
    /// </summary>
    public static string? ToolExe, ToolHome;

    static QueryAnswer ThroughTool(Ctx c, Question q)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ToolExe!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (string a in new[] { "query2", "--stdin", "--rows", "100000", "--max-chars", "8000000", "--json" }) psi.ArgumentList.Add(a);
        if (q.Scope == ScopeKind.Plugins) { psi.ArgumentList.Add("--plugins"); psi.ArgumentList.Add(string.Join(",", q.Plugins)); }
        else { psi.ArgumentList.Add("--scope"); psi.ArgumentList.Add(q.Scope.ToString().ToLowerInvariant()); }
        psi.Environment["SFSEARCH_HOME"] = ToolHome!;
        psi.Environment["SFSEARCH_DATA"] = c.Data;
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardInput.Write(q.Sql);
        p.StandardInput.Close();
        string text = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var answer = new QueryAnswer();
        System.Text.Json.JsonElement root;
        try { root = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(text); }
        catch (System.Text.Json.JsonException) { answer.Error = $"the tool didn't answer with JSON (exit code {p.ExitCode}): {Cut(text + " " + err, 400)}"; return answer; }
        if (root.TryGetProperty("error", out var error)) { answer.Error = error.GetProperty("message").GetString(); return answer; }
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("rows", out var rows)) { answer.Error = $"the tool's answer has no rows: {Cut(text, 400)}"; return answer; }
        foreach (var col in data.GetProperty("columns").EnumerateArray()) answer.Columns.Add(col.GetString() ?? "");
        foreach (var row in rows.EnumerateArray())
            answer.Rows.Add(row.EnumerateArray().Select(v => v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Null => (object?)null,
                System.Text.Json.JsonValueKind.Number => v.TryGetInt64(out long l) ? l : v.GetDouble(),
                System.Text.Json.JsonValueKind.String => v.GetString(),
                _ => v.GetRawText(),
            }).ToArray());
        answer.TotalRows = data.GetProperty("totalRows").GetInt64();
        answer.HasResult = true;
        if (answer.TotalRows != answer.Rows.Count) answer.Error = $"the tool gave {answer.Rows.Count:N0} of {answer.TotalRows:N0} rows";
        if (root.TryGetProperty("cost", out var cost) && cost.TryGetProperty("ms", out var ms)) answer.Cost.Seconds = ms.GetInt64() / 1000.0;
        return answer;
    }

    public static string Run(Ctx c)
    {
        var all = All();
        var failures = new List<string>();
        var lines = new List<string>();
        int benchmark = 0;
        foreach (var q in all)
        {
            try
            {
                var answer = ToolExe != null ? ThroughTool(c, q) : q.Scope == ScopeKind.Plugins ? c.Sql(q.Sql, q.Plugins) : c.Lo(q.Sql, q.Scope);
                Must(answer, q.Sql);
                var got = Rows(answer);
                var want = q.Truth(c).ToList();
                Same(q.Id, got, want, q.Ordered);
                string joined = string.Join("\n", got.Select(g => g + " "));
                foreach (string k in q.Known) Check(joined.Contains(k, StringComparison.OrdinalIgnoreCase), $"{q.Id}: the known answer \"{k}\" isn't in the rows [{Cut(string.Join(" ; ", got), 300)}]");
                if (q.Id.StartsWith('q')) benchmark++;
                lines.Add($"{q.Id}\tok\t{got.Count} row(s)\t{answer.Cost.Seconds:F3} s\t{q.Ask}\t{Cut(string.Join(" ; ", got), 200)}");
            }
            catch (TestFailed e) { failures.Add(e.Message); lines.Add($"{q.Id}\tFAILED\t\t\t{q.Ask}\t{e.Message}"); }
        }
        c.Save(ToolExe != null ? "sql-questions-tool.tsv" : "sql-questions.tsv", lines);
        if (failures.Count > 0) throw new TestFailed($"{failures.Count} of {all.Count} questions failed. {Cut(string.Join("  ||  ", failures.Take(6)), 2500)}");
        int ids = all.Where(q => q.Id.StartsWith('q')).Select(q => q.Id.TrimEnd('b')).Distinct().Count();
        return $"{all.Count} SQL questions{(ToolExe != null ? " through SFSearch's query tool (query2 --stdin --json)" : "")}, each answer equal to Mutagen's: " +
            (benchmark > 0 ? $"{ids} of the agent benchmark's 60 questions (as {benchmark} queries, their known answers held too) and {all.Count - benchmark} of this milestone's own" : "this milestone's own");
    }
}

/// <summary>The load order as Mutagen reads it, and the winners as Mutagen works them out.</summary>
static class MutagenOrder
{
    static List<IStarfieldModGetter>? _loaded;
    static Dictionary<FormKey, IWeaponGetter>? _weapons;
    public static string OrderNote = "";

    /// <summary>The enabled plugins, first loaded first, each as the overlay Mutagen reads.</summary>
    public static List<IStarfieldModGetter> Loaded(Ctx c)
    {
        if (_loaded != null) return _loaded;
        // The plugins and their order from Mutagen's own reading of plugins.txt, Starfield.ccc and the implicit list
        var listings = Mutagen.Bethesda.Plugins.Order.LoadOrder.GetLoadOrderListings(GameRelease.Starfield, c.Data, throwOnMissingMods: false).ToList();
        var names = listings.Where(l => l.Enabled).Select(l => l.ModKey.FileName.String).Where(n => File.Exists(Path.Combine(c.Data, n))).ToList();
        var mine = c.Index.Plugins.Where(p => p.Order.Enabled).OrderBy(p => p.Order.Position).Select(p => p.Name).ToList();
        OrderNote = names.SequenceEqual(mine, StringComparer.OrdinalIgnoreCase)
            ? $"Mutagen's own load order and the header index's are the same {names.Count} plugins in the same order"
            : $"Mutagen's own load order has {names.Count} enabled plugins, the header index's {mine.Count}; first difference at {Enumerable.Range(0, Math.Min(names.Count, mine.Count)).FirstOrDefault(i => !names[i].Equals(mine[i], StringComparison.OrdinalIgnoreCase), -1)}";
        var mods = new List<IStarfieldModGetter>();
        foreach (string n in names)
        {
            var p = c.Index.Plugin(n);
            // Mutagen can't read FormIDs through a missing master either, and its load order drops a plugin it refuses (C204:
            // one FormKey held twice) and any plugin built on a dropped one (fixes.17, DropPlugin): MissionsGalore.esm here
            if (p?.Segment == null || p.Problems.MissingMasters.Count > 0 || p.LeftOut != null) continue;
            mods.Add(c.Mod(p.Name));
        }
        return _loaded = mods;
    }

    /// <summary>Mutagen's winning override of every weapon in the load order.</summary>
    public static Dictionary<FormKey, IWeaponGetter> WinningWeapons(Ctx c)
    {
        if (_weapons != null) return _weapons;
        var lo = new Mutagen.Bethesda.Plugins.Order.LoadOrder<Mutagen.Bethesda.Plugins.Order.IModListingGetter<IStarfieldModGetter>>(
            Loaded(c).Select(m => (Mutagen.Bethesda.Plugins.Order.IModListingGetter<IStarfieldModGetter>)new Mutagen.Bethesda.Plugins.Order.ModListing<IStarfieldModGetter>(m, enabled: true)));
        return _weapons = lo.PriorityOrder.Weapon().WinningOverrides().ToDictionary(w => w.FormKey);
    }
}
