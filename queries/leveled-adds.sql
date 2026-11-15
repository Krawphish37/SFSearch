-- name: leveled-adds
-- answers: What a plugin adds to leveled lists: the entries of its own new lists, and the entries it adds to lists of its masters.
-- A list of a master that the plugin changes is held against the list as the plugin that made it has it: an entry
-- (the same item, level and count) that the plugin has more often than the original counts as added. Edits that
-- other plugins make to the same list are not looked at; "multi-edits" shows who else changes a list.
-- Leveled lists of items, of NPCs and of pack-ins are covered.
-- param: plugin | text | needed | The plugin to read, by file name. One plugin: its masters are read with it.
-- param: pattern | pattern | optional | A part of the lists' EditorIDs (* stands for any text).
-- scope: plugins with masters
-- column: List | The leveled list's EditorID.
-- column: ListKey | The leveled list's FormKey.
-- column: ListType | LeveledItem, LeveledNpc or LeveledPackIn.
-- column: Kind | "new list" for a list the plugin makes, "added" for an entry added to a master's list.
-- column: Item | The FormKey of what the entry gives.
-- column: ItemEditorID | Its EditorID.
-- column: ItemType | Its record type. Another leveled list here means the entry hands on to that list.
-- column: Level | The player level from which the entry counts.
-- column: Count | How many it gives.
-- column: Times | How many such entries were added (the same item, level and count can be in a list more than once).
-- example: plugin=SFBGS003.esm
CREATE TEMP TABLE entries AS
SELECT 'LeveledItem' AS ListType, e.FormKey, e.EditorID, e.Plugin, l.IsOverride, e.Reference, e.Level, e.Count, count(*) AS N
FROM LeveledItem_Entries e JOIN LeveledItem l ON l.FormKey = e.FormKey AND l.Plugin = e.Plugin
GROUP BY e.FormKey, e.Plugin, e.Reference, e.Level, e.Count
UNION ALL
SELECT 'LeveledNpc', e.FormKey, e.EditorID, e.Plugin, l.IsOverride, e.Reference, e.Level, e.Count, count(*)
FROM LeveledNpc_Entries e JOIN LeveledNpc l ON l.FormKey = e.FormKey AND l.Plugin = e.Plugin
GROUP BY e.FormKey, e.Plugin, e.Reference, e.Level, e.Count
UNION ALL
SELECT 'LeveledPackIn', e.FormKey, e.EditorID, e.Plugin, l.IsOverride, e.Reference, e.Level, e.Count, count(*)
FROM LeveledPackIn_Entries e JOIN LeveledPackIn l ON l.FormKey = e.FormKey AND l.Plugin = e.Plugin
GROUP BY e.FormKey, e.Plugin, e.Reference, e.Level, e.Count;
CREATE INDEX entries_key ON entries(FormKey COLLATE NOCASE, IsOverride);
SELECT m.EditorID AS List, m.FormKey AS ListKey, m.ListType, CASE WHEN m.IsOverride = 0 THEN 'new list' ELSE 'added' END AS Kind,
       m.Reference AS Item, edid(m.Reference) AS ItemEditorID, type(m.Reference) AS ItemType, m.Level, m.Count,
       m.N - coalesce(b.N, 0) AS Times
FROM entries m
LEFT JOIN entries b ON b.FormKey = m.FormKey COLLATE NOCASE AND b.IsOverride = 0 AND b.Plugin <> m.Plugin AND b.ListType = m.ListType
                   AND b.Reference IS m.Reference COLLATE NOCASE AND b.Level = m.Level AND b.Count = m.Count
WHERE m.Plugin = :plugin COLLATE NOCASE AND m.N > coalesce(b.N, 0)
  AND (:pattern IS NULL OR m.EditorID LIKE :pattern ESCAPE '\')
ORDER BY m.ListType, m.EditorID, m.FormKey, m.Level, ItemEditorID, m.Reference, m.Count;
