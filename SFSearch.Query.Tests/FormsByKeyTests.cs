// GAPS S204: Forms narrowed by FormKey. A statement that reads Forms only by FormKey = '…' or FormKey IN (…), every
// key a literal, gets the header index's lines of those records alone, in a table of its own; the whole of Forms is
// not put in a table for it. Held here against the whole table: the same statement written so that it can't be read
// by key (FormKey || '' = '…') gives the same rows in every scope.
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class FormsByKeyTests
{
    const string SF = "Starfield.esm", SS = "ShatteredSpace.esm";

    /// <summary>An engine on an empty scratch folder: what an earlier run left there would be "already read".</summary>
    static QueryEngine Fresh(Ctx c, string folder)
    {
        string path = Path.Combine(c.Work, folder);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return c.NewEngine(folder);
    }

    public static string Run(Ctx c)
    {
        // Records of Starfield.esm that ShatteredSpace.esm overrides, and some it doesn't
        var both = c.Ok($"SELECT s.FormKey FROM Weapon s WHERE s.Plugin = '{SS}' AND s.IsOverride = 1 ORDER BY 1 LIMIT 3", SF, SS);
        var one = c.Ok($"SELECT FormKey FROM Weapon WHERE Plugin = '{SF}' ORDER BY 1 LIMIT 3", SF, SS);
        var keys = both.Rows.Concat(one.Rows).Select(r => (string)r[0]!).Distinct().ToList();
        Check(keys.Count >= 3, $"weapons to ask Forms about: {keys.Count}");
        string inList = string.Join(", ", keys.Select(k => $"'{k}'"));
        const string cols = "FormKey, Sig, Type, EditorID, Plugin, Parent, IsWinner, IsOverride, Pos, LoadOrder, Size, DataSize, Compressed, Flags, FlagNames";

        int compared = 0;
        // A fresh engine each time: Forms is in no table yet, so the keyed read is what answers
        void Hold(string what, Func<QueryEngine, string, QueryAnswer> run)
        {
            using var byKey = Fresh(c, $"formskey-{compared}-k");
            var k = Must(run(byKey, $"SELECT {cols} FROM Forms WHERE FormKey IN ({inList}) ORDER BY FormKey, Plugin"), what);
            Check(k.Cost.Read.Any(r => r.StartsWith("Forms by FormKey", StringComparison.Ordinal)), $"{what}: read by key ({string.Join("; ", k.Cost.Read)})");
            using var whole = Fresh(c, $"formskey-{compared}-w");
            var w = Must(run(whole, $"SELECT {cols} FROM Forms WHERE FormKey || '' IN ({inList}) ORDER BY FormKey, Plugin"), what);
            Check(w.Cost.Read.Any(r => r.StartsWith("Forms (", StringComparison.Ordinal)), $"{what}: the other statement reads the whole table ({string.Join("; ", w.Cost.Read)})");
            Same(what, Rows(k), Rows(w), ordered: true);
            Check(k.Rows.Count > 0, $"{what}: rows");
            compared += k.Rows.Count;
        }
        Hold("two plugins", (e, sql) => e.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF, SS }, PageRows = 1_000_000, AllowHeavy = true }));
        Hold("one plugin", (e, sql) => e.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF }, PageRows = 1_000_000, AllowHeavy = true }));
        Hold("the versions scope", (e, sql) => e.Run(new QueryRequest { Sql = sql, Scope = ScopeKind.Versions, PageRows = 1_000_000, AllowHeavy = true }));
        Hold("the winners scope", (e, sql) => e.Run(new QueryRequest { Sql = sql, Scope = ScopeKind.Winners, PageRows = 1_000_000, AllowHeavy = true }));

        // By key, the load order's Forms isn't heavy: it runs without being allowed
        using (var fresh = Fresh(c, "formskey-gate"))
        {
            var a = fresh.Run(new QueryRequest { Sql = $"SELECT Plugin, IsWinner FROM Forms WHERE FormKey = '{keys[0]}' ORDER BY 1", Scope = ScopeKind.Versions, PageRows = 100 });
            Check(a.Error == null && a.NotRun == null && a.Rows.Count > 0, $"one FormKey of the load order's Forms runs without allow_heavy: {a.Error ?? (a.NotRun != null ? "not run: asked first" : "no rows")}");
        }
        return $"{keys.Count} FormKeys of {SF} and {SS}: Forms read by key gives the whole table's rows in 4 scopes ({compared} rows, 15 columns each), and one FormKey of the load order's Forms runs without being allowed";
    }
}
