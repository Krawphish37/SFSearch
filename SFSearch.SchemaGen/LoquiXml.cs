// Reads Mutagen's record definitions (Loqui XML) into the schema model: each object's own fields, once.
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SFSearch.SchemaGen;

public static class LoquiXml
{
    static readonly XNamespace Ns = "http://tempuri.org/LoquiSource.xsd";
    static readonly Regex SigName = new("^[A-Z0-9_]{4}$", RegexOptions.Compiled);

    public static SchemaModel Load(string recordsDir)
    {
        var model = new SchemaModel();
        var xml = new Dictionary<string, XElement?>(StringComparer.Ordinal);
        var majorFlag = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(recordsDir, "*.xml", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var doc = XDocument.Load(f);
            foreach (var o in doc.Root!.Elements(Ns + "Object"))
            {
                var d = new ObjDef
                {
                    Name = o.Attribute("name")!.Value,
                    Sig = o.Attribute("recordType")?.Value,
                    Base = o.Attribute("baseClass")?.Value,
                    ObjType = o.Attribute("objType")?.Value ?? "Subrecord",
                    Abstract = o.Attribute("abstract")?.Value == "true",
                    Marker = o.Attribute("markerType")?.Value,
                    EndMarker = o.Attribute("endMarkerType")?.Value,
                    CustomEnd = o.Attribute("customBinaryEnd")?.Value is { } ce && ce != "Off" ? ce : null,
                    SelectedBy = o.Attribute("selectedBy")?.Value,
                    SelectorValue = o.Attribute("selectorValue")?.Value,
                    ConditionFunction = o.Attribute("conditionFunction")?.Value,
                    // The Creation Kit's display name, which Mutagen's XML carries from fixes.17 on (C220)
                    CkName = o.Attribute("ckName")?.Value,
                    File = Path.GetRelativePath(recordsDir, f).Replace('\\', '/'),
                };
                d.Contains.AddRange(o.Elements(Ns + "AdditionalContainedRecordType").Select(e => e.Value.Trim()).Where(x => x.Length > 0).Distinct());
                d.LinkInterfaces.AddRange(o.Elements(Ns + "LinkInterface").Select(e => e.Value.Trim()));
                model.Objects[d.Name] = d;
                xml[d.Name] = o.Element(Ns + "Fields");
                if (o.Attribute("majorFlag")?.Value == "true") majorFlag.Add(d.Name);
            }
        }

        // The base of every record lives in Mutagen's core project, whose XML isn't in the copy. Its four fields are
        // written here by hand and checked against the compiled records like every other field.
        var major = new ObjDef { Name = "MajorRecord", ObjType = "Record", Abstract = true, File = "(Mutagen.Bethesda.Core: MajorRecord, written by hand)" };
        major.Fields.Add(new FieldDef { Path = "FormKey", Name = "FormKey", Kind = "FormKey", Size = 4, Note = "the record's own ID, in the record header" });
        major.Fields.Add(new FieldDef { Path = "VersionControl", Name = "VersionControl", Kind = "UInt32", Size = 4, Note = "record header" });
        major.Fields.Add(new FieldDef { Path = "EditorID", Name = "EditorID", Kind = "String", Subrecord = "EDID", HasOwnSig = true, Nullable = true, TextForm = "NullTerminate" });
        major.Fields.Add(new FieldDef { Path = "MajorRecordFlagsRaw", Name = "MajorRecordFlagsRaw", Kind = "Int32", Size = 4, Note = "record header flags" });
        // Two views of header flags on Mutagen's hand-written MajorRecord, written by the serializer like a field
        // (Mutagen's docs/Contributing/development/Code-Generation.md, "properties that aren't xml fields", C172)
        major.Fields.Add(Derived("IsCompressed", "Bool", null, "MajorRecord (Mutagen.Bethesda.Core, hand-written)",
            "the record header's flag 0x40000 (compressed): a view of MajorRecordFlagsRaw"));
        major.Fields.Add(Derived("IsDeleted", "Bool", null, "MajorRecord (Mutagen.Bethesda.Core, hand-written)",
            "the record header's flag 0x20 (deleted): a view of MajorRecordFlagsRaw"));
        model.Objects[major.Name] = major;
        xml[major.Name] = null;

        foreach (var o in model.Objects.Values)
            if (o.Base != null && !model.Objects.ContainsKey(o.Base))
                o.Base = o.Base == "Bethesda.MajorRecord" ? "MajorRecord" : model.Objects.ContainsKey(o.Base.Split('.').Last()) ? o.Base.Split('.').Last() : o.Base;

        foreach (var o in model.Objects.Values)
        {
            if (xml[o.Name] is { } fields) new Flattener(model, o).Walk(fields, null, 0);
            AddDerived(o, xml[o.Name], majorFlag.Contains(o.Name));
            for (int i = 0; i < o.Fields.Count; i++) { o.Fields[i].Object = o.Name; o.Fields[i].Ord = i + 1; }
        }
        model.Link();
        Layout(model);
        return model;
    }

    /// <summary>A property Mutagen adds to an object beyond the fields its XML declares. It holds no bytes of its own
    /// (a view of the header flags, or how far a subrecord's data went), so it has no subrecord and no offset, and takes
    /// no place in the generated reader's order. <paramref name="source"/> says what makes it.</summary>
    static FieldDef Derived(string name, string kind, string? enumName, string source, string note) => new()
    {
        Path = name, Name = name, Kind = kind, Enum = enumName, Binary = "Derived", Source = source,
        Note = $"not declared in Mutagen's XML: {source}; {note}",
    };

    /// <summary>
    /// The properties Mutagen's generator adds to an object from its XML (Mutagen's
    /// docs/Contributing/development/Code-Generation.md, C172), applied to the XML here and checked against the compiled
    /// package by <see cref="Reflect.Apply"/> both ways (each one is a property; no such property is left without one):
    /// <c>Versioning</c> for <c>&lt;Break /&gt;</c> directly in the object's Fields (VersioningModule: the first field,
    /// <c>&lt;Object&gt;.VersioningBreaks</c>, Break0.. in XML order); <c>&lt;Sig&gt;DataTypeState</c> for a
    /// <c>&lt;Data&gt;</c> holding <c>&lt;Break /&gt;</c> or <c>&lt;Range&gt;</c>, or optional (DataTypeModule:
    /// <c>&lt;Object&gt;.&lt;Sig&gt;DataType</c>, Break0.., Range0.., Absent; a Data without recordType is DATA);
    /// <c>MajorFlags</c> for <c>majorFlag="true"</c> (<c>&lt;Record&gt;.MajorFlag</c>, a view of the header flags). And one
    /// hand-written property the document names: RegionData's <c>DataType</c> (RDAT's first 4 bytes).
    /// </summary>
    static void AddDerived(ObjDef o, XElement? fields, bool majorFlag)
    {
        if (fields != null)
        {
            int breaks = fields.Elements(Ns + "Break").Count();
            if (breaks > 0)
                o.Fields.Insert(0, Derived("Versioning", "Enum", $"{o.Name}.VersioningBreaks", "Mutagen's generator adds it for <Break /> in the object's fields (VersioningModule)",
                    $"which of the object's {breaks} version break{(breaks == 1 ? "" : "s")} its data ended at (Break0{(breaks > 1 ? $" to Break{breaks - 1}" : "")}, in the XML's order); the writer stops there"));
            foreach (var data in fields.Elements(Ns + "Data"))
            {
                var inner = data.Element(Ns + "Fields")?.Elements().ToList() ?? new();
                int b = inner.Count(e => e.Name.LocalName == "Break"), r = inner.Count(e => e.Name.LocalName == "Range");
                bool optional = bool.TryParse(data.Attribute("optional")?.Value, out bool opt) && opt;
                if (b + r == 0 && !optional) continue;
                string sig = data.Attribute("recordType")?.Value ?? "DATA";
                var states = Enumerable.Range(0, b).Select(i => "Break" + i).Concat(Enumerable.Range(0, r).Select(i => "Range" + i)).Concat(optional ? new[] { "Absent" } : Array.Empty<string>());
                o.Fields.Add(Derived($"{sig}DataTypeState", "Enum", $"{o.Name}.{sig}DataType", "Mutagen's generator adds it for a data block with a break, a range or optional=\"true\" (DataTypeModule)",
                    $"how far {sig}'s data went ({string.Join(", ", states)}{(optional ? "; Absent: the record had no " + sig : "")})"));
            }
        }
        if (majorFlag)
            o.Fields.Add(Derived("MajorFlags", "Enum", $"{o.Name}.MajorFlag", "Mutagen's generator adds it for majorFlag=\"true\" (MajorRecordFlagModule)",
                "the record header's flags (MajorRecordFlagsRaw) as this record type names them"));
        if (o.Name == "RegionData")
        {
            // Hand-written in RegionData.cs: abstract here, each kind returns its own constant; read from RDAT's first 4
            // bytes to pick the kind, written back from it (so read-only, and always the kind's). Real bytes: placed.
            var f = Derived("DataType", "Enum", "RegionData.RegionDataType", "hand-written in Mutagen (RegionData.cs), each kind of region data returns its own value",
                "the kind of region data, RDAT's first 4 bytes (Mutagen reads them to pick the kind and writes them from it)");
            f.Subrecord = "RDAT"; f.Size = 4; f.LayoutSubrecord = "RDAT"; f.LayoutOffset = "0"; f.Binary = "NoGeneration"; f.Custom = true;
            int at = o.Fields.FindIndex(x => x.Kind == "CustomLogic");
            o.Fields.Insert(at + 1, f);
        }
    }

    sealed class Flattener(SchemaModel model, ObjDef obj)
    {
        int _blocks;

        public void Walk(XElement fields, string? blockSig, int block)
        {
            foreach (var e in fields.Elements())
            {
                string kind = e.Name.LocalName;
                string? name = e.Attribute("name")?.Value;
                if (kind == "Data")
                {
                    // A data block: its fields sit at the top of the record, all inside one subrecord
                    if (e.Element(Ns + "Fields") is { } inner) Walk(inner, e.Attribute("recordType")?.Value ?? "DATA", ++_blocks); // a data block with no signature is DATA
                    continue;
                }
                if (kind == "Break")
                {
                    int nth = obj.Fields.Count(f => f.Kind == "Break") + 1;
                    string breakName = nth == 1 ? "#break" : "#break" + nth;
                    obj.Fields.Add(new FieldDef { Path = breakName, Name = breakName, Kind = "Break", Subrecord = blockSig, Block = block, Note = "older files end here (version break)" });
                    continue;
                }
                if (name == null && kind != "Marker") continue;
                Emit(e, name ?? "#marker", name ?? "#marker", blockSig, block, false, false, null);
            }
        }

        static string? Map(XElement e, string child)
            => e.Element(Ns + child) is { } m ? string.Join(",", m.Elements(Ns + "Mapping").Select(x => $"{x.Attribute("From")!.Value}={x.Attribute("To")!.Value}")) : null;

        void SetRef(FieldDef row, XElement e, string? refName)
        {
            row.Kind = "Object";
            row.GenericArg = e.Descendants(Ns + "GenericSpecification").FirstOrDefault()?.Attribute("Definition")?.Value;
            if (refName == null) return;
            string last = refName.Split('.').Last();
            if (model.Objects.ContainsKey(refName)) row.RefObject = refName;
            else if (model.Objects.ContainsKey(last)) row.RefObject = last;
            else row.Note = Join(row.Note, $"object {refName}, defined outside the Starfield XML");
        }

        void Emit(XElement e, string path, string name, string? defaultSig, int block, bool sub, bool inList, string? sigMap)
        {
            string kind = e.Name.LocalName;
            string? own = e.Attribute("recordType")?.Value;
            string? nullable = e.Attribute("nullable")?.Value;
            string? binary = e.Attribute("binary")?.Value;
            // A text's strings table: Mutagen's StringType reads "translated" as a StringsSource (Normal, DL, IL); on
            // any other kind of field the attribute means nothing (SurfacePatternConfig's Float "Common" has translated="")
            string? table = kind == "String" && e.Attribute("translated")?.Value is "Normal" or "DL" or "IL" ? e.Attribute("translated")!.Value : null;
            var row = new FieldDef
            {
                Path = path, Name = name, Kind = kind, Block = block, IsSub = sub, InList = inList,
                Subrecord = SchemaModel.MapSig(sigMap, own ?? defaultSig), HasOwnSig = own != null,
                Nullable = nullable != null ? !nullable.Equals("False", StringComparison.OrdinalIgnoreCase) : own != null && block == 0 && !sub,
                Unknown = e.Attribute("unknown")?.Value == "true" || name.Contains("Unknown") || name.Contains("Unused") || SigName.IsMatch(name),
                Translated = table != null,
                StringsTable = table,
                // Mutagen's StringType (and AssetLinkType, built on it) reads binaryType with NullTerminate as the default
                TextForm = kind is "String" or "AssetLink" ? e.Attribute("binaryType")?.Value ?? "NullTerminate" : null,
                Default = e.Attribute("default")?.Value,
                Enum = e.Attribute("enumName")?.Value,
                Custom = binary is "NoGeneration" or "Custom" or "CustomWrite",
                Binary = binary ?? "Normal",
                ByteLength = e.Attribute("byteLength") is { } blen ? int.Parse(blen.Value) : null,
                SigMap = Map(e, "RecordTypeOverrides") ?? sigMap,
                Marker = e.Attribute("markerType")?.Value,
                EndMarker = e.Attribute("endMarkerType")?.Value,
                LayoutOffset = e.Attribute("layoutOffset")?.Value,
                LayoutBits = e.Attribute("layoutBits")?.Value,
                LayoutSubrecord = e.Attribute("layoutSubrecord")?.Value,
                LayoutEncoding = e.Attribute("layoutEncoding")?.Value,
                LayoutDroppedBits = e.Attribute("layoutDroppedBits")?.Value,
                LayoutPresentWhen = e.Attribute("layoutPresentWhen")?.Value,
                LayoutGroup = e.Attribute("layoutGroup")?.Value,
                SelectedBy = e.Attribute("selectedBy")?.Value,
                LayoutMap = e.Attribute("layoutMap")?.Value,
                CkName = e.Attribute("ckName")?.Value,
            };
            if (kind == "CustomLogic" && binary == null) row.Binary = "Custom"; // a parsing step: its own hand-written method, in the generated reader's order
            if (binary != null) row.Note = Join(row.Note, binary == "NoGeneration" ? "read by hand-written Mutagen code" : $"binary={binary}");
            if (LayoutNote(row) is { } ln) row.Note = Join(row.Note, ln);
            if (e.Attribute("overflowRecordType") is { } ov) row.Note = Join(row.Note, $"may continue in {ov.Value}");
            // A list's count: counterLength bytes before the entries, or a counter subrecord (4 bytes unless the XML says
            // otherwise: PluginListBinaryTranslationGeneration.Load sets 4 when counterRecType comes with no counterLength)
            if (kind is "List" or "RefList" or "Array")
            {
                int cl = e.Attribute("counterLength") is { } cla ? int.Parse(cla.Value) : 0;
                if (cl == 0 && e.Attribute("counterRecType") != null) cl = 4;
                if (cl > 0) row.CounterLength = cl;
                if (cl > 0 && e.Attribute("counterRecType") == null) row.Note = Join(row.Note, $"its count ({cl} byte{(cl == 1 ? "" : "s")}) comes before the entries");
            }
            if (e.Attribute("counterRecType") is { } cr) row.Note = Join(row.Note, $"counted by {cr.Value}");
            if (e.Attribute("divisor") is { } dv) row.Note = Join(row.Note, $"stored value divided by {dv.Value}");
            if (e.Attribute("multiplier") is { } mu) row.Note = Join(row.Note, $"stored value multiplied by {mu.Value}");
            if (e.Attribute("integerType") is { } it) row.Note = Join(row.Note, $"stored as {it.Value}");

            switch (kind)
            {
                case "RefDirect":
                    SetRef(row, e, e.Attribute("refName")?.Value);
                    row.StartsSubrecord = block == 0 && !sub;
                    obj.Fields.Add(row);
                    break;
                case "Ref":
                    SetRef(row, e, e.Element(Ns + "Direct")?.Attribute("refName")?.Value);
                    row.StartsSubrecord = block == 0 && !sub;
                    obj.Fields.Add(row);
                    break;
                case "RefList":
                {
                    row.Kind = "List";
                    obj.Fields.Add(row);
                    var item = new FieldDef { Path = path + "[*]", Name = name, Block = block, IsSub = true, InList = true, Subrecord = row.Subrecord, SigMap = row.SigMap, StartsSubrecord = true, Binary = row.Binary };
                    SetRef(item, e, e.Attribute("refName")?.Value);
                    obj.Fields.Add(item);
                    break;
                }
                case "List":
                case "Array":
                case "Array2D":
                case "Array2d":
                {
                    row.Kind = kind == "List" ? "List" : kind == "Array" ? "Array" : "Array2D";
                    if (e.Attribute("fixedSize") is { } fs) row.Note = Join(row.Note, $"always {fs.Value} items");
                    if (e.Attribute("fixedWidth") is { } fw) row.Note = Join(row.Note, $"always {fw.Value} x {e.Attribute("fixedHeight")?.Value} items");
                    obj.Fields.Add(row);
                    var item = e.Elements().FirstOrDefault();
                    if (item?.Name.LocalName == "Fields") item = item.Elements().FirstOrDefault(); // the item may be wrapped
                    if (item == null) break;
                    int firstItem = obj.Fields.Count;
                    EmitItem(item, path + "[*]", name, row.Subrecord, block, true, row.SigMap);
                    for (int i = firstItem; i < obj.Fields.Count; i++)
                    {
                        obj.Fields[i].Custom |= row.Custom; // the items are read by the same hand-written code
                        if (row.Binary is "NoGeneration" or "Custom") obj.Fields[i].Binary = row.Binary;
                    }
                    // A list of a fixed number of fixed-size items has a size
                    var last = obj.Fields[^1];
                    if (last != row && last.Size is int isz && last.Path == path + "[*]")
                    {
                        if (e.Attribute("fixedSize") is { } n) row.Size = isz * int.Parse(n.Value);
                        else if (e.Attribute("fixedWidth") is { } w && e.Attribute("fixedHeight") is { } h) row.Size = isz * int.Parse(w.Value) * int.Parse(h.Value);
                    }
                    break;
                }
                case "Dict":
                {
                    row.Kind = "Dictionary";
                    var key = e.Element(Ns + "Key")?.Elements().FirstOrDefault();
                    if (key != null) row.Note = Join(row.Note, $"keyed by {key.Name.LocalName}{(key.Attribute("enumName") is { } ke ? " " + ke.Value : "")}");
                    obj.Fields.Add(row);
                    var value = e.Element(Ns + "Value")?.Elements().FirstOrDefault();
                    if (value != null) EmitItem(value, path + "[*]", name, row.Subrecord, block, true, row.SigMap);
                    break;
                }
                case "Gendered":
                {
                    row.Kind = "Gendered";
                    string? mm = e.Attribute("maleMarker")?.Value, fm = e.Attribute("femaleMarker")?.Value;
                    if (mm != null || fm != null) row.Note = Join(row.Note, $"male after marker {mm}, female after marker {fm}");
                    if (e.Attribute("genderEnumRecord") is { } ge) row.Note = Join(row.Note, $"gender given by {ge.Value}");
                    obj.Fields.Add(row);
                    var items = e.Element(Ns + "Fields")?.Elements().ToList() ?? new();
                    foreach (var g in new[] { "Male", "Female" })
                    {
                        string? map = Map(e, g + "TypeOverrides") ?? sigMap;
                        foreach (var item in items)
                        {
                            string? iname = item.Attribute("name")?.Value;
                            EmitItem(item, iname == null ? $"{path}.{g}" : $"{path}.{g}.{iname}", iname ?? name, row.Subrecord, block, inList, map);
                        }
                    }
                    break;
                }
                case "CustomLogic":
                    row.Custom = true;
                    if (binary != "NoGeneration") row.Note = Join(row.Note, "read by hand-written Mutagen code");
                    obj.Fields.Add(row);
                    break;
                case "Marker":
                    row.Nullable = false; row.Unknown = false; row.Size = 0;
                    row.Note = Join(row.Note, "empty marker subrecord, not a value");
                    obj.Fields.Add(row);
                    break;
                default:
                    if (kind is "FormLink" or "FormLinkOrIndex")
                    {
                        var ifaces = e.Elements(Ns + "Interface").Select(x => x.Value.Trim()).ToList();
                        row.LinkTo = e.Attribute("refName")?.Value ?? (ifaces.Count > 0 ? string.Join("|", ifaces) : null);
                    }
                    row.Size = SizeOf(e);
                    if (e.Attribute("boolAsMarker") != null) { row.Size = 0; row.Note = Join(row.Note, "true when the subrecord is present"); }
                    obj.Fields.Add(row);
                    break;
            }
        }

        /// <summary>The item of a list, dictionary or gendered pair, at the path given.</summary>
        void EmitItem(XElement item, string path, string name, string? defaultSig, int block, bool inList, string? sigMap)
        {
            if (item.Name.LocalName == "Ref")
            {
                string? refName = item.Element(Ns + "Direct")?.Attribute("refName")?.Value;
                if (refName == null) return; // a generic type parameter (the group's record cache): no fields of its own
                var row = new FieldDef { Path = path, Name = name, Block = block, IsSub = true, InList = inList, Subrecord = SchemaModel.MapSig(sigMap, defaultSig), SigMap = sigMap, StartsSubrecord = true };
                SetRef(row, item, refName);
                obj.Fields.Add(row);
                return;
            }
            int before = obj.Fields.Count;
            Emit(item, path, name, defaultSig, block, true, inList, sigMap);
            if (obj.Fields.Count > before && obj.Fields[before].Kind == "Object") obj.Fields[before].StartsSubrecord = true;
        }
    }

    /// <summary>The fixed size in bytes of a plain value, or null when it varies or isn't known for sure.</summary>
    static int? SizeOf(XElement e)
    {
        if (e.Attribute("byteLength") is { } bl) return int.Parse(bl.Value);
        return e.Name.LocalName switch
        {
            "Float" => e.Attribute("integerType") is { } it ? it.Value switch { "Byte" => 1, "UShort" => 2, "UInt" => 4, _ => null } : 4,
            "Percent" => e.Attribute("integerType") is { } it ? it.Value switch { "Byte" or "ByteHundred" => 1, "UShort" => 2, "UInt" => 4, _ => null } : 4,
            "Int32" or "UInt32" or "FormLink" or "FormLinkOrIndex" or "Enum" or "RecordType" => 4,
            "Int16" or "UInt16" or "Half" => 2,
            "Int8" or "UInt8" or "Bool" => 1,
            "Double" or "Int64" or "UInt64" or "P2Float" or "P2Int32" => 8,
            "P3Float" => 12,
            "P3Int16" => 6,
            "P2Int16" => 4,
            "Guid" => 16,
            "Color" => e.Attribute("binaryType")?.Value switch { "Alpha" => 4, "NoAlpha" => 3, _ => null },
            _ => null,
        };
    }

    /// <summary>
    /// The bytes a field takes where Mutagen's generated reader reads the fields of an object or data block one after
    /// another (BinaryTranslationModule.IteratePassedLengths in the generator). A field with binary="Custom" (or a
    /// CustomLogic step) takes its byteLength, or an unknown number when it has none. A field with
    /// binary="NoGeneration" takes none in that order, but hand-written code reads it somewhere the XML doesn't say
    /// (before the generated fields, as a script property's name; inside another field's bytes, as a condition's compare
    /// operator), so where the fields after it stand isn't known either: null. Null: not known.
    /// </summary>
    static int? Advance(FieldDef f, Func<FieldDef, int?> size)
    {
        if (f.Kind == "Break" || f.Binary == "Derived") return 0;
        if (f.Binary == "NoGeneration") return null;
        if (f.Binary == "Custom") return f.ByteLength;
        return size(f);
    }

    /// <summary>Works out which objects are plain structs, their fixed sizes, and every field's offset.</summary>
    static void Layout(SchemaModel model)
    {
        foreach (var o in model.Objects.Values)
            o.Struct = o.ObjType == "Subrecord" && model.Ancestry(o.Name).All(a => a.Fields.All(f => !f.HasOwnSig && f.Block == 0 && f.Kind != "Marker"));

        var sizes = new Dictionary<string, int?>(StringComparer.Ordinal);
        var busy = new HashSet<string>(StringComparer.Ordinal);
        int? SizeOfObject(string name)
        {
            if (sizes.TryGetValue(name, out var known)) return known;
            if (!model.Objects.TryGetValue(name, out var o) || !busy.Add(name)) return null;
            int? total = o.Struct && !o.Abstract && model.Variants(name).Count == 0 ? 0 : null;
            if (total != null)
                foreach (var (f, _) in model.AllFields(name))
                {
                    if (f.IsSub) continue;
                    int? s = Advance(f, x => x.Kind == "Object" ? (x.RefObject != null ? SizeOfObject(x.RefObject) : null) : x.Size);
                    if (s == null) { total = null; break; }
                    total += s;
                }
            busy.Remove(name);
            return sizes[name] = total;
        }
        foreach (var o in model.Objects.Values)
        {
            o.FixedSize = SizeOfObject(o.Name);
            foreach (var f in o.Fields)
                if (f.Kind == "Object" && f.RefObject != null && !f.IsSub) f.Size = SizeOfObject(f.RefObject);
        }
        // A list of a fixed number of fixed-size objects
        foreach (var o in model.Objects.Values)
            for (int i = 0; i + 1 < o.Fields.Count; i++)
            {
                var (list, item) = (o.Fields[i], o.Fields[i + 1]);
                if (list.Kind is "Array" or "Array2D" && item.Kind == "Object" && item.Path == list.Path + "[*]" && item.RefObject != null
                    && SizeOfObject(item.RefObject) is int isz && list.Note is { } note)
                {
                    var m = Regex.Match(note, @"always (\d+)(?: x (\d+))? items");
                    if (m.Success) list.Size = isz * int.Parse(m.Groups[1].Value) * (m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1);
                }
            }

        // Offsets: where Mutagen's generated reader reads each field. A field read by hand-written code
        // (binary="NoGeneration") has no place in that order, so it gets no offset (GAPS S36).
        foreach (var o in model.Objects.Values)
        {
            if (o.Struct)
            {
                // Offsets count from the start of the object, after its base's bytes
                int? at = 0;
                foreach (var b in model.Ancestry(o.Name).Skip(1).Reverse())
                    foreach (var f in b.Fields.Where(f => !f.IsSub)) at = at is int a && Advance(f, x => x.Size) is int s ? a + s : null;
                foreach (var f in o.Fields)
                {
                    if (f.IsSub || f.Binary == "Derived") continue;
                    if (f.Kind == "Object") f.StartsSubrecord = false; // inline: its fields count on from here
                    if (f.Binary == "NoGeneration") { f.Offset = null; f.OffsetRel = false; at = null; continue; }
                    f.Offset = at; f.OffsetRel = true;
                    at = at is int a && Advance(f, x => x.Size) is int s ? a + s : null;
                }
            }
            else
            {
                // Offsets count from the start of each data block
                int block = 0; int? at = null;
                foreach (var f in o.Fields)
                {
                    if (f.IsSub || f.Binary == "Derived") continue;
                    if (f.Block == 0) { block = 0; continue; }
                    if (f.Block != block) { block = f.Block; at = 0; }
                    if (f.Binary == "NoGeneration") { f.Offset = null; at = null; continue; }
                    f.Offset = at;
                    at = at is int a && Advance(f, x => x.Size) is int s ? a + s : null;
                }
            }
        }
        HandEntered(model);
    }

    /// <summary>
    /// Objects that only hand-written Mutagen code enters: the target of a field with binary="NoGeneration" or "Custom"
    /// (a condition's Data, a perk rank's Effects), and every kind of it, when no field Mutagen's generated reader reads
    /// refers to it. That code decides where the object starts and may read parts of it itself, so the offsets of its
    /// fields aren't file addresses: they are taken away (GAPS S36). Fills <see cref="ObjDef.HandRead"/> with the field
    /// it is entered through; <see cref="Reflect.HandRead"/> adds the compiled methods.
    /// </summary>
    static void HandEntered(SchemaModel model)
    {
        var byHand = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var generated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in model.Objects.Values)
            foreach (var f in o.Fields)
            {
                if (f.RefObject == null) continue;
                if (f.Binary is "NoGeneration" or "Custom")
                {
                    string via = $"{o.Name}.{f.Path.Replace("[*]", "")}";
                    if (!(byHand.TryGetValue(f.RefObject, out var l) ? l : byHand[f.RefObject] = new()).Contains(via)) byHand[f.RefObject].Add(via);
                }
                else generated.Add(f.RefObject);
            }
        foreach (var (target, via) in byHand)
        {
            if (generated.Contains(target)) continue;
            foreach (var name in new[] { target }.Concat(model.Variants(target)))
            {
                if (name != target && generated.Contains(name)) continue;
                var od = model.Objects[name];
                od.EnteredVia.AddRange(via.Where(v => !od.EnteredVia.Contains(v)));
                foreach (var f in od.Fields) { f.Offset = null; f.OffsetRel = false; }
            }
        }
    }

    static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + "; " + b;

    /// <summary>The XML's layout attributes on a field (C186) in words, for describe and the SQL layer's column notes:
    /// "Mutagen's layout: offset 0, bits 5-7". Null when it has none.</summary>
    static string? LayoutNote(FieldDef f)
    {
        var parts = new List<string>();
        if (f.LayoutSubrecord != null) parts.Add("in " + f.LayoutSubrecord);
        if (f.LayoutOffset != null) parts.Add(f.LayoutOffset.StartsWith("after:", StringComparison.Ordinal) ? "after " + f.LayoutOffset[6..] : "offset " + f.LayoutOffset);
        if (f.LayoutBits != null) parts.Add((f.LayoutBits.Contains('-') ? "bits " : "bit ") + f.LayoutBits);
        if (f.LayoutEncoding != null) parts.Add("encoding " + f.LayoutEncoding);
        if (f.LayoutDroppedBits != null) parts.Add("bit " + f.LayoutDroppedBits + " kept elsewhere");
        if (f.LayoutPresentWhen != null) parts.Add("there when " + f.LayoutPresentWhen);
        if (f.LayoutGroup != null) parts.Add("from the child group " + f.LayoutGroup);
        if (f.SelectedBy != null) parts.Add("its kind picked by " + f.SelectedBy);
        if (f.LayoutMap != null) parts.Add("not stored as such: the number there gives it (" + string.Join(", ", f.LayoutMapEntries.Select(m => $"{m.Key} {m.Value}")) + ")");
        return parts.Count == 0 ? null : "Mutagen's layout: " + string.Join(", ", parts);
    }
}
