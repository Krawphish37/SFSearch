using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SFSearch.Query;
using SFSearch.Records;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// saved (tool sfs_saved, command-line verb saved): the stored queries of SFSearch.Query. list, show, check, run
    /// with named arguments, and save into the home's queries\ folder. The shipped ones travel with the program
    /// (queries\ beside SFSearch.exe); an add-on X.dll brings X.queries\ beside it in extensions\.
    /// (design\agent-first\ARCHITECTURE.md section 7; milestones M10 and M8.)
    /// </summary>
    public static class SavedTool
    {
        public const string Help = """
            saved list
            saved show <name>
            saved check [<name>]
            saved run <name> [parameter=value ...] [--plugins <plugin,...>] [--scope plugins|winners|versions]
                      [--rows N] [--cursor C] [--language <language>] [--max-chars N] [--allow-heavy] [--json]
            saved save <name> --file query.sql [--overwrite]
              The stored queries: SQL files with a short header that says what each answers, its parameters and its columns.

              list     every stored query: the shipped ones, yours (queries\ in the SFSearch home) and add-ons'
              show     one query: what it answers, its parameters, its columns, its SQL
              check    is a query's header in order, and does its SQL name real tables and columns? No plugin is read.
              run      runs one. Its parameters are given by name: saved run cobj plugin=kgcdoom.esm
                       The scope comes from the query's header unless --plugins or --scope says otherwise.
              save     checks a query file and keeps it as yours (queries\<name>.sql in the SFSearch home)
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...); 7 not run: heavy work that wasn't allowed;
              8 not enough memory free for heavy work that was allowed.
            """;

        static readonly string[] Known = { "action", "name", "args", "plugins", "scope", "rows", "cursor", "language", "max_chars", "allow_heavy", "sql", "overwrite" };
        static readonly VerbSpec Spec = new()
        {
            Verb = "saved", Positional = new[] { "action", "name", "args" }, Options = new[] { "plugins", "scope", "rows", "cursor", "language", "max_chars", "file", "sql" }, Flags = new[] { "allow_heavy", "overwrite" },
            Aliases = new(StringComparer.OrdinalIgnoreCase) { ["lang"] = "language", ["plugin"] = "plugins" },
        };

        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            var answer = new EngineAnswer { AsTool = true };
            if (a.Unknown(Known) is { Count: > 0 } wrong)
                answer.Fail($"saved has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", $"its arguments are: {string.Join(", ", Known)}", 2);
            else Guarded(a, answer, a.Text("sql"));
            answer.Emit();
        }

        public static void Verb(List<string> args)
        {
            if (args.Count == 0 || args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            var answer = new EngineAnswer();
            var given = Spec.Parse(args, answer);
            if (given != null)
            {
                var a = new ToolArgs(given);
                string? text = a.Text("sql");
                if (a.Text("file") is { } file)
                {
                    string path = Home.UserPath(file);
                    if (!File.Exists(path)) answer.Fail($"there is no file {path}", "check the path given with --file", 2);
                    else text = File.ReadAllText(path);
                }
                if (!answer.Failed) Guarded(a, answer, text);
            }
            answer.Emit();
        }

        static void Guarded(ToolArgs a, EngineAnswer answer, string? text)
        {
            try { Run(a, answer, text); }
            catch (QueryException e) { Fail(answer, e.Message, e.Fix); }
            catch (RecordException e) { answer.Fail(e.Message.Split('\n')[0], "ask again; loadorder2 problems lists what is wrong with a plugin"); }
            catch (FileNotFoundException e) { answer.Fail(e.Message.Split('\n')[0], "set the Data folder with SetData, or the SFSEARCH_DATA environment variable"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                answer.Fail($"the stored query couldn't be run ({e.Message.Split('\n')[0].TrimEnd('.')})", "ask again; saved check <name> says whether the query itself is in order");
            }
            finally
            {
                if (EngineRecords.TrimIfLarge() is { } trimmed) answer.After(trimmed);
            }
        }

        /// <summary>The library's error: "… Did you mean: a, b?" becomes the fix.</summary>
        static void Fail(EngineAnswer answer, string what, string? fix)
        {
            var (w, f) = EngineLibs.ErrorParts(what, fix, "saved list shows every stored query; saved show <name> shows one with its parameters");
            answer.Fail(w, f);
        }

        /// <summary>The named arguments of a run: an object ({"plugin": "x.esm"}), or text ("plugin=x.esm type=Weapon").</summary>
        static Dictionary<string, string>? Arguments(ToolArgs a, EngineAnswer answer)
        {
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (a.Element("args") is not { } e) return named;
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                    named[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                return named;
            }
            IEnumerable<string> parts = e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText())
                : Words.Split(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText());
            foreach (string part in parts)
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) { answer.Fail($"\"{part}\" isn't parameter=value", "give a stored query's arguments by name: plugin=kgcdoom.esm type=Weapon (the tool also takes an object: {\"plugin\": \"kgcdoom.esm\"})", 2); return null; }
                named[part[..eq].Trim()] = part[(eq + 1)..].Trim().Trim('"');
            }
            return named;
        }

        static void Run(ToolArgs a, EngineAnswer answer, string? text)
        {
            string action = (a.Text("action") ?? "list").ToLowerInvariant();
            string? name = a.Text("name");
            if (action is not ("list" or "show" or "check" or "run" or "save"))
            {
                // "saved cobj plugin=x.esm" is a run
                answer.Fail($"saved has no action '{action}'", "use list, show <name>, check [<name>], run <name> parameter=value, or save <name>", 2);
                return;
            }
            var eng = EngineIndex.Get();
            EngineRecords.TryLanguage(null, out var english);
            if (action == "list")
            {
                var stored = EngineLibs.Stored(EngineLibs.Query(eng, eng.Index, english));
                var lines = stored.ListText().TrimEnd('\n').Split('\n');
                foreach (var l in lines)
                {
                    if (l.StartsWith("[notes] ", StringComparison.Ordinal)) answer.Note(l[8..]); else answer.Line(l);
                }
                answer.Line($"Yours are kept in {EngineLibs.UserQueries}.");
                return;
            }
            if (action is "show" or "check" && name == null && action == "show") { answer.Fail("saved show needs the query's name", "saved list shows every stored query", 2); return; }
            if (action == "show")
            {
                var stored = EngineLibs.Stored(EngineLibs.Query(eng, eng.Index, english));
                var query = stored.Find(name!);
                foreach (string line in query.Describe(withSql: true).TrimEnd('\n').Split('\n'))
                {
                    // A shipped query's file is beside the program, and the service runs from a copy of the program in a
                    // temporary folder: that path means nothing to a reader, and changes at every service start
                    if (line.StartsWith("File: ", StringComparison.Ordinal) && query.Source == StoredQueries.Shipped)
                        answer.Line($"File: queries\\{System.IO.Path.GetFileName(query.File)}, shipped with SFSearch. To change it, save a copy under another name: saved save <name>.");
                    else answer.Line(line.TrimEnd('\r'));
                }
                return;
            }
            if (action == "check")
            {
                var stored = EngineLibs.Stored(EngineLibs.Query(eng, eng.Index, english));
                answer.Checked(eng.Check());
                var which = name != null ? new List<StoredQuery> { stored.Find(name) } : stored.All();
                int faulty = 0;
                foreach (var q in which)
                {
                    var check = stored.Check(q);
                    if (check.Ok) { answer.Line($"ok      {q.FullName}"); continue; }
                    faulty++;
                    answer.Line($"FAULTY  {q.FullName}");
                    foreach (string p in check.Problems) answer.Line($"        {p}");
                }
                answer.Line($"{which.Count - faulty:N0} of {which.Count:N0} stored queries are in order.");
                return;
            }
            if (name == null) { answer.Fail($"saved {action} needs the query's name", "saved list shows every stored query", 2); return; }
            if (action == "save")
            {
                if (string.IsNullOrWhiteSpace(text)) { answer.Fail("saved save got no query text", "pass the file's text as sql (command line: --file query.sql). saved show <name> shows the format of an existing one", 2); return; }
                var stored = EngineLibs.Stored(EngineLibs.Query(eng, eng.Index, english));
                answer.Checked(eng.Check());
                var done = stored.Save(name, text, a.Flag("overwrite"));
                if (!done.Ok)
                {
                    answer.Fail($"the query wasn't saved: {string.Join("; ", done.Problems.Select(p => p.TrimEnd('.')))}", "correct the text and save it again. queries\\README.md describes the header; saved show <name> shows a working one");
                    return;
                }
                answer.Line($"Saved as user.{name} in {done.File}.");
                if (done.Note != null) answer.Note(done.Note);
                return;
            }

            // ---- run
            var named = Arguments(a, answer);
            if (named == null) return;
            int rows = a.Int("rows") ?? 40, maxChars = Math.Max(RecordTool.LeastChars, a.Int("max_chars") ?? RecordTool.DefaultChars);
            answer.Checked(eng.Check());
            // The plugin a query is about may be a file outside Data: it is then read in an index of its own
            string? scopeText = a.Text("scope"), pluginsText = a.Text("plugins");
            var ix = eng.Index;
            var readFrom = new List<(SFSearch.Index.HeaderIndex Ix, SFSearch.Index.IndexedPlugin Plugin, string? ShownAs)>();
            if (named.TryGetValue("plugin", out var about) && (about.Contains('\\') || about.Contains('/')))
            {
                var side = RecordPick.Plugin(eng, about, answer);
                if (side == null) return;
                if (side.Value.ByPath) { ix = side.Value.Ix; readFrom.Add((ix, side.Value.Plugin, side.Value.Plugin.Path)); }
                named["plugin"] = side.Value.Plugin.Name;
            }
            QueryTool.Target? target = null;
            if (pluginsText != null || scopeText != null)
            {
                target = QueryTool.Resolve(eng, pluginsText, scopeText, english, answer, "saved run", defaultWinners: false);
                if (target == null) return;
                ix = target.Ix;
                readFrom.AddRange(target.ReadFrom);
            }
            else if (named.TryGetValue("plugin", out var p) && ix.Plugin(p) is { } known && readFrom.Count == 0) readFrom.Add((ix, known, null));
            if (!ProblemNotes.Add(answer, readFrom, required: true)) return;
            var engine = EngineLibs.Query(eng, ix, english);
            var storedQueries = EngineLibs.Stored(engine);
            var run = new StoredRunRequest
            {
                Name = name, Arguments = named, Scope = target?.Scope, Plugins = target?.Plugins ?? new List<string>(),
                AllowHeavy = a.Flag("allow_heavy") || JobContext.HeavyAllowed, PageRows = rows, Cursor = a.Text("cursor")?.Trim('"'), MaxChars = maxChars, Language = a.Text("language"),
            };
            var result = storedQueries.Run(run);
            foreach (var n in result.Notes) answer.Note(n);
            if (result.Error != null)
            {
                if (result.Error == EngineLibs.RefusedMark) { answer.Refused(); return; }
                Fail(answer, result.Error, result.Fix);
                return;
            }
            if (result.NotRun != null)
            {
                JobContext.NotRun("Not run. " + result.NotRun.What, result.NotRun.MemoryMB, result.NotRun.What);
                answer.Refused();
                return;
            }
            var shownTarget = target ?? new QueryTool.Target { Engine = engine, Ix = ix, ReadFrom = readFrom, Scope = result.Scope };
            if (EngineAnswer.JsonMode)
            {
                foreach (var t in result.Tables) Parts(answer, t.Answer, shownTarget);
                var o = result.ToJson();
                answer.Line(o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                return;
            }
            // Which query ran, then each table. The first table that is cut gets the [more] line; another cut table says so in a note
            string head = result.ToText(maxChars).Split('\n')[0];
            if (head.StartsWith("[query] ", StringComparison.Ordinal)) answer.Line(head[8..]);
            int each = Math.Max(2000, maxChars / Math.Max(1, result.Tables.Count));
            foreach (var t in result.Tables)
            {
                bool heading = result.Tables.Count > 1 || t.Heading != null;
                if (heading) answer.Line("## " + (t.Heading ?? $"Table {t.Number}"));
                var one = new EngineAnswer();
                QueryTool.Render(one, t.Answer, each, shownTarget, $"t{t.Number}:");
                Parts(answer, t.Answer, shownTarget);
                foreach (var l in EngineLibs.BodyLines(t.Answer.ToText(each))) answer.Line(l);
                if (t.Answer.Ok && t.Answer.HasResult && t.Answer.TotalRows == 0) answer.Line("(no rows)");
                if (one.More is { } more)
                {
                    if (answer.More == null) answer.More = more;
                    else answer.Note($"{t.Heading ?? $"Table {t.Number}"} is cut too: {more.Shown:N0} of {more.Total:N0} rows shown.{(more.Cursor != null ? $" Its next rows: cursor \"{more.Cursor}\"." : "")}");
                }
                if (t.Answer.Error != null) answer.Line($"Error: {t.Answer.Error.TrimEnd('.')}.{(t.Answer.Fix != null ? " Fix: " + t.Answer.Fix.TrimEnd('.') + "." : "")}");
            }
            if (result.Tables.Count == 0) answer.Line("The query gave no table: none of its statements ran with these arguments (saved show <name> says which argument each table needs).");
        }

        /// <summary>The notes, sources and reads of one table.</summary>
        static void Parts(EngineAnswer answer, QueryAnswer t, QueryTool.Target target)
        {
            foreach (var n in t.Notes)
            {
                if (n.Contains("can't be read", StringComparison.Ordinal) || n.Contains("another type than", StringComparison.Ordinal) || n.Contains("different type than", StringComparison.Ordinal) || n.Contains("more than once", StringComparison.Ordinal)) answer.Problem(n);
                else answer.Note(n);
            }
            QueryTool.Sources(answer, t, target);
            foreach (var r in t.Cost.Read) answer.Read(r);
            if (t.Cost.Read.Count == 0 && t.Cost.AlreadyRead.Count > 0) answer.Read($"nothing new ({string.Join(", ", t.Cost.AlreadyRead.Take(6))}{(t.Cost.AlreadyRead.Count > 6 ? $" and {t.Cost.AlreadyRead.Count - 6} more" : "")} already read)");
        }
    }
}
