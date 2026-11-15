-- name: arma
-- answers: The armors of a plugin with their armor addons: the male and female meshes and the body slots of each.
-- An armor names one or more armor addons (xEdit: "Armatures"); the addon holds the meshes. The addon is read from
-- the plugin itself when it has it, otherwise from the plugin that made it, which is why the plugin's masters are
-- read too. An override of the addon by some other plugin is not looked at.
-- param: plugin | text | needed | The plugin to read, by file name. One plugin: its masters are read with it.
-- param: record | text | optional | One armor, by EditorID or FormKey.
-- param: pattern | pattern | optional | A part of the armors' EditorIDs (* stands for any text).
-- scope: plugins with masters
-- column: Armor | The armor's FormKey.
-- column: EditorID | The armor's EditorID.
-- column: Idx | The addon's place in the armor's list, from 0.
-- column: Addon | The armor addon's FormKey.
-- column: AddonEditorID | The armor addon's EditorID.
-- column: MaleMesh | The path of the male world model.
-- column: FemaleMesh | The path of the female world model.
-- column: AddonSlots | The body slots the addon covers (xEdit: "First Person Flags").
-- column: ArmorSlots | The body slots the armor itself names.
-- column: AddonFrom | The plugin the addon was read from. Empty when neither the plugin nor its masters have the addon.
-- example: plugin=Starfield.esm pattern=Spacesuit_Constellation
CREATE TEMP TABLE addons AS
SELECT FormKey, Plugin, IsOverride, WorldModel_Male_File AS Male, WorldModel_Female_File AS Female, FirstPersonFlags AS Slots FROM ArmorAddon;
CREATE INDEX addons_key ON addons(FormKey COLLATE NOCASE);
SELECT a.FormKey AS Armor, a.EditorID, t.Idx, t.ArmorAddon AS Addon, edid(t.ArmorAddon) AS AddonEditorID,
       d.Male AS MaleMesh, d.Female AS FemaleMesh,
       replace(replace(replace(replace(d.Slots, '["', ''), '"]', ''), '","', ', '), '[]', '') AS AddonSlots,
       replace(replace(replace(replace(a.FirstPersonFlags, '["', ''), '"]', ''), '","', ', '), '[]', '') AS ArmorSlots,
       d.Plugin AS AddonFrom
FROM Armor a
JOIN Armor_Armatures t ON t.FormKey = a.FormKey AND t.Plugin = a.Plugin
LEFT JOIN addons d ON d.rowid = (SELECT x.rowid FROM addons x WHERE x.FormKey = t.ArmorAddon COLLATE NOCASE ORDER BY (x.Plugin = :plugin COLLATE NOCASE) DESC, x.IsOverride LIMIT 1)
WHERE a.Plugin = :plugin
  AND (:record IS NULL OR a.FormKey = :record OR a.EditorID = :record)
  AND (:pattern IS NULL OR a.EditorID LIKE :pattern ESCAPE '\')
ORDER BY a.EditorID, a.FormKey, t.Idx;
