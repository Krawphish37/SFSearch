-- name: omod
-- answers: The object modifications (OMODs) of a plugin: where each attaches, what it may go on, what it holds, and its mesh.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: record | text | optional | One modification, by EditorID or FormKey.
-- param: pattern | pattern | optional | A part of the modifications' EditorIDs (* stands for any text).
-- scope: plugins
-- column: FormKey | The modification.
-- column: Type | Its kind: WeaponModification, ArmorModification, ObjectModification, …
-- column: EditorID | Its EditorID.
-- column: Name | Its name in the game.
-- column: AttachPoint | The EditorID of the attach point it goes on (a keyword; xEdit: "Attach Point").
-- column: TargetKeywords | The EditorIDs of the keywords an item must carry for the modification to fit (xEdit: MNAM).
-- column: Includes | How many other modifications it brings along.
-- column: Properties | How many property changes it makes.
-- column: Mesh | The path of its model file. Empty when it has no model.
-- column: IsOverride | 1 when the plugin changes a modification of one of its masters.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm pattern=mod_Eon
CREATE TEMP TABLE targets AS
SELECT FormKey, Plugin, group_concat(coalesce(edid(Value), Value), ', ' ORDER BY Idx) AS TargetKeywords
FROM ObjectModification_TargetOmodKeywords GROUP BY FormKey, Plugin;
CREATE INDEX targets_key ON targets(FormKey, Plugin);
CREATE TEMP TABLE incs AS
SELECT FormKey, Plugin, count(*) AS N FROM ObjectModification_Includes GROUP BY FormKey, Plugin;
CREATE INDEX incs_key ON incs(FormKey, Plugin);
CREATE TEMP TABLE props AS
SELECT FormKey, Plugin, count(*) AS N FROM ObjectModification_Properties GROUP BY FormKey, Plugin;
CREATE INDEX props_key ON props(FormKey, Plugin);
SELECT o.FormKey, o.Type, o.EditorID, display(o.Name) AS Name, edid(o.AttachPoint) AS AttachPoint, t.TargetKeywords,
       coalesce(i.N, 0) AS Includes, coalesce(p.N, 0) AS Properties, o.Model_File AS Mesh, o.IsOverride, o.Plugin
FROM ObjectModification o
LEFT JOIN targets t ON t.FormKey = o.FormKey AND t.Plugin = o.Plugin
LEFT JOIN incs i ON i.FormKey = o.FormKey AND i.Plugin = o.Plugin
LEFT JOIN props p ON p.FormKey = o.FormKey AND p.Plugin = o.Plugin
WHERE (:record IS NULL OR o.FormKey = :record OR o.EditorID = :record)
  AND (:pattern IS NULL OR o.EditorID LIKE :pattern ESCAPE '\')
ORDER BY o.Plugin, o.EditorID, o.FormKey;
