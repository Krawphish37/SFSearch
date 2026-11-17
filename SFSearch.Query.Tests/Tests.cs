// The tests of the SQL layer besides the SQL questions (Questions.cs). Each returns one line with its numbers and
// throws TestFailed with what went wrong.
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Query;
using SFSearch.Records;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class Tests
{
    const string SF = "Starfield.esm";

    // ------------------------------------------------------------------ The prototype's queries
    public static string ReadmeQueries(Ctx c)
    {
        // 1. The join, exactly as design\agent-first\prototypes\mgsql\README.md has it
        var a = c.Ok("SELECT w.EditorID, w.AttackDamage FROM Weapon w JOIN Weapon_Keywords k ON k.FormKey = w.FormKey WHERE w.AttackDamage > 20 AND k.Value = '04A0A0:Starfield.esm' ORDER BY 2 DESC", SF);
        var rows = Rows(a);
        Check(rows.Count == 9, $"the prototype's join gave 9 rows; this gives {rows.Count}");
        Check(rows.Take(3).SequenceEqual(new[] { "MagShot | 53", "Razorback | 51", "UrbanEagle | 39" }), $"the prototype's first rows were MagShot 53, Razorback 51, UrbanEagle 39; these are {string.Join(" ; ", rows.Take(3))}");
        var pistol = FormKey.Factory("04A0A0:Starfield.esm");
        var want = c.Mod(SF).Weapons.Where(w => (float)P(w, "AttackDamage")! > 20 && w.Keywords != null && w.Keywords.Any(k => k.FormKey == pistol)).Select(w => Row(w.EditorID, P(w, "AttackDamage"))).ToList();
        Same("the join against Mutagen", rows, want, ordered: false);

        // 2. A wrong name is answered with the real one, xEdit's name included
        var wrong = c.Sql("SELECT EditorID, BaseDamage FROM Weapon WHERE BaseDamage > 50", SF);
        Check(wrong.Error != null && wrong.Error.Contains("Weapon has no column \"BaseDamage\"") && wrong.Error.Contains("AttackDamage (Float, WDMG, xEdit name \"Base Damage\")")
            && wrong.Error.Contains("MeleeBashDamage (Float, WMEL, xEdit name \"Bash Damage\")"), $"BaseDamage should lead to AttackDamage and MeleeBashDamage: {wrong.Error}");

        // 3. A value the JSON leaves out reads as the default
        var scope = Rows(c.Ok("SELECT sum(HasScope = 0) AS typed_view_says_no_scope, sum((Json ->> '$.HasScope') = 0) AS raw_json_says_no_scope, count(*) FROM Weapon", SF));
        Check(scope.Count == 1 && scope[0] == "393 | 0 | 406", $"\"has no scope\" should give 393 | 0 | 406; it gives {scope.FirstOrDefault()}");
        int mutagen = c.Mod(SF).Weapons.Count(w => !(bool)P(w, "HasScope")!);
        Check(mutagen == 393, $"Mutagen itself counts {mutagen} weapons without a scope");

        // 4. No rows, with the reason
        var none = c.Ok("SELECT EditorID FROM Weapon WHERE AttackDamage > 100000", SF);
        Check(none.TotalRows == 0 && none.Notes.Any(n => n.StartsWith("No rows") && n.Contains("406")), $"an empty answer must say why: {string.Join(" / ", none.Notes)}");
        return $"the join gives the prototype's 9 rows (MagShot 53, Razorback 51, UrbanEagle 39 first), equal to Mutagen's; BaseDamage leads to AttackDamage (\"Base Damage\") and MeleeBashDamage (\"Bash Damage\"); \"has no scope\" is 393 of 406 through the typed view and 0 through the raw JSON; the empty answer says \"{none.Notes.First(n => n.StartsWith("No rows"))}\"";
    }

    // ------------------------------------------------------------------ The three scopes
    public static string Scopes(Ctx c)
    {
        // Plugins: two files attached side by side, told apart by Plugin
        var two = Rows(c.Ok("SELECT Plugin, count(*) FROM Keyword GROUP BY Plugin ORDER BY Plugin", "kgcdoom.esm", "darkstar.esm"));
        Same("two plugins", two, new[] { "darkstar.esm", "kgcdoom.esm" }.Select(p => Row(p, c.Mod(p).Keywords.Count)).ToList(), ordered: true);

        // More plugins than SQLite can attach: one scratch file for the set
        var many = c.Index.Plugins.Where(p => p.Segment != null && p.Problems.MissingMasters.Count == 0 && p.Stamp.Length < 4 << 20 && p.Segment.Signatures.Any(s => s.Signature == "WEAP" && s.Count > 0))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(12).Select(p => p.Name).ToArray();
        Check(many.Length == 12, "the Data folder has fewer than 12 small plugins with weapons");
        var set = Rows(c.Ok("SELECT Plugin, count(*), sum(IsOverride) FROM Weapon GROUP BY Plugin ORDER BY Plugin", many));
        Same("twelve plugins", set, many.Select(p => { var m = c.Mod(p); return Row(c.Index.Plugin(p)!.Name, m.Weapons.Count, m.Weapons.Count(w => w.FormKey.ModKey != m.ModKey)); }).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList(), ordered: false);

        // Versions: every copy in every loaded plugin, with its place in the load order and who wins
        var versions = c.OkLo("SELECT FormKey, Plugin, LoadOrder, IsWinner FROM Weapon ORDER BY LoadOrder", ScopeKind.Versions);
        var loaded = MutagenOrder.Loaded(c);
        var want = new List<string>(); var winnerOf = new Dictionary<FormKey, string>();
        foreach (var m in loaded) foreach (var w in m.Weapons) winnerOf[w.FormKey] = m.ModKey.FileName.String;
        foreach (var m in loaded) foreach (var w in m.Weapons) want.Add($"{w.FormKey} | {m.ModKey.FileName.String} | {(winnerOf[w.FormKey] == m.ModKey.FileName.String ? 1 : 0)}".ToLowerInvariant());
        Same("versions of every weapon", versions.Rows.Select(r => $"{r[0]} | {r[1]} | {Cell(r[3])}".ToLowerInvariant()).ToList(), want, ordered: false);
        // LoadOrder rises in Mutagen's order
        var place = loaded.Select((m, i) => (m.ModKey.FileName.String, i)).ToDictionary(x => x.String, x => x.i, StringComparer.OrdinalIgnoreCase);
        int last = -1; long lastLo = -1;
        foreach (var r in versions.Rows)
        {
            int p = place[(string)r[1]!]; long lo = (long)r[2]!;
            Check(lo > lastLo ? p > last : lo == lastLo && p == last, $"LoadOrder {lo} of {r[1]} doesn't follow Mutagen's order");
            last = p; lastLo = lo;
        }
        return $"plugins: 2 attached files and a 12-plugin set ({set.Count} rows) equal Mutagen; versions: {versions.TotalRows:N0} copies of weapons in {versions.Rows.Select(r => r[1]).Distinct().Count()} loaded plugins, each with the right winner mark and a LoadOrder that follows Mutagen's order. {MutagenOrder.OrderNote}";
    }

    public static string WinnersEqualMutagen(Ctx c)
    {
        var a = c.OkLo("SELECT FormKey, Plugin, Json FROM Weapon", ScopeKind.Winners);
        var winners = MutagenOrder.WinningWeapons(c);
        var loaded = MutagenOrder.Loaded(c);
        var seen = new HashSet<FormKey>();
        int jsonDiffers = 0, pluginDiffers = 0; string first = "";
        foreach (var r in a.Rows)
        {
            var key = FormKey.Factory((string)r[0]!);
            Check(seen.Add(key), $"the winners scope gives {key} twice");
            if (!winners.TryGetValue(key, out var w)) throw new TestFailed($"the winners scope has {key} ({r[1]}), which Mutagen's winning overrides don't");
            if (Compact(RecordSerializer.ToJson(w)) != Compact((string)r[2]!)) { jsonDiffers++; if (first.Length == 0) first = $"{w.EditorID} ({key}) of {r[1]}"; }
            string holder = loaded.Last(m => m.Weapons.RecordCache.ContainsKey(key)).ModKey.FileName.String;
            if (!holder.Equals((string)r[1]!, StringComparison.OrdinalIgnoreCase)) { pluginDiffers++; if (first.Length == 0) first = $"{key}: SQL says {r[1]}, Mutagen {holder}"; }
        }
        Check(seen.Count == winners.Count, $"the winners scope has {seen.Count:N0} weapons, Mutagen's winning overrides {winners.Count:N0}; missing: {string.Join(", ", winners.Keys.Where(k => !seen.Contains(k)).Take(3))}");
        Check(jsonDiffers == 0 && pluginDiffers == 0, $"{jsonDiffers} winners differ from Mutagen's record, {pluginDiffers} name another plugin. First: {first}");
        return $"{seen.Count:N0} winning weapons from {a.Rows.Select(r => (string)r[1]!).Distinct(StringComparer.OrdinalIgnoreCase).Count()} of {loaded.Count} loaded plugins: the same FormKeys as Mutagen's WinningOverrides, each read from the plugin that wins it, each record's JSON equal to Mutagen's";
    }

    // ------------------------------------------------------------------ Links
    public static string LinksEqualMutagen(Ctx c)
    {
        var mod = c.Mod(SF);
        var types = new (string Type, Func<IEnumerable<IMajorRecordGetter>> Records)[]
        {
            ("Weapon", () => mod.Weapons), ("Armor", () => mod.Armors), ("Npc", () => mod.Npcs), ("ConstructibleObject", () => mod.ConstructibleObjects),
            ("LeveledItem", () => mod.LeveledItems), ("Perk", () => mod.Perks), ("Race", () => mod.Races), ("FormList", () => mod.FormLists),
            ("MagicEffect", () => mod.MagicEffects), ("ObjectModification", () => mod.ObjectModifications),
        };
        var parts = new List<string>(); long total = 0, records = 0;
        foreach (var (type, get) in types)
        {
            var a = c.Ok($"SELECT l.FromKey, l.ToKey FROM {type} t JOIN Links l ON l.FromKey = t.FormKey", SF);
            var got = new List<string>(); var json = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in a.Rows)
            {
                if (r[0] != null) got.Add($"{r[0]}>{r[1]}".ToUpperInvariant());
            }
            // The record's JSON, once per record
            var perRecord = c.Ok($"SELECT FormKey, Json FROM {type}", SF);
            foreach (var r in perRecord.Rows) json[(string)r[0]!] = (string)r[1]!;
            var want = new List<string>(); int n = 0, jsonDiffers = 0;
            foreach (var rec in get())
            {
                n++;
                foreach (var link in rec.EnumerateFormLinks()) if (!link.IsNull) want.Add($"{rec.FormKey}>{link.FormKey}".ToUpperInvariant());
                if (!json.TryGetValue(rec.FormKey.ToString(), out var j) || Compact(j) != Compact(RecordSerializer.ToJson(rec))) jsonDiffers++;
            }
            Check(json.Count == n, $"{type}: the view has {json.Count:N0} records, Mutagen {n:N0}");
            Check(jsonDiffers == 0, $"{type}: {jsonDiffers:N0} of {n:N0} records have another JSON in the table than Mutagen's serializer gives");
            Same($"links of {type}", got, want, ordered: false);
            parts.Add($"{type} {n:N0}/{want.Count:N0}");
            total += want.Count; records += n;
        }
        return $"Links equals Mutagen's EnumerateFormLinks (links that aren't null, as pairs of record and target) for 10 record types of Starfield.esm: {records:N0} records, {total:N0} links ({string.Join(", ", parts)}: records/links). Every one of those records' JSON in the table equals Mutagen's serializer's";
    }

    // ------------------------------------------------------------------ Names
    public static string WrongNames(Ctx c)
    {
        var cases = new (string Sql, string Plugin, string Real)[]
        {
            ("SELECT EditorID, BaseDamage FROM Weapon", SF, "AttackDamage"),
            ("SELECT * FROM Wepon", SF, "Weapon"),
            ("SELECT * FROM WEAP", SF, "Weapon"),
            ("SELECT \"Base Damage\" FROM Weapon", SF, "AttackDamage"),
            ("SELECT Keywords FROM Weapon", SF, "Weapon_Keywords"),
            ("SELECT * FROM Weapon_Keyword", SF, "Weapon_Keywords"),
            ("SELECT * FROM Npcs", SF, "Npc"),
            ("SELECT * FROM NPC_", SF, "Npc"),
            ("SELECT * FROM ObjectMod", "darkstar.esm", "ObjectModification"),
            ("SELECT * FROM OMOD", "darkstar.esm", "ObjectModification"),
            ("SELECT EditorId2 FROM Armor", SF, "EditorID"),
            ("SELECT ArmorRatting FROM Armor", SF, "ArmorRating"),
            ("SELECT Val FROM Weapon_Keywords", SF, "Value"),
            ("SELECT * FROM Record", SF, "Records"),
            ("SELECT * FROM Link", SF, "Links"),
            ("SELECT FromKy FROM Links", SF, "FromKey"),
            ("SELECT * FROM GameSettings", SF, "GameSetting"),
            ("SELECT w.Ammo FROM Weapon w", SF, "AmmoType"),
            ("SELECT * FROM LeveledItem_Entry", SF, "LeveledItem_Entries"),
            ("SELECT k.Value, w.CritMult FROM Weapon w JOIN Weapon_Keywords k ON k.FormKey = w.FormKey", SF, "CritDamageMult"),
            ("SELECT Bash FROM Weapon", SF, "MeleeBashDamage"),
            ("SELECT Reference, Cnt FROM LeveledItem_Entries", SF, "Count"),
            ("SELECT * FROM Form", SF, "Forms"),
            ("SELECT Winner FROM Forms", SF, "IsWinner"),
        };
        var lines = new List<string>(); int ok = 0;
        foreach (var (sql, plugin, real) in cases)
        {
            var a = c.Sql(sql, plugin);
            Check(a.Error != null, $"\"{sql}\" should fail on its wrong name; it gave {a.TotalRows} rows");
            // The name the answer leads to must be a real table or a real column of a table the query names
            bool isReal = c.Engine.ColumnsOf(real, ScopeKind.Plugins) != null
                || sql.Split(' ', ',', '.').Select(w => c.Engine.ColumnsOf(w.Trim(), ScopeKind.Plugins)).Any(cols => cols != null && cols.Contains(real, StringComparer.OrdinalIgnoreCase));
            Check(isReal, $"the test's own expectation {real} isn't a real name");
            Check(System.Text.RegularExpressions.Regex.IsMatch(a.Error + " " + a.Fix, $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(real)}(?![A-Za-z0-9])"), $"\"{sql}\" should lead to {real}: {a.Error}");
            lines.Add($"{sql}\t{a.Error}");
            ok++;
        }
        c.Save("wrong-names.tsv", lines);
        return $"{ok} wrong names tried (tables, columns, signatures, xEdit names, lists asked as columns): each answer names a real table or column";
    }

    public static string ChecksBeforeAQuery(Ctx c)
    {
        // An EditorID where a FormKey is needed
        var lit = c.Ok("SELECT count(*) FROM Weapon_Keywords WHERE Value = 'WeaponTypePistol'", SF);
        Check(Rows(lit)[0] == "0" && lit.Notes.Any(n => n.Contains("'WeaponTypePistol' is the EditorID of WeaponTypePistol (04A0A0:Starfield.esm)") && n.Contains("edid(Value) = 'WeaponTypePistol'") && n.Contains("'04A0A0:Starfield.esm'")),
            $"an EditorID against a link column needs the note that names edid(): {string.Join(" / ", lit.Notes)}");
        var json = c.Ok("SELECT count(*) FROM Weapon WHERE Json LIKE '%WeaponTypePistol%'", SF);
        Check(json.Notes.Any(n => n.Contains("EditorIDs aren't inside a record's JSON") && n.Contains("04A0A0:Starfield.esm")), $"an EditorID searched in Json needs a note: {string.Join(" / ", json.Notes)}");
        var inList = c.Ok("SELECT count(*) FROM Npc WHERE Race IN ('HumanRace', 'x')", SF);
        Check(inList.Notes.Any(n => n.Contains("'HumanRace' is the EditorID of HumanRace")), $"an EditorID in an IN list needs the note: {string.Join(" / ", inList.Notes)}");
        var right = c.Ok("SELECT count(*) FROM Weapon_Keywords WHERE edid(Value) = 'WeaponTypePistol'", SF);
        var pistol = FormKey.Factory("04A0A0:Starfield.esm");
        int want = c.Mod(SF).Weapons.Sum(w => w.Keywords?.Count(k => k.FormKey == pistol) ?? 0);
        Check(Rows(right)[0] == want.ToString() && !right.Notes.Any(n => n.Contains("EditorID of")), $"edid(Value) = … is right and needs no note; it gave {Rows(right)[0]} (Mutagen {want}) with {right.Notes.Count} notes");
        var edid = c.Ok("SELECT count(*) FROM Weapon WHERE EditorID = 'Eon'", SF);
        Check(Rows(edid)[0] == "1" && edid.Notes.Count == 0, "EditorID = 'Eon' is right and needs no note");

        // No ORDER BY
        var unordered = c.Ok("SELECT EditorID FROM Weapon", SF);
        Check(unordered.Notes.Any(n => n.Contains("no ORDER BY")), "rows with no ORDER BY need a note");
        var ordered = c.Ok("SELECT EditorID FROM Weapon ORDER BY EditorID", SF);
        Check(!ordered.Notes.Any(n => n.Contains("ORDER BY")), "rows with an ORDER BY need no note");
        var inner = c.Ok("SELECT EditorID FROM (SELECT EditorID FROM Weapon ORDER BY EditorID)", SF);
        Check(inner.Notes.Any(n => n.Contains("no ORDER BY")), "an ORDER BY inside a subquery doesn't order the answer: the note is still needed");
        var single = c.Ok("SELECT count(*) FROM Weapon", SF);
        Check(single.Notes.Count == 0, "one row needs no ORDER BY note");

        // File order: the table's rows come in the order of the plugin file
        var fileOrder = Rows(c.Ok("SELECT FormKey FROM Weapon", SF));
        var mutagenOrder = c.Index.RecordsOfType(SF, "WEAP").Select(c.Index.Describe).OrderBy(i => i.Offset).Select(i => i.FormKey).ToList();
        Check(fileOrder.SequenceEqual(mutagenOrder, StringComparer.OrdinalIgnoreCase), "the Weapon view's rows aren't in file order");
        return $"an EditorID literal against a link column, in an IN list and in Json gets the note that names edid() and the FormKey; edid(Value) = 'WeaponTypePistol' finds {want} and gets none; rows without ORDER BY get the note (also when only a subquery is ordered), one row and ordered rows don't; the Weapon view's {fileOrder.Count} rows come in file order";
    }

    // ------------------------------------------------------------------ No silent empty answers
    public static string EmptyAnswers(Ctx c)
    {
        var said = new List<string>();
        string Why(QueryAnswer a, string must, string what)
        {
            Check(a.Ok && a.TotalRows == 0, $"{what}: expected an answer with no rows, got {a.Error ?? a.NotRun?.What ?? a.TotalRows + " rows"}");
            string? note = a.Notes.FirstOrDefault(n => n.StartsWith("No rows:"));
            Check(note != null && note.Contains(must, StringComparison.OrdinalIgnoreCase), $"{what}: the answer has no rows and should say \"{must}\"; it says: {string.Join(" / ", a.Notes)}");
            said.Add(note!);
            return note!;
        }
        // The type isn't in this plugin
        string noNpc = c.Index.Plugins.First(p => p.Segment != null && p.Problems.MissingMasters.Count == 0 && p.Segment.Signatures.Any(s => s.Signature == "WEAP") && !p.Segment.Signatures.Any(s => s.Signature == "NPC_")).Name;
        Why(c.Sql("SELECT EditorID FROM Npc", noNpc), $"{noNpc} has no Npc records (the header index has 0 NPC_", "a type the plugin doesn't have");
        Why(c.Sql("SELECT * FROM Npc_Factions", noNpc), "has no Npc records", "a list of a type the plugin doesn't have");
        // The filter matched nothing among N rows
        int weapons = c.Mod(SF).Weapons.Count;
        Why(c.Sql("SELECT EditorID FROM Weapon WHERE AttackDamage > 100000", SF), $"the filter matched nothing among {weapons} Weapon rows of Starfield.esm", "a filter that matches nothing");
        Why(c.Sql("SELECT * FROM Weapon_Keywords WHERE Value = 'nothing'", SF), $"among the list entries of {weapons} Weapon rows", "a list filter that matches nothing");
        // The plugin isn't in Data
        var missing = c.Sql("SELECT * FROM Weapon", "starfeild.esm");
        Check(missing.Error != null && missing.Error.Contains("there is no plugin named \"starfeild.esm\" in the Data folder") && missing.Error.Contains("Did you mean: Starfield.esm?"), $"a plugin that isn't in Data must be said, with the nearest name: {missing.Error} {missing.Fix}");
        Why(c.Sql("SELECT * FROM Weapon WHERE Plugin = 'notthere.esm'", SF), "notthere.esm isn't in the Data folder", "a plugin named in the SQL that isn't in Data");
        Why(c.Sql("SELECT * FROM Weapon WHERE Plugin = 'darkstar.esm'", SF), "darkstar.esm isn't one of this query's plugins", "a plugin named in the SQL that isn't in the scope");
        // The plugin isn't in the load order
        var off = c.Index.Plugins.FirstOrDefault(p => !p.Order.Enabled && p.Segment != null);
        if (off != null) Why(c.Lo($"SELECT * FROM Weapon WHERE Plugin = '{off.Name}'", ScopeKind.Winners), $"{off.Name} is in Data but not in the load order", "a plugin that isn't loaded, in the winners scope");
        // A multi-type signature with none of the type asked
        Why(c.Sql("SELECT * FROM Records WHERE Type = 'NoSuchType'", SF), "Records holds only the record types read so far", "Records");
        Why(c.Sql("SELECT 1 WHERE 0", SF), "the query names no record table", "a query with no table");
        c.Save("empty-answers.txt", said);
        return $"{said.Count} kinds of empty answer, each with its reason (type not in the plugin: \"{Cut(said[0], 120)}\"; filter: \"{Cut(said[2], 120)}\"; a plugin that isn't in Data, isn't in the scope, isn't loaded{(off != null ? $" ({off.Name})" : " (none found in Data)")}); a scope plugin that isn't in Data stops with the nearest name";
    }

    // ------------------------------------------------------------------ The user's SQL can't write
    public static string UserSqlCantWrite(Ctx c)
    {
        c.Ok("SELECT count(*) FROM Weapon; SELECT count(*) FROM Links", SF);
        var file = c.Engine.PluginFile(c.Index.Plugin(SF)!);
        string Fingerprint()
        {
            using var con = new SqliteConnection($"Data Source={file.Path};Mode=ReadOnly;Pooling=False");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT (SELECT count(*) || '/' || sum(length(Json)) || '/' || sum(Pos) || '/' || group_concat(DISTINCT Type) FROM Rec) || ' links ' || (SELECT count(*) FROM LinkRows) || ' done ' || (SELECT group_concat(Sig || Rows) FROM Done) || ' schema ' || (SELECT count(*) || '/' || sum(length(sql)) FROM sqlite_master)";
            return (string)cmd.ExecuteScalar()!;
        }
        string before = Fingerprint();
        long schemaRows = (long)c.Ok("SELECT count(*) FROM schema.RecordTypes", SF).Rows[0][0]!;
        string outside = Path.Combine(c.Work, "written-by-user-sql.db");
        string uri = outside.Replace('\\', '/');
        var attempts = new[]
        {
            "DELETE FROM p0.Rec", "UPDATE p0.Rec SET EditorID = 'changed'", "INSERT INTO p0.Rec(Ord, Type, Sig, FormKey, IsOverride, Pos, Json) VALUES (1, 'Weapon', 'WEAP', '000001:x.esm', 0, 0, jsonb('{}'))",
            "DROP TABLE p0.Rec", "DROP INDEX p0.Rec_Type", "ALTER TABLE p0.Rec ADD COLUMN Extra", "ALTER TABLE p0.Rec RENAME TO Rec2", "CREATE TABLE p0.Mine(x)", "CREATE INDEX p0.MineIndex ON Rec(Pos)",
            "CREATE VIEW p0.MineView AS SELECT 1", "DELETE FROM p0.LinkRows", "UPDATE p0.Done SET Rows = 0", "DELETE FROM p0.Done", "INSERT INTO p0.Skipped VALUES ('WEAP', 'x', 'y', 'z')",
            "DELETE FROM Weapon", "UPDATE Weapon SET EditorID = 'x'", "INSERT INTO Weapon(FormKey) VALUES ('x')", "DROP VIEW Weapon; DELETE FROM p0.Rec",
            "DELETE FROM schema.RecordTypes", "UPDATE schema.Enums SET Member = 'x'", "DROP TABLE schema.Meta",
            $"ATTACH DATABASE '{uri}' AS other", "DETACH DATABASE p0", "DETACH DATABASE schema", $"VACUUM INTO '{uri}'", "VACUUM",
            "PRAGMA p0.journal_mode = DELETE", "PRAGMA writable_schema = 1", "PRAGMA p0.user_version = 7", "PRAGMA query_only = 0",
            "CREATE TEMP TRIGGER t AFTER INSERT ON sqlite_temp_master BEGIN DELETE FROM p0.Rec; END", "SELECT load_extension('x')",
            "BEGIN IMMEDIATE", "REINDEX", "ANALYZE", "REPLACE INTO p0.Done(Sig, Rows) VALUES ('WEAP', 1)",
            "CREATE TEMP TABLE t AS SELECT 1 AS x; UPDATE p0.Rec SET Pos = (SELECT x FROM t)",
            "WITH gone AS (SELECT FormKey FROM Weapon) DELETE FROM p0.Rec WHERE FormKey IN (SELECT FormKey FROM gone)",
        };
        var lines = new List<string>(); int refused = 0;
        foreach (string sql in attempts)
        {
            var a = c.Sql(sql, SF);
            Check(a.Error != null, $"\"{sql}\" should be refused; it ran");
            Check(Fingerprint() == before, $"\"{sql}\" changed the scratch tables");
            lines.Add($"{sql}\t{a.Error}");
            refused++;
        }
        Check(!File.Exists(outside), "a file was written outside the scratch tables");
        Check((long)c.Ok("SELECT count(*) FROM schema.RecordTypes", SF).Rows[0][0]! == schemaRows, "the schema changed");
        // What it may do: tables, indexes and views of its own
        var own = c.Ok("CREATE TEMP TABLE mine AS SELECT FormKey, AttackDamage FROM Weapon; CREATE INDEX temp.mine_fk ON mine(FormKey); INSERT INTO mine VALUES ('x', 1); UPDATE mine SET AttackDamage = AttackDamage * 2; DELETE FROM mine WHERE FormKey = 'x'; " +
            "CREATE TEMP VIEW strong AS SELECT * FROM mine WHERE AttackDamage > 60; CREATE TABLE plain(x); INSERT INTO plain VALUES (1); DROP TABLE plain; SELECT count(*), (SELECT count(*) FROM strong) FROM mine", SF);
        int weapons = c.Mod(SF).Weapons.Count, strong = c.Mod(SF).Weapons.Count(w => (float)P(w, "AttackDamage")! * 2 > 60);
        Check(Rows(own)[0] == $"{weapons} | {strong}", $"temporary tables of the query's own should work: {Rows(own)[0]} (expected {weapons} | {strong})");
        Check(Fingerprint() == before, "the query's own temporary tables changed the scratch tables");
        // Nothing of one query is left for the next
        var next = c.Sql("SELECT count(*) FROM mine", SF);
        Check(next.Error != null, "a temporary table outlived its query");
        c.Save("read-only.tsv", lines);
        return $"{refused} attempts to change the scratch tables, the schema or a file (DELETE, UPDATE, INSERT, DROP, ALTER, CREATE in an attached file, through a view, ATTACH, DETACH, VACUUM INTO, PRAGMA, a trigger, load_extension) were all refused and the tables' fingerprint stayed the same; a query's own temporary table, index and view work and are gone with the query";
    }

    // ------------------------------------------------------------------ The answer object
    public static string AnswerObject(Ctx c)
    {
        const string sql = "SELECT EditorID, AttackDamage, FormKey FROM Weapon ORDER BY EditorID";
        var all = Rows(c.Ok(sql, SF));
        var pages = new List<string>(); string? cursor = null; int n = 0; string firstText = "";
        do
        {
            var a = c.Engine.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF }, PageRows = 40, Cursor = cursor });
            Must(a, sql);
            if (n == 0) firstText = a.ToText();
            pages.AddRange(Rows(a));
            Check(a.TotalRows == all.Count, "every page must give the whole count");
            cursor = a.Cursor; n++;
            Check(n < 100, "the cursor doesn't end");
        } while (cursor != null);
        Check(pages.SequenceEqual(all), $"the pages put together ({pages.Count} rows) aren't the whole answer ({all.Count} rows)");
        Check(firstText.Contains($"[more] 40 of {all.Count:N0} rows shown. Next: cursor \""), $"the first page must say how to go on: {Cut(firstText[^Math.Min(400, firstText.Length)..])}");
        var wrongCursor = c.Engine.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF }, Cursor = "zzzzzz-40" });
        Check(wrongCursor.Error != null && wrongCursor.Error.Contains("cursor"), "a cursor of another query must be refused");

        var one = c.Ok(sql, SF);
        var p = c.Index.Plugin(SF)!;
        Check(one.Sources.Count == 1 && one.Sources[0].Plugin == SF && one.Sources[0].Bytes == new FileInfo(p.Path).Length && one.Sources[0].ContentKey == p.ContentKey.ToString(), "the source must be the plugin's size and content key");
        string text = c.Engine.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF } }).ToText();
        Check(text.StartsWith("| EditorID | AttackDamage | FormKey |\n|---|---|---|\n") && text.Contains($"[source] Starfield.esm {AnswerSource.Size(p.Stamp.Length)}, key {p.ContentKey.ToString()[..8]}") && text.Contains("\n[cost] ") && text.Contains("Weapon already read"),
            $"the text form must follow the output contract: {Cut(text[^Math.Min(300, text.Length)..])}");
        // A small character budget ends the page early and the cursor goes on from there
        var small = c.Engine.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF }, PageRows = 40 });
        string cut = small.ToText(maxChars: 600);
        var m = System.Text.RegularExpressions.Regex.Match(cut, @"\[more\] (\d+) of [\d,]+ rows shown\. Next: cursor ""([^""]+)""");
        Check(m.Success && int.Parse(m.Groups[1].Value) < 40, $"a small budget must cut the page and say so: {Cut(cut)}");
        var after = c.Engine.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF }, PageRows = 1, Cursor = m.Groups[2].Value });
        Check(Rows(after)[0] == all[int.Parse(m.Groups[1].Value)], "the cursor of a cut page must go on at the first row left out");

        var j = JsonNode.Parse(one.ToJsonText())!.AsObject();
        Check(j["columns"]!.AsArray().Count == 3 && j["rows"]!.AsArray().Count == all.Count && j["notes"] != null && j["source"]!["plugins"]!.AsArray().Count == 1 && j["cost"]!["seconds"] != null,
            "the JSON form must have columns, rows, notes, source and cost");
        var lo = c.OkLo("SELECT count(*) FROM Weapon", ScopeKind.Winners);
        // The load order names the plugins the game loads and, when some are left out (Mutagen refuses them, C204), how
        // many were read and which were left out and why; the sources are the plugins read
        var enabled = c.Index.Plugins.Where(p => p.Order.Enabled).ToList();
        var leftOut = enabled.Where(p => !p.CantLoad && p.LeftOut != null).ToList();
        string want = $"({enabled.Count(p => !p.CantLoad):N0} loaded plugins" + (leftOut.Count == 0 ? "" : $", {MutagenOrder.Loaded(c).Count:N0} read: " + string.Join(", ", leftOut.Take(5).Select(p => $"{p.Name} ({p.LeftOutShort})")));
        Check(lo.LoadOrder != null && lo.ToText().Contains("load order of ") && lo.LoadOrder.Contains(want, StringComparison.Ordinal) && lo.Sources.Count == MutagenOrder.Loaded(c).Count,
            $"a load-order answer must name the load order, the plugins loaded and those read and left out (\"{want}\"): {lo.LoadOrder}, {lo.Sources.Count} sources, Mutagen's load order {MutagenOrder.Loaded(c).Count}");
        var err = c.Sql("SELECT * FROM Wepon", SF).ToText();
        Check(err.StartsWith("Error: ") && err.Contains(" Fix: ") && err.Split('\n')[0].Length < 600, $"an error is one line with its fix: {Cut(err)}");
        return $"{all.Count} rows in {n} pages of 40 by cursor, put together equal to the whole answer; a cursor of another query is refused; a 600-character budget cuts the page at {m.Groups[1].Value} rows and its cursor goes on there; the text form has the table, [more], [source] with the plugin's size and content key, and [cost]; the JSON form has the same parts; a load-order answer names {lo.Sources.Count} plugins and \"{lo.LoadOrder}\"; an error is one line with its fix";
    }

    public static string FunctionsEqualMutagen(Ctx c)
    {
        var winners = MutagenOrder.WinningWeapons(c);
        var loaded = MutagenOrder.Loaded(c);
        var a = c.OkLo("SELECT FormKey, edid(FormKey), name(FormKey), winner(FormKey), versions(FormKey) FROM Weapon", ScopeKind.Winners);
        var got = a.Rows.Select(r => string.Join(" | ", r.Select(Cell)).ToLowerInvariant()).ToList();
        var want = winners.Values.Select(w =>
        {
            var holders = loaded.Where(m => m.Weapons.RecordCache.ContainsKey(w.FormKey)).Select(m => m.ModKey.FileName.String).ToList();
            // Mutagen names a plugin as the master lists spell it; the functions give the file's own name
            var names = holders.Select(h => c.Index.Plugin(h)!.Name).ToList();
            return Row(w.FormKey, w.EditorID, w.Name, names[^1], new JsonArray(names.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()).ToJsonString()).ToLowerInvariant();
        }).ToList();
        Same("edid, name, winner and versions of every winning weapon", got, want, ordered: false);

        var texts = c.Ok("SELECT FormKey, Name, text(Json -> '$.Name', 'de'), text(Json -> '$.Name', 'French'), text(Json -> '$.Name', 'pl') FROM Weapon", SF);
        var wantTexts = c.Mod(SF).Weapons.Select(w =>
        {
            string? T(Language l) => w.Name != null && w.Name.TryLookup(l, out var s) ? s : null;
            return Row(w.FormKey, T(Language.English), T(Language.German), T(Language.French), T(Language.Polish));
        }).ToList();
        Same("names in four languages", Rows(texts), wantTexts, ordered: false);
        int differ = texts.Rows.Count(r => r[1] != null && r[2] != null && !Equals(r[1], r[2]));
        var nulls = Rows(c.Ok("SELECT edid(NULL), edid('nonsense'), edid('FFFFFF:Starfield.esm'), name('FFFFFF:Starfield.esm'), winner('000001:notthere.esm'), text(NULL), text('plain')", SF));
        Check(nulls[0] == " |  |  |  |  |  | plain", $"the functions give NULL for what isn't there: {nulls[0]}");
        return $"edid(), name(), winner() and versions() equal Mutagen for all {winners.Count:N0} winning weapons of the load order; the Name column and text(value, 'de' / 'French' / 'pl') equal Mutagen's lookup by language for {texts.Rows.Count} weapons of Starfield.esm ({differ} have a German name that differs from the English one); a FormKey that isn't there gives NULL";
    }

    // ------------------------------------------------------------------ Cost said first
    public static string CostNotice(Ctx c)
    {
        var said = new List<string>();
        c.Ok("SELECT count(*) FROM Weapon", SF);
        using var probe = new SqliteConnectionHolder(c.Engine.PluginFile(c.Index.Plugin(SF)!).Path);
        string before = probe.State();
        foreach (var (sql, must) in new[]
        {
            // (a count for one base is read from the records' bytes, its rows alone kept, and needs no notice: J30)
            ("SELECT Base, count(*) FROM PlacedObject GROUP BY Base", "PlacedObject in Starfield.esm is 3,291,860 records"),
            ("SELECT * FROM PlacedObject LIMIT 1", "PlacedObject in Starfield.esm is 3,291,860 records"),
            ("SELECT count(*) FROM Cell WHERE EditorID LIKE 'x%' AND Json LIKE '%x%'", "Cell in Starfield.esm is"),
            ("SELECT count(*) FROM NavigationMeshInfoMap WHERE Json LIKE '%x%'", "NavigationMeshInfoMap in Starfield.esm is 1 record ("),
            ("SELECT count(*) FROM SurfaceTree WHERE Json LIKE '%x%'", "SurfaceTree in Starfield.esm is"),
            // (a navmesh's geometry lists are read from the records' own bytes since Mutagen lays NVNM out, GAPS S153: a
            // count of its 9,955,594 vertices takes 5.5 s and needs no notice; every navmesh with its JSON still does)
            ("SELECT count(*) FROM NavigationMesh WHERE Json LIKE '%x%'", "NavigationMesh in Starfield.esm is"),
        })
        {
            var a = c.Sql(sql, SF);
            Check(a.NotRun != null && a.NotRun.What.Contains(must) && a.NotRun.What.Contains("allow_heavy=true") && a.Cost.Read.Count == 0 && a.Rows.Count == 0,
                $"\"{sql}\" must answer with its cost and run nothing: {a.Error ?? a.NotRun?.What ?? a.TotalRows + " rows, read " + string.Join(", ", a.Cost.Read)}");
            Check(a.ToText().StartsWith("Not run. "), "the text form starts with Not run.");
            said.Add(a.NotRun!.What);
        }
        Check(probe.State() == before, "a refused query changed the scratch tables");
        // Allowed, but the memory isn't there: refused at once
        using var tight = new QueryEngine(c.Records, new QueryOptions { ScratchFolder = Path.Combine(c.Work, "sql"), HeavyGate = (mb, what) => $"needs about {(mb + 4096) / 1024.0:F1} GB, 0.5 GB is free" });
        var refused = tight.Run(new QueryRequest { Sql = "SELECT * FROM PlacedObject LIMIT 1", Plugins = new[] { SF }, AllowHeavy = true });
        Check(refused.Error != null && refused.Error.Contains("needs about") && refused.Error.Contains("GB is free") && probe.State() == before, $"a heavy read with too little memory must fail at once: {refused.Error}");
        // The world types' estimates are the measured ones (fix C14), and a read the disk hasn't room for is refused at once
        foreach (string t in new[] { "Cell", "NavigationMeshInfoMap", "SurfaceTree", "NavigationMesh" })
            Check(said.Any(s => s.Contains($"rates measured on Starfield.esm's whole {t}")), $"the estimate for {t} says it is measured: {string.Join(" / ", said)}");
        using var noRoom = new QueryEngine(c.Records, new QueryOptions { ScratchFolder = Path.Combine(c.Work, "sql"), ScratchSpareMB = 1L << 40 });
        var noDisk = noRoom.Run(new QueryRequest { Sql = "SELECT count(*) FROM SurfaceTree WHERE Json LIKE '%x%'", Plugins = new[] { SF }, AllowHeavy = true });
        Check(noDisk.Error != null && noDisk.Error.Contains("of free disk for its scratch files") && noDisk.Error.Contains("rates measured") && probe.State() == before, $"a heavy read the disk hasn't room for must fail at once: {noDisk.Error}");
        // GAPS S4 (fix-s4s5-world-reads): no column of a world type is read out of each record's JSON any more. The
        // columns J75 (S101) found slow that way (20 to 112 s for Cell's Flags) are read from the records' own bytes or
        // straight from Mutagen's object and run unasked; a column of every one of the 3.29 million placed objects
        // still asks first (QueryOptions.HeavyRecords), but says it reads the bytes, not the JSON
        var types = new List<string>();
        foreach (var (type, column, how) in new[]
        {
            ("Cell", "Flags", "from the records' own bytes"), ("Cell", "Name", "from the records' own bytes"), ("NavigationMesh", "MajorFlags", "from the records' own bytes"),
            ("NavigationMeshInfoMap", "StarfieldMajorRecordFlags", "from the records' own bytes"), ("SurfaceTree", "StarfieldMajorRecordFlags", "from the records' own bytes"),
            ("Cell", "Location", "from the records' own bytes"), ("NavigationMesh", "NavmeshGeometry_NavmeshVersion", "from the records' own bytes"),
            ("SurfaceTree", "EdgeSize", "from the records' own bytes"),
        })
        {
            string sql = $"SELECT count(*) FROM {type} WHERE {column} IS NOT NULL";
            // Each on scratch tables of its own, so that each is read as it would be first
            using var alone = new QueryEngine(c.Records, new QueryOptions { ScratchFolder = Path.Combine(c.Work, "sql-cost-" + type + "-" + column) });
            var a = alone.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF }, PageRows = 1_000_000 });
            Check(a.NotRun == null && a.Error == null && a.Cost.Read.Count == 1 && a.Cost.Read[0].Contains(how, StringComparison.Ordinal) && !a.Cost.Read[0].Contains("JSON", StringComparison.Ordinal),
                $"\"{sql}\" runs unasked, {how}: {a.Error ?? a.NotRun?.What ?? a.Cost.Text}");
            types.Add($"{type}.{column} {a.Cost.Seconds:F1} s");
        }
        // GAPS S146 (J99): on one engine, a column read before doesn't join the next read of the type: Cell's Name (straight
        // from the records, through Mutagen) and then Location reads Location alone from the bytes (8.6 s with Name before
        // J99, 0.7 s now), and a statement naming both joins the two tables, with the counts each gives alone
        using (var both = new QueryEngine(c.Records, new QueryOptions { ScratchFolder = Path.Combine(c.Work, "sql-s146") }))
        {
            QueryAnswer Count(string sql) => both.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF }, PageRows = 1_000_000 });
            var name = Count("SELECT count(*) FROM Cell WHERE Name IS NOT NULL");
            var loc = Count("SELECT count(*) FROM Cell WHERE Location IS NOT NULL");
            Check(name.Error == null && name.NotRun == null && loc.Error == null && loc.NotRun == null && loc.Cost.Read.Count == 1
                && loc.Cost.Read[0].Contains("column Location, from the records' own bytes", StringComparison.Ordinal),
                $"S146: Location after Name is read alone, from the bytes: {loc.Error ?? loc.NotRun?.What ?? loc.Cost.Text}");
            var all = Count("SELECT count(*), count(Name), count(Location) FROM Cell");
            Check(all.Error == null && Rows(all)[0] == $"{Rows(Count("SELECT count(*) FROM Cell"))[0]} | {Rows(name)[0]} | {Rows(loc)[0]}",
                $"S146: Name and Location from their two tables: {(all.Error ?? string.Join(" / ", Rows(all)))} against {Rows(name)[0]} and {Rows(loc)[0]}");
            said.Add($"S146: Cell's Location after its Name read alone from the bytes in {loc.Cost.Seconds:F1} s; both from their two tables: {Rows(all)[0]}");
        }
        var placed = c.Sql("SELECT count(*) FROM PlacedObject WHERE StarfieldMajorRecordFlags IS NOT NULL", SF);
        Check(placed.NotRun != null && placed.NotRun.What.Contains("PlacedObject in Starfield.esm is 3,291,860 records") && placed.NotRun.What.Contains("from the records' own bytes") && !placed.NotRun.What.Contains("JSON"),
            $"a column of every placed object asks first, read from the bytes: {placed.Error ?? placed.NotRun?.What ?? placed.Cost.Text}");
        Check(c.Schema.TypesOf("LAND").Count == 0 && c.Index.Plugin(SF)!.Segment!.Signatures.All(s => s.Signature != "LAND"), "Starfield has no LAND records (no type in the schema, none in Starfield.esm)");
        // Over the load order the same (a set scope reads each plugin's cells in turn)
        var lo = c.Lo("SELECT count(*) FROM Cell WHERE Flags IS NOT NULL", ScopeKind.Winners);
        Check(lo.NotRun == null && lo.Error == null && lo.Cost.Read.Any(r => r.Contains("from the records' own bytes", StringComparison.Ordinal)), $"the load order's cells' Flags run from the bytes: {lo.Error ?? lo.NotRun?.What ?? lo.Cost.Text}");
        said.Add($"read without the JSON, unasked: {string.Join(", ", types)}; the load order's cells' Flags in {lo.Cost.Seconds:F1} s");
        // A small plugin's placed objects are under the limit and just run
        var small = c.Ok("SELECT count(*) FROM PlacedObject", "kgcdoom.esm");
        Check(small.NotRun == null, "a small plugin's placed objects need no notice");
        c.Save("cost-notice.txt", said);
        return $"{said.Count} heavy queries on Starfield.esm answered with their cost and ran nothing (the scratch tables stayed as they were): \"{Cut(said[0], 260)}\"; with heavy work allowed and too little memory the answer is \"{refused.Error}\"; kgcdoom.esm's {Rows(small)[0]} placed objects run without a notice";
    }

    sealed class SqliteConnectionHolder(string path) : IDisposable
    {
        public string State()
        {
            using var con = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT (SELECT count(*) FROM Rec) || '/' || (SELECT count(*) FROM Done) || '/' || (SELECT count(*) FROM Proj) || '/' || (SELECT count(*) FROM sqlite_master)";
            return (string)cmd.ExecuteScalar()!;
        }
        public void Dispose() { }
    }

    // ------------------------------------------------------------------ Time
    static readonly HashSet<string> WorldSignatures = new(StringComparer.Ordinal)
        { "CELL", "WRLD", "REFR", "ACHR", "PGRE", "PHZD", "PARW", "PBAR", "PBEA", "PCON", "PFLA", "PMIS", "NAVM", "NAVI", "LAND", "SFTR" };

    public static string Timing(Ctx c)
    {
        var engine = c.NewEngine("sql-timing");
        QueryAnswer Run(string sql, string plugin = SF) => Must(engine.Run(new QueryRequest { Sql = sql, Plugins = new[] { plugin }, PageRows = 1000 }), sql);
        // The one-time start-up of the reading code, on a type of another plugin that isn't measured
        var warmUp = Run("SELECT count(*) FROM Keyword", "kgcdoom.esm");
        var p = c.Index.Plugin(SF)!;
        var lines = new List<string> { "Sig\tType\tRecords\tFileMB\tFirstReadSeconds\tEstimateSeconds\tSecondQueryMs\tRowsQueryMs\tFieldFilterMs" };
        var first = new List<(string Type, int Records, double Seconds)>(); var second = new List<(string Type, double Ms)>(); var rowsQ = new List<(string Type, double Ms)>(); var field = new List<(string Type, double Ms)>();
        foreach (var s in p.Segment!.Signatures.OrderBy(s => s.Signature, StringComparer.Ordinal))
        {
            if (WorldSignatures.Contains(s.Signature) || c.Schema.TypesOf(s.Signature).Count == 0 || s.Count == 0) continue;
            string type = engine.LabelOf(s.Signature);
            var a = Run($"SELECT count(*) FROM {type}");
            Check(a.Cost.Read.Count == 1, $"{type} should have been read by this query: {a.Cost.Text}");
            Check((long)a.Rows[0][0]! == s.Count, $"{type}: the view has {a.Rows[0][0]} rows, the header index {s.Count} {s.Signature} records{(a.Notes.Count > 0 ? " (" + a.Notes[0] + ")" : "")}");
            // Twice each, the better one: other sessions share this PC
            double Best(string sql) => Math.Min(Run(sql).Cost.Seconds, Run(sql).Cost.Seconds) * 1000;
            double again = Best($"SELECT count(*) FROM {type} WHERE EditorID LIKE 'a%'");
            double rows = Best($"SELECT * FROM {type} ORDER BY EditorID LIMIT 40");
            string? column = engine.Views.Find(type)!.Columns.FirstOrDefault(col => col.Kind is "Float" or "Int32" or "UInt32" or "FormLink" or "Bool" && col.SchemaPath.IndexOf('.') < 0 && !col.Name.StartsWith("Version") && col.Name is not ("MajorRecordFlagsRaw" or "FormVersion"))?.Name;
            double filter = column == null ? 0 : Best($"SELECT count(*) FROM {type} WHERE \"{column}\" IS NOT NULL AND \"{column}\" <> 0");
            first.Add((type, s.Count, a.Cost.Seconds)); second.Add((type, again)); rowsQ.Add((type, rows)); field.Add((type, filter));
            lines.Add($"{s.Signature}\t{type}\t{s.Count}\t{s.Bytes / 1048576.0:F2}\t{a.Cost.Seconds:F3}\t{CostModel.Full(CostModel.Load(p, s.Signature)).Seconds:F2}\t{again:F1}\t{rows:F1}\t{filter:F1}");
        }
        c.Save("timing.tsv", lines);
        var slow = first.OrderByDescending(f => f.Seconds).ToList();
        var over = slow.Where(f => f.Seconds > 3).ToList();
        var slowSecond = second.OrderByDescending(x => x.Ms).First(); var slowRows = rowsQ.OrderByDescending(x => x.Ms).First(); var slowField = field.OrderByDescending(x => x.Ms).ToList();
        string summary = $"{first.Count} research types of Starfield.esm ({first.Sum(f => (long)f.Records):N0} records) read for the first time in {first.Sum(f => f.Seconds):F1} s in all; the slowest: {string.Join(", ", slow.Take(4).Select(f => $"{f.Type} {f.Seconds:F2} s ({f.Records:N0})"))}; {first.Count(f => f.Seconds < 0.1)} types under 0.1 s. "
            + $"A second query on a type (a count with a filter on EditorID): {second.Average(x => x.Ms):F1} ms on average, the slowest {slowSecond.Type} {slowSecond.Ms:F0} ms; forty whole rows in EditorID order: {rowsQ.Count(x => x.Ms <= 50)} of {rowsQ.Count} types under 50 ms, the slowest {slowRows.Type} {slowRows.Ms:F0} ms. "
            + $"A filter on a field of every row: {field.Count(x => x.Ms <= 50)} of {field.Count} types under 50 ms; over it: {string.Join(", ", slowField.Where(x => x.Ms > 50).Take(6).Select(x => $"{x.Type} {x.Ms:F0} ms"))}. "
            + $"The start-up before the first read in a new process: {warmUp.Cost.Seconds:F2} s (Keyword of kgcdoom.esm)";
        Check(over.Count == 0, $"{over.Count} types took over 3 s to read: {string.Join(", ", over.Select(f => $"{f.Type} {f.Seconds:F2} s"))}. {summary}");
        Check(slowSecond.Ms < 50, $"a second query took over 50 ms. {summary}");
        return summary;
    }

    // ------------------------------------------------------------------ Every view
    public static string EveryViewRuns(Ctx c)
    {
        var p = c.Index.Plugin(SF)!;
        var fallbacks = new[] { "kgcdoom.esm", "darkstar.esm", "ShatteredSpace.esm", "sfbgs003.esm", "SFBGS00D.esm" }.Where(n => c.Index.Plugin(n)?.Segment != null).ToArray();
        int withRows = 0, empty = 0, heavy = 0, heavyElsewhere = 0, lists = 0, listRows = 0, typesInSchema = 0, jsonChecked = 0;
        var errors = new List<string>(); var lines = new List<string>(); var heavyNames = new List<string>(); var noRecordAnywhere = new List<string>();
        foreach (string root in c.Engine.Views.RootNames.OrderBy(r => r, StringComparer.Ordinal))
        {
            typesInSchema++;
            var cat = c.Engine.Views.Catalog(root);
            string plugin = SF;
            var a = c.Sql($"SELECT * FROM {root} LIMIT 3", SF);
            if (a.NotRun != null)
            {
                heavy++; heavyNames.Add(root);
                // Too big to read whole from Starfield.esm without being asked: the same view on a smaller plugin that has the type
                string sig = c.Schema.SignatureOf(cat.Typed.Types[0])!;
                string? other = fallbacks.FirstOrDefault(f => c.Index.Plugin(f)!.Segment!.Signatures.Any(s => s.Signature == sig && s.Count > 0) && c.Sql($"SELECT * FROM {root} LIMIT 3", f).Ok);
                if (other == null) { noRecordAnywhere.Add(root); lines.Add($"{root}\theavy in Starfield.esm, and in no smaller plugin tried\t{a.NotRun.What}"); continue; }
                plugin = other; heavyElsewhere++;
                a = c.Sql($"SELECT * FROM {root} LIMIT 3", plugin);
            }
            if (a.Error != null) { errors.Add($"{root}: {a.Error}"); lines.Add($"{root}\tERROR\t{a.Error}"); continue; }
            Check(a.Columns.SequenceEqual(c.Engine.ColumnsOf(root, ScopeKind.Plugins)!), $"{root}: the view's columns aren't the ones the engine names");
            if (a.TotalRows == 0)
            {
                Check(a.Notes.Any(n => n.StartsWith("No rows:")), $"{root} is empty in {plugin} and doesn't say why");
                empty++;
            }
            else withRows++;
            jsonChecked += JsonStaysJson(c, cat.Typed, plugin, errors);
            lines.Add($"{root}\t{plugin}\t{a.TotalRows} row(s) of {a.Columns.Count} columns\t{cat.Lists.Count} list views");
            foreach (var list in cat.Lists.Values)
            {
                var l = c.Sql($"SELECT * FROM {list.Name} LIMIT 2", plugin);
                lists++;
                if (l.Error != null) { errors.Add($"{list.Name}: {l.Error}"); lines.Add($"{list.Name}\tERROR\t{l.Error}"); }
                else if (l.NotRun != null) { errors.Add($"{list.Name}: {l.NotRun.What}"); }
                else if (l.TotalRows > 0) { listRows++; jsonChecked += JsonStaysJson(c, list, plugin, errors); }
            }
        }
        // The special views' JSON columns too (GAPS S80)
        foreach (var (sql, plugin) in new[]
        {
            ("SELECT count(*) FROM Records WHERE Type = 'Weapon' AND json_type(json_object('j', Json), '$.j') IS NOT 'object'", "kgcdoom.esm"),
            ("SELECT count(*) FROM Conditions WHERE Type = 'Quest' AND (json_type(json_object('j', Flags), '$.j') IS NOT 'array' OR json_type(json_object('j', Json), '$.j') IS NOT 'object')", "sfta01.esm"),
            ("SELECT count(*) FROM Plugins WHERE json_type(json_object('j', Masters), '$.j') IS NOT 'array'", "kgcdoom.esm"),
        })
        {
            var a = c.Sql(sql, plugin);
            if (a.Error != null || a.NotRun != null) errors.Add($"{sql}: {a.Error ?? a.NotRun!.What}");
            else if ((long)a.Rows[0][0]! != 0) errors.Add($"{sql}: {a.Rows[0][0]} value(s) come back as text inside json_object (GAPS S80)");
            else jsonChecked++;
        }
        c.Save("views-all.tsv", lines);
        Check(errors.Count == 0, $"{errors.Count} views failed: {Cut(string.Join("  ||  ", errors.Take(8)), 2000)}");
        int types = c.Schema.Model.RecordTypes.Count;
        Check(c.Schema.Model.RecordTypes.All(t => c.Engine.Views.Find(t.Name) != null), "a record type of the schema has no view");
        return $"all {types} record types of the schema have a view ({typesInSchema} typed views with the 2 union views; the type ObjectModification is rows of the union view of the same name). On Starfield.esm: {withRows + empty - heavyElsewhere} ran without an SQL error ({withRows - heavyElsewhere} with rows, {empty} empty with the reason said), {heavy} answered with their cost ({string.Join(", ", heavyNames)}) and ran on a smaller plugin that has the type ({heavyElsewhere}){(noRecordAnywhere.Count > 0 ? $"; not run anywhere: {string.Join(", ", noRecordAnywhere)}" : "")}. {lists:N0} list views ran too, {listRows:N0} of them with rows. {jsonChecked:N0} JSON columns (Json, flags, objects too deep for columns) stay JSON inside json_object (GAPS S80)";
    }

    /// <summary>
    /// GAPS S80: each JSON column of a view (its Json, its flags, an object too deep for columns of its own) is JSON
    /// inside SQLite's JSON functions: json_object embeds it as the object or array it is, not as a string. One column
    /// of each kind per view, over the view's first rows. Returns how many columns were checked.
    /// </summary>
    static int JsonStaysJson(Ctx c, ViewSpec v, string plugin, List<string> errors)
    {
        var cols = v.Columns.Where(ViewBuilder.IsJson).GroupBy(x => x.Kind).Select(g => g.First().Name).ToList();
        if (!v.IsList || !v.ScalarEntries) cols.Add("Json");
        int n = 0;
        foreach (string col in cols)
        {
            string q = SqlText.Name(col);
            // The JSON functions over the view's column itself: a value that leaves a subquery loses SQLite's mark anyway
            var a = c.Sql($"SELECT count(*) FROM (SELECT {q} IS NOT NULL AS has, json_type(json_object('j', {q}), '$.j') AS inside, json_type({q}) AS own FROM {v.Name} LIMIT 20) WHERE has AND inside IS NOT own", plugin);
            if (a.Error != null || a.NotRun != null) { errors.Add($"{v.Name}.{col} as JSON: {a.Error ?? a.NotRun!.What}"); continue; }
            if ((long)a.Rows[0][0]! != 0) errors.Add($"{v.Name}.{col}: {a.Rows[0][0]} value(s) come back as text inside json_object, not as JSON (GAPS S80)");
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------ Two processes
    static readonly (string Sql, string Plugin)[] SharedQueries =
    {
        ("SELECT FormKey, EditorID, AttackDamage, HasScope FROM Weapon ORDER BY FormKey", SF),
        ("SELECT FormKey, Idx, Value FROM Weapon_Keywords ORDER BY FormKey, Idx", SF),
        ("SELECT FormKey, EditorID, Race, Flags FROM Npc ORDER BY FormKey", SF),
        ("SELECT FormKey, EditorID, Weight, Value FROM Armor ORDER BY FormKey", SF),
        ("SELECT FormKey, Type, Name FROM ObjectModification ORDER BY FormKey", SF),
        ("SELECT FormKey, CreatedObject, AmountProduced FROM ConstructibleObject ORDER BY FormKey", SF),
        ("SELECT FormKey, Idx, Reference, Count FROM LeveledItem_Entries ORDER BY FormKey, Idx", SF),
        ("SELECT FormKey, EditorID FROM Keyword ORDER BY FormKey", SF),
        ("SELECT FormKey, EditorID, Name FROM Perk ORDER BY FormKey", SF),
        ("SELECT FormKey, EditorID FROM Quest ORDER BY FormKey", SF),
        ("SELECT FormKey, length(Json) FROM GenericBaseForm ORDER BY FormKey", SF),
        ("SELECT FormKey, EditorID, Name FROM MiscItem ORDER BY FormKey", SF),
        ("SELECT FromKey, Field, Idx, ToKey FROM Links WHERE FromKey IN (SELECT FormKey FROM Weapon UNION ALL SELECT FormKey FROM Armor) ORDER BY FromKey, Path", SF),
        ("SELECT FormKey, Base FROM PlacedObject ORDER BY FormKey", "kgcdoom.esm"),
        ("SELECT FormKey, EditorID, Name FROM Weapon ORDER BY FormKey", "darkstar.esm"),
        ("SELECT FormKey, Type FROM ObjectModification ORDER BY FormKey", "darkstar.esm"),
        ("SELECT Sig, count(*) FROM Forms GROUP BY Sig ORDER BY Sig", "darkstar.esm"),
        ("SELECT FormKey, EditorID FROM Static ORDER BY FormKey", SF),
        ("SELECT FormKey, EditorID, Name FROM Book ORDER BY FormKey", SF),
        ("SELECT FormKey, EditorID FROM Scene ORDER BY FormKey", SF),
    };

    static string HashRows(QueryAnswer a)
    {
        var h = new XxHash128();
        foreach (var r in a.Rows) { h.Append(Encoding.UTF8.GetBytes(string.Join("|", r.Select(Cell)))); h.Append(new byte[] { 10 }); }
        return Convert.ToHexString(h.GetCurrentHash());
    }

    /// <summary>One of the two processes: waits for the start file, runs the shared queries on the shared scratch
    /// folder, prints a line for each.</summary>
    public static int Child(string data, string work, string name, string startFile)
    {
        try
        {
            using var c = new Ctx(data, work, null, false, "sql-shared");
            Console.WriteLine("READY");
            Console.Out.Flush();
            for (int i = 0; !File.Exists(startFile); i++) { if (i > 60000) return 3; Thread.Sleep(5); }
            // One child goes through the list forwards, the other from the middle, so they meet on the same types
            int n = SharedQueries.Length, shift = name == "A" ? 0 : n / 2;
            for (int k = 0; k < n; k++)
            {
                int i = (k + shift) % n;
                var (sql, plugin) = SharedQueries[i];
                var a = c.Sql(sql, plugin);
                if (!a.Ok) { Console.WriteLine($"Q\t{i}\tERROR\t{a.Error ?? a.NotRun?.What}"); continue; }
                Console.WriteLine($"Q\t{i}\t{HashRows(a)}\t{a.TotalRows}\t{string.Join("; ", a.Cost.Read)}");
            }
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"CRASH\t{e.GetType().Name}: {e.Message}");
            return 4;
        }
    }

    public static string TwoProcesses(Ctx c)
    {
        string shared = Path.Combine(c.Work, "sql-shared");
        if (Directory.Exists(shared)) Directory.Delete(shared, true);
        string start = Path.Combine(c.Work, "start-" + Guid.NewGuid().ToString("N"));
        string exe = Environment.ProcessPath!;
        var children = new[] { "A", "B" }.Select(name =>
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in new[] { "--child", c.Data, c.Work, name, start }) psi.ArgumentList.Add(arg);
            var proc = Process.Start(psi)!;
            var output = new List<string>();
            var ready = new ManualResetEventSlim();
            proc.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (output) output.Add(e.Data); if (e.Data == "READY") ready.Set(); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.Add("ERR\t" + e.Data); };
            proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
            return (name, proc, output, ready);
        }).ToList();
        try
        {
            foreach (var ch in children) Check(ch.ready.Wait(TimeSpan.FromMinutes(3)), $"child {ch.name} didn't get ready");
            File.WriteAllText(start, "go");
            foreach (var ch in children) { Check(ch.proc.WaitForExit(600_000), $"child {ch.name} didn't finish"); ch.proc.WaitForExit(); }
        }
        finally
        {
            foreach (var ch in children) if (!ch.proc.HasExited) ch.proc.Kill();
            if (File.Exists(start)) File.Delete(start);
        }
        var results = new Dictionary<string, Dictionary<int, (string Hash, string Rows, string Read)>>();
        foreach (var ch in children)
        {
            Check(ch.proc.ExitCode == 0, $"child {ch.name} ended with {ch.proc.ExitCode}: {Cut(string.Join(" / ", ch.output.TakeLast(4)))}");
            var bad = ch.output.Where(l => l.StartsWith("ERR") || l.StartsWith("CRASH") || l.Contains("\tERROR\t")).ToList();
            Check(bad.Count == 0, $"child {ch.name} had errors: {Cut(string.Join(" / ", bad.Take(3)))}");
            results[ch.name] = ch.output.Where(l => l.StartsWith("Q\t")).Select(l => l.Split('\t')).ToDictionary(f => int.Parse(f[1]), f => (f[2], f[3], f.Length > 4 ? f[4] : ""));
            Check(results[ch.name].Count == SharedQueries.Length, $"child {ch.name} answered {results[ch.name].Count} of {SharedQueries.Length} queries");
        }
        // The same queries in this process on tables of its own
        var alone = c.NewEngine("sql-alone");
        int readsA = 0, readsB = 0; long rows = 0;
        var typesRead = new Dictionary<string, int>();
        for (int i = 0; i < SharedQueries.Length; i++)
        {
            var (sql, plugin) = SharedQueries[i];
            var a = Must(alone.Run(new QueryRequest { Sql = sql, Plugins = new[] { plugin }, PageRows = 1_000_000 }), sql);
            string hash = HashRows(a);
            Check(results["A"][i].Hash == hash && results["B"][i].Hash == hash, $"\"{sql}\" on {plugin}: the two processes sharing a scratch folder gave {results["A"][i].Rows} and {results["B"][i].Rows} rows, this process alone {a.TotalRows}; the rows differ");
            rows += a.TotalRows;
            foreach (var (who, r) in new[] { ("A", results["A"][i]), ("B", results["B"][i]) })
                foreach (string read in r.Read.Split("; ", StringSplitOptions.RemoveEmptyEntries))
                {
                    if (who == "A") readsA++; else readsB++;
                    // What was read: the type, and for a read of named columns alone which ones ("Weapon (406, columns
                    // EditorID, Name, from the records' own bytes) from ..."). One query's named columns and another's
                    // whole records are two reads of a type, each done once (GAPS S188); the same read twice is the fault
                    int open = read.IndexOf('('), close = read.IndexOf(") from ", StringComparison.Ordinal);
                    int comma = open >= 0 && close > open ? read.IndexOf(", ", open, close - open, StringComparison.Ordinal) : -1;
                    string key = plugin + ":" + read.Split(' ')[0] + (comma < 0 ? "" : " [" + read[(comma + 2)..close] + "]");
                    typesRead[key] = typesRead.GetValueOrDefault(key) + 1;
                }
        }
        // Links are indexed in steps, for the types read since the last step: both processes may do a step, never the same one
        var twice = typesRead.Where(t => t.Value > 1 && !t.Key.EndsWith(":Links")).Select(t => t.Key).ToList();
        Check(twice.Count == 0, $"the same read went into the same scratch file from both processes: {string.Join(", ", twice)}");
        Check(readsA > 0 && readsB > 0, $"both processes should have filled tables: A did {readsA} reads, B {readsB}");
        return $"two processes ran the same {SharedQueries.Length} queries ({rows:N0} rows) at once on one empty scratch folder, one from the start of the list and one from the middle: no error, every query's rows equal between the two and equal to a third run alone on tables of its own; {readsA + readsB} reads filled the shared tables, {readsA} by one process and {readsB} by the other, no record type by both";
    }

    // ------------------------------------------------------------------ World types: only the columns a query names
    public static string WorldColumns(Ctx c)
    {
        // A limit this low makes kgcdoom.esm's placed objects "too big to read whole", so the same route is taken
        // that Starfield.esm's 3.29 million take: only the named columns are read, and no JSON is stored.
        string folder = Path.Combine(c.Work, "sql-columns");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        using var low = new QueryEngine(c.Records, new QueryOptions { ScratchFolder = folder, HeavySeconds = 0.001 });
        const string plugin = "kgcdoom.esm";
        var refused = low.Run(new QueryRequest { Sql = "SELECT Base, count(*) FROM PlacedObject GROUP BY Base", Plugins = new[] { plugin } });
        Check(refused.NotRun != null && refused.NotRun.What.Contains("only the column the query names: Base"), $"under the low limit the query must be announced with the columns it reads: {refused.Error ?? refused.NotRun?.What}");
        QueryAnswer Low(string sql) => Must(low.Run(new QueryRequest { Sql = sql, Plugins = new[] { plugin }, AllowHeavy = true, PageRows = 1_000_000 }), sql);
        // Over a set of plugins too (J63, GAPS S89: a set read the type whole, as JSON): the winners scope and nine plugins
        var setRefused = low.Run(new QueryRequest { Sql = "SELECT Base, count(*) FROM PlacedObject GROUP BY Base", Scope = ScopeKind.Winners });
        Check(setRefused.NotRun != null && setRefused.NotRun.What.Contains("only the column the query names: Base, from the records' own bytes") && setRefused.NotRun.What.Contains(" plugins is "),
            $"over the load order the query must be announced with the column it reads from the bytes: {setRefused.Error ?? setRefused.NotRun?.What}");
        var nine = c.Index.Plugins.Where(p => p.Segment != null && p.Problems.MissingMasters.Count == 0 && p.LeftOut == null && p.Stamp.Length < 32 << 20 && p.Segment.Signatures.Any(s => s.Signature == "REFR" && s.Count > 0))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(9).Select(p => p.Name).ToArray();
        var nineRefused = low.Run(new QueryRequest { Sql = "SELECT Base, count(*) FROM PlacedObject GROUP BY Base", Plugins = nine });
        Check(nineRefused.NotRun != null && nineRefused.NotRun.What.Contains("PlacedObject in 9 plugins is") && nineRefused.NotRun.What.Contains("only the column the query names: Base"),
            $"over nine plugins (one scratch file) the query must be announced with the column it reads: {nineRefused.Error ?? nineRefused.NotRun?.What}");
        var mod = c.Mod(plugin);
        var placed = mod.EnumerateMajorRecords<IPlacedObjectGetter>().ToList();

        var byBase = Low("SELECT Base, count(*) FROM PlacedObject GROUP BY Base");
        Same("placed objects by base", Rows(byBase), placed.GroupBy(r => r.Base.IsNull ? "" : r.Base.FormKey.ToString()).Select(g => Row(g.Key, g.Count())).ToList(), ordered: false);
        Check(byBase.Cost.Read.Count == 1 && byBase.Cost.Read[0].Contains("column Base"), $"only the column Base should have been read: {byBase.Cost.Text}");
        var count = Low("SELECT count(*) FROM PlacedObject");
        Check(Rows(count)[0] == placed.Count.ToString() && count.Cost.Read.Count == 0, $"a count needs nothing more read: {Rows(count)[0]}, {count.Cost.Text}");
        // More columns: read again with the union, and equal to the typed view over the whole records
        const string wide = "SELECT FormKey, EditorID, IsOverride, Base, Scale, Position, Rotation, Location, Layer FROM PlacedObject ORDER BY FormKey";
        var named = Low(wide);
        var whole = c.Ok(wide, plugin);
        Same("named columns against the whole records", Rows(named), Rows(whole), ordered: true);
        Check(named.Cost.Read.Count == 1 && !named.Cost.Read[0].Contains("Json"), $"the wider query reads its columns once: {named.Cost.Text}");
        var again = Low("SELECT count(*) FROM PlacedObject WHERE Base IS NOT NULL AND Scale > 0");
        Check(again.Cost.Read.Count == 0, "columns already read aren't read again");
        // Nothing of the records was stored
        using (var con = new SqliteConnection($"Data Source={low.PluginFile(c.Index.Plugin(plugin)!).Path};Mode=ReadOnly;Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT (SELECT count(*) FROM Rec) || ' records stored, ' || (SELECT count(*) FROM Proj) || ' column tables'";
            string state = (string)cmd.ExecuteScalar()!;
            Check(state == "0 records stored, 2 column tables", $"the named-columns route must store no record: {state}");
        }
        // Asking for Json needs the whole records: announced as such
        var json = low.Run(new QueryRequest { Sql = "SELECT Json FROM PlacedObject LIMIT 1", Plugins = new[] { plugin } });
        Check(json.NotRun != null && !json.NotRun.What.Contains("only the column"), "asking for Json needs the whole records");

        string heavy = "";
        if (c.Heavy)
        {
            // Starfield.esm's 3.29 million placed objects against Mutagen's own walk over them
            var clock = Stopwatch.StartNew();
            var counts = new Dictionary<FormKey, int>();
            int all = 0;
            foreach (var r in c.Mod(SF).EnumerateMajorRecords<IPlacedObjectGetter>()) { all++; if (!r.Base.IsNull) counts[r.Base.FormKey] = counts.GetValueOrDefault(r.Base.FormKey) + 1; }
            double mutagenSeconds = clock.Elapsed.TotalSeconds;
            var top = counts.OrderByDescending(k => k.Value).First();
            // A column of all 3.29 million is announced first; a count for one base reads them from their bytes and keeps
            // only that base's rows (J30), so it isn't heavy
            var notice = c.Sql("SELECT Base, count(*) FROM PlacedObject GROUP BY Base", SF);
            Check(notice.NotRun != null, "Starfield.esm's placed objects must be announced first");
            var sf = c.Engine.Run(new QueryRequest { Sql = $"SELECT count(*) FROM PlacedObject WHERE Base = '{top.Key}'", Plugins = new[] { SF } });
            Must(sf, "the count on Starfield.esm, not heavy");
            Check(sf.Cost.Read.Count == 1 && sf.Cost.Read[0].Contains("only those kept", StringComparison.Ordinal), $"the count for one base keeps only its rows: {sf.Cost.Text}");
            Check((long)sf.Rows[0][0]! == top.Value, $"Starfield.esm: SQL counts {sf.Rows[0][0]} placed objects with the base {top.Key}, Mutagen {top.Value}");
            var groups = c.Engine.Run(new QueryRequest { Sql = "SELECT Base, count(*) FROM PlacedObject WHERE Base IS NOT NULL GROUP BY Base", Plugins = new[] { SF }, AllowHeavy = true, PageRows = 10_000_000 });
            Same("every base of Starfield.esm's placed objects", Rows(Must(groups, "bases")), counts.Select(k => Row(k.Key, k.Value)).ToList(), ordered: false);
            var total = c.Engine.Run(new QueryRequest { Sql = "SELECT count(*) FROM PlacedObject", Plugins = new[] { SF }, AllowHeavy = true });
            Check((long)total.Rows[0][0]! == all, $"Starfield.esm: SQL counts {total.Rows[0][0]} placed objects, Mutagen {all}");
            heavy = $" Starfield.esm: \"{Cut(notice.NotRun!.What, 200)}\"; allowed, the count for the most placed base {c.Records.Label(top.Key.ToString())} is {top.Value:N0}, equal to Mutagen's, in {sf.Cost.Seconds:F1} s ({sf.Cost.Text}); all {counts.Count:N0} bases' counts equal Mutagen's ({groups.Cost.Seconds * 1000:F0} ms once read); Mutagen's own walk over the {all:N0} took {mutagenSeconds:F1} s.";
        }
        return $"{placed.Count:N0} placed objects of kgcdoom.esm through the named-columns route (forced by a low limit): counts by Base equal Mutagen's; 9 named columns equal the typed view over the whole records for every row; a count reads nothing more; no record's JSON was stored (0 rows in Rec); asking for Json is announced as a read of the whole records.{heavy}";
    }
}
