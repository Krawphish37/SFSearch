using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Reports;

namespace SFSearch.Engine
{
    /// <summary>
    /// refs (tool sfs_refs, command-line verb refs2; Refs was the older command, removed in 2.0): what points at a record, what
    /// it points at, or both. Links into a record are found two ways, both exact (design\agent-first\ARCHITECTURE.md
    /// section 5): from the link index of each plugin that has one, and by a search of the plugin's bytes for the
    /// target's FormID, each hit confirmed by reading that record through Mutagen. World records (cells' content,
    /// navmeshes, terrain) are never in the link index: they are read from the world link index of a plugin that has one
    /// (J32: made only when the user asks, indexes build world), else their bytes are searched as above.
    /// The answer always says what was searched and what wasn't.
    /// </summary>
    public static class RefsTool
    {
        public const int DefaultLimit = 40, MostLimit = 2000;
        /// <summary>For the "what it would cost" line: a byte search reads about this much a second, hits to confirm aside
        /// (measured on this build: Starfield.esm's 1.1 GB of world records in about 0.5 s on 8 threads).</summary>
        const double SearchBytesPerSecond = 1.0e9;
        /// <summary>World data is searched by default when the plugins that can point at the record hold at most this
        /// much of it. Measured (notes\fix-tools.md, B6): 26 MB took 28 ms; Starfield.esm's 2.73 GB in 119 plugins took
        /// 2.7 to 2.9 s for the search alone, and 3.2 to 24 s with the hits to confirm.</summary>
        public const long AutoWorldBytes = 256L << 20;

        public const string Help = """
            refs2 <record> [--direction in|out|both] [--in <plugin,...>] [--types <type,...>] [--world | --world=save-index | --no-world] [--search index|bytes]
                  [--plugin <plugin>] [--limit N] [--cursor C]
              What points at a record (in, the default), what it points at (out), or both. Read live: no database is read or built.

              record       a FormKey (0476C4:Starfield.esm), an EditorID, or a FormID with --plugin
              --direction  in: the records that link to it. out: the records it links to. both.
              --in         the plugins to search for links into it: names (wildcards allowed), a set (@official, @loaded,
                           @all), or the path of a plugin file. Left out: every plugin that could point at it,
                           which is the record's own plugin and every plugin that lists it as a master.
              --types      only records of these types (signatures or Mutagen names): the records that point, or for out the targets
              --world      always search world data (cells, placed references, navmeshes, terrain). It is read from the
                           world link index of each plugin that has one (made only when you ask: indexes build world; a
                           fraction of a second), and searched by its bytes in the others. Left out of the call: a world
                           link index is always used, and the bytes are searched when the plugins without one hold 256 MB
                           of world data or less (a mod's record: tens of ms); more (any record of Starfield.esm: 2.7 GB,
                           5 to 15 s) is left out, and the answer's first lines say so.
              --world=save-index  search world data now as --world does, then save the world link index of the plugins
                           whose world data was searched by its bytes (made after the answer: by the service in the background,
                           or right after it without the service); refs into world data then answers in about 0.2 s. It is
                           kept and kept up to date: indexes shows it, indexes drop world deletes it. (MCP: world "save_index")
              --no-world   never search world data
              --search     index (default): the link index and the world link index where a plugin has one, its bytes where it has none yet.
                           bytes: every plugin's bytes, both indexes left aside. Slower, the same links: it is how the indexes are checked.
              --plugin     for out: which plugin's version of the record to read (default: the one the game uses)
              --limit      rows per answer (default 40). The answer's [more] line gives the cursor for the next rows.

              Links into a record come from the link index of each plugin that has one, and from a search of the plugin's
              bytes where it has none yet; both give the same links. Each row is one record and one field: the field is the
              link's path in the record's JSON, with list indexes left out, and "x3" when the record has three such links.
              "Wins" says whether that plugin's version of the pointing record is the one the game uses.
              Exit code: 0 answered (also when nothing points at it: the answer says what was searched); 2 an error (one
              line, Error: ... Fix: ...); 7 not run: heavy work that wasn't allowed; 8 not enough memory free for it.
            """;

        static readonly string[] Known = { "record", "plugin", "direction", "in", "types", "world", "search", "limit", "cursor" };

        sealed class Ask
        {
            public string Record = "";
            public string? Plugin, Direction, In, Types, Cursor, Search;
            /// <summary>True: always search world data. False: never. Null: when it is small (<see cref="AutoWorldBytes"/>).</summary>
            public bool? World;
            /// <summary>world: "save_index" (J33, the third answer): search world data now, and save the world link index of
            /// the plugins whose world data was searched by its bytes, after the answer.</summary>
            public bool SaveIndex;
            public int Limit = DefaultLimit;
        }

        // ---------------------------------------------------------------- Entry points
        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            var answer = new EngineAnswer { AsTool = true };
            if (a.Unknown(Known) is { Count: > 0 } wrong)
                answer.Fail($"refs has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", $"its arguments are: {string.Join(", ", Known)}", 2);
            else
                Guarded(new Ask { Record = a.Text("record") ?? "", Plugin = a.Text("plugin"), Direction = a.Text("direction"), In = a.Text("in"), Types = a.Text("types"), World = a.Text("world") is null ? null : a.Flag("world"), SaveIndex = HeavyAnswers.IsSave(a.Text("world")), Search = a.Text("search"), Cursor = a.Text("cursor"), Limit = a.Int("limit") ?? DefaultLimit }, answer);
            answer.Emit();
        }

        public static void Verb(List<string> args)
        {
            if (args.Count == 0 || args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            // --world=save-index: the third answer (J33): search world data now and save its world link index after the answer
            bool save = args.RemoveAll(x => x.StartsWith("--world=", StringComparison.OrdinalIgnoreCase) && HeavyAnswers.IsSave(x["--world=".Length..])) > 0;
            var ask = new Ask
            {
                Plugin = CommandLine.TakeOption(args, "--plugin"), Direction = CommandLine.TakeOption(args, "--direction"), In = CommandLine.TakeOption(args, "--in"),
                Types = CommandLine.TakeOption(args, "--types") ?? CommandLine.TakeOption(args, "--type"), Cursor = CommandLine.TakeOption(args, "--cursor"), World = save || CommandLine.TakeFlag(args, "--world") ? true : CommandLine.TakeFlag(args, "--no-world") ? false : null, Search = CommandLine.TakeOption(args, "--search"),
                SaveIndex = save,
            };
            var answer = new EngineAnswer();
            string? limit = CommandLine.TakeOption(args, "--limit");
            var stray = args.Where(x => x.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (limit != null && !int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out ask.Limit))
                answer.Fail($"--limit takes a number, not '{limit}'", "give the number of rows, e.g. --limit 100", 2);
            else if (stray.Count > 0)
                answer.Fail($"refs2 has no option {string.Join(", ", stray)}", "its options are --direction, --in, --types, --world, --world=save-index, --no-world, --search, --plugin, --limit and --cursor (refs2 --help shows them)", 2);
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
            catch (RecordException e) { RecordTool.FailRecord(answer, e); }
            catch (FileNotFoundException e) { answer.Fail(e.Message.Split('\n')[0], "set the Data folder with SetData, or the SFSEARCH_DATA environment variable"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                answer.Fail($"the links couldn't be read ({e.Message.TrimEnd('.')})", "ask again; if it stays, run loadorder2 problems to see which plugin is at fault");
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

        // ---------------------------------------------------------------- The rows
        /// <summary>One record of one plugin that points at the target through one field.</summary>
        readonly record struct InHit(int Place, int Entry, string Field, int Count, bool ByBytes);
        readonly record struct OutLink(string Path, string Target, string? Sig);
        /// <summary>What reading one byte hit through Mutagen said: the fields that point at the target and how often, none (not a link), or why it couldn't be read.</summary>
        readonly record struct Confirmed(int Place, int Phase, int Entry, Dictionary<string, int>? Fields, string? Why);

        sealed class Searched
        {
            public int IndexPlugins, BytePlugins, WorldPlugins, WorldSkippedPlugins, UnreadSearched, WorldIndexPlugins, WorldUnreadSearched;
            public long IndexRecords, ByteRecords, WorldRecords, WorldSkippedRecords, WorldSkippedBytes, WorldIndexRecords;
            public double ByteMs, WorldMs, ConfirmMs;
            public int Confirmed;
            /// <summary>World data was searched because it was small, without being asked for.</summary>
            public bool WorldByDefault;
            public bool? WorldAsked;
            public List<string> ByteNames = new();
            /// <summary>The plugins whose world data was left out, and those whose world data was searched by its bytes
            /// (they have no world link file): what the world link index would be made for (J33).</summary>
            public List<IndexedPlugin> WorldSkippedList = new(), WorldByteList = new();
            public long WorldByteBytes;
            public List<(int Place, int Entry)> NotLinks = new();
            public List<(int Place, int Entry, string Why)> Unreadable = new();
        }

        // ---------------------------------------------------------------- Run
        static void Run(Ask ask, EngineAnswer answer)
        {
            string direction = (ask.Direction ?? "in").Trim().ToLowerInvariant();
            if (direction is not ("in" or "out" or "both")) { answer.Fail($"refs has no direction '{ask.Direction}'", "use in (what points at it), out (what it points at) or both", 2); return; }
            int limit = ask.Limit <= 0 ? DefaultLimit : Math.Min(ask.Limit, MostLimit);
            string search = (ask.Search ?? "index").Trim().ToLowerInvariant();
            if (search is not ("index" or "bytes")) { answer.Fail($"refs has no search '{ask.Search}'", "use index (the link index where there is one, the default) or bytes (every plugin's bytes)", 2); return; }
            string question = $"{ask.Record.Trim()}|{ask.Plugin?.Trim()}|{direction}|{ask.In?.Trim()}|{ask.Types?.Trim()}|{(ask.World == false ? "never" : (ask.World ?? false).ToString())}|{search}".ToLowerInvariant();
            if (!Cursors.TryRead(ask.Cursor, question, out int offset))
            {
                answer.Fail($"the cursor \"{ask.Cursor}\" doesn't belong to this question", "ask again with the same record, direction, in, types and world as the answer that gave the cursor, or leave the cursor out to start over", 2);
                return;
            }

            var eng = EngineIndex.Get();
            var validity = eng.Check();
            answer.Checked(validity);
            if (validity.Cold || validity.Report.AnythingChanged) { EngineLinks.Trim(eng.Index); WorldLinks.Trim(eng.Index); }
            var main = eng.Index;
            var schema = SchemaTypes.Default;

            HashSet<string>? sigs = null;
            if (!string.IsNullOrWhiteSpace(ask.Types))
            {
                sigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in ask.Types!.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (schema.SigOf(raw) is { } sig) { sigs.Add(sig); continue; }
                    if (schema.TypesOf(raw).Count > 0 || raw.Length == 4 && main.Plugins.Any(p => p.Segment != null && p.Segment.Signatures.Any(s => s.Signature.Equals(raw, StringComparison.OrdinalIgnoreCase)))) { sigs.Add(raw.ToUpperInvariant()); continue; }
                    var near = schema.Nearest(raw);
                    answer.Fail($"there is no record type '{raw}'", near.Count > 0 ? $"the nearest real names: {string.Join("; ", near)}. A type is a signature (WEAP) or a Mutagen name (Weapon)" : "use a signature (WEAP) or a Mutagen name (Weapon)", 2);
                    return;
                }
            }

            // ---- The record. For "in" a FormKey that no plugin holds can still be asked about: links to nothing are worth finding
            Picked? pick = null;
            string owner; uint localId; string targetLabel;
            bool nothing = false;
            if (direction == "in" && string.IsNullOrWhiteSpace(ask.Plugin) && HeaderIndex.TryParseFormKey(ask.Record.Trim().Trim('"', '\''), out var fo, out var fid)
                && main.Plugin(fo) is { } op && main.Versions(op.Name, fid) == null)
            {
                owner = op.Name; localId = fid; nothing = true;
                targetLabel = $"[no such record] ({fid:X6}:{op.Name})";
                answer.Note($"No plugin holds {fid:X6}:{op.Name}: a link to it points at nothing. Links to it are searched all the same.");
            }
            else
            {
                pick = RecordPick.Resolve(eng, ask.Record, ask.Plugin, answer, "refs2");
                if (pick == null) return;
                owner = pick.Owner; localId = pick.LocalId;
                targetLabel = ProblemNotes.Label(pick.Info);
                if (!ProblemNotes.Add(answer, new[] { (pick.Ix, pick.Plugin, pick.ByPath ? (string?)pick.Plugin.Path : null) }, required: true)) return;
            }

            var uses = new List<(RecordEngine Engine, SessionUse Use)>();
            RecordEngine EngineFor(HeaderIndex ix) => EngineRecords.For(eng, ix);
            SessionUse Use(HeaderIndex ix, IndexedPlugin p)
            {
                var engine = EngineFor(ix);
                var use = engine.Sessions.Use(p.Name);
                foreach (var n in use.Notes) answer.Note(n);
                // The index looked at the files again because one changed since the check at the start of this command
                if (use.Refresh is { AnythingChanged: true }) throw new RecordException($"{p.Name} changed during the read: run the command again.");
                uses.Add((engine, use));
                return use;
            }

            // ---- Out: the links of the record's own JSON
            var outRows = new List<OutLink>();
            int children = 0, outNull = 0;
            if (direction is "out" or "both")
            {
                var engine = EngineFor(pick!.Ix);
                Use(pick.Ix, pick.Plugin);
                bool wasHeavy = engine.AllowHeavy;
                engine.AllowHeavy = true; // walked only: no JSON, no card
                LoadedRecord rec;
                try { rec = engine.Load(pick.Ref); } finally { engine.AllowHeavy = wasHeavy; }
                var sink = RecordLinks.Collector(indexed: true);
                RecordLinks.Walk(rec.Record, sink);
                children = rec.Children;
                foreach (var (field, target) in sink.Links)
                {
                    string key = $"{target.ID:X6}:{target.ModKey.FileName.String}";
                    string? sig = null;
                    if (main.Plugin(target.ModKey.FileName.String) is { } tp && main.Versions(tp.Name, target.ID) is { } tv)
                        sig = main.Describe(tv.Winner >= 0 ? tv.Holders[tv.Winner] : tv.Holders[0]).Signature;
                    if (sigs != null && (sig == null || !sigs.Contains(sig))) continue;
                    outRows.Add(new OutLink(sink.Paths[field], key, sig));
                }
                answer.Read($"1 {pick.Info.Signature} of {pick.Plugin.Name}");
                _ = outNull;
            }

            // ---- In: every plugin that could point at it
            var inHits = new List<InHit>();
            var places = new List<(HeaderIndex Ix, IndexedPlugin P, bool ByPath)>();
            var searched = new Searched();
            var cantPoint = new List<string>();
            // A set among the plugins (@official): how it was named, and its plugins that can't point at the record (one note for them all)
            string? shownSet = null;
            var setCantPoint = new List<string>();
            var leftOut = new List<(HeaderIndex, IndexedPlugin, string?)>();
            if (direction is "in" or "both")
            {
                bool Could(IndexedPlugin p) => p.Name.Equals(owner, StringComparison.OrdinalIgnoreCase) || (p.Header?.Masters.Any(m => m.Equals(owner, StringComparison.OrdinalIgnoreCase)) ?? false);
                if (string.IsNullOrWhiteSpace(ask.In))
                    foreach (var p in main.Plugins.Where(p => p.Segment != null && Could(p)).OrderBy(EngineIndex.Rank)) places.Add((main, p, false));
                else
                {
                    // Names, patterns and sets (@official) through the one resolver every verb uses (GAPS S117)
                    PluginNaming naming;
                    try { naming = PluginSets.Resolve(main, Home.SplitPluginList(ask.In!), winnersAllowed: false, verb: "refs2"); }
                    catch (PluginSetException e) { answer.Fail(e.Message, e.Fix, 2); return; }
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in naming.Picks)
                    {
                        if (item.Plugin is { } p && item.Kind != PluginPickKind.Name)
                        {
                            if (!seen.Add(p.Name)) continue;
                            // A plugin a set or a pattern holds that can't be read is left out, and said; one named by itself is an error
                            if (ProblemNotes.Blocking(p) is { } cantRead) { answer.Problem($"{cantRead.What}. It was left out of {item.Item}."); continue; }
                            if (Could(p)) places.Add((main, p, false)); else (item.Kind == PluginPickKind.Set ? setCantPoint : cantPoint).Add(p.Name);
                            continue;
                        }
                        var named = RecordPick.Plugin(eng, item.Item, answer);
                        if (named == null) return;
                        if (!seen.Add(named.Value.ByPath ? named.Value.Plugin.Path : named.Value.Plugin.Name)) continue;
                        if (ProblemNotes.Blocking(named.Value.Plugin, named.Value.ByPath ? named.Value.Plugin.Path : null) is { } blocked) { answer.Fail(blocked.What, blocked.Fix); return; }
                        if (Could(named.Value.Plugin)) places.Add(named.Value); else cantPoint.Add(named.Value.Plugin.Name);
                    }
                    places = places.OrderBy(s => s.ByPath ? 1 : 0).ThenBy(s => s.ByPath ? 0 : EngineIndex.Rank(s.P)).ToList();
                    if (naming.NamesASet) shownSet = naming.Shown(naming.Picks.Select(x => x.Plugin?.Name ?? x.Item).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                    // A plugin that was named and can't be read is an error: nothing is mapped by guess (trap 14)
                    foreach (var (_, p, byPath) in places)
                        if (ProblemNotes.Blocking(p, byPath ? p.Path : null) is { } block) { answer.Fail(block.What, block.Fix); return; }
                }

                // World data: from the world link index of a plugin that has one (J32), unless never asked for; the
                // others' bytes always, never, or (not asked) when those plugins hold little of it
                WorldLinkFile? WorldFile(IndexedPlugin p) => search == "bytes" || ask.World == false ? null : WorldLinks.For(p);
                long worldHeld = places.Where(s => ProblemNotes.Blocking(s.P) == null && s.P.Segment != null && WorldFile(s.P) == null).Sum(s => WorldTypes.Totals(s.P.Segment!).Bytes);
                searched.WorldByDefault = ask.World == null && worldHeld <= AutoWorldBytes;
                searched.WorldAsked = ask.World;
                bool searchWorld = ask.World ?? worldHeld <= AutoWorldBytes;
                var noLink = LinkSubrecords.For(EngineFor(main).Schema);
                // The byte searches are gathered first, so that all plugins are searched at once: (place, phase, job). Phase
                // 0 is a plugin's records outside world data (it has no link file yet, or its link file couldn't read them),
                // phase 1 its world data. Rows and notes keep the order they had when each plugin was searched in turn.
                var outside = new List<(int Place, int Phase, ByteSearch.Job Job, bool Unread)>();
                var worldJobs = new List<(int Place, int Phase, ByteSearch.Job Job, bool Unread)>();
                var byteSearched = new List<int>();
                // Each place's target as its own segment numbers it (owner << 24 | local ID), for the world records' byte readers
                var keyOf = new ulong[places.Count];
                for (int place = 0; place < places.Count; place++)
                {
                    var (ix, p, byPath) = places[place];
                    if (ProblemNotes.Blocking(p) != null || p.Segment == null) { leftOut.Add((ix, p, byPath ? p.Path : null)); continue; }
                    var seg = p.Segment;
                    int o = seg.OwnerOf(owner);
                    var raw = o < 0 ? null : FormIdPattern.For(p, o, localId);
                    if (o < 0 || raw == null) continue; // it can't write that FormID at all (a local ID past the owner's size)
                    ulong key = (ulong)o << 24 | localId;
                    keyOf[place] = key;
                    var world = WorldTypes.Of(seg);
                    var (worldRecords, worldBytes) = WorldTypes.Totals(seg);
                    // Only the types asked for, when types were given
                    bool[]? typeMask = sigs == null ? null : seg.Signatures.Select(s => sigs.Contains(s.Signature)).ToArray();
                    bool[] Mask(bool wantWorld) => seg.Signatures.Select((s, i) => world[i] == wantWorld && (typeMask == null || typeMask[i])).ToArray();

                    var lf = search == "bytes" ? null : EngineLinks.For(p);
                    if (lf != null)
                    {
                        searched.IndexPlugins++;
                        searched.IndexRecords += lf.Walked - lf.UnreadCount;
                        var (first, end) = lf.Range(key);
                        var links = lf.Links;
                        for (int i = first; i < end;)
                        {
                            int j = i + 1;
                            while (j < end && links[j].From == links[i].From && links[j].Field == links[i].Field) j++;
                            int entry = (int)links[i].From;
                            if (typeMask == null || typeMask[seg[entry].Signature]) inHits.Add(new InHit(place, entry, lf.Fields[(int)links[i].Field], j - i, false));
                            i = j;
                        }
                        if (lf.UnreadCount > 0)
                        {
                            // Records the walk couldn't read through Mutagen: their bytes are searched, so nothing is left out unsaid
                            outside.Add((place, 0, new ByteSearch.Job(p, raw, typeMask, lf.Unread.ToArray()), true));
                            searched.UnreadSearched += lf.UnreadCount;
                        }
                    }
                    else
                    {
                        outside.Add((place, 0, new ByteSearch.Job(p, raw, Mask(false)), false));
                        searched.BytePlugins++;
                        if (searched.ByteNames.Count < 6) searched.ByteNames.Add(p.Name);
                        byteSearched.Add(place);
                    }
                    var wf = worldRecords > 0 ? WorldFile(p) : null;
                    if (wf != null)
                    {
                        // The world link index: the records, fields and counts, read from the file; no byte search
                        searched.WorldIndexPlugins++;
                        searched.WorldIndexRecords += wf.Records - wf.UnreadCount;
                        var (first, end) = wf.Range(key);
                        var postings = wf.Postings;
                        for (int i = first; i < end; i++)
                        {
                            int entry = (int)postings[i].From;
                            if (typeMask == null || typeMask[seg[entry].Signature])
                                inHits.Add(new InHit(place, entry, wf.Fields[postings[i].Field], (int)Math.Min(int.MaxValue, wf.CountAt(i)), false));
                        }
                        if (wf.UnreadCount > 0)
                        {
                            // World records the build couldn't read: their bytes are searched, so nothing is left out unsaid
                            var worldMask = Mask(true);
                            worldJobs.Add((place, 1, new ByteSearch.Job(p, raw, worldMask, wf.Unread.ToArray(), noLink.For(seg, worldMask)), true));
                            searched.WorldUnreadSearched += wf.UnreadCount;
                        }
                    }
                    else if (searchWorld && worldRecords > 0)
                    {
                        // A hit whose bytes sit only in subrecords with no link field (a placed object's position) is no link
                        var worldMask = Mask(true);
                        worldJobs.Add((place, 1, new ByteSearch.Job(p, raw, worldMask, NoLink: noLink.For(seg, worldMask)), false));
                        searched.WorldPlugins++;
                        searched.WorldByteList.Add(p); searched.WorldByteBytes += worldBytes;
                        if (!byteSearched.Contains(place)) byteSearched.Add(place);
                    }
                    else if (worldRecords > 0)
                    {
                        searched.WorldSkippedPlugins++; searched.WorldSkippedRecords += worldRecords; searched.WorldSkippedBytes += worldBytes;
                        searched.WorldSkippedList.Add(p);
                    }
                }

                // Each hit: (place, phase, entry). The notes are sorted by the same keys, so their first lines are the ones
                // the plugin-by-plugin search gave.
                var toConfirm = new List<(int Place, int Phase, int Entry)>();
                var notLinks = new List<(int Place, int Phase, int Entry)>();
                var unreadable = new List<(int Place, int Phase, int Step, int Entry, string Why)>();
                void SearchAll(List<(int Place, int Phase, ByteSearch.Job Job, bool Unread)> jobs, bool isWorld)
                {
                    if (jobs.Count == 0) return;
                    var results = ByteSearch.SearchMany(jobs.Select(j => j.Job).ToList(), 0, out double ms);
                    for (int k = 0; k < jobs.Count; k++)
                    {
                        var (place, phase, _, unread) = jobs[k];
                        var r = results[k];
                        if (isWorld && !unread) searched.WorldRecords += r.Records;
                        else if (!unread) searched.ByteRecords += r.Records;
                        // The records a link file couldn't read are searched without a word on the ones that can't be inflated
                        if (!unread) foreach (int bad in r.NotInflated) unreadable.Add((place, phase, 0, bad, "its compressed data couldn't be inflated"));
                        foreach (int e in r.Hits) toConfirm.Add((place, phase, e));
                        foreach (int e in r.NoLinkField) notLinks.Add((place, phase, e));
                    }
                    if (isWorld) searched.WorldMs += ms;
                    else if (jobs.Any(j => !j.Unread)) searched.ByteMs += ms;
                }
                SearchAll(outside, false);
                SearchAll(worldJobs, true);
                foreach (int place in byteSearched)
                {
                    var p = places[place].P;
                    QuickStamp now;
                    try { now = QuickStamp.Read(p.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { now = default; }
                    if (now != p.Stamp) throw new RecordException($"{p.Name} changed during the read: run the command again.");
                }

                // Every hit is confirmed by reading its record through Mutagen, all at once on the pipeline's threads
                if (toConfirm.Count > 0)
                {
                    var clock = Stopwatch.StartNew();
                    var targetModKey = ModKey.FromFileName(owner);
                    var byPlace = toConfirm.GroupBy(h => h.Place).OrderBy(g => g.Key).ToList();
                    foreach (var g in byPlace) Use(places[g.Key].Ix, places[g.Key].P);
                    foreach (var g in byPlace) EngineFor(places[g.Key].Ix).OpenForThreads(g.Select(h => new RecordRef(places[g.Key].P.Id, h.Entry)), alone: true);
                    var engineOf = new RecordEngine[places.Count];
                    foreach (var g in byPlace) engineOf[g.Key] = EngineFor(places[g.Key].Ix);
                    var engines = byPlace.Select(g => engineOf[g.Key]).Distinct().ToList();
                    // One set per place and phase, in the order of the hits
                    var sets = new List<ReadSet>(); var setKey = new List<(int Place, int Phase)>();
                    foreach (var g in toConfirm.GroupBy(h => (h.Place, h.Phase)).OrderBy(g => g.Key.Place).ThenBy(g => g.Key.Phase))
                    {
                        sets.Add(new ReadSet { Plugin = places[g.Key.Place].P, Entries = g.Select(h => h.Entry).ToList() });
                        setKey.Add(g.Key);
                    }
                    using var sinks = new System.Threading.ThreadLocal<Mutagen.Bethesda.Serialization.FormLinkPlaceCollector>(() => RecordLinks.Collector());
                    // World records are read as the world link index reads them (J32): the bulky subrecords (cells'
                    // traversals, navmesh geometry, surface tree grids, the navmesh info map's entries) by their bytes, the
                    // rest through Mutagen and the walk. The same links as the walk of the whole record (tests\SFSearch.RefsCheck, world)
                    var worldOf = new WorldReader.Plugin?[places.Count];
                    foreach (var g in toConfirm.Where(h => h.Phase == 1).GroupBy(h => h.Place))
                        worldOf[g.Key] = new WorldReader.Plugin { P = places[g.Key].P, Session = EngineFor(places[g.Key].Ix).Sessions.Get(places[g.Key].P.Name) };
                    var worldFields = new List<string>();
                    var worldIds = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
                    int WorldField(string path) => worldIds.GetOrAdd(path, x => { lock (worldFields) { worldFields.Add(x); return worldFields.Count - 1; } });
                    var recordSchema = EngineFor(main).Schema;
                    using var worldReaders = new System.Threading.ThreadLocal<WorldReader>(() => new WorldReader(recordSchema, WorldField));
                    using var worldRows = new System.Threading.ThreadLocal<List<WorldRow>>(() => new List<WorldRow>());
                    var wasHeavy = engines.Select(e => e.AllowHeavy).ToList();
                    // A record of any size is walked for its links: the walk makes no JSON and no card
                    foreach (var e in engines) e.AllowHeavy = true;
                    try
                    {
                        PipelineWork<Confirmed> confirm = (in PipelineRecord r, List<Confirmed> output) =>
                        {
                            var (place, phase) = setKey[r.Set];
                            var engine = engineOf[place];
                            try
                            {
                                if (phase == 1 && worldOf[place] is { } wp && r.Problem == PipelineProblem.None)
                                {
                                    var rows = worldRows.Value!; rows.Clear();
                                    var how = worldReaders.Value!.Read(wp, r.EntryIndex, r.Head, r.Data, rows, out string? why, out _);
                                    if (how == WorldReader.How.Unread) { output.Add(new Confirmed(place, phase, r.EntryIndex, null, why ?? "it couldn't be read")); return; }
                                    Dictionary<string, int>? hit = null;
                                    foreach (var row in rows)
                                    {
                                        if (row.Target != keyOf[place]) continue;
                                        string f; lock (worldFields) f = worldFields[row.Field];
                                        hit ??= new Dictionary<string, int>(StringComparer.Ordinal);
                                        hit[f] = hit.GetValueOrDefault(f) + (int)Math.Min(int.MaxValue, row.Count);
                                    }
                                    output.Add(new Confirmed(place, phase, r.EntryIndex, hit, null));
                                    return;
                                }
                                // A record that holds others is read alone: its children's links are their own
                                var rec = engine.LoadAlone(r.Ref);
                                if (phase == 1)
                                {
                                    bool listed = false;
                                    foreach (var l in rec.Record.EnumerateFormLinks())
                                        if (l.FormKey.ID == localId && l.FormKey.ModKey == targetModKey) { listed = true; break; }
                                    if (!listed) { output.Add(new Confirmed(place, phase, r.EntryIndex, null, null)); return; }
                                }
                                var sink = sinks.Value!;
                                RecordLinks.Walk(rec.Record, sink);
                                var found = new Dictionary<string, int>(StringComparer.Ordinal);
                                foreach (var (field, target) in sink.Links)
                                    if (target.ID == localId && target.ModKey.FileName.String.Equals(owner, StringComparison.OrdinalIgnoreCase))
                                        found[sink.Paths[field]] = found.GetValueOrDefault(sink.Paths[field]) + 1;
                                output.Add(new Confirmed(place, phase, r.EntryIndex, found.Count == 0 ? null : found, null));
                            }
                            catch (RecordException e) { output.Add(new Confirmed(place, phase, r.EntryIndex, null, e.Message)); }
                            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException) { output.Add(new Confirmed(place, phase, r.EntryIndex, null, e.Message.Split('\n')[0])); }
                        };
                        // The records' bytes, inflated, for the world readers (a record outside world data is read by Mutagen from the file)
                        foreach (var c in RecordPipeline.Read(sets, confirm, new PipelineOptions { Data = PipelineData.Plain, CostPerByte = 8, MemoryBytes = Service.JobContext.MemoryBudgetMB << 20, PieceRecords = 256, PieceBytes = 1L << 20 }))
                        {
                            if (c.Why != null) unreadable.Add((c.Place, c.Phase, 1, c.Entry, c.Why));
                            else if (c.Fields == null) notLinks.Add((c.Place, c.Phase, c.Entry));
                            else foreach (var (field, n) in c.Fields) inHits.Add(new InHit(c.Place, c.Entry, field, n, true));
                        }
                    }
                    finally { for (int k = 0; k < engines.Count; k++) engines[k].AllowHeavy = wasHeavy[k]; }
                    searched.Confirmed += toConfirm.Count;
                    searched.ConfirmMs += clock.Elapsed.TotalMilliseconds;
                }
                searched.NotLinks.AddRange(notLinks.OrderBy(n => n.Place).ThenBy(n => n.Phase).ThenBy(n => n.Entry).Select(n => (n.Place, n.Entry)));
                searched.Unreadable.AddRange(unreadable.OrderBy(n => n.Place).ThenBy(n => n.Phase).ThenBy(n => n.Step).ThenBy(n => n.Entry).Select(n => (n.Place, n.Entry, n.Why)));
                // Plugins in load order, then the pointing record's type, then its FormKey
                inHits.Sort((a, b) => a.Place != b.Place ? a.Place.CompareTo(b.Place)
                    : SigOf(a) != SigOf(b) ? string.CompareOrdinal(SigOf(a), SigOf(b)) : a.Entry != b.Entry ? a.Entry.CompareTo(b.Entry) : string.CompareOrdinal(a.Field, b.Field));
                string SigOf(InHit h) { var s = places[h.Place].P.Segment!; return s.SignatureOf(s[h.Entry]); }
            }

            // ---- The page
            int total = outRows.Count + inHits.Count;
            int pageOutFrom = Math.Min(offset, outRows.Count), pageOutTo = Math.Min(outRows.Count, offset + limit);
            int inOffset = Math.Max(0, offset - outRows.Count), inTake = Math.Max(0, limit - (pageOutTo - pageOutFrom));
            var inPage = inHits.Skip(inOffset).Take(inTake).ToList();
            int shown = (pageOutTo - pageOutFrom) + inPage.Count;

            // ---- A link in a script property: which script and property hold it, and what an injector script adds it to.
            // Only the page's rows of that kind, each record read once. An injector is told by its compiled code (its
            // AddForm calls), each script read once per process; when the code can't tell, by the older rules.
            var scriptWords = new Dictionary<InHit, string>();
            var entries = new Dictionary<string, ListEntries?>(StringComparer.OrdinalIgnoreCase);
            // Which form lists add their entries to which through AddToList (winning versions), read when first needed
            FormListAdds? listAdds = null;
            FormListAdds ListAdds() => listAdds ??= FormListAdds.Read(main);
            if (inPage.Any(h => ScriptLinks.IsScriptProperty(h.Field)))
            {
                var clock = Stopwatch.StartNew();
                int read = 0;
                var code = ScriptInjections.For(eng.DataFolder);
                var codeClock = new Stopwatch();
                string target = $"{localId:X6}:{owner}";
                foreach (var group in inPage.Where(h => ScriptLinks.IsScriptProperty(h.Field)).GroupBy(h => (h.Place, h.Entry)))
                {
                    var (ix, p, _) = places[group.Key.Place];
                    var engine = EngineFor(ix);
                    Use(ix, p);
                    List<ScriptLink> found;
                    try
                    {
                        var rec = engine.Load(new RecordRef(p.Id, group.Key.Entry));
                        var json = engine.Json(rec);
                        string pluginName = p.Name;
                        codeClock.Start();
                        try { found = ScriptLinks.Into(json, target, SignatureOfKey, name => code.Of(name, pluginName), ScriptLinks.AnyList, EntriesOfKey); }
                        finally { codeClock.Stop(); }
                        read++;
                    }
                    // A record that can't be read here keeps its row as it is: the field's path still says where the link is
                    catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException) { continue; }
                    foreach (var h in group)
                        if (ScriptWords(found.Where(f => ScriptLinks.Unindexed(f.Path) == h.Field).ToList(), engine) is { } words) scriptWords[h] = words;
                }
                if (read > 0) answer.Read($"{read:N0} record{(read == 1 ? "" : "s")} through Mutagen to name the script properties ({(clock.Elapsed - codeClock.Elapsed).TotalMilliseconds:N0} ms)");
                if (code.ScriptsLookedUp > 0) answer.Read($"the compiled code of {code.ScriptsLookedUp:N0} script{(code.ScriptsLookedUp == 1 ? "" : "s")} for AddForm calls ({code.ScriptsRead:N0} read from their files, {codeClock.Elapsed.TotalMilliseconds:N0} ms)");
                if (entries.Count > 0) answer.Read($"{entries.Count:N0} form list{(entries.Count == 1 ? "" : "s")} through Mutagen for the lists an injector adds to (each as the game uses it)");
            }
            // A form list's entries as the game uses it (its winning version, and the entries other form lists add to it
            // through AddToList), for an injector whose target lists are its entries
            ListEntries? EntriesOfKey(string formKey)
            {
                if (entries.TryGetValue(formKey, out var have)) return have;
                return entries[formKey] = ListAdds().Entries(formKey, OwnEntriesOfKey);
            }
            IReadOnlyList<string>? OwnEntriesOfKey(string formKey)
            {
                List<string>? got = null;
                if (HeaderIndex.TryParseFormKey(formKey, out var o, out var id) && main.Plugin(o) is { } fp && main.Versions(fp.Name, id) is { } fv)
                {
                    var at = fv.Winner >= 0 ? fv.Holders[fv.Winner] : fv.Holders[0];
                    try
                    {
                        Use(main, main.Plugins[at.Plugin]);
                        var engine = EngineFor(main);
                        var json = engine.Json(engine.Load(at));
                        got = (json["Items"] as JsonArray)?.Select(x => ReportSession.IsLink(x, out var k) ? ReportSession.Canon(k) : null).OfType<string>().ToList() ?? new List<string>();
                    }
                    catch (RecordException) { got = null; }
                }
                return got;
            }

            // ---- A link from a form list: one that adds its entries to it through AddToList, or one that holds it and
            // adds its entries on to another list through its own AddToList. Only the page's form-list rows, each read once.
            var listWords = new Dictionary<InHit, string>();
            foreach (var h in inPage)
            {
                { var hs = places[h.Place].P.Segment!; if (hs.SignatureOf(hs[h.Entry]) != "FLST") continue; }
                if (h.Field == "AddToList") { listWords[h] = "the game adds its entries to this list"; continue; }
                if (!h.Field.StartsWith("Items", StringComparison.Ordinal)) continue;
                var (ix, p, _) = places[h.Place];
                var engine = EngineFor(ix);
                string? to = null;
                try
                {
                    Use(ix, p);
                    if (ReportSession.IsLink(engine.Json(engine.Load(new RecordRef(p.Id, h.Entry)))["AddToList"], out var k)) to = ReportSession.Canon(k);
                }
                catch (RecordException) { continue; }
                if (to == null || SignatureOfKey(to) != "FLST") continue;
                var on = ListAdds().Chain(to);
                listWords[h] = $"the list adds its entries to {Bracket(engine.Label(to))} through its AddToList{(on.Count > 0 ? $", and on to {string.Join(", ", on.Take(3).Select(x => Bracket(engine.Label(x))))}{(on.Count > 3 ? $" and {on.Count - 3} more" : "")}" : "")}";
            }
            // ---- A form list asked about: the lists that add their entries to it, and the list it adds its own to
            string? listNote = null;
            if (pick != null && pick.Info.Signature == "FLST")
            {
                string self = $"{localId:X6}:{owner}";
                var adders = ListAdds().Into(self);
                var on = ListAdds().Chain(self);
                var engine = EngineFor(main);
                if (adders.Count > 0)
                    listNote = $"{adders.Count:N0} form list{(adders.Count == 1 ? " adds its" : "s add their")} entries to it through AddToList, so the game's copy holds them too: {string.Join(", ", adders.Take(6).Select(x => Bracket(engine.Label(x))))}{(adders.Count > 6 ? $" and {adders.Count - 6} more" : "")}"
                        + (ListAdds().Entries(self, OwnEntriesOfKey) is { } all && OwnEntriesOfKey(self) is { } mine ? $" ({all.Entries.Count - mine.Count:N0} entries more than its own {mine.Count:N0}{(all.AddedBy.Count > adders.Count ? $", {all.AddedBy.Count - adders.Count} of the lists adding through those" : "")})" : "") + ".";
                if (on.Count > 0)
                    listNote = (listNote == null ? "" : listNote + " ") + $"Its entries are added to {string.Join(", then ", on.Take(4).Select(x => Bracket(engine.Label(x))))}{(on.Count > 4 ? $" and {on.Count - 4} more" : "")} through AddToList (the version the game uses).";
            }
            if (listAdds != null) answer.Read(listAdds.Cost);
            string? SignatureOfKey(string formKey) =>
                HeaderIndex.TryParseFormKey(formKey, out var o, out var id) && main.Plugin(o) is { } tp && main.Versions(tp.Name, id) is { } tv
                    ? main.Describe(tv.Winner >= 0 ? tv.Holders[tv.Winner] : tv.Holders[0]).Signature : null;

            // A plugin that changed while it was read makes the answer untrustworthy: the command fails
            foreach (var (engine, use) in uses) engine.Sessions.Confirm(use);

            // ---- The answer
            if (listNote != null) answer.Note(listNote);
            if (offset > 0 && offset >= total) { answer.Fail($"the cursor points past the end ({total:N0} rows)", "leave the cursor out to start over", 2); return; }
            int headerLines = 0;

            if (direction is "out" or "both")
            {
                int distinct = outRows.Select(r => r.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                answer.Line(outRows.Count == 0
                    ? $"{targetLabel} points at nothing{(sigs != null ? " of the types asked for" : "")}: its version in {pick!.Plugin.Name} has no link{(sigs != null ? " to such a record" : "")}."
                    : $"{targetLabel} points at {distinct:N0} record{(distinct == 1 ? "" : "s")} through {outRows.Count:N0} link{(outRows.Count == 1 ? "" : "s")} (its version in {pick!.Plugin.Name}: {pick.Why}).");
                headerLines++;
                if (pageOutTo > pageOutFrom)
                {
                    answer.Line("| Field | Points at | Type |");
                    answer.Line("| :--- | :--- | :--- |");
                    headerLines += 2;
                    var engine = EngineFor(pick!.Ix);
                    for (int i = pageOutFrom; i < pageOutTo; i++)
                        answer.Line($"| {outRows[i].Path} | {Bracket(engine.Label(outRows[i].Target))} | {(outRows[i].Sig == null ? "" : schema.Label(outRows[i].Sig!))} |");
                }
                if (direction == "out" && HowToGet(pick!, main, answer.AsTool) is { } how) answer.Note(how);
                if (children > 0) answer.Note($"{children:N0} record(s) sit inside {targetLabel} (a cell's references, a quest's topics). They are records of their own, not links: find lists them by what they are inside.");
            }
            if (direction is "in" or "both")
            {
                if (direction == "both") { answer.Line(); headerLines = 0; }
                int records = inHits.Select(h => (h.Place, h.Entry)).Distinct().Count();
                long links = inHits.Sum(h => (long)h.Count);
                int plugins = inHits.Select(h => h.Place).Distinct().Count();
                bool worldLeft = searched.WorldSkippedPlugins > 0;
                answer.Line(inHits.Count == 0
                    ? worldLeft
                        ? $"No link to {targetLabel} was found outside world data{(sigs != null ? " (records of the types asked for)" : "")}. World data wasn't searched, so this doesn't say that nothing points at it."
                        : $"Nothing points at {targetLabel} in what was searched{(sigs != null ? " (records of the types asked for)" : "")}."
                    : $"{records:N0} record{(records == 1 ? "" : "s")} point{(records == 1 ? "s" : "")} at {targetLabel}: {links:N0} link{(links == 1 ? "" : "s")} in {plugins:N0} plugin{(plugins == 1 ? "" : "s")}{(worldLeft ? ", world data left out" : "")}.");
                if (worldLeft) answer.Line(WorldLine(searched));
                // The third answer (J33): left out by default, the world link index of those plugins would answer it fast
                var offer = worldLeft && searched.WorldAsked == null ? IndexOffers.World(searched.WorldSkippedList, LaterSeconds, Later) : null;
                if (offer != null) answer.Line(HeavyAnswers.SaveLine(offer, "world: \"save_index\"", "--world=save-index", "search it now") + " Or leave it out, as this answer did.");
                answer.Line(SearchedLine(searched, places.Count, searchWorld: searched.WorldPlugins > 0, sigs, search == "bytes", named: !string.IsNullOrWhiteSpace(ask.In), shownSet: shownSet));
                if (direction == "in") headerLines = (worldLeft ? 3 : 2) + (offer != null ? 1 : 0);
                if (ask.SaveIndex)
                {
                    // world: "save_index": world data was searched now; the world link index of the plugins searched by their bytes is saved after the answer
                    if (IndexOffers.World(searched.WorldByteList, LaterSeconds, Later) is { } save) IndexOffers.Take(answer, save);
                    else answer.Note("world: \"save_index\" was asked, and there is nothing to save: every plugin searched that holds world data already has its world link index (world data came from it), or none holds world data.");
                }
                else if (searched.WorldByteBytes > AutoWorldBytes && IndexOffers.World(searched.WorldByteList, LaterSeconds, Later) is { } could)
                    answer.Note($"World data was searched by its bytes ({Sizes.Text(searched.WorldByteBytes)} in {searched.WorldByteList.Count:N0} plugin{(searched.WorldByteList.Count == 1 ? "" : "s")}). "
                        + HeavyAnswers.SaveLine(could, "world: \"save_index\"", "--world=save-index", "search it the same way", "To make this fast next time, call with"));
                if (searched.NotLinks.Count > 0)
                {
                    answer.Line($"Bytes matched, not a link: {searched.NotLinks.Count:N0} record{(searched.NotLinks.Count == 1 ? " holds" : "s hold")} the four bytes of the FormID, and no link of {(searched.NotLinks.Count == 1 ? "it" : "theirs")} points at the target. "
                        + $"{(searched.NotLinks.Count == 1 ? "It is" : "The first are")}: {string.Join("; ", searched.NotLinks.Take(5).Select(n => LabelSig(places[n.Place], n.Entry)))}.");
                    if (direction == "in") headerLines++;
                }
                if (searched.Unreadable.Count > 0)
                {
                    answer.Line($"Bytes matched, couldn't be checked: {searched.Unreadable.Count:N0} record{(searched.Unreadable.Count == 1 ? "" : "s")} Mutagen couldn't read. "
                        + $"{string.Join("; ", searched.Unreadable.Take(3).Select(n => $"{LabelSig(places[n.Place], n.Entry)}: {n.Why.TrimEnd('.')}"))}.");
                    if (direction == "in") headerLines++;
                }
                if (pick != null && HowToGet(pick, main, answer.AsTool) is { } how)
                {
                    answer.Line(how);
                    if (direction == "in") headerLines++;
                }
                if (inPage.Count > 0)
                {
                    answer.Line("| Record | Type | Field | Read from | Wins |");
                    answer.Line("| :--- | :--- | :--- | :--- | :--- |");
                    if (direction == "in") headerLines += 2;
                    foreach (var h in inPage)
                    {
                        var (ix, p, byPath) = places[h.Place];
                        var r = new RecordRef(p.Id, h.Entry);
                        var info = ix.Describe(r);
                        string wins;
                        if (byPath) wins = "outside Data";
                        else if (main.IsWinner(r)) wins = "yes";
                        else
                        {
                            RecordVersions? v = HeaderIndex.TryParseFormKey(info.FormKey, out var ro, out var rid) && main.Plugin(ro) != null ? main.Versions(ro, rid) : null;
                            wins = v is { Winner: >= 0 } ? $"no: {main.Plugins[v.Holders[v.Winner].Plugin].Name} wins" : p.LeftOutShort != null ? $"no: {p.LeftOutShort}" : "no: not loaded";
                        }
                        answer.Line($"| {ProblemNotes.Label(info)} | {schema.Label(info.Signature)} | {h.Field}{(h.Count > 1 ? $" x{h.Count}" : "")}{(scriptWords.TryGetValue(h, out var said) ? ": " + said : "")}{(listWords.TryGetValue(h, out var listSaid) ? ": " + listSaid : "")} | {p.Name} | {wins} |");
                    }
                }
                foreach (var name in cantPoint.Take(8)) answer.Note($"{name} can't point at it: {owner} isn't {name} or one of its masters.");
                if (cantPoint.Count > 8) answer.Note($"{cantPoint.Count - 8} more of the plugins named can't point at it: {owner} isn't one of their masters.");
                if (setCantPoint.Count > 0) answer.Note($"{setCantPoint.Count:N0} of the plugins of {shownSet} can't point at it: {owner} isn't one of their masters ({string.Join(", ", setCantPoint.Take(6))}{(setCantPoint.Count > 6 ? $" and {setCantPoint.Count - 6:N0} more" : "")}).");
            }
            answer.HeaderLines = direction == "both" ? 0 : headerLines;
            int next = offset + shown;
            answer.Page(offset + 1, shown, total, next < total ? Cursors.Make(next, question) : null);

            // ---- Problems of the plugins the rows came from lead the notes; then where the answer was read from
            var from = new List<(HeaderIndex, IndexedPlugin, string?)>(leftOut);
            from.AddRange(inPage.Select(h => h.Place).Distinct().Select(pl => (places[pl].Ix, places[pl].P, places[pl].ByPath ? (string?)places[pl].P.Path : null)));
            ProblemNotes.Add(answer, from, required: false);

            if (direction != "out")
            {
                if (searched.IndexPlugins > 0) answer.Read($"the link index of {searched.IndexPlugins} plugin{(searched.IndexPlugins == 1 ? "" : "s")}");
                if (searched.WorldIndexPlugins > 0) answer.Read($"the world link index of {searched.WorldIndexPlugins} plugin{(searched.WorldIndexPlugins == 1 ? "" : "s")}");
                if (searched.BytePlugins > 0) answer.Read($"the bytes of {searched.ByteRecords:N0} records of {searched.BytePlugins} plugin{(searched.BytePlugins == 1 ? "" : "s")} ({searched.ByteMs:N0} ms)");
                if (searched.WorldPlugins > 0) answer.Read($"the bytes of {searched.WorldRecords:N0} world records of {searched.WorldPlugins} plugin{(searched.WorldPlugins == 1 ? "" : "s")} ({searched.WorldMs:N0} ms)");
                if (searched.Confirmed > 0) answer.Read($"{searched.Confirmed:N0} record{(searched.Confirmed == 1 ? "" : "s")} through Mutagen to confirm ({searched.ConfirmMs:N0} ms)");
            }
            if (pick != null && direction != "in") answer.Source(EngineIndex.Source(pick.Plugin, pick.ByPath ? pick.Plugin.Path : null));
            if (direction != "out")
            {
                if (places.Count > 8) answer.Source(new Service.SourceInfo { Plugin = $"the {places.Count} plugins that can point at it", Bytes = places.Sum(s => s.P.Stamp.Length), Key = Service.Wire.Hash(string.Join("", places.Select(s => s.P.ContentKey.ToString())))[..8].ToLowerInvariant() });
                else foreach (var s in places) answer.Source(EngineIndex.Source(s.P, s.ByPath ? s.P.Path : null));
            }
            answer.Source(eng.LoadOrderSource());
            _ = nothing;
        }

        static string LabelSig((HeaderIndex Ix, IndexedPlugin P, bool ByPath) place, int entry)
        {
            var info = place.Ix.Describe(new RecordRef(place.P.Id, entry));
            return $"{info.Signature} {ProblemNotes.Label(info)} in {place.P.Name}";
        }

        /// <summary>The plain words beside a row whose link sits in a script property: the script, the property, the struct
        /// member, and for a leveled-list injector the lists the script adds the record to (the words sources uses).</summary>
        static string? ScriptWords(List<ScriptLink> found, RecordEngine engine)
        {
            var said = found.Select(f =>
            {
                string text = $"script {(f.Script.Length > 0 ? f.Script : "[no name]")}{(f.Alias is { } a ? $" on alias {a}" : "")}, property {(f.Property.Length > 0 ? f.Property : "[no name]")}"
                    + (string.IsNullOrEmpty(f.Member) ? "" : $", member {f.Member}") + ".";
                if (f.Targets.Count > 0)
                    text += $" The script adds {(f.Entries ? "its entries" : "it")} to {string.Join(", ", f.Targets.Take(4).Select(t => Bracket(engine.Label(t))))}{(f.Targets.Count > 4 ? $" and {f.Targets.Count - 4} more lists" : "")}{(f.How != null ? $", {f.How}" : "")}"
                        + (f.AddedBy is { Count: > 0 } by ? $"; added through AddToList by {string.Join(", ", by.Take(4).Select(t => Bracket(engine.Label(t))))}{(by.Count > 4 ? $" and {by.Count - 4} more" : "")}" : "") + ".";
                // Its code adds it to the lists in form lists that hold none
                else if (f.FoundBy == "code") text += $" The script adds {(f.Entries ? "its entries" : "it")} to no list, {f.How}.";
                return text;
            }).Distinct().ToList();
            if (said.Count == 0) return null;
            return string.Join(" Also ", said.Take(3)) + (said.Count > 3 ? $" And {said.Count - 3} more." : "");
        }

        /// <summary>Record types the player can carry, and leveled item lists: refs on one points at the report made for
        /// "how does the player get it".</summary>
        static readonly HashSet<string> Holdable = new(StringComparer.Ordinal) { "WEAP", "ARMO", "AMMO", "ALCH", "BOOK", "MISC", "KEYM", "LVLI" };

        /// <summary>The one line that names sources2 for an item: the call in the form the caller can make.</summary>
        static string? HowToGet(Picked pick, HeaderIndex main, bool asTool)
        {
            string sig = pick.Info.Signature;
            if (!Holdable.Contains(sig)) return null;
            // sources2 reads the items a plugin adds: the record's own plugin
            string? plugin = pick.ByPath && pick.Plugin.Name.Equals(pick.Owner, StringComparison.OrdinalIgnoreCase) ? pick.Plugin.Path : main.Plugin(pick.Owner)?.Name;
            if (plugin == null) return null;
            string item = pick.Info.EditorId is { Length: > 0 } e && !e.Contains(' ') ? e : $"{pick.LocalId:X6}:{pick.Owner}";
            var args = new List<string> { "sources2", plugin, "--item", item };
            if (sig is not ("WEAP" or "ARMO")) args.AddRange(new[] { "--types", sig });
            string call = asTool ? $"sfs_run with args [{string.Join(", ", args.Select(x => "\"" + x.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""))}]" : string.Join(" ", args.Select(x => x.Contains(' ') ? $"\"{x}\"" : x));
            return $"How the player gets it: {call}. That report follows these links on to vendors, quest rewards, recipes, containers and placed copies.";
        }

        /// <summary>The record layer writes "(no EditorID)"; the engine's tools write "[no EditorID]".</summary>
        static string Bracket(string label) => label.StartsWith("(no EditorID) ", StringComparison.Ordinal) ? "[no EditorID] " + label["(no EditorID) ".Length..] : label;

        /// <summary>What was searched, what wasn't, and what searching the rest would cost. Always part of the answer.</summary>
        /// <summary>The line that says world data was left out, with its size and the argument that brings it in. It is
        /// the second line of the answer, right under the count: an answer that leaves it out never reads as complete.</summary>
        /// <summary>refs into world data from the world link index, measured through the service (J32 note): XMarker 184 ms,
        /// the chair 85 ms, four other targets 87 to 125 ms.</summary>
        const double LaterSeconds = 0.2;
        const string Later = "refs into world data then answers in about 0.2 s, from the index";

        static string WorldLine(Searched s)
        {
            double seconds = s.WorldSkippedBytes / SearchBytesPerSecond + 0.05 * s.WorldSkippedPlugins / 8.0;
            string cost = $"about {(seconds < 1 ? "1 s" : seconds.ToString("0", CultureInfo.InvariantCulture) + " s")} more, longer when many records have to be confirmed";
            string why = s.WorldAsked == false ? " (left out as asked: world false)"
                : $" (left out by default: more than {Sizes.Text(AutoWorldBytes)} of it)";
            return WorldData.LeftOut("wasn't searched" + why, s.WorldSkippedRecords, s.WorldSkippedBytes, s.WorldSkippedPlugins, cost);
        }

        static string SearchedLine(Searched s, int candidates, bool searchWorld, HashSet<string>? sigs, bool bytesAsked, bool named = false, string? shownSet = null)
        {
            var parts = new List<string>();
            if (candidates == 0) return named ? $"Searched{(shownSet != null ? $" {shownSet}" : "")}: nothing. None of the plugins named can point at it (the notes say why)." : "Searched: nothing. No plugin in Data could point at it.";
            string outside = "every record outside cells' content, navmeshes and terrain";
            if (s.IndexPlugins > 0) parts.Add($"the link index of {s.IndexPlugins:N0} plugin{(s.IndexPlugins == 1 ? "" : "s")} ({outside}: {s.IndexRecords:N0} records)"
                + (s.UnreadSearched > 0 ? $", and the bytes of {s.UnreadSearched:N0} record{(s.UnreadSearched == 1 ? "" : "s")} of theirs that the index couldn't read" : ""));
            if (s.BytePlugins > 0) parts.Add($"the bytes of {s.BytePlugins:N0} plugin{(s.BytePlugins == 1 ? "" : "s")}{(bytesAsked ? ", as asked (search: bytes)" : s.BytePlugins == 1 ? " that has no link index yet" : " that have no link index yet")} ({(s.IndexPlugins > 0 ? "the same records" : outside)}: {s.ByteRecords:N0} records"
                + (s.BytePlugins <= 6 ? ": " + string.Join(", ", s.ByteNames) : "") + ")");
            if (s.WorldIndexPlugins > 0) parts.Add($"world data (cells, placed references, navmeshes, terrain) from the world link index of {s.WorldIndexPlugins:N0} plugin{(s.WorldIndexPlugins == 1 ? "" : "s")} ({s.WorldIndexRecords:N0} records)"
                + (s.WorldUnreadSearched > 0 ? $", and the bytes of {s.WorldUnreadSearched:N0} world record{(s.WorldUnreadSearched == 1 ? "" : "s")} of theirs that the index couldn't read" : ""));
            if (s.WorldPlugins > 0) parts.Add($"world data (cells, placed references, navmeshes, terrain) by its bytes{(s.WorldByDefault ? $", searched by default because it is small (at most {Sizes.Text(AutoWorldBytes)})" : "")}: {s.WorldRecords:N0} records of {s.WorldPlugins:N0} plugin{(s.WorldPlugins == 1 ? "" : "s")}"
                + (s.WorldIndexPlugins > 0 ? " that have no world link index" : ""));
            string line = (shownSet != null ? $"Searched {shownSet}, {candidates:N0} of them could point at it: " : "Searched: ") + (parts.Count == 0 ? $"{candidates:N0} plugin{(candidates == 1 ? "" : "s")} that could point at it, none of which can be read" : string.Join("; ", parts)) + ".";
            if (sigs != null) line += $" Only records of type {string.Join(", ", sigs)} were looked at.";
            if (s.WorldSkippedPlugins == 0 && !searchWorld && s.WorldIndexPlugins == 0) line += " These plugins hold no world records, so nothing was left out.";
            return line;
        }
    }
}
