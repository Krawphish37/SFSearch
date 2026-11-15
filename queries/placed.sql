-- name: placed
-- answers: Where a plugin places an object or an NPC in the world: each placed copy with the cell it stands in.
-- Give the thing that is placed (a container, an activator, a weapon, an NPC): the answer lists every placed
-- reference to it in the plugin. In a big plugin the placed objects are many (Starfield.esm has 3.3 million):
-- the query then answers with its cost first and asks for heavy work to be allowed.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: record | text | needed | The thing placed, by EditorID or FormKey.
-- scope: plugins
-- column: Ref | The placed reference's FormKey.
-- column: RefEditorID | The placed reference's EditorID. Most have none.
-- column: Kind | PlacedObject or PlacedNpc.
-- column: Base | The EditorID of the thing placed.
-- column: Cell | The EditorID of the cell it stands in.
-- column: CellKey | That cell's FormKey.
-- column: IsOverride | 1 when the plugin changes a placed reference of one of its masters.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=SFBGS003.esm record=MacKitLgBase09
SELECT p.FormKey AS Ref, p.EditorID AS RefEditorID, 'PlacedObject' AS Kind, edid(p.Base) AS Base, edid(f.Parent) AS Cell, f.Parent AS CellKey, p.IsOverride, p.Plugin
FROM PlacedObject p LEFT JOIN Forms f ON f.FormKey = p.FormKey AND f.Plugin = p.Plugin
WHERE p.Base = formkey(:record)
UNION ALL
SELECT p.FormKey, p.EditorID, 'PlacedNpc', edid(p.Base), edid(f.Parent), f.Parent, p.IsOverride, p.Plugin
FROM PlacedNpc p LEFT JOIN Forms f ON f.FormKey = p.FormKey AND f.Plugin = p.Plugin
WHERE p.Base = formkey(:record)
ORDER BY Kind, Cell, Ref;
