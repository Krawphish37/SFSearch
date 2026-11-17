// A statement's memory (GAPS S104, J82): the three layers, each with its own check.
//   1. A grouped statement works out a typed view's columns once per record: Cell joined to Cell_Traversals over
//      SFBGS00D.esm's cells read whole, grouped, with c.Location, carried each cell's JSON for each of its 900,271
//      traversals (3.2 TB of JSON in all; 16 GB in 5 s before SQLite gave up). Now it finishes under an 8 GB PC's
//      1,536 MB a command, with the same rows as the two narrower statements it is made of.
//   2. A sort bigger than that limit finishes: SQLite writes sorted runs to its temporary files.
//   3. A statement that still grows (one 900 MB string) is stopped near the limit with the error that says what to do,
//      and one whose temporary files would take the drive's free space is stopped too. Garbage .NET hasn't collected
//      yet isn't the statement's: it doesn't stop the next one (found when J82 was merged).
// And GAPS S109/S110 (J87, J88): the temporary files really go to the engine's folder; S110's grouped join, which would
// keep each cell's whole Json for each traversal, is said before it runs ("Not run", with its size); the same join
// narrowed runs; grouped and sorted statements over the same cells that keep no Json, or keep it once, aren't refused.
// And GAPS S126/S127 (J99): a join that names a cell's Json outside a GROUP BY makes each cell's JSON once, not once per
// traversal (the same rows, in seconds instead of minutes); sorts inside a CTE and in a compound SELECT, and a sort of a
// list view's entry Json, are said before they run.
// And GAPS S142/S143 (J109): what a statement works out of a cell's Json alone (length(c.Json), c.Json ->> '$.EditorID')
// is worked out once per cell in any join: grouped, a LEFT JOIN, a FROM with a subquery, a correlated subquery, through a
// subquery that hands the Json up; the same rows as narrower statements, in seconds. A sort of a big value handed up out
// of a subquery or CTE is said before it runs.
// And GAPS S144/S145 (J116): expressions that may give a value on NULL (coalesce, IS NULL, CASE) are worked out once per
// cell too, a LEFT JOIN's row without a cell reading what they give on NULL; the choice to work the cells out once counts
// only the cells the view's own conditions keep; a positional parameter gets a plain answer (S161).
// The limit is the memory the process holds when the test starts plus the command's allowance, as a worker's would be.
using System.Globalization;
using SFSearch.Query;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class MemoryTests
{
    const string P = "SFBGS00D.esm";

    /// <summary>The limit the engine of the test asks for (MB, 0: none): as a worker's, set again for each command.</summary>
    static long _limitMB;
    /// <summary>What a statement's temporary files may take in the test (MB, 0: the engine's own rule).</summary>
    static long _tempMB;

    static QueryEngine Engine(Ctx c, string folder, long allowanceMB, long scratchSpareMB = 10240)
    {
        string path = Path.Combine(c.Work, folder);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        Allow(allowanceMB);
        return new QueryEngine(c.Records, new QueryOptions
        {
            ScratchFolder = path, ScratchSpareMB = scratchSpareMB,
            MemoryLimitMB = () => _limitMB, MemoryLimitSetting = "workerMaxMB in sfsearch.json", TempFilesLimitMB = () => _tempMB,
        });
    }

    /// <summary>A command's allowance on top of what the test process holds now. The garbage of the steps before is
    /// collected first: the statement's watch collects it before it would stop a statement (Session.Memory.cs), so
    /// counted in the start it would only widen the room, and the 900 MB string below would then reach SQLite's slow
    /// growth of a string past about 500 MB (notes/fix-j82-sqlite-memory.md) before the limit: minutes, not a stop.</summary>
    static void Allow(long allowanceMB)
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        _limitMB = allowanceMB > 0 ? Math.Max(0, MemoryNow.PrivateMB()) + allowanceMB : 0;
    }

    static QueryAnswer Run(QueryEngine e, string sql, int page = 1_000_000, bool allowHeavy = true) =>
        e.Run(new QueryRequest { Sql = sql, Plugins = new[] { P }, PageRows = page, AllowHeavy = allowHeavy });

    /// <summary>Arrays of <paramref name="mb"/> MB in all, written to and let go: garbage the GC hasn't collected.</summary>
    static void Garbage(int mb)
    {
        var keep = new List<byte[]>();
        for (int i = 0; i < mb / 4; i++) { var b = new byte[4 << 20]; Array.Fill(b, (byte)1); keep.Add(b); }
        GC.KeepAlive(keep);
    }

    const string Big = "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n LIMIT 3000000) SELECT x, randomblob(1000) AS b FROM n";

    public static string Run(Ctx c)
    {
        var said = new List<string>();
        // SQLite's temporary files go under the engine's scratch folder (SqliteTuning.TempFolder)
        string temp = Path.Combine(Path.GetFullPath(c.Work), "sql", "temp");
        Check(string.Equals(SqliteTuning.TempFolderInUse, temp, StringComparison.OrdinalIgnoreCase) || SqliteTuning.TempFolderInUse?.StartsWith(Path.GetFullPath(c.Work), StringComparison.OrdinalIgnoreCase) == true,
            $"SQLite's temporary files go to {SqliteTuning.TempFolderInUse ?? "(not set)"}, not under {c.Work}");

        // 1. S104 on an 8 GB PC's allowance
        using (var e = Engine(c, "sql-memory", 1536))
        {
            Must(Run(e, "SELECT count(*) FROM Cell WHERE Json IS NOT NULL"), "Cell whole");
            const string join = "FROM Cell c JOIN Cell_Traversals t ON t.FormKey = c.FormKey AND t.Plugin = c.Plugin";
            string sql = $"SELECT c.FormKey, c.Plugin, c.Location, t.Plugin, count(*) {join} GROUP BY c.FormKey, c.Plugin ORDER BY c.Plugin, c.FormKey";
            var plan = Must(Run(e, "EXPLAIN QUERY PLAN " + sql), "plan");
            Check(plan.Rows.Any(r => Cell(r[3]).StartsWith("CO-ROUTINE", StringComparison.Ordinal) || Cell(r[3]).StartsWith("MATERIALIZE", StringComparison.Ordinal)),
                $"the typed view is worked out on its own, not folded into the grouped statement: {string.Join(" / ", plan.Rows.Select(r => Cell(r[3])))}");
            var a = Run(e, sql);
            Must(a, sql);
            var counts = Must(Run(e, $"SELECT c.FormKey, c.Plugin, t.Plugin, count(*) {join} GROUP BY c.FormKey, c.Plugin ORDER BY c.Plugin, c.FormKey"), "counts");
            var loc = Must(Run(e, "SELECT FormKey, Location FROM Cell"), "locations").Rows.ToDictionary(r => Cell(r[0]), r => Cell(r[1]));
            var want = counts.Rows.Select(r => string.Join(" | ", Cell(r[0]), Cell(r[1]), loc[Cell(r[0])], Cell(r[2]), Cell(r[3]))).ToList();
            Same("S104's join against its two narrower statements", Rows(a), want, ordered: true);
            long traversals = counts.Rows.Sum(r => Convert.ToInt64(r[3], CultureInfo.InvariantCulture));
            said.Add($"S104's join of {traversals:N0} traversals of {want.Count:N0} cells under 1.5 GB ({a.Cost.Seconds:F1} s)");

            // S110: the same join keeping each cell's whole JSON (3.2 TB through the sorter) is said before it runs, with
            // its size, past the 6 GB one statement may write at 1.5 GB a command (set here: the test's limit is the
            // process's memory plus the allowance, so the rule would give more); asked to run anyway, it isn't started,
            // as no drive has room for it. (S110's own statement, length(c.Json), no longer keeps the Json: S142 below.)
            _tempMB = 6144;
            string s110 = $"SELECT c.FormKey, max(c.Json), count(*) {join} GROUP BY c.FormKey";
            var j = Run(e, s110, allowHeavy: false);
            Check(j.NotRun != null && j.NotRun.What.StartsWith("This query's GROUP BY would keep the whole Json of Cell for each of its ", StringComparison.Ordinal)
                && j.NotRun.What.Contains("One query may write 6.0 GB. Narrow it: name the fields you need instead of Json", StringComparison.Ordinal)
                && j.NotRun.TempFilesMB > 1_000_000 && j.Cost.Seconds < 60,
                $"S110's join with Json: {j.NotRun?.What ?? j.Error} ({j.Cost.Seconds:F1} s)");
            said.Add($"S110's join with Json not run: about {j.NotRun!.TempFilesMB / 1048576.0:F1} TB of temporary files said in {j.Cost.Seconds:F1} s");
            var ja = Run(e, s110);
            Check(ja.Error != null && ja.Error.StartsWith("the query wasn't run: its GROUP BY would keep the whole Json of Cell", StringComparison.Ordinal) && ja.Fix != null && ja.Fix.StartsWith("narrow it:", StringComparison.Ordinal),
                $"S110's join with Json and allow_heavy: {ja.Error} Fix: {ja.Fix}");
            // The same join narrowed to one cell (the one with the fewest traversals) runs, with that cell's count
            var small = counts.Rows.OrderBy(r => Convert.ToInt64(r[3], CultureInfo.InvariantCulture)).First();
            var n = Must(Run(e, $"SELECT c.FormKey, length(c.Json), count(*) {join} WHERE c.FormKey = '{Cell(small[0])}' GROUP BY c.FormKey", allowHeavy: false), "S110 narrowed");
            Check(n.Rows.Count == 1 && Convert.ToInt64(n.Rows[0][2], CultureInfo.InvariantCulture) == Convert.ToInt64(small[3], CultureInfo.InvariantCulture), $"S110 narrowed to {Cell(small[0])}: {string.Join(" / ", Rows(n))}");
            // S127: sorts the estimate didn't see before J99, said before they run: a GROUP BY inside a CTE, and the parts
            // of a compound SELECT sorted by its ORDER BY
            string cte = $"WITH x AS (SELECT c.FormKey, max(c.Json) AS j, count(*) AS k {join} GROUP BY c.FormKey) SELECT FormKey, k FROM x ORDER BY k DESC";
            var jc = Run(e, cte, allowHeavy: false);
            Check(jc.NotRun != null && jc.NotRun.What.StartsWith("This query's GROUP BY in its CTE x would keep the whole Json of Cell for each of its ", StringComparison.Ordinal)
                && jc.NotRun.TempFilesMB > 1_000_000 && jc.Cost.Seconds < 60, $"S127, a sort in a CTE: {jc.NotRun?.What ?? jc.Error ?? "it ran"} ({jc.Cost.Seconds:F1} s)");
            string compound = $"SELECT c.FormKey, c.Json, t.Idx {join} UNION ALL SELECT FormKey, Json, NULL FROM Cell ORDER BY 1";
            var ju = Run(e, compound, allowHeavy: false);
            Check(ju.NotRun != null && ju.NotRun.What.StartsWith("This query's ORDER BY in part 1 of 2 would keep the whole Json of Cell for each of its ", StringComparison.Ordinal)
                && ju.NotRun.TempFilesMB > 1_000_000 && ju.Cost.Seconds < 60, $"S127, a sort in a compound SELECT: {ju.NotRun?.What ?? ju.Error ?? "it ran"} ({ju.Cost.Seconds:F1} s)");
            said.Add($"S127: a GROUP BY in a CTE ({jc.NotRun!.TempFilesMB / 1048576.0:F1} TB) and a compound's ORDER BY ({ju.NotRun!.TempFilesMB / 1048576.0:F1} TB) not run, said in {jc.Cost.Seconds:F1} s and {ju.Cost.Seconds:F1} s");
            // S143: a cell's whole Json handed up out of a subquery (without a name) or a CTE and sorted by the SELECT
            // around it: said before it runs (before J109 it ran until its temporary files passed the limit)
            var hs = Run(e, $"SELECT * FROM (SELECT c.FormKey, c.Json {join}) ORDER BY 1", allowHeavy: false);
            Check(hs.NotRun != null && hs.NotRun.What.StartsWith("This query's ORDER BY would keep Json of a subquery (the whole Json of Cell) for each of its ", StringComparison.Ordinal)
                && hs.NotRun.TempFilesMB > 1_000_000 && hs.Cost.Seconds < 60, $"S143, a subquery's Json sorted around it: {hs.NotRun?.What ?? hs.Error ?? "it ran"} ({hs.Cost.Seconds:F1} s)");
            var hc = Run(e, $"WITH x AS (SELECT c.FormKey, c.Json AS j {join}) SELECT FormKey, j FROM x ORDER BY FormKey", allowHeavy: false);
            Check(hc.NotRun != null && hc.NotRun.What.StartsWith("This query's ORDER BY would keep x.j (the whole Json of Cell) for each of its ", StringComparison.Ordinal)
                && hc.NotRun.TempFilesMB > 1_000_000 && hc.Cost.Seconds < 60, $"S143, a CTE's Json sorted around it: {hc.NotRun?.What ?? hc.Error ?? "it ran"} ({hc.Cost.Seconds:F1} s)");
            said.Add($"S143: a subquery's and a CTE's Json sorted around them not run ({hs.NotRun!.TempFilesMB / 1048576.0:F1} TB, {hc.NotRun!.TempFilesMB / 1048576.0:F1} TB), said in {hs.Cost.Seconds:F1} s and {hc.Cost.Seconds:F1} s");
            // A big value that isn't a record's Json: a list view's entry Json, sorted, past a limit of 64 MB
            _tempMB = 64;
            var jl = Run(e, "SELECT t.FormKey, t.Json FROM Cell_Traversals t ORDER BY t.Idx", allowHeavy: false);
            Check(jl.NotRun != null && jl.NotRun.What.StartsWith($"This query's ORDER BY would keep Cell_Traversals.Json for each of its {traversals:N0} rows", StringComparison.Ordinal),
                $"S127, a list view's entry Json sorted: {jl.NotRun?.What ?? jl.Error ?? "it ran"}");
            said.Add($"S127: {traversals:N0} traversals' entry Json sorted said as {jl.NotRun!.TempFilesMB:N0} MB");
            _tempMB = 6144;

            // S126: a join that names a cell's Json outside a GROUP BY makes each cell's JSON once, not once per traversal.
            // The cell with the most traversals: before J99 each of its rows made the cell's whole JSON again (the like-sized
            // NAViewport, 29,070 traversals of 6.4 MB: 270 s); the view is now worked out on its own and read first in the
            // join (Session.JsonJoins.cs), with the same rows
            var most = counts.Rows.OrderByDescending(r => Convert.ToInt64(r[3], CultureInfo.InvariantCulture)).First();
            string mostKey = Cell(most[0]);
            string s126 = $"SELECT c.FormKey, c.Json ->> '$.EditorID', t.Idx {join} WHERE c.FormKey = '{mostKey}' ORDER BY t.Idx";
            var p126 = Must(Run(e, "EXPLAIN QUERY PLAN " + s126, allowHeavy: false), "S126 plan");
            Check(p126.Rows.Any(r => Cell(r[3]).StartsWith("CO-ROUTINE", StringComparison.Ordinal) || Cell(r[3]).StartsWith("MATERIALIZE", StringComparison.Ordinal)),
                $"S126: the typed view is worked out on its own, not folded into the join: {string.Join(" / ", p126.Rows.Select(r => Cell(r[3])))}");
            var a126 = Must(Run(e, s126, allowHeavy: false), "S126");
            string editorId = Cell(Must(Run(e, $"SELECT EditorID FROM Cell WHERE FormKey = '{mostKey}'"), "EditorID").Rows[0][0]);
            var want126 = Must(Run(e, $"SELECT Idx FROM Cell_Traversals WHERE FormKey = '{mostKey}' ORDER BY Idx"), "S126's traversals").Rows
                .Select(r => string.Join(" | ", mostKey, editorId, Cell(r[0]))).ToList();
            Same("S126's join against its two narrower statements", Rows(a126), want126, ordered: true);
            Check(a126.Cost.Seconds < 120, $"S126's join of {want126.Count:N0} rows took {a126.Cost.Seconds:F1} s (before J99 about 270 s)");
            said.Add($"S126: {editorId}'s {want126.Count:N0} traversals with c.Json ->> '$.EditorID' in {a126.Cost.Seconds:F1} s, the same rows");

            // S142: what the statement works out of a cell's Json alone is worked out once per cell, in each shape of join
            // SFSearch can't reorder; each against narrower statements, each timed (before J109 the like-sized NAViewport's
            // length(c.Json) took 163 s read first, a LEFT JOIN over aseliving.esm 130 s, its correlated form 152 s)
            var lens = Must(Run(e, "SELECT FormKey, length(Json) FROM Cell"), "lengths").Rows.ToDictionary(r => Cell(r[0]), r => Cell(r[1]));
            string mostLen = lens[mostKey];
            var idx = want126.Select(w => w.Split(" | ")[2]).ToList();
            var perCell = counts.Rows.Select(r => string.Join(" | ", Cell(r[0]), lens[Cell(r[0])], Cell(r[3]))).ToList();
            var shapes = new (string What, string Sql, List<string> Want, string Column)[]
            {
                ("grouped (S110's statement)", $"SELECT c.FormKey, length(c.Json), count(*) {join} GROUP BY c.FormKey ORDER BY c.FormKey", perCell, "length(c.Json)"),
                ("per traversal", $"SELECT c.FormKey, length(c.Json), t.Idx {join} WHERE c.FormKey = '{mostKey}' ORDER BY t.Idx",
                    idx.Select(i => string.Join(" | ", mostKey, mostLen, i)).ToList(), "length(c.Json)"),
                ("a LEFT JOIN", $"SELECT t.Idx, c.Json ->> '$.EditorID', length(c.Json) FROM Cell_Traversals t LEFT JOIN Cell c ON c.FormKey = t.FormKey AND c.Plugin = t.Plugin WHERE t.FormKey = '{mostKey}' ORDER BY t.Idx",
                    idx.Select(i => string.Join(" | ", i, editorId, mostLen)).ToList(), "c.Json ->> '$.EditorID'"),
                ("a LEFT JOIN without a match", $"SELECT t.Idx, c.Json ->> '$.EditorID', length(c.Json) FROM Cell_Traversals t LEFT JOIN Cell c ON c.FormKey = t.FormKey AND c.Plugin = t.Plugin AND c.EditorID = '' WHERE t.FormKey = '{mostKey}' ORDER BY t.Idx",
                    idx.Select(i => string.Join(" | ", i, "", "")).ToList(), "length(c.Json)"),
                ("a FROM with a subquery", $"SELECT c.FormKey, length(c.Json), t.n FROM Cell c JOIN (SELECT FormKey, Plugin, count(*) AS n FROM Cell_Traversals GROUP BY FormKey, Plugin) t ON t.FormKey = c.FormKey AND t.Plugin = c.Plugin ORDER BY c.FormKey",
                    perCell, "length(c.Json)"),
                ("a correlated subquery", $"SELECT t.Idx, (SELECT c.Json ->> '$.EditorID' FROM Cell c WHERE c.FormKey = t.FormKey AND c.Plugin = t.Plugin) FROM Cell_Traversals t WHERE t.FormKey = '{mostKey}' ORDER BY t.Idx",
                    idx.Select(i => string.Join(" | ", i, editorId)).ToList(), "(SELECT c.Json ->> '$.EditorID' FROM Cell c WHERE c.FormKey = t.FormKey AND c.Plugin = t.Plugin)"),
                ("a correlated subquery in an aggregate", $"SELECT t.FormKey, count(*), max((SELECT length(c.Json) FROM Cell c WHERE c.FormKey = t.FormKey AND c.Plugin = t.Plugin)) FROM Cell_Traversals t GROUP BY t.FormKey ORDER BY t.FormKey",
                    counts.Rows.Select(r => string.Join(" | ", Cell(r[0]), Cell(r[3]), lens[Cell(r[0])])).ToList(), "max((SELECT length(c.Json) FROM Cell c WHERE c.FormKey = t.FormKey AND c.Plugin = t.Plugin))"),
                ("the Json handed up by a subquery", $"SELECT x.FormKey, length(x.Json), t.Idx FROM (SELECT FormKey, Plugin, Json FROM Cell) x JOIN Cell_Traversals t ON t.FormKey = x.FormKey AND t.Plugin = x.Plugin WHERE t.FormKey = '{mostKey}' ORDER BY t.Idx",
                    idx.Select(i => string.Join(" | ", mostKey, mostLen, i)).ToList(), "length(x.Json)"),
            };
            var times = new List<string>();
            foreach (var (what, q, wanted, column) in shapes)
            {
                var a142 = Must(Run(e, q, allowHeavy: false), "S142, " + what);
                Same("S142, " + what + ", against narrower statements", Rows(a142), wanted, ordered: true);
                Check(a142.Columns.Contains(column), $"S142, {what}: the column keeps its name {column}: {string.Join(", ", a142.Columns)}");
                Check(a142.Cost.Seconds < 60, $"S142, {what}: {a142.Cost.Seconds:F1} s");
                times.Add($"{what} {a142.Cost.Seconds:F1} s");
            }
            said.Add($"S142: {string.Join("; ", times)} ({idx.Count:N0} traversals of {editorId}, {perCell.Count:N0} cells), the same rows");
            // S154: a cell's Json given to json_object in a join stays JSON (the view is read where the function reads it)
            var mark = Must(Run(e, $"SELECT json_type(json_object('j', c.Json), '$.j'), json_type(json_array(c.Json -> '$.EditorID')) FROM Cell c JOIN Cell_Traversals t ON t.FormKey = c.FormKey AND t.Plugin = c.Plugin WHERE c.FormKey = '{mostKey}' LIMIT 1", allowHeavy: false), "S154");
            Check(Cell(mark.Rows[0][0]) == "object" && Cell(mark.Rows[0][1]) == "array", $"S154: json_object of a cell's Json in a join gives {Cell(mark.Rows[0][0])}, not object");

            // S144 (J116): an expression of one cell that may give a value on NULL (coalesce, IS NULL, CASE, iif) is worked
            // out once per cell too; on a LEFT JOIN's side that can be NULL, a row without a cell reads what the expression
            // gives on NULL. Each against a narrower statement over Cell alone, the plan rewritten, timed
            var own = Must(Run(e, $"SELECT length(coalesce(Json, '')), Json IS NULL, CASE WHEN Json LIKE '%Traversals%' THEN 'x' ELSE 'y' END FROM Cell WHERE FormKey = '{mostKey}'"), "S144's cell").Rows[0].Select(Cell).ToList();
            var loose = new (string What, string Sql, List<string> Want)[]
            {
                ("an inner join", $"SELECT t.Idx, length(coalesce(c.Json, '')), c.Json IS NULL, CASE WHEN c.Json LIKE '%Traversals%' THEN 'x' ELSE 'y' END {join} WHERE c.FormKey = '{mostKey}' ORDER BY t.Idx",
                    idx.Select(i => string.Join(" | ", i, own[0], own[1], own[2])).ToList()),
                ("a LEFT JOIN", $"SELECT t.Idx, coalesce(c.Json, '{{}}') ->> '$.EditorID', c.Json IS NOT NULL, iif(c.Json IS NULL, 'no', 'yes') FROM Cell_Traversals t LEFT JOIN Cell c ON c.FormKey = t.FormKey AND c.Plugin = t.Plugin WHERE t.FormKey = '{mostKey}' ORDER BY t.Idx",
                    idx.Select(i => string.Join(" | ", i, editorId, "1", "yes")).ToList()),
                ("a LEFT JOIN without a match", $"SELECT t.Idx, coalesce(c.Json, '{{}}') ->> '$.EditorID', c.Json IS NOT NULL, iif(c.Json IS NULL, 'no', 'yes') FROM Cell_Traversals t LEFT JOIN Cell c ON c.FormKey = t.FormKey AND c.Plugin = t.Plugin AND c.EditorID = '' WHERE t.FormKey = '{mostKey}' ORDER BY t.Idx",
                    idx.Select(i => string.Join(" | ", i, "", "0", "no")).ToList()),
                ("a correlated subquery", $"SELECT t.Idx, (SELECT iif(c.Json IS NULL, 'no', 'yes') FROM Cell c WHERE c.FormKey = t.FormKey AND c.Plugin = t.Plugin) FROM Cell_Traversals t WHERE t.FormKey = '{mostKey}' ORDER BY t.Idx",
                    idx.Select(i => string.Join(" | ", i, "yes")).ToList()),
            };
            var looseTimes = new List<string>();
            foreach (var (what, q, wanted) in loose)
            {
                var a144 = Must(Run(e, q, allowHeavy: false), "S144, " + what);
                Same("S144, " + what + ", against narrower statements", Rows(a144), wanted, ordered: true);
                // A LEFT JOIN that matches no cell makes no cell's JSON folded: it stays folded
                var p144 = Must(Run(e, "EXPLAIN QUERY PLAN " + q, allowHeavy: false), "S144 plan, " + what).Rows.Select(r => Cell(r[3])).ToList();
                Check(what.EndsWith("without a match", StringComparison.Ordinal) || p144.Any(l => l is "CO-ROUTINE c" or "MATERIALIZE c" || l.StartsWith("SEARCH c USING INDEX sfs_p", StringComparison.Ordinal)),
                    $"S144, {what}: worked out once per cell: {string.Join(" / ", p144)}");
                Check(a144.Cost.Seconds < 60, $"S144, {what}: {a144.Cost.Seconds:F1} s");
                looseTimes.Add($"{what} {a144.Cost.Seconds:F1} s");
            }
            // A positional parameter: a plain answer before anything is read (GAPS S161; it was an exception)
            var pos = Run(e, $"SELECT t.Idx, c.Json ->> ? {join}", allowHeavy: false);
            Check(pos.Error != null && pos.Error.StartsWith("the SQL has the positional parameter ?, and parameters are given by name", StringComparison.Ordinal), $"S161: {pos.Error ?? "it ran"}");
            said.Add($"S144: {string.Join("; ", looseTimes)}, the same rows; S161: a positional parameter answered \"{pos.Error}\"");

            // S145 (J116): the choice between working out the cells once and folding the view counts only the cells the
            // view's own conditions keep: a LEFT JOIN narrowed by its ON to one cell whose traversals make less JSON than
            // twice all the cells (before J116 it was folded: that cell's Json kept in an automatic index, read back per row)
            long all = lens.Values.Sum(v => long.Parse(v, CultureInfo.InvariantCulture));
            var edids = Must(Run(e, "SELECT FormKey, EditorID FROM Cell"), "EditorIDs").Rows.ToDictionary(r => Cell(r[0]), r => Cell(r[1]));
            var narrow = counts.Rows.Select(r => (Key: Cell(r[0]), N: Convert.ToInt64(r[3], CultureInfo.InvariantCulture)))
                .Select(r => (r.Key, r.N, Bytes: r.N * long.Parse(lens[r.Key], CultureInfo.InvariantCulture)))
                .Where(r => r.N > 4 && r.Bytes < all * 2 && edids[r.Key].Length > 0).OrderByDescending(r => r.Bytes).First();
            string q145 = $"SELECT t.Idx, c.Json ->> '$.EditorID' FROM Cell_Traversals t LEFT JOIN Cell c ON c.FormKey = t.FormKey AND c.Plugin = t.Plugin AND c.EditorID LIKE '{edids[narrow.Key]}' WHERE t.FormKey = '{narrow.Key}' ORDER BY t.Idx";
            var p145 = Must(Run(e, "EXPLAIN QUERY PLAN " + q145, allowHeavy: false), "S145 plan").Rows.Select(r => Cell(r[3])).ToList();
            Check(p145.Any(l => l is "CO-ROUTINE c" or "MATERIALIZE c"), $"S145: {narrow.Key}'s {narrow.N:N0} traversals ({narrow.Bytes / 1048576.0:F0} MB of its JSON, all cells {all / 1048576.0:F0} MB) worked out once: {string.Join(" / ", p145)}");
            var a145 = Must(Run(e, q145, allowHeavy: false), "S145");
            var want145 = Must(Run(e, $"SELECT Idx FROM Cell_Traversals WHERE FormKey = '{narrow.Key}' ORDER BY Idx"), "S145's traversals").Rows.Select(r => Cell(r[0]) + " | " + edids[narrow.Key]).ToList();
            Same("S145 against narrower statements", Rows(a145), want145, ordered: true);
            said.Add($"S145: {edids[narrow.Key]}'s {narrow.N:N0} traversals ({narrow.Bytes / 1048576.0:F0} MB of JSON under the {2 * all / 1048576.0:F0} MB of twice all cells) worked out once in {a145.Cost.Seconds:F1} s, the same rows");


            // Grouped and sorted statements over the same cells that keep no Json, or keep it once a cell, run
            string[] normal =
            {
                $"SELECT c.Location, count(*) {join} GROUP BY c.Location ORDER BY 2 DESC",
                "SELECT Plugin, count(*) FROM Cell WHERE Json LIKE '%Location%' GROUP BY Plugin",
                "SELECT Plugin, max(length(Json)), count(*) FROM Cell GROUP BY Plugin",
                "SELECT FormKey, Json FROM Cell ORDER BY EditorID",
                "SELECT * FROM Cell ORDER BY length(Json) DESC",
                "SELECT DISTINCT Json FROM Cell",
            };
            foreach (string q in normal) Must(Run(e, q, page: 1, allowHeavy: false), q);
            _tempMB = 0;
            said.Add($"S110 narrowed to one cell runs; {normal.Length} grouped and sorted statements over its cells run");
        }

        // 2. A 3 GB sort under the same allowance: written out in runs into the engine's own folder (S109: SQLite's
        //    temporary files, delete-on-close, are seen there while it runs), every row comes back
        using (var e = Engine(c, "sql-memory-sort", 1536))
        {
            // The temporary files' limit while it runs (no Json here, so no estimate before): stopped at 1 GB
            _tempMB = 1024;
            var stopped = Run(e, $"SELECT x FROM ({Big}) ORDER BY b", page: 1);
            _tempMB = 0;
            Check(stopped.Error != null && stopped.Error.StartsWith("the query was stopped: its temporary files passed ", StringComparison.Ordinal) && stopped.Error.Contains("more than one query may write (1.0 GB)", StringComparison.Ordinal)
                && stopped.Fix != null && stopped.Fix.StartsWith("narrow it:", StringComparison.Ordinal) && stopped.Cost.Seconds < 60,
                $"the 3 GB sort under a 1 GB limit of temporary files: {stopped.Error} Fix: {stopped.Fix} ({stopped.Cost.Seconds:F1} s)");
            string folder = Path.Combine(Path.GetFullPath(c.Work), "sql-memory-sort", "temp");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var done = new CancellationTokenSource();
            var watch = Task.Run(() =>
            {
                while (!done.IsCancellationRequested)
                {
                    try { foreach (var f in Directory.EnumerateFiles(folder)) seen.Add(Path.GetFileName(f)); }
                    catch (IOException) { }
                    Thread.Sleep(20);
                }
            });
            var a = Must(Run(e, $"SELECT x FROM ({Big}) ORDER BY b", page: 1), "3 GB sort");
            done.Cancel(); watch.Wait();
            Check(a.TotalRows == 3_000_000, $"the 3 GB sort gave {a.TotalRows:N0} rows, not 3,000,000");
            Check(string.Equals(SqliteTuning.TempFolderInUse, folder, StringComparison.OrdinalIgnoreCase) && seen.Count > 0 && Directory.GetFiles(folder).Length == 0,
                $"the 3 GB sort's temporary files: {seen.Count} seen in {folder} (in use: {SqliteTuning.TempFolderInUse}), {Directory.GetFiles(folder).Length} left");
            said.Add($"a 3 GB sort under 1.5 GB ({a.Cost.Seconds:F1} s), its {seen.Count} temporary file(s) in the engine's folder; under a 1 GB limit stopped in {stopped.Cost.Seconds:F1} s");
        }

        // 3. One 900 MB string under 300 MB: stopped near the limit, with what to do
        using (var e = Engine(c, "sql-memory-stop", 300))
        {
            var a = Run(e, "SELECT length(group_concat(b)) FROM (WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n LIMIT 900000) SELECT hex(randomblob(500)) AS b FROM n)");
            Check(a.Error != null && a.Error.StartsWith("the query was stopped: it took this command to ", StringComparison.Ordinal) && a.Error.Contains("GB of memory, and one command may use", StringComparison.Ordinal)
                && a.Fix != null && a.Fix.StartsWith("narrow it:", StringComparison.Ordinal) && a.Fix.EndsWith("or raise workerMaxMB in sfsearch.json if this PC has the memory to spare", StringComparison.Ordinal),
                $"a 900 MB string under 300 MB: {a.Error} Fix: {a.Fix}");
            said.Add($"a 900 MB string stopped: \"{a.Error}\"");
            // The engine answers the next command as before (a new command, a new allowance). The allowance is room
            // above what the process holds now, and the watch stops a statement a little under the limit, by a 16th of
            // it and at most 256 MB (Session.Memory.cs): late in a full run this process holds about 1.6 GB, which left
            // 180 MB of the 300 for the cells' read (250 MB when the test runs alone) and stopped it. So the margin's
            // most is added, and the read has its 300 MB however much the tests before it left in the process
            Allow(300 + 256);
            Must(Run(e, "SELECT count(*) FROM Cell"), "after the stop");
            // Garbage .NET hasn't collected (here 400 MB of arrays let go, as a read leaves some) isn't the statement's:
            // the watch has it collected before it would stop the statement for memory
            Allow(300);
            Garbage(400);
            long before = MemoryNow.PrivateMB();
            Check(before > _limitMB, $"the garbage is in the private bytes: {before} MB against a limit of {_limitMB} MB");
            Must(Run(e, "SELECT count(*) FROM Cell"), "with .NET's garbage past the limit");
            said.Add($"a statement after 400 MB of .NET garbage ({before:N0} MB private, the limit {_limitMB:N0} MB) runs");
        }

        // 4. Temporary files that would leave less free than the engine keeps: stopped once they passed 1 GB
        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(c.Work))!).AvailableFreeSpace >> 20;
        using (var e = Engine(c, "sql-memory-disk", 0, scratchSpareMB: free + 100_000))
        {
            var a = Run(e, $"SELECT x FROM ({Big}) ORDER BY b", page: 1);
            Check(a.Error != null && a.Error.StartsWith("the query was stopped: its temporary files left ", StringComparison.Ordinal) && a.Fix != null && a.Fix.EndsWith("or free some disk space there", StringComparison.Ordinal),
                $"temporary files past the room kept free: {a.Error} Fix: {a.Fix}");
            said.Add("temporary files past the room kept free stopped");
        }
        return string.Join("; ", said);
    }
}
