// Records nested in others (placed references in cells, cells in worldspaces, responses in topics, topics and scenes
// in quests) are split out of their parent's JSON: the parent keeps a {"$Child": "<FormKey>"} marker where the child
// was, and the child is a record of its own. Assemble puts them back.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFSearch.Records;

/// <summary>A record split out of its parent.</summary>
public sealed record SplitChild(string FormKey, string? Type, string ParentFormKey, string Path, JsonObject Json);

public static partial class RecordSplit
{
    public const string ChildKey = "$Child";
    public const string TypeKey = "MutagenObjectType";

    [GeneratedRegex(@"^[0-9A-Fa-f]{6}:[^:\s]+\.(esm|esp|esl)$", RegexOptions.IgnoreCase)]
    private static partial Regex FormKeyRx();

    public static bool IsFormKey(string s) => s.Length >= 11 && s[6] == ':' && FormKeyRx().IsMatch(s);

    /// <summary>The FormKey a marker names, or null when the node isn't a marker.</summary>
    public static string? ChildOf(JsonNode? node) =>
        node is JsonObject o && o.Count == 1 && o[ChildKey] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static bool IsRecord(JsonNode? node, out JsonObject obj, out string formKey)
    {
        obj = null!; formKey = "";
        if (node is not JsonObject o || o["FormKey"] is not JsonValue v || !v.TryGetValue<string>(out var s) || !IsFormKey(s)) return false;
        obj = o; formKey = s;
        return true;
    }

    /// <summary>
    /// Replaces every record nested in <paramref name="record"/> by a marker, deepest first. Each record taken out is
    /// handed to <paramref name="children"/> (when given) as it would be written on its own: with no
    /// <c>MutagenObjectType</c> at its top, since its own type's serializer writes none.
    /// </summary>
    public static void Split(JsonObject record, List<SplitChild>? children)
    {
        string owner = record["FormKey"]?.GetValue<string>() ?? "";
        Walk(record, owner, "", children);
    }

    static void Walk(JsonNode? node, string owner, string path, List<SplitChild>? children)
    {
        if (node is JsonObject o)
        {
            foreach (var (k, v) in o.ToList())
            {
                if (k == "FormKey" || v == null) continue;
                string p = path.Length == 0 ? k : path + "." + k;
                if (IsRecord(v, out var child, out var key)) o[k] = TakeOut(child, key, owner, p, children);
                else Walk(v, owner, p, children);
            }
        }
        else if (node is JsonArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                string p = $"{path}[{i}]";
                if (IsRecord(a[i], out var child, out var key)) a[i] = TakeOut(child, key, owner, p, children);
                else Walk(a[i], owner, p, children);
            }
        }
    }

    static JsonObject TakeOut(JsonObject child, string key, string owner, string path, List<SplitChild>? children)
    {
        Walk(child, key, "", children);
        if (children != null)
        {
            string? type = child[TypeKey]?.GetValue<string>();
            var own = new JsonObject();
            foreach (var (k, v) in child.ToList()) { child.Remove(k); if (k != TypeKey) own[k] = v; }
            children.Add(new SplitChild(key, type, owner, path, own));
        }
        return new JsonObject { [ChildKey] = key };
    }

    /// <summary>
    /// Puts every nested record back in place of its marker. A place that can hold several kinds of record (a cell's
    /// lists of placed references) gets the child's <c>MutagenObjectType</c> written first, as the serializer does.
    /// </summary>
    /// <param name="type">The Mutagen type of <paramref name="record"/>.</param>
    /// <param name="child">Gives a child's own JSON (markers and all) and its Mutagen type.</param>
    public static void Assemble(JsonObject record, string type, RecordSchema schema, Func<string, (JsonObject Json, string Type)> child)
    {
        foreach (var (k, v) in record.ToList())
        {
            if (v == null) continue;
            var prop = schema.Prop(type, k);
            record[k] = Put(v, prop, schema, child, out _);
        }
    }

    static JsonNode Put(JsonNode node, SchemaProp? prop, RecordSchema schema, Func<string, (JsonObject Json, string Type)> child, out bool replaced)
    {
        replaced = false;
        if (ChildOf(node) is string key)
        {
            var (json, childType) = child(key);
            Assemble(json, childType, schema, child);
            // The record lists of a cell name no single type; a list of one type needs no type written
            bool several = prop == null || schema.IsPolymorphic(prop.Item?.RefObject ?? prop.Main.RefObject);
            replaced = true;
            if (!several) return json;
            var typed = new JsonObject { [TypeKey] = childType };
            foreach (var (k, v) in json.ToList()) { json.Remove(k); typed[k] = v; }
            return typed;
        }
        if (node is JsonArray a)
        {
            for (int i = 0; i < a.Count; i++)
                if (a[i] is { } item)
                {
                    var n = Put(item, prop, schema, child, out bool r);
                    if (r) a[i] = n;
                }
        }
        else if (node is JsonObject o)
        {
            // Blocks and sub-blocks of a worldspace: plain objects on the way down to the cells
            string? inner = schema.ObjectFor(prop?.Item?.RefObject ?? prop?.Main.RefObject, o[TypeKey]?.GetValue<string>());
            foreach (var (k, v) in o.ToList())
                if (v != null)
                {
                    var n = Put(v, inner == null ? null : schema.Prop(inner, k), schema, child, out bool r);
                    if (r) o[k] = n;
                }
        }
        return node;
    }

    /// <summary>The same record with every marker (and the lists that held only markers) taken away: what is left is
    /// the record's own fields, which is what the file's bytes for that record hold.</summary>
    public static JsonObject WithoutChildren(JsonObject record)
    {
        var copy = (JsonObject)record.DeepClone();
        Strip(copy);
        return copy;
    }

    static bool Strip(JsonNode? node)
    {
        if (ChildOf(node) != null) return true;
        if (node is JsonObject o)
        {
            foreach (var (k, v) in o.ToList()) if (Strip(v)) o.Remove(k);
        }
        else if (node is JsonArray a)
        {
            for (int i = a.Count - 1; i >= 0; i--) if (Strip(a[i])) a.RemoveAt(i);
        }
        return false;
    }
}
