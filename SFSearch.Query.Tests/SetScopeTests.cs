// J63 (GAPS S89): a big type's named columns read from the records' own bytes, and a col = literal applied while
// reading, over a set of plugins: a long plugin list (one scratch file for the set), the winners scope and the versions
// scope. Each plugin's records go through the same byte reader and filter as a plugin of its own; each row carries its
// plugin, its place in the load order and whether it wins, from the header index. Every answer is held against the
// same question read the old way (the whole records as JSON), row for row; with --heavy, the load order's placed
// objects and cells (8 million and 47,000) against each plugin's own answer and the header index's winners.
using System.Diagnostics;
using SFSearch.Index;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class SetScopeTests
{
    const string SF = "Starfield.esm";

    static QueryEngine Engine(Ctx c, string folder, double heavySeconds, bool direct = true)
    {
        string path = Path.Combine(c.Work, folder);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return new QueryEngine(c.Records, new QueryOptions { ScratchFolder = path, HeavySeconds = heavySeconds, DirectColumns = direct });
    }

    static QueryAnswer Ask(QueryEngine e, string sql, ScopeKind scope, string[]? plugins = null, bool heavy = true) =>
        e.Run(new QueryRequest { Sql = sql, Scope = scope, Plugins = plugins ?? Array.Empty<string>(), PageRows = 2_000_000, AllowHeavy = heavy });

    public static string Run(Ctx c)
    {
        var said = new List<string>();
        // A low limit makes every type "too big to read whole", so the named-columns route is taken; the other engine
        // reads the same small types whole, as JSON, the way a set scope read every world type before (with no column
        // read straight from the records or their bytes: such columns are read that way whatever the type's size, J123)
        using var low = Engine(c, "sql-set-low", 0.0001);
        using var whole = Engine(c, "sql-set-whole", 1e9, direct: false);
        void Both(string what, string sql, ScopeKind scope, string[]? plugins, string mustSay)
        {
            var a = Must(Ask(low, sql, scope, plugins), $"{what}, named columns");
            var b = Must(Ask(whole, sql, scope, plugins), $"{what}, whole");
            Check(a.Cost.Read.Count <= 1 && a.Cost.Text.Contains(mustSay, StringComparison.Ordinal), $"{what}: the read must say \"{mustSay}\": {a.Cost.Text}");
            Check(b.Cost.Read.All(r => !r.Contains("column", StringComparison.Ordinal)), $"{what}: the reference must read the records whole: {b.Cost.Text}");
            Same(what, Rows(a), Rows(b), ordered: true);
            said.Add($"{what}: {a.TotalRows:N0} rows equal ({a.Cost.Text})");
        }

        // 1. Nine plugins with placed objects and cells: more than the engine attaches, so one scratch file for the set
        var nine = c.Index.Plugins.Where(p => p.Segment != null && p.Problems.MissingMasters.Count == 0 && p.LeftOut == null && p.Stamp.Length < 32 << 20
                && p.Segment.Signatures.Any(s => s.Signature == "REFR" && s.Count >= 100) && p.Segment.Signatures.Any(s => s.Signature == "CELL" && s.Count > 0))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(9).Select(p => p.Name).ToArray();
        Check(nine.Length == 9, "the Data folder has fewer than 9 small plugins with placed objects and cells");
        // (the filter first: once the column table holds Base, it answers the filtered question with nothing read)
        string topBase = (string)Must(Ask(whole, "SELECT Base FROM PlacedObject WHERE Base IS NOT NULL GROUP BY Base ORDER BY count(DISTINCT Plugin) DESC, count(*) DESC LIMIT 1", ScopeKind.Plugins, nine), "the most placed base").Rows[0][0]!;
        Both($"9 plugins: Base = '{topBase}'", $"SELECT FormKey, Plugin, Base FROM PlacedObject WHERE Base = '{topBase}' ORDER BY Plugin, FormKey", ScopeKind.Plugins, nine, "only those kept");
        Both("9 plugins: placed objects' Base and Position", "SELECT FormKey, Plugin, IsOverride, Base, Position FROM PlacedObject ORDER BY Plugin, FormKey", ScopeKind.Plugins, nine, "from 9 plugins");
        Both("9 plugins: cells' Location and LightingTemplate", "SELECT FormKey, Plugin, Location, LightingTemplate FROM Cell ORDER BY Plugin, FormKey", ScopeKind.Plugins, nine, "from the records' own bytes");

        // 2. The load order: the winners and the versions scope, on a world type small enough to read whole quickly
        // (placed traps); the filter on the layer most of them are on
        string layer = (string)Must(Ask(whole, "SELECT Layer FROM PlacedTrap WHERE Layer IS NOT NULL GROUP BY Layer ORDER BY count(*) DESC LIMIT 1", ScopeKind.Versions), "the most used layer of placed traps").Rows[0][0]!;
        Both($"winners: Layer = '{layer}'", $"SELECT FormKey, Plugin, Layer FROM PlacedTrap WHERE Layer = '{layer}' ORDER BY FormKey", ScopeKind.Winners, null, "only those kept");
        Both($"versions: Layer = '{layer}'", $"SELECT FormKey, Plugin, LoadOrder, IsWinner, Layer FROM PlacedTrap WHERE Layer = '{layer}' ORDER BY LoadOrder, FormKey", ScopeKind.Versions, null, "only those kept");
        Both("winners: placed traps' Layer and Position", "SELECT FormKey, Plugin, Layer, Position FROM PlacedTrap ORDER BY FormKey", ScopeKind.Winners, null, "columns Layer, Position");
        Both("versions: placed traps' Layer", "SELECT FormKey, Plugin, LoadOrder, IsWinner, IsOverride, Layer FROM PlacedTrap ORDER BY LoadOrder, FormKey", ScopeKind.Versions, null, "already read");
        // An empty filter result in the winners scope still says why (the winning rows counted)
        var none = Must(Ask(low, "SELECT FormKey FROM PlacedTrap WHERE Layer = '000001:NoSuchPlugin.esm'", ScopeKind.Winners), "no such base");
        Check(none.TotalRows == 0, "a layer no plugin has gives no rows");

        if (c.Heavy) said.Add(LoadOrder(c));
        return "set scopes (J63, GAPS S89): " + string.Join("; ", said);
    }

    /// <summary>
    /// The load order's 8 million placed objects and 47,000 cells: the S89 questions run without allow_heavy, in about
    /// a second, and agree with each plugin's own answer (J30's route, held against Mutagen in direct-columns) and with
    /// the header index's winners.
    /// </summary>
    static string LoadOrder(Ctx c)
    {
        using var e = Engine(c, "sql-set-lo", 10);
        var parts = new List<string>();
        var clock = Stopwatch.StartNew();
        var versions = Must(Ask(e, "SELECT FormKey, Plugin, IsWinner FROM PlacedObject WHERE Base = '00003B:Starfield.esm'", ScopeKind.Versions, heavy: false), "versions, Base = 00003B");
        double vs = clock.Elapsed.TotalSeconds;
        Check(versions.Cost.Read.Count == 1 && versions.Cost.Read[0].Contains("only those kept", StringComparison.Ordinal), $"the versions read must keep only the matching rows: {versions.Cost.Text}");
        var winners = Must(Ask(e, "SELECT FormKey, Plugin FROM PlacedObject WHERE Base = '00003B:Starfield.esm'", ScopeKind.Winners, heavy: false), "winners, Base = 00003B");
        // Each plugin's own answer (its own scratch file), summed
        var byPlugin = versions.Rows.GroupBy(r => (string)r[1]!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        int checkedPlugins = 0;
        foreach (var p in c.Index.Plugins.Where(p => p.Order.Enabled && p.LeftOut == null && p.Segment != null && p.Problems.MissingMasters.Count == 0 && p.Segment.Signatures.Any(s => s.Signature == "REFR" && s.Count > 0)))
        {
            var own = Must(Ask(e, "SELECT count(*) FROM PlacedObject WHERE Base = '00003B:Starfield.esm'", ScopeKind.Plugins, new[] { p.Name }, heavy: false), $"{p.Name}'s own count");
            long n = (long)own.Rows[0][0]!;
            Check(n == byPlugin.GetValueOrDefault(p.Name), $"{p.Name}: {n:N0} placed objects with base 00003B of its own, {byPlugin.GetValueOrDefault(p.Name):N0} in the versions scope");
            checkedPlugins++;
        }
        // The winners are the versions the header index says win, one per FormKey
        var won = versions.Rows.Where(r => (long)r[2]! == 1).Select(r => $"{r[0]} | {r[1]}".ToLowerInvariant()).ToList();
        Same("winners against the versions' winning rows", Rows(winners).Select(r => r.ToLowerInvariant()).ToList(), won, ordered: false);
        foreach (var r in winners.Rows)
        {
            var v = c.Index.Versions((string)r[0]!);
            Check(v != null && v.Winner >= 0 && c.Index.Plugins[v.Holders[v.Winner].Plugin].Name.Equals((string)r[1]!, StringComparison.OrdinalIgnoreCase), $"{r[0]}: the winners scope reads it from {r[1]}, the header index's winner is another plugin");
        }
        parts.Add($"placed objects with base 00003B: {versions.TotalRows:N0} versions in {byPlugin.Count:N0} plugins ({vs:F1} s, not heavy), each plugin's count equal to its own answer ({checkedPlugins:N0} plugins asked), {winners.TotalRows:N0} winners, each the header index's");
        clock.Restart();
        var cells = Must(Ask(e, "SELECT FormKey, Plugin, Location FROM Cell WHERE Location IS NOT NULL", ScopeKind.Winners, heavy: false), "winning cells with a Location");
        double cs = clock.Elapsed.TotalSeconds;
        var cellVersions = Must(Ask(e, "SELECT FormKey, Plugin, Location FROM Cell WHERE Location IS NOT NULL AND IsWinner = 1", ScopeKind.Versions, heavy: false), "the versions' winning cells");
        Same("winning cells with a Location", Rows(cells), Rows(cellVersions), ordered: false);
        parts.Add($"cells with a Location: {cells.TotalRows:N0} winners ({cs:F1} s, not heavy; {cells.Cost.Text}), the same as the versions scope's winning rows");
        return string.Join("; ", parts);
    }
}
