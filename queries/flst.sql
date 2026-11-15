-- name: flst
-- answers: The FormLists of a plugin with their size, or the items of the lists you name, each with its place, type and name.
-- With no record and no pattern it lists the FormLists. With one of them it lists the items of each list that fits.
-- Places count from 0, as the game's scripts count them.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: record | text | optional | One FormList, by EditorID or FormKey: its items are listed.
-- param: pattern | pattern | optional | A part of the lists' EditorIDs (* stands for any text): the items of every list that fits are listed.
-- scope: plugins
-- column: FormKey | The FormList.
-- column: EditorID | The FormList's EditorID.
-- column: Name | The FormList's name, if it has one.
-- column: Items | How many items the list holds.
-- column: AddsTo | The EditorID of the list this one adds its items to when the game loads (xEdit: ANAM). Empty for most lists.
-- column: IsOverride | 1 when the plugin changes a list of one of its masters, 0 when the list is its own.
-- column: Plugin | The plugin the row was read from.
-- column: List | The EditorID of the list the item is in.
-- column: ListKey | The FormKey of that list.
-- column: Idx | The item's place in the list, from 0.
-- column: Item | The item's FormKey.
-- column: ItemEditorID | The item's EditorID.
-- column: ItemType | The item's record type (Armor, Weapon, Keyword, …). Empty when no plugin in Data has the record.
-- column: ItemName | The item's name in the game, if it has one.
-- example: plugin=Starfield.esm
-- example: plugin=Starfield.esm pattern=Weapon
CREATE TEMP TABLE sizes AS
SELECT FormKey, Plugin, count(*) AS Items FROM FormList_Items GROUP BY FormKey, Plugin;
CREATE INDEX sizes_key ON sizes(FormKey, Plugin);
-- table: FormLists
-- unless: record, pattern
SELECT f.FormKey, f.EditorID, display(f.Name) AS Name, coalesce(s.Items, 0) AS Items, edid(f.AddToList) AS AddsTo, f.IsOverride, f.Plugin
FROM FormList f LEFT JOIN sizes s ON s.FormKey = f.FormKey AND s.Plugin = f.Plugin
ORDER BY f.Plugin, f.EditorID, f.FormKey;
-- table: Items of the lists
-- when: record, pattern
SELECT i.EditorID AS List, i.FormKey AS ListKey, i.Idx, i.Value AS Item, edid(i.Value) AS ItemEditorID, type(i.Value) AS ItemType,
       display(name(i.Value)) AS ItemName, i.Plugin
FROM FormList_Items i
WHERE (:record IS NULL OR i.FormKey = :record OR i.EditorID = :record)
  AND (:pattern IS NULL OR i.EditorID LIKE :pattern ESCAPE '\')
ORDER BY i.Plugin, i.EditorID, i.FormKey, i.Idx;
