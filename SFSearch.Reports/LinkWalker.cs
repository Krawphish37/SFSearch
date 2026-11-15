// Every link of a record, found by walking its JSON beside the schema: where it sits (a path of Mutagen names) and
// which kinds of record the schema allows there. And the rule that says whether a record type is one of those kinds:
// the type itself, a base it derives from, or a link interface it carries (IItem, IKeywordFormNode, …).
using System.Text.Json.Nodes;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Reports;

/// <summary>One link of a record.</summary>
/// <param name="Top">The record's own field the link sits in (Keywords, VirtualMachineAdapter).</param>
/// <param name="Path">Where it sits: Entries[3].Reference, Conditions[0].Data.FirstParameter.</param>
/// <param name="FormKey">The record it names, as the JSON writes it.</param>
/// <param name="LinkTo">What the schema allows there: a record type, a base type or a link interface. Null when the
/// schema doesn't know the field.</param>
public readonly record struct LinkFound(string Top, string Path, string FormKey, string? LinkTo);

public sealed class LinkWalker
{
    readonly RecordSchema _schema;

    public LinkWalker(RecordSchema schema) { _schema = schema; }

    /// <summary>Calls <paramref name="found"/> for every link of a record that isn't null. Records nested in it
    /// (<c>$Child</c> markers) are records of their own and are not walked.</summary>
    public void Walk(string type, JsonObject record, Action<LinkFound> found)
    {
        foreach (var (name, value) in record)
        {
            if (name is "FormKey" or RecordSplit.TypeKey || value == null) continue;
            var prop = _schema.Prop(type, name);
            Node(prop?.Main, prop, value, name, name, found, prop == null);
        }
    }

    void Object(string? obj, JsonObject o, string top, string path, Action<LinkFound> found)
    {
        foreach (var (name, value) in o)
        {
            if (name == RecordSplit.TypeKey || value == null) continue;
            var prop = obj == null ? null : _schema.Prop(obj, name);
            Node(prop?.Main, prop, value, top, path + "." + name, found, prop == null);
        }
    }

    static bool IsLinkKind(FieldDef def) => def.Kind is "FormLink" or "FormLinkOrIndex" or "FormKey" || def.LinkTo != null;

    void Node(FieldDef? def, SchemaProp? prop, JsonNode value, string top, string path, Action<LinkFound> found, bool unknown)
    {
        switch (value)
        {
            case JsonValue v:
                // A text that only looks like a FormKey isn't a link: the schema says which fields are
                if (v.TryGetValue<string>(out var s) && RecordSplit.IsFormKey(s) && (unknown || def == null || IsLinkKind(def)))
                    found(new LinkFound(top, path, s, def?.LinkTo));
                return;
            case JsonObject o:
                if (RecordSplit.ChildOf(o) != null) return;
                if (o.ContainsKey("TargetLanguage")) return; // a translated text
                if (def != null && def.Kind == "FormLinkOrIndex") { Generic(o, top, path, def.LinkTo, found); return; }
                if (def is { Kind: "Gendered" } && prop != null)
                {
                    foreach (var (k, x) in o)
                    {
                        if (x == null) continue;
                        prop.Parts.TryGetValue(k, out var part);
                        var partProp = part == null ? null : new SchemaProp { Name = k, Main = part, Item = prop.PartItems.GetValueOrDefault(k) };
                        Node(part, partProp, x, top, path + "." + k, found, part == null);
                    }
                    return;
                }
                string? variant = o[RecordSplit.TypeKey] is JsonValue t && t.TryGetValue<string>(out var name) ? name : null;
                string? obj = unknown ? null : _schema.ObjectFor(def?.RefObject, variant);
                if (obj == null) { Generic(o, top, path, null, found); return; }
                Object(obj, o, top, path, found);
                return;
            case JsonArray a:
                var item = prop?.Item;
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is not { } x) continue;
                    string at = $"{path}[{i}]";
                    if (item == null) { if (x is JsonValue) Node(null, null, x, top, at, found, true); else Generic(x, top, at, null, found); }
                    else Node(item, null, x, top, at, found, false);
                }
                return;
        }
    }

    /// <summary>A value the schema doesn't spell out (a dictionary, a grid, a field it doesn't know): every text
    /// in it that is a FormKey is a link, with what little the schema says about its kind.</summary>
    static void Generic(JsonNode node, string top, string path, string? linkTo, Action<LinkFound> found)
    {
        switch (node)
        {
            case JsonValue v:
                if (v.TryGetValue<string>(out var s) && RecordSplit.IsFormKey(s)) found(new LinkFound(top, path, s, linkTo));
                return;
            case JsonObject o:
                if (RecordSplit.ChildOf(o) != null || o.ContainsKey("TargetLanguage")) return;
                foreach (var (k, x) in o) if (x != null && k != RecordSplit.TypeKey) Generic(x, top, path + "." + k, linkTo, found);
                return;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++) if (a[i] is { } x) Generic(x, top, $"{path}[{i}]", linkTo, found);
                return;
        }
    }
}

/// <summary>Which record types a link may point at, from the schema: its link targets, interfaces included.</summary>
public sealed class LinkRules
{
    readonly RecordSchema _schema;
    readonly Dictionary<string, HashSet<string>> _byInterface = new(StringComparer.Ordinal);
    readonly Dictionary<(string LinkTo, string Signature), bool?> _cache = new();
    readonly Dictionary<string, string> _described = new(StringComparer.Ordinal);

    public LinkRules(RecordSchema schema)
    {
        _schema = schema;
        foreach (var o in schema.Model.Objects.Values)
            foreach (var i in o.LinkInterfaces)
                (_byInterface.TryGetValue(i, out var set) ? set : _byInterface[i] = new(StringComparer.Ordinal)).Add(o.Name);
    }

    /// <summary>Every link interface the schema knows, with the objects that carry it.</summary>
    public IReadOnlyDictionary<string, HashSet<string>> Interfaces => _byInterface;

    /// <summary>True when a link target of the schema means "any record".</summary>
    public static bool IsAnyRecord(string linkTo) => linkTo is "StarfieldMajorRecord" or "IStarfieldMajorRecord" or "MajorRecord" or "IMajorRecord";

    /// <summary>True when the schema knows what the link target is: a record type, a base, or an interface that at least one type carries.</summary>
    public bool Knows(string linkTo) => IsAnyRecord(linkTo) || _schema.Model.Objects.ContainsKey(linkTo) || _byInterface.ContainsKey(linkTo);

    /// <summary>May a field whose link target is <paramref name="linkTo"/> point at a record of this Mutagen type?</summary>
    public bool AllowsType(string linkTo, string type)
    {
        // "Any record" is any major record: the plugin header (TES4) isn't one
        if (IsAnyRecord(linkTo)) return _schema.Model.Ancestry(type).Any(o => o.Name is "StarfieldMajorRecord" or "MajorRecord");
        _byInterface.TryGetValue(linkTo, out var carriers);
        foreach (var o in _schema.Model.Ancestry(type))
            if (o.Name == linkTo || (carriers != null && carriers.Contains(o.Name))) return true;
        return false;
    }

    /// <summary>
    /// May a field whose link target is <paramref name="linkTo"/> point at a record with this signature? Null when
    /// it can't be judged: the schema doesn't know the target name, or has no record type for the signature. A
    /// signature with several Mutagen types (OMOD, GMST) is allowed when any of them is.
    /// </summary>
    public bool? Allows(string linkTo, string signature)
    {
        if (_cache.TryGetValue((linkTo, signature), out var have)) return have;
        bool? result;
        var types = _schema.TypesOf(signature);
        if (!Knows(linkTo) || types.Count == 0) result = null;
        else result = types.Any(t => AllowsType(linkTo, t));
        return _cache[(linkTo, signature)] = result;
    }

    /// <summary>The link target for people: "Keyword", or "IItem (Armor, Weapon, … 14 types)".</summary>
    public string Describe(string linkTo)
    {
        if (_described.TryGetValue(linkTo, out var have)) return have;
        string text;
        if (IsAnyRecord(linkTo)) text = "any record";
        else if (_byInterface.TryGetValue(linkTo, out var carriers))
        {
            var names = carriers.OrderBy(n => n, StringComparer.Ordinal).ToList();
            text = $"{linkTo} ({string.Join(", ", names.Take(4))}{(names.Count > 4 ? $", … {names.Count} types" : "")})";
        }
        else text = linkTo;
        return _described[linkTo] = text;
    }
}
