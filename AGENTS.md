# SFSearch: instructions for coding agents

SFSearch reads Starfield plugins (`.esm`, `.esp`) live and answers lookups, load-order questions, comparisons, asset checks and patch checks. Use it for all Starfield plugin data instead of writing a parser. It never changes a plugin, and it holds a mod's or a patch's file only while a command reads it: another program can write that file between your calls.

**Use the `sfsearch` MCP tools when they're loaded:** `sfs_find`, `sfs_record`, `sfs_describe`, `sfs_query`, `sfs_refs`, `sfs_compare`, `sfs_loadorder`, `sfs_check`, `sfs_saved`, `sfs_run`, `sfs_job`, `sfs_status`. Otherwise run the command line from any folder:
```powershell
& "<SFSearch folder>\SFSearch\sfs.cmd" <verb> [arguments]
```
The verbs are `find`, `record`, `describe`, `query2`, `refs2`, `compare`, `loadorder2`, `check2`, `saved`, and the reports `leveled2`, `dialogue2`, `sources2`, `archive2`, `assets2`, `materials2`, `scripts2`, `changes2`. `<verb> --help` shows a verb's options.

**Before your first lookup, read `agent-docs/skills/sfsearch/SKILL.md`.** It says which tool answers which question and how to read an answer. Its `references/` folder has SQL (`sql.md`: read before writing a query), each tool in depth (`research.md`), and the new call for every old tool and command (`old-tools.md`).

The rules:
1. **Write every record as `EditorID (FormID:plugin)`.** The same six digits are different records in different plugins; `sfs_find` lists them all.
2. **Don't guess field names.** `sfs_describe` gives them, with xEdit's name beside each.
3. **Read the `[notes]` lines** under an answer (a missing master, a patch built for an older master, a part that wasn't searched) and pass on what matters. `[source]` says which copy of which plugins was read.
4. **`[more]` means the answer was cut:** repeat the call with its cursor.
5. **Heavy work runs without asking.** Reading millions of records or a whole plugin takes seconds to a minute; what it cost is in `[cost]`. It starts only when the PC has the memory and the disk for it, and says what is missing when it hasn't. Only where the user set `askBeforeHeavy` does an answer `Not run. …` state the cost first: then repeat with `allow_heavy`.
6. **A call that returns a job number is still working:** collect it with `sfs_job`.
7. **Links are FormKeys.** In SQL, match one by name with `edid(column) = '…'`.
8. On the command line, quote FormIDs, and end inline SQL with `;` (or use `--file`).

**Nothing about a record is stored.** SFSearch keeps indexes in `SFSearch\indexes\` (which records exist, which plugin holds each, what points at what, in-game names: never a record's values; about 0.8 GB, made again in about 20 s), working files in `SFSearch\scratch\` (emptied at every service start), two small things in `SFSearch\kept\` (decompiled scripts, a history log), and the service's log and long answers saved to files in `SFSearch\Service\`. The indexes are the user's to keep or not: `sfs_run ["indexes"]` shows each one's size and what it speeds up, `["indexes", "drop", "all"]` deletes them, and `keepStartupIndexes` in `sfsearch.json` says whether they stay between service starts. One more, the world link index, is made only when the user agrees (an answer offers it as `save_index`). A folder `SFSearch\Cache\` or `SFSearch\Service\data\` is left over from 1.x and isn't used. All of these hold game and mod data: never commit, upload or share them.

**`SFSearch\sfsearch.json` belongs to the user.** Change it only when they ask. If you're changing SFSearch's own code, read `DEVELOPING.md` first.
