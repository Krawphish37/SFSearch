using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// compare (tool sfs_compare, command-line verb compare): the five kinds of comparison of SFSearch.Compare, on the
    /// engine's own header index and record engine. Nothing is stored.
    /// (design\agent-first\ARCHITECTURE.md sections 7 and 9; milestones M7 and M8.)
    /// </summary>
    public static class CompareTool
    {
        public static readonly string[] Kinds = { "versions", "records", "plugin_vs_masters", "plugins", "patch_vs_reference" };

        public const string Help = """
            compare versions <record> [--plugin <plugin>]
            compare records <a: plugin> <record> <b: plugin> [<record_b>] [--expect <field,...>]
            compare plugin_vs_masters <a: plugin> [<filter>]
            compare plugins <a> <b> [--match formkey|editorid] [--as <plugin name>]
            compare patch_vs_reference <a> <b> [--match formkey|editorid]
                    [--detail] [--max-chars N] [--cursor C] [--language <language>] [--json]
              Comparisons, read live from the plugin files. No database is read or built.

              versions            one record across the load order: who holds it, who wins, what each changes against the one before
              records             two records. --expect lists the fields allowed to differ: any other difference fails (exit code 1).
                                  Two FormKeys alone work too: compare records 219447:Starfield.esm 0082B0:darkstar.esm
              plugin_vs_masters   what a plugin changes in its masters; overrides that change nothing (ITMs) and overrides of nothing listed.
                                  An ITM holds its master's data; its row names the record header's numbers that differ (form version, version control)
                                  A filter keeps records whose EditorID holds the text, or one FormKey, or one type (WEAP)
              plugins             two versions of one mod (a file in Data against a copy elsewhere), or a patch against the plugin it masters.
                                  --match editorid pairs a plugin's own records by type and EditorID; repeated EditorIDs are listed, not dropped.
                                  --as reads a file under another plugin name on purpose (a renamed master)
              patch_vs_reference  two patches for the same masters: same change, only in a, only in b, different

              A plugin is a file name in Data or the path of a plugin file. A record is a FormKey, an EditorID, or a FormID with its plugin.
              --detail   every difference on a line of its own with both values, and the record header's own numbers
              A translated text counts in every language: a language dropped or added, or no text against an empty one,
              is a change of that field. A field that is the same value written another way is listed too, and says so.
              Exit code: 0 answered; 1 answered, and compare records with --expect found a difference outside the expected
              fields; 2 an error (one line, Error: ... Fix: ...); 7 not run: heavy work that wasn't allowed; 8 not enough
              memory free for it.
            """;

        static readonly string[] Known = { "kind", "a", "b", "record", "record_b", "plugin", "expect", "match", "as", "filter", "detail", "max_chars", "cursor", "language" };
        static readonly VerbSpec Spec = new()
        {
            Verb = "compare", Positional = new[] { "kind", "p1", "p2", "p3", "p4" }, Options = new[] { "a", "b", "record", "record_b", "plugin", "expect", "match", "as", "filter", "max_chars", "cursor", "language" },
            Flags = new[] { "detail" }, Aliases = new(StringComparer.OrdinalIgnoreCase) { ["lang"] = "language", ["in"] = "plugin" }, JoinRest = false,
        };

        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            var answer = new EngineAnswer { AsTool = true };
            if (a.Unknown(Known) is { Count: > 0 } wrong)
                answer.Fail($"compare has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", $"its arguments are: {string.Join(", ", Known)}", 2);
            else Guarded(a, answer);
            answer.Emit();
        }

        public static void Verb(List<string> args)
        {
            if (args.Count == 0 || args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            var answer = new EngineAnswer();
            var given = Spec.Parse(args, answer);
            if (given != null)
            {
                // The words after the kind, in the order each kind takes them
                string kind = (given.TryGetValue("kind", out var k) ? k.GetString() ?? "" : "").ToLowerInvariant().Replace('-', '_');
                string[] order = kind switch
                {
                    "versions" => new[] { "record", "plugin" },
                    "records" => new[] { "a", "record", "b", "record_b" },
                    "plugin_vs_masters" => new[] { "a", "filter" },
                    _ => new[] { "a", "b" },
                };
                var words = new[] { "p1", "p2", "p3", "p4" }.Where(given.ContainsKey).Select(p => given[p]).ToList();
                foreach (var p in new[] { "p1", "p2", "p3", "p4" }) given.Remove(p);
                // compare records <FormKey> <FormKey>: two records, each in its own plugin
                if (kind == "records" && words.Count == 2) order = new[] { "a", "b" };
                var free = order.Where(o => !given.ContainsKey(o)).ToList();
                if (words.Count > free.Count) answer.Fail($"compare {kind} got more than it takes", "compare --help shows what each kind takes; put a value with spaces in quotes", 2);
                else
                {
                    for (int i = 0; i < words.Count; i++) given[free[i]] = words[i];
                    Guarded(new ToolArgs(given), answer);
                }
            }
            answer.Emit();
        }

        static void Guarded(ToolArgs a, EngineAnswer answer)
        {
            try { Run(a, answer); }
            catch (CompareException e) { Fail(answer, e.Message, e.Fix); }
            catch (RecordException e) { answer.Fail(e.Message.Split('\n')[0], "ask again; loadorder2 problems lists what is wrong with a plugin"); }
            catch (FileNotFoundException e) { answer.Fail(e.Message.Split('\n')[0], "set the Data folder with SetData, or the SFSEARCH_DATA environment variable"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                answer.Fail($"the comparison couldn't be made ({e.Message.Split('\n')[0].TrimEnd('.')})", "ask again; if it stays, loadorder2 problems shows which plugin is at fault");
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException and not System.Threading.ThreadAbortException)
            {
                RecordTool.FailMutagen(answer, e);
            }
            finally
            {
                if (EngineRecords.TrimIfLarge() is { } trimmed) answer.After(trimmed);
            }
        }

        static void Fail(EngineAnswer answer, string what, string? fix)
        {
            // A name that fits several records: the library lists them in its message. It stays one line.
            answer.Fail(what.Replace("\r", "").Replace("\n", "; "), fix ?? "check the names: find shows what a FormID or EditorID is, loadorder2 lists the plugins");
        }

        static bool IsFormKey(string? text) => text != null && HeaderIndex.TryParseFormKey(text.Trim(), out var owner, out _)
            && (owner.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || owner.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || owner.EndsWith(".esl", StringComparison.OrdinalIgnoreCase));

        static void Run(ToolArgs a, EngineAnswer answer)
        {
            string kind = (a.Text("kind") ?? "").ToLowerInvariant().Replace('-', '_');
            if (!Kinds.Contains(kind))
            {
                answer.Fail(kind.Length == 0 ? "compare got no kind" : $"there is no kind of comparison named '{a.Text("kind")}'", $"use one of: {string.Join(", ", Kinds)}", 2);
                return;
            }
            if (!EngineLibs.Language(a.Text("language"), answer, out var language)) return;
            MatchBy match = MatchBy.FormKey;
            if (a.Text("match") is { } m)
            {
                if (m.Equals("editorid", StringComparison.OrdinalIgnoreCase)) match = MatchBy.EditorId;
                else if (!m.Equals("formkey", StringComparison.OrdinalIgnoreCase)) { answer.Fail($"there is no way of pairing named '{m}'", "use match formkey (the default) or editorid", 2); return; }
            }
            int maxChars = Math.Max(RecordTool.LeastChars, a.Int("max_chars") ?? RecordTool.DefaultChars);
            bool detail = a.Flag("detail");
            string question = string.Join("|", Known.Where(k => k is not ("cursor" or "max_chars")).Select(k => k + "=" + a.Text(k)));
            if (!Cursors.TryRead(a.Text("cursor"), question, out int start))
            { answer.Fail($"the cursor \"{a.Text("cursor")}\" doesn't belong to this comparison", "pass a cursor together with the same arguments that gave it, or leave it out to start at the top", 2); return; }

            var eng = EngineIndex.Get();
            answer.Checked(eng.Check());
            using var session = EngineLibs.Compare(eng, language);
            var compare = new Comparer(session);
            string? pa = a.Text("a"), pb = a.Text("b"), record = a.Text("record"), recordB = a.Text("record_b");
            CompareAnswer result;
            switch (kind)
            {
                case "versions":
                {
                    record ??= pa; // "a" may hold the record when only one thing is named
                    if (record == null) { answer.Fail("compare versions needs a record", "give record: a FormKey (2377CF:Starfield.esm) or an EditorID", 2); return; }
                    string? inPlugin = a.Text("plugin");
                    // Which record is meant goes by the engine's rule: one loaded record of that EditorID is picked, several are listed
                    var picked = RecordPick.Resolve(eng, record, null, answer, "compare");
                    if (picked == null) return;
                    result = compare.Versions(picked.FormKey, inPlugin);
                    break;
                }
                case "records":
                {
                    // Two FormKeys alone: each record in its own plugin
                    if (record == null && IsFormKey(pa) && IsFormKey(pb))
                    {
                        record = pa; recordB = pb;
                        HeaderIndex.TryParseFormKey(pa!.Trim(), out var oa, out _); HeaderIndex.TryParseFormKey(pb!.Trim(), out var ob, out _);
                        pa = oa; pb = ob;
                    }
                    if (pa == null || pb == null || record == null)
                    {
                        answer.Fail("compare records needs two plugins and a record", "give a (the first plugin), record (the record in it), b (the second plugin) and, when it is another record there, record_b. Two FormKeys as a and b work too", 2);
                        return;
                    }
                    result = compare.Records(pa, record, pb, recordB, a.Text("expect")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                }
                case "plugin_vs_masters":
                {
                    if (pa == null) { answer.Fail("compare plugin_vs_masters needs a plugin", "give a: the plugin whose changes to its masters you want", 2); return; }
                    string? filter = a.Text("filter") ?? record;
                    result = compare.PluginVsMasters(pa, filter?.Trim('*'));
                    break;
                }
                case "plugins":
                    if (pa == null || pb == null) { answer.Fail("compare plugins needs two plugin files", "give a and b: two versions of one mod, or a plugin and a patch that masters it", 2); return; }
                    result = compare.Plugins(pa, pb, match, a.Text("as"));
                    break;
                default:
                    if (pa == null || pb == null) { answer.Fail("compare patch_vs_reference needs two patches", "give a (the patch) and b (the reference patch for the same masters)", 2); return; }
                    result = compare.PatchVsReference(pa, pb, match);
                    break;
            }
            EngineLibs.Parts(answer, result.Notes, result.Sources, result.Read);
            if (EngineAnswer.JsonMode)
            {
                var o = result.ToJson();
                o.Remove("notes"); o.Remove("source"); o.Remove("cost");
                string json = o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                int most = Math.Max(maxChars, 200_000);
                if (json.Length > most)
                { answer.Fail($"the structured form of this comparison is {json.Length:N0} characters, more than the {most:N0} allowed", "ask for the text form, which comes in pages; or narrow the comparison (a filter, one record); or raise max_chars", 2); return; }
                answer.Line(json);
            }
            else
            {
                string text = result.ToText(new TextOptions { Detail = detail, MaxChars = maxChars, Cursor = start }, out int next);
                EngineLibs.Paged(answer, text, start, next, question);
            }
            if (result.Failed) Environment.ExitCode = 1;
        }
    }
}
