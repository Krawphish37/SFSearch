using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Engine
{
    /// <summary>
    /// find (tool sfs_find, command-line verb find): what is this FormID, FormKey or EditorID; which records match an
    /// EditorID pattern or a record type; who holds each one and who wins. It reads the header index only: no record is
    /// opened, no database is built. In-game names come from each record's own bytes (its name subrecord, through
    /// SFSearch.Records' RecordNames): a row's name is read from the record, and words that aren't an EditorID are looked
    /// for in the link files' names, or, for a plugin whose link file isn't made yet, in the names read from its bytes.
    /// With --text, every text field the game shows is held against a regular expression instead (RunText, GAPS S19).
    /// (design\agent-first\ARCHITECTURE.md sections 5 to 7 and 11; milestones M5 and M8.)
    /// </summary>
    public static class FindTool
    {
        public const int DefaultLimit = 40, MostLimit = 2000;
        /// <summary>A "Held by" cell names this many plugins and counts the rest.</summary>
        const int MostHolders = 8;

        /// <summary>The start of a "Held by" cell when no version wins: no loaded plugin holds the record, or the loaded
        /// ones are refused by Mutagen (C204), or built on a master that can't be read, and left out.</summary>
        internal static string NoWinner(HeaderIndex main, RecordVersions v)
            => v.Holders.Any(h => main.Plugins[h.Plugin] is { Order.Enabled: true, LeftOut: not null }) ? "no loaded plugin Mutagen reads holds it: " : "no loaded plugin holds it: ";

        public const string Help = """
            find <query ...> [--type <type>] [--in <plugin,...>] [--limit N] [--cursor C]
            find --text <regex> [--from stored|strings|both] [--language <language>] [--type <type>] [--in <plugin,...>] [--world] [--plain]
              What a FormID, FormKey or EditorID is, which records match a pattern or a type, who holds each one and who wins.
              It answers from the header index: no record is opened and no database is built.
              With --text: the records whose text fits a regular expression, in every plugin (or the ones named).

              query    one or more, separated by spaces or commas:
                         0476C4:Starfield.esm     a FormKey: the record, every plugin that holds it, the winner first
                         000CF2                   a FormID with no plugin: every plugin that has such a record is listed, none is picked
                         0A0003E6, FE09A123       a FormID with its runtime slot, read with today's load order
                         Eon                      an EditorID (letter case doesn't count)
                         Super Shotgun            words that are no EditorID are looked for in in-game names: every record
                                                  whose name holds all of them, as whole words
                         DarkStar_Mk2_*           a pattern for EditorIDs and in-game names: * is any text, ? is one character
              --type   a signature (WEAP) or a Mutagen name (Weapon). Alone, it lists every record of the type.
              --in     only records these plugins hold: names in Data (wildcards allowed), a set: @official (the game's own
                       plugins and Bethesda's Creations), @loaded (every plugin the load order enables), @all; or the path
                       of a plugin file elsewhere
              --limit  rows per answer (default 40). The answer's [more] line gives the cursor for the next rows.
              --text   a .NET regular expression held against every text field the game shows: names, descriptions,
                       book text, dialogue, quest objectives ... of each plugin's records (each plugin's own version).
                       [^\x00-\x7F] finds text outside plain ASCII, (?i) ignores letter case. A row gives the record,
                       the field, the text around the first match, and where it is: stored in the plugin, or a strings file.
              --from   stored   text stored in plugins with no strings files, read as Mutagen reads it (code page 1252)
                       strings  the strings files of localized plugins, in the language asked
                       both     (default) both
              --language  the strings files' language (default English). Stored text reads the same in every language.
              --world  with --text: search world data too, placed references' own texts (map markers' names). It reads
                       every placed reference's bytes: 0.1 to 1.7 s more over the whole load order.
              --plain  with --text: search plain text fields too, the ones never translated, stored in every plugin
                       (localized ones too): EditorIDs, a quest's summary and log entries' notes, components' node
                       names and file paths, scripts' names, property names and string properties (VMAD). A row of
                       one says "plain text". Not with --from strings.
              Game settings' texts (an EditorID starting with s) are searched. A perk entry point's text and the other
              fields whose subrecord also holds numbers or links are told by reading only the records whose bytes
              could hold a match through Mutagen; the answer counts them.

              Every record is written as EditorID (FormID:plugin). "Held by" names the plugins that hold it, the winner first.
              Quote FormIDs in a shell: PowerShell reads an unquoted 0003E6 as the number 3000000.
              Words separated by spaces only are also read as one in-game name ("New Atlantis"); commas make a list of
              EditorIDs only. A word that is only layers' EditorIDs (LAYR: Neon, Akila) is looked for in names too.
              In-game names are read from each record's own bytes. A search by name looks at every record outside world
              data, in the link index's names or in the plugin's bytes while its link file isn't made, and at cells and
              worldspaces (their own names, never what they hold). A cell's row says "interior", or its worldspace and grid.
              Exit code: 0 answered (also when nothing was found: the answer says why); 2 an error (one line, Error: ... Fix: ...).
            """;

        static readonly string[] Known = { "query", "type", "in", "limit", "cursor", "text", "from", "language", "world", "plain" };

        sealed class Ask
        {
            public string Query = "";
            public string? Type, In, Cursor, Text, From, Language;
            public int Limit = DefaultLimit;
            /// <summary>With Text: world data's texts too (placed references' map marker names).</summary>
            public bool World;
            /// <summary>With Text: plain text fields too (never translated: EditorIDs, scripts' strings, ...).</summary>
            public bool Plain;
        }

        // ---------------------------------------------------------------- Entry points
        /// <summary>The tool: named arguments in, the answer's parts out through the service.</summary>
        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            var answer = new EngineAnswer { AsTool = true };
            if (a.Unknown(Known) is { Count: > 0 } wrong)
                answer.Fail($"find has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", $"its arguments are: {string.Join(", ", Known)}", 2);
            else
                Guarded(new Ask { Query = a.Text("query") ?? "", Type = a.Text("type"), In = a.Text("in"), Cursor = a.Text("cursor"), Limit = a.Int("limit") ?? DefaultLimit,
                    Text = a.Text("text"), From = a.Text("from"), Language = a.Text("language"), World = a.Flag("world"), Plain = a.Flag("plain") }, answer);
            answer.Emit();
        }

        /// <summary>The command-line verb: find &lt;query...&gt; [--type T] [--in plugins] [--limit N] [--cursor C].</summary>
        public static void Verb(List<string> args)
        {
            if (args.Count == 0 || args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            var ask = new Ask { Type = CommandLine.TakeOption(args, "--type"), In = CommandLine.TakeOption(args, "--in"), Cursor = CommandLine.TakeOption(args, "--cursor"),
                Text = CommandLine.TakeOption(args, "--text"), From = CommandLine.TakeOption(args, "--from"), Language = CommandLine.TakeOption(args, "--language") ?? CommandLine.TakeOption(args, "--lang"),
                World = CommandLine.TakeFlag(args, "--world"), Plain = CommandLine.TakeFlag(args, "--plain") };
            var answer = new EngineAnswer();
            string? limit = CommandLine.TakeOption(args, "--limit");
            var stray = args.Where(x => x.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (limit != null && !int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out ask.Limit))
                answer.Fail($"--limit takes a number, not '{limit}'", "give the number of rows, e.g. --limit 100", 2);
            else if (stray.Count > 0)
                answer.Fail($"find has no option {string.Join(", ", stray)}", "its options are --type, --in, --limit, --cursor, and --text with --from, --language, --world and --plain (find --help shows them)", 2);
            else
            {
                ask.Query = string.Join(" ", args);
                Guarded(ask, answer);
            }
            answer.Emit();
        }

        static void Guarded(Ask ask, EngineAnswer answer)
        {
            try
            {
                if (ask.Text != null) RunText(ask, answer);
                else if (ask.World) answer.Fail("world goes with a text pattern", "ask find --text \"<regex>\" --world to search placed references' texts (map markers' names); a search by EditorID or name already finds cells and worldspaces", 2);
                else if (ask.Plain) answer.Fail("plain goes with a text pattern", "ask find --text \"<regex>\" --plain to search plain text fields (EditorIDs, scripts' strings, a quest's summary)", 2);
                else Run(ask, answer);
            }
            catch (FileNotFoundException e) { answer.Fail(e.Message.Split('\n')[0], "set the Data folder with SetData, or the SFSEARCH_DATA environment variable"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                answer.Fail($"the header index couldn't be brought up to date ({e.Message.TrimEnd('.')})", "ask again; if it stays, run loadorder2 problems to see which plugin is at fault");
            }
        }

        // ---------------------------------------------------------------- The scope: which plugins are asked about
        sealed class Scoped
        {
            public required HeaderIndex Ix;
            public required IndexedPlugin P;
            /// <summary>A file outside Data, given by path: it isn't part of the load order.</summary>
            public bool ByPath;
            public string Shown = "";
            public IndexedPlugin?[] Owners = Array.Empty<IndexedPlugin?>();
            /// <summary>
            /// No plugin that holds one of this plugin's own records comes before it in the load order: it is loaded, and
            /// no plugin that lists it as a master loads ahead of it. Its own records then need no look at their holders.
            /// </summary>
            public bool OwnFirst;
        }

        sealed class Scope
        {
            public List<Scoped> Plugins = new();
            /// <summary>No plugin was named: every plugin in Data.</summary>
            public bool All;
            /// <summary>For each plugin of the main index: its place in <see cref="Plugins"/>, or -1.</summary>
            public int[] PlaceOfMain = Array.Empty<int>();
            public bool Holds(int mainPluginId) => PlaceOfMain[mainPluginId] >= 0;
            /// <summary>How the plugins were named when a set was among them: "@official (33 plugins)". Null: by their names.</summary>
            public string? Named;
            public string Words => All ? $"the {Plugins.Count:N0} plugins in Data" : Named ?? (Plugins.Count == 1 ? Plugins[0].Shown : $"the {Plugins.Count} plugins named");
            public long Records => Plugins.Sum(p => (long)(p.P.Segment?.Count ?? 0));
            public long EditorIds => Plugins.Sum(p => (long)(p.P.Segment?.EditorIdCount ?? 0));
        }

        readonly record struct Hit(int Place, int Entry);

        static Scope? BuildScope(EngineIndex eng, string? inArg, EngineAnswer answer)
        {
            var main = eng.Index;
            var scope = new Scope { All = string.IsNullOrWhiteSpace(inArg) };
            if (scope.All)
                foreach (var p in main.Plugins.Where(p => p.Segment != null).OrderBy(EngineIndex.Rank))
                    scope.Plugins.Add(new Scoped { Ix = main, P = p, Shown = p.Name });
            else
            {
                // Names, patterns and sets (@official) through the one resolver every verb uses (GAPS S117)
                PluginNaming naming;
                try { naming = PluginSets.Resolve(main, Home.SplitPluginList(inArg!), winnersAllowed: false, verb: "find"); }
                catch (PluginSetException e) { answer.Fail(e.Message, e.Fix, 2); return null; }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pick in naming.Picks)
                {
                    if (pick.Plugin != null)
                    {
                        // A plugin a set or a pattern holds that can't be read is left out, and said; one named by itself is an error (below)
                        if (pick.Kind is PluginPickKind.Set or PluginPickKind.Pattern && ProblemNotes.Blocking(pick.Plugin) is { } blocked)
                        { answer.Problem($"{blocked.What}. It was left out of {pick.Item}."); continue; }
                        if (seen.Add(pick.Plugin.Name)) scope.Plugins.Add(new Scoped { Ix = main, P = pick.Plugin, Shown = pick.Plugin.Name });
                        continue;
                    }
                    // A path, or a name that isn't in Data: read from the file, or the nearest names
                    var one = RecordPick.Plugin(eng, pick.Item, answer);
                    if (one == null) return null;
                    if (!one.Value.ByPath) { if (seen.Add(one.Value.Plugin.Name)) scope.Plugins.Add(new Scoped { Ix = main, P = one.Value.Plugin, Shown = one.Value.Plugin.Name }); continue; }
                    if (seen.Add(one.Value.Plugin.Path)) scope.Plugins.Add(new Scoped { Ix = one.Value.Ix, P = one.Value.Plugin, ByPath = true, Shown = one.Value.Plugin.Name });
                }
                if (scope.Plugins.Count == 0) { answer.Fail(naming.Items.Count > 0 ? $"no plugin {string.Join(", ", naming.Items)} names can be read" : "--in names no plugin", naming.Items.Count > 0 ? "loadorder2 problems says what is wrong with each" : "name one or more plugins, separated by commas", 2); return null; }
                if (naming.NamesASet) scope.Named = naming.Shown(scope.Plugins.Select(s => s.Shown).ToList());
                // Plugins of the load order first, in its order; files outside Data last
                scope.Plugins = scope.Plugins.OrderBy(s => s.ByPath ? 1 : 0).ThenBy(s => s.ByPath ? 0 : EngineIndex.Rank(s.P)).ToList();
            }
            scope.PlaceOfMain = Enumerable.Repeat(-1, main.Plugins.Count).ToArray();
            var loadedLate = new HashSet<string>(main.Plugins.SelectMany(p => p.Problems.MastersLoadedAfter), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < scope.Plugins.Count; i++)
            {
                var s = scope.Plugins[i];
                s.OwnFirst = !s.ByPath && s.P.Order.Enabled && !loadedLate.Contains(s.P.Name);
                if (!s.ByPath) scope.PlaceOfMain[s.P.Id] = i;
                if (s.P.Segment != null) s.Owners = Enumerable.Range(0, s.P.Segment.Names.Count).Select(o => eng.Owner(s.Ix, s.P, o)).ToArray();
            }
            return scope;
        }

        // ---------------------------------------------------------------- The record type asked for
        /// <summary>The signatures a type argument stands for. Null with a failed answer when it names no type.</summary>
        static HashSet<string>? ResolveType(EngineIndex eng, SchemaTypes schema, string type, EngineAnswer answer)
        {
            var sigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in type.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim();
                if (schema.SigOf(t) is { } sig)
                {
                    sigs.Add(sig);
                    if (schema.TypesOf(sig).Count > 1)
                        answer.Note($"{t} is one of {schema.TypesOf(sig).Count} Mutagen types stored as {sig} ({string.Join(", ", schema.TypesOf(sig))}). The header index can't tell them apart: every {sig} record is listed.");
                    continue;
                }
                bool inSchema = schema.TypesOf(t).Count > 0;
                bool inPlugins = t.Length == 4 && eng.Index.Plugins.Any(p => p.Segment != null && p.Segment.Signatures.Any(s => s.Signature.Equals(t, StringComparison.OrdinalIgnoreCase)));
                if (inSchema || inPlugins) { sigs.Add(t.ToUpperInvariant()); continue; }
                var near = schema.Nearest(t);
                answer.Fail($"there is no record type '{t}'",
                    !schema.Available ? $"schema.db isn't beside SFSearch.exe ({schema.Path}), so only 4-letter signatures are known: use one, e.g. WEAP"
                    : near.Count > 0 ? $"the nearest real names: {string.Join("; ", near)}. A type is a signature (WEAP) or a Mutagen name (Weapon)"
                    : "use a signature (WEAP) or a Mutagen name (Weapon)", 2);
                return null;
            }
            return sigs;
        }

        // ---------------------------------------------------------------- The query's parts
        enum Kind { FormKey, Id, Runtime, EditorId, Pattern }

        sealed class Token
        {
            public string Text = "";
            public Kind Kind;
            public string Plugin = "";
            public uint LocalId;
            /// <summary>All hex digits, so it could be a FormID, and it has a letter, so it could be an EditorID too.</summary>
            public bool AlsoEditorId;
            /// <summary>Set by the lookup: it is the FormID of some records and the EditorID of others.</summary>
            public bool HitBoth;
        }

        static readonly Regex FormKeyInText = new(@"(?<![0-9A-Za-z_])[0-9A-Fa-f]{1,6}:[^,:]+?\.es[mpl]\b|(?<![0-9A-Za-z_:])[^,:\s]+?\.es[mpl]:[0-9A-Fa-f]{1,6}(?![0-9A-Za-z_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Hex = new("^[0-9A-Fa-f]{1,8}$", RegexOptions.Compiled);
        static readonly Regex ShellNumber = new(@"^-?\d+(\.\d+)?E[+-]\d+$|^-?\d+\.\d+(E[+-]?\d+)?$|^-?Infinity$|^NaN$|^∞$|^\d{9,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static List<Token> Tokens(string query)
        {
            var tokens = new List<(int At, Token T)>();
            // FormKeys first: a plugin's name may hold spaces
            string rest = FormKeyInText.Replace(query, m =>
            {
                if (HeaderIndex.TryParseFormKey(m.Value.Trim(), out var plugin, out var id))
                {
                    tokens.Add((m.Index, new Token { Text = m.Value.Trim(), Kind = Kind.FormKey, Plugin = plugin, LocalId = id }));
                    return new string(' ', m.Length);
                }
                return m.Value;
            });
            int pos = 0;
            foreach (var piece in rest.Split(new[] { ',', ' ', '\t', '\r', '\n', ';' }))
            {
                int at = pos; pos += piece.Length + 1;
                string t = piece.Trim().Trim('"', '\'');
                if (t.Length == 0) continue;
                var tok = new Token { Text = t };
                if (Glob.IsPattern(t)) tok.Kind = Kind.Pattern;
                else if (Hex.IsMatch(t))
                {
                    uint value = uint.Parse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                    tok.Kind = t.Length <= 6 ? Kind.Id : Kind.Runtime;
                    tok.LocalId = value;
                    tok.AlsoEditorId = t.Any(char.IsLetter);
                }
                else tok.Kind = Kind.EditorId;
                tokens.Add((at, tok));
            }
            return tokens.OrderBy(x => x.At).Select(x => x.T).ToList();
        }

        /// <summary>The texts a shell's number conversion could have started from: 3000000 comes from 3E6, 0003E6, 30E5 ...; 12 from 12D.</summary>
        static IEnumerable<uint> ShellOrigins(string digits)
        {
            var seen = new HashSet<uint>();
            int zeros = digits.Length - digits.TrimEnd('0').Length;
            for (int k = 1; k <= zeros && k < digits.Length; k++)
                for (int pad = 0; pad <= 2; pad++)
                {
                    string text = digits[..^k] + "E" + new string('0', pad) + k.ToString(CultureInfo.InvariantCulture);
                    if (text.Length <= 6 && uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint id) && seen.Add(id)) yield return id;
                }
            // 12D is the decimal number 12 to PowerShell
            if (digits.Length <= 5 && uint.TryParse(digits + "D", NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint d) && seen.Add(d)) yield return d;
        }

        // ---------------------------------------------------------------- Run
        static void Run(Ask ask, EngineAnswer answer)
        {
            var eng = EngineIndex.Get();
            var validity = eng.Check();
            answer.Checked(validity);
            answer.Read($"the header index of {eng.Index.Plugins.Count:N0} plugins (checked in {validity.Ms:N0} ms)");
            var main = eng.Index;
            var schema = SchemaTypes.Default;

            var tokens = Tokens(ask.Query);
            if (tokens.Count == 0 && string.IsNullOrWhiteSpace(ask.Type) && string.IsNullOrWhiteSpace(ask.In))
            {
                answer.Fail("find got nothing to look for", "give a query (a FormID, FormKey, EditorID or pattern), a type, or plugins with in: e.g. query \"Eon\", or type \"WEAP\" with in \"darkstar.esm\"", 2);
                return;
            }
            int limit = ask.Limit <= 0 ? DefaultLimit : Math.Min(ask.Limit, MostLimit);
            string question = $"{ask.Query.Trim()}|{ask.Type?.Trim()}|{ask.In?.Trim()}".ToLowerInvariant();
            if (!Cursors.TryRead(ask.Cursor, question, out int offset))
            {
                answer.Fail($"the cursor \"{ask.Cursor}\" doesn't belong to this question", "ask again with the same query, type and in as the answer that gave the cursor, or leave the cursor out to start over", 2);
                return;
            }

            var scope = BuildScope(eng, ask.In, answer);
            if (scope == null) return;
            // A plugin that was named and can't be read is an error: nothing is mapped by guess (trap 14)
            if (!scope.All && !ProblemNotes.Add(answer, scope.Plugins.Select(s => (s.Ix, s.P, (string?)s.Shown)), required: true)) return;

            HashSet<string>? sigs = null;
            if (!string.IsNullOrWhiteSpace(ask.Type))
            {
                sigs = ResolveType(eng, schema, ask.Type!, answer);
                if (sigs == null) return;
            }
            string typeWords = sigs == null ? "" : string.Join(" or ", sigs.Select(schema.Label));

            // ---- Look everything up
            var rows = new List<(string Asked, Hit? Hit, string? Nothing, bool ById)>();
            if (tokens.Count == 0)
            {
                var hits = Scan(eng, scope, sigs, null, out long looked, out _);
                foreach (var h in hits) rows.Add(("", h, null, false));
                if (hits.Count == 0) rows.Add(("", null, NothingOfType(scope, sigs, typeWords, looked), false));
            }
            foreach (var t in tokens)
            {
                var hits = Lookup(eng, scope, t, sigs, out string nothing, out var otherTypes);
                if (t.Kind is Kind.Id or Kind.Runtime && IsShellNumber(t.Text))
                {
                    // An ID made of decimal digits only may be what a shell left of an unquoted FormID (trap 1)
                    if (t.Text == "0")
                    {
                        answer.Fail("0 isn't the FormID of a record. A shell turns an unquoted ID such as 000E82 into the number 0",
                            $"quote FormIDs: find \"000E82\"{(ask.In != null ? " --in " + ask.In : "")} (in PowerShell an unquoted 0003E6 becomes 3000000)", 2);
                        return;
                    }
                    // Every ID a shell would have turned into this number, and that is a record here
                    var origins = new List<(uint Id, string Example)>();
                    foreach (uint origin in ShellOrigins(t.Text))
                    {
                        var was = Lookup(eng, scope, new Token { Text = origin.ToString("X6"), Kind = Kind.Id, LocalId = origin }, sigs, out _, out _);
                        if (was.Count > 0 && origins.Count < 4) origins.Add((origin, LabelOf(eng, scope, was[0])));
                    }
                    if (origins.Count > 0)
                    {
                        string ids = string.Join(" or ", origins.Select(o => o.Id.ToString("X6")));
                        string examples = string.Join("; ", origins.Select(o => $"{o.Id:X6} is {o.Example}"));
                        if (hits.Count == 0)
                        {
                            answer.Fail($"{t.Text} isn't the FormID of any record in {scope.Words}, but a shell turns an unquoted {ids} into the number {t.Text}, and {examples}",
                                $"quote FormIDs: find \"{origins[0].Id:X6}\"{(ask.In != null ? " --in " + ask.In : "")}", 2);
                            return;
                        }
                        answer.Note($"{t.Text} was read as the FormID {t.LocalId:X6}. A shell turns an unquoted {ids} into {t.Text} too, and {(origins.Count == 1 ? "that is a record" : "those are records")} here as well. Quote FormIDs.");
                    }
                }
                if (hits.Count == 0 && ShellNumber.IsMatch(t.Text))
                {
                    answer.Fail($"'{t.Text}' isn't a FormID or an EditorID: it looks like what a shell made of an unquoted FormID (an ID such as 0005E9 is a number to PowerShell)",
                        "quote FormIDs: find \"0005E9\"", 2);
                    return;
                }
                bool byId = t.Kind is Kind.FormKey or Kind.Id or Kind.Runtime;
                foreach (var h in hits) rows.Add((t.Text, h, null, byId));
                if (hits.Count == 0)
                    rows.Add((t.Text, null, otherTypes.Count > 0
                        ? $"{otherTypes.Count:N0} record(s) fit \"{t.Text}\" in {scope.Words}, but none is of type {typeWords}: {(otherTypes.Count == 1 ? "it is" : "they are")} {string.Join(", ", otherTypes.Distinct().Select(schema.Label))}"
                        : nothing, byId));
                else if (t.HitBoth) answer.Note($"\"{t.Text}\" is both a FormID ({t.LocalId:X6}) and an EditorID: the records of both are listed.");
                if (t.Kind == Kind.Runtime && hits.Count > 0) answer.Note($"{t.Text} was read as a runtime FormID: its slot is {SlotOwner(main, t.LocalId)?.Name} in today's load order.");
            }

            // ---- In-game names: the words that aren't IDs, taken together, or one pattern
            var words = tokens.Where(t => t.Kind == Kind.EditorId).Select(t => t.Text).ToList();
            bool byWords = tokens.Count > 0 && words.Count == tokens.Count;
            bool byPattern = tokens.Count == 1 && tokens[0].Kind == Kind.Pattern;
            // A word that is a record's EditorID is that record: names are searched only when no EditorID fits, or by a pattern
            NameFiles? names = null;
            // Several words that are no EditorID were asked as one name and found nothing: one answer, not one "nothing" row a word
            bool asOneName = false;
            // Several words with no comma between them are one name too, even when some of them are EditorIDs: "New
            // Atlantis" is a worldspace's name, and "new" a layer's EditorID. Commas make a list of EditorIDs only.
            bool phrase = byWords && words.Count > 1 && !ask.Query.Contains(',');
            // A word whose records by EditorID are layers only (LAYR: the Creation Kit's groups of placed objects, with no
            // in-game name) is most likely a place's name: "Neon", "Akila" and "City" are layers' EditorIDs
            var layerWords = byWords
                ? rows.Where(r => r.Hit != null).GroupBy(r => r.Asked, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.All(r => SignatureOf(scope, r.Hit!.Value) == "LAYR")).Select(g => g.Key).ToList()
                : new List<string>();
            if (byWords && rows.All(r => r.Hit == null) || byPattern || phrase || layerWords.Count > 0)
            {
                names = NameData(scope, eng.DataFolder);
                var listed = new HashSet<(string, uint)>(rows.Where(r => r.Hit != null).Select(r => KeyOf(scope, r.Hit!.Value)));
                var found = NameHits(scope, names, byWords ? words : null, byPattern ? new Glob(tokens[0].Text) : null, sigs);
                string asked = byWords ? string.Join(" ", words) : tokens[0].Text;
                var byName = new List<(string Asked, Hit? Hit, string? Nothing, bool ById)>();
                foreach (var h in found)
                    if (listed.Add(KeyOf(scope, h))) byName.Add((asked, h, null, false));
                int added = byName.Count;
                // Some of the words are EditorIDs: their records stay listed whatever the name search finds
                var idWords = byWords ? rows.Where(r => r.Hit != null).Select(r => r.Asked).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
                var missed = rows.Where(r => r.Hit == null).Select(r => r.Asked).ToList();
                string looked = $"{names.Count:N0} in-game names of {scope.Plugins.Count:N0} plugin(s) looked at";
                if (!byPattern && idWords.Count > 0)
                {
                    // When nothing has the words as its name, the answer is the EditorIDs' as before
                    if (added > 0)
                    {
                        rows.RemoveAll(r => r.Hit == null);
                        string ids = string.Join(", ", idWords.Select(w => $"\"{w}\""));
                        if (missed.Count > 0 || layerWords.Count > 0)
                        {
                            // A word that is no EditorID, or only a layer's: the words were most likely a name, so its records come first
                            rows.InsertRange(0, byName);
                            string why = missed.Count > 0
                                ? $"No record has the EditorID {string.Join(" or ", missed.Select(w => $"\"{w}\""))}"
                                : $"{string.Join(" and ", layerWords.Select(w => $"\"{w}\""))} {(layerWords.Count == 1 ? "is the EditorID" : "are EditorIDs")} of layers only (LAYR, the Creation Kit's groups of placed objects)";
                            answer.Note($"{why}, so \"{asked}\" was read as {(words.Count == 1 ? "an" : "one")} in-game name: the first {added:N0} row(s) are the records whose name holds {(words.Count == 1 ? "that word" : "those words")}, then the record(s) whose EditorID is {ids}.");
                        }
                        else
                        {
                            rows.AddRange(byName);
                            answer.Note($"Each word of \"{asked}\" is an EditorID, and the words are an in-game name too: the record(s) of the EditorID(s) come first, then {added:N0} record(s) whose name holds those words. Separate EditorIDs with commas to look up only EditorIDs.");
                        }
                    }
                }
                else if (added > 0 || found.Count > 0)
                {
                    // The words were a name: a word that is no EditorID isn't an empty answer any more
                    rows.AddRange(byName);
                    rows.RemoveAll(r => r.Hit == null);
                    answer.Note(byPattern
                        ? $"\"{asked}\" was held against EditorIDs and against in-game names: {rows.Count - added:N0} record(s) by EditorID, listed first, and {added:N0} more by name."
                        : $"No record has the EditorID \"{asked}\": the row(s) are the {added:N0} record(s) whose in-game name holds {(words.Count == 1 ? "that word" : "those words")}.");
                }
                else
                {
                    if (byWords && words.Count > 1 && rows.All(r => r.Nothing != null && r.Nothing.StartsWith("no EditorID is ", StringComparison.Ordinal)))
                    {
                        // The words were one question: one line says what was looked for, as EditorIDs and as a name
                        asOneName = true;
                        string ids = string.Join(" or ", words.Select(w => $"\"{w}\""));
                        rows.Clear();
                        rows.Add((asked, null, $"no EditorID is {ids} ({scope.EditorIds:N0} EditorIDs in {scope.Words} looked at)"
                            + $", and no in-game name holds the words \"{asked}\" ({looked})", false));
                    }
                    else
                        for (int i = 0; i < rows.Count; i++)
                            if (rows[i].Hit == null && rows[i].Nothing is { Length: > 0 } n)
                                rows[i] = (rows[i].Asked, null, n + (byWords && words.Count > 1 ? $", and no in-game name holds the words \"{asked}\" ({looked})" : byWords ? $", and no in-game name holds that word ({looked}). For a part of an EditorID or of a name use wildcards: *{asked}*" : $", and no in-game name fits it ({looked})"), rows[i].ById);
                }
            }

            // ---- The page
            int total = rows.Count(r => r.Hit != null);
            bool many = tokens.Count > 1 && !asOneName;
            var page = rows.Skip(offset).Take(limit).ToList();
            if (offset > 0 && page.Count == 0)
            {
                answer.Fail($"the cursor points past the end ({rows.Count:N0} rows)", "leave the cursor out to start over", 2);
                return;
            }
            var built = page.Select(r => r.Hit is { } h ? BuildRow(eng, scope, schema, h, r.ById, sigs) : null).ToList();
            bool inside = built.Any(b => b?.Inside != null);

            if (total == 0 && !many)
            {
                answer.Line(rows.Count > 0 ? Sentence(rows[0].Nothing!) : "Nothing was found.");
            }
            else
            {
                var cols = new List<string>();
                if (many) cols.Add("Asked");
                cols.Add("Record"); cols.Add("Type"); cols.Add("Name");
                if (inside) cols.Add("Inside");
                cols.Add("Held by (winner first)");
                answer.Line("| " + string.Join(" | ", cols) + " |");
                answer.Line("| " + string.Join(" | ", cols.Select(_ => ":---")) + " |");
                answer.HeaderLines = 2;
                for (int i = 0; i < page.Count; i++)
                {
                    var cells = new List<string>();
                    if (many) cells.Add(page[i].Asked);
                    if (built[i] is { } b) { cells.Add(b.Record); cells.Add(b.Type); cells.Add(b.Name); if (inside) cells.Add(b.Inside ?? ""); cells.Add(b.HeldBy); }
                    else { cells.Add("nothing: " + page[i].Nothing); cells.Add(""); cells.Add(""); if (inside) cells.Add(""); cells.Add(""); }
                    answer.Line("| " + string.Join(" | ", cells.Select(Cell)) + " |");
                }
                int next = offset + page.Count;
                answer.Page(offset + 1, page.Count, rows.Count, next < rows.Count ? Cursors.Make(next, question) : null);
                if (offset == 0 && tokens.Count == 1 && tokens[0].Kind == Kind.Id && total > 1)
                    answer.Note($"{total:N0} records have the ID {tokens[0].LocalId:X6}. They are different records: name the plugin (FormID:plugin, or in) to mean one.");
            }

            // ---- Problems of the plugins the rows came from lead the notes; then where the answer was read from
            var shownHolders = built.Where(b => b != null).SelectMany(b => b!.Holders).Distinct().ToList();
            var from = new List<(HeaderIndex, IndexedPlugin, string?)>();
            if (scope.All) from.AddRange(main.Plugins.Where(p => p.Problems.MissingMasters.Count > 0 || p.Problems.ReadError != null).Select(p => (main, p, (string?)null)));
            from.AddRange(shownHolders.OrderBy(EngineIndex.Rank).Select(p => (main, p, (string?)null)));
            if (!scope.All) from.AddRange(scope.Plugins.Where(s => s.ByPath).Select(s => (s.Ix, s.P, (string?)s.Shown)));
            ProblemNotes.Add(answer, from, required: false);

            if (built.Any(b => b != null && b.Type.EndsWith('*')))
                foreach (var sig in built.Where(b => b != null && b.Type.EndsWith('*')).Select(b => b!.Type.Split(' ')[0]).Distinct().Where(g => sigs == null || !sigs.Contains(g) || schema.SigOf(ask.Type ?? "") == null))
                    answer.Note($"* {sig} holds {schema.TypesOf(sig).Count} Mutagen types ({string.Join(", ", schema.TypesOf(sig))}). The header index doesn't say which one a record is.");
            var notLoaded = scope.Plugins.Where(s => !scope.All && !s.ByPath && !s.P.Order.Enabled).ToList();
            if (scope.Named != null && notLoaded.Count > 1)
                answer.Note($"{notLoaded.Count:N0} plugins of {scope.Named} are in Data but not in the load order: the game doesn't load them ({string.Join(", ", notLoaded.Take(8).Select(s => s.Shown))}{(notLoaded.Count > 8 ? $" and {notLoaded.Count - 8:N0} more" : "")}).");
            else
                foreach (var s in notLoaded)
                    answer.Note($"{s.Shown} is in Data but not in the load order: the game doesn't load it.");
            // Names searched in plugins with no link file yet were read from their bytes: the same names, only slower
            if (names is { FromBytes: > 0 })
                answer.Read($"the in-game names of {names.FromBytes:N0} plugin(s) from their own bytes{(names.ReadMs > 0 ? $" ({names.ReadMs:N0} ms)" : " (read before)")}: {(names.FromBytes == 1 ? "its link file isn't" : "their link files aren't")} made yet");
            if (built.Any(b => b != null)) answer.Read("each row's in-game name from the record's own bytes");
            foreach (var s in scope.Plugins.Where(s => s.ByPath))
                answer.Note($"{s.Shown} was read from {s.P.Path}, outside Data: it isn't part of the load order. \"Held by\" names the plugins in Data that hold the same record.");

            if (scope.All)
            {
                answer.Source(eng.AllSource());
                foreach (var p in shownHolders.OrderBy(EngineIndex.Rank).Take(8)) answer.Source(EngineIndex.Source(p));
            }
            else
            {
                // How the plugins were named, first: "@official (33 plugins)"
                if (scope.Named != null) answer.Source(scope.Named);
                foreach (var s in scope.Plugins) answer.Source(EngineIndex.Source(s.P, s.ByPath ? s.P.Path : null));
            }
            answer.Source(eng.LoadOrderSource());
        }

        // ---------------------------------------------------------------- Text by its characters (--text, GAPS S19)
        /// <summary>How long a text pattern may take on one text before it gives up (a pattern that backtracks without end).</summary>
        static readonly TimeSpan TextPatternTimeout = TimeSpan.FromSeconds(2);
        /// <summary>The text shown around the first match: about twice this many characters.</summary>
        const int TextAround = 50;

        /// <summary>
        /// find --text: every text field the game shows (names, descriptions, book text, dialogue, ...) of each plugin's
        /// records, held against a regular expression. Stored text is read from the plugins' bytes as Mutagen reads it;
        /// a localized plugin's texts come from its strings files in the language asked (SFSearch.Records' RecordTexts).
        /// Each plugin's own version of a record is searched, so a record whose text an override changes can be listed
        /// once for each plugin.
        /// </summary>
        static void RunText(Ask ask, EngineAnswer answer)
        {
            if (!string.IsNullOrWhiteSpace(ask.Query))
            {
                answer.Fail("find takes a query or a text pattern, not both", $"ask find --text \"{ask.Text}\" alone (with --type or --in to narrow it), or find \"{ask.Query.Trim()}\" alone", 2);
                return;
            }
            if (string.IsNullOrEmpty(ask.Text))
            {
                answer.Fail("--text got an empty pattern", "give a regular expression, e.g. --text \"[^\\x00-\\x7F]\" for text outside plain ASCII, or --text \"(?i)handbook\"", 2);
                return;
            }
            Regex pattern;
            try { pattern = new Regex(ask.Text, RegexOptions.CultureInvariant, TextPatternTimeout); }
            catch (ArgumentException e)
            {
                answer.Fail($"'{ask.Text}' isn't a regular expression .NET can read ({e.Message.TrimEnd('.')})", "check the pattern; a literal [ ( ) . * + ? \\ | needs a \\ in front", 2);
                return;
            }
            if (!EngineRecords.TryLanguage(ask.Language, out var language))
            {
                answer.Fail($"'{ask.Language}' isn't a language SFSearch knows", $"use one of the languages Starfield ships: {SFSearch.Query.TextLanguages.ShippedText}", 2);
                return;
            }
            var from = (ask.From ?? "both").Trim().ToLowerInvariant() switch
            {
                "both" or "" or "all" => RecordTexts.From.Both,
                "stored" or "plugin" or "plugins" => RecordTexts.From.Stored,
                "strings" or "string" => RecordTexts.From.Strings,
                _ => (RecordTexts.From)0,
            };
            if (from == 0)
            {
                answer.Fail($"--from takes stored, strings or both, not '{ask.From}'", "stored: text stored in plugins with no strings files; strings: the strings files of localized plugins; both (the default)", 2);
                return;
            }
            if (ask.Plain && from == RecordTexts.From.Strings)
            {
                answer.Fail("plain goes with stored text, not from strings", "plain text fields are stored in the plugins' own bytes: ask from stored or both (the default)", 2);
                return;
            }

            var eng = EngineIndex.Get();
            var validity = eng.Check();
            answer.Checked(validity);
            answer.Read($"the header index of {eng.Index.Plugins.Count:N0} plugins (checked in {validity.Ms:N0} ms)");
            var main = eng.Index;
            var types = SchemaTypes.Default;
            int limit = ask.Limit <= 0 ? DefaultLimit : Math.Min(ask.Limit, MostLimit);
            string question = ($"text|{ask.Text}|{ask.Type?.Trim()}|{ask.In?.Trim()}|{language}|{from}" + (ask.World ? "|world" : "") + (ask.Plain ? "|plain" : "")).ToLowerInvariant();
            if (!Cursors.TryRead(ask.Cursor, question, out int offset))
            {
                answer.Fail($"the cursor \"{ask.Cursor}\" doesn't belong to this question", "ask again with the same text, from, language, type, in, world and plain as the answer that gave the cursor, or leave the cursor out to start over", 2);
                return;
            }
            var scope = BuildScope(eng, ask.In, answer);
            if (scope == null) return;
            if (!scope.All && !ProblemNotes.Add(answer, scope.Plugins.Select(s => (s.Ix, s.P, (string?)s.Shown)), required: true)) return;
            HashSet<string>? sigs = null;
            if (!string.IsNullOrWhiteSpace(ask.Type))
            {
                sigs = ResolveType(eng, types, ask.Type!, answer);
                if (sigs == null) return;
            }

            // ---- Every plugin of the scope, in load order
            var schema = EngineRecords.Schema;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var found = new List<(int Place, RecordTexts.Hit Hit)>();
            int storedPlugins = 0, stringsPlugins = 0, noStrings = 0, notNamed = 0, shared = 0, notInflated = 0;
            long texts = 0, strings = 0, worldLeft = 0, worldRead = 0;
            int typedRead = 0, typedNotRead = 0; double typedMs = 0;
            var noStringsNames = new List<string>();
            try
            {
                for (int place = 0; place < scope.Plugins.Count; place++)
                {
                    var sp = scope.Plugins[place];
                    if (sp.P.Segment is not { } seg) continue;
                    var world = WorldTypes.Of(seg);
                    // Outside world data, and cells' and worldspaces' own texts; with --world, everything; a type named
                    // with --type is read whatever it is
                    var mask = seg.Signatures.Select((x, i) => sigs != null ? sigs.Contains(x.Signature) : ask.World || NamedWorld(x.Signature) || !world[i]).ToArray();
                    bool localized = RecordTexts.IsLocalized(sp.P);
                    // A localized plugin's plain text fields are stored in its own bytes (plain)
                    if (localized ? (from & RecordTexts.From.Strings) == 0 && !(ask.Plain && (from & RecordTexts.From.Stored) != 0) : (from & RecordTexts.From.Stored) == 0) continue;
                    if (sigs == null) for (int i = 0; i < mask.Length; i++) if (!mask[i]) worldLeft += seg.Signatures[i].Count;
                    if (sigs == null && ask.World) for (int i = 0; i < mask.Length; i++) if (world[i] && !NamedWorld(seg.Signatures[i].Signature)) worldRead += seg.Signatures[i].Count;
                    // A subrecord that text and other values both fill (a perk's entry point data) is told by reading
                    // its record through Mutagen's reader, only when its bytes could hold a match (GAPS S32)
                    using var read = RecordTexts.Reader(sp.P, language, eng.DataFolder);
                    var r = RecordTexts.Search(sp.P, mask, schema, pattern, language, eng.DataFolder, from, read.Read, ask.Plain);
                    if (localized) { stringsPlugins++; strings += r.Strings; } else storedPlugins++;
                    if (r.NoStrings) { noStrings++; if (noStringsNames.Count < 4) noStringsNames.Add(sp.Shown); }
                    texts += r.Texts; notNamed += r.StringsNotNamed; shared += r.SharedIds; notInflated += r.NotInflated;
                    typedRead += r.TypedRead; typedNotRead += r.TypedNotRead; typedMs += r.TypedMs;
                    foreach (var h in r.Hits) found.Add((place, h));
                }
            }
            catch (Exception e) when (e is RegexMatchTimeoutException || e is AggregateException a && a.InnerExceptions.Any(x => x is RegexMatchTimeoutException))
            {
                answer.Fail($"the pattern '{ask.Text}' took over {TextPatternTimeout.TotalSeconds:F0} s on one text: it backtracks too much", "write it more simply (fewer nested *, + and alternatives), or narrow the search with --in or --type", 2);
                return;
            }
            double ms = clock.Elapsed.TotalMilliseconds;

            // ---- The page
            string langName = language.ToString();
            string where = from switch
            {
                RecordTexts.From.Stored => $"the stored text of {storedPlugins:N0} plugin(s) with no strings files",
                RecordTexts.From.Strings => $"the {langName} strings files of {stringsPlugins:N0} localized plugin(s)",
                _ => $"the stored text of {storedPlugins:N0} plugin(s) with no strings files and the {langName} strings files of {stringsPlugins:N0} localized plugin(s)",
            };
            if (ask.Plain) where += $", and the plain text fields of all {storedPlugins + stringsPlugins:N0}";
            int total = found.Count;
            var byPlugin = found.GroupBy(f => f.Place).Select(g => (Name: scope.Plugins[g.Key].Shown, Count: g.Count())).ToList();
            if (total == 0)
            {
                answer.Line($"No text fits /{ask.Text}/ in {where}{(sigs != null ? $" (records of type {string.Join(" or ", sigs.Select(types.Label))})" : "")}: {texts:N0} text fields{(strings > 0 ? $" and {strings:N0} strings" : "")} looked at.");
            }
            else
            {
                var page = found.Skip(offset).Take(limit).ToList();
                if (offset > 0 && page.Count == 0)
                {
                    answer.Fail($"the cursor points past the end ({total:N0} rows)", "leave the cursor out to start over", 2);
                    return;
                }
                answer.Line($"{total:N0} text(s) in {byPlugin.Count:N0} plugin(s) fit /{ask.Text}/ ({where}): " +
                    string.Join(", ", byPlugin.Take(12).Select(b => $"{b.Name} {b.Count:N0}")) + (byPlugin.Count > 12 ? $", and {byPlugin.Count - 12:N0} more plugins" : "") + ".");
                answer.Line("| Record | Type | Field | Text | In |");
                answer.Line("| :--- | :--- | :--- | :--- | :--- |");
                answer.HeaderLines = 3;
                foreach (var (place, h) in page)
                {
                    var sp = scope.Plugins[place];
                    var info = sp.Ix.Describe(new RecordRef(sp.P.Id, h.Entry));
                    string inCell = sp.Shown + (h.File == null ? h.Plain ? ", stored, plain text" : ", stored" : ", " + h.File);
                    // Whether the game shows this version: a record held by several plugins names the winner when it isn't this one
                    if (!sp.ByPath && HeaderIndex.TryParseFormKey(info.FormKey, out var owner, out var id) && main.Plugin(owner) != null && main.Versions(owner, id) is { Holders.Count: > 1 } v
                        && v.Winner >= 0 && v.Holders[v.Winner].Plugin != sp.P.Id)
                        inCell += $" (the game uses {main.Plugins[v.Holders[v.Winner].Plugin].Name}'s version)";
                    answer.Line($"| {Cell(ProblemNotes.Label(info))} | {Cell(types.Label(info.Signature))} | {Cell(h.Field)} | {Cell(Around(h.Text, pattern))} | {Cell(inCell)} |");
                }
                int next = offset + page.Count;
                answer.Page(offset + 1, page.Count, total, next < total ? Cursors.Make(next, question) : null);
            }

            // ---- Notes: which language, what wasn't read
            if ((from & RecordTexts.From.Strings) != 0)
                answer.Note($"Strings files searched: {langName} (.strings, .dlstrings, .ilstrings). Stored text reads the same in every language: Mutagen reads it as code page 1252.{(language == Mutagen.Bethesda.Strings.Language.English ? " Add language to search another language's strings files." : "")}");
            else
                answer.Note("Only stored text was searched: Mutagen reads it as code page 1252, the same in every language. Add from: both (--from both) to search the strings files of localized plugins too.");
            if (noStrings > 0)
                answer.Note($"{noStrings:N0} localized plugin(s) have no {langName} strings files, so their texts weren't searched: {string.Join(", ", noStringsNames)}{(noStrings > noStringsNames.Count ? ", …" : "")}.");
            if (notNamed > 0)
                answer.Note($"{notNamed:N0} string(s) in the strings files fit too, but no record searched names them (world data such as placed references, or strings no record uses).");
            if (shared > 0)
                answer.Note($"{shared:N0} matching string ID(s) are in two strings files of one plugin: each file's text is listed.");
            if (notInflated > 0)
                answer.Note($"{notInflated:N0} compressed record(s) couldn't be inflated: their text wasn't read.");
            if (worldLeft > 0)
                answer.Note($"World data wasn't searched: {worldLeft:N0} records (placed references, navmeshes, terrain). Cells' and worldspaces' own texts were. Add world (--world) to search placed references' texts too (map markers' names; 0.1 to 1.7 s more over the whole load order), or name a world type with type (REFR).");
            if (worldRead > 0)
                answer.Note($"World data was searched too: {worldRead:N0} records (placed references, navmeshes, terrain); placed references' texts (map markers' names) are the ones it holds.");
            if (total == 0 && !ask.Plain && (from & RecordTexts.From.Stored) != 0)
                answer.Note("Only text the game shows was searched. Add plain (--plain) to search plain text fields too: EditorIDs, a quest's summary, components' node names, scripts' strings (VMAD).");
            if (typedNotRead > 0)
                answer.Note($"{typedNotRead:N0} record(s) whose bytes could hold a match in a field that shares its subrecord with numbers or links (a perk's entry point data, a script's properties) couldn't be read through Mutagen: those fields weren't searched.");
            answer.Read($"{texts:N0} text fields of {scope.Plugins.Count:N0} plugin(s) from their own bytes{(strings > 0 ? $", and {strings:N0} {langName} strings" : "")}" +
                $"{(typedRead > 0 ? $", {typedRead:N0} record(s) read through Mutagen to tell a shared subrecord's text ({typedMs:N0} ms)" : "")}, in {ms:N0} ms");

            if (scope.All)
            {
                ProblemNotes.Add(answer, main.Plugins.Where(p => p.Problems.MissingMasters.Count > 0 || p.Problems.ReadError != null).Select(p => (main, p, (string?)null)), required: false);
                answer.Source(eng.AllSource());
                foreach (var b in byPlugin.Take(8)) if (main.Plugin(b.Name) is { } p) answer.Source(EngineIndex.Source(p));
            }
            else
            {
                if (scope.Named != null) answer.Source(scope.Named);
                foreach (var s in scope.Plugins) answer.Source(EngineIndex.Source(s.P, s.ByPath ? s.P.Path : null));
            }
            answer.Source(eng.LoadOrderSource());
        }

        /// <summary>The text around the pattern's first match: about 100 characters, with … where it was cut.</summary>
        static string Around(string text, Regex pattern)
        {
            text = text.Replace("\r\n", "\n").Replace('\n', ' ');
            if (text.Length <= 2 * TextAround) return text;
            var m = pattern.Match(text);
            int at = m.Success ? m.Index : 0;
            int start = Math.Max(0, at - TextAround / 2);
            int end = Math.Min(text.Length, start + 2 * TextAround);
            start = Math.Max(0, end - 2 * TextAround);
            return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
        }

        static bool IsShellNumber(string text) => text.All(c => c is >= '0' and <= '9') && (text == "0" || text[0] != '0');

        static string Sentence(string text)
        {
            text = text.TrimEnd();
            if (text.Length == 0) return text;
            // A sentence that starts with a file's name keeps the name as it is
            if (!text.Split(' ')[0].Contains('.')) text = char.ToUpperInvariant(text[0]) + text[1..];
            return text.EndsWith('.') || text.EndsWith('*') ? text : text + ".";
        }

        static string Cell(string text) => text.Replace("|", "\\|").Replace('\n', ' ').Replace("\r", "");

        static IndexedPlugin? SlotOwner(HeaderIndex main, uint runtimeId)
        {
            uint top = runtimeId >> 24;
            string slot = top == 0xFE ? $"FE:{(runtimeId >> 12) & 0xFFF:X3}" : top == 0xFD ? $"FD:{(runtimeId >> 16) & 0xFF:X2}" : $"{top:X2}";
            var e = main.LoadOrderEntries.FirstOrDefault(x => x.Enabled && x.Slot == slot);
            return e == null ? null : main.Plugin(e.Name);
        }

        static string NothingOfType(Scope scope, HashSet<string>? sigs, string typeWords, long looked)
        {
            if (sigs == null) return $"{scope.Words} hold{(scope.Plugins.Count == 1 ? "s" : "")} no record ({scope.Records:N0} records in the index).";
            if (scope.Plugins.Count == 1)
                return $"{scope.Words} has no {typeWords} record: the header index counts 0 among its {scope.Records:N0} records.";
            return $"None of {scope.Words} has a {typeWords} record: the header index counts 0 among their {scope.Records:N0} records.";
        }

        // ---------------------------------------------------------------- Lookups
        /// <summary>True when this version of a record passes the type filter and, when there is one, the pattern.</summary>
        static bool Fits(Segment seg, int entry, HashSet<string>? sigs, Glob? glob)
        {
            var e = seg[entry];
            if (sigs != null && !sigs.Contains(seg.SignatureOf(e))) return false;
            return glob == null || (seg.EditorId(entry) is { } id && glob.IsMatch(id));
        }

        /// <summary>
        /// A record is listed once, at the first plugin of the scope (in load order) that holds a fitting version of it.
        /// True when this entry's plugin is that one.
        /// </summary>
        static bool First(EngineIndex eng, Scope scope, int place, in Entry e, HashSet<string>? sigs, Glob? glob)
        {
            var sp = scope.Plugins[place];
            if (sp.OwnFirst && e.Owner == sp.Owners.Length - 1) return true; // its own record, and nothing loads ahead of it
            var owner = e.Owner < sp.Owners.Length ? sp.Owners[e.Owner] : null;
            if (owner == null) return true; // the owner isn't in Data: there is nothing to compare with
            var main = eng.Index;
            if (sp.ByPath && main.Plugin(owner.Name) == null) return true;
            var versions = main.Versions(owner.Name, e.LocalId);
            if (versions == null) return true;
            foreach (var h in versions.Holders)
            {
                if (!sp.ByPath && h.Plugin == sp.P.Id) return true;
                if (!scope.Holds(h.Plugin)) continue;
                if (Fits(main.Plugins[h.Plugin].Segment!, h.Entry, sigs, glob)) return false;
            }
            return true;
        }

        /// <summary>Every record of the scope that fits the type and the pattern, in load order and then FormKey order. No record is opened.</summary>
        static List<Hit> Scan(EngineIndex eng, Scope scope, HashSet<string>? sigs, Glob? glob, out long looked, out long lookedIds)
        {
            const int Piece = 1 << 16;
            var pieces = new List<(int Place, int From, int To)>();
            var ok = new bool[scope.Plugins.Count][];
            for (int place = 0; place < scope.Plugins.Count; place++)
            {
                var seg = scope.Plugins[place].P.Segment;
                if (seg == null) continue;
                ok[place] = seg.Signatures.Select(s => sigs == null || sigs.Contains(s.Signature)).ToArray();
                if (!ok[place].Any(x => x)) continue;
                for (int from = 0; from < seg.FirstUnresolved; from += Piece) pieces.Add((place, from, Math.Min(from + Piece, seg.FirstUnresolved)));
            }
            var found = new List<Hit>[pieces.Count];
            long lookedAll = 0, idsAll = 0;
            bool single = scope.Plugins.Count == 1;
            void Work(int n)
            {
                var (place, from, to) = pieces[n];
                var seg = scope.Plugins[place].P.Segment!;
                var entries = seg.Entries;
                var okSig = ok[place];
                var list = new List<Hit>();
                long lookedHere = 0, idsHere = 0;
                for (int i = from; i < to; i++)
                {
                    ref readonly var e = ref entries[i];
                    if (!okSig[e.Signature]) continue;
                    lookedHere++;
                    if (glob != null)
                    {
                        if (!e.HasEditorId) continue;
                        idsHere++;
                        if (seg.EditorId(i) is not { } id || !glob.IsMatch(id)) continue;
                    }
                    if (!single && !First(eng, scope, place, in e, sigs, glob)) continue;
                    list.Add(new Hit(place, i));
                }
                found[n] = list;
                Interlocked.Add(ref lookedAll, lookedHere);
                Interlocked.Add(ref idsAll, idsHere);
            }
            if (pieces.Count <= 2) for (int n = 0; n < pieces.Count; n++) Work(n);
            else Parallel.For(0, pieces.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, Work);
            looked = lookedAll; lookedIds = idsAll;
            var all = new List<Hit>(found.Sum(f => f.Count));
            foreach (var f in found) all.AddRange(f);
            return all;
        }

        /// <summary>One part of the query. When nothing fits, <paramref name="nothing"/> says why, from what the index knows.</summary>
        static List<Hit> Lookup(EngineIndex eng, Scope scope, Token t, HashSet<string>? sigs, out string nothing, out List<string> otherTypes)
        {
            var main = eng.Index;
            nothing = ""; otherTypes = new List<string>();
            var candidates = new List<Hit>();
            switch (t.Kind)
            {
                case Kind.Pattern:
                    {
                        var hits = Scan(eng, scope, sigs, new Glob(t.Text), out long looked, out long ids);
                        if (hits.Count == 0)
                            nothing = $"no EditorID matches \"{t.Text}\" among the {ids:N0} EditorIDs of {looked:N0} {(sigs == null ? "" : string.Join("/", sigs) + " ")}records looked at in {scope.Words}";
                        return hits;
                    }
                case Kind.EditorId:
                    EditorIdHits(scope, t.Text, candidates);
                    nothing = $"no EditorID is \"{t.Text}\" ({scope.EditorIds:N0} EditorIDs in {scope.Words} looked at)";
                    break;
                case Kind.Id:
                    LocalIdHits(scope, t.LocalId, candidates);
                    if (t.AlsoEditorId)
                    {
                        int ids = candidates.Count;
                        EditorIdHits(scope, t.Text, candidates);
                        t.HitBoth = ids > 0 && candidates.Count > ids;
                    }
                    nothing = scope.All
                        ? $"no plugin has a record of its own with the ID {t.LocalId:X6} ({scope.Records:N0} records in {scope.Words} looked at)"
                        : $"no record has the ID {t.LocalId:X6} in {scope.Words} ({scope.Records:N0} records looked at)";
                    if (t.AlsoEditorId) nothing += $", and no EditorID is \"{t.Text}\"";
                    break;
                case Kind.Runtime:
                    {
                        var owner = SlotOwner(main, t.LocalId);
                        uint top = t.LocalId >> 24;
                        uint local = top == 0xFE ? t.LocalId & 0xFFF : top == 0xFD ? t.LocalId & 0xFFFF : t.LocalId & 0xFFFFFF;
                        if (owner == null)
                        {
                            nothing = $"{t.LocalId:X8} was read as a runtime FormID, and no loaded plugin has its slot. A FormID with no slot has at most 6 digits";
                            return candidates;
                        }
                        FormKeyHits(eng, scope, owner.Name, local, candidates);
                        nothing = $"{t.LocalId:X8} was read as a runtime FormID: its slot is {owner.Name}, which has no record {local:X6} ({owner.Segment?.Count ?? 0:N0} records)";
                        break;
                    }
                case Kind.FormKey:
                    {
                        FormKeyHits(eng, scope, t.Plugin, t.LocalId, candidates);
                        var owner = main.Plugin(t.Plugin);
                        bool named = scope.Plugins.Any(s => s.ByPath && s.P.Name.Equals(t.Plugin, StringComparison.OrdinalIgnoreCase));
                        if (owner == null && !named)
                            nothing = main.LoadOrderEntries.Any(e => e.Name.Equals(t.Plugin, StringComparison.OrdinalIgnoreCase))
                                ? $"{t.Plugin} is in the load order (plugins.txt) but its file isn't in Data, so {t.LocalId:X6}:{t.Plugin} can't be looked up"
                                : $"{t.Plugin} isn't in Data, so {t.LocalId:X6}:{t.Plugin} can't be looked up ({main.Plugins.Count} plugins are there)";
                        else if (candidates.Count == 0 && owner != null && main.Versions(owner.Name, t.LocalId) is { } elsewhere)
                            nothing = $"{t.LocalId:X6}:{owner.Name} is held by {string.Join(", ", elsewhere.Holders.Select(h => main.Plugins[h.Plugin].Name))}, not by {scope.Words}";
                        else
                        {
                            var seg = owner?.Segment;
                            string top = seg != null && seg.FirstUnresolved > seg.FirstOwn ? $", its own IDs go up to {seg[seg.FirstUnresolved - 1].LocalId:X6}" : "";
                            nothing = $"no plugin holds {t.LocalId:X6}:{owner?.Name ?? t.Plugin} ({owner?.Name ?? t.Plugin} has {seg?.Count ?? 0:N0} records{top})";
                        }
                        break;
                    }
            }
            // One row per record: the first fitting version in the scope's order
            var seen = new HashSet<(string, uint)>();
            var otherSeen = new HashSet<(string, uint)>();
            var added = new HashSet<Hit>();
            var result = new List<Hit>();
            foreach (var c in candidates.OrderBy(c => c.Place).ThenBy(c => c.Entry))
            {
                var sp = scope.Plugins[c.Place];
                var seg = sp.P.Segment!;
                var e = seg[c.Entry];
                string owner = (e.Owner < sp.Owners.Length ? sp.Owners[e.Owner]?.Name : null) ?? (e.Owner < seg.Names.Count ? seg.Names[e.Owner] : "?");
                if (sigs != null && !sigs.Contains(seg.SignatureOf(e)))
                {
                    // Counted once per record, however many plugins hold it
                    if (otherSeen.Add((owner.ToLowerInvariant(), e.LocalId))) otherTypes.Add(seg.SignatureOf(e));
                    continue;
                }
                if (!seen.Add((owner.ToLowerInvariant(), e.LocalId))) continue;
                // A plugin that holds a FormKey twice (MissionsGalore.esm) shows both records
                int first = c.Entry;
                while (first > 0 && seg[first - 1].Key == e.Key) first--;
                for (int n = first; n < seg.FirstUnresolved && seg[n].Key == e.Key; n++)
                    if ((sigs == null || sigs.Contains(seg.SignatureOf(seg[n]))) && added.Add(new Hit(c.Place, n))) result.Add(new Hit(c.Place, n));
            }
            return result;
        }

        // ---------------------------------------------------------------- In-game names (the link files' name data)
        sealed class NameFiles
        {
            /// <summary>For each plugin of the scope: its link file, or null.</summary>
            public LinkFile?[] Files = Array.Empty<LinkFile?>();
            /// <summary>For each plugin of the scope: names read from its bytes (every name while it has no link file;
            /// with one, only the names of types the link index doesn't name, such as ship modules), or null.</summary>
            public RecordNames.PluginNames?[] Read = Array.Empty<RecordNames.PluginNames?>();
            public int Have, FromBytes;
            public long Count;
            public double ReadMs;
            public List<string> FromBytesNames = new();
        }

        // Names read from a plugin's bytes, kept for the life of the program: per file copy, and whether it was all of them
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, QuickStamp Stamp, Key128 Strings, bool All), RecordNames.PluginNames> ByteNames = new();

        /// <summary>The names the scope's plugins can be searched by. Nothing waits for the link index: a plugin with no
        /// link file yet has its names read from its bytes (the whole load order: 5 s the first time, 0.8 s after).</summary>
        static NameFiles NameData(Scope scope, string data)
        {
            var n = new NameFiles { Files = new LinkFile?[scope.Plugins.Count], Read = new RecordNames.PluginNames?[scope.Plugins.Count] };
            var schema = EngineRecords.Schema;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int place = 0; place < scope.Plugins.Count; place++)
            {
                var sp = scope.Plugins[place];
                if (sp.P.Segment is not { } seg) continue;
                var f = sp.ByPath || !EngineLinks.Walkable(sp.P) ? null : EngineLinks.For(sp.P);
                n.Files[place] = f;
                bool all = f == null;
                var world = WorldTypes.Of(seg);
                // With a link file: only the types whose name the link index doesn't have. Of world data, cells and
                // worldspaces are read for their own name subrecord (the link index leaves world data out); placed
                // objects and the rest of world data are never read here
                var mask = seg.Signatures.Select((s, i) => NamedWorld(s.Signature) || !world[i] && (all || !RecordNames.PlaceOf(schema, s.Signature).FromSchema)).ToArray();
                RecordNames.PluginNames? read = null;
                if (mask.Any(m => m))
                {
                    var key = (sp.P.Path.ToLowerInvariant(), sp.P.Stamp, sp.P.Strings.Key, all);
                    if (!ByteNames.TryGetValue(key, out read))
                    {
                        read = RecordNames.All(sp.P, mask, schema, dataFolder: data);
                        ByteNames[key] = read;
                        n.ReadMs += read.Ms;
                    }
                    // A plugin whose link file is made now needs its whole list no longer
                    if (!all) ByteNames.TryRemove((key.Item1, key.Stamp, key.Item3, true), out _);
                }
                n.Read[place] = read;
                if (f != null) { n.Have++; n.Count += f.NameCount; }
                if (all) { n.FromBytes++; if (n.FromBytesNames.Count < 4) n.FromBytesNames.Add(sp.P.Name); }
                n.Count += read?.Names.Count ?? 0;
            }
            _ = clock;
            return n;
        }

        /// <summary>The types of world data that have names worth a search: cells and worldspaces. Their names are read
        /// from their own records' bytes (the header index says where each one is), never from what they hold.</summary>
        static bool NamedWorld(string signature) => signature is "CELL" or "WRLD";

        static string SignatureOf(Scope scope, Hit h)
        {
            var seg = scope.Plugins[h.Place].P.Segment!;
            return seg.SignatureOf(seg[h.Entry]);
        }

        /// <summary>The record a hit stands for: its owner plugin and its ID.</summary>
        static (string, uint) KeyOf(Scope scope, Hit h)
        {
            var sp = scope.Plugins[h.Place];
            var seg = sp.P.Segment!;
            var e = seg[h.Entry];
            string owner = (e.Owner < sp.Owners.Length ? sp.Owners[e.Owner]?.Name : null) ?? (e.Owner < seg.Names.Count ? seg.Names[e.Owner] : "?");
            return (owner.ToLowerInvariant(), e.LocalId);
        }

        /// <summary>
        /// The records whose in-game name holds every word (whole words, letter case aside), or fits the pattern.
        /// One row per record: the first version in the scope's order whose name fits. Records whose name is the words
        /// and nothing more come first ("Akila City" the worldspace before "Akila City Security"), each part in the
        /// scope's order.
        /// </summary>
        static List<Hit> NameHits(Scope scope, NameFiles names, List<string>? words, Glob? glob, HashSet<string>? sigs)
        {
            var perPlugin = new List<(Hit Hit, bool Exact)>?[scope.Plugins.Count];
            byte[][]? ascii = words != null && words.All(w => w.All(c => c < 128)) ? words.Select(w => System.Text.Encoding.ASCII.GetBytes(w.ToLowerInvariant())).ToArray() : null;
            string? whole = words == null ? null : string.Join(" ", words);
            bool Exact(string? name) => whole != null && name != null && string.Equals(name.Trim(), whole, StringComparison.OrdinalIgnoreCase);
            System.Threading.Tasks.Parallel.For(0, scope.Plugins.Count, place =>
            {
                var seg = scope.Plugins[place].P.Segment;
                if (seg == null) return;
                List<(Hit, bool)>? mine = null;
                if (names.Read[place] is { } read)
                    foreach (var (entry, name) in read.Names)
                    {
                        if (!(glob != null ? glob.IsMatch(name) : words!.All(w => HasWord(name, w)))) continue;
                        if (entry >= seg.FirstUnresolved) continue;
                        if (sigs != null && !sigs.Contains(seg.SignatureOf(seg[entry]))) continue;
                        (mine ??= new()).Add((new Hit(place, entry), Exact(name)));
                    }
                var f = names.Files[place];
                if (f == null || f.NameCount == 0) { perPlugin[place] = mine; return; }
                for (int i = 0; i < f.NameCount; i++)
                {
                    bool fits;
                    if (ascii != null)
                    {
                        var bytes = AfterTag(f.NameBytes(i));
                        fits = true;
                        foreach (var w in ascii) if (!HasWord(bytes, w)) { fits = false; break; }
                    }
                    else
                    {
                        string name = SFSearch.Records.CardBuilder.NoTag(f.NameAt(i)) ?? "";
                        fits = glob != null ? glob.IsMatch(name) : words!.All(w => HasWord(name, w));
                    }
                    if (!fits) continue;
                    int entry = f.NameEntry(i);
                    if (entry >= seg.FirstUnresolved) continue;
                    if (sigs != null && !sigs.Contains(seg.SignatureOf(seg[entry]))) continue;
                    (mine ??= new()).Add((new Hit(place, entry), Exact(whole == null ? null : SFSearch.Records.CardBuilder.NoTag(f.NameAt(i)))));
                }
                perPlugin[place] = mine;
            });
            var seen = new HashSet<(string, uint)>();
            var result = new List<(Hit Hit, bool Exact)>();
            foreach (var list in perPlugin)
                if (list != null)
                    foreach (var h in list.OrderBy(h => h.Hit.Entry))
                        if (seen.Add(KeyOf(scope, h.Hit))) result.Add(h);
            return result.Where(h => h.Exact).Concat(result.Where(h => !h.Exact)).Select(h => h.Hit).ToList();
        }

        /// <summary>A stored name without the Creation Kit's tag in front of it (&lt;ID=0000793F&gt;).</summary>
        static ReadOnlySpan<byte> AfterTag(ReadOnlySpan<byte> name)
        {
            if (name.Length < 5 || name[0] != (byte)'<' || name[1] != (byte)'I' || name[2] != (byte)'D' || name[3] != (byte)'=') return name;
            int end = name.IndexOf((byte)'>');
            return end < 5 ? name : name[(end + 1)..];
        }

        static bool IsWordByte(byte b) => b >= 128 || b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

        /// <summary>True when the text holds the word (given in lower case, ASCII) as a whole word, letter case aside.</summary>
        static bool HasWord(ReadOnlySpan<byte> text, byte[] word)
        {
            int n = word.Length;
            for (int i = 0; i + n <= text.Length; i++)
            {
                byte c = text[i];
                if ((c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c) != word[0]) continue;
                if (i > 0 && LetterBefore(text, i) && IsWordByte(word[0])) continue;
                int k = 1;
                for (; k < n; k++)
                {
                    byte d = text[i + k];
                    if ((d is >= (byte)'A' and <= (byte)'Z' ? (byte)(d + 32) : d) != word[k]) break;
                }
                if (k < n) continue;
                if (i + n < text.Length && LetterAt(text, i + n) && IsWordByte(word[n - 1])) continue;
                return true;
            }
            return false;
        }

        // A name's bytes are UTF-8: a character outside ASCII ends a word unless it is a letter or digit, as in the
        // search of names read as text (HasWord on a string), so "Samson" is a word of "Samson’s Art" in both
        static bool LetterAt(ReadOnlySpan<byte> text, int at) =>
            text[at] < 128 ? IsWordByte(text[at]) : System.Text.Rune.DecodeFromUtf8(text[at..], out var r, out _) != System.Buffers.OperationStatus.Done || System.Text.Rune.IsLetterOrDigit(r);

        static bool LetterBefore(ReadOnlySpan<byte> text, int at) =>
            text[at - 1] < 128 ? IsWordByte(text[at - 1]) : System.Text.Rune.DecodeLastFromUtf8(text[..at], out var r, out _) != System.Buffers.OperationStatus.Done || System.Text.Rune.IsLetterOrDigit(r);

        static bool HasWord(string text, string word)
        {
            for (int at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                bool before = at == 0 || !char.IsLetterOrDigit(text[at - 1]) || !char.IsLetterOrDigit(word[0]);
                bool after = at + word.Length >= text.Length || !char.IsLetterOrDigit(text[at + word.Length]) || !char.IsLetterOrDigit(word[^1]);
                if (before && after) return true;
            }
            return false;
        }

        static void EditorIdHits(Scope scope, string text, List<Hit> into)
        {
            var found = new List<int>();
            for (int place = 0; place < scope.Plugins.Count; place++)
            {
                var seg = scope.Plugins[place].P.Segment;
                if (seg == null) continue;
                found.Clear();
                seg.FindEditorId(text, found);
                foreach (int entry in found) if (entry < seg.FirstUnresolved) into.Add(new Hit(place, entry));
            }
        }

        /// <summary>Records with this local ID. With no plugin named: each plugin's own. With plugins named: also the masters' records they hold.</summary>
        static void LocalIdHits(Scope scope, uint id, List<Hit> into)
        {
            for (int place = 0; place < scope.Plugins.Count; place++)
            {
                var seg = scope.Plugins[place].P.Segment;
                if (seg == null) continue;
                for (int owner = scope.All ? seg.SelfOwner : 0; owner <= seg.SelfOwner; owner++)
                    for (int at = seg.Find(owner, id); at >= 0 && at < seg.FirstUnresolved && seg[at].Owner == owner && seg[at].LocalId == id; at++)
                        into.Add(new Hit(place, at));
            }
        }

        static void FormKeyHits(EngineIndex eng, Scope scope, string plugin, uint id, List<Hit> into)
        {
            if (scope.All)
            {
                var versions = eng.Index.Versions(plugin, id);
                if (versions == null) return;
                foreach (var h in versions.Holders)
                    if (scope.Holds(h.Plugin)) into.Add(new Hit(scope.PlaceOfMain[h.Plugin], h.Entry));
                return;
            }
            for (int place = 0; place < scope.Plugins.Count; place++)
            {
                var seg = scope.Plugins[place].P.Segment;
                if (seg == null) continue;
                int owner = seg.OwnerOf(plugin);
                if (owner < 0) continue;
                for (int at = seg.Find(owner, id); at >= 0 && at < seg.FirstUnresolved && seg[at].Owner == owner && seg[at].LocalId == id; at++)
                    into.Add(new Hit(place, at));
            }
        }

        // ---------------------------------------------------------------- A row
        sealed class Row
        {
            public string Record = "", Type = "", HeldBy = "", Name = "";
            public string? Inside;
            public List<IndexedPlugin> Holders = new();
        }

        static string LabelOf(EngineIndex eng, Scope scope, Hit h)
        {
            var sp = scope.Plugins[h.Place];
            return ProblemNotes.Label(sp.Ix.Describe(new RecordRef(sp.P.Id, h.Entry)));
        }

        static Row BuildRow(EngineIndex eng, Scope scope, SchemaTypes schema, Hit h, bool byId, HashSet<string>? sigs)
        {
            var main = eng.Index;
            var sp = scope.Plugins[h.Place];
            var here = sp.Ix.Describe(new RecordRef(sp.P.Id, h.Entry));
            HeaderIndex.TryParseFormKey(here.FormKey, out string owner, out uint id);
            var versions = main.Plugin(owner) != null ? main.Versions(owner, id) : null;
            var row = new Row();

            // The record is named as the game sees it, by its winning version, when that is what was asked for: an ID, or
            // a search the winning version fits too. A version that was found by its own type or EditorID, which the
            // winner doesn't share (a patch built for an older master), is named as itself, and so is a file outside Data.
            RecordInfo shown = here;
            bool second = false;
            if (!sp.ByPath && versions != null)
            {
                var w = versions.Winner >= 0 ? versions.Holders[versions.Winner] : versions.Holders[0];
                // The same plugin holds this FormKey twice, and this is the other record: it is named as itself
                second = versions.Holders.Any(x => x.Plugin == sp.P.Id && x.Entry != h.Entry);
                var win = main.Describe(w);
                bool sameThing = win.Signature == here.Signature && string.Equals(win.EditorId, here.EditorId, StringComparison.OrdinalIgnoreCase);
                if (!second && (sameThing || byId && (sigs == null || sigs.Contains(win.Signature)))) shown = win;
            }
            row.Record = ProblemNotes.Label(shown) + (second ? " [a second record with this FormKey in the same plugin]" : "");
            row.Type = schema.Label(shown.Signature);
            // The in-game name of the version that is shown, read from that record's own bytes (no link index needed)
            IndexedPlugin nameHolder; int nameEntry;
            {
                bool self = sp.ByPath || ReferenceEquals(shown, here) || versions == null;
                var from = self ? new RecordRef(sp.P.Id, h.Entry) : versions!.Winner >= 0 ? versions.Holders[versions.Winner] : versions.Holders[0];
                nameHolder = self ? sp.P : main.Plugins[from.Plugin];
                nameEntry = from.Entry;
                row.Name = RecordNames.Of(nameHolder, nameEntry, EngineRecords.Schema, dataFolder: eng.DataFolder) ?? "";
            }

            var names = new List<string>();
            if (sp.ByPath) names.Add($"{sp.Shown} (the file you named, outside Data)");
            if (versions != null)
            {
                // The winner, then the other loaded plugins from the last loaded to the first, then the ones not loaded
                var order = new List<int>();
                if (versions.Winner >= 0) order.Add(versions.Winner);
                for (int i = versions.Holders.Count - 1; i >= 0; i--)
                    if (i != versions.Winner && main.Plugins[versions.Holders[i].Plugin].Order.Enabled) order.Add(i);
                for (int i = 0; i < versions.Holders.Count; i++)
                    if (!main.Plugins[versions.Holders[i].Plugin].Order.Enabled) order.Add(i);
                foreach (int i in order)
                {
                    var p = main.Plugins[versions.Holders[i].Plugin];
                    row.Holders.Add(p);
                    if (names.Count >= MostHolders) continue;
                    var v = main.Describe(versions.Holders[i]);
                    string name = p.Name;
                    if (!second && v.Signature != shown.Signature) name += $" [as {v.Signature}]";
                    if (!second && !string.Equals(v.EditorId, shown.EditorId, StringComparison.Ordinal)) name += $" (as {v.EditorId ?? "[no EditorID]"})";
                    if (!p.Order.Enabled) name += " (not loaded)";
                    else if (p.LeftOutShort != null) name += $" ({p.LeftOutShort}, left out)";
                    names.Add(name);
                }
                if (order.Count + (sp.ByPath ? 1 : 0) > names.Count) names.Add($"+{order.Count + (sp.ByPath ? 1 : 0) - names.Count} more");
            }
            else if (!sp.ByPath) { names.Add(sp.Shown + (sp.P.Order.Enabled ? "" : " (not loaded)")); row.Holders.Add(sp.P); }
            row.HeldBy = (versions is { Winner: < 0 } && !sp.ByPath ? NoWinner(main, versions) : "") + string.Join(" > ", names);

            if (here.Parent != null)
            {
                row.Inside = here.Parent;
                if (HeaderIndex.TryParseFormKey(here.Parent, out string po, out uint pid) && main.Plugin(po) != null && main.Versions(po, pid) is { } pv)
                {
                    var parent = main.Describe(pv.Winner >= 0 ? pv.Holders[pv.Winner] : pv.Holders[0]);
                    row.Inside = $"{parent.Signature} {ProblemNotes.Label(parent)}";
                }
                // An exterior cell: its worldspace, and its place on the worldspace's grid (none for the persistent cell)
                if (shown.Signature == "CELL" && RecordNames.CellGrid(nameHolder, nameEntry) is { } grid)
                    row.Inside += $", grid {grid.X}, {grid.Y}";
            }
            else if (shown.Signature == "CELL") row.Inside = "interior";
            return row;
        }
    }
}
