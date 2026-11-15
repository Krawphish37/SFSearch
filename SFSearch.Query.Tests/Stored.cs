// The tests of the stored queries (milestone M10): the format and the library, every shipped query run against the
// reference plugins, and the answers of the queries that aren't one of today's Q macros held against Mutagen.
// (The ten Q macros are held against today's SFSearch in Macros.cs.)
using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Query;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class Stored
{
    const string SF = "Starfield.esm", DS = "darkstar.esm", A74 = "atom74.esm", KGC = "kgcdoom.esm";

    public static string ShippedFolder => Path.Combine(AppContext.BaseDirectory, "queries");

    public static StoredQueries Lib(Ctx c, string? user = null, string? addOns = null, string? shipped = null) => new(c.Engine, new StoredOptions
    {
        ShippedFolder = shipped ?? ShippedFolder, UserFolder = user, AddOnFolders = addOns == null ? Array.Empty<string>() : new[] { addOns },
    });

    public static Dictionary<string, string> Args(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) throw new TestFailed($"\"{part}\" isn't parameter=value");
            d[part[..eq]] = part[(eq + 1)..];
        }
        return d;
    }

    public static StoredAnswer Run(StoredQueries lib, string name, string args, ScopeKind? scope = null, bool heavy = false) =>
        lib.Run(new StoredRunRequest { Name = name, Arguments = Args(args), Scope = scope, AllowHeavy = heavy, PageRows = 1_000_000 });

    public static StoredAnswer Ok(StoredQueries lib, string name, string args, ScopeKind? scope = null, bool heavy = false)
    {
        var a = Run(lib, name, args, scope, heavy);
        if (a.Error != null) throw new TestFailed($"{name} {args} failed: {a.Error} {a.Fix}");
        if (a.NotRun != null) throw new TestFailed($"{name} {args} wasn't run: {a.NotRun.What}");
        return a;
    }

    /// <summary>The named columns of a table's rows, as text, FormKeys in lower case.</summary>
    static List<string> Cols(QueryAnswer a, params string[] columns)
    {
        var at = columns.Select(col =>
        {
            int i = a.Columns.FindIndex(x => x.Equals(col, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new TestFailed($"the answer has no column {col} (it has {string.Join(", ", a.Columns)})");
            return i;
        }).ToArray();
        return a.Rows.Select(r => string.Join(" | ", at.Select(i => Text(r[i])))).ToList();
    }

    /// <summary>A value as text for comparing: a number by its value (80 and 80.0 are the same), text in lower case.</summary>
    static string Text(object? v) => v switch
    {
        null => "",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Num(d),
        float f => Num(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        bool b => b ? "1" : "0",
        string s => s.ToLowerInvariant(),
        Enum e => e.ToString().ToLowerInvariant(),
        IFormLinkGetter l => l.IsNull ? "" : l.FormKey.ToString().ToLowerInvariant(),
        FormKey k => k.IsNull ? "" : k.ToString().ToLowerInvariant(),
        IFormattable n => Num(Convert.ToDouble(n, CultureInfo.InvariantCulture)),
        _ => (v.ToString() ?? "").ToLowerInvariant(),
    };

    static string Num(double d) => Math.Round(d, 4).ToString("R", CultureInfo.InvariantCulture);

    static string Line(params object?[] values) => string.Join(" | ", values.Select(Text));

    /// <summary>A property of a Mutagen object by name, or null when it has none of that name.</summary>
    static object? Try(object? o, string path)
    {
        try { return P(o, path); }
        catch (TestFailed) { return null; }
    }

    static IEnumerable<object?> TryList(object? o, string path) => Try(o, path) is System.Collections.IEnumerable e and not string ? e.Cast<object?>() : Enumerable.Empty<object?>();

    static string Label(Ctx c, object record) => c.Engine.LabelOf(c.Schema.SignatureOf(Ctx.Type(record))!);

    // ------------------------------------------------------------------ The format and the library
    public static string Format(Ctx c)
    {
        string home = Path.Combine(c.Work, "stored-home");
        if (Directory.Exists(home)) Directory.Delete(home, true);
        string user = Path.Combine(home, "queries"), addOns = Path.Combine(home, "extensions");
        var lib = Lib(c, user, addOns);
        int shipped = lib.All().Count;
        Check(shipped >= 22, $"at least 22 stored queries ship; {shipped} were found in {ShippedFolder}");
        Check(lib.All().All(q => q.Source == StoredQueries.Shipped), "with an empty home every query is a shipped one");

        // 1. A header's faults are said in plain lines
        var bad = StoredQuery.Parse("-- name: has space\n-- param: x | wibble | maybe | what\n-- param: z | text\n-- scope: everywhere\nSELECT :y FROM Weapon;", "user", null);
        foreach (string want in new[] { "may hold letters", "four parts", "the kinds are text, number and pattern", "isn't one of plugins", "doesn't say what the query answers", "names the parameter :y" })
            Check(bad.Problems.Any(p => p.Contains(want)), $"a faulty header should say \"{want}\": {string.Join(" / ", bad.Problems)}");

        // 2. Save: the header and the SQL are checked first; a wrong column is refused with the nearest real names
        string good = "-- name: heavy-guns\n-- answers: Weapons of a plugin at or above a damage.\n-- param: plugin | text | needed | The plugin.\n-- param: min | number | optional, default 50 | The least damage.\n-- scope: plugins\n"
            + "-- column: EditorID | The weapon.\n-- column: AttackDamage | Its damage.\nSELECT EditorID, AttackDamage FROM Weapon WHERE AttackDamage >= :min ORDER BY 2 DESC, 1;\n";
        var refused = lib.Save("heavy-guns", good.Replace("AttackDamage FROM", "BaseDamage FROM"));
        Check(!refused.Ok && refused.Problems.Any(p => p.Contains("BaseDamage") && p.Contains("AttackDamage")), $"a save with a wrong column should be refused with the nearest name: {string.Join(" / ", refused.Problems)}");
        Check(!Directory.Exists(user) || !File.Exists(Path.Combine(user, "heavy-guns.sql")), "a refused save writes nothing");
        var noDoc = lib.Save("heavy-guns", good.Replace("-- column: AttackDamage | Its damage.\n", ""));
        Check(!noDoc.Ok && noDoc.Problems.Any(p => p.Contains("AttackDamage") && p.Contains("doesn't describe")), $"a returned column the header doesn't describe should be refused: {string.Join(" / ", noDoc.Problems)}");
        var saved = lib.Save("heavy-guns", good);
        Check(saved.Ok && File.Exists(Path.Combine(user, "heavy-guns.sql")), $"a good query is saved: {string.Join(" / ", saved.Problems)}");
        Check(!lib.Save("heavy-guns", good).Ok && lib.Save("heavy-guns", good, overwrite: true).Ok, "an existing file is replaced only when asked");

        // 3. Run: the default of a number, an argument, a wrong argument, a missing plugin
        var def = Ok(lib, "heavy-guns", $"plugin={SF}");
        long at50 = c.Mod(SF).Weapons.Count(w => (float)P(w, "AttackDamage")! >= 50);
        Check(def.Tables.Count == 1 && def.Tables[0].Answer.TotalRows == at50 && def.Query!.FullName == "user.heavy-guns", $"heavy-guns with its default gives {def.TotalRows} rows; Mutagen counts {at50}");
        var arg = Ok(lib, "heavy-guns", $"plugin={SF} min=100");
        long at100 = c.Mod(SF).Weapons.Count(w => (float)P(w, "AttackDamage")! >= 100);
        Check(arg.TotalRows == at100 && at100 < at50, $"min=100 gives {arg.TotalRows} rows; Mutagen counts {at100}");
        var wrongArg = Run(lib, "heavy-guns", $"plugin={SF} minimum=3");
        Check(wrongArg.Error != null && wrongArg.Error.Contains("no parameter \"minimum\"") && (wrongArg.Fix ?? "").Contains("min ("), $"a wrong argument names the real ones: {wrongArg.Error} {wrongArg.Fix}");
        var notNumber = Run(lib, "heavy-guns", $"plugin={SF} min=lots");
        Check(notNumber.Error != null && notNumber.Error.Contains("is a number"), $"a number that isn't one: {notNumber.Error}");
        var noPlugin = Run(lib, "heavy-guns", "");
        Check(noPlugin.Error != null && noPlugin.Error.Contains("none was named"), $"a plugins query with no plugin: {noPlugin.Error}");
        var noSuch = Run(lib, "heavy-gun", $"plugin={SF}");
        Check(noSuch.Error != null && noSuch.Error.Contains("user.heavy-guns") || (noSuch.Error ?? "").Contains("heavy-guns"), $"a wrong name gets the nearest real one: {noSuch.Error}");

        // 4. A value is bound, never pasted: SQL in an argument is only text
        var inject = Ok(lib, "kw", $"plugin={KGC} record=x'_OR_1=1_--");
        Check(inject.TotalRows == 0, $"an argument that is SQL must match nothing; it gave {inject.TotalRows} rows");
        var inject2 = Ok(lib, "find", $"plugin={KGC} pattern=%'_OR_'1'='1");
        Check(inject2.TotalRows == 0, $"a pattern that is SQL must match nothing; it gave {inject2.TotalRows} rows");
        Check(StoredQueries.LikePattern("a_b%c*d?") == @"%a\_b\%c%d_%", $"the pattern for LIKE: {StoredQueries.LikePattern("a_b%c*d?")}");

        // 5. The same name in two places: neither wins silently
        File.WriteAllText(Path.Combine(user, "kw.sql"), "-- name: kw\n-- answers: My own keywords query.\n-- param: plugin | text | needed | The plugin.\n-- scope: plugins\n-- column: N | A count.\nSELECT count(*) AS N FROM Keyword;\n");
        var twice = Run(lib, "kw", $"plugin={KGC}");
        Check(twice.Error != null && twice.Error.Contains("shipped.kw") && twice.Error.Contains("user.kw"), $"a name in two places must be asked for by its full name: {twice.Error}");
        var mine = Ok(lib, "user.kw", $"plugin={KGC}");
        var theirs = Ok(lib, "shipped.kw", $"plugin={KGC} type=Weapon");
        Check(mine.Query!.Source == "user" && mine.Tables[0].Answer.Columns.SequenceEqual(new[] { "N" }) && theirs.Query!.Source == "shipped" && theirs.Tables[0].Answer.Columns.Contains("KeywordEditorID"),
            "the full name picks the user's or the shipped one");
        Check(mine.Notes.Any(n => n.Contains("shipped.kw") && n.Contains("not run")) && mine.ToText().StartsWith("[query] user.kw"), $"the answer says which was run: {mine.ToText().Split('\n')[0]}");
        Check(lib.ListText().Contains("| shipped.kw |") && lib.ListText().Contains("| user.kw |"), "the list shows both by their full names");

        // 6. An add-on's queries sit beside its .dll, and carry its name
        Directory.CreateDirectory(Path.Combine(addOns, "My.AddOn.queries"));
        File.WriteAllText(Path.Combine(addOns, "My.AddOn.queries", "perks.sql"), "-- name: perks\n-- answers: The perks of a plugin.\n-- param: plugin | text | needed | The plugin.\n-- scope: plugins\n-- column: EditorID | The perk.\nSELECT EditorID FROM Perk ORDER BY 1;\n");
        Check(!lib.All().Any(q => q.Name == "perks"), "a queries folder with no .dll beside it is not an add-on's");
        File.WriteAllBytes(Path.Combine(addOns, "My.AddOn.dll"), Array.Empty<byte>());
        var addOn = Ok(lib, "perks", $"plugin={A74}");
        Check(addOn.Query!.FullName == "My.AddOn.perks" && addOn.TotalRows == c.Mod(A74).Perks.Count, $"the add-on's query runs as {addOn.Query.FullName} with {addOn.TotalRows} rows");
        Check(Ok(lib, "My.AddOn.perks", $"plugin={A74}").TotalRows == addOn.TotalRows, "and by its full name");

        // 7. A stored query whose SQL names something that doesn't exist fails with its name and the nearest real names
        string broken = Path.Combine(c.Work, "stored-broken");
        if (Directory.Exists(broken)) Directory.Delete(broken, true);
        Directory.CreateDirectory(broken);
        string cobj = File.ReadAllText(Path.Combine(ShippedFolder, "cobj.sql"));
        File.WriteAllText(Path.Combine(broken, "cobj.sql"), cobj.Replace("c.AmountProduced AS Amount", "c.AmountMade AS Amount"));
        string weap = File.ReadAllText(Path.Combine(ShippedFolder, "weapon-ammo.sql"));
        File.WriteAllText(Path.Combine(broken, "weapon-ammo.sql"), weap.Replace("FROM Weapon w", "FROM Weapons w"));
        var brokenLib = Lib(c, shipped: broken);
        var check = brokenLib.Check(brokenLib.Find("cobj"));
        Check(!check.Ok && check.Problems.Any(p => p.Contains("AmountMade") && p.Contains("AmountProduced")), $"the check of a query with a wrong column names the real one: {string.Join(" / ", check.Problems)}");
        var ran = Run(brokenLib, "cobj", $"plugin={A74}");
        Check(ran.Error != null && ran.Error.Contains("shipped.cobj") && ran.Error.Contains("AmountMade") && ran.Error.Contains("AmountProduced"), $"running it fails with the query's name and the real column: {ran.Error}");
        var ranTable = Run(brokenLib, "weapon-ammo", $"plugin={A74}");
        Check(ranTable.Error != null && ranTable.Error.Contains("shipped.weapon-ammo") && ranTable.Error.Contains("Weapon"), $"a wrong table too: {ranTable.Error}");

        // 8. One table by its cursor, and the JSON form
        var page = lib.Run(new StoredRunRequest { Name = "templates", Arguments = Args($"plugin={KGC} record=kgcDoom_WEAP_SuperShotgun"), PageRows = 5 });
        Check(page.Ok && page.Tables.Count == 2 && page.Tables[1].Answer.Cursor != null, $"templates with a record gives two tables, the second with more rows than a page: {page.Error}");
        var next = lib.Run(new StoredRunRequest { Name = "templates", Arguments = Args($"plugin={KGC} record=kgcDoom_WEAP_SuperShotgun"), PageRows = 5, Cursor = $"t2:{page.Tables[1].Answer.Cursor}" });
        Check(next.Ok && next.Tables.Count == 1 && next.Tables[0].Number == 2 && next.Tables[0].Answer.Offset == 5, $"the cursor of table 2 gives table 2 from row 6 on: {next.Error}");
        var json = page.ToJson();
        Check(json["query"]!["name"]!.GetValue<string>() == "shipped.templates" && json["tables"]!.AsArray().Count == 2 && json["tables"]![1]!["more"]!["cursor"]!.GetValue<string>().StartsWith("t2:"), "the JSON form has the query, both tables and the cursor");

        // 9. With a load-order scope asked for, a plugins query runs there and says so
        var wide = Ok(lib, "weapon-ammo", "pattern=Eon", ScopeKind.Winners);
        Check(wide.TotalRows > 0 && wide.Notes.Any(n => n.Contains("winners scope")), $"weapon-ammo in the winners scope: {wide.TotalRows} rows; {string.Join(" / ", wide.Notes)}");

        File.Delete(Path.Combine(user, "kw.sql"));
        return $"{shipped} shipped queries found; a faulty header says what is wrong (6 kinds); save refuses a wrong column (\"{Cut(refused.Problems[0], 110)}\") and an undescribed one, then saves; "
            + $"a default and an argument equal Mutagen ({at50} and {at100} weapons); wrong arguments, a wrong name and a missing plugin each get a plain line; SQL in an argument matches nothing; "
            + $"kw in two places must be asked for as shipped.kw or user.kw, and the answer says which ran; an add-on's query runs as My.AddOn.perks; a query with a wrong column fails with its name and the real column; "
            + $"a table's cursor gives that table's next page; a plugins query runs in the winners scope when asked";
    }

    // ------------------------------------------------------------------ Every shipped query runs
    /// <summary>One run of a shipped query in the test: its arguments and how many rows each table must give at least.</summary>
    sealed record Case(string Name, string Args, int[] MinRows, ScopeKind? Scope = null);

    static readonly Case[] Cases =
    {
        new("flst", $"plugin={DS}", new[] { 1 }), new("flst", $"plugin={DS} pattern=Apparel", new[] { 1 }), new("flst", $"plugin={KGC} record=kgcDoom_CharacterPlushies_StaticsList", new[] { 6 }),
        new("vmad", $"plugin={KGC}", new[] { 100 }), new("vmad", $"plugin={A74}", new[] { 1 }), new("vmad", $"plugin={DS} type=MiscItem script=EquipmentDatum", new[] { 100 }),
        new("appr", $"plugin={A74}", new[] { 1 }), new("appr", $"plugin={SF} type=Weapon pattern=Beowulf", new[] { 1 }),
        new("kw", $"plugin={A74} type=Weapon", new[] { 1 }), new("kw", $"plugin={SF} record=Eon", new[] { 1 }),
        new("templates", $"plugin={A74}", new[] { 1 }), new("templates", $"plugin={KGC} record=kgcDoom_WEAP_SuperShotgun", new[] { 4, 14 }),
        new("cobj", $"plugin={A74}", new[] { 110 }), new("cobj", $"plugin={DS} pattern=co_aid", new[] { 1 }), new("cobj", $"plugin={SF} makes=Eon", new[] { 0 }),
        new("omod", $"plugin={A74}", new[] { 140 }), new("omod", $"plugin={SF} pattern=mod_Eon", new[] { 1 }),
        new("arma", $"plugin={KGC}", new[] { 11 }), new("arma", $"plugin={DS} pattern=Mk2", new[] { 1 }),
        new("new", $"plugin={A74}", new[] { 317 }), new("new", $"plugin={KGC} type=Weapon", new[] { 2 }),
        new("overrides", $"plugin={KGC}", new[] { 1 }), new("overrides", $"plugin={DS}", new[] { 1 }),
        new("items", $"plugin={KGC}", new[] { 2, 2 }), new("items", $"plugin={SF}", new[] { 50, 100 }),
        new("leveled-adds", $"plugin={KGC}", new[] { 36 }), new("leveled-adds", $"plugin={DS}", new[] { 1 }),
        new("leveled-list", $"record=LL_Weapon_ccAtom74 plugin={A74}", new[] { 1 }),
        new("item-sources", $"record=ccAtom74 plugin={A74},{SF}", new[] { 1 }),
        new("multi-edits", "type=Weapon", new[] { 1 }),
        new("broken-links", $"plugin={KGC} all=1", new[] { 1 }), new("broken-links", $"plugin={KGC}", new[] { 0 }), new("broken-links", $"plugin={A74}", new[] { 0 }),
        new("scripts-used", $"plugin={KGC}", new[] { 21, 0 }),
        new("recipes-by-workbench", $"plugin={A74}", new[] { 1 }), new("recipes-by-workbench", $"plugin={A74} workbench=Weapon", new[] { 1 }),
        new("recipes-needing", $"item=ResInorgCommonIron plugin={SF}", new[] { 100 }),
        new("find", $"plugin={A74} pattern=Barrel", new[] { 1 }), new("find", $"plugin={SF} pattern=mod_*_Receiver type=OMOD", new[] { 1 }),
        new("counts", $"plugin={A74}", new[] { 12 }),
        new("placed", $"plugin={KGC} record=kgcDoom_ACT_AirlockControl", new[] { 1 }),
        new("omod-properties", $"plugin={A74} pattern=Barrel", new[] { 1, 0 }), new("omod-properties", $"plugin={SF} pattern=mod_Eon", new[] { 1, 0 }),
        new("links-to", $"plugin={KGC} target=kgcDoom_WEAP_SuperShotgun", new[] { 8 }),
        new("zero-bounds", $"plugin={KGC}", new[] { 3 }),
        new("weapon-ammo", $"plugin={A74}", new[] { 3 }), new("weapon-ammo", $"plugin={SF} ammo=777mm", new[] { 21 }),
        new("keyword-users", $"plugin={SF} keyword=WeaponTypeShotgun type=Weapon", new[] { 5 }),
    };

    public static string Shipped(Ctx c)
    {
        var lib = Lib(c);
        var all = lib.All();
        var faults = new List<string>();
        // 1. Every header and every statement, with no plugin read
        foreach (var q in all)
        {
            var check = lib.Check(q);
            if (!check.Ok) faults.Add($"{q.FullName}: {string.Join("; ", check.Problems)}");
            if (q.Examples.Count == 0) faults.Add($"{q.FullName}: its header has no example line");
            if (q.Columns.Count == 0) faults.Add($"{q.FullName}: its header describes no column");
        }
        // 2. Every query has a run here, and every run gives rows where rows are expected
        foreach (var q in all)
            if (!Cases.Any(k => k.Name == q.Name)) faults.Add($"{q.FullName}: the test has no run for it (add one to Stored.Cases)");
        long rows = 0; int runs = 0, tables = 0;
        var lines = new List<string>();
        foreach (var k in Cases)
        {
            if (!all.Any(q => q.Name == k.Name)) { faults.Add($"the test runs {k.Name}, which isn't shipped"); continue; }
            var a = Run(lib, k.Name, k.Args, k.Scope);
            runs++;
            if (a.Error != null) { faults.Add($"{k.Name} {k.Args}: {a.Error} {a.Fix}"); continue; }
            if (a.NotRun != null) { faults.Add($"{k.Name} {k.Args}: not run: {a.NotRun.What}"); continue; }
            if (a.Tables.Count != k.MinRows.Length) { faults.Add($"{k.Name} {k.Args}: {a.Tables.Count} table(s), {k.MinRows.Length} expected"); continue; }
            for (int i = 0; i < a.Tables.Count; i++)
            {
                var t = a.Tables[i].Answer;
                tables++; rows += t.TotalRows;
                if (t.TotalRows < k.MinRows[i]) faults.Add($"{k.Name} {k.Args}: table {i + 1} gave {t.TotalRows} rows, at least {k.MinRows[i]} expected. {string.Join(" ", t.Notes)}");
                if (t.TotalRows == 0 && !t.Notes.Any(n => n.StartsWith("No rows"))) faults.Add($"{k.Name} {k.Args}: table {i + 1} is empty and doesn't say why");
            }
            lines.Add($"{k.Name}\t{k.Args}\t{string.Join(" + ", a.Tables.Select(t => t.Answer.TotalRows))}\t{a.Seconds:F2}");
        }
        // 3. The header's own examples run too: what "show" tells a user to type must work
        int examples = 0;
        foreach (var q in all)
            foreach (string e in q.Examples)
            {
                var a = Run(lib, q.Name, e);
                examples++;
                if (a.Error != null) faults.Add($"{q.Name}'s example \"{e}\": {a.Error} {a.Fix}");
                else if (a.NotRun != null) faults.Add($"{q.Name}'s example \"{e}\" is too heavy to be an example: {a.NotRun.What}");
                else if (a.TotalRows == 0) faults.Add($"{q.Name}'s example \"{e}\" gives no rows");
                lines.Add($"{q.Name}\t(example) {e}\t{string.Join(" + ", a.Tables.Select(t => t.Answer.TotalRows))}\t{a.Seconds:F2}");
            }
        // 4. The one query that is heavy by design says its cost first
        var heavy = Run(lib, "multi-edits", "");
        if (heavy.NotRun == null || !heavy.NotRun.What.Contains("Forms")) faults.Add($"multi-edits with no type should answer with the cost of Forms for the load order: {heavy.Error ?? (heavy.NotRun?.What ?? $"{heavy.TotalRows} rows")}");
        c.Save("stored-shipped.tsv", lines.Prepend("Query\tArguments\tRows\tSeconds"));
        if (faults.Count > 0) throw new TestFailed($"{faults.Count} fault(s): {Cut(string.Join(" || ", faults), 3000)}");
        return $"{all.Count} shipped queries: every header and statement checks; {runs} runs on {DS}, {A74}, {KGC} and {SF} gave {rows:N0} rows in {tables} tables, none empty where rows are expected; "
            + $"{examples} header examples run and give rows; multi-edits with no type answers with its cost (\"{Cut(heavy.NotRun!.What, 90)}\")";
    }

    // ------------------------------------------------------------------ The answers against Mutagen
    static readonly FormKey HumanRace = FormKey.Factory("00347D:Starfield.esm");

    static double Damage(IWeaponGetter w) => Convert.ToDouble(P(w, "AttackDamage"), CultureInfo.InvariantCulture) + TryList(w, "DamageTypes").Sum(d => Convert.ToDouble(P(d, "Value"), CultureInfo.InvariantCulture));

    static bool NotPlayable(IMajorRecordGetter r) => (r.MajorRecordFlagsRaw & 0x4) != 0;

    static string NameOf(object r) => Fmt(Try(r, "Name")).Trim();

    public static string AgainstMutagen(Ctx c)
    {
        var lib = Lib(c);
        var said = new List<string>();

        // items: the general rules, applied through Mutagen
        foreach (string plugin in new[] { KGC, SF, DS })
        {
            var mod = c.Mod(plugin);
            var a = Ok(lib, "items", $"plugin={plugin}");
            var weapons = mod.Weapons.Where(w => !(bool)P(w, "NonPlayable")! && !NotPlayable(w) && NameOf(w).Length > 0 && !w.EquipmentType.IsNull && Damage(w) > 0)
                .Select(w => Line(w.FormKey, Damage(w), P(w, "BaseValue"), P(w, "BaseWeight"))).ToList();
            Same($"items of {plugin}, weapons", Cols(a.Tables[0].Answer, "FormKey", "Damage", "Value", "Weight"), weapons, ordered: false);
            var armors = mod.Armors.Where(r => !NotPlayable(r) && NameOf(r).Length > 0 && (r.Race.IsNull || r.Race.FormKey == HumanRace))
                .Select(r => Line(r.FormKey, P(r, "Value"), P(r, "Weight"))).ToList();
            Same($"items of {plugin}, armor", Cols(a.Tables[1].Answer, "FormKey", "Value", "Weight"), armors, ordered: false);
            said.Add($"items {plugin}: {weapons.Count} of {mod.Weapons.Count} weapons and {armors.Count} of {mod.Armors.Count} armors");
        }

        // weapon-ammo and zero-bounds
        foreach (string plugin in new[] { A74, SF })
        {
            var a = Ok(lib, "weapon-ammo", $"plugin={plugin}");
            var want = c.Mod(plugin).Weapons.Select(w => Line(w.FormKey, P(w, "AmmoType"), P(w, "AmmoCapacity"), P(w, "ProjectilesCount"), P(w, "AttackDamage"))).ToList();
            Same($"weapon-ammo of {plugin}", Cols(a.Tables[0].Answer, "FormKey", "AmmoKey", "Capacity", "Projectiles", "Damage"), want, ordered: false);
            said.Add($"weapon-ammo {plugin}: {want.Count}");
        }
        foreach (string plugin in new[] { KGC, SF })
        {
            var mod = c.Mod(plugin);
            var a = Ok(lib, "zero-bounds", $"plugin={plugin}");
            static bool Zero(object r) => Fmt(P(r, "ObjectBounds.First")) == "0, 0, 0" && Fmt(P(r, "ObjectBounds.Second")) == "0, 0, 0";
            var want = mod.Weapons.Where(w => w.FormKey.ModKey == mod.ModKey && Zero(w)).Select(w => Line("weapon", w.FormKey))
                .Concat(mod.Armors.Where(r => r.FormKey.ModKey == mod.ModKey && Zero(r)).Select(r => Line("armor", r.FormKey))).ToList();
            Same($"zero-bounds of {plugin}", Cols(a.Tables[0].Answer, "Type", "FormKey"), want, ordered: false);
            said.Add($"zero-bounds {plugin}: {want.Count}");
        }

        // counts, new, overrides, find: every record of the plugin, as Mutagen walks it
        {
            var mod = c.Mod(KGC);
            var all = mod.EnumerateMajorRecords().ToList();
            var counts = all.GroupBy(r => Label(c, r)).Select(g => Line(g.Key, g.Count(), g.Count(r => r.FormKey.ModKey == mod.ModKey), g.Count(r => r.FormKey.ModKey != mod.ModKey))).ToList();
            Same($"counts of {KGC}", Cols(Ok(lib, "counts", $"plugin={KGC}").Tables[0].Answer, "Type", "Records", "Own", "Overrides"), counts, ordered: false);
            Same($"new of {KGC}", Cols(Ok(lib, "new", $"plugin={KGC}").Tables[0].Answer, "FormKey", "Type", "EditorID"),
                all.Where(r => r.FormKey.ModKey == mod.ModKey).Select(r => Line(r.FormKey, Label(c, r), r.EditorID)).ToList(), ordered: false);
            Same($"overrides of {KGC}", Cols(Ok(lib, "overrides", $"plugin={KGC}").Tables[0].Answer, "FormKey", "Type", "EditorID", "Origin"),
                all.Where(r => r.FormKey.ModKey != mod.ModKey).Select(r => Line(r.FormKey, Label(c, r), r.EditorID, r.FormKey.ModKey.FileName.String)).ToList(), ordered: false);
            Same($"find Shotgun in {KGC}", Cols(Ok(lib, "find", $"plugin={KGC} pattern=Shotgun").Tables[0].Answer, "FormKey", "EditorID"),
                all.Where(r => (r.EditorID ?? "").Contains("Shotgun", StringComparison.OrdinalIgnoreCase)).Select(r => Line(r.FormKey, r.EditorID)).ToList(), ordered: false);
            Same($"find a*Quality_0? in {KGC}", Cols(Ok(lib, "find", $"plugin={KGC} pattern=Armor*Quality_0? type=OMOD").Tables[0].Answer, "FormKey"),
                mod.ObjectModifications.Where(r => System.Text.RegularExpressions.Regex.IsMatch(r.EditorID ?? "", "Armor.*Quality_0.", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Select(r => Line(r.FormKey)).ToList(), ordered: false);
            said.Add($"counts, new, overrides, find {KGC}: {all.Count:N0} records in {counts.Count} types, {all.Count(r => r.FormKey.ModKey != mod.ModKey)} overrides");

            // placed: the placed copies of one base
            var baseKey = mod.Activators.First(x => x.EditorID == "kgcDoom_ACT_AirlockControl").FormKey;
            var placed = mod.EnumerateMajorRecords<IPlacedObjectGetter>().Where(r => r.Base.FormKey == baseKey).Select(r => Line(r.FormKey)).ToList();
            var pa = Ok(lib, "placed", $"plugin={KGC} record=kgcDoom_ACT_AirlockControl").Tables[0].Answer;
            Same($"placed in {KGC}", Cols(pa, "Ref"), placed, ordered: false);
            Check(Cols(pa, "CellKey").All(k => k.Length > 0), "every placed copy names its cell");
            // The most placed base, to have more than one row
            var top = mod.EnumerateMajorRecords<IPlacedObjectGetter>().Where(r => !r.Base.IsNull).GroupBy(r => r.Base.FormKey).OrderByDescending(g => g.Count()).First();
            Same($"placed {top.Key} in {KGC}", Cols(Ok(lib, "placed", $"plugin={KGC} record={top.Key}").Tables[0].Answer, "Ref"), top.Select(r => Line(r.FormKey)).ToList(), ordered: false);
            said.Add($"placed {KGC}: {placed.Count} and {top.Count()} copies");

            // links-to and broken-links: every link of every record, as Mutagen lists them
            var target = mod.Weapons.First(w => w.EditorID == "kgcDoom_WEAP_SuperShotgun").FormKey;
            var from = new HashSet<string>(); var brokenWant = new HashSet<string>(); long links = 0;
            // A record that holds other records (a cell with its placed objects, a topic with its responses) gives
            // their links too in Mutagen. The Links table has each link under the record it is written in, so here
            // the links of the records inside are taken off.
            var own = new Dictionary<FormKey, Dictionary<FormKey, int>>();
            Dictionary<FormKey, int> Own(IMajorRecordGetter rec)
            {
                if (own.TryGetValue(rec.FormKey, out var have)) return have;
                var full = new Dictionary<FormKey, int>();
                foreach (var link in rec.EnumerateFormLinks()) if (!link.IsNull) full[link.FormKey] = full.GetValueOrDefault(link.FormKey) + 1;
                if (rec is IMajorRecordGetterEnumerable holder)
                    foreach (var inner in holder.EnumerateMajorRecords())
                    {
                        if (inner.FormKey == rec.FormKey) continue;
                        foreach (var (k, n) in Own(inner)) { int left = full.GetValueOrDefault(k) - n; if (left > 0) full[k] = left; else full.Remove(k); }
                    }
                return own[rec.FormKey] = full;
            }
            foreach (var rec in all)
                foreach (var (key, n) in Own(rec))
                {
                    links += n;
                    if (key == target) from.Add(Line(rec.FormKey));
                    if (c.Index.Versions(key.ToString()) == null) brokenWant.Add(Line(rec.FormKey, key));
                }
            Same($"links-to in {KGC}", Cols(Ok(lib, "links-to", $"plugin={KGC} target=kgcDoom_WEAP_SuperShotgun").Tables[0].Answer, "FormKey").Distinct().ToList(), from.ToList(), ordered: false);
            Same($"broken-links of {KGC}, all", Cols(Ok(lib, "broken-links", $"plugin={KGC} all=1").Tables[0].Answer, "FormKey", "Target").Distinct().ToList(), brokenWant.ToList(), ordered: false);
            // Without all=1, the links into Starfield.esm below 000800 are left out
            var real = brokenWant.Where(p => !(p.EndsWith(":starfield.esm", StringComparison.Ordinal) && string.CompareOrdinal(p[(p.IndexOf(" | ", StringComparison.Ordinal) + 3)..][..6], "000800") < 0)).ToList();
            Same($"broken-links of {KGC}", Cols(Ok(lib, "broken-links", $"plugin={KGC}").Tables[0].Answer, "FormKey", "Target").Distinct().ToList(), real, ordered: false);
            said.Add($"links-to and broken-links {KGC}: {links:N0} links, {from.Count} records point at the shotgun, {brokenWant.Count} broken pairs, {real.Count} of them not a low ID of Starfield.esm");

            // scripts-used: every script name with the records that attach it, on the record, as its fragment script, or on a quest's alias
            var uses = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in all)
            {
                var scripts = TryList(rec, "VirtualMachineAdapter.Scripts").ToList();
                if (Try(rec, "VirtualMachineAdapter.Script") is { } fragment) scripts.Add(fragment);
                if (Try(rec, "VirtualMachineAdapter.ScriptFragments.Script") is { } fragments) scripts.Add(fragments);
                foreach (var alias in TryList(rec, "VirtualMachineAdapter.Aliases")) scripts.AddRange(TryList(alias, "Scripts"));
                foreach (var s in scripts)
                {
                    string name = (string?)P(s, "Name") ?? "";
                    if (name.Length == 0) continue;
                    if (!uses.TryGetValue(name, out var set)) uses[name] = set = new HashSet<FormKey>();
                    set.Add(rec.FormKey);
                }
            }
            Same($"scripts-used of {KGC}", Cols(Ok(lib, "scripts-used", $"plugin={KGC}").Tables[0].Answer, "Script", "Records"), uses.Select(u => Line(u.Key, u.Value.Count)).ToList(), ordered: false);
            said.Add($"scripts-used {KGC}: {uses.Count} scripts on {uses.Values.SelectMany(v => v).Distinct().Count()} records");
        }

        // keyword-users
        {
            var kw = c.Mod(SF).Keywords.First(k => k.EditorID == "WeaponTypeShotgun").FormKey;
            var want = c.Mod(SF).Weapons.Where(w => w.Keywords != null && w.Keywords.Any(k => k.FormKey == kw)).Select(w => Line(w.FormKey)).ToList();
            Same("keyword-users WeaponTypeShotgun", Cols(Ok(lib, "keyword-users", $"plugin={SF} keyword=WeaponTypeShotgun type=Weapon").Tables[0].Answer, "FormKey"), want, ordered: false);
            Same("keyword-users by FormKey", Cols(Ok(lib, "keyword-users", $"plugin={SF} keyword={kw} type=WEAP").Tables[0].Answer, "FormKey"), want, ordered: false);
            said.Add($"keyword-users {SF}: {want.Count} weapons");
        }

        // recipes-by-workbench and recipes-needing
        foreach (string plugin in new[] { A74, DS })
        {
            var mod = c.Mod(plugin);
            var want = mod.ConstructibleObjects.GroupBy(r => r.WorkbenchKeyword.IsNull ? "" : r.WorkbenchKeyword.FormKey.ToString().ToLowerInvariant())
                .Select(g => Line(g.Key, g.Count(), g.Count(r => r.FormKey.ModKey == mod.ModKey), g.Select(r => r.CreatedObject.IsNull ? "" : r.CreatedObject.FormKey.ToString().ToLowerInvariant()).Where(k => k.Length > 0).Distinct().Count())).ToList();
            Same($"recipes-by-workbench of {plugin}", Cols(Ok(lib, "recipes-by-workbench", $"plugin={plugin}").Tables[0].Answer, "WorkbenchKey", "Recipes", "OwnRecipes", "Makes"), want, ordered: false);
            said.Add($"recipes-by-workbench {plugin}: {mod.ConstructibleObjects.Count:N0} recipes on {want.Count} workbenches");
        }
        {
            var iron = FormKey.Factory(c.Index.Describe(c.Index.FindEditorId("ResInorgCommonIron")[0]).FormKey);
            var want = new List<string>();
            foreach (var r in c.Mod(SF).ConstructibleObjects)
                foreach (var k in TryList(r, "ConstructibleComponents"))
                    if (P(k, "Component") is IFormLinkGetter l && l.FormKey == iron) want.Add(Line(r.FormKey, P(k, "RequiredCount")));
            var a = Ok(lib, "recipes-needing", $"item=ResInorgCommonIron plugin={SF}").Tables[0].Answer;
            Check(Cols(a, "Needs").All(n => n.Contains("resinorgcommoniron")), "every row needs something named like the item");
            var exact = a.Rows.Select((r, i) => (Key: Cols(a, "NeedsKey")[i], Row: Cols(a, "FormKey", "Count")[i])).Where(x => x.Key == iron.ToString().ToLowerInvariant()).Select(x => x.Row).ToList();
            Same("recipes-needing iron", exact, want, ordered: false);
            said.Add($"recipes-needing {SF}: {want.Count} recipes need ResInorgCommonIron");
        }

        // omod-properties
        {
            var mod = c.Mod(A74);
            var a = Ok(lib, "omod-properties", $"plugin={A74}");
            var props = new List<string>(); var incs = new List<string>();
            foreach (var o in mod.ObjectModifications)
            {
                int i = 0;
                foreach (var p in TryList(o, "Properties")) props.Add(Line(o.FormKey, i++, P(p, "Property"), P(p, "FunctionType")));
                i = 0;
                foreach (var inc in TryList(o, "Includes")) incs.Add(Line(o.FormKey, i++, P(inc, "Mod")));
            }
            Same($"omod-properties of {A74}", Cols(a.Tables[0].Answer, "FormKey", "Idx", "Property", "Function"), props, ordered: false);
            Same($"omod-properties of {A74}, includes", Cols(a.Tables[1].Answer, "FormKey", "Idx", "ModKey"), incs, ordered: false);
            said.Add($"omod-properties {A74}: {props.Count} property changes and {incs.Count} included mods of {mod.ObjectModifications.Count} OMODs");
        }

        // leveled-list and leveled-adds
        {
            var list = c.Mod(A74).LeveledItems.First(l => l.EditorID == "LL_Weapon_ccAtom74");
            var want = (list.Entries ?? Array.Empty<ILeveledItemEntryGetter>()).Select((e, i) => Line(i, P(e, "Level"), P(e, "Reference"), P(e, "Count"))).ToList();
            Same("leveled-list of atom74", Cols(Ok(lib, "leveled-list", $"record=LL_Weapon_ccAtom74 plugin={A74}").Tables[0].Answer, "Idx", "Level", "Item", "Count"), want, ordered: false);
            said.Add($"leveled-list {A74}: {want.Count} entries");
        }
        foreach (string plugin in new[] { KGC, DS })
        {
            var mod = c.Mod(plugin);
            var want = new List<string>();
            void Lists<T>(IEnumerable<T> mine, Func<IStarfieldModGetter, IEnumerable<T>> of) where T : class, IMajorRecordGetter
            {
                var origins = new Dictionary<string, Dictionary<FormKey, T>>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in mine)
                {
                    static Dictionary<string, int> Count(T rec) =>
                        TryList(rec, "Entries").GroupBy(e => Line(P(e, "Reference"), P(e, "Level"), P(e, "Count"))).ToDictionary(g => g.Key, g => g.Count());
                    var have = Count(l);
                    var was = new Dictionary<string, int>();
                    if (l.FormKey.ModKey != mod.ModKey)
                    {
                        string owner = l.FormKey.ModKey.FileName.String;
                        if (!origins.TryGetValue(owner, out var byKey)) origins[owner] = byKey = of(c.Mod(c.Index.Plugin(owner)!.Name)).ToDictionary(x => x.FormKey);
                        if (byKey.TryGetValue(l.FormKey, out var original)) was = Count(original);
                    }
                    foreach (var (entry, n) in have)
                        if (n > was.GetValueOrDefault(entry)) want.Add($"{Text(l.FormKey)} | {entry} | {n - was.GetValueOrDefault(entry)}");
                }
            }
            Lists(mod.LeveledItems, m => m.LeveledItems);
            Lists(mod.LeveledNpcs, m => m.LeveledNpcs);
            Lists(mod.LeveledPackIns, m => m.LeveledPackIns);
            Same($"leveled-adds of {plugin}", Cols(Ok(lib, "leveled-adds", $"plugin={plugin}").Tables[0].Answer, "ListKey", "Item", "Level", "Count", "Times"), want, ordered: false);
            said.Add($"leveled-adds {plugin}: {want.Count} added entries in {mod.LeveledItems.Count + mod.LeveledNpcs.Count + mod.LeveledPackIns.Count} lists");
        }

        // item-sources, over two plugins that hold no record in common
        {
            var mods = new[] { c.Mod(A74), c.Mod(SF) };
            var item = c.Mod(A74).Weapons.First(w => w.EditorID == "ccAtom74").FormKey;
            var entries = mods.SelectMany(m => m.LeveledItems).SelectMany(l => (l.Entries ?? Array.Empty<ILeveledItemEntryGetter>()).Where(e => !e.Reference.IsNull).Select(e => (List: l.FormKey, Ref: e.Reference.FormKey))).ToList();
            var depth = new Dictionary<FormKey, int> { [item] = 0 };
            var queue = new Queue<FormKey>(); queue.Enqueue(item);
            var holders = entries.ToLookup(e => e.Ref, e => e.List);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (depth[node] >= 12) continue;
                foreach (var up in holders[node])
                    if (!depth.ContainsKey(up)) { depth[up] = depth[node] + 1; queue.Enqueue(up); }
            }
            var want = new List<string>();
            foreach (var e in entries) if (depth.TryGetValue(e.Ref, out int d)) want.Add(Line("leveled list", e.List, d));
            foreach (var m in mods)
            {
                foreach (var r in m.ConstructibleObjects) if (r.CreatedObject.FormKey == item) want.Add(Line("recipe", r.FormKey, 0));
                foreach (var r in m.Containers) foreach (var e in TryList(r, "Items")) if (P(e, "Item.Item") is IFormLinkGetter l && depth.TryGetValue(l.FormKey, out int d)) want.Add(Line("container", r.FormKey, d));
                foreach (var r in m.Npcs) foreach (var e in TryList(r, "Items")) if (P(e, "Item.Item") is IFormLinkGetter l && depth.TryGetValue(l.FormKey, out int d)) want.Add(Line("npc", r.FormKey, d));
                foreach (var r in m.Outfits) foreach (var e in TryList(r, "Items")) if (e is IFormLinkGetter l && depth.TryGetValue(l.FormKey, out int d)) want.Add(Line("outfit", r.FormKey, d));
            }
            var a = Ok(lib, "item-sources", $"record=ccAtom74 plugin={A74},{SF}").Tables[0].Answer;
            Same("item-sources of ccAtom74", Cols(a, "Source", "FormKey", "Steps"), want, ordered: false);
            said.Add($"item-sources ccAtom74 in {A74} and {SF}: {want.Count} sources through {depth.Count - 1} leveled lists");
        }

        // multi-edits: every loaded plugin's weapons, as Mutagen's load order has them
        {
            var loaded = MutagenOrder.Loaded(c);
            var holders = new Dictionary<FormKey, List<string>>();
            foreach (var m in loaded)
                foreach (var w in m.Weapons)
                {
                    if (!holders.TryGetValue(w.FormKey, out var list)) holders[w.FormKey] = list = new List<string>();
                    list.Add(m.ModKey.FileName.String);
                }
            var want = holders.Where(h => h.Value.Count(p => !p.Equals(h.Key.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase)) >= 2)
                .Select(h => Line(h.Key, h.Value.Count(p => !p.Equals(h.Key.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase)), string.Join(" < ", h.Value), h.Value[^1])).ToList();
            Same("multi-edits of Weapon", Cols(Ok(lib, "multi-edits", "type=Weapon").Tables[0].Answer, "FormKey", "Edits", "Chain", "Winner"), want, ordered: false);
            said.Add($"multi-edits Weapon: {want.Count} weapons changed by 2 or more of {loaded.Count} loaded plugins");
            if (c.Heavy)
            {
                // By type, from the record headers of the whole load order (about 9 million rows): the Weapon line must say the same
                var byType = Ok(lib, "multi-edits", "", heavy: true).Tables[0].Answer;
                var weaponLine = Cols(byType, "Type", "Records").FirstOrDefault(l => l.StartsWith("weapon |", StringComparison.Ordinal));
                Check(weaponLine == $"weapon | {want.Count}", $"multi-edits by type says \"{weaponLine}\" for weapons; Mutagen counts {want.Count}");
                said.Add($"multi-edits by type: {byType.TotalRows} record types, Weapon {want.Count}");
            }
        }
        c.Save("stored-mutagen.txt", said);
        return string.Join("; ", said);
    }

    // ------------------------------------------------------------------ Time
    public static string Timing(Ctx c)
    {
        // Every query on darkstar.esm, each with empty scratch tables of its own: the first run pays for all its reads,
        // the second finds them there. multi-edits has no plugin to give: it reads the weapons of the whole load order.
        var runs = new (string Name, string Args)[]
        {
            ("flst", ""), ("flst", "pattern=Apparel"), ("vmad", ""), ("vmad", "record=DarkStar_ScalingConfigurator_Mods"), ("appr", ""), ("kw", ""), ("kw", "type=Weapon"), ("templates", ""),
            ("templates", "record=DarkStar_Mk2_Orion"), ("cobj", ""), ("omod", ""), ("arma", ""), ("new", ""), ("overrides", ""),
            ("items", ""), ("leveled-adds", ""), ("leveled-list", "record=ll_DarkStar_AmmoSales"), ("item-sources", "record=DarkStar_Mk2_Orion"), ("multi-edits", "type=Weapon"),
            ("broken-links", ""), ("scripts-used", ""), ("recipes-by-workbench", ""), ("recipes-needing", "item=ResInorgCommonIron"), ("find", "pattern=Mk2"), ("counts", ""),
            ("placed", "record=act_DarkStar_CodexMach_Activator_v2"), ("omod-properties", ""), ("links-to", "target=DarkStar_Mk2_Orion"), ("zero-bounds", ""), ("weapon-ammo", ""),
            ("keyword-users", "keyword=WeaponTypeRifle"),
        };
        var lines = new List<string> { "Query\tArguments\tRows\tFirst run (s)\tAgain (s)\tRead on the first run" };
        double first = 0, again = 0, slowestAgain = 0; string slowest = "";
        int n = 0;
        foreach (var (name, args) in runs)
        {
            var engine = c.NewEngine(Path.Combine("sql-stored-timing", (++n).ToString(CultureInfo.InvariantCulture)));
            var lib = new StoredQueries(engine, new StoredOptions { ShippedFolder = ShippedFolder });
            string all = name == "multi-edits" ? args : $"plugin={DS} {args}".Trim();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var a = lib.Run(new StoredRunRequest { Name = name, Arguments = Args(all), PageRows = 40 });
            double t1 = clock.Elapsed.TotalSeconds;
            if (!a.Ok) throw new TestFailed($"{name} {all}: {a.Error ?? a.NotRun?.What}");
            clock.Restart();
            var b = lib.Run(new StoredRunRequest { Name = name, Arguments = Args(all), PageRows = 40 });
            double t2 = clock.Elapsed.TotalSeconds;
            Check(b.Ok && b.TotalRows == a.TotalRows, $"{name}: the second run gave {b.TotalRows} rows, the first {a.TotalRows}");
            first += t1; again += t2;
            if (t2 > slowestAgain) { slowestAgain = t2; slowest = name; }
            lines.Add($"{name}\t{args}\t{string.Join(" + ", a.Tables.Select(t => t.Answer.TotalRows))}\t{t1:F2}\t{t2:F2}\t{a.Tables.Sum(t => t.Answer.Cost.Read.Count)} reads");
            engine.Dispose();
        }
        c.Save("stored-timing-darkstar.tsv", lines);
        foreach (string l in lines) Console.WriteLine("      " + l);
        return $"{runs.Length} runs on {DS}: {first:F1} s in all the first time, {again:F1} s again; the slowest again is {slowest} ({slowestAgain:F2} s)";
    }
}
