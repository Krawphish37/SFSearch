// A record view's Json in a join (GAPS S126, J99; S142, J109). Folded into a join, a typed view (or Records) over records
// read whole makes the record's JSON text from its stored JSONB again for each row of the join that reads it: the cell
// NAViewport's 6.4 MB of JSON for each of its 29,070 traversals. CreateViews gives such a view in two parts (the second
// without rows and without a FROM, so SQLite never folds it into the statement), so that its rows are worked out once per
// record; so it does for a statement that groups rows (J82, GAPS S104). Once the views are made, two steps settle the
// statement that runs.
//
// 1. What the statement works out of a record's Json, worked out once per record (S142, PerRecordValues). Where every use
//    of a view's Json (and of its columns that hold JSON) in a SELECT is an expression of that record alone (c.Json ->>
//    '$.EditorID', length(c.Json), json_array_length(c.Json, '$.Traversals'), c.Json LIKE '%x%'), the view in that
//    SELECT's FROM is given as a subquery of two parts that works those expressions out once per record, under names of
//    their own (sfs_e1, …), with the view's other columns the statement reads; each use reads its name instead. Its
//    values are small, so it doesn't matter where SQLite puts it: read first, kept in an automatic index (GAPS S141 only
//    hurts a big value kept there), on the inner side of a LEFT JOIN, in a FROM with subqueries or functions. In a
//    correlated subquery (read again for each row around it) the values go into a temporary table instead, made once and
//    indexed on the view's columns the subquery matches with = (PerRecordTable): as a subquery SQLite may run it again for
//    each row, and in an aggregate's argument it plans the subquery as run once, so it gives it no automatic index (a
//    co-routine over 900 s, a MATERIALIZED CTE scanned for each of 1,039,117 traversals 150 s). The same rows, because:
//      - only expressions that give the same value wherever they are worked out: each of its column references is to that
//        record (c.x), the rest are literals, parameters, operators, CASE … END, IS [NOT] NULL, AND/OR and functions that
//        give one value for one input (length, octet_length, json_extract, jsonb_extract, json_type, json_array_length,
//        instr, substr, substring, lower, upper, trim, ltrim, rtrim, replace, abs, unicode, like, glob, round, sign, min
//        and max of two values or more; and, giving a value on NULL, coalesce, ifnull, iif, nullif, hex, quote, typeof,
//        printf, format, char, concat, concat_ws, json_valid, json_error_position, display: GAPS S144); ->, ->> with a
//        constant path; LIKE, GLOB with a literal pattern. Not functions that do more than give a value (name(), text(),
//        edid() note what they read for the answer) or give another value each time (random());
//      - a row an outer join makes without a record (the view on the right of a LEFT JOIN, either side of a FULL JOIN,
//        before a RIGHT JOIN) has NULL in each such column. An expression that gives NULL on NULL gives the same there.
//        One that may give a value on NULL (coalesce(c.Json, '{}'), c.Json IS NULL, CASE) reads the subquery's marker
//        (1 AS sfs_r): CASE WHEN c.sfs_r IS NULL THEN <the expression with NULL for each of the record's columns> ELSE
//        c.sfs_e1 END, which is what it gave on that row (S144);
//      - an expression is taken whole: a function call or a CASE around the use, the path operators after it, the LIKE
//        or IS NULL after it, only where SQLite's operator precedence makes it one operand (nothing tighter before or
//        after it);
//      - the view's own columns keep their names, types and collations (the two-part form: notes/fix-j99-json-joins.md),
//        and a WHERE on them is still pushed into the subquery.
//    It isn't done (the view stays as below) where a use is the whole Json (SELECT c.Json, GROUP BY c.Json, a star, an
//    aggregate of it, a table function's argument): that value is the answer's own, on each row; where a use names
//    another table or anything else; where a NATURAL join or INDEXED BY would read the view's columns by their names;
//    and where the view is on the inner side of a join or read for each row of a correlated subquery while the join
//    reads its records' JSON at most twice the size of the records the SELECT works out once: those its own conditions
//    keep (the WHERE's and its ON's terms on the view's own columns, which SQLite pushes into the subquery, and with
//    which a correlated subquery's table is filled: GAPS S145, OwnTerms). Then folding it makes no more JSON.
//    (A positional parameter never gets this far: the request gives parameters by name, so the statement is answered
//    with a plain line first, GAPS S161; the rewrite still numbers a ? it moves, as SQLite numbers it.)
//    A subquery in a FROM that hands a record's whole Json up (SELECT c.FormKey, c.Json FROM Cell c) to a SELECT that
//    uses it only in such expressions (length(x.Json)) is done the same way: the expressions go down into it, worked out
//    once per record of the view there.
//
// 2. Where a view of two parts still stands (J99). SQLite reads it either first (a CO-ROUTINE scanned once: each record's
//    JSON made once, and the rows of the join that need it read it from there), or on the inner side of the join, kept
//    in an automatic index (or a MATERIALIZE). Such a temporary index is a b-tree of its own whose page cache is SQLite's
//    built-in default of 2 MB whatever cache_size says (btree.c sets it when a b-tree is opened), and with temp_store =
//    FILE (Session.cs SpillPragmas, GAPS S104) each row of the join then reads its record's JSON back from the temporary
//    file page by page: aseliving.esm's cells joined to their 29,141 traversals with c.Json ->> '$.EditorID' took 417 s
//    that way, against 123 s folded and 14 s with the cells read first. So SQLite's plan settles it:
//      - read first already, or not kept in a temporary index: the two parts stay;
//      - kept on the inner side (in its SELECT's join, or in the join a subquery of its FROM is folded into), and the
//        join reads the records' JSON more than twice over (its rows counted, each weighed by its record's stored size,
//        against the records' sizes once; a WHERE term on the view's Json left out of the count, which then counts more
//        rows than the join keeps): the statement's FROM is given with the view first (CROSS JOIN, which SQLite never
//        reorders), when the FROM is plain inner joins of names (no LEFT, RIGHT, FULL, NATURAL, USING, subquery or
//        function in it); the join's ON conditions go on its last table, which for inner joins is the same statement;
//      - else (each record read about once, a FROM that can't be given so, a correlated subquery): the view is folded
//        again, as before J99.
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    sealed partial class Session
    {
        /// <summary>The views CreateViews gave in two parts because a join names their Json: each one's folded form (one part).</summary>
        readonly Dictionary<string, string> _jsonJoinViews = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The views CreateViews gave in two parts because the statement groups rows (J82, GAPS S104): each one's
        /// folded form.</summary>
        readonly Dictionary<string, string> _groupedViews = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The temporary tables of per-record values made for the statement before (step 1, a correlated subquery).</summary>
        readonly List<string> _perRecordTables = new();

        /// <summary>
        /// Settles the statement that runs (see the top of this file): what it works out of a record's Json worked out
        /// once per record, and where each view of <see cref="_jsonJoinViews"/> still standing goes. Returns the statement
        /// as written, or as changed (<paramref name="toks"/> then its tokens).
        /// </summary>
        string PlaceJsonViews(string sql, ref List<Tok> toks, Probed probed, bool explain = false)
        {
            foreach (string table in _perRecordTables) ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{table}");
            _perRecordTables.Clear();
            if (_jsonJoinViews.Count == 0 && _groupedViews.Count == 0 || toks.Count == 0) return sql;
            if (toks[0].Is("EXPLAIN"))
            {
                // EXPLAIN shows the statement as it will run
                int i = toks.Count > 2 && toks[1].Is("QUERY") && toks[2].Is("PLAN") ? 3 : 1;
                if (i >= toks.Count) return sql;
                string head = sql[..toks[i].Start], body = sql[toks[i].Start..];
                var inner = SqlText.Tokenize(body);
                string placed = PlaceJsonViews(body, ref inner, probed, explain: true);
                if (ReferenceEquals(placed, body)) return sql;
                toks = SqlText.Tokenize(head + placed);
                return head + placed;
            }
            KeepJsonMarks(toks, probed);
            sql = PerRecordValues(sql, ref toks, probed, explain);
            if (_jsonJoinViews.Count == 0) return sql;
            var cores = Core.All(toks);
            var first = cores.FirstOrDefault(c => c.Group.Open < 0);
            var plan = first == null ? null : PlanOf(sql, toks);
            if (first == null || plan == null) { FoldJsonViews(_jsonJoinViews.Keys.ToList()); return sql; }
            var regions = new Regions(cores);
            regions.Walk(plan);
            var big = BigViews(probed);
            var fold = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var edits = new List<(int Start, int End, string Text)>();
            var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var c in cores)
                    foreach (var item in Items(toks, c, big).Where(i => _jsonJoinViews.ContainsKey(i.Big.Name)))
                    {
                        if (!StoredHere(regions, cores, c, item)) continue;
                        // On the inner side: how much JSON the join reads of it, against its records' JSON once
                        long once = item.Big.RecordJson ? OnceFor(sql, toks, c, item, probed, made).Bytes : 0;
                        var weighed = item.Big.RecordJson ? Weigh(sql, toks, Prefix(sql, toks, first), c, new List<Kept> { new(item, null) }, probed, made, distinct: false, dropJsonTerms: true) : null;
                        if (weighed is var (_, bytes) && bytes > 2 * once && FirstInJoin(sql, toks, c, item) is { } edit
                            && !edits.Any(e => e.Start < edit.End && edit.Start < e.End))
                            edits.Add(edit);
                        else fold.Add(item.Big.Name);
                    }
            }
            catch (SqliteException e) when (e.SqliteErrorCode != raw.SQLITE_INTERRUPT) { fold.UnionWith(_jsonJoinViews.Keys); edits.Clear(); }
            finally
            {
                foreach (var name in made.Values) ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{name}");
            }
            FoldJsonViews(fold.ToList());
            if (edits.Count == 0) return sql;
            foreach (var (start, end, text) in edits.OrderByDescending(e => e.Start)) sql = sql[..start] + text + sql[end..];
            var now = toks = SqlText.Tokenize(sql);
            // A view the plan still keeps on the inner side (another SELECT of the statement reads it there): folded
            cores = Core.All(now);
            plan = PlanOf(sql, now);
            if (plan == null) return sql;
            regions = new Regions(cores);
            regions.Walk(plan);
            FoldJsonViews(cores.SelectMany(c => Items(now, c, big).Where(i => _jsonJoinViews.ContainsKey(i.Big.Name) && StoredHere(regions, cores, c, i)).Select(i => i.Big.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            return sql;
        }

        /// <summary>True when SQLite keeps the view in a temporary index or table: in the SELECT's own plan lines, or, when
        /// the SELECT is a subquery of a FROM that SQLite folds into the SELECT around it (it has no lines of its own),
        /// in that SELECT's.</summary>
        static bool StoredHere(Regions regions, List<Core> cores, Core c, FromItem item)
        {
            for (var at = c; at != null; at = Folded(regions, cores, at))
                if (regions.Stores(at, item.Alias, item.Big.Name)) return true;
            return false;
        }

        /// <summary>The SELECT a subquery of a FROM is folded into (it has no plan lines of its own); null otherwise.</summary>
        static Core? Folded(Regions regions, List<Core> cores, Core c) =>
            c.Group.Arms.Count == 1 && c.Group.Place.StartsWith(" in its subquery ", StringComparison.Ordinal) && !regions.HasLines(c) ? Around(cores, c) : null;

        /// <summary>The SELECT whose text holds the parenthesis of <paramref name="c"/>'s group (the innermost); null for
        /// the statement's own.</summary>
        static Core? Around(List<Core> cores, Core c) =>
            c.Group.Open < 0 ? null : cores.Where(o => o.Select < c.Group.Open && o.End > c.Group.Open).OrderByDescending(o => o.Select).FirstOrDefault();

        /// <summary>The views given back in one part: SQLite folds each into the statement, as before J99.</summary>
        void FoldJsonViews(List<string> views)
        {
            foreach (string v in views)
            {
                if (!_jsonJoinViews.Remove(v, out var one) && !_groupedViews.Remove(v, out one)) continue;
                ScratchFile.Exec(_con, $"DROP VIEW IF EXISTS temp.{SqlText.Name(v)}; CREATE TEMP VIEW {SqlText.Name(v)} AS {one}");
            }
        }

        /// <summary>The functions that read SQLite's JSON mark on a value they are given: with it the value goes in as JSON,
        /// without it as a string (json_object('j', Json) gives {"j":{…}} or {"j":"{…}"}).</summary>
        static readonly HashSet<string> MarkReaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "json_array", "json_object", "json_insert", "json_replace", "json_set", "json_group_array", "json_group_object", "json_quote", "json_array_insert",
            "jsonb_array", "jsonb_object", "jsonb_insert", "jsonb_replace", "jsonb_set", "jsonb_group_array", "jsonb_group_object", "jsonb_array_insert",
        };

        /// <summary>
        /// GAPS S154: a view's JSON columns (Json, a JSON or flags column) carry SQLite's JSON mark only while they are
        /// worked out in the expression that reads them: SQLite drops a value's mark when it leaves a subquery (a view of
        /// two parts is one: its co-routine's or its table's values come out as plain text). So a view whose JSON column
        /// (or what is made of it: c.Json -> '$.X', a name a subquery gives it) is given to a function that reads the mark
        /// is folded into the statement, as before J99: the value is then made where the function reads it.
        /// </summary>
        void KeepJsonMarks(List<Tok> t, Probed probed)
        {
            var views = probed.Views.Where(kv => _jsonJoinViews.ContainsKey(kv.Key.Name) || _groupedViews.ContainsKey(kv.Key.Name)).ToList();
            var special = probed.Special.Where(_jsonJoinViews.ContainsKey).ToList();
            if (views.Count == 0 && special.Count == 0) return;
            // The names that carry the mark: each such view's JSON columns the statement reads, then the names the
            // statement's result columns give to what is made of them (a subquery's or CTE's column), until none is new
            var marked = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (view, used) in views)
                marked[view.Name] = new HashSet<string>(view.Columns.Where(c => ViewBuilder.IsJson(c) && used.Contains(c.Name)).Select(c => c.Name)
                    .Concat(used.Contains("Json") ? new[] { "Json" } : Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
            foreach (string s in special) marked[s] = new HashSet<string>(new[] { "Json" }, StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(marked.Values.SelectMany(x => x), StringComparer.OrdinalIgnoreCase);
            var cores = Core.All(t);
            bool Names(int from, int to)
            {
                for (int k = from; k < to; k++)
                    if (t[k].IsName && names.Contains(t[k].Text) && !(k >= 1 && t[k - 1].Is("AS")) && !(k + 1 < t.Count && t[k + 1].IsPunct("("))) return true;
                return false;
            }
            for (bool more = true; more;)
            {
                more = false;
                foreach (var c in cores)
                    foreach (var (s, e) in c.Results)
                    {
                        string? name = e - s >= 3 && t[e - 2].Is("AS") ? t[e - 1].Text : Named(t, s, e) ? t[e - 1].Text : null;
                        if (name != null && !names.Contains(name) && Names(s, e - 1)) { names.Add(name); more = true; }
                    }
            }
            // A function that reads the mark, given one of them
            bool needed = false;
            for (int k = 0; k + 1 < t.Count && !needed; k++)
            {
                if (t[k].Kind != TokKind.Word || !MarkReaders.Contains(t[k].Text) || !t[k + 1].IsPunct("(")) continue;
                int close = k + 2;
                while (close < t.Count && t[close].Depth > t[k + 1].Depth) close++;
                needed = Names(k + 2, close);
            }
            if (needed) FoldJsonViews(marked.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList());
        }

        // -------------------------------------------------------------- 1. Worked out once per record (GAPS S142)

        /// <summary>One place the statement works a value out of a record view's big column: its tokens, its text as the
        /// view's SELECT gives it, who reads the value there (the view's name in that SELECT, or a subquery's alias the
        /// value comes up through), and, where the view can be NULL in that SELECT's rows and the expression may give a
        /// value on NULL, the value it gives there (S144).</summary>
        sealed record Use(int Start, int End, string Expr, string Reader, string? WhenNull);

        /// <summary>A subquery's result column that hands the view's Json up (<c>c.Json</c>, <c>Json AS j</c>): given as
        /// the values worked out of it instead.</summary>
        sealed record HandedUp(int Start, int End, string Reader);

        /// <summary>Functions that give one value for one input and NULL for a NULL input (see the top of this file).</summary>
        static readonly HashSet<string> PerRecordFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "length", "octet_length", "json_extract", "jsonb_extract", "json_type", "json_array_length", "instr", "substr", "substring",
            "lower", "upper", "trim", "ltrim", "rtrim", "replace", "abs", "unicode", "like", "glob", "round", "sign",
        };

        /// <summary>Functions that give one value for one input but may give a value for NULL (GAPS S144): worked out once
        /// per record too; on the side of an outer join that can be NULL the use reads the value they give on NULL there.
        /// Not the functions that do more than give a value (name(), text(), edid() note what they read for the answer) or
        /// give another value each time (random()).</summary>
        static readonly HashSet<string> PerRecordAnyFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "coalesce", "ifnull", "iif", "nullif", "hex", "quote", "typeof", "printf", "format", "char", "concat", "concat_ws",
            "json_valid", "json_error_position", "display",
        };

        /// <summary>Words an expression of one record may hold; those of <see cref="NotNullStrict"/> may give a value on NULL.</summary>
        static readonly HashSet<string> ExprWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "NULL", "CASE", "WHEN", "THEN", "ELSE", "END", "IS", "NOT", "AND", "OR", "LIKE", "GLOB", "BETWEEN", "ESCAPE", "TRUE", "FALSE", "ISNULL", "NOTNULL",
        };
        static readonly HashSet<string> NotNullStrict = new(StringComparer.OrdinalIgnoreCase) { "CASE", "IS", "AND", "OR", "ISNULL", "NOTNULL" };

        /// <summary>Words before which a LIKE's (or IS NULL's) left side starts, and after which it ends.</summary>
        static readonly HashSet<string> LowBefore = new(StringComparer.OrdinalIgnoreCase) { "WHERE", "ON", "AND", "OR", "NOT", "WHEN", "THEN", "ELSE", "SELECT", "HAVING", "DISTINCT", "ALL", "CASE", "BY" };
        static readonly HashSet<string> LowAfter = new(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "OR", "THEN", "WHEN", "ELSE", "END", "GROUP", "ORDER", "LIMIT", "HAVING", "WINDOW", "UNION", "INTERSECT", "EXCEPT", "WHERE",
            "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "NATURAL", "AS", "FROM", "ASC", "DESC", "NULLS",
        };

        /// <summary>
        /// Step 1 (see the top of this file): each record view of two parts whose Json the statement uses only in
        /// expressions of one record is given, in that SELECT's FROM, as a subquery that works them out once per record.
        /// </summary>
        string PerRecordValues(string sql, ref List<Tok> toks, Probed probed, bool explain)
        {
            var cores = Core.All(toks);
            var first = cores.FirstOrDefault(c => c.Group.Open < 0);
            if (first == null) return sql;
            var big = BigViews(probed);
            var numbers = ParameterNumbers(toks);
            bool TwoParts(FromItem i) => _jsonJoinViews.ContainsKey(i.Big.Name) || _groupedViews.ContainsKey(i.Big.Name);
            var found = new List<(Core Core, FromItem Item, List<Use> Uses, List<HandedUp> Handed)>();
            foreach (var c in cores)
                foreach (var item in Items(toks, c, big).Where(TwoParts))
                    if (Uses(sql, toks, cores, c, item, numbers) is var (uses, handed) && uses.Count > 0) found.Add((c, item, uses, handed));
            if (found.Count == 0) return sql;
            var plan = PlanOf(sql, toks);
            if (plan == null) return sql;
            var regions = new Regions(cores);
            regions.Walk(plan);
            var edits = new List<(int Start, int End, string Text)>();
            var whole = new HashSet<int>();
            var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            try
            {
                foreach (var (c, item, uses, handed) in found)
                {
                    // A view of J82's (the statement groups rows) is worked out once per record already: this only takes the
                    // record's JSON out of what is kept. Else, where the view is kept on the inner side of a join or read
                    // for each row of a correlated subquery, every record is worked out once: done when the statement
                    // reads its records' JSON more than twice over
                    bool correlated = regions.Correlated(c);
                    if (_jsonJoinViews.ContainsKey(item.Big.Name) && (correlated || StoredHere(regions, cores, c, item))
                        && !(item.Big.RecordJson && Worth(sql, toks, cores, regions, first, c, item, uses, big, probed, made)))
                        continue;
                    var names = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var u in uses)
                        if (!names.ContainsKey(u.Expr)) names[u.Expr] = $"sfs_e{++n}";
                    var bigCols = BigNames(item);
                    var cols = Used(probed, item.Big.Name).Where(x => !bigCols.Contains(x)).Select(SqlText.Name)
                        .Concat(names.Select(kv => $"{kv.Key} AS {kv.Value}")).ToList();
                    // A row of the view is told from a row an outer join makes without one (S144)
                    if (uses.Any(u => u.WhenNull != null)) cols.Add("1 AS sfs_r");
                    string alias = SqlText.Name(item.Alias);
                    string select = $"SELECT {string.Join(", ", cols)} FROM {SqlText.Name(item.Big.Name)} AS {alias}";
                    if (correlated)
                    {
                        // Read again for each row around it: a table made once, indexed on the columns the subquery
                        // matches (as a subquery SQLite may run it again for each row, and in an aggregate's argument it
                        // plans it as run once: no automatic index, measured; see the top of this file), holding the
                        // records the subquery's own conditions on them keep (S145)
                        var keep = OwnTerms(sql, toks, c, item, probed, numbers, forFill: true);
                        string table = PerRecordTable(select + (keep.Count > 0 ? " WHERE " + string.Join(" AND ", keep) : ""), Keys(toks, c, item.Alias), item, probed, fill: !explain);
                        edits.Add((toks[item.Start].Start, toks[item.End - 1].End, $"temp.{table} AS {alias}"));
                    }
                    else
                        edits.Add((toks[item.Start].Start, toks[item.End - 1].End, $"({select} UNION ALL SELECT {string.Join(", ", Enumerable.Repeat("NULL", cols.Count))} WHERE 0) AS {alias}"));
                    foreach (var u in uses)
                    {
                        string reader = SqlText.Name(u.Reader), value = $"{reader}.{names[u.Expr]}";
                        edits.Add((toks[u.Start].Start, toks[u.End - 1].End, u.WhenNull == null ? value : $"CASE WHEN {reader}.sfs_r IS NULL THEN ({u.WhenNull}) ELSE {value} END"));
                    }
                    foreach (var h in handed)
                    {
                        var mine = uses.Where(u => u.Reader == h.Reader).ToList();
                        edits.Add((toks[h.Start].Start, toks[h.End - 1].End,
                            string.Join(", ", mine.Select(u => names[u.Expr]).Distinct().Select(e => $"{alias}.{e} AS {e}")
                                .Concat(mine.Any(u => u.WhenNull != null) ? new[] { $"{alias}.sfs_r AS sfs_r" } : Array.Empty<string>()))));
                        whole.Add(toks[h.Start].Start);
                    }
                }
                if (edits.Count == 0) return sql;
                // A positional parameter keeps its number where an expression with one moved before it (S144): each ?
                // the edits don't take is written with its number, as SQLite numbers it
                var tk = toks;
                foreach (var (at, num) in numbers)
                    if (!edits.Any(x => x.Start <= tk[at].Start && tk[at].End <= x.End))
                        edits.Add((tk[at].End, tk[at].End, num.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                // A result column whose text changed keeps the name SQLite gives it: its text as written
                var renamed = new List<(int Start, int End, string Text)>();
                foreach (var core in cores)
                    foreach (var (s, e) in core.Results)
                    {
                        int from = toks[s].Start, to = toks[e - 1].End;
                        if (whole.Contains(from) || e - s == 1 && toks[s].IsPunct("*") || Named(toks, s, e)) continue;
                        if (edits.Any(x => x.Start >= from && x.End <= to)) renamed.Add((to, to, $" AS {SqlText.Name(sql[from..to])}"));
                    }
                edits.AddRange(renamed);
            }
            catch (SqliteException e) when (e.SqliteErrorCode != raw.SQLITE_INTERRUPT) { return sql; }
            finally
            {
                foreach (var name in made.Values) ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{name}");
            }
            // Insertions at one place go in the order they were made (a parameter's number before a column's name)
            foreach (var (start, end, text) in edits.Select((x, i) => (x, i)).OrderByDescending(p => p.x.Start).ThenByDescending(p => p.i).Select(p => p.x))
                sql = sql[..start] + text + sql[end..];
            toks = SqlText.Tokenize(sql);
            return sql;
        }

        /// <summary>
        /// The numbers SQLite gives the statement's positional parameters (<c>?</c>), by token: one more than the largest
        /// number given before it (<c>?NNN</c> gives NNN, a named parameter the next number the first time it appears).
        /// </summary>
        static Dictionary<int, int> ParameterNumbers(List<Tok> t)
        {
            var numbers = new Dictionary<int, int>();
            var named = new HashSet<string>(StringComparer.Ordinal);
            int max = 0;
            for (int i = 0; i < t.Count; i++)
            {
                var x = t[i];
                if (x.Kind == TokKind.Word && x.Text.Length > 1 && x.Text[0] is ':' or '@' or '$') { if (named.Add(x.Text)) max++; continue; }
                if (!x.IsPunct("?")) continue;
                if (i + 1 < t.Count && t[i + 1].Kind == TokKind.Number && t[i + 1].Start == x.End
                    && int.TryParse(t[i + 1].Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int k))
                    max = Math.Max(max, k);
                else numbers[i] = ++max;
            }
            return numbers;
        }

        /// <summary>The text of the tokens [s, e) as written, each positional parameter with its number, and each column
        /// reference <paramref name="refAt"/> finds (its length in tokens) written as <paramref name="with"/>.</summary>
        static string Source(string sql, List<Tok> t, int s, int e, Dictionary<int, int> numbers, Func<int, int>? refAt = null, string? with = null)
        {
            var sb = new System.Text.StringBuilder();
            int pos = t[s].Start;
            for (int i = s; i < e; i++)
            {
                int len = refAt?.Invoke(i) ?? 0;
                if (len > 0)
                {
                    sb.Append(sql, pos, t[i].Start - pos).Append(with);
                    pos = t[i + len - 1].End;
                    i += len - 1;
                    continue;
                }
                if (numbers.TryGetValue(i, out int num))
                {
                    sb.Append(sql, pos, t[i].End - pos).Append(num.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    pos = t[i].End;
                }
            }
            sb.Append(sql, pos, t[e - 1].End - pos);
            return sb.ToString();
        }

        /// <summary>True when the FROM item starting at token <paramref name="start"/> can be NULL in its SELECT's rows: the
        /// right side of a LEFT (or FULL) JOIN, or before a RIGHT (or FULL) JOIN.</summary>
        static bool NullSide(List<Tok> t, Core c, int start)
        {
            var ops = new List<(int Start, string Op)>();
            string op = "INNER";
            bool next = true;
            for (int k = c.From + 1; k < c.FromEnd; k++)
            {
                if (t[k].Depth != c.Depth) continue;
                if (next) { ops.Add((k, op)); next = false; op = "INNER"; }
                if (t[k].IsPunct(",")) { next = true; continue; }
                if (t[k].Is("LEFT") || t[k].Is("RIGHT") || t[k].Is("FULL")) op = t[k].Text.ToUpperInvariant();
                if (t[k].Is("JOIN")) next = true;
            }
            int i = ops.FindIndex(o => o.Start == start);
            return i >= 0 && (ops[i].Op is "LEFT" or "FULL" || ops.Skip(i + 1).Any(o => o.Op is "RIGHT" or "FULL"));
        }

        /// <summary>
        /// A temporary table of a record view's per-record values for a correlated subquery (<paramref name="select"/>):
        /// its columns of the types SQLite gives them (CREATE TABLE … AS, read back) and the view's collations (FormKey,
        /// EditorID, Plugin and link columns NOCASE, as the scratch tables and ViewBuilder have them), an index on
        /// <paramref name="keys"/>, filled once (not for EXPLAIN, which shows the plan over it empty). Dropped when the
        /// next statement starts.
        /// </summary>
        string PerRecordTable(string select, List<string> keys, FromItem item, Probed probed, bool fill)
        {
            string table = $"sfs_p{_perRecordTables.Count + 1}", name = SqlText.Name(table);
            _perRecordTables.Add(table);
            ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{name}; CREATE TEMP TABLE {name} AS {select} LIMIT 0");
            var cols = new List<(string Name, string Type)>();
            using (var cmd = _con.CreateCommand())
            {
                cmd.CommandText = $"SELECT name, type FROM pragma_table_info({SqlText.Literal(table)}, 'temp')";
                using var r = cmd.ExecuteReader();
                while (r.Read()) cols.Add((r.GetString(0), r.GetString(1)));
            }
            var view = probed.Views.Keys.FirstOrDefault(v => v.Name.Equals(item.Big.Name, StringComparison.OrdinalIgnoreCase));
            bool NoCase(string c) => c is "FormKey" or "EditorID" or "Plugin" || view != null && view.ByName.TryGetValue(c, out var spec) && spec.IsLink;
            ScratchFile.Exec(_con, $"DROP TABLE temp.{name}; CREATE TEMP TABLE {name}("
                + string.Join(", ", cols.Select(c => $"{SqlText.Name(c.Name)} {c.Type}{(NoCase(c.Name) ? " COLLATE NOCASE" : "")}")) + ")"
                + (keys.Count > 0 ? $"; CREATE INDEX temp.{SqlText.Name(table + "_k")} ON {name}({string.Join(", ", keys.Select(SqlText.Name))})" : ""));
            if (fill)
            {
                using var ins = _con.CreateCommand();
                ins.CommandText = $"INSERT INTO temp.{name} {select}";
                ins.CommandTimeout = 0;
                BindParameters(ins, SqlText.Tokenize(ins.CommandText));
                ins.ExecuteNonQuery();
            }
            return table;
        }

        /// <summary>The view's columns a correlated SELECT matches with <c>=</c> in its WHERE (top-level AND terms
        /// <c>alias.col = …</c> whose other side doesn't name the view): what its table is indexed on.</summary>
        static List<string> Keys(List<Tok> t, Core c, string alias)
        {
            var keys = new List<string>();
            if (c.Where < 0) return keys;
            int start = c.Where + 1;
            if (Enumerable.Range(start, c.WhereEnd - start).Any(k => t[k].Depth == c.Depth && t[k].Is("OR"))) return keys;
            bool Mine(int k) => t[k].IsName && t[k].Text.Equals(alias, StringComparison.OrdinalIgnoreCase) && k + 1 < t.Count && t[k + 1].IsPunct(".");
            for (int k = start; k <= c.WhereEnd; k++)
            {
                if (k < c.WhereEnd && !(t[k].Depth == c.Depth && t[k].Is("AND") && !BetweenAnd(t, start, k, c.Depth))) continue;
                int eq = Enumerable.Range(start, k - start).FirstOrDefault(i => t[i].Depth == c.Depth && (t[i].IsPunct("=") || t[i].IsPunct("==")), -1);
                if (eq > start)
                {
                    bool left = eq - start == 3 && Mine(start) && t[start + 2].IsName, right = k - eq - 1 == 3 && Mine(eq + 1) && t[eq + 3].IsName;
                    if (left && !Enumerable.Range(eq + 1, k - eq - 1).Any(Mine)) keys.Add(t[start + 2].Text);
                    else if (right && !Enumerable.Range(start, eq - start).Any(Mine)) keys.Add(t[eq + 3].Text);
                }
                start = k + 1;
            }
            return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Functions whose value changes from call to call: a condition with one is never moved.</summary>
        static readonly HashSet<string> Volatile = new(StringComparer.OrdinalIgnoreCase) { "random", "randomblob", "changes", "total_changes", "last_insert_rowid" };

        /// <summary>
        /// GAPS S145: the conditions of the SELECT that name only the view's own columns (not its Json or JSON columns),
        /// each as its text: the top-level AND terms of its WHERE (not where the view can be NULL in the SELECT's rows) and
        /// of the ON after the view. SQLite pushes them into the view's subquery, so they say which records are worked
        /// out; a correlated subquery's table is filled with those records alone (<paramref name="forFill"/>: without a
        /// condition whose value changes from call to call).
        /// </summary>
        static List<string> OwnTerms(string sql, List<Tok> t, Core c, FromItem item, Probed probed, Dictionary<int, int> numbers, bool forFill)
        {
            var terms = new List<string>();
            var big = BigNames(item);
            var cols = Used(probed, item.Big.Name);
            bool single = item.Start == c.From + 1 && item.End == c.FromEnd;
            void Split(int from, int to, int depth)
            {
                if (to <= from) return;
                bool or = Enumerable.Range(from, to - from).Any(k => t[k].Depth == depth && t[k].Is("OR"));
                int start = from;
                for (int k = from; k <= to; k++)
                    if (k == to || !or && t[k].Depth == depth && t[k].Is("AND") && !BetweenAnd(t, start, k, depth))
                    {
                        if (k > start && Own(start, k)) terms.Add("(" + Source(sql, t, start, k, numbers) + ")");
                        start = k + 1;
                    }
            }
            bool Own(int s, int e)
            {
                for (int k = s; k < e; k++)
                {
                    var x = t[k];
                    if (x.Is("SELECT") || x.IsPunct("?") && !forFill) return false;
                    if (!x.IsName) continue;
                    if (k + 1 < e && t[k + 1].IsPunct("(")) { if (forFill && Volatile.Contains(x.Text)) return false; continue; }
                    if (k + 1 < e && t[k + 1].IsPunct("."))
                    {
                        if (!x.Text.Equals(item.Alias, StringComparison.OrdinalIgnoreCase)) return false;
                        continue;
                    }
                    if (k >= 2 && t[k - 1].IsPunct(".")) { if (big.Contains(x.Text)) return false; continue; }
                    if (x.Kind == TokKind.Word && (ExprWords.Contains(x.Text) || x.Text.Length > 1 && x.Text[0] is ':' or '@' or '$' || k >= 1 && t[k - 1].Is("COLLATE"))) continue;
                    if (x.Is("COLLATE") || x.Is("IN") || x.Is("GLOB") || x.Is("REGEXP") || x.Is("MATCH")) continue;
                    // A name alone is the view's column where the view is the SELECT's only table
                    if (!single || !cols.Contains(x.Text) || big.Contains(x.Text)) return false;
                }
                return true;
            }
            if (c.Where >= 0 && !NullSide(t, c, item.Start)) Split(c.Where + 1, c.WhereEnd, c.Depth);
            if (item.End < c.FromEnd && t[item.End].Is("ON"))
            {
                int end = item.End + 1;
                while (end < c.FromEnd && !(t[end].Depth == c.Depth && (t[end].IsPunct(",") || t[end].Is("JOIN") || t[end].Is("LEFT") || t[end].Is("RIGHT")
                    || t[end].Is("FULL") || t[end].Is("INNER") || t[end].Is("CROSS") || t[end].Is("NATURAL")))) end++;
                Split(item.End + 1, end, c.Depth);
            }
            return terms;
        }

        /// <summary>
        /// GAPS S145: the records of the view the SELECT works out (count, and the size of their stored Json all together):
        /// those its own conditions keep (<see cref="OwnTerms"/>), counted by FormKey and plugin against the view's sizes;
        /// all of them where it has none or the count can't be made.
        /// </summary>
        (long Count, long Bytes) OnceFor(string sql, List<Tok> toks, Core c, FromItem item, Probed probed, Dictionary<string, string> made)
        {
            var terms = OwnTerms(sql, toks, c, item, probed, ParameterNumbers(toks), forFill: false);
            using var cmd = _con.CreateCommand();
            cmd.CommandText = terms.Count == 0 ? $"SELECT count(*), total(n) FROM temp.{SizeTable(item.Big.Name, made)}"
                : $"SELECT count(*), total(z.n) FROM (SELECT FormKey, Plugin FROM {SqlText.Name(item.Big.Name)} AS {SqlText.Name(item.Alias)} WHERE {string.Join(" AND ", terms)}) AS sfs_f "
                    + $"JOIN temp.{SizeTable(item.Big.Name, made)} z ON z.k = sfs_f.FormKey AND z.p = sfs_f.Plugin";
            cmd.CommandTimeout = 0;
            try
            {
                BindParameters(cmd, SqlText.Tokenize(cmd.CommandText));
                using var r = cmd.ExecuteReader();
                r.Read();
                return (r.GetInt64(0), (long)r.GetDouble(1));
            }
            catch (SqliteException e) when (e.SqliteErrorCode != raw.SQLITE_INTERRUPT && terms.Count > 0)
            {
                using var all = _con.CreateCommand();
                all.CommandText = $"SELECT count(*), total(n) FROM temp.{SizeTable(item.Big.Name, made)}";
                using var r = all.ExecuteReader();
                r.Read();
                return (r.GetInt64(0), (long)r.GetDouble(1));
            }
        }

        /// <summary>Words that end an expression, or stand before its last word, where a name after them isn't the column's
        /// own name (x IS NULL, CASE … END, a COLLATE's name).</summary>
        static readonly HashSet<string> NotAName = new(StringComparer.OrdinalIgnoreCase)
        {
            "NULL", "END", "TRUE", "FALSE", "CURRENT_TIME", "CURRENT_DATE", "CURRENT_TIMESTAMP",
        };
        static readonly HashSet<string> BeforeNotAName = new(StringComparer.OrdinalIgnoreCase)
        {
            "IS", "NOT", "COLLATE", "AND", "OR", "LIKE", "GLOB", "BETWEEN", "IN", "ESCAPE", "CASE", "WHEN", "THEN", "ELSE", "DISTINCT", "CAST", "SELECT", "ALL",
        };

        /// <summary>True when the result column [s, e) gives itself a name (AS name, or a name after the expression).</summary>
        static bool Named(List<Tok> t, int s, int e)
        {
            if (e - s < 2 || !t[e - 1].IsName || t[e - 1].Kind == TokKind.Word && NotAName.Contains(t[e - 1].Text)) return false;
            var b = t[e - 2];
            if (b.Is("AS")) return true;
            if (b.IsPunct(".")) return false;
            return b.IsPunct(")") || b.Kind is TokKind.String or TokKind.Number || b.Is("END")
                || b.IsName && !(b.Kind == TokKind.Word && BeforeNotAName.Contains(b.Text));
        }

        /// <summary>A record view's big columns: its Json (a record's) and its columns that hold JSON.</summary>
        static HashSet<string> BigNames(FromItem item)
        {
            var names = new HashSet<string>(item.Big.Columns, StringComparer.OrdinalIgnoreCase);
            if (item.Big.RecordJson) names.Add("Json");
            return names;
        }

        /// <summary>
        /// True when working out the view's records once makes less JSON than the statement as it stands: the rows that
        /// read a record's Json (the SELECT's FROM and WHERE counted, joined to the SELECT around it for a correlated
        /// subquery, and the rows of the SELECT a subquery hands the Json up to), each weighed by its record's stored size,
        /// more than twice the size of the records the SELECT's own conditions keep (S145), once. False when a count can't
        /// be made.
        /// </summary>
        bool Worth(string sql, List<Tok> toks, List<Core> cores, Regions regions, Core first, Core c, FromItem item, List<Use> uses, Dictionary<string, BigView> big, Probed probed, Dictionary<string, string> made)
        {
            string prefix = Prefix(sql, toks, first);
            long bytes = 0;
            var kept = new List<Kept> { new(item, null) };
            if (uses.Any(u => u.Reader.Equals(item.Alias, StringComparison.OrdinalIgnoreCase)))
            {
                (long Rows, long Bytes)? w;
                if (regions.Correlated(c))
                    w = Around(cores, c) is { } o ? Weigh(sql, toks, prefix, c, kept, probed, made, distinct: false, dropJsonTerms: true, outer: (o, Items(toks, o, big).Where(i => i.Big.RecordJson).ToList())) : null;
                else w = Weigh(sql, toks, prefix, c, kept, probed, made, distinct: false, dropJsonTerms: true);
                if (w is not var (_, b)) return false;
                bytes += b;
            }
            foreach (string via in uses.Select(u => u.Reader).Where(r => !r.Equals(item.Alias, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Handed up to the SELECT around it: that SELECT's rows, each with the Json the subquery hands up
                if (Around(cores, c) is not { } p) return false;
                var carried = CarriedItems(sql, toks, cores, p, big, probed, 0);
                var x = carried.FirstOrDefault(i => i.Alias.Equals(via, StringComparison.OrdinalIgnoreCase));
                if (x == null || Weigh(sql, toks, prefix, p, x.Big.Columns.Select(col => new Kept(x, col)).ToList(), probed, made, distinct: false, dropJsonTerms: true, carried: carried) is not var (_, b)) return false;
                bytes += b;
            }
            var (records, size) = OnceFor(sql, toks, c, item, probed, made);
            long once = 2 * size;
            if (regions.Correlated(c) && Keys(toks, c, item.Alias).Count == 0)
            {
                // Read for each row around it with nothing to index its table on: scanned whole each time, each of its
                // rows counted as 18 bytes of JSON made (a scan of a materialized table's small rows, 29.3 million rows in
                // 2.4 s, 80 ns each, against 4.5 ns a byte to make a record's JSON: J99's py/render.py)
                if (Around(cores, c) is not { } o || Weigh(sql, toks, prefix, o, new List<Kept>(), probed, made, distinct: false) is not var (rows, _)) return false;
                once += rows * records * ScanRowAsJsonBytes;
            }
            return bytes > once;
        }

        /// <summary>A scan of one small row of a materialized CTE, in bytes of a record's JSON made in the same time (see Worth).</summary>
        const long ScanRowAsJsonBytes = 18;

        /// <summary>
        /// Every use of the view's big columns in the SELECT (and in the subqueries inside it), each an expression of the
        /// record alone, and the result columns that hand its Json up to the SELECT around it (whose uses of it are then
        /// among the uses); null when one use isn't such an expression, or the view's columns are read in a way the
        /// subquery would change (a star, NATURAL, INDEXED BY, a SELECT inside that names something else by the same name).
        /// A use that may give a value on NULL, where the view can be NULL in the SELECT's rows, carries what it gives
        /// there (<see cref="Use.WhenNull"/>: the expression with NULL for each of the record's columns).
        /// </summary>
        static (List<Use> Uses, List<HandedUp> Handed)? Uses(string sql, List<Tok> t, List<Core> cores, Core c, FromItem item, Dictionary<int, int> numbers)
        {
            var bigNames = BigNames(item);
            string alias = item.Alias;
            if (item.End < c.FromEnd && (t[item.End].Is("INDEXED") || t[item.End].Is("NOT"))) return null;
            for (int k = c.From + 1; k < c.FromEnd; k++) if (t[k].Depth == c.Depth && t[k].Is("NATURAL")) return null;
            if (c.Results.Any(r => r.End - r.Start == 1 && t[r.Start].IsPunct("*"))) return null;
            var nested = cores.Where(d => d != c && d.Select > c.Select && d.Select < c.End).ToList();
            if (nested.Any(d => Names(t, d, alias))) return null;
            bool single = item.Start == c.From + 1 && item.End == c.FromEnd;
            bool nullSide = NullSide(t, c, item.Start);
            var uses = new List<Use>();
            var handed = new List<HandedUp>();
            bool Column(int i) => i >= 2 && t[i - 1].IsPunct(".") && t[i - 2].IsName && t[i - 2].Text.Equals(alias, StringComparison.OrdinalIgnoreCase);
            for (int k = c.Select + 1; k < c.End; k++)
            {
                if (k >= item.Start && k < item.End) continue;
                if (t[k].IsPunct("*") && Column(k)) return null;
                if (!t[k].IsName || !bigNames.Contains(t[k].Text)) continue;
                int s;
                if (Column(k)) s = k - 2;
                else if (k >= 1 && (t[k - 1].IsPunct(".") || t[k - 1].Is("AS")) || k + 1 < t.Count && (t[k + 1].IsPunct(".") || t[k + 1].IsPunct("("))) continue;
                // A name alone is this view's only where it is the SELECT's only table, outside the SELECTs inside it
                else if (!single || nested.Any(d => k > d.Select && k < d.End)) return null;
                else s = k;
                int use = k;
                bool Of(int i) => Column(i) || single && i == use;
                int RefAt(int i) => i + 2 < t.Count && t[i + 1].IsPunct(".") && Column(i + 2) ? 3 : single && i == use && !(i >= 1 && t[i - 1].IsPunct(".")) ? 1 : 0;
                var (es, ee, strict) = Extend(t, s, k + 1, c.Select + 1, c.End, Of);
                if (ee - es > k + 1 - s)
                {
                    uses.Add(new Use(es, ee, Source(sql, t, es, ee, numbers), alias, strict || !nullSide ? null : Source(sql, t, es, ee, numbers, RefAt, "NULL")));
                    k = ee - 1;
                    continue;
                }
                // The whole value: a result column that hands it up to the SELECT around it, or a use that needs it whole
                if (HandUp(sql, t, cores, c, s, k + 1, alias, single, numbers) is not { } up) return null;
                handed.Add(up.Handed);
                uses.AddRange(up.Uses);
                k = up.Handed.End - 1;
            }
            return (uses, handed);
        }

        /// <summary>True when the SELECT's FROM (at its own level) names <paramref name="name"/>: then it, not the view,
        /// is what that name means inside it.</summary>
        static bool Names(List<Tok> t, Core d, string name)
        {
            if (d.From < 0) return false;
            for (int k = d.From + 1; k < d.FromEnd; k++)
                if (t[k].Depth == d.Depth && t[k].IsName && t[k].Text.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// A result column of the SELECT that is the view's big column whole (<c>c.Json</c>, <c>Json</c>, then
        /// <c>AS j</c> or a name), in a subquery of a FROM that has only that view, doesn't group, sort, make rows distinct
        /// or limit them: the uses of it by the SELECT around it (<c>x.j</c>), each an expression of that value alone, as
        /// expressions of the record. Null when it isn't so, or the SELECT around it uses it whole, in a star, or by its
        /// name alone.
        /// </summary>
        static (HandedUp Handed, List<Use> Uses)? HandUp(string sql, List<Tok> t, List<Core> cores, Core c, int s, int e, string alias, bool single, Dictionary<int, int> numbers)
        {
            if (!single || c.Grouped || c.Distinct || c.Ordered || c.Group.Arms.Count != 1 || !c.Group.Place.StartsWith(" in its subquery ", StringComparison.Ordinal)) return null;
            for (int k = c.Select + 1; k < c.End; k++) if (t[k].Depth == c.Depth && t[k].Is("LIMIT")) return null;
            var col = c.Results.FirstOrDefault(r => r.Start == s);
            if (col == default) return null;
            string name = t[e - 1].Text;
            if (col.End == e + 1 && t[e].IsName) name = t[e].Text;
            else if (col.End == e + 2 && t[e].Is("AS") && t[e + 1].IsName) name = t[e + 1].Text;
            else if (col.End != e) return null;
            string via = c.Group.Names[0];
            if (Around(cores, c) is not { } p) return null;
            int open = c.Group.Open, close = open + 1;
            while (close < t.Count && !(t[close].IsPunct(")") && t[close].Depth == t[open].Depth)) close++;
            for (int k = p.From + 1; k < p.FromEnd; k++) if (t[k].Depth == p.Depth && t[k].Is("NATURAL")) return null;
            if (p.Results.Any(r => r.End - r.Start == 1 && t[r.Start].IsPunct("*"))) return null;
            var inner = cores.Where(d => d != p && d.Select > p.Select && d.Select < p.End && !(d.Select > open && d.Select < close)).ToList();
            if (inner.Any(d => Names(t, d, via))) return null;
            bool nullSide = NullSide(t, p, open);
            bool Column(int i) => i >= 2 && t[i - 1].IsPunct(".") && t[i - 2].IsName && t[i - 2].Text.Equals(via, StringComparison.OrdinalIgnoreCase)
                && t[i].Text.Equals(name, StringComparison.OrdinalIgnoreCase);
            int RefAt(int i) => i + 2 < t.Count && t[i + 1].IsPunct(".") && Column(i + 2) ? 3 : 0;
            var uses = new List<Use>();
            string column = $"{SqlText.Name(alias)}.{SqlText.Name(t[e - 1].Text)}";
            for (int k = p.Select + 1; k < p.End; k++)
            {
                if (k > open && k < close) { k = close; continue; }
                if (t[k].IsPunct("*") && k >= 2 && t[k - 1].IsPunct(".") && t[k - 2].Text.Equals(via, StringComparison.OrdinalIgnoreCase)) return null;
                if (!t[k].IsName || !t[k].Text.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (k >= 1 && t[k - 1].Is("AS") || k + 1 < t.Count && (t[k + 1].IsPunct("(") || t[k + 1].IsPunct("."))) continue;
                // Another table's column of that name; the name alone may be this one
                if (k >= 2 && t[k - 1].IsPunct(".") && !Column(k)) continue;
                if (!Column(k)) return null;
                var (es, ee, strict) = Extend(t, k - 2, k + 1, p.Select + 1, p.End, Column);
                if (ee - es == 3) return null;
                // The expression as the view's SELECT gives it: x.j read as the view's own column
                uses.Add(new Use(es, ee, Source(sql, t, es, ee, numbers, RefAt, column), via, strict || !nullSide ? null : Source(sql, t, es, ee, numbers, RefAt, "NULL")));
                k = ee - 1;
            }
            return uses.Count == 0 ? null : (new HandedUp(s, col.End, via), uses);
        }

        /// <summary>
        /// The expression around the tokens [s, e) (a column), as far as it stays an expression of that column alone, and
        /// whether it gives NULL for a NULL column (<c>Strict</c>). Grown, each step only where SQLite's precedence makes
        /// it one operand: -> and ->> with a constant path after it; a call of <see cref="PerRecordFunctions"/> or
        /// <see cref="PerRecordAnyFunctions"/> (or min/max of two values or more) around it whose other arguments are
        /// literals, parameters, operators, <see cref="ExprWords"/>, such calls and the columns <paramref name="column"/>
        /// allows; a CASE … END around it of the same; then [NOT] LIKE or GLOB with a literal pattern, IS [NOT] NULL,
        /// ISNULL, NOTNULL after it.
        /// </summary>
        static (int Start, int End, bool Strict) Extend(List<Tok> t, int s, int e, int lo, int hi, Func<int, bool> column)
        {
            bool strict = true;
            bool Param(int i) => t[i].Kind == TokKind.Word && t[i].Text.Length > 1 && t[i].Text[0] is ':' or '@' or '$' || t[i].IsPunct("?");
            bool Allowed(int i)
            {
                var x = t[i];
                if (x.Kind is TokKind.String or TokKind.Number || Param(i)) return true;
                if (x.Kind == TokKind.Punct)
                    return x.Text is "," or "(" or ")" or "+" or "-" or "*" or "/" or "%" or "||" or "->" or "->>" or "."
                        or "=" or "==" or "!=" or "<>" or "<" or ">" or "<=" or ">=" or "&" or "|" or "<<" or ">>" or "~";
                if (i + 1 < t.Count && t[i + 1].IsPunct("("))
                    return x.Kind == TokKind.Word && (PerRecordFunctions.Contains(x.Text) || PerRecordAnyFunctions.Contains(x.Text));
                if (x.Kind == TokKind.Word && ExprWords.Contains(x.Text)) return true;
                if (column(i)) return true;
                // The table's name before a column's dot
                return i + 2 < t.Count && t[i + 1].IsPunct(".") && column(i + 2);
            }
            bool Strict(int from, int to) => !Enumerable.Range(from, to - from).Any(i => t[i].Kind == TokKind.Word
                && (NotNullStrict.Contains(t[i].Text) || i + 1 < t.Count && t[i + 1].IsPunct("(") && PerRecordAnyFunctions.Contains(t[i].Text)));
            // Nothing that binds as tight as -> (or tighter) just before it: ||, ->, ->>, a sign
            bool Free(int at) => at <= lo || !(t[at - 1].Kind == TokKind.Punct && t[at - 1].Text is "||" or "->" or "->>" or "-" or "+" or "~" or ".");
            // A constant: a literal or a parameter (?NNN is two tokens)
            int Constant(int i) => t[i].Kind is TokKind.String or TokKind.Number || Param(i) && !t[i].IsPunct("?") ? 1
                : t[i].IsPunct("?") ? (i + 1 < t.Count && t[i + 1].Kind == TokKind.Number && t[i + 1].Start == t[i].End ? 2 : 1) : 0;
            // Where a LIKE or IS NULL after it may take it as its left side, and where its right side ends
            bool LowLeft(int at) => at > lo && (t[at - 1].IsPunct("(") || t[at - 1].IsPunct(",") || t[at - 1].Kind == TokKind.Word && LowBefore.Contains(t[at - 1].Text));
            bool LowRight(int at) => at >= t.Count || t[at].IsPunct(")") || t[at].IsPunct(",") || t[at].IsPunct(";") || t[at].Kind == TokKind.Word && LowAfter.Contains(t[at].Text);
            while (true)
            {
                if (e + 1 < hi && (t[e].IsPunct("->") || t[e].IsPunct("->>")) && Constant(e + 1) is int cl && cl > 0 && !(e + 1 + cl < t.Count && t[e + 1 + cl].Is("COLLATE")) && Free(s))
                {
                    e += 1 + cl;
                    continue;
                }
                int d = t[s].Depth;
                int open = s - 1;
                while (open >= lo && t[open].Depth >= d) open--;
                if (open > lo && t[open].IsPunct("(") && t[open].Depth == d - 1 && t[open - 1].Kind == TokKind.Word && !(open >= 2 && t[open - 2].IsPunct("."))
                    && (PerRecordFunctions.Contains(t[open - 1].Text) || PerRecordAnyFunctions.Contains(t[open - 1].Text) || t[open - 1].Is("min") || t[open - 1].Is("max")))
                {
                    int close = e;
                    while (close < hi && t[close].Depth >= d) close++;
                    // min and max of one value are aggregates: only of two values or more
                    bool scalar = !(t[open - 1].Is("min") || t[open - 1].Is("max")) || Enumerable.Range(open + 1, Math.Max(0, close - open - 1)).Any(i => t[i].Depth == d && t[i].IsPunct(","));
                    if (scalar && close < hi && t[close].IsPunct(")") && Enumerable.Range(open + 1, close - open - 1).All(i => i >= s && i < e || Allowed(i)))
                    {
                        if (PerRecordAnyFunctions.Contains(t[open - 1].Text) || !Strict(open + 1, close)) strict = false;
                        s = open - 1;
                        e = close + 1;
                        continue;
                    }
                }
                // CASE … END around it (a CASE holds no comma at its own level, so a comma means an argument list)
                int cs = -1;
                for (int i = s - 1, nest = 0; i > lo - 1 && t[i].Depth >= d; i--)
                {
                    if (t[i].Depth > d) continue;
                    if (t[i].IsPunct(",") || t[i].IsPunct(";") || t[i].Kind == TokKind.Word && (t[i].Is("SELECT") || t[i].Is("FROM") || t[i].Is("WHERE") || t[i].Is("ON") || t[i].Is("BY") || t[i].Is("HAVING"))) break;
                    if (t[i].Is("END")) nest++;
                    else if (t[i].Is("CASE")) { if (nest == 0) { cs = i; break; } nest--; }
                }
                if (cs >= 0)
                {
                    int ce = -1;
                    for (int i = e, nest = 0; i < hi && t[i].Depth >= d; i++)
                    {
                        if (t[i].Depth > d) continue;
                        if (t[i].Is("CASE")) nest++;
                        else if (t[i].Is("END")) { if (nest == 0) { ce = i; break; } nest--; }
                    }
                    if (ce > 0 && Enumerable.Range(cs, ce - cs + 1).All(i => i >= s && i < e || Allowed(i)))
                    {
                        strict = false;
                        s = cs;
                        e = ce + 1;
                        continue;
                    }
                }
                // x [NOT] LIKE 'pattern', x IS [NOT] NULL, x ISNULL, x NOTNULL: of a lower level than the operators above,
                // so only where nothing of their level or tighter is on either side
                if (LowLeft(s))
                {
                    int op = e < hi && t[e].Is("NOT") ? e + 1 : e;
                    if (op + 1 < hi && (t[op].Is("LIKE") || t[op].Is("GLOB")) && t[op + 1].Kind == TokKind.String && LowRight(op + 2)) { e = op + 2; continue; }
                    int ie = e < hi && t[e].Is("IS") ? (e + 1 < hi && t[e + 1].Is("NOT") ? e + 2 : e + 1) : -1;
                    if (ie > 0 && ie < hi && t[ie].Is("NULL") && LowRight(ie + 1)) { strict = false; e = ie + 1; continue; }
                    if (e < hi && (t[e].Is("ISNULL") || t[e].Is("NOTNULL")) && LowRight(e + 1)) { strict = false; e += 1; continue; }
                    if (e + 1 < hi && t[e].Is("NOT") && t[e + 1].Is("NULL") && LowRight(e + 2)) { strict = false; e += 2; continue; }
                }
                break;
            }
            return (s, e, strict);
        }

        static readonly HashSet<string> OuterJoin = new(StringComparer.OrdinalIgnoreCase) { "LEFT", "RIGHT", "FULL", "OUTER", "NATURAL" };
        static readonly HashSet<string> NotInnerJoin = new(StringComparer.OrdinalIgnoreCase) { "LEFT", "RIGHT", "FULL", "OUTER", "NATURAL", "USING", "INDEXED", "NOT" };

        /// <summary>
        /// The SELECT's FROM given with <paramref name="view"/> first and held there (CROSS JOIN), its ON conditions on the
        /// last table; null when the FROM isn't plain inner joins of names (then the order can't be changed safely).
        /// </summary>
        static (int Start, int End, string Text)? FirstInJoin(string sql, List<Tok> t, Core c, FromItem view)
        {
            int d = c.Depth;
            var items = new List<(int Start, int End)>();
            var conds = new List<(int Start, int End)>();
            int k = c.From + 1, start = k;
            bool inCond = false;
            void Close(int at)
            {
                if (at > start) (inCond ? conds : items).Add((start, at));
            }
            for (; k < c.FromEnd; k++)
            {
                if (t[k].Depth != d) { if (!inCond) return null; continue; }
                // Outer and natural joins can't be reordered; inside an ON condition only a join's own words end it
                if (t[k].Kind == TokKind.Word && (inCond ? OuterJoin.Contains(t[k].Text) : NotInnerJoin.Contains(t[k].Text))) return null;
                if (t[k].IsPunct("(") && !inCond) return null;
                if (t[k].Is("ON")) { Close(k); inCond = true; start = k + 1; continue; }
                bool comma = t[k].IsPunct(",");
                bool join = t[k].Is("JOIN") || (t[k].Is("INNER") || t[k].Is("CROSS")) && k + 1 < c.FromEnd && t[k + 1].Is("JOIN");
                if (!comma && !join) continue;
                Close(k);
                inCond = false;
                if (!comma && !t[k].Is("JOIN")) k++;
                start = k + 1;
            }
            Close(c.FromEnd);
            if (items.Count < 2) return null;
            int vi = items.FindIndex(i => i.Start == view.Start);
            if (vi < 0) return null;
            string Text((int Start, int End) r) => sql[t[r.Start].Start..t[r.End - 1].End];
            var rest = items.Where((_, i) => i != vi).Select(Text).ToList();
            string text = Text(items[vi]) + " CROSS JOIN " + string.Join(" JOIN ", rest)
                + (conds.Count == 0 ? "" : " ON " + string.Join(" AND ", conds.Select(r => "(" + Text(r) + ")")));
            return (t[c.From + 1].Start, t[c.FromEnd - 1].End, text);
        }
    }
}
