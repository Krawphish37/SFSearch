# Working on SFSearch's code

For using SFSearch, see `README.md`. This file is for changing it. This file and each library's README hold what you need to change the code. The author's design notes, the measurements behind them and the list of gaps (what SFSearch can't reach yet, and why) are kept by the author and are not part of the public repository; code comments that cite a design note or a gap by its number point there.

## Rules
- **It only reads.** No command may write to the Data folder or the game's folder. Test plugins, extracted files and scratch files go elsewhere; the libraries refuse a scratch or target folder that holds plugins.
- **Nothing about a record is stored.** Plugins are read live through Mutagen. Working files go in the scratch folder, which the service empties at every start. Only decompiled scripts and the history log outlive a start, in `kept\`. Don't add a cache.
- **Never judge by "newer than".** A file's copy is told by equality of a stamp of its content, checked on every use. Mod managers install files with old dates.
- **No silent empty answer, no guess.** An answer with no rows says why. A name that fits several records lists them. A missing master is an error.
- **Every answer goes through the output contract** (`EngineAnswer`): the answer, `[more]`, `[notes]`, `[source]`, `[cost]`; an error is one line, `Error: … Fix: …`. Records are written `EditorID (FormID:plugin)`.
- **Heavy work says its cost first.** Over 10 s or 2 GB a tool answers "Not run" with the estimate, and runs only with `allow_heavy`.
- **Build with no warnings.** `sfs.cmd` rebuilds SFSearch by itself when any `.cs` file or the `.csproj` is newer than the build. Run `dotnet build SFSearch.Mcp -c Release` for the MCP server.
- **Keep the core general.** Checks for one mod belong in an add-on or a stored query, not in SFSearch.
- **Agents read the answers and the tool descriptions.** Every session loads the 12 tool definitions: keep them under 10,000 characters (`sfsharness mcp describe` counts them).

## The pieces
| Folder | What it is |
|---|---|
| `SFSearch.SchemaGen\` | Builds `schema.db` at build time from Mutagen's record definitions (the XML copy in `xml\<version>\`), the compiled package, xEdit's name pairs and `annotations.tsv`. Never reads a plugin |
| `SFSearch.Index\` | The header index: one pass over every plugin's record headers, no Mutagen. A memory-mapped segment per plugin; the load order, winners, problems, stamps and content keys |
| `SFSearch.Records\` | One record read live through Mutagen: its JSON, the card, named fields, raw subrecords, evidence, the one-record verify. Keeps plugins open as overlays |
| `SFSearch.Query\` | The SQL layer: scratch tables per record type, typed views and list views made from the schema, the three scopes, the checks before a query runs, the stored queries |
| `SFSearch.Compare\` | The schema-driven diff engine and the five kinds of comparison |
| `SFSearch.Reports\` | The reports: check, leveled, dialogue, sources, archive, assets, materials, scripts, changes |
| `SFSearch\Engine\` | The tools: one file each (`FindTool.cs`, `RecordTool.cs`, `DescribeTool.cs`, `QueryTool.cs`, `SavedTool.cs`, `RefsTool.cs`, `CompareTool.cs`, `LoadOrderTool.cs`, `ReportTools.cs`, `ServiceTools.cs`). `EngineCommands.cs` lists the verbs, `VerbArgs.cs` turns a command line into named arguments, `EngineAnswer.cs` is the output contract, `ProblemNotes.cs` puts a plugin's problems first, `EngineIndex.cs`, `EngineRecords.cs` and `EngineLibs.cs` hold one index and one record engine for every tool, `EngineLinks.cs`, `LinkFile.cs`, `LinkWalker.cs` and `LinksJob.cs` are the link index; the byte search is `SFSearch.Index\ByteSearch.cs` |
| `SFSearch\Service\` | The service: coordinator (`ServiceHost.cs`), workers (`ServiceWorker.cs`), settings (`ServiceConfig.cs`), messages and rendering (`Protocol.cs`), what a command can tell or ask the service (`JobContext.cs`), tools by name (`ToolCalls.cs`), the PC's memory (`MemoryInfo.cs`), test-only commands (`TestHooks.cs`) |
| `SFSearch\` (the rest) | `Program.cs` (command routing; `Program.Retired`, the older commands' names and the verb each points at), `Home.cs` (the home, the caller's folder, the Data folder and `SetData`, the scratch, kept and state folders, the note about 1.x databases), `CommandLine.cs` (the global options `--json`, `--csv`, `--maxwidth`, `--raw`, and `TakeOption`/`TakeFlag`), `AddOnApi.cs` (the add-on API), `Extensions.cs` (loading add-ons), `SessionServer.cs` (the command line's side of the service). The older commands were removed in 2.0 |
| `SFSearch.Mcp\` | The MCP server. `NewTools.cs`: the 12 tools, each sending the service a tool name and named arguments. `Bridge.cs`: the pipe, paging, jobs. (`--legacy-tools` and the 25 older tools were removed in 2.0: the flag is read past with a line on stderr) |
| `SFSearch.*.Runner\`, `SFSearch.*.Tests\` | Each library's own command-line runner (`sfsindex`, `sfsrecord`, `sfsquery`, `sfscompare`, `sfsreport`) and test program |
| `queries\` | The shipped stored queries, one `.sql` file each. `queries\README.md` is the format |
| `tests\SFSearch.Harness\` | `sfsharness`: the tools' goldens, the mapping of the recorded old commands, the 16 traps, the polish cases, round trips, the soak, the MCP test |
| `tests\SFSearch.ServiceTest\` | The service's tests: a client that drives a service on a test home |
| `extensions\SFSearch.Example\` | The add-on template |
| `packages\` | The Mutagen build SFSearch needs (see below) |
| `agent-docs\` | The guide agents read. Keep it in step with the tools |

Each library folder has a README with its API, its files and its tests.

## How data flows
```text
plugin files in Data
   │  SFSearch.Index: one pass over the record headers          → scratch\index (a segment per plugin)
   │  SFSearch\Engine link walk, in the service's indexer lane  → scratch\links (a link file per plugin, with in-game names)
   ▼
SFSearch.Records: a record read through Mutagen → JSON (Mutagen.Bethesda.Serialization, full profile)
   ├─ card, fields, raw, evidence, verify                       (sfs_record)
   ├─ SFSearch.Query: record types read into scratch tables     → scratch\query; typed views from schema.db (sfs_query, sfs_saved)
   ├─ SFSearch.Compare: two records' JSON, field by field       (sfs_compare)
   └─ SFSearch.Reports: checks and reports                      (sfs_check, sfs_run); kept\scripts, kept\history
```
- **The schema** (`schema.db`) is made at build time and copied beside `SFSearch.exe`. It gives every field three names (the Mutagen path, the file address, the readable xEdit name), its type, its link target, whether it may be absent, whether it is still unknown. The typed views, the cards, the diff and `describe` all read it. A new record type or field in Mutagen needs no SFSearch code.
- **The header index** answers what a FormKey or EditorID is, who holds a record and who wins, and how many records and bytes of each type a plugin has (which is where cost estimates come from). `Refresh()` runs before every command: it compares each plugin's quick stamp (size, date, the first and last 64 KB) and redoes only what changed.
- **A record's form is Mutagen's full-profile JSON.** Records nested in others (placed references, topics, scenes) are records of their own; the parent holds a `{"$Child": FormKey}` marker.
- **The link index** is built after the header index, plugin by plugin, small plugins first, and gives way when an agent is waiting. World records aren't in it; `refs` searches their bytes when asked, and confirms each hit through the same link walk.
- **Scratch files are named by a reading key:** the plugin's content key, its masters' names and sizes, and this build of SFSearch, Mutagen and the schema. A changed plugin, a master that changed size, or another build never reads an old file.
- **The service:** a coordinator that never reads a plugin, and worker processes that each run one command at a time and keep the plugins they have read open. One queue per agent, taken in turn. A crashed worker is replaced and its command runs once more. The coordinator and workers talk over named pipes open to the current user only.
- **Lanes.** Agents' commands first. Then the indexer lane (`~index`: the header index, then the link index; low priority). (The warm-up lane, `~warmup`, filled the older commands' tables; it went with them in 2.0.)
- **Heavy commands.** A tool calls `JobContext.RequireHeavy(estimateMB, what)` before a part that needs 2 GB or more. One heavy command runs at a time, and only when Windows has its estimate plus `heavySpareMB` available ("available", not "free": `MemoryInfo.cs`). If not, it fails at once with one line. Work that is heavy by time only (`JobContext.NotRun`) needs `allow_heavy` and then runs as an ordinary job.
- **Memory limits** (`sfsearch.json`): a worker is replaced after a command above `workerRecycleMB`, stopped above `workerMaxMB`, and no new command starts while the workers together hold more than `serviceMaxMB`. A worker closes its plugins after a command above 1.5 GB. Left at 0 (the default), these and `workers` come from the PC's memory and cores (`ServiceConfig.PcDefaults`). The coordinator looks at Windows' available memory four times a second: under `memoryFloorMB` while agents' commands run, the indexer gives way, then the biggest ordinary command is stopped (exit code 8), one at a time. The indexer's jobs keep 3 GB even when `workerMaxMB` is lower. `SFSEARCH_TEST_MEMORY` (with `SFSEARCH_TEST_HOOKS=1`) makes the service see a pretend PC; `SFSearch.ServiceTest smallpc` uses it.
- **Two message forms** (`Protocol.cs`). The older one: a request holds `Args`, the reply `Out`, `Err` and `Code`. The structured one: a request that sets `Structured`, `Tool`, `PageChars` or `Cursor` gets `Answer`, `More`, `Notes`, `Source`, `Cost` and `Failure`. `Wire.Render` prints the parts as text, `Wire.RenderJson` as one JSON object.
- **The MCP server** starts the service through `sfs.ps1` (which rebuilds first if needed). A call waits up to 45 s, then hands back a job number. A long answer is paged: the agent sees one `[more]` line and one cursor, whether the tool or the service cut it.
- **The older commands and tools were removed in 2.0** (`Query`, `Names`, `Refs`, `Check`, `Inspect`, `LoadOrder` and the rest; the 25 older MCP tools; their tables in `scratch\legacy`, `LegacyStore`, `LiveDb`, the `Sfs` helpers; the warm-up). Each old name answers with one line, `Error: <Name> was one of SFSearch's older commands, removed in 2.0. Fix: use <verb> ...`, exit code 2 (`Program.Retired`, which lists each old name and the verb it became). `Cache\` and `Service\data\` of 1.x are never read, written or deleted; the first command copies the Data folder's path out of `Cache\` and says once how much the two folders hold (`Home.OldDataNote`). Small state (the Data folder's path, the service's marker, which one-time notes were said) is in `state\`.

## Adding things
**A tool.**
1. Write `SFSearch\Engine\<Name>Tool.cs` with two entry points: `Tool(IReadOnlyDictionary<string, JsonElement> args)` for a call by name, and `Verb(List<string> args)` for the command line. The verb takes the tool's argument names as options (`VerbArgs.cs`), so the logic is written once.
2. Build the answer with `EngineAnswer`: rows, `Fail(what, fix, code)` for an error, notes, sources, what was read. Call `ProblemNotes.Add` for the plugins the answer was read from. Use `EngineLibs` for the index, the record engine, the SQL engine and the scratch and kept folders.
3. Register the tool in `Service\ToolCalls.cs` and the verb in `EngineCommands.Verbs`.
4. Add the MCP tool in `SFSearch.Mcp\NewTools.cs`. What every tool shares (`cursor`, `limit`, `rows`, `max_chars`, `language`, `format`, `allow_heavy`) is described once, in the server's instructions.
5. Put it under test: a golden set (`tests\SFSearch.Harness\replay\<set>.jsonl`, then `sfsharness replay baseline --set <set>`; `replay compare --via mcp` runs it through the MCP server), and a case in `polish\` for each fault you fix. (The author also adds the tool to the agent benchmark, below.)
6. Add its row to `agent-docs\skills\sfsearch\SKILL.md` and its section to `references\research.md`, with real answers.

**A report.** Write it in `SFSearch.Reports\` against `ReportSession` (it returns a `ReportAnswer`; errors are a `ReportException` with the contract's line). Add its verb in `SFSearch\Engine\ReportTools.cs` (a help text, a `VerbSpec`, a call of `Report(…)`) and in `EngineCommands.Verbs`. It is then reachable through `sfs_run`. Add its test to `SFSearch.Reports.Tests` and a golden line to `replay\m8-run.jsonl`.

**A stored query.** Add `queries\<name>.sql` with its header (`queries\README.md`), and a row in `queries\README.md` and `agent-docs\skills\sfsearch\references\sql.md`. `sfsquery --saved check` checks the header and every statement with no plugin read. The test `stored-shipped` runs every shipped query and every header example; a shipped query with no run in the test fails it.

**A schema note** (a unit or a remark on a field). Add a row to `SFSearch.SchemaGen\annotations.tsv`: the Mutagen object, the field, the unit, a remark. The build fails when a row names a field this Mutagen version doesn't have. Readable names come from the xEdit pairs in `SFSearch.SchemaGen\inputs\`, not from this file.

## Add-ons and SFSearch's version
- An add-on is a class library that references `SFSearch.csproj` and implements `ISfsExtension`; `extensions\SFSearch.Example` is the template. SFSearch loads `extensions\*.dll` in its home (from memory, so a `.dll` can be replaced while the service runs) and records which SFSearch each was built against. An add-on `X.dll` can bring stored queries in `X.queries\` beside it.
- **SFSearch's version is the add-on API's version** (`<Version>` in `SFSearch\SFSearch.csproj`). When a helper add-ons use, or `ISfsExtension`, changes in a way that breaks a built add-on, raise the first number: add-ons built for the old one are then refused with "rebuild it" instead of failing oddly. Additions only need the second number. **2.0** is that break: the older commands' helpers that 1.x add-ons could use (`Sfs`, `Program.EnsureCacheForFile` and the like, `RecordStore`, `InspectCommands`, `LegacyStore`) went with them, so an add-on built for 1.x is refused with "rebuild it". An add-on built against a 2.0 preview build (assembly version 2.0.0) loads as it is. What add-ons use now is the add-on API below, `ISfsExtension`, and `Home`/`CommandLine` behind it.
- An add-on's name is its file name without `SFSearch.`. Its commands answer to their short names unless two add-ons share one, and always to `<Name>.<Command>`. SFSearch's own commands come first.
- **The add-on API is `SFSearch\AddOnApi.cs` (namespace `SFSearch.AddOns`).** It hands an add-on the engine and nothing of the old commands:
    - `GameData`: the Data folder, the load order, a plugin's header (`PluginInfo`: masters, size flag, highest own local ID, counts), any record's EditorID, signature and holders, `FindEditorId`, SQL in a scope (`Query`), comparison and report sessions, `OverrideChanges` (plugin_vs_masters: what each override changes, ITMs), `Conditions` (a condition list as SFSearch.Reports reads it), `OpenLoadOrder` (every loaded plugin as Mutagen mods with a link cache), `Archives` (below).
    - `GameData.Archives(plugin, beside)`: a plugin's own archives and every file in them, read as `archive2 <plugin> list` reads them (`SFSearch.Reports`' archive reader, Mutagen's archive reader under it; nothing stored). The archives are the ones in Data named `<plugin name> - <part>.ba2`, the names the game loads with the plugin; a plugin named by a path outside Data gets the ones in Data named for it, or the ones beside the file with `beside: true`. A `.ba2` (a name in Data or a path) or `all` works too. It gives an `ArchiveContents` (`SFSearch.Reports`): `Archives`, each an `ArchiveListing` (`Name`, `Path`, `Length`, `Error`, `Files`), each file an `ArchiveFileRow` (`Path` inside the archive, `Size` in bytes, null for a texture: the archive's table doesn't hold it; `Extension`; `LooseCopy`: Data also holds the path as a loose file, which the game uses instead), and `Notes` (why there are none, an archive that couldn't be read). No loose file is tied to a plugin: the game loads loose files whatever plugin uses them. A plugin with no archives is no error: the list is empty and a note says why.
    - `PluginView` (`GameData.Open`): a plugin with its masters as the plugin sees records; its own records by type (FormKey or file order), `Get`, `Previous`, `In`, `Edid`, `Show`, Mutagen's mod objects and a link cache over the chain.
    - `AddOnRecord`: the header index's line, Mutagen's object (`Record`), the record layer's JSON (`Json`) and the same with every always-there field at its default (`JsonWithDefaults`).
    - `JsonText`, `FormKeys`, `Args`, `Out` (lines, tables, `--out` reports and the error line under the output contract).
  The author's Patcher add-on (kept outside this repository) is built on it.

## Testing changes
**How much to test (the user's rule, 2026-11-12):**
- **A bug fix is confirmed by one targeted test, at most 5 minutes, that exercises that bug.** Add it to the library's or the harness's tests so the bug can't come back. Then run the tests of what you changed.
- **No soaks, no stress runs over 5 minutes, no second or parallel run to confirm the same thing.**
- **The long soak and the full golden replay (`sfsharness all`, about 16 minutes, plus a soak of an hour) are for a major release, when the user asks.**

- **Use a test home,** never your own SFSearch folder: set `SFSEARCH_HOME` to an empty folder outside the repo, and `SFSEARCH_DATA` to the Data folder (it is only read) or to a folder of plugin copies. A different home is a different service. (`SFSEARCH_WARMUP` did nothing but the older commands' warm-up, and is gone with it.)
- **Build SFSearch before a test run, never during one:** `sfs.cmd` rebuilds SFSearch when a source file is newer than the program. After rebuilding, `sfs.cmd Service restart` puts a running service on the new build.
- **The harness:** `tests\SFSearch.Harness` (`dotnet build -c Release`, then `sfsharness all --work <folder outside the repo>`). It starts SFSearch on test homes under the work folder, only reads the game's Data folder, and prints one table that ends with "Everything passes." It takes about 16 minutes and reads Starfield.esm many times: run one such job at a time on a PC with plenty of free memory.

  | Part | Sub-command |
  |---|---|
  | The tools' goldens (`replay\*.jsonl`) against a baseline | `replay baseline`, `replay compare` |
  | The 455 recorded old commands and 18 SQL files (`mapping\`): each held to the call that answers it now, and each old name to its pointer line. No plugin read, seconds | `mapping` |
  | The 16 traps | `traps` |
  | The polish cases (`polish\*.json`): places where the tools once fell short | `polish` |
  | The MCP server: its list, its size, `sfs_status` under load, paging, heavy calls, jobs, `--legacy-tools` read past | `mcp describe`, `mcp test`, `mcp call <tool> <json>` |
  | Round trips through Mutagen | `roundtrip <plugin>` |
  | The soak: many clients, workers killed, a plugin changing | `soak` |
  | One call through the service, timed | `call <tool> <json> [--times N]` |

  `sfsharness help` lists every part with its options.

  **The agent benchmark is a dev tool kept by the author,** not part of the public repository: real questions with known answers, asked of a fresh agent that has only the MCP tools, and marked by pattern. It is kept out so that the agents it measures can't have seen its answers. Its harness code, its question set, the MCP test's calls of every tool with their known answers and the tests that hold a benchmark question's answer all stay with the author. The public harness and tests build and run without them: `sfsharness benchmark` says it isn't in the checkout and exits 2, `all` and `mcp test` leave its parts out, and `sfsquery-tests` runs its own SQL questions.
- **Each library's test program:** `sfsindex-tests`, `sfsrecord-tests`, `sfsquery-tests`, `sfscompare-tests`, `sfsreport-tests` (build `SFSearch.<Library>.Tests -c Release`, then run it with the Data folder as its first argument; each library's README has the line). `dotnet test SFSearch.SchemaGen.Tests -c Release` tests the schema. Their work folders are under `%TEMP%` unless `--work` names another.
- **The service's tests:** `tests\SFSearch.ServiceTest` (`SFSearch.ServiceTest.exe <test> --home <test home>`). Start the service on the test home with `SFSEARCH_TEST_HOOKS=1` first: that turns on the `__test` commands the tests drive it with. `SFSearch.ServiceTest.exe` with no arguments lists the tests.
- **By hand:** `SFSearch __index status` and `SFSearch __links status` say how far the two indexes are; `__links check <plugin>` holds the link walk against the record's JSON and against Mutagen's own list of links.
- **The docs are tested by hand:** every example in `README.md` and `agent-docs\` was run and pasted. When a tool's answer changes, run its examples again.

## Releases
A release is published by the author as one commit with the release's tree; this repository's history isn't pushed as it is. Mutagen's packages in `packages/` are built from the public branches named in THIRD_PARTY_NOTICES.md.

## The Creation Kit's names
Every build of SFSearch carries the Creation Kit's display names, tooltips, ranges and Hidden flags (the author's decision): `schema.db` is made from `SFSearch.SchemaGen\inputs\ck-names-1.16.244.0.tsv`, a data file in the repo that holds only what SFSearch uses of the CK's reflection data, keyed by the engine's names. No dump, flag or release build is needed. The schema's `Meta` records the file (`CkNamesFile`, `CkNamesSha256`) and, from its header, the Creation Kit and the dump it was made from (`CkReflection` = CreationKit.exe 1.16.244.0, `CkReflectionSha256`).

**When the Creation Kit updates:** dump its reflection data again, run `schemagen ck-import <the dump .json> SFSearch.SchemaGen\inputs`, delete the older `ck-names-*.tsv`, point `SchemaCkNames` in `SFSearch.SchemaGen.csproj` at the new file, build, and run `dotnet test SFSearch.SchemaGen.Tests -c Release`. The SchemaGen README has the steps. The dump itself never goes into the repo; `ck-import` is the only thing that reads it.

## The Mutagen build
SFSearch needs fixes that aren't in a published Mutagen yet. `packages\` holds `0.55.0-fixes.17` (Core, Kernel, Starfield) and Mutagen.Bethesda.Serialization `1.38.6-fixes.3` (with its Newtonsoft and SourceGenerator packages), used through `nuget.config`. Their source is public: branch `fixes17` of github.com/Krawphish37/Mutagen and branch `fixes17` of github.com/Krawphish37/Mutagen.Bethesda.Serialization; each package's nuspec names the commit it was built from. 1.0.x used `0.55.0-fixes.5` from the branch `starfield-fixes5`.

**To move to a new Mutagen build:**
1. Pack it and put the `.nupkg` files in `packages\`; remove the old ones.
2. Change the version in every project that names it: `SFSearch\SFSearch.csproj`, `SFSearch.Records\`, `SFSearch.SchemaGen\` (`MutagenVersion`), `SFSearch.Index.Tests\`, `tests\SFSearch.Harness\`, `tests\SFSearch.ServiceTest\`.
3. **The schema generator reads the new record definitions from the package itself** (Mutagen.Bethesda.Starfield ships its Loqui XML under `loqui\` since fixes.14, C182): nothing to copy. The build fails when the restored package's nuspec doesn't name the compiled assembly's version and commit, when one of its XML files differs from the package's `.nupkg`, or when `annotations.tsv` names a field that no longer exists.
4. Build everything, with no warnings. Run `dotnet test SFSearch.SchemaGen.Tests -c Release`: `describe WEAP` is held against a golden file, so a changed field name shows (rerun with `SFS_UPDATE_GOLDEN=1` when the change is meant, and read the difference).
5. Run `sfsharness all`. A field that was renamed in Mutagen changes a column name, so check the shipped stored queries (`stored-shipped`) and the examples in `agent-docs\`.

Nothing else has to be rebuilt: there is no stored data. Once Mutagen publishes a version with these fixes, `packages\` can go.
