// GAPS S201 to S203 (an agent's session, 2026-11-19): a statement with several WITH parts.
//   S201  SQLite names one of the statement's own WITH names as the missing table when a record table that a later
//         part reads isn't there yet (a WITH name used inside a recursive part and again after it). The probe took
//         that for an unknown table and answered: there is no table or record type "xn". The statement now runs,
//         and gives what the same steps give as temporary tables.
//   S202  a column that a list view doesn't have is answered with the view's own columns (a list of objects has no
//         Value); a WITH part named as a record type is answered with the reason, not "circular reference".
//   S203  SQLite's own list of tables has no record tables: the empty answer says where they are.
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class WithNameTests
{
    const string SF = "Starfield.esm";

    public static string Run(Ctx c)
    {
        // S201: xn is used in the recursive part and after it; xl's table (LeveledNpc_Entries) is the one SQLite hasn't been given when it names xn
        const string parts = @"WITH RECURSIVE nv AS (SELECT n.FormKey, n.DefaultTemplate, max(1) OVER (PARTITION BY n.FormKey) AS mr FROM Npc n),
xn AS (SELECT FormKey fk, DefaultTemplate par FROM nv WHERE mr = 1),
lv AS (SELECT l.FormKey, l.Reference, max(1) OVER (PARTITION BY l.FormKey, l.Idx) mr FROM LeveledNpc_Entries l),
xl AS (SELECT FormKey fk, Reference ent FROM lv WHERE mr = 1),
walk(start, cur, depth) AS (SELECT fk, fk, 0 FROM xn WHERE fk IN (SELECT fk FROM xn ORDER BY fk LIMIT 200)
  UNION SELECT w.start, n.par, w.depth + 1 FROM walk w JOIN xn n ON n.fk = w.cur WHERE n.par IS NOT NULL AND w.depth < 6
  UNION SELECT w.start, l.ent, w.depth + 1 FROM walk w JOIN xl l ON l.fk = w.cur WHERE w.depth < 6),
own AS (SELECT DISTINCT w.start, w.cur, o.fk AS isNpc FROM walk w LEFT JOIN xn o ON o.fk = w.cur)
SELECT count(*), count(isNpc), count(DISTINCT start) FROM own";
        var with = c.Ok(parts, SF);
        var temp = c.Ok(@"CREATE TEMP TABLE t_xn AS SELECT FormKey fk, DefaultTemplate par FROM Npc;
CREATE TEMP TABLE t_xl AS SELECT FormKey fk, Reference ent FROM LeveledNpc_Entries;
WITH RECURSIVE walk(start, cur, depth) AS (SELECT fk, fk, 0 FROM t_xn WHERE fk IN (SELECT fk FROM t_xn ORDER BY fk LIMIT 200)
  UNION SELECT w.start, n.par, w.depth + 1 FROM walk w JOIN t_xn n ON n.fk = w.cur WHERE n.par IS NOT NULL AND w.depth < 6
  UNION SELECT w.start, l.ent, w.depth + 1 FROM walk w JOIN t_xl l ON l.fk = w.cur WHERE w.depth < 6),
own AS (SELECT DISTINCT w.start, w.cur, o.fk AS isNpc FROM walk w LEFT JOIN t_xn o ON o.fk = w.cur)
SELECT count(*), count(isNpc), count(DISTINCT start) FROM own", SF);
        string got = Rows(with)[0], want = Rows(temp)[0];
        Check(got == want && Convert.ToInt64(with.Rows[0][0]) > 200, $"the WITH statement gives what the same steps give as temporary tables: {got} against {want}");

        // A name that is no WITH name and no table is still answered as before
        var none = c.Sql("WITH a AS (SELECT FormKey fk FROM Npc) SELECT count(*) FROM b", SF);
        Check(none.Error != null && none.Error.Contains("no table or record type \"b\"", StringComparison.Ordinal), $"a name that is nowhere: {none.Error}");

        // S202: a WITH part named as the record type it reads
        var circle = c.Sql("WITH nv AS (SELECT FormKey FROM Npc), npc AS (SELECT FormKey fk FROM nv), w AS (SELECT fk FROM npc) SELECT count(*) FROM w", SF);
        Check(circle.Error != null && circle.Error.Contains("the WITH name \"npc\" is also the table Npc", StringComparison.Ordinal) && (circle.Fix ?? "").Contains("another name", StringComparison.Ordinal),
            $"a WITH part named npc: {circle.Error} / {circle.Fix}");

        // S202: a list of objects has no Value: the view's own columns are said
        var value = c.Sql("SELECT f.Value FROM Npc_Factions f ORDER BY 1", SF);
        Check(value.Error != null && value.Error.Contains("Npc_Factions has the columns FormKey, EditorID, Plugin, Idx, Faction, Rank", StringComparison.Ordinal), $"Value on Npc_Factions: {value.Error}");
        var faction = c.Ok("SELECT count(*), count(Faction) FROM Npc_Factions", SF);
        Check(Convert.ToInt64(faction.Rows[0][1]) > 0, "Npc_Factions has a Faction column with links in it");

        // S203: SQLite's own list of tables
        var master = c.Ok("SELECT name FROM sqlite_master WHERE name LIKE 'Npc%'", SF);
        Check(master.Rows.Count == 0 && master.Notes.Any(n => n.Contains("aren't in SQLite's own list of tables", StringComparison.Ordinal) && n.Contains("schema.RecordTypes", StringComparison.Ordinal)),
            $"sqlite_master: {string.Join(" / ", master.Notes)}");

        return $"{SF}: a statement whose WITH name is used in a recursive part and after it runs and gives the temporary-table answer ({got}); a WITH part named as its record type, a missing column of a list view and sqlite_master are each answered with what to do";
    }
}
