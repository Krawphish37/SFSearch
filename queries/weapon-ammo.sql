-- name: weapon-ammo
-- answers: The weapons of a plugin with what they fire: ammunition, magazine size, the projectile they override, and their damage.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: pattern | pattern | optional | A part of the weapons' EditorIDs (* stands for any text).
-- param: ammo | pattern | optional | A part of the ammunition's EditorID.
-- scope: plugins
-- column: FormKey | The weapon.
-- column: EditorID | Its EditorID.
-- column: Name | Its name in the game.
-- column: Ammo | The EditorID of its ammunition. Empty for a melee weapon.
-- column: AmmoKey | The ammunition's FormKey.
-- column: Capacity | How many rounds the magazine holds.
-- column: AmmoList | The EditorID of the leveled list NPCs get ammunition from.
-- column: Projectile | The EditorID of the projectile the weapon fires instead of the ammunition's own. Empty when it uses the ammunition's.
-- column: Projectiles | How many projectiles one shot fires.
-- column: Damage | The physical damage of one hit.
-- column: IsOverride | 1 when the plugin changes a weapon of one of its masters.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm ammo=777mm
SELECT w.FormKey, w.EditorID, display(w.Name) AS Name, edid(w.AmmoType) AS Ammo, w.AmmoType AS AmmoKey, w.AmmoCapacity AS Capacity, edid(w.AmmoList) AS AmmoList,
       edid(w.OverrideProjectile) AS Projectile, w.ProjectilesCount AS Projectiles, w.AttackDamage AS Damage, w.IsOverride, w.Plugin
FROM Weapon w
WHERE (:pattern IS NULL OR w.EditorID LIKE :pattern ESCAPE '\')
  AND (:ammo IS NULL OR edid(w.AmmoType) LIKE :ammo ESCAPE '\')
ORDER BY w.Plugin, w.EditorID, w.FormKey;
