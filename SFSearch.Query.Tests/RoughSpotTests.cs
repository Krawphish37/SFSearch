// GAPS S210, S211, S213, S214: the rest of what replaying pre-2.0 queries found (2026-11-19).
//   S210  edid(), name() and type() of one of the engine's built-in forms (Starfield.esm below 000800) give nothing,
//         and the answer says why.
//   S211  a JSON path that names no field of the record types the SQL names is said, with where it stops.
//   S213  a stored query's record= that names no record says what record= takes, names pattern= and the EditorIDs
//         that hold the text; a stored query's answer has no notes about how to write SQL.
//   S214  every unknown column of a statement in one error; a "no rows" note says how much was read; Forms read by
//         EditorID alone reads those records' lines of the header index.
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class RoughSpotTests
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
        // S210
        var built = c.Ok("SELECT edid('000014:Starfield.esm'), name('000014:Starfield.esm'), type('000014:Starfield.esm'), edid(FormKey) FROM Weapon ORDER BY 4 LIMIT 1", KGC);
        Check(built.Rows[0][0] == null && built.Rows[0][3] != null && built.Notes.Any(n => n.Contains("000014:Starfield.esm", StringComparison.Ordinal) && n.Contains("built into the game", StringComparison.Ordinal)),
            $"a built-in form asked by edid(): {string.Join(" / ", built.Notes)}");
        var plain = c.Ok("SELECT edid(FormKey) FROM Weapon ORDER BY 1 LIMIT 1", KGC);
        Check(!plain.Notes.Any(n => n.Contains("built into the game", StringComparison.Ordinal)), "no such note where none was asked");

        // S211
        string? Nowhere(QueryAnswer a) => a.Notes.FirstOrDefault(n => n.Contains("names no field", StringComparison.Ordinal));
        var old = c.Ok("SELECT count(Json ->> '$.Model.File.GivenPath') FROM Weapon", KGC);
        Check(Convert.ToInt64(old.Rows[0][0]) == 0 && Nowhere(old) is { } n1 && n1.Contains("'$.Model.File.GivenPath'", StringComparison.Ordinal) && n1.Contains("File is one value", StringComparison.Ordinal),
            $"a path below a file's path: {Nowhere(old) ?? "(no note)"}");
        var guess = c.Ok("SELECT count(json_extract(Json, '$.IsJunk')) FROM Weapon", KGC);
        Check(Nowhere(guess) is { } n2 && n2.Contains("Weapon has no field IsJunk", StringComparison.Ordinal), $"a field Weapon doesn't have: {Nowhere(guess) ?? "(no note)"}");
        var fine = c.Ok("SELECT count(Json ->> '$.Model.File'), count(Json ->> '$.Name.Value'), count(Json -> '$.Keywords[0]'), count(Json ->> '$.ObjectTemplates[0].Name'), count(Json ->> '$.MutagenObjectType') FROM Weapon", KGC);
        Check(Nowhere(fine) == null && Convert.ToInt64(fine.Rows[0][0]) > 0, $"real paths, a translated text's Value and a list's item get no such note: {Nowhere(fine)}");

        // S214: every unknown column at once
        var two = c.Sql("SELECT EditorID, Foo, Bar FROM Weapon ORDER BY 1", KGC);
        Check(two.Error != null && two.Error.Contains("\"Foo\"", StringComparison.Ordinal) && two.Error.Contains("\"Bar\"", StringComparison.Ordinal), $"two unknown columns in one error: {two.Error}");
        var one = c.Sql("SELECT EditorID, Foo FROM Weapon ORDER BY 1", KGC);
        Check(one.Error != null && one.Error.Contains("\"Foo\"", StringComparison.Ordinal) && !one.Error.Contains("Also:", StringComparison.Ordinal), $"one unknown column as before: {one.Error}");

        // S214: no rows, with every type of the scope read, and with some
        using (var e = Fresh(c, "rough-norows"))
        {
            var all = Must(e.Run(new QueryRequest { Sql = "SELECT FromKey FROM Links WHERE Field = 'NoSuchField'", Plugins = new[] { KGC }, PageRows = 10 }), "no rows, all read");
            Check(all.Rows.Count == 0 && all.Notes.Any(n => n.StartsWith("No rows:", StringComparison.Ordinal) && n.Contains("of every record type of this scope", StringComparison.Ordinal)),
                $"no rows after every type was read: {string.Join(" / ", all.Notes.Where(n => n.StartsWith("No rows", StringComparison.Ordinal)))}");
        }
        using (var e = Fresh(c, "rough-norows2"))
        {
            var some = Must(e.Run(new QueryRequest { Sql = "SELECT FromKey FROM Links WHERE FromType = 'Weapon' AND Field = 'NoSuchField'", Plugins = new[] { KGC }, PageRows = 10 }), "no rows, one type read");
            Check(some.Rows.Count == 0 && some.Notes.Any(n => n.StartsWith("No rows:", StringComparison.Ordinal) && n.Contains("record type(s) read so far", StringComparison.Ordinal) && n.Contains("doesn't hold the other", StringComparison.Ordinal)),
                $"no rows with one type read: {string.Join(" / ", some.Notes.Where(n => n.StartsWith("No rows", StringComparison.Ordinal)))}");
        }

        // S214: Forms by EditorID, against the whole table, in the load order
        var ids = c.Ok("SELECT EditorID FROM Weapon WHERE EditorID IS NOT NULL ORDER BY 1 LIMIT 3", KGC).Rows.Select(r => (string)r[0]!).ToList();
        string inList = string.Join(", ", ids.Select(i => $"'{i}'"));
        const string cols = "FormKey, Sig, Type, EditorID, Plugin, IsWinner, IsOverride, LoadOrder, Size";
        QueryAnswer byId, whole;
        using (var e = Fresh(c, "rough-edid-k"))
            byId = Must(e.Run(new QueryRequest { Sql = $"SELECT {cols} FROM Forms WHERE EditorID IN ({inList}) ORDER BY FormKey, Plugin", Scope = ScopeKind.Versions, PageRows = 1_000_000 }), "Forms by EditorID");
        Check(byId.Cost.Read.Any(r => r.StartsWith("Forms by EditorID", StringComparison.Ordinal)) && byId.Rows.Count >= ids.Count, $"Forms by EditorID is read by key: {string.Join("; ", byId.Cost.Read)}; {byId.Rows.Count} rows");
        using (var e = Fresh(c, "rough-edid-w"))
            whole = Must(e.Run(new QueryRequest { Sql = $"SELECT {cols} FROM Forms WHERE EditorID || '' IN ({inList}) ORDER BY FormKey, Plugin", Scope = ScopeKind.Versions, PageRows = 1_000_000, AllowHeavy = true }), "Forms whole");
        Same("Forms by EditorID against the whole table, versions scope", Rows(byId), Rows(whole), ordered: true);
        // LIKE isn't a lookup by key: the whole table, as before
        using (var e = Fresh(c, "rough-edid-like"))
        {
            var like = Must(e.Run(new QueryRequest { Sql = $"SELECT count(*) FROM Forms WHERE EditorID LIKE '{ids[0]}%'", Plugins = new[] { KGC }, PageRows = 10 }), "Forms LIKE");
            Check(like.Cost.Read.Any(r => r.StartsWith("Forms (", StringComparison.Ordinal)), $"EditorID LIKE reads the whole table: {string.Join("; ", like.Cost.Read)}");
        }

        // S213: a stored query's record= that names no record
        var lib = Stored.Lib(c);
        string part = ids[0].Length > 6 ? ids[0][..^3] : ids[0];
        var none = Stored.Run(lib, "templates", $"plugin={KGC} record={part}");
        string notes = string.Join(" / ", none.Notes);
        Check(none.Error == null && notes.Contains("record= takes one record's whole EditorID", StringComparison.Ordinal) && notes.Contains($"pattern=*{part}*", StringComparison.Ordinal) && notes.Contains(ids[0], StringComparison.Ordinal),
            $"templates record={part}: {notes}");
        var cobj = Stored.Run(lib, "cobj", $"plugin={KGC} record=NoSuchRecipeAnywhere");
        Check(string.Join(" / ", cobj.Notes).Contains("record= takes one record's whole EditorID", StringComparison.Ordinal), $"cobj record= that names nothing: {string.Join(" / ", cobj.Notes)}");
        // …and no notes about writing SQL for someone who wrote none
        var vmad = Stored.Run(lib, "vmad", $"plugin={KGC}");
        string all2 = string.Join(" / ", vmad.Notes.Concat(vmad.Tables.SelectMany(t => t.Answer.Notes)));
        Check(!all2.Contains("coalesce(", StringComparison.Ordinal) && !all2.Contains("WHERE Type =", StringComparison.Ordinal), $"a stored query's notes hold no SQL advice: {all2}");

        return $"{KGC}: built-in forms, a path that names no field, two unknown columns, the two \"no rows\" wordings, Forms by EditorID ({byId.Rows.Count} rows equal to the whole table's in the load order) and a stored query's record= are each answered as S210 to S214 say";
    }
}
