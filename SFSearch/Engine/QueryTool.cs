using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Query;
using SFSearch.Records;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// query (tool sfs_query, command-line verb query2): SQL over typed views, read live through the SQL layer
    /// (SFSearch.Query). No database is kept: the record types a query names are read on the spot into scratch tables
    /// under scratch\query, which the service empties at every start.
    /// On the command line the verb is query2: the plain name Query was the older command (removed in 2.0), and
    /// answers with a pointer here, so an old script's SQL over the old tables isn't run against the typed views.
    /// (design\agent-first\ARCHITECTURE.md sections 7 and 8; milestone M8.)
    /// </summary>
    public static class QueryTool
    {
        public const string Help = """
            query2 [<plugins>] "<sql>;" [--plugins <plugin,...>] [--scope plugins|winners|versions] [--rows N] [--cursor C]
                   [--language <language>] [--max-chars N] [--allow-heavy] [--file query.sql | --stdin] [--json]
              SQL over typed views, read live from the plugin files. No database is read or built.

              sql        One or more statements; the last SELECT's rows are the answer. Typed on the command line it must
                         end with ';': cmd.exe cuts a command at a line break, and a cut query would run as a shorter one.
                         --file and --stdin take any SQL.
              --plugins  the plugins to read, each as it is: names in Data, or the path of one plugin file elsewhere.
                         (The first word may name them too: query2 kgcdoom.esm "SELECT ...;")
              --scope    plugins   (default when plugins are named) each plugin named, as it is; a Plugin column tells them apart
                         winners   (default with no plugin) for each record, the version the game uses
                         versions  every copy in every loaded plugin, with LoadOrder and IsWinner
              --rows     rows a page (default 40). The [more] line gives the cursor for the next page.
              --language the language of text columns (default English). Starfield ships English (en), German (de),
                         Spanish (es), French (fr), Italian (it), Japanese (ja), Polish (pl), Portuguese_Brazil (ptbr),
                         ChineseSimplified (zhhans, zh); text(Name, 'ja') gives one column in another language.
              --allow-heavy  run a query estimated over 10 s or 2 GB. Without it the answer is what it would cost.

              What SQL sees: one view per record type (Weapon, Npc, ...: FormKey, EditorID, Plugin, IsOverride, one column
              per plain field, Json), one per list (Weapon_Keywords: FormKey, Idx, Value), ObjectModification and GameSetting
              over all types of their signature, Records, Forms (every record, read or not), Links, schema.* tables.
              Functions: edid(fk), name(fk), winner(fk), versions(fk), type(fk), sig(fk), formkey(text), text(value, 'de').
              describe <type> lists a type's fields. Names shown have the Creation Kit's <ID=...> tag taken off; Json keeps it.
              SQL here only reads: DELETE, UPDATE, INSERT or DROP on a record table is an error. A temporary table of your
              own (CREATE TEMP TABLE ... AS SELECT ...) can be made and changed.
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...); 7 not run: heavy work that wasn't allowed;
              8 not enough memory free for heavy work that was allowed.
            """;

        static readonly string[] Known = { "sql", "plugins", "scope", "rows", "cursor", "language", "max_chars", "allow_heavy" };
        public static readonly VerbSpec Spec = new()
        {
            Verb = "query2", Positional = new[] { "sql" }, Options = new[] { "plugins", "scope", "rows", "cursor", "language", "max_chars", "file" }, Flags = new[] { "allow_heavy", "stdin" },
            Aliases = new(StringComparer.OrdinalIgnoreCase) { ["lang"] = "language", ["plugin"] = "plugins", ["in"] = "plugins" },
        };

        // ---------------------------------------------------------------- Entry points
        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            var answer = new EngineAnswer { AsTool = true };
            if (a.Unknown(Known) is { Count: > 0 } wrong)
                answer.Fail($"query has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", $"its arguments are: {string.Join(", ", Known)}", 2);
            else Guarded(a, answer, a.Text("sql"));
            answer.Emit();
        }

        public static void Verb(List<string> args)
        {
            if (args.Count == 0 || args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            var answer = new EngineAnswer();
            // The first word may name the plugins, as the older Query took them: query2 kgcdoom.esm "SELECT ...;"
            string? first = null;
            if (args.Count >= 2 && !args[0].StartsWith("--", StringComparison.Ordinal) && LooksLikePlugins(args[0]) && !args.Any(x => x.Equals("--plugins", StringComparison.OrdinalIgnoreCase) || x.Equals("--plugin", StringComparison.OrdinalIgnoreCase)))
            {
                first = args[0];
                args = args.Skip(1).ToList();
            }
            var given = Spec.Parse(args, answer);
            if (given != null)
            {
                if (first != null) given["plugins"] = JsonSerializer.SerializeToElement(first);
                var a = new ToolArgs(given);
                string? sql = a.Text("sql");
                string? file = a.Text("file");
                if (file != null && sql != null) answer.Fail("query2 got SQL on the command line and --file", "give one of them", 2);
                else if (file != null)
                {
                    string path = Home.UserPath(file);
                    if (!File.Exists(path)) answer.Fail($"there is no file {path}", "check the path given with --file", 2);
                    else sql = File.ReadAllText(path);
                }
                else if (a.Flag("stdin")) sql = Console.In.ReadToEnd();
                else if (sql != null && first == null && LooksLikePlugins(sql.Trim()))
                    answer.Fail($"query2 got the plugin {sql.Trim()} and no SQL", "write the SQL after it, in quotes and ended with ';': query2 kgcdoom.esm \"SELECT EditorID FROM Weapon ORDER BY 1;\" (or pass it with --file or --stdin)", 2);
                else if (sql != null && !sql.TrimEnd().EndsWith(';'))
                {
                    // Trap 10: cmd.exe cuts a command at a line break, so SQL typed over several lines arrives as its first line
                    string shown = sql.Replace('\r', ' ').Replace('\n', ' ');
                    answer.Fail($"the SQL typed on the command line doesn't end with ';' (it ends \"… {(shown.Length > 60 ? shown[^60..] : shown)}\"). cmd.exe cuts a command at a line break, so a query typed over several lines arrives as its first line only, and would run as a shorter query",
                        "end inline SQL with ';', or pass it with --file <file> or --stdin, which take any SQL. The sfs_query tool takes SQL over several lines as it is", 2);
                }
                if (!answer.Failed) Guarded(a, answer, sql);
            }
            answer.Emit();
        }

        static bool LooksLikePlugins(string text) =>
            text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } parts
            && parts.All(p => p.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".esl", StringComparison.OrdinalIgnoreCase));

        static void Guarded(ToolArgs a, EngineAnswer answer, string? sql)
        {
            try { Run(a, answer, sql); }
            catch (QueryException e) { var (what, fix) = EngineLibs.ErrorParts(e.Message, e.Fix, "check the SQL; describe <type> lists a type's fields"); answer.Fail(what, fix, 1); }
            catch (RecordException e) { answer.Fail(e.Message.Split('\n')[0], "ask again; loadorder2 problems lists what is wrong with a plugin"); }
            catch (FileNotFoundException e) { answer.Fail(e.Message.Split('\n')[0], "set the Data folder with SetData, or the SFSEARCH_DATA environment variable"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                answer.Fail($"the query couldn't be run ({e.Message.Split('\n')[0].TrimEnd('.')})", "ask again; if it stays, loadorder2 problems shows which plugin is at fault");
            }
            finally
            {
                if (EngineRecords.TrimIfLarge() is { } trimmed) answer.After(trimmed);
            }
        }

        // ---------------------------------------------------------------- Run
        /// <summary>What a query runs on: the SQL engine, the plugins by the names the engine knows them by, and the scope.</summary>
        public sealed class Target
        {
            public required QueryEngine Engine;
            public required HeaderIndex Ix;
            public List<string> Plugins = new();
            public ScopeKind Scope;
            public List<(HeaderIndex Ix, IndexedPlugin Plugin, string? ShownAs)> ReadFrom = new();
        }

        /// <summary>
        /// The plugins and the scope a caller named. A plugin file outside Data is read with its masters from Data, in
        /// an index of its own: it can be queried alone or beside its masters, never beside other plugins.
        /// Null with a failed answer.
        /// </summary>
        public static Target? Resolve(EngineIndex eng, string? pluginsText, string? scopeText, Language language, EngineAnswer answer, string verb, bool defaultWinners = true)
        {
            ScopeKind? scope = null;
            if (!string.IsNullOrWhiteSpace(scopeText))
            {
                if (!Enum.TryParse<ScopeKind>(scopeText.Trim(), true, out var s) || int.TryParse(scopeText, out _))
                { answer.Fail($"{verb} has no scope named '{scopeText}'", "use plugins (each plugin named, as it is), winners (the version the game uses) or versions (every copy in every loaded plugin)", 2); return null; }
                scope = s;
            }
            var names = (pluginsText ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => n.Trim('"')).Where(n => n.Length > 0).ToList();
            if (scope is ScopeKind.Winners or ScopeKind.Versions && names.Count > 0)
            { answer.Fail($"the {scope.ToString()!.ToLowerInvariant()} scope reads the whole load order, and plugins were named too ({string.Join(", ", names)})", "leave plugins out, or use scope plugins. In the load-order scopes a WHERE Plugin = '…' narrows the rows", 2); return null; }
            scope ??= names.Count > 0 || !defaultWinners ? ScopeKind.Plugins : ScopeKind.Winners;

            var ix = eng.Index;
            var t = new Target { Engine = null!, Ix = ix, Scope = scope.Value };
            if (scope == ScopeKind.Plugins)
            {
                if (names.Count == 0) { answer.Fail($"{verb} names no plugin", "name one or more plugins, or use scope winners or versions", 2); return null; }
                var picked = new List<(HeaderIndex Ix, IndexedPlugin Plugin, bool ByPath)>();
                foreach (var n in names)
                {
                    // A wildcard names every plugin in Data that fits
                    if (Glob.IsPattern(n) && !n.Contains('\\') && !n.Contains('/'))
                    {
                        var g = new Glob(n);
                        var fits = ix.Plugins.Where(p => g.IsMatch(p.Name)).ToList();
                        if (fits.Count == 0) { answer.Fail($"no plugin in Data fits \"{n}\" ({ix.Plugins.Count} plugins are there)", "check the pattern: loadorder2 lists the plugins"); return null; }
                        picked.AddRange(fits.Select(p => (ix, p, false)));
                        continue;
                    }
                    var one = RecordPick.Plugin(eng, n, answer);
                    if (one == null) return null;
                    picked.Add(one.Value);
                }
                var byPath = picked.Where(p => p.ByPath).ToList();
                if (byPath.Count > 1) { answer.Fail($"{byPath.Count} plugin files outside Data were named ({string.Join(", ", byPath.Select(p => p.Plugin.Path))})", "a file outside Data is read alone, or beside its own masters: ask about one at a time", 2); return null; }
                if (byPath.Count == 1)
                {
                    var side = byPath[0];
                    t.Ix = side.Ix;
                    foreach (var p in picked)
                    {
                        if (p.ByPath) { t.Plugins.Add(p.Plugin.Name); t.ReadFrom.Add((side.Ix, p.Plugin, p.Plugin.Path)); continue; }
                        var inSide = side.Ix.Plugin(p.Plugin.Name);
                        if (inSide == null) { answer.Fail($"{p.Plugin.Name} isn't a master of {side.Plugin.Path}", "a file outside Data is read alone, or beside its own masters", 2); return null; }
                        t.Plugins.Add(inSide.Name); t.ReadFrom.Add((side.Ix, inSide, null));
                    }
                }
                else
                    foreach (var p in picked.DistinctBy(p => p.Plugin.Name, StringComparer.OrdinalIgnoreCase)) { t.Plugins.Add(p.Plugin.Name); t.ReadFrom.Add((ix, p.Plugin, null)); }
            }
            t.Engine = EngineLibs.Query(eng, t.Ix, language);
            return t;
        }

        static void Run(ToolArgs a, EngineAnswer answer, string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) { answer.Fail("query got no SQL", "pass the SQL as sql (command line: query2 \"SELECT ...;\" --plugins x.esm, or --file, or --stdin)", 2); return; }
            int rows = a.Int("rows") ?? 40, maxChars = a.Int("max_chars") ?? RecordTool.DefaultChars;
            if (a.Text("rows") != null && (a.Int("rows") == null || rows < 1 || rows > 100000)) { answer.Fail($"rows takes a number from 1 to 100,000, not '{a.Text("rows")}'", "give the rows a page, e.g. rows 200", 2); return; }
            if (maxChars < RecordTool.LeastChars) maxChars = RecordTool.LeastChars;
            var eng = EngineIndex.Get();
            var v = eng.Check();
            answer.Checked(v);
            if (!EngineRecords.TryLanguage(null, out var english)) return;
            var target = Resolve(eng, a.Text("plugins"), a.Text("scope"), english, answer, "query");
            if (target == null) return;
            if (!ProblemNotes.Add(answer, target.ReadFrom, required: true)) return;
            target.Engine.LinkIndex ??= LinkHolders;

            var request = new QueryRequest
            {
                Sql = sql, Scope = target.Scope, Plugins = target.Plugins, AllowHeavy = a.Flag("allow_heavy") || JobContext.HeavyAllowed,
                PageRows = rows, Cursor = a.Text("cursor")?.Trim('"'), MaxChars = maxChars, Language = a.Text("language"),
            };
            var result = target.Engine.Run(request);
            SqlNotes(answer, sql, result);
            Render(answer, result, maxChars, target);
        }

        /// <summary>
        /// What the link index (scratch\links) says of the records of a plugin that link to a FormKey: their signatures,
        /// and the signatures it doesn't cover (world data, records the walk couldn't read). Null when the plugin has no
        /// link file yet. The SQL layer reads those types first for a query over Links WHERE ToKey = '…'.
        /// </summary>
        static SFSearch.Query.LinkHolders? LinkHolders(IndexedPlugin p, string formKey)
        {
            var seg = p.Segment;
            if (seg == null || EngineLinks.For(p) is not { } file) return null;
            var sigs = new HashSet<string>(StringComparer.Ordinal);
            if (HeaderIndex.TryParseFormKey(formKey, out var owner, out var localId) && seg.OwnerOf(owner) is int o && o >= 0)
            {
                var (first, end) = file.Range((ulong)o << 24 | localId);
                var links = file.Links;
                for (int i = first; i < end; i++) sigs.Add(seg.Signatures[seg[(int)links[i].From].Signature].Signature);
            }
            var notIndexed = new HashSet<string>(StringComparer.Ordinal);
            var world = WorldTypes.Of(seg);
            for (int s = 0; s < world.Length; s++) if (world[s]) notIndexed.Add(seg.Signatures[s].Signature);
            foreach (int entry in file.Unread) notIndexed.Add(seg.Signatures[seg[entry].Signature].Signature);
            return new SFSearch.Query.LinkHolders(sigs, notIndexed);
        }

        static readonly System.Text.RegularExpressions.Regex JsonEach = new(@"\bjson_(each|tree)\s*\(\s*(?:\w+\s*\.\s*)?Json\s*(?:,\s*'\$\.(?<list>[A-Za-z0-9_]+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Two things about the SQL itself that an agent must be told (trap 11, and the meaning of Records):
        /// json_each over a record's Json is run once for every row, where a list view has the same rows ready; and
        /// Records holds only the record types that have been read, which a query that names nothing else leaves short.
        /// The second is said only when the SQL reads the table Records (the SQL layer says so: a column or an alias
        /// of that name isn't the table), and it lists the types Records holds for these plugins after the query.
        /// </summary>
        static void SqlNotes(EngineAnswer answer, string sql, QueryAnswer result)
        {
            if (result.Error != null || result.NotRun != null) return;
            string plain = System.Text.RegularExpressions.Regex.Replace(sql, @"--[^\n]*|/\*.*?\*/|'(?:[^']|'')*'", m => m.Value.StartsWith('\'') ? m.Value : " ", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (JsonEach.Match(plain) is { Success: true } each)
            {
                string list = each.Groups["list"].Success ? each.Groups["list"].Value : "Keywords";
                answer.Note($"json_{each.Groups[1].Value.ToLowerInvariant()} over a record's Json is run once for every row of the table, which is what makes such a query slow on a big type. Each list of a record type is a list view with its own rows, ready to join: for $.{list} that is <Type>_{list} (Weapon_{list}: FormKey, EditorID, Plugin, Idx, Value). describe <type> lists a type's fields.");
            }
            // With no rows the SQL layer's own "No rows: Records holds only …" note already says it
            bool saidByEmpty = result.Notes.Any(n => n.StartsWith("No rows:", StringComparison.Ordinal) && n.Contains("Records holds only", StringComparison.Ordinal));
            if (result.SpecialTables.Contains("Records", StringComparer.OrdinalIgnoreCase) && !saidByEmpty)
            {
                // The types this query used first (what it read now, or found already read), then the others
                var used = new HashSet<string>(result.Cost.AlreadyRead.Select(l => l.Split(' ')[0]), StringComparer.Ordinal);
                foreach (var r in result.Cost.Read) used.Add(r.Split(' ')[0]);
                var read = result.TypesRead.OrderBy(t => used.Contains(t) ? 0 : 1).ToList();
                answer.Note($"Records holds only the record types read so far for {(result.LoadOrder != null ? "the load order" : "these plugins")} ({(read.Count == 0 ? "none yet" : string.Join(", ", read.Take(8)) + (read.Count > 8 ? $" and {read.Count - 8} more" : ""))}): a type is read when a query names its own view or narrows Records to it (WHERE Type = '…', FormKey = '…', EditorID = '…'). A count over Records is a count of those, not of every record: Forms has every record's header, and a stored query (saved list) reads the types it needs first.");
            }
        }

        /// <summary>Where an answer was read from: each plugin copy, up to six by name and the rest counted, then the load order.</summary>
        public static void Sources(EngineAnswer answer, QueryAnswer result, Target target)
        {
            const int most = 6;
            foreach (var s in result.Sources.Take(result.Sources.Count > most + 1 ? most : int.MaxValue))
            {
                var p = target.Ix.Plugin(s.Plugin);
                if (p != null) answer.Source(EngineIndex.Source(p, target.ReadFrom.FirstOrDefault(r => ReferenceEquals(r.Plugin, p)).ShownAs));
                else answer.Source(s.Text);
            }
            if (result.Sources.Count > most + 1)
                answer.Source($"and {result.Sources.Count - most:N0} more plugins ({EngineIndex.Mb(result.Sources.Skip(most).Sum(s => s.Bytes))})");
            if (result.LoadOrder != null) answer.Source(result.LoadOrder);
        }

        /// <summary>One answer of the SQL layer as the engine's: the table, [more], the notes, where it was read from, what was read.</summary>
        public static void Render(EngineAnswer answer, QueryAnswer result, int maxChars, Target target, string cursorPrefix = "")
        {
            // Notes about a plugin that reads wrong lead; the ones ProblemNotes already gave aren't said twice
            foreach (var n in result.Notes)
            {
                if (target.ReadFrom.Any(r => n.StartsWith(r.Plugin.Name + ": ", StringComparison.OrdinalIgnoreCase) && n.Contains("another type than the record they override", StringComparison.Ordinal))) continue;
                if (n.Contains("can't be read", StringComparison.Ordinal) || n.Contains("another type than", StringComparison.Ordinal) || n.Contains("different type than", StringComparison.Ordinal) || n.Contains("more than once", StringComparison.Ordinal)) answer.Problem(n);
                else answer.Note(n);
            }
            Sources(answer, result, target);
            foreach (var r in result.Cost.Read) answer.Read(r);
            if (result.Cost.Read.Count == 0 && result.Cost.AlreadyRead.Count > 0) answer.Read($"nothing new ({string.Join(", ", result.Cost.AlreadyRead.Take(6))}{(result.Cost.AlreadyRead.Count > 6 ? $" and {result.Cost.AlreadyRead.Count - 6} more" : "")} already read)");
            // A query over 5 s says where the time went (trap 11)
            if (result.Cost.Seconds > 5 && result.Cost.Steps.Count > 0)
                answer.Note($"This took {result.Cost.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s ({result.Cost.QuerySeconds.ToString("0.0", CultureInfo.InvariantCulture)} s of it the SQL itself): {string.Join(", ", result.Cost.Steps)}.");

            if (result.Error != null)
            {
                if (result.Error == EngineLibs.RefusedMark) { answer.Refused(); return; }
                var (what, fix) = EngineLibs.ErrorParts(result.Error, result.Fix, "check the SQL; describe <type> lists a type's fields");
                answer.Fail(what, fix);
                return;
            }
            if (result.NotRun != null)
            {
                JobContext.NotRun("Not run. " + result.NotRun.What, result.NotRun.MemoryMB, result.NotRun.What);
                answer.Refused();
                return;
            }
            if (EngineAnswer.JsonMode)
            {
                // The rows as values, on one line: the caller's JSON form puts it under "data"
                var o = result.ToJson();
                o.Remove("notes"); o.Remove("source"); o.Remove("cost");
                answer.Line(o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                if (result.Offset + result.Rows.Count < result.TotalRows || result.Offset > 0)
                    answer.More = new MoreInfo { From = (int)result.Offset + 1, Shown = result.Rows.Count, Total = (int)Math.Min(int.MaxValue, result.TotalRows), Unit = "rows", Cursor = result.Offset + result.Rows.Count < result.TotalRows ? cursorPrefix + result.CursorAt(result.Offset + result.Rows.Count) : null };
                return;
            }
            string text = result.ToText(maxChars);
            var body = EngineLibs.BodyLines(text);
            foreach (var l in body) answer.Line(l);
            if (text.Contains("\n[notes] Long values were cut to fit", StringComparison.Ordinal))
                answer.Note("Long values were cut to fit; ask for that column alone, or use format json, to get them whole.");
            if (!result.HasResult) { answer.Line("The statements ran and gave no rows of their own (no SELECT came last)."); return; }
            int shown = Math.Max(0, body.Count - 2);
            answer.HeaderLines = Math.Min(2, body.Count);
            long after = result.Offset + shown;
            if (after < result.TotalRows || result.Offset > 0)
                answer.More = new MoreInfo { From = (int)result.Offset + 1, Shown = shown, Total = (int)Math.Min(int.MaxValue, result.TotalRows), Unit = "rows", Cursor = after < result.TotalRows ? cursorPrefix + result.CursorAt(after) : null };
        }
    }
}
