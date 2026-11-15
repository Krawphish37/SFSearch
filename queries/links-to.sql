-- name: links-to
-- answers: What in a plugin points at a record: every record of the plugin that names it, and in which field.
-- Every field of every record that points at another record is looked at, script properties included.
-- It searches the plugins named, not the whole Data folder: "refs" does that. Record types that are too big to
-- read whole (the placed objects and cells of a big plugin) are left out, and the answer says which.
-- param: plugin | text | needed | The plugin to search, by file name. Several with commas.
-- param: target | text | needed | The record pointed at, by EditorID or FormKey.
-- param: type | text | optional | One record type (FormList) or signature (FLST): only it is searched.
-- scope: plugins
-- reads: every type
-- column: FormKey | The record that points at it.
-- column: Type | That record's type.
-- column: EditorID | That record's EditorID.
-- column: Field | Where in the record the link is (Items[36], VirtualMachineAdapter.Scripts[0].Properties[17].Object).
-- column: Target | The EditorID of the record pointed at.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=SFBGS003.esm target=SFTA00
SELECT l.FromKey AS FormKey, l.FromType AS Type, edid(l.FromKey) AS EditorID, l.Path AS Field, edid(l.ToKey) AS Target, l.Plugin
FROM Links l
WHERE l.ToKey = formkey(:target)
  AND (:type IS NULL OR sig(l.FromKey) = :type)
ORDER BY l.Plugin, l.FromType, EditorID, l.FromKey, l.Path;
