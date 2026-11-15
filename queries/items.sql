-- name: items
-- answers: The weapons and armor of a plugin that a player can use, with their group, damage or protection, value and weight.
-- Which records count as usable is decided by general rules only, the ones the game itself goes by:
-- a weapon must have a name, an equipment type and damage above 0, and must not be marked non-playable
-- (its own flag, or the record flag);
-- an armor must have a name, must not be marked non-playable (the record flag), and must be made for humans
-- (no race, or HumanRace: armor for another race is a creature's skin).
-- Words in an EditorID (NOTPLAYABLE, Skin_, a mod's own prefixes) are not a rule here: they differ from mod to mod.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: pattern | pattern | optional | A part of the items' EditorIDs (* stands for any text).
-- scope: plugins
-- column: FormKey | The item.
-- column: EditorID | Its EditorID.
-- column: Name | Its name in the game.
-- column: Group | For a weapon: its weapon-type keywords (Ranged, Ballistic, Pistol, …). For an armor: Spacesuit, Helmet, Pack, Apparel, Headwear or Neuroamp by the body slot it covers, otherwise Accessory.
-- column: Damage | A weapon's damage: its physical damage plus every other damage type it has.
-- column: DamageTypes | The parts of that damage, as "type amount".
-- column: Ammo | The EditorID of the weapon's ammunition.
-- column: Protection | An armor's resistances, as "type amount".
-- column: Value | The price in credits before any modification.
-- column: Weight | The weight (mass) before any modification.
-- column: IsOverride | 1 when the plugin changes an item of one of its masters.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm
CREATE TEMP TABLE wgroups AS
SELECT FormKey, Plugin, group_concat(substr(edid(Value), 11), ', ' ORDER BY Idx) AS Grp
FROM Weapon_Keywords WHERE edid(Value) LIKE 'WeaponType%' GROUP BY FormKey, Plugin;
CREATE INDEX wgroups_key ON wgroups(FormKey, Plugin);
CREATE TEMP TABLE wdamage AS
SELECT FormKey, Plugin, sum(Value) AS Total, group_concat(coalesce(edid(DamageType), DamageType) || ' ' || Value, ', ' ORDER BY Idx) AS Parts
FROM Weapon_DamageTypes GROUP BY FormKey, Plugin;
CREATE INDEX wdamage_key ON wdamage(FormKey, Plugin);
CREATE TEMP TABLE aresist AS
SELECT FormKey, Plugin, group_concat(coalesce(edid(DamageType), DamageType) || ' ' || Value, ', ' ORDER BY Idx) AS Parts
FROM Armor_Resistances GROUP BY FormKey, Plugin;
CREATE INDEX aresist_key ON aresist(FormKey, Plugin);
-- table: Weapons
SELECT w.FormKey, w.EditorID, display(w.Name) AS Name, g.Grp AS "Group", w.AttackDamage + coalesce(d.Total, 0) AS Damage,
       trim(CASE WHEN w.AttackDamage > 0 THEN 'Physical ' || w.AttackDamage ELSE '' END || coalesce(CASE WHEN w.AttackDamage > 0 THEN ', ' ELSE '' END || d.Parts, '')) AS DamageTypes,
       edid(w.AmmoType) AS Ammo, w.BaseValue AS Value, w.BaseWeight AS Weight, w.IsOverride, w.Plugin
FROM Weapon w
LEFT JOIN wgroups g ON g.FormKey = w.FormKey AND g.Plugin = w.Plugin
LEFT JOIN wdamage d ON d.FormKey = w.FormKey AND d.Plugin = w.Plugin
WHERE w.NonPlayable = 0 AND w.StarfieldMajorRecordFlags NOT LIKE '%"NotPlayable"%'
  AND coalesce(w.Name, '') <> '' AND w.EquipmentType IS NOT NULL
  AND w.AttackDamage + coalesce(d.Total, 0) > 0
  AND (:pattern IS NULL OR w.EditorID LIKE :pattern ESCAPE '\')
ORDER BY w.Plugin, w.EditorID, w.FormKey;
-- table: Armor
SELECT a.FormKey, a.EditorID, display(a.Name) AS Name,
       CASE WHEN a.FirstPersonFlags LIKE '%"SSBODY"%' THEN 'Spacesuit' WHEN a.FirstPersonFlags LIKE '%"SSHelmet"%' THEN 'Helmet'
            WHEN a.FirstPersonFlags LIKE '%"SSBackpack"%' THEN 'Pack' WHEN a.FirstPersonFlags LIKE '%"Neuroamp"%' THEN 'Neuroamp'
            WHEN a.FirstPersonFlags LIKE '%"BODY"%' THEN 'Apparel' WHEN a.FirstPersonFlags LIKE '%"Hat"%' THEN 'Headwear' ELSE 'Accessory' END AS "Group",
       r.Parts AS Protection, a.Value, a.Weight, a.IsOverride, a.Plugin
FROM Armor a
LEFT JOIN aresist r ON r.FormKey = a.FormKey AND r.Plugin = a.Plugin
WHERE a.StarfieldMajorRecordFlags NOT LIKE '%"NotPlayable"%'
  AND coalesce(a.Name, '') <> ''
  AND (a.Race IS NULL OR edid(a.Race) = 'HumanRace')
  AND (:pattern IS NULL OR a.EditorID LIKE :pattern ESCAPE '\')
ORDER BY a.Plugin, a.EditorID, a.FormKey;
