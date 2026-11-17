---
name: sfsearch
description: Read this before any lookup in a Starfield plugin (.esm, .esp). SFSearch reads the plugins live and answers what a FormID, EditorID or in-game name is, shows records and fields, runs SQL, finds what points at a record, says who wins in the load order and what each plugin changes, compares patches, and checks a plugin, its assets and its scripts. This guide says which sfsearch tool (sfs_find, sfs_record, sfs_describe, sfs_query and eight more, or the sfs.cmd command line) answers which question and how to read the answer. Never write a script to parse a plugin instead.
---

# SFSearch

SFSearch reads Starfield plugins live through Mutagen and answers questions about them. Nothing about a record is stored, so no answer is stale. Use it for every plugin lookup, comparison and check; don't write a parser.

It never changes a plugin. Use the `sfsearch` MCP tools when they are loaded; otherwise the command line (below). The first call starts the SFSearch service by itself.

## Which tool answers which question
Each example call was run as written. `references/research.md` shows what the tools print.

| The question | Tool | Example call |
|---|---|---|
| What is this FormID, FormKey or EditorID? (several at once) | `sfs_find` | `{"query": "0003E6 000AA2", "in": "darkstar.esm"}` |
| A FormID with no plugin named: which plugins have it? | `sfs_find` | `{"query": "000CF2"}` lists every record with that ID and picks none |
| What is it called in game? Which record has this in-game name? | `sfs_find` | `{"query": "Super Shotgun", "in": "kgcdoom.esm"}` |
| Which records have this text or character in a name, description, book, dialogue line, quest log entry, game setting or perk text? Text outside plain ASCII? | `sfs_find` | `{"text": "(?i)galaxy handbook"}`, `{"text": "[^\\x00-\\x7F]", "from": "stored"}`; `"language": "de"` searches the German strings files; `"world": true` adds map markers' names; `"plain": true` adds the fields that are never translated (EditorIDs, quest log notes, script names and string properties): `{"text": "LLInjection", "plain": true, "in": "atom74.esm"}` |
| Which weapons (or any type) does a plugin hold? | `sfs_find` | `{"type": "WEAP", "in": "kgcdoom.esm"}` |
| Is a plugin Full, Medium or Light (Small)? Loaded? Its console prefix, masters, room for new records, records per type (new and overrides)? Can I trust it? | `sfs_loadorder` | `{"view": "plugin", "plugin": "atom74.esm"}` |
| The load order. Which plugins have problems (a missing master, a patch built for an older master)? | `sfs_loadorder` | `{}`, `{"view": "problems"}` |
| What changed in Data since last time? Which patches are at risk? | `sfs_loadorder` | `{"view": "changes"}` |
| Show me one record | `sfs_record` | `{"record": "kgcDoom_WEAP_SuperShotgun"}` |
| One value: its damage, its keywords, what a recipe makes, its templates | `sfs_record` | `{"record": "kgcDoom_WEAP_SuperShotgun", "view": "fields", "fields": "AttackDamage,Keywords"}` |
| Prove a value from the file's bytes | `sfs_record` | `{"record": "kgcDoom_WEAP_SuperShotgun", "view": "evidence", "fields": "AttackDamage"}` |
| A plugin file outside Data (a backup, a test build, a downloaded mod) | `sfs_record`, `sfs_compare` | `{"record": "kgcDoom_WEAP_SuperShotgun", "plugin": "C:/backup/kgcdoom.esm"}`; `sfs_compare` with `"kind": "plugins"` takes two paths |
| A plugin's header: its masters, flags, record count (HEDR), author | `sfs_record` | `{"record": "darkstar.esm"}` (the plugin's name is its header, TES4); `"view": "evidence", "fields": "Flags,Stats.NumRecords,MasterReferences[0].Master"` gives each one's bytes |
| Every plugin's header at once: which are light or medium, the most records or masters, which need a given master | `sfs_query` | `SELECT Plugin, Size, NumRecords, MasterCount FROM Plugins ORDER BY MasterCount DESC`; `SELECT p.Plugin FROM Plugins p, json_each(p.Masters) m WHERE m.value = 'darkstar.esm'` (every plugin in Data, read from the headers alone) |
| Every subrecord as the file has it; a record Mutagen can't read | `sfs_record` | `{"record": "000863:darkstarpatch_bublumi.esm", "view": "raw"}`: header and each subrecord's bytes, from the file itself |
| What is this field called? What fields does a record type have? An enum's values? | `sfs_describe` | `{"what": "WEAP", "filter": "damage"}` |
| Reflected data (a REFL subrecord: atmospheres, weather settings, image spaces, fog volumes, effect sequences, LOD and material components) | `sfs_record`, `sfs_describe` | The card decodes it under `Decoded`: `{"record": "0EC2B1:Starfield.esm"}` shows `Image Space: Day (pImageSpaceDay): IS_Atmo_Orange_LUT_Day (13DAAF:Starfield.esm)`; `"view": "evidence", "fields": "ReflectionDiff.Decoded.Settings.Misc.pImageSpaceDay"` gives its bytes. `{"what": "BGSAtmosphere::MiscSettings"}` lists a reflected class's fields, with the Creation Kit's name beside the engine's |
| How many, or which, records of a plugin fit a condition? | `sfs_query` | `{"plugins": "kgcdoom.esm", "sql": "SELECT count(*) AS N FROM Weapon_Keywords WHERE edid(Value) = 'kgcDoom_Tag_WeaponTypeDoom'"}` |
| The same over what the game uses (the winners of the load order) | `sfs_query` | `{"sql": "SELECT count(*) AS N FROM Weapon"}` |
| Recipes, keywords, attach points, templates, FormLists, OMODs, armor meshes, leveled lists, new records, overrides, broken links: ready-made | `sfs_saved` | `{"action": "list"}`, then `{"action": "run", "name": "cobj", "args": {"plugin": "kgcdoom.esm"}}` |
| What points at this record? Does anything use it? | `sfs_refs` | `{"record": "0076BE:kgcdoom.esm", "in": "kgcdoom.esm"}` |
| What does this record point at? | `sfs_refs` | `{"record": "kgcDoom_WEAP_CrucibleBlade", "direction": "out"}` |
| Who wins this record, and what does each plugin change? | `sfs_compare` | `{"kind": "versions", "record": "2377CF:Starfield.esm"}` |
| What does this plugin or patch change in its masters? In one record? | `sfs_compare` | `{"kind": "plugin_vs_masters", "a": "darkstarpatch_atom74.esm", "detail": true}`; add `"filter": "<EditorID text, FormKey or type>"` |
| How do two records differ? | `sfs_compare` | `{"kind": "records", "a": "219447:Starfield.esm", "b": "0082B0:darkstar.esm"}` |
| What does a patch change in one record of its master? | `sfs_compare` | `{"kind": "records", "a": "atom74.esm", "record": "ccAtom74Mk2", "b": "darkstarpatch_atom74.esm"}` |
| What changed between two versions of a mod? My patch against a reference patch? | `sfs_compare` | `{"kind": "plugins", "a": "<old file>", "b": "<new file>"}`, `{"kind": "patch_vs_reference", "a": "<my patch>", "b": "<reference patch>"}` |
| Is this plugin built right? | `sfs_check` | `{"plugin": "atom74.esm"}` |
| How many overrides change nothing (ITMs)? | `sfs_check` | `{"plugin": "darkstar.esm", "what": "itm"}` |
| Are the meshes and other files there? The textures behind the materials? | `sfs_check` | `{"plugin": "atom74.esm", "what": "assets"}`, `"what": "materials"` |
| What is this material made of (layers, texture sets, textures, parent), compiled or a `.mat` file, and which copy does the game use? Which materials (compiled and `.mat` files) use this texture? | `sfs_run` | `{"args": ["materials2", "--material", "materials\\water\\waterchoppylarge.mat"]}`, `{"args": ["materials2", "--texture", "textures\\water\\foamdefault_color.dds"]}`; add `"--all-paths"` when the answer says some materials' paths aren't told (a few seconds, once; then kept) |
| Does Mutagen read and write this plugin without loss? | `sfs_check` | `{"plugin": "atom74.esm", "what": "roundtrip"}` |
| How does the player get this item? | `sfs_run` | `{"args": ["sources2", "kgcdoom.esm", "--item", "kgcDoom_WEAP_SuperShotgun"]}` |
| What can this leveled list give? | `sfs_run` | `{"args": ["leveled2", "kgcDoom_LL_Weapon_SuperShotgun_HighestQuality"]}` |
| What do this quest's characters say? | `sfs_run` | `{"args": ["dialogue2", "kgcDoom_MQ01"]}` |
| What is in a mod's archives? Which archive has this file? | `sfs_run` | `{"args": ["archive2", "atom74.esm"]}` |
| Which mod's file names this material, texture, sound or script string (inside meshes, material databases, interface files, scripts)? | `sfs_run` | `{"args": ["archive2", "all", "grep", "textures\\common\\concrete", "--ext", "mat,cdb"]}`; it says the cost first, and over 10 s needs `"allow_heavy": true`; add `"--count"` for hits and files per kind of file instead of the hits |
| How is a string table or text file written (UTF-8, Windows-1252, mixed, UTF-16)? Which strings aren't UTF-8? | `sfs_run` | `{"args": ["archive2", "darkstar.esm", "encoding", "--ext", "strings,dlstrings,ilstrings"]}`; `"--detail"` lists every string not in UTF-8 |
| What do this mod's Papyrus scripts do? | `sfs_run` | `{"args": ["scripts2", "atom74.esm"]}` |
| What values does this field take across plugins (zeros, top values, are they FormIDs, strings IDs or floats)? The unknown fields of a type? The GRUP headers? | `sfs_run` | `{"args": ["profile", "Planet.OrbitalData.Unknown1", "--in", "@official"]}`, `{"args": ["profile", "--unknown", "--type", "NPC_", "--in", "@official"]}`, `{"args": ["profile", "--groups", "--in", "kgcdoom.esm"]}` |
| Which condition functions do the plugins use, how often, where? | `sfs_run` (census), `sfs_query` (rows) | `{"args": ["profile", "--conditions", "--in", "@official"]}`, `{"args": ["profile", "--conditions", "--function", "961", "--in", "@all"]}` (heavy: the first answer is its cost, about 48 s and 78 s; `"allow_heavy": true` runs it); `SELECT FormKey, Path, Param1 FROM Conditions WHERE Function = 961` |
| What does a link field point at across plugins (record types, distinct targets, nulls, missing)? Which record types actually hold a subrecord? | `sfs_run` | `{"args": ["profile", "*.GroupedPackIn.HNAM", "--in", "@all", "--allow-heavy"]}`, `{"args": ["profile", "--subrecords", "STRD", "--in", "@official"]}` (about 10 s: asks first) |
| In what order does the game write a type's subrecords (is BNAM always right after UNAM, what follows XNAM)? | `sfs_run` | `{"args": ["profile", "--subrecords", "XNAM,UNAM,BNAM,PNAM", "--order", "--type", "PACK", "--in", "@all"]}`; `"*"` for every subrecord |
| What does a record type hold, how big, how linked (every subrecord with counts and bytes, links per field)? | `sfs_run` | `{"args": ["profile", "--subrecords", "*", "--type", "CELL", "--in", "Starfield.esm"]}` |
| Which values does a subrecord take across plugins (which component kinds exist; a subrecord no field covers)? | `sfs_run` | `{"args": ["profile", "--subrecords", "BFCB", "--values", "--in", "@all", "--allow-heavy"]}` (about 20 s) |
| Which subrecords are empty (no bytes), always or only sometimes; which have one length? | `sfs_run` | `{"args": ["profile", "--subrecords", "*", "--lengths", "--in", "@official", "--allow-heavy"]}` (about 20 s); one type: `--type SCEN` |
| Which plugins store FormIDs oddly (past the master list, FE outside a Small plugin, FD outside a Medium one, own records in two forms)? | `sfs_run` | `{"args": ["profile", "--formids", "--in", "@all", "--allow-heavy"]}`; one plugin: `--in <plugin>`; `check2 <plugin>` names each record of a plugin that mixes its forms |
| An add-on's command; any verb's full options | `sfs_run` | `{"args": ["help"]}`, `{"args": ["refs2", "--help"]}` |
| A call came back "Still working on it (job …)" | `sfs_job` | `{"job": "<the job number>"}`; `"cancel": true` stops it |
| Is the service healthy? Are the indexes ready? | `sfs_status` | `{}` |
| What does SFSearch keep on disk, how big is it, and how does the user delete it? | `sfs_run` | `{"args": ["indexes"]}` (each index, its size, age, what it speeds up); `{"args": ["indexes", "drop", "all"]}` deletes them. Only indexes made from the plugin files are kept, never record data; `keepStartupIndexes` in sfsearch.json turns keeping them between starts off |
| The user asks often what points at Starfield.esm records from placed objects, cells or navmeshes | `sfs_run` | `{"args": ["indexes", "build", "world"], "allow_heavy": true}` once, when the user agrees (about 14 to 16 s and 275 MB for every plugin; `"--in", "Starfield.esm"` about 7 s and 77 MB): `sfs_refs` then reads world data from this world link index in a fraction of a second. Kept until `{"args": ["indexes", "drop", "world"]}` |

Try a purpose-built tool first. Write SQL for what they don't cover, and read `references/sql.md` before you do.

## How to read an answer
Every tool answers in the same parts. A real call and its answer:
```text
> sfs_find {"type": "KYWD", "in": "kgcdoom.esm", "limit": 3}
| Record | Type | Name | Held by (winner first) |
| :--- | :--- | :--- | :--- |
| kgcDoom_ma_Spacesuit_Praetor_Body (0076BA:kgcdoom.esm) | KYWD Keyword |  | kgcdoom.esm |
| kgcDoom_Tag_SpeechChallenge_Mitch03 (0076BB:kgcdoom.esm) | KYWD Keyword |  | kgcdoom.esm |
| kgcDoom_ap_Quality (0076BC:kgcdoom.esm) | KYWD Keyword | Quality | kgcdoom.esm |
[more] 3 of 42 rows shown. Next: cursor "3.a819".
[source] kgcdoom.esm 3.5 MB, key 9561e0c8, strings 2d590079; load order (224 loaded, plugins.txt) 9 KB, key b730711a
[cost] 126 ms; read the header index of 370 plugins (checked in 19 ms), each row's in-game name from the record's own bytes
```
- **`[more]`**: the answer was cut. Repeat the call with the same arguments and `cursor` set to the text in quotes. A cursor only works for the question that gave it. Nothing is cut without this line.
- **`[notes]`**: warnings and reasons. Read them every time, and pass on the ones that matter. Problems of a plugin (a patch built for an older master) come first. An answer with no rows says why here.
- **`[source]`**: which copy of which plugins the answer was read from: size, a key of the file's content, and the path when the file is outside Data. Every tool writes a size the same way: 1 KB is 1,024 bytes; whole KB rounded down under 1 MB (53,235 bytes is `51 KB`), one decimal for MB, two for GB.
- **`[cost]`**: the time, and what had to be read. The first question about a record type reads it (Starfield.esm's 7,131 NPCs: 0.2 s); later ones find it ready.
- **An error is one line:** `Error: <what is wrong>. Fix: <what to do>.` Do what the fix says.
- **`Not run. … about N s and M GB of memory.`** The work is heavy (over 10 s, 2 GB or a million records) and wasn't started. The line gives three answers: `allow_heavy: true` runs it now; leaving it out runs nothing; and, where an index would make that kind of question fast, `allow_heavy: "save_index"` runs it now and then saves that index (the line gives its size, the time to make it in the background, and how fast the same kind of question then gets). Where no index would help, the line says so. A line about world data left out of `sfs_refs` gives the same three as `world: true`, leaving it out, and `world: "save_index"`. Ask the user first when it is many GB (Starfield.esm's round trip is about 71 s and 13.8 GB), and before saving an index: it stays on their disk (`sfs_run ["indexes"]` shows it, `["indexes", "drop", "world"]` deletes it).
- **`Still working on it (job …)`**: a call waits up to 45 s. Collect the answer with `sfs_job`.
- **Shared arguments:** `limit` or `rows` (rows a page, default 40), `max_chars` (default 24,000), `language` (default English), `format: "json"` (the parts as fields; `sfs_query`, `sfs_saved`, `sfs_compare`, `sfs_check` and the reports give their rows as values).

## The rules that still matter
1. **Write every record as `EditorID (FormID:plugin)`,** as the tools print it. Never a bare FormID in a reply or a note. A record that has no EditorID is printed `[no EditorID] (FormID:plugin)`; a link to one of the game's own built-in forms as `PlayerRef [built into the game] (000014:Starfield.esm)`.
2. **Don't guess field names.** `sfs_describe` gives the name SQL, `sfs_record` and the cards use. A wrong name is answered with the nearest real ones.
3. **Read `[notes]`.**
4. **Links are FormKeys** (`0476C4:Starfield.esm`). To match a link by name in SQL, write `edid(column) = 'TheEditorID'`.
5. **Say whose version you read.** `sfs_record` reads the version the game uses unless `plugin` names another, and its third line says which. A plugin that isn't loaded never wins.
6. **A plugin is a file name in Data or the full path of a file elsewhere.** A record is a FormKey, an EditorID, or a FormID with its plugin.
7. **`sfsearch.json` in the SFSearch folder is the user's.** Change it only when asked.

## What is no longer a worry
The 16 traps of the old guide are gone or caught. Each has a tested case (`tests\SFSearch.Harness\traps\traps.json`).

| Was | Now |
|---|---|
| An unquoted FormID in PowerShell turned into a number | MCP takes text. On the command line an error says to quote it |
| The same six digits in different plugins | A bare ID lists every record that has it and picks none |
| New or override judged by a database's name | `IsOverride` comes from the record's FormKey. There are no databases |
| Searching record data by EditorID found 0 | SQL gets a note that names `edid()`; `sfs_refs` takes either |
| OMODs weren't `ObjectModification` | `ObjectModification` holds all 8 OMOD types, with a `Type` column |
| Vanilla text came German first | Text is English, or the `language` asked for |
| Two files of one name shared one database | Each file is read by its content; `[source]` shows the key and the path |
| A plugin comparison dropped repeated EditorIDs | Pairs are by FormKey. With `match: "editorid"` a warning lists the repeated ones |
| Many record types weren't stored | Every type is read on request; a big one says its cost first |
| Inline SQL cut at a line break ran as a shorter query | MCP takes any SQL. The command line refuses inline SQL that doesn't end with `;` |
| `json_each` per row was slow; the shell's 2-minute limit | Every list is a view with its own rows. A note says so; long work is a job |
| A patch whose master was renamed couldn't be paired | An error names the expected master; `as` reads the file under that name |
| Assets of a file outside Data read Data's archives silently | A note and `[source]` say which archives were read; `beside` reads the ones next to the file |
| A missing master put FormIDs on the wrong plugin | An error names the master. Nothing is read through a guess |
| A patch built before its master changed size read wrong | The warning leads `[notes]` of every answer that reads it |
| SQL with no `ORDER BY` changed row order | A note says the order isn't fixed |

## The command line
For scripts, and when the MCP tools aren't loaded:
```powershell
& "<SFSearch folder>\SFSearch\sfs.cmd" <verb> [arguments]
```
| Tool | Verb | | Tool | Verb |
|---|---|---|---|---|
| `sfs_find` | `find` | | `sfs_compare` | `compare` |
| `sfs_record` | `record` | | `sfs_loadorder` | `loadorder2` |
| `sfs_describe` | `describe` | | `sfs_check` | `check2` |
| `sfs_query` | `query2` | | `sfs_saved` | `saved` |
| `sfs_refs` | `refs2` | | `sfs_status`, `sfs_job` | `status`, `job` |

The reports are verbs too: `leveled2`, `dialogue2`, `sources2`, `archive2`, `assets2`, `materials2`, `scripts2`, `changes2`, `profile`.
- **A tool's arguments are the verb's options:** `max_chars` is `--max-chars`, a yes/no argument is a bare flag. The first words fill the main arguments: `record Eon --view versions`, `query2 kgcdoom.esm "SELECT …;"`, `saved run cobj plugin=kgcdoom.esm`.
- **`<verb> --help` shows a verb's options.** `--json` gives the answer's parts as one JSON object.
- **Quote FormIDs** in a shell, and **end inline SQL with `;`**. Longer SQL goes in `--file q.sql` or `--stdin`.
- **A verb whose name ends in `2`** replaced an older command of the plain name (`Query`, `Check`, `Refs`, …), removed in 2.0. The plain name only answers with a pointer to the verb.
- Exit codes, the same for every verb: 0 an answer (also one with no rows); 1 an answer that is a failure of what was asked (`check2` found errors, a round trip differs, `compare records` with `expect` found another difference); 2 an error (the one line `Error: … Fix: …`); 7 "not run" (heavy work that wasn't allowed); 8 not enough memory free for heavy work that was allowed, or the PC ran short of memory while the command ran (ask again).

## Where the old tools went
The 25 older tools (`sfs_names`, `sfs_conflicts`, `sfs_delta`, `sfs_fields`, `sfs_q` and the rest) and the older commands (`Names`, `Inspect`, `Query`, `Delta`, …) were removed in SFSearch 2.0. An old command's name answers with one line, `Error: Names was one of SFSearch's older commands, removed in 2.0. Fix: use find <FormID or EditorID> (sfs_find). …`. An old note or script: `references/old-tools.md` gives the new call for each, and what differs in the answer.

## Detail files (read when needed)
- `references/sql.md`: what SQL sees, the scopes, the functions, the checks, the stored queries. Read before writing SQL.
- `references/research.md`: each tool in depth, with real answers.
- `references/old-tools.md`: old tool or command (removed in 2.0) on the left, new call on the right; old SQL and JSON paths too.

## Keeping this skill up to date
- **Edit in place.** When a tool, argument or rule changes, change its line here. Don't append dated notes.
- **Every example is real.** Run it and paste what it printed.
- Keep this file under about 150 lines. Detail goes in `references/`.
