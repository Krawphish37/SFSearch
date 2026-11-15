// profile: what a field holds across many plugins, in one call (GAPS S40, to-do F29). For each field (a record type's
// schema path, nested ones too: Components[*]{AdaptiveTriggerDataComponent}.AdsEffect.Unknown; or every Unknown field
// of a type, or of every type) over a set of plugins: how many records and values each plugin has, the distinct
// values, the most common ones and the share of zeros; for 4-byte values, how many read as a float, as a FormID of a
// record that exists (resolved with the plugin's own masters, and to which record types) and as a strings ID of the
// plugin's strings files; and samples, each with its record and the scalar values beside it.
//
// The values are read through the record layer (RecordEngine.Load: the record's own top-level group, or its own bytes
// for records inside others), then along the path through the Mutagen object's properties: the value is Mutagen's own.
// The records of a plugin are read on several threads, a batch each; the batches are added up in file order, so the
// answer is the same whatever thread finishes first.
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Noggog;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Reports;

public sealed class ProfileOptions
{
    /// <summary>Fields as <c>Type.Path</c> (a Mutagen type name or a signature, then the schema path).</summary>
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
    /// <summary>Every Unknown field: of <see cref="Type"/>, or of every record type when none is given.</summary>
    public bool Unknown { get; init; }
    public string? Type { get; init; }
    /// <summary>The plugins (<see cref="PluginSet.Parse"/>). Default: @winners.</summary>
    public string? In { get; init; }
    /// <summary>Samples per field in the text (the JSON keeps up to <see cref="ProfileReport.KeptSamples"/>).</summary>
    public int Samples { get; init; } = 3;
}

/// <summary>One field to profile: a record type and a path of the schema.</summary>
public sealed class ProfileTarget
{
    public required string Type { get; init; }
    public required string Signature { get; init; }
    /// <summary>The schema path with its list marks and kinds of object: <c>Components[*]{X}.Unknown</c>.</summary>
    public required string Path { get; init; }
    public required FieldRow Row { get; init; }
    /// <summary>A part of the field the path ends in (a point's X), or null.</summary>
    public string? Part { get; init; }
    public string Name => $"{Type}.{Path}";
}

/// <summary>What a profile will read, from the header index: said before it runs, so a heavy one can be refused.</summary>
public sealed class ProfilePlan
{
    public required List<ProfileTarget> Targets { get; init; }
    public required PluginSet Set { get; init; }
    public long Records { get; init; }
    public long Bytes { get; init; }
    public double Seconds { get; init; }
    public long MemoryMB { get; init; }
    public required string What { get; init; }
    public List<string> Notes { get; } = new();
}

public sealed class ProfileReport(ReportSession session)
{
    /// <summary>Samples kept per field (nonzero ones; a third as many zero ones).</summary>
    public const int KeptSamples = 12;
    /// <summary>Distinct values counted per field; values past this are counted together.</summary>
    public const int MaxDistinct = 20000;
    /// <summary>Values whose object ID (low 24 bits) is under this aren't tested as FormIDs or strings IDs.</summary>
    public const uint SmallObjectId = 0x1000;
    // Measured on Starfield.esm and the official plugins (notes\fix-s40-profile.md): reading a record through the
    // record layer and walking its paths, with 8 threads
    const double SecondsPerRecord = 2.5e-6, SecondsPerMB = 0.012;

    // ------------------------------------------------------------------ What to read
    public ProfilePlan Plan(ProfileOptions o)
    {
        session.Index.Refresh();
        var set = PluginSet.Parse(session.Index, o.In);
        var targets = new List<ProfileTarget>();
        var notes = new List<string>();
        var model = session.Schema.Model;
        if (o.Unknown)
        {
            if (o.Fields.Count > 0) throw new ReportException("fields and unknown were both given", "give fields, or unknown (with a type, or alone for every type), not both");
            var types = o.Type == null ? model.RecordTypes.ToList() : TypesNamed(o.Type);
            foreach (var t in types)
                foreach (var row in model.Expand(t.Name).Where(r => r.Def.Unknown && r.Def.Kind is not ("Break" or "Marker")))
                    targets.Add(new ProfileTarget { Type = t.Name, Signature = t.Sig!, Path = row.Path, Row = row });
            if (targets.Count == 0) throw new ReportException($"{(o.Type == null ? "the schema" : o.Type)} has no field marked unknown", $"describe {o.Type ?? "<type>"} lists the fields");
        }
        else
        {
            if (o.Fields.Count == 0) throw new ReportException("no field was named", "give fields as Type.Path (Weapon.AttackDamage, OMOD.Unknown3, Weapon.Components[*]{AdaptiveTriggerDataComponent}.AdsEffect.Unknown), or unknown with a type");
            foreach (var f in o.Fields) targets.AddRange(Targets(f.Trim()));
        }
        targets = targets.GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => g.First()).ToList();
        // What the read costs: every record of the fields' signatures in the plugins
        long records = 0, bytes = 0;
        var sigs = targets.Select(t => t.Signature).ToHashSet(StringComparer.Ordinal);
        foreach (var p in set.Plugins)
            foreach (var s in session.Index.Stats(p.Name))
                if (sigs.Contains(s.Signature)) { records += s.Count; bytes += s.Bytes; }
        double seconds = 0.5 + records * SecondsPerRecord + bytes / 1048576.0 * SecondsPerMB;
        long memory = 300 + bytes / 1048576 * 2;
        string what = $"The profile of {ReportAnswer.Plural(targets.Count, "field")} reads {ReportAnswer.N(records)} records ({Sizes.Text(bytes)}) of {string.Join(", ", sigs.OrderBy(s => s, StringComparer.Ordinal).Take(8))}{(sigs.Count > 8 ? $" and {sigs.Count - 8} more types" : "")} in {set.Shown}";
        var plan = new ProfilePlan { Targets = targets, Set = set, Records = records, Bytes = bytes, Seconds = seconds, MemoryMB = memory, What = what };
        plan.Notes.AddRange(notes);
        return plan;
    }

    List<ObjDef> TypesNamed(string text)
    {
        var model = session.Schema.Model;
        var bySig = model.RecordTypes.Where(r => r.Sig!.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (bySig.Count > 0) return bySig;
        var byName = model.RecordTypes.Where(r => r.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count > 0) return byName;
        var near = NearNames.Pick(text, model.RecordTypes, r => new[] { r.Name, r.Sig! }, 4).Select(r => $"{r.Name} ({r.Sig})");
        throw new ReportException($"there is no record type named \"{text}\"", NearNames.Fix(near) + ". describe <type> lists a type's fields");
    }

    /// <summary>The fields a <c>Type.Path</c> names: a path without its kinds of object ({X}) gives each kind that has it.</summary>
    List<ProfileTarget> Targets(string spec)
    {
        int dot = spec.IndexOf('.');
        if (dot <= 0 || dot == spec.Length - 1) throw new ReportException($"\"{spec}\" isn't a field as Type.Path", "write the record type, a dot and the field's path: Weapon.AttackDamage, OMOD.Unknown3 (describe <type> lists the paths)");
        string path = spec[(dot + 1)..];
        var model = session.Schema.Model;
        // *.Path: the field in every record type that has it
        bool every = spec[..dot] == "*";
        var types = every ? model.RecordTypes.ToList() : TypesNamed(spec[..dot]);
        var found = new List<ProfileTarget>();
        SchemaModel.Lookup? last = null;
        foreach (var t in types)
        {
            var look = model.Resolve(t.Name, path, ignoreCase: true);
            last = look;
            foreach (var f in look.Found)
            {
                if (f.Match is "bytes" or "count" or "flag")
                    throw new ReportException($"{t.Name}.{path} is {(f.Match == "flag" ? "one flag of a field" : f.Match == "count" ? "a list's count" : "a slice of a field's bytes")}", $"profile the whole field: {t.Name}.{f.Row.Path}");
                // A path asked with its kind of object ({X}) keeps that kind; without one, every kind that has the field
                if (path.Contains('{') && !SameKinds(path, f.Row.Path)) continue;
                found.Add(new ProfileTarget { Type = t.Name, Signature = t.Sig!, Path = f.Part == null ? f.Row.Path : f.Row.Path + "." + f.Part, Row = f.Row, Part = f.Part });
            }
        }
        if (found.Count == 0 && every)
            throw new ReportException($"no record type has a field {path}", "check the path: describe *word* searches every field name");
        if (found.Count == 0)
        {
            string where = last == null ? "" : last.PathSoFar.Length > 0 ? $" (the path stops at \"{last.StoppedAtSegment}\" after {last.PathSoFar})" : $" (no field \"{last.StoppedAtSegment}\")";
            throw new ReportException($"{types[0].Name} has no field {path}{where}", $"describe {types[0].Name} lists its fields and their paths; describe {types[0].Name} <word> filters them");
        }
        return found;
    }

    /// <summary>True when every kind of object written in the asked path is in the found one, in order.</summary>
    static bool SameKinds(string asked, string found)
    {
        var a = System.Text.RegularExpressions.Regex.Matches(asked, @"\{([^}]*)\}").Select(m => m.Groups[1].Value).ToList();
        var f = System.Text.RegularExpressions.Regex.Matches(found, @"\{([^}]*)\}").Select(m => m.Groups[1].Value).ToList();
        int at = 0;
        foreach (var k in a)
        {
            while (at < f.Count && !f[at].Equals(k, StringComparison.OrdinalIgnoreCase)) at++;
            if (at == f.Count) return false;
            at++;
        }
        return true;
    }

    // ------------------------------------------------------------------ Reading
    public ProfileAnswer Run(ProfilePlan plan, int samples = 3) => session.Run(scope =>
    {
        var answer = new ProfileAnswer { Set = plan.Set, ShownSamples = Math.Clamp(samples, 0, KeptSamples) };
        answer.Notes.AddRange(plan.Notes);
        var targets = plan.Targets;
        var fields = targets.Select((t, i) => new FieldStats(t, i)).ToList();
        answer.Fields.AddRange(fields);
        var tries = targets.Select((t, i) => (t, i)).GroupBy(x => x.t.Type).ToDictionary(g => g.Key, g =>
        {
            var root = new Node();
            foreach (var (t, i) in g)
            {
                var n = root;
                foreach (var seg in Segments(t.Path)) n = n.Child(seg);
                n.Leaves.Add(i);
            }
            return root;
        }, StringComparer.Ordinal);
        var sigs = targets.Select(t => t.Signature).ToHashSet(StringComparer.Ordinal);
        var engine = session.Engine;
        var index = session.Index;
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        // Each field's samples are picked by marks seeded by its name: the same samples whatever else is asked with it
        var walker = new Walker(tries, targets.Select(t => Walker.Seed(t.Name)).ToArray());
        foreach (var p in plan.Set.Plugins)
        {
            if (p.Segment is not { } seg)
            {
                answer.Notes.Add($"{p.Name} can't be read ({p.Problems.ReadError ?? "it has no header index"}): left out.");
                continue;
            }
            var entries = new List<int>();
            foreach (var s in sigs) entries.AddRange(seg.EntriesOf(s));
            ReportSession.AddSource(answer, p);
            ReportSession.AddProblemNotes(answer, p);
            answer.PluginsRead++;
            if (entries.Count == 0) continue;
            scope.Touch(engine, p.Name);
            var arr = entries.ToArray();
            var offsets = arr.Select(e => seg[e].Offset).ToArray();
            Array.Sort(offsets, arr);
            var ctx = new PluginCtx(p, plan.Set.WinnersOnly);
            const int batch = 256;
            int batches = (arr.Length + batch - 1) / batch;
            // A wave of batches at a time on the reading threads, then added up in file order
            for (int wave = 0; wave < batches; wave += threads * 4)
            {
                int count = Math.Min(threads * 4, batches - wave);
                var results = new BatchResult[count];
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
                {
                    int from = (wave + k) * batch, to = Math.Min(arr.Length, from + batch);
                    results[k] = walker.Batch(engine, index, ctx, arr, from, to);
                });
                foreach (var r in results) Merge(answer, fields, ctx, r);
            }
        }
        // The background a FormID reading is held against: the share of each record type among Starfield.esm's own records
        var background = new Dictionary<string, double>(StringComparer.Ordinal);
        if (index.Plugin("Starfield.esm")?.Segment is { } sf && sf.Count > 0)
            foreach (var st in sf.Signatures) background[st.Signature] = (double)st.Count / sf.Count;
        foreach (var f in fields)
        {
            f.Finish();
            foreach (var w in f.Words.Values) w.Judge(background, f.Target.Row.Def.Kind is "Float" or "Double");
        }
        if (fields.Any(f => f.Words.Values.Any(w => w.Tested > 0)))
            answer.Notes.Add("A 4-byte value is tested as a FormID and a strings ID only when its object ID (low 24 bits) is 0x1000 or more. It reads as links when 90% of the tested values name a record and one record type takes 60% of those and 3 times its share of Starfield.esm's own records (whose IDs fill 91% of 0x000001-0x3FFFFF, 86% of them placed references): the rule of the Mutagen session's analysis (C208).");
        answer.Read.Add($"{ReportAnswer.N(answer.RecordsRead)} records of {ReportAnswer.Plural(answer.PluginsRead, "plugin")}");
        foreach (var (why, n) in answer.Skipped.OrderByDescending(x => x.Value))
            answer.Notes.Add($"{ReportAnswer.Plural(n, "record")} couldn't be read and {(n == 1 ? "is" : "are")} left out: {why}.");
        if (plan.Set.WinnersOnly) answer.Notes.Add("@winners: each record counts once, in the version the game uses (the last loaded plugin that holds it).");
        return answer;
    });

    /// <summary>The steps of a schema path: "Components[*]{X}", "AdsEffect", "Unknown". Dots inside {…} don't part them.</summary>
    internal static List<string> Segments(string path)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '{') depth++;
            else if (path[i] == '}') depth--;
            else if (path[i] == '.' && depth == 0) { parts.Add(path[start..i]); start = i + 1; }
        }
        parts.Add(path[start..]);
        return parts;
    }

    // Adds a batch's counts to the fields: the plugin's counts, the values, and for each 4-byte value what it reads as
    void Merge(ProfileAnswer answer, List<FieldStats> fields, PluginCtx ctx, BatchResult r)
    {
        answer.RecordsRead += r.Records;
        foreach (var (why, n) in r.Skipped) answer.Skipped[why] = answer.Skipped.GetValueOrDefault(why) + n;
        foreach (var (i, acc) in r.Fields)
        {
            var f = fields[i];
            var ps = f.Plugin(ctx.Plugin.Name);
            ps.Records += acc.Records; ps.RecordsNonZero += acc.RecordsNonZero; ps.Values += acc.Values; ps.NonZero += acc.NonZero;
            f.Values += acc.Values; f.NonZero += acc.NonZero;
            foreach (var (repr, c) in acc.Counts)
            {
                if (f.Counts.TryGetValue(repr, out var have)) f.Counts[repr] = have + c;
                else if (f.Counts.Count < MaxDistinct) f.Counts[repr] = c;
                else f.OtherValues += c;
            }
            if (acc.Min < f.Min) f.Min = acc.Min;
            if (acc.Max > f.Max) f.Max = acc.Max;
            f.NonFinite += acc.NonFinite;
            foreach (var (len, c) in acc.Lengths) f.Lengths[len] = f.Lengths.GetValueOrDefault(len) + c;
            foreach (var (off, words) in acc.Words)
            {
                var ws = f.WordAt(off);
                foreach (var (w, c) in words)
                {
                    ws.N += c;
                    if (w == 0) { ws.Zero += c; continue; }
                    if (w <= 1) ws.BoolLike += c;
                    float fl = BitConverter.UInt32BitsToSingle(w);
                    if (float.IsFinite(fl) && Math.Abs(fl) >= 1e-4 && Math.Abs(fl) <= 1e6) ws.FloatLike += c;
                    if ((w & 0xFFFFFF) < ProfileReport.SmallObjectId) continue;
                    ws.Tested += c;
                    var (sig, str) = ctx.Look(w, session);
                    if (sig != null) { ws.FormIdHits += c; ws.FormIdTypes[sig] = ws.FormIdTypes.GetValueOrDefault(sig) + c; }
                    if (str) ws.StringHits += c;
                }
            }
            f.Offer(acc.Samples); f.Offer(acc.SamplesZero);
        }
    }

    // ------------------------------------------------------------------ One plugin's lookups
    /// <summary>The plugin a batch is read from, and what its 4-byte values read as (each value looked up once).</summary>
    internal sealed class PluginCtx(IndexedPlugin plugin, bool winnersOnly)
    {
        public IndexedPlugin Plugin { get; } = plugin;
        public bool WinnersOnly { get; } = winnersOnly;
        readonly Dictionary<uint, (string? Sig, bool Str)> _seen = new();

        /// <summary>The record type a value names as a FormID of this plugin (its own masters' numbering), when such a
        /// record is in Data; and whether it is a strings ID of this plugin's strings files (English).</summary>
        public (string? Sig, bool Str) Look(uint w, ReportSession session)
        {
            if (_seen.TryGetValue(w, out var have)) return have;
            string? sig = null;
            var ids = Plugin.FormIds;
            // A number past the plugin's own (in its numbering) reads as the plugin's record in Mutagen, but no tool writes it
            bool past = FormIdForms.IsOwn(w, ids, out _, out bool atOwn) && !atOwn;
            if (!past && ids.TryResolve(w, out string owner, out uint local))
            {
                // The record must be one its owner holds (or this plugin, when it adds a record in its master's
                // name): a plugin elsewhere in Data that holds the FormKey without its owner doesn't count
                if (session.Index.Plugin(owner)?.Segment is { } os && os.Find(os.SelfOwner, local) is >= 0 and var at)
                    sig = os.SignatureOf(os[at]);
                else if (Plugin.Segment is { } ps && ps.OwnerOf(owner) is >= 0 and var o && ps.Find(o, local) is >= 0 and var mine)
                    sig = ps.SignatureOf(ps[mine]);
            }
            bool str = false;
            if (Plugin.Header is { } h && (h.Flags & 0x80) != 0)
            {
                try { str = session.Engine.Sessions.Get(Plugin.Name).StringsFor(w, Language.English).Count > 0; }
                catch (RecordException) { }
            }
            return _seen[w] = (sig, str);
        }
    }

    // ------------------------------------------------------------------ The walk along the paths
    sealed class Node
    {
        public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);
        public readonly List<int> Leaves = new();
        public Node Child(string seg) => Children.TryGetValue(seg, out var n) ? n : Children[seg] = new Node();
    }

    sealed class Acc
    {
        public long Records, RecordsNonZero, Values, NonZero, NonFinite;
        public double Min = double.PositiveInfinity, Max = double.NegativeInfinity;
        public readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal);
        public readonly Dictionary<int, long> Lengths = new();
        public readonly Dictionary<int, Dictionary<uint, long>> Words = new();
        public readonly List<ProfileSample> Samples = new(), SamplesZero = new();
        public int LastRecord = -1, LastNonZero = -1, Instance;
        public void Word(int off, uint w)
        {
            if (!Words.TryGetValue(off, out var d)) Words[off] = d = new();
            d[w] = d.GetValueOrDefault(w) + 1;
        }
    }

    sealed class BatchResult
    {
        public long Records;
        public readonly Dictionary<int, Acc> Fields = new();
        public readonly Dictionary<string, long> Skipped = new(StringComparer.Ordinal);
        public Acc Of(int i) => Fields.TryGetValue(i, out var a) ? a : Fields[i] = new Acc();
    }

    sealed class Walker(Dictionary<string, Node> tries, ulong[] seeds)
    {
        static readonly ConcurrentDictionary<(Type, string), Func<object, object?>?> Getters = new();
        static readonly ConcurrentDictionary<(Type, string), bool> Kinds = new();
        static readonly ConcurrentDictionary<Type, (string Name, Func<object, object?> Get)[]> Scalars = new();

        public BatchResult Batch(RecordEngine engine, HeaderIndex index, PluginCtx ctx, int[] entries, int from, int to)
        {
            var res = new BatchResult();
            var place = new List<string>(8);
            for (int k = from; k < to; k++)
            {
                var r = new RecordRef(ctx.Plugin.Id, entries[k]);
                if (ctx.WinnersOnly && !index.IsWinner(r)) continue;
                var info = index.Describe(r);
                LoadedRecord rec;
                try { rec = engine.Load(r, info); }
                catch (RecordException e) { Skip(res, e.Message.Contains("isn't read unless", StringComparison.Ordinal) ? $"over {Sizes.Text(engine.HeavyRecordBytes)} each (heavy records: ask for one with record)" : Short(e.Message)); continue; }
                catch (Exception e) when (e is not OutOfMemoryException) { Skip(res, $"Mutagen couldn't read it ({(e is TargetInvocationException { InnerException: { } ie } ? ie : e).GetType().Name})"); continue; }
                if (!tries.TryGetValue(rec.Type, out var trie)) continue;
                res.Records++;
                var c = new Visit { Res = res, Rec = rec, Info = info, Serial = k, Place = place };
                try { Walk(rec.Record, trie, c); }
                catch (Exception e) when (e is not OutOfMemoryException) { Skip(res, $"Mutagen couldn't read a value of it ({(e is TargetInvocationException { InnerException: { } ie } ? ie : e).GetType().Name})"); }
                place.Clear();
            }
            return res;
        }

        static string Short(string message) { var s = message.Split('\n')[0]; return s.Length > 160 ? s[..160] + "…" : s.TrimEnd('.'); }
        static void Skip(BatchResult res, string why) => res.Skipped[why] = res.Skipped.GetValueOrDefault(why) + 1;

        sealed class Visit
        {
            public required BatchResult Res;
            public required LoadedRecord Rec;
            public required RecordInfo Info;
            public required int Serial;
            public required List<string> Place;
        }

        void Walk(object obj, Node node, Visit c)
        {
            foreach (var (seg, child) in node.Children)
            {
                var (name, list, kind) = Parse(seg);
                var get = Getter(obj.GetType(), name);
                if (get == null) continue;
                object? v = get(obj);
                if (v == null) continue;
                if (list && v is IEnumerable en && v is not string && !IsBytes(v))
                {
                    int i = 0;
                    foreach (var raw in en)
                    {
                        int at = i++;
                        if (raw == null) continue;
                        var item = Unwrap(raw);
                        if (kind != null && !IsKind(item, kind)) continue;
                        c.Place.Add($"{name}[{at}]");
                        foreach (int leaf in child.Leaves) Value(leaf, item, obj, c);
                        if (child.Children.Count > 0) Walk(item, child, c);
                        c.Place.RemoveAt(c.Place.Count - 1);
                    }
                }
                else
                {
                    if (kind != null && !IsKind(v, kind)) continue;
                    c.Place.Add(name);
                    foreach (int leaf in child.Leaves) Value(leaf, v, obj, c);
                    if (child.Children.Count > 0) Walk(v, child, c);
                    c.Place.RemoveAt(c.Place.Count - 1);
                }
            }
        }

        static object Unwrap(object item)
        {
            var t = item.GetType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)) return t.GetProperty("Value")!.GetValue(item)!;
            return item;
        }

        static bool IsBytes(object v) => v is ReadOnlyMemorySlice<byte> or byte[] or IReadOnlyList<byte> or Memory<byte> or ReadOnlyMemory<byte>;

        void Value(int field, object value, object parent, Visit c)
        {
            var acc = c.Res.Of(field);
            var (repr, num, bytes) = Repr(value);
            bool zero = IsZero(repr, num, bytes);
            acc.Values++;
            if (!zero) acc.NonZero++;
            if (acc.LastRecord != c.Serial) { acc.LastRecord = c.Serial; acc.Records++; acc.Instance = 0; }
            if (!zero && acc.LastNonZero != c.Serial) { acc.LastNonZero = c.Serial; acc.RecordsNonZero++; }
            int instance = acc.Instance++;
            acc.Counts[repr] = acc.Counts.GetValueOrDefault(repr) + 1;
            if (num is { } d)
            {
                if (!double.IsFinite(d)) acc.NonFinite++;
                else { if (d < acc.Min) acc.Min = d; if (d > acc.Max) acc.Max = d; }
            }
            if (bytes != null)
            {
                acc.Lengths[bytes.Length] = acc.Lengths.GetValueOrDefault(bytes.Length) + 1;
                for (int i = 0; i + 4 <= bytes.Length && i < 64; i += 4) acc.Word(i, BitConverter.ToUInt32(bytes, i));
            }
            else if (WordOf(value) is { } w) acc.Word(-1, w);
            // A sample: kept when its mark is among the smallest of the field's (the same records whichever thread reads them)
            ulong mark = Mark(c.Info.Plugin, c.Info.FormKey, seeds[field], instance);
            int cap = zero ? KeptSamples / 3 : KeptSamples;
            var same = zero ? acc.SamplesZero : acc.Samples;
            // One sample a record: the instance with the smallest mark
            int had = same.FindIndex(x => x.Plugin == c.Info.Plugin && x.FormKey == c.Info.FormKey);
            if (had >= 0) { if (same[had].Mark <= mark) return; same.RemoveAt(had); }
            if (same.Count >= cap)
            {
                int worst = 0;
                for (int i = 1; i < same.Count; i++) if (same[i].Mark > same[worst].Mark) worst = i;
                if (same[worst].Mark <= mark) return;
                same.RemoveAt(worst);
            }
            same.Add(new ProfileSample
            {
                Mark = mark, Zero = zero, Plugin = c.Info.Plugin, FormKey = c.Info.FormKey, EditorId = c.Rec.Record.EditorID ?? c.Info.EditorId,
                RecordType = c.Rec.Type, Place = string.Join('.', c.Place), Value = Shown(value, repr), Number = num,
                Beside = Beside(parent, c.Place.Count > 0 ? LastName(c.Place[^1]) : null),
            });
        }

        static string LastName(string place) { int b = place.IndexOf('['); return b < 0 ? place : place[..b]; }

        /// <summary>The scalar values beside the field in the object that holds it (at most 10).</summary>
        static List<KeyValuePair<string, string>> Beside(object parent, string? self)
        {
            var props = Scalars.GetOrAdd(parent.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod != null && !Noise.Contains(p.Name) && IsScalar(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
                .Select(p => (p.Name, Compile(p))).ToArray());
            var list = new List<KeyValuePair<string, string>>();
            foreach (var (name, get) in props)
            {
                if (name == self) continue;
                object? v;
                try { v = get(parent); } catch (Exception e) when (e is not OutOfMemoryException) { continue; }
                if (v == null) continue;
                var (repr, _, _) = Repr(v);
                string s = Shown(v, repr);
                if (s.Length > 48) s = v is Enum ? repr : s[..48] + "…";
                list.Add(new(name, s));
                if (list.Count == 10) break;
            }
            return list;
        }

        static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
        {
            "FormKey", "Registration", "StaticRegistration", "FormVersion", "Version2", "MajorRecordFlagsRaw", "VersionControl",
            "IsCompressed", "IsDeleted", "FormKeyNullable", "EditorID", "Type", "BinaryWriteTranslator", "CommonInstance",
        };

        static bool IsScalar(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Percent) || t == typeof(Half)
            || typeof(IFormLinkGetter).IsAssignableFrom(t) || typeof(ITranslatedStringGetter).IsAssignableFrom(t);

        static Func<object, object?> Compile(PropertyInfo p)
        {
            var param = Expression.Parameter(typeof(object));
            var body = Expression.Convert(Expression.Property(Expression.Convert(param, p.DeclaringType!), p), typeof(object));
            return Expression.Lambda<Func<object, object?>>(body, param).Compile();
        }

        static Func<object, object?>? Getter(Type t, string name) => Getters.GetOrAdd((t, name), k =>
        {
            foreach (var type in k.Item1.GetInterfaces().Prepend(k.Item1))
            {
                PropertyInfo? p;
                try { p = type.GetProperty(k.Item2, BindingFlags.Public | BindingFlags.Instance); }
                catch (AmbiguousMatchException) { p = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == k.Item2); }
                if (p != null && p.GetIndexParameters().Length == 0 && p.GetMethod != null) return Compile(p);
            }
            return null;
        });

        static bool IsKind(object o, string kind) => Kinds.GetOrAdd((o.GetType(), kind), k =>
            k.Item1.Name == k.Item2 || k.Item1.Name == k.Item2 + "BinaryOverlay" || k.Item1.GetInterfaces().Any(i => i.Name == "I" + k.Item2 + "Getter"));

        static readonly ConcurrentDictionary<string, (string Name, bool List, string? Kind)> Parsed = new(StringComparer.Ordinal);
        static (string Name, bool List, string? Kind) Parse(string seg) => Parsed.GetOrAdd(seg, s =>
        {
            string? kind = null;
            int b = s.IndexOf('{');
            if (b >= 0) { kind = s[(b + 1)..s.IndexOf('}')]; s = s[..b]; }
            bool list = s.EndsWith("[*]", StringComparison.Ordinal);
            if (list) s = s[..^3];
            return (s, list, kind);
        });

        public static ulong Seed(string name)
        {
            ulong h = 14695981039346656037UL;
            foreach (char ch in name) { h ^= ch; h *= 1099511628211UL; }
            return h;
        }

        static ulong Mark(string plugin, string formKey, ulong seed, int instance)
        {
            ulong h = 14695981039346656037UL;
            foreach (char ch in plugin) { h ^= char.ToLowerInvariant(ch); h *= 1099511628211UL; }
            foreach (char ch in formKey) { h ^= char.ToLowerInvariant(ch); h *= 1099511628211UL; }
            h ^= seed; h *= 1099511628211UL;
            h ^= (ulong)instance; h *= 1099511628211UL;
            return h;
        }
    }

    // ------------------------------------------------------------------ Values as text
    /// <summary>A value as the profile counts it: its text, its number when it is one, its bytes when it is bytes.</summary>
    internal static (string Repr, double? Num, byte[]? Bytes) Repr(object v)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (v)
        {
            case bool b: return (b ? "true" : "false", b ? 1 : 0, null);
            case float f: return (f.ToString("R", inv), f, null);
            case double d: return (d.ToString("R", inv), d, null);
            case Half h: return (((double)h).ToString("R", inv), (double)h, null);
            case Enum e:
            {
                double n = Enum.GetUnderlyingType(e.GetType()) == typeof(ulong) ? Convert.ToUInt64(e, inv) : Convert.ToInt64(e, inv);
                return (n.ToString(inv), n, null);
            }
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return (Convert.ToDecimal(v, inv).ToString(inv), Convert.ToDouble(v, inv), null);
            case Percent p: return (p.Value.ToString("R", inv), p.Value, null);
            case string s: return ("\"" + (s.Length > 80 ? s[..80] + "…" : s) + "\"", null, null);
            case ITranslatedStringGetter ts: return ("\"" + (ts.String is { } x ? (x.Length > 80 ? x[..80] + "…" : x) : "") + "\"", null, null);
            case IFormLinkGetter fl: return (fl.IsNull ? "null" : fl.FormKey.ToString(), null, null);
            case FormKey fk: return (fk.IsNull ? "null" : fk.ToString(), null, null);
            case ReadOnlyMemorySlice<byte> ms: return Hex(ms.ToArray());
            case byte[] arr: return Hex(arr);
            case IReadOnlyList<byte> bl: return Hex(bl.ToArray());
            case P3Float p3: return (FormattableString.Invariant($"{p3.X}, {p3.Y}, {p3.Z}"), Math.Abs(p3.X) + Math.Abs(p3.Y) + Math.Abs(p3.Z), null);
            case P2Float p2: return (FormattableString.Invariant($"{p2.X}, {p2.Y}"), Math.Abs(p2.X) + Math.Abs(p2.Y), null);
            case P3Int16 i3: return (FormattableString.Invariant($"{i3.X}, {i3.Y}, {i3.Z}"), Math.Abs(i3.X) + Math.Abs(i3.Y) + Math.Abs(i3.Z), null);
            case P2Int16 i2: return (FormattableString.Invariant($"{i2.X}, {i2.Y}"), Math.Abs(i2.X) + Math.Abs(i2.Y), null);
            case System.Drawing.Color col: return ($"#{col.A:X2}{col.R:X2}{col.G:X2}{col.B:X2}", col.ToArgb(), null);
            case IEnumerable en:
            {
                int n = 0;
                foreach (var _ in en) n++;
                return ($"[{n} {(n == 1 ? "entry" : "entries")}]", n, null);
            }
            default: return ("{" + v.GetType().Name.Replace("BinaryOverlay", "", StringComparison.Ordinal) + "}", 1, null);
        }

        static (string, double?, byte[]) Hex(byte[] b) => ("0x" + Convert.ToHexString(b.Length > 64 ? b[..64] : b) + (b.Length > 64 ? $"… ({b.Length} bytes)" : ""), null, b);
    }

    /// <summary>A value as the answer shows it: an enum with its name beside its number.</summary>
    static string Shown(object v, string repr) => v is Enum e && e.ToString() is { } name && name != repr ? $"{repr} ({name})" : repr;

    static bool IsZero(string repr, double? num, byte[]? bytes)
    {
        if (bytes != null) return bytes.All(b => b == 0);
        if (num is { } d) return d == 0;
        return repr is "\"\"" or "null";
    }

    /// <summary>The 4 bytes of a 4-byte number (an int, a uint, a float), for what they read as.</summary>
    static uint? WordOf(object v) => v switch
    {
        int i => unchecked((uint)i),
        uint u => u,
        float f => BitConverter.SingleToUInt32Bits(f),
        _ => null,
    };
}

/// <summary>One value of a field with the record it is in.</summary>
public sealed class ProfileSample
{
    internal ulong Mark;
    internal bool Zero;
    public required string Plugin, FormKey, RecordType, Place, Value;
    public string? EditorId;
    public double? Number;
    public required List<KeyValuePair<string, string>> Beside;
    public string Label => $"{EditorId ?? "(no EditorID)"} ({FormKey})";
}

/// <summary>What a field's 4-byte values read as, at one offset (-1: the value itself; 0, 4, …: in a run of bytes).</summary>
public sealed class WordStats
{
    public long N, Zero, FloatLike, FormIdHits, StringHits, BoolLike;
    /// <summary>Nonzero values tested as a FormID and a strings ID: those whose low 24 bits (the object ID) are 0x1000 or
    /// more. A smaller number is taken by so many records and strings that any small count or index would "resolve"
    /// (the rule of the Mutagen session's uprof, C208).</summary>
    public long Tested;
    public readonly Dictionary<string, long> FormIdTypes = new(StringComparer.Ordinal);
    /// <summary>What the values most likely are, by the rule of <see cref="Judge"/>: "links to IMAD", "strings IDs",
    /// "floats", or null when no reading stands out.</summary>
    public string? ReadsAs { get; private set; }
    /// <summary>Why a FormID reading was turned down: it is no better than chance.</summary>
    public string? Chance { get; private set; }

    /// <summary>
    /// The verdict, by the rule of the Mutagen session's analysis (C208, analyze.py): Starfield.esm's own IDs fill 91% of
    /// 0x000001-0x3FFFFF, 86% of them placed references, so that a number resolves says little by itself. A field reads as
    /// links when at least 3 values were tested (object ID 0x1000 or more), they are at least half the nonzero ones, 90%
    /// of them name a record its owner holds, and one record type takes at least 60% of those and at least 3 times its
    /// share of Starfield.esm's own records (<paramref name="background"/>). Strings IDs: 98% of the tested values, and
    /// not links. Floats: 90% of the nonzero values read as floats (1e-4 to 1e6), for a field that isn't a float.
    /// </summary>
    internal void Judge(IReadOnlyDictionary<string, double> background, bool isFloat)
    {
        long nz = N - Zero;
        if (nz <= 0) return;
        if (Tested >= 3 && Tested * 2 >= nz)
        {
            if (FormIdHits >= 0.9 * Tested && FormIdTypes.Count > 0)
            {
                var (sig, n) = FormIdTypes.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First();
                double share = (double)n / FormIdHits, bg = background.GetValueOrDefault(sig), lift = share / Math.Max(bg, 1e-6);
                if (share >= 0.6 && lift >= 3) { ReadsAs = $"links to {sig}"; return; }
                Chance = $"they resolve only at chance level: the most common target, {sig}, is {share * 100:0}% of them, and {bg * 100:0}% of Starfield.esm's own records are {sig}";
            }
            if (StringHits >= 0.98 * Tested) { ReadsAs = "strings IDs"; return; }
        }
        if (!isFloat && nz >= 3 && FloatLike >= 0.9 * nz) ReadsAs = "floats";
    }
}

public sealed class PluginCounts
{
    public long Records, RecordsNonZero, Values, NonZero;
}

/// <summary>Everything the profile found about one field.</summary>
public sealed class FieldStats(ProfileTarget target, int index)
{
    public ProfileTarget Target { get; } = target;
    public int Index { get; } = index;
    public readonly Dictionary<string, PluginCounts> Plugins = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal);
    public long Values, NonZero, OtherValues, NonFinite;
    public double Min = double.PositiveInfinity, Max = double.NegativeInfinity;
    public readonly Dictionary<int, long> Lengths = new();
    public readonly SortedDictionary<int, WordStats> Words = new();
    public List<ProfileSample> Samples { get; private set; } = new();
    public PluginCounts Plugin(string name) => Plugins.TryGetValue(name, out var p) ? p : Plugins[name] = new PluginCounts();
    public WordStats WordAt(int off) => Words.TryGetValue(off, out var w) ? w : Words[off] = new WordStats();
    public long Records => Plugins.Values.Sum(p => p.Records);
    public long Zero => Values - NonZero;
    public int Distinct => Counts.Count;

    internal void Offer(List<ProfileSample> more)
    {
        foreach (var s in more) Samples.Add(s);
        // Keep the smallest marks: the same samples whatever order the batches came in
        // One sample a record (a record read in two batches never happens: a batch is whole records)
        Samples = Samples.Where(s => !s.Zero).GroupBy(s => (s.Plugin, s.FormKey)).Select(g => g.MinBy(s => s.Mark)!).OrderBy(s => s.Mark).Take(ProfileReport.KeptSamples)
            .Concat(Samples.Where(s => s.Zero).GroupBy(s => (s.Plugin, s.FormKey)).Select(g => g.MinBy(s => s.Mark)!).OrderBy(s => s.Mark).Take(ProfileReport.KeptSamples / 3)).ToList();
    }

    internal void Finish() => Samples = Samples.OrderBy(s => s.Zero).ThenBy(s => s.Mark).ToList();

    /// <summary>The most common values, the most first (then by text).</summary>
    public IEnumerable<KeyValuePair<string, long>> Top(int n) => Counts.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).Take(n);
}

public sealed class ProfileAnswer : ReportAnswer
{
    public override string Kind => "profile";
    public required PluginSet Set { get; init; }
    public List<FieldStats> Fields { get; } = new();
    public long RecordsRead { get; set; }
    public int PluginsRead { get; set; }
    public Dictionary<string, long> Skipped { get; } = new(StringComparer.Ordinal);
    public int ShownSamples { get; init; } = 3;
    /// <summary>Over this many fields the text gives one table row each (detail gives each field in full).</summary>
    public const int FullUpTo = 10;

    static string Pct(long part, long whole) => whole == 0 ? "0%" : (100.0 * part / whole).ToString(part == whole || part == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + "%";
    static string Num(double d) => d.ToString(Math.Abs(d) >= 1e7 || d != Math.Floor(d) ? "G7" : "N0", CultureInfo.InvariantCulture);

    static string About(ProfileTarget t)
    {
        var d = t.Row.Def;
        var parts = new List<string> { t.Part != null ? "a part of " + d.Kind : d.Kind };
        if (t.Row.Subrecord != null) parts.Add(t.Row.Offset is { } o ? $"{t.Signature}/{t.Row.Subrecord} at {o}" : $"{t.Signature}/{t.Row.Subrecord}");
        if (d.XEdit is { Length: > 0 } x && x != d.Name) parts.Add($"xEdit \"{x}\"");
        if (d.Unknown) parts.Add("unknown");
        return string.Join("; ", parts);
    }

    /// <summary>"0 ×9,800 (99.2%); 1065353216 ×70": the most common values.</summary>
    static string TopText(FieldStats f, int n)
    {
        var top = f.Top(n).Select(v => $"{Cell(v.Key)} ×{N(v.Value)} ({Pct(v.Value, f.Values)})").ToList();
        if (f.Distinct > n) top.Add($"… {N(f.Distinct - n)} more");
        return string.Join("; ", top);
    }

    /// <summary>What the field's nonzero 4-byte values read as, one clause per offset.</summary>
    static string WordsText(FieldStats f)
    {
        var parts = new List<string>();
        foreach (var (off, w) in f.Words)
        {
            long nz = w.N - w.Zero;
            if (nz == 0) { if (off < 0) parts.Add("every one is 0"); continue; }
            var types = w.FormIdTypes.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(5).Select(x => $"{x.Key} {N(x.Value)}");
            string at = off < 0 ? "" : $"bytes {off}-{off + 3}: ";
            parts.Add($"{at}of {N(nz)} nonzero, {N(w.FloatLike)} read as floats (1e-4 to 1e6), {N(w.BoolLike)} are exactly 1; "
                + (w.Tested == 0 ? "none has an object ID of 0x1000 or more, so none is tested as a FormID or strings ID"
                : $"of the {N(w.Tested)} with an object ID of 0x1000 or more, {N(w.FormIdHits)} name a record its owner holds{(w.FormIdHits > 0 ? $" ({string.Join(", ", types)})" : "")} and {N(w.StringHits)} are strings IDs of the plugin's strings files")
                + (w.ReadsAs != null ? $". They read as {w.ReadsAs}" : w.Chance != null ? $". Not links: {w.Chance}" : ""));
        }
        return string.Join("; ", parts);
    }

    static string SampleText(ProfileSample s, bool many) =>
        $"{s.Label}{(many ? " " + s.RecordType : "")} {s.Place} = {Cell(s.Value)}" + (s.Beside.Count > 0 ? $" [beside: {Cell(string.Join("; ", s.Beside.Select(b => b.Key + " " + b.Value)))}]" : "");

    protected override void Render(ReportBody body, ReportTextOptions options)
    {
        int types = Fields.Select(f => f.Target.Type).Distinct().Count();
        body.Head.Add($"Profile of {Plural(Fields.Count, "field")} of {Plural(types, "record type")} over {Set.Shown}: {N(RecordsRead)} records read in {Plural(PluginsRead, "plugin")}.");
        bool full = options.Detail || Fields.Count <= FullUpTo;
        body.RowNoun = "fields";
        if (!full)
        {
            body.Head.Add($"One row per field; ask for a few fields (or detail) for each one's plugins, values and samples.");
            body.Head.Add("");
            body.Head.Add("| Field | Kind | Records (plugins) | Values | Zero | Distinct | Most common | 4 bytes read as |");
            body.Head.Add("|---|---|---|---|---|---|---|---|");
            foreach (var f in Fields.Where(f => f.Values > 0))
            {
                var w = f.Words.GetValueOrDefault(-1) ?? f.Words.Values.FirstOrDefault();
                long nz = w == null ? 0 : w.N - w.Zero;
                string reads = w == null ? "" : nz == 0 ? "all 0" : (w.ReadsAs != null ? $"**{w.ReadsAs}**; " : "") + $"float {Pct(w.FloatLike, nz)}" + (w.Tested == 0 ? "; FormID, strings ID: none tested (small)" : $"; of {N(w.Tested)} tested: FormID {Pct(w.FormIdHits, w.Tested)}, strings ID {Pct(w.StringHits, w.Tested)}");
                body.Rows.Add($"| {Cell(f.Target.Name)} | {Cell(f.Target.Row.Def.Kind)} | {N(f.Records)} ({f.Plugins.Count(p => p.Value.Records > 0)}) | {N(f.Values)} | {Pct(f.Zero, f.Values)} | {N(f.Distinct)}{(f.OtherValues > 0 ? "+" : "")} | {TopText(f, 3)} | {reads} |");
            }
            var empty = Fields.Where(f => f.Values == 0).Select(f => f.Target.Name).ToList();
            if (empty.Count > 0) body.Rows.Add($"No value in {Set.Shown} ({Plural(empty.Count, "field")}): {string.Join(", ", empty)}.");
            if (Fields.Count == 0) body.Empty = "No field to profile.";
            return;
        }
        foreach (var f in Fields)
        {
            var lines = new List<string> { "" };
            string range = double.IsFinite(f.Min) ? $"; from {Num(f.Min)} to {Num(f.Max)}" : "";
            int plugins = f.Plugins.Count(p => p.Value.Records > 0);
            lines.Add($"## {f.Target.Name} ({About(f.Target)})");
            if (f.Values == 0)
            {
                lines.Add($"No record of {Set.Shown} has a value here: {(RecordsRead == 0 ? "no record of the type was read" : "the field is absent (a subrecord or object not there)")}.");
                body.Rows.Add(string.Join("\n", lines));
                continue;
            }
            lines.Add($"{N(f.Values)} values in {N(f.Records)} records of {Plural(plugins, "plugin")}; {N(f.Zero)} zero ({Pct(f.Zero, f.Values)}); {N(f.Distinct)}{(f.OtherValues > 0 ? $" distinct counted, {N(f.OtherValues)} values past the first {N(ProfileReport.MaxDistinct)} distinct ones not told apart" : " distinct")}{range}{(f.NonFinite > 0 ? $"; {N(f.NonFinite)} not finite (NaN or infinite)" : "")}.");
            lines.Add($"Most common: {TopText(f, options.Detail ? 25 : 10)}.");
            if (f.Lengths.Count > 0) lines.Add($"Lengths in bytes: {string.Join(", ", f.Lengths.OrderByDescending(l => l.Value).ThenBy(l => l.Key).Take(8).Select(l => $"{l.Key} ×{N(l.Value)}"))}.");
            if (f.Words.Count > 0) lines.Add($"As 4-byte values: {WordsText(f)}.");
            var byPlugin = f.Plugins.Where(p => p.Value.Records > 0).OrderByDescending(p => p.Value.Values).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
            int showP = options.Detail ? byPlugin.Count : 8;
            lines.Add($"By plugin (records, nonzero records; values, nonzero): {string.Join("; ", byPlugin.Take(showP).Select(p => $"{p.Key} {N(p.Value.Records)}, {N(p.Value.RecordsNonZero)}; {N(p.Value.Values)}, {N(p.Value.NonZero)}"))}{(byPlugin.Count > showP ? $"; … {byPlugin.Count - showP} more plugins (detail lists them)" : "")}.");
            var nonzero = f.Samples.Where(s => !s.Zero).Take(options.Detail ? ProfileReport.KeptSamples : ShownSamples).ToList();
            var zero = f.Samples.Where(s => s.Zero).Take(options.Detail ? ProfileReport.KeptSamples / 3 : 1).ToList();
            bool many = Fields.Select(x => x.Target.Type).Distinct().Count() > 1 || f.Samples.Select(s => s.RecordType).Distinct().Count() > 1;
            foreach (var s in nonzero) lines.Add("- " + SampleText(s, many));
            foreach (var s in zero) lines.Add("- zero: " + SampleText(s, many));
            body.Rows.Add(string.Join("\n", lines));
        }
        if (Fields.Count == 0) body.Empty = "No field to profile.";
    }

    protected override void Fill(JsonObject json)
    {
        json["plugins"] = Set.Shown;
        json["winnersOnly"] = Set.WinnersOnly;
        json["recordsRead"] = RecordsRead;
        json["pluginsRead"] = PluginsRead;
        var arr = new JsonArray();
        foreach (var f in Fields)
        {
            var d = f.Target.Row.Def;
            var o = new JsonObject
            {
                ["field"] = f.Target.Name, ["type"] = f.Target.Type, ["signature"] = f.Target.Signature, ["path"] = f.Target.Path,
                ["kind"] = d.Kind, ["subrecord"] = f.Target.Row.Subrecord, ["offset"] = f.Target.Row.Offset, ["unknown"] = d.Unknown, ["xedit"] = d.XEdit,
                ["records"] = f.Records, ["values"] = f.Values, ["zero"] = f.Zero, ["distinct"] = f.Distinct, ["otherValues"] = f.OtherValues,
                ["min"] = double.IsFinite(f.Min) ? f.Min : null, ["max"] = double.IsFinite(f.Max) ? f.Max : null, ["nonFinite"] = f.NonFinite,
                ["top"] = new JsonArray(f.Top(25).Select(v => (JsonNode)new JsonArray(v.Key, v.Value)).ToArray()),
                ["byPlugin"] = new JsonObject(f.Plugins.Where(p => p.Value.Records > 0).OrderBy(p => Set.Plugins.FindIndex(x => x.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => KeyValuePair.Create(p.Key, (JsonNode?)new JsonObject { ["records"] = p.Value.Records, ["recordsNonZero"] = p.Value.RecordsNonZero, ["values"] = p.Value.Values, ["nonZero"] = p.Value.NonZero }))),
            };
            if (f.Lengths.Count > 0) o["lengths"] = new JsonObject(f.Lengths.OrderBy(l => l.Key).Select(l => KeyValuePair.Create(l.Key.ToString(CultureInfo.InvariantCulture), (JsonNode?)l.Value)));
            if (f.Words.Count > 0)
                o["words"] = new JsonArray(f.Words.Select(w => (JsonNode)new JsonObject
                {
                    ["offset"] = w.Key < 0 ? null : w.Key, ["n"] = w.Value.N, ["zero"] = w.Value.Zero, ["float"] = w.Value.FloatLike, ["tested"] = w.Value.Tested, ["readsAs"] = w.Value.ReadsAs, ["formId"] = w.Value.FormIdHits,
                    ["formIdTypes"] = new JsonObject(w.Value.FormIdTypes.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
                    ["stringsId"] = w.Value.StringHits, ["one"] = w.Value.BoolLike,
                }).ToArray());
            o["samples"] = new JsonArray(f.Samples.Select(s => (JsonNode)new JsonObject
            {
                ["plugin"] = s.Plugin, ["formKey"] = s.FormKey, ["editorId"] = s.EditorId, ["recordType"] = s.RecordType, ["place"] = s.Place, ["value"] = s.Value,
                ["number"] = s.Number is { } n && double.IsFinite(n) ? n : null, ["zero"] = s.Zero,
                ["beside"] = new JsonObject(s.Beside.Select(b => KeyValuePair.Create(b.Key, (JsonNode?)b.Value))),
            }).ToArray());
            arr.Add(o);
        }
        json["fields"] = arr;
        if (Skipped.Count > 0) json["skipped"] = new JsonObject(Skipped.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value)));
    }
}
