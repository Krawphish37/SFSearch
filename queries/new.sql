-- name: new
-- answers: The records a plugin adds: the ones that are its own, not changes to a master's record.
-- It is read from the record headers, so every record type is listed, placed objects and cells too, and nothing
-- of the records is opened. That is why there is no name column: name(FormKey) in a query of your own gives it.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: type | text | optional | One record type (Weapon) or signature (WEAP).
-- param: pattern | pattern | optional | A part of the records' EditorIDs (* stands for any text).
-- scope: plugins
-- column: FormKey | The record.
-- column: Type | Its record type.
-- column: Sig | Its 4-letter signature.
-- column: EditorID | Its EditorID. Many placed objects have none.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm type=Weapon
SELECT f.FormKey, f.Type, f.Sig, f.EditorID, f.Plugin
FROM Forms f
WHERE f.IsOverride = 0
  AND (:type IS NULL OR f.Sig = :type)
  AND (:pattern IS NULL OR f.EditorID LIKE :pattern ESCAPE '\')
ORDER BY f.Plugin, f.Type, f.EditorID, f.FormKey;
