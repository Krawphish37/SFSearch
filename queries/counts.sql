-- name: counts
-- answers: How many records of each type a plugin holds: its own, and the ones of its masters that it changes.
-- It is read from the record headers, so every record type is counted, placed objects and cells too.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- scope: plugins
-- column: Type | The record type.
-- column: Sig | Its 4-letter signature.
-- column: Records | How many records of the type the plugin holds.
-- column: Own | How many of them are the plugin's own.
-- column: Overrides | How many of them change a record of a master.
-- column: Example | The EditorID of one of them (the first by name).
-- column: Plugin | The plugin the row was read from.
-- example: plugin=SFBGS003.esm
SELECT f.Type, f.Sig, count(*) AS Records, sum(f.IsOverride = 0) AS Own, sum(f.IsOverride = 1) AS Overrides, min(f.EditorID) AS Example, f.Plugin
FROM Forms f
GROUP BY f.Plugin, f.Sig
ORDER BY f.Plugin, Records DESC, f.Type;
