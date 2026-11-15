// Joins the xEdit name pairs to the schema: every pair is matched to a field (or a part of one), or gets a reason.
using System.Text.RegularExpressions;

namespace SFSearch.SchemaGen;

public sealed class PairStats
{
    public int Rows, DistinctPaths, Matched, MatchedDistinct, Exact, Flag, Component, Bytes, Count, BySubrecord, WholeValue;
    /// <summary>Pairs made on a name Mutagen has since changed, matched under the new one (PairMatcher.Renamed).</summary>
    public int Renamed;
    public int SubrecordCompared, SubrecordAgree, SignaturesFilled;
    public int FieldsNamed, EnumMembersNamed;
    /// <summary>Unmatched pairs, by reason. Every pair is matched, not data, or here.</summary>
    public Dictionary<string, int> Reasons = new(StringComparer.Ordinal);
    /// <summary>Pairs whose Mutagen side is Mutagen's own bookkeeping, not data in the file (Match "not data"), by reason.</summary>
    public Dictionary<string, int> NotData = new(StringComparer.Ordinal);
    public double MatchedShare => Rows == 0 ? 0 : (double)Matched / Rows;
}

public static class PairMatcher
{
    static readonly Regex SigPrefix = new(@"^[A-Za-z0-9_]{4} - (.+)$", RegexOptions.Compiled);
    static readonly Regex TranslationEntry = new(@"^(.*)=(.*) \((\d+)\)$", RegexOptions.Compiled);
    static readonly Regex SigLike = new("^[A-Z0-9_]{4}$", RegexOptions.Compiled);

    /// <summary>
    /// Properties of a value's .NET type that are the value itself: a pair naming one names the whole field
    /// (<c>Use.Type</c> is the four letters of the RecordType <c>Use</c>; <c>AnimationRoot.DataRelativePath.Path</c> is the file path).
    /// Any other property of an asset link (<c>IsNull</c>, <c>AssetTypeInstance…</c>, <c>Type…</c>) is Mutagen's own bookkeeping.
    /// </summary>
    /// <summary>The pairing tool's (xecmp's) suffix for an enum field taken as its number: <c>Tier#num</c> is <c>Tier</c>.</summary>
    public const string NumberSuffix = "#num";

    static readonly Dictionary<string, string[]> ValueParts = new(StringComparer.Ordinal)
    {
        ["RecordType"] = new[] { "Type", "TypeInt" },
        ["AssetLink"] = new[] { "DataRelativePath.Path", "GivenPath", "RawPath" },
    };

    /// <summary>
    /// xEdit's subrecord can be held against the schema's: the pair names a field (not a count, not one matched by its
    /// subrecord), and xEdit gives a real signature. xEdit writes NULL for a part with no signature of its own.
    /// </summary>
    public static bool SubrecordComparable(PairRow p)
        => p.Subrecord.Length == 4 && p.Subrecord != "NULL" && p.Match is not (null or "count" or "subrecord" or "not data");

    /// <summary>The last part of an xEdit path, without its signature ("WDMG - Base Damage" gives "Base Damage").</summary>
    public static string LastName(string xeditPath, int fromEnd = 1)
    {
        var parts = xeditPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < fromEnd) return "";
        string last = parts[^fromEnd];
        var m = SigPrefix.Match(last);
        return m.Success ? m.Groups[1].Value : last;
    }

    public static string SpacedOut(string name)
        => Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    static int Weight(PairRow p) => p.Method.StartsWith("order only", StringComparison.Ordinal) ? 1 : 1000;

    /// <summary>A property Mutagen's generator adds that only notes how far a subrecord's data went (Versioning,
    /// &lt;Sig&gt;DataTypeState): Mutagen's bookkeeping, no data in the file.</summary>
    public static bool Bookkeeping(FieldDef f)
        => f.Source != null && (f.Name == "Versioning" || f.Name.EndsWith("DataTypeState", StringComparison.Ordinal));

    static bool UnknownName(string segment)
    {
        string name = segment.Replace("[*]", "");
        return name.Contains("Unknown") || name.Contains("Unused") || SigLike.IsMatch(name);
    }

    sealed class Resolver(SchemaModel model)
    {
        readonly Dictionary<string, List<string>> _typesBySig = model.RecordTypes.GroupBy(r => r.Sig!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(o => o.Name).ToList(), StringComparer.Ordinal);
        readonly Dictionary<(string, string), (string? Type, SchemaModel.Lookup? Look)> _cache = new();

        /// <summary>The first record type of the signature in which the path is found (else the first type, with where it stopped).</summary>
        public (string? Type, SchemaModel.Lookup? Look) Find(PairRow p)
        {
            if (_cache.TryGetValue((p.Sig, p.MutagenPath), out var hit)) return hit;
            SchemaModel.Lookup? best = null; string? bestType = null;
            if (_typesBySig.TryGetValue(p.Sig, out var types))
                foreach (var t in types)
                {
                    var look = model.Resolve(t, p.MutagenPath);
                    if (look.Found.Count > 0) { best = look; bestType = t; break; }
                    best ??= look; bestType ??= t;
                }
            return _cache[(p.Sig, p.MutagenPath)] = (bestType, best);
        }

        /// <summary>The fields a pair's path leads to. When several kinds of object share the path, xEdit's subrecord picks.</summary>
        public static List<SchemaModel.Found> Pick(PairRow p, List<SchemaModel.Found> found)
            => found.Count > 1 && p.Subrecord.Length == 4 && found.Any(f => f.Row.Subrecord == p.Subrecord)
                ? found.Where(f => f.Row.Subrecord == p.Subrecord).ToList() : found;
    }

    /// <summary>
    /// Where Mutagen reads a subrecord with hand-written code, the XML doesn't say which subrecord a field sits in.
    /// xEdit's pairs do. A field gets its signature from them when every pair that reaches it names the same one.
    /// </summary>
    public static int FillSignatures(SchemaModel model, List<PairRow> pairs)
    {
        var resolver = new Resolver(model);
        var tally = new Dictionary<FieldDef, Dictionary<string, int>>();
        foreach (var p in pairs)
        {
            if (p.Subrecord.Length != 4 || p.Subrecord == "NULL" || p.MutagenPath.EndsWith("#count", StringComparison.Ordinal)) continue;
            var (_, look) = resolver.Find(p);
            if (look == null || look.Found.Count == 0) continue;
            foreach (var f in Resolver.Pick(p, look.Found))
            {
                if (f.Row.Subrecord == p.Subrecord) continue;
                // Up from the field to the first row that isn't inside a plain struct: that is where a signature is declared
                var chain = new List<FieldRow>();
                for (FieldRow? r = f.Row; r != null; r = r.Parent) chain.Add(r);
                int anchor = chain.FindIndex(r => !model.Objects[r.Def.Object].Struct);
                if (anchor < 0) continue;
                FieldDef? target = null;
                if (f.Row.Subrecord == null && chain[anchor].Def.Subrecord == null) target = chain[anchor].Def;
                else if (f.Row.Subrecord != null && chain[anchor].Def.Custom && anchor > 0 && chain[anchor - 1].Def.Subrecord == null) target = chain[anchor - 1].Def;
                if (target == null) continue;
                var d = tally.TryGetValue(target, out var x) ? x : tally[target] = new(StringComparer.Ordinal);
                d[p.Subrecord] = d.GetValueOrDefault(p.Subrecord) + 1;
            }
        }
        int filled = 0;
        foreach (var (def, sigs) in tally)
        {
            if (sigs.Count != 1) continue;
            string sig = sigs.Keys.First();
            foreach (var same in model.Objects[def.Object].Fields.Where(x => x.Name == def.Name && x.Subrecord == null && (x == def || x.Path.StartsWith(def.Name, StringComparison.Ordinal))))
            {
                same.Subrecord = sig;
                same.Note = (same.Note == null ? "" : same.Note + "; ") + "signature from xEdit's pairs (the XML doesn't give one)";
            }
            filled++;
        }
        return filled;
    }

    /// <summary>
    /// Fields Mutagen renamed after the pairs were made: (object, old name, new name, Mutagen's item). The pairing tool
    /// (xecmp) wrote its pairs on the names of its Mutagen build; until it runs again on current names, a pair naming
    /// one of these is matched under the new name (<see cref="ApplyRenames"/>). An old name is replaced only where the
    /// pair's path no longer leads anywhere and the new name is a field of that object at that place.
    /// </summary>
    public static readonly (string Object, string Old, string New, string Item)[] Renamed =
    {
        ("SoundReference", "Start", "StartEvent", "C218"),
        ("SoundReference", "Stop", "StopEvent", "C218"),
        ("SoundReference", "Condition", "ConditionForm", "C218"),
        ("SoundReference", "EventMapping", "EventForm", "C218"),
        ("PlayerDialogueSceneAction", "WED0", "Sound", "C218"),
        ("PlacedObjectSpline", "Thickness", "SplineRadius", "C220"),
        ("ActorValueModulation", "TNAM", "Name", "C207"),
        ("Worldspace", "GNAM", "GravityScale", "C207"),
    };

    /// <summary>
    /// A pair made on a name Mutagen has since changed (<see cref="Renamed"/>) gets the new path: its MutagenPath is
    /// rewritten, RenamedFrom keeps the old one, and Detail says so. Returns how many pairs were renamed.
    /// </summary>
    public static int ApplyRenames(SchemaModel model, List<PairRow> pairs)
    {
        var typesBySig = model.RecordTypes.GroupBy(r => r.Sig!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(o => o.Name).ToList(), StringComparer.Ordinal);
        bool Any(string sig, string path, Func<SchemaModel.Found, bool> ok)
            => typesBySig.TryGetValue(sig, out var types) && types.Any(t => model.Resolve(t, path).Found.Any(ok));
        var done = new Dictionary<(string, string), (string Path, List<string> Items)?>();
        int n = 0;
        foreach (var p in pairs)
        {
            if (!done.TryGetValue((p.Sig, p.MutagenPath), out var hit))
            {
                hit = null;
                // The part Resolve takes as a part of a field (a byte slice, a count, an enum's number) stays as it is
                string path = p.MutagenPath, suffix = "";
                int cut = path.IndexOfAny(new[] { '@', '#' });
                if (cut > 0) { suffix = path[cut..]; path = path[..cut]; }
                if (Renamed.Any(r => path.Contains(r.Old, StringComparison.Ordinal)) && !Any(p.Sig, p.MutagenPath, _ => true))
                {
                    var segs = path.Split('.');
                    var items = new List<string>();
                    for (int i = 0; i < segs.Length; i++)
                    {
                        string bare = segs[i].Replace("[*]", "");
                        foreach (var r in Renamed.Where(r => r.Old == bare))
                        {
                            string seg = r.New + segs[i][bare.Length..];
                            string prefix = string.Join('.', segs.Take(i).Append(seg));
                            if (!Any(p.Sig, prefix, f => f.Row.Def.Object == r.Object && f.Row.Def.Name == r.New)) continue;
                            segs[i] = seg; items.Add($"{r.Object}.{r.Old} is {r.New} ({r.Item})");
                            break;
                        }
                    }
                    string renamed = string.Join('.', segs) + suffix;
                    if (items.Count > 0 && Any(p.Sig, renamed, _ => true)) hit = (renamed, items);
                }
                done[(p.Sig, p.MutagenPath)] = hit;
            }
            if (hit is not { } h) continue;
            p.RenamedFrom = p.MutagenPath;
            p.MutagenPath = h.Path;
            p.Detail = $"Made on Mutagen's old name {p.RenamedFrom}: {string.Join(" and ", h.Items)} since. Matched under the new name.";
            n++;
        }
        return n;
    }

    public static PairStats Match(SchemaModel model, List<PairRow> pairs)
    {
        int renamed = ApplyRenames(model, pairs);
        var st = new PairStats { Rows = pairs.Count, DistinctPaths = pairs.Select(p => (p.Sig, p.MutagenPath)).Distinct().Count(), Renamed = renamed };
        st.SignaturesFilled = FillSignatures(model, pairs);
        var resolver = new Resolver(model);
        var names = new Dictionary<FieldDef, Dictionary<string, int>>();
        var memberNames = new Dictionary<(string Enum, string Member), Dictionary<string, int>>();
        var bySub = new Dictionary<string, Dictionary<string, List<FieldRow>>>(StringComparer.Ordinal);

        // A pair held up by position alone ("order only": xEdit shows the same value on every record) names a field only
        // when no better pair does: it counts 1, any other pair 1000.
        void Tally<TKey>(Dictionary<TKey, Dictionary<string, int>> into, TKey key, string name, int weight = 1000) where TKey : notnull
        {
            if (name.Length == 0) return;
            var d = into.TryGetValue(key, out var x) ? x : into[key] = new(StringComparer.Ordinal);
            d[name] = d.GetValueOrDefault(name) + weight;
        }

        Dictionary<string, List<FieldRow>> Subrecords(string type)
        {
            if (bySub.TryGetValue(type, out var subs)) return subs;
            subs = bySub[type] = new(StringComparer.Ordinal);
            foreach (var r in model.Expand(type))
                if (r.Subrecord != null && r.Def.Kind is not ("Break" or "Marker") && !r.Def.Path.EndsWith("[*]", StringComparison.Ordinal))
                    (subs.TryGetValue(r.Subrecord, out var l) ? l : subs[r.Subrecord] = new()).Add(r);
            return subs;
        }

        foreach (var p in pairs)
        {
            var (type, look) = resolver.Find(p);
            if (type == null || look == null)
            {
                p.Reason = "no record type"; p.Detail = $"Mutagen's XML has no record type with the signature {p.Sig}.";
                continue;
            }
            p.Type = type;
            var found = Resolver.Pick(p, look.Found);
            string lastSeg = p.MutagenPath.Split('@', '#')[0].Split('.').Last();

            if (found.Count > 0 && found.All(f => Bookkeeping(f.Row.Def)))
            {
                // A property Mutagen's generator adds to note how far a subrecord's data went (Versioning,
                // <Sig>DataTypeState: GAPS Part 2 row 8). It is a field of the schema now, but holds no bytes: an xEdit
                // field paired with it was paired by a coincidence of values
                var def = found[0].Row.Def;
                bool versioning = def.Name == "Versioning";
                string at = p.MutagenPath.Contains('.') ? $"{type}.{p.MutagenPath[..p.MutagenPath.LastIndexOf('.')]}" : type;
                string sub = versioning ? (p.Subrecord.Length == 4 ? p.Subrecord : "the subrecord") : def.Name[..^"DataTypeState".Length];
                p.Match = "not data"; p.Reason = versioning ? "Mutagen's versioning marker" : "Mutagen's data type state";
                p.Object = def.Object; p.Ord = def.Ord;
                p.Detail = $"\"{def.Name}\" under {at} is Mutagen's note of which optional tail of {sub} was present: a field the schema derives from Mutagen's generator rules, "
                    + $"not data in the file{(p.Subrecord.Length == 4 && p.Subrecord != "NULL" ? $" (xEdit's field is a value in {p.Subrecord})" : "")}: the pairing was a coincidence.";
                continue;
            }

            if (found.Count > 0 && p.Subrecord.Length == 4 && found[0].Match != "count" && found.All(f => f.Row.Subrecord != null && f.Row.Subrecord != p.Subrecord)
                && UnknownName(lastSeg) && found.Any(f => f.Row.Path.Contains('{')))
            {
                // The name still exists, but on another kind of object: the pair was made for a field that has since been named
                p.Reason = "unknown field since named or split";
                p.Detail = $"The path still leads somewhere ({type}.{found[0].Row.Path}, subrecord {found[0].Row.Subrecord}), but xEdit's field is in {p.Subrecord}: a different kind of object. "
                    + Today(p, type, Subrecords(type), out _);
                found = new();
            }
            if (found.Count == 0 && p.Reason == null && p.MutagenPath.EndsWith(NumberSuffix, StringComparison.Ordinal))
            {
                // xecmp's "#num": an enum field's number. The pair names the whole field, when xEdit's subrecord is the field's.
                string path = p.MutagenPath[..^NumberSuffix.Length];
                var num = Resolver.Pick(p, model.Resolve(type, path).Found).Where(f => f.Match == "exact" && f.Row.Enum != null).ToList();
                var same = num.Where(f => p.Subrecord.Length != 4 || f.Row.Subrecord == p.Subrecord).ToList();
                if (same.Count > 0)
                {
                    var row = same[0].Row;
                    p.Match = "whole value"; p.Part = NumberSuffix; p.Object = row.Def.Object; p.Ord = row.Def.Ord; p.SchemaSubrecord = row.Subrecord;
                    p.Detail = $"\"{NumberSuffix}\" is the number of the enum field {type}.{row.Path}: the pair names the whole field.";
                    foreach (var def in same.Select(f => f.Row.Def).Distinct()) Tally(names, def, LastName(p.XEditPath), Weight(p));
                    continue;
                }
                if (num.Count > 0)
                {
                    p.Reason = "enum number in another subrecord";
                    p.Detail = $"\"{NumberSuffix}\" is the number of the enum field {type}.{num[0].Row.Path} (in {num[0].Row.Subrecord}), but xEdit's field is in {p.Subrecord}: the pairing was a coincidence of values. "
                        + Today(p, type, Subrecords(type), out _);
                    p.Detail = p.Detail.TrimEnd();
                    continue;
                }
            }
            if (found.Count == 0 && p.Reason == null && WholeValueOrNotData(p, type, look, out var whole))
            {
                if (whole != null) Tally(names, whole.Def, LastName(p.XEditPath), Weight(p));
                continue;
            }
            if (found.Count == 0)
            {
                if (p.Reason == null) Explain(p, type, look, Today(p, type, Subrecords(type), out _));
                // A field that was named after its subrecord held all of it. If exactly one field holds that subrecord
                // at the same place today, it is the same bytes under a new name.
                Today(p, type, Subrecords(type), out var here);
                if (p.Reason == "unknown field since named or split" && lastSeg.Replace("[*]", "") == p.Subrecord && !p.MutagenPath.Contains('@') && !p.MutagenPath.Contains('#') && here.Count == 1)
                {
                    var row = here[0];
                    p.Match = "subrecord"; p.Part = null; p.Object = row.Def.Object; p.Ord = row.Def.Ord; p.SchemaSubrecord = row.Subrecord;
                    p.Detail = $"Matched by subrecord, not by name: \"{lastSeg}\" was the whole of {p.Subrecord}, and today {type}.{row.Path} is the only field there.";
                    p.Reason = null;
                    Tally(names, row.Def, LastName(p.XEditPath), Weight(p));
                }
                continue;
            }
            var first = found[0];
            p.Match = first.Match; p.Part = first.Part; p.Object = first.Row.Def.Object; p.Ord = first.Row.Def.Ord; p.SchemaSubrecord = first.Row.Subrecord;
            foreach (var def in found.Select(f => f.Row.Def).Distinct())
            {
                if (first.Match == "exact") Tally(names, def, LastName(p.XEditPath), Weight(p));
                else if (first.Match == "component") Tally(names, def, LastName(p.XEditPath, 2), Weight(p));
            }
            if (first.Match == "flag")
                foreach (var en in found.Select(f => f.Row.Enum).Where(e => e != null).Distinct())
                    Tally(memberNames, (en!, first.Part!), LastName(p.XEditPath));
            // "Very Loud=VeryLoud (11)": xEdit spells an enum member differently
            if (first.Match == "exact" && p.Translation.Length > 0)
                foreach (var en in found.Select(f => f.Row.Enum).Where(e => e != null).Distinct())
                    if (model.Enums.TryGetValue(en!, out var members))
                        foreach (var entry in p.Translation.Split("; "))
                            if (TranslationEntry.Match(entry) is { Success: true } m && members.Any(x => x.Name == m.Groups[2].Value) && !long.TryParse(m.Groups[1].Value, out _))
                                Tally(memberNames, (en!, m.Groups[2].Value), m.Groups[1].Value);
        }

        foreach (var (def, tally) in names)
        {
            var bestName = tally.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).First().Key;
            def.XEdit = bestName; def.Readable = bestName; def.XEditPairs = tally.Values.Sum(v => v / 1000 + v % 1000);
            st.FieldsNamed++;
        }
        foreach (var ((en, member), tally) in memberNames)
        {
            var m = model.Enums[en].First(x => x.Name == member);
            m.XEditName = tally.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).First().Key;
            st.EnumMembersNamed++;
        }
        foreach (var f in model.Objects.Values.SelectMany(o => o.Fields))
            f.Readable ??= SpacedOut(f.Name);

        foreach (var p in pairs)
        {
            if (p.Match == null) { st.Reasons[p.Reason!] = st.Reasons.GetValueOrDefault(p.Reason!) + 1; continue; }
            if (p.Match == "not data") { st.NotData[p.Reason!] = st.NotData.GetValueOrDefault(p.Reason!) + 1; continue; }
            st.Matched++;
            switch (p.Match)
            {
                case "exact": st.Exact++; break;
                case "flag": st.Flag++; break;
                case "component": st.Component++; break;
                case "bytes": st.Bytes++; break;
                case "count": st.Count++; break;
                case "subrecord": st.BySubrecord++; break;
                case "whole value": st.WholeValue++; break;
            }
            // xEdit's subrecord against the schema's, as a check on the signatures carried into nested objects
            if (SubrecordComparable(p))
            {
                st.SubrecordCompared++;
                if (p.SchemaSubrecord == p.Subrecord) st.SubrecordAgree++;
            }
        }
        st.MatchedDistinct = pairs.Where(p => p.Match is not (null or "not data")).Select(p => (p.Sig, p.MutagenPath)).Distinct().Count();
        return st;
    }

    /// <summary>
    /// A pair whose path goes past a field into a property of the value's .NET type. When that property is the value
    /// itself (ValueParts) and xEdit's subrecord is the field's, the pair names the whole field: Match "whole value",
    /// <paramref name="whole"/> is the field. Mutagen's own bookkeeping (a struct's versioning marker, an asset link's
    /// IsNull or file-type object) is no data in the file: Match "not data", the pairing was a coincidence.
    /// Any other pair is left to Explain.
    /// </summary>
    static bool WholeValueOrNotData(PairRow p, string type, SchemaModel.Lookup look, out FieldRow? whole)
    {
        whole = null;
        string seg = look.StoppedAtSegment;
        string where = look.PathSoFar.Length == 0 ? type : $"{type}.{look.PathSoFar}";
        if (look.StoppedAtLeaf is { } leaf && seg != "#count")
        {
            bool part = ValueParts.TryGetValue(leaf.Def.Kind, out var parts) && parts.Contains(seg, StringComparer.Ordinal);
            bool sameSubrecord = p.Subrecord.Length != 4 || p.Subrecord == "NULL" || leaf.Subrecord == p.Subrecord;
            if (part && sameSubrecord && !p.MutagenPath.Contains('@') && !p.MutagenPath.EndsWith("#count", StringComparison.Ordinal))
            {
                p.Match = "whole value"; p.Part = seg; p.Object = leaf.Def.Object; p.Ord = leaf.Def.Ord; p.SchemaSubrecord = leaf.Subrecord;
                p.Detail = $"\"{seg}\" is a property of Mutagen's {leaf.Def.Kind} type that holds the value itself: the pair names the whole field, {where}.";
                whole = leaf;
                return true;
            }
            if (leaf.Def.Kind == "AssetLink" && !part)
            {
                p.Match = "not data"; p.Reason = "inside Mutagen's asset link";
                p.Detail = $"{where} is a file path{(leaf.Subrecord != null ? $" (in {leaf.Subrecord})" : "")}. \"{seg}\" is Mutagen's own bookkeeping of it, not data in the file"
                    + $"{(p.Subrecord.Length == 4 && p.Subrecord != leaf.Subrecord ? $", and xEdit's field is in {p.Subrecord}" : "")}: the pairing was a coincidence.";
                return true;
            }
            return false;
        }
        if (look.StoppedAtLeaf == null && seg == "Versioning")
        {
            p.Match = "not data"; p.Reason = "Mutagen's versioning marker";
            p.Detail = $"\"Versioning\" under {where} is Mutagen's note of which optional tail of {(p.Subrecord.Length == 4 ? p.Subrecord : "the subrecord")} was present. "
                + "It isn't in the XML and isn't data in the file: the pairing was a coincidence.";
            return true;
        }
        return false;
    }

    /// <summary>What stands in the pair's subrecord today, beside the place the pair points at.</summary>
    static string Today(PairRow p, string type, Dictionary<string, List<FieldRow>> subs, out List<FieldRow> here)
    {
        here = new();
        if (p.Subrecord.Length != 4) return "";
        string plain = p.MutagenPath.Split('@', '#')[0];
        string parent = plain.Contains('.') ? plain[..plain.LastIndexOf('.')] : "";
        static string Parent(string x) => x.Contains('.') ? x[..x.LastIndexOf('.')] : "";
        static string Last(string x) => x.Contains('.') ? x[(x.LastIndexOf('.') + 1)..] : x;
        var all = subs.TryGetValue(p.Subrecord, out var l) ? l : new List<FieldRow>();
        here = all.Where(r => Parent(r.PlainPath) == parent).GroupBy(r => r.PlainPath).Select(g => g.First()).ToList();
        var show = (here.Count > 0 ? here : all).Select(r => Last(r.PlainPath)).Distinct().ToList();
        return show.Count == 0 ? $"No field of {type} is in {p.Subrecord} today."
            : $"In {p.Subrecord} today: {string.Join(", ", show.Take(8))}{(show.Count > 8 ? $" and {show.Count - 8} more" : "")}.";
    }

    /// <summary>Says why a pair matched nothing.</summary>
    static void Explain(PairRow p, string type, SchemaModel.Lookup look, string today)
    {
        string where = look.PathSoFar.Length == 0 ? type : $"{type}.{look.PathSoFar}";
        var objs = look.StoppedAtObjects;
        string who = objs.Count == 1 ? $"{objs[0]} has no field" : $"None of the {objs.Count} objects that can stand there ({string.Join(", ", objs.Take(3))}{(objs.Count > 3 ? ", …" : "")}) has a field";
        string seg = look.StoppedAtSegment;

        if (look.StoppedAtLeaf is { } leaf && seg == "#count")
        {
            p.Reason = "count of a field that isn't a list";
            p.Detail = $"{where} is a {leaf.Def.Kind} today, not a list. {today}";
        }
        else if (look.StoppedAtLeaf is { } lf)
        {
            if (lf.Def.Kind == "AssetLink")
            {
                // The path itself, but xEdit's field is in another subrecord (the bookkeeping parts are "not data")
                p.Reason = "inside Mutagen's asset link";
                p.Detail = $"{where} is a file path in {lf.Subrecord ?? "no subrecord"}, and \"{seg}\" is the path itself, but xEdit's field is in {p.Subrecord}. {today}";
            }
            else if (lf.Enum != null)
            {
                p.Reason = "enum member renamed or removed";
                p.Detail = $"{where} is the enum {lf.Enum}, which has no member \"{seg}\" today. {today}";
            }
            else
            {
                p.Reason = "part of a plain value";
                p.Detail = $"{where} is one {lf.Def.Kind} value today; the pair goes on with \"{seg}\". {today}";
            }
        }
        else if (!seg.Contains('.') && p.MutagenPath.EndsWith(seg, StringComparison.Ordinal) && objs.FirstOrDefault(o => Reflect.HasProperty(o, seg)) is { } owner)
        {
            // A real property of the compiled object that the XML doesn't declare (GAPS Part 2 row 8)
            p.Reason = "property not in Mutagen's XML";
            p.Detail = $"{owner} has a property \"{seg}\" in Mutagen's compiled code (written by hand or added by the generator), not declared in the XML, so the schema has no field for it (looked under {where}). {today}";
        }
        else
        {
            p.Reason = UnknownName(seg) ? "unknown field since named or split" : "field renamed or removed";
            p.Detail = $"{who} \"{seg}\" today (looked under {where}). {today}";
        }
        p.Detail = p.Detail.TrimEnd();
    }
}
