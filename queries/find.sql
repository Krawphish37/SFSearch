-- name: find
-- answers: The records of a plugin whose EditorID holds a text, of any record type or of one.
-- It is read from the record headers, so every record type is searched, placed objects and cells too, and nothing
-- of the records is opened. The commonest question asked of SQL: "which records are named like this?"
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: pattern | pattern | needed | A part of the EditorID (* stands for any text, ? for one character).
-- param: type | text | optional | One record type (Weapon) or signature (WEAP).
-- scope: plugins
-- column: FormKey | The record.
-- column: Type | Its record type.
-- column: EditorID | Its EditorID.
-- column: IsOverride | 1 when the plugin changes a record of one of its masters, 0 when the record is its own.
-- column: Winner | The loaded plugin whose copy the game uses. Empty when no loaded plugin has the record.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm pattern=Eon
-- example: plugin=Starfield.esm pattern=mod_*_Receiver type=OMOD
SELECT f.FormKey, f.Type, f.EditorID, f.IsOverride, winner(f.FormKey) AS Winner, f.Plugin
FROM Forms f
WHERE f.EditorID LIKE :pattern ESCAPE '\'
  AND (:type IS NULL OR f.Sig = :type)
ORDER BY f.Plugin, f.Type, f.EditorID, f.FormKey;
