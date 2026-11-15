-- name: appr
-- answers: The attach points of records: the slots a weapon, an armor or a modification offers for parts to go on.
-- One row per slot (xEdit: APPR, "Attach Parent Slots"). Each slot is a keyword.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: record | text | optional | One record, by EditorID or FormKey.
-- param: pattern | pattern | optional | A part of the records' EditorIDs (* stands for any text).
-- param: type | text | optional | One record type (Weapon, Armor) or signature (WEAP): only it is read.
-- scope: plugins
-- reads: every type with AttachParentSlots
-- column: FormKey | The record that has the slot.
-- column: Type | That record's type.
-- column: EditorID | That record's EditorID.
-- column: Idx | The slot's place in the record's list, from 0.
-- column: Slot | The slot keyword's FormKey.
-- column: SlotEditorID | The slot keyword's EditorID.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm type=Weapon pattern=Beowulf
SELECT r.FormKey, r.Type, r.EditorID, s.key AS Idx, s.value AS Slot, edid(s.value) AS SlotEditorID, r.Plugin
FROM Records r JOIN json_each(r.Json, '$.AttachParentSlots') s
WHERE (:record IS NULL OR r.FormKey = :record OR r.EditorID = :record)
  AND (:pattern IS NULL OR r.EditorID LIKE :pattern ESCAPE '\')
  AND (:type IS NULL OR r.Sig = :type)
ORDER BY r.Plugin, r.Type, r.EditorID, r.FormKey, s.key;
