// List views of a type too big or slow to read whole (GAPS S45): only the records that hold the list's subrecords are
// read (found in the plugin's bytes), and the list's named columns from the records' own bytes where the list is laid
// out exactly (RawLists.cs, GAPS S123), else straight from Mutagen's objects (ListReads.cs).
// Held against Mutagen read directly, and against the same records read whole through their JSON.
using System.Globalization;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
using Mutagen.Bethesda.Starfield;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class ListReadTests
{
    const string P = "SFBGS00D.esm";

    static QueryEngine Fresh(Ctx c, string folder)
    {
        string path = Path.Combine(c.Work, folder);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return c.NewEngine(folder);
    }

    /// <summary>Every row of a query, a page at a time, each row as a hash of its text (so the test holds neither
    /// the answers' objects nor their texts: 900,271 rows with their JSON were gigabytes). With the first page's answer.</summary>
    static (List<string> Rows, QueryAnswer First) Paged(QueryEngine e, string sql)
    {
        var rows = new List<string>();
        QueryAnswer? first = null;
        string? cursor = null;
        do
        {
            var a = Must(e.Run(new QueryRequest { Sql = sql, Plugins = new[] { P }, PageRows = 1_000_000, AllowHeavy = true, Cursor = cursor }), sql);
            first ??= a;
            foreach (var r in a.Rows)
            {
                string text = string.Join(" | ", r.Select(Cell));
                rows.Add($"{r[0]} #{System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(text)):x16}");
            }
            cursor = a.Cursor;
            if (first != a) a.Rows.Clear();
        } while (cursor != null);
        return (rows, first);
    }

    static string Pt(P3Float p) => $"{F(p.X)}, {F(p.Y)}, {F(p.Z)}";
    static string F(float f) => f.ToString(CultureInfo.InvariantCulture);

    public static string Run(Ctx c)
    {
        var said = new List<string>();
        var mod = c.Mod(P);
        using var e = Fresh(c, "sql-lists");

        // 1. Cell_Traversals: the cells that hold XTV2, their traversals straight from the records
        var a = Must(e.Run(new QueryRequest { Sql = "SELECT FormKey, Idx, \"From\", \"To\", UnknownVector, Traversal FROM Cell_Traversals", Plugins = new[] { P }, PageRows = 10_000_000, AllowHeavy = true }), "Cell_Traversals");
        var cells = mod.EnumerateMajorRecords<ICellGetter>().Where(x => x.Traversals != null).ToList();
        var want = cells.SelectMany(x => x.Traversals!.Select((t, i) => Row(x.FormKey, (long)i, Pt(t.From), Pt(t.To), Pt(t.UnknownVector), t.Traversal.IsNull ? null : t.Traversal.FormKey))).ToList();
        Same($"{P}: Cell_Traversals against Mutagen", Rows(a).Select(r => r.ToLowerInvariant()).ToList(), want.Select(r => r.ToLowerInvariant()).ToList(), ordered: false);
        string held = $"{cells.Count:N0} of the {mod.EnumerateMajorRecords<ICellGetter>().Count():N0} Cell records of {P} hold its subrecord (XTV2)";
        Check(a.Notes.Any(n => n.StartsWith("Cell_Traversals: " + held, StringComparison.Ordinal)) && a.Cost.Read.Any(r => r.StartsWith($"Cell_Traversals ({want.Count:N0} rows of {cells.Count:N0} records", StringComparison.Ordinal)),
            $"the narrowing is said ({held}): {string.Join(" / ", a.Notes)} / {string.Join(" / ", a.Cost.Read)}");
        said.Add($"Cell_Traversals {want.Count:N0} rows of {cells.Count:N0} cells");

        // 2. Asked again for fewer columns: the table already read answers it
        var again = Must(e.Run(new QueryRequest { Sql = "SELECT count(*) FROM Cell_Traversals WHERE \"From\" IS NOT NULL", Plugins = new[] { P }, PageRows = 10 }), "again");
        Check(Convert.ToInt64(again.Rows[0][0], CultureInfo.InvariantCulture) == want.Count && again.Cost.Read.Count == 0, $"asked again: {again.Rows[0][0]} rows, read {string.Join(" / ", again.Cost.Read)}");
        said.Add("asked again, nothing read");

        // 3. A list in a list: the navmesh info map's traversals
        var n = Must(e.Run(new QueryRequest { Sql = "SELECT FormKey, Idx, Idx2, CellOrObject, FromPosition, ToPosition, UnknownVector FROM NavigationMeshInfoMap_MapInfos_Traversals", Plugins = new[] { P }, PageRows = 10_000_000, AllowHeavy = true }), "NAVI traversals");
        var navi = mod.EnumerateMajorRecords<INavigationMeshInfoMapGetter>().SelectMany(x => x.MapInfos.SelectMany((m, i) => m.Traversals.Select((t, j) =>
            Row(x.FormKey, (long)i, (long)j, t.CellOrObject.IsNull ? null : t.CellOrObject.FormKey, Pt(t.FromPosition), Pt(t.ToPosition), Pt(t.UnknownVector))))).ToList();
        Same($"{P}: NavigationMeshInfoMap_MapInfos_Traversals against Mutagen", Rows(n).Select(r => r.ToLowerInvariant()).ToList(), navi.Select(r => r.ToLowerInvariant()).ToList(), ordered: false);
        said.Add($"NAVI traversals {navi.Count:N0} rows");

        // 4. A column only the JSON gives (the entry's Json, a list of flags): the list's rows taken out of each held
        //    record's JSON one record at a time (GAPS S58), row for row against the type read whole first
        using var whole = Fresh(c, "sql-lists-whole");
        Must(whole.Run(new QueryRequest { Sql = "SELECT count(*) FROM Cell WHERE Json IS NOT NULL", Plugins = new[] { P }, AllowHeavy = true }), "Cell whole");
        using var j = Fresh(c, "sql-lists-json");
        // Every 25th entry's Json row for row (the whole 211 MB of JSON as rows would be most of this test's memory), and
        // every entry's JSON summed
        const string cellJson = "SELECT FormKey, Idx, \"From\", \"To\", UnknownVector, Traversal, Json, json_type(json_object('j', Json), '$.j') FROM Cell_Traversals WHERE Idx % 25 = 0 ORDER BY FormKey, Idx";
        const string cellJsonAll = "SELECT count(*), sum(length(Json)), total(json_type(json_object('j', Json), '$.j') = 'object'), count(DISTINCT Json) FROM Cell_Traversals";
        var (cj, cjFirst) = Paged(j, cellJson);
        Check(cjFirst.Notes.Any(x => x.StartsWith("Cell_Traversals: " + held, StringComparison.Ordinal) && x.Contains("taken out of each record's JSON", StringComparison.Ordinal)),
            "the JSON column is read out of each record's JSON: " + string.Join(" / ", cjFirst.Notes));
        var (cw, _) = Paged(whole, cellJson);
        Same($"{P}: Cell_Traversals with Json (every 25th entry), out of each record's JSON against the cells read whole", cj, cw, ordered: true);
        var sumJ = Must(j.Run(new QueryRequest { Sql = cellJsonAll, Plugins = new[] { P } }), cellJsonAll);
        var sumW = Must(whole.Run(new QueryRequest { Sql = cellJsonAll, Plugins = new[] { P } }), cellJsonAll);
        Check(sumJ.Cost.Read.Count == 0 && Rows(sumJ)[0] == Rows(sumW)[0] && Convert.ToInt64(sumJ.Rows[0][0], CultureInfo.InvariantCulture) == want.Count,
            $"every entry's JSON: {Rows(sumJ)[0]} against {Rows(sumW)[0]} read whole, Mutagen {want.Count:N0} entries");
        // Flags (a list of names, JSON): from the entries the table holds, nothing read again; then in a new scratch
        const string cellFlags = "SELECT FormKey, Idx, Flags, json_type(json_object('f', Flags), '$.f'), Unknown2, Traversal FROM Cell_Traversals ORDER BY FormKey, Idx";
        var (fj, fjFirst) = Paged(j, cellFlags);
        Check(fjFirst.Cost.Read.Count == 0, "Flags from the entries already read: " + string.Join(" / ", fjFirst.Cost.Read));
        var (fw, _) = Paged(whole, cellFlags);
        Same($"{P}: Cell_Traversals' Flags from the entries held against the cells read whole", fj, fw, ordered: true);
        using var flagsEngine = Fresh(c, "sql-lists-flags");
        var (ff, ffFirst) = Paged(flagsEngine, cellFlags);
        // Since S4 (fix-s4s5-world-reads) a flags column comes straight from the records too, as the same JSON list; since
        // J103 (GAPS S123) from the cells' own bytes, by Mutagen's layout of XTV2 (the flags without the bit it keeps elsewhere)
        Check(ffFirst.Notes.Any(x => x.StartsWith("Cell_Traversals: " + held, StringComparison.Ordinal) && x.Contains("the named columns from the records' own bytes", StringComparison.Ordinal)),
            "Flags are read from the records' own bytes: " + string.Join(" / ", ffFirst.Notes));
        Same($"{P}: Cell_Traversals' Flags from the records' own bytes against the cells read whole", ff, fw, ordered: true);
        said.Add($"JSON columns of Cell_Traversals (Json out of each record's JSON, Flags from the records' own bytes): {Rows(sumJ)[0]} (rows, JSON characters, JSON objects, distinct), {cj.Count:N0} sampled rows and {ff.Count:N0} Flags rows, the same as the cells read whole, JSON as JSON");

        // 5. The typed view joined to its list by FormKey: only the list's FormKeys are read of it (GAPS S58)
        int narrowed = 0;
        foreach (string sql in new[]
        {
            "SELECT c.FormKey, c.EditorID, c.Name, c.Location, c.IsOverride, t.Idx, t.Traversal, t.Json FROM Cell c JOIN Cell_Traversals t ON t.FormKey = c.FormKey WHERE t.Idx % 25 = 0 ORDER BY c.FormKey, t.Idx",
            "SELECT c.EditorID, count(*) FROM Cell_Traversals t, Cell c WHERE t.Idx >= 0 AND c.FormKey = t.FormKey GROUP BY c.EditorID ORDER BY 1",
            "SELECT count(*), count(DISTINCT Cell.EditorID) FROM Cell_Traversals JOIN Cell USING (FormKey)",
            "SELECT count(*), count(c.Name) FROM Cell_Traversals t LEFT JOIN Cell c ON c.FormKey = t.FormKey",
        })
        {
            using var two = Fresh(c, $"sql-lists-two-{narrowed}");
            var (tg, tFirst) = Paged(two, sql);
            Check(tFirst.Notes.Any(x => x.Contains("Cell (joined to it by FormKey) for those FormKeys alone", StringComparison.Ordinal))
                && tFirst.Cost.Read.Any(r => r.StartsWith($"Cell ({cells.Count:N0} with the FormKeys that hold the list", StringComparison.Ordinal)),
                $"the typed view is narrowed ({sql}): {string.Join(" / ", tFirst.Notes)} / {string.Join(" / ", tFirst.Cost.Read)}");
            var (tw, _) = Paged(whole, sql);
            Same($"{P}: {sql} against the cells read whole", tg, tw, ordered: true);
            narrowed++;
        }
        // Where the typed view could give a row the list hasn't, it is read whole as before: the estimate says so
        // (with every read heavy, nothing runs)
        int other = 0;
        foreach (string sql in new[]
        {
            "SELECT count(*), count(t.FormKey) FROM Cell c LEFT JOIN Cell_Traversals t ON t.FormKey = c.FormKey",
            "SELECT count(*) FROM Cell c JOIN Cell_Traversals t ON t.FormKey = c.FormKey OR c.EditorID IS NOT NULL",
            "SELECT (SELECT count(*) FROM Cell), count(*) FROM Cell_Traversals",
            "SELECT count(*) FROM Cell c JOIN Cell_Traversals t ON NOT c.FormKey = t.FormKey",
        })
        {
            string folder = Path.Combine(c.Work, $"sql-lists-other-{other++}");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            using var est = new QueryEngine(c.Records, new QueryOptions { ScratchFolder = folder, HeavySeconds = 0.0001 });
            var a2 = est.Run(new QueryRequest { Sql = sql, Plugins = new[] { P } });
            Check(a2.NotRun != null && a2.NotRun.What.Contains($"Cell in {P} is 2,037 records", StringComparison.Ordinal) && !a2.NotRun.What.Contains("whose FormKeys", StringComparison.Ordinal),
                $"read whole, not narrowed ({sql}): {a2.NotRun?.What ?? a2.Error ?? "it ran"}");
        }
        said.Add($"Cell joined to Cell_Traversals by FormKey: {narrowed} queries read {cells.Count:N0} cells, the same answers as read whole; {other} others read Cell whole");

        // 6. A set of plugins (J63, GAPS S89): more than the engine attaches, so one scratch file; the list and the typed
        // view joined to it, each row with its plugin, against each plugin asked on its own file. (The same join over the
        // set's cells read whole, with c.Location and GROUP BY, takes SQLite past 12 GB in 4 s: GAPS S104.)
        var set = new[] { P }.Concat(c.Index.Plugins.Where(x => x.Segment != null && x.Problems.MissingMasters.Count == 0 && x.LeftOut == null && x.Stamp.Length < 16 << 20
                && x.Segment.Signatures.Any(s => s.Signature == "CELL" && s.Count > 0) && !x.Name.Equals(P, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(8).Select(x => x.Name)).ToArray();
        Check(set.Length == 9, "the Data folder has fewer than 8 small plugins with cells");
        const string setSql = "SELECT c.FormKey, c.Plugin, c.Location, count(*) FROM Cell c JOIN Cell_Traversals t ON t.FormKey = c.FormKey AND t.Plugin = c.Plugin GROUP BY c.FormKey, c.Plugin";
        string setFolder = Path.Combine(c.Work, "sql-lists-set");
        if (Directory.Exists(setFolder)) Directory.Delete(setFolder, true);
        using var setLow = new QueryEngine(c.Records, new QueryOptions { ScratchFolder = setFolder, HeavySeconds = 0.0001 });
        var sa = Must(setLow.Run(new QueryRequest { Sql = setSql, Plugins = set, PageRows = 1_000_000, AllowHeavy = true }), setSql);
        Check(sa.Notes.Any(x => x.Contains("Cell (joined to it by FormKey) for those FormKeys alone", StringComparison.Ordinal)) && sa.Cost.Read.Any(r => r.StartsWith("Cell (", StringComparison.Ordinal) && r.Contains("with the FormKeys that hold the list", StringComparison.Ordinal)),
            $"over a set the typed view is narrowed too: {string.Join(" / ", sa.Notes)} / {sa.Cost.Text}");
        var each = new List<string>();
        foreach (string plugin in set)
        {
            // The narrowed route on each plugin's own file (step 5 holds it against the whole read): the same join over
            // records read whole carries each cell's JSON into every traversal row (GAPS S104)
            string oneFolder = Path.Combine(c.Work, "sql-lists-set-one");
            if (Directory.Exists(oneFolder)) Directory.Delete(oneFolder, true);
            using var one = new QueryEngine(c.Records, new QueryOptions { ScratchFolder = oneFolder, HeavySeconds = 0.0001 });
            var a1 = Must(one.Run(new QueryRequest { Sql = "SELECT c.FormKey, c.Location, count(*) FROM Cell c JOIN Cell_Traversals t ON t.FormKey = c.FormKey GROUP BY c.FormKey", Plugins = new[] { plugin }, PageRows = 1_000_000, AllowHeavy = true }), plugin);
            each.AddRange(a1.Rows.Select(r => Row(r[0], c.Index.Plugin(plugin)!.Name, r[1], r[2])));
        }
        Same("9 plugins: Cell joined to Cell_Traversals against each plugin asked on its own", Rows(sa), each, ordered: false);
        said.Add($"a set of 9 plugins: Cell joined to Cell_Traversals, {sa.TotalRows:N0} cells with their plugins, the same as each plugin's own answer");
        return string.Join("; ", said);
    }
}
