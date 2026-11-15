-- name: leveled-list
-- answers: What one leveled list gives: each entry with its level, count and chance, as the game uses the list.
-- It reads the copy of the list that wins in the load order. Name plugins to see one plugin's copy instead.
-- An entry whose type is another leveled list hands on to that list: run the query again on it.
-- Leveled lists of items, of NPCs and of pack-ins are covered.
-- param: record | text | needed | The leveled list, by EditorID or FormKey.
-- param: plugin | text | optional | With the plugins scope: the plugin whose copy to read.
-- scope: winners
-- column: List | The leveled list's EditorID.
-- column: ListType | LeveledItem, LeveledNpc or LeveledPackIn.
-- column: Idx | The entry's place in the list, from 0.
-- column: Level | The player level from which the entry counts.
-- column: Item | The FormKey of what the entry gives.
-- column: ItemEditorID | Its EditorID.
-- column: ItemType | Its record type.
-- column: Count | How many it gives.
-- column: ChanceNone | The chance that this entry gives nothing, as a part of 1 (0.15 is 15 in 100).
-- column: Plugin | The plugin the list was read from: the one that wins it.
-- example: record=LL_Weapon_Eon
SELECT e.EditorID AS List, 'LeveledItem' AS ListType, e.Idx, e.Level, e.Reference AS Item, edid(e.Reference) AS ItemEditorID,
       type(e.Reference) AS ItemType, e.Count, e.ChanceNone, e.Plugin
FROM LeveledItem_Entries e WHERE e.FormKey = :record OR e.EditorID = :record
UNION ALL
SELECT e.EditorID, 'LeveledNpc', e.Idx, e.Level, e.Reference, edid(e.Reference), type(e.Reference), e.Count, e.ChanceNone, e.Plugin
FROM LeveledNpc_Entries e WHERE e.FormKey = :record OR e.EditorID = :record
UNION ALL
SELECT e.EditorID, 'LeveledPackIn', e.Idx, e.Level, e.Reference, edid(e.Reference), type(e.Reference), e.Count, e.ChanceNone, e.Plugin
FROM LeveledPackIn_Entries e WHERE e.FormKey = :record OR e.EditorID = :record
ORDER BY ListType, Plugin, Idx;
