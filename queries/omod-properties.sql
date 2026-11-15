-- name: omod-properties
-- answers: What object modifications (OMODs) change: each property with how it is changed and by how much, and the modifications each brings along.
-- One row per property change (xEdit: the "Properties" of the OMOD's DATA). A second table lists the other
-- modifications each one includes (a modification group, or a "tiers" modification that picks one by level).
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: record | text | optional | One modification, by EditorID or FormKey.
-- param: pattern | pattern | optional | A part of the modifications' EditorIDs (* stands for any text).
-- scope: plugins
-- column: FormKey | The modification.
-- column: Type | Its kind: WeaponModification, ArmorModification, …
-- column: EditorID | Its EditorID.
-- column: Idx | The property's (or the included modification's) place in the list, from 0.
-- column: Property | The property that is changed (Mutagen's name for it).
-- column: Function | How it is changed: Set, Add, MultAndAdd (multiply, then add) or Remove.
-- column: Kind | What the value is: Float, Int, Bool, Enum, or FormLinkInt and FormLinkFloat for a record with a number.
-- column: Value | The first value: the amount. 0 when the plugin leaves it out.
-- column: Value2 | The second value, where the function takes two.
-- column: Record | For a property that holds a record: its EditorID, or its FormKey when no plugin in Data has it.
-- column: Step | The step size, for properties that scale.
-- column: Mod | The included modification's EditorID.
-- column: ModKey | The included modification's FormKey.
-- column: MinLevel | The lowest level at which the included modification is picked.
-- column: Optional | 1 when the included modification may be left off.
-- column: DoNotUseAll | 1 when only one of the included modifications is picked.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm pattern=mod_Eon_Barrel
-- table: Property changes
SELECT o.FormKey, o.Type, o.EditorID, p.Idx, p.Property, p.FunctionType AS Function,
       replace(replace(substr(p.ObjectType, 1, instr(p.ObjectType || '<', '<') - 1), 'ObjectMod', ''), 'Property', '') AS Kind,
       p.Value, p.Value2, coalesce(edid(p.Record), p.Record) AS Record, p.Step, o.Plugin
FROM ObjectModification o JOIN ObjectModification_Properties p ON p.FormKey = o.FormKey AND p.Plugin = o.Plugin
WHERE (:record IS NULL OR o.FormKey = :record OR o.EditorID = :record)
  AND (:pattern IS NULL OR o.EditorID LIKE :pattern ESCAPE '\')
ORDER BY o.Plugin, o.EditorID, o.FormKey, p.Idx;
-- table: Modifications each one brings along
SELECT o.FormKey, o.Type, o.EditorID, i.Idx, coalesce(edid(i.Mod), i.Mod) AS Mod, i.Mod AS ModKey, i.MinimumLevel AS MinLevel, i.Optional, i.DoNotUseAll, o.Plugin
FROM ObjectModification o JOIN ObjectModification_Includes i ON i.FormKey = o.FormKey AND i.Plugin = o.Plugin
WHERE (:record IS NULL OR o.FormKey = :record OR o.EditorID = :record)
  AND (:pattern IS NULL OR o.EditorID LIKE :pattern ESCAPE '\')
ORDER BY o.Plugin, o.EditorID, o.FormKey, i.Idx;
