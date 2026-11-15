-- name: templates
-- answers: The object templates of weapons, armor and other records: each template with the modifications (OMODs) it puts on.
-- A template is one ready-made build of the item. The one marked IsDefault is what the item is when nothing else is said.
-- With a record or a pattern, a second table lists each modification of each template on its own row.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: record | text | optional | One record, by EditorID or FormKey.
-- param: pattern | pattern | optional | A part of the records' EditorIDs (* stands for any text).
-- param: type | text | optional | One record type (Weapon, Armor) or signature (WEAP): only it is read.
-- scope: plugins
-- reads: every type with ObjectTemplates
-- column: FormKey | The record that has the template.
-- column: Type | That record's type.
-- column: EditorID | That record's EditorID.
-- column: Template | The template's place in the record's list, from 0.
-- column: Name | The template's name.
-- column: IsDefault | 1 for the default template.
-- column: EditorOnly | 1 for a template the game doesn't use (only the editor shows it).
-- column: MinLevel | The lowest player level the template is used at.
-- column: MaxLevel | The highest player level the template is used at; 0 means no limit.
-- column: Mods | How many modifications the template puts on.
-- column: ModEditorIDs | Their EditorIDs, in order. A FormKey stands where no plugin in Data has the record.
-- column: Keywords | The EditorIDs of the keywords the template adds.
-- column: Plugin | The plugin the row was read from.
-- column: Idx | The modification's place in the template, from 0.
-- column: Mod | The modification's FormKey.
-- column: ModEditorID | The modification's EditorID.
-- column: AttachPointIndex | The place of the attach point the modification goes on.
-- column: Optional | 1 when the modification may be left off.
-- column: DontUseAll | 1 when, for a group of modifications, only one of them is picked.
-- example: plugin=Starfield.esm record=Eon
-- table: Templates
SELECT r.FormKey, r.Type, r.EditorID, t.key AS Template, display(text(t.value -> '$.Name')) AS Name,
       coalesce(t.value ->> '$.Default', 0) AS IsDefault, coalesce(t.value ->> '$.IsEditorOnly', 0) AS EditorOnly,
       coalesce(t.value ->> '$.LevelMin', 0) AS MinLevel, coalesce(t.value ->> '$.LevelMax', 0) AS MaxLevel,
       coalesce(json_array_length(t.value, '$.Includes'), 0) AS Mods,
       (SELECT group_concat(coalesce(edid(i.value ->> '$.Mod'), i.value ->> '$.Mod'), ', ' ORDER BY i.key) FROM json_each(t.value, '$.Includes') i) AS ModEditorIDs,
       (SELECT group_concat(coalesce(edid(k.value), k.value), ', ' ORDER BY k.key) FROM json_each(t.value, '$.Keywords') k) AS Keywords,
       r.Plugin
FROM Records r JOIN json_each(r.Json, '$.ObjectTemplates') t
WHERE (:record IS NULL OR r.FormKey = :record OR r.EditorID = :record)
  AND (:pattern IS NULL OR r.EditorID LIKE :pattern ESCAPE '\')
  AND (:type IS NULL OR r.Sig = :type)
ORDER BY r.Plugin, r.Type, r.EditorID, r.FormKey, t.key;
-- table: The modifications of each template
-- when: record, pattern
SELECT r.FormKey, r.EditorID, t.key AS Template, i.key AS Idx, i.value ->> '$.Mod' AS Mod, edid(i.value ->> '$.Mod') AS ModEditorID,
       coalesce(i.value ->> '$.AttachPointIndex', 0) AS AttachPointIndex, coalesce(i.value ->> '$.Optional', 0) AS Optional,
       coalesce(i.value ->> '$.DontUseAll', 0) AS DontUseAll, r.Plugin
FROM Records r JOIN json_each(r.Json, '$.ObjectTemplates') t JOIN json_each(t.value, '$.Includes') i
WHERE (:record IS NULL OR r.FormKey = :record OR r.EditorID = :record)
  AND (:pattern IS NULL OR r.EditorID LIKE :pattern ESCAPE '\')
  AND (:type IS NULL OR r.Sig = :type)
ORDER BY r.Plugin, r.Type, r.EditorID, r.FormKey, t.key, i.key;
