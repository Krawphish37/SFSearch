-- name: broken-links
-- answers: The links in a plugin's records that point at a record no plugin in the Data folder has.
-- Every field of every record that points at another record is looked at. A link is broken when the plugin it
-- points into isn't in Data, or is there and has no record with that ID. In the game such a link reads as empty.
-- A link into Starfield.esm with an ID below 000800 may be a form the game has built in (the player, for one):
-- those are left out unless all=1 asks for them, and are then marked.
-- Record types that are too big to read whole (the placed objects and cells of a big plugin) are left out, and the
-- answer says which.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: type | text | optional | One record type (Weapon) or signature (WEAP): only it is read.
-- param: all | number | optional, default 0 | 1 lists the links into Starfield.esm with an ID below 000800 too.
-- scope: plugins
-- reads: every type
-- column: FormKey | The record that holds the link.
-- column: Type | That record's type.
-- column: EditorID | That record's EditorID.
-- column: Field | Where in the record the link is (ObjectTemplates[0].Includes[2].Mod).
-- column: Target | The FormKey the link points at.
-- column: TargetPlugin | The plugin that FormKey belongs to.
-- column: Remark | "may be built into the game" for a low ID of Starfield.esm; otherwise empty.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=SFBGS003.esm all=1
SELECT l.FromKey AS FormKey, l.FromType AS Type, edid(l.FromKey) AS EditorID, l.Path AS Field, l.ToKey AS Target,
       substr(l.ToKey, instr(l.ToKey, ':') + 1) AS TargetPlugin,
       CASE WHEN lower(substr(l.ToKey, instr(l.ToKey, ':') + 1)) = 'starfield.esm' AND substr(l.ToKey, 1, instr(l.ToKey, ':') - 1) < '000800'
            THEN 'may be built into the game' ELSE '' END AS Remark,
       l.Plugin
FROM Links l
WHERE versions(l.ToKey) IS NULL
  AND (:type IS NULL OR sig(l.FromKey) = :type)
  AND (:all = 1 OR NOT (lower(substr(l.ToKey, instr(l.ToKey, ':') + 1)) = 'starfield.esm' AND substr(l.ToKey, 1, instr(l.ToKey, ':') - 1) < '000800'))
ORDER BY l.Plugin, l.FromType, EditorID, l.FromKey, l.Path;
