-- name: kw
-- answers: The keywords on records, one row per keyword, in the record's own order.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: record | text | optional | One record, by EditorID or FormKey.
-- param: pattern | pattern | optional | A part of the records' EditorIDs (* stands for any text).
-- param: type | text | optional | One record type (Weapon, Armor) or signature (WEAP): only it is read.
-- scope: plugins
-- reads: every type with Keywords
-- column: FormKey | The record that carries the keyword.
-- column: Type | That record's type.
-- column: EditorID | That record's EditorID.
-- column: Idx | The keyword's place in the record's list, from 0.
-- column: Keyword | The keyword's FormKey.
-- column: KeywordEditorID | The keyword's EditorID.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm record=Eon
SELECT r.FormKey, r.Type, r.EditorID, k.key AS Idx, k.value AS Keyword, edid(k.value) AS KeywordEditorID, r.Plugin
FROM Records r JOIN json_each(r.Json, '$.Keywords') k
WHERE (:record IS NULL OR r.FormKey = :record OR r.EditorID = :record)
  AND (:pattern IS NULL OR r.EditorID LIKE :pattern ESCAPE '\')
  AND (:type IS NULL OR r.Sig = :type)
ORDER BY r.Plugin, r.Type, r.EditorID, r.FormKey, k.key;
