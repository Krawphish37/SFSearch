// Records and Links hold only the record types that have been read. A statement over them that names no type's own
// view used to get the rows of whatever had been read before (often none) and a note. Now it has read first what it
// narrows to (fix B8, 2026-11-12):
//   WHERE Type = 'Weapon' / FromType = … / Sig = 'WEAP'      that type
//   WHERE FormKey = '…' / FromKey = '…' / EditorID = '…'     the type of that record (header index)
//   WHERE ToKey = '…'                                        the types whose records link to it (the host's link
//                                                            index), or that can link to it (the schema)
// and, when it narrows to nothing and nothing is read yet for the scope, every type of the scope. The usual cost
// check comes after: a read over the limit is answered with its estimate and the call that runs it.
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    /// <summary>For a target signature: the record signatures whose fields can link to such a record, by the schema.</summary>
    readonly Dictionary<string, HashSet<string>> _canLinkTo = new(StringComparer.Ordinal);

    /// <summary>The signatures whose records can hold a link to a record of <paramref name="targetSig"/>: a link field
    /// whose target is its type, a base of it, or an interface it has.</summary>
    internal HashSet<string> CanLinkTo(string targetSig)
    {
        lock (_canLinkTo)
        {
            if (_canLinkTo.TryGetValue(targetSig, out var have)) return have;
            var model = _schema.Model;
            var accepts = new HashSet<string>(StringComparer.Ordinal);
            foreach (string t in _schema.TypesOf(targetSig))
            {
                accepts.Add(t);
                foreach (var o in model.Ancestry(t)) accepts.Add(o.Name);
                if (model.Objects.TryGetValue(t, out var def)) foreach (var i in def.LinkInterfaces) accepts.Add(i);
            }
            var sigs = new HashSet<string>(StringComparer.Ordinal);
            foreach (string sig in _schema.Signatures)
                foreach (string type in _schema.TypesOf(sig))
                {
                    if (sigs.Contains(sig)) break;
                    // Each field once: Expand walks every kind of object a field can hold
                    var seen = new HashSet<FieldDef>();
                    foreach (var row in model.Expand(type))
                        if (seen.Add(row.Def) && row.Def.LinkTo is { } to && accepts.Contains(to)) { sigs.Add(sig); break; }
                }
            return _canLinkTo[targetSig] = sigs;
        }
    }

    sealed partial class Session
    {
        static readonly string[] TypeColumns = { "Type", "FromType", "Sig" };
        static readonly string[] RecordColumns = { "FormKey", "FromKey" };
        /// <summary>What was read first for Records or Links, said once the read is let through.</summary>
        string? _autoReadNote, _autoReadRefused;
        /// <summary>The statement narrows Records, Links or Conditions to some types or records.</summary>
        bool _narrowed;
        /// <summary>J33: plugins with no world link file whose world types a Links WHERE ToKey = '…' would have to read whole,
        /// and those types: what the world link index would answer instead (offered as the third answer).</summary>
        readonly HashSet<IndexedPlugin> _worldHelpPlugins = new();
        readonly HashSet<string> _worldHelpSigs = new(StringComparer.Ordinal);
        /// <summary>The statement has an OR: a type it names is read whole whatever index there is (J33: so no index is offered for that read).</summary>
        bool _anyOr;

        /// <summary>The world link index of the plugins that need it, as the host offers it; null when there is none to offer.</summary>
        IndexOffer? WorldOffer() => _worldHelpPlugins.Count == 0 ? null : _e._options.WorldIndexOffer?.Invoke(_worldHelpPlugins);

        /// <summary>
        /// Each text literal the statement compares a column with: <c>Col = 'x'</c>, <c>'x' = Col</c>,
        /// <c>Col IN ('x', 'y')</c> (not NOT IN), with the column's name (its last part when qualified).
        /// </summary>
        static List<(string Column, string Value)> Comparisons(List<Tok> toks)
        {
            var found = new List<(string, string)>();
            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].Kind != TokKind.String) continue;
                string? col = null;
                int j = i - 1;
                if (j >= 0 && (toks[j].IsPunct("=") || toks[j].IsPunct("==")))
                {
                    if (j - 1 >= 0 && toks[j - 1].IsName) col = toks[j - 1].Text;
                }
                else
                {
                    while (j >= 0 && (toks[j].Kind == TokKind.String || toks[j].IsPunct(","))) j--;
                    if (j >= 1 && toks[j].IsPunct("(") && toks[j - 1].Is("IN") && !(j >= 2 && toks[j - 2].Is("NOT")) && j >= 2 && toks[j - 2].IsName)
                        col = toks[j - 2].Text;
                }
                // 'x' = Col, 'x' = t.Col
                if (col == null && i + 2 < toks.Count && (toks[i + 1].IsPunct("=") || toks[i + 1].IsPunct("==")) && toks[i + 2].IsName)
                    col = i + 4 < toks.Count && toks[i + 3].IsPunct(".") && toks[i + 4].IsName ? toks[i + 4].Text : toks[i + 2].Text;
                if (col != null) found.Add((col, toks[i].Text));
            }
            return found;
        }

        /// <summary>The signature a text names as a record type: a signature (WEAP), a record type (Weapon) or a union view (ObjectModification).</summary>
        string? SigOfType(string text)
        {
            string t = text.Trim();
            var sig = _e._schema.Signatures.FirstOrDefault(s => s.Equals(t, StringComparison.OrdinalIgnoreCase));
            if (sig != null) return sig;
            if (_e.Views.UnionNames.FirstOrDefault(u => u.Key.Equals(t, StringComparison.OrdinalIgnoreCase)) is { Key: not null } union) return union.Value;
            return _e.Views.Find(t) is { IsList: false } v ? _e._schema.SignatureOf(v.Types[0]) : null;
        }

        /// <summary>Signatures each member plugin has records of.</summary>
        Dictionary<string, long> ScopeSignatures()
        {
            var have = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var p in _members)
                if (p.Segment != null)
                    foreach (var s in p.Segment.Signatures) have[s.Signature] = have.GetValueOrDefault(s.Signature) + s.Count;
            return have;
        }

        /// <summary>
        /// The typed views a statement over Records or Links must have read first, and the note that says why. Null when
        /// it needs nothing more read. See the top of this file.
        /// </summary>
        List<ViewSpec>? AutoReads(Probed probed, List<Tok> toks)
        {
            bool links = probed.Special.Contains("Links"), records = probed.Special.Contains("Records"), conds = probed.Special.Contains("Conditions");
            _narrowed = false;
            _anyOr = toks.Any(t => t.Is("OR"));
            if (!links && !records && !conds || _req.NoAutoReads) return null;
            var tables = new List<string>();
            if (records) tables.Add("Records");
            if (links) tables.Add("Links");
            if (conds) tables.Add("Conditions");
            string table = tables.Count == 1 ? tables[0] : string.Join(", ", tables.Take(tables.Count - 1)) + " and " + tables[^1];
            bool several = tables.Count > 1;
            // Conditions alone: only the types whose records can hold a condition (Conditions.cs)
            Func<string, bool> candidate = !links && !records ? _e.ConditionSigs.Contains : _ => true;
            var inScope = ScopeSignatures();
            var named = new HashSet<string>(StringComparer.Ordinal);     // asked for by type: read even when big
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            var why = new List<string>();
            var unknownTypes = new List<string>();
            var tooBig = new List<string>();
            var tooBigSigs = new List<string>();
            // Plugins with no link file, for a ToKey: searched by their bytes once every comparison is known
            var byBytes = new List<(IndexedPlugin Plugin, string Key, HashSet<string> SchemaSigs)>();
            // World records that link to a ToKey, from a plugin's world link index (J32): read alone
            var fromWorld = new List<(IndexedPlugin Plugin, string Key, IReadOnlyList<int> Entries)>();
            // J33: for each world type, the plugins whose world link index covers it: a statement that names it with a
            // ToKey (and no OR) gets its records that link to the key from the index, not by reading the type whole
            var worldCovered = new Dictionary<string, HashSet<IndexedPlugin>>(StringComparer.Ordinal);
            // Links joined to a view by what the links point at (JOIN Keyword k ON k.FormKey = l.ToKey): the links of
            // the types read so far would give a short answer, or none (GAPS S208)
            bool linksByTarget = links && probed.Views.Count > 0 && probed.SpecialColumns.TryGetValue("Links", out var linkColumns) && linkColumns.Contains("ToKey");
            // The types whose records link to one record: from the link index of each plugin, the schema for what the
            // index doesn't cover, the world link index or a byte search for world data (the ToKey = '…' rule)
            void LinkTarget(string value)
            {
                if (_functions.Signature(value) is not { } targetSig) { Note($"No plugin in Data holds {value}, so no type is read for it."); return; }
                var schemaSigs = _e.CanLinkTo(targetSig);
                bool fromIndex = false, fromSchema = false;
                foreach (var p in _members)
                {
                    var held = p.Segment == null ? null : _e.LinkIndex?.Invoke(p, value);
                    if (held != null)
                    {
                        fromIndex = true;
                        wanted.UnionWith(held.Signatures);
                        foreach (var s in held.NotIndexed) if (schemaSigs.Contains(s)) { wanted.Add(s); fromSchema = true; }
                        if (held.WorldEntries != null)
                        {
                            fromWorld.Add((p, value, held.WorldEntries));
                            foreach (var ws in held.WorldTypes ?? Array.Empty<string>())
                                if (!held.NotIndexed.Contains(ws)) { if (!worldCovered.TryGetValue(ws, out var by)) worldCovered[ws] = by = new HashSet<IndexedPlugin>(); by.Add(p); }
                        }
                        else if (held.WorldTypes is { Count: > 0 } worldSigs && worldSigs.Any(schemaSigs.Contains))
                        {
                            // Its world records would come from its world link index, if the user made one (J33)
                            _worldHelpPlugins.Add(p);
                            foreach (var ws in worldSigs) if (schemaSigs.Contains(ws)) _worldHelpSigs.Add(ws);
                        }
                    }
                    else if (p.Segment != null) byBytes.Add((p, value, schemaSigs));
                }
                if (fromIndex)
                    why.Add(!fromSchema ? $"the types whose records link to {value}, from the link index"
                        : $"the types whose records link to {value} (link index), and those the index doesn't cover that can (schema)");
            }
            bool narrowed = false;
            foreach (var (col, value) in Comparisons(toks).Distinct())
            {
                if (TypeColumns.Any(c => c.Equals(col, StringComparison.OrdinalIgnoreCase)))
                {
                    narrowed = true;
                    if (SigOfType(value) is { } sig) { named.Add(sig); wanted.Add(sig); why.Add("the types it names"); }
                    else unknownTypes.Add(value);
                }
                else if (RecordColumns.Any(c => c.Equals(col, StringComparison.OrdinalIgnoreCase)) || col.Equals("EditorID", StringComparison.OrdinalIgnoreCase))
                {
                    narrowed = true;
                    var refs = new List<RecordRef>();
                    if (col.Equals("EditorID", StringComparison.OrdinalIgnoreCase)) refs.AddRange(Index.FindEditorId(value));
                    else if (HeaderIndex.TryParseFormKey(value, out _, out _) && Index.Versions(value) is { } v) refs.AddRange(v.Holders);
                    int before = wanted.Count;
                    foreach (var r in refs)
                        if (_members.Contains(Index.Plugins[r.Plugin])) wanted.Add(Index.Describe(r).Signature);
                    if (wanted.Count > before) why.Add($"the type of {value}");
                    if (linksByTarget)
                        foreach (string key in refs.Select(r => Index.Describe(r).FormKey).Distinct(StringComparer.OrdinalIgnoreCase).Take(64)) LinkTarget(key);
                }
                else if (col.Equals("ToKey", StringComparison.OrdinalIgnoreCase) && links && probed.Views.Count == 0)
                {
                    narrowed = true;
                    LinkTarget(value);
                }
            }
            _narrowed = narrowed;
            if (WorldOffer() is { } offer) _answer.IndexWouldHelp = offer;
            foreach (var (p, key, schemaSigs) in byBytes) SearchBytes(p, key, schemaSigs, named, tooBig, tooBigSigs);
            NoteByBytes();
            FromWorldIndex(fromWorld);
            foreach (string u in unknownTypes.Distinct(StringComparer.OrdinalIgnoreCase))
                Note($"'{u}' isn't a record type, so nothing is read for it.{SFSearch.SchemaGen.NearNames.DidYouMean(_e.Views.RootNames.Where(r => r.Contains(u, StringComparison.OrdinalIgnoreCase) || u.Contains(r, StringComparison.OrdinalIgnoreCase)).Take(4).ToList())}");

            // Joined by ToKey with no record named: any type may hold such a link, so every type is read, as for Links alone
            if (!narrowed && (probed.Views.Count == 0 || linksByTarget))
            {
                bool anyRead = _files.Any(f => _done[f].Keys.Any(k => k != FormsDone && candidate(k)));
                var unread = inScope.Keys.Where(s => candidate(s) && _e._schema.TypesOf(s).Count > 0 && _files.Any(f => !_done[f].ContainsKey(s))).ToList();
                if (unread.Count == 0) return null;
                // Some of it read already: the rest is read too, so the answer doesn't depend on what an earlier query
                // happened to read (GAPS S208). Only a heavy rest, not allowed, is said instead of read.
                var est = Estimate.None;
                foreach (var f in _files) foreach (string s in unread) foreach (var l in Loads(f, s)) est += CostModel.Full(l.Load);
                if (anyRead && !_req.AllowHeavy && Heavy(est))
                {
                    Note($"{table} {(several ? "hold" : "holds")} only the record types read so far for {(_winners || _versions ? "the load order" : "these plugins")}. {unread.Count:N0} more type(s) of them aren't read ({string.Join(", ", unread.Select(_e.LabelOf).OrderBy(x => x, StringComparer.Ordinal).Take(8))}{(unread.Count > 8 ? ", …" : "")}): " +
                        $"{Seconds(est.Seconds)} and {Memory(est.MemoryMB)} to read them all. Narrow to what you need and it is read first (WHERE Type = 'Armor', WHERE FormKey = '…', WHERE EditorID = '…'{(links ? ", WHERE ToKey = '…'" : "")}), or call again with allow_heavy=true to read every type. "
                        + $"No saved index would make reading every type faster{(links ? " (the link indexes answer WHERE ToKey = '…')" : "")}.");
                    return null;
                }
                wanted.UnionWith(unread);
                why.Add($"every record type {(!links && !records ? "that can hold a condition " : "")}of {(_winners || _versions ? "the load order" : "these plugins")}, because the query narrows to none (WHERE Type = '…', FormKey, EditorID{(links ? " or ToKey" : "")} would read less)");
            }
            // What a byte search left out is said even when nothing else is read
            if (wanted.Count == 0)
            {
                if (tooBig.Count > 0) NoteTooBig(tooBig, tooBigSigs, table, links);
                return null;
            }

            // Only what the scope has and hasn't read; a type over the limit by itself only when the query named it
            var views = new List<ViewSpec>();
            foreach (string sig in wanted.OrderBy(s => s, StringComparer.Ordinal))
            {
                if (!inScope.ContainsKey(sig) || _e._schema.TypesOf(sig).Count == 0) continue;
                if (_files.All(f => _done[f].ContainsKey(sig))) { if (!narrowed) continue; _usedLabels.Add(_e.LabelOf(sig)); continue; }
                // A world type named with a ToKey, covered by the world link index of every plugin of the scope that holds it:
                // its records that link to the key are read alone (FromWorldIndex), not the whole type (J33)
                if (named.Contains(sig) && !_anyOr && worldCovered.TryGetValue(sig, out var covering)
                    && _members.Where(m => m.Segment != null && m.Segment.Signatures.Any(x => x.Signature == sig)).All(covering.Contains)) continue;
                var est = Estimate.None; long count = 0;
                foreach (var f in _files) foreach (var l in Loads(f, sig)) { est += CostModel.Full(l.Load); count += l.Load.Count; }
                if (!named.Contains(sig) && (Heavy(est) || count > _e._options.HeavyRecords)) { tooBig.Add($"{_e.LabelOf(sig)} ({count:N0} records)"); tooBigSigs.Add(sig); continue; }
                if (_e.Views.Find(_e.LabelOf(sig)) is { IsList: false } view && !probed.Views.ContainsKey(view)) views.Add(view);
            }
            if (tooBig.Count > 0) NoteTooBig(tooBig, tooBigSigs, table, links);
            if (views.Count > 0)
            {
                string which = $"{string.Join(", ", views.Select(v => v.Name).Take(12))}{(views.Count > 12 ? ", …" : "")} ({string.Join("; ", why.Distinct())})";
                string holds = $"{table} {(several ? "hold" : "holds")} only the record types read";
                _autoReadNote = $"Read first, because {holds}: {(views.Count == 1 ? "1 type" : $"{views.Count:N0} types")}, {which}.";
                _autoReadRefused = $"{holds}: this query needs {(views.Count == 1 ? "this one" : $"these {views.Count:N0}")} read first: {which}.";
            }
            return views;
        }

        /// <summary>The types left out of Records or Links because each is too big to read whole. When they are world types the
        /// world link index would answer for (J33), the note offers it: the answer as it is now, and the index saved after it.</summary>
        void NoteTooBig(List<string> tooBig, List<string> sigs, string table, bool links)
        {
            string note = $"Not read for {table}, because each is over the cost limit by itself: {string.Join(", ", tooBig.Distinct())}. Their records aren't in this answer: name one (WHERE {(links ? "FromType" : "Type")} = '…') to have it read, with its cost said first.";
            if (links && sigs.Any(_worldHelpSigs.Contains) && WorldOffer() is { } offer)
                note += " " + HeavyAnswers.SaveLine(offer, "allow_heavy=\"save_index\"", "--allow-heavy=save-index", "get this answer as it is") + " Or leave them out, as this answer did.";
            else note += $" No saved index would bring them in{(links ? "" : " (the link indexes answer Links WHERE ToKey = '…')")}.";
            Note(note);
        }

        /// <summary>What the byte searches of this statement found, per FormKey, said once.</summary>
        readonly Dictionary<string, BytesFound> _bytesFound = new(StringComparer.OrdinalIgnoreCase);

        sealed class BytesFound
        {
            public int Plugins, Hits, NotInflated;
            public long Records, Bytes;
            public double Ms;
            public readonly SortedSet<string> Types = new(StringComparer.Ordinal);
        }

        /// <summary>
        /// Links WHERE ToKey = '…' for a plugin with no link file (GAPS S3): the records of the types that can link to
        /// the key (by the schema) whose bytes hold its FormID, found as refs finds them (ByteSearch), are read alone,
        /// for this statement. A type over the cost limit by itself is left out unless the statement names it, as
        /// when it would be read whole. A type already read whole has its links in the Links table already.
        /// </summary>
        void SearchBytes(IndexedPlugin p, string key, HashSet<string> schemaSigs, HashSet<string> named, List<string> tooBig, List<string> tooBigSigs)
        {
            var seg = p.Segment!;
            var file = _files.FirstOrDefault(f => f.Members.Any(m => m.Plugin.Equals(p.Name, StringComparison.OrdinalIgnoreCase)));
            if (file == null || !HeaderIndex.TryParseFormKey(key, out string owner, out uint localId)) return;
            int o = seg.OwnerOf(owner);
            // A plugin that can't write that FormID can't point at it
            if (o < 0 || FormIdPattern.For(p, o, localId) is not { } pattern) return;
            var mask = new bool[seg.Signatures.Count];
            bool any = false;
            for (int s = 0; s < mask.Length; s++)
            {
                string sig = seg.Signatures[s].Signature;
                if (!schemaSigs.Contains(sig) || _e._schema.TypesOf(sig).Count == 0 || _done[file].ContainsKey(sig)) continue;
                var est = Estimate.None; long count = 0;
                foreach (var l in Loads(file, sig)) { est += CostModel.Full(l.Load); count += l.Load.Count; }
                if (!named.Contains(sig) && (Heavy(est) || count > _e._options.HeavyRecords)) { tooBig.Add($"{_e.LabelOf(sig)} ({count:N0} records)"); tooBigSigs.Add(sig); continue; }
                mask[s] = any = true;
            }
            if (!any) return;
            var r = ByteSearch.Search(p, pattern, mask, null, _e._options.Threads);
            var refs = r.Hits.Concat(r.NotInflated).Select(e => new RecordRef(p.Id, e)).ToList();
            if (!_bytesFound.TryGetValue(key, out var found)) _bytesFound[key] = found = new BytesFound();
            found.Plugins++; found.Records += r.Records; found.Bytes += r.Bytes; found.Ms += r.Ms; found.Hits += r.Hits.Count; found.NotInflated += r.NotInflated.Count;
            foreach (int e in r.Hits) found.Types.Add(_e.LabelOf(seg.SignatureOf(seg[e])));
            if (refs.Count > 0) _keyedJobs.Add((file, refs, true, "", $"found by a byte search of {p.Name}"));
        }

        /// <summary>
        /// Links WHERE ToKey = '…' for plugins with a world link index (J32): the world records that link to the key are
        /// read alone for this statement, as a byte search's hits are, without reading their types whole. A type already
        /// read whole has its links in the Links table already.
        /// </summary>
        void FromWorldIndex(List<(IndexedPlugin Plugin, string Key, IReadOnlyList<int> Entries)> found)
        {
            var said = new Dictionary<string, (int Plugins, int Records, SortedSet<string> Types)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, key, entries) in found)
            {
                var seg = p.Segment!;
                var file = _files.FirstOrDefault(f => f.Members.Any(m => m.Plugin.Equals(p.Name, StringComparison.OrdinalIgnoreCase)));
                if (file == null) continue;
                if (!said.TryGetValue(key, out var s)) said[key] = s = (0, 0, new SortedSet<string>(StringComparer.Ordinal));
                var refs = entries.Where(e => !_done[file].ContainsKey(seg.SignatureOf(seg[e]))).Select(e => new RecordRef(p.Id, e)).ToList();
                foreach (var r in refs) s.Types.Add(_e.LabelOf(seg.SignatureOf(seg[r.Entry])));
                said[key] = (s.Plugins + 1, s.Records + refs.Count, s.Types);
                if (refs.Count > 0) _keyedJobs.Add((file, refs, true, "", $"from the world link index of {p.Name}"));
            }
            foreach (var (key, s) in said)
                Note($"Links WHERE ToKey = '{key}': the world records that link to it (cells, placed objects, navmeshes, terrain) came from the world link index of {(s.Plugins == 1 ? "1 plugin" : $"{s.Plugins:N0} plugins")}: {s.Records:N0} record(s){(s.Types.Count > 0 ? $" ({string.Join(", ", s.Types.Take(8))}{(s.Types.Count > 8 ? ", …" : "")})" : "")}, read alone.");
        }

        void NoteByBytes()
        {
            foreach (var (key, f) in _bytesFound)
                Note($"Links WHERE ToKey = '{key}': {(f.Plugins == 1 ? "this plugin has" : $"{f.Plugins:N0} plugins have")} no link index yet, so the records that can link to it were searched by their bytes "
                    + $"({f.Records:N0} records, {AnswerSource.Size(f.Bytes)}, {Took(f.Ms / 1000)}): {f.Hits:N0} hold its FormID{(f.Types.Count > 0 ? $" ({string.Join(", ", f.Types.Take(8))}{(f.Types.Count > 8 ? ", …" : "")})" : "")}, and only those were read."
                    + (f.NotInflated > 0 ? $" {f.NotInflated:N0} compressed record(s) couldn't be inflated to search: they were read too." : ""));
        }
    }
}
