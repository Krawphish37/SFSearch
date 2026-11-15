# SQL in SFSearch

`sfs_query` runs SQL (SQLite) over plugin data that is read live. There is no database to build or keep: the record types a query names are read from the plugin files the first time, into scratch tables that live only while the service runs.

Every answer below is real, cut with "…" where long. The examples are written as the command line's `query2`; `sfs_query` takes the same SQL in `sql` (over several lines, no `;` needed) and prints the same answer.

```text
> query2 kgcdoom.esm "SELECT EditorID, Name, AttackDamage FROM Weapon ORDER BY 1;"
| EditorID | Name | AttackDamage |
|---|---|---|
| kgcDoom_WEAP_CrucibleBlade | Crucible Blade | 37 |
| kgcDoom_WEAP_SuperShotgun | Super Shotgun | 80 |
[source] kgcdoom.esm 3.5 MB, key 9561e0c8
[cost] 139 ms; read Weapon (2) from kgcdoom.esm in 14 ms
```

## What SQL sees
| Name | What it is | Columns |
|---|---|---|
| `Weapon`, `Armor`, `Npc`, `ConstructibleObject`, … | One typed view per record type, under its Mutagen name | `FormKey`, `EditorID`, `Plugin`, `IsOverride`, one column per plain field, `Json` |
| `Weapon_Keywords`, `Npc_Factions`, … | One list view per list of a type: `<Type>_<List>` | `FormKey`, `EditorID`, `Plugin`, `Idx`, then `Value` (a list of links or plain values) or the entry's own fields and its `Json` |
| `Weapon_ObjectTemplates_Includes`, … | A list inside a list | The same, with `Idx2` for the inner position |
| `Npc_Flags`, … | A flags field as a list of names | `FormKey`, `EditorID`, `Plugin`, `Idx`, `Value` |
| `ObjectModification`, `GameSetting` | All record types of one signature together (OMOD has 8) | The typed view's columns, and `Type` |
| `Forms` | Every record of the scope from the header index, read or not | `FormKey`, `Sig`, `Type`, `EditorID`, `Plugin`, `Parent`, `IsWinner`, `IsOverride`, `Pos`, `LoadOrder` |
| `Records` | Every record **read so far** in this scope | `FormKey`, `Type`, `Sig`, `EditorID`, `Name`, `Plugin`, `IsOverride`, `Json` |
| `Links` | The links of the record types **read so far** | `FromKey`, `FromType`, `Field`, `Idx`, `ToKey`, `Path`, `Plugin` |
| `schema.Fields`, `schema.RecordTypes`, `schema.Enums`, … | The schema `sfs_describe` reads | See "The schema tables" |

- **Column names are Mutagen's field names.** A field inside an object joins the names with `_`: `ObjectBounds_First`, `Model_File`, `Transforms_InventoryIcon`. `sfs_describe WEAP` lists a type's fields; never guess.
- **A link column holds a FormKey** (`0476C4:Starfield.esm`), or NULL for a null link. FormKeys, EditorIDs and plugin names compare without regard to letter case.
- **`IsOverride`** is 1 when the plugin changes a record of one of its masters, 0 for its own record.
- **`Json`** is the whole record as Mutagen's JSON. Read a field that has no column with `Json ->> '$.Path'`.
- **Enums are names, flags are a JSON list of names** (`["AutoCalcStats"]`); the flags' list view has one row per name.
- **`Forms.Parent`** is the record a record sits under: a placed reference's cell, a topic's quest.
- **`Records` and `Links` hold only the types a query has named so far.** A type is read when a query names its own view. To count every record, use `Forms`. A note says so when a query names `Records` or `Links`.
- **A query may make temporary tables and indexes of its own,** in several statements. The last `SELECT`'s rows are the answer. It can't change anything else: `Error: a query may read, and may make temporary tables and indexes of its own; it can't run ATTACH.`

## The three scopes
| `scope` | Rows | When |
|---|---|---|
| `plugins` | Each plugin named in `plugins`, as it is. A `Plugin` column tells them apart | The default when plugins are named |
| `winners` | For each record, the version the game uses: the last loaded plugin that holds it | The default when no plugin is named |
| `versions` | Every copy in every loaded plugin. Every typed view also has `LoadOrder` and `IsWinner` | On request |

`plugins` takes names in Data separated by commas, or the full path of one plugin file elsewhere. A file outside Data is read with its masters from Data, and `[source]` shows its path.
```text
> query2 kgcdoom.esm,atom74.esm "SELECT Plugin, count(*) AS Weapons FROM Weapon GROUP BY Plugin ORDER BY 1;"
| Plugin | Weapons |
|---|---|
| atom74.esm | 3 |
| kgcdoom.esm | 2 |

> query2 "SELECT count(*) AS N FROM Weapon;"
| N |
|---|
| 1579 |
[source] Starfield.esm 1.36 GB, key d1c0e07a; Constellation.esm 38 KB, key a7ae4650; … and 218 more plugins (811.8 MB); load order of 2026-11-10 06:48 (224 loaded plugins)
[cost] 2.51 s; read Weapon (1,753) from 72 plugins in 2.43 s

> query2 "SELECT EditorID, Plugin, LoadOrder, IsWinner FROM InstanceNamingRules WHERE FormKey = '2377CF:Starfield.esm' ORDER BY LoadOrder;" --scope versions
| EditorID | Plugin | LoadOrder | IsWinner |
|---|---|---|---|
| dn_CommonGun | Starfield.esm | 0 | 0 |
| dn_CommonGun | SFBGS00D.esm | 10 | 1 |

> query2 --plugins "C:\Modding\copies\atom74.esm" "SELECT EditorID, Name, IsOverride FROM Weapon ORDER BY 1;"
| EditorID | Name | IsOverride |
|---|---|---|
| ccAtom74 | Atom-74 | 0 |
| ccAtom74Dusty | Dusty | 0 |
| ccAtom74Mk2 | Atom-74 Mk.2 | 0 |
[source] C:\Modding\copies\atom74.esm 82 KB, key 99071b6c
```

## Defaults, languages and names
- **A value the record leaves out reads as the field's default,** wherever the schema says the field is always there. Asking the raw JSON misses those records:
```text
> query2 Starfield.esm "SELECT (SELECT count(*) FROM Weapon) AS Weapons, (SELECT count(*) FROM Weapon WHERE HasScope = 0) AS NoScopeByColumn, (SELECT count(*) FROM Weapon WHERE Json ->> '$.HasScope' = 0) AS NoScopeByJson;"
| Weapons | NoScopeByColumn | NoScopeByJson |
|---|---|---|
| 406 | 393 | 0 |
```
- **Text columns give one language, found by its name:** English, or the `language` asked for (`German`, `de`, …). `text(value, 'de')` gives another language for one value.
```text
> query2 kgcdoom.esm "SELECT EditorID, Name, text(Json -> '$.Name', 'de') AS German FROM Weapon ORDER BY 1;"
| EditorID | Name | German |
|---|---|---|
| kgcDoom_WEAP_CrucibleBlade | Crucible Blade | Schmelztiegel-Klinge |
| kgcDoom_WEAP_SuperShotgun | Super Shotgun | Super-Shotgun |
```
- **Names come without the Creation Kit's tag.** A plugin saved without string files stores `<ID=0000793F>AA-99 Mk.II`; the text columns of the typed views, `Records.Name` and `name(fk)` give `AA-99 Mk.II`. `Json` keeps the text as stored; `display(text)` takes the tag off.

## The functions
| Function | Gives |
|---|---|
| `edid(fk)` | The EditorID of any record in Data |
| `name(fk)` | Its in-game name |
| `type(fk)`, `sig(fk)` | Its record type (`Weapon`) and signature (`WEAP`) |
| `winner(fk)` | The plugin whose version the game uses |
| `versions(fk)` | The loaded plugins that hold it, in load order, as a JSON list. NULL when no plugin in Data has the record |
| `formkey(text)` | The FormKey a text stands for: itself when it is a FormKey, else the FormKey of the record with that EditorID |
| `text(value, 'de')` | A translated value in another language |
| `display(text)` | A text without the Creation Kit's tag |

They answer for any record in Data, in or out of the scope.
```text
> query2 "SELECT edid('0476C4:Starfield.esm') AS edid, name('0476C4:Starfield.esm') AS name, type('0476C4:Starfield.esm') AS type, sig('0476C4:Starfield.esm') AS sig, formkey('Eon') AS formkey, winner('2377CF:Starfield.esm') AS winner, versions('2377CF:Starfield.esm') AS versions;"
| edid | name | type | sig | formkey | winner | versions |
|---|---|---|---|---|---|---|
| Eon | Eon | Weapon | WEAP | 0476C4:Starfield.esm | SFBGS00D.esm | ["Starfield.esm","SFBGS00D.esm"] |
```

## Lists: join the list view
Every list of a record type is a view with one row per entry. Don't unpack `Json` with `json_each`: it runs once for every row, and a note tells you the list view to use.
```text
> query2 Starfield.esm "SELECT w.EditorID, w.AttackDamage FROM Weapon w JOIN Weapon_Keywords k ON k.FormKey = w.FormKey WHERE w.AttackDamage > 20 AND edid(k.Value) = 'WeaponTypePistol' ORDER BY 2 DESC, 1;"
| EditorID | AttackDamage |
|---|---|
| MagShot | 53 |
| Razorback | 51 |
| UrbanEagle | 39 |
…
[source] Starfield.esm 1.36 GB, key d1c0e07a
[cost] 81 ms; read Weapon (406) from Starfield.esm in 31 ms

> query2 kgcdoom.esm "SELECT count(*) AS N FROM Weapon_Keywords WHERE edid(Value) = 'kgcDoom_Tag_WeaponTypeDoom';"
| N |
|---|
| 2 |
```
For a query that joins a list view many times, put it in a temporary table first (a view has no index). Several statements go through `sfs_query` as they are; on the command line use `--file` or `--stdin`:
```sql
CREATE TEMP TABLE kw AS
  SELECT FormKey, count(*) AS Keywords FROM Weapon_Keywords GROUP BY FormKey;
SELECT w.EditorID, kw.Keywords
FROM Weapon w JOIN kw ON kw.FormKey = w.FormKey
ORDER BY 2 DESC, 1;
```
```text
| EditorID | Keywords |
|---|---|
| kgcDoom_WEAP_SuperShotgun | 20 |
| kgcDoom_WEAP_CrucibleBlade | 12 |
```
A temporary table forgets that FormKeys compare without regard to case. Where a link is held against a record of another plugin, write `COLLATE NOCASE` on the comparison.

## All OMODs, every record, links
```text
> query2 darkstar.esm "SELECT Type, count(*) AS N FROM ObjectModification GROUP BY Type ORDER BY 2 DESC;"
| Type | N |
|---|---|
| ArmorModification | 1668 |
| WeaponModification | 1363 |

> query2 kgcdoom.esm "SELECT Sig, Type, count(*) AS N FROM Forms GROUP BY Sig, Type ORDER BY 3 DESC;" --rows 3
| Sig | Type | N |
|---|---|---|
| REFR | PlacedObject | 14366 |
| INFO | DialogResponses | 131 |
| NAVM | NavigationMesh | 103 |
[more] 3 of 68 rows shown. Next: cursor "ed5b91-3".

> query2 kgcdoom.esm "SELECT edid(FromKey) AS Record, FromType, Field, Path FROM Links WHERE ToKey = '0076BE:kgcdoom.esm' ORDER BY 1;"
| Record | FromType | Field | Path |
|---|---|---|---|
| kgcDoom_WEAP_CrucibleBlade | Weapon | Keywords | Keywords[7] |
| kgcDoom_WEAP_SuperShotgun | Weapon | Keywords | Keywords[7] |
[notes] Links holds the links of the record types read so far in this scope (1: Weapon). It is not a search of every type: name a type in the query to have it read.
```
- `Links.Field` is the field with list positions left out (`Keywords`, `ObjectTemplates[*].Includes[*].Mod`); `Path` is the exact place; `Idx` the innermost list position.
- **For "what points at this record" use `sfs_refs`,** which searches every type. `Links` is for joins inside a query.
- A cursor belongs to its query: pass it with the same SQL and scope.

## World data and heavy queries
Every record type can be queried, placed objects, cells and dialogue included. A mod's world data is small:
```text
> query2 kgcdoom.esm "SELECT edid(Base) AS Base, count(*) AS Placed FROM PlacedObject GROUP BY Base ORDER BY 2 DESC, 1;" --rows 3
| Base | Placed |
|---|---|
| kgcDoom_STAT_CandleGen_A01Lit_NoAnim | 873 |
| kgcDoom_STAT_CandleGen_A02Lit_NoAnim | 599 |
| kgcDoom_STAT_CandleGen_A03Lit_NoAnim | 385 |
[more] 3 of 2,197 rows shown. Next: cursor "0f0384-3".
```
The game's own is not. A read estimated over 10 s or 2 GB, or of a million records or more, is announced and not started:
```text
> query2 "SELECT count(*) FROM PlacedObject WHERE Base = '0002F6:Starfield.esm';" --plugins Starfield.esm
Not run. PlacedObject in Starfield.esm is 3,291,860 records (304.5 MB): about 9 s and under 1 GB to read (only the column the query names: Base). Call again with allow_heavy=true (command line: --allow-heavy).
```
With `allow_heavy` that query ran in 3.89 s. A type too big to read whole is read for the columns the query names only. A query that needs nothing of the records (`SELECT count(*) FROM PlacedObject`) is answered from the header index.

## The checks before a query runs
1. **Names.** A table or column that doesn't exist stops the query with the nearest real names:
```text
> query2 kgcdoom.esm "SELECT EditorID, BaseDamage FROM Weapon;"
Error: Weapon has no column "BaseDamage". Fix: the nearest real names: AttackDamage (Float, WDMG, xEdit name "Base Damage"); MeleeBashDamage (Float, WMEL, xEdit name "Bash Damage"); BaseValue (UInt32, WGEN). Every field of a type is in SELECT Path, Kind, Readable FROM schema.Fields WHERE Type = 'Weapon'; a field that isn't a column can be read with Json ->> '$.Path'.

> query2 kgcdoom.esm "SELECT EditorID FROM Weapons;"
Error: there is no table or record type "Weapons". Fix: the nearest real names: Weapon. The record types are in SELECT Sig, Type FROM schema.RecordTypes; a type's lists are views named Type_List (Weapon_Keywords).
```
2. **An EditorID where a FormKey is needed.** Links are stored as FormKeys, so an EditorID is never inside `Json` or a link column:
```text
> query2 kgcdoom.esm "SELECT EditorID FROM Weapon WHERE Json LIKE '%kgcDoom_Tag_WeaponTypeDoom%';"
| EditorID |
|---|
[notes] '%kgcDoom_Tag_WeaponTypeDoom%' is the EditorID of kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm). EditorIDs aren't inside a record's JSON: links are written as FormKeys. Search for '0076BE:kgcdoom.esm', or compare a link column with edid(column) = 'kgcDoom_Tag_WeaponTypeDoom'.
[notes] No rows: the filter matched nothing among 2 Weapon rows of kgcdoom.esm.
```
3. **No `ORDER BY`.** `[notes] The query has no ORDER BY, so the order of its rows isn't fixed (they come in file order today). Add ORDER BY to get the same order every time.`
4. **SQL here only reads.** `DELETE`, `UPDATE`, `INSERT`, `DROP`, `ALTER` or an index on a record table is an error, and so is `ATTACH`. A temporary table of your own can be made and changed:
```text
> query2 kgcdoom.esm "DELETE FROM Weapon;"
Error: a query may read, and may make temporary tables and indexes of its own; it can't run DELETE on Weapon, which holds the plugins' records as they are read from the files. Fix: write a SELECT, or CREATE TEMP TABLE … AS SELECT … followed by a SELECT. A temporary table of your own can be changed.
```
5. **Cost.** Above.
6. **No rows.** The answer says why:
```text
> query2 atom74.esm "SELECT EditorID FROM Static ORDER BY 1;"
| EditorID |
|---|
[notes] No rows: atom74.esm has no Static records (the header index has 0 STAT in it).
```
On the command line, inline SQL must end with `;`. A query cut at a line break by cmd.exe doesn't, and is refused.

## The schema tables
The schema is attached as `schema`. `sfs_describe` is the easy way in; SQL reaches everything.

| Table | Holds |
|---|---|
| `schema.RecordTypes` | `Sig`, `Type`, and how many fields each type has |
| `schema.Fields` | Every record type's fields as paths: `Type`, `Path`, `Kind`, `Subrecord`, `Readable` (the xEdit name) and more. Add `ORDER BY Sort` for file order |
| `schema.Enums` | Every enum's members and values |
| `schema.Objects`, `schema.ObjectFields` | Every object Mutagen defines, and its own fields, once |
| `schema.Meta` | The Mutagen version and commit the schema was made from |

```text
> query2 "SELECT Path, Kind, Subrecord, Readable FROM schema.Fields WHERE Type = 'Weapon' AND Path LIKE '%Damage%' ORDER BY Path;" --rows 5
| Path | Kind | Subrecord | Readable |
|---|---|---|---|
| AttackDamage | Float | WDMG | Base Damage |
…
[more] 5 of 14 rows shown. Next: cursor "5fc9c2-5".
```

## Stored queries (`sfs_saved`)
A stored query is a ready-made question in one `.sql` file, with a name, what it answers, and named parameters. 27 ship with SFSearch.

| `action` | Does |
|---|---|
| `list` (default) | Every stored query with its scope, parameters and what it answers |
| `show` | One query: parameters, columns, an example, its SQL |
| `run` | Runs it. Parameters go in `args` by name: `{"plugin": "kgcdoom.esm"}`. On the command line: `saved run cobj plugin=kgcdoom.esm` |
| `check` | Is the header in order, and does the SQL name real tables and columns? No plugin is read |
| `save` | Checks a query file (its text in `sql`; command line `--file`) and keeps it as yours |

| Name | Answers |
|---|---|
| `flst` | The FormLists of a plugin, or one list item by item |
| `vmad` | The scripts on records, with each property and its value |
| `appr` | The attach points of records |
| `kw` | The keywords on records |
| `templates` | Object templates and the modifications each puts on |
| `cobj` | Recipes: what they make, on which workbench, from what, under which conditions |
| `omod`, `omod-properties` | Object modifications: attach point, what they hold, the mesh; what they change, property by property |
| `arma` | Armor to armor addon to male and female mesh and body slots |
| `new`, `overrides`, `counts`, `find` | The records a plugin adds; the ones it changes and who wins each; records by type; records by a part of the EditorID |
| `items` | Weapons and armor a player can use: group, damage or protection, value, weight |
| `weapon-ammo` | Weapons with ammunition, magazine, projectile, damage |
| `leveled-adds`, `leveled-list` | What a plugin adds to leveled lists; what one list gives |
| `item-sources` | Where an item can come from: leveled lists, recipes, containers, NPCs, outfits |
| `recipes-by-workbench`, `recipes-needing` | Recipes counted by workbench; the recipes that need a given part |
| `multi-edits` | The records several loaded plugins change, by type |
| `broken-links` | Links to records that no plugin in Data has |
| `scripts-used` | Every script the plugin's records attach |
| `placed` | Where a plugin places an object or an NPC |
| `links-to`, `keyword-users` | What in a plugin points at a record; the records that carry a keyword |
| `zero-bounds` | Weapons and armor with object bounds of zero |

- **A parameter in [brackets] is optional.** `plugin` takes several plugins with commas. A record is given by EditorID or FormKey.
- **The answer starts with which query ran** and with what arguments. A file with several statements gives several tables; each has its own cursor (`t1:…`).
- **A name that is in two places is never guessed at.** The shipped ones are `shipped.<name>`, the user's `user.<name>`, an add-on's `<AddOn>.<name>`.

```text
> saved run cobj plugin=kgcdoom.esm --rows 3
shipped.cobj (plugins kgcdoom.esm; plugin=kgcdoom.esm): The recipes (ConstructibleObject) of a plugin: what each makes, on which workbench, from what, and under which conditions.
| FormKey | EditorID | Makes | MakesKey | MakesType | Amount | Workbench | Needs | Perks | Conditions | Filters | Description | IsOverride | Plugin |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 00B9F7:kgcdoom.esm | kgcDoom_co_CharacterPlushie_Arachnatron | kgcDoom_Plushy_Arachnatron | 00772B:kgcdoom.esm | MiscItem | 1 | WorkbenchIndustrialKeyword | 2 ResOrgCommonFiber, 1 ResOrgUncommonOrnamental |  | GetVMQuestVariable(kgcDoom_PlushieQuest, bArachnatron) = 1.0 |  | A craftable memory of your time battling Cultists on Mars. | 0 | kgcdoom.esm |
…
[more] 3 of 55 rows shown. Next: cursor "t1:e55f30-3".
[source] kgcdoom.esm 3.5 MB, key 9561e0c8
[cost] 90 ms; read ConstructibleObject (55) from kgcdoom.esm in 25 ms

> saved run kw
Error: shipped.kw reads the plugins it is given, and none was named. Fix: name one or more: plugin=Starfield.esm (several with commas), or ask for the winners or the versions scope.
```

### Writing one
A stored query is SQL with a header in SQL comments. This file was saved and run as shown:
```sql
-- name: heavy-guns
-- answers: The weapons of a plugin at or above a damage.
-- param: plugin | text | needed | The plugin to read, by file name.
-- param: min | number | optional, default 50 | The least damage.
-- scope: plugins
-- column: EditorID | The weapon.
-- column: AttackDamage | Its physical damage.
-- example: plugin=Starfield.esm min=80
SELECT EditorID, AttackDamage FROM Weapon WHERE AttackDamage >= :min ORDER BY 2 DESC, 1;
```
```text
> saved save heavy-guns --file C:\Modding\heavy-guns.sql
Saved as user.heavy-guns in C:\Modding\SFSearch\SFSearch\queries\heavy-guns.sql.

> saved run heavy-guns plugin=Starfield.esm min=100
user.heavy-guns (plugins Starfield.esm; plugin=Starfield.esm; min=100): The weapons of a plugin at or above a damage.
| EditorID | AttackDamage |
|---|---|
| DebugVorpalGun | 10000 |
| MagSniper | 223 |
| HardTarget | 116 |
| AutoRivet | 100 |
| SWL_Particle_SpaceTurret10 | 100 |
```
- **A parameter is a value.** The SQL names it `:name`; it is bound, never pasted in. Kinds: `text`, `number`, `pattern` (`*` any text, `?` one character; write `EditorID LIKE :name ESCAPE '\'`).
- **An optional parameter that isn't given is NULL:** `(:record IS NULL OR EditorID = :record)`.
- **Scopes:** `plugins`, `plugins with masters` (one plugin, its masters read with it), `winners`, `versions`.
- **A query over `Records` or `Links` needs a `-- reads:` line** that says which types to read first (`-- reads: Weapon, Armor`, `-- reads: every type`).
- **`save` checks first** and writes nothing when the check fails. Yours live in `queries\` of the SFSearch home; an add-on `X.dll` brings `X.queries\` beside it.
- The whole format, with every header line, is in `queries\README.md` of the repo.
