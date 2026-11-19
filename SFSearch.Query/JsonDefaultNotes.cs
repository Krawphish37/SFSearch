// A record's JSON leaves out a field that holds its default (an operator that is EqualTo, a multiplier that is 0), so
// SQL that reads such a field out of the JSON gets NULL where a column of the same name gives the default. An agent
// that doesn't know reads the NULL as "nothing there". This finds the JSON paths a query reads that end in such a
// field and words the note that says so, with each field's default. (The user, 2026-11-19, from an agent's first use:
// "The new SFSearch leaves out the default operator ("equal to") in its JSON. Re-running with that fixed.")
//
// A path is followed through the schema, not matched by its last name: "$.Name" of a weapon is a text that may be
// missing, while a script entry's Name is always there. A path read from a record's own Json starts at the record
// types the SQL names and is named when it ends in such a field in any of them. A path read from anything else (an
// item of json_each, a list view's Value) may start at any object that has its first field, and is named only when
// every place it can end is such a field.
//
// A path read from a record's own Json that the schema doesn't have at all (GAPS S211: an old path such as
// "$.Model.File.GivenPath", a guessed field) is NULL for every record, and looked like "no record has it". Where the
// path stops in every record type the SQL names, the note says where: the object that has no such field (with the
// nearest names), or the field that is one value with nothing below it. A shape this doesn't follow (a translated
// text's Value and Tables, a dictionary, the two halves of a gendered value) is left alone.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

static class JsonDefaultNotes
{
    const int Most = 6;

    // The kinds whose default is one plain value the serializer leaves out. A list, an object, bytes and a translated
    // text are left out by other rules (empty, every field at its default, no text) and aren't a value to read.
    static readonly HashSet<string> Plain = new(StringComparer.Ordinal)
    {
        "Enum", "Bool", "Int8", "Int16", "Int32", "Int64", "UInt8", "UInt16", "UInt32", "UInt64", "Float", "Double", "Percent", "Half",
        "String", "FormLink", "P2Float", "P2Int16", "P2Int32", "P3Float", "P3Int16", "P3Int32", "Guid",
    };

    sealed class Lookups
    {
        /// <summary>Field name → the objects that have a field of that name.</summary>
        public readonly Dictionary<string, List<string>> Owners = new(StringComparer.Ordinal);
        /// <summary>Record type names (the tables), by name without case.</summary>
        public readonly Dictionary<string, string> RecordTypes = new(StringComparer.OrdinalIgnoreCase);
    }
    static readonly ConditionalWeakTable<RecordSchema, Lookups> Cache = new();

    static Lookups For(RecordSchema schema) => Cache.GetValue(schema, s =>
    {
        var l = new Lookups();
        foreach (string obj in s.Model.Objects.Keys.OrderBy(o => o, StringComparer.Ordinal))
            foreach (var p in s.Props(obj))
                (l.Owners.TryGetValue(p.Name, out var list) ? list : l.Owners[p.Name] = new List<string>()).Add(obj);
        foreach (var t in s.Model.RecordTypes) l.RecordTypes[t.Name] = t.Name;
        return l;
    });

    /// <summary>The default as the JSON would write it, when the field is always there and of a plain kind; else null.</summary>
    static string? LeftOutDefault(RecordSchema schema, FieldDef f)
    {
        if (f.Nullable || f.Translated || !Plain.Contains(f.Kind)) return null;
        var (json, _) = FieldLookup.DefaultOf(schema, f);
        if (json is JsonArray { Count: 0 }) return "no flag set";
        if (json is not JsonValue v) return null;
        if (!v.TryGetValue<string>(out var str)) return json.ToJsonString();
        return str.Length == 0 ? "an empty text" : str == "Null" && f.Kind == "FormLink" ? "no link" : str;
    }

    /// <summary>
    /// The note for a query's tokens, or null: the fields its JSON paths end in that the JSON leaves out at their
    /// default. A path inside coalesce() or ifnull() has its default given already and isn't named; json_each's and
    /// json_tree's path names a list, not a value.
    /// </summary>
    public static List<string> For(RecordSchema schema, List<Tok> toks)
    {
        var notes = new List<string>();
        var nowhere = new List<string>();
        var look = For(schema);
        List<string>? named = null; // the record types the SQL names, found on the first path that needs them
        var found = new List<(string Name, List<string> Defaults)>();
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind != TokKind.String || !(t.Text.StartsWith("$.", StringComparison.Ordinal) || t.Text.StartsWith("$[", StringComparison.Ordinal))) continue;
            var path = Fields(t.Text);
            if (path == null || path.Count == 0 || found.Any(f => f.Name == path[^1])) continue;
            if (Enclosed(toks, i)) continue;

            bool ofRecord = FromRecordJson(toks, i);
            named ??= toks.Where(k => k.IsName && look.RecordTypes.ContainsKey(k.Text)).Select(k => look.RecordTypes[k.Text]).Distinct(StringComparer.Ordinal).ToList();
            var ends = new List<FieldDef>();
            var seen = new HashSet<(string, int)>();
            bool strict = !ofRecord || named.Count == 0;
            IEnumerable<string> roots = strict ? look.Owners.GetValueOrDefault(path[0]) ?? new List<string>() : named;
            foreach (string root in roots) Walk(schema, root, path, 0, ends, seen);
            if (ends.Count == 0)
            {
                // In no record type the SQL names: where it stops, when it stops for a reason this can say in every one
                if (!strict && !path.Contains("MutagenObjectType") && nowhere.Count < 4 && !nowhere.Any(n => n.StartsWith("'" + t.Text + "'", StringComparison.Ordinal)))
                {
                    var stops = named.Select(root => Follow(schema, root, path, 0, new HashSet<(string, int)>())).ToList();
                    if (stops.All(st => !st.Ok && st.Reason != null)) nowhere.Add($"'{t.Text}': {stops[0].Reason}");
                }
                continue;
            }
            var defaults = ends.Select(e => LeftOutDefault(schema, e)).ToList();
            if (strict ? defaults.Any(d => d == null) : defaults.All(d => d == null)) continue;
            found.Add((path[^1], defaults.Where(d => d != null).Select(d => d!).Distinct(StringComparer.Ordinal).ToList()));
        }
        if (nowhere.Count > 0)
            notes.Add($"A JSON path that names no field gives NULL for every record: {string.Join("; ", nowhere)}. describe <Type> lists a type's fields with their paths.");
        if (found.Count == 0) return notes;
        static string One((string Name, List<string> Defaults) f) =>
            $"{f.Name} (default {(f.Defaults.Count == 1 ? f.Defaults[0] : string.Join(" or ", f.Defaults.Take(3)) + (f.Defaults.Count > 3 ? ", …" : "") + ", by type")})";
        string list = string.Join(", ", found.Take(Most).Select(One)) + (found.Count > Most ? $" and {found.Count - Most} more" : "");
        notes.Add($"A record's JSON leaves out a field that holds its default, so reading it from the JSON gives NULL there, not the default: {list}. "
             + "A NULL is the default, not \"nothing there\": write coalesce(<the read>, <the default>), or read the type's column of that name (or the Conditions table), which gives the default.");
        return notes;
    }

    // Kinds that are one value in the JSON: nothing is below them
    static readonly HashSet<string> OneValue = new(Plain, StringComparer.Ordinal) { "AssetLink", "FormKey", "RecordType", "TimeOnly" };

    /// <summary>
    /// Follows a path from an object: Ok when some kind of the object reaches its end, or when it meets a shape this
    /// doesn't follow (then nothing is said). Else the deepest place it stopped, in words.
    /// </summary>
    static (bool Ok, int Depth, string? Reason) Follow(RecordSchema schema, string obj, List<string> path, int at, HashSet<(string, int)> seen)
    {
        if (!seen.Add((obj, at)) || seen.Count > 4000) return (true, at, null);
        (int Depth, string? Reason) best = (-1, null);
        bool any = false;
        foreach (string kind in new[] { obj }.Concat(schema.Model.Variants(obj)))
        {
            if (!schema.Model.Objects.ContainsKey(kind)) continue;
            var props = schema.Props(kind);
            if (props.Count == 0) return (true, at, null);
            any = true;
            if (schema.Prop(kind, path[at]) is not { } p)
            {
                if (at > best.Depth)
                {
                    var near = Nearest.Pick(path[at], props.Select(x => x.Name), n => new[] { n }, 3, always: false);
                    best = (at, $"{kind} has no field {path[at]}{(near.Count > 0 ? $" (nearest: {string.Join(", ", near)})" : "")}");
                }
                continue;
            }
            if (at == path.Count - 1) return (true, at, null);
            var f = p.Main;
            if (f.Translated || p.Parts.Count > 0 || p.PartItems.Count > 0 || f.Kind is "Dictionary" or "Gendered" or "CustomLogic" or "Color" or "Array2D") return (true, at, null);
            var below = new[] { p.Item?.RefObject, f.RefObject }.Where(x => x != null).Select(x => x!).Distinct(StringComparer.Ordinal).ToList();
            if (below.Count == 0)
            {
                if (p.Item != null || !OneValue.Contains(f.Kind)) return (true, at, null);
                if (at + 1 > best.Depth) best = (at + 1, $"{path[at]} is one value{(f.Kind == "AssetLink" ? " (a file's path)" : f.Kind == "FormLink" ? " (a link)" : "")}, so nothing is below it ({path[at + 1]} isn't there)");
                continue;
            }
            foreach (string next in below)
            {
                var r = Follow(schema, next, path, at + 1, seen);
                if (r.Ok) return r;
                if (r.Depth > best.Depth) best = (r.Depth, r.Reason);
            }
        }
        return any ? (false, best.Depth, best.Reason) : (true, at, null);
    }

    /// <summary>Every field the path can end in, starting at an object (and, where the object is one of several kinds, at each kind).</summary>
    static void Walk(RecordSchema schema, string obj, List<string> path, int at, List<FieldDef> ends, HashSet<(string, int)> seen)
    {
        if (!seen.Add((obj, at)) || seen.Count > 4000) return;
        foreach (string kind in new[] { obj }.Concat(schema.Model.Variants(obj)))
        {
            if (kind != obj && !seen.Add((kind, at))) continue;
            if (!schema.Model.Objects.ContainsKey(kind) || schema.Prop(kind, path[at]) is not { } p) continue;
            if (at == path.Count - 1) { if (!ends.Contains(p.Main)) ends.Add(p.Main); continue; }
            // Below the field: the object it holds, or the object of one item of its list (a path may leave the [n] out)
            foreach (string? next in new[] { p.Item?.RefObject, p.Main.RefObject })
                if (next != null) Walk(schema, next, path, at + 1, ends, seen);
        }
    }

    /// <summary>The fields of a JSON path in order, list positions left out: "$.Entries[0].Conditions[0].CompareOperator" → Entries, Conditions, CompareOperator. Null when it isn't a plain path.</summary>
    static List<string>? Fields(string path)
    {
        var fields = new List<string>();
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '[')
            {
                int close = path.IndexOf(']', i);
                if (close < 0) return null;
                i = close + 1;
                continue;
            }
            if (path[i] != '.') return null;
            int start = ++i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
            string name = path[start..i].Trim('"');
            if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_')) return null;
            fields.Add(name);
        }
        return fields;
    }

    /// <summary>True when the path at <paramref name="i"/> is read from a record's own Json column: <c>Json -> '$…'</c>, <c>json_extract(w.Json, '$…')</c>.</summary>
    static bool FromRecordJson(List<Tok> toks, int i)
    {
        for (int j = i - 1; j >= 0 && j >= i - 4; j--)
        {
            if (toks[j].Kind == TokKind.Punct) continue;
            return toks[j].IsName && toks[j].Text.Equals("Json", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>True when the string at <paramref name="i"/> sits inside coalesce( ) or ifnull( ) (up to three calls out), or is json_each's or json_tree's own path.</summary>
    static bool Enclosed(List<Tok> toks, int i)
    {
        int level = 0, calls = 0;
        for (int j = i - 1; j >= 0 && calls < 3; j--)
        {
            if (toks[j].IsPunct(")")) { level++; continue; }
            if (!toks[j].IsPunct("(")) continue;
            if (level > 0) { level--; continue; }
            calls++;
            if (j == 0 || toks[j - 1].Kind != TokKind.Word) continue;
            var fn = toks[j - 1];
            if (fn.Is("coalesce") || fn.Is("ifnull")) return true;
            if (calls == 1 && (fn.Is("json_each") || fn.Is("json_tree"))) return true;
        }
        return false;
    }
}
