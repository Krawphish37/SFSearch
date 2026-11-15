-- name: multi-edits
-- answers: The records that several loaded plugins change, by record type: where mods may be stepping on each other.
-- It reads every copy of every record in the load order. "Changes" means the plugin holds its own copy of a
-- record that belongs to another plugin; whether the copies differ is a question for "compare".
-- Without a type it counts such records for every record type. That needs the whole load order's record headers
-- in a table (about 9 million rows, about 15 s the first time), so the first run answers with its cost and asks
-- for heavy work to be allowed. With a type it lists that type's records, and reads only that type.
-- param: type | text | optional | One record type (Weapon) or signature (WEAP): its records are listed.
-- param: min | number | optional, default 2 | The least number of plugins that change the record.
-- param: pattern | pattern | optional | With a type: a part of the records' EditorIDs (* stands for any text).
-- scope: versions
-- reads: every type
-- column: Type | The record type.
-- column: Records | How many records of the type at least "min" plugins change.
-- column: MostEdits | The largest number of plugins that change one record of the type.
-- column: Example | The record of the type that the most plugins change, as EditorID (FormKey).
-- column: FormKey | The record.
-- column: EditorID | Its EditorID, as the winning copy has it.
-- column: Edits | How many loaded plugins change it.
-- column: Chain | Every loaded plugin that holds the record, in load order: the plugin it belongs to first, the winner last.
-- column: Winner | The plugin whose copy the game uses.
-- example: type=Weapon
-- example: type=LeveledItem min=3
-- unless: type
CREATE TEMP TABLE edits AS
SELECT f.FormKey, max(f.Type) AS Type, count(*) AS Edits FROM Forms f WHERE f.IsOverride = 1 GROUP BY f.FormKey HAVING count(*) >= :min;
-- table: Records several plugins change, by type
-- unless: type
SELECT e.Type, count(*) AS Records, max(e.Edits) AS MostEdits,
       max(CASE WHEN e.Place = 1 THEN coalesce(edid(e.FormKey), '(no EditorID)') || ' (' || e.FormKey || ')' END) AS Example
FROM (SELECT FormKey, Type, Edits, row_number() OVER (PARTITION BY Type ORDER BY Edits DESC, FormKey) AS Place FROM edits) e
GROUP BY e.Type
ORDER BY Records DESC, e.Type;
-- table: Records of the type that several plugins change
-- when: type
SELECT r.FormKey, max(CASE WHEN r.IsWinner = 1 THEN r.EditorID END) AS EditorID, sum(r.IsOverride) AS Edits,
       group_concat(r.Plugin, ' < ' ORDER BY r.LoadOrder) AS Chain, max(CASE WHEN r.IsWinner = 1 THEN r.Plugin END) AS Winner
FROM Records r
WHERE r.Sig = :type
GROUP BY r.FormKey
HAVING sum(r.IsOverride) >= :min AND (:pattern IS NULL OR max(CASE WHEN r.IsWinner = 1 THEN r.EditorID END) LIKE :pattern ESCAPE '\')
ORDER BY Edits DESC, EditorID, r.FormKey;
