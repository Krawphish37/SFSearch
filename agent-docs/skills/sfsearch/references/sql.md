# SQL in SFSearch

`sfs_query` runs SQL (SQLite) over plugin data that is read live. There is no database to build or keep: the record types a query names are read from the plugin files the first time, into scratch tables that live only while the service runs.

Every answer below is real, cut with "…" where long. The examples are written as the command line's `query2`; `sfs_query` takes the same SQL in `sql` (over several lines, no `;` needed) and prints the same answer.

```text
> query2 kgcdoom.esm "SELECT EditorID, Name, AttackDamage FROM Weapon ORDER BY 1;"
| EditorID | Name | AttackDamage |
|---|---|---|
| kgcDoom_WEAP_CrucibleBlade | Crucible Blade | 37 |
| kgcDoom_WEAP_SuperShotgun | Super Shotgun | 80 |
[source] kgcdoom.esm 3.5 MB, key 9561e0c8, strings 2d590079
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
| `Forms` | Every record of the scope from the header index, read or not | `FormKey`, `Sig`, `Type`, `EditorID`, `Plugin`, `Parent`, `IsWinner`, `IsOverride`, `Pos`, `LoadOrder`, `Size`, `DataSize`, `Compressed`, `Flags`, `FlagNames` |
| `Records` | Every record **read so far** in this scope | `FormKey`, `Type`, `Sig`, `EditorID`, `Name`, `Plugin`, `IsOverride`, `Json` |
| `Links` | The links of the record types **read so far** | `FromKey`, `FromType`, `Field`, `Idx`, `ToKey`, `Path`, `Plugin` |
| `Conditions` | Every condition (CTDA) of the record types **read so far**, one row each | `FormKey`, `Type`, `EditorID`, `Plugin`, `Field`, `Idx`, `Path`, `Function` (the index, 961), `FunctionName`, `RunOn`, `Reference`, `Comparison`, `Value`, `Global`, `Flags`, `FlagsRaw`, `Param1`, `Param2`, `Param3`, `Json` |
| `Plugins` | Every plugin in Data, one row each, read from each file's header alone (whatever the scope) | `Plugin`, `LoadOrder`, `Slot`, `Size` (Full, Medium, Light), `Flags`, `FlagNames`, `FormVersion`, `Version`, `NumRecords`, `NextFormID` (HEDR), `MasterCount`, `Masters` (a JSON list), `Author`, `Description`, `OverriddenCount`, `InteriorCellCount`, `Subrecords`, `HeaderSize`, `FileSize` |
| `schema.Fields`, `schema.RecordTypes`, `schema.Enums`, … | The schema `sfs_describe` reads | See "The schema tables" |

- **Column names are Mutagen's field names.** A field inside an object joins the names with `_`: `ObjectBounds_First`, `Model_File`, `Transforms_InventoryIcon`. `sfs_describe WEAP` lists a type's fields; never guess.
- **A link column holds a FormKey** (`0476C4:Starfield.esm`), or NULL for a null link. FormKeys, EditorIDs and plugin names compare without regard to letter case.
- **`IsOverride`** is 1 when the plugin changes a record of one of its masters, 0 for its own record.
- **`Json`** is the whole record as Mutagen's JSON. Read a field that has no column with `Json ->> '$.Path'`.
- **Enums are names, flags are a JSON list of names** (`["AutoCalcStats"]`); the flags' list view has one row per name.
- **Each flags column also comes as its number,** `<Field>Raw`, right after it (`EntryTypes`, `EntryTypesRaw`): every bit that is set, named or not, as an integer of the enum's width (bit 31 is 2147483648). Test a bit by its number: `(EntryTypesRaw & 0x80000000) <> 0`, the same in every type. The record header's flags have theirs already (`MajorRecordFlagsRaw`, `Forms.Flags`); `Conditions` has `FlagsRaw`.
```text
> query2 Starfield.esm "SELECT count(*) AS Markers, sum((EntryTypesRaw & 0x80000000) <> 0) AS EntryBit31, sum((ExitTypesRaw & 0x80000000) <> 0) AS ExitBit31 FROM Furniture_MarkerParameters;"
| Markers | EntryBit31 | ExitBit31 |
|---|---|---|
| 976 | 976 | 976 |
[source] Starfield.esm 1.36 GB, key d1c0e07a, strings 4b75842f
[cost] 6.47 s; read Furniture (899) from Starfield.esm in 2.67 s
```
- **`Forms.Parent`** is the record a record sits under: a placed reference's cell, a topic's quest.
- **`Forms.DataSize`** is a record's subrecords in bytes as read (inflated when compressed; `Size` is as stored): the biggest records of a type are `SELECT EditorID, FormKey, DataSize FROM Forms WHERE Type = 'Cell' ORDER BY DataSize DESC LIMIT 5`.
- **`Forms.Flags`** is the record header's flags as the file stores them (bytes 8-11), so a bit is counted by type without reading a record: `SELECT Sig, count(*) FROM Forms WHERE Flags & 0x40000000 GROUP BY Sig`. `FlagNames` names them by Mutagen's enum for the record's type (`NonPlayable`), plus `Deleted` and `Compressed`; a bit Mutagen has no name for is its number (`0x40000000`).
- **`Conditions`** reads what a statement narrows it to (`WHERE Type = 'Perk'`, `FormKey`, `EditorID`), or, narrowed to nothing, every type of the scope that can hold a condition (its cost said first). Functions by count: `SELECT Function, FunctionName, count(*) FROM Conditions GROUP BY 1, 2 ORDER BY 3 DESC`. Across many plugins at once (`@official`, `@all`) the census is `profile --conditions`.
- **`Plugins`** is the plugins' headers: which are light or medium, which have the most records or masters, which need a given master (`json_each(Masters)`). It reads no record.
- **A list view of a huge type (`PlacedObject_Traversals`, `Cell_Traversals`, `NavigationMeshInfoMap_MapInfos_Traversals`) reads only the records that hold the list's subrecord**, found in the plugin's bytes, when the query names no column of the type's own view: joins of two such lists across many plugins run in minutes, not "Not run". A note says how many records held it.
- **`Records` and `Links` hold only the types a query has named so far.** A type is read when a query names its own view. To count every record, use `Forms`. A note says so when a query names `Records` or `Links`. A query narrowed by `ToKey`, `FromKey` or `FromType` reads first the types it needs (for `ToKey`, the types whose records link to it, from the link index and the schema), and says which.
- **A query may make temporary tables and indexes of its own,** in several statements. The last `SELECT`'s rows are the answer. It can't change anything else: `Error: a query may read, and may make temporary tables and indexes of its own; it can't run ATTACH.`

## The three scopes
| `scope` | Rows | When |
|---|---|---|
| `plugins` | Each plugin named in `plugins`, as it is. A `Plugin` column tells them apart | The default when plugins are named |
| `winners` | For each record, the version the game uses: the last loaded plugin that holds it | The default when no plugin is named |
| `versions` | Every copy in every loaded plugin. Every typed view also has `LoadOrder` and `IsWinner` | On request |

`plugins` takes names in Data separated by commas, names with `*` (`sfta*.esm`), a set: `@official` (the game's own plugins and Bethesda's Creations, 33 here), `@loaded` (every plugin the load order enables), `@all` (every plugin in Data), `@winners` (the winners scope); or the full path of one plugin file elsewhere. A set's plugin that can't be read is left out, with a note, and `[source]` starts with how the set was named: `@official (33 plugins)`. `find --in`, `refs2 --in`, `saved run --plugins` (and `plugin=`), `scripts2`, `indexes --in` and `profile --in` take the same forms. A file outside Data is read with its masters from Data, and `[source]` shows its path.
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
[notes] MissionsGalore.esm is left out: Mutagen refuses it: DuplicateFormKeyException: MissionsGalore.esm refused: 2 pairs of its records share a FormKey (the plugin was probably written for another master list); …
[source] Starfield.esm 1.36 GB, key d1c0e07a, strings 4b75842f; Constellation.esm 38 KB, key a7ae4650, strings 446208fc; … and 218 more plugins (811.8 MB); load order of 2026-11-10 06:48 (224 loaded plugins)
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
- **Such a plugin may still ship strings tables** (darkstar.esm: nine languages in its archive, each text under the ID of its tag). Asked in another language than English (`text(Name, 'de')`, or `language: "de"`), a tagged text is that language's table's text, with a note; in English, and where the tables don't hold the ID, it is the plugin's own. `Json` has both: `"Value"` (the plugin's) and `"Tables"` (each language whose table says something else; English is among them when the plugin's text was changed after the tables were made). `sfs_record`'s card and fields say the same. `name(fk)` and `sfs_find`'s text search read the plugin's own text only.

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
| `wwise(guid)` | A sound's event GUID in the form Wwise and xEdit show (`{59A5EC16-836F-4D41-A2D7-FC1B91F934D7}`); a GUID column itself gives the text Mutagen reads |
| `wwise_name(guid)`, `wwise_path(guid)` | The event's name (`Play_WPN_Pistol_Eon_Equip_Up`) and its path in the Wwise project (`\Events\Starfield_WPN\WPN_Hand\Pistol\Eon\…`), from the sound banks' descriptions in Data (the game's, and each mod's that ships one). NULL when none names the GUID. `sfs_record`'s card shows both beside the GUID |

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
[source] Starfield.esm 1.36 GB, key d1c0e07a, strings 4b75842f
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
| kgcDoom_MGEF_EnchShotgunCloak | MagicEffect | VirtualMachineAdapter.Scripts[*].Properties[*].Object | VirtualMachineAdapter.Scripts[0].Properties[2].Object |
| kgcDoom_Perk_BonusDamageToPredators | Perk | Ranks[*].Effects[*].Conditions[*].Conditions[*].Data.FirstParameter | Ranks[0].Effects[0].Conditions[0].Conditions[0].Data.FirstParameter |
| kgcDoom_WEAP_CrucibleBlade | Weapon | Keywords | Keywords[7] |
| kgcDoom_WEAP_SuperShotgun | Weapon | Keywords | Keywords[7] |
[notes] Read first, because Links holds only the record types read: 10 types, PlacedNpc, Cell, MagicEffect, NavigationMeshInfoMap, NavigationMesh, Perk, PlacedObject, SurfaceBlock, Weapon, Worldspace (the types whose records link to 0076BE:kgcdoom.esm (link index), and those the index doesn't cover that can (schema)).
```
```text
> query2 "SELECT Plugin, Size, NumRecords, MasterCount FROM Plugins ORDER BY MasterCount DESC, Plugin;" --rows 5
| Plugin | Size | NumRecords | MasterCount |
|---|---|---|---|
| PandaWorks AutoLoot - Watchtower.esm | Light | 5 | 10 |
| SVF-Watchtower-Patch.esm | Light | 21 | 10 |
| df_weaponmod_tapatch.esm | Light | 195 | 10 |
| PandaWorks AutoLoot.esm | Light | 980 | 8 |
| atbb_watchtower.esm | Light | 46 | 8 |
[more] 5 of 370 rows shown. Next: cursor "f887c6-5".

> query2 "SELECT p.Plugin FROM Plugins p, json_each(p.Masters) m WHERE m.value = 'darkstar.esm' ORDER BY 1;" --rows 5
| Plugin |
|---|
| DarkStarTGO_Fixes.esm |
| GB_SBCX_DSTGO.esm |
| darkstarpatch_aa50longwatch.esm |
| darkstarpatch_aseveil.esm |
| darkstarpatch_astra19.esm |
[more] 5 of 58 rows shown. Next: cursor "ec9f21-5".

> query2 kgcdoom.esm "SELECT Function, FunctionName, count(*) AS N FROM Conditions GROUP BY 1, 2 ORDER BY 3 DESC, 1;" --rows 3
| Function | FunctionName | N |
|---|---|---|
| 874 | IsResearchComplete | 30 |
| 426 | GetIsVoiceType | 13 |
| 629 | GetVMQuestVariable | 12 |
[more] 3 of 15 rows shown. Next: cursor "66147f-3".
[notes] Read first, because Conditions holds only the record types read: 53 types, PlacedNpc, Activator, ArmorAddon, … (every record type that can hold a condition of these plugins, because the query narrows to none …
```
- `Links.Field` is the field with list positions left out (`Keywords`, `ObjectTemplates[*].Includes[*].Mod`); `Path` is the exact place; `Idx` the innermost list position.
- **For "what points at this record" use `sfs_refs`,** which searches every type. `Links` is for joins inside a query. `Links WHERE ToKey = '…'` also gives the links from world records (placed objects, cells, navmeshes) of the plugins that have a world link index (`sfs_run ["indexes", "build", "world"]`); without one, world types are left out unless the query names them.
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
> query2 "SELECT count(*) FROM Cell WHERE Flags IS NOT NULL;" --plugins Starfield.esm
Not run. Cell in Starfield.esm is 30,717 records (473.4 MB): about 27 s and about 1.5 GB to read (only the column the query names: Flags, out of each record's JSON; rate measured on Starfield.esm's Cell, 2026-11-15). Call again with allow_heavy=true (command line: --allow-heavy) to run it now. …
```
Such an answer gives three answers: `allow_heavy: true` runs it; leaving it out runs nothing; and where the world link index would make that kind of question fast (`Links WHERE ToKey` over world data), `allow_heavy: "save_index"` runs it and then saves the index, with its size and build time. Otherwise the line says no index would help, as here.

A type too big to read whole is read for the columns the query names only; a column the records' own bytes give (a cell's Location, a placed object's Base) is read from them in about a second, and `col = 'literal'` on it keeps only the matching rows, with no `allow_heavy` needed: `SELECT count(*) FROM PlacedObject WHERE Base = '00003B:Starfield.esm'` 0.55 s on Starfield.esm, 1.2 s over the whole load order (winners or versions). A query that needs nothing of the records (`SELECT count(*) FROM PlacedObject`) is answered from the header index. Where the bytes of some records don't say the answer alone, those records are read through Mutagen, and the `[cost]` line counts them (`from the records' own bytes; 12 records through Mutagen, where the bytes alone don't say`): the answer is the same, the read slower.

A query is held to the memory one command may use (`workerMaxMB`, 1.5 GB on an 8 GB PC; settings that allow all memory let it use all of it). Big sorts and groups go to temporary files instead of memory, so they finish on any PC. A statement that would still pass the limit, or whose temporary files would fill the drive, stops with what to do:
```text
Error: the query was stopped: it took this command to 1.4 GB of memory, and one command may use 1.5 GB. Fix: narrow it: fewer rows into the join or the GROUP BY (a WHERE on a plugin, a FormKey or a column), fewer columns, and no Json (or a column of it) in a GROUP BY over a join; or raise workerMaxMB in sfsearch.json if this PC has the memory to spare.
```
`Json` itself in a grouped join (in the result, in an aggregate such as `max(c.Json)`, in GROUP BY) keeps each record's whole JSON for every row of the join. Such a query is said before it runs, with its size, when its temporary files would pass what one query may write (4 times `workerMaxMB`, 4 to 32 GB):
```text
Not run. This query's GROUP BY would keep the whole Json of Cell for each of its 29,141 rows: about 34.1 GB of temporary files, about 3 min to write and sort. One query may write 6.0 GB (4 times what one command may use). Narrow it: name the fields you need instead of Json (a typed column, or Json ->> '$.Field'), or fewer rows into the sort (a WHERE on a plugin, a FormKey or a column). Call again with allow_heavy=true (command line: --allow-heavy) to run it now. No saved index would make this faster: a sort's temporary files hold the rows it sorts, and SFSearch keeps indexes only of where records and links are.
```
`allow_heavy: true` runs it when the drive has room (half its free space, keeping 10 GB free); when it hasn't, the answer says so and allow_heavy won't run it. The same holds for an ORDER BY or DISTINCT whose result has `Json` itself (or `*`). A column of the typed view (`c.Location`), or `Json` named only in WHERE, is not kept. The estimate looks at every SELECT of the statement: a sort inside a CTE or a subquery, the parts of a compound SELECT (`UNION`, `ORDER BY` after the last part), a DISTINCT (each distinct row once), and the other big columns (a list view's entry `Json`, a typed view's JSON columns) as well as a record's `Json`; the answer says where ("This query's GROUP BY in its CTE x …", "… ORDER BY in part 1 of 2 …"). A big value a subquery or CTE hands up (`SELECT * FROM (SELECT c.FormKey, c.Json FROM …) ORDER BY 1`) is counted with the sort of the SELECT around it ("… would keep x.j (the whole Json of Cell) …"). What it can't see before running stops with "its temporary files passed … GB" when the files pass the limit: a correlated subquery, a recursive CTE, and a value SQLite only knows once made (an aggregate's result such as `group_concat`, or an expression's such as `Json ->> '$.Field'`).

A join that names a typed view's `Json` (or one of its JSON columns) makes each record's JSON once, not once per row of the join, and what the statement works out of one record's JSON alone (`length(c.Json)`, `c.Json ->> '$.EditorID'`, `json_array_length(c.Json, '$.Traversals')`, `c.Json LIKE '%x%'`, `coalesce(c.Json, '{}') ->> '$.X'`, `c.Json IS NULL`, a `CASE` over it) is worked out once per record too, in any join: grouped, a LEFT JOIN, a FROM with subqueries, a correlated subquery, or through a subquery that hands the Json up (`length(x.Json)` over `(SELECT FormKey, Plugin, Json FROM Cell) x`). Over the cell NAViewport's 6 MB JSON and its 29,070 traversals `length(c.Json)` takes under a second (163 s before). What needs the whole JSON on each row is still made on each row, as the answer holds it there: `SELECT c.Json`, `GROUP BY c.Json`, `max(c.Json)`, `json_each(c.Json)`, `c.*`, or an expression that also names another table's column.

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
[source] kgcdoom.esm 3.5 MB, key 9561e0c8, strings 2d590079
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
- **A query over `Records`, `Links` or `Conditions` needs a `-- reads:` line** that says which types to read first (`-- reads: Weapon, Armor`, `-- reads: every type`).
- **`save` checks first** and writes nothing when the check fails. Yours live in `queries\` of the SFSearch home; an add-on `X.dll` brings `X.queries\` beside it.
- The whole format, with every header line, is in `queries\README.md` of the repo.
