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
| `Forms` | Every record of the scope from the header index, read or not: `FormKey`, `Sig`, `Type`, `EditorID`, `Plugin`, `Parent`, `IsWinner`, `IsOverride`, `Pos`, `LoadOrder` |
| `Links` | The links of the record types read so far: `FromKey`, `FromType`, `Field`, `Idx`, `ToKey`, `Path`, `Plugin` |

**Records and Links read what a statement narrows to** (`Session.AutoReads.cs`). A statement over them has read first: the type it names (`WHERE Type = 'Weapon'`, `Sig = 'WEAP'`, `FromType = …`, `IN (…)` too), the type of the record it names (`FormKey`/`FromKey` = a FormKey, `EditorID` = …, from the header index), and for `Links WHERE ToKey = '…'` the types whose records link to it, from the host's link index (`QueryEngine.LinkIndex`; the tool gives it the link files). **A plugin with no link file** (none made yet, or no host index at all) is searched by its bytes instead (`Session.AutoReads.cs`, `ByteSearch` in SFSearch.Index, as `refs` does): of the types whose link fields can point at the record (by the schema), the records whose bytes hold its FormID are read alone, for that statement only, and their links answer it. The answer says so (`… were searched by their bytes (… records, … MB, … ms): N hold its FormID (…), and only those were read.`). Starfield.esm, keyword 04A0A0: rows in seconds where it was "Not run … about 35 s". A statement that narrows to nothing and names no type's view reads every type of the scope when nothing is read yet; when some are, it answers from those and says what the rest would cost and how to read them (`allow_heavy`). A type over the cost limit by itself is read only when named. The usual cost check comes after: over the limit, the answer is the estimate. Stored queries read their types themselves (`QueryRequest.NoAutoReads`).

**`ViewDocs`** says what every name SQL can read is, column by column (type and one line each): typed views, union views, list views, Records, Forms, Links and the `schema.*` tables. `describe` answers from it.
| `schema.*` | The schema tables (`schema.Fields`, `schema.RecordTypes`, `schema.Enums`, …) |
| Functions | `edid(fk)`, `name(fk)`, `winner(fk)`, `versions(fk)`, `text(value, 'de')`, `type(fk)`, `sig(fk)`, `formkey(text)`, `display(name)` |

- **A value the JSON leaves out reads as the field's default** where the schema says the field is always there. A field inside an object that is absent, or of another kind, reads as NULL.
- **A link column holds a FormKey, or NULL for a null link.** FormKeys, EditorIDs and plugin names compare without regard to letter case.
- **Text columns give one language, found by its name:** English, or `QueryOptions.Language`, or `QueryRequest.Language`. `text(Name, 'de')` (a typed view's text column, or `Records.Name`) and `text(Json -> '$.Name', 'de')` give another: on a view's text column the session makes the call read the translated value from the view's `Json`, since the column itself is already plain text in one language.
- **Languages** are Mutagen's names or codes (`TextLanguages`): Starfield ships English (en), German (de), Spanish (es), French (fr), Italian (it), Japanese (ja), Polish (pl), Portuguese_Brazil (ptbr, pt) and ChineseSimplified (zhhans, zh, cn, Chinese). Anything else that isn't a Mutagen language name stops the query with that list. A text with no text in the language asked reads as NULL with a note, never as another language's text. A plugin that keeps its text in itself (no strings files) has one language: that text is given for every language, as the game shows it, with a note. A plain text given to `text(…, 'ja')` (a column of a subquery) reads as NULL with a note that says how to ask.
- **A flags column is a JSON list of names.** Its list view (`Npc_Flags`) has one row per name.
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
3. **The cost.** For each record type that isn't in the scratch tables yet, the header index gives records and bytes. Over the limit (10 s, 2 GB, or a million records opened) the answer is the estimate, and nothing runs without `AllowHeavy`.
4. **The read.** Reading threads (`QueryOptions.Threads`, 8) read each record through Mutagen, turn it into JSON, find its links and make SQLite's JSONB. One thread inserts, in file order. Records nested in others (placed objects, dialogue) are read by every reading thread too: `QueryOptions.DirectReaders` can limit that, and is 0 (no limit) by default.
5. **The views** are made for this statement, with only the columns it reads.
6. **The SQL runs.** The answer gets its notes: an EditorID used where a FormKey is needed, no `ORDER BY`, why there are no rows, records that couldn't be read.

**A type too big to read whole** (Starfield.esm's 3.29 million placed objects) is read for the columns the query names only. No JSON is stored for it. A query that needs none of the record (`SELECT count(*) FROM PlacedObject`) is answered from the header index alone. **The plain columns are read straight from Mutagen's object, with no JSON at all** (`DirectColumns.cs`): links, numbers, yes/no, enum names, plain text and points, of the record or of an object in it. Translated text, flags, fields under a "kind of object" and whole objects as JSON still go through the record's JSON (a query naming some of each reads both ways at once). `QueryOptions.DirectColumns = false` reads every column through the JSON, as before; the test `direct-columns` holds the two against each other, value and SQLite type, for every column and every record.

**A typed view narrowed by FormKey reads only those records** (`Session.Keyed.cs`): `SELECT Base FROM PlacedObject WHERE FormKey = '…'` (or `IN ('…', …)`, or `'…' = FormKey`) takes the copies of those FormKeys from the header index and reads them alone, into a temporary table of the query's own connection (never the scratch files: a scratch file holds whole types). It is done when the whole read would take 1 s or more, or be heavy, and only when nothing can be left out: SQLite's own plan of the statement (over the empty stand-in tables, with an index on each view's FormKey) finds every row it reads of the view by FormKey, and every FormKey the statement compares is a text literal. Anything else (`OR` with another column, `>=`, a subquery, a parameter, a join on FormKey, `USING`, `NATURAL`) reads as before. The read line says `PlacedObject (1 by FormKey, from the header index)`.

## The scratch files
One SQLite file per plugin, named by the plugin's reading key (`q1-english-<key>.db`), and for a localized plugin with string files by their stamp too (`q1-english-<key>-s<strings key>.db`, `IndexedPlugin.Strings`): after a string file changes the next query reads a new file, never the old texts. One file per plugin set (`lo1-…`, `set1-…`), whose name takes in every plugin's strings stamp too. A plugins-scope query takes one look at its plugins' string files (about a millisecond) and refreshes the index when one changed; a read checks them again at its end, as it checks the plugin.

| Table | Holds |
|---|---|
| `Rec` | One row per record: type, signature, FormKey, EditorID, IsOverride, file position, the JSON as JSONB, its links |
| `Done` | Which signatures are complete |
| `Skipped` | Records that couldn't be read, with the reason |
| `LinkRows` | The links as rows, indexed both ways. Filled the first time a query names `Links` |
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
| `RecordReader.cs` | The reading threads, the link finder |
| `DirectColumns.cs` | The named columns of a big type read straight from Mutagen's object |
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
- The stored queries have five tests: `stored-format`, `stored-shipped`, `stored-mutagen`, `stored-macros`, `stored-timing`. `stored-macros` holds the stored queries against today's `Q` macros and needs `--baseline <folder>` (the recorded answers of the golden replay: the harness's baseline's `cli\recorded` folder). With `--today <SFSearch.exe> --today-home <test home>` it also runs today's macros itself, for the ones no recorded command used.
- `sfsquery --saved list | show <name> | check [<name>] | run <name> parameter=value … | save <name> --file q.sql` works with the stored queries (`queries\README.md`).
- Nothing is ever written to the Data folder.
