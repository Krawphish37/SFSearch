// A statement's temporary files, said before it starts (GAPS S110 and S127; J88, J99). A sort (GROUP BY, ORDER BY,
// DISTINCT, a window, the parts of a UNION, INTERSECT or EXCEPT) keeps the columns it needs for each row it sorts, and
// writes them to temporary files when they don't fit in its share of memory (Session.cs SpillPragmas). Most sorts write a
// few GB at most. The kind that writes far more is a sort that keeps a big value for every row of a join: Cell joined to
// Cell_Traversals over SFBGS00D.esm, grouped with length(c.Json), keeps each cell's JSON for each of its traversals,
// 3.2 TB. Such a statement is found here, after its record types are read and before it runs, in every SELECT of it:
// the statement's own, each part of a compound SELECT, each CTE and each subquery.
//   1. Which big values each SELECT's sort keeps: a grouped SELECT (GROUP BY, a window) keeps every column its result,
//      HAVING, GROUP BY and ORDER BY are made from; a SELECT that only sorts (ORDER BY, DISTINCT, a part of a compound
//      that is sorted or made distinct) keeps its result columns' values, so only a result column that is a big column
//      itself (or *). A big column named only in WHERE or ON isn't kept. Big columns: a record's whole Json (a typed view
//      or Records over records read whole), a list view's entry Json, and a typed view's columns that hold a part of the
//      record's JSON (kind Json).
//   2. SQLite's plan says whether that SELECT sorts at all: "USE TEMP B-TREE" (or "UNION USING TEMP B-TREE", …) among
//      the plan lines of that SELECT. The plan is a tree: a CTE's or a subquery's lines are under its CO-ROUTINE or
//      MATERIALIZE line (named as the statement names it), a scalar or IN subquery's under "SCALAR SUBQUERY n" (n is
//      SQLite's number of the SELECT: its parser numbers each SELECT when it has read all of it), a compound's parts
//      under COMPOUND QUERY or MERGE in their order. A GROUP BY that an index answers in order sorts nothing.
//   3. How much: that SELECT's own FROM and WHERE counted (no sort made, no record's Json made), each row weighted by the
//      size of each kept record's stored Json (read by length from the scratch tables, not the JSON itself) and by the
//      length of each other big column it keeps (worked out once per row, as the statement would). A DISTINCT (or a
//      UNION's part kept in a temporary b-tree) keeps each distinct row once: counted so; a UNION SQLite merges sorts
//      each part whole first (MERGE, "USE TEMP B-TREE FOR ORDER BY" in each part): every row counted. A record view SQLite makes a table of first (MATERIALIZE,
//      an automatic index) keeps each of its records' Json once more.
//   4. Past what one statement may write (TempFilesLimit: 4 times the command's memory, 4 to 32 GB) it isn't run:
//      "Not run" with the size, the time and how to narrow it, like a heavy read (J33's answers; no index would help).
//      allow_heavy runs it, its temporary files then allowed to its estimate and half again, when the drive has room
//      (half its free space, keeping ScratchSpareMB free); without room it isn't run at all.
// What isn't estimated stays with the watch while it runs (Session.Memory.cs): a SELECT that reads a column of the
// statement around it (a correlated subquery: it can't be counted alone), a part of a recursive CTE, a big value passed
// up out of a subquery or CTE and sorted by the SELECT around it, and big values whose size SQLite only knows once it
// has made them: an aggregate's result (group_concat, json_group_array) and an expression's result (Json ->> '$.Field')
// kept by a sort.
using System.Globalization;
using Microsoft.Data.Sqlite;
using SFSearch.Records;
using SQLitePCL;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    sealed partial class Session
    {
        /// <summary>For each record view this statement reads whole (a typed view, or Records), the SELECTs that give each
        /// record's FormKey, plugin and the size of its stored Json: what <see cref="AdmitTempFiles"/> weighs a row by.
        /// Filled by CreateViews.</summary>
        readonly Dictionary<string, List<string>> _jsonSources = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The temporary files this statement may write (MB) when allow_heavy let it past the limit; 0: the limit.</summary>
        long _tempAllowedMB;

        /// <summary>Seconds per GB of records' Json a sort keeps, writes, merges and reads back. Measured 2026-11-15 on
        /// astra-19 sidearm.esm's cells joined to their 3,697 traversals and grouped with length(c.Json), 3.0 GB of Json,
        /// under 1.5 GB a command: 13.1 s (notes/fix-j87-j88-sqlite.md); 3 GB of 1,000-byte rows sorted: 11 to 16 s.</summary>
        const double TempSecondsPerGB = 4.5;

        /// <summary>A typed view's records read whole from one scratch table: the SELECT of their Json sizes.</summary>
        void NoteJsonSource(ViewSpec view, ViewBuilder.Member mem)
        {
            if (view.IsList) return;
            string plugin = mem.PluginLiteral != null ? SqlText.Literal(mem.PluginLiteral) : "r.Plugin";
            string types = string.Join(", ", view.Types.Select(SqlText.Literal));
            AddJsonSource(view.Name, $"SELECT r.FormKey, {plugin}, length(r.Json) FROM {mem.Table ?? mem.Db + ".Rec"} r WHERE r.Type IN ({types}){(mem.Where != null ? " AND " + mem.Where : "")}");
        }

        /// <summary>Records' rows from one scratch table (<paramref name="where"/>: "" or " WHERE …").</summary>
        void NoteJsonSource(string table, string plugin, string where) =>
            AddJsonSource("Records", $"SELECT r.FormKey, {plugin}, length(r.Json) FROM {table} r{where}");

        void AddJsonSource(string view, string select)
        {
            if (!_jsonSources.TryGetValue(view, out var list)) _jsonSources[view] = list = new();
            list.Add(select);
        }

        /// <summary>A view the statement names that has a big column: a record's whole Json (<see cref="RecordJson"/>:
        /// weighed by its stored size), or other columns that hold JSON (weighed by their length). Or a subquery or CTE
        /// read in a FROM that hands such values up (<see cref="Carried"/>, GAPS S143): its <see cref="Columns"/> are the
        /// names it gives them, and in the count it gives each one's size in its place.</summary>
        sealed record BigView(string Name, bool RecordJson, HashSet<string> Columns)
        {
            public bool Carried { get; init; }
            /// <summary>What a carried column holds, as the answer says it ("the whole Json of Cell").</summary>
            public string? Origin { get; init; }
        }

        /// <summary>The views of the statement with a big column the statement reads.</summary>
        Dictionary<string, BigView> BigViews(Probed probed)
        {
            var big = new Dictionary<string, BigView>(StringComparer.OrdinalIgnoreCase);
            foreach (var (view, used) in probed.Views)
            {
                bool record = !view.IsList && _jsonSources.ContainsKey(view.Name);
                var cols = new HashSet<string>(view.Columns.Where(c => c.Kind == "Json" && used.Contains(c.Name)).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
                if (view.IsList && !view.ScalarEntries && used.Contains("Json")) cols.Add("Json");
                if (record || cols.Count > 0) big[view.Name] = new BigView(view.Name, record, cols);
            }
            if (_jsonSources.ContainsKey("Records")) big["Records"] = new BigView("Records", true, new(StringComparer.OrdinalIgnoreCase));
            return big;
        }

        /// <summary>
        /// True when the statement may run. False with <see cref="QueryAnswer.NotRun"/> set when its sorts would write
        /// more temporary files than one statement may and the request doesn't allow heavy work. Throws when it is allowed
        /// and the drive hasn't the room.
        /// </summary>
        bool AdmitTempFiles(string sql, List<Tok> toks, Probed probed)
        {
            _tempAllowedMB = 0;
            if (toks.Count == 0 || toks[0].Is("EXPLAIN") || toks[0].Is("PRAGMA")) return true;
            var big = BigViews(probed);
            if (big.Count == 0) return true;
            var cores = Core.All(toks);
            var first = cores.FirstOrDefault(c => c.Group.Open < 0);
            if (first == null) return true;
            // By the words first (no plan, no count): the SELECTs whose sort keeps a big value (of its own FROM's views, or
            // handed up by a subquery or CTE it reads: GAPS S143), and whether a record's Json is named in a join (SQLite
            // may keep it once in an automatic index)
            var keeping = new List<(Core Core, List<Kept> Kept, List<FromItem> Carried)>();
            foreach (var c in cores)
            {
                if (!c.Sorts) continue;
                var carried = CarriedItems(sql, toks, cores, c, big, probed, 0);
                if (KeptValues(toks, c, big, carried) is { Count: > 0 } kept) keeping.Add((c, kept, carried));
            }
            bool stored = JoinsRows(toks) && cores.Any(c => Items(toks, c, big).Any(i => i.Big.RecordJson));
            if (keeping.Count == 0 && !stored) return true;
            var plan = PlanOf(sql, toks);
            if (plan == null) return true;
            var regions = new Regions(cores);
            regions.Walk(plan);

            string prefix = Prefix(sql, toks, first);
            var parts = new List<(Core Core, List<Kept> Kept, long Rows, long Bytes)>();
            var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Each SELECT whose sort the plan shows, weighed
                foreach (var (core, kept, carried) in keeping)
                    if (regions.Sorts(core) && Weigh(sql, toks, prefix, core, kept, probed, made, distinct: !regions.SortsEveryRow(core), carried: carried) is var (rows, bytes))
                        parts.Add((core, kept, rows, bytes));
                // A record view made into a table first (MATERIALIZE, an automatic index) keeps each of its records' Json once
                long once = 0;
                var onceViews = new List<string>();
                foreach (var c in cores)
                    foreach (var item in Items(toks, c, big).Where(i => i.Big.RecordJson))
                        if (!onceViews.Contains(item.Big.Name, StringComparer.OrdinalIgnoreCase) && regions.Stores(c, item.Alias, item.Big.Name))
                        {
                            using var cmd = _con.CreateCommand();
                            cmd.CommandText = $"SELECT total(n) FROM temp.{SizeTable(item.Big.Name, made)}";
                            once += (long)Convert.ToDouble(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                            onceViews.Add(item.Big.Name);
                        }
                return Decide(parts, once, onceViews);
            }
            catch (SqliteException e) when (e.SqliteErrorCode != raw.SQLITE_INTERRUPT) { return true; }
            finally
            {
                foreach (var name in made.Values) ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{name}");
            }
        }

        /// <summary>The statement's WITH clause, which a count of one of its SELECTs needs ("" without one).</summary>
        static string Prefix(string sql, List<Tok> toks, Core first) => toks[0].Is("WITH") ? sql[..toks[first.Select].Start] : "";

        /// <summary>The answer from the weighed sorts: run, Not run, allowed with a note, or an error.</summary>
        bool Decide(List<(Core Core, List<Kept> Kept, long Rows, long Bytes)> parts, long onceBytes, List<string> onceViews)
        {
            long bytes = parts.Sum(p => p.Bytes) + onceBytes;
            long tempMB = bytes >> 20, limitMB = MemoryLimit(), capMB = TempFilesLimit(_e._options, limitMB);
            if (tempMB <= capMB) return true;

            // Past the limit: said before it starts, naming the biggest sort
            string folder = SqliteTuning.TempFolderInUse ?? Path.Combine(_e._options.ScratchFolder, "temp");
            long free = FreeMB(folder), roomMB = free < 0 ? long.MaxValue : Math.Max(0, Math.Min(free / 2, free - _e._options.ScratchSpareMB));
            bool fits = tempMB * 3 / 2 <= roomMB;
            string kind, keeps;
            string onceText = $"the whole Json of {string.Join(" and ", onceViews)} once in a temporary index";
            if (parts.Count > 0)
            {
                var top = parts.OrderByDescending(p => p.Bytes).First();
                kind = top.Core.Kind + top.Core.Place;
                keeps = $"would keep {Describe(top.Kept)} for each of its {top.Rows:N0} rows";
                if (parts.Count > 1 && onceBytes > 0) keeps += $" (with its other sorts, and {onceText})";
                else if (parts.Count > 1) keeps += " (with its other sorts)";
                else if (onceBytes > 0) keeps += $" (and {onceText})";
            }
            else
            {
                kind = "join";
                keeps = "would keep " + onceText;
            }
            double seconds = tempMB / 1024.0 * TempSecondsPerGB;
            string what = $"This query's {kind} {keeps}: about {Gb(tempMB)} GB of temporary files, {Duration(seconds)} to write and sort. "
                + $"One query may write {GB(capMB)} GB{(limitMB > 0 && !HostTempLimit(_e._options) ? " (4 times what one command may use)" : "")}.";
            const string narrow = "Narrow it: name the fields you need instead of Json (a typed column, or Json ->> '$.Field'), or fewer rows into the sort (a WHERE on a plugin, a FormKey or a column).";
            string noRoom = $"The drive of {folder} has {Gb(Math.Max(0, free))} GB free, and one query's temporary files may take half of it, keeping {GB(_e._options.ScratchSpareMB)} GB free";
            if (!_req.AllowHeavy)
            {
                _answer.NotRun = new HeavyEstimate(seconds, SortMemoryMB(limitMB), what + " " + narrow
                    + (fits ? HeavyAnswers.NotRunTail(null, "a sort's temporary files hold the rows it sorts, and SFSearch keeps indexes only of where records and links are")
                        : $" allow_heavy wouldn't run it either: {char.ToLowerInvariant(noRoom[0])}{noRoom[1..]}."))
                    { TempFilesMB = tempMB, Allowable = fits };
                return false;
            }
            if (!fits)
                throw new QueryException($"the query wasn't run: its {kind} {keeps}, about {Gb(tempMB)} GB of temporary files. {noRoom} ({Gb(roomMB)} GB)",
                    char.ToLowerInvariant(narrow[0]) + narrow[1..].TrimEnd('.') + "; or free some disk space there");
            _tempAllowedMB = tempMB * 3 / 2;
            Note($"Allowed (allow_heavy): this query's {kind} {(keeps.StartsWith("would keep", StringComparison.Ordinal) ? "keeps" + keeps["would keep".Length..] : keeps)}, about {Gb(tempMB)} GB of temporary files (one query may otherwise write {GB(capMB)} GB).");
            return true;
        }

        /// <summary>"the whole Json of Cell and Cell_Traversals.Json": what a sort keeps.</summary>
        static string Describe(List<Kept> kept) => string.Join(" and ", kept
            .Select(k => k.Column == null ? $"the whole Json of {k.Item.Big.Name}"
                : k.Item.Big.Carried ? (k.Item.Alias.Length > 0 ? $"{k.Item.Alias}.{k.Column} ({k.Item.Big.Origin})" : $"{k.Column} of a subquery ({k.Item.Big.Origin})") : $"{k.Item.Big.Name}.{k.Column}")
            .Distinct(StringComparer.OrdinalIgnoreCase));

        static string Duration(double s) => s < 5400 ? Seconds(s) : $"about {s / 3600:#,##0} h";

        static string Gb(long mb) => (mb / 1024.0).ToString("#,##0.0", CultureInfo.InvariantCulture);

        /// <summary>What a spilling sort holds in memory (MB): its temporary tables' cache (SpillPragmas) and its runs.</summary>
        static long SortMemoryMB(long limitMB) => (limitMB > 0 ? Math.Max(64, limitMB / 8) : 1024) + 4 * SortRunKB / 1024;

        // -------------------------------------------------------------- The statement's SELECTs

        /// <summary>The SELECTs of one parenthesis (or of the statement): one, or the parts of a compound SELECT.</summary>
        sealed class Group
        {
            public readonly List<Core> Arms = new();
            /// <summary>The operator before each part after the first ("UNION ALL", "UNION", "INTERSECT", "EXCEPT").</summary>
            public readonly List<string> Ops = new();
            /// <summary>The token of the parenthesis that holds them; -1: the statement's own.</summary>
            public int Open = -1;
            /// <summary>The names the plan may give it: a CTE's name, a subquery's alias, and the aliases a CTE is read by.</summary>
            public readonly List<string> Names = new();
            /// <summary>Where it is, as the answer says it: "" (the statement's own), " in its CTE x", " in a subquery".</summary>
            public string Place = "";
            /// <summary>A compound with ORDER BY: each part is sorted.</summary>
            public bool Ordered;
            /// <summary>A part of a recursive CTE: run step by step, not estimated.</summary>
            public bool Recursive;
            public bool SetOp => Ops.Any(o => o != "UNION ALL");
        }

        /// <summary>One SELECT of the statement, by token index.</summary>
        sealed class Core
        {
            static readonly string[] Ends = { "WHERE", "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT" };

            /// <summary>SQLite's number of this SELECT (its parser numbers a SELECT when it has read all of it).</summary>
            public int Id;
            public int Select, End, Depth, Arm;
            public int From = -1, FromEnd = -1, Where = -1, WhereEnd = -1;
            public bool Grouped, Ordered, Distinct;
            /// <summary>The clauses whose columns a grouped SELECT keeps: the result, GROUP BY, HAVING, WINDOW, ORDER BY.</summary>
            public readonly List<(int Start, int End)> Kept = new();
            /// <summary>The result columns, split at its commas.</summary>
            public readonly List<(int Start, int End)> Results = new();
            public Group Group = null!;

            /// <summary>True when its words make a sort: a GROUP BY, a window, an ORDER BY, DISTINCT, or being a part of a
            /// compound that is sorted or made distinct.</summary>
            public bool Sorts => From >= 0 && !Group.Recursive && (Grouped || Ordered || Distinct || Group.Ordered || Group.SetOp);

            /// <summary>Only distinct rows are kept: DISTINCT without ORDER BY or grouping, or a part of UNION, INTERSECT, EXCEPT.</summary>
            public bool DistinctOnly => !Grouped && !Ordered && !Group.Ordered && (Distinct || Group.SetOp);

            public string Kind => Grouped ? "GROUP BY" : Ordered || Group.Ordered ? "ORDER BY" : Distinct ? "DISTINCT" : Group.Ops.FirstOrDefault(o => o != "UNION ALL") ?? "sort";

            public string Place => Group.Arms.Count > 1 ? $" in part {Arm + 1} of {Group.Arms.Count}{Group.Place}" : Group.Place;

            /// <summary>Every SELECT of the statement, with SQLite's numbers.</summary>
            public static List<Core> All(List<Tok> t)
            {
                var cores = new List<Core>();
                var groups = new Dictionary<int, Group>();
                bool recursive = t.Count > 1 && t[0].Is("WITH") && t[1].Is("RECURSIVE");
                for (int i = 0; i < t.Count; i++)
                {
                    if (!t[i].Is("SELECT")) continue;
                    int d = t[i].Depth, open = -1;
                    for (int k = i - 1; k >= 0; k--) if (t[k].Depth < d) { open = k; break; }
                    int end = i + 1;
                    while (end < t.Count && t[end].Depth >= d && !(t[end].Depth == d && (t[end].Is("UNION") || t[end].Is("INTERSECT") || t[end].Is("EXCEPT") || t[end].IsPunct(";")))) end++;
                    var c = new Core { Select = i, End = end, Depth = d };
                    c.Clauses(t);
                    if (!groups.TryGetValue(open, out var g))
                    {
                        groups[open] = g = new Group { Open = open };
                        Name(t, g, recursive);
                    }
                    else g.Ops.Add(t[i - 1].Is("ALL") && i >= 2 && t[i - 2].Is("UNION") ? "UNION ALL" : t[i - 1].Text.ToUpperInvariant());
                    c.Group = g;
                    c.Arm = g.Arms.Count;
                    g.Arms.Add(c);
                    cores.Add(c);
                }
                // A compound's ORDER BY, written after its last part, sorts every part
                foreach (var g in groups.Values.Where(g => g.Arms.Count > 1 && g.Arms[^1].Ordered))
                {
                    g.Ordered = true;
                    g.Arms[^1].Ordered = false;
                }
                // A CTE read under another name (FROM x AS y): the plan may give either
                foreach (var g in groups.Values.Where(g => g.Place.StartsWith(" in its CTE ", StringComparison.Ordinal)).ToList())
                    for (int k = 1; k + 1 < t.Count; k++)
                        if (t[k].IsName && t[k].Text.Equals(g.Names[0], StringComparison.OrdinalIgnoreCase) && (t[k - 1].Is("FROM") || t[k - 1].Is("JOIN") || t[k - 1].IsPunct(",")))
                        {
                            int a = k + 1;
                            if (a < t.Count && t[a].Is("AS")) a++;
                            if (a < t.Count && t[a].IsName && !FromWords.Contains(t[a].Text)) g.Names.Add(t[a].Text);
                        }
                int id = 0;
                foreach (var c in cores.OrderBy(c => c.End)) c.Id = ++id;
                return cores;
            }

            /// <summary>How the plan and the answer name a group: a CTE, a subquery in a FROM, or one in an expression.</summary>
            static void Name(List<Tok> t, Group g, bool recursive)
            {
                int open = g.Open;
                if (open < 0) return;
                int p = open - 1;
                while (p >= 0 && (t[p].Is("MATERIALIZED") || t[p].Is("NOT"))) p--;
                if (p >= 1 && t[p].Is("AS"))
                {
                    int n = p - 1;
                    if (t[n].IsPunct(")"))
                    {
                        int depth = t[n].Depth;
                        n--;
                        while (n >= 0 && !(t[n].IsPunct("(") && t[n].Depth == depth)) n--;
                        n--;
                    }
                    if (n >= 0 && t[n].IsName)
                    {
                        g.Names.Add(t[n].Text);
                        g.Place = $" in its CTE {t[n].Text}";
                        g.Recursive = recursive;
                        return;
                    }
                }
                g.Place = " in a subquery";
                if (open >= 1 && (t[open - 1].Is("FROM") || t[open - 1].Is("JOIN") || t[open - 1].IsPunct(",")))
                {
                    int close = open + 1;
                    while (close < t.Count && !(t[close].IsPunct(")") && t[close].Depth == t[open].Depth)) close++;
                    int a = close + 1;
                    if (a < t.Count && t[a].Is("AS")) a++;
                    if (a < t.Count && t[a].IsName && t[a].Depth == t[open].Depth && !FromWords.Contains(t[a].Text))
                    {
                        g.Names.Add(t[a].Text);
                        g.Place = $" in its subquery {t[a].Text}";
                    }
                }
            }

            void Clauses(List<Tok> t)
            {
                int s = Select, e = End, d = Depth;
                Distinct = s + 1 < e && t[s + 1].Is("DISTINCT");
                From = Next(t, s + 1, e, d, "FROM");
                int resultEnd = From >= 0 ? From : NextAny(t, s + 1, e, d, Ends);
                if (From >= 0)
                {
                    FromEnd = NextAny(t, From + 1, e, d, Ends);
                    if (FromEnd < e && t[FromEnd].Is("WHERE"))
                    {
                        Where = FromEnd;
                        WhereEnd = NextAny(t, Where + 1, e, d, Ends.Where(w => w != "WHERE").ToArray());
                    }
                }
                Kept.Add((s + 1, resultEnd));
                int after = Where >= 0 ? WhereEnd : From >= 0 ? FromEnd : resultEnd;
                if (after < e) Kept.Add((after, e));
                for (int k = s + 1; k + 1 < e; k++)
                {
                    if (t[k].Depth != d) continue;
                    if (t[k].Is("GROUP") && t[k + 1].Is("BY") || t[k].Is("WINDOW") || t[k].Is("OVER")) Grouped = true;
                    if (t[k].Is("ORDER") && t[k + 1].Is("BY") && k > resultEnd) Ordered = true;
                }
                int start = Distinct ? s + 2 : s + 1;
                if (start < resultEnd && t[start].Is("ALL")) start++;
                for (int k = start; k <= resultEnd; k++)
                    if (k == resultEnd || t[k].Depth == d && t[k].IsPunct(",")) { if (k > start) Results.Add((start, k)); start = k + 1; }
            }

            static int Next(List<Tok> t, int from, int end, int d, string word)
            {
                for (int k = from; k < end; k++) if (t[k].Depth == d && t[k].Is(word)) return k;
                return -1;
            }

            static int NextAny(List<Tok> t, int from, int end, int d, string[] words)
            {
                for (int k = from; k < end; k++) if (t[k].Depth == d && words.Any(t[k].Is)) return k;
                return end;
            }
        }

        /// <summary>A view with a big column in a SELECT's FROM: the view, what the SELECT calls it, its tokens. A carried
        /// subquery or CTE (<see cref="BigView.Carried"/>) has <see cref="Text"/>: what stands in its place in a count.</summary>
        sealed record FromItem(BigView Big, string Alias, int Start, int End)
        {
            public string? Text { get; init; }
        }

        /// <summary>A big value a sort keeps: a record's whole Json (<see cref="Column"/> null) or a column of a view.</summary>
        sealed record Kept(FromItem Item, string? Column);

        static readonly HashSet<string> FromWords = new(StringComparer.OrdinalIgnoreCase)
        { "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "OUTER", "CROSS", "NATURAL", "ON", "USING", "INDEXED", "NOT", "WHERE", "GROUP", "ORDER", "HAVING", "WINDOW", "LIMIT", "UNION", "INTERSECT", "EXCEPT" };

        /// <summary>The views with a big column in the SELECT's own FROM, by the name the SELECT calls each.</summary>
        static List<FromItem> Items(List<Tok> t, Core c, Dictionary<string, BigView> big)
        {
            var items = new List<FromItem>();
            if (c.From < 0) return items;
            for (int k = c.From + 1; k < c.FromEnd; k++)
            {
                if (t[k].Depth != c.Depth || !t[k].IsName || !big.TryGetValue(t[k].Text, out var view)) continue;
                if (!(t[k - 1].Is("FROM") || t[k - 1].Is("JOIN") || t[k - 1].IsPunct(","))) continue;
                if (k + 1 < c.FromEnd && t[k + 1].IsPunct(".")) continue;
                string alias = t[k].Text;
                int a = k + 1, end = k + 1;
                if (a < c.FromEnd && t[a].Is("AS")) a++;
                if (a < c.FromEnd && t[a].IsName && t[a].Depth == c.Depth && !FromWords.Contains(t[a].Text)) { alias = t[a].Text; end = a + 1; }
                items.Add(new FromItem(view, alias, k, end));
            }
            return items;
        }

        /// <summary>The big values the SELECT's sort keeps (a SELECT that sorts by its words); <paramref name="carried"/>:
        /// the subqueries and CTEs of its FROM that hand big values up.</summary>
        static List<Kept> KeptValues(List<Tok> t, Core c, Dictionary<string, BigView> big, List<FromItem>? carried = null)
        {
            var items = Items(t, c, big);
            if (carried != null) items.AddRange(carried);
            var kept = new List<Kept>();
            if (items.Count == 0) return kept;
            // The tokens of SELECTs inside this one are theirs
            var inner = new List<(int Start, int End)>();
            for (int k = c.Select + 1; k < c.End; k++)
                if (t[k].Is("SELECT") && t[k].Depth > c.Depth)
                {
                    int e = k + 1;
                    while (e < c.End && t[e].Depth >= t[k].Depth) e++;
                    inner.Add((k, e));
                    k = e - 1;
                }
            bool Mine(int k) => !inner.Any(r => k >= r.Start && k < r.End);
            void Add(FromItem i, string? col)
            {
                if (!kept.Any(x => x.Item == i && string.Equals(x.Column, col, StringComparison.OrdinalIgnoreCase))) kept.Add(new Kept(i, col));
            }
            void Keep(int k)
            {
                // Json, x.Json, a big column, x.column, *, x.*
                bool star = t[k].IsPunct("*");
                if (!star && !t[k].IsName) return;
                // A star is the result's columns only as a whole result column (*, x.*): not a multiplication
                if (star && !(k >= 1 && (t[k - 1].IsPunct(".") || t[k - 1].IsPunct(",") || t[k - 1].Is("SELECT") || t[k - 1].Is("DISTINCT") || t[k - 1].Is("ALL"))
                    && k + 1 < t.Count && (t[k + 1].IsPunct(",") || t[k + 1].Is("FROM")))) return;
                // A name before a dot (an alias) or a parenthesis (a function) isn't a column
                if (!star && k + 1 < t.Count && (t[k + 1].IsPunct(".") || t[k + 1].IsPunct("("))) return;
                string? qual = k >= 2 && t[k - 1].IsPunct(".") && t[k - 2].IsName ? t[k - 2].Text : null;
                foreach (var it in items)
                {
                    if (qual != null && !it.Alias.Equals(qual, StringComparison.OrdinalIgnoreCase)) continue;
                    if (star)
                    {
                        if (it.Big.RecordJson) Add(it, null);
                        else foreach (var col in it.Big.Columns) Add(it, col);
                    }
                    else if (t[k].Text.Equals("Json", StringComparison.OrdinalIgnoreCase) && it.Big.RecordJson) Add(it, null);
                    else if (it.Big.Columns.FirstOrDefault(x => x.Equals(t[k].Text, StringComparison.OrdinalIgnoreCase)) is { } col) Add(it, col);
                }
            }
            if (c.Grouped)
            {
                foreach (var (s, e) in c.Kept)
                    for (int k = s; k < e; k++)
                        // count(*) keeps nothing: a star inside parentheses is an argument, not the result's columns
                        if (Mine(k) && (!t[k].IsPunct("*") || t[k].Depth == c.Depth && k > c.Select && k < c.From)) Keep(k);
            }
            else
            {
                // A sort without grouping keeps its result columns' values: only a column that is a big column itself, or a star
                foreach (var (s, e) in c.Results)
                {
                    int n = e - s;
                    if (n == 1 || n == 3 && t[s + 1].IsPunct(".")) Keep(e - 1);
                    else if (n == 3 && t[s + 1].Is("AS") || n == 2 && t[s + 1].IsName) Keep(s);
                    else if (n == 5 && t[s + 1].IsPunct(".") && t[s + 3].Is("AS") || n == 4 && t[s + 1].IsPunct(".")) Keep(s + 2);
                }
            }
            return kept;
        }

        // -------------------------------------------------------------- SQLite's plan, by SELECT

        /// <summary>One line of EXPLAIN QUERY PLAN and the lines under it.</summary>
        sealed class PlanLine
        {
            public int Id, Parent;
            public string Detail = "";
            public readonly List<PlanLine> Kids = new();
        }

        /// <summary>SQLite's plan of the statement as it will run (the views made), as a tree under a root line; null
        /// when it can't be had.</summary>
        PlanLine? PlanOf(string sql, List<Tok> toks)
        {
            try
            {
                using var cmd = _con.CreateCommand();
                cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
                BindParameters(cmd, toks);
                using var r = cmd.ExecuteReader();
                var root = new PlanLine();
                var byId = new Dictionary<int, PlanLine> { [0] = root };
                while (r.Read())
                {
                    var line = new PlanLine { Id = r.GetInt32(0), Parent = r.GetInt32(1), Detail = r.GetString(3) };
                    (byId.TryGetValue(line.Parent, out var parent) ? parent : root).Kids.Add(line);
                    byId[line.Id] = line;
                }
                return root;
            }
            catch (SqliteException e) when (e.SqliteErrorCode != raw.SQLITE_INTERRUPT) { return null; }
        }

        /// <summary>
        /// The plan's lines of each SELECT of the statement. A CO-ROUTINE or MATERIALIZE line names the CTE or subquery
        /// (or a view) whose lines are under it; "SCALAR SUBQUERY n" and "LIST SUBQUERY n" give SQLite's number of the
        /// SELECT; COMPOUND QUERY and MERGE hold a compound's parts in their order. Lines under anything the statement's
        /// SELECTs don't name (a view's own lines) belong to no SELECT.
        /// </summary>
        sealed class Regions
        {
            readonly List<Core> _cores;
            readonly Dictionary<Core, List<string>> _lines = new();
            readonly HashSet<Core> _correlated = new();

            public Regions(List<Core> cores) => _cores = cores;

            public void Walk(PlanLine root)
            {
                var top = _cores.First(c => c.Group.Open < 0).Group;
                Under(root, Of(top), false);
            }

            /// <summary>True when the SELECT runs again for rows of the statement around it: its lines are under a
            /// CORRELATED subquery's line (its own, or one it is inside).</summary>
            public bool Correlated(Core c) => _correlated.Contains(c);

            /// <summary>True when the plan has lines of the SELECT's own (a subquery SQLite folds into the SELECT around it has none).</summary>
            public bool HasLines(Core c) => _lines.ContainsKey(c);

            /// <summary>True when the plan shows a sort among the SELECT's own lines (or the temporary table of the
            /// UNION, INTERSECT or EXCEPT it is a part of).</summary>
            public bool Sorts(Core c) => _lines.TryGetValue(c, out var l) && l.Any(x => x.StartsWith("USE TEMP B-TREE", StringComparison.Ordinal))
                || c.Group.SetOp && c.Group.Arms.Any(a => _lines.TryGetValue(a, out var al) && al.Any(x => !x.StartsWith("USE ", StringComparison.Ordinal) && x.EndsWith(" USING TEMP B-TREE", StringComparison.Ordinal)));

            /// <summary>True when the SELECT sorts every row it makes (ORDER BY): a UNION SQLite merges sorts each part whole
            /// before it drops the rows they share, so its parts keep every row, not the distinct ones.</summary>
            public bool SortsEveryRow(Core c) => _lines.TryGetValue(c, out var l) && l.Any(x => x.StartsWith("USE TEMP B-TREE FOR ", StringComparison.Ordinal) && x.Contains("ORDER BY", StringComparison.Ordinal));

            /// <summary>True when the SELECT's own lines make a table of the view first (MATERIALIZE) or an automatic index on it.</summary>
            public bool Stores(Core c, string alias, string view) => _lines.TryGetValue(c, out var l) && l.Any(x =>
                x.Contains(" USING AUTOMATIC ", StringComparison.Ordinal)
                    && (x.StartsWith($"SEARCH {alias} ", StringComparison.OrdinalIgnoreCase) || x.StartsWith($"SCAN {alias} ", StringComparison.OrdinalIgnoreCase))
                || x.Equals("MATERIALIZE " + alias, StringComparison.OrdinalIgnoreCase) || x.Equals("MATERIALIZE " + view, StringComparison.OrdinalIgnoreCase));

            void Add(object? ctx, string line)
            {
                if (ctx is not Core c) return;
                if (!_lines.TryGetValue(c, out var l)) _lines[c] = l = new();
                l.Add(line);
            }

            Group? Named(string name) =>
                _cores.Select(c => c.Group).FirstOrDefault(g => g.Names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                ?? (name.StartsWith("(subquery-", StringComparison.Ordinal) && name.EndsWith(')') && int.TryParse(name[10..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? Numbered(n) : null);

            Group? Numbered(int n) => _cores.FirstOrDefault(c => c.Id == n)?.Group;

            static object Of(Group g) => g.Arms.Count == 1 ? g.Arms[0] : g;

            void Under(PlanLine node, object? ctx, bool correlated)
            {
                if (correlated && ctx is Core cc) _correlated.Add(cc);
                else if (correlated && ctx is Group cg) _correlated.UnionWith(cg.Arms);
                foreach (var kid in node.Kids)
                {
                    string d = kid.Detail;
                    if (d.StartsWith("CO-ROUTINE ", StringComparison.Ordinal) || d.StartsWith("MATERIALIZE ", StringComparison.Ordinal))
                    {
                        // A CTE or subquery of the statement; else a view's own lines, kept apart (a view made first stays a
                        // line of the SELECT that reads it)
                        if (Named(d[(d.IndexOf(' ') + 1)..]) is { } g) { Under(kid, Of(g), correlated); continue; }
                        Add(ctx, d);
                        Under(kid, null, correlated);
                        continue;
                    }
                    int sub = d.IndexOf("SUBQUERY ", StringComparison.Ordinal);
                    if (sub >= 0 && (d.StartsWith("SCALAR", StringComparison.Ordinal) || d.StartsWith("LIST", StringComparison.Ordinal) || d.StartsWith("CORRELATED", StringComparison.Ordinal))
                        && int.TryParse(d[(sub + 9)..], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                    {
                        Under(kid, Numbered(n) is { } g ? Of(g) : null, correlated || d.StartsWith("CORRELATED", StringComparison.Ordinal));
                        continue;
                    }
                    if (d == "COMPOUND QUERY" || d.StartsWith("MERGE (", StringComparison.Ordinal))
                    {
                        var arms = Arms(kid);
                        if (ctx is Group g && arms.Count == g.Arms.Count)
                            for (int i = 0; i < arms.Count; i++) { Add(g.Arms[i], arms[i].Detail); Under(arms[i], g.Arms[i], correlated); }
                        else
                            // A view's parts spread over the SELECT that reads it: their lines are still that SELECT's
                            foreach (var a in arms) Under(a, ctx as Core, correlated);
                        continue;
                    }
                    Add(ctx, d);
                    Under(kid, ctx, correlated);
                }
            }

            /// <summary>A compound's parts in their order: COMPOUND QUERY's lines, or MERGE's LEFT (itself a MERGE for
            /// more than two parts) and RIGHT.</summary>
            static List<PlanLine> Arms(PlanLine compound)
            {
                if (!compound.Detail.StartsWith("MERGE (", StringComparison.Ordinal)) return compound.Kids.ToList();
                var arms = new List<PlanLine>();
                foreach (var k in compound.Kids)
                    if (k.Detail == "LEFT" && k.Kids.Count == 1 && k.Kids[0].Detail.StartsWith("MERGE (", StringComparison.Ordinal)) arms.AddRange(Arms(k.Kids[0]));
                    else arms.Add(k);
                return arms;
            }
        }

        // -------------------------------------------------------------- The count

        /// <summary>The temporary table of a record view's records' Json sizes (made once per statement).</summary>
        string SizeTable(string view, Dictionary<string, string> made)
        {
            if (made.TryGetValue(view, out var name)) return name;
            name = $"sfs_jsonsize{made.Count + 1}";
            ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{name}; CREATE TEMP TABLE {name}(k TEXT COLLATE NOCASE, p TEXT COLLATE NOCASE, n INT, PRIMARY KEY (k, p)) WITHOUT ROWID; "
                + $"INSERT OR REPLACE INTO temp.{name} {string.Join(" UNION ALL ", _jsonSources[view])}");
            made[view] = name;
            return name;
        }

        /// <summary>
        /// The rows a SELECT's sort takes and the big values they keep (bytes): its FROM and WHERE counted, each row
        /// weighted by the size of each kept record's stored Json and the length of each other kept column (a carried
        /// column's size as its subquery gives it); for a sort that keeps only distinct rows, its distinct rows. With
        /// <paramref name="outer"/> (a correlated SELECT, GAPS S142): the rows it reads for all the rows of the SELECT
        /// around it, that SELECT's FROM and WHERE joined to its own. Null when the count can't be made.
        /// </summary>
        (long Rows, long Bytes)? Weigh(string sql, List<Tok> toks, string prefix, Core core, List<Kept> kept, Probed probed, Dictionary<string, string> made,
            bool distinct = true, bool dropJsonTerms = false, List<FromItem>? carried = null, (Core Core, List<FromItem> Records)? outer = null)
        {
            try
            {
                var records = kept.Where(k => k.Column == null).Select(k => k.Item).Distinct().ToList();
                var columns = kept.Where(k => k.Column != null).ToList();
                carried ??= new List<FromItem>();
                // One record view alone, with no WHERE, keeping its Json only: its rows are its records
                if (outer == null && columns.Count == 0 && records.Count == 1 && records[0].Start == core.From + 1 && records[0].End == core.FromEnd && core.Where < 0)
                {
                    using var one = _con.CreateCommand();
                    one.CommandText = $"SELECT count(*), total(n) FROM temp.{SizeTable(records[0].Big.Name, made)}";
                    using var r1 = one.ExecuteReader();
                    r1.Read();
                    return (r1.GetInt64(0), (long)r1.GetDouble(1));
                }
                var z = records.ToDictionary(i => i, i => $"sfs_z{records.IndexOf(i) + 1}");
                var weights = records.Select(i => $"coalesce({z[i]}.n, 0)")
                    .Concat(columns.Select(k => k.Item.Big.Carried ? $"coalesce({(k.Item.Alias.Length > 0 ? SqlText.Name(k.Item.Alias) + "." : "")}{SqlText.Name(k.Column!)}, 0)"
                        : $"coalesce(length({SqlText.Name(k.Item.Alias)}.{SqlText.Name(k.Column!)}), 0)")).ToList();
                string weight = weights.Count == 0 ? "0" : string.Join(" + ", weights);
                var body = new System.Text.StringBuilder(" FROM ");
                if (outer is { } o) body.Append(FromText(sql, toks, o.Core, o.Records, new List<FromItem>(), probed, made)).Append(", ");
                body.Append(FromText(sql, toks, core, records, carried, probed, made));
                foreach (var item in records)
                {
                    string a = SqlText.Name(item.Alias);
                    body.Append($" LEFT JOIN temp.{SizeTable(item.Big.Name, made)} {z[item]} ON {z[item]}.k = {a}.FormKey AND {z[item]}.p = {a}.Plugin");
                }
                var where = new List<string>();
                if (outer is { } o2 && o2.Core.Where >= 0 && WhereWithoutJson(sql, toks, o2.Core, o2.Records, new List<FromItem>(), true) is { Length: > 0 } ow) where.Add(ow);
                if (core.Where >= 0 && WhereWithoutJson(sql, toks, core, records, carried, dropJsonTerms) is { Length: > 0 } cw) where.Add(cw);
                if (where.Count > 0) body.Append(" WHERE ").Append(string.Join(" AND ", where));
                string count = outer == null && distinct && core.DistinctOnly && DistinctKey(sql, toks, core, records) is { } key
                    // Each distinct row once: the result's values, a kept record's Json by its record
                    ? $"{prefix}SELECT count(*), total(sfs_w) FROM (SELECT DISTINCT {key}, {weight} AS sfs_w{body})"
                    : $"{prefix}SELECT count(*), total({weight}){body}";
                using var cmd = _con.CreateCommand();
                cmd.CommandText = count;
                cmd.CommandTimeout = 0;
                BindParameters(cmd, SqlText.Tokenize(count));
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                return (r.GetInt64(0), (long)r.GetDouble(1));
            }
            catch (SqliteException e) when (e.SqliteErrorCode != raw.SQLITE_INTERRUPT) { return null; }
        }

        /// <summary>
        /// A SELECT's FROM as written (without the word FROM) for a count: each record view of <paramref name="records"/>
        /// given as a SELECT of the columns the statement reads but Json, in two parts (SQLite then works the view out
        /// once per record instead of making each record's Json again for every row of the join, which is what a count
        /// avoids), and each carried subquery or CTE by its text with the sizes in place of its big values.
        /// </summary>
        string FromText(string sql, List<Tok> toks, Core core, List<FromItem> records, List<FromItem> carried, Probed probed, Dictionary<string, string> made)
        {
            var body = new System.Text.StringBuilder();
            int pos = toks[core.From].End, fromEnd = core.FromEnd < toks.Count ? toks[core.FromEnd].Start : sql.Length;
            foreach (var item in records.Concat(carried).OrderBy(c => c.Start))
            {
                body.Append(sql, pos, toks[item.Start].Start - pos).Append(' ');
                if (item.Text != null) body.Append(Sized(item.Text, made));
                else
                {
                    string cols = string.Join(", ", Used(probed, item.Big.Name).Where(c => !c.Equals("Json", StringComparison.OrdinalIgnoreCase)).Concat(new[] { "FormKey", "Plugin" })
                        .Distinct(StringComparer.OrdinalIgnoreCase).Select(SqlText.Name));
                    string name = SqlText.Name(item.Big.Name);
                    body.Append($"(SELECT {cols} FROM {name} UNION ALL SELECT {cols} FROM {name} WHERE 0) AS {SqlText.Name(item.Alias)}");
                }
                pos = toks[item.End - 1].End;
            }
            body.Append(sql, pos, fromEnd - pos);
            return body.ToString();
        }

        /// <summary>The columns the statement reads of a view (Probe's count).</summary>
        static HashSet<string> Used(Probed probed, string view) =>
            probed.Views.FirstOrDefault(kv => kv.Key.Name.Equals(view, StringComparison.OrdinalIgnoreCase)).Value
            ?? (probed.SpecialColumns.TryGetValue(view, out var sc) ? sc : new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        /// <summary>Where a carried text names a record view's size table (<see cref="SizeMark"/>), that table, made now.</summary>
        string Sized(string text, Dictionary<string, string> made) =>
            System.Text.RegularExpressions.Regex.Replace(text, "\u0001([^\u0001]+)\u0001", m => "temp." + SizeTable(m.Groups[1].Value, made));

        /// <summary>The mark a carried text puts where a record view's size table goes (filled in by <see cref="Sized"/>).</summary>
        static string SizeMark(string view) => "\u0001" + view + "\u0001";

        // -------------------------------------------------------------- Big values handed up (GAPS S143)

        /// <summary>
        /// The subqueries and CTEs in a SELECT's FROM that hand a big value up to it: a record's whole Json, or a column
        /// that holds JSON, given as a result column (by name, or in a star) of a SELECT they read, at any depth. Each comes
        /// with its text for a count, in which those columns give the size of their value instead (Weigh); the SELECT's
        /// sort then counts what it keeps of them as it counts its own views' values.
        /// </summary>
        static List<FromItem> CarriedItems(string sql, List<Tok> t, List<Core> cores, Core c, Dictionary<string, BigView> big, Probed probed, int depth)
        {
            var list = new List<FromItem>();
            if (c.From < 0 || depth > 4) return list;
            for (int k = c.From + 1; k < c.FromEnd; k++)
            {
                if (t[k].Depth != c.Depth || !(t[k - 1].Is("FROM") || t[k - 1].Is("JOIN") || t[k - 1].IsPunct(","))) continue;
                Group? g;
                int end;
                if (t[k].IsPunct("("))
                {
                    g = cores.Select(x => x.Group).FirstOrDefault(x => x.Open == k);
                    end = k + 1;
                    while (end < t.Count && !(t[end].IsPunct(")") && t[end].Depth == t[k].Depth)) end++;
                    end++;
                }
                else if (t[k].IsName && !(k + 1 < c.FromEnd && t[k + 1].IsPunct(".")))
                {
                    // A CTE of the statement read by name
                    string name = t[k].Text;
                    g = cores.Select(x => x.Group).FirstOrDefault(x => x.Place == $" in its CTE {name}");
                    end = k + 1;
                }
                else continue;
                if (g == null) continue;
                string alias = t[k].IsName ? t[k].Text : "";
                int a = end;
                if (a < c.FromEnd && t[a].Is("AS")) a++;
                if (a < c.FromEnd && t[a].IsName && t[a].Depth == c.Depth && !FromWords.Contains(t[a].Text)) { alias = t[a].Text; end = a + 1; }
                if (Carry(sql, t, cores, g, big, probed, depth) is not var (text, cols, origin)) continue;
                // A subquery without a name: its columns are read by their names alone
                list.Add(new FromItem(new BigView(alias.Length > 0 ? alias : "a subquery", false, cols) { Carried = true, Origin = origin }, alias, k, end)
                    { Text = alias.Length > 0 ? $"({text}) AS {SqlText.Name(alias)}" : $"({text})" });
            }
            return list;
        }

        /// <summary>
        /// A subquery's or CTE's SELECT given for a count: each of its FROM's views with a big column, and each subquery or
        /// CTE there that hands one up, replaced by a SELECT that gives the size of the value under the same name; its
        /// WHERE without the terms that name such a value (the count then keeps more rows than the statement: an upper
        /// bound). Its result columns that are such a value as a whole (by name or in a star) are what it hands up. Null
        /// when it hands up none, or is a part of a recursive CTE.
        /// </summary>
        static (string Text, HashSet<string> Columns, string Origin)? Carry(string sql, List<Tok> t, List<Core> cores, Group g, Dictionary<string, BigView> big, Probed probed, int depth)
        {
            if (g.Recursive || g.Arms.Count == 0 || g.Open < 0) return null;
            int close = g.Open + 1;
            while (close < t.Count && !(t[close].IsPunct(")") && t[close].Depth == t[g.Open].Depth)) close++;
            if (close >= t.Count) return null;
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var origins = new List<string>();
            var edits = new List<(int Start, int End, string Text)>();
            foreach (var arm in g.Arms)
            {
                if (arm.From < 0) continue;
                var items = Items(t, arm, big);
                var carried = CarriedItems(sql, t, cores, arm, big, probed, depth + 1);
                var all = items.Concat(carried).ToList();
                if (all.Count == 0) continue;
                foreach (var i in items) edits.Add((t[i.Start].Start, t[i.End - 1].End, SizedView(i, probed)));
                foreach (var i in carried) edits.Add((t[i.Start].Start, t[i.End - 1].End, i.Text!));
                if (arm.Where >= 0)
                {
                    int ws = t[arm.Where].Start, we = arm.WhereEnd < t.Count ? t[arm.WhereEnd].Start : sql.Length;
                    string w = WhereWithoutJson(sql, t, arm, items.Where(i => i.Big.RecordJson).ToList(), carried.Concat(items.Where(i => i.Big.Columns.Count > 0)).ToList(), true);
                    edits.Add((ws, we, w.Length > 0 ? "WHERE " + w + " " : ""));
                }
                // What it hands up: its result columns that are a big value as a whole (the first part's names are the
                // compound's); a big value inside an expression is that expression's, not handed up
                foreach (var (s, e) in arm.Results)
                {
                    int n = e - s;
                    if (n == 1 && t[s].IsPunct("*") || n == 3 && t[s + 1].IsPunct(".") && t[s + 2].IsPunct("*"))
                    {
                        string? q = n == 3 ? t[s].Text : null;
                        foreach (var i in all.Where(i => q == null || i.Alias.Equals(q, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (i.Big.RecordJson) { cols.Add("Json"); origins.Add($"the whole Json of {i.Big.Name}"); }
                            foreach (var c in i.Big.Columns) { cols.Add(c); origins.Add(i.Big.Carried ? i.Big.Origin! : $"{i.Big.Name}.{c}"); }
                        }
                        continue;
                    }
                    // Json, x.Json, a big column, x.column; then AS name, or a name
                    int last = n >= 3 && t[s + 1].IsPunct(".") ? s + 2 : s;
                    if (!t[last].IsName) continue;
                    string? named = last + 1 == e ? null : last + 2 == e && t[last + 1].IsName ? t[last + 1].Text : last + 3 == e && t[last + 1].Is("AS") && t[last + 2].IsName ? t[last + 2].Text : "\u0001";
                    if (named == "\u0001") continue;
                    string? qual = last == s + 2 ? t[s].Text : null;
                    string col = t[last].Text;
                    foreach (var i in all.Where(i => qual == null || i.Alias.Equals(qual, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (i.Big.RecordJson && col.Equals("Json", StringComparison.OrdinalIgnoreCase)) { cols.Add(named ?? "Json"); origins.Add($"the whole Json of {i.Big.Name}"); break; }
                        if (i.Big.Columns.FirstOrDefault(x => x.Equals(col, StringComparison.OrdinalIgnoreCase)) is { } bc)
                        {
                            cols.Add(named ?? bc);
                            origins.Add(i.Big.Carried ? i.Big.Origin! : $"{i.Big.Name}.{bc}");
                            break;
                        }
                    }
                }
            }
            if (cols.Count == 0) return null;
            var text = new System.Text.StringBuilder();
            int pos = t[g.Open].End;
            foreach (var (start, end, txt) in edits.OrderBy(e => e.Start))
            {
                text.Append(sql, pos, start - pos).Append(txt);
                pos = end;
            }
            text.Append(sql, pos, t[close].Start - pos);
            return (text.ToString(), cols, string.Join(" and ", origins.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        /// <summary>A view with a big column given for a count, under the name the statement calls it: the columns the
        /// statement reads, a record's Json as its stored size (from its size table, <see cref="SizeMark"/>) and a column
        /// that holds JSON as its length.</summary>
        static string SizedView(FromItem i, Probed probed)
        {
            string name = SqlText.Name(i.Big.Name), alias = SqlText.Name(i.Alias);
            var used = Used(probed, i.Big.Name).Concat(new[] { "FormKey", "Plugin" }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!i.Big.RecordJson)
                return $"(SELECT {string.Join(", ", used.Select(c => i.Big.Columns.Contains(c) ? $"length({SqlText.Name(c)}) AS {SqlText.Name(c)}" : SqlText.Name(c)))} FROM {name}) AS {alias}";
            var inner = used.Where(c => !c.Equals("Json", StringComparison.OrdinalIgnoreCase)).Select(SqlText.Name).ToList();
            string cols = string.Join(", ", used.Select(c => c.Equals("Json", StringComparison.OrdinalIgnoreCase) ? "coalesce(sfs_z.n, 0) AS Json"
                : i.Big.Columns.Contains(c) ? $"length(sfs_v.{SqlText.Name(c)}) AS {SqlText.Name(c)}" : $"sfs_v.{SqlText.Name(c)}"));
            return $"(SELECT {cols} FROM (SELECT {string.Join(", ", inner)} FROM {name} UNION ALL SELECT {string.Join(", ", inner)} FROM {name} WHERE 0) AS sfs_v "
                + $"LEFT JOIN {SizeMark(i.Big.Name)} sfs_z ON sfs_z.k = sfs_v.FormKey AND sfs_z.p = sfs_v.Plugin) AS {alias}";
        }

        /// <summary>The SELECT's WHERE (without the word WHERE) without its terms (split at its top-level ANDs) that name a
        /// counted record's Json (when <paramref name="dropJson"/>: the count can't read that Json without making it, so it
        /// counts more rows than the statement keeps, an upper bound) or a big column a count gives as its size
        /// (<paramref name="sized"/>: carried subqueries, views with JSON columns). "" when nothing is left.</summary>
        static string WhereWithoutJson(string sql, List<Tok> t, Core core, List<FromItem> records, List<FromItem> sized, bool dropJson)
        {
            var terms = new List<(int Start, int End)>();
            int start = core.Where + 1;
            // A top-level OR binds looser than AND: then the WHERE is one term
            bool or = Enumerable.Range(start, core.WhereEnd - start).Any(k => t[k].Depth == core.Depth && t[k].Is("OR"));
            for (int k = start; k <= core.WhereEnd; k++)
                if (k == core.WhereEnd || !or && t[k].Depth == core.Depth && t[k].Is("AND") && !BetweenAnd(t, start, k, core.Depth))
                {
                    if (k > start) terms.Add((start, k));
                    start = k + 1;
                }
            bool Names((int Start, int End) r)
            {
                for (int k = r.Start; k < r.End; k++)
                {
                    if (!t[k].IsName || k + 1 < r.End && (t[k + 1].IsPunct(".") || t[k + 1].IsPunct("("))) continue;
                    string? qual = k >= 2 && t[k - 1].IsPunct(".") && t[k - 2].IsName ? t[k - 2].Text : null;
                    bool json = t[k].Text.Equals("Json", StringComparison.OrdinalIgnoreCase);
                    if (dropJson && json && (qual == null || records.Any(i => i.Alias.Equals(qual, StringComparison.OrdinalIgnoreCase)))) return true;
                    if (sized.Any(i => (qual == null || i.Alias.Equals(qual, StringComparison.OrdinalIgnoreCase)) && i.Big.Columns.Contains(t[k].Text))) return true;
                }
                return false;
            }
            return string.Join(" AND ", terms.Where(r => !Names(r)).Select(r => "(" + sql[t[r.Start].Start..t[r.End - 1].End] + ")"));
        }

        /// <summary>True when the AND at <paramref name="k"/> is a BETWEEN's (x BETWEEN a AND b), not a term's end.</summary>
        static bool BetweenAnd(List<Tok> t, int from, int k, int depth)
        {
            for (int i = k - 1; i >= from; i--)
            {
                if (t[i].Depth != depth) continue;
                if (t[i].Is("AND")) return false;
                if (t[i].Is("BETWEEN")) return true;
            }
            return false;
        }

        /// <summary>The result columns of a SELECT that keeps distinct rows, each kept record's Json named by its record
        /// (FormKey and plugin); null when the result has a star.</summary>
        static string? DistinctKey(string sql, List<Tok> t, Core core, List<FromItem> records)
        {
            var parts = new List<string>();
            foreach (var (s, e) in core.Results)
            {
                // The column's expression, without its name after AS
                int last = e;
                for (int k = s; k < e; k++) if (t[k].Is("AS") && t[k].Depth == core.Depth) { last = k; break; }
                var sb = new System.Text.StringBuilder();
                int pos = t[s].Start;
                for (int k = s; k < last; k++)
                {
                    if (t[k].IsPunct("*")) return null;
                    if (!(t[k].IsName && t[k].Text.Equals("Json", StringComparison.OrdinalIgnoreCase)) || k + 1 < last && t[k + 1].IsPunct("(")) continue;
                    string? qual = k >= 2 && t[k - 1].IsPunct(".") && t[k - 2].IsName ? t[k - 2].Text : null;
                    var item = qual != null ? records.FirstOrDefault(i => i.Alias.Equals(qual, StringComparison.OrdinalIgnoreCase)) : records.Count == 1 ? records[0] : null;
                    if (item == null) continue;
                    int from = qual != null ? t[k - 2].Start : t[k].Start;
                    string a = SqlText.Name(item.Alias);
                    sb.Append(sql, pos, from - pos).Append($"({a}.FormKey || '|' || {a}.Plugin)");
                    pos = t[k].End;
                }
                int end = t[last - 1].End;
                if (end > pos) sb.Append(sql, pos, end - pos);
                parts.Add(sb.ToString());
            }
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }
}
