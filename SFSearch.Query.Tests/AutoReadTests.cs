// Records and Links read what a statement narrows to (fix B8, 2026-11-12): a query over them that names no type's
// view used to see only what earlier queries had read, often nothing, and say so in a note.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class AutoReadTests
{
    const string SF = "Starfield.esm", KGC = "kgcdoom.esm";

    static QueryAnswer Run(QueryEngine e, string sql, string plugin, bool heavy = false) =>
        e.Run(new QueryRequest { Sql = sql, Plugins = new[] { plugin }, PageRows = 1_000_000, AllowHeavy = heavy });

    /// <summary>An engine on scratch tables of its own, empty.</summary>
    static QueryEngine Fresh(Ctx c, string folder)
    {
        string path = Path.Combine(c.Work, folder);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return c.NewEngine(folder);
    }

    public static string Run(Ctx c)
    {
        var said = new List<string>();
        // 1. By type, on empty scratch tables: the counts equal Mutagen's
        var e = Fresh(c, "sql-auto-1");
        var a = Must(Run(e, "SELECT Type, count(*) FROM Records WHERE Type = 'Weapon' OR Sig = 'ARMO' GROUP BY 1 ORDER BY 1", SF), "by type");
        var mod = c.Mod(SF);
        Same("Records narrowed by type", Rows(a), new[] { Row("Armor", (long)mod.Armors.Count), Row("Weapon", (long)mod.Weapons.Count) }, ordered: true);
        Check(a.Notes.Any(n => n.StartsWith("Read first, because Records holds only the record types read: 2 types, Armor, Weapon", StringComparison.Ordinal)), $"the read is said: {string.Join(" / ", a.Notes)}");
        said.Add($"Type = 'Weapon' OR Sig = 'ARMO': {mod.Weapons.Count} and {mod.Armors.Count}");

        // 2. By FormKey and by EditorID: the record's own type is read
        e = Fresh(c, "sql-auto-2");
        var byKey = Must(Run(e, "SELECT EditorID, Type FROM Records WHERE FormKey = '0476C4:Starfield.esm'", SF), "by FormKey");
        Check(Rows(byKey).SequenceEqual(new[] { "Eon | Weapon" }), $"by FormKey: {string.Join(" / ", Rows(byKey))}");
        var book = mod.Books.First(b => b.EditorID != null);
        var byEdid = Must(Run(e, $"SELECT FormKey, Type FROM Records WHERE EditorID = '{book.EditorID}'", SF), "by EditorID");
        Check(Rows(byEdid).SequenceEqual(new[] { Row(book.FormKey, "Book") }), $"by EditorID {book.EditorID}: {string.Join(" / ", Rows(byEdid))}");
        said.Add("FormKey and EditorID read the record's own type");

        // 3. Links WHERE ToKey with no link index: the records of the types that can link to it (by the schema) whose
        // bytes hold its FormID, world data too on a plugin this small, so the answer is every link Mutagen finds to it.
        var kgc = c.Mod(KGC);
        // A cell, a worldspace, a topic or a quest gives Mutagen the links of the records inside it too: those are left
        // out on both sides, the records inside are counted themselves
        var containers = new HashSet<string>(StringComparer.Ordinal) { "Cell", "Worldspace", "DialogTopic", "Quest" };
        var linksTo = new Dictionary<FormKey, HashSet<FormKey>>();
        foreach (var rec in kgc.EnumerateMajorRecords())
        {
            if (containers.Contains(Ctx.Type(rec))) continue;
            foreach (var l in rec.EnumerateFormLinks())
                if (!l.IsNull) (linksTo.TryGetValue(l.FormKey, out var s) ? s : linksTo[l.FormKey] = new HashSet<FormKey>()).Add(rec.FormKey);
        }
        var target = linksTo.Where(kv => kv.Key.ModKey == kgc.ModKey).OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key.ToString()).First();
        e = Fresh(c, "sql-auto-3");
        var links = Must(Run(e, $"SELECT DISTINCT FromKey FROM Links WHERE ToKey = '{target.Key}' AND FromType NOT IN ('Cell', 'Worldspace', 'DialogTopic', 'Quest')", KGC), "links by schema");
        Same($"the records that link to {target.Key} in {KGC}", links.Rows.Select(r => (string)r[0]!).ToList(), target.Value.Select(k => k.ToString()).ToList(), ordered: false);
        // With no link index the records are found by a byte search and read alone (fix g-sql, GAPS S3)
        Check(links.Notes.Any(n => n.Contains("searched by their bytes", StringComparison.Ordinal)), $"the byte search is said: {string.Join(" / ", links.Notes)}");
        said.Add($"ToKey {target.Key} by a byte search: {target.Value.Count} records, equal to Mutagen's links");

        // 4. With a link index (the host's): only the types it names are read
        e = Fresh(c, "sql-auto-4");
        e.LinkIndex = (p, fk) => new LinkHolders(new[] { "WEAP" }, Array.Empty<string>());
        var viaIndex = Must(Run(e, "SELECT DISTINCT FromType FROM Links WHERE ToKey = '04A0A0:Starfield.esm'", SF), "links by index");
        Check(Rows(viaIndex).SequenceEqual(new[] { "Weapon" }) && viaIndex.Cost.Read.Count(r => r.Contains(" from ", StringComparison.Ordinal)) == 1, $"with an index naming WEAP only Weapon is read: {string.Join(" / ", Rows(viaIndex))}; read {string.Join(", ", viaIndex.Cost.Read)}");
        said.Add("a link index's types alone are read");

        // 5. Narrowed to nothing, nothing read yet: every type of a small plugin is read at once ...
        e = Fresh(c, "sql-auto-5");
        var all = Must(Run(e, "SELECT count(*) FROM Records", KGC), "every type");
        var known = Must(Run(e, "SELECT count(*) FROM Forms WHERE Sig IN (SELECT Sig FROM schema.RecordTypes)", KGC), "forms");
        Check(Equals(all.Rows[0][0], known.Rows[0][0]), $"every type of {KGC}: Records has {all.Rows[0][0]}, the header index {known.Rows[0][0]}");
        said.Add($"every type of {KGC} read when nothing narrows: {all.Rows[0][0]} records, as many as the header index has");
        // ... and Starfield.esm's are too many: the estimate and the call that runs it
        e = Fresh(c, "sql-auto-6");
        var big = Run(e, "SELECT count(*) FROM Records", SF);
        Check(big.NotRun != null && big.NotRun.What.Contains("allow_heavy=true", StringComparison.Ordinal) && big.Notes.Any(n => n.Contains("needs these", StringComparison.Ordinal)), $"Starfield.esm, nothing narrowed: {big.NotRun?.What} / {string.Join(" / ", big.Notes)}");
        // Some read already: the answer is what is there, with the cost of the rest and the call
        Must(Run(e, "SELECT count(*) FROM Weapon", SF), "weapon");
        var some = Must(Run(e, "SELECT count(*) FROM Records", SF), "some read");
        Check(Equals(some.Rows[0][0], (long)mod.Weapons.Count) && some.Notes.Any(n => n.Contains("to read them all", StringComparison.Ordinal) && n.Contains("allow_heavy=true", StringComparison.Ordinal)), $"some read: {some.Rows[0][0]} / {string.Join(" / ", some.Notes)}");
        said.Add($"Starfield.esm: not run with the estimate ({big.NotRun!.What[..Math.Min(80, big.NotRun.What.Length)]}…)");

        // 6. A type that isn't one: nothing read, and said
        var none = Run(e, "SELECT * FROM Records WHERE Type = 'NoSuchType'", SF);
        Check(none.Error == null && none.Rows.Count == 0 && none.Notes.Any(n => n.StartsWith("'NoSuchType' isn't a record type", StringComparison.Ordinal)), $"NoSuchType: {string.Join(" / ", none.Notes)}");
        return string.Join("; ", said);
    }
}
