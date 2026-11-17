// Which subrecords Mutagen's hand-written code reads, and which fields that code fills (GAPS S38).
//
// The XML names a CustomLogic step's subrecords (its recordType, or its <RecordType> items) and, from fixes.18 on
// (C36, C44), the fields the step's code fills (fills=) and what the writer makes again what a step that fills nothing
// read (writtenFrom=): a PERS's ItemsParse reads XXXX and DAT2 and fills Items, a field the XML marks
// binary="NoGeneration" with no signature. The fields get the step's subrecords. (Before fixes.18 the fields were read
// from the step's compiled IL; GAPS Part 2 row 37.)
//
// From fixes.20 on (C60 W5) the XML also says what the reader of a binary="Custom" field fills beside the field (fills=
// on the field) and what an object's customBinaryEnd code fills from the record's child group (customBinaryEndFills=);
// Mutagen's CustomLogicFillsTests holds both to the compiled readers. Nothing here reads Mutagen's compiled code.
using System.Xml.Linq;

namespace SFSearch.SchemaGen;

public sealed class HandFillStats
{
    /// <summary>CustomLogic steps, and of them: those that name subrecords of their own, those read inside their
    /// object's subrecord, and those in a group's header (not a subrecord).</summary>
    public int Steps, OwnSubrecords, InSubrecord, GroupHeader;
    /// <summary>Steps whose compiled reader fills at least one field of the object; fields so filled.</summary>
    public int StepsFilling, FieldsFilled;
    /// <summary>Fields the objects' customBinaryEnd code fills (from the record's child group, not a subrecord).</summary>
    public int FieldsFilledAtEnd;
    /// <summary>binary="NoGeneration" fields that the reader of a binary="Custom" field fills (its subrecord).</summary>
    public int FieldsFilledByCustomFields;
    /// <summary>Steps that read subrecords of their own whose reader fills no field of the object: Object.Step (sigs).</summary>
    public List<string> FillNothing = new();
    /// <summary>Names a step's fills= gives that are no field of the object or its kinds (a property Mutagen's hand-written
    /// code adds beyond the XML): Object.Step: Name.</summary>
    public List<string> FillsBeyondXml = new();
    /// <summary>Fields Mutagen reads by hand (binary="NoGeneration", not inside a subrecord, not in the record header, not
    /// inside an object a hand-read field holds) that no hand-written reader fills, that have no subrecord of their own
    /// and that the XML's layout attributes don't place: Object.Field. Mutagen's files don't say where they come from.</summary>
    public List<string> FieldsNoStep = new();
    /// <summary>Subrecords a hand-filled field got from HandReadLayouts.xml beyond its step's: Object.Field SIG.</summary>
    public List<string> FromLayouts = new();
}

public static class HandFills
{
    /// <summary>The <see cref="FieldDef.FilledBy"/> of a field the object's customBinaryEnd code fills (CustomBinaryEndImport).</summary>
    public const string CustomEnd = "customBinaryEnd";

    public static HandFillStats Apply(SchemaModel model)
    {
        var st = new HandFillStats();
        foreach (var o in model.Objects.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            foreach (var step in o.Fields.Where(f => f.Kind == "CustomLogic" && f.Path == f.Name))
            {
                st.Steps++;
                if (step.CustomSubrecords != null) st.OwnSubrecords++;
                else if (o.ObjType == "Group") { st.GroupHeader++; step.Note = Join(step.Note, "reads the group's header, not a subrecord"); }
                else if (Enclosing(model, o, step) is { Count: > 0 } enc) { step.CustomSubrecords = string.Join(",", enc); st.InSubrecord++; }

                // What the step fills: the XML's fills= (fixes.18 on, C36/C44; Mutagen's CustomLogicFillsTests holds it to
                // the compiled readers). A name is a field of the object or of the kinds made from it (a placed trap's
                // TrapForm fills Projectile on PlacedArrow, PlacedBeam, ... and Hazard on PlacedHazard); a dotted name
                // fills a part of a field (Clouds.XSpeed: the field Clouds).
                if (step.XmlFills == null)
                    throw new SchemaException($"Mutagen's XML gives the CustomLogic step {o.Name}.{step.Name} no fills= (every step has one from 0.55.0-fixes.18 on, C36).");
                var kinds = new[] { o }.Concat(model.Objects.Values.Where(x => x != o && model.Ancestry(x.Name).Contains(o)).OrderBy(x => x.Name, StringComparer.Ordinal)).ToList();
                var fills = new List<FieldDef>();
                foreach (var name in step.XmlFills.Split(',').Select(x => x.Trim().Split('.')[0]).Where(x => x.Length > 0).Distinct())
                {
                    bool any = false;
                    foreach (var k in kinds)
                        if (model.Ancestry(k.Name).SelectMany(a => a.Fields).FirstOrDefault(f => f.Path == name && f.Name == name && f.Kind != "CustomLogic") is { } hit)
                        {
                            any = true;
                            if (!fills.Contains(hit)) fills.Add(hit);
                        }
                    // A property Mutagen's hand-written code adds beyond its XML (Package.ProcedureTreeDataOrder, C51)
                    if (!any) st.FillsBeyondXml.Add($"{o.Name}.{step.Name}: {name}");
                }
                if (fills.Count == 0)
                {
                    if (step.CustomSubrecords != null) st.FillNothing.Add($"{o.Name}.{step.Name} ({step.CustomSubrecords})");
                    continue;
                }
                st.StepsFilling++;
                st.FieldsFilled += fills.Count(f => f.FilledBy == null);
                step.CustomFills = string.Join(",", fills.Select(f => f.Object == o.Name ? f.Name : $"{f.Object}.{f.Name}"));
                foreach (var f in fills)
                {
                    f.FilledBy = f.FilledBy == null ? step.Name : string.Join(",", f.FilledBy.Split(',').Append(step.Name).Distinct());
                    if (step.CustomSubrecords != null)
                        f.CustomSubrecords = string.Join(",", f.CustomSubrecordList.Concat(step.CustomSubrecordList).Distinct());
                }
            }
        }
        // Mutagen's hand-read layouts (HandReadLayouts.xml, C186) name a hand-filled field's subrecords where the step
        // names fewer: a footstep set's Count step names XCNT, and its code goes on to read the five lists from DATA
        foreach (var (field, sig) in LayoutSubrecords(model))
            if (field.FilledBy != null && sig != field.Subrecord && !field.CustomSubrecordList.Contains(sig))
            {
                field.CustomSubrecords = string.Join(",", field.CustomSubrecordList.Append(sig));
                st.FromLayouts.Add($"{field.Object}.{field.Name} {sig}");
            }

        var byCustomField = new HashSet<FieldDef>();
        // What the reader of a binary="Custom" field fills besides the field itself (the field's fills=, C60 W5): a field
        // the XML marks binary="NoGeneration" (a package's DataInputVersion from PKCU). Its subrecord is the Custom field's own.
        foreach (var o in model.Objects.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
            foreach (var cf in o.Fields.Where(f => f.Binary == "Custom" && f.Kind != "CustomLogic" && f.Path == f.Name && f.XmlFills != null))
                foreach (var name in Names(cf.XmlFills!))
                    if (name != cf.Name
                        && model.Ancestry(o.Name).SelectMany(a => a.Fields).FirstOrDefault(f => f.Path == name && f.Name == name && f.Binary == "NoGeneration" && f.Kind != "CustomLogic") is { FilledBy: null } hit)
                    {
                        hit.FilledBy = cf.Name;
                        if (cf.Subrecord != null) hit.CustomSubrecords = cf.Subrecord;
                        byCustomField.Add(hit);
                        st.FieldsFilledByCustomFields++;
                    }
        // What an object's customBinaryEnd code fills (the object's customBinaryEndFills=, C60 W5): the record's child group
        // (a cell's placed objects, a quest's dialog), read after the record's subrecords. Not a CustomLogic step and no
        // subrecord, but the fields it fills are accounted for (they aren't fields whose subrecords nobody names).
        foreach (var o in model.Objects.Values.Where(o => o.CustomEnd != null).OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            if (o.CustomEndFills == null)
                throw new SchemaException($"Mutagen's XML gives {o.Name}'s customBinaryEnd no customBinaryEndFills= (every one has one from 0.55.0-fixes.20 on, C60 W5).");
            foreach (var name in Names(o.CustomEndFills))
                if (model.Ancestry(o.Name).SelectMany(a => a.Fields).FirstOrDefault(f => f.Path == name && f.Name == name && f.Kind != "CustomLogic") is { FilledBy: null } hit)
                {
                    hit.FilledBy = CustomEnd;
                    st.FieldsFilledAtEnd++;
                }
        }
        // What the method that reads a whole object by hand fills: the XML's handRead and handReadFills (C73, fixes.22 on;
        // Mutagen's CustomLogicFillsTests holds them to the compiled reader).
        // UnknownComponent (a BFCB that names no kind, C63) is read by its ReadRaw: TypeName (the BFCB text) and Data (the
        // subrecords up to the BFCE). The fields get the method's name, as the package's manifest gives them (filledBy).
        foreach (var o in model.Objects.Values.Where(o => o.XmlHandRead != null).OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            foreach (var name in (o.XmlHandReadFills ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                if (o.Fields.FirstOrDefault(f => f.Path == name && f.Name == name && f.Kind != "CustomLogic") is not { } hit)
                    throw new SchemaException($"Mutagen's XML says {o.Name}.{o.XmlHandRead} fills {name}, which is no field of {o.Name}.");
                hit.FilledBy = hit.FilledBy == null ? o.XmlHandRead : string.Join(",", hit.FilledBy.Split(',').Append(o.XmlHandRead!).Distinct());
            }
        }
        foreach (var o in model.Objects.Values)
        {
            // Copy to the rows of a filled field's items (a list's [*] row), so every row of the field carries it
            foreach (var f in o.Fields.Where(f => f.FilledBy != null && f.Path == f.Name).ToList())
                foreach (var sub in o.Fields.Where(x => x.Path.StartsWith(f.Name + "[", StringComparison.Ordinal) || x.Path.StartsWith(f.Name + ".", StringComparison.Ordinal)))
                {
                    sub.FilledBy ??= f.FilledBy;
                    sub.CustomSubrecords ??= f.CustomSubrecords;
                }
        }
        // In words, for describe and the SQL layer's column notes
        foreach (var o in model.Objects.Values)
            foreach (var f in o.Fields.Where(f => f.Path == f.Name))
            {
                if (f.Kind == "CustomLogic")
                {
                    if (o.ObjType == "Group") continue;
                    string from = f.CustomSubrecords == null ? "" : $"reads {string.Join(", ", f.CustomSubrecordList)}";
                    string fills = f.CustomFills != null ? $"fills {f.CustomFills.Replace(",", ", ")}"
                        : f.WrittenFrom != null ? $"fills no field of its object: Mutagen drops what it reads and writes it again from {WrittenFromText(f.WrittenFrom)}"
                        : "fills no field of its object: Mutagen drops what it reads (and writes it again from other fields), or other hand-written code reads it";
                    f.Note = Join(f.Note, from.Length > 0 ? $"{from}; {fills}" : fills);
                }
                else if (f.FilledBy == CustomEnd) f.Note = Join(f.Note, "filled by Mutagen's hand-written code from the record's child group (CustomBinaryEndImport)");
                else if (f.FilledBy != null)
                {
                    string from = f.CustomSubrecords != null ? " from " + string.Join(", ", f.CustomSubrecordList) : "";
                    f.Note = Join(f.Note, byCustomField.Contains(f) ? $"filled by Mutagen's hand-written reader of {f.FilledBy}{from}"
                        : $"filled by Mutagen's hand-written code ({f.FilledBy.Replace(",", ", ")}){from}");
                }
            }

        // Objects a hand-read field holds (a package's Data holds PackageDataBool, whose Data the procedure tree's reader fills)
        var heldByHand = model.Objects.Values.SelectMany(x => x.Fields).Where(f => f.RefObject != null && (f.FilledBy != null || f.Binary is "NoGeneration" or "Custom"))
            .Select(f => f.RefObject!).ToHashSet(StringComparer.Ordinal);
        foreach (var o in model.Objects.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
            foreach (var f in o.Fields.Where(f => f.Path == f.Name && f.Binary == "NoGeneration" && f.Kind != "CustomLogic" && f.FilledBy == null && f.Subrecord == null && f.Source == null && f.Block == 0 && !o.Struct
                && f.LayoutSubrecord == null && f.LayoutGroup == null                    // the XML's layout attributes say where it is
                && o.Name != "StarfieldMajorRecord"                                      // the record header's flags, not a subrecord
                && o.Name != "MajorRecord"                                               // the subrecords no reader knows, kept by Core's
                                                                                         // MajorRecordUnknownSubrecords (any signature), and the masters they were read with
                && !model.Ancestry(o.Name).Any(a => a.EnteredVia.Count > 0 || heldByHand.Contains(a.Name))))   // inside an object a hand-read field holds
                st.FieldsNoStep.Add($"{o.Name}.{f.Name}");
        return st;
    }

    static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + "; " + b;

    /// <summary>The XML's writtenFrom= (C36) in words: count:List, marker, class, field:Field.</summary>
    static string WrittenFromText(string w) => w switch
    {
        "marker" => "nothing (an empty marker)",
        "class" => "the kind of object it reads",
        _ when w.StartsWith("count:", StringComparison.Ordinal) => $"the count of {w[6..]}",
        _ when w.StartsWith("field:", StringComparison.Ordinal) => $"the field {w[6..]}",
        _ => w,
    };

    /// <summary>
    /// The subrecords HandReadLayouts.xml gives each top-level field of a record type: for each Sub, SubRepeat or
    /// Segments (its start and end), the outermost <c>field</c> above it, else its own, else the outermost ones below it
    /// (FSTS's DATA holds five lists). <c>Use</c> is read as its Define. AnyRecord entries (VMAD) are left out: they
    /// name a subrecord of every record type.
    /// </summary>
    static IEnumerable<(FieldDef Field, string Sig)> LayoutSubrecords(SchemaModel model)
    {
        var defines = model.HandLayouts.Where(h => h.Kind == "Define" && h.Name != null).ToDictionary(h => h.Name!, h => h.Element, StringComparer.Ordinal);
        foreach (var rec in model.HandLayouts.Where(h => h.Kind == "Record" && h.Object != null))
        {
            var root = Inline(new XElement(rec.Element), defines, 0);
            var kinds = new[] { rec.Object! }.Concat(model.Objects.Values.Where(x => model.Ancestry(x.Name).Any(a => a.Name == rec.Object)).Select(x => x.Name)).Distinct().ToList();
            foreach (var sub in root.Descendants().Where(e => e.Name.LocalName is "Sub" or "SubRepeat" or "Segments"))
            {
                // A Segments' start may be several signatures ("ALST,ALLS,ALCS,ALMI": a quest's aliases, GAPS S67)
                var sigs = sub.Name.LocalName == "Segments" ? (sub.Attribute("start")?.Value ?? "").Split(',').Append(sub.Attribute("end")?.Value).ToArray() : new[] { sub.Attribute("sig")?.Value };
                string? above = sub.AncestorsAndSelf().TakeWhile(a => a != root).Reverse().Select(a => a.Attribute("field")?.Value).FirstOrDefault(f => f != null);
                var tops = above != null ? new List<string> { above }
                    : sub.Descendants().Where(d => d.Attribute("field") != null && !d.Ancestors().TakeWhile(a => a != sub).Any(a => a.Attribute("field") != null))
                        .Select(d => d.Attribute("field")!.Value).ToList();
                foreach (var top in tops.Select(t => t.Split('.')[0]).Where(t => t.Length > 0).Distinct())
                    foreach (var k in kinds)
                        if (model.Ancestry(k).SelectMany(a => a.Fields).FirstOrDefault(f => f.Path == top && f.Name == top) is { } field)
                            foreach (var sig in sigs.Where(x => x is { Length: 4 }))
                                yield return (field, sig!);
            }
        }
    }

    /// <summary>A layout with every <c>Use</c> replaced by its Define's children.</summary>
    static XElement Inline(XElement e, Dictionary<string, XElement> defines, int depth)
    {
        foreach (var use in e.Descendants().Where(d => d.Name.LocalName == "Use").ToList())
            if (depth < 8 && defines.TryGetValue(use.Attribute("name")?.Value ?? "", out var def))
                use.ReplaceWith(Inline(new XElement(def), defines, depth + 1).Elements());
            else use.Remove();
        return e;
    }

    /// <summary>The subrecord a step without signatures of its own reads in: its data block's, its object's own, or, for
    /// an object with none (one run of bytes inside its user's subrecord), the subrecords of the fields that hold it.</summary>
    static List<string> Enclosing(SchemaModel model, ObjDef o, FieldDef step)
    {
        if (step.Subrecord != null) return new() { step.Subrecord };
        if (model.Ancestry(o.Name).Select(a => a.ObjType == "Subrecord" ? a.Sig : null).FirstOrDefault(x => x != null) is { } own) return new() { own };
        var kinds = model.Ancestry(o.Name).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        return model.Objects.Values.SelectMany(x => x.Fields).Where(f => f.RefObject != null && kinds.Contains(f.RefObject) && f.Subrecord != null)
            .Select(f => f.Subrecord!).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>The fields a fills= names, in its order: a dotted name fills a part of a field (Data.StringParameter1: Data).</summary>
    static IEnumerable<string> Names(string fills) => fills.Split(',').Select(x => x.Trim().Split('.')[0]).Where(x => x.Length > 0).Distinct();
}
