# SFSearch.Reports: checks and reports

Nine reports on the new layers, each a structured answer and text under the output contract:

| Report | Question | Replaces today's |
|---|---|---|
| `check` | Is this plugin built right? | `Check` |
| `leveled` | What can this leveled list give? | `Leveled` |
| `dialogue` | What do this quest's (or one topic's) characters say? | `Dialogue` |
| `sources` | How does the player get this plugin's items? | `Sources` |
| `archive` | What is inside these BA2 archives? | `Archive` |
| `assets` | Are the files this plugin's records name there? | `Assets` |
| `materials` | Are the textures behind this plugin's materials there? | `Materials` |
| `scripts` | What do this plugin's Papyrus scripts do, and which records carry them? | `Scripts`, `Q vmad` |
| `changes` | What changed in Data since last time? Which patches are at risk? | `Changes` |

It is a library: SFSearch's `check2` verb and the reports behind `sfs_run` (tools `sfs_check` and `sfs_run`, `SFSearch\Engine\ReportTools.cs`) are built on it. No record data is stored, and nothing in it knows about any one mod.

It stands on `SFSearch.Index` (who holds a record, who wins, what is wrong with a plugin, each plugin's content key), `SFSearch.Records` (plugins held open, every record as full-profile JSON), `SFSearch.Compare` (the diff engine, for overrides that change nothing), `schema.db` (which record types a link may point at, which fields are file paths, where a script list sits) and Mutagen's archive reader.

## Use it
```csharp
using SFSearch.Records;
using SFSearch.Reports;

var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
using var session = new ReportSession(dataFolder, scratchFolder, schema);
session.KeptFolder = keptFolder;                                          // for scripts and changes: outside the game's folders
session.Champollion = ScriptsReport.FindChampollion(null, sfsearchHome);  // for scripts

CheckAnswer c = new CheckReport(session).Check("darkstarpatch_crimsonx.esm", new CheckOptions { World = false });
LeveledAnswer l = new LeveledReport(session).Leveled("0032EE:Starfield.esm", new LeveledOptions { In = null, Depth = 8 });
DialogueAnswer d = new DialogueReport(session).Dialogue("MQ101", new DialogueOptions { In = null, Language = "German" });
SourcesAnswer s = new SourcesReport(session).Sources("atom74.esm", new SourcesOptions { Dependents = true, With = new[] { "sfbgs003.esm" } });

var archive = new ArchiveReport(session);
ArchiveAnswer a1 = archive.Summary("darkstar.esm");                       // or "x - main.ba2", or "all"
ArchiveAnswer a2 = archive.List("darkstar.esm", "*.mat");
ArchiveAnswer a3 = archive.Find("bullpupdummyreceiver");                  // across every archive in Data
ArchiveAnswer a4 = archive.Show("darkstar.esm", @"materials\darkstar\concrete_black.mat");
ArchiveAnswer a5 = archive.Extract("darkstar.esm", "*.mat", @"C:\somewhere\outside\the\game");
ArchiveContents c = archive.Contents("darkstar.esm");                    // every file of each archive as data, no text (the add-on API's GameData.Archives)
using (var plan = archive.PlanGrep("all", new GrepOptions { Text = @"textures\common\concrete", Extensions = new[] { "mat", "cdb" } }))
{
    // plan.Files, plan.Bytes, plan.Seconds: the cost, before anything is read
    GrepAnswer g = archive.Grep(plan, maxChars: 24000);                   // g.NextItem: where the next page starts (GrepOptions.Start)
}
AssetsAnswer as1 = new AssetsReport(session).Assets("darkstar.esm", new AssetsOptions { World = false });
MaterialsAnswer m = new MaterialsReport(session).Materials("aa50_longwatch.esm");
ScriptsAnswer sc = new ScriptsReport(session).Scripts("darkstar.esm", new ScriptsOptions { Find = "AddItem" });
ChangesAnswer ch = new ChangesReport(session).Changes(new ChangesOptions { Plugin = null, All = false });

string text = c.ToText(new ReportTextOptions { Detail = false, MaxChars = 24000 }, out int next);   // next: the cursor of the next page, or -1
JsonObject json = c.ToJson();                                                                       // the same parts as fields, nothing cut
bool failing = c.Failed;                                                                            // a check with the level ERROR found something
```
- **A plugin is a file name in Data or a path.** A file outside Data is read with its masters taken from Data.
- **A record is a FormKey or an EditorID.** Six hex digits are also tried as a local ID. A name that fits several records lists them and picks none. Quests named `CF03` or `FC01` are EditorIDs, not IDs.
- **Errors** are a `ReportException`. Its `Line` is the output contract's one line: `Error: <what>. Fix: <what to do>.`
- **One caller at a time,** like the layers it stands on. Every plugin a report reads has its stamp checked before the first read and again at the end.
- **Nothing is ever written to the Data folder or the game's folder.** `ReportSession.ReadOnlyFolderOf` says whether a path is inside them; an extraction target or a kept folder there is refused.

## check
One PASS, WARN or ERROR line per check, then the records each check found.

| Check | Level | From |
|---|---|---|
| Masters present | ERROR | the header index |
| Masters load before the plugin (and are loaded) | ERROR | the header index and the load order |
| The header has one size (not both Small and Medium) | ERROR | the plugin's header |
| FormIDs fit the plugin's size (Small 0xFFF, Medium 0xFFFF) | ERROR | the header index, every record |
| FormIDs name the plugin or one of its masters (no master number past the plugin's masters) | ERROR | 4 bytes of each record's header |
| Own FormIDs are stored one way: not both the full index (03xxxxxx) and the size's form (FE001xxx, FD01xxxx) | WARN | 4 bytes of each own record's header (every record), and each link to an own record (from the JSON walk) found in its record's bytes |
| Own FormIDs use a form the plugin's size has (FE only in a Small plugin, FD only in a Medium one) | WARN | the same |
| Links point at records that exist | ERROR | the records, read as JSON |
| Links point at the right kind of record | ERROR | the records, and the schema's link targets (interfaces included) |
| Links stay inside the plugin and its masters | ERROR | the records |
| Overrides keep their base record's type | ERROR | the header index |
| Overrides have a record to override | WARN | the header index |
| No FormKey is used twice | ERROR | the header index |
| No deleted records | WARN | the header index, every record |
| EditorIDs are unique in the plugin | WARN | the header index, every record |
| New EditorIDs don't repeat a master's | WARN | the header index, every record |
| No identical-to-master overrides (ITMs) | WARN | the diff engine of `SFSearch.Compare` |
| Every record can be read | WARN | the records |
| Stored text reads one way: text stored in the plugin (not in strings files) is plain ASCII | WARN | each text field's bytes (RecordTexts), read as Mutagen reads them (code page 1252) and as UTF-8 |

- **Every record type is read,** not only the ones today's databases store.
- **World data is left out of the checks that read records, by default (the three link checks, the ITM check, the FormIDs' master numbers):** cells and worldspaces and everything nested in them (placed references, navmeshes, terrain), the navmesh info map, surface trees, blocks and patterns. The answer says how many records that leaves out and about what including them costs. `World = true` includes them. The checks made from record headers always cover every record.
- **Left out, with a note that says how many:** links to the engine's built-in forms (Starfield.esm below 0x800) and links inside a navmesh info map (counted through Mutagen, without making its JSON).
- **A link is judged by the version of its target the plugin sees:** its own record, else the last of its masters that holds it. When the plugin holds the target FormKey twice, the link is right when either record fits.
- **An ITM is an override with no value that differs,** the EditorID included. The record header's own numbers, a data block's length and other languages of a text don't count; a row says when an override holds a text in fewer languages than its master.
- **How the plugin stores its own FormIDs** is a line of its own above the table: the record headers and the links to its own records counted by form, and the form the header gives (`OwnForms`; JSON `ownFormIds`). In each record whose links (the walk's) include one to its own records, Mutagen says where each FormID naming the plugin's own records sits and in which form it is stored (`FormIdForms.OwnLinks`, Mutagen's `GetStoredFormIds`, fixes.16 C203); the headers are read from the file (4 bytes each); links in world data count only with `World = true`.
- **A plugin whose master is missing** gets ERROR for that, and NOT RUN for the checks that read records: without the master its FormIDs can't be told apart.

## leveled
- Works for every leveled list type the schema has: `LVLI`, `LVLN`, `LVLB` (ships and other base forms), `LVLP`, `LVSC`.
- Each list is read from the version that wins in the load order. With `In`, the lists that plugin holds are read from it.
- Each entry: level, count, its own chance of nothing, the record it gives with its type, conditions. Each list: its chance of nothing (or the global that gives it), flags, the most it gives, its own conditions, filter keywords.
- A value the JSON leaves out is its default, from the schema.
- A list that leads back to a list above it is reported (`Stopped = "loop"`), not followed. Lists deeper than `Depth` are named, not unfolded.

## dialogue
- Topics and lines are found through the header index: a topic's parent is its quest, a line's parent its topic. Every loaded plugin that holds dialogue and knows the quest's plugin is looked through. Each topic and line is read from the version that wins. With `In`, only that plugin's topics and lines.
- A line: speaker, the texts of its responses in the chosen language, the player's prompt, conditions, the scene it starts, the quest stage it sets (when it begins, when it ends), the speech challenge.
- The speaker comes from the line's `Speaker` field, from a `GetIsID` or `GetIsVoiceType` condition, or from the line it is grouped under (`DialogGroup`). `SpeakerFrom` says which.
- A line that shares another line's responses (`SharedDialog`) shows that line's texts.
- Lines come in the order of the topic's own list. Lines the topic holds and its list doesn't name follow.

## sources
- The items are the plugin's own records of the item types: weapons and armor unless `Signatures` names others.
- From each item the links are followed backwards, through leveled lists, form lists and outfits (`Depth` lists deep), to: a recipe (with its conditions and workbench), a quest (stage reward, alias, script property, dialogue), a script that adds the item's list to other lists, a vendor, a container, a placed copy, an NPC.
- **A form list's AddToList (ANAM) is followed** (`FormLists.cs`, GAPS S61): the game adds a form list's entries to the form list its AddToList names, so from a form list the walk goes on to that list (its winning version's AddToList; for a plugin outside Data, its own version's), and the via says it ("X (by Y's AddToList)"). A form list that names the list in its AddToList doesn't hold what the list holds: the walk doesn't go back along that link. A form list's entries as the game uses it (`ReportSession.FormListEntries`, a `ListEntries`: for an injector whose target lists are a form list's entries) are its own Items, then those of every form list whose winning version adds to it, followed on, each list once (a cycle ends at a list already taken); `ListEntries.AddedBy` names the lists that added, and the source's text names them. Which lists add to which (`FormListAdds`) is read once per command from the bytes of the winning FLST records of the loaded plugins, on all cores (3,884 lists, about 50 to 200 ms).
- Searched: the plugin, the plugins named in `With`, and (unless `Dependents = false`) every loaded plugin that has the plugin as a master. Its masters aren't searched.
- Each source has a `Reach`: yes, no or unclear. An item's `Reach` is the best of its sources': whether any path ends at something the player can reach.
- **Vendors are found the way the game finds them** (`Vendors.cs`). A faction's merchant container (`Faction.MerchantContainer`, VENC) is a placed chest; its base container's items are the faction's stock; an NPC whose `Factions` hold the faction sells them. The factions of the searched plugins and their masters are looked at once per run, only when the walk first meets a container, a master's list nothing searched holds, or a list a script adds to: each faction's merchant container (VENC) and that chest's base (NAME) are read from the files' bytes on all cores, and only the factions and chests of the container met are then read through Mutagen, which decides (all of them when a list's vendors or the NPCs are needed). A container that such a chest is a copy of, or a master's list such a container holds (down to `Depth` lists), is a source of the kind Vendor, "found by the faction's merchant container". A container or list with "Vendor" in its EditorID that no faction names is still a vendor, "found by its EditorID only". `ItemSource.FoundBy` and `ItemSource.Vendors` carry it; the JSON has `foundBy` and `vendors`. NPCs are looked for with a byte sieve (the faction's FormID in the NPC's bytes), then read through Mutagen.
- **"What points at this" sits behind `IBackLinks`** (`BackLinks.cs`). The default, `ByteBackLinks`, searches the bytes of the searched plugins' records (placed references included, other world data left out) for the FormID of each record the walk asks about, many at once (`ByteHolders`: one pass over each plugin for each list depth of the walk, all plugins at once, all cores), and reads through Mutagen only the records that hold it, walking their links. `ScanBackLinks` gives the same links by reading every record of the searched plugins once (`sources2 --scan`). SFSearch's link index can be put behind the same interface: pass it as `SourcesOptions.BackLinks`.
- **A script that adds the item (or its list) to other lists is told by its compiled code** (`ScriptLinks.cs`, `ScriptInjections`): a record that holds the item in a script property is an injector when the script's code (or a script it extends) calls `AddForm` on a leveled list or form list held in a property, with the item's property (its value, a struct member, an array element, or each entry of a form list it holds). The lists are that property's values on the record, or, when the code takes the list as an entry of a form list held in a property (`lists.GetAt(i).AddForm(item)`), each entry of that form list as the game uses it (its winning version). The values are followed through the script's own functions' parameters and paired by the call that passes them (`ProcessItemList(Armors, ArmorLists)` pairs the armors with the armor lists only), and into a function of another script the code calls with them (a library's `Inject(item, list)`). When no compiled script is found, or its code shows no such call, a quest falls back to the older rules: a struct that pairs the item with a list, or a script whose name says it injects (`AddItemToLeveledList`, `LLInjector`, `Inject`). Each such source says how it was found ("found in its code: LeveledItem.AddForm in OnQuestInit", "found by the script's name (its compiled script … is in no loose file or archive in Data)"); `ItemSource.InjectorFoundBy` and JSON `injectorFoundBy` carry "code", "struct" or "name". The compiled scripts are read once per process, a loose file first, then the general archives (the attaching plugin's first, then the game's).
- Other Papyrus code isn't read here. Today's `--scripts` part (scripts that call `AddItem` or `GetFormFromFile`) is `scripts` with `Calls` (`scripts2 <plugin> --calls`); the answer's note says so.

## archive
- **What is asked about:** `all` (every archive in Data), a `.ba2` (a name in Data, or a path), or a plugin: its own archives, `<plugin name> - <part>.ba2`, the names the game loads with the plugin.
- **`Summary`:** one line per archive: files, size, kinds of file, how many files also exist as a loose file in Data.
- **`List`:** the files whose path fits a pattern. A pattern with `*` or `?` must fit the whole path (`*.mat`, `meshes\*`). Any other text is looked for anywhere in the path. `Find` is `List` over every archive in Data.
- **`Show`:** one file's content as text (`.mat`, `.psc`, `.json`, …). A binary file is described, not printed.
- **`Extract`:** copies the files that fit a pattern (or one whole path) into a folder, with their folders inside the archive.
    - **Never into the Data folder or the game's folder:** refused with one line.
    - **A file whose path inside the archive leads outside the target folder is refused** (`..`, a drive, a rooted path, a colon). The answer names each one. This is the 1.0.1 security fix of today's `Archive --extract`, kept.
- **A loose file in Data stands in for an archived one of the same path:** each such file is marked.
- **A texture (`.dds`) has no size:** an archive's table doesn't hold a texture's size as a file.
- **`PlanGrep`, then `Grep` (`archive2 <target> grep <text>`, J18):** which files hold a text. A modder asks it to find which mod's file names a material, texture, mesh, sound or script string: a path written inside a mesh, a compiled material database, a particle file, an interface file or a script, where no record holds it.
    - **The contents of the archives' files, and of the loose files the game reads in their place:** for `all`, every loose file in Data's folders; for a plugin or one `.ba2`, the loose copies of its archives' files. A hit in an archived file that a loose copy stands in for is marked.
    - **The text, letter case not counting;** a `\` or `/` in it matches `\`, `\\` (a `.mat` or other JSON file writes a path so) or `/`. **Or a .NET regular expression** (`Regex`), compiled; a hit is at most 64 KB. `^` and `$` are a line's start and end, `\A` the file's first byte (J46). An ASCII text or regex is matched over the bytes read as Latin-1 (one character a byte: `\xE9` is the byte E9). A character written as a code matches only itself, while letters ignore case (J69, `Matcher.ExactCodes`): `\xC3` is made `(?:(?-i:\xC3))`, a class with such a code is split; without it `\xC3` also matched E3.
    - **A text or regex with a character outside ASCII (J55)** is matched over an 8-bit file read as text (`ArchiveGrep.ReadText`): UTF-8, and a byte that isn't part of a valid UTF-8 character as Windows-1252, decided byte by byte. So a letter outside ASCII matches in either case and in either encoding: `GRÖSSE` finds `grösse`. Data holds both, even in one file: the game's `starfield_en.strings` keeps 57 strings in 1252 (`Swaney’s Slate`) among its UTF-8 ones, and DarkStar's German, French and Spanish tables are 1252 throughout; every other 8-bit text file with a byte outside ASCII is UTF-8. Offsets are still bytes of the file (`TextMap`); the text around a hit is the text read. A piece is read as text only when its bytes hold the pattern's ASCII anchor (`Matcher.Anchor`: `AUSRÜSTUNG`, `STUNG`; a regex's plain letters outside groups), so a search of all of Data takes about as long as an ASCII one (34 s; 42 s and 38 s for a text and a regex outside ASCII). A plain text outside ASCII is timed on a sample before the estimate, as a regex is.
    - **On the bytes, not read (J71, `ArchiveGrepBytes.cs`):** a text outside ASCII, and a regex made of characters, classes, groups, alternatives and quantifiers, is made into a pattern over the bytes: each character it can match outside ASCII as its UTF-8 bytes and its Windows-1252 byte, with the conditions under which the reading takes that byte as 1252 (it doesn't start a valid UTF-8 character, it isn't inside one). It finds the same hits at the same bytes as the reading; only a kept hit's text is read around it. `ö` over all of Data no longer reads every piece.
    - **Every construct over the bytes (J80, `ArchiveGrepBytes.cs`):** the regex is read into its parts (`Matcher.Reader`), its inline options applied as .NET applies them (to the rest of the group they are in): `i` sets each atom's letter case, `s` what `.` matches, `x` the blanks and comments; `m` and `n` are kept as written. An atom of many characters (`.`, `\w`, `\d`, `\p{L}`, a negated class) is written as UTF-8 byte ranges (a lead byte and the continuation bytes it takes, grouped) and grouped Windows-1252 forms (`Matcher.Unit`). A character outside the BMP is two halves in the text read: the first stands at its 4-byte UTF-8 lead byte, the second at the three bytes after it, as the reading's offsets put them, so `.`, a class and a plain text (whose ordinal case also folds Deseret letters) match them there. `\b` and `\B` are look-arounds over the word characters (`\w` and U+200C, U+200D, measured); next to an atom that is all word characters or none, one look-around. Look-arounds and conditionals keep their place, their content in byte forms.
    - **Which route (J80, `Matcher.MakeBytes`):** the pattern over the bytes when a hit can start at 64 byte values or fewer (the regex skips to them); else **near a rare character** every hit holds (outside look-arounds, alternatives and optional parts): its places are found on the bytes, the text read around each (as far as a hit and its look-arounds can reach, from the pattern's lengths) and the real regex matched there (`FindNear`). A back-reference (it repeats a text in either case and either encoding) always takes the near route. With no length limit (`\w+ö`, `(\w+)ö\1`, `.*ö`) **in the runs** around such a place (`FindInRuns`): a hit holds only characters its atoms can match, so it lies in one unbroken run of them; the run's ends are found on the bytes (right to left and left to right; at once when the characters outside the set are ASCII, as `.`'s line feed). A pattern that repeats a many-character atom with no limit, or keeps a `\b` of four look-arounds, goes near or to runs first. Each thread has its own copy of the regexes used once per place (`Matcher.Own`): a `Regex` keeps one runner, and the threads made a new one on nearly every call. What has no rare character (`[^ö]+ö`, `ö|.`; GAPS S113) is read as text, as J55 did, and the answer's note says why (`Matcher.TextReason`): on the bytes it would be tried at almost every byte (5-90 MB/s a thread on the game's meshes, against 120-550 read as text). The made-bytes test checks every route, and every regex's pattern over the bytes, against J55's reading.
    - **UTF-16 text (J39):** a text kind (`.txt`, `.json`, ...) that starts with a UTF-16 byte-order mark (`FF FE`, not UTF-32's `FF FE 00 00`; or `FE FF`) is searched as UTF-16 text: the interface's 9 `translate_*.txt` tables, the only such game files (the S69 census). Its offsets are still bytes of the file (two a character); its hits are marked. Decided by the kind, not the bytes: the 548 `.lod` start with `FE FF` as a number. A file without a mark isn't tried as UTF-16 (the census: every UTF-16-looking run in other game files is noise).
    - **Pieces:** a file is read 4 MB at a time; each piece carries the 64 KB before it as context (already searched: `^`, `\b` and a look-behind see it, `\A` doesn't match there) and the 64 KB after it to search next; a regular expression goes on from the end of its last match, so each match is found once, as in a search of the whole file.
    - **`GrepCount` (`--count`, J45):** no hit listed; the hits and the files that hold them per kind of file, and per archive, over the whole scope: "how many of the game's meshes name this?".
    - **`all` leaves out the files at Data's own top level** (not plugins or archives: a mod manager's backups, read-mes, settings), and says how many and of which kinds (J47): the game reads assets only by paths with a folder.
    - **Each hit:** the archive (or `loose`), the file, the byte offset, the line in a text file, and the run of printable text it sits in (up to 60 characters each side). A file's hits are all counted; the first 1,000 kept. A compressed Flash file (`.swf` `CWS`, `.gfx` `CFX`: 313 of the game's 316 `.swf`) is searched inflated, its offsets those of the uncompressed file.
    - **The scope:** the target, `Extensions` (`--ext`) and `Folders` (`--folder`). Textures and images, sounds and video are passed over unless an extension names them (`*`: every kind); a note counts them.
    - **The cost first.** `PlanGrep` reads the archives' name tables (all at once), the file records of only those with a name in the scope, and the loose files' sizes: about 2 s for all 457 archives of a Data folder. From the files and their inflated bytes it estimates the time, for a cold disk; a regular expression's own speed is timed first on the first MB of up to 8 files spread over the scope (at most 0.25 s), since it ranges from 41 MB/s to 15 GB/s a thread (J48). `archive2` answers "Not run" with that estimate over the heavy limits (10 s), and runs with `allow_heavy`. The answer says the estimate beside the time the search took.
    - **The speed order:** only the files in the scope; the stretches of each archive that hold the scope's files are read ahead from disk in the archive's order, neighbours less than 1 MB apart merged, while its files are inflated (cold, file by file, a mesh archive came at 55 MB/s; one pass at about 4 GB/s); where a file's bytes are, how many and whether they are compressed is Mutagen's `IArchiveFile.GetStorage()` (fixes.18, GAPS S68), and the estimate takes each file's own stored bytes and compression; the files inflated and searched on all cores in their order, a batch at a time; the search stops once the page is full, and the cursor goes on from the next file. Nothing is kept between calls.
- **`PlanEncodings`, then `Encodings` (`archive2 <target> encoding`, J70):** how each file's text is written, for a translator or modder about to edit a string table or text file. The same scope, cost and reading on all cores as grep (`ArchiveEncoding.cs`).
    - **Per file:** ASCII only, UTF-8 (with its byte-order mark or not), Windows-1252, mixed UTF-8 and Windows-1252, UTF-16 LE or BE (by its byte-order mark), or binary: the first control byte (below 20 but tab, line feed, form feed and carriage return, or 7F) and where it is. Each byte outside ASCII counts as UTF-8 where it is part of a valid UTF-8 character, as Windows-1252 where it isn't (the rule grep reads text by); the first such byte with the text around it.
    - **A string table** (`.strings` zero-ended, `.dlstrings`/`.ilstrings` with lengths) string by string: ASCII, UTF-8, Windows-1252 or mixed strings, and the strings not in UTF-8 with their IDs, offsets and text (the first 1,000). A file whose layout isn't a string table's is read as bytes and said so.
    - **Only the bytes needed:** a file is read from its start until it is known; the first piece is 64 KB, so a binary file (a mesh) stops there, and the estimate counts a binary kind's first 64 KB and a text kind or string table whole.
    - **The list** names the files with text outside ASCII (`--detail`: every file, every string not in UTF-8); `EncodingsCount` (`--count`) counts files per kind and per archive. All string tables of Data (1,161 files) in 0.3 s.
- **Read live with Mutagen's archive reader.** File lists stay in memory for up to 250,000 files while an archive keeps its size and date. A walk over all 457 archives of the Data folder reads each list, uses it and lets it go. A summary, and the lookups of `assets` and `materials`, read only an archive's name table (its header and the names at its end, not the file records), through Mutagen's `EnumerateFilePaths` since fixes.16 (C201): `archive all` in 0.9 s instead of 2.6 s. The names are kept as hashes for the rest of the process, shared by these reports.

## assets
- **Every file path on the plugin's records** is looked for: loose in Data, in the plugin's own archives, in its masters' archives, in the other archives in Data.
- **Which fields are paths:**
    - The fields the schema types as file paths: the ones Mutagen types as asset links. There are 7 (a model's `File`, a first-person model's `File`, a debris model, a landscape texture's material, a perk's icon, a skeleton's rig and animation root). They can be reached at 402 places in 109 record types.
    - Mutagen types the other fields that hold a path as plain text (an ammunition's casing model, a projectile's muzzle flash model, a terminal's marker model, a component's rig file, a tint's texture). Those are found by their value: a text ending in `.nif`, `.mesh`, `.mat`, `.rig`, `.agx` or `.dds` in a field the schema calls a string.
    - Every row says which way it was found. No field is named in the code.
- **How a record is read (GAPS S30):** from its bytes, where the schema places every subrecord that can name a file (`AssetFields.cs`): a model's file, a gendered model's parts, a component's fields (between its BFCB and BFCE; the kind is the one the BFCB text picks by Mutagen's XML, `SFSearch.SchemaGen\Selectors.cs`), an armature entry's armor addon link. A field that can't hold a file name (a number, a flag, another link) is passed over. A record with any other subrecord that holds a file extension's text, or that may hold a typed path or an armor addon link, is read through Mutagen and its JSON walked, as before. The test `assets-fields` holds the two against each other on every record of 12 plugins (44,921 of 44,945 read from their bytes; 24 left to Mutagen). The records are read on all cores; then every path is looked for at once, the archives' names read a few archives at a time on all cores (only their hashes), stopped once every file is found.
- **In a new process** (assets2 with no service), Mutagen's start-up runs on another thread, and the count of path fields, the plans for the plugin's record types and the names of its own and its masters' first archives are made while the header index waits for it. atom74.esm: 2.5 s to 1.07 s.
- **Where a path is looked for:** as written (a leading `Data\` taken off), then under `meshes\`, `materials\` and `textures\`. A model `Weapons\x.nif` is the file `meshes\weapons\x.nif`.
- **Results:**
    - Found: loose, the plugin's archives, a master's archives, another loaded plugin's archives (said in a note: it holds while that plugin stays).
    - MISSING: no loose file and no archive holds it.
        - *Inherited* when the master's version of the same record names the same path: the plugin didn't bring it. Starfield.esm's own records name about 2,000 files that aren't in the game's archives.
        - *By value* when its field isn't typed as a path: the text may not be a file name as written (a body part's `BASE Meshes\…`).
    - NOT LOADED: only in an archive the game doesn't load (its plugin isn't in the load order, or isn't in Data).
    - A material that is no file: compiled into a material database, or missing. Counted apart, not judged.
    - Not checked: the path holds a variable (`$(Subgraph)\…`), or has no file extension (an icon's name).
- **An armor's addons that live in a master are followed** (the links the schema says name an `ArmorAddon`): their models are what the armor shows. Each addon is read once and its paths listed once, with every record of the plugin that uses it (`UsedBy`; `Record` is the first of them, in file order). What is missing there counts as inherited: the master's to bring.
- **An armor addon link that names a record of another type** (its real type from the header index: an OMOD, a FURN, a MISC) is the plugin's problem, listed first, in compact and in detail (`WrongLinks`), and the answer fails. Its files aren't checked as an addon's. When a master holds an armor addon with the same ID, the row names it: most often the FormID names the wrong master (a patch built for an older version of a master).
- **Compact: the plugin's own problems, one per row, then the inherited ones folded by record type and field.** `Detail` lists every path.
- World data is left out unless `World = true`, as in `check`.

## materials
- **The meshes are the `.nif` files in the plugin's own archives, and every mesh its records name, wherever it is** (GAPS S8): a mesh of its own archives that is also a loose file is read from the loose file (the game uses that one); a mesh its records name elsewhere is read from where the game takes it, a loose file first, then its masters' archives (the game's too), then other loaded plugins'. Its rows say where (`meshes\x.nif (loose)`, `(Starfield - Meshes01.ba2)`). A mesh its records name that no loose file or loaded archive holds is counted and named in a note (the assets report lists it). A record's mesh paths are every text ending in `.nif` in its subrecords, read from the bytes the report reads anyway. The structured form counts, instead of listing, the materials only other plugins' meshes name that have nothing wrong (`ofOtherMeshesFound`: darkstar.esm's records name 360 of the game's meshes). A mesh names its materials as text (its header's strings, and length-prefixed texts in its blocks; a text counts only whole, after its length) or by hash: a world's object LOD meshes (`meshes\terrain\<world>\objects\*.nif`, BSWeakReferenceNode blocks) name each material by its resource ID, the folder's and the file name's CRC-32 then `mat\0` (`MaterialsMesh.cs`, GAPS S46). A material named by hash is the plugin's own path row when the hashes agree; otherwise it is a row "(named by hash: folder 0x..., file 0x...)" with the name its database gives it, looked up in the compiled databases by its hashes.
- **The records:** every text ending in `.mat` on the plugin's records (a landscape texture's material, a biome's or worldspace's water, a planet's ring). A record is read only when its bytes hold `.mat`; world data is read for a plugin of up to 100,000 records, as in `scripts`. A row says which records name it (`NamedBy`).
- **A material that is a `.mat` file** (in the plugin's archives, loose, or in another archive) is read as JSON. Each texture it names (`FileName` ending in `.dds`) is looked for the same way as in `assets`.
- **A material that is no file is looked up in the compiled material databases** (`materialsbeta.cdb`, `MaterialDatabase.cs`): the game's own first (`materials\materialsbeta.cdb` in `Starfield - Materials.ba2`), then those in the plugin's archives, its masters', other loaded plugins', and archives the game doesn't load, then the databases the game doesn't read. The row says which database holds it, and whether that is the game's. One in no database is **missing**. If a database can't be read, such a material is "not judged" instead.
- **The databases the game reads** (`MaterialDatabase.GameReads`): its own, and every `.cdb`, at any path, in an archive named `<plugin> - Main.ba2` (`- Main01.ba2` and so on). Read from the game's code (j811): it loads its own by its path (`Materials/` + `MaterialsBeta.cdb` or `MaterialsFinal.cdb`), and its file listener for materials and particles (`MaterialAndParticleFileListener`) keeps the extension `.cdb`, matches each archive's name against `.* - Main[0-9]*\.ba2` (case ignored) and takes every `.cdb` of a matching archive; nothing else in the code names `.cdb`. The test holds this against the executables it finds (`ExeListener.cs`). Not run in the game. A material only in a database the game doesn't read (loose in Data, or in another archive) is a problem: `NOT READ BY THE GAME`. All 21 databases of this Data folder are read by the game.
- **A database is read as far as its material list, once per process, and only when a material is no file.** A database knows a material by a hash of its path (CRC-32 of the folder and of the name), so it can be asked about a path but not list its paths. The game's list ends 16.7 MB into 100 MB; reading it takes 0.15 to 0.7 s and allocates about 3 MB. The `[cost]` line says what each read took.
- **A compiled material's textures** (`MaterialContents.cs`): a database that holds one of the answer's compiled materials is read whole, once per process: its objects, which object inherits from which (a mod's may inherit from one of the game's), and the links and texture names in their components. The material's parts are followed (layers, blenders, LOD materials, their materials, texture sets) and every texture they name is looked for as a file, as a `.mat` file's are. Each object's name (its `BSComponentDB::CTName`) is kept too, for the view below. The game's: 100 MB, 0.7 to 1.4 s from its archive, about 195 MB allocated, 57.8 MB kept (16 MB of it the names). The databases read whole are let go, and the memory compacted, once no view has used them for 5 minutes (`ArchiveStore.IdleRelease`): a worker that answered `--texture` holds about 650 MB until then (125 MB of it the 21 databases, most of the rest the garbage of the read), about 100 MB after. The material-path gather lets go of the databases it read itself as soon as it ends. A texture a material of the game's own database names and the game doesn't ship (`ar99_reflex_curvature.dds`, `beard02_rough.dds`: 509 of the 87,279 the game's materials name) is listed apart, not counted as missing.
- A texture found only in an archive the game doesn't load counts as missing, and the row says where it is.
- **One material, and a texture's users** (`MaterialsView.cs`, `materials2 --material <path>` and `--texture <path>`): which hold a material's path (a `.mat` file; each compiled database, and whether the game reads it), and its parts as a tree: the material and its parent material, its layers, each layer's material, texture set (each texture by its slot, found or missing) and UV stream, its blenders and LOD materials, with the names the format gives them. A `.mat` file is shown the same way, with what it takes from its parent (an object of the game's database, by its `res:` ID) marked. `--texture` reads every database whole and walks up from the objects that name the texture to the materials that end up with it. `--texture` also lists the `.mat` files, loose and in every archive, that name the texture themselves: each archive's `.mat` texture names are read once per process, and only the files that name the texture are read as JSON. When more than one place holds a material's path, the answer says which copy the game most likely uses and that only a test in the game proves it. Texture slots carry the game's own names, the enum `BSMaterial::MRTextureChannel` (0 to 20). A database holds hashes of paths, not paths: a material it names is given a path when its name is its file's and its folder is one of those tried (the cheap tries: the game's 39,434 of 48,749); when that fails, `MaterialPaths.cs` gathers the paths and folders Data names (`.mat` files, every mesh's header strings, every archive folder, the textures' folders, the `.mat` texts of the plugins' records, read raw from the 14 record types that hold them: 60,342 records of 127 plugins, 229 MB, 0.2 to 1.7 s; the particle files, `.blob`; and the databases' own object names that hold a path, such as SFBGS007's `Data\Materials\Test\Test\MGoulet_Pattern05_Test.mat_Material2`): the game's 48,644 of 48,749. The 105 left are named nowhere in Data: every file of every archive but textures, sounds and video, every loose file and every record of every plugin were searched for them. The gather runs with `--all-paths` or in the service's indexer lane (a few seconds) and is kept in the scratch folder, keyed on every archive, loose `.mat` file and plugin. Warm in the service: 130 to 170 ms for a material, 0.4 s for a texture.
- **Compact: the materials with something missing.** `Detail` lists every material.

## scripts
- **The compiled scripts (`.pex`)** come from the plugin's own archives, from loose files under `Data\Scripts` whose path holds the plugin's name, and from other loose scripts its records attach. A loose copy stands in for an archived one of the same path, as in the game, and the answer says how many do.
    - A loose script without the plugin's name is taken when the plugin's script blocks name it: each name is looked for, with the length a script entry writes before it, in the raw bytes of every `VMAD` (several threads, straight from the file; darkstar.esm's 47,021 records in about 0.5 s). The answer names each one taken. One that a master's archive holds too is the master's script with a loose copy (MissionsGalore.esm's `missionparentscript`): not taken.
    - A loose script without the plugin's name is also taken when the plugin's scripts use it: their parent (`extends`), a script their code calls (a static call, or a method of a variable of that script's type), or a script they name as a type. The game links all of them when it loads the script. This is followed again from each script taken. One that a master holds (a master's archive, or a master's name in its path) is the master's: not taken, and the answer says so. UnityHealthScaler.esm's loose script calls the loose library `CassiopeiaPapyrusExtender`, which names `ArmorAddon` and `Biome` as types: the three are taken, each with why ("called from UnityHealthScaler", "named as a type in CassiopeiaPapyrusExtender").
- **Decompiled with Champollion once per content.** The sources go to `<kept folder>\scripts\<content key>\`:
    - The content key is a hash of every compiled script's name and bytes. The same scripts are never decompiled twice, wherever the archive is and whatever its date says. A changed script gives another key and another folder.
    - A quick stamp of each archive (size, date, a hash of its first and last 64 KB) only saves reading the compiled scripts again: when it is the same as last time, the content key is the one remembered.
    - The folder holds script source and a manifest. No record data.
    - Once made, the sources answer without Champollion.
- **Champollion** is found by `ScriptsReport.FindChampollion`: a path given, the `SFSEARCH_CHAMPOLLION` variable, the `champollion` setting of `sfsearch.json` in an SFSearch home, or `tools\champollion\Champollion.exe` there. When it isn't found and the scripts were never decompiled, the report stops with one line.
- **List:** each script with its lines, where its compiled script came from, and the plugin's records that attach it, or "attached to nothing" (with the plugin's scripts that extend it, call it or name it as a type, from the compiled scripts).
    - A record's scripts are read live: the name of every script entry in the record (on the record itself, on a quest's aliases, in script fragments). The schema says what a script entry is and which subrecord holds a script list (`VMAD`); a record is read only when its bytes hold that subrecord.
    - Scripts on the plugin's records that aren't the plugin's (the game's, a master's, another mod's) are listed after, each with where the game finds it: loose, or the archive. One that no loose file and no archive the game loads holds is **missing**: named in the answer's head and marked `MISSING` in the list. Archives that hold no file of the extension are passed over by their file tables (36 bytes a file), without reading their names.
    - **The scripts its scripts use** (their parent, a script their code calls, a type they name; `PexObject.Uses`) that aren't its own are looked for the same way (loose, its archives, its masters', the other loaded plugins'; the game's own Game, Debug, ObjectReference … are in `Starfield - Misc.ba2`). One that is nowhere is **missing**: the game can't link it (a script whose parent is missing doesn't load; code that calls or names a missing script fails, harmless only when that code never runs, as in optional support for another mod). The answer's head names each ("Missing: 3 scripts the plugin's scripts use …") and the list has a line each: `MISSING: <script>, the parent of / called from / named as a type in <script>`. JSON: `missingUsed`, counts `usedLookedFor`, `missingUsed`.
    - A plugin of up to 100,000 records is read whole, placed references included: a placed object can carry a script. A larger one (Starfield.esm) is read without its world data, and the answer says so. `World` forces either.
- **`Find`:** every line that contains a text. Letter case doesn't count. The text is taken as it is: no wildcards.
- **`Show`:** one script's source, by its name or the part after the last colon.
- **The code that reaches records without a link in a record** (in the list, and alone with `Calls`, which needs no decompiler). Each compiled script is read (`Pex.cs`: Starfield's format, version 3.12, checked on all 4,740 scripts of `Starfield - Misc.ba2` to the last byte) and its functions walked (`ScriptCode.cs`):
    - `Game.GetFormFromFile`, `Game.GetForm`, `GetFormByEditorID` and the like, with the FormID and plugin when the code gives them as constants (also through a local or a script variable set once), and the record they name in this Data folder, or why there is none (`DarkStar_Test.esm isn't in Data`, no such record). `Game.GetForm` with a load-order byte other than 00 can't be named.
    - `Game.IsPluginInstalled` and the plugin it names.
    - `AddItem`, `AddAliasedItem`, `AddForm`, `PlaceAtMe`, `PlaceActorAtMe`, `PlaceShipAtMe`, `AddPerk`, `AddSpell`, `EquipItem` on a record the code got with one of the getters (followed through assign, cast, arrays, struct members and method results, and through a script variable set in another function), on a script property, or on one decided at run time (what another function returns, what another plugin passes in).
    - **Branches are followed:** each variable keeps every value it can have at each instruction (up to 8; loops settle in at most 40 passes). An `if` whose paths set different constants, or hand out different properties, gives each (MojoSTC.esm's `OnDying` hands out one of four properties: all four are listed).
    - **A parameter is followed to the calls of its function** in the plugin's scripts (the function's script or one that extends it, by the declared type of what the method is called on), up to 3 callers deep and 64 values: darkstar.esm's `CheckForESM(asName)` gets five plugin names from `CheckAll`. With no such call, the answer says so (an event the game sends, or "no script of the plugin calls it").
    - **A value held in a script property** (the record handed out, a FormID, a plugin name, a member of a struct property, an element of an array property) is read from the plugin's records that attach the script or a script that extends it, through Mutagen: one value per record, "on <record>: <property> = <value>", records with the same value together. A placed reference that doesn't set the property has its base object's value; a record that sets it nowhere has the script's default. In `Calls`, only the records whose script block names those scripts are read (darkstar.esm: 50 records).

## changes
- **Now:** the header index's plugins, each with its content key, size, masters and their sizes, records and problems.
- **Before:** a history log, `<kept folder>\history\<a hash of the Data folder's path>\`:
    - `log.jsonl`: one line per plugin copy seen: content key, size, masters with their sizes, record counts per type, overrides of another type than their base.
    - `lists\`: per copy, a compact list of its FormKeys and signatures (6 bytes a record before compression). A plugin with more than 250,000 records gets none.
    - No record contents and no EditorIDs.
- **A plugin gets a new line when its copy isn't the one the log saw last:** another content key, or the same content read with other master sizes. Nothing is judged by a file date. The time on a line is when the report saw the copy.
- **The report:** plugins added, removed, back, and updated (records added, removed and changed type; a change of size; masters changed), the ones found by this call first. Default: the last 30 days. `Since`, `All`.
- **Patches at risk:**
    - a plugin whose overrides are of another type than the records they override (from the header index's problems);
    - a plugin whose master changed size after this copy of it was first seen (from the log).
- **With a plugin:** each of its snapshots, and what its last update added and removed, record by record. An added record is named from the plugin as it is now. A removed record that no plugin holds any more has only its FormKey and type.
- **The first call begins the history:** every plugin is remembered as it is, and nothing is reported.

## The answer
```text
<what was asked, the summary, the table>
<rows>
[more] 40 of 312 lines shown (1 to 40). Next: cursor "40".
[notes] …
[source] darkstarpatch_crimsonx.esm 52.0 KB, key 0f3c9a1e; …
[cost] 0.31 s; 122 of 122 records of darkstarpatch_crimsonx.esm read; 4 overrides compared with their master
```
- **Compact by default.** `check` prints 25 rows per check and how many more there are; `sources` folds long lists; `assets` and `materials` print what is missing. `Detail` prints everything.
- **A long answer is cut between rows** at `MaxChars` and says so. The same call with `Cursor` continues it. The heading is on every page.
- **An answer with no rows says why** (`EmptyReason`).
- **`[source]` names each copy read:** a plugin by its size and content key, an archive by its size and date.

## The files
| File | Holds |
|---|---|
| `ReportSession.cs` | The session over the Data folder, the lookups the reports share, which records are world data, the game's folders that are never written to, the kept folder |
| `ReportAnswer.cs` | The answer's text and pages, `ReportException` |
| `LinkWalker.cs` | Every link of a record from its JSON and the schema; the rule for which record types a link may point at |
| `PathWalker.cs` | Every file path of a record from its JSON and the schema |
| `Conditions.cs` | Conditions as function, parameters, operator and value |
| `Check.cs`, `Leveled.cs`, `Dialogue.cs`, `Sources.cs` | One report each: its answer object and its method |
| `BackLinks.cs` | `IBackLinks`; the byte search that answers it by default (`ByteBackLinks`, `ByteHolders`) and the scan of every record (`ScanBackLinks`) |
| `Vendors.cs` | The vendors of a set of plugins: each vendor faction's chest, its container, the lists it holds, the NPCs in the faction. Used by sources |
| `FormLists.cs` | A form list's AddToList (ANAM): `FormListAdds` (which winning form lists add their entries to which, from the plugins' bytes, once per command), `ListEntries` (a form list's entries as the game uses it: its own, then those its adders add). Used by sources and by refs (SFSearch) |
| `ScriptLinks.cs` | Links in script properties: which script, property and struct member hold one, and what a leveled-list injector script adds it to, told by the script's compiled code (`ScriptInjections`: its `AddForm` calls, read with `Pex.cs` and `ScriptCode.cs`), else by a struct pairing or the script's name. Used by sources and by refs (SFSearch) |
| `Archives.cs` | The archives of the Data folder through Mutagen's archive reader, or their name tables alone; the loose files; the compiled material databases they hold, each read once |
| `MaterialDatabase.cs` | A compiled material database (`materialsbeta.cdb`): its format, the resource ID of a path, the reader that stops after the material list, which databases the game reads |
| `MaterialContents.cs` | The rest of a compiled material database, read whole: its objects, their names, parents and components, and the textures each material's parts name; one object's links and textures with what it inherits; the materials that use a texture |
| `MaterialsView.cs` | One material by its path (a `.mat` file or compiled) as a tree of parts, with which copy the game most likely uses; and the compiled materials and `.mat` files that use a texture (`materials2 --material`, `--texture`) |
| `MaterialPaths.cs` | The paths of materials a compiled database knows only by hash, from the paths and folders Data names (`.mat` files, mesh header strings, archive folders), gathered once per process |
| `FileFinder.cs` | Where a file is: loose, the plugin's archives, its masters', other loaded plugins', archives the game doesn't load |
| `AssetFields.cs` (with `SFSearch.SchemaGen\Selectors.cs`) | assets' reader of a record's path fields and armor addon links from its bytes, by the schema's subrecords; which component kind a BFCB names, by the selector values of Mutagen's XML (C57 W4, C63) |
| `ArchiveReport.cs`, `Assets.cs`, `Materials.cs`, `Scripts.cs`, `Changes.cs` | One report each |
| `Profile.cs` | profile: fields (schema paths, nested ones too, or every Unknown field) across a set of plugins: counts per plugin, values, zeros, what 4-byte values read as (float, FormID of a record its owner holds, strings ID, with C208's rule against chance), samples with their neighbours. Read through the record layer on several threads, added up in file order. `--conditions`: the condition census (GAPS S44): every condition at every place the schema has one, counted by its function (Mutagen's Condition.Function): conditions, records, plugins, record types, samples; `--function` one function. A link field's values come with the census of what they point at (`LinkCensus`: distinct targets, their record types by records and by uses, nulls, links to records no plugin holds, the most used; several link fields together) |
| `ProfileSubrecords.cs` | profile --subrecords: which record types hold a subrecord, across plugins, from the files' bytes (records found through the header index, inflated, their subrecords stepped through): records that hold it, times, plugins, a sample; and the types the schema puts fields of in it |
| `ProfileStats.cs` | profile --subrecords * (no --order): what a record type's records hold, from the files' bytes on all cores (RecordPipeline): records, bytes in the file and as read, every subrecord (records, times, bytes, largest), every link field with links, distinct per record, distinct targets and records: at its fixed place (`LinkPlaces`: a fixed offset in a subrecord of its own, a list packed in its subrecord, a list entry's own subrecord), or read by the byte link walk (`LinkPlan`, `SFSearch.Records\LinkWalk.cs`, GAPS S92) in the records that hold one of its subrecords; a field the walk can't reach is named with the profile call that counts it through Mutagen |
| `ProfileBytes.cs` | profile <Type.Path> when every field asked is a link field the bytes give (GAPS S92): the records stepped through on all cores, the answer Mutagen's walk gives (values, nulls, records, distinct values, census), the same samples, whose records alone are read through Mutagen |
| `ProfileWorld.cs` | the same census from the world link index (`IWorldLinkIndex`, made and kept by SFSearch's engine: `SFSearch\Engine\WorldLinkCensus.cs`) when every plugin read has its file (GAPS S99): the field's postings, null links not kept |
| `ProfileOrder.cs` | profile --subrecords X,Y --order (or `*`): the orders a record type's subrecords come in, from the files' bytes on all cores (RecordPipeline): each record's named subrecords with others as `…`, units repeated in a row as `(A B)+` (innermost first; a unit that repeats somewhere is written so where it stands once too); records grouped by order, per type, with plugins, a sample, and each repeat's least/most in a row and total |
| `ProfileValues.cs` | profile --subrecords X,Y --values (GAPS S118): every distinct value of the subrecords named, from the files' bytes on all cores (RecordPipeline): per subrecord and where it stands (the record, or the component by its BFCB text), text or hex, uses, records, record types, plugins, a sample |
| `ProfileLengths.cs` | profile --subrecords X,Y (or *) --lengths (GAPS S128): each record type's subrecords by length, from the files' bytes on all cores (RecordPipeline), inside components too: always empty, one length, varying or sometimes empty; the empty ones with records, plugins, where they stand and sample records |
| `ProfileFormIds.cs` | profile --formids (GAPS S119): each record header's FormID and every link (`LinkPlan.Links`, the links of profile --subrecords *) classified against the plugin's masters (a master's record, its own in the full index, FD or FE, past the master list, another size's form); by plugin and record type, the odd places with samples |
| `Groups.cs` | The GRUP-header view: every group's label decoded, type, stamp, version, the Unknown at bytes 20-23, size; per group type over a set of plugins. Read from a mapped file, records stepped over |
| `PluginSets.cs` | A set of plugins as a caller names it: names, `*` patterns, `@official`, `@loaded`, `@winners`, `@all` |
| `Pex.cs` | Compiled Papyrus (`.pex`) as Starfield writes it: string table, objects, variables, properties, every function's instructions |
| `ScriptCode.cs` | The calls in a compiled script that reach records without a link in a record (GetFormFromFile and the like, and what the code hands out) |

## The runner and the tests
```powershell
dotnet build SFSearch.Reports.Runner -c Release
$r = "SFSearch.Reports.Runner\bin\Release\net10.0\sfsreport.exe"
& $r check darkstarpatch_crimsonx.esm
& $r check kgcdoom.esm --world --detail
& $r leveled 0032EE:Starfield.esm --depth 4
& $r dialogue MQ101 --lang German
& $r sources atom74.esm --only
& $r links kgcdoom.esm 00B9A5
& $r archive darkstar.esm --list "*.mat"
& $r archive all --find bullpupdummyreceiver
& $r archive darkstar.esm --extract "*.mat" --to C:\somewhere\outside\the\game
& $r assets darkstar.esm
& $r materials aa50_longwatch.esm
& $r scripts darkstar.esm --find AddItem --kept <folder> --home <SFSearch home>
& $r changes --kept <folder>
& $r changes darkstar.esm --kept <folder>

dotnet build SFSearch.Reports.Tests -c Release
SFSearch.Reports.Tests/bin/Release/net10.0/sfsreport-tests.exe "<Data folder>" --work <folder outside the repo and Data> --report <folder> `
    --today <folder with today's answers> --sfsearch SFSearch\bin\Release\net10.0\SFSearch.exe --home <an empty test home> --baseline <the harness's work folder>\baseline\cli\recorded `
    --champollion-home <an SFSearch home with Champollion: only read>
```
- The runner's options are listed at the top of `SFSearch.Reports.Runner\Program.cs`. Its exit code is 1 for an error, for a check that found errors, and for assets or materials that found something missing. Its scratch folder is `%TEMP%\sfsreport-scratch` unless `--scratch` or `SFSREPORT_SCRATCH` names another. Its kept folder is `%LOCALAPPDATA%\SFSearch\kept` unless `--kept` or `SFSREPORT_KEPT` names another.
- The tests make their plugins with Mutagen in the work folder, and their kept files there too. They never write to the Data folder.
- `--today` is a folder of today's answers, one file per command. An answer that isn't there is made with `--sfsearch` on `--home` and kept. That builds today's stored databases on the test home (about 3 GB for the first four reports; `Assets` on darkstar.esm adds Starfield.esm's). Run today's `LoadOrder` there first, or today's `Leveled` reads each list from its own plugin (see the note). Delete the home afterwards.
- Without Champollion the two scripts tests print SKIP with the reason.
