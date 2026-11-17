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
    /// <summary>The condition census (GAPS S44): every condition of every record (of <see cref="Type"/>, or of every type
    /// that can hold one), counted by its function.</summary>
    public bool Conditions { get; init; }
    /// <summary>With <see cref="Conditions"/>: only this function (its index, 961, or its name).</summary>
    public string? Function { get; init; }
    /// <summary>The world link index, when the caller keeps one (GAPS S99): a world type's link field is counted from it
    /// when every plugin read has its file.</summary>
    public IWorldLinkIndex? World { get; init; }
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
    /// <summary>A place that holds conditions, counted by function (the condition census), not profiled as a value.</summary>
    public bool Census { get; init; }
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
    /// <summary>The condition census: the function asked for, or null for every one.</summary>
    public long? Function { get; init; }
    public bool Census => Targets.Count > 0 && Targets.All(t => t.Census);
    /// <summary>Every field asked is a link field the files' bytes give (GAPS S92): read from them, not through Mutagen.</summary>
    internal List<ProfileReport.ByteField>? FromBytes { get; init; }
    /// <summary>The fields are counted from the world link index (GAPS S99): every plugin read has its file.</summary>
    internal IWorldLinkIndex? FromWorld { get; init; }
    /// <summary>The plugins whose world link files would answer the fields (whether made or not); null when the index
    /// can't answer them: what a "save the index" answer would make.</summary>
    public IReadOnlyList<IndexedPlugin>? WorldPlugins { get; init; }
}

public sealed partial class ProfileReport(ReportSession session)
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
        long? function = null;
        if (o.Conditions)
        {
            if (o.Fields.Count > 0 || o.Unknown) throw new ReportException("conditions was given with fields or unknown", "ask for the condition census alone (with a type, or alone for every type)");
            var types = o.Type == null ? model.RecordTypes.ToList() : TypesNamed(o.Type);
            foreach (var t in types)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in model.Expand(t.Name).Where(r => r.Def.RefObject is { } ro && ConditionObjects.Contains(ro)))
                    if (seen.Add(row.Path)) targets.Add(new ProfileTarget { Type = t.Name, Signature = t.Sig!, Path = row.Path, Row = row, Census = true });
            }
            if (targets.Count == 0) throw new ReportException($"{o.Type} can't hold a condition (no field of it is a Condition)", "describe *Conditions* lists the types that can; or leave the type out for every one");
            if (o.Function is { } fn) function = FunctionIndex(fn);
        }
        else if (o.Function != null) throw new ReportException("function was given without conditions", "add --conditions: profile --conditions --function 961");
        else if (o.Unknown)
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
        // Link fields whose places the bytes give are read from the bytes on all cores (GAPS S92): as the type
        // statistics, 0.003 s a MB as stored and 1 µs a record (profile --subrecords "*", ProfileStats.cs)
        var fromBytes = o.Conditions || o.Unknown ? null : ByteRoute(targets);
        if (fromBytes != null) { seconds = 0.5 + bytes / 1048576.0 * 0.003 + records * 1e-6; memory = 1500; }
        // A world type's link field the world link index answers (GAPS S99): its postings, 0.1 µs a record
        List<IndexedPlugin>? worldPlugins = fromBytes != null && o.World != null && WorldAnswers(fromBytes, session.Schema) ? WorldPlugins(set, targets, o.World) : null;
        bool fromWorld = worldPlugins is { Count: > 0 } && worldPlugins.All(o.World!.Has);
        if (fromWorld) { seconds = 0.5 + records * 1e-7; memory = 500; }
        string what = $"The profile of {ReportAnswer.Plural(targets.Count, "field")} reads {ReportAnswer.N(records)} records ({Sizes.Text(bytes)}) of {string.Join(", ", sigs.OrderBy(s => s, StringComparer.Ordinal).Take(8))}{(sigs.Count > 8 ? $" and {sigs.Count - 8} more types" : "")} in {set.Shown}";
        if (o.Conditions) what = $"The condition census reads {ReportAnswer.N(records)} records ({Sizes.Text(bytes)}) of the {ReportAnswer.Plural(sigs.Count, "record type")} that can hold a condition in {set.Shown}";
        if (fromWorld) what += ", from the world link index";
        else if (fromBytes != null) what += ", from the files' bytes";
        var plan = new ProfilePlan
        {
            Targets = targets, Set = set, Records = records, Bytes = bytes, Seconds = seconds, MemoryMB = memory, What = what, Function = function,
            FromBytes = fromBytes, FromWorld = fromWorld ? o.World : null, WorldPlugins = worldPlugins is { Count: > 0 } ? worldPlugins : null,
        };
        plan.Notes.AddRange(notes);
        return plan;
    }

    static readonly HashSet<string> ConditionObjects = new(StringComparer.Ordinal) { "Condition", "ConditionFloat", "ConditionGlobal" };

    /// <summary>A condition function by its index (961) or its name (GetGameplayOptionCurrentValue, Mutagen's Condition.Function).</summary>
    long FunctionIndex(string text)
    {
        string t = text.Trim();
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) return n;
        var members = session.Schema.Enum("Condition.Function") ?? (IReadOnlyList<EnumMember>)Array.Empty<EnumMember>();
        if (members.FirstOrDefault(m => m.Name.Equals(t, StringComparison.OrdinalIgnoreCase)) is { } m) return m.Value;
        var near = NearNames.Pick(t, members, x => new[] { x.Name }, 4).Select(x => $"{x.Name} ({x.Value})");
        throw new ReportException($"there is no condition function named \"{t}\"", NearNames.Fix(near) + "; or give its index (961)");
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
        var answer = new ProfileAnswer { Set = plan.Set, ShownSamples = Math.Clamp(samples, 0, KeptSamples), Census = plan.Census, FunctionAsked = plan.Function };
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
        var walker = new Walker(tries, targets.Select(t => Walker.Seed(t.Name)).ToArray(), targets.Select(t => t.Census).ToArray(), plan.Function);
        if (plan.FromBytes is { } route && plan.FromWorld is { } world) RunWorld(plan, answer, fields, route, walker, scope, world);
        else if (plan.FromBytes is { } route2) RunBytes(plan, answer, fields, route2, walker, scope);
        else foreach (var p in plan.Set.Plugins)
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
            if (f.Targets != null) f.Links = LinkCensus.Of(f.Targets, f.NullLinks, index);
        }
        // Several link fields asked together (GroupedPackIn's GNAM and JNAM, one field in two record types): the
        // census of all their targets as one, each record counted once
        var linked = fields.Where(f => f.Targets != null).ToList();
        if (linked.Count > 1)
        {
            var all = new Dictionary<FormKey, long>();
            foreach (var f in linked) foreach (var (k, n) in f.Targets!) all[k] = all.GetValueOrDefault(k) + n;
            answer.LinksTogether = LinkCensus.Of(all, linked.Sum(f => f.NullLinks), index);
            answer.LinksTogetherOf = linked.Select(f => f.Target.Name).ToList();
        }
        if (fields.Any(f => f.Words.Values.Any(w => w.Tested > 0)))
            answer.Notes.Add("A 4-byte value is tested as a FormID and a strings ID only when its object ID (low 24 bits) is 0x1000 or more. It reads as links when 90% of the tested values name a record and one record type takes 60% of those and 3 times its share of Starfield.esm's own records (whose IDs fill 91% of 0x000001-0x3FFFFF, 86% of them placed references): the rule of the Mutagen session's analysis (C208).");
        answer.Read.Add($"{ReportAnswer.N(answer.RecordsRead)} records of {ReportAnswer.Plural(answer.PluginsRead, "plugin")}{(answer.ReadFromBytes != null ? ", " + answer.ReadFromBytes : "")}");
        if (plan.Census)
        {
            foreach (var f in answer.Functions.Values) f.Finish();
            answer.Notes.Add("Each condition is counted where Mutagen reads it: its function is the condition data's own (Mutagen's Condition.Function, the index the CTDA stores). The same rows, one per condition, are SQL's Conditions view (query2).");
        }
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
        answer.CensusRecords += r.CondRecords;
        foreach (var (fn, acc) in r.Functions)
        {
            if (!answer.Functions.TryGetValue(fn, out var f)) answer.Functions[fn] = f = new FunctionStats { Index = fn, Name = acc.Name };
            f.Conditions += acc.Conditions; f.Records += acc.Records; answer.CensusConditions += acc.Conditions;
            f.Plugins[ctx.Plugin.Name] = f.Plugins.GetValueOrDefault(ctx.Plugin.Name) + acc.Conditions;
            foreach (var (t, n) in acc.Types) f.Types[t] = f.Types.GetValueOrDefault(t) + n;
            f.Offer(acc.Samples);
        }
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
            f.NullLinks += acc.NullLinks;
            if (acc.Targets != null)
            {
                f.Targets ??= new();
                foreach (var (k, n) in acc.Targets) f.Targets[k] = f.Targets.GetValueOrDefault(k) + n;
            }
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
        /// <summary>A link field's targets (GAPS S45): each FormKey with its uses; and the null links.</summary>
        public Dictionary<FormKey, long>? Targets;
        public long NullLinks;
        public readonly List<ProfileSample> Samples = new(), SamplesZero = new();
        public int LastRecord = -1, LastNonZero = -1, Instance;
        public void Word(int off, uint w)
        {
            if (!Words.TryGetValue(off, out var d)) Words[off] = d = new();
            d[w] = d.GetValueOrDefault(w) + 1;
        }
    }

    sealed class CensusAcc
    {
        public string Name = "";
        public long Conditions, Records;
        public int LastRecord = -1;
        public readonly Dictionary<string, long> Types = new(StringComparer.Ordinal);
        public readonly List<ProfileSample> Samples = new();
    }

    sealed class BatchResult
    {
        public long Records, CondRecords;
        public int LastCondRecord = -1;
        public readonly Dictionary<long, CensusAcc> Functions = new();
        public readonly Dictionary<int, Acc> Fields = new();
        public readonly Dictionary<string, long> Skipped = new(StringComparer.Ordinal);
        public Acc Of(int i) => Fields.TryGetValue(i, out var a) ? a : Fields[i] = new Acc();
    }

    sealed class Walker(Dictionary<string, Node> tries, ulong[] seeds, bool[] census, long? function)
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
            // A dictionary's entry, and a 2D array's (Noggog's KeyValue<P2Int, T>: a surface pattern's blocks): its value
            if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) || t.GetGenericTypeDefinition() == typeof(Noggog.KeyValue<,>))) return t.GetProperty("Value")!.GetValue(item)!;
            return item;
        }

        static readonly ConcurrentDictionary<Type, bool> OrIndexTypes = new();

        /// <summary>A FormLinkOrIndex (a condition's parameter, GAPS S158): its link when it is one, else its index, an
        /// alias's or a package data input's (FormLinkOrIndex.UsesLink, as Mutagen's EnumerateFormLinks follows it).</summary>
        static object OrIndexValue(object v)
        {
            var t = v.GetType();
            if (!OrIndexTypes.GetOrAdd(t, x => x.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkOrIndexGetter<>)))) return v;
            if (((IFormLinkContainerGetter)v).EnumerateFormLinks().FirstOrDefault() is { } link) return link;
            bool alias = t.GetMethod("UsesAlias")?.Invoke(v, null) is true;
            // Index is never null since fixes.20 (C68: a new FormLinkOrIndex has index 0, as the binary write always gave)
            return new IndexParameter(alias, (uint)t.GetProperty("Index")!.GetValue(v)!);
        }

        static bool IsBytes(object v) => v is ReadOnlyMemorySlice<byte> or byte[] or IReadOnlyList<byte> or Memory<byte> or ReadOnlyMemory<byte>;

        void Value(int field, object value, object parent, Visit c)
        {
            if (census[field]) { Census(field, value, c); return; }
            value = OrIndexValue(value);
            var acc = c.Res.Of(field);
            var (repr, num, bytes) = Repr(value);
            bool zero = IsZero(repr, num, bytes);
            acc.Values++;
            if (!zero) acc.NonZero++;
            if (acc.LastRecord != c.Serial) { acc.LastRecord = c.Serial; acc.Records++; acc.Instance = 0; }
            if (!zero && acc.LastNonZero != c.Serial) { acc.LastNonZero = c.Serial; acc.RecordsNonZero++; }
            int instance = acc.Instance++;
            acc.Counts[repr] = acc.Counts.GetValueOrDefault(repr) + 1;
            // A link: its target, for the census of what the field points at
            if (value is IFormLinkGetter link) Target(acc, link.IsNull ? null : link.FormKey);
            else if (value is FormKey key) Target(acc, key.IsNull ? null : key);
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

        static void Target(Acc acc, FormKey? key)
        {
            if (key is not { } k) { acc.NullLinks++; return; }
            acc.Targets ??= new();
            acc.Targets[k] = acc.Targets.GetValueOrDefault(k) + 1;
        }

        /// <summary>One condition, counted by its function (the condition census).</summary>
        void Census(int field, object value, Visit c)
        {
            if (value is not Mutagen.Bethesda.Starfield.IConditionGetter cond) return;
            var (fn, name) = FunctionOf(cond.Data);
            if (function is { } only && fn != only) return;
            var res = c.Res;
            if (res.LastCondRecord != c.Serial) { res.LastCondRecord = c.Serial; res.CondRecords++; }
            if (!res.Functions.TryGetValue(fn, out var acc)) res.Functions[fn] = acc = new CensusAcc { Name = name };
            acc.Conditions++;
            if (acc.LastRecord != c.Serial) { acc.LastRecord = c.Serial; acc.Records++; }
            acc.Types[c.Rec.Type] = acc.Types.GetValueOrDefault(c.Rec.Type) + 1;
            // Samples: the smallest marks of the function, one a record (the same whichever thread reads them)
            ulong mark = Mark(c.Info.Plugin, c.Info.FormKey, Seed(name), 0);
            int had = acc.Samples.FindIndex(x => x.Plugin == c.Info.Plugin && x.FormKey == c.Info.FormKey);
            if (had >= 0) { if (acc.Samples[had].Mark <= mark) return; acc.Samples.RemoveAt(had); }
            if (acc.Samples.Count >= KeptSamples)
            {
                int worst = 0;
                for (int i = 1; i < acc.Samples.Count; i++) if (acc.Samples[i].Mark > acc.Samples[worst].Mark) worst = i;
                if (acc.Samples[worst].Mark <= mark) return;
                acc.Samples.RemoveAt(worst);
            }
            var beside = Beside(cond.Data, null);
            foreach (var kv in Beside(cond, null)) if (beside.Count < 10) beside.Add(kv);
            acc.Samples.Add(new ProfileSample
            {
                Mark = mark, Plugin = c.Info.Plugin, FormKey = c.Info.FormKey, EditorId = c.Rec.Record.EditorID ?? c.Info.EditorId,
                RecordType = c.Rec.Type, Place = string.Join('.', c.Place), Value = name, Number = fn, Beside = beside,
            });
        }

        /// <summary>A condition's function: the condition data's own (Mutagen's IConditionDataGetter.Function, real for
        /// every ConditionData class since fixes.20, C58 W19).</summary>
        static (long, string) FunctionOf(Mutagen.Bethesda.Starfield.IConditionDataGetter data)
        {
            var f = data.Function;
            long n = Convert.ToInt64(f, CultureInfo.InvariantCulture);
            return (n, Enum.IsDefined(f) ? f.ToString() : n.ToString(CultureInfo.InvariantCulture));
        }

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

        /// <summary>The object is of the kind the path names (<c>{ObjectModFormLinkIntProperty}</c>): its class, its overlay or
        /// its getter interface, a generic one by its name without the arity (ObjectModFormLinkIntProperty`1).</summary>
        static bool IsKind(object o, string kind) => Kinds.GetOrAdd((o.GetType(), kind), k =>
        {
            static string Plain(string name) => name.Split('`')[0];
            string n = Plain(k.Item1.Name);
            return n == k.Item2 || n == k.Item2 + "BinaryOverlay" || k.Item1.GetInterfaces().Any(i => Plain(i.Name) == "I" + k.Item2 + "Getter");
        });

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

        internal static ulong Mark(string plugin, string formKey, ulong seed, int instance)
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
            case IndexParameter ip: return ((ip.Alias ? "alias index " : "package data index ") + ip.Index.ToString(inv), ip.Index, null);
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

/// <summary>
/// What a link field points at across the plugins read (GAPS S45): the links that aren't null, the distinct records
/// they name, and the record type of each (from the header index: the record its owner holds, or else any plugin's
/// copy of it), by distinct records and by uses; the links to a record that no plugin in Data holds; the most used
/// targets. C221 asked it of GroupedPackIn's HNAM (39,917 targets, all placed references) with a scratch scan.
/// </summary>
public sealed class LinkCensus
{
    public long Links, Nulls, Distinct, Missing, MissingUses;
    /// <summary>By signature: distinct targets and uses.</summary>
    public readonly SortedDictionary<string, (long Distinct, long Uses)> Types = new(StringComparer.Ordinal);
    public List<(string FormKey, string? EditorId, string Sig, long Uses)> Top { get; } = new();

    internal static LinkCensus Of(Dictionary<FormKey, long> targets, long nulls, HeaderIndex index)
    {
        var c = new LinkCensus { Nulls = nulls, Distinct = targets.Count };
        var all = new List<(string, string?, string, long)>(targets.Count);
        foreach (var (k, n) in targets)
        {
            c.Links += n;
            string key = k.ToString();
            string sig = "", edid = "";
            if (index.Versions(key) is { } v && v.Holders.Count > 0)
            {
                // The record its owner holds, else the first copy in Data
                var pick = v.Holders[0];
                foreach (var h in v.Holders) if (index.Plugins[h.Plugin].Name.Equals(k.ModKey.FileName, StringComparison.OrdinalIgnoreCase)) { pick = h; break; }
                var info = index.Describe(pick);
                sig = info.Signature; edid = info.EditorId ?? "";
            }
            if (sig.Length == 0) { c.Missing++; c.MissingUses += n; sig = "(not in Data)"; }
            var t = c.Types.GetValueOrDefault(sig);
            c.Types[sig] = (t.Distinct + 1, t.Uses + n);
            all.Add((key, edid.Length > 0 ? edid : null, sig, n));
        }
        c.Top.AddRange(all.OrderByDescending(x => x.Item4).ThenBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).Take(25));
        return c;
    }

    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public string Text(int top) =>
        $"Points at: {N(Links)} links that aren't null ({N(Nulls)} null) to {N(Distinct)} distinct records: "
        + string.Join(", ", Types.OrderByDescending(t => t.Value.Uses).Select(t => $"{t.Key} {N(t.Value.Distinct)} ({N(t.Value.Uses)} uses)"))
        + (Missing > 0 ? $"; {N(Missing)} of them ({N(MissingUses)} uses) are in no plugin in Data" : "")
        + $". Most used: {string.Join("; ", Top.Take(top).Select(t => $"{t.EditorId ?? "(no EditorID)"} ({t.FormKey}) {t.Sig} ×{N(t.Uses)}"))}.";

    public string Short() => $"links to {string.Join(", ", Types.OrderByDescending(t => t.Value.Uses).Take(3).Select(t => $"{t.Key} {N(t.Value.Distinct)}"))} ({N(Distinct)} distinct, {N(Nulls)} null)";

    public JsonObject ToJson() => new()
    {
        ["links"] = Links, ["null"] = Nulls, ["distinct"] = Distinct, ["missing"] = Missing, ["missingUses"] = MissingUses,
        ["types"] = new JsonObject(Types.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)new JsonObject { ["distinct"] = t.Value.Distinct, ["uses"] = t.Value.Uses }))),
        ["top"] = new JsonArray(Top.Select(t => (JsonNode)new JsonObject { ["formKey"] = t.FormKey, ["editorId"] = t.EditorId, ["sig"] = t.Sig, ["uses"] = t.Uses }).ToArray()),
    };
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
    /// <summary>For a link field: every target with its uses (while reading), then the census of them.</summary>
    internal Dictionary<FormKey, long>? Targets;
    internal long NullLinks;
    public LinkCensus? Links { get; internal set; }
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

/// <summary>One condition function in the condition census.</summary>
public sealed class FunctionStats
{
    public long Index { get; init; }
    public string Name { get; init; } = "";
    public long Conditions, Records;
    /// <summary>Conditions by the record type that holds them.</summary>
    public readonly Dictionary<string, long> Types = new(StringComparer.Ordinal);
    /// <summary>Conditions by plugin.</summary>
    public readonly Dictionary<string, long> Plugins = new(StringComparer.OrdinalIgnoreCase);
    public List<ProfileSample> Samples { get; private set; } = new();

    internal void Offer(List<ProfileSample> more)
    {
        Samples.AddRange(more);
        Samples = Samples.GroupBy(s => (s.Plugin, s.FormKey)).Select(g => g.MinBy(s => s.Mark)!).OrderBy(s => s.Mark).Take(ProfileReport.KeptSamples).ToList();
    }

    internal void Finish() => Samples = Samples.OrderBy(s => s.Mark).ToList();
}

public sealed class ProfileAnswer : ReportAnswer
{
    public override string Kind => "profile";
    public required PluginSet Set { get; init; }
    /// <summary>The condition census (profile --conditions): <see cref="Functions"/> instead of fields.</summary>
    public bool Census { get; init; }
    public long? FunctionAsked { get; init; }
    public SortedDictionary<long, FunctionStats> Functions { get; } = new();
    public long CensusConditions { get; set; }
    /// <summary>How the records were read when not through Mutagen (the files' bytes, GAPS S92), for the Read line.</summary>
    public string? ReadFromBytes { get; set; }
    /// <summary>Two or more link fields asked: the census of all their targets together, and which fields.</summary>
    public LinkCensus? LinksTogether { get; set; }
    public List<string> LinksTogetherOf { get; set; } = new();
    /// <summary>Records (each copy in each plugin) that hold at least one condition counted.</summary>
    public long CensusRecords { get; set; }
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

    void RenderCensus(ReportBody body, ReportTextOptions options)
    {
        int holding = Fields.Select(f => f.Target.Type).Distinct().Count();
        string which = FunctionAsked is { } fa ? $"function {fa}{(Functions.TryGetValue(fa, out var only) ? " " + only.Name : "")}" : "every function";
        body.Head.Add($"Condition census ({which}) over {Set.Shown}: {N(CensusConditions)} conditions in {N(CensusRecords)} records, {Plural(Functions.Count, "function")}; {N(RecordsRead)} records of the {Plural(holding, "record type")} that can hold a condition read in {Plural(PluginsRead, "plugin")}.");
        body.RowNoun = "functions";
        if (Functions.Count == 0) { body.Empty = $"No condition{(FunctionAsked != null ? " of that function" : "")} in {Set.Shown}."; return; }
        bool full = options.Detail || Functions.Count == 1;
        var ordered = Functions.Values.OrderByDescending(f => f.Conditions).ThenBy(f => f.Index).ToList();
        if (!full)
        {
            body.Head.Add("Most used first. --function <index or name> gives one function's plugins, record types and samples; --json has every one in full.");
            body.Head.Add("");
            body.Head.Add("| Function | Name | Conditions | Records | Plugins | Record types | Sample |");
            body.Head.Add("|---|---|---|---|---|---|---|");
            foreach (var f in ordered)
            {
                var types = f.Types.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
                string typeText = string.Join(", ", types.Take(3).Select(x => $"{x.Key} {N(x.Value)}")) + (types.Count > 3 ? $", +{types.Count - 3}" : "");
                string sample = f.Samples.FirstOrDefault() is { } s ? $"{s.Label} {s.Place}" : "";
                body.Rows.Add($"| {f.Index} | {Cell(f.Name)} | {N(f.Conditions)} | {N(f.Records)} | {f.Plugins.Count} | {Cell(typeText)} | {Cell(sample)} |");
            }
            return;
        }
        foreach (var f in ordered)
        {
            var lines = new List<string> { "", $"## {f.Index} {f.Name}", $"{N(f.Conditions)} conditions in {N(f.Records)} records of {Plural(f.Plugins.Count, "plugin")}." };
            lines.Add($"By record type: {string.Join(", ", f.Types.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} {N(x.Value)}"))}.");
            var byPlugin = f.Plugins.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
            int showP = options.Detail ? byPlugin.Count : 12;
            lines.Add($"By plugin: {string.Join(", ", byPlugin.Take(showP).Select(x => $"{x.Key} {N(x.Value)}"))}{(byPlugin.Count > showP ? $", … {byPlugin.Count - showP} more plugins (detail lists them)" : "")}.");
            foreach (var s in f.Samples.Take(options.Detail ? ProfileReport.KeptSamples : Math.Max(ShownSamples, 1)))
                lines.Add($"- {s.Label} {s.RecordType} {s.Place}" + (s.Beside.Count > 0 ? $" [{Cell(string.Join("; ", s.Beside.Select(b => b.Key + " " + b.Value)))}]" : ""));
            body.Rows.Add(string.Join("\n", lines));
        }
    }

    protected override void Render(ReportBody body, ReportTextOptions options)
    {
        if (Census) { RenderCensus(body, options); return; }
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
                string reads = f.Links is { } lcs ? lcs.Short() : w == null ? "" : nz == 0 ? "all 0" : (w.ReadsAs != null ? $"**{w.ReadsAs}**; " : "") + $"float {Pct(w.FloatLike, nz)}" + (w.Tested == 0 ? "; FormID, strings ID: none tested (small)" : $"; of {N(w.Tested)} tested: FormID {Pct(w.FormIdHits, w.Tested)}, strings ID {Pct(w.StringHits, w.Tested)}");
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
            if (f.Links is { } lc) lines.Add(lc.Text(options.Detail ? 25 : 8));
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
        if (LinksTogether is { } lt) body.Rows.Add($"\n## The {LinksTogetherOf.Count} link fields together ({string.Join(", ", LinksTogetherOf)})\n{lt.Text(options.Detail ? 25 : 8)}");
    }

    protected override void Fill(JsonObject json)
    {
        json["plugins"] = Set.Shown;
        json["winnersOnly"] = Set.WinnersOnly;
        json["recordsRead"] = RecordsRead;
        json["pluginsRead"] = PluginsRead;
        if (Census)
        {
            json["census"] = "conditions";
            json["function"] = FunctionAsked;
            json["conditions"] = CensusConditions;
            json["recordsWithConditions"] = CensusRecords;
            json["recordTypes"] = new JsonArray(Fields.Select(f => f.Target.Type).Distinct().Select(t => (JsonNode)t).ToArray());
            // Every function's counts; its plugins, all its record types and its samples in full only for a few functions
            // (--function), so the census of every function stays small enough to read
            bool few = Functions.Count <= FullUpTo;
            json["functions"] = new JsonArray(Functions.Values.OrderByDescending(f => f.Conditions).ThenBy(f => f.Index).Select(f =>
            {
                var types = f.Types.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
                var o = new JsonObject
                {
                    ["index"] = f.Index, ["name"] = f.Name, ["conditions"] = f.Conditions, ["records"] = f.Records, ["plugins"] = f.Plugins.Count, ["recordTypes"] = types.Count,
                    ["byType"] = new JsonObject(types.Take(few ? types.Count : 5).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
                };
                if (few) o["byPlugin"] = new JsonObject(f.Plugins.OrderBy(p => Set.Plugins.FindIndex(x => x.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase))).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
                o["samples"] = new JsonArray(f.Samples.Take(few ? f.Samples.Count : 1).Select(s =>
                {
                    var so = new JsonObject { ["plugin"] = s.Plugin, ["formKey"] = s.FormKey, ["editorId"] = s.EditorId, ["recordType"] = s.RecordType, ["place"] = s.Place };
                    if (few) so["beside"] = new JsonObject(s.Beside.Select(b => KeyValuePair.Create(b.Key, (JsonNode?)b.Value)));
                    return (JsonNode)so;
                }).ToArray());
                return (JsonNode)o;
            }).ToArray());
            if (Skipped.Count > 0) json["skipped"] = new JsonObject(Skipped.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value)));
            return;
        }
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
            if (f.Links is { } lc) o["links"] = lc.ToJson();
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
        if (LinksTogether is { } lt) json["linksTogether"] = new JsonObject { ["fields"] = new JsonArray(LinksTogetherOf.Select(x => (JsonNode)x).ToArray()), ["census"] = lt.ToJson() };
        if (Skipped.Count > 0) json["skipped"] = new JsonObject(Skipped.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value)));
    }
}

/// <summary>A condition parameter that is an index, not a link: an alias's (UseAliases) or a package data input's.</summary>
internal readonly record struct IndexParameter(bool Alias, uint Index);
