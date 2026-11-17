// Reads Mutagen's record definitions (Loqui XML) into the schema model: each object's own fields, once.
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SFSearch.SchemaGen;

public static class LoquiXml
{
    static readonly XNamespace Ns = "http://tempuri.org/LoquiSource.xsd";
    static readonly Regex SigName = new("^[A-Z0-9_]{4}$", RegexOptions.Compiled);

    /// <param name="recordsDir">The Starfield package's record XML.</param>
    /// <param name="coreRecordsDir">Mutagen.Bethesda.Core's (fixes.18 on, C49): MajorRecord.xml, the base of every record,
    /// and MasterReference.xml, a plugin header's master (the Starfield XML names them Bethesda.MajorRecord and
    /// Bethesda.MasterReference).</param>
    /// <param name="layoutsFile">The package's Layouts/HandReadLayouts.xml: its record Header (C57 W2) lays out the views of
    /// the header's flags that Core's hand-written MajorRecord adds (IsDeleted, IsCompressed).</param>
    /// <param name="manifestFile">The package's Manifest/Properties.xml (C60 W6): the properties Mutagen adds beyond the XML,
    /// and where the generated reader reads each value (offsets and lengths).</param>
    public static SchemaModel Load(string recordsDir, string coreRecordsDir, string layoutsFile, string manifestFile)
    {
        var manifest = PropertyManifest.Load(manifestFile);
        var model = new SchemaModel();
        var xml = new Dictionary<string, XElement?>(StringComparer.Ordinal);
        void Read(string dir, string filePrefix)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
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
                        CustomEndFills = o.Attribute("customBinaryEndFills")?.Value,
                        XmlHandRead = o.Attribute("handRead")?.Value,
                        XmlHandReadFills = o.Attribute("handReadFills")?.Value,
                        SelectedBy = o.Attribute("selectedBy")?.Value,
                        SelectorValue = o.Attribute("selectorValue")?.Value,
                        SelectorEnum = o.Attribute("selectorEnum")?.Value,
                        EngineClass = o.Attribute("engineClass")?.Value,
                        ConditionFunction = o.Attribute("conditionFunction")?.Value,
                        ShortCircuit = o.Attribute("shortCircuitToLastRequired")?.Value == "true" ? "ToLastRequired" : null,
                        // The Creation Kit's display name, which Mutagen's XML carries from fixes.17 on (C220)
                        CkName = o.Attribute("ckName")?.Value,
                        File = filePrefix + Path.GetRelativePath(dir, f).Replace('\\', '/'),
                    };
                    if (filePrefix.Length > 0 && model.Objects.ContainsKey(d.Name)) throw new SchemaException($"Mutagen.Bethesda.Core's record XML defines {d.Name}, which the Starfield XML defines too ({d.File}).");
                    d.Contains.AddRange(o.Elements(Ns + "AdditionalContainedRecordType").Select(e => e.Value.Trim()).Where(x => x.Length > 0).Distinct());
                    d.LinkInterfaces.AddRange(o.Elements(Ns + "LinkInterface").Select(e => e.Value.Trim()));
                    model.Objects[d.Name] = d;
                    xml[d.Name] = o.Element(Ns + "Fields");
                }
            }
        }
        Read(recordsDir, "");
        // The base of every record (MajorRecord: the record header's flags, FormKey and version control, and EditorID)
        // and a plugin header's master (MasterReference: Master in MAST, FileSize in DATA), from Core's package since
        // fixes.18 (C49). They were written here by hand before (GAPS Part 2 row 40).
        Read(coreRecordsDir, "Mutagen.Bethesda.Core/Plugins/Records/");
        foreach (var core in new[] { "MajorRecord", "MasterReference" })
            if (!model.Objects.ContainsKey(core)) throw new SchemaException($"Mutagen.Bethesda.Core's record XML ({coreRecordsDir}) defines no {core}.");

        foreach (var o in model.Objects.Values)
            if (o.Base != null && !model.Objects.ContainsKey(o.Base))
                o.Base = o.Base == "Bethesda.MajorRecord" ? "MajorRecord" : model.Objects.ContainsKey(o.Base.Split('.').Last()) ? o.Base.Split('.').Last() : o.Base;

        foreach (var o in model.Objects.Values)
        {
            if (xml[o.Name] is { } fields) new Flattener(model, o).Walk(fields, null, 0);
            AddDerived(o, xml[o.Name], manifest);
        }
        AddHeaderViews(model, layoutsFile);
        foreach (var o in model.Objects.Values)
            for (int i = 0; i < o.Fields.Count; i++) { o.Fields[i].Object = o.Name; o.Fields[i].Ord = i + 1; }
        model.Link();
        Layout(model, manifest);
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
    /// The properties Mutagen adds to an object beyond the fields its XML declares, as Mutagen's property manifest lists them
    /// (derived=, C60 W6): <c>Versioning</c> for the object's version breaks (Break), <c>&lt;Sig&gt;DataTypeState</c> for a
    /// data block with a break, a range or optional="true" (DataType, from the block's subrecord), <c>MajorFlags</c> for a
    /// record's header flags (majorFlag), and a hand-written property with bytes of its own (handWritten with a subrecord
    /// and offset: RegionData's <c>DataType</c>, RDAT's first 4 bytes). The manifest's other hand-written properties are
    /// views of another field or of the object's class (an archetype's AssociationKey, a game setting's SettingType, a
    /// package's ProcedureTreeDataOrder): no bytes and no value of their own, so no field. The notes say what the XML says
    /// of each (how many breaks, a block's states).
    /// </summary>
    static void AddDerived(ObjDef o, XElement? fields, PropertyManifest manifest)
    {
        foreach (var p in manifest.Of(o.Name).Where(p => p.Derived != null && !p.Step))
            switch (p.Derived)
            {
                case "Break":
                {
                    int breaks = fields?.Elements(Ns + "Break").Count() ?? 0;
                    o.Fields.Insert(0, Derived(p.Name, "Enum", p.Type, "Mutagen adds it for <Break /> in the object's fields (its property manifest: derived=\"Break\")",
                        $"which of the object's {breaks} version break{(breaks == 1 ? "" : "s")} its data ended at (Break0{(breaks > 1 ? $" to Break{breaks - 1}" : "")}, in the XML's order); the writer stops there"));
                    break;
                }
                case "DataType":
                {
                    string sig = p.From ?? "DATA";
                    var data = fields?.Elements(Ns + "Data").FirstOrDefault(d => (d.Attribute("recordType")?.Value ?? "DATA") == sig);
                    var inner = data?.Element(Ns + "Fields")?.Elements().ToList() ?? new();
                    int b = inner.Count(e => e.Name.LocalName == "Break"), r = inner.Count(e => e.Name.LocalName == "Range");
                    bool optional = bool.TryParse(data?.Attribute("optional")?.Value, out bool opt) && opt;
                    var states = Enumerable.Range(0, b).Select(i => "Break" + i).Concat(Enumerable.Range(0, r).Select(i => "Range" + i)).Concat(optional ? new[] { "Absent" } : Array.Empty<string>());
                    o.Fields.Add(Derived(p.Name, "Enum", p.Type, "Mutagen adds it for a data block with a break, a range or optional=\"true\" (its property manifest: derived=\"DataType\")",
                        $"how far {sig}'s data went ({string.Join(", ", states)}{(optional ? "; Absent: the record had no " + sig : "")})"));
                    break;
                }
                case "majorFlag":
                    o.Fields.Add(Derived(p.Name, "Enum", p.Type, "Mutagen adds it for majorFlag=\"true\" (its property manifest: derived=\"majorFlag\")",
                        "the record header's flags (MajorRecordFlagsRaw) as this record type names them"));
                    break;
                case "handWritten" when p.Subrecord != null && p.Offset is int at && p.Length is int length:
                {
                    // A hand-written property read from bytes of its own (RegionData.DataType: RDAT 0-3 pick the kind, and
                    // the writer writes them from it, so read-only and always the kind's)
                    var f = Derived(p.Name, "Enum", p.Type, $"hand-written in Mutagen (its property manifest: derived=\"handWritten\", {p.Note ?? "from " + p.From})",
                        $"{p.Subrecord}'s bytes {at}-{at + length - 1}, which Mutagen reads to pick the kind and writes from it");
                    f.Subrecord = p.Subrecord; f.Size = length; f.LayoutSubrecord = p.Subrecord; f.LayoutOffset = at.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    f.Binary = "NoGeneration"; f.Custom = true;
                    int step = o.Fields.FindIndex(x => x.Kind == "CustomLogic");
                    o.Fields.Insert(step + 1, f);
                    break;
                }
            }
    }

    /// <summary>
    /// The views of the record header's flags that the record XML doesn't declare: Core's hand-written MajorRecord adds
    /// IsDeleted and IsCompressed (Code-Generation.md, "properties that aren't xml fields", C172), and the layout file's
    /// record Header lays them out as Bits of the flags (C57 W2). Each Bit (and each View that isn't optional: a type's own
    /// MajorFlags comes from majorFlag="true" above) whose field no object of the Header's chain declares is added to the
    /// outermost base whose compiled getter has the property, as the serializer writes it like a field. Reflect.Apply holds
    /// the result against the compiled getters both ways.
    /// </summary>
    static void AddHeaderViews(SchemaModel model, string layoutsFile)
    {
        var root = XDocument.Load(layoutsFile).Root ?? throw new SchemaException($"{layoutsFile} is empty.");
        var header = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Header" && e.Attribute("sig") == null)
            ?? throw new SchemaException($"{layoutsFile} lays out no record header (<Header object=...> without a sig; Mutagen fixes.20 on, C57).");
        string obj = HandLayouts.ClassName(header.Attribute("object")?.Value ?? "");
        var chain = model.Ancestry(obj).ToList();
        if (chain.Count == 0) throw new SchemaException($"{layoutsFile}: the record Header's object {obj} isn't in the record XML.");
        foreach (var e in header.Elements().Where(e => e.Name.LocalName is "Bit" or "View" && e.Attribute("optional")?.Value != "true"))
        {
            string? field = e.Attribute("field")?.Value;
            if (field == null || chain.Any(o => o.Fields.Any(f => f.Name == field))) continue;
            var at = HandLayouts.HeaderFields(header, null)[field];
            var owner = Enumerable.Reverse(chain).FirstOrDefault(o => Reflect.HasProperty(o.Name, field))
                ?? throw new SchemaException($"{layoutsFile}: the record Header's {field} is a property of no object of {obj}'s chain in the compiled package.");
            owner.Fields.Add(e.Name.LocalName == "Bit"
                ? Derived(field, "Bool", null, $"{owner.Name}, hand-written in Mutagen; laid out in HandReadLayouts.xml's record Header",
                    $"the record header's flag 0x{at.Bit:X} (bit {System.Numerics.BitOperations.Log2(at.Bit)} of {at.ViewOf}): a view of {at.ViewOf}")
                : Derived(field, "Enum", null, $"{owner.Name}, hand-written in Mutagen; laid out in HandReadLayouts.xml's record Header",
                    $"the record header's flags as an enum: a view of {at.ViewOf}"));
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
                // What the reader of a binary="Custom" field fills besides it (C60 W5; a CustomLogic step's own below)
                XmlFills = binary == "Custom" ? e.Attribute("fills")?.Value : null,
                ByteLength = e.Attribute("byteLength") is { } blen ? int.Parse(blen.Value) : null,
                SigMap = Map(e, "RecordTypeOverrides") ?? sigMap,
                Marker = e.Attribute("markerType")?.Value,
                EndMarker = e.Attribute("endMarkerType")?.Value,
                LayoutOffset = e.Attribute("layoutOffset")?.Value,
                LayoutBits = e.Attribute("layoutBits")?.Value,
                LayoutSubrecord = e.Attribute("layoutSubrecord")?.Value,
                LayoutEncoding = e.Attribute("layoutEncoding")?.Value,
                LayoutDroppedBits = e.Attribute("layoutDroppedBits")?.Value,
                MaxIsNone = e.Attribute("maxIsNone")?.Value == "true",
                ItemMarker = e.Attribute("itemMarkerType")?.Value, ItemEndMarker = e.Attribute("itemEndMarkerType")?.Value,
                LayoutPresentWhen = e.Attribute("layoutPresentWhen")?.Value,
                LayoutGroup = e.Attribute("layoutGroup")?.Value,
                SelectedBy = e.Attribute("selectedBy")?.Value,
                LayoutMap = e.Attribute("layoutMap")?.Value,
                CkName = e.Attribute("ckName")?.Value,
                SelectorEnum = e.Attribute("selectorEnum")?.Value,
                EngineField = e.Attribute("engineField")?.Value,
                EngineEnum = e.Attribute("engineEnum")?.Value,
            };
            if (kind == "CustomLogic" && binary == null) row.Binary = "Custom"; // a parsing step: its own hand-written method, in the generated reader's order
            if (binary != null) row.Note = Join(row.Note, binary == "NoGeneration" ? "read by hand-written Mutagen code" : $"binary={binary}");
            if (LayoutNote(row) is { } ln) row.Note = Join(row.Note, ln);
            if (e.Attribute("overflowRecordType") is { } ov) row.Note = Join(row.Note, $"may continue in {ov.Value}");
            // A point whose two numbers the file stores the other way round (P2Int16 swapCoords: Y, then X)
            if (e.Attribute("swapCoords")?.Value == "true") row.Note = Join(row.Note, SwapCoordsNote);
            // A list's count: counterLength bytes before the entries, or a counter subrecord (4 bytes unless the XML says
            // otherwise: PluginListBinaryTranslationGeneration.Load sets 4 when counterRecType comes with no counterLength)
            if (kind is "List" or "RefList" or "Array")
            {
                int cl = e.Attribute("counterLength") is { } cla ? int.Parse(cla.Value) : 0;
                if (cl == 0 && e.Attribute("counterRecType") != null) cl = 4;
                if (cl > 0) row.CounterLength = cl;
                if (cl > 0 && e.Attribute("counterRecType") == null) row.Note = Join(row.Note, $"its count ({cl} byte{(cl == 1 ? "" : "s")}) comes before the entries");
                // expectedLengthLength: after the count, the length of one entry, which Mutagen's reader checks (a planet
                // biome's flora: ListBinaryTranslation.Parse with expectedLength)
                if (e.Attribute("expectedLengthLength") is { } el) row.Note = Join(row.Note, $"then the length of an entry ({el.Value} bytes)");
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
                    row.MaleMarker = mm; row.FemaleMarker = fm; row.GenderRecord = e.Attribute("genderEnumRecord")?.Value;
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
                {
                    row.Custom = true;
                    if (binary != "NoGeneration") row.Note = Join(row.Note, "read by hand-written Mutagen code");
                    // The subrecords the step reads (GAPS S38): its recordType and its <RecordType> items, in the XML's
                    // order (Mutagen's generator sends each of them to the step's FillBinary<Step>Custom)
                    var sigs = new List<string>();
                    if (own != null) sigs.Add(SchemaModel.MapSig(sigMap, own) ?? own);
                    foreach (var rt in e.Elements(Ns + "RecordType").Select(x => x.Value.Trim()).Where(x => x.Length > 0))
                        if (SchemaModel.MapSig(sigMap, rt) is { } mapped && !sigs.Contains(mapped)) sigs.Add(mapped);
                    if (sigs.Count > 0) row.CustomSubrecords = string.Join(",", sigs);
                    // The fields the step's code fills and what the writer makes again what it reads from (C36, C44)
                    row.XmlFills = e.Attribute("fills")?.Value;
                    row.WrittenFrom = e.Attribute("writtenFrom")?.Value;
                    obj.Fields.Add(row);
                    break;
                }
                case "Marker":
                    row.Nullable = false; row.Unknown = false; row.Size = 0;
                    row.Note = Join(row.Note, "empty marker subrecord, not a value");
                    obj.Fields.Add(row);
                    break;
                case "ModKey":
                    // A plugin's file name (Core's MasterReference.Master): Mutagen's ModKeyBinaryTranslation reads it as a
                    // text ending in a zero byte
                    row.Kind = "String"; row.TextForm = "NullTerminate"; row.ClrType = "ModKey";
                    row.Note = Join(row.Note, "a ModKey: the master's file name, as the plugin writes it");
                    obj.Fields.Add(row);
                    break;
                default:
                    if (kind is "FormLink" or "FormLinkOrIndex")
                    {
                        var ifaces = e.Elements(Ns + "Interface").Select(x => x.Value.Trim()).ToList();
                        row.LinkTo = e.Attribute("refName")?.Value ?? (ifaces.Count > 0 ? string.Join("|", ifaces) : null);
                    }
                    row.Size = SizeOf(e);
                    // A yes/no that is a subrecord's presence: the subrecord is the one boolAsMarker names (fix-s4s5-world-reads)
                    if (e.Attribute("boolAsMarker")?.Value is { } marker) { row.Size = 0; row.Subrecord ??= marker; row.Note = Join(row.Note, "true when the subrecord is present"); }
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
            "Int32" or "UInt32" or "FormLink" or "FormLinkOrIndex" or "Enum" or "RecordType" or "FormKey" => 4, // a FormKey is stored as its FormID
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
    /// Where Mutagen's generated reader reads each field, from Mutagen's property manifest (C60 W6: offset and length, by
    /// the generator's own model) instead of the generator's rules applied again here. Also which objects are plain structs
    /// (no field of their own subrecord: the XML's shape) and their fixed sizes: a struct's size is where its last field ends
    /// when the manifest gives every field of it and its bases an offset and a length. A field read by hand-written code
    /// (binary="NoGeneration") has no place in the generated order, so the manifest gives it none (GAPS S36).
    /// </summary>
    static void Layout(SchemaModel model, PropertyManifest manifest)
    {
        foreach (var o in model.Objects.Values)
            o.Struct = o.ObjType == "Subrecord" && model.Ancestry(o.Name).All(a => a.Fields.All(f => !f.HasOwnSig && f.Block == 0 && f.Kind != "Marker"));
        // An object whose first field is an object that begins with a subrecord of its own begins with that subrecord
        // too, though the XML gives the field no signature: a container entry's Item is a ContainerItem (CNTO), so a
        // container entry is a CNTO (and its COED), not bytes inside its user's subrecord (GAPS S59). Such an object is
        // no struct. Only the first field counts: an object whose bytes start inside its user's subrecord and that holds
        // subrecords after them (a condition's CTDA and the CIS1, CIS2 some kinds of its data hold; a legendary item's
        // LNAM entry and its CITC, CTDA) is read there by hand-written code and stays one.
        foreach (var o in model.Objects.Values)
            if (o.Struct && FirstField(model, o.Name) is { } first && BeginsWithSubrecord(model, first, new HashSet<string>(StringComparer.Ordinal)))
                o.Struct = false;

        // The manifest's place of an object's own field (a CustomLogic step's too)
        PropertyManifest.Property? Place(ObjDef o, FieldDef f) => manifest.Get(o.Name, f.Name) is { } p && p.Step == (f.Kind == "CustomLogic") ? p : null;

        foreach (var o in model.Objects.Values)
        {
            // A struct's fixed size: the end of its last field, when every field of it and its bases has its place
            int? size = o.Struct && !o.Abstract && model.Variants(o.Name).Count == 0 ? 0 : null;
            foreach (var a in model.Ancestry(o.Name))
                foreach (var f in a.Fields.Where(f => !f.IsSub && f.Binary != "Derived" && f.Kind != "Break"))
                    size = size is int s && Place(a, f) is { Offset: int at, Length: int len } ? Math.Max(s, at + len) : null;
            o.FixedSize = size;
        }
        // A struct's offsets count from the object's start only when its bases' fields all have their places: a base read
        // by hand-written code (a script property's name, type and flags) puts the generated fields after bytes the
        // manifest doesn't measure, and its offsets count from there
        bool BasesPlaced(ObjDef o) => model.Ancestry(o.Name).Skip(1).All(a => a.Fields.Where(f => !f.IsSub && f.Binary != "Derived" && f.Kind != "Break").All(f => Place(a, f)?.Offset != null));
        foreach (var o in model.Objects.Values)
            foreach (var f in o.Fields)
            {
                if (f.IsSub || f.Binary == "Derived" || f.Kind == "Break") continue;
                var p = Place(o, f);
                if (o.Struct && !BasesPlaced(o)) p = p is null ? null : p with { Offset = null };
                if (f.Kind == "Object" && f.RefObject != null) f.Size = p?.Length ?? model.Objects.GetValueOrDefault(f.RefObject)?.FixedSize;
                // C81: a field of an object hand-written code enters (enteredBy) names the readers that fill it (filledBy); the
                // other fields' readers are the XML's steps and fills= (HandFills)
                if (p?.FilledBy is { } by && f.FilledBy == null && manifest.EnteredBy(o.Name).Count > 0) f.FilledBy = by;
                if (o.Struct)
                {
                    // Offsets count from the start of the object, after its base's bytes
                    if (f.Kind == "Object") f.StartsSubrecord = false; // inline: its fields count on from here
                    f.Offset = p?.Offset; f.OffsetRel = f.Binary != "NoGeneration";
                }
                else if (f.Block > 0) f.Offset = p?.Offset; // offsets count from the start of each data block
            }
        // Places the manifest gives that overlap in one data block (or one struct) can't all be right. fixes.21 to fixes.23
        // gave every field of one Loqui type in a block the place of the last of them (GAPS S171); fixes.24 (C80) gives each
        // its own, held by Mutagen's PropertyManifestTests (Offsets_RunInFieldOrderWithoutOverlapping, all games: 0). The
        // schema takes the manifest's places as they are; one that overlaps another fails the build, to be looked at.
        var overlaps = new List<string>();
        foreach (var o in model.Objects.Values)
            foreach (var block in o.Fields.Where(f => !f.IsSub && f.Offset != null && f.Binary != "Derived" && f.Kind != "Break").GroupBy(f => o.Struct ? 0 : f.Block))
            {
                var placed = block.Select(f => (F: f, At: f.Offset!.Value, End: f.Offset!.Value + (Place(o, f)?.Length ?? f.Size ?? 1))).ToList();
                overlaps.AddRange(placed.Where(a => placed.Any(b => b.F != a.F && a.At < b.End && b.At < a.End)).Select(a => $"{o.Name}.{a.F.Name} at {a.At}"));
            }
        if (overlaps.Count > 0) throw new SchemaException($"Mutagen's property manifest gives {overlaps.Count} places that overlap another field's in one data block (GAPS S171):\n  " + string.Join("\n  ", overlaps.Take(40)));
        foreach (var o in model.Objects.Values)
        {
            // A version break stands where the next field of its block starts (or where the last one before it ends)
            for (int i = 0; i < o.Fields.Count; i++)
            {
                var br = o.Fields[i];
                if (br.Kind != "Break") continue;
                bool same(FieldDef f) => !f.IsSub && f.Kind != "Break" && f.Binary != "Derived" && (o.Struct || f.Block == br.Block);
                var next = o.Fields.Skip(i + 1).FirstOrDefault(same);
                var prev = o.Fields.Take(i).LastOrDefault(same);
                br.Offset = next != null && Place(o, next) is { Offset: int n } ? n
                    : prev != null && Place(o, prev) is { Offset: int at, Length: int len } ? at + len : null;
                br.OffsetRel = o.Struct && br.Offset != null;
            }
        }
        // A list of a fixed number of fixed-size objects
        foreach (var o in model.Objects.Values)
            for (int i = 0; i + 1 < o.Fields.Count; i++)
            {
                var (list, item) = (o.Fields[i], o.Fields[i + 1]);
                if (list.Kind is "Array" or "Array2D" && item.Kind == "Object" && item.Path == list.Path + "[*]" && item.RefObject != null
                    && model.Objects.GetValueOrDefault(item.RefObject)?.FixedSize is int isz && list.Note is { } note)
                {
                    var m = Regex.Match(note, @"always (\d+)(?: x (\d+))? items");
                    if (m.Success) list.Size = isz * int.Parse(m.Groups[1].Value) * (m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1);
                }
            }
        HandEntered(model, manifest);
    }

    /// <summary>
    /// Objects that hand-written Mutagen code makes or enters (C81: the manifest's <c>enteredBy</c>, on the object or a base
    /// it inherits from: a package target, an object mod property, a perk effect, VMAD's script properties, ...). That code
    /// decides where the object starts and may read parts of it itself, so the manifest gives its fields no offset and names
    /// the readers that fill them (<c>filledBy</c>). Fills <see cref="ObjDef.EnteredVia"/> with those readers;
    /// <see cref="Reflect.HandRead"/> words them. SFSearch's own rule for which objects these are (the targets of a
    /// binary="NoGeneration" or "Custom" field no generated field refers to) is gone: the manifest is Mutagen's.
    /// </summary>
    static void HandEntered(SchemaModel model, PropertyManifest manifest)
    {
        foreach (var od in model.Objects.Values)
            foreach (var by in manifest.EnteredBy(od.Name))
                if (!od.EnteredVia.Contains(by)) od.EnteredVia.Add(by);
    }

    /// <summary>The first field of an object (its bases' first) that takes bytes: not a version break or a property
    /// Mutagen adds beyond its XML. Null when it has none.</summary>
    static FieldDef? FirstField(SchemaModel model, string obj)
        => model.AllFields(obj).Select(x => x.F).FirstOrDefault(f => f.Kind != "Break" && f.Binary != "Derived" && !f.IsSub);

    /// <summary>True when a field's bytes begin with a subrecord of its own: it has a signature, it is a list counted
    /// by a subrecord (that subrecord comes first), or it is an object (a list of objects) that begins with one: an
    /// object with a signature (its own or a base's), or whose first field begins with one, or every kind of which does.</summary>
    static bool BeginsWithSubrecord(SchemaModel model, FieldDef f, HashSet<string> seen)
    {
        if (f.HasOwnSig || f.Kind == "Marker") return true;
        if (f.Kind is "List" or "Array" && f.Note is { } note && note.Contains("counted by ", StringComparison.Ordinal)) return true;
        string? refObj = f.RefObject;
        if (refObj == null && f.Kind is "List" or "Array")
            refObj = model.Objects[f.Object].Fields.FirstOrDefault(x => x.Path == f.Path + "[*]")?.RefObject;
        if (refObj == null || !model.Objects.ContainsKey(refObj)) return false;
        var kinds = model.Objects[refObj].Abstract && model.Variants(refObj).Count > 0 ? model.Variants(refObj) : new[] { refObj };
        return kinds.All(k =>
        {
            if (model.Ancestry(k).Any(a => a.Sig != null)) return true;
            if (!seen.Add(k)) return false;
            try { return FirstField(model, k) is { } first && BeginsWithSubrecord(model, first, seen); }
            finally { seen.Remove(k); }
        });
    }

    static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + "; " + b;

    /// <summary>The XML's layout attributes on a field (C186) in words, for describe and the SQL layer's column notes:
    /// "Mutagen's layout: offset 0, bits 5-7". Null when it has none.</summary>
    /// <summary>The note of a point stored Y first (the XML's swapCoords): what a reader of its bytes swaps back.</summary>
    public const string SwapCoordsNote = "stored Y then X (swapCoords)";

    static string? LayoutNote(FieldDef f)
    {
        var parts = new List<string>();
        if (f.LayoutSubrecord != null) parts.Add(f.LayoutSubrecord == "header" ? "in the record header" : "in " + f.LayoutSubrecord);
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
