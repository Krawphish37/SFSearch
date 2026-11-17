// Builds schema.db: the record XML of the restored Mutagen package + compiled assembly + xEdit pairs + unknown-field evidence + annotations
// + the Creation Kit's names (inputs\ck-names-<CK version>.tsv).
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SFSearch.SchemaGen;

public sealed class BuildOptions
{
    /// <summary>The restored Mutagen.Bethesda.Starfield package's folder (its nuspec, .nupkg and loqui\ XML).</summary>
    public string PackageDir = "";
    public string PairsFile = "", EvidenceFile = "", AnnotationsFile = "", OutDb = "";
    /// <summary>xEdit's record type names (<c>inputs/xedit-record-names-&lt;date&gt;.tsv</c>, GAPS S98).</summary>
    public string RecordNamesFile = "";
    public string? ReportFile;
    /// <summary>The Creation Kit's names (to-do F30): the data file in the repo, <c>inputs\ck-names-&lt;CK version&gt;.tsv</c>
    /// (<see cref="CkNamesFile"/>). Null builds without them (only <c>--ck-names none</c> asks for that).</summary>
    public string? CkNamesPath;
    /// <summary>The CK's names already read (a test builds a schema straight from the dump with it); else the file is read.</summary>
    internal CkData? CkData;
    public bool Quiet;
}

public sealed class BuildResult
{
    public SchemaModel Model = null!;
    public ReflectStats Reflect = null!;
    public PairStats Pairs = null!;
    public List<PairRow> PairRows = new();
    public int RecordRootChecked, RecordRootMissing, EvidenceRows, EvidenceMatched, Annotations;
    /// <summary>Record types with xEdit's name for them (GAPS S98).</summary>
    public int RecordTypesNamed;
    public int Objects, ObjectFields, UnknownFields, Paths, Leaves, LeavesWithSubrecord;
    /// <summary>Fields of the properties Mutagen adds beyond its XML (GAPS Part 2 row 8), and how many by name.</summary>
    public int DerivedFields;
    public Dictionary<string, int> DerivedByName = new(StringComparer.Ordinal);
    /// <summary>How many objects and fields the XML gives an end marker (endMarkerType) and a start marker (markerType).</summary>
    public int EndMarkers, Markers;
    /// <summary>Translated texts by strings table (Normal, DL, IL); lists with a count length; texts by stored form;
    /// objects read in part or whole by hand-written Mutagen code; fields left without an offset for that reason.</summary>
    public Dictionary<string, int> StringsTables = new(), TextForms = new();
    public int CountedLists, HandReadObjects, HandPlacedFields;
    /// <summary>The CustomLogic steps: their subrecords and the fields their compiled readers fill (GAPS S38).</summary>
    public HandFillStats HandFills = new();
    /// <summary>Fields and objects with the XML's layout attributes (C186); HandReadLayouts.xml's entries by kind.</summary>
    public int LayoutFields, LayoutObjects;
    /// <summary>The component kinds the XML gives the BFCB text that picks them (selectorValue, C57 W4; GAPS S64).</summary>
    public int ComponentKinds;
    public Dictionary<string, int> HandLayouts = new();
    public List<string> RecordRootMissingList = new();
    /// <summary>What the Creation Kit's dump gave, or null when the schema was built without it.</summary>
    public CkStats? Ck;
}

public static class Generator
{
    public const string Version = "18"; // 2: markers (markerType, endMarkerType) and contained subrecords in Objects and ObjectFields
                                       // 3: ObjectFields.StringsTable, CounterLength, TextForm; Objects.HandRead; no offset for a field hand-written code places
                                       // 4: the XML's layout attributes (ObjectFields.Layout*, SelectedBy; Objects.SelectedBy, SelectorValue,
                                       //    ConditionFunction) and the HandLayouts table (Layouts\HandReadLayouts.xml), C186
                                       // 5: ObjectFields.LayoutMap (the XML's layoutMap, C196)
                                       // 6: the properties Mutagen adds beyond its XML (MajorFlags, IsCompressed, IsDeleted, Versioning,
                                       //    <Sig>DataTypeState, RegionData.DataType) as fields, ObjectFields.Source and ReadOnly (GAPS Part 2 row 8)
                                       // 7: the Creation Kit's names (to-do F30): CkTypes, CkFields, CkMembers (from inputs\ck-names-*.tsv since 2026-11-14);
                                       //    Objects.CkClass, CkName; ObjectFields.CkName, CkField; Enums.CkName, CkMember
                                       // 8: ObjectFields.CustomSubrecords, CustomFills, FilledBy: the subrecords a CustomLogic step reads and the
                                       //    fields its compiled reader fills (GAPS S38)
                                       // 9: Objects.Struct is 0 for an object holding a field whose object starts a subrecord of its own (a
                                       //    container entry's CNTO and COED, GAPS S59)
                                       // 10: which BFCB text picks which component: AComponent's SelectedBy, each kind's SelectorValue and the
                                       //    enum AComponent.ComponentType in Enums, read from Mutagen's hand-written reader and writer (GAPS S64)
                                       // 11: HandLayouts.Source, and SFSearch's own hand-read layouts (inputs/hand-read-layouts.xml) beside the
                                       //    package's: the hand-read lists the package's file doesn't lay out (GAPS S67)
                                       // 12: the plugin header's master (MasterReference: Master in MAST, FileSize in DATA, from the core
                                       //    project's XML, written by hand) and TES4's master list in SFSearch's hand-read layouts (GAPS S88)
                                       // 13: MajorRecord and MasterReference read from Mutagen.Bethesda.Core's package (its record XML, fixes.18
                                       //    on, C49) instead of written by hand: MajorRecord gains UnknownSubrecords and UnknownSubrecordsSource,
                                       //    its fields come in the XML's order (GAPS Part 2 row 40, to-do J67)
                                       // 14: RecordTypes.XEdit, xEdit's name of each record type (inputs/xedit-record-names-*.tsv), and xEdit's
                                       //    names for objects and lists in PairNames and ObjectFields (GAPS S98)
                                       // 15: Objects.ShortCircuit (the XML's shortCircuitToLastRequired) and a list's entry length after its
                                       //    count in the field's note (expectedLengthLength), for the link walk (GAPS S105)
                                       // 16: a point stored Y then X (the XML's swapCoords) says so in its note, for list views read from the
                                       //    bytes (GAPS S153)
                                       // 17: Mutagen 0.55.0-fixes.20: HandLayouts without Source (SFSearch's own file is gone, C57 W2: the
                                       //    package lays out TES4 and the record header, kind Header); Objects.SelectorEnum and
                                       //    ObjectFields.SelectorEnum (the XML's selectorEnum, C57 W3/W4); the component kinds' selectors and
                                       //    the engine names read from the XML (selectorValue, engineClass, engineField, engineEnum)
                                       // 18: Mutagen's property manifest (C60 W6) for the derived properties and the offsets, the XML's fills= for
                                       //    a Custom field's and a customBinaryEnd's fills (W5); ObjectFields.GenderRecord, MaleMarker,
                                       //    FemaleMarker (the XML's genderEnumRecord, maleMarker, femaleMarker, C57 W3) for the gendered rule

    public static BuildResult Build(BuildOptions o)
    {
        void Say(string s) { if (!o.Quiet) Console.WriteLine(s); }

        // 1. The package's record XML must belong to the compiled assembly (version and commit), unedited
        var (source, problems) = Inputs.CheckPackage(o.PackageDir, Reflect.AssemblyVersion());
        if (problems.Count > 0 || source == null) throw new SchemaException("The Mutagen package's record definitions can't be used:\n  " + string.Join("\n  ", problems));

        // 2. Objects and their own fields, from the XML; the layouts Mutagen reads by hand (C186)
        if (source.LayoutsFile == null) throw new SchemaException($"Mutagen {source.Version} carries no {Inputs.LayoutsPath} (it ships from 0.55.0-fixes.14 on, C186).");
        if (source.ManifestFile == null) throw new SchemaException($"Mutagen {source.Version} carries no {Inputs.ManifestPath} (it ships from 0.55.0-fixes.20 on, C60 W6).");
        var model = LoquiXml.Load(source.RecordsDir, source.CoreRecordsDir, source.LayoutsFile, source.ManifestFile);
        model.HandLayouts.AddRange(HandLayouts.Load(source.LayoutsFile, model));
        var res = new BuildResult { Model = model };

        // 3. The compiled assembly: every field is a property; enum members; may-be-null
        res.Reflect = Reflect.Apply(model);
        res.HandReadObjects = Reflect.HandRead(model);
        // 3a. Which BFCB text picks which kind of component: the XML's selectorValue on each kind (C57 W4, GAPS S64)
        res.ComponentKinds = model.Variants(Selectors.Component).Count(k => model.Objects[k].SelectorValue != null);
        // 3b. The subrecords hand-written code reads and the fields it fills: the XML's CustomLogic record types, and the
        // compiled readers' IL for the fields (GAPS S38)
        res.HandFills = HandFills.Apply(model);
        foreach (var rt in model.RecordTypes)
            foreach (var (f, _) in model.AllFields(rt.Name))
            {
                if (f.Path != f.Name || f.Kind is "Break" or "Marker" || (f.Kind == "CustomLogic" && !f.Property)) continue;
                res.RecordRootChecked++;
                if (!f.Property) { res.RecordRootMissing++; res.RecordRootMissingList.Add($"{rt.Name}.{f.Name}"); }
            }

        // 4. xEdit names
        res.PairRows = Inputs.ReadPairs(o.PairsFile);
        res.Pairs = PairMatcher.Match(model, res.PairRows);
        // 4a. xEdit's record type names (GAPS S98)
        var recordNames = Inputs.ReadRecordNames(o.RecordNamesFile);
        foreach (var rt in model.RecordTypes)
            if (recordNames.TryGetValue(rt.Sig!, out var xn)) { rt.XEditName = xn; res.RecordTypesNamed++; }

        // 5. Evidence for unknown fields
        var evidence = Inputs.ReadEvidence(o.EvidenceFile);
        foreach (var e in evidence)
            e.Matched = model.Objects.TryGetValue(e.Object, out var eo) && eo.Fields.Any(f => f.Name == e.Field && f.Path == f.Name);
        res.EvidenceRows = evidence.Count; res.EvidenceMatched = evidence.Count(e => e.Matched);

        // 6. Hand-kept units and remarks
        var annotations = Inputs.ReadAnnotations(o.AnnotationsFile);
        foreach (var a in annotations)
        {
            var f = model.Objects.TryGetValue(a.Object, out var ao) ? ao.Fields.FirstOrDefault(x => x.Path == a.Field) : null;
            if (f == null) throw new SchemaException($"{o.AnnotationsFile}: there is no field {a.Object}.{a.Field} in this Mutagen version. Fix or remove the row.");
            if (a.Unit.Length > 0) f.Unit = a.Unit;
            if (a.Remark.Length > 0) f.Note = f.Note == null ? a.Remark : f.Note + "; " + a.Remark;
        }
        res.Annotations = annotations.Count;

        // 6b. The Creation Kit's names: from the data file in the repo, which ck-import makes from the CK's dump (to-do F30).
        // Mutagen's XML may carry some itself (ckName, fixes.17 on); those stay.
        bool hasCk = o.CkData != null || o.CkNamesPath != null;
        if (hasCk)
        {
            model.Ck = o.CkData ?? CkNamesFile.Read(o.CkNamesPath!);
            res.Ck = CkReflection.Apply(model, model.Ck);
            if (res.Ck.MappingMissing.Count > 0)
                throw new SchemaException("The Creation Kit's names can't be given to Mutagen's fields: " + string.Join(", ", res.Ck.MappingMissing)
                    + ". Mutagen's engineClass, engineField or engineEnum names no class, field or enum of this CK names file.");
        }

        // 7. Per record type: how many real fields, paths and unknowns
        var typeStats = new Dictionary<string, (int Fields, int Paths, int Unknown, int Named)>(StringComparer.Ordinal);
        foreach (var rt in model.RecordTypes)
        {
            var defs = new HashSet<FieldDef>(); int paths = 0;
            foreach (var r in model.Expand(rt.Name))
            {
                paths++;
                if (r.Def.Kind is "Break" or "Marker") continue;
                defs.Add(r.Def);
                if (r.Def.RefObject == null && r.Def.Kind is not ("List" or "Array" or "Array2D" or "Dictionary" or "Gendered"))
                {
                    res.Leaves++;
                    if (r.Subrecord != null) res.LeavesWithSubrecord++;
                }
            }
            var real = defs.Where(d => d.Path == d.Name).ToList();
            typeStats[rt.Name] = (real.Count, paths, real.Count(d => d.Unknown), real.Count(d => d.XEdit != null));
            res.Paths += paths;
        }
        res.Objects = model.Objects.Count;
        res.ObjectFields = model.Objects.Values.Sum(x => x.Fields.Count);
        res.UnknownFields = model.Objects.Values.Sum(x => x.Fields.Count(f => f.Unknown && f.Path == f.Name));
        res.EndMarkers = model.Objects.Values.Sum(x => (x.EndMarker != null ? 1 : 0) + x.Fields.Count(f => f.EndMarker != null && f.Path == f.Name));
        res.Markers = model.Objects.Values.Sum(x => (x.Marker != null ? 1 : 0) + x.Fields.Count(f => f.Marker != null && f.Path == f.Name));
        var stored = model.Objects.Values.SelectMany(x => x.Fields).ToList();
        foreach (var g in stored.Where(f => f.StringsTable != null).GroupBy(f => f.StringsTable!)) res.StringsTables[g.Key] = g.Count();
        foreach (var g in stored.Where(f => f.TextForm != null).GroupBy(f => f.TextForm!)) res.TextForms[g.Key] = g.Count();
        res.CountedLists = stored.Count(f => f.CounterLength != null);
        res.DerivedFields = stored.Count(f => f.Source != null);
        foreach (var g in stored.Where(f => f.Source != null).GroupBy(f => f.Name.EndsWith("DataTypeState", StringComparison.Ordinal) ? "<Sig>DataTypeState" : f.Name)) res.DerivedByName[g.Key] = g.Count();
        res.LayoutFields = stored.Count(f => f.LayoutOffset != null || f.LayoutBits != null || f.LayoutSubrecord != null || f.LayoutEncoding != null
            || f.LayoutDroppedBits != null || f.LayoutPresentWhen != null || f.LayoutGroup != null || f.SelectedBy != null || f.LayoutMap != null);
        res.LayoutObjects = model.Objects.Values.Count(o => o.SelectedBy != null || o.SelectorValue != null || o.ConditionFunction != null);
        res.HandLayouts = model.HandLayouts.GroupBy(h => h.Kind).ToDictionary(g => g.Key, g => g.Count());
        res.HandPlacedFields = stored.Count(f => f.Offset == null && !f.IsSub && (f.Binary == "NoGeneration" || model.Objects[f.Object].EnteredVia.Count > 0) && (f.Block > 0 || model.Objects[f.Object].Struct));

        // 8. Meta: what this schema was made from
        string Sha(string file) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(file).Replace("\r\n", "\n")))).ToLowerInvariant();
        var meta = new List<(string, string)>
        {
            ("MutagenPackage", "Mutagen.Bethesda.Starfield"),
            ("MutagenVersion", source.Version),
            ("MutagenCommit", source.Commit),
            ("XmlSha256", source.Sha256),
            ("HandLayoutsSha256", Sha(source.LayoutsFile!)),
            ("PairsFile", Path.GetFileName(o.PairsFile)), ("PairsSha256", Sha(o.PairsFile)),
            ("RecordNamesFile", Path.GetFileName(o.RecordNamesFile)), ("RecordNamesSha256", Sha(o.RecordNamesFile)),
            ("EvidenceFile", Path.GetFileName(o.EvidenceFile)), ("EvidenceSha256", Sha(o.EvidenceFile)),
            ("AnnotationsSha256", File.Exists(o.AnnotationsFile) ? Sha(o.AnnotationsFile) : ""),
            // The CK's names: the Creation Kit and the dump they were taken from (the data file's header), and the data file
            ("CkReflection", hasCk ? model.Ck.Source ?? "" : ""),
            ("CkReflectionSha256", hasCk ? model.Ck.DumpSha256 ?? "" : ""),
            ("CkNamesFile", o.CkNamesPath != null ? Path.GetFileName(o.CkNamesPath) : ""),
            ("CkNamesSha256", o.CkNamesPath != null ? Sha(o.CkNamesPath) : ""),
            ("Generator", Version),
        };
        string all = string.Join("|", meta.Select(m => m.Item2));
        meta.Add(("SchemaVersion", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all))).ToLowerInvariant()[..16]));
        foreach (var (k, v) in meta) model.Meta[k] = v;

        Write(o.OutDb, model, res.PairRows, res.Pairs.Containers, evidence, typeStats, meta);
        if (o.ReportFile != null) WriteReport(o.ReportFile, res, source);

        Say($"schema.db for {source.Version} ({source.Commit[..10]}, {source.Files} XML files read from the packages), schema version {model.Meta["SchemaVersion"]}");
        Say($"  objects {res.Objects}, record types {model.RecordTypes.Count}, object fields stored {res.ObjectFields}, unknown fields {res.UnknownFields}");
        Say($"  end markers {res.EndMarkers}, start markers {res.Markers} (objects and fields)");
        Say($"  translated texts by strings table {string.Join(", ", res.StringsTables.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} {x.Value}"))}; lists with a count length {res.CountedLists}; texts by stored form {string.Join(", ", res.TextForms.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"))}");
        Say($"  objects read in part or whole by hand-written Mutagen code {res.HandReadObjects}; fields inside a subrecord that code places, given no offset {res.HandPlacedFields}");
        var hf = res.HandFills;
        Say($"  CustomLogic steps {hf.Steps}: subrecords of their own {hf.OwnSubrecords}, inside their object's subrecord {hf.InSubrecord}, in a group's header {hf.GroupHeader}; "
            + $"steps whose code fills fields (the XML's fills=) {hf.StepsFilling}, fields filled {hf.FieldsFilled}; fields filled from a record's child group (customBinaryEnd) {hf.FieldsFilledAtEnd}, by a binary=Custom field's reader {hf.FieldsFilledByCustomFields}");
        if (hf.FillNothing.Count > 0) Say("    steps that read subrecords and fill no field of their object: " + string.Join(", ", hf.FillNothing));
        if (hf.FillsBeyondXml.Count > 0) Say("    fills= names that are no field of the XML (properties Mutagen's hand-written code adds): " + string.Join(", ", hf.FillsBeyondXml));
        if (hf.FromLayouts.Count > 0) Say("    subrecords of hand-filled fields named by Mutagen's hand-read layouts beyond their step's: " + string.Join(", ", hf.FromLayouts));
        if (hf.FieldsNoStep.Count > 0) Say("    hand-read fields no step fills and with no subrecord of their own: " + string.Join(", ", hf.FieldsNoStep));
        Say($"  component kinds with the BFCB text that picks them (the XML's selectorValue): {res.ComponentKinds}");
        Say($"  Mutagen's layout attributes on {res.LayoutFields} fields and {res.LayoutObjects} objects; hand-read layouts {string.Join(", ", res.HandLayouts.Select(x => $"{x.Key} {x.Value}"))}");
        Say($"  paths when every record type is spelled out {res.Paths}; leaf fields {res.Leaves}, with a subrecord signature {res.LeavesWithSubrecord}");
        Say($"  properties Mutagen adds beyond its XML, from its property manifest: {res.DerivedFields} fields ({string.Join(", ", res.DerivedByName.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"))})");
        Say($"  compiled check: {res.Reflect.FieldsChecked} fields of {res.Reflect.ObjectsChecked} objects, {res.Reflect.Missing.Count} missing; record-root fields {res.RecordRootChecked}, missing {res.RecordRootMissing}");
        if (res.Reflect.Missing.Count > 0) Say("    missing: " + string.Join(", ", res.Reflect.Missing.Take(20)));
        if (res.Reflect.NoType.Count > 0) Say($"    objects with no compiled getter: {string.Join(", ", res.Reflect.NoType.Take(20))}");
        Say($"    may-be-absent comes from the compiled property; it differs from the XML's own rule on {res.Reflect.NullableChanged} fields");
        Say($"  enums {res.Reflect.EnumsFound} of {res.Reflect.EnumsNamed} found, {res.Reflect.EnumMembers} members{(res.Reflect.EnumsNotFound.Count > 0 ? " (not found: " + string.Join(", ", res.Reflect.EnumsNotFound) + ")" : "")}");
        Say($"  pairs {res.Pairs.Matched} of {res.Pairs.Rows} matched ({res.Pairs.MatchedShare:P1}): exact {res.Pairs.Exact}, flag {res.Pairs.Flag}, component {res.Pairs.Component}, byte slice {res.Pairs.Bytes}, count {res.Pairs.Count}, whole value {res.Pairs.WholeValue}, by subrecord only {res.Pairs.BySubrecord}");
        Say($"    distinct paths {res.Pairs.MatchedDistinct} of {res.Pairs.DistinctPaths}; xEdit's subrecord equals the schema's on {res.Pairs.SubrecordAgree} of {res.Pairs.SubrecordCompared}; signatures taken from xEdit for {res.Pairs.SignaturesFilled} fields");
        foreach (var (reason, n) in res.Pairs.Reasons.OrderByDescending(r => r.Value)) Say($"    unmatched, {reason}: {n}");
        foreach (var (reason, n) in res.Pairs.NotData.OrderByDescending(r => r.Value)) Say($"    not data (Mutagen's own bookkeeping), {reason}: {n}");
        if (res.Ck is { } ck)
            Say($"  the Creation Kit's names ({model.Ck.Source}): {ck.Classes} classes ({ck.TypeNames} named), {ck.Enums} enums; {ck.Fields} fields ({ck.FieldNames} named, {ck.Ranges} with a range), {ck.Members} enum members ({ck.MemberNames} named); {ck.Hidden} hidden, {ck.ToolTips} tooltips; Mutagen objects {ck.MutagenObjects}, fields {ck.MutagenFields}, enum members {ck.MutagenEnumMembers}");
        else Say("  the Creation Kit's names: not read (--ck-names none: the schema is built without them)");
        Say($"  record types with xEdit's name {res.RecordTypesNamed} of {model.RecordTypes.Count}; objects and lists of record types named after the xEdit element their fields sit in: {res.Pairs.Containers.Count} paths, {res.Pairs.ContainerFieldsNamed} container fields");
        Say($"  fields with an xEdit name {res.Pairs.FieldsNamed} ({res.Pairs.FlagFieldsNamed} sets of flags named after the element their flags sit in), enum members with one {res.Pairs.EnumMembersNamed}; evidence rows {res.EvidenceMatched} of {res.EvidenceRows} matched; annotations {res.Annotations}");
        return res;
    }

    static void WriteReport(string file, BuildResult res, Inputs.PackageSource source)
    {
        var sb = new StringBuilder();
        var un = res.PairRows.Where(p => p.Match == null).ToList();
        var notData = res.PairRows.Where(p => p.Match == "not data").ToList();
        sb.Append($"# xEdit pairs that match no field of Mutagen {source.Version} ({source.Commit[..10]}): {un.Count} of {res.PairRows.Count}.\n");
        sb.Append("# Written by schemagen at every build. A pair listed here needs a rerun of the pairing on current names.\n");
        sb.Append($"# After them: the {notData.Count} pairs whose Mutagen side is Mutagen's own bookkeeping, not data in the file (reason \"not data: …\", not counted above);\n");
        sb.Append("# then two lists of matched pairs worth a look: matched by subrecord only, and matched by name where xEdit's subrecord differs from the schema's.\n");
        sb.Append("Sig\tType\tSubrecord\tMutagenPath\txEditPath\tReason\tDetail\n");
        IEnumerable<PairRow> Sorted(IEnumerable<PairRow> rows)
            => rows.OrderBy(p => p.Sig, StringComparer.Ordinal).ThenBy(p => p.MutagenPath, StringComparer.Ordinal).ThenBy(p => p.XEditPath, StringComparer.Ordinal);
        foreach (var p in Sorted(un))
            sb.Append($"{p.Sig}\t{p.Type}\t{p.Subrecord}\t{p.MutagenPath}\t{p.XEditPath}\t{p.Reason}\t{p.Detail}\n");
        foreach (var p in Sorted(notData))
            sb.Append($"{p.Sig}\t{p.Type}\t{p.Subrecord}\t{p.MutagenPath}\t{p.XEditPath}\tnot data: {p.Reason}\t{p.Detail}\n");
        foreach (var p in Sorted(res.PairRows.Where(p => p.Match == "subrecord")))
            sb.Append($"{p.Sig}\t{p.Type}\t{p.Subrecord}\t{p.MutagenPath}\t{p.XEditPath}\tmatched by subrecord only\t{p.Detail}\n");
        foreach (var p in Sorted(res.PairRows.Where(p => PairMatcher.SubrecordComparable(p) && p.SchemaSubrecord != p.Subrecord)))
            sb.Append($"{p.Sig}\t{p.Type}\t{p.Subrecord}\t{p.MutagenPath}\t{p.XEditPath}\tmatched, subrecords differ\txEdit says {p.Subrecord}, the schema says {p.SchemaSubrecord ?? "nothing"} ({p.Object}).\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, sb.ToString());
    }

    // ---- the database ----

    const string Tables = @"
        CREATE TABLE Meta (Key TEXT PRIMARY KEY, Value TEXT) WITHOUT ROWID;
        CREATE TABLE RecordTypes (Sig TEXT NOT NULL, Type TEXT PRIMARY KEY, File TEXT, Fields INTEGER, Paths INTEGER, Unknown INTEGER, Named INTEGER, XEdit TEXT) WITHOUT ROWID;
        CREATE TABLE Objects (Name TEXT PRIMARY KEY, Base TEXT, Abstract INTEGER, Sig TEXT, ObjType TEXT, Struct INTEGER, FixedSize INTEGER, Variants INTEGER, File TEXT,
            Marker TEXT, EndMarker TEXT, Contains TEXT, HandRead TEXT, SelectedBy TEXT, SelectorValue TEXT, ConditionFunction TEXT, CkClass TEXT, CkName TEXT, ShortCircuit TEXT, SelectorEnum TEXT) WITHOUT ROWID;
        CREATE TABLE ObjectFields (Object TEXT NOT NULL, Ord INTEGER NOT NULL, Path TEXT NOT NULL, Name TEXT NOT NULL, Kind TEXT NOT NULL, Subrecord TEXT,
            Offset INTEGER, OffsetRel INTEGER, Size INTEGER, StartsSubrecord INTEGER, Nullable INTEGER, InList INTEGER, RefObject TEXT, LinkTo TEXT, Enum TEXT,
            DefaultValue TEXT, Translated INTEGER, Unknown INTEGER, Custom INTEGER, Property INTEGER, SigMap TEXT, GenericArg TEXT, ClrType TEXT,
            Readable TEXT, XEdit TEXT, XEditPairs INTEGER, Unit TEXT, Note TEXT, Marker TEXT, EndMarker TEXT, StringsTable TEXT, CounterLength INTEGER, TextForm TEXT,
            LayoutOffset TEXT, LayoutBits TEXT, LayoutSubrecord TEXT, LayoutEncoding TEXT, LayoutDroppedBits TEXT, LayoutPresentWhen TEXT, LayoutGroup TEXT, SelectedBy TEXT,
            LayoutMap TEXT, Source TEXT, ReadOnly INTEGER, CkName TEXT, CkField TEXT, CustomSubrecords TEXT, CustomFills TEXT, FilledBy TEXT, SelectorEnum TEXT, GenderRecord TEXT, MaleMarker TEXT, FemaleMarker TEXT, MaxIsNone INTEGER, ItemMarker TEXT, ItemEndMarker TEXT,
            IntegerType TEXT, Multiplier TEXT, Divisor TEXT, PRIMARY KEY (Object, Ord)) WITHOUT ROWID;
        CREATE TABLE HandLayouts (Ord INTEGER PRIMARY KEY, Kind TEXT NOT NULL, Sig TEXT, Object TEXT, Name TEXT, Xml TEXT NOT NULL);
        CREATE TABLE ObjectAncestors (Object TEXT NOT NULL, Level INTEGER NOT NULL, Ancestor TEXT NOT NULL, PRIMARY KEY (Object, Level)) WITHOUT ROWID;
        CREATE TABLE ObjectTargets (Ref TEXT NOT NULL, Idx INTEGER NOT NULL, Target TEXT NOT NULL, Marker TEXT NOT NULL, PRIMARY KEY (Ref, Idx)) WITHOUT ROWID;
        CREATE TABLE ObjectVariants (Base TEXT NOT NULL, Variant TEXT NOT NULL, PRIMARY KEY (Base, Variant)) WITHOUT ROWID;
        CREATE TABLE LinkInterfaces (Interface TEXT NOT NULL, Type TEXT NOT NULL, PRIMARY KEY (Interface, Type)) WITHOUT ROWID;
        CREATE TABLE Enums (Enum TEXT NOT NULL, Member TEXT NOT NULL, Value INTEGER, IsFlags INTEGER, XEditName TEXT, CkName TEXT, CkMember TEXT, PRIMARY KEY (Enum, Member)) WITHOUT ROWID;
        CREATE TABLE CkTypes (Type TEXT PRIMARY KEY, Kind TEXT NOT NULL, Base TEXT, DisplayName TEXT, ToolTip TEXT, Hidden INTEGER, Deprecated INTEGER) WITHOUT ROWID;
        CREATE TABLE CkFields (Type TEXT NOT NULL, Ord INTEGER NOT NULL, Field TEXT NOT NULL, FieldType TEXT, DisplayName TEXT, ToolTip TEXT, Hidden INTEGER, Deprecated INTEGER,
            RangeMin REAL, RangeMax REAL, RangeStep REAL, PRIMARY KEY (Type, Ord)) WITHOUT ROWID;
        CREATE TABLE CkMembers (Type TEXT NOT NULL, Ord INTEGER NOT NULL, Member TEXT NOT NULL, Value INTEGER, DisplayName TEXT, ToolTip TEXT, Hidden INTEGER, Deprecated INTEGER,
            PRIMARY KEY (Type, Ord)) WITHOUT ROWID;
        CREATE TABLE Pairs (Id INTEGER PRIMARY KEY, Sig TEXT, Type TEXT, Subrecord TEXT, MutagenPath TEXT, XEditPath TEXT, Method TEXT, Checked INTEGER, Agree INTEGER,
            Disagree INTEGER, Translation TEXT, Match TEXT, Object TEXT, Ord INTEGER, Part TEXT, SchemaSubrecord TEXT, Reason TEXT, Detail TEXT);
        CREATE TABLE PairNames (Sig TEXT NOT NULL, Path TEXT NOT NULL, XEditPath TEXT, Readable TEXT, PRIMARY KEY (Sig, Path)) WITHOUT ROWID;
        CREATE TABLE UnknownEvidence (Sig TEXT NOT NULL, Object TEXT NOT NULL, Field TEXT NOT NULL, RecordClass TEXT, XmlType TEXT, Subrecord TEXT, Kinds TEXT,
            RecordsWithField INTEGER, RecordsNonDefault INTEGER, Instances INTEGER, InstancesNonDefault INTEGER, DistinctValues INTEGER, RecordsNonDefaultOfficial INTEGER,
            NeverSet TEXT, Priority TEXT, Note TEXT, Evidence TEXT, TopValues TEXT, Matched INTEGER, PRIMARY KEY (Sig, Object, Field)) WITHOUT ROWID;";

    /// <summary>
    /// The Fields view: every record type's fields as paths, worked out when asked from Objects and ObjectFields.
    /// It follows the same rules as SchemaModel.Expand; a test holds the two to the same rows.
    /// </summary>
    static string FieldsView()
    {
        // One field row of a node. {P}=path prefix, {Q}=prefix without variant markers, {CTX}=the subrecord the node sits in,
        // {MAP}=signature swaps, {GEN}=the enum handed to a generic object, {BASE}=byte offset of the node, {DEPTH}, {STACK}, {SORT}, {INLIST}
        const string row = @"
            {SIG}, {TYPE},
            CASE WHEN {P} = '' THEN f.Path ELSE {P} || '.' || f.Path END,
            CASE WHEN {Q} = '' THEN f.Path ELSE {Q} || '.' || f.Path END,
            f.Object, f.Ord,
            CASE WHEN coalesce(f.SigMap, {MAP}) IS NOT NULL AND instr(coalesce(f.SigMap, {MAP}), coalesce(f.Subrecord, ro.Sig, CASE WHEN o.Struct THEN {CTX} END) || '=') > 0
                 THEN substr(coalesce(f.SigMap, {MAP}), instr(coalesce(f.SigMap, {MAP}), coalesce(f.Subrecord, ro.Sig, CASE WHEN o.Struct THEN {CTX} END) || '=') + 5, 4)
                 ELSE coalesce(f.Subrecord, ro.Sig, CASE WHEN o.Struct THEN {CTX} END) END,
            coalesce(f.SigMap, {MAP}),
            CASE WHEN f.Enum = 'T' THEN {GEN} ELSE f.Enum END,
            CASE WHEN f.GenericArg = 'T' THEN {GEN} ELSE f.GenericArg END,
            CASE WHEN f.OffsetRel THEN {BASE} + f.Offset ELSE f.Offset END,
            CASE WHEN f.StartsSubrecord THEN 0 WHEN f.OffsetRel THEN {BASE} + f.Offset ELSE f.Offset END,
            {DEPTH}, {STACK}, {SORT} || (9 - a.Level) || printf('%04d', f.Ord) || '.', ({INLIST} OR f.InList), f.RefObject";
        string Fill(string sig, string type, string p, string q, string ctx, string map, string gen, string bas, string depth, string stack, string sort, string inlist)
            => row.Replace("{SIG}", sig).Replace("{TYPE}", type).Replace("{P}", p).Replace("{Q}", q).Replace("{CTX}", ctx).Replace("{MAP}", map).Replace("{GEN}", gen)
                  .Replace("{BASE}", bas).Replace("{DEPTH}", depth).Replace("{STACK}", stack).Replace("{SORT}", sort).Replace("{INLIST}", inlist);
        const string joins = @"
              JOIN ObjectFields f ON f.Object = a.Ancestor
              JOIN Objects o ON o.Name = f.Object
              LEFT JOIN Objects ro ON ro.Name = f.RefObject";
        return $@"
        CREATE VIEW Fields AS
        WITH RECURSIVE w(Sig, Type, Path, PlainPath, Object, Ord, Subrecord, Map, Enum, ChildGen, Offset, ChildBase, Depth, Stack, Sort, InList, RefObject) AS (
            SELECT {Fill("rt.Sig", "rt.Type", "''", "''", "NULL", "NULL", "NULL", "0", "0", "'/' || rt.Type || '/'", "''", "0")}
            FROM RecordTypes rt
              JOIN ObjectAncestors a ON a.Object = rt.Type{joins}
            UNION ALL
            SELECT {Fill("p.Sig", "p.Type", "(p.Path || t.Marker)", "p.PlainPath", "p.Subrecord", "p.Map", "p.ChildGen", "p.ChildBase", "p.Depth + 1", "(p.Stack || t.Target || '/')", "(p.Sort || printf('%03d', t.Idx) || '.')", "p.InList")}
            FROM w p
              JOIN ObjectTargets t ON t.Ref = p.RefObject AND p.Depth < {SchemaModel.MaxDepth} AND instr(p.Stack, '/' || t.Target || '/') = 0
              JOIN ObjectAncestors a ON a.Object = t.Target{joins}
        )
        SELECT w.Sig, w.Type, w.Path, f.Object, f.Ord, f.Name, f.Kind, w.Subrecord, w.Offset, f.Size, f.Nullable, w.InList, f.RefObject, f.LinkTo, w.Enum,
               f.DefaultValue, f.Translated, f.Unknown, f.Custom, f.Property,
               coalesce(pn.Readable, f.Readable) AS Readable, coalesce(pn.XEditPath, f.XEdit) AS XEdit, f.Unit, f.Note, e.Evidence, w.Depth, w.PlainPath, w.Sort
        FROM w
          JOIN ObjectFields f ON f.Object = w.Object AND f.Ord = w.Ord
          LEFT JOIN PairNames pn ON pn.Sig = w.Sig AND pn.Path = w.PlainPath
          LEFT JOIN UnknownEvidence e ON e.Sig = w.Sig AND e.Object = f.Object AND e.Field = f.Name AND f.Path = f.Name;";
    }

    /// <summary>
    /// The Mutagen name of the field a pair names: the field the pair was matched to (its object and order), else the
    /// path's last part. A readable name falls back to it where xEdit's is a non-name and it says more (PairMatcher.ReadableName).
    /// </summary>
    static string MutagenName(SchemaModel model, PairRow p, string path)
        => p.Object != null && p.Ord != null && model.Objects.TryGetValue(p.Object, out var o) && o.Fields.FirstOrDefault(f => f.Ord == p.Ord) is { } def
            ? def.Name
            : path.Split('.')[^1].Replace("[*]", "");

    static void Write(string outDb, SchemaModel model, List<PairRow> pairs, List<PairMatcher.ContainerName> containers, List<EvidenceRow> evidence,
        Dictionary<string, (int Fields, int Paths, int Unknown, int Named)> typeStats, List<(string, string)> meta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outDb))!);
        string tmp = outDb + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var con = new SqliteConnection($"Data Source={tmp};Pooling=False"))
        {
            con.Open();
            Exec(con, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
            Exec(con, Tables);
            using var tx = con.BeginTransaction();

            foreach (var (k, v) in meta) Insert(con, "Meta", k, v);
            Insert(con, "Meta", "Built", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

            foreach (var o in model.Objects.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
            {
                Insert(con, "Objects", o.Name, o.Base, o.Abstract, o.Sig, o.ObjType, o.Struct, o.FixedSize, model.Variants(o.Name).Count, o.File,
                    o.Marker, o.EndMarker, o.Contains.Count > 0 ? string.Join(",", o.Contains) : null, o.HandRead, o.SelectedBy, o.SelectorValue, o.ConditionFunction, o.CkClass, o.CkName, o.ShortCircuit, o.SelectorEnum);
                int level = 0;
                foreach (var a in model.Ancestry(o.Name)) Insert(con, "ObjectAncestors", o.Name, level++, a.Name);
                foreach (var t in model.Targets(o.Name)) Insert(con, "ObjectTargets", o.Name, t.Idx, t.Name, t.Marker);
                foreach (var v in model.Variants(o.Name)) Insert(con, "ObjectVariants", o.Name, v);
                foreach (var i in o.LinkInterfaces.Distinct()) Insert(con, "LinkInterfaces", i, o.Name);
                foreach (var f in o.Fields)
                    Insert(con, "ObjectFields", f.Object, f.Ord, f.Path, f.Name, f.Kind, f.Subrecord, f.Offset, f.OffsetRel, f.Size, f.StartsSubrecord, f.Nullable, f.InList,
                        f.RefObject, f.LinkTo, f.Enum, f.Default, f.Translated, f.Unknown, f.Custom, f.Property, f.SigMap, f.GenericArg, f.ClrType,
                        f.Readable, f.XEdit, f.XEditPairs, f.Unit, f.Note, f.Marker, f.EndMarker, f.StringsTable, f.CounterLength, f.TextForm,
                        f.LayoutOffset, f.LayoutBits, f.LayoutSubrecord, f.LayoutEncoding, f.LayoutDroppedBits, f.LayoutPresentWhen, f.LayoutGroup, f.SelectedBy, f.LayoutMap, f.Source, f.ReadOnly, f.CkName, f.CkField,
                        f.CustomSubrecords, f.CustomFills, f.FilledBy, f.SelectorEnum, f.GenderRecord, f.MaleMarker, f.FemaleMarker, f.MaxIsNone, f.ItemMarker, f.ItemEndMarker,
                        f.IntegerType, f.Multiplier, f.Divisor);
            }
            int hl = 0;
            foreach (var h in model.HandLayouts) Insert(con, "HandLayouts", ++hl, h.Kind, h.Sig, h.Object, h.Name, h.Xml);
            foreach (var rt in model.RecordTypes)
            {
                var s = typeStats[rt.Name];
                Insert(con, "RecordTypes", rt.Sig, rt.Name, rt.File, s.Fields, s.Paths, s.Unknown, s.Named, rt.XEditName);
            }
            foreach (var (name, members) in model.Enums.OrderBy(e => e.Key, StringComparer.Ordinal))
                foreach (var m in members.GroupBy(m => m.Name).Select(g => g.First()))
                    Insert(con, "Enums", name, m.Name, m.Value, m.IsFlags, m.XEditName, m.CkName, m.CkMember);
            foreach (var t in model.Ck.Types.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                Insert(con, "CkTypes", t.Name, t.Kind, t.Base, t.DisplayName, t.ToolTip, t.Hidden, t.Deprecated);
                foreach (var f in t.Fields) Insert(con, "CkFields", t.Name, f.Ord, f.Name, f.FieldType, f.DisplayName, f.ToolTip, f.Hidden, f.Deprecated, f.RangeMin, f.RangeMax, f.RangeStep);
                foreach (var m in t.Members) Insert(con, "CkMembers", t.Name, m.Ord, m.Name, m.Value, m.DisplayName, m.ToolTip, m.Hidden, m.Deprecated);
            }
            int id = 0;
            var seen = new HashSet<(string, string)>();
            foreach (var p in pairs)
                Insert(con, "Pairs", ++id, p.Sig, p.Type, p.Subrecord, p.MutagenPath, p.XEditPath, p.Method, p.Checked, p.Agree, p.Disagree, p.Translation,
                    p.Match, p.Object, p.Ord, p.Part, p.SchemaSubrecord, p.Reason, p.Detail);
            // Several pairs can name one path: each kind of a list's items has its own xEdit field (a scene's Dialogue and
            // Radio actions' Topic, a quest's reference and location aliases' Name; J108). The one checked on the most
            // records names it, then the pairs file's order
            foreach (var p in pairs.OrderByDescending(p => p.Checked))
            {
                // A pair that names a whole value ("Use.Type", "Tier#num") gives the field's own path ("Use", "Tier")
                string? named = p.Match switch
                {
                    "exact" => p.MutagenPath,
                    "whole value" => p.MutagenPath[..^(p.Part!.Length + (p.Part.StartsWith('#') ? 0 : 1))],
                    _ => null,
                };
                if (named != null && seen.Add((p.Sig, named)))
                    Insert(con, "PairNames", p.Sig, named, p.XEditPath, PairMatcher.ReadableName(PairMatcher.LastName(p.XEditPath), MutagenName(model, p, named)));
            }
            // A set of flags no pair names as a whole: the xEdit element all its flags sit in (PairMatcher.FlagParent)
            foreach (var g in pairs.Where(p => PairMatcher.FlagParent(p) != null).GroupBy(p => (p.Sig, Field: PairMatcher.FlagField(p))).OrderBy(g => g.Key.Sig, StringComparer.Ordinal).ThenBy(g => g.Key.Field, StringComparer.Ordinal))
            {
                var parents = g.Select(p => PairMatcher.FlagParent(p)!).Distinct(StringComparer.Ordinal).ToList();
                if (parents.Count == 1 && seen.Add((g.Key.Sig, g.Key.Field)))
                    Insert(con, "PairNames", g.Key.Sig, g.Key.Field, parents[0], PairMatcher.ReadableName(PairMatcher.LastName(parents[0]), MutagenName(model, g.First(), g.Key.Field)));
            }
            // An object or a list: the xEdit element its paired fields sit in (PairMatcher.ContainerNames, GAPS S98)
            foreach (var c in containers)
                if (seen.Add((c.Sig, c.Path)))
                    Insert(con, "PairNames", c.Sig, c.Path, c.XEditPath, PairMatcher.ReadableName(PairMatcher.LastName(c.XEditPath), c.Def.Name));
            var seenEv = new HashSet<(string, string, string)>();
            foreach (var e in evidence)
                if (seenEv.Add((e.Sig, e.Object, e.Field)))
                    Insert(con, "UnknownEvidence", e.Sig, e.Object, e.Field, e.RecordClass, e.XmlType, e.Subrecord, e.Kinds, e.RecordsWithField, e.RecordsNonDefault, e.Instances,
                        e.InstancesNonDefault, e.DistinctValues, e.RecordsNonDefaultOfficial, e.NeverSet, e.Priority, e.Note, e.Evidence, e.TopValues, e.Matched);

            Exec(con, @"CREATE INDEX ix_of_name ON ObjectFields(Name COLLATE NOCASE);
                CREATE INDEX ix_of_readable ON ObjectFields(Readable COLLATE NOCASE);
                CREATE INDEX ix_of_sub ON ObjectFields(Subrecord);
                CREATE INDEX ix_rt_sig ON RecordTypes(Sig);
                CREATE INDEX ix_pairs ON Pairs(Sig, MutagenPath);");
            Exec(con, FieldsView());
            tx.Commit();
        }
        File.Move(tmp, outDb, true);
    }



    static void Insert(SqliteConnection con, string table, params object?[] values)
    {
        using var c = con.CreateCommand();
        c.CommandText = $"INSERT INTO {table} VALUES ({string.Join(",", values.Select((_, i) => "$" + i))})";
        for (int i = 0; i < values.Length; i++)
            c.Parameters.AddWithValue("$" + i, values[i] switch { null => DBNull.Value, bool b => b ? 1 : 0, var v => v });
        c.ExecuteNonQuery();
    }

    static void Exec(SqliteConnection con, string sql)
    {
        using var c = con.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery();
    }
}
