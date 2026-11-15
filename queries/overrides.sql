-- name: overrides
-- answers: The records of its masters that a plugin changes, with the plugin each record belongs to and the plugin that wins it.
-- It is read from the record headers, so every record type is listed, placed objects and cells too, and nothing
-- of the records is opened. It says that the plugin holds its own copy of the record, not what it changed in it.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: type | text | optional | One record type (Weapon) or signature (WEAP).
-- param: pattern | pattern | optional | A part of the records' EditorIDs (* stands for any text).
-- scope: plugins
-- column: FormKey | The record.
-- column: Type | Its record type.
-- column: Sig | Its 4-letter signature.
-- column: EditorID | Its EditorID, as this plugin has it.
-- column: Origin | The plugin the record belongs to (the master that made it).
-- column: Winner | The loaded plugin whose copy the game uses. Empty when no loaded plugin has the record.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=ShatteredSpace.esm type=Npc
SELECT f.FormKey, f.Type, f.Sig, f.EditorID, substr(f.FormKey, instr(f.FormKey, ':') + 1) AS Origin, winner(f.FormKey) AS Winner, f.Plugin
FROM Forms f
WHERE f.IsOverride = 1
  AND (:type IS NULL OR f.Sig = :type)
  AND (:pattern IS NULL OR f.EditorID LIKE :pattern ESCAPE '\')
ORDER BY f.Plugin, f.Type, f.EditorID, f.FormKey;
