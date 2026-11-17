using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// record (tool sfs_record, command-line verb record): one record, read live through Mutagen by the record layer
    /// (SFSearch.Records). Seven views: the card (set fields, readable names, links labelled), named fields with their
    /// defaults, the JSON, the raw subrecords, every version in the load order, evidence for a field (the file's bytes
    /// beside the Mutagen field and the xEdit path), and the one-record verify. No database is read or built.
    /// (design\agent-first\ARCHITECTURE.md sections 5 to 7 and 10; milestone M5, second half.)
    /// </summary>
    public static class RecordTool
    {
        public const int DefaultChars = 24_000, LeastChars = 1_000, MostChars = 8_000_000;
        static readonly string[] Views = { "card", "fields", "json", "raw", "versions", "evidence", "verify" };

        public const string Help = """
            record <record> [--plugin <plugin>] [--view card|fields|json|raw|versions|evidence|verify] [--fields <name,...>]
                   [--max-chars N] [--cursor C] [--language <language>] [--allow-heavy | --allow-heavy=save-index]
              One record, read live from the plugin file through Mutagen. No database is read or built.

              record      a FormKey (0476C4:Starfield.esm), an EditorID (Eon), or a FormID with --plugin (0003C2).
                          A name that fits more than one record lists them all and picks none.
                          A plugin's name (darkstar.esm), or a plugin file's path, is its plugin header (TES4): its flags,
                          HEDR (version, record count, next object ID), masters, author and the rest. The card, fields,
                          json, raw and evidence views take it (evidence: Flags, Stats.NumRecords, MasterReferences[0].Master).
              --plugin    which plugin's version to read: a name in Data, or the path of a plugin file elsewhere.
                          Left out: the version the game uses (the last loaded plugin that holds the record). The answer says which.
              --view      card      (default) the fields that are set, with readable names; links as EditorID (FormID:plugin)
                          fields    the fields named with --fields: their values, with the default for a field the record leaves out
                                    (a name is a Mutagen path: AttackDamage, Keywords[0], Components[*].SpaceshipAIActor)
                          json      the record as Mutagen's JSON: every value, field names as SQL and paths use them
                          raw       the record as the file has it: its header and every subrecord's bytes
                          versions  every plugin that holds the record, in load order, the winner marked
                          evidence  for the fields named with --fields: the file's bytes beside the Mutagen field and the xEdit path
                          verify    the JSON turned back into a record, written by Mutagen, compared with the file's bytes
              --max-chars the answer is cut to about this many characters (default 24,000). The [more] line gives the cursor for the rest.
              --language  the language of translated text (default English)
              --allow-heavy  read a record of over 16 MB whole (its full card, json or verify), at once. Without it such a
                          record is read in parts: the card and json views give the whole card or JSON a part at a time
                          (each item of a big list is a part; the cursor goes on), verify checks it part by part, and the
                          fields and evidence views read only the fields named (a big list a page at a time).
              --allow-heavy=save-index  the same as --allow-heavy here: no saved index would make a big record faster,
                          and the answer says so. (MCP: allow_heavy "save_index")
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...); 7 not run: a heavy record that wasn't allowed;
              8 not enough memory free for a heavy record that was allowed.
            """;

        static readonly string[] Known = { "record", "plugin", "view", "fields", "max_chars", "cursor", "language", "allow_heavy" };
        /// <summary>Why no saved index makes one huge record's whole view faster (J33): its own data is the answer.</summary>
        const string RecordNoIndex = "it is one record's own data, which is never kept";

        sealed class Ask
        {
            public string Record = "";
            public string? Plugin, View, Fields, Cursor, Language;
            public int MaxChars = DefaultChars;
        }

        // ---------------------------------------------------------------- Entry points
        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            var answer = new EngineAnswer { AsTool = true };
            if (a.Unknown(Known) is { Count: > 0 } wrong)
                answer.Fail($"record has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", $"its arguments are: {string.Join(", ", Known)}", 2);
            else
                Guarded(new Ask { Record = a.Text("record") ?? "", Plugin = a.Text("plugin"), View = a.Text("view"), Fields = a.Text("fields"), Cursor = a.Text("cursor"), Language = a.Text("language"), MaxChars = a.Int("max_chars") ?? DefaultChars }, answer);
            answer.Emit();
        }

        public static void Verb(List<string> args)
        {
            if (args.Count == 0 || args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            var ask = new Ask
            {
                Plugin = CommandLine.TakeOption(args, "--plugin"), View = CommandLine.TakeOption(args, "--view"), Fields = CommandLine.TakeOption(args, "--fields"),
                Cursor = CommandLine.TakeOption(args, "--cursor"), Language = CommandLine.TakeOption(args, "--language") ?? CommandLine.TakeOption(args, "--lang"),
            };
            var answer = new EngineAnswer();
            string? max = CommandLine.TakeOption(args, "--max-chars");
            var stray = args.Where(x => x.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (max != null && !int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out ask.MaxChars))
                answer.Fail($"--max-chars takes a number, not '{max}'", "give the number of characters, e.g. --max-chars 60000", 2);
            else if (stray.Count > 0)
                answer.Fail($"record has no option {string.Join(", ", stray)}", "its options are --plugin, --view, --fields, --max-chars, --cursor, --language and --allow-heavy (record --help shows them)", 2);
            else
            {
                ask.Record = string.Join(" ", args);
                Guarded(ask, answer);
            }
            answer.Emit();
        }

        static void Guarded(Ask ask, EngineAnswer answer)
        {
            try { Run(ask, answer); }
            catch (RecordException e) { FailRecord(answer, e); }
            catch (FileNotFoundException e) { answer.Fail(e.Message.Split('\n')[0], "set the Data folder with SetData, or the SFSEARCH_DATA environment variable"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                answer.Fail($"the record couldn't be read ({e.Message.TrimEnd('.')})", "ask again; if it stays, run loadorder2 problems to see which plugin is at fault");
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException and not System.Threading.ThreadAbortException)
            {
                // Mutagen's own reader gave up on the record (it wraps the cause in an exception of its own): one line, no stack trace
                RecordTool.FailMutagen(answer, e);
            }
            finally
            {
                if (EngineRecords.TrimIfLarge() is { } trimmed) answer.After(trimmed);
            }
        }

        /// <summary>An error out of Mutagen itself: the record's bytes are something its reader doesn't expect.</summary>
        internal static void FailMutagen(EngineAnswer answer, Exception e) =>
            answer.Fail($"Mutagen couldn't read the record: {Cause(e)}", "view raw shows the record's bytes as the file has them. The file writes something Mutagen's reader doesn't expect: it is a fault of the plugin or of Mutagen, worth telling the Mutagen session");

        /// <summary>A report that failed inside Mutagen (a plugin or an archive it read), said as the report's failure, not
        /// a record's: archive2 extract of a texture used to say "couldn't read the record … view raw" (found in the fixes.16 move).</summary>
        internal static void FailMutagenReport(EngineAnswer answer, Exception e) =>
            answer.Fail($"Mutagen couldn't read what the report needed: {Cause(e)}", "the file it was reading (a plugin or an archive) holds something Mutagen's reader doesn't expect: a fault of the file or of Mutagen, worth telling the Mutagen session; loadorder2 problems shows what is wrong with a plugin");

        /// <summary>The exception in one line: its type and first line, and its innermost cause's when that says more.</summary>
        static string Cause(Exception e)
        {
            var cause = e;
            while (cause.InnerException != null) cause = cause.InnerException;
            string first = e.Message.Split('\n')[0].Trim().TrimEnd('.');
            return ReferenceEquals(cause, e) || first.Contains(cause.Message.Split('\n')[0].Trim().TrimEnd('.'), StringComparison.Ordinal) ? $"{cause.GetType().Name}: {first}" : $"{first} ({cause.GetType().Name}: {cause.Message.Split('\n')[0].Trim().TrimEnd('.')})";
        }

        /// <summary>The record layer says what is wrong in one line; the fix depends on what it is.</summary>
        internal static void FailRecord(EngineAnswer answer, RecordException e)
        {
            string m = e.Message.Trim();
            if (m.Contains("changed during the read", StringComparison.Ordinal))
                answer.Fail(m[..m.IndexOf(':')], "run it again");
            else if (m.Contains("Did you mean", StringComparison.Ordinal) || m.Contains("has no field", StringComparison.Ordinal) || m.Contains("isn't a field name", StringComparison.Ordinal) || m.Contains("isn't a list", StringComparison.Ordinal) || m.Contains("there is no [", StringComparison.Ordinal) || m.Contains("is a plain value", StringComparison.Ordinal))
            {
                // A wrong field name is worded as describe and query word it: the nearest real names, chosen the same way
                var (what, fix) = EngineLibs.ErrorParts(m, m.Contains(" Did you mean", StringComparison.Ordinal) ? "the card view shows every field that is set, with its Mutagen name" : null,
                    "use a Mutagen field path, as the card and the json view show them (AttackDamage, Keywords[0])");
                answer.Fail(what, fix);
            }
            else if (m.Contains("master", StringComparison.OrdinalIgnoreCase) && m.Contains("isn't there", StringComparison.Ordinal))
                answer.Fail(m, "put the master in the Data folder, then ask again");
            else answer.Fail(m, "the raw view shows the record's bytes as the file has them; loadorder2 problems lists what is wrong with a plugin");
        }

        // ---------------------------------------------------------------- Run
        static void Run(Ask ask, EngineAnswer answer)
        {
            string view = (ask.View ?? (ask.Fields != null ? "fields" : "card")).Trim().ToLowerInvariant();
            if (!Views.Contains(view)) { answer.Fail($"record has no view '{ask.View}'", $"its views are: {string.Join(", ", Views)}", 2); return; }
            var fields = (ask.Fields ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (view is "fields" or "evidence" && fields.Count == 0)
            {
                answer.Fail($"the {view} view needs the fields to show", "name them with fields, e.g. fields \"AttackDamage, Keywords[0]\" (the card view shows every field that is set, with its Mutagen name)", 2);
                return;
            }
            if (!EngineRecords.TryLanguage(ask.Language, out var language))
            {
                answer.Fail($"'{ask.Language}' isn't a language SFSearch knows", $"use one of the languages Starfield ships: {SFSearch.Query.TextLanguages.ShippedText}", 2);
                return;
            }
            int maxChars = ask.MaxChars <= 0 ? DefaultChars : Math.Clamp(ask.MaxChars, LeastChars, MostChars);
            string question = $"{ask.Record.Trim()}|{ask.Plugin?.Trim()}|{view}|{string.Join(",", fields)}|{language}".ToLowerInvariant();
            // A heavy record's json and card go a part at a time, and a part longer than a page goes on at a line of it:
            // that cursor is "part-line.mark" (HeavyRecordView)
            int line = 0;
            if (!HeavyRecordView.TryCursor(ask.Cursor, question, out int offset, out line) && !Cursors.TryRead(ask.Cursor, question, out offset))
            {
                answer.Fail($"the cursor \"{ask.Cursor}\" doesn't belong to this question", "ask again with the same record, plugin, view and fields as the answer that gave the cursor, or leave the cursor out to start over", 2);
                return;
            }

            var eng = EngineIndex.Get();
            var validity = eng.Check();
            answer.Checked(validity);

            // A plugin's name (or a plugin file's path) names its header, TES4 (GAPS S88)
            if (RecordHeaderView.Names(ask.Record))
            {
                RecordHeaderView.Run(eng, ask.Record, ask.Plugin, view, fields, language, offset, maxChars, question, answer);
                return;
            }
            if (ask.Record.Trim().Trim('"', '\'').Equals("TES4", StringComparison.OrdinalIgnoreCase) && eng.Index.FindEditorId("TES4").Count == 0)
            {
                string example = string.IsNullOrWhiteSpace(ask.Plugin) ? "darkstar.esm" : ask.Plugin.Trim();
                answer.Fail("no record has the EditorID \"TES4\": the plugin header (TES4) has no EditorID or FormID", $"name the header by its plugin's name: record {example}", 2);
                return;
            }

            for (int attempt = 0; ; attempt++)
            {
                var pick = RecordPick.Resolve(eng, ask.Record, ask.Plugin, answer, "record");
                if (pick == null) return;
                // Problems of the plugin the record is read from lead the notes; one that can't be read at all is an error
                if (!ProblemNotes.Add(answer, new[] { (pick.Ix, pick.Plugin, pick.ByPath ? (string?)pick.Plugin.Path : null) }, required: true)) return;
                var schema = SchemaTypes.Default;
                var info = pick.Info;

                if (view == "versions")
                {
                    VersionsView(eng, pick, answer, schema, offset, maxChars, question);
                    Sources(eng, pick, answer, withLoadOrder: true);
                    return;
                }

                var engine = EngineRecords.For(eng, pick.Ix, language);
                int opened = engine.Sessions.Opens;
                var use = engine.Sessions.Use(pick.Plugin.Name);
                foreach (var n in use.Notes) answer.Note(n);
                if (use.Refresh is { AnythingChanged: true } && attempt == 0)
                {
                    // A plugin changed between the check and the read: the index looked again, so the record is looked up again
                    answer.Checked(eng.Check());
                    continue;
                }
                bool justOpened = engine.Sessions.Opens > opened;

                // ---- A heavy record says its cost first, and runs only when the caller allows it, through the heavy lane
                long bytes = engine.DataBytes(pick.Ref);
                bool heavy = bytes > engine.HeavyRecordBytes && view != "raw";
                // A heavy record read in part (GAPS S7): the fields and evidence views always; the card, json and verify
                // views a part at a time unless the caller allowed the whole record at once. Only the subrecords of a
                // part go through Mutagen.
                if (heavy && (view is "fields" or "evidence" || view is "card" or "json" or "verify" && !JobContext.HeavyAllowed))
                {
                    HeavyRecordView.Run(engine, eng, pick, view, fields, offset, line, maxChars, question, answer, justOpened);
                    if (answer.Failed) return;
                    // A plugin that changed while it was read makes the answer untrustworthy: the command fails
                    engine.Sessions.Confirm(use);
                    Sources(eng, pick, answer, withLoadOrder: ask.Plugin == null);
                    return;
                }
                if (heavy)
                {
                    // Measured on Starfield.esm's 51.4 MB navigation mesh info map. Through this tool, in a service worker:
                    // json 4.6 GB, verify 4.3 GB. The card was measured by M3 alone (10.3 GB); the tool also holds its text
                    // as lines, so it is given more.
                    int perMB = view switch { "json" => 95, "verify" => 90, _ => 250 };
                    int estimateMB = (int)Math.Min(int.MaxValue / 2, Math.Max(ServiceConfig.Load(Home.Dir).HeavyMinMB, bytes / 1048576.0 * perMB));
                    string what = $"{ProblemNotes.Label(info)} is a {info.Signature} of {EngineIndex.Mb(bytes)}. Read through Mutagen, its {view} view";
                    if (!JobContext.RequireHeavy(estimateMB, what, RecordNoIndex))
                    {
                        answer.Refused();
                        answer.Note($"{ProblemNotes.Label(info)} can be read without that cost, a part at a time: leave allow_heavy out and the card, json and verify views go through the record a part at a time (with the cursor), the fields and evidence views read only the fields named, and the raw view gives its bytes.");
                        Sources(eng, pick, answer, withLoadOrder: ask.Plugin == null);
                        return;
                    }
                }

                if (heavy) IndexOffers.NothingToSave(answer, RecordNoIndex);
                var lines = new List<string>();
                int head;
                bool wasHeavy = engine.AllowHeavy;
                engine.AllowHeavy = heavy || wasHeavy;
                try
                {
                    string readFrom = ReadFrom(eng, pick);
                    if (view == "raw")
                    {
                        lines.AddRange(Split(engine.RawText(pick.Ref, 64)));
                        if (lines.Count > 0) lines[0] = ProblemNotes.Label(info);
                        lines.Insert(Math.Min(2, lines.Count), readFrom);
                        head = Math.Min(3, lines.Count);
                        answer.Read($"the bytes of 1 {info.Signature} of {pick.Plugin.Name}");
                    }
                    else
                    {
                        var rec = engine.Load(pick.Ref);
                        string typeLine = $"{info.Signature} {rec.Type} in {info.Plugin}{(info.IsOverride ? " (an override)" : " (its own record)")}{(info.Parent == null ? "" : ", inside " + engine.Label(info.Parent))}, at 0x{info.Offset:X}, {info.Size.ToString("N0", CultureInfo.InvariantCulture)} bytes{((info.Flags & RawRecord.CompressedFlag) != 0 ? ", compressed" : "")}{StoredForm(engine, pick)}";
                        lines.Add(ProblemNotes.Label(info)); lines.Add(typeLine); lines.Add(readFrom);
                        head = 3;
                        switch (view)
                        {
                            case "card":
                                {
                                    var json = engine.Json(rec);
                                    var card = engine.Card(rec, json, withDefaults: true);
                                    foreach (var n in card.Notes) answer.Note(n);
                                    foreach (var n in engine.TextNotes(rec, new JsonNode?[] { json })) answer.Note(n);
                                    card.Notes.Clear();
                                    // Every item of every list: the answer is cut by size, with a cursor, not by a count of items
                                    lines.AddRange(Split(card.ToText(int.MaxValue, 64)).Skip(2));
                                    if (rec.Children > 0) answer.Note($"{rec.Children:N0} record(s) sit inside this one. Each is shown as \"→ EditorID (FormID:plugin)\" and is a record of its own: ask for it by its FormKey.");
                                    break;
                                }
                            case "json":
                                {
                                    var json = engine.Json(rec);
                                    lines.AddRange(Split(json.ToJsonString(Pretty)));
                                    foreach (var n in engine.TextNotes(rec, new JsonNode?[] { json })) answer.Note(n);
                                    if (rec.Children > 0) answer.Note($"{rec.Children:N0} record(s) sit inside this one. Each is a {{\"$Child\": \"FormID:plugin\"}} marker here and a record of its own: ask for it by its FormKey.");
                                    break;
                                }
                            case "fields":
                                {
                                    var values = engine.Fields(rec, fields);
                                    foreach (var v in values)
                                        foreach (var l in Split(v.ToText())) lines.Add("  " + l);
                                    foreach (var n in engine.TextNotes(rec, values.Select(v => v.Json))) answer.Note(n);
                                    break;
                                }
                            case "evidence":
                                foreach (var f in fields)
                                    lines.AddRange(Split(engine.Evidence(rec, f).ToText()));
                                foreach (var n in engine.TextNotes(rec, engine.Fields(rec, fields).Select(v => v.Json))) answer.Note(n);
                                break;
                            case "verify":
                                {
                                    var v = engine.Verify(new[] { pick.Ref })[0];
                                    lines.AddRange(Split(v.ToText()).Select(l => "  " + l));
                                    if (!v.Equal) answer.Note("The record's JSON doesn't write back to the file's bytes: something of the record isn't in its JSON, or Mutagen writes it differently. The first difference is above.");
                                    break;
                                }
                        }
                        answer.Read($"1 {info.Signature} of {pick.Plugin.Name}{(rec.Route == ReadRoute.Direct ? justOpened ? $" (from its own bytes; {pick.Plugin.Name} was opened for it)" : " (from its own bytes)" : justOpened ? $" ({pick.Plugin.Name} was opened for it)" : "")}");
                    }
                    // A plugin that changed while it was read makes the answer untrustworthy: the command fails
                    engine.Sessions.Confirm(use);
                }
                finally { engine.AllowHeavy = wasHeavy; }

                if (offset > 0 && offset >= lines.Count - head)
                {
                    answer.Fail($"the cursor points past the end ({lines.Count - head:N0} lines)", "leave the cursor out to start over", 2);
                    return;
                }
                answer.Lines(lines, head, offset, maxChars, next => Cursors.Make(next, question));
                Sources(eng, pick, answer, withLoadOrder: ask.Plugin == null);
                return;
            }
        }

        static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>For the card's type line: how the record's own FormID is stored, where the form can vary (GAPS S39):
        /// an own record of a Small or Medium plugin (the full index or the size's form), or an own record of a Full plugin
        /// stored in the Small or Medium form. Nothing for an override or a Full plugin's full index.</summary>
        static string StoredForm(RecordEngine engine, Picked pick)
        {
            var info = pick.Info;
            if (info.IsOverride || info.Unresolved) return "";
            var style = pick.Plugin.Header?.Style ?? MasterStyle.Full;
            Span<byte> b = stackalloc byte[4];
            engine.Sessions.Get(info.Plugin).ReadAt(info.Offset + 12, b);
            uint raw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b);
            var form = FormIdForms.FormOf(raw);
            if (style == MasterStyle.Full && form == SFSearch.Records.StoredForm.FullIndex) return "";
            return $", its FormID stored as {raw:X8} ({FormIdForms.Name(form)}{(FormIdForms.Allowed(form, style) ? "" : $", which a {style} plugin's header doesn't give")})";
        }

        static IEnumerable<string> Split(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        /// <summary>Which plugin's version was read, and why that one.</summary>
        internal static string ReadFrom(EngineIndex eng, Picked pick)
        {
            var main = eng.Index;
            string line = $"Read from {pick.Plugin.Name}: {pick.Why}.";
            if (pick.Versions is { } v && (v.Holders.Count > 1 || pick.ByPath))
            {
                line += $" Held by (winner first): {HeldBy(main, v)}.";
                if (!pick.ByPath && !pick.IsWinner && v.Winner >= 0) line += $" The game uses the version in {main.Plugins[v.Holders[v.Winner].Plugin].Name}.";
            }
            if (!pick.ByPath && !pick.Plugin.Order.Enabled) line += $" {pick.Plugin.Name} isn't in the load order: the game doesn't load it.";
            return line;
        }

        internal static string HeldBy(HeaderIndex main, RecordVersions v, int most = 8)
        {
            var order = new List<int>();
            if (v.Winner >= 0) order.Add(v.Winner);
            for (int i = v.Holders.Count - 1; i >= 0; i--) if (i != v.Winner && main.Plugins[v.Holders[i].Plugin].Order.Enabled) order.Add(i);
            for (int i = 0; i < v.Holders.Count; i++) if (!main.Plugins[v.Holders[i].Plugin].Order.Enabled) order.Add(i);
            var names = order.Take(most).Select(i => main.Plugins[v.Holders[i].Plugin] is var p && !p.Order.Enabled ? p.Name + " (not loaded)" : p.LeftOutShort != null ? p.Name + $" ({p.LeftOutShort}, left out)" : p.Name).ToList();
            if (order.Count > most) names.Add($"+{order.Count - most} more");
            return (v.Winner < 0 ? FindTool.NoWinner(main, v) : "") + string.Join(" > ", names);
        }

        static void Sources(EngineIndex eng, Picked pick, EngineAnswer answer, bool withLoadOrder)
        {
            answer.Source(EngineIndex.Source(pick.Plugin, pick.ByPath ? pick.Plugin.Path : null));
            if (withLoadOrder && !pick.ByPath) answer.Source(eng.LoadOrderSource());
        }

        // ---------------------------------------------------------------- Versions
        /// <summary>Every plugin that holds the record, in load order, the winner marked. From the header index: no record is read.</summary>
        static void VersionsView(EngineIndex eng, Picked pick, EngineAnswer answer, SchemaTypes schema, int offset, int maxChars, string question)
        {
            var main = eng.Index;
            var lines = new List<string>();
            var v = pick.Versions;
            string label = ProblemNotes.Label(pick.Versions is { } vv && !pick.ByPath ? main.Describe(vv.Winner >= 0 ? vv.Holders[vv.Winner] : vv.Holders[0]) : pick.Info);
            int holders = (v?.Holders.Count ?? 0) + (pick.ByPath ? 1 : 0);
            string winner = v == null ? "" : v.Winner >= 0 ? $" The game uses the version in {main.Plugins[v.Holders[v.Winner].Plugin].Name}." : " No loaded plugin holds it, so the game doesn't have it.";
            lines.Add($"{label} is held by {holders:N0} plugin{(holders == 1 ? "" : "s")}.{winner}");
            lines.Add("| Order | Plugin | Slot | Wins | EditorID there | Type | Bytes | Notes |");
            lines.Add("| :--- | :--- | :--- | :--- | :--- | :--- | ---: | :--- |");
            string? baseSig = null;
            void Row(HeaderIndex ix, RecordRef r, bool wins, bool byPath)
            {
                var p = ix.Plugins[r.Plugin];
                var d = ix.Describe(r);
                var notes = new List<string>();
                if (byPath) notes.Add("the file you named, outside Data: not part of the load order");
                else if (!p.Order.Enabled) notes.Add("not loaded");
                else if (p.LeftOutShort != null) notes.Add($"{p.LeftOutShort}: left out of queries and of the winners");
                notes.Add(d.IsOverride ? "an override" : "its own record");
                if (baseSig != null && d.Signature != baseSig) notes.Add($"a different type than its base ({baseSig}): built for an older version of a master");
                if (!d.IsOverride) baseSig ??= d.Signature;
                if ((d.Flags & 0x20) != 0) notes.Add("marked deleted");
                lines.Add($"| {(byPath || p.Order.Position < 0 ? "" : p.Order.Position.ToString(CultureInfo.InvariantCulture))} | {p.Name} | {(byPath ? "" : p.Order.Enabled ? p.Order.Slot : "")} | {(wins ? "yes" : "")} | {d.EditorId ?? "[no EditorID]"} | {schema.Label(d.Signature)} | {d.Size.ToString("N0", CultureInfo.InvariantCulture)} | {string.Join("; ", notes)} |");
            }
            if (v != null)
            {
                // The base first, so that an override of another type can be told
                var own = v.Holders.FirstOrDefault(h => main.Plugins[h.Plugin].Name.Equals(pick.Owner, StringComparison.OrdinalIgnoreCase), new RecordRef(-1, -1));
                if (own.Plugin >= 0) baseSig = main.Describe(own).Signature;
                for (int i = 0; i < v.Holders.Count; i++) Row(main, v.Holders[i], i == v.Winner, false);
            }
            if (pick.ByPath) Row(pick.Ix, pick.Ref, false, true);
            answer.Lines(lines, 3, offset, maxChars, next => Cursors.Make(next, question), "rows");
            answer.Note("The rows are in load order: the first loads first, the last loaded wins. For what each version changes against the one before it, use compare with kind versions (sfs_compare kind: \"versions\"); record with plugin reads one version.");
            answer.Read($"the header index of {main.Plugins.Count:N0} plugins (checked in {eng.Last.Ms:N0} ms)");
        }
    }
}
