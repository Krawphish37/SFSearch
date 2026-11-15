# SFSearch.Index: the header index

One pass over each plugin's record headers, with no Mutagen. It answers, without opening a record:
- what a FormKey or EditorID is, and where it sits in the file;
- who holds a record, in load order, and who wins;
- how many records and bytes of each type a plugin has;
- what is wrong with a plugin (a patch built for an older master, a missing master, a master that loads late).

It is a library, and every tool of SFSearch starts from it: what a FormKey or EditorID is, who holds a record, and what a read will cost all come from the header index. It reads record headers only, no Mutagen.

## Use it
```csharp
using SFSearch.Index;

using var index = new HeaderIndex(
    PluginSource.Folder(dataFolder),
    new IndexOptions { ScratchFolder = scratch, VersionText = "sfsearch 2.0 / mutagen 0.55.0-fixes.17 / schema 1" });
var report = index.Refresh();            // about 2 s for 370 plugins the first time, about 30 ms when nothing changed

var versions = index.Versions("0476C4:Starfield.esm");                 // every holder in load order, and the winner
var record = index.Describe(versions.Holders[versions.Winner]);        // record.Label = "Eon (0476C4:Starfield.esm)"
var hits = index.FindEditorId("eon");                                  // letter case aside, every plugin
var stats = index.Stats("Starfield.esm");                              // records and bytes per record type
foreach (var p in index.Plugins.Where(p => p.Problems.Any)) { /* p.Problems.Lines() */ }
```
- **Call `Refresh()` before each command.** It compares every plugin's quick stamp with the one it remembers and redoes only what changed. `RefreshLoadOrder()` checks plugins.txt alone.
- **`Refresh(verifyContent: true)` hashes every file again** (about 0.5 s for 4 GB in the file cache). Only this notices a change in the middle of a file that kept its size, date and first and last 64 KB.
- **String files of localized plugins** are stamped when `IndexOptions.StringFiles` is given (the record layer's `StringFiles`): `IndexedPlugin.Strings` holds every `.strings`/`.dlstrings`/`.ilstrings` file the plugin's texts are read from, loose or in an archive, with its quick stamp (an archived file has its archive's). A change is in `RefreshReport.StringsChanged`; the segment and keys stay. `StringsNow` takes them again for a quick check.
- **Files that change during a refresh:** a refresh lists the folder once and works from that list. A plugin deleted before it is read again is left out (and listed in `Removed` if it was known); one that changed while it was read is read once more, with the masters its new header names; one that changed during both reads gets no segment and the reason "it changed while it was being read", and the next refresh reads it. Whatever reading a file throws becomes that plugin's read error, never the refresh's. `sfsindex-tests --only late-header,folder-race` checks it.
- **One caller at a time.** A `HeaderIndex` isn't made for several threads at once. Several processes can each have their own on the same scratch folder: they map the same segment files.
- **The scratch folder is never a folder that holds plugins, or inside one.** `Refresh` refuses it before anything is written.

## The files
| File | Holds |
|---|---|
| `PluginHeader.cs` | The TES4 header: flags, size (Full, Medium, Small), masters. |
| `Stamps.cs` | The quick stamp, the content key (XXH3, 128 bit) and the reading key. |
| `StringsStamps.cs` | The strings stamp of a localized plugin, and the hook (`StringFilesSource`) that finds its string files. |
| `HeaderPass.cs` | The pass: FormIDs resolved with separated master numbering, EditorIDs, parent groups. A big plugin is cut into pieces of about 3 MB that any free thread reads. |
| `Segment.cs` | The segment file (one per plugin, memory-mapped read only) and its lookups. |
| `LoadOrder.cs` | plugins.txt, Starfield.ccc and the implicit plugins, read the way Mutagen reads them. |
| `HeaderIndex.cs` | The whole: refresh, the override map, winners, problems, lookups. |
| `ByteSearch.cs` | Which records of a plugin hold the bytes of a FormID (`FormIdPattern`, `ByteSearch`): the link search of `refs` and of the SQL layer's `Links WHERE ToKey` where no link file exists. Moved here from `SFSearch\Engine`. |

## A segment
A file named `<reading key>.seg` in the scratch folder. Entries are 24 bytes each and sorted by FormKey.

| Part | Holds |
|---|---|
| Header, 256 bytes | Format version, reading key, content key, counts, where each part starts. |
| Names | The masters in header order with the size each had, then the plugin itself. An entry's owner is a place in this list. |
| Signatures | Each record type with its count, its bytes (headers included) and how many are compressed. |
| Entries | Owner, local ID, signature, flags, file position, data size, parent, "has an EditorID". |
| Parents | The FormKey of every record that has others inside it (cells, worldspaces, topics, quests). |
| EditorIDs | Two sorted tables (by entry, by hash of the lower-cased text) and the text itself. |

A record whose FormID can't be resolved because a master is missing keeps its raw FormID and is marked unresolved. It is never given to a plugin by guess.

## The runner and the tests
```powershell
dotnet build SFSearch.Index.Runner -c Release
SFSearch.Index.Runner\bin\Release\net10.0\sfsindex.exe index "<Data folder>" --fresh
SFSearch.Index.Runner\bin\Release\net10.0\sfsindex.exe find "<Data folder>" Eon
SFSearch.Index.Runner\bin\Release\net10.0\sfsindex.exe problems "<Data folder>"

dotnet build SFSearch.Index.Tests -c Release
SFSearch.Index.Tests/bin/Release/net10.0/sfsindex-tests.exe "<Data folder>"
```
- The runner's commands are listed at the top of `SFSearch.Index.Runner\Program.cs`. Its scratch folder is `%TEMP%\sfsindex-scratch` unless `--scratch` names another.
- The tests need Mutagen 0.55.0-fixes.17 from the repository's `packages\` (`SFSearch.Index.Tests\nuget.config`). Only the test project uses Mutagen.
- `--all-plugins` compares every plugin with Mutagen. `--starfield-whole` includes Starfield.esm's cells and worldspaces: run that one alone, on a PC with memory to spare (4.3 GB).
- The tests copy a few small plugins to `%TEMP%\sfsindex-tests` and change the copies. They never write to the Data folder.
