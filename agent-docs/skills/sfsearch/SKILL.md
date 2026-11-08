---
name: sfsearch-pipeline
description: How to look up, compare and check Starfield .esm data with SFSearch, our own plugin database tool (the sfsearch MCP tools, or sfs.cmd). Covers which command answers which question, the traps that give wrong answers, and where the database layout and research commands are documented. Load before any .esm lookup, FormID name lookup, load-order or "who wins" question, patch check (Delta, Diff), mod-update check or mod reverse-engineering. Never write scripts to parse plugins instead.
---

# SFSearch

SFSearch is our own tool for reading Starfield plugins. It turns each `.esm` into a local database (`SFSearch\Cache\<plugin>.db`) and has commands to look up, compare and check records. Use it for every `.esm` lookup and every patch check. Don't write Python or C# scripts to parse plugins or Mutagen JSON: SFSearch already does that, and its answers have been checked.

## Running it
**The `sfsearch` MCP tools are the main way in** (set up as the repo's README says; the server is `SFSearch.Mcp`). Each tool takes plain values, so there are no PowerShell quoting traps: FormIDs stay text and SQL can span lines. The first call starts the SFSearch service by itself.

| Tool | Answers |
|---|---|
| `sfs_names` | What these FormIDs or EditorIDs are, in every plugin that has them |
| `sfs_record` | One record: `view` fields (Inspect), xedit (Read), json (Dump) or file (Live, with `links`, `chain`, `subrecords`) |
| `sfs_refs`, `sfs_conflicts` | What points at a record; who wins it in the load order and who edits it |
| `sfs_query` | SQL on plugins; `with_masters`, `with`, `load_order` (attaches `lo`), `scripts` (attaches `scr`) |
| `sfs_fields` | Every JSON path a record type has in `Data`, with counts and a sample: run it before writing `json_extract` for a type you haven't queried |
| `sfs_q` | The saved queries (`flst`, `vmad`, `appr`, `kw`, `templates`, `cobj`, `omod`, `arma`, `new`, `overrides`; `list`) |
| `sfs_loadorder`, `sfs_changes`, `sfs_scripts` | The load order with runtime slots; what changed since last time and which patches are at risk; Papyrus source |
| `sfs_delta`, `sfs_diff`, `sfs_plugin_diff`, `sfs_header`, `sfs_stats`, `sfs_profile`, `sfs_add`, `sfs_verifydb` | As the commands below |
| `sfs_run` | Any other command, as a list of arguments |
| `sfs_job`, `sfs_status` | Collect a long answer; the service's health |

- **A call waits up to 45 s.** A first read of a big plugin can take longer (Starfield.esm up to about a minute): the call then returns a job number, and `sfs_job` collects the answer. The service keeps working either way.
- **Answers over about 20,000 characters go to a file** in `SFSearch\Service\results`; the call returns the start, the size and the path. Read the file, or narrow the query.
- **Lines under `[notes]`** are warnings: a missing master, a plugin changed on disk, SF1Edit open, a patch whose overrides don't match their base. Pass them on when they matter.

**The command line still works**, for scripts and when MCP isn't loaded:
```powershell
& "<SFSearch folder>\SFSearch\sfs.cmd" <Command> [Args...]
```
- `sfs.cmd` works from any folder and rebuilds SFSearch by itself when its code has changed. While the service runs, every command goes to it.
- **`sfs.cmd --help` lists every command and flag**, and `sfs.cmd Q --list` lists the saved query macros. Look there for flags. This skill doesn't repeat them.
- Every command takes `--json` / `--csv` (the default is a Markdown table), `--maxwidth N` and `--raw` (no `[EditorID]` labels after FormKeys).
- A record can be given by EditorID or FormKey (`000CF2:darkstar.esm`, or just `CF2` inside its own plugin). A plugin is found in the Data folder by file name; most commands also take a full path.

### The service (what the MCP tools use)
- **Many agents at once.** A coordinator hands each command to one of several worker programs (8 by default), so commands run side by side; each agent gets its own place in the queue. Only commands that need the same plugin's record type read for the first time wait for each other. Tested 2026-11-07 with 32 clients at once and workers killed on purpose.
- **Crashes stay small.** A worker that crashes is replaced and its command runs once more; a command past 10 minutes, or a worker past 12 GB, is stopped with a clear error. Nothing hangs.
- **Its data** is in `SFSearch\Service\data`: one database per plugin, named after the exact copy of the plugin it was read from, filled one record type at a time as commands need them, kept between restarts (40 GB cap, least recently used dropped first). The stored databases in `Cache` are never used or changed by it.
- **It doesn't lock plugins.** SF1Edit and Vortex can save between commands. A plugin changed on disk is read again, and the answer says so. It never shuts itself down when idle, and an open SF1Edit only adds a note.
- **Warm-up:** after 30 s with no agent command it reads, in the background and only while no agent is waiting, the load-order index and the common types (weapons, armor, recipes, OMODs, keywords, lists) of the base game files (Starfield.esm, ShatteredSpace.esm) and the 15 plugins agents asked about most recently. Nothing is tied to one mod list, so it suits any load order. After a game or mod update it reads the changed files again. `Service status` shows how far it got.
- **Settings: `SFSearch\sfsearch.json`**, every line explained inside. It covers the warm-up (on or off, what to read: plugin names, patterns such as `SFBGS*.esm`, `@base`, `@official`, `@recent`, `@loaded`; which types; after updates) the service's limits (workers, time limit, memory, queue, data cap), the **item filter** (`itemFilter`: how weapons and armor are sorted into groups and what counts as junk; defaults are the built-in rules (the All-in-One Patcher's `UniversalItemFilter`), and a database built with different rules is rebuilt by itself), and `champollion` (the path to `Champollion.exe` for `Scripts`; otherwise `SFSEARCH_CHAMPOLLION`, `tools\champollion\` in the home folder). Saved edits apply without a restart. `sfs.cmd Service config` (or `sfs_run ["Service","config"]`) shows what's in effect. The user owns this file: change it only when asked.
- **Which Data folder:** `SFSEARCH_DATA`, then `Cache\DataPath.txt`, then the Steam or Xbox install found by itself. `sfs.cmd SetData "<path to Data>"` writes `DataPath.txt`.
- `sfs.cmd Service status | stop | restart | start` (`Session` is the same command). **After SFSearch is rebuilt, `Service status` says the service runs the old build: `Service restart`.** Its log is `Service\logs\service-<date>.log`.

### Other ways to get the data (same commands, same answers)
| Way | How | When |
|---|---|---|
| Stored databases | Run `sfs.cmd` commands with no service running | A plugin's database is built the first time it's needed and kept in `Cache` |
| `--live` | Add `--live` to any command (no service running) | One-off checks with no plugin database kept: about 1 s for most mods, up to about a minute when a command needs all of Starfield.esm |

The load-order index (`Cache\_loadorder.db`), the history (`Cache\History.db`) and decompiled scripts (`Cache\Scripts.db`) are kept in `Cache` in every way.

## Which command answers which question
| Question | Command |
|---|---|
| What is this FormID? (to write "EditorID (FormID)") | `Names "id1 id2" [--in p1,p2]` |
| Show me one record | `Inspect <plugin> <record>` (fields), `Read` (xEdit layout), `Dump` (raw JSON) |
| Read a record straight from the file, no cache | `Live <plugin> <record>` (`--chain`, `--links`, `--vs-cache`, `--subrecords`) |
| One field from every record of a type, including what the cache skips (ship parts, dialogue, statics) | `Live <plugin> --sig <Type> --field "Components[*].SpaceshipAIActor"` (`--filter "EDID*"`, `--value text`, `--new`, `--empty`, `--all`). Never loop `Live` per record |
| What points at this record, including types the cache skips | `Live <plugin> <record> --refs [--with p1,p2]`. Use `Refs` first: it's faster, and with no `--in` it searches every plugin in Data that can point at the record |
| Compare two records | `Diff <p1> <p2> <rec1> [rec2] --summary` (`--expect f1,f2` exits 1 on any other change) |
| What does this plugin change in its masters? | `Delta <plugin> [FormKey or EDID] [--detail] [--out f.md]` |
| Who wins this record, and who edits it? | `Conflicts <record>`: in the real load order (plugins.txt), each row with what it changes against the one before; plugins in Data that aren't loaded are listed apart |
| Which version of many records wins in game? | `Query --loadorder "<SQL>"` on `lo.Winners` (FormKey, Plugin, Signature, EditorID, Versions, Pos) or `lo.WinningRecords` (the winners' full `Data`, for the types the SQL names with `Signature = '…'`); `lo.Versions` lists every copy. Functions `winner(fk)`, `versions(fk)`. Columns in `references/database.md` |
| The load order, with console prefixes | `LoadOrder [plugin] [--all]`: position, runtime slot (`0A`, `FD:03`, `FE:09A` → console `FE09Axxx`), size, records, missing or late masters |
| What changed since last time? Is a patch broken by a mod update? | `Changes [plugin] [--since yyyy-mm-dd]`: mods added, removed or updated (Full ↔ Light, records added or removed), and patches at risk (trap 15). With a plugin: its history and the records its last update added or removed |
| What does this mod's Papyrus do? | `Scripts <plugin> [--find text] [--show ScriptName]`: decompiled once with Champollion and kept; `Query --scripts` joins `scr.Scripts` / `scr.Lines` to records |
| What points at this record? | `Refs <record> [--in plugins]` |
| How does the player get this mod's items? | `Sources <plugin> [--item X] [--with p1,p2] [--scripts] [--out f.md]`: follows Refs back through leveled lists to quests, recipes (with conditions), vendors, LL injector scripts, containers, placed copies, NPCs; `--scripts` lists scripts calling AddItem / GetFormFromFile |
| Header flags, masters, Light/Medium room left | `Header <plugin>` |
| How many records of each type? | `Stats <plugin>` |
| Are all meshes and materials there? | `Assets <plugin> [--missing]` |
| What changed between two versions of a plugin, or what does a patch change in the plugin it masters? | `PluginDiff <old> <new> [--detail]` |
| Would a Mutagen read and save lose anything in this plugin? | `LossCheck <plugin or path> ... [--all] [--report file] [--samples N]` saves to a temp folder through both readers, never Data. LOST, CHANGED and ADDED are listed, and NOTE for a deleted record saved as its header only; order, FormID style and empty groups aren't counted. |
| A type isn't in the database and I need more than `Live` gives (SQL, `Refs`, many records) | `Add <plugin> <Type> ...` stores it in full, with no rebuild. `Add <plugin> --list` shows what's missing, with row counts. `--all` gives the whole plugin. Safe to stop and run again; a rebuild adds the types back. |
| Is this plugin's database right? | `VerifyDb <plugin> [--report file] [--samples N]` checks the rows in the database against the plugin file: every record present, headers, links, parents, read-back, field values. Prints PASS or the problems. Field checks sample up to 200 records per type. Starfield.esm with Climate added always shows 1 problem: ClimateNone (3D248E:Starfield.esm) has a `TNAM` one byte short, which is harmless. |
| Full dossier before patching a new mod | `Profile <plugin> [--against darkstar.esm] [--out file.md]` |
| How does mod X build its Mk.II / Mk.III? | `MkDiff <plugin> --sig Armor` or `--sig Weapon` |
| How are its quality tiers built? | `OmodCensus <plugin> "<EditorID glob>"` |
| How are its recipes built? | `RecipeCensus <plugin> [--bench <keyword>] [--filter <glob>]` |
| FormLists, scripts, APPR, keywords, templates, recipes, OMODs (with their mesh), armor meshes | `Q flst` / `vmad` / `appr` / `kw` / `templates` / `cobj` / `omod` / `arma` `<plugin> [filter]` |
| Only the new, or only the override, records | `Q new` / `Q overrides <plugin> [Signature]` |
| Which JSON paths does this type have? | `Fields <plugin> <Type> [--filter text]`: every path in `Data` (arrays as `[*]`), how many records have it, a sample value |
| Anything else | `Query <plugins> "<SQL>"` or `Query <plugins> --file x.sql` |

Try the purpose-built command first. Write SQL only for what they don't cover.

## Traps that give wrong answers
1. **Quote FormIDs in PowerShell** (the MCP tools don't need it). An unquoted `0003E6` is read as the number 3000000. One quoted string can hold many IDs: `Names "003A8F 0003E6"`.
2. **The same six digits are usually different records** in Starfield.esm, darkstar.esm and DSM. `Names` lists every plugin that has a bare ID: pick from context, never just the first row.
3. **New vs override: use the `IsOverride` column.** Never "the FormKey's plugin = the cache name". A plugin read by path gets a cache named `_file1_<name>.esm`, but its FormKeys keep `<name>.esm`.
4. **Search raw data by FormKey, not by EditorID.** The `[EditorID]` after a FormKey is added only when printing. The stored data holds just `27CBC3:Starfield.esm`, so `Data LIKE '%Skill_SpacesuitDesign%'` or `instr(Data,'DarkStarDetected')` silently finds 0. Match `'%27CBC3:Starfield.esm%'` instead.
5. **Armor and weapon OMODs** are stored as `ArmorModification` / `WeaponModification`, not `ObjectModification`.
6. **Vanilla text inside `Data` is German first.** Use the `Name` column (already English) or `en(json_extract(Data,'$.Description'))`, never `->> '$[0]'`.
7. **Caches follow the plugin file, but one name = one cache.** Each cache remembers the size, date and folder of the file it was built from and rebuilds by itself when any of them changes, even to an older date (a mod manager keeps the archive's date). Masters are refreshed too, so a mod update needs no manual cache deletion. Caches made before 2026-10-25 rebuild once on first use (Starfield.esm took about a minute). A path (`"C:\…\x.esm"`, a `.bak`) works in every command and gets its own `_file1_<name>` cache, apart from the Data copy's. Two different files with the same name read by path share that one cache and rebuild each time you switch, so for repeated comparisons copy them under unique names (`zold_x.esm`, `znew_x.esm`).
8. **PluginDiff collapses repeated EditorIDs.** When the old file repeats an EditorID, removed duplicates show as neither Removed nor Added. Count records per type with `Query` to catch it.
9. **Not every record type is cached in full.** Dialogue, scenes, navmeshes, cells, worldspaces, planets, biomes, statics, lights, sounds, visual records and ship-builder parts only get a `Forms` row (FormKey, type, EditorID). Parts share the type GenericBaseForm with whole ships, and are told apart by name (SM + letters + _, such as `SMS_` or `SMX_`; `Stats` shows the type as "Partly"). Whole ships, their spawn lists (LeveledBaseForm) and locations (Location, with encounter levels at `$.Owner.EncounterMinLevel`) are cached in full since 2026-11-04. In a plugin with more than 50,000 placed references (Starfield.esm, ShatteredSpace.esm, some big mods such as nfvc0003vigilance.esm; `Meta` `PlacedMode` tells) only placed objects with scripts are cached in full; placed NPCs always are, flags such as Starts Dead included. Why they're left out: terrain, navmeshes, cells, worldspaces and unscripted placed objects are huge in the big plugins, and no patcher reads them; planets, biomes, dialogue and scenes only matter for world and quest mods; statics, lights, sounds and visual records are small but rarely asked about. Read such a record with `Live`, or store its type in that plugin's database with `Add <plugin> <Type>` (no rebuild). Ask the user before `--all`, or before adding a multi-gigabyte type to Starfield.esm or ShatteredSpace.esm. Sizes in Starfield.esm: Static 2 s and 27 MB of text, Cell 61 s and 2.6 GB, NavigationMesh 88 s and 3 GB, SurfaceTree 4.7 GB; everything with `--all` takes about 20 minutes and 50 GB, mostly link rows from surface trees (197 million of 209 million). **Pack-ins (PackIn) and moveable statics (MoveableStatic) are cached in full since 2026-11-01**, with their own scripts (`Q vmad`) and keywords (`Q kw`, `Refs`). The objects a pack-in places are placed references in its storage cell: find them through `Forms.Parent` = the pack-in's `Cell`.
10. **Inline SQL on the command line must be one line** (the MCP tools take any SQL). `sfs.cmd` cuts an argument at its first line break, and the cut query can still run and return a wrong answer with no error. Put longer SQL in `--file x.sql`, or pipe it in: `$sql | & sfs.cmd Query <plugin> --stdin`.
11. **The 2-minute limit is the shell tool's timeout, not SFSearch's.** Slow SQL is usually `json_each` re-run for every row. Extract once into a temp table, index it, then join (pattern in `references/database.md`). If it's still slow, run it in the background.
12. **PluginDiff needs the master link to check a patch.** When one file masters the other it compares each record with the same FormID, and leaves out what the patch doesn't contain. If the patch's master was renamed, it can't pair them and prints a WARNING that overrides weren't compared: give the plugin under the file name the patch masters.
13. **Assets given a path checks the archives in Data.** It matches them by the plugin's real file name, so archives sitting beside a copy outside Data aren't read.
14. **A missing Small master makes a plugin's FormIDs wrong.** Mutagen needs each master's size, and a missing file has none, so records read as overrides of the wrong plugin and links land on the wrong one. Every command warns (`WARNING ... needs master(s) that aren't in Data`) and `VerifyDb` counts it as a problem. Don't trust those results: put the master in Data and the database rebuilds by itself. `Live --loader gameenv` also shows a few false "unresolved" links: trust the normal `--links`.
15. **A patch built before its master changed size reads wrong, with no warning.** FormIDs count masters by size, so when a Full master later becomes Light, the patch's records land on the wrong plugins. Signs: almost no new records (`IsOverride` = 1 on nearly everything), and overrides whose type differs from their base. **`Changes` lists them** ("Patches at risk"), `LoadOrder` notes them, and every command that reads such a plugin prints a WARNING, once the load-order index has checked it. Known (2026-11-07): darkstarpatch_bountyhunteroutfitz.esm and darkstarpatch_crimsonx.esm, both broken by later updates of the mods they patch.
16. **SQL with no `ORDER BY` can list rows in a different order each time.** The service fills a plugin's database one record type at a time, so its row order isn't the file's. Add `ORDER BY` whenever order matters, and compare results as sets; `group_concat` lists too.

## Output
- Never write a bare FormID in a reply or a note. Run `Names` and write `EditorID (FormID)`.
- For long results, use `--out report.md` and point the user to the file.

## Detail files (read when needed)
- `references/database.md`: tables and columns, attaching masters, SQL functions, multi-statement files. Read before writing SQL.
- `references/research.md`: MkDiff, OmodCensus, RecipeCensus and Profile in depth, plus the options of the lookup and compare commands.
- `queries/quality_keyword_check.sql`: lists quality OMODs that can never attach because the item lacks their target keyword. Run it on every generated patch: `Query <patch> --file "<path to it>"`.

## Keeping this skill up to date
- **Edit in place.** When a command, flag or trap changes, change its line here. Don't append dated notes.
- A new command gets its full description in SFSearch's `--help`, one row in the table above, and a section in `references/` only if it needs more.
- Keep this file under about 150 lines. If it grows past that, move detail into `references/`.
