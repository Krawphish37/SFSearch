// Every file path of a record, found by walking its JSON beside the schema.
//  - A field the schema types as a file path (Mutagen's AssetLink: a model's File, a landscape texture's material,
//    a perk's icon, a race's rig) is a path whatever its text is.
//  - Mutagen types most other fields that hold a path as plain text (an ammunition's casing model, a projectile's
//    muzzle flash model, a water material). Those are found by their value: a text in a field the schema calls a
//    string (or doesn't know) that ends in one of the file extensions asked for.
// No field is named here: which fields are paths comes from the schema, the rest from what the records hold.
using System.Text.Json.Nodes;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Reports;

/// <summary>One file path of a record.</summary>
/// <param name="Path">Where it sits: Model.File, WorldModel.Male.File, Components[2].RigFile.</param>
/// <param name="Value">The path as the record writes it.</param>
/// <param name="Typed">True when the schema types the field as a file path; false when it was found by its value.</param>
public readonly record struct PathFound(string Path, string Value, bool Typed);

public sealed class PathWalker
{
    readonly RecordSchema _schema;
    readonly HashSet<string> _extensions;

    /// <param name="extensions">File extensions (".nif") a text must end in to count as a path when the schema doesn't type its field as one.</param>
    public PathWalker(RecordSchema schema, IEnumerable<string> extensions)
    {
        _schema = schema;
        _extensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        TypedFields = schema.Model.Objects.Values.SelectMany(o => o.Fields).Where(f => f.Kind == "AssetLink")
            .OrderBy(f => f.Object, StringComparer.Ordinal).ThenBy(f => f.Ord).ToList();
    }

    /// <summary>The fields the schema types as file paths, each in the object that defines it.</summary>
    public IReadOnlyList<FieldDef> TypedFields { get; }

    /// <summary>The record types in which a typed path field can be reached, with how many places each has.</summary>
    public Dictionary<string, int> TypedPlaces()
    {
        var places = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in _schema.Model.RecordTypes)
        {
            int n = _schema.Model.Expand(t.Name).Count(r => r.Def.Kind == "AssetLink");
            if (n > 0) places[t.Name] = n;
        }
        return places;
    }

    public static string ExtensionOf(string value)
    {
        string v = value.TrimEnd();
        int dot = v.LastIndexOf('.');
        if (dot < 0 || v.Length - dot > 12 || v.IndexOfAny(new[] { '\\', '/', ' ' }, dot) >= 0) return "";
        return v[dot..].ToLowerInvariant();
    }

    bool ByValue(string s) => s.Length > 4 && _extensions.Contains(ExtensionOf(s));

    /// <summary>Calls <paramref name="found"/> for every file path of a record. Records nested in it are records of their own and are not walked.</summary>
    public void Walk(string type, JsonObject record, Action<PathFound> found)
    {
        foreach (var (name, value) in record)
        {
            if (name is "FormKey" or "EditorID" or RecordSplit.TypeKey || value == null) continue;
            var prop = _schema.Prop(type, name);
            Node(prop?.Main, prop, value, name, found, prop == null);
        }
    }

    void Object(string? obj, JsonObject o, string path, Action<PathFound> found)
    {
        foreach (var (name, value) in o)
        {
            if (name == RecordSplit.TypeKey || value == null) continue;
            var prop = obj == null ? null : _schema.Prop(obj, name);
            Node(prop?.Main, prop, value, path + "." + name, found, prop == null);
        }
    }

    void Node(FieldDef? def, SchemaProp? prop, JsonNode value, string path, Action<PathFound> found, bool unknown)
    {
        switch (value)
        {
            case JsonValue v:
                if (!v.TryGetValue<string>(out var s) || s.Length == 0) return;
                if (def is { Kind: "AssetLink" }) found(new PathFound(path, s, true));
                else if ((unknown || def == null || def.Kind == "String") && ByValue(s)) found(new PathFound(path, s, false));
                return;
            case JsonObject o:
                if (RecordSplit.ChildOf(o) != null) return;
                if (o.ContainsKey("TargetLanguage")) return; // a translated text
                if (def is { Kind: "Gendered" } && prop != null)
                {
                    foreach (var (k, x) in o)
                    {
                        if (x == null) continue;
                        prop.Parts.TryGetValue(k, out var part);
                        var partProp = part == null ? null : new SchemaProp { Name = k, Main = part, Item = prop.PartItems.GetValueOrDefault(k) };
                        Node(part, partProp, x, path + "." + k, found, part == null);
                    }
                    return;
                }
                string? variant = o[RecordSplit.TypeKey] is JsonValue t && t.TryGetValue<string>(out var name) ? name : null;
                string? obj = unknown ? null : _schema.ObjectFor(def?.RefObject, variant);
                if (obj == null) { Generic(o, path, found); return; }
                Object(obj, o, path, found);
                return;
            case JsonArray a:
                var item = prop?.Item;
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is not { } x) continue;
                    string at = $"{path}[{i}]";
                    if (item == null) Generic(x, at, found);
                    else Node(item, null, x, at, found, false);
                }
                return;
        }
    }

    /// <summary>A value the schema doesn't spell out (a dictionary, a grid, a field it doesn't know): every text in it that ends in a wanted extension.</summary>
    void Generic(JsonNode node, string path, Action<PathFound> found)
    {
        switch (node)
        {
            case JsonValue v:
                if (v.TryGetValue<string>(out var s) && ByValue(s)) found(new PathFound(path, s, false));
                return;
            case JsonObject o:
                if (RecordSplit.ChildOf(o) != null || o.ContainsKey("TargetLanguage")) return;
                foreach (var (k, x) in o) if (x != null && k != RecordSplit.TypeKey) Generic(x, path + "." + k, found);
                return;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++) if (a[i] is { } x) Generic(x, $"{path}[{i}]", found);
                return;
        }
    }

    /// <summary>A path with its list positions taken out: Components[2].RigFile → Components[*].RigFile.</summary>
    public static string Plain(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+\]", "[*]");
}
