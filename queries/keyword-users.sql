-- name: keyword-users
-- answers: The records of a plugin that carry a keyword, by record type.
-- It looks at the keyword list every record type has (xEdit: KWDA). A keyword used in some other field, such as
-- a recipe's workbench or a modification's attach point, is found by "links-to".
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: keyword | text | needed | The keyword, by EditorID or FormKey.
-- param: type | text | optional | One record type (Weapon) or signature (WEAP): only it is read.
-- scope: plugins
-- reads: every type with Keywords
-- column: FormKey | The record that carries the keyword.
-- column: Type | That record's type.
-- column: EditorID | That record's EditorID.
-- column: Name | That record's name in the game, if it has one.
-- column: Idx | The keyword's place in the record's list, from 0.
-- column: Keywords | How many keywords the record carries in all.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm keyword=WeaponTypeShotgun
SELECT r.FormKey, r.Type, r.EditorID, display(r.Name) AS Name, k.key AS Idx, json_array_length(r.Json, '$.Keywords') AS Keywords, r.Plugin
FROM Records r JOIN json_each(r.Json, '$.Keywords') k
WHERE k.value = formkey(:keyword) COLLATE NOCASE
  AND (:type IS NULL OR r.Sig = :type)
ORDER BY r.Plugin, r.Type, r.EditorID, r.FormKey;
