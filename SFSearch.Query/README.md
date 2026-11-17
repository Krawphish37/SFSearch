# SFSearch.Query: the SQL layer

SQL over plugin data that is read live. Nothing is kept between runs.
- The record types a query names are read through `SFSearch.Records` the first time, into scratch tables.
- SQL sees typed views made from `schema.db`: one per record type, one per list.
- A wrong name, a heavy read and an empty answer each get a plain line that says what to do.

It is a library: SFSearch's `query2` and `saved` verbs (tools `sfs_query` and `sfs_saved`, `SFSearch\Engine\QueryTool.cs`, `SavedTool.cs`) are built on it.

## Use it
```csharp
using SFSearch.Index;
using SFSearch.Query;
using SFSearch.Records;

SqliteTuning.NoMemoryStatistics();   // once, before the process opens any SQLite connection
var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
using var index = new HeaderIndex(PluginSource.Folder(dataFolder), new IndexOptions { ScratchFolder = indexScratch, VersionText = "…" });
index.Refresh();
using var records = new RecordEngine(index, schema, verifyScratch, dataFolder);
using var engine = new QueryEngine(records, new QueryOptions { ScratchFolder = sqlScratch });

QueryAnswer a = engine.Run(new QueryRequest
{
    Sql = "SELECT EditorID, AttackDamage FROM Weapon WHERE AttackDamage > 50 ORDER BY 2 DESC",
    Plugins = new[] { "Starfield.esm" },          // or Scope = ScopeKind.Winners / ScopeKind.Versions
});
Console.Write(a.ToText());                        // or a.ToJsonText()
```
- **`Run` never throws for a wrong query.** The answer has `Error` and `Fix`, or `NotRun` with the estimate, or rows.
- **One caller at a time,** like the header index and the records layer. Several processes share one scratch folder.
- **The scratch folder is never a folder that holds plugins.** The engine refuses one that is.
- **`QueryOptions.HeavyGate`** is where the service puts its own memory check (`JobContext.RequireHeavy`). Without it the engine asks Windows itself.
- **A statement's memory** (GAPS S104, J82). `QueryOptions.MemoryLimitMB` is what one command may hold (the service gives its allowance for the command: `workerMaxMB`, or a heavy command's own). A statement that takes the process near it is stopped with `the query was stopped: it took this command to 1.4 GB of memory, and one command may use 1.5 GB` and a fix that says what to narrow and which setting raises it (`MemoryLimitSetting`). SQLite's own heap limit (`sqlite3_hard_heap_limit64`) isn't used: it counts only while SQLite's memory statistics are on, and they are off (`NoMemoryStatistics`). Big sorts, groups and temporary tables go to temporary files (`temp_store = FILE`) in `<scratch folder>/temp` (`SqliteTuning.TempFolder`, set when an engine is made; the files are deleted when the query ends or the program stops): a sort keeps 8 MB runs in memory and merges them on 4 threads, temporary tables keep an eighth of the limit (1 GB without one). A statement whose temporary files pass `QueryOptions.TempFilesLimitMB` (by default 4 times the memory limit, 4 to 32 GB; 16 GB with none: GAPS S110), would take half the drive's free space, or would leave less than `ScratchSpareMB`, is stopped too. Before it starts (GAPS S110 and S127, J88 and J99; `Session.TempFiles.cs`): every SELECT of the statement (its own, each part of a compound SELECT, each CTE and subquery) whose sort keeps a big value is found by its words (a grouped SELECT naming a big column in its result, GROUP BY, HAVING or ORDER BY; a sort, DISTINCT or compound part whose result has the column itself or `*`; big columns: the whole Json of a typed view or Records read whole, a list view's entry Json, a typed view's JSON columns) and confirmed by SQLite's plan (`USE TEMP B-TREE` among that SELECT's own lines of EXPLAIN QUERY PLAN's tree: CO-ROUTINE/MATERIALIZE by name, SCALAR/LIST SUBQUERY by SQLite's number of the SELECT, COMPOUND QUERY/MERGE parts in order), then counted (its FROM and WHERE, each row weighed by its record's stored Json size and the length of each other kept column; a DISTINCT's distinct rows once; a subquery or CTE of its FROM that hands a big value up, by name or in a star, counted in its place with each such value's size under the same name: GAPS S143, J109); a record view SQLite makes into a table or an automatic index adds its records' Json once. Past that limit it answers "Not run" with the size (`HeavyEstimate.TempFilesMB`), and `AllowHeavy` runs it with its estimate and half again allowed when the drive has room. SQLite's temporary-files folder is set through SQLite's own `sqlite3_win32_set_directory8`, imported from e_sqlite3: SQLitePCLRaw's `raw.sqlite3_win32_set_directory` is `return 1` in the provider built for a plain .NET target (GAPS S109; `SqliteWin32` says why). A grouped statement (GROUP BY, a window), and a statement that reads more than one source and names a typed view's Json or JSON column (or Records' Json), works out the view's columns once per record (`Session.Reads.cs`, `CreateViews`, GAPS S104 and S126): the view is given as two parts joined by UNION ALL, the second `SELECT NULL, … WHERE 0` with no FROM, which SQLite's flattener never folds into the statement (rule 17c; J82's second part `SELECT * FROM (…) WHERE 0` is folded into a join that doesn't group, the join spread over both parts). A WHERE on the view's own columns is still pushed into the first part. Where such a view goes in a join is then settled from SQLite's plan (`Session.JsonJoins.cs`, `PlaceJsonViews`): read first (a co-routine), it stays; kept on the inner side in an automatic index or a materialized table (a b-tree whose page cache is SQLite's fixed 2 MB, read back from its temporary file page by page for each row: slower than folded), the statement's FROM is given with the view first (`CROSS JOIN`, its ON conditions on the last table) when the join reads the records' JSON more than twice over and the FROM is plain inner joins of names; else the view is folded again. Before that (GAPS S142, J109, `PerRecordValues`): where every use of such a view's Json (or JSON column) in a SELECT, and in the subqueries inside it, is an expression of that record alone (a function that gives one value for one input: `length`, `json_extract`, `json_array_length`, `substr`, `coalesce`, `iif`, `hex`, …; CASE … END; `->`/`->>` with a constant path; LIKE/GLOB with a literal pattern; IS [NOT] NULL; each taken whole only where SQLite's precedence makes it one operand; on the side of an outer join that can be NULL, one that may give a value on NULL reads the subquery's marker `sfs_r` and gives on a row without a record what it gives on NULL: GAPS S144, J116), the view in that SELECT's FROM is given as a subquery of two parts that works those expressions out once per record (`sfs_e1`, …, beside the view's other columns the statement reads); each use reads its name, and a result column whose text changed keeps its name (`AS` its text). Its values are small, so SQLite may put it anywhere: first, in an automatic index, on the inner side of a LEFT JOIN, beside subqueries and table functions, and for a correlated subquery the values go into a temporary table (`temp.sfs_p1`, the view's types and collations, indexed on the columns the subquery matches with `=`, dropped when the next statement starts; empty under EXPLAIN): as a subquery SQLite may run it again for each row, and in an aggregate's argument it plans the subquery as run once and gives it no automatic index. A subquery of a FROM that hands the Json up whole to a SELECT using it only so has the expressions moved down into it. On the inner side or in a correlated subquery it is done when the statement reads its records' JSON more than twice the size of the records the SELECT works out once (counted as above; a correlated subquery joined to the SELECT around it; the records those its own WHERE and ON terms on the view's columns keep, GAPS S145, J116: SQLite pushes them into the subquery, and a correlated subquery's table is filled with them); else the view is folded. Not done where a use needs the whole value (`SELECT c.Json`, `max(c.Json)`, a star, a table function's argument), names another table, or a NATURAL join or INDEXED BY reads the view's columns by name. A positional parameter (`?`) is answered with a plain line before anything is read (GAPS S161): parameters are given by name. EXPLAIN shows the statement as it runs.
- **`QueryOptions.HeavyRecords`** (1,000,000): a read that opens more records than this is announced whatever its estimate. One column of Starfield.esm's 3.29 million placed objects is estimated at about 9 s, under the 10 s limit, and is still said first.
- **A host wants one line in its project file:** `<RuntimeHostConfigurationOption Include="System.Runtime.TieredCompilation.CallCountingDelayMs" Value="0" />`. Without it .NET runs Mutagen's readers and the JSON serializers in their slow first form for seconds while record types are read for the first time. Measured: the first read of all 171 research types of Starfield.esm takes 11 to 13 s with it and 17 to 18 s without. The runner and the tests have it.

## What SQL sees
| Name | What it is |
|---|---|
| `Weapon`, `Npc`, … | One typed view per record type: `FormKey`, `EditorID`, `Plugin`, `IsOverride`, one column per plain field (`AttackDamage`, `ObjectBounds_First`), `Json` |
| `Weapon_Keywords`, `Weapon_ObjectTemplates_Includes`, … | One view per list: `FormKey`, `EditorID`, `Plugin`, `Idx` (`Idx2`, `Idx3` for lists in lists), then `Value`, or the entry's own fields and its `Json` |
| `Npc_Flags`, … | A flags field of the record as a list of names |
| `ObjectModification`, `GameSetting` | A union view over all types of one signature, with a `Type` column |
| `Records` | Every record read so far in this scope: `FormKey`, `Type`, `Sig`, `EditorID`, `Name`, `Plugin`, `IsOverride`, `Json` |
| `Forms` | Every record of the scope from the header index, read or not: `FormKey`, `Sig`, `Type`, `EditorID`, `Plugin`, `Parent`, `IsWinner`, `IsOverride`, `Pos`, `LoadOrder`, `Size`, `DataSize`, `Compressed`, `Flags` (the record header's flags as stored, bytes 8-11), `FlagNames` (by Mutagen's MajorFlag enum of the record's type, `RecordFlags.cs`; a bit with no name as its number) |
| `Links` | The links of the record types read so far: `FromKey`, `FromType`, `Field`, `Idx`, `ToKey`, `Path`, `Plugin` |
| `Conditions` | Every condition (CTDA) of the record types read so far, one row each (`Conditions.cs`, GAPS S44): `FormKey`, `Type`, `EditorID`, `Plugin`, `Field`, `Idx`, `Path`, `Function` (index), `FunctionName`, `RunOn`, `Reference`, `Comparison`, `Value`, `Global`, `Flags`, `FlagsRaw` (the flags as their number, `sfs_flagbits`), `Param1`-`Param3`, `Json` (and `LoadOrder`, `IsWinner` in the versions scope). Taken out of the records' JSON (every ConditionFloat or ConditionGlobal object) the first time a statement names it. Param1 and Param2 are the data class's own fields that Mutagen doesn't name unused, in file order (GetIsVoiceType's VoiceTypeOrList; GetVMQuestVariable's string). Narrowed to nothing, it reads only the types that can hold a condition |

**Records and Links read what a statement narrows to** (`Session.AutoReads.cs`). A statement over them has read first: the type it names (`WHERE Type = 'Weapon'`, `Sig = 'WEAP'`, `FromType = …`, `IN (…)` too), the type of the record it names (`FormKey`/`FromKey` = a FormKey, `EditorID` = …, from the header index), and for `Links WHERE ToKey = '…'` the types whose records link to it, from the host's link index (`QueryEngine.LinkIndex`; the tool gives it the link files). **A plugin with no link file** (none made yet, or no host index at all) is searched by its bytes instead (`Session.AutoReads.cs`, `ByteSearch` in SFSearch.Index, as `refs` does): of the types whose link fields can point at the record (by the schema), the records whose bytes hold its FormID are read alone, for that statement only, and their links answer it. The answer says so (`… were searched by their bytes (… records, … MB, … ms): N hold its FormID (…), and only those were read.`). Starfield.esm, keyword 04A0A0: rows in seconds where it was "Not run … about 35 s". A statement that narrows to nothing and names no type's view reads every type of the scope when nothing is read yet; when some are, it answers from those and says what the rest would cost and how to read them (`allow_heavy`). A type over the cost limit by itself is read only when named. The usual cost check comes after: over the limit, the answer is the estimate. Stored queries read their types themselves (`QueryRequest.NoAutoReads`).

**`ViewDocs`** says what every name SQL can read is, column by column (type and one line each): typed views, union views, list views, Records, Forms, Links, Conditions, Plugins (every plugin's header, from `PluginRows.cs`) and the `schema.*` tables. `describe` answers from it.
| `schema.*` | The schema tables (`schema.Fields`, `schema.RecordTypes`, `schema.Enums`, …) |
| Functions | `edid(fk)`, `name(fk)`, `winner(fk)`, `versions(fk)`, `text(value, 'de')`, `type(fk)`, `sig(fk)`, `formkey(text)`, `display(name)` |

- **A value the JSON leaves out reads as the field's default** where the schema says the field is always there. A field inside an object that is absent, or of another kind, reads as NULL.
- **A link column holds a FormKey, or NULL for a null link.** FormKeys, EditorIDs and plugin names compare without regard to letter case.
- **Text columns give one language, found by its name:** English, or `QueryOptions.Language`, or `QueryRequest.Language`. `text(Name, 'de')` (a typed view's text column, or `Records.Name`) and `text(Json -> '$.Name', 'de')` give another: on a view's text column the session makes the call read the translated value from the view's `Json`, since the column itself is already plain text in one language.
- **Languages** are Mutagen's names or codes (`TextLanguages`): Starfield ships English (en), German (de), Spanish (es), French (fr), Italian (it), Japanese (ja), Polish (pl), Portuguese_Brazil (ptbr, pt) and ChineseSimplified (zhhans, zh, cn, Chinese). Anything else that isn't a Mutagen language name stops the query with that list. A text with no text in the language asked reads as NULL with a note, never as another language's text. A plugin that keeps its text in itself (no strings files) has one language: that text is given for every language, as the game shows it, with a note. A plain text given to `text(…, 'ja')` (a column of a subquery) reads as NULL with a note that says how to ask.
- **A flags column is a JSON list of names.** Its list view (`Npc_Flags`) has one row per name. Right after it comes its number, `<Field>Raw` (`FlagsRaw.cs`, GAPS S168): every set bit, named or not, as an integer of the enum's width, so a bit is tested by number (`(EntryTypesRaw & 0x80000000) <> 0`). From the JSON it is the names mapped back to their bits by the enum (`sfs_flagbits`); straight from Mutagen's object and from the bytes it is the enum's value (`FlagBits.Raw`). The record header's flags get none: the view has `MajorRecordFlagsRaw`.
- **In the versions scope** every typed view also has `LoadOrder` and `IsWinner`.
- **`Links.Field`** is the field with every list position written `[*]` and a last one dropped (`Keywords`, `ObjectTemplates[*].Includes[*].Mod`). `Idx` is the innermost list position. `Path` is the exact place (`ObjectTemplates[0].Includes[2].Mod`).
- **The query may make tables, indexes and views of its own** (`CREATE TEMP TABLE … AS SELECT …`), in several statements. The last statement's rows are the answer. Nothing of them outlives the query.
- **It can't change a scratch table or the schema, and can't attach a file.** SQLite's authorizer refuses it, and the files are attached read only.
- **Names come without the Creation Kit's tag** (`<ID=0000793F>AA-99 Mk.II` reads `AA-99 Mk.II`): the text columns of the typed views, `Records.Name` and `name(fk)` take it off. `Json` keeps the text as the plugin stores it. **`display(name)`** takes the tag off a text that still has it, such as `text(Json -> '$.Name')`.
- **`type(fk)` and `sig(fk)`** give the record type (`Weapon`; the union view for a signature with several types) and the signature (`WEAP`) of any record in Data, from the header index. **`formkey(text)`** gives the FormKey a text stands for: the text itself when it is a FormKey, otherwise the FormKey of the record with that EditorID.
- **Parameters.** SQL can name values as `:name`. `QueryRequest.Parameters` gives them, and they are bound, never pasted into the SQL. A parameter the request doesn't give stops the query with a plain line.
- **`QueryEngine.Check(request)`** compiles and runs the statements against empty tables with each view's columns. No plugin is read. A wrong name gets the nearest real ones, and the answer has the columns of the last SELECT.

## Stored queries
A stored query is a `.sql` file with a short header: its name, what it answers, its parameters, its scope and its columns. `queries\README.md` describes the format and lists the ones that ship.
```csharp
var stored = new StoredQueries(engine, new StoredOptions
{
    ShippedFolder = Path.Combine(AppContext.BaseDirectory, "queries"),   // the shipped ones travel with this library
    UserFolder = Path.Combine(home, "queries"),                          // the user's; "save" writes here
    AddOnFolders = new[] { Path.Combine(home, "extensions") },           // X.dll brings X.queries\ beside it
});
string list = stored.ListText();
StoredQuery q = stored.Find("cobj");                    // or "shipped.cobj"; a name in two places throws with both full names
StoredCheck check = stored.Check(q);                    // the header and every statement, no plugin read
StoredAnswer a = stored.Run(new StoredRunRequest { Name = "cobj", Arguments = { ["plugin"] = "Starfield.esm" } });
Console.Write(a.ToText());                              // or a.ToJsonText()
StoredSaved s = stored.Save("my-query", text);          // into the user's folder, after the same check
```
- **`Run` goes through `QueryEngine.Run`,** so a stored query gets the same answer object and the same checks: names, cost, the reason for an empty answer. Each SELECT of the file is a table of the answer (`StoredAnswer.Tables`), with its own rows, notes, sources, cost and cursor (`t2:…`).
- **`Run` never throws** for a wrong name, a wrong argument or wrong SQL. The error names the query (`shipped.cobj`) and, for a wrong table or column, the nearest real ones.
- **The files are read from disk on every call.** Nothing about them is kept.

`sfsquery --views` lists every view. `sfsquery --views Weapon` lists one type's columns and list views.

## The three scopes
| Scope | Rows |
|---|---|
| `Plugins` | Each plugin named, as it is. Up to 8 are attached side by side; more go into one table for the set. |
| `Winners` | For each record, the version the game uses: the last loaded plugin that holds it. |
| `Versions` | Every copy in every loaded plugin, with `LoadOrder` and `IsWinner`. |

Winners and versions share one table. It is named by every plugin in Data and the load order, so any change gives a new one.

## What happens to a query
1. **The scope.** The quick stamps of the plugins named (and their masters) are held against the header index. For the load-order scopes the whole index is refreshed (about 30 ms).
2. **The names.** The statement is compiled by SQLite against empty tables that have each view's columns. SQLite says which views and columns it reads. A name it doesn't know stops the query with the nearest real names.
3. **The cost.** For each record type that isn't in the scratch tables yet, the header index gives records and bytes. Over the limit (10 s, 2 GB, or a million records opened) the answer is the estimate, and nothing runs without `AllowHeavy`. A read of named columns through Mutagen is estimated by its bytes and its largest record too, at the world types' own measured rates when a column comes out of each record's JSON (GAPS S101: Starfield.esm's cells' Flags, about 27 s and 1.5 GB, was estimated at 0.08 s by the count of records alone).
4. **The read.** Reading threads (`QueryOptions.Threads`, 8) read each record through Mutagen, turn it into JSON, find its links and make SQLite's JSONB. One thread inserts, in file order. Records nested in others (placed objects, dialogue) are read by every reading thread too: `QueryOptions.DirectReaders` can limit that, and is 0 (no limit) by default.
5. **The views** are made for this statement, with only the columns it reads.
6. **The SQL runs.** The answer gets its notes: an EditorID used where a FormKey is needed, no `ORDER BY`, why there are no rows, records that couldn't be read.

**A type too big to read whole** (Starfield.esm's 3.29 million placed objects) is read for the columns the query names only. No JSON is stored for it. A query that needs none of the record (`SELECT count(*) FROM PlacedObject`) is answered from the header index alone. **The plain columns are read straight from Mutagen's object, with no JSON at all** (`DirectColumns.cs`): links, numbers, yes/no, enum names, plain text and points, of the record or of an object in it. Translated text, flags, fields under a "kind of object" and whole objects as JSON still go through the record's JSON (a query naming some of each reads both ways at once). `QueryOptions.DirectColumns = false` (internal: only the tests set it) reads every column through the JSON, as before; the test `direct-columns` holds the two against each other, value and SQLite type, for every column and every record.

**Columns read from the records' own bytes** (`RawColumns.cs`, J30): when every named column is a field the schema places alone at a fixed offset of a subrecord used nowhere else in the type (a cell's Location in XLCN, a placed object's Base in NAME: 33 of a cell's columns, 138 of a placed object's), the records are inflated and stepped through on the shared record pipeline and the values read at their offsets, with no Mutagen object; a record the bytes can't answer for sure (the subrecord twice, too short, absent where Mutagen would give a default, a link through a missing master) is read through Mutagen. `Cell WHERE Location IS NOT NULL` over Starfield.esm: 0.8 s, was 6.6 to 18 s. The test `direct-columns` holds the bytes against Mutagen record by record on Starfield.esm's cells and placed objects.

**`col = literal` applied while reading** (`Session.Filter.cs`, J30): when a statement compares a column of a big type's typed view only with literals (`=`, `IN`) and SQLite's plan over the stand-ins reaches every row of the view through an index on that column, only the records whose value is one of them are kept, in a temporary table of the statement (`PlacedObject WHERE Base = '00003B:Starfield.esm'`: 2,648 of 3,291,860 kept, 0.55 s). The rules are the FormKey narrowing's (`ColumnLiterals`, `OnlyThrough`). Such a read from the bytes isn't heavy by its record count.

**Over a set scope too** (J63, GAPS S89): the winners and the versions scope, and a list of more plugins than are attached (one scratch file for the set), take both routes: each plugin's records go through the same byte reader and filter in turn, and each row carries its plugin, its place in the load order and its winner mark from the header index (a set's column table has `Plugin`, `LoadOrder`, `IsWinner`); the winners scope keeps the winning rows. Over the load order (223 plugins) `Cell WHERE Location IS NOT NULL` takes 1.75 s (205 s whole as JSON), `PlacedObject WHERE Base = '00003B:Starfield.esm'` 1.2 s (80 s).

**A list view of a type too big or slow to read whole reads only the records that hold the list's subrecords** (`ListReads.cs`, GAPS S45). When a statement reads only list views of a type (not its typed view) and the whole read would be heavy or take a second or more, the type's records are stepped through in the plugin's bytes (inflated when compressed) for the subrecords the list's columns sit in (`PlacedObject_Traversals`: XTV2), and only those records are read: the list's named columns straight from Mutagen's objects (`DirectColumns.cs`, `DirectList`) into a scratch table of their own (`XL_<view>_<n>`, registered in `Proj` as `list:<view>`, reused by later queries), or, when a column has to come from the JSON (`Json`, flags, translated text, a field under a kind of object), the list's rows taken out of each record's JSON on the reading thread with the view's own expressions (`ViewBuilder.ListProjectSql`), the record's JSON let go at once, into the same kind of scratch table (GAPS S58; a JSON value is kept as JSONB and given back as JSON). A record type that holds others (a cell) has its nested records split out of its JSON only when the list's field could hold one (`RecordSchema.FieldHoldsRecords`). When the statement names the type's typed view too and joins it to the list by FormKey (`t.FormKey = l.FormKey` among the ANDed conditions of the WHERE, or of the ON of an inner join or of a LEFT JOIN whose right side is the typed view, or `USING (FormKey)` between the two alone; no OR there; no RIGHT, FULL or NATURAL join), the typed view is read only for the FormKeys of the records that hold the list (every copy of them in the scope), its named columns into a temporary table of the statement; anything else reads the type as before. Nothing is left out: a record without the subrecords has no row in the list. The answer says it: `PlacedObject_Traversals: 4,544 of the 3,291,860 PlacedObject records of Starfield.esm hold its subrecord (XTV2), found by stepping through their bytes in 0.26 s; only those were read`. Joining every plugin's navmesh info map traversals to the cells', placed objects' and components' traversal references on six floats (C221) takes 107 s over 369 plugins.

**A typed view narrowed by FormKey reads only those records** (`Session.Keyed.cs`): `SELECT Base FROM PlacedObject WHERE FormKey = '…'` (or `IN ('…', …)`, or `'…' = FormKey`) takes the copies of those FormKeys from the header index and reads them alone, into a temporary table of the query's own connection (never the scratch files: a scratch file holds whole types). It is done when the whole read would take 1 s or more, or be heavy, and only when nothing can be left out: SQLite's own plan of the statement (over the empty stand-in tables, with an index on each view's FormKey) finds every row it reads of the view by FormKey, and every FormKey the statement compares is a text literal. Anything else (`OR` with another column, `>=`, a subquery, a parameter, a join on FormKey, `USING`, `NATURAL`) reads as before. The read line says `PlacedObject (1 by FormKey, from the header index)`.

## The scratch files
One SQLite file per plugin, named by the plugin's reading key (`q1-english-<key>.db`), and for a localized plugin with string files by their stamp too (`q1-english-<key>-s<strings key>.db`, `IndexedPlugin.Strings`): after a string file changes the next query reads a new file, never the old texts. One file per plugin set (`lo1-…`, `set1-…`), whose name takes in every plugin's strings stamp too. A plugins-scope query takes one look at its plugins' string files (about a millisecond) and refreshes the index when one changed; a read checks them again at its end, as it checks the plugin.

| Table | Holds |
|---|---|
| `Rec` | One row per record: type, signature, FormKey, EditorID, IsOverride, file position, the JSON as JSONB, its links |
| `Done` | Which signatures are complete |
| `Skipped` | Records that couldn't be read, with the reason |
| `LinkRows` | The links as rows, indexed both ways. Filled the first time a query names `Links` |
| `CondRows` | The conditions as rows (`Done.CondDone` says which types are in). Filled the first time a query names `Conditions` |
| `FormRows` | The header index as rows. Filled the first time a query names `Forms` |
| `Proj`, `X_<Type>_<n>` | The named columns of a type too big to read whole |

- **WAL mode.** Readers never wait for the writer.
- **One writer per file,** through a named mutex. Windows hands it on if the holder dies.
- **A signature goes in inside one transaction.** `Done` says it is complete.
- **A new file is made under a temporary name** and renamed when it is whole.
- **After each read the WAL is folded back into the file.** The query connections are read only, so nothing else would ever empty it.
- **Any file can be deleted at any time** when no process has it open. It is made again on the next query.

## The files
| File | Holds |
|---|---|
| `QueryEngine.cs` | The engine, its options, `SqliteTuning` |
| `Session.cs` | One query from start to end: scope, connection, the probe, running it |
| `Session.Reads.cs` | What a statement needs read, the cost check, the reads, the views |
| `Session.Checks.cs` | Wrong names, EditorID literals, the reason for an empty answer |
| `ViewBuilder.cs` | Typed views, list views and union views from the schema |
| `RecordReader.cs` | Reading records on the shared record pipeline (SFSearch.Index `RecordPipeline`), the link finder |
| `DirectColumns.cs` | The named columns of a big type read straight from Mutagen's object |
| `RawColumns.cs` | The named columns of a big type read from the records' own bytes at the schema's offsets |
| `Session.Filter.cs` | A statement's `col = literal` applied while a big type's columns are read |
| `ListReads.cs` | A list view of a type too big or slow to read whole: the records that hold its subrecords (`SubrecordScan`), its named columns straight from them |
| `Conditions.cs` | The Conditions view: which types can hold a condition, a condition's JSON taken apart into its columns, CondRows filled |
| `Session.Keyed.cs` | Records read by key for one statement: a view narrowed by FormKey, the byte search's records for Links WHERE ToKey |
| `Scratch.cs` | The scratch files: making, the writer's lock, the WAL |
| `Cost.cs` | The estimates and their measured rates, the free-memory check |
| `Guard.cs` | What the user's SQL may do |
| `Functions.cs` | `edid`, `name`, `winner`, `versions`, `text`, `type`, `sig`, `formkey`, `display` |
| `Answer.cs` | The request and the answer, as text and as JSON |
| `SqlText.cs`, `BulkInsert.cs` | A small SQL reader; one prepared insert run many times |
| `StoredQuery.cs` | A stored query's file format: the header, the statements, what is wrong with a header |
| `StoredQueries.cs` | The stored queries: find, list, show, check, run, save; what a query over `Records` or `Links` has read first |
| `Session.Stored.cs` | Parameters bound as values; the check of a statement list against empty tables |

## The runner and the tests
```powershell
dotnet build SFSearch.Query.Runner -c Release
$q = "SFSearch.Query.Runner\bin\Release\net10.0\sfsquery.exe"
& $q "SELECT EditorID, AttackDamage FROM Weapon WHERE AttackDamage > 50 ORDER BY 2 DESC" --plugins Starfield.esm
& $q "SELECT Plugin, count(*) FROM Weapon GROUP BY Plugin ORDER BY 2 DESC" --versions
& $q "SELECT count(*) FROM PlacedObject WHERE Base = '0002F6:Starfield.esm'" --plugins Starfield.esm --allow-heavy
& $q --views Weapon
& $q --stats Starfield.esm          # what a read of each type is estimated to cost

dotnet build SFSearch.Query.Tests -c Release
SFSearch.Query.Tests/bin/Release/net10.0/sfsquery-tests.exe "<Data folder>" --report <folder>
```
- The runner's options are listed at the top of `SFSearch.Query.Runner\Program.cs`. Its scratch folder is `%TEMP%\sfsquery-scratch` unless `--scratch` or `SFSQUERY_SCRATCH` names another. Exit code 7 means "not run: the estimate is over the limit".
- The tests' work folder is `%TEMP%\sfsquery-tests` unless `--work` names another. `--heavy` adds the check on Starfield.esm's placed objects against Mutagen's own walk over them: run that alone, on a PC with memory to spare.
- The stored queries have four tests: `stored-format`, `stored-shipped`, `stored-mutagen`, `stored-timing`. (`stored-macros`, which held them against SFSearch 1.x's `Q` macros, was retired on 2026-11-15: 2.0 removed `Q`. `stored-mutagen` holds their answers against Mutagen.)
- `sfsquery --saved list | show <name> | check [<name>] | run <name> parameter=value … | save <name> --file q.sql` works with the stored queries (`queries\README.md`).
- Nothing is ever written to the Data folder.
