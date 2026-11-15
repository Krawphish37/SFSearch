-- name: scripts-used
-- answers: Every script a plugin's records attach, with how many records attach it, and the script attachments that hold nothing.
-- The first table is the list to hold against a mod's script files: a script file whose name isn't in it is
-- attached to no record of the plugin. (This query can't see the script files themselves: they are in the mod's
-- archive, not in the plugin.)
-- The second table lists attachments that hold nothing: a script adapter with no script in it, a script with no
-- name, and an object property that points at a record no plugin in Data has. A property that points into
-- Starfield.esm with an ID below 000800 is not counted: such forms (the player, for one) are built into the game.
-- Scripts on the record itself, fragment scripts and the scripts of a quest's aliases are all counted.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: type | text | optional | One record type (MiscItem) or signature (MISC): only it is read.
-- param: script | pattern | optional | A part of the script's name.
-- scope: plugins
-- reads: every type with VirtualMachineAdapter
-- column: Script | The script's name, as Papyrus names it.
-- column: Records | How many records attach the script.
-- column: Types | The record types of those records, with the number of each.
-- column: Example | One record that attaches it, as EditorID (FormKey).
-- column: Fault | What is wrong: "adapter with no script", "script with no name", or "property points at a missing record".
-- column: FormKey | The record.
-- column: Type | Its record type.
-- column: EditorID | Its EditorID.
-- column: Property | The property's name.
-- column: Target | The FormKey the property points at.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm type=Container
CREATE TEMP TABLE adapters AS
SELECT r.FormKey, r.Type, r.EditorID, r.Plugin, r.Json -> '$.VirtualMachineAdapter' AS A
FROM Records r
WHERE json_type(r.Json, '$.VirtualMachineAdapter') = 'object' AND (:type IS NULL OR r.Sig = :type);
CREATE TEMP TABLE uses AS
SELECT v.FormKey, v.Type, v.EditorID, v.Plugin, s.value AS S FROM adapters v JOIN json_each(v.A, '$.Scripts') s
UNION ALL
SELECT v.FormKey, v.Type, v.EditorID, v.Plugin, v.A -> '$.Script' FROM adapters v WHERE json_type(v.A, '$.Script') = 'object'
UNION ALL
SELECT v.FormKey, v.Type, v.EditorID, v.Plugin, v.A -> '$.ScriptFragments.Script' FROM adapters v WHERE json_type(v.A, '$.ScriptFragments.Script') = 'object'
UNION ALL
SELECT v.FormKey, v.Type, v.EditorID, v.Plugin, s.value FROM adapters v JOIN json_each(v.A, '$.Aliases') a JOIN json_each(a.value, '$.Scripts') s;
CREATE TEMP TABLE named AS
SELECT S ->> '$.Name' AS Script, Type, FormKey, EditorID, Plugin FROM uses;
CREATE INDEX named_script ON named(Script COLLATE NOCASE);
CREATE INDEX uses_key ON uses(FormKey, Plugin);
-- table: Scripts and the records that attach them
SELECT u.Script, count(DISTINCT u.FormKey) AS Records,
       (SELECT group_concat(t.Type || ' ' || t.N, ', ' ORDER BY t.N DESC, t.Type) FROM (SELECT x.Type, count(DISTINCT x.FormKey) AS N FROM named x WHERE x.Script = u.Script COLLATE NOCASE GROUP BY x.Type) t) AS Types,
       coalesce(min(CASE WHEN u.EditorID IS NOT NULL THEN u.EditorID || ' (' || u.FormKey || ')' END), '(no EditorID) (' || min(u.FormKey) || ')') AS Example
FROM named u
WHERE coalesce(u.Script, '') <> '' AND (:script IS NULL OR u.Script LIKE :script ESCAPE '\')
GROUP BY u.Script COLLATE NOCASE
ORDER BY Records DESC, u.Script;
-- table: Script attachments that hold nothing
SELECT 'adapter with no script' AS Fault, v.FormKey, v.Type, v.EditorID, NULL AS Script, NULL AS Property, NULL AS Target, v.Plugin
FROM adapters v
WHERE NOT EXISTS (SELECT 1 FROM uses u WHERE u.FormKey = v.FormKey AND u.Plugin = v.Plugin)
UNION ALL
SELECT 'script with no name', u.FormKey, u.Type, u.EditorID, u.Script, NULL, NULL, u.Plugin
FROM named u WHERE coalesce(u.Script, '') = ''
UNION ALL
SELECT 'property points at a missing record', u.FormKey, u.Type, u.EditorID, u.S ->> '$.Name', p.value ->> '$.Name', p.value ->> '$.Object', u.Plugin
FROM uses u JOIN json_each(u.S, '$.Properties') p
WHERE p.value ->> '$.Object' IS NOT NULL AND p.value ->> '$.Object' <> 'Null' AND versions(p.value ->> '$.Object') IS NULL
  AND NOT (lower(substr(p.value ->> '$.Object', 8)) = 'starfield.esm' AND substr(p.value ->> '$.Object', 1, 6) < '000800')
ORDER BY Fault, Type, EditorID, FormKey, Property;
