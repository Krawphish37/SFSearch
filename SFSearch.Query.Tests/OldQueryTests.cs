// GAPS S205 to S209: what replaying 258 queries written for SFSearch before 2.0 found (2026-11-19).
//   S205  type(fk) of a record whose signature several types share (an OMOD) is the record's own type.
//   S206  a column whose name is a word of SQL (a list view's Default) is answered with how to write it.
//   S207  a table, column or function SQL had before 2.0 is answered with what replaced it.
//   S208  Links (and Records, Conditions) read with no narrowing give the same answer whatever an earlier query read;
//         Links joined to a view by ToKey reads the types that hold such links, not only the ones read so far.
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class OldQueryTests
{
    const string SF = "Starfield.esm", KGC = "kgcdoom.esm";

    /// <summary>An engine on an empty scratch folder: what an earlier run left there would be "already read".</summary>
    static QueryEngine Fresh(Ctx c, string folder)
    {
        string path = Path.Combine(c.Work, folder);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return c.NewEngine(folder);
    }

    public static string Run(Ctx c)
    {
        // S205: type() against the view's own Type column, for every object modification of a plugin
        var types = c.Ok("SELECT count(*), sum(type(FormKey) = Type), count(DISTINCT Type), sum(sig(FormKey) = 'OMOD') FROM ObjectModification", KGC);
        long omods = Convert.ToInt64(types.Rows[0][0]);
        Check(omods > 0 && Convert.ToInt64(types.Rows[0][1]) == omods && Convert.ToInt64(types.Rows[0][3]) == omods, $"type(FormKey) is the Type column for all {omods:N0} object modifications of {KGC}: {Rows(types)[0]}");
        var weapon = c.Ok("SELECT count(*), sum(type(FormKey) = 'Weapon') FROM Weapon", KGC);
        Check(Convert.ToInt64(weapon.Rows[0][0]) == Convert.ToInt64(weapon.Rows[0][1]), "type() of a weapon is Weapon, from the header index as before");

        // S206: a column named as a word of SQL
        var word = c.Sql("SELECT EditorID, Idx, Default FROM Weapon_ObjectTemplates ORDER BY 1", KGC);
        Check(word.Error != null && word.Error.Contains("\"Default\" is a column of Weapon_ObjectTemplates", StringComparison.Ordinal) && (word.Fix ?? "").Contains("\"Default\"", StringComparison.Ordinal), $"Default unquoted: {word.Error} / {word.Fix}");
        var quoted = c.Ok("SELECT count(*), count(\"Default\") FROM Weapon_ObjectTemplates", KGC);
        Check(Convert.ToInt64(quoted.Rows[0][0]) > 0, "Default in double quotes reads the column");

        // S207: the old names
        void Old(string sql, string says)
        {
            var a = c.Sql(sql, KGC);
            Check(a.Error != null && a.Error.Contains(says, StringComparison.Ordinal), $"{sql}: {a.Error ?? "(no error)"}");
        }
        Old("SELECT Signature FROM Forms", "Sig (WEAP) and Type (Weapon)");
        Old("SELECT Data FROM Records", "is Json now");
        Old("SELECT FromFormKey FROM Links", "FromFormKey is FromKey now");
        Old("SELECT * FROM Refs", "it is Links now");
        Old("SELECT * FROM SignatureCounts", "FROM Forms GROUP BY Sig, Type");
        Old("SELECT * FROM lo.Winners", "the winners scope");
        Old("SELECT fkname(FormKey) FROM Weapon", "fkname(fk) is name(fk) now");
        Old("SELECT en(Json) FROM Weapon", "text columns are one language already");
        var meta = c.Ok("SELECT Value FROM Meta WHERE Key = 'SourceStamp'", KGC);
        Check(meta.Rows.Count == 0 && meta.Notes.Any(n => n.Contains("the table Plugins now", StringComparison.Ordinal)), $"the old Meta table: {string.Join(" / ", meta.Notes)}");
        // A real column of the same name as an old one is read, not refused
        var real = c.Ok("SELECT count(*), count(Base) FROM PlacedObject", KGC);
        Check(Convert.ToInt64(real.Rows[0][1]) > 0, "PlacedObject.Base is a column and is read");

        // S208: Links with no narrowing, on an engine that has read one type, and on one that has read none
        const string census = "SELECT FromType, count(*) FROM Links GROUP BY 1 ORDER BY 1";
        List<string> fresh, after;
        using (var e = Fresh(c, "oldq-fresh")) fresh = Rows(Must(e.Run(new QueryRequest { Sql = census, Plugins = new[] { KGC }, PageRows = 1_000_000 }), census));
        using (var e = Fresh(c, "oldq-after"))
        {
            Must(e.Run(new QueryRequest { Sql = "SELECT count(*) FROM Weapon", Plugins = new[] { KGC }, PageRows = 10 }), "Weapon first");
            after = Rows(Must(e.Run(new QueryRequest { Sql = census, Plugins = new[] { KGC }, PageRows = 1_000_000 }), census));
        }
        Check(fresh.Count > 3, $"the link census of {KGC} has several types: {fresh.Count}");
        Same("Links read with no narrowing, after Weapon alone was read, against a fresh engine", after, fresh, ordered: true);

        // S208: Links joined to a view by ToKey: the same rows as ToKey = '…'
        var kw = c.Ok("SELECT k.FormKey, k.EditorID FROM Keyword k WHERE k.IsOverride = 0 ORDER BY 1", KGC);
        int held = 0, withLinks = 0;
        foreach (var row in kw.Rows.Take(6))
        {
            string fk = (string)row[0]!, edid = (string)row[1]!;
            List<string> byJoin, byKey;
            using (var e = Fresh(c, $"oldq-join-{held}"))
                byJoin = Rows(Must(e.Run(new QueryRequest { Sql = $"SELECT l.FromType, l.FromKey, l.Path FROM Links l JOIN Keyword k ON k.FormKey = l.ToKey WHERE k.EditorID = '{edid}' ORDER BY 1, 2, 3", Plugins = new[] { KGC }, PageRows = 1_000_000 }), edid));
            using (var e = Fresh(c, $"oldq-key-{held}"))
                byKey = Rows(Must(e.Run(new QueryRequest { Sql = $"SELECT FromType, FromKey, Path FROM Links WHERE ToKey = '{fk}' ORDER BY 1, 2, 3", Plugins = new[] { KGC }, PageRows = 1_000_000 }), fk));
            Same($"links to {edid} ({fk}) by a join against ToKey = '…'", byJoin, byKey, ordered: true);
            held++;
            if (byKey.Count > 0) withLinks++;
        }
        Check(held > 0 && withLinks > 0, $"keywords of {KGC} with links to them: {withLinks} of {held}");
        // No record named: every type is read, and the join still gives what ToKey gives for one of them
        using (var e = Fresh(c, "oldq-like"))
        {
            var like = Must(e.Run(new QueryRequest { Sql = "SELECT count(*) FROM Links l JOIN Keyword k ON k.FormKey = l.ToKey WHERE k.IsOverride = 0", Plugins = new[] { KGC }, PageRows = 10 }), "join, no record named");
            var all = Must(e.Run(new QueryRequest { Sql = "SELECT count(*) FROM Links WHERE ToKey IN (SELECT FormKey FROM Keyword WHERE IsOverride = 0)", Plugins = new[] { KGC }, PageRows = 10 }), "the same by IN");
            Check(Convert.ToInt64(like.Rows[0][0]) == Convert.ToInt64(all.Rows[0][0]) && Convert.ToInt64(like.Rows[0][0]) > 0, $"the join with no record named: {like.Rows[0][0]} against {all.Rows[0][0]}");
        }
        return $"{KGC}: type() is the Type column for {omods:N0} object modifications; \"Default\" unquoted, 8 old names and the old Meta table are answered with what to write; a link census is the same after one type was read ({fresh.Count} types); {held} keywords' links by a join equal ToKey = '…'";
    }
}
