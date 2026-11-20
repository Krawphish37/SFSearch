![SFSearch: Starfield plugin data for AI coding agents](.github/social-preview.png)

SFSearch reads Starfield plugins (`.esm`, `.esp`) and answers questions about them. It's built for AI coding agents working on Starfield mods: Claude Code, Codex, Cursor and the like. You can run it by hand too.

Think of it as SF1Edit's data, ready for an agent to search. Instead of writing its own plugin parser and getting things subtly wrong, an agent asks SFSearch:

- What is this FormID, in every plugin that has it? What is it called in game?
- Which version of this record wins in my load order, and what does each plugin change?
- What points at this record?
- What does this mod change in its masters? Are any of its overrides ITMs?
- How does my patch differ from another patch for the same mod?
- What changed since a mod updated, and which patches did that break?
- What do this mod's Papyrus scripts do?
- Is this plugin built right? (like SF1Edit's Check for Errors)
- What's inside this mod's archives, and are the textures behind its materials all there?
- Which mod's file names this material, texture or sound (inside its meshes, material databases, interface files or scripts)?
- What can this leveled list give, and what do this quest's characters say?
- What is in this plugin's header (masters, flags, record count)? Which plugins need darkstar.esm?
- Which condition functions do the plugins use? In what order does the game write a record type's subrecords, and what does a type hold?
- Anything else, in SQL, across any plugin or the whole load order.

**It only reads.** SFSearch never changes anything in your Data folder. **Starfield only.**

**It reads the plugins live, every time.** SFSearch keeps no database of your records, so an answer can't be out of date, and there is nothing to rebuild after a game or mod update.

## Try it in 5 minutes
You need Windows, Starfield, Claude Code (or another agent that speaks MCP), and the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```powershell
git clone https://github.com/Krawphish37/SFSearch.git C:\Modding\SFSearch
C:\Modding\SFSearch\SFSearch\sfs.cmd Version
dotnet build C:\Modding\SFSearch\SFSearch.Mcp -c Release
claude mcp add sfsearch --scope user -- "C:\Modding\SFSearch\SFSearch.Mcp\bin\Release\net10.0\SFSearch.Mcp.exe" --home "C:\Modding\SFSearch\SFSearch"
```

Start a new Claude Code session, approve the `sfsearch` server, and ask:

> Use SFSearch: which of my mods change the Eon pistol, and which version wins in my load order?

The first call starts SFSearch's service, which is ready for questions in about 2 seconds. The sections below explain each step, other agents, and the settings.

## How agents use it
- **The MCP server (`SFSearch.Mcp`) is the main way in.** It gives an agent 12 tools with plain arguments: no shell quoting, and SQL can span lines.

  | Tool | Answers |
  |---|---|
  | `sfs_find` | What a FormID, EditorID or in-game name is; records by pattern or type; who holds each and who wins |
  | `sfs_record` | One record: its fields, its JSON, the file's bytes, every plugin's version, proof of a value |
  | `sfs_describe` | What a record type's fields are called, with xEdit's name beside each |
  | `sfs_query` | SQL over one plugin, several, or what the game uses from the whole load order |
  | `sfs_refs` | What points at a record, and what it points at |
  | `sfs_compare` | A record across the load order; two records; a plugin against its masters; two versions of a mod; a patch against a reference patch |
  | `sfs_loadorder` | The load order, one plugin, the problems found, what changed since last time |
  | `sfs_check` | Is a plugin built right: 19 checks, ITMs, missing assets and textures, a read-and-write round trip |
  | `sfs_saved` | 27 ready-made queries (recipes, keywords, attach points, leveled lists, …), and your own |
  | `sfs_run` | The reports (leveled lists, dialogue, where items come from, archives, scripts) and add-ons |
  | `sfs_job`, `sfs_status` | Collect a long answer; the service's health |

- **Every answer has the same parts:** the answer, `[more]` with a cursor when it was cut, `[notes]` with warnings, `[source]` (which copy of which plugins it was read from) and `[cost]`. An error is one line that says what to do. This is a real answer:
  ```text
  > sfs_record {"record": "kgcDoom_WEAP_SuperShotgun", "view": "fields", "fields": "AttackDamage,HasScope,Keywords[7],Name"}
  kgcDoom_WEAP_SuperShotgun (007751:kgcdoom.esm)
  WEAP Weapon in kgcdoom.esm (its own record), at 0xD313, 2,403 bytes, its FormID stored as FD007751 (the Medium form)
  Read from kgcdoom.esm: the plugin that wins it (the last loaded of the 2 that hold it). Held by (winner first): kgcdoom.esm > darkstarpatch_athellsgate.esm (not loaded).
    AttackDamage = 80.0
    HasScope = false   [the default: the record's JSON leaves it out, and the schema says the field is always there]
    Keywords[7] = kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm)
    Name = Super Shotgun
  [source] kgcdoom.esm 3.5 MB, key 9561e0c8, strings 2d590079; load order (224 loaded, plugins.txt) 9 KB, key b730711a
  [cost] 33 ms; read 1 WEAP of kgcdoom.esm
  ```
- **Heavy work runs, and says what it cost.** Anything estimated over 10 seconds or 2 GB of memory is heavy: it runs alone, starts only when Windows has the memory and the disk has room for its working files, and says what is missing when they aren't there. It no longer answers with its cost first and waits to be asked again: of 21 big asks measured on a 370-plugin setup, the 9 that were stopped took 4 to 46 s when allowed. `"askBeforeHeavy": true` in `sfsearch.json` brings the asking back (the answer `Not run. …`, repeated with `allow_heavy`). Where an index would make a kind of question fast (today the world link index: what points at a record from placed objects, cells and navmeshes), the answer also offers `allow_heavy: "save_index"` (for refs, `world: "save_index"`; command line `--allow-heavy=save-index`, `--world=save-index`): run it now, then make the index in the background and keep it, with its size, the time to make it and how fast the same question then gets. Where no index would help, the answer says so. A job that needs more memory than Windows has available is refused at once; it never makes the PC page.
- **The tools talk to the SFSearch service,** which starts by itself on the first call. It serves many agents at once: several worker programs run commands side by side, a crashed worker is replaced and its command run again, and nothing hangs.
- **The command line works too:** `SFSearch\sfs.cmd <verb> ...`, with the same verbs and arguments as the tools (`find`, `record`, `describe`, `query2`, `refs2`, `compare`, `loadorder2`, `check2`, `saved`), and the reports and other verbs `sfs_run` reaches: `leveled2`, `dialogue2`, `sources2`, `archive2` (also `grep` inside archived files and `encoding` of text files), `assets2`, `materials2`, `scripts2`, `changes2`, `profile` (a field, the conditions or the subrecords across many plugins) and `indexes` (what SFSearch keeps on disk). `sfs.cmd help` lists them; `sfs.cmd <verb> --help` shows a verb's options.

## What you need
- **Windows 10 or 11.**
- **Starfield.** Steam and Xbox (Game Pass) installs are found by themselves; only Steam has been tested. Any other location works with `sfs.cmd SetData "<path to Data>"`.
- **The .NET 10 SDK** (SFSearch builds itself the first time you run it):
  ```powershell
  winget install Microsoft.DotNet.SDK.10
  ```
- **Disk space: about 1 GB that stays, and 1 to 2 GB that comes and goes.** The indexes SFSearch keeps between starts take about 0.8 GB for 370 plugins (keeping them is your choice: see [What is on disk](#what-is-on-disk)). Working files take 1 to 2 GB while the service runs and are deleted at every service start.
- **For the `scripts2` report only:** [Champollion](https://github.com/Orvid/Champollion/releases) 1.3.2 or newer. Put `Champollion.exe` in `SFSearch\tools\champollion\`, or set its path in `sfsearch.json`.

## Install
1. Get the repository into a folder of its own (avoid `Program Files`):
   ```powershell
   git clone https://github.com/Krawphish37/SFSearch.git C:\Modding\SFSearch
   ```
2. Build and try it. The first run builds SFSearch:
   ```powershell
   C:\Modding\SFSearch\SFSearch\sfs.cmd Version
   C:\Modding\SFSearch\SFSearch\sfs.cmd find "000CF2" --in Starfield.esm
   ```
   The second line prints the record with that FormID:
   ```text
   | Record | Type | Name | Inside | Held by (winner first) |
   | :--- | :--- | :--- | :--- | :--- |
   | [no EditorID] (000CF2:Starfield.esm) | REFR PlacedObject |  | CELL LC008SlaytonAerospace (06A716:Starfield.esm) | Starfield.esm |
   ```
   If Starfield isn't found by itself, tell SFSearch once: `sfs.cmd SetData "D:\Games\Starfield\Data"`.
3. Build the MCP server:
   ```powershell
   dotnet build C:\Modding\SFSearch\SFSearch.Mcp -c Release
   ```

## Connect your agent
**Claude Code**, for every project:
```powershell
claude mcp add sfsearch --scope user -- "C:\Modding\SFSearch\SFSearch.Mcp\bin\Release\net10.0\SFSearch.Mcp.exe" --home "C:\Modding\SFSearch\SFSearch"
```
Or for one project, put this in a `.mcp.json` file in that project's folder:
```json
{
  "mcpServers": {
    "sfsearch": {
      "type": "stdio",
      "command": "C:\\Modding\\SFSearch\\SFSearch.Mcp\\bin\\Release\\net10.0\\SFSearch.Mcp.exe",
      "args": ["--home", "C:\\Modding\\SFSearch\\SFSearch"]
    }
  }
}
```
Start a new session and approve the `sfsearch` server when asked. Ask something like "what is 000CF2 in Starfield.esm?" to check it works.

**Other agents** that speak MCP: the same program and arguments. Agents without MCP can run `sfs.cmd` directly.

**Give your agent the guide:** `AGENTS.md`, and `agent-docs/skills/sfsearch/SKILL.md` (which tool answers which question, and how to read an answer). For Claude Code, copy the `agent-docs/skills/sfsearch` folder into your project's `.claude/skills/` folder.

**Coming from SFSearch 1.x:** the 25 older tools and the older commands (`Query`, `Names`, `Refs`, `Check`, `Inspect`, `LoadOrder` and the rest) were removed in 2.0. An old command's name now answers with one line that names the verb to use (`Error: Names was one of SFSearch's older commands, removed in 2.0. Fix: use find <FormID or EditorID> (sfs_find) ...`), and an MCP configuration that still passes `--legacy-tools` starts the server with the 12 tools. `agent-docs/skills/sfsearch/references/old-tools.md` maps every old command and tool to its new call. Add-ons built for 1.x need a rebuild (`sfs.cmd` does it by itself when it finds their source).

## One way to run: the service
The service is started by the MCP tools, or by hand:
```powershell
sfs.cmd Service start      # also: stop | restart | status | config
```
While it runs, every `sfs.cmd` command goes to it. With no service, a command still works: it reads what it needs by itself and is slower, because nothing is held open between commands.

**Your mods' files stay yours.** The service holds a mod's or a patch's file only while a command reads it, so a mod manager, SF1Edit or a patcher can replace, rename or delete the file between commands, even a program that asks Windows for the file all to itself. The next command reads the file as it is then. Only the game's own plugins (Starfield.esm, the expansions, Bethesda's Creations) stay open while the service runs: `sfs.cmd Service stop` lets go of those too, before a game update or a file check by Steam.

**What the first question costs.** No record data is prepared ahead and kept: the service reads the plugin files. It keeps only indexes between starts (`indexes\`, below): its two start-up indexes, ready in a few seconds after the first start with only the parts of new or changed plugins made, and the world link index when you ask for it. Measured on a Data folder of 370 plugins (3.96 GB, 12.2 million records):

| Step | Time |
|---|---|
| The service's index of every record (FormKey, type, EditorID, who holds it): ready for questions | about 2 s the first time; kept after that |
| Its index of links and in-game names, built in the background after that | about 15 s the first time (9.5 s on one run); kept after that |
| A later start, both indexes kept: until the link index is ready (each plugin then checked by a full hash: 0.4 s) | about 2.5 s (20 s when nothing is kept) |
| The first question about a record type reads that type: Starfield.esm's 406 weapons, its 7,131 NPCs | 0.03 s, 0.2 s |
| The same type asked again | found ready |
| The slowest ordinary type of Starfield.esm (126,347 dialogue lines) | 1.3 to 1.7 s |
| Every placed object of Starfield.esm (3,291,860), one field read from the records' bytes (Base) | about 3.4 s |
| The placed objects of one base among them (`PlacedObject WHERE Base = '…'`): doesn't ask | about 0.8 s |
| A typical answer once things are read | 0.03 to 0.15 s |
| What points at a Starfield.esm record from world data (placed objects, cells, navmeshes): XMarker, 5,489 records | 2 to 4 s, its world data searched by its bytes (left out unless asked); 0.17 s with the world link index |
| Making the world link index, once, when you ask (`sfs.cmd indexes build world`) | about 14 s for every plugin with world data (275 MB); Starfield.esm alone about 7 s (77 MB) |

Until the link index is ready, tools still answer; they say what isn't there yet (in-game names for some plugins), and "what points at this" searches the files' bytes instead.

## What is on disk
Everything is in the `SFSearch` folder (the "home").

**The one choice you have here is the indexes.** They are lists of what is in your plugins: which records exist, which plugin holds each, what points at what, and in-game names. They never hold a record's values (those are always read from the plugin file), and they are what lets a question start in a fraction of a second.
- **Kept on disk (the default):** about 0.8 GB for 370 plugins, and a service start is ready in about 2.5 s. Only a new or changed plugin's part is made again.
- **Not kept** (`"keepStartupIndexes": false` in `sfsearch.json`): nothing stays on disk between starts, and every service start makes them again, about 20 s.
- `sfs.cmd indexes` shows what is kept, how big it is and how long each would take to make again. `sfs.cmd indexes drop all` deletes them; SFSearch makes them again when it needs them.

| Folder or file | What it is |
|---|---|
| `indexes\` | The service's two indexes: `header\` (every record's FormID, EditorID, type, who holds it) and `links\` (what points at what, in-game names); and `world\`, the world link index (what points at a record from placed objects, cells, navmeshes and terrain), only when you ask for it with `sfs.cmd indexes build world [--in <plugins>]`: then kept whatever `keepStartupIndexes` says, made again for a plugin you named when it changes, deleted with `sfs.cmd indexes drop world` (about 275 MB for 119 plugins with world data, 77 MB for Starfield.esm alone). Made from your plugin files, never record data; each plugin's part is named by its content, so a changed plugin's part is never read. **Kept between service starts** and brought up to date plugin by plugin; every plugin is checked by a full hash at start. A plugin no longer in Data loses its part at the next start, so one that comes back is indexed again, unless `keepRemovedPluginIndexMB` keeps those parts up to a size. For 370 plugins about 0.8 GB (header 481 MB, links 310 MB). `sfs.cmd indexes` shows each one's size and age; `sfs.cmd indexes drop all` deletes them (the service makes them again); `keepStartupIndexes: false` in `sfsearch.json` empties them at every start instead |
| `scratch\` | Working files: the tables SQL reads, round-trip copies. **Emptied at every service start.** Safe to delete when the service isn't running. The tables grow with what is asked (all ordinary record types of Starfield.esm: 1.1 GB). A `scratch\legacy` folder left by a 2.0 preview build goes with the next service start |
| `kept\` | Two small things that outlive a service start: decompiled Papyrus scripts (`kept\scripts\`), and the log of what changed in your Data folder (`kept\history\`). Safe to delete: scripts are decompiled again, and the history starts again. `Scripts.db` and `History.db`, which 2.0 preview builds kept here, aren't read any more: delete them when you like |
| `state\` | Three small files: your Data folder's path if you set one with `SetData`, the running service's marker, and which one-time notes were shown |
| `sfsearch.json` | Your settings |
| `queries\` | Stored queries you saved |
| `extensions\` | Add-ons |
| `Service\` | The service's log (`logs\`) and long answers the MCP server saved to files (`results\`, deleted after a week). A `usage.txt` left by an earlier version isn't read any more |
| `Cache\`, `Service\data\` | **Databases of SFSearch 1.x. They aren't read or written any more, and SFSearch never deletes them: delete both folders yourself** (they can be many GB). Run one command with this version first: it copies the one small thing it still needs out of `Cache\` (your Data folder's path, to `state\`) and says once how much the two folders hold. `sfs.cmd Service status` shows that line until they are gone |

## Updating
```powershell
cd C:\Modding\SFSearch
git pull
dotnet build SFSearch.Mcp -c Release
```
Then start a new agent session (the MCP server is loaded when a session starts). SFSearch itself rebuilds on its next command; if the service is running, `sfs.cmd Service restart` puts it on the new version. Your settings, stored queries and add-ons aren't touched, and there is nothing to rebuild: the service starts from the plugin files. `sfs.cmd Version` shows the version you have.

If `dotnet build` says the MCP server's `.exe` is in use, close your agent sessions first.

## Settings: `SFSearch\sfsearch.json`
SFSearch writes this file the first time the service starts (or when you run `sfs.cmd Service config`), with every setting explained above its line: what it does, the values it takes and what they do, its default, and what the default comes to on your PC. It's yours: updates never change it. Edit and save: the service reads it again by itself. Delete a line to use its default; delete the file to go back to all of them. **`sfs.cmd Service config` shows every setting in effect and where it came from** (the file, an environment variable, the default, or your PC).

The memory settings and `workerGarbageCollector` are worked out from your PC when they are left at their default, so the same file suits an 8 GB laptop and a 64 GB PC; a number you put in is kept. The table gives what they come to on an 8, 16, 32 and 64 GB PC with 10 or more cores (fewer cores allow fewer workers); `Service config` gives yours. A file written by an older version is rewritten once: the numbers that version put there by itself (`"workers": 8`, `"workerMaxMB": 12288` or `6144`, `"workerRecycleMB": 3072`) become 0, so they come from the PC; every other line stays, and the file as it was is kept as `sfsearch.json.before-v2.bak`.

| Setting | What it does | Values | Default, and what it does |
|---|---|---|---|
| `workers` | How many commands run at once, each in its own worker program | 0, or 1 and up | 0: one per 2 GB of `serviceMaxMB`, at least 2, at most 8 and at most the cores less 2: **2 / 4 / 8 / 8** workers on an 8 / 16 / 32 / 64 GB PC |
| `serviceMaxMB` | All workers together may hold this much memory; above it no new command starts | 0, or 1,024 and up (MB) | 0: half the PC's memory: **4 / 8 / 16 / 32 GB** |
| `workerMaxMB` | One command is stopped, with a clear error, when its worker passes this much memory (a heavy command and the indexer's jobs have their own allowance). A SQL query is held to it too: a statement that would pass it stops a little under it with what to narrow, and big sorts and groups write what doesn't fit to temporary files (in the scratch folder) instead of growing in memory, up to 4 times this per query (4 to 32 GB) | 0, or 512 and up (MB) | 0: a third of `serviceMaxMB`, 1.5 to 6 GB: **1.5 / 2.7 / 5.3 / 6 GB** |
| `workerRecycleMB` | A worker that a command left holding more than this is replaced by a fresh one | 0, or 256 and up (MB) | 0: half of `workerMaxMB`, 1 to 3 GB: **1 / 1.3 / 2.7 / 3 GB** |
| `memoryFloorMB` | While commands run and Windows has less than this available, the indexer gives way, then the biggest command is stopped with one line (`Error: the PC was short of memory …`, exit code 8) and can be asked again | 0, or 256 and up (MB) | 0: the larger of 1.5 GB and 5% of the PC's memory: **1.5 / 1.5 / 1.6 / 3.2 GB** |
| `workerGarbageCollector` (new in 2.1) | How a command cleans up the memory Mutagen makes while it reads, which trades speed against memory. Covers every command: those the service runs and one run without the service. The service itself and the MCP server always use workstation | `"server"`: a read on many cores up to 2.5 times as fast, about 0.4 to 0.7 GB more memory at a command's peak. `"workstation"`: less memory, slower on many cores. `"auto"`: server in the service's workers on every PC (where one command may use less than 2.5 GB, `workerMaxMB`, a server worker holds .NET's memory under that limit, so it stays under it); in a command run without the service, server when one command may use 2.5 GB or more (`workerMaxMB`), else workstation | `"auto"`: **the workers server on every PC; a command without the service workstation on an 8 GB PC, server from about 15 GB**. On a 16-core 64 GB PC, workstation against server: every cell of Starfield.esm through the service 63 s against 26 s, refs of XMarker 11 s against 7 s; with an 8 GB PC's settings (1.5 GB a command, 8 cores) refs of XMarker 15 s against 8 s, every cell 94 s (stopped over 1.5 GB in two runs of three) against 38 to 46 s. A command run without the service where the setting picks workstation starts itself again on it (about 0.2 s more). |
| `jobTimeoutMinutes` | A command still running after this long is stopped with a clear error | 1 and up (minutes) | 10 |
| `workerRecycleCommands` | A worker is also replaced after this many commands | 1 and up | 500 |
| `queueLimit` | More waiting commands than this are turned away with "busy, try again" | 1 and up | 500 |
| `heavyMinMB` | A command that estimates it needs this much or more is heavy: heavy commands run one at a time | 1 and up (MB) | 2048: 2 GB or more is heavy |
| `heavySpareMB` | A heavy command starts only when Windows has its estimate plus this much available; if not, it fails at once with one line (it never waits, and never makes the PC page) | 0 and up (MB) | 4096: its estimate plus 4 GB |
| `heavyJobTimeoutMinutes` | A heavy command's own time limit | 1 and up (minutes) | 30 |
| `pluginCheckSeconds` | How often the idle service (no command for 30 s) checks the plugins in Data, so a plugin you add, remove or change is in its indexes before your next command; about 30 ms when nothing changed. A command checks the plugins it reads anyway, so nothing is read stale | 10 to 86400 (a day); outside counts as the nearest end. Shorter shows a change sooner and dates it closer in `changes2` | 600: every 10 minutes |
| `historyLook` | Whether the service looks by itself for what changed in Data, for `changes2`'s history (kept in `kept\history`: each plugin copy's FormKeys and record types, no record contents) | true: at its start and at each check above, so `changes2` dates each change near when it happened. false: only `changes2` looks, and dates a change by that call | true |
| `keepStartupIndexes` | Whether the header index and the link index are kept in `indexes\` between service starts | true: kept and brought up to date plugin by plugin, so a start has them ready in about 2.5 s instead of about 20 s (370 plugins, about 0.8 GB). false: emptied at every start and made again | true. `sfs.cmd indexes` shows what is kept and its size |
| `askBeforeHeavy` | Whether heavy work (estimated over 10 s, or at `heavyMinMB` of memory or more) answers with its cost first and runs only when asked again with `allow_heavy` | false: it runs (alone, and only when Windows has the memory and the disk the room; else it says what is missing). true: `Not run. …` with the cost first, as up to 2.3 | false |
| `keepRemovedPluginIndexMB` (new in 2.1) | How much of the kept index parts of plugins no longer in Data to keep: a plugin you took out, or the copy a changed plugin replaced (only with `keepStartupIndexes` true) | 0: deleted when SFSearch notices (at the latest at the next start), so a plugin you put back is indexed again (a mod's part takes a fraction of a second to about 1 s, Starfield.esm's about 15 s). A number: kept up to that many MB; over it, the parts of the plugin out of Data longest go first, its segment and link file together, so switching between mod lists doesn't index the same plugins again | 0: deleted, as in 2.0. **If you switch between mod lists often and starts get slow, please say so in an issue.** `sfs.cmd indexes` lists what is kept for plugins not in Data |
| `pluginsTxt` | The load order (`plugins.txt`) SFSearch reads | A path (e.g. a second Windows account's: `C:\\Users\\Other\\AppData\\Local\\Starfield\\Plugins.txt`), or empty | Empty: Starfield's own, `%LOCALAPPDATA%\Starfield\Plugins.txt` of your Windows user |
| `champollion` | `Champollion.exe` (1.3.2 or newer), the Papyrus decompiler `scripts2` uses | A path, or empty | Empty: SFSearch looks in its `tools\champollion\` folder |
| `settingsVersion` | The version of the file's settings, for SFSearch itself; leave it | 2 | 2 |

**No longer used:** `warmup` and `dataCapGB` were for the older commands' tables, removed in 2.0. A file that still has them works as before; they change nothing, and `sfs.cmd Service config` says so. Delete them when you like.

**Environment variables.** These are set outside the file (in Windows' settings, or in the `env` of an MCP server's configuration):

| Variable | What it does | Default |
|---|---|---|
| `SFSEARCH_HOME` | The SFSearch folder to use (settings, indexes, queries, add-ons). The MCP server takes `--home` instead | The `SFSearch` folder the program is built in |
| `SFSEARCH_DATA` | Starfield's Data folder | The folder `sfs.cmd SetData` remembered, else found by itself (Steam, Xbox, the registry) |
| `SFSEARCH_PLUGINS_TXT` | Overrides `pluginsTxt` | Not set: `pluginsTxt` |
| `SFSEARCH_CHAMPOLLION` | Overrides `champollion` | Not set: `champollion` |
| `SFSEARCH_AGENT` | The name a command line's commands carry in the service's status (who asked) | `cli` |
| `SFSEARCH_WORKERS`, `SFSEARCH_SERVICE_MAX_MB`, `SFSEARCH_WORKER_MAX_MB`, `SFSEARCH_WORKER_RECYCLE_MB`, `SFSEARCH_MEMORY_FLOOR_MB`, `SFSEARCH_JOB_MINUTES`, `SFSEARCH_WORKER_JOBS`, `SFSEARCH_QUEUE_LIMIT`, `SFSEARCH_HEAVY_MIN_MB`, `SFSEARCH_HEAVY_SPARE_MB`, `SFSEARCH_ASK_BEFORE_HEAVY`, `SFSEARCH_HEAVY_JOB_MINUTES` | Override `workers`, `serviceMaxMB`, `workerMaxMB`, `workerRecycleMB`, `memoryFloorMB`, `jobTimeoutMinutes`, `workerRecycleCommands`, `queueLimit`, `heavyMinMB`, `heavySpareMB`, `askBeforeHeavy` (1 or true), `heavyJobTimeoutMinutes` (a number above 0) | Not set: the file |

`workerGarbageCollector`, `pluginCheckSeconds`, `historyLook`, `keepStartupIndexes` and `keepRemovedPluginIndexMB` have no environment variable: they are set in the file only.

## Your own commands (add-ons)
SFSearch knows Starfield's records, not any one mod. Knowledge about a mod goes in an **add-on**:
- a check for patches built against a framework mod
- a lookup that explains a crafting overhaul's tiers
- a report a mod's authors want every patcher to run

Add-ons can be made by anyone and shared.

**Using one:** put its `.dll` in `SFSearch\extensions\`. Its commands then work like SFSearch's own: on the command line, through the service, and over MCP with `sfs_run`. `sfs.cmd Extensions` lists what's installed, and `Service status` names them too. An add-on `X.dll` can bring stored queries with it, in a folder `X.queries\` beside it; they show in `sfs_saved` as `X.<name>`.

**Making one:**
1. Copy `extensions\SFSearch.Example` and rename the folder and project to `SFSearch.<ModFamily>`, e.g. `SFSearch.MyMod`. The part after `SFSearch.` is the add-on's name.
2. Write your commands in the class that implements `ISfsExtension`: each command's name and its help line, and a `Run` method. Use the add-on API (`SFSearch\AddOnApi.cs`, namespace `SFSearch.AddOns`): `GameData` finds plugins and records, runs SQL and comparisons, `PluginView` reads a plugin's records live through Mutagen, `Args` takes options, `Out` prints. The example's `KeywordCount` opens a plugin, finds a keyword by EditorID or FormKey, walks the plugin's records and prints a table. `DEVELOPING.md` lists the whole API.
3. `dotnet build -c Release` in its folder. The build puts the `.dll` in `SFSearch\extensions\` (or where `-p:SfsExtensionsDir=<folder>` says). From then on, `sfs.cmd` rebuilds it by itself when its code or SFSearch changes. A `.dll` can be replaced while the service runs.

**For a question that is only SQL, write a stored query,** not an add-on: a `.sql` file with a short header, no code (`queries\README.md`).

**Names:** keep command names short (`Lint`, `TierDiff`). If two add-ons have a command with the same name, SFSearch asks for the full name, `<AddOn>.<Command>` (`MyMod.Lint`), instead of guessing. SFSearch's own commands always come first, and so do the names of its removed older commands (`Query`, `Check`, ...), which answer with the verb to use.

**Versions:** an add-on is built against one SFSearch. When SFSearch's first version number changes (its add-on API changed), older add-ons aren't loaded and SFSearch says to rebuild them.

**Sharing, and safety:** an add-on is a program. It runs with your rights, like any mod tool or script extender plugin. Share add-ons as source code that people build themselves, and only install `.dll` files from people you trust. SFSearch's own commands only read your Data folder; an add-on is expected to as well.

## About the Mutagen build
SFSearch reads plugins with [Mutagen](https://github.com/Mutagen-Modding/Mutagen). It needs fixes that aren't in a published Mutagen version yet: real data that used to be lost, crashes on some plugins, text that kept changing on save. That build (`0.55.0-fixes.27`, with Mutagen.Bethesda.Serialization `1.38.6-fixes.7`) is in `packages/`.

Its full source is public, as GPL-3.0 asks: the `fixes27` branch of [Krawphish37/Mutagen](https://github.com/Krawphish37/Mutagen/branches) and the `fixes/1.38.6-fixes.7` branch of [Krawphish37/Mutagen.Bethesda.Serialization](https://github.com/Krawphish37/Mutagen.Bethesda.Serialization/branches). Each package names the commit it was built from. To get them:
```powershell
git clone -b fixes27 https://github.com/Krawphish37/Mutagen
git clone -b fixes/1.38.6-fixes.7 https://github.com/Krawphish37/Mutagen.Bethesda.Serialization
```
(GitHub often times out showing pages of the Mutagen repository, which is very large; cloning always works.) SFSearch 1.0.x used `0.55.0-fixes.5`, from the `starfield-fixes5` branch.

## Status
Version `2.4.1` (its release notes list what changed since 2.4.0), with the older commands and tools removed. Tested on one PC (Steam install, 370 plugins, 224 loaded). The numbers below were taken before the removal:
- **455 real past commands** recorded from agent sessions: 452 run (3 name files that no longer exist), and all 452 answer line for line as before the new tools were added. Since the removal each of them is held to the call that answers it now, and each old command's name to its pointer line (`sfsharness mapping`).
- **The 16 traps of the 1.x agent guide** (wrong answers with no error): each now can't happen, or gives a clear error or warning. All 16 have a tested case, and all pass.
- **The MCP server's own test** (with the author's calls of every tool): 98 calls of the 12 tools, 102 of 102 checks pass. The 12 tool definitions are 8,796 characters (the 25 older ones, removed in 2.0, were 14,789).
- **69 SQL questions** (32 in the repository's tests, 37 from the agent benchmark), each held against Mutagen's own answer: 69 of 69 equal.
- **Round trips** (read a plugin, write it through Mutagen, compare byte for byte): identical for weap_starlight.esm, kgcdoom.esm, darkstar.esm, BlueprintShips-Starfield.esm (303,656,303 bytes) and Starfield.esm (1,457,098,709 bytes, 3,829,246 records).
- **A soak test:** 240 seconds, 16 clients, old and new tools mixed, workers killed on purpose: 10,136 commands with no wrong answer and no hang. One earlier run of four had 2 wrong answers among 10,704, both from the older `Live` command sent through the older tools (removed in 2.0).
- **60 agent benchmark questions** from past sessions (a dev tool the author keeps out of the repository, so that the agents it measures can't have seen its answers): the tools' own answers hold the known answer for 59. The 60th is the ITM count of darkstar.esm, where the new check is right (21) and the old known answer wasn't (20).

- **A blind run:** a fresh agent with only the MCP tools and their descriptions (no guide, no answers) got 91 of 92 questions right (the 60 benchmark questions and 32 SQL questions).

Not tested yet: Game Pass installs; other PCs. Found a problem? Open an issue with the output of `sfs.cmd Version` and the command you ran.

## License
GPL-3.0, because Mutagen and the libraries around it are. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.
