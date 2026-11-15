// The layouts Mutagen reads by hand and the record XML can't describe: the package's Layouts\HandReadLayouts.xml
// (C186, fixes.14 on). SchemaGen checks the file (every element known, every Use a Define, every Mutagen class it
// names an object of the schema) and keeps each Record, AnyRecord and Define as it is in schema.db (HandLayouts).
// SFSearch.Records walks them over a record's bytes (HandLayoutWalk) for evidence. The file's own comment says what
// each element means.
using System.Xml.Linq;

namespace SFSearch.SchemaGen;

/// <summary>One top-level entry of HandReadLayouts.xml: a <c>Record</c> (one record type's subrecords, <c>Sig</c> and
/// <c>Object</c>), an <c>AnyRecord</c> (a subrecord checked on every record that has it, <c>Sig</c>), or a <c>Define</c>
/// (a block used by name, <c>Name</c>). <c>Xml</c> is the element as the file has it.</summary>
public sealed record HandLayout(string Kind, string? Sig, string? Object, string? Name, string Xml)
{
    XElement? _element;
    /// <summary>The element, parsed once.</summary>
    public XElement Element => _element ??= XElement.Parse(Xml);
}

public static class HandLayouts
{
    /// <summary>The elements the file's comment describes. A file with another one fails the build: the walk wouldn't
    /// know what it means.</summary>
    public static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "Sub", "SubRepeat", "AnyOrder", "Segments", "Generated", "Object", "Switch", "Case", "Default",
        "U8", "I8", "U16", "I16", "U32", "I32", "F32", "I32String", "FormID", "Color", "ZString", "LString", "Const", "WString", "Bytes",
        "Repeat", "Bit", "IfMore", "Peek", "TypeSwitch", "Use", "Note",
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
                    if (!model.Objects.ContainsKey(ClassName(obj))) problems.Add($"Record {sig} names the class {obj}, which the schema doesn't have");
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
            }
            list.Add(new HandLayout(kind, sig, obj == null ? null : ClassName(obj), name, e.ToString(SaveOptions.DisableFormatting)));
        }
        if (problems.Count > 0) throw new SchemaException($"{file} can't be used:\n  " + string.Join("\n  ", problems.Distinct()));
        return list;
    }
}
