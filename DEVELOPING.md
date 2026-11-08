# Working on SFSearch's code

For using SFSearch, see `README.md`. This file is for changing it.

## Rules
- **It only reads.** No command may write to the Data folder. Test patches and scratch files go elsewhere.
- **Store what the plugin holds, faithfully.** A field that can't be stored is reported as a problem, not silently dropped. `VerifyDb` and `LossCheck` are the checks for this.
- **Build with no warnings.** `sfs.cmd` rebuilds SFSearch by itself when any `.cs` file or the `.csproj` is newer than the build. Run `dotnet build SFSearch.Mcp -c Release` for the MCP server.
- **Keep commands general.** Checks for one mod belong in an add-on (`Extensions.cs`, README "Your own commands"), not in SFSearch.
- **Agents read the answers.** Output is Markdown tables by default. Warnings go to stderr (shown to agents as `[notes]`). An error is one line starting `Error:`.

## The pieces
| Folder or file | What it is |
|---|---|
| `SFSearch\Program.cs` | Command routing, `--help`, building and refreshing a plugin's database, the Data folder (`SFSEARCH_DATA`, `Cache\DataPath.txt`, or the Steam/Xbox install) |
| `SFSearch\Sfs.cs` | Shared helpers: the home folder, opening databases, FormKey and EditorID handling, tables and output |
| `SFSearch\LiveDb.cs` | Reads a plugin with Mutagen and writes its database, one record type at a time when only some are needed |
| `SFSearch\MetadataExtractor.cs`, `ItemFilter.cs` | The columns stored beside each record's JSON; how weapons and armor are sorted (configurable in `sfsearch.json`) |
| `SFSearch\*Command.cs`, `InspectCommands.cs`, `ResearchCommands.cs`, `SavedQueries.cs` | The commands |
| `SFSearch\LoadOrderCommand.cs`, `HistoryCommand.cs`, `ScriptsCommand.cs`, `RefScanCache.cs` | Shared indexes in `Cache\`: the load order (`_loadorder.db`), plugin history (`History.db`), decompiled scripts (`Scripts.db`), remembered `Refs` scans (`_refscan.db`) |
| `SFSearch\Service\` | The service: coordinator (`ServiceHost.cs`), workers (`ServiceWorker.cs`), its databases (`ServiceDb.cs`), warm-up (`Warmup.cs`), settings (`ServiceConfig.cs`), messages (`Protocol.cs`) |
| `SFSearch\Extensions.cs` | Loads extra commands from `extensions\*.dll` |
| `SFSearch.Mcp\` | The MCP server: typed tools that build `sfs.cmd` arguments and pass them to the service (`Bridge.cs`) |
| `packages\` | The Mutagen build SFSearch needs (see below) |
| `agent-docs\` | The guide agents read. Keep it in step with commands and traps |

## How data flows
- **A plugin's database** (SQLite) has a `Records` table (FormKey, Signature, EditorID, Name, Origin, IsOverride, Data as JSON), plus `Forms` (a row for every record, stored or not), `Refs` (links between records), `Masters`, `Meta` and `SignatureCounts`. The full layout is in `agent-docs\skills\sfsearch\references\database.md`.
- **Three ways to get it, same commands:**
    - The service, when running: `Service\data\<plugin>@<stamp>.db`. The stamp comes from the plugin's size and date, so a changed plugin gets a new file and readers of the old one aren't disturbed.
    - Stored databases: `Cache\<plugin>.db`, rebuilt when the plugin changes.
    - `--live`: built for one command in a temp folder, then deleted.
- **The service:** a coordinator that never reads a plugin, and worker processes that each run one command at a time through `Program.Main`. One queue per agent, taken in turn. A crashed worker is replaced and its command runs once more. Databases use WAL, so readers never wait. Building a type takes a named mutex per database (`ServiceDb.BuildLock`). The coordinator and workers talk over named pipes open to the current user only.
- **The MCP server** starts the service through `sfs.ps1` (which rebuilds first if needed). A call waits up to 45 s, then hands back a job number. A command cut off by a service restart is sent again.

## Add-ons and SFSearch's version
- An add-on is a class library that references `SFSearch.csproj` and implements `ISfsExtension`; `extensions\SFSearch.Example` is the template. SFSearch loads `SFSearch\extensions\*.dll` (from memory, so a `.dll` can be replaced while the service runs) and records which SFSearch each was built against.
- **SFSearch's version is the add-on API's version** (`<Version>` in `SFSearch\SFSearch.csproj`). Add-ons use `Sfs`, `Program`'s public helpers, `ProfileCommand.GenerateDossier`, `InspectCommands` and the like. When one of those, or `ISfsExtension`, changes in a way that breaks a built add-on, raise the first number: add-ons built for the old one are then refused with "rebuild it" instead of failing oddly. Additions only need the second number.
- An add-on's name is its file name without `SFSearch.`. Its commands answer to their short names unless two add-ons share one, and always to `<Name>.<Command>`. SFSearch's own commands come first.

## Testing changes
- **Use a test home** so your real databases stay untouched: set `SFSEARCH_HOME` to an empty folder, and `SFSEARCH_DATA` to a folder holding copies of the plugins you test with.
- `VerifyDb <plugin>` checks a database against the plugin file. `LossCheck <plugin>` reads and saves a plugin through Mutagen (in a temp folder) and lists anything lost.
- After changing what a database stores, raise `CacheSchemaVersion` in `Program.cs` so old databases rebuild.
- After rebuilding, `sfs.cmd Service restart` puts the service on the new build.

## The Mutagen build
SFSearch needs fixes that aren't in a published Mutagen yet. `packages\` holds `0.55.0-fixes.5` (Core, Kernel, Starfield), used through `nuget.config`. The source is [Krawphish37/Mutagen, branch starfield-fixes5](https://github.com/Krawphish37/Mutagen/tree/starfield-fixes5). To move to another build: pack it, put the three `.nupkg` files in `packages\`, change the version in `SFSearch\SFSearch.csproj`, and check a few plugins with `VerifyDb`. Once Mutagen publishes a version with these fixes, `packages\` can go.
