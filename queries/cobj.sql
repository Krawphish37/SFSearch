-- name: cobj
-- answers: The recipes (ConstructibleObject) of a plugin: what each makes, on which workbench, from what, and under which conditions.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: record | text | optional | One recipe, by EditorID or FormKey.
-- param: pattern | pattern | optional | A part of the recipes' EditorIDs (* stands for any text).
-- param: makes | pattern | optional | A part of the EditorID of the thing made.
-- scope: plugins
-- column: FormKey | The recipe.
-- column: EditorID | The recipe's EditorID.
-- column: Makes | The EditorID of the thing made (xEdit: CNAM, "Created Object").
-- column: MakesKey | Its FormKey.
-- column: MakesType | Its record type (Weapon, Armor, WeaponModification, …).
-- column: Amount | How many are made at once. 0 on a recipe that adds a modification to an item.
-- column: Workbench | The EditorID of the workbench keyword (xEdit: BNAM): the bench the recipe shows at.
-- column: Needs | What it costs: each part as "count EditorID", in the recipe's order.
-- column: Perks | The perks it needs, each as "EditorID rank".
-- column: Conditions | When the recipe shows, written as Function(arguments) = value, joined by AND or OR as the record joins them.
-- column: Filters | The EditorIDs of its recipe filters (xEdit: FNAM): the category it is listed under.
-- column: Description | The text shown for the recipe.
-- column: IsOverride | 1 when the plugin changes a recipe of one of its masters.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm pattern=co_gun_mod_Eon
CREATE TEMP TABLE needs AS
SELECT FormKey, Plugin, group_concat(RequiredCount || ' ' || coalesce(edid(Component), Component), ', ' ORDER BY Idx) AS Needs
FROM ConstructibleObject_ConstructibleComponents GROUP BY FormKey, Plugin;
CREATE INDEX needs_key ON needs(FormKey, Plugin);
CREATE TEMP TABLE perks AS
SELECT FormKey, Plugin, group_concat(coalesce(edid(Perk), Perk) || ' ' || Rank, ', ' ORDER BY Idx) AS Perks
FROM ConstructibleObject_RequiredPerks GROUP BY FormKey, Plugin;
CREATE INDEX perks_key ON perks(FormKey, Plugin);
CREATE TEMP TABLE filters AS
SELECT FormKey, Plugin, group_concat(coalesce(edid(Value), Value), ', ' ORDER BY Idx) AS Filters
FROM ConstructibleObject_RecipeFilters GROUP BY FormKey, Plugin;
CREATE INDEX filters_key ON filters(FormKey, Plugin);
CREATE TEMP TABLE conds AS
SELECT FormKey, Plugin, group_concat(
         replace(coalesce(Json ->> '$.Data.MutagenObjectType', '?'), 'ConditionData', '')
         || '(' || coalesce(edid(Json ->> '$.Data.FirstParameter'), Json ->> '$.Data.FirstParameter', '')
         || coalesce(', ' || coalesce(edid(Json ->> '$.Data.SecondParameter'), Json ->> '$.Data.SecondParameter'), '') || ')'
         || CASE CompareOperator WHEN 'EqualTo' THEN ' = ' WHEN 'NotEqualTo' THEN ' != ' WHEN 'GreaterThan' THEN ' > '
                 WHEN 'GreaterThanOrEqualTo' THEN ' >= ' WHEN 'LessThan' THEN ' < ' WHEN 'LessThanOrEqualTo' THEN ' <= ' ELSE ' ' || CompareOperator || ' ' END
         || coalesce(edid(Json ->> '$.ComparisonValue'), Json ->> '$.ComparisonValue', 0)
         || CASE WHEN Flags LIKE '%"OR"%' THEN ' OR ' ELSE ' AND ' END, '' ORDER BY Idx) AS Conditions
FROM ConstructibleObject_Conditions GROUP BY FormKey, Plugin;
CREATE INDEX conds_key ON conds(FormKey, Plugin);
SELECT c.FormKey, c.EditorID, edid(c.CreatedObject) AS Makes, c.CreatedObject AS MakesKey, type(c.CreatedObject) AS MakesType,
       c.AmountProduced AS Amount, edid(c.WorkbenchKeyword) AS Workbench, n.Needs, p.Perks,
       CASE WHEN d.Conditions LIKE '% OR ' THEN substr(d.Conditions, 1, length(d.Conditions) - 4)
            WHEN d.Conditions LIKE '% AND ' THEN substr(d.Conditions, 1, length(d.Conditions) - 5) ELSE d.Conditions END AS Conditions,
       f.Filters, display(c.Description) AS Description, c.IsOverride, c.Plugin
FROM ConstructibleObject c
LEFT JOIN needs n ON n.FormKey = c.FormKey AND n.Plugin = c.Plugin
LEFT JOIN perks p ON p.FormKey = c.FormKey AND p.Plugin = c.Plugin
LEFT JOIN conds d ON d.FormKey = c.FormKey AND d.Plugin = c.Plugin
LEFT JOIN filters f ON f.FormKey = c.FormKey AND f.Plugin = c.Plugin
WHERE (:record IS NULL OR c.FormKey = :record OR c.EditorID = :record)
  AND (:pattern IS NULL OR c.EditorID LIKE :pattern ESCAPE '\')
  AND (:makes IS NULL OR edid(c.CreatedObject) LIKE :makes ESCAPE '\')
ORDER BY c.Plugin, c.EditorID, c.FormKey;
