// Joins the xEdit name pairs to the schema: every pair is matched to a field (or a part of one), or gets a reason.
using System.Text.RegularExpressions;

namespace SFSearch.SchemaGen;

public sealed class PairStats
{
    public int Rows, DistinctPaths, Matched, MatchedDistinct, Exact, Flag, Component, Bytes, Count, BySubrecord, WholeValue;
    public int SubrecordCompared, SubrecordAgree, SignaturesFilled;
    public int FieldsNamed, EnumMembersNamed;
    /// <summary>Sets of flags named after the xEdit element their flags sit in (FieldsNamed counts them too).</summary>
    public int FlagFieldsNamed;
    /// <summary>Objects and lists of record types named after the xEdit element their paired fields sit in (GAPS S98), and
    /// how many container fields (once each, in ObjectFields) took a name that way.</summary>
    public List<PairMatcher.ContainerName> Containers = new();
    public int ContainerFieldsNamed;
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
        // A plugin header's master (MasterReference.Master, compiled type ModKey): its file name is the whole MAST
        ["ModKey"] = new[] { "FileName.String", "FileName" },
    };

    /// <summary>
    /// The xEdit path of the field a flag pair's flag belongs to: the pair's xEdit path without its last part
    /// ("\Record Header\Record Flags\Small" gives "\Record Header\Record Flags"). Null for a pair that isn't a flag's,
    /// or one held up by position alone.
    /// </summary>
    public static string? FlagParent(PairRow p)
    {
        if (p.Match != "flag" || p.Part == null || Weight(p) < 1000) return null;
        int cut = p.XEditPath.LastIndexOf('\\');
        return cut > 0 ? p.XEditPath[..cut] : null;
    }

    /// <summary>The field a flag pair names one flag of ("Flags.Light" gives "Flags").</summary>
    public static string FlagField(PairRow p) => p.MutagenPath[..^(p.Part!.Length + 1)];

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

    static readonly Regex NumberedNonName = new(@"^(unknown|unused)\s*#?\s*(\d+|\*)$|^unused$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // A subrecord's signature ("CTDA", "NAM4"), or a list of them as Mutagen names it ("QDUPs", "XCLAs")
    static readonly Regex SignatureName = new(@"^[A-Z][A-Z0-9_]{3}s?$", RegexOptions.Compiled);

    static string WithoutSignature(string name)
    {
        name = name.Trim();
        return SigPrefix.Match(name) is { Success: true } m ? m.Groups[1].Value.Trim() : name;
    }

    /// <summary>
    /// xEdit's name for a field is "Unknown": in any case, with or without the element's signature before it
    /// ("RECF - Unknown"). Such a name says xEdit doesn't know the field; it is no name to show.
    /// </summary>
    public static bool IsUnknownName(string xeditName)
        => WithoutSignature(xeditName).Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How much a name says about its field: 0 for "Unknown" (IsUnknownName), 1 for another non-name (a numbered
    /// "Unknown #2", "Unknown #*", "Unknown2"; "Unused" and "Unused 2"; only a subrecord's signature, "CTDA", or a list of
    /// them, "QDUPs"), 2 for a real name ("First Parameter", "Unknown Strings", "Sprint"). A list item's placeholder
    /// ("Keyword #*") is a real name: it names the item.
    /// </summary>
    public static int NameStrength(string name)
    {
        string n = WithoutSignature(name);
        if (n.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return 0;
        return NumberedNonName.IsMatch(n) || SignatureName.IsMatch(n) ? 1 : 2;
    }

    /// <summary>A name that says nothing about its field: "Unknown", numbered "Unknown #n", "Unused", or only a signature.</summary>
    public static bool IsNonName(string name) => NameStrength(name) < 2;

    /// <summary>
    /// The readable name a field takes from xEdit's name: xEdit's, except where xEdit's is a non-name (IsNonName) and
    /// SFSearch's own, the Mutagen name spaced out, says more (NameStrength): there SFSearch keeps its own, and xEdit's
    /// stays in the field's xEdit name. Where the own name is no better (Mutagen's "Unknown" beside xEdit's "Unused",
    /// "NAM4" beside "Unknown #*"), xEdit's is kept. The user's decisions, 2026-11-15: "Keep SFSearch's name where
    /// xEdit's is Unknown", then "Yes, as recommended" (the same for every non-name).
    /// </summary>
    public static string ReadableName(string xeditName, string mutagenName)
        => NameStrength(mutagenName) > NameStrength(xeditName) ? SpacedOut(mutagenName) : xeditName;

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
                // A field Mutagen's hand-written code fills: Mutagen's files name its subrecords on that step or in HandReadLayouts.xml (GAPS S38), not on the field
                string said = same.CustomSubrecords == null ? "signature from xEdit's pairs (the XML doesn't give one)"
                    : same.CustomSubrecordList.Contains(sig) ? $"signature from xEdit's pairs (Mutagen's files name it for the hand-written code that fills the field, {same.FilledBy})"
                    : $"signature from xEdit's pairs (Mutagen's files name {string.Join(", ", same.CustomSubrecordList)} for the hand-written code that fills the field, {same.FilledBy})";
                same.Note = (same.Note == null ? "" : same.Note + "; ") + said;
            }
            filled++;
        }
        return filled;
    }

    public static PairStats Match(SchemaModel model, List<PairRow> pairs)
    {
        var st = new PairStats { Rows = pairs.Count, DistinctPaths = pairs.Select(p => (p.Sig, p.MutagenPath)).Distinct().Count() };
        st.SignaturesFilled = FillSignatures(model, pairs);
        var resolver = new Resolver(model);
        var names = new Dictionary<FieldDef, Dictionary<string, int>>();
        var flagNames = new Dictionary<FieldDef, Dictionary<string, int>>();
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

        // A set of flags no pair names as a whole takes the name of the xEdit element its flags sit in, when every flag
        // pair of the field (in one record type) sits in the same one: "\Record Header\Record Flags\Small" and
        // "\Record Header\Record Flags\Master" name TES4's Flags "Record Flags" (GAPS S90)
        foreach (var g in pairs.Where(p => FlagParent(p) != null && p.Type != null).GroupBy(p => (p.Sig, Field: FlagField(p))))
        {
            var parents = g.Select(p => FlagParent(p)!).Distinct(StringComparer.Ordinal).ToList();
            if (parents.Count != 1) continue;
            var defs = Resolver.Pick(g.First(), model.Resolve(g.First().Type!, g.Key.Field).Found).Where(f => f.Match == "exact").Select(f => f.Row.Def).Distinct().ToList();
            foreach (var def in defs.Where(d => !names.ContainsKey(d)))
            {
                if (!flagNames.TryGetValue(def, out var t)) flagNames[def] = t = new(StringComparer.Ordinal);
                t[LastName(parents[0])] = t.GetValueOrDefault(LastName(parents[0])) + g.Count();
            }
        }
        foreach (var (def, tally) in flagNames) names[def] = tally.ToDictionary(t => t.Key, t => t.Value * 1000, StringComparer.Ordinal);
        st.FlagFieldsNamed = flagNames.Count;

        foreach (var (def, tally) in names)
        {
            var bestName = tally.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).First().Key;
            def.XEdit = bestName; def.Readable = ReadableName(bestName, def.Name); def.XEditPairs = tally.Values.Sum(v => v / 1000 + v % 1000);
            st.FieldsNamed++;
        }
        // The record header's flags every record type shares (StarfieldMajorRecordFlags): xEdit names a bit after what it
        // means in each type (bit 2 is REFR's "Minimal Use Door", STAT's "Heading Marker"), so a member whose pairs give
        // more than one name takes none (GAPS S97)
        var headerFlagEnums = pairs.Where(p => p.Match == "flag" && p.Object != null && p.XEditPath.StartsWith(@"\Record Header\Record Flags\", StringComparison.Ordinal))
            .Select(p => model.Objects[p.Object!].Fields.FirstOrDefault(f => f.Ord == p.Ord)?.Enum).Where(e => e != null).ToHashSet(StringComparer.Ordinal);
        foreach (var ((en, member), tally) in memberNames)
        {
            if (headerFlagEnums.Contains(en) && tally.Count > 1) continue;
            var m = model.Enums[en].First(x => x.Name == member);
            m.XEditName = tally.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).First().Key;
            st.EnumMembersNamed++;
        }

        // Objects and lists (GAPS S98): the xEdit element their paired fields sit in. A field by itself has no name, so
        // each record type's container takes its own; an object's container field then takes the name most types give.
        st.Containers = ContainerNames(model, pairs);
        var containerTally = new Dictionary<FieldDef, Dictionary<string, int>>();
        foreach (var c in st.Containers)
        {
            if (c.Def.XEdit != null) continue;
            if (!containerTally.TryGetValue(c.Def, out var t)) containerTally[c.Def] = t = new(StringComparer.Ordinal);
            t[LastName(c.XEditPath)] = t.GetValueOrDefault(LastName(c.XEditPath)) + 1;
        }
        foreach (var (def, tally) in containerTally)
        {
            var bestName = tally.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).First().Key;
            def.XEdit = bestName; def.Readable = ReadableName(bestName, def.Name);
            st.ContainerFieldsNamed++;
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

    /// <summary>A record type's object or list, with the xEdit element its paired fields sit in (GAPS S98).</summary>
    public sealed record ContainerName(string Sig, string Path, string XEditPath, FieldDef Def);

    /// <summary>The kinds of field that hold other fields: an object, a list (and the other collections), a male/female pair.</summary>
    public static bool IsContainer(FieldRow r) => r.Def.RefObject != null || r.Def.Kind is "Object" or "List" or "Array" or "Array2D" or "Dictionary" or "Gendered";

    static readonly Regex PathToken = new(@"\[\*\]|[^.\[\]]+", RegexOptions.Compiled);

    /// <summary>The xEdit path's parts, without the empty first one.</summary>
    static string[] XParts(string xeditPath) => xeditPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    static string XJoin(IEnumerable<string> parts) => "\\" + string.Join('\\', parts);

    /// <summary>
    /// xEdit's name for each object and list of each record type (GAPS S98): the xEdit element its paired fields share.
    /// Every matched pair gives its field's own xEdit element (the pair's path; a component's or a flag's parent). A
    /// field <c>n</c> levels below a container (a list's <c>[*]</c> counts as a level) gives the container its element
    /// without the last <c>n</c> parts; the container takes the longest start all its fields' elements share (the item
    /// element of a list of components of many kinds is <c>\Base Form Components\Component</c>, where each kind's data
    /// sits one level deeper). A list is the element its items sit in: <c>Keywords[*]</c> at
    /// <c>\Keywords\KWDA - Keywords\Keyword</c> makes <c>Keywords</c> <c>\Keywords\KWDA - Keywords</c>. A container whose
    /// element is the same as the one it sits in gets none (the two sides nest differently there), nor does one no pair
    /// reaches. A pair that names the container itself keeps its own name (it isn't listed here).
    /// </summary>
    public static List<ContainerName> ContainerNames(SchemaModel model, List<PairRow> pairs)
    {
        // The element of each paired field, per record type: (type, field path) -> xEdit elements
        var fieldEls = new Dictionary<(string Sig, string Type), Dictionary<string, List<string>>>();
        var named = new HashSet<(string, string)>();
        // The pairs checked on the most records first: where two kinds of a list's items give a container as many fields (a
        // quest's reference and location aliases' Conditions), the element of the one on more records wins (J108)
        foreach (var p in pairs.OrderByDescending(p => p.Checked))
        {
            if (p.Type == null || p.Match == null) continue;
            (string? field, string? el) = p.Match switch
            {
                "exact" => (p.MutagenPath, p.XEditPath),
                "whole value" => (p.MutagenPath[..^(p.Part!.Length + (p.Part.StartsWith('#') ? 0 : 1))], p.XEditPath),
                "component" => (p.MutagenPath[..^(p.Part!.Length + 1)], XJoin(XParts(p.XEditPath)[..^1])),
                "flag" => (FlagField(p), XJoin(XParts(p.XEditPath)[..^1])),
                _ => (null, null),
            };
            if (field == null || el == null || XParts(el).Length == 0) continue;
            if (p.Match is "exact" or "whole value") named.Add((p.Sig, field));
            var key = (p.Sig, p.Type);
            if (!fieldEls.TryGetValue(key, out var byField)) fieldEls[key] = byField = new(StringComparer.Ordinal);
            (byField.TryGetValue(field, out var l) ? l : byField[field] = new()).Add(el);
        }

        // The deepest element three quarters of a container's fields sit in. A few fields nest differently on the two
        // sides (a condition's CIS2 has no xEdit element for Mutagen's Data; a component's data sits in a "Component Data -
        // <kind>" element of its own kind), and would otherwise cut every container above them short or lead it into one kind
        static string[] Choose(List<(string Field, string[] El)> list)
        {
            int fields = list.Select(x => x.Field).Distinct(StringComparer.Ordinal).Count();
            string[] best = Array.Empty<string>();
            for (int len = 1; ; len++)
            {
                var top = list.Where(x => x.El.Length >= len).GroupBy(x => string.Join('|', x.El.Take(len)), StringComparer.Ordinal)
                    .Select(g => (El: g.First().El[..len], Fields: g.Select(x => x.Field).Distinct(StringComparer.Ordinal).Count()))
                    .OrderByDescending(g => g.Fields).FirstOrDefault();
                if (top.El == null || top.Fields * 4 < fields * 3) return best;
                best = top.El;
            }
        }

        // Each container's candidates, per record type: its fields' elements, cut by how deep each field sits below it
        var perType = new List<(string Sig, string Type, Dictionary<string, FieldRow> Rows, Dictionary<string, List<(string Field, string[] El)>> Cands, Dictionary<string, List<string>> ByField)>();
        // A container that holds one of several kinds of object (a component, a condition): its candidates from every
        // record type, by path and base object. A type with one kind only (COBJ's object window filter) would name the
        // item after that kind's element (\Base Form Components\Component\Component Data - Object Window Filter)
        var kinds = new Dictionary<(string Path, string Base), List<(string Field, string[] El)>>();
        foreach (var ((sig, type), byField) in fieldEls.OrderBy(k => k.Key.Sig, StringComparer.Ordinal).ThenBy(k => k.Key.Type, StringComparer.Ordinal))
        {
            var rows = new Dictionary<string, FieldRow>(StringComparer.Ordinal);
            foreach (var r in model.Expand(type)) rows.TryAdd(r.PlainPath, r);
            var cands = new Dictionary<string, List<(string Field, string[] El)>>(StringComparer.Ordinal);
            foreach (var (field, els) in byField)
            {
                var tokens = PathToken.Matches(field).Select(m => m.Value).ToList();
                for (int i = 0; i < tokens.Count - 1; i++)
                {
                    string c = string.Concat(tokens.Take(i + 1).Select((t, k) => k == 0 || t == "[*]" ? t : "." + t));
                    int depth = tokens.Count - 1 - i;
                    foreach (var el in els)
                    {
                        var parts = XParts(el);
                        // A field xEdit keeps shallower than Mutagen gives no element, but still counts among the fields
                        var cut = parts.Length - depth < 1 ? Array.Empty<string>() : parts[..^depth];
                        (cands.TryGetValue(c, out var l) ? l : cands[c] = new()).Add((field, cut));
                        if (rows.TryGetValue(c, out var cr) && cr.Def.RefObject is { } baseObj && model.Variants(baseObj).Count > 0)
                            (kinds.TryGetValue((c, baseObj), out var kl) ? kl : kinds[(c, baseObj)] = new()).Add((sig + "|" + field, cut));
                    }
                }
            }
            perType.Add((sig, type, rows, cands, byField));
        }

        var res = new List<ContainerName>();
        foreach (var (sig, type, rows, cands, byField) in perType)
        {
            var found = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var (c, list) in cands)
            {
                var e = Choose(list);
                // Several kinds of object: what every record type's kinds share, where this type's element goes on from it
                if (rows.TryGetValue(c, out var cr) && cr.Def.RefObject is { } baseObj && kinds.TryGetValue((c, baseObj), out var all)
                    && Choose(all) is { Length: > 0 } shared && shared.Length < e.Length && e.Take(shared.Length).SequenceEqual(shared))
                    e = shared;
                if (e.Length > 0) found[c] = e;
            }
            // A list is the element its items sit in: its item's element (a container's, or a paired item's own) without the last part
            foreach (var c in cands.Keys.Where(c => !c.EndsWith("[*]", StringComparison.Ordinal)).ToList())
            {
                string item = c + "[*]";
                string[]? itemEl = found.TryGetValue(item, out var ie) ? ie
                    : byField.TryGetValue(item, out var own) && Choose(own.Select(x => (item, XParts(x))).ToList()) is { Length: > 0 } oe ? oe : null;
                if (itemEl is { Length: > 1 }) found[c] = itemEl[..^1];
            }
            foreach (var (c, e) in found.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                if (!rows.TryGetValue(c, out var row) || !IsContainer(row) || named.Contains((sig, c))) continue;
                // The container it sits in: a container with the same element nests differently on the two sides
                int cut = Math.Max(c.LastIndexOf('.'), c.EndsWith("[*]", StringComparison.Ordinal) ? c.Length - 3 : -1);
                string? outer = cut > 0 ? c[..cut] : null;
                if (outer != null && found.TryGetValue(outer, out var oe2) && oe2.SequenceEqual(e)) continue;
                // Three quarters of the fields below it must sit in it (a list's element, taken from its items', too):
                // WTHR's Clouds gathers each layer from several xEdit arrays (Cloud Speeds, Cloud Colors, Cloud Alphas)
                if (cands.TryGetValue(c, out var mine))
                {
                    int all = mine.Select(x => x.Field).Distinct(StringComparer.Ordinal).Count();
                    int inside = mine.Where(x => x.El.Length >= e.Length && x.El.Take(e.Length).SequenceEqual(e)).Select(x => x.Field).Distinct(StringComparer.Ordinal).Count();
                    if (inside * 4 < all * 3) continue;
                }
                res.Add(new ContainerName(sig, c, XJoin(e), row.Def));
            }
        }
        return res;
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
            bool part = (ValueParts.TryGetValue(leaf.Def.Kind, out var parts) && parts.Contains(seg, StringComparer.Ordinal))
                || (leaf.Def.ClrType != null && ValueParts.TryGetValue(leaf.Def.ClrType, out var clrParts) && clrParts.Contains(seg, StringComparer.Ordinal));
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
