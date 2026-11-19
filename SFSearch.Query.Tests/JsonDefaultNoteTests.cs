// GAPS S199: a record's JSON leaves out a field that holds its default, so SQL that reads the field from the JSON gets
// NULL there. The answer says so, naming the field and its default, when a JSON path the SQL reads ends in such a
// field; a path inside coalesce() has its default already, and a field that may truly be missing (a weapon's Name)
// isn't named. The note's claim is held against the type's column: where the JSON gives NULL, the column gives the
// default.
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class JsonDefaultNoteTests
{
    const string SF = "Starfield.esm";
    const string Says = "leaves out a field that holds its default";

    public static string Run(Ctx c)
    {
        static string? NoteOf(QueryAnswer a) => a.Notes.FirstOrDefault(n => n.Contains(Says, StringComparison.Ordinal));

        // A plain field of the record, read from its own Json: named, with its default; and the claim is true
        var scope = c.Ok("SELECT sum(Json ->> '$.HasScope' IS NULL), sum(HasScope = 0), count(*) FROM Weapon", SF);
        string? note = NoteOf(scope);
        Check(note != null && note.Contains("HasScope (default false)", StringComparison.Ordinal), $"a weapon's HasScope read from the JSON is named with its default: {note ?? "(no note)"}");
        long byJson = Convert.ToInt64(scope.Rows[0][0]), byColumn = Convert.ToInt64(scope.Rows[0][1]), all = Convert.ToInt64(scope.Rows[0][2]);
        Check(byJson == byColumn && byJson > 0 && byJson < all, $"the JSON gives NULL exactly where the column gives the default: {byJson:N0} by the JSON, {byColumn:N0} by the column, of {all:N0} weapons");

        // A field below a list's item, read from json_each's value (no record type to start at): named when every place the path can end is such a field
        var op = c.Ok("SELECT json_extract(c.value, '$.CompareOperator') AS Op, count(*) AS N FROM ConstructibleObject r, json_each(r.Json, '$.Conditions') c GROUP BY 1 ORDER BY 1", SF);
        note = NoteOf(op);
        Check(note != null && note.Contains("CompareOperator (default EqualTo)", StringComparison.Ordinal), $"a condition's operator read from a list item is named: {note ?? "(no note)"}");
        Check(op.Rows.Any(r => r[0] == null && Convert.ToInt64(r[1]) > 0) && !op.Rows.Any(r => (r[0] as string) == "EqualTo"),
            "the operator is NULL, never EqualTo, in the JSON of the recipes' conditions");

        // The same path down from the record: two fields, each named once
        var two = c.Ok("SELECT Json ->> '$.Conditions[0].CompareOperator', Json ->> '$.Conditions[0].CompareOperator', Json ->> '$.AmountProduced' FROM ConstructibleObject ORDER BY 1", SF);
        note = NoteOf(two);
        Check(note != null && note.Split("CompareOperator").Length == 2, $"a field read twice is named once: {note ?? "(no note)"}");

        // The default already given: no note
        var given = c.Ok("SELECT coalesce(Json ->> '$.HasScope', 0), ifnull(json_extract(Json, '$.HasScope'), 0) FROM Weapon ORDER BY 1", SF);
        Check(NoteOf(given) == null, $"a path inside coalesce() or ifnull() isn't named: {NoteOf(given)}");

        // A field that may truly be missing, and a list: no note
        var name = c.Ok("SELECT text(Json -> '$.Name', 'de'), Json ->> '$.Name.Value', json_array_length(Json, '$.Keywords') FROM Weapon ORDER BY 1", SF);
        Check(NoteOf(name) == null, $"a weapon's Name and its Keywords aren't named: {NoteOf(name)}");

        // No JSON path at all: no note
        var plain = c.Ok("SELECT EditorID, HasScope FROM Weapon ORDER BY 1", SF);
        Check(NoteOf(plain) == null, "a query that reads no JSON path has no such note");

        return $"{SF}: {byJson:N0} of {all:N0} weapons leave HasScope out of the JSON and the column says false for the same {byColumn:N0}; the note names HasScope and CompareOperator with their defaults, once each, and stays away from coalesce(), ifnull(), Name and lists";
    }
}
