// The Creation Kit's own names for the engine's reflected classes, fields and enum members (to-do F30): its display
// names, tooltips, Hidden flags and ranges. The release game strips them; CreationKit.exe keeps them in its reflection
// registry, which the Mutagen session dumped at runtime (C219) to a JSON file.
//
// Every build reads them from a small data file checked into the repo, inputs\ck-names-<CK version>.tsv (read and
// written by CkNamesFile.cs): each reflected class and enum with its base, its fields (order, type name, display name, tooltip, range,
// Hidden, Deprecated) and its members (order, value, the same), keyed by the engine's names. The user decided that
// the program carries the names (GAPS S41: "Why not just use the information and code it into the program?").
// The dump itself stays out of the repo: only `schemagen ck-import <dump.json> <inputs folder>` reads it (Load
// below), when a new Creation Kit means a new dump. Its addresses, vftables, callbacks, sizes and alignment are left
// out; type ids are resolved to type names.
//
// The dump's shape: "types" by address, each with "name", "kind" (Class, Enumeration, List, ...), "properties" (a
// class's fields: "name", "type" an address), "members" (an enum's: "name", "value"), "upcasts" (base classes) and
// "attributes" ({"type": the address of a BSReflection::Metadata::* class, plus its values}). The metadata classes
// are found by name, never by address.
//
// Mutagen's own objects, fields and enums that are the engine's reflected ones get the CK's names too: from Mutagen's
// XML (its ckName attribute, C220, from fixes.17 on), else through the engine names the XML carries (engineClass,
// engineField, engineEnum, C57 W15, fixes.20 on), whose CK names come from the data file. No name is written in this code.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace SFSearch.SchemaGen;

/// <summary>A reflected class or enum as the Creation Kit registers it.</summary>
public sealed class CkType
{
    /// <summary>The engine's name: <c>BGSAtmosphere::MiscSettings</c>.</summary>
    public string Name = "";
    /// <summary>Class or Enumeration (the other kinds aren't kept).</summary>
    public string Kind = "";
    /// <summary>The first class it derives from (its first upcast), or null.</summary>
    public string? Base;
    public string? DisplayName, ToolTip;
    public bool Hidden, Deprecated;
    public List<CkField> Fields = new();
    public List<CkMember> Members = new();

    /// <summary>The name after the last "::": <c>MiscSettings</c>.</summary>
    public string Short => Name.LastIndexOf("::", StringComparison.Ordinal) is int at and >= 0 ? Name[(at + 2)..] : Name;
}

/// <summary>A field of a reflected class.</summary>
public sealed class CkField
{
    public string Type = "", Name = "";
    public int Ord;
    /// <summary>The engine's name of the field's type (<c>float</c>, <c>BGSAtmosphere::ATMOSPHERE_TYPE</c>, <c>BSTArray</c>).</summary>
    public string? FieldType;
    public string? DisplayName, ToolTip;
    public bool Hidden, Deprecated;
    public double? RangeMin, RangeMax, RangeStep;
}

/// <summary>A member of a reflected enum.</summary>
public sealed class CkMember
{
    public string Type = "", Name = "";
    public int Ord;
    public long Value;
    public string? DisplayName, ToolTip;
    public bool Hidden, Deprecated;
}

/// <summary>What the Creation Kit's reflection data gives, by engine name. Empty when the build had none.</summary>
public sealed class CkData
{
    public readonly Dictionary<string, CkType> Types = new(StringComparer.Ordinal);
    /// <summary>"CreationKit.exe 1.16.244.0", or null when the schema was built without the CK's names.</summary>
    public string? Source;
    /// <summary>The SHA-256 of the dump the names were taken from (the data file's header), or null.</summary>
    public string? DumpSha256;

    public bool Present => Types.Count > 0;

    /// <summary>A class or enum by its engine name, in any case.</summary>
    public CkType? Type(string? name)
    {
        if (name == null) return null;
        if (Types.TryGetValue(name, out var t)) return t;
        return Types.Values.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The classes and enums whose name after the last "::" is this (<c>MiscSettings</c>), in any case.</summary>
    public List<CkType> ByShortName(string name) =>
        Types.Values.Where(x => x.Short.Equals(name, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();

    /// <summary>A class's field, its own or a base class's, in any case.</summary>
    public CkField? Field(string? cls, string field)
    {
        for (var t = Type(cls); t != null; t = t.Base == null ? null : Type(t.Base))
        {
            var f = t.Fields.FirstOrDefault(x => x.Name == field) ?? t.Fields.FirstOrDefault(x => x.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (f != null) return f;
            if (t.Base == t.Name) break;
        }
        return null;
    }

    /// <summary>An enum's member by name, in any case.</summary>
    public CkMember? Member(string? enumName, string member) =>
        Type(enumName)?.Members is { } ms ? ms.FirstOrDefault(m => m.Name == member) ?? ms.FirstOrDefault(m => m.Name.Equals(member, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>The CK's display name when it says something the engine name doesn't: null when there is none, or
    /// when it is the engine name itself.</summary>
    public static string? Shown(string? displayName, string engineName) =>
        displayName is { Length: > 0 } d && !d.Equals(engineName, StringComparison.Ordinal) ? d : null;

    /// <summary>A range as "0 to 6, step 0.01", or null.</summary>
    public static string? RangeText(CkField f)
    {
        if (f.RangeMin == null && f.RangeMax == null) return null;
        static string N(double v) => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);
        string text = $"{(f.RangeMin is double lo ? N(lo) : "?")} to {(f.RangeMax is double hi ? N(hi) : "?")}";
        if (f.RangeStep is double s && s > 0) text += $", step {N(s)}";
        return text;
    }
}

public sealed class CkStats
{
    public int Classes, Enums, Fields, Members, TypeNames, FieldNames, MemberNames, Hidden, ToolTips, Ranges;
    public int MutagenObjects, MutagenFields, MutagenEnumMembers;
    public List<string> MappingMissing = new();
}

public static class CkReflection
{
    public static string Sha256(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    // ---- reading the dump (only ck-import does) ----

    /// <summary>Reads the CK's reflection dump. Only <c>schemagen ck-import</c> calls it; builds read the data file.</summary>
    public static CkData Load(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { MaxDepth = 256 });
        var root = doc.RootElement;
        var data = new CkData();
        if (root.TryGetProperty("generated_from", out var from) && from.ValueKind == JsonValueKind.Object)
            data.Source = $"{Str(from, "file_name") ?? "?"} {Str(from, "version") ?? "?"}".Trim();
        if (!root.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Object)
            throw new SchemaException($"{file} has no \"types\": it isn't a reflection dump this reader knows.");

        // Every type's name by its address; the metadata classes by their name
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in types.EnumerateObject()) if (Str(t.Value, "name") is { } n) names[t.Name] = n;
        string Meta(string what) => "BSReflection::Metadata::" + what;
        string? AttrName(JsonElement a) => a.TryGetProperty("type", out var ty) && names.TryGetValue(Key(ty), out var n) ? n : null;

        foreach (var t in types.EnumerateObject())
        {
            var v = t.Value;
            string kind = Str(v, "kind") ?? "";
            if (kind is not ("Class" or "Enumeration")) continue;
            var ct = new CkType { Name = Str(v, "name") ?? "", Kind = kind };
            if (ct.Name.Length == 0 || ct.Name.StartsWith("BSReflection::Metadata::", StringComparison.Ordinal)) continue;
            if (v.TryGetProperty("upcasts", out var ups) && ups.ValueKind == JsonValueKind.Array)
                foreach (var u in ups.EnumerateArray())
                    if (u.TryGetProperty("type", out var ut) && names.TryGetValue(Key(ut), out var un)) { ct.Base = un; break; }
            Attrs(v, out ct.DisplayName, out ct.ToolTip, out ct.Hidden, out ct.Deprecated, out _, out _, out _);
            if (v.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
            {
                int ord = 0;
                foreach (var p in props.EnumerateArray())
                {
                    var f = new CkField { Type = ct.Name, Name = Str(p, "name") ?? "", Ord = ++ord };
                    if (p.TryGetProperty("type", out var pt) && names.TryGetValue(Key(pt), out var ptn)) f.FieldType = ptn;
                    Attrs(p, out f.DisplayName, out f.ToolTip, out f.Hidden, out f.Deprecated, out f.RangeMin, out f.RangeMax, out f.RangeStep);
                    if (f.Name.Length > 0) ct.Fields.Add(f);
                }
            }
            if (v.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
            {
                int ord = 0;
                foreach (var m in members.EnumerateArray())
                {
                    var cm = new CkMember { Type = ct.Name, Name = Str(m, "name") ?? "", Ord = ++ord, Value = m.TryGetProperty("value", out var mv) && mv.TryGetInt64(out long lv) ? lv : 0 };
                    Attrs(m, out cm.DisplayName, out cm.ToolTip, out cm.Hidden, out cm.Deprecated, out _, out _, out _);
                    if (cm.Name.Length > 0) ct.Members.Add(cm);
                }
            }
            data.Types.TryAdd(ct.Name, ct);
        }
        if (data.Types.Count == 0) throw new SchemaException($"{file} holds no class or enum: it isn't a reflection dump this reader knows.");
        data.DumpSha256 = Sha256(file);
        return data;

        void Attrs(JsonElement owner, out string? display, out string? tip, out bool hidden, out bool deprecated, out double? min, out double? max, out double? step)
        {
            display = tip = null; hidden = deprecated = false; min = max = step = null;
            if (!owner.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Array) return;
            foreach (var a in attrs.EnumerateArray())
            {
                string? an = AttrName(a);
                if (an == Meta("DisplayName")) display ??= Str(a, "value");
                else if (an == Meta("ToolTip")) tip ??= Str(a, "value");
                else if (an == Meta("Hidden")) hidden = true;
                else if (an == Meta("Deprecated")) deprecated = true;
                else if (an == Meta("Range"))
                {
                    min = Num(a, "min"); max = Num(a, "max"); step = Num(a, "step");
                }
            }
        }
    }

    static string Key(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetRawText() : e.ToString();
    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;

    // ---- Mutagen's objects and fields that are the engine's reflected ones ----

    /// <summary>Gives Mutagen's objects, fields and enum members their CK names: kept where Mutagen's XML already
    /// gives one (ckName), else from the CK's names through the engine names Mutagen's XML carries since fixes.20 (C57
    /// W15): <c>engineClass</c> on an object, <c>engineField</c> on a field of such an object (the field's name in that
    /// class), <c>engineEnum</c> on an enum field. Every engine name the XML gives must be in the CK's names: the build
    /// fails otherwise.</summary>
    public static CkStats Apply(SchemaModel model, CkData ck)
    {
        var s = new CkStats();
        foreach (var t in ck.Types.Values)
        {
            if (t.Kind == "Class") s.Classes++; else s.Enums++;
            if (t.DisplayName != null) s.TypeNames++;
            s.Fields += t.Fields.Count; s.Members += t.Members.Count;
            s.FieldNames += t.Fields.Count(f => f.DisplayName != null);
            s.MemberNames += t.Members.Count(m => m.DisplayName != null);
            s.Hidden += t.Fields.Count(f => f.Hidden) + t.Members.Count(m => m.Hidden);
            s.ToolTips += t.Fields.Count(f => f.ToolTip != null) + t.Members.Count(m => m.ToolTip != null) + (t.ToolTip != null ? 1 : 0);
            s.Ranges += t.Fields.Count(f => f.RangeMin != null || f.RangeMax != null);
        }
        var enums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in model.Objects.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            if (o.EngineClass is { } cls)
            {
                if (ck.Type(cls) is { } ct)
                {
                    o.CkClass = ct.Name;
                    o.CkName ??= ct.DisplayName;
                    s.MutagenObjects++;
                }
                else s.MappingMissing.Add($"class {cls} (engineClass of {o.Name})");
            }
            foreach (var def in o.Fields.Where(x => x.Path == x.Name))
            {
                if (def.EngineField is { } ef)
                {
                    if (o.EngineClass == null) { s.MappingMissing.Add($"{o.Name}.{def.Name}: engineField {ef} on an object with no engineClass"); continue; }
                    var f = ck.Field(o.EngineClass, ef);
                    if (f == null) { s.MappingMissing.Add($"field {o.EngineClass}.{ef} (engineField of {o.Name}.{def.Name})"); continue; }
                    def.CkField = $"{f.Type}.{f.Name}";
                    def.CkName ??= f.DisplayName;
                    s.MutagenFields++;
                }
                if (def.EngineEnum is { } ee && def.Enum is { } en)
                {
                    if (enums.TryGetValue(en, out var had) && had != ee) s.MappingMissing.Add($"enum {en}: engineEnum {had} and {ee}");
                    enums[en] = ee;
                }
            }
        }
        foreach (var (en, cken) in enums.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var ct = ck.Type(cken);
            if (ct == null) { s.MappingMissing.Add($"enum {cken} (engineEnum of {en})"); continue; }
            if (!model.Enums.TryGetValue(en, out var members)) { s.MappingMissing.Add($"Mutagen enum {en}"); continue; }
            foreach (var m in members)
            {
                var cm = ct.Members.FirstOrDefault(x => x.Value == m.Value && !x.Hidden) ?? ct.Members.FirstOrDefault(x => x.Value == m.Value);
                if (cm == null) continue;
                m.CkMember = $"{ct.Name}.{cm.Name}";
                m.CkName ??= cm.DisplayName;
                s.MutagenEnumMembers++;
            }
        }
        return s;
    }
}
