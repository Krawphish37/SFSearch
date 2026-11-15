# Stored queries

A stored query is a ready-made question in one `.sql` file. It has a name, says what it answers, and takes named values such as a plugin or a record. Nobody has to write SQL to use one.

## Use them
In SFSearch they are the `sfs_saved` tool (`action`: list, show, run, check, save) and the `saved` verb:
```powershell
sfs.cmd saved list
sfs.cmd saved show cobj
sfs.cmd saved run cobj plugin=MyMod.esm
sfs.cmd saved save my-query --file my-query.sql     # into queries\ of the SFSearch home, after a check
```
The agent guide lists them with real answers (`agent-docs\skills\sfsearch\references\sql.md`). The library's own runner takes the same words after `--saved`:
```powershell
$q = "SFSearch.Query.Runner\bin\Release\net10.0\sfsquery.exe"
& $q --saved list                                  # every stored query, with its parameters
& $q --saved show cobj                             # what one answers, its parameters, columns and SQL
& $q --saved run cobj plugin=MyMod.esm             # run it
& $q --saved run flst plugin=MyMod.esm record=MyList
& $q --saved run item-sources record=Eon           # reads the load order: no plugin needed
& $q --saved check                                 # every header and every statement, with no plugin read
& $q --saved save my-query --file my-query.sql --home <SFSearch home>   # into <home>\queries, after a check
```
- **A parameter in [brackets] is optional.** `plugin` takes several plugins with commas.
- **`--plugins a.esm`, `--winners` and `--versions` choose the scope** when the query's own isn't wanted. `--rows`, `--cursor`, `--json` and `--allow-heavy` work as for any query.
- **The answer starts with which query ran** (`[query] shipped.cobj …`), then each table with its notes, source and cost.

## Where they live
| Source | Folder | Full name |
|---|---|---|
| `shipped` | `queries\` in the repo (copied beside the program) | `shipped.cobj` |
| `user` | `<SFSearch home>\queries\` | `user.my-query` |
| an add-on `X.dll` | `X.queries\` beside `X.dll` in `<home>\extensions\` | `X.my-query` |

**A name that is in two places is never guessed at.** `run kw` then stops and lists `shipped.kw` and `user.kw`; ask for one by its full name. The answer always says which one ran, and names the other.

## The queries that ship
| Name | Answers | Reads |
|---|---|---|
| `flst` | The FormLists of a plugin, or one list item by item with place, type and name | the plugin |
| `vmad` | The scripts on records, with each property and its value | the plugin |
| `appr` | The attach points of records | the plugin |
| `kw` | The keywords on records | the plugin |
| `templates` | Object templates and the modifications each puts on | the plugin |
| `cobj` | Recipes: what they make, on which workbench, from what, under which conditions | the plugin |
| `omod` | Object modifications: attach point, target keywords, what they hold, the mesh | the plugin |
| `arma` | Armor to armor addon to male and female mesh and body slots | the plugin and its masters |
| `new` | The records a plugin adds | the plugin's record headers |
| `overrides` | The records of its masters a plugin changes, and who wins each | the plugin's record headers |
| `items` | Weapons and armor a player can use: group, damage or protection, value, weight | the plugin |
| `leveled-adds` | What a plugin adds to leveled lists | the plugin and its masters |
| `leveled-list` | What one leveled list gives | the load order |
| `item-sources` | Where an item can come from: leveled lists, recipes, containers, NPCs, outfits | the load order |
| `multi-edits` | The records several loaded plugins change, by type | every copy in the load order |
| `broken-links` | Links to records that no plugin in Data has | the plugin |
| `scripts-used` | Every script the plugin's records attach, and attachments that hold nothing | the plugin |
| `recipes-by-workbench` | Recipes counted by workbench, or the recipes of one | the plugin |
| `recipes-needing` | The recipes that need a given part | the load order |
| `find` | Records whose EditorID holds a text | the plugin's record headers |
| `counts` | Records by type: own and overrides | the plugin's record headers |
| `placed` | Where a plugin places an object or an NPC | the plugin |
| `omod-properties` | What modifications change, property by property | the plugin |
| `links-to` | What in a plugin points at a record | the plugin |
| `zero-bounds` | Weapons and armor with object bounds of zero | the plugin |
| `weapon-ammo` | Weapons with ammunition, magazine, projectile, damage | the plugin |
| `keyword-users` | The records that carry a keyword | the plugin |

## The format
A stored query is SQL with a header in SQL comments. Everything SQL can do in `sfsquery` it can do here (`SFSearch.Query\README.md` lists the tables and functions).

```sql
-- name: heavy-guns
-- answers: The weapons of a plugin at or above a damage.
-- Any other comment line up here is free text. "show" prints it.
-- param: plugin | text | needed | The plugin to read, by file name.
-- param: min | number | optional, default 50 | The least damage.
-- scope: plugins
-- column: EditorID | The weapon.
-- column: AttackDamage | Its physical damage.
-- example: plugin=Starfield.esm min=80
SELECT EditorID, AttackDamage FROM Weapon WHERE AttackDamage >= :min ORDER BY 2 DESC, 1;
```

| Header line | Means |
|---|---|
| `-- name:` | The name it is asked for by. Letters, digits, `-` and `_`. The file is `<name>.sql`. |
| `-- answers:` | One line: what it answers. Shown in the list. |
| `-- param: name \| kind \| needed or optional[, default value] \| what it is` | One line per parameter. The kinds are below. |
| `-- scope:` | The scope when the caller names none: `plugins`, `plugins with masters`, `winners` or `versions`. |
| `-- reads:` | Only for a query over `Records` or `Links`: what must be read first. See below. |
| `-- column: Name \| what it holds` | One line for every column a SELECT returns. |
| `-- example:` | Arguments that work. "show" prints them, and the tests run them. |

**Parameters are values.** The SQL names one as `:name`. The value is bound, never pasted into the SQL: text that looks like SQL is only text.

| Kind | The SQL gets |
|---|---|
| `text` | The text as written. |
| `number` | A whole number or a decimal number. |
| `pattern` | A part of a name: it matches anywhere, `*` stands for any text and `?` for one character. Write `EditorID LIKE :name ESCAPE '\'`. |

- **An optional parameter that isn't given is NULL.** Write `(:record IS NULL OR EditorID = :record)`.
- **A record is given by its EditorID or its FormKey** (`0476C4:Starfield.esm`). Write `(FormKey = :record OR EditorID = :record)`, or `formkey(:record)` to turn either into a FormKey.

**Four parameter names have a fixed meaning:**
| Name | Meaning |
|---|---|
| `plugin` | The plugins of a `plugins` scope. A query with that scope must have it. In a load-order query that never names `:plugin`, giving it reads those plugins and not the load order. |
| `type` | A record type (`Weapon`), a union view (`ObjectModification`) or a signature (`WEAP`). The SQL gets the signature: write `Sig = :type`. With a `reads` line, only that type is read. |
| `record` | With a `reads` line: only the types of that record are read. Use another name (`target`, `keyword`) for a record that is pointed at and not listed. |
| a `pattern` parameter | With a `reads` line: only the types that have a record whose EditorID fits are read. |

**The scopes.** `plugins` reads the plugins named, each as it is. `plugins with masters` takes one plugin and reads its masters with it, so the query can look into what the plugin points at: keep to the plugin with `Plugin = :plugin`. `winners` reads the version of each record the game uses. `versions` reads every copy in the load order, with `LoadOrder` and `IsWinner`.

**`-- reads:`** `Records` and `Links` hold only the record types read so far. A query over them says what it needs:
```sql
-- reads: every type with VirtualMachineAdapter
-- reads: every type
-- reads: Weapon, Armor
```
- Only types the scope has records of are read.
- **A type that is over the cost limit by itself is left out, and the answer says which** (the placed objects of Starfield.esm, for one). To read it, name it with `type=…` and allow heavy work.

**Several tables.** A file can hold several statements. Each SELECT gives a table. Statements that make temporary tables run before each SELECT.
```sql
-- table: FormLists                 (the table's heading)
-- unless: record, pattern          (skip this statement when one of them is given)
SELECT …;
-- table: Items of the lists
-- when: record, pattern            (run this statement only when one of them is given)
SELECT …;
```
Every statement needs its own `when` or `unless` line, a `CREATE INDEX` too.

**Things to know when writing one**
- **Join list views through a temporary table,** not a subquery per row: a view has no index. `CREATE TEMP TABLE x AS SELECT FormKey, Plugin, count(*) … GROUP BY FormKey, Plugin; CREATE INDEX x_key ON x(FormKey, Plugin);`
- **A temporary table forgets that FormKeys compare without regard to case.** A plugin writes a master's name as its own header spells it. Where a link is held against a record of another plugin, write `COLLATE NOCASE` on the comparison and on the index.
- **`edid(fk)`, `name(fk)`, `type(fk)`, `sig(fk)` and `winner(fk)` answer for any record in Data,** in or out of the scope. `versions(fk) IS NULL` means no plugin in Data has the record.
- **A name can start with a Creation Kit tag** (`<ID=0000793F>AA-99 Mk.II`: every weapon name of a plugin saved without string files has one). Since 2.0 the text columns of the typed views (`Name` and the like), `Records.Name` and `name(fk)` give the text without it. `Json` keeps the text as the plugin stores it, and so does `text(Json -> '$.Name')`. `display(Name)` takes the tag off a text that still has it; on a view's `Name` column it changes nothing, and the shipped queries keep it so that they read the same either way.

## Checking
`sfsquery --saved check` reads no plugin. For every query it checks:
- the header: a name, what it answers, a scope, every parameter declared and used;
- every statement, compiled and run against empty tables, in each case its `when` and `unless` lines allow: a table or column that doesn't exist is reported with the nearest real names;
- every column a SELECT returns is described, and every described column is returned;
- a query over `Records` or `Links` has a `reads` line.

`save` runs the same check first and writes nothing when it fails.

The test `stored-shipped` (`SFSearch.Query.Tests`) runs every shipped query against darkstar.esm, atom74.esm, kgcdoom.esm and Starfield.esm, and runs every header example. A query that names a view or column that no longer exists, gives an SQL error, or gives no rows where rows are expected fails it. A shipped query with no run in the test fails it too.
