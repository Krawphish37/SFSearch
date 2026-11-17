// The value walk's side of Mutagen's hand-read layouts (GAPS S164, S165): what Mutagen reads with hand-written code and
// its package's Layouts/HandReadLayouts.xml lays out (an object template's OBTS with its keywords, includes and
// properties, a volumes component's VLMS, a pattern swap's DAT2, a patrol's topics, VMAD's scripts) is given to the value
// walk from the layout, walked over the record's bytes (HandLayoutWalk.RunBytes), where Mutagen's generated reader meets
// the field: no copy of Mutagen's rules, the layout file is Mutagen's own, held to Mutagen's parse by its
// HandReadLayoutFileTests and here by sfsrecord-tests value-positions (every value at Mutagen's value position).
//
// The generated walk (LinkWalk.cs) reads a record as Mutagen's generated reader does. Where it comes to a field of an
// object that a layout names in that object (HandLayoutWalk.Named: the fields each layout's scope names, in the Case
// the bytes picked), the field's values are the layout's: each object the layout described under it, with the kind its
// Case picked (or the one the schema has there), and each value with its bytes. A field of such an object that no layout
// names is said unread, as is a value Mutagen derives (a Const, a Bit, a View, a stored number it scales) and anything
// below a place a layout didn't follow.
using System.Collections.Concurrent;
using System.Xml.Linq;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

/// <summary>What Mutagen's hand-read layouts lay out, from the layout file alone (no record).</summary>
static class HandLaidOut
{
    static readonly ConcurrentDictionary<HandLayout, HashSet<string>> PathsOf = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Every place a layout names, as a path without kinds with list entries as [*] (<c>ObjectTemplates[*].Includes[*].Mod</c>,
    /// <c>Components[*].Items[*].Row0.X</c>, <c>Patrol.Topics[*].Subtype</c>): each value, each list's entries, each object.
    /// A Define used inside itself (a script struct's members are script properties again) is followed twice.
    /// </summary>
    public static HashSet<string> Paths(SchemaModel m, HandLayout h) => PathsOf.GetOrAdd(h, x =>
    {
        var defines = m.HandLayouts.Where(d => d.Kind == "Define" && d.Name != null).GroupBy(d => d.Name!).ToDictionary(g => g.Key, g => g.Last().Element, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var using_ = new Dictionary<string, int>(StringComparer.Ordinal);
        static string Join(string p, string f) => p.Length == 0 ? f : p + "." + f;
        void Walk(XElement e, string path, int depth)
        {
            if (depth > 60) return;
            foreach (var c in e.Elements())
            {
                string n = c.Name.LocalName;
                string? field = c.Attribute("field")?.Value;
                switch (n)
                {
                    case "Use":
                    {
                        string name = c.Attribute("name")?.Value ?? "";
                        if (!defines.TryGetValue(name, out var d) || using_.GetValueOrDefault(name) >= 2) break;
                        using_[name] = using_.GetValueOrDefault(name) + 1;
                        Walk(d, path, depth + 1);
                        using_[name]--;
                        break;
                    }
                    case "Header" or "Note" or "Generated" or "Peek": break;
                    // A reflection's FormLinks (C86): a list whose entries are the FormIDs found in the bytes before it
                    case "ReflectionFormLinks":
                        if (field != null) found.Add(Join(path, field) + "[*]");
                        break;
                    case "SubRepeat" or "Segments" or "Repeat":
                    {
                        if (field == null || field == ".") { Walk(c, path, depth + 1); break; }
                        string list = Join(path, field) + "[*]";
                        found.Add(list);
                        Walk(c, list, depth + 1);
                        break;
                    }
                    case "Sub" or "Object":
                    {
                        if (field == null || field == ".") { Walk(c, path, depth + 1); break; }
                        string at = Join(path, field);
                        found.Add(at);
                        Walk(c, at, depth + 1);
                        break;
                    }
                    case "Switch" or "TypeSwitch" or "Case" or "Default" or "AnyOrder" or "IfMore" or "NotLast" or "Versioning": Walk(c, path, depth + 1); break;
                    default:
                        // A value: its field (a list's element itself is the list's entry; Subtype.TypeInt is Subtype)
                        if (field == null || field == "." || field == "FormKey" && path.EndsWith("[*]", StringComparison.Ordinal)) break;
                        found.Add(Join(path, field.Split('.')[0]));
                        break;
                }
            }
        }
        Walk(x.Element, "", 0);
        return found;
    });
}

/// <summary>A record's components' kinds by their BFCB texts, read once per record, for a layout's TypeSwitch.</summary>
sealed class ComponentKinds(SchemaModel m, RawRecord raw)
{
    /// <summary>
    /// The class Mutagen made at a path inside a component (<c>Components[2].Items[0].Ender</c>), from Mutagen's own read
    /// of that component alone (<paramref name="component"/>): the object at the path's rest on it, as the schema names
    /// the class. Null when there is none, or Mutagen read no such component.
    /// </summary>
    public static string? MadeBy(SchemaModel m, Func<int, object?> component, string path)
    {
        int close = path.IndexOf(']');
        if (!path.StartsWith("Components[", StringComparison.Ordinal) || close < 0
            || !int.TryParse(path.AsSpan(11, close - 11), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int k)) return null;
        object? o = component(k);
        foreach (System.Text.RegularExpressions.Match step in System.Text.RegularExpressions.Regex.Matches(path[(close + 1)..], @"\.([^.\[]+)|\[(\d+)\]"))
        {
            if (o == null) return null;
            if (step.Groups[1].Success) o = o.GetType().GetProperty(step.Groups[1].Value)?.GetValue(o);
            else if (o is System.Collections.IEnumerable list) o = list.Cast<object?>().ElementAtOrDefault(int.Parse(step.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            else return null;
        }
        if (o == null) return null;
        // Mutagen's class, or its binary overlay, by the name the schema has
        for (var t = o.GetType(); t != null; t = t.BaseType)
        {
            string name = t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal) ? t.Name[..^"BinaryOverlay".Length] : t.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            if (m.Objects.ContainsKey(name)) return name;
        }
        return null;
    }

    List<string?>? _kinds;

    /// <summary>The kind of the record's component <paramref name="k"/> (its own level: BFCB … BFCE), null when there is none.</summary>
    public string? Of(int k)
    {
        if (_kinds == null)
        {
            _kinds = new List<string?>();
            int depth = 0;
            foreach (var s in raw.Subrecords)
            {
                if (s.Signature == "BFCB") { if (depth == 0) _kinds.Add(Selectors.ComponentKind(m, raw.Content(s))); depth++; }
                else if (s.Signature == "BFCE" && depth > 0) depth--;
            }
        }
        return k >= 0 && k < _kinds.Count ? _kinds[k] : null;
    }
}

internal sealed partial class ItemLocator
{
    /// <summary>A value walk's hand-read layouts, walked over the record (GAPS S164); null when none applies.</summary>
    HandLayoutWalk? _hand;
    /// <summary>A value walk's cut lists and objects (the runs give them).</summary>
    HashSet<string>? _cuts;
    string[]? _handObjects, _handValues;

    /// <summary>The fields the hand-read layouts name in the object at <paramref name="path"/>; null when none does.</summary>
    HashSet<string>? HandNames(string path) => _hand?.Named is { } named && named.TryGetValue(path, out var set) ? set : null;

    static readonly ConcurrentDictionary<(SchemaModel, string), bool> GenericKinds = new();

    /// <summary>An object's kind as a value walk names it: with its generic argument when Mutagen's class is generic
    /// (<c>ObjectTemplate&lt;Weapon.Property&gt;</c>, <c>ObjectModFloatProperty&lt;Weapon.Property&gt;</c>), else its name.</summary>
    string KindName(string obj, string? gen)
    {
        if (gen == null) return obj;
        bool generic = GenericKinds.GetOrAdd((_m, obj), k => k.Item1.Ancestry(k.Item2).SelectMany(o => o.Fields).Any(f => f.GenericArg == "T" || f.Enum == "T"));
        return generic ? obj + "<" + gen + ">" : obj;
    }

    /// <summary>
    /// A field a hand-read layout names in the object at <paramref name="prefix"/> (of kind <paramref name="obj"/>): each
    /// object the layout described at or under it, with its kind (the Case the bytes picked, else the one the schema has
    /// there) and the fields of it no layout names said unread; each value with its bytes as the layout stores it, or
    /// said unread when Mutagen derives it (a Const, a Bit, a View, an encoded number).
    /// </summary>
    void HandGroup(string prefix, string obj, string? gen, Group g)
    {
        var hand = _hand!;
        string at = PathOf(prefix, g.Name);
        _handObjects ??= hand.Named!.Keys.Order(StringComparer.Ordinal).ToArray();
        _handValues ??= hand.Values.Keys.Order(StringComparer.Ordinal).ToArray();
        // The objects: by their paths, outer ones first
        foreach (var q in Under(_handObjects, at).OrderBy(x => x.Length))
        {
            if (!Resolve(obj, gen, g, at, q, out var def, out var kindGen, out var named)) { Unread(q, "the schema has no field there"); continue; }
            if (def.RefObject is not { } ro) continue; // a list of plain values: its entries are values
            string? kind = hand.Marks.TryGetValue(q, out var mk) ? mk : TargetsOf(ro) is { Count: 1 } one ? one[0].Name : null;
            if (kind == null) { Unread(named, $"{ro} has {TargetsOf(ro).Count} kinds, and the layout didn't pick one"); continue; }
            _values!.Object(named, KindName(kind, kindGen));
            var names = hand.Named![q];
            foreach (var f in Groups(kind, null))
                if (!names.Contains(f.Name) && f.Main.Kind != "CustomLogic" && !NoBytes(f.Main))
                    Unread(PathOf(named, f.Name), "Mutagen's hand-read layout doesn't name it");
        }
        // The values; a text with its length before it at the place Mutagen notes it, the length's bytes included
        foreach (var vp in Under(_handValues, at))
        {
            var v = hand.Values[vp];
            if (!Resolve(obj, gen, g, at, vp, out var def, out _, out var named)) { Unread(vp, "the schema has no field there"); continue; }
            // A value the layout states (a Const): given as stated, with no bytes
            if (v.Kind == "Const" && v.Const != null) { _values!.Stated(named, def, v.Const); continue; }
            // A yes/no the layout takes from one bit of a value read earlier (a weather's cloud layers, each by a bit of
            // NAM1, inverted): stated from that bit
            if (v.Kind == "Bit" && def.Kind == "Bool" && v.Sub >= 0 && v.Sub < _raw.Subrecords.Count && _raw.Content(_raw.Subrecords[v.Sub]) is var bitsOf && v.Offset >= 0 && v.Offset < bitsOf.Length)
            {
                _values!.Stated(named, def, ((bitsOf[v.Offset] & v.Mask) != 0) != v.Invert ? "True" : "False");
                continue;
            }
            if (v.Kind is "Const" or "Bit" or "View" || v.Encoding is not (null or "bool")) { Unread(named, "a value Mutagen's hand-written code works out from the bytes"); continue; }
            int length = v.Kind switch { "WString" => 2, "I32String" => 4, _ => 0 };
            _values!.LaidOut(named, def, v.Sub, v.Offset - length, v.Offset + v.Size, v.Kind);
        }
    }

    /// <summary>The paths of a sorted array at <paramref name="at"/> or under it (<c>at.</c>…, <c>at[</c>…).</summary>
    static IEnumerable<string> Under(string[] sorted, string at)
    {
        int i = Array.BinarySearch(sorted, at, StringComparer.Ordinal);
        if (i >= 0) yield return sorted[i];
        foreach (string sep in new[] { ".", "[" })
        {
            string head = at + sep;
            int j = Array.BinarySearch(sorted, head, StringComparer.Ordinal);
            if (j < 0) j = ~j;
            for (; j < sorted.Length && sorted[j].StartsWith(head, StringComparison.Ordinal); j++) yield return sorted[j];
        }
    }

    /// <summary>
    /// The schema's row of the field at <paramref name="path"/>, which stands at or under the group <paramref name="g"/> of
    /// an object of kind <paramref name="obj"/> (at <paramref name="at"/>): along the path, each list's entry by its row,
    /// each object's field in the kind the layout picked there (or the one the schema has); and the generic argument
    /// there. False when the schema has no such field.
    /// </summary>
    /// <param name="named">The path as Mutagen names it: a dictionary's entry by its key, an enum's by the member's name
    /// (a race's biped objects: <c>BipedObjects[UpperBody2VisOnly]</c>, where the layout's walk has the key's number).</param>
    bool Resolve(string obj, string? gen, Group g, string at, string path, out FieldDef def, out string? kindGen, out string named)
    {
        var cur = g;
        bool indexed = false;
        def = g.Main;
        kindGen = def.GenericArg == "T" ? gen : def.GenericArg ?? gen;
        var name = new System.Text.StringBuilder(path, 0, at.Length, path.Length + 16);
        named = path;
        int p = at.Length;
        while (p < path.Length)
        {
            if (path[p] == '[')
            {
                int e = path.IndexOf(']', p);
                if (e < 0 || cur.Item == null) return false;
                string index = path[(p + 1)..e];
                if (cur.Main.Kind == "Dictionary" && KeyDef(cur.Main) is { Enum: { } en } && _m.Enums.TryGetValue(en, out var members)
                    && long.TryParse(index, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long key)
                    && members.FirstOrDefault(x => x.Value == key) is { } member)
                    index = member.Name;
                name.Append('[').Append(index).Append(']');
                indexed = true;
                def = cur.Item;
                kindGen = def.GenericArg == "T" ? kindGen : def.GenericArg ?? kindGen;
                p = e + 1;
                continue;
            }
            if (path[p] != '.') return false;
            int q = p + 1;
            while (q < path.Length && path[q] != '.' && path[q] != '[') q++;
            string step = path[(p + 1)..q];
            // The kind of the object the step is in
            string here = path[..p];
            string? ro = (indexed ? cur.Item : cur.Main)?.RefObject;
            if (ro == null) return false;
            string? kind = _hand!.Marks.TryGetValue(here, out var mk) ? mk : TargetsOf(ro) is { Count: 1 } one ? one[0].Name : null;
            if (kind == null) return false;
            var next = Groups(kind, null).FirstOrDefault(x => x.Name == step);
            if (next == null) return false;
            name.Append('.').Append(step);
            cur = next; indexed = false;
            def = cur.Main;
            kindGen = def.GenericArg == "T" ? kindGen : def.GenericArg ?? kindGen;
            p = q;
        }
        named = name.ToString();
        return true;
    }
}
