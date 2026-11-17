# Old tools and commands, and what replaces them

SFSearch 1.x had 25 MCP tools and a command for each. **They were removed in SFSearch 2.0.** This file maps every old tool and command to the call that answers it now, and says what differs in the answer. Use it to turn an old note, script or SQL into the new call.

- **An old command's name answers with one line that names its verb,** on the command line and through `sfs_run`, with exit code 2:
  `Error: Names was one of SFSearch's older commands, removed in 2.0. Fix: use find <FormID or EditorID> (sfs_find). 'SFSearch --help' lists the verbs.`
- **The MCP server has only the 12 tools.** `--legacy-tools` in its arguments is read past, with a line on stderr.
- **A verb whose name ends in `2`** (`query2`, `refs2`, `check2`, `loadorder2`, `leveled2`, `dialogue2`, `sources2`, `archive2`, `assets2`, `materials2`, `scripts2`, `changes2`) is the new form of the old command of that name. The plain names stay the old commands' pointers, so an old script gets a clear line rather than a new tool's answer to arguments it wasn't written for.
- **Nothing is stored.** The old tables (`Records.Data`, `Refs`, `lo.*`, `scr.*`) are gone with the old commands. The databases SFSearch 1.x kept in `Cache\` and `Service\data\` aren't read.

## The 25 old tools
| Old tool | New call | What differs |
|---|---|---|
| `sfs_names` (`ids`, `in`) | `sfs_find` (`query`, `in`) | Also takes EditorIDs, in-game names, patterns and a type. Each row names every plugin that holds the record, the winner first. A bare ID also lists placed references and navmeshes of the game's own plugins, which `sfs_names` left out |
| `sfs_record` view `fields` (Inspect) | `sfs_record` view `card`, or `fields` with `fields` | The record comes first, `plugin` is optional (default: the version the game uses). Field names are Mutagen's, with the xEdit name and subrecord beside them |
| `sfs_record` view `xedit` (Read) | `sfs_record` view `card` | The card has xEdit's names where a checked pair exists. `evidence` gives the xEdit path of a field |
| `sfs_record` view `json` (Dump) | `sfs_record` view `json` | Mutagen's own JSON, not the old converter's (see "The record's JSON") |
| `sfs_record` view `file` (Live) | `sfs_record` (every view reads the file) | `subrecords` is view `raw`. `chain` is view `versions`, or `sfs_compare` `versions` for what each changes. `links` is `sfs_refs` with `direction: "out"` |
| `sfs_refs` (`record`, `in`) | `sfs_refs` (`record`, `in`) | Searches every record type, and names the real record (a scene, a topic), where the old one filed those under their quest. Placed references and other world records only with `world: true`. `direction: "out"` is new |
| `sfs_conflicts` | `sfs_compare` `kind: "versions"` | The same holders, order and winner. Differences are by field with both values under `detail` |
| `sfs_delta` (`plugin`, `record`, `detail`) | `sfs_compare` `kind: "plugin_vs_masters"` (`a`, `filter`, `detail`) | Every record type is compared. The EditorID counts as a field. A text counts in every language: one held in fewer languages is a change of that field, even with the same English (the old tool agreed). A summary by field replaces the old labels (Mesh/Visual, Stats, …) |
| `sfs_diff` (`plugin1`, `plugin2`, `record1`, `record2`, `expect`) | `sfs_compare` `kind: "records"` (`a`, `b`, `record`, `record_b`, `expect`) | Two FormKeys alone work as `a` and `b`. An empty text and string ID 0 compare equal |
| `sfs_plugin_diff` (`old_plugin`, `new_plugin`, `detail`) | `sfs_compare` `kind: "plugins"` (`a`, `b`, `detail`) | Pairs by FormKey. EditorID pairing only with `match: "editorid"`, with repeated EditorIDs listed. A patch's own records aren't listed as added. A renamed master needs `as` |
| (none) | `sfs_compare` `kind: "patch_vs_reference"` | New: two patches for the same masters |
| `sfs_header` | `sfs_loadorder` `view: "plugin"` | Size, masters with their sizes, room for new records, slot. Not there: author, description, the header's version and record count, the ONAM list |
| `sfs_stats` | `sfs_loadorder` `view: "plugin"` | Records, new and overrides per type, with bytes. No "stored" column: nothing is stored |
| `sfs_loadorder` (`plugin`, `all`) | `sfs_loadorder` (`view`, `plugin`, `all`) | Views `order`, `plugin`, `problems`, `changes`. Ready in about 2 s after a service start, not a minute. Counts placed references of the game's own plugins too |
| `sfs_changes` (`plugin`, `since`) | `sfs_loadorder` `view: "changes"`; `since` through `sfs_run` `["changes2", "--since", "<day>"]` | The history is a new log under `kept\history`: it starts again from the first call. Nothing is judged by a file date |
| `sfs_fields` (`plugins`, `type`, `filter`) | `sfs_describe` (`what`, `filter`) | Answers from the schema, not from a plugin: every field the type can have, with type, link target and xEdit name. No counts or sample values; count with `sfs_query` |
| `sfs_query` (`plugins`, `sql`, `with_masters`, `with`, `load_order`, `scripts`) | `sfs_query` (`sql`, `plugins`, `scope`) | Other tables (see "Old SQL"). `load_order` is `scope: "winners"` or `"versions"`. `with_masters` and `with` are gone: name several plugins, or use a scope. `scripts` is `sfs_run` `["scripts2", …]` |
| `sfs_q` (`macro`, `plugin`, `filter`) | `sfs_saved` (`action: "run"`, `name`, `args`) | The same ten names and 17 more. Arguments by name: `{"plugin": "x.esm", "record": "…"}` |
| `sfs_check` (`plugin`, `all`) | `sfs_check` (`plugin`, `what`, `detail`) | 19 checks. Every record type is read. World data only with `world`. The EditorID counts in the ITM check, and every record type is compared: darkstar.esm has 20 ITMs, as the old check, but not the same 20 (the old check listed 2 globals whose override adds an EditorID, and never compared the 2 material swaps). `all` is `detail` |
| `sfs_materials` (`plugin`, `missing`) | `sfs_check` `what: "materials"` | The compact answer is what is missing; `detail` lists every material |
| `sfs_archive` (`target`, `list`, `find`, `show`) | `sfs_run` `["archive2", target, "list" \| "find" \| "show" \| "extract", …]` | The options are words. `--beside` reads the archives next to a file outside Data |
| `sfs_leveled` (`list`, `in`, `depth`) | `sfs_run` `["leveled2", list, "--in", plugin, "--depth", N]` | The same report |
| `sfs_dialogue` (`quest`, `in`) | `sfs_run` `["dialogue2", quest, "--in", plugin]` | Nothing is added to a database first. `--language` |
| `sfs_scripts` (`plugin`, `find`, `show`) | `sfs_run` `["scripts2", plugin, "--find", text]`, `"--show", script` | Also says which records carry each script. Sources are kept under `kept\scripts` |
| `sfs_add` | Not needed | Every record type is read when asked for. A big one says its cost first |
| `sfs_verifydb` | Not needed | There is no database to verify. `sfs_record` view `verify` proves one record's JSON against the file; `sfs_check` `what: "roundtrip"` a whole plugin |
| `sfs_run` (`args`) | `sfs_run` (`args`, `allow_heavy`) | Runs the verbs and add-on commands; an old command's name gives its pointer line. `["help"]` lists the verbs |
| `sfs_job` (`job`, `wait_seconds`) | `sfs_job` (`job`, `wait_seconds`, `cancel`) | Can cancel. The answer comes in the contract's parts |
| `sfs_status` | `sfs_status` | Adds both indexes, open plugins, the scratch folder, the kept files |

## The old commands
| Old command | New verb (tool) | Notes |
|---|---|---|
| `Names <ids> [--in p]` | `find <ids> [--in p]` (`sfs_find`) | As `sfs_names` above |
| `Inspect <plugin> <record> [field]` | `record <record> [--plugin p]`, `--view fields --fields <names>` (`sfs_record`) | |
| `Read <plugin> <record>` | `record <record> --plugin p` | |
| `Dump <plugin> <record>` | `record <record> --plugin p --view json` | |
| `Live <plugin> <record>` | `record <record> --plugin p` | `--field` is `--view fields --fields`; `--subrecords` is `--view raw`; `--chain` is `--view versions` or `compare versions`; `--links` is `refs2 <record> --direction out`; `--vs-cache` has nothing to compare with; `--with` isn't needed: the load order is always read |
| `Live <plugin> <record> --refs` | `refs2 <record> --in p --world` (`sfs_refs`) | |
| `Live <plugin> --sig T --field "Path[*].X"` | `query2 <plugin> "SELECT EditorID, <column> FROM <Type>;"`, or the type's list view (`sfs_query`) | Every type has a view. `--filter` and `--value` are a `WHERE`; `--new` is `IsOverride = 0` |
| `List <plugin> <Type>` | `find --type <Type> --in <plugin>` | Or the stored query `find`, `new` or `counts` |
| `Fields <plugin> <Type> [--filter t]` | `describe <Type> [<filter>]` (`sfs_describe`) | As `sfs_fields` above |
| `Q <macro> <plugin> [filter]`, `Q --list` | `saved run <name> plugin=<plugin> …`, `saved list` (`sfs_saved`) | |
| `Query <plugins> "<SQL>"` | `query2 <plugins> "<SQL>;"` (`sfs_query`) | Other tables: "Old SQL" below. `--file` and `--stdin` stay. `--timing` is the `[cost]` line. `--out` is gone: page with `--cursor`, or `--json` |
| `Query --loadorder` | `query2 "<SQL>;" --scope winners` or `--scope versions` | |
| `Query --scripts` | `scripts2 <plugin> --find <text>`; `scripts2 <plugin>` for which records attach each script and which are attached to nothing | Script source isn't a table any more |
| `Query --with-masters`, `--with p1,p2` | `query2 p1,p2 "<SQL>;"`, or a scope | A stored query can ask for `plugins with masters` |
| `Diff <p1> <p2> <rec1> [rec2]` | `compare records <p1> <rec1> <p2> [<rec2>]` (`sfs_compare`) | `--expect` stays. `--summary` is the default; `--detail` gives every value. `--ignore` is gone: use `--expect` |
| `Delta <plugin> [record] [--detail]` | `compare plugin_vs_masters <plugin> [<filter>] [--detail]` | |
| `Conflicts <record> [--in p]` | `compare versions <record>` | |
| `PluginDiff <old> <new> [--detail] [--shifts]` | `compare plugins <old> <new> [--match editorid] [--as name] [--detail]` | `--shifts` is gone: records pair by FormKey, or by EditorID when asked |
| `Refs <record> [--in p]` | `refs2 <record> [--in p]` (`sfs_refs`) | |
| `Header <plugin>`, `Stats <plugin>` | `loadorder2 plugin <plugin>` (`sfs_loadorder`) | |
| `LoadOrder [plugin] [--all]` | `loadorder2 [order] [--all]`, `loadorder2 plugin <plugin>`, `loadorder2 problems` | `--quick` isn't needed |
| `Changes [plugin] [--since d] [--all]` | `loadorder2 changes [plugin]`, `changes2 [plugin] [--since d] [--all]` | |
| `Check <plugin> [--all]` | `check2 <plugin> [--detail] [--world]` (`sfs_check`) | `--out` is gone |
| `Assets <plugin> [--missing]` | `check2 <plugin> --what assets`, `assets2 <plugin>` | Looks in every archive in Data, and says when a file is only in an archive the game doesn't load |
| `Materials <plugin> [--missing]` | `check2 <plugin> --what materials`, `materials2 <plugin>` | |
| `Archive <target> [--list g] [--find t] [--show p] [--extract p --to d]` | `archive2 <target> [list g \| find t \| show p \| extract p --to d]` | |
| `Leveled <list> [--in p] [--depth N]` | `leveled2 <list> [--in p] [--depth N]` | |
| `Dialogue <quest> [--in p]` | `dialogue2 <quest> [--in p]` | |
| `Sources <plugin> [--sig T] [--item X] [--with p] [--depth N] [--scripts]` | `sources2 <plugin> [--types T] [--item X] [--with p] [--depth N]` | `--sig` is `--types`. `--scripts` is `scripts2 <plugin> --calls`: each GetFormFromFile call with its FormID, plugin and record, and what AddItem and the like hand out. `--out` is gone |
| `Scripts <plugin> [--find t] [--show s]` | `scripts2 <plugin> [--find t] [--show s]` | |
| `LossCheck <plugin>` | `check2 <plugin> --what roundtrip` | Compares the written file with the plugin byte for byte, and names the first record that differs |
| `VerifyDb <plugin>` | `record <record> --view verify`; `check2 <plugin> --what roundtrip` | |
| `Databases` | `indexes` (through `sfs_run`) | What SFSearch keeps on disk: the header index and the link index, each one's size, coverage and age, and how to drop it. `sfs_status` has one line of it |
| `Add`, `BuildCache`, `LiveScanCheck` | Nothing replaces them | They looked after stored tables |
| `Session …` | `Service …` | The old name of the service command |
| `--live` on any command | Not needed | Everything is read live |
| `--json`, `--csv`, `--maxwidth`, `--raw` | `--json` on a verb | The parts of the answer as one JSON object |
| `SetData <path>`, `Version`, `Extensions`, `Service start \| stop \| restart \| status \| config` | The same | `status` (the verb) is the fuller status the tools use |

## The record's JSON
`sfs_record` view `json`, the `Json` column in SQL and every field path use Mutagen's own JSON. Old notes and scripts that name paths of the old stored form need these changes:

| Old form | Now |
|---|---|
| `$.Model.File.GivenPath` (and `Extension`, `DataRelativePath`, …) | `$.Model.File`: the path as one value |
| `Signature`, `IsDeleted`, `IsCompressed`, `MajorFlags` | `MajorRecordFlagsRaw` and `StarfieldMajorRecordFlags`. The signature is the record type |
| `"Record Header"`, `"EDID - Editor ID"` | `EditorID`; the header's values are `FormVersion`, `Version2`, `VersionControl` |
| `WorldModel[*].LightLayer` (a gendered item as a list of two) | `WorldModel.Male.LightLayer`, `WorldModel.Female.LightLayer` |
| `Components[*].Type` | The kind is in the path: `Components[*]{ModelComponent}.Model.File` |
| A quest's scenes and topics, a cell's placed references, inside the parent | Records of their own. The parent holds a `{"$Child": "<FormKey>"}` marker |
| Translated text as `"[English, The Lodge]"`, vanilla text German first | The language asked for, by name |
| OMOD fields `MaxRank` and `LevelTierScaledOffset` left out | Both are there |

## Old SQL
The old tables went with the old `Query` command in 2.0. To turn old SQL into `query2` SQL:

| Old | New |
|---|---|
| `Records` (`FormKey`, `Signature`, `EditorID`, `Name`, `Origin`, `IsOverride`, `Data`) | The typed view of the record type (`Weapon`, …), or `Records` (`Type`, `Sig`, `Json`) for the types read so far |
| `json_extract(Data, '$.AttackDamage')` | The column `AttackDamage` |
| `Signature = 'WeaponModification'` | `FROM WeaponModification`, or `FROM ObjectModification WHERE Type = 'WeaponModification'` |
| `json_each(Data, '$.Keywords')` | The list view `<Type>_Keywords` |
| `Refs` (`FromFormKey`, `FromSignature`, `FromEditorID`, `Path`, `ToFormKey`) | `Links` (`FromKey`, `FromType`, `Field`, `Path`, `ToKey`) for the types a query names; `sfs_refs` for a search of every type |
| `Forms` (`Base`, `Parent`, `HasScripts`) | `Forms` (`Parent`, `IsWinner`, `Pos`, `LoadOrder`); a placed reference's base is `PlacedObject.Base` |
| `SignatureCounts` | `SELECT Sig, Type, count(*) FROM Forms GROUP BY Sig, Type`, or `sfs_loadorder` `view: "plugin"` |
| `Masters`, `Meta` | `sfs_loadorder` `view: "plugin"` |
| `AllRecords`, `sf.Records` (attached masters) | Name the plugins, or use a scope; `edid(fk)`, `name(fk)`, `type(fk)` answer for any record in Data |
| `lo.Winners`, `lo.WinningRecords` | `scope: "winners"`: every typed view holds the winning versions |
| `lo.Versions` | `scope: "versions"`, with `LoadOrder` and `IsWinner`; `Forms` for every record's header |
| `lo.Plugins` | `sfs_loadorder` |
| `scr.Scripts`, `scr.Lines` | `sfs_run` `["scripts2", …]` |
| `fkname(fk)` | `name(fk)` |
| `en(json)` | Text columns are already one language; `text(value, 'de')` for another |
