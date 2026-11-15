-- name: item-sources
-- answers: Where an item can come from: the leveled lists that give it, the recipes that make it, and the containers, NPCs and outfits that hold it.
-- It follows leveled lists upwards: a list that holds the item, a list that holds that list, and so on, and then
-- every container, NPC inventory and outfit that holds the item or one of those lists.
-- It reads the load order as the game uses it (the winning copy of every record).
-- Not covered: items placed by hand in the world (see "placed"), and items a script gives. A vendor shows as the
-- chest it sells from, a container; which vendor owns which chest is a placed reference and is not followed here.
-- param: record | text | needed | The item, by EditorID or FormKey.
-- param: plugin | text | optional | With the plugins scope: the plugins to look in, instead of the load order.
-- scope: winners
-- column: Source | Leveled list, Recipe, Container, NPC or Outfit.
-- column: Steps | How many leveled lists lie between the item and this source. 0: the source holds or makes the item itself.
-- column: EditorID | The source's EditorID.
-- column: FormKey | The source's FormKey.
-- column: Holds | The EditorID of what the source holds directly: the item, or the leveled list that leads to it.
-- column: Level | For a leveled list: the player level from which the entry counts.
-- column: Count | How many: the entry's count, the recipe's amount, or the count in the inventory.
-- column: Plugin | The plugin the source was read from: the one that wins it.
-- example: record=Eon
CREATE TEMP TABLE lists AS
SELECT FormKey AS List, EditorID AS ListEditorID, Plugin, Reference AS Ref, Level, Count FROM LeveledItem_Entries WHERE Reference IS NOT NULL;
CREATE INDEX lists_ref ON lists(Ref COLLATE NOCASE);
CREATE TEMP TABLE reach AS
WITH RECURSIVE up(Node, Depth) AS (
    SELECT formkey(:record), 0
    UNION
    SELECT l.List, up.Depth + 1 FROM up JOIN lists l ON l.Ref = up.Node COLLATE NOCASE WHERE up.Depth < 12
)
SELECT Node, min(Depth) AS Depth FROM up WHERE Node IS NOT NULL GROUP BY Node;
CREATE INDEX reach_node ON reach(Node COLLATE NOCASE);
SELECT 'Leveled list' AS Source, r.Depth AS Steps, l.ListEditorID AS EditorID, l.List AS FormKey, edid(l.Ref) AS Holds, l.Level, l.Count, l.Plugin
FROM lists l JOIN reach r ON r.Node = l.Ref COLLATE NOCASE
UNION ALL
SELECT 'Recipe', 0, c.EditorID, c.FormKey, edid(c.CreatedObject), NULL, c.AmountProduced, c.Plugin
FROM ConstructibleObject c WHERE c.CreatedObject = formkey(:record)
UNION ALL
SELECT 'Container', r.Depth, i.EditorID, i.FormKey, edid(i.Item_Item), NULL, i.Item_Count, i.Plugin
FROM Container_Items i JOIN reach r ON r.Node = i.Item_Item COLLATE NOCASE
UNION ALL
SELECT 'NPC', r.Depth, i.EditorID, i.FormKey, edid(i.Item_Item), NULL, i.Item_Count, i.Plugin
FROM Npc_Items i JOIN reach r ON r.Node = i.Item_Item COLLATE NOCASE
UNION ALL
SELECT 'Outfit', r.Depth, i.EditorID, i.FormKey, edid(i.Value), NULL, 1, i.Plugin
FROM Outfit_Items i JOIN reach r ON r.Node = i.Value COLLATE NOCASE
ORDER BY Steps, Source, EditorID, FormKey, Level;
