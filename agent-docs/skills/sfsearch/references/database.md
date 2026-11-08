# SFSearch database and SQL

Each plugin gets its own SQLite database, `SFSearch\Cache\<plugin>.db`. It is built the first time a command needs it and rebuilt by itself when the plugin is newer, the schema changed, or the list of cached record types changed.

## What gets cached
- The list of fully cached record types lives in one place: `CachedSignatures` in `SFSearch\Program.cs`. These types get a full `Records` row (all fields as JSON) and `Refs` rows. Every other type gets only a `Forms` row.
- **Cached in full:** items and crafting (Weapon, Armor, ArmorAddon, all OMOD types, Keyword, ConstructibleObject, FormList, LeveledItem, LeveledNpc, MiscItem, Ammunition, Ingestible, Component, Book, InstanceNamingRules, LegendaryItem, Resource, ResearchProject); actors and effects (Npc, Faction, Race, Class, Outfit, Perk, Spell, MagicEffect, ObjectEffect, Enchantment, DamageType, ActorValueInformation, Projectile, Explosion, Global, ConditionRecord, Quest, Furniture, Container, Message, Terminal, TerminalMenu); and Activator, CombatStyle, the GameSetting types, BodyPartData, AimModel, GameplayOption, GameplayOptionsGroup, DefaultObject, ActorValueModulation, MorphableObject, EquipType.
- **Placed references** (PlacedObject, PlacedNpc): a plugin with at most 50,000 of them caches them all in full. In a bigger plugin (Starfield.esm, ShatteredSpace.esm, nfvc0003vigilance.esm and any other big mod), every PlacedNpc is still cached in full (since 2026-11-01; Starfield.esm has 9,530), but only the PlacedObjects that carry scripts are. `Meta` `PlacedMode` says which mode was used.
- **Left out on purpose** (`Forms` row only): dialogue, scenes, navmeshes, cells, worldspaces, planets, biomes, statics, lights, sounds, visual records (material swaps, art objects, effect shaders, image spaces) and ship-builder parts (GenericBaseForm named SM + letters + _, such as `SMX_` or `SMC_`; whole ships are cached). `SignatureCounts.Cached` is 2 for a type only partly cached.
- **Added types:** `Add <plugin> <Type>` stores a left-out type in that plugin's database with no rebuild. It stores every field, and records nested under another (a cell's references, a topic's responses) in their own rows, not inside the parent. `Meta` `AddedTypes` lists them, `SignatureCounts.Cached` turns to 1, and a rebuild adds them back. A normal row still skips face data and weapon unknowns.
- **Stale caches:** a cache whose plugin is gone (deleted or disabled patch) can't be rebuilt. `Query` prints "Skipped <plugin>" and moves on. Delete the dead `Cache\<plugin>.db` to silence it.

## Tables

### `Records` (fully cached types)
| Column | Meaning |
|---|---|
| `FormKey` | `000CF2:darkstar.esm` form: 6 upper-case hex digits, colon, defining plugin. Primary key. |
| `Signature` | Mutagen record type: `Weapon`, `Armor`, `ArmorAddon`, `ArmorModification`, `WeaponModification`, `ObjectModification`, `InstanceNamingRules`, ... |
| `EditorID` | Indexed. |
| `Name` | English display name, Creation Kit `<ID=...>` tags stripped. |
| `Origin` | The plugin that defines the record (e.g. `Starfield.esm`). |
| `IsOverride` | `1` = override of a master's record, `0` = new in this plugin. Indexed. |
| `Data` | All fields as JSON. Every link is stored as a plain `XXXXXX:Plugin.esm` string, with the plugin spelled as its file in Data (`Starfield.esm`, `SFBGS00D.esm`), whatever the referring plugin's header says. A field object with nothing filled in is stored as `{"Type":"<Mutagen type>"}`, e.g. an empty naming rule set or a `ScannableComponent`. Raw bytes are hex. Percents are the exact number, not "33%". Empty text is kept as `""`, an empty list slot as `null`. Dictionaries and grids are lists of `{Key, Value}`, except translated text, which stays `"[English, The Lodge]"`. |

Where things sit in `Data`: a weapon's or OMOD's mesh is `json_extract(Data,'$.Model.File.GivenPath')` (NULL when the record has no MODL; `Q omod` shows it as `Mesh`). OMODs have no `MaxRank`, `LevelTierScaledOffset` or `DataName`: Mutagen misreads them, so they're left out.

### `Refs` (who points at whom)
`FromFormKey`, `FromSignature`, `FromEditorID`, `Path` (exact JSON path, e.g. `Items[36]`, `CreatedObject`, `VirtualMachineAdapter.Scripts[0].Properties[17].Object`), `ToFormKey`. Both FormKey columns are indexed.

### `Forms` (every record, also the types not cached in full)
| Column | Meaning |
|---|---|
| `FormKey`, `Signature`, `EditorID`, `IsOverride` | As in `Records`. `EditorID` is NULL when the record has none. |
| `Base` | Placed references: the base object's FormKey. Indexed. |
| `Parent` | The record this one sits under, from the plugin's own groups: a placed reference's cell, a cell's worldspace, a response's topic, a topic's quest. Indexed. Check the parent's `Signature` if you only want cells. |
| `HasScripts` | `1` when the record itself carries scripts (VMAD), e.g. a placed Codex machine that sets its own properties. |

Use it for placed references and for FormID-clash checks (a FormKey that two plugins both define as new).

### `SignatureCounts`
`Signature`, `TotalCount`, `OverrideCount`, `Cached` (`1` all in `Records`, `0` none, `2` some: placed references with scripts in a big plugin). Counts every record type, cached or not.

### `Masters` and `Meta`
- `Masters (Idx, MasterName)`: the plugin's masters in their exact order.
- `Meta (Key, Value)`: `SchemaVersion`, `CachedTypes`, `PlacedMode`, `FileName` (the plugin's real file name), `MasterStyle` (`Light (Small)` / `Medium` / `Full`), `HeaderFlags`, `RecordCount`, `CachedRecordCount`, `OverrideCount`, `MaxLocalFormID`, `Author`, `Description`.

## Running SQL
- **Several plugins at once:** `<Target>` takes comma lists, globs (`darkstarpatch_*`, `*ecliptic*`), `--cached`, `--all`, `--allnods`, `--active` or `--official`. A `Plugin` column is added in front of the merged result.
- **Inline SQL must be one line.** `sfs.cmd` runs through cmd.exe, which cuts an argument at its first line break; the cut query may still run and give a wrong answer. For longer SQL use `--file x.sql`, or pipe it in: `$sql | & sfs.cmd Query <Target> --stdin` (a PowerShell `@" … "@` here-string works).
- **`--file x.sql`** avoids PowerShell quoting problems. A file can hold several statements: every `SELECT` prints its own table, and `CREATE TEMP TABLE` / `CREATE INDEX` just run. A comment line `-- ## Title` right before a statement becomes its heading. If one statement fails, the error names it and the rest is skipped.
- **`--with-masters`** attaches the target's masters' caches; **`--with p1,p2`** attaches others. The aliases are printed: `Starfield.esm` is always `sf`, others are their lower-case name without extension (`darkstar`, `darkstar_manufacturing`, `sfbgs00d`). Join like `JOIN sf.Records s ON s.FormKey = ...`. Never hand-write `ATTACH DATABASE`.
- **`AllRecords`**: a temp table built whenever something is attached. `FormKey, Signature, EditorID, Name, Origin, IsOverride, Plugin` over the target and every attached cache, indexed on FormKey and EditorID. When a FormKey is in several caches, the target's version wins, then its masters (later first), then `--with` plugins. It has no `Data`: for JSON, join `main.Records` or `sf.Records`.
- **`--out report.md`** writes every table to the file and prints only one summary line per table. **`--timing`** prints each statement's run time (anything over 5 s is always reported).
- **SQL functions**, in every query with nothing attached: `edid(fk)` EditorID of any FormKey, `fkname(fk)` its English name, `en(json)` the English text of a localized JSON value, `tier(edid)` 1..8 for `..._Quality_N` EditorIDs (0 for `_Quality_TiersAny`).

## Finding a field's JSON path
`Fields <plugin> <Type>` (MCP `sfs_fields`) lists every path a type has in `Data`, with how many records have it and a sample value. Use it instead of guessing; `--filter text` narrows the list.

## The load order: `Query --loadorder` (MCP: `sfs_query` with `load_order`)
Attaches `Cache\_loadorder.db` as `lo`. The target plugin can be left out to query the load order alone. It is read from the record headers of every plugin in Data and updated plugin by plugin when one changes (the first build takes about a minute; the service's warm-up builds it while idle).
- `lo.Plugins (Id, Plugin, Pos, Enabled, Slot, Size, Stamp, Length, Modified, Records, NewRecords, Overrides, Listed, Masters, MasterSizes, MissingMasters, LateMasters, TypeMismatches, NoBase, Problem)`: every plugin in Data. `Pos` is the place in the load order (NULL when plugins.txt doesn't list it); `Enabled` 1 when loaded; `Slot` the runtime prefix (`0A`, `FD:03`, `FE:09A`); `Size` Full / Medium / Light. `Records`, `NewRecords`, `Overrides` count everything; `Listed` is how many rows it has in `Versions`. `TypeMismatches` and `NoBase` count overrides of a different type than their base, and overrides of nothing (trap 15).
- `lo.Versions (FormKey, Plugin, Signature, Sig4, EditorID, IsOverride, Pos, Enabled)`: every copy of every record, except navmeshes, landscape and the game's own placed references (counted, not listed); mods' placed references are listed. `Signature` is the type name; all OMODs are `ObjectModification` here (`Sig4` `OMOD`), unlike `Records`.
- `lo.Winners (FormKey, Plugin, Signature, Sig4, EditorID, Versions, Pos)`: the last loaded copy of each record, the one the game uses. `Versions` is how many loaded plugins have it.
- `lo.WinningRecords (FormKey, Plugin, Signature, EditorID, Name, Origin, IsOverride, Data)`: the winners' full data, filled for the types the SQL names with `Signature = '…'` or `Signature IN (…)`, from each winning plugin's database. Without a type it stays empty and a note says so.
- Functions: `winner(fk)` the plugin that wins a record; `versions(fk)` every plugin that has it, in load order (`A.esm > B.esm`, then the ones not loaded).

```sql
-- Every weapon that wins in game, with its damage, and which plugin it comes from
SELECT Plugin, EditorID, Name, json_extract(Data, '$.AttackDamage') AS dmg FROM lo.WinningRecords WHERE Signature = 'Weapon' ORDER BY Plugin, EditorID;
-- Records three or more loaded plugins edit
SELECT FormKey, EditorID, versions(FormKey) FROM lo.Winners WHERE Signature = 'ConstructibleObject' AND Versions >= 3;
```

## Scripts: `Query --scripts` (MCP: `sfs_query` with `scripts`)
Attaches `Cache\Scripts.db` as `scr`, after decompiling the target plugins' scripts if needed (Champollion; once per copy of their archives).
- `scr.Scripts (Plugin, Script, File, Lines, Source)`: one row per script; `Script` is the name on its `ScriptName` line (`DarkStar:TGO:Scaling:Master`), as records' scripts name it.
- `scr.Lines (Plugin, Script, Line, Text)`: every line, for searching.
- `scr.Sources (Plugin, Stamp, Scripts, Decompiled, Problem)`.

```sql
-- Records whose scripts call HasKeyword, with the line
SELECT r.EditorID, s.value ->> '$.Name' AS script, l.Line, trim(l.Text) FROM Records r, json_each(r.Data, '$.VirtualMachineAdapter.Scripts') s
JOIN scr.Lines l ON l.Script = s.value ->> '$.Name' WHERE l.Text LIKE '%HasKeyword%';
```

## Examples
Where something is placed, and whether the placed copy has its own script:
```sql
-- act_DarkStar_CodexMach_Activator_v2 (001D2F:darkstar.esm)
SELECT f.FormKey, edid(f.Base) AS base, edid(f.Parent) AS cell, f.HasScripts FROM Forms f WHERE f.Base = '001D2F:darkstar.esm';
```
Then `Q vmad <plugin> <placed FormID>` shows its script properties.

Placed containers (run with `--with-masters` so `AllRecords` exists):
```sql
SELECT f.FormKey, f.EditorID, edid(f.Base) AS base FROM Forms f JOIN AllRecords r ON r.FormKey = f.Base WHERE r.Signature = 'Container';
```

Fast pattern for list fields: extract once, index, then join. A query that runs `json_each` inside a per-row subquery can take minutes.
```sql
CREATE TEMP TABLE kw AS SELECT r.FormKey, j.value AS kw FROM Records r, json_each(r.Data,'$.Keywords') j WHERE r.Signature='Armor';
CREATE INDEX temp.ix_kw ON kw(kw);
-- ## Armor per keyword
SELECT edid(kw) AS keyword, COUNT(*) FROM kw GROUP BY kw ORDER BY 2 DESC;
```
Queries already run with an in-memory temp store, a 256 MB page cache and memory-mapped reads. Before writing a long census query, check whether `MkDiff`, `OmodCensus` or `RecipeCensus` already gives the number.
