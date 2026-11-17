// The layouts Mutagen reads by hand and the record XML can't describe: the package's Layouts/HandReadLayouts.xml
// (C186, fixes.14 on). Since fixes.20 (C57) it lays out every list SFSearch walks, the plugin header's master list and
// the record header (Header) too, so SFSearch keeps no layout file of its own (GAPS S67, S88; WORKAROUNDS W2). SchemaGen
// checks the file (every element known, every Use a Define, every Mutagen class it names an object of the schema) and
// keeps each Header, Record, AnyRecord and Define as it is in schema.db (HandLayouts). SFSearch.Records walks them over a
// record's bytes (HandLayoutWalk) for evidence, and places the record header's fields from the Header entries
// (HeaderPlace). The file's own comment says what each element means.
using System.Xml.Linq;

namespace SFSearch.SchemaGen;

/// <summary>One top-level entry of HandReadLayouts.xml: a <c>Header</c> (the 24-byte record header, <c>Object</c>; with
/// <c>Sig</c> only that record type's: TES4), a <c>Record</c> (one record type's subrecords, <c>Sig</c> and <c>Object</c>),
/// an <c>AnyRecord</c> (a subrecord checked on every record that has it, <c>Sig</c>), or a <c>Define</c> (a block used by
/// name, <c>Name</c>). <c>Xml</c> is the element as the file has it.</summary>
public sealed record HandLayout(string Kind, string? Sig, string? Object, string? Name, string Xml)
{
    /// <summary>The element, parsed when the entry is made, so many threads may read it at once (GAPS S65).</summary>
    public XElement Element { get; } = XElement.Parse(Xml);
}

public static class HandLayouts
{
    /// <summary>Where a layout comes from, in words: "Mutagen's hand-read layout (Record QUST in HandReadLayouts.xml)".</summary>
    public static string Said(string what) => $"Mutagen's hand-read layout ({what} in HandReadLayouts.xml)";

    /// <summary>The elements the file's comment describes. A file with another one fails the build: the walk wouldn't
    /// know what it means.</summary>
    public static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "Sub", "SubRepeat", "AnyOrder", "Segments", "Generated", "Object", "Switch", "Case", "Default",
        "U8", "I8", "U16", "I16", "U32", "I32", "U64", "F32", "F16", "Guid", "View", "P3F32", "P3I16", "I32String", "FormID", "Color", "ZString", "LString", "Const", "WString", "Bytes",
        "Repeat", "Bit", "IfMore", "NotLast", "Peek", "TypeSwitch", "Use", "Note", "Null",
        // fixes.24 (C84, C86): the children only from a form version on; a reflection's FormIDs (no bytes)
        "Versioning", "ReflectionFormLinks",
    };

    /// <summary>A Mutagen class named in the file (<c>ObjectModIntProperty`1</c>) as the schema names it.</summary>
    public static string ClassName(string type) => type.Split('`')[0].Trim();

    /// <summary>Reads and checks the file. The problems found throw: the build fails.</summary>
    public static List<HandLayout> Load(string file, SchemaModel model)
    {
        var doc = XDocument.Load(file);
        var root = doc.Root ?? throw new SchemaException($"{file} is empty.");
        if (root.Name.LocalName != "HandReadLayouts" || root.Attribute("game")?.Value != "Starfield")
            throw new SchemaException($"{file}: the root isn't <HandReadLayouts game=\"Starfield\">.");
        var list = new List<HandLayout>();
        var problems = new List<string>();
        var defines = root.Elements().Where(e => e.Name.LocalName == "Define").Select(e => e.Attribute("name")?.Value).Where(n => n != null).ToHashSet(StringComparer.Ordinal);
        foreach (var e in root.Elements())
        {
            string kind = e.Name.LocalName;
            string? sig = e.Attribute("sig")?.Value, obj = e.Attribute("object")?.Value, name = e.Attribute("name")?.Value;
            switch (kind)
            {
                case "Record" when sig != null && obj != null:
                case "Header" when obj != null:
                    if (!model.Objects.ContainsKey(ClassName(obj))) problems.Add($"{kind} {sig} names the class {obj}, which the schema doesn't have");
                    break;
                case "AnyRecord" when sig != null:
                case "Define" when name != null:
                    break;
                default:
                    problems.Add($"a top-level <{kind}> the walk doesn't know (or without its sig, object or name)");
                    continue;
            }
            foreach (var d in e.Descendants())
            {
                string n = d.Name.LocalName;
                if (!Known.Contains(n)) problems.Add($"{kind} {sig ?? name}: an element <{n}> the walk doesn't know");
                if (n == "Use" && !defines.Contains(d.Attribute("name")?.Value ?? "")) problems.Add($"{kind} {sig ?? name}: <Use name=\"{d.Attribute("name")?.Value}\"> names no Define");
                if (n == "Case" && d.Attribute("type")?.Value is { } types)
                    foreach (var t in types.Split(','))
                        if (!model.Objects.ContainsKey(ClassName(t))) problems.Add($"{kind} {sig ?? name}: Case type {t}, which the schema doesn't have");
                // class: the one class the reader makes there though the field's type has kinds (C57), an object of the schema
                if (n == "Object" && d.Attribute("class")?.Value is { } cls && !model.Objects.ContainsKey(ClassName(cls)))
                    problems.Add($"{kind} {sig ?? name}: Object class {cls}, which the schema doesn't have");
            }
            list.Add(new HandLayout(kind, sig, obj == null ? null : ClassName(obj), name, e.ToString(SaveOptions.DisableFormatting)));
        }
        if (problems.Count > 0) throw new SchemaException($"{file} can't be used:\n  " + string.Join("\n  ", problems.Distinct()));
        return list;
    }

    /// <summary>A field of the record header: <c>Offset</c> and <c>Size</c> in the 24 bytes; <c>Bit</c> the flag a Bool view
    /// tests (0 for a whole value); <c>ViewOf</c> the field a Bit or View is a view of (null for a value of its own);
    /// <c>Optional</c> a view only some record types have (MajorFlags); <c>Sig</c> the record type whose header it is
    /// (TES4), or null for every other record's.</summary>
    public sealed record HeaderField(int Offset, int Size, uint Bit, string? ViewOf, bool Optional, string? Sig);

    /// <summary>Where a field of a record's 24-byte header sits, from the layout file's Header entries (C57): a plugin
    /// header's fields from <c>Header sig="TES4"</c> (its object's own fields), a record's from the Header without a sig
    /// (fields of its object, StarfieldMajorRecord, and its bases; an optional view, MajorFlags, on each record type that
    /// has the property). Null for a field the record header doesn't hold.</summary>
    public static HeaderField? HeaderPlace(SchemaModel model, FieldDef def)
    {
        foreach (var (h, fields) in HeaderPlaces.GetValue(model, MakeHeaderPlaces))
        {
            if (!fields.TryGetValue(def.Name, out var at)) continue;
            if (h.Sig != null ? def.Object == h.Object : model.Ancestry(h.Object!).Any(o => o.Name == def.Object)) return at;
            if (h.Sig == null && at.Optional && model.Objects.TryGetValue(def.Object, out var rec) && rec.Sig != null) return at;
        }
        return null;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchemaModel, List<(HandLayout, Dictionary<string, HeaderField>)>> HeaderPlaces = new();

    /// <summary>Each Header entry's fields, walked once: values by their sizes from offset 0, a Bit or View at the value it
    /// names.</summary>
    static List<(HandLayout, Dictionary<string, HeaderField>)> MakeHeaderPlaces(SchemaModel model)
    {
        var list = new List<(HandLayout, Dictionary<string, HeaderField>)>();
        foreach (var h in model.HandLayouts.Where(x => x.Kind == "Header" && x.Object != null).OrderBy(x => x.Sig == null))
            list.Add((h, HeaderFields(h.Element, h.Sig)));
        return list;
    }

    /// <summary>The fields a Header element lays out (see <see cref="HeaderPlace"/>).</summary>
    public static Dictionary<string, HeaderField> HeaderFields(XElement header, string? sig)
    {
        var fields = new Dictionary<string, HeaderField>(StringComparer.Ordinal);
        var vars = new Dictionary<string, (int Offset, int Size, string? Field)>(StringComparer.Ordinal);
        int p = 0;
        foreach (var e in header.Elements())
        {
            string n = e.Name.LocalName;
            string? field = e.Attribute("field")?.Value, name = e.Attribute("name")?.Value;
            if (n is "Bit" or "View")
            {
                if (field == null || !vars.TryGetValue(e.Attribute("of")?.Value ?? "", out var of)) continue;
                uint bit = n == "Bit" ? 1u << int.Parse(e.Attribute("index")?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture) : 0;
                fields[field] = new HeaderField(of.Offset, of.Size, bit, of.Field, e.Attribute("optional")?.Value == "true", sig);
                continue;
            }
            int size = n switch
            {
                "U8" or "I8" => 1, "U16" or "I16" => 2, "U32" or "I32" or "F32" or "FormID" => 4, "U64" => 8,
                "Bytes" => int.Parse(e.Attribute("length")?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new SchemaException($"Header {sig}: <{n}> in the record header, which has no fixed size"),
            };
            if (name != null) vars[name] = (p, size, field);
            if (field != null) fields[field] = new HeaderField(p, size, 0, null, false, sig);
            p += size;
        }
        return fields;
    }
}
