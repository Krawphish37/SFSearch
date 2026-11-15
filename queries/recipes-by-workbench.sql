-- name: recipes-by-workbench
-- answers: The recipes of a plugin by workbench: how many each workbench has, or the recipes of one workbench.
-- A recipe names its workbench with a keyword (xEdit: BNAM). Without a workbench the query counts the recipes of
-- each. With one it lists that workbench's recipes and what each makes.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: workbench | pattern | optional | A part of the workbench keyword's EditorID (* stands for any text): its recipes are listed.
-- scope: plugins
-- column: Workbench | The workbench keyword's EditorID. "(none)" for recipes that name no workbench.
-- column: WorkbenchKey | The workbench keyword's FormKey.
-- column: Recipes | How many recipes of the plugin use the workbench.
-- column: OwnRecipes | How many of them are the plugin's own (not changes to a master's recipe).
-- column: Makes | How many different things those recipes make.
-- column: FormKey | The recipe.
-- column: EditorID | The recipe's EditorID.
-- column: MakesEditorID | The EditorID of the thing the recipe makes.
-- column: MakesType | Its record type.
-- column: Amount | How many are made at once.
-- column: Parts | How many different parts the recipe needs.
-- column: IsOverride | 1 when the plugin changes a recipe of one of its masters.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm
-- example: plugin=Starfield.esm workbench=Weapon
-- table: Workbenches
-- unless: workbench
SELECT coalesce(edid(c.WorkbenchKeyword), c.WorkbenchKeyword, '(none)') AS Workbench, c.WorkbenchKeyword AS WorkbenchKey, count(*) AS Recipes,
       sum(c.IsOverride = 0) AS OwnRecipes, count(DISTINCT c.CreatedObject) AS Makes, c.Plugin
FROM ConstructibleObject c
GROUP BY c.Plugin, c.WorkbenchKeyword
ORDER BY c.Plugin, Recipes DESC, Workbench;
-- when: workbench
CREATE TEMP TABLE parts AS
SELECT FormKey, Plugin, count(*) AS N FROM ConstructibleObject_ConstructibleComponents GROUP BY FormKey, Plugin;
-- when: workbench
CREATE INDEX parts_key ON parts(FormKey, Plugin);
-- table: Recipes of the workbench
-- when: workbench
SELECT edid(c.WorkbenchKeyword) AS Workbench, c.FormKey, c.EditorID, edid(c.CreatedObject) AS MakesEditorID, type(c.CreatedObject) AS MakesType,
       c.AmountProduced AS Amount, coalesce(p.N, 0) AS Parts, c.IsOverride, c.Plugin
FROM ConstructibleObject c LEFT JOIN parts p ON p.FormKey = c.FormKey AND p.Plugin = c.Plugin
WHERE edid(c.WorkbenchKeyword) LIKE :workbench ESCAPE '\'
ORDER BY c.Plugin, Workbench, c.EditorID, c.FormKey;
