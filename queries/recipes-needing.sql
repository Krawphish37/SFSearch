-- name: recipes-needing
-- answers: The recipes that need a given part: every recipe that lists it among what it costs, with how many it takes.
-- It reads the load order as the game uses it (the winning copy of every recipe). Name plugins to look in those only.
-- param: item | pattern | needed | A part of the EditorID of the thing needed (* stands for any text): a resource, a manufactured part, an item.
-- param: plugin | text | optional | With the plugins scope: the plugins to look in, instead of the load order.
-- scope: winners
-- column: Needs | The EditorID of the part needed.
-- column: NeedsKey | Its FormKey.
-- column: Count | How many of it the recipe takes.
-- column: Recipe | The recipe's EditorID.
-- column: FormKey | The recipe's FormKey.
-- column: Makes | The EditorID of the thing the recipe makes.
-- column: MakesType | Its record type.
-- column: Workbench | The EditorID of the workbench keyword.
-- column: Plugin | The plugin the recipe was read from: the one that wins it.
-- example: item=ResInorgCommonIron
CREATE TEMP TABLE costs AS
SELECT k.FormKey, k.Plugin, k.Component, edid(k.Component) AS Needs, k.RequiredCount
FROM ConstructibleObject_ConstructibleComponents k
WHERE edid(k.Component) LIKE :item ESCAPE '\';
CREATE INDEX costs_key ON costs(FormKey, Plugin);
SELECT k.Needs, k.Component AS NeedsKey, k.RequiredCount AS Count, c.EditorID AS Recipe, c.FormKey, edid(c.CreatedObject) AS Makes,
       type(c.CreatedObject) AS MakesType, edid(c.WorkbenchKeyword) AS Workbench, c.Plugin
FROM ConstructibleObject c JOIN costs k ON k.FormKey = c.FormKey AND k.Plugin = c.Plugin
ORDER BY k.Needs, Workbench, c.EditorID, c.FormKey;
