# SFSearch: instructions for coding agents

SFSearch reads Starfield plugins (`.esm`, `.esp`) and answers lookups, load-order questions, diffs, asset checks and patch checks. Use it for all Starfield plugin data instead of writing a parser.

**Use the `sfsearch` MCP tools when they're loaded** (`sfs_query`, `sfs_names`, `sfs_record`, `sfs_refs`, `sfs_conflicts`, `sfs_loadorder`, `sfs_fields`, `sfs_run` and the rest). Otherwise run the command line from any folder:
```powershell
& "<SFSearch folder>\SFSearch\sfs.cmd" <Command> [Args...]
```
`sfs.cmd --help` lists every command.

**Before your first lookup, read `agent-docs/skills/sfsearch/SKILL.md`.** It says which tool answers which question and lists the traps that give wrong answers with no error. Its `references/` folder has the database layout (read before writing SQL) and the larger commands in depth.

Rules that catch most mistakes:
1. The same six digits are usually different records in different plugins. Use `sfs_names`, and write `EditorID (FormID:plugin)` to the user.
2. Don't guess JSON paths in `Data`: `sfs_fields` lists them for a record type.
3. Add `ORDER BY` whenever row order matters; the service doesn't store rows in file order.
4. A call that returns a job number is still working: collect it with `sfs_job`.
5. Read the `[notes]` lines under an answer (a missing master, a plugin changed on disk, SF1Edit open, a patch built for an older master) and pass them on when they matter.
6. On the command line, quote FormIDs in PowerShell, and keep inline SQL on one line (or use `--file`).

SFSearch never changes a plugin. Its databases (`Cache\`, `Service\data\`) hold game and mod data: never commit, upload or share them.

**`SFSearch\sfsearch.json` belongs to the user.** Change it only when they ask (warm-up, item filter, limits). If you're changing SFSearch's own code, read `DEVELOPING.md` first.

## When a record type isn't stored
Some types only get a basic row by default (cells, statics, dialogue and others). For a few records, read them straight from the file (`sfs_record` with view `file`). For SQL or many records, store the type with `sfs_add` (`Add <plugin> <Type>`): no rebuild needed. Ask the user first before `--all`, or before adding multi-gigabyte types (SurfaceTree, NavigationMesh, Cell, Worldspace, PlacedObject) to Starfield.esm or ShatteredSpace.esm.
