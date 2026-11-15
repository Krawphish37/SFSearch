-- name: vmad
-- answers: The scripts attached to records, with every script property and its value.
-- One row per property; a script with no property gets one row. A property the plugin leaves out reads as its
-- default (0, false, an empty text), as the game reads it. Lists and structs are given as JSON.
-- Three places are read: the scripts on the record itself, the fragment script of a quest, scene, perk, package
-- or terminal, and the scripts on a quest's aliases. The column Attached says which.
-- param: plugin | text | needed | The plugin to read, by file name. Several with commas.
-- param: record | text | optional | One record, by EditorID or FormKey.
-- param: pattern | pattern | optional | A part of the records' EditorIDs (* stands for any text).
-- param: type | text | optional | One record type (MiscItem, Quest) or signature (MISC): only it is read.
-- param: script | pattern | optional | A part of the script's name.
-- scope: plugins
-- reads: every type with VirtualMachineAdapter
-- column: FormKey | The record the script is attached to.
-- column: Type | That record's type.
-- column: EditorID | That record's EditorID.
-- column: Attached | Where on the record: "record" for its own scripts, "fragments" for its fragment script, "alias N" for the scripts of a quest's alias number N.
-- column: Script | The script's name, as Papyrus names it (MyMod:Folder:Script).
-- column: Property | The property's name. Empty for a script with no properties.
-- column: Kind | What the property holds: Object (a record), Int, Float, Bool, String, Struct, or a list of one of them.
-- column: Value | The value. For an Object: the FormKey of the record it points at; empty when it points at nothing.
-- column: ValueEditorID | For an Object: the EditorID of the record it points at.
-- column: Plugin | The plugin the row was read from.
-- example: plugin=Starfield.esm type=Armor
-- example: plugin=Starfield.esm record=MQ101
CREATE TEMP TABLE adapters AS
SELECT r.FormKey, r.Type, r.EditorID, r.Plugin, r.Json -> '$.VirtualMachineAdapter' AS A
FROM Records r
WHERE json_type(r.Json, '$.VirtualMachineAdapter') = 'object'
  AND (:record IS NULL OR r.FormKey = :record OR r.EditorID = :record)
  AND (:pattern IS NULL OR r.EditorID LIKE :pattern ESCAPE '\')
  AND (:type IS NULL OR r.Sig = :type);
CREATE TEMP TABLE scripts AS
SELECT v.FormKey, v.Type, v.EditorID, v.Plugin, 'record' AS Attached, s.key AS Ord, s.value AS S
FROM adapters v JOIN json_each(v.A, '$.Scripts') s
UNION ALL
SELECT v.FormKey, v.Type, v.EditorID, v.Plugin, 'fragments', 100000, v.A -> '$.Script'
FROM adapters v WHERE json_type(v.A, '$.Script') = 'object'
UNION ALL
SELECT v.FormKey, v.Type, v.EditorID, v.Plugin, 'fragments', 100000, v.A -> '$.ScriptFragments.Script'
FROM adapters v WHERE json_type(v.A, '$.ScriptFragments.Script') = 'object'
UNION ALL
SELECT v.FormKey, v.Type, v.EditorID, v.Plugin, 'alias ' || coalesce(a.value ->> '$.Property.Alias', a.key), 200000 + a.key * 1000 + s.key, s.value
FROM adapters v JOIN json_each(v.A, '$.Aliases') a JOIN json_each(a.value, '$.Scripts') s;
SELECT x.FormKey, x.Type, x.EditorID, x.Attached, x.S ->> '$.Name' AS Script, p.value ->> '$.Name' AS Property,
       replace(replace(p.value ->> '$.MutagenObjectType', 'Script', ''), 'Property', '') AS Kind,
       CASE p.value ->> '$.MutagenObjectType'
            WHEN 'ScriptObjectProperty' THEN p.value ->> '$.Object'
            WHEN 'ScriptIntProperty' THEN coalesce(p.value ->> '$.Data', 0)
            WHEN 'ScriptBoolProperty' THEN coalesce(p.value ->> '$.Data', 0)
            WHEN 'ScriptFloatProperty' THEN coalesce(p.value ->> '$.Data', 0.0)
            WHEN 'ScriptStringProperty' THEN coalesce(p.value ->> '$.Data', '')
            ELSE coalesce(p.value -> '$.Objects', p.value -> '$.Structs', p.value -> '$.Members', p.value -> '$.Data')
       END AS Value,
       edid(p.value ->> '$.Object') AS ValueEditorID, x.Plugin
FROM scripts x LEFT JOIN json_each(x.S, '$.Properties') p
WHERE (:script IS NULL OR x.S ->> '$.Name' LIKE :script ESCAPE '\')
ORDER BY x.Plugin, x.Type, x.EditorID, x.FormKey, x.Ord, p.key;
