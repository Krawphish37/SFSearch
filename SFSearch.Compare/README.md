# SFSearch.Compare: comparisons

One diff engine, driven by the schema, and the five kinds of comparison built on it:

| Kind | Question |
|---|---|
| `versions` | Who holds this record, who wins, and what does each change against the one before? |
| `records` | How do these two records differ? (`expect` lists the fields allowed to differ.) |
| `plugin_vs_masters` | What does this plugin change in its masters? Which overrides change nothing (ITMs)? |
| `plugins` | What changed between two versions of a mod? Or: what does a patch change in the plugin it masters? |
| `patch_vs_reference` | Two patches for the same masters: what does each change, where do they agree, where do they differ? |

It is a library: SFSearch's `compare` verb (tool `sfs_compare`, `SFSearch\Engine\CompareTool.cs`) is built on it. Nothing is stored, and nothing in it knows about any one mod.

It stands on `SFSearch.Index` (who holds a record, who wins), `SFSearch.Records` (plugins held open, every record as full-profile JSON) and `schema.db`.

## Use it
```csharp
using SFSearch.Compare;
using SFSearch.Records;

var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
using var session = new CompareSession(dataFolder, scratchFolder, schema);
var compare = new Comparer(session);

VersionsAnswer v = compare.Versions("2377CF:Starfield.esm");                     // or an EditorID
RecordsAnswer r = compare.Records("Starfield.esm", "Eon", "svc3holsters.esm", expect: new[] { "Keywords", "Name" });
PluginVsMastersAnswer d = compare.PluginVsMasters("darkstarpatch_atom74.esm");
PluginsAnswer p = compare.Plugins(@"C:\backup\mymod.esm.bak", "mymod.esm");      // match: MatchBy.EditorId, readAs: "x.esm"
PatchVsReferenceAnswer x = compare.PatchVsReference("mypatch.esm", "darkstarpatch_atom74.esm");

string text = x.ToText(new TextOptions { Detail = false, MaxChars = 24000 }, out int next);   // next: the cursor of the next page, or -1
JsonObject json = x.ToJson();                                                                 // the same parts as fields, nothing cut
bool failing = r.Failed;                                                                      // a difference outside "expect"
```
- **A plugin is a file name in Data or a path.** A file outside Data is read with its masters taken from Data. A file whose name isn't a plugin's name (`x.esm.bak`) is read under the name up to `.esm`. Such a file, and a file read under another name on purpose, is copied into the scratch folder first: nothing is ever written where plugins are.
- **A record is a FormKey, a local ID or an EditorID.** A name that fits several records lists them and picks none.
- **Errors** are a `CompareException`. Its `Line` is the output contract's one line: `Error: <what>. Fix: <what to do>.`
- **One caller at a time,** like the index and the record engine it stands on. The stamps of every plugin a comparison reads are checked before it starts and again when it ends.

## The diff engine (`RecordDiffer`)
It walks two records' JSON beside the schema and gives a `RecordDiff`: one `FieldDiff` for each difference, with the record's own field (`Top`), the place (`Path`, and `Shown` with links labelled), the readable name, the subrecord, and both values as text and as JSON.

| Rule | What it does |
|---|---|
| Defaults | A field the schema says is always there, left out on one side, is given its default before comparing. Left out and spelled out at the default are the same. A field that may be absent is different: set on one side only is a difference. |
| Links | Compared by the record they name, plugin names letter case aside. Shown as `EditorID (FormID:plugin)`. |
| A list of links | A set (entries counted). Each link only one side has is a difference. The same links in another order is one difference of the kind "order". |
| A list of objects | Entries that are wholly equal are paired first, wherever they stand. The rest is paired by a key field when the schema makes one evident, else by position. The answer says which: "matched by Item", "matched by position". |
| The key field (`ListKeys`) | A whole number named `Index` or `ID`; else the entry's first link (a container entry's `Item`, a recipe component's `Component`); else a text named `Name`; else, for a list of several kinds of object, the kind, when no kind is there twice. `sfscompare keys` prints the rule's result for all 437 lists of objects in the schema. |
| Flags | A set. Their order is nothing. |
| Numbers | `12` and `12.0` are the same. |
| Nested records | A `{"$Child": FormKey}` marker is a record of its own. Markers are collected (`ChildrenOnlyA`, `ChildrenOnlyB`, `ChildrenInBoth`) and never reported as fields of the record that holds them. |
| Different types | Two records of different Mutagen types are reported as that. No field is compared. |
| A partial form | A record with the flag 0x4000 holds only part of the record. The fields it leaves out are compared as not there, and the answer adds a line that says so. The engine doesn't merge it with its master. |

**A translated text counts in every language.** Another text in any language, a language added or dropped, and no text against an empty text (string ID 0 in a localized plugin against an empty text in one that isn't) are each a change of that field (`Value`), with a note that names the languages. A patch that isn't localized, over a localized master, changes what players in the other languages see.

**What differs without being a value of the record** is kept apart (`DiffClass`). The record header's numbers are shown in detail only. The same value written another way is still listed with the record's fields, compact or detail, under "the same value, written another way" (`RecordDiff.RewrittenFields`), and never counted as a change:

| Class | What |
|---|---|
| `Header` | `FormVersion`, `Version2`, `VersionControl`, `IsCompressed`, the compressed bit of the record flags, and the header values of the group that holds a record's children. |
| `Layout` | `…DataTypeState` and `Versioning`: how long a data block is written. An older, shorter block leaves its last fields at their defaults; a newer one spells them out. |
| `WrittenAnotherWay` | A translated text that every language shows the same on both sides, held another way: localized strings on one side, the text itself on the other (a name that isn't translated), or the same strings in another order. |

`StarfieldMajorRecordFlags` and `MajorFlags` are the bits of `MajorRecordFlagsRaw` by name. They are shown with it and not compared a second time.

## The five kinds
- **`versions`:** every loaded plugin that holds the record, in load order, each against the one before it; from the third on, also which fields differ from the first. The winner is the last loaded one. Plugins in Data that hold it and aren't loaded are listed apart, each against the first version.
- **`records`:** `expect` names fields by their Mutagen name, readable name or path. Any other difference is listed as unexpected and sets `Failed`. A name that fits no field of the record type is pointed out. Records nested in both (the same FormKey under both) are compared as records of their own, up to 500.
- **`plugin_vs_masters`:** each override against the version before it: the last of the plugin's masters, in the header's order, that holds the record. Classes: changed, ITM, another type than its base, override of nothing, not read. A summary by record type and field. The plugin's own records are counted by type.
- **`plugins`:**
    - **Two files of one name** (after `.bak` and the like are cut, or with `readAs`) are two versions of one plugin. Records are paired by FormKey: added, removed, changed, unchanged. With the same masters in both, a pair with equal bytes isn't read at all (`Comparer.TrustEqualBytes`, internal: only the tests turn it off).
    - **`MatchBy.EditorId`** pairs the plugin's own records by record type and EditorID. A link to a record paired that way is compared as its partner in the other file, so a record that moved to another FormKey doesn't make every record that points at it look changed. EditorIDs a file holds more than once are listed and counted, and those records are paired by FormKey: none is dropped.
    - **A file and a patch that masters it** are compared record by record on the patch's FormKeys. What the patch doesn't hold isn't a removal.
    - **Anything else stops** with an error that names the plugin name expected, and how to pair the two on purpose (`readAs`).
- **`patch_vs_reference`:** every record at least one of the two overrides. Each field change (and each entry of a set) is classed: same change, only in a, only in b, different. A record both hold is "the same" exactly when a direct comparison of the two overrides finds nothing. The two patches' own records are counted by type; `MatchBy.EditorId` pairs and compares them too.

## The answer
Every answer is a structured object (`ToJson()`) and text under the output contract (`ToText()`):
```text
<what was compared, the summary>
<rows>
[more] 40 of 312 records shown (1 to 40). Next: cursor "40".
[notes] …
[source] darkstarpatch_atom74.esm 35.6 KB, key 277b098a; atom74.esm 82.9 KB, key 99071b6c
[cost] 0.31 s; 1 overrides of darkstarpatch_atom74.esm, each with the version before it in its masters
```
- **Compact by default:** one line for each of a record's fields that differ. `Detail` prints every difference on a line of its own with both values, and what differs without being a value.
- **A long answer is cut between rows** at `MaxChars` (24,000 by default) and says so. The same call with `Cursor` continues it. The heading is on every page.
- **`[notes]`** carry what the header index knows is wrong with a plugin (a patch built for an older master, a missing master), and plugins that changed on disk.

## The files
| File | Holds |
|---|---|
| `DiffModel.cs` | `FieldDiff`, `RecordDiff`, the kinds and classes of a difference |
| `RecordDiffer.cs` | The diff engine |
| `ListKeys.cs` | Which field pairs the entries of a list |
| `CompareSession.cs` | The Data folder's index and engine, files outside Data, labels, the source line |
| `Answers.cs` | The answer's text: pages, the shared lines for differences |
| `Comparer.cs` | What the five kinds share |
| `Versions.cs`, `Records.cs`, `PluginVsMasters.cs`, `Plugins.cs`, `PatchVsReference.cs` | One kind each: its answer object and its method |

## The runner and the tests
```powershell
dotnet build SFSearch.Compare.Runner -c Release
$c = "SFSearch.Compare.Runner\bin\Release\net10.0\sfscompare.exe"
& $c versions 2377CF:Starfield.esm
& $c records Starfield.esm 219447:Starfield.esm darkstar.esm 0082B0:darkstar.esm --expect SpaceGeneralPilotSkill
& $c plugin_vs_masters darkstarpatch_atom74.esm --detail
& $c plugins "C:\backup\mymod.esm" mymod.esm --match editorid
& $c patch_vs_reference dstgo_atom74.esm darkstarpatch_atom74.esm
& $c keys ConstructibleObject

dotnet build SFSearch.Compare.Tests -c Release
SFSearch.Compare.Tests/bin/Release/net10.0/sfscompare-tests.exe "<Data folder>" --work <folder outside the repo and Data> --report <folder>
```
- The runner's options are listed at the top of `SFSearch.Compare.Runner\Program.cs`. Its exit code is 1 for a failing result (a difference outside `--expect`) and for an error. Its scratch folder is `%TEMP%\sfscompare-scratch` unless `--scratch` or `SFSCOMPARE_SCRATCH` names another.
- The tests make their plugins with Mutagen in the work folder. They never write to the Data folder and never copy a `darkstarpatch_` file.
- The tests that held the comparisons against SFSearch 1.x's `Check`, `Conflicts`, `Delta`, `Diff` and `PluginDiff` were retired on 2026-11-15: 2.0 removed those commands. `itm-headers` still holds every ITM of darkstar.esm against the files' bytes.
