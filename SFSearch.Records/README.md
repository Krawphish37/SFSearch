# SFSearch.Records: records, cards and evidence

Reads one record of one plugin live through Mutagen and gives it in the forms an agent needs:
- **JSON:** Mutagen.Bethesda.Serialization's full-profile JSON, the one record form.
- **A card:** set fields only, readable names, links labelled, unknown fields with their bytes. A reflection blob (REFL and the like) is decoded below its bytes as `Decoded`, each field by the Creation Kit's name (`ReflectionView.cs`; every schema has them, from `SFSearch.SchemaGen\inputs\ck-names-*.tsv`); `fields` and `evidence` take its paths.
- **Named fields:** with the default for a field the JSON leaves out. A property Mutagen adds beyond its XML (the schema's `Source` fields: `MajorFlags`, `IsCompressed`, `Versioning`, `…DataTypeState`, `RegionData.DataType`, which the serializer never writes) is read from Mutagen's record instead (`Card.FromRecord`).
- **Raw subrecords,** and **evidence** for one field (the file's bytes beside the Mutagen field and the xEdit path).
- **A verify:** the JSON turned back into a record, written by Mutagen, compared with the file's bytes.

It is a library. SFSearch's `record` and `refs2` verbs (tools `sfs_record` and `sfs_refs`: `SFSearch\Engine\RecordTool.cs`, `RefsTool.cs`) are built on it. Nothing about a record is stored.

## Use it
```csharp
using SFSearch.Index;
using SFSearch.Records;

var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
using var index = new HeaderIndex(PluginSource.Folder(dataFolder), new IndexOptions { ScratchFolder = scratch, VersionText = "…" });
index.Refresh();
using var engine = new RecordEngine(index, schema, verifyScratchFolder, dataFolder);

var use = engine.Sessions.Use("darkstar.esm");            // before each command: checks the quick stamps, reopens what changed
var rec = engine.Load("darkstar.esm", "0003C2");          // a FormKey, a local ID, the file's FormID, or an EditorID
JsonObject json = engine.Json(rec);                       // children are {"$Child": "<FormKey>"} markers
Card card = engine.Card(rec, json);                       // card.ToText(), card.ToJson()
var values = engine.Fields(rec, new[] { "AttackDamage", "HasScope" });
Evidence ev = engine.Evidence(rec, "AttackDamage");       // ev.ToText()
string raw = engine.RawText(rec.Ref);
VerifyResult v = engine.Verify(new[] { rec.Ref })[0];     // v.Equal, or the first differing subrecord
engine.Sessions.Confirm(use);                             // at the end: fails if a plugin changed during the command
foreach (var note in use.Notes) { /* "darkstar.esm changed on disk since it was opened…" */ }
```
- **Call `Sessions.Use` before each command and `Sessions.Confirm` after it.** `Use` takes the quick stamp of each plugin named and of its masters and compares them with the header index's. Only when one differs does the index look at the files again. An open plugin whose file or whose masters' sizes changed is closed and read again, and `use.Notes` says so.
- **A localized plugin's string files count as part of it** when the header index stamps them (`IndexOptions.StringFiles = new StringFiles(dataFolder, scratch)`): a changed loose string file, or a changed archive it is read from, reopens the plugin and `use.Notes` names the file. `StringFiles.ReadParameters` is what Mutagen is given, so the stamp names the files the texts come from. A plugin outside Data reads loose string files from the `Strings` folder beside it when it has some there.
- **One caller at a time** for `Use`, `Confirm` and the first `Get` of a plugin, like the header index. Once a plugin is open, several threads can read its records at once: `Load`, `Raw`, `ReadDirect`, `ReadAt`.
- **A plugin whose master is missing is refused** with a line that names the master. Nothing is read through a guess.
- **Plugins are opened for reading with every kind of sharing.** Mutagen's own open would stop a mod manager from replacing a plugin that is held open. For the same reason a plugin is never kept memory-mapped: Windows refuses to overwrite a file that has a mapped view.
- **A record's own bytes come through `PluginSession.ReadAt`.** Each reading thread has a window of its own on the file (4 to 64 KB), filled through a file handle no other thread is using. One shared handle doesn't do: Windows lets one thread at a time read through a handle opened for synchronous reads. A session opens at most one more handle for each thread that reads at the same moment.

## How a record is read
| The record | Route | Why |
|---|---|---|
| Has a top-level group of its own and holds no other records (weapons, armor, NPCs, almost every type) | The overlay's group for its type (`TryGetTopLevelGroup`), then the group's own FormKey lookup | The header index gives the type, so Mutagen's untyped lookup is never used |
| Sits inside another record (placed references, navmeshes, cells of a worldspace, topics, responses, scenes) | Read straight from its own bytes at the header index's position, with Mutagen's reader of one record (`MajorRecordReader.Read`, 0.55.0-fixes.14), from bytes the session reads itself | No group is read: Starfield.esm's cells and worldspaces are about 1.1 GB of groups |
| Holds other records (cells, worldspaces, quests, topics) | The same, with its children group behind it and every child cut down to its 24-byte header | Mutagen lays the children out (which list, which order, the group's own values) without reading them; each child becomes a `$Child` marker |

`engine.Assembled(rec)` puts every child back, each read from its own bytes.

- **A record of over 16 MB of data isn't read unless asked** (`engine.AllowHeavy`, `sfsrecord --heavy`). `Load` then stops with a line that says what the record is and why. Starfield.esm has one: its navigation mesh info map, 51.4 MB, whose JSON is 370 MB. The raw view works at once.
- **A heavy record can be read in part** (`HeavyRecord.cs`): `engine.Layout(r)` reads its bytes once and gives each subrecord to the top-level field that holds it; `engine.ReadPart(layout, asked)` reads through Mutagen only the subrecords kept (every one except those of the big fields not asked for, and of a list whose items are one subrecord each only the items asked for); `PartCard` and `PartEvidence` then work as `Card` and `Evidence` do, with list items named by their index in the whole list.
- **`engine.PreferDirect`** (`sfsrecord --direct`) reads a top-level record from its own bytes too, when its signature has one Mutagen type. Off by default. It gives the same JSON and keeps no group in memory.
- **A plugin that holds a FormKey twice** (MissionsGalore.esm): `Find` lists every copy and picks none. A copy named by its EditorID is read, the one Mutagen's group leaves out included.

## The files
| File | Holds |
|---|---|
| `PluginSessions.cs` | Overlays kept open, the stamp check, the direct read of one record, the file's bytes for several threads at once (`ReadAt`), the shared-read file system |
| `Serializers.cs` | The full profile and the per-type JSON serializers (written into this library by the source generator at build time). `RecordSerializer.ToUtf8` gives a record's JSON as UTF-8 through a writer the thread keeps from one record to the next: the serializer package makes a new writer, 5 KB of buffers, for every record |
| `RecordSplit.cs` | `$Child` markers: split, put back, strip |
| `RecordSchema.cs` | What the layer needs from `schema.db` |
| `Card.cs` | The card, as text and as a structured object |
| `ReflectionView.cs` | A reflection blob's value on the card (Mutagen's ReflectionData), with the CK's names and each value's place in the blob |
| `FieldLookup.cs` | "Fields" mode with defaults, and evidence |
| `HeavyRecord.cs` | Heavy records read in part: the layout of their subrecords, the part read, its card and evidence |
| `EvidenceItems.cs` | Evidence for a field inside a list item: which subrecord holds the item and where the field sits in it, worked out as Mutagen lays the record out and checked against Mutagen's value. A condition's CTDA is placed by the XML's layout attributes (schema columns `Layout*`, fixes.14), the fields Mutagen reads by hand by `HandLayoutWalk.cs` |
| `HandLayoutWalk.cs` | Walks Mutagen's hand-read layouts (`HandReadLayouts.xml`, the schema's `HandLayouts` table, C186) over one record: a perk's effects, an object template's OBTS, an object mod's DATA and its 24-byte properties, VMAD, weather clouds and colors, a footstep set, a package's idles, a region's RDAT, a race's biped objects, placed traps. Gives each value's subrecord, offset, size and encoding for evidence |
| `FormIdForms.cs` | How a FormID is stored: the full index, the Small form (FE) or the Medium form (FD); whether it names the plugin's own record; what it means, for the raw view's "FormID as stored" line; the record headers' FormIDs of a plugin read through one mapping; where a record's links to its own records sit in its bytes and in which form, from Mutagen's `GetStoredFormIds` (fixes.16, C203) (`check2`'s "FormID forms" rules). Test: `sfsreport-tests --only check-formid-forms` |
| `RawRecord.cs` | The raw reader (header, inflate, subrecords, `XXXX` long forms). A FormID it meets is read by the header index's own resolver (`IndexedPlugin.FormIds`): the copy this library had is gone |
| `Verify.cs` | The one-record verify |
| `RecordNames.cs` | In-game names read from each record's own bytes, with Mutagen's encoding for stored text (find's names) |
| `RecordTexts.cs` | Every text field of a plugin's records read from the bytes (the schema's String fields, told apart inside components and object templates; a subrecord that other values fill too is told by a game setting's EditorID or, for a perk's entry point data and the like, by reading only the records whose bytes could hold a match through Mutagen's reader, `Reader`, `Confirm`), every string of its strings files, a search of both by pattern (`find --text`), and how a stored text reads as code page 1252 and as UTF-8 (`check2`'s stored-text rule). Tests: `text-places`, `text-search` |
| `RecordEngine.cs` | All of it behind one object |
| `MutagenStart.cs` | Mutagen's one-time start-up (`Warmup.Init`, about 0.9 s on fixes.13), run once at a program's start by the service's workers, the CLI's record-reading verbs and sfsquery, so it doesn't land on the first query |

## The runner and the tests
```powershell
dotnet build SFSearch.Records.Runner -c Release
$r = "SFSearch.Records.Runner\bin\Release\net10.0\sfsrecord.exe"
& $r card     darkstar.esm 0003C2
& $r json     darkstar.esm 0003C2
& $r fields   darkstar.esm 0003C2 AttackDamage HasScope "Keywords[0]"
& $r raw      darkstar.esm 0003C2
& $r evidence darkstar.esm 0003C2 AttackDamage
& $r verify   darkstar.esm 0003C2
& $r list     Starfield.esm REFR 5

dotnet build SFSearch.Records.Tests -c Release
SFSearch.Records.Tests/bin/Release/net10.0/sfsrecord-tests.exe "<Data folder>" --report <folder>
```
- The runner's commands and options are listed at the top of `SFSearch.Records.Runner\Program.cs`. Its scratch folder is `%TEMP%\sfsrecord-scratch` unless `--scratch` or `SFSRECORD_SCRATCH` names another.
- The build needs the packages in the repo's `packages\` folder (Mutagen 0.55.0-fixes.17 and Mutagen.Bethesda.Serialization 1.38.6-fixes.3). `schema.db` comes from the build of `SFSearch.SchemaGen` and is copied beside the library.
- The tests copy two small plugins to `%TEMP%\sfsrecord-tests` and change the copies. They never write to the Data folder. `--heavy` adds the checks that read Starfield.esm's cells through the overlay: run those alone, on a PC with memory to spare.
- The verify writes small test plugins into its scratch folder and removes each one after comparing.
