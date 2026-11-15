-- name: zero-bounds
-- answers: The weapons and armor of a plugin whose object bounds are all zero.
-- Object bounds (xEdit: OBND) are the box around an item lying in the world. A box of no size can make a dropped
-- item hard to see or pick up. It is not a fault by itself: 943 of the game's own 1,423 weapons and armors have
-- none either. Hold a new item against the vanilla item it was copied from. Only the plugin's own records are
-- listed, not its changes to a master's records.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: pattern | pattern | optional | A part of the items' EditorIDs (* stands for any text).
-- scope: plugins
-- column: Type | Weapon or Armor.
-- column: FormKey | The item.
-- column: EditorID | Its EditorID.
-- column: Name | Its name in the game.
-- column: Mesh | The path of its model file: an item with a model and no bounds is the usual fault.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm
SELECT 'Weapon' AS Type, w.FormKey, w.EditorID, display(w.Name) AS Name, w.Model_File AS Mesh, w.Plugin
FROM Weapon w
WHERE w.IsOverride = 0 AND w.ObjectBounds_First = '0, 0, 0' AND w.ObjectBounds_Second = '0, 0, 0'
  AND (:pattern IS NULL OR w.EditorID LIKE :pattern ESCAPE '\')
UNION ALL
SELECT 'Armor', a.FormKey, a.EditorID, display(a.Name), coalesce(a.WorldModel_Male_File, a.WorldModel_Female_File), a.Plugin
FROM Armor a
WHERE a.IsOverride = 0 AND a.ObjectBounds_First = '0, 0, 0' AND a.ObjectBounds_Second = '0, 0, 0'
  AND (:pattern IS NULL OR a.EditorID LIKE :pattern ESCAPE '\')
ORDER BY Plugin, Type, EditorID, FormKey;
