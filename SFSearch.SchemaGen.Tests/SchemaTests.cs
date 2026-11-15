// Tests for milestone M1 (design/agent-first/PLAN.md). They build a schema of their own into a temp folder from the
// inputs kept in the repo, so they need no plugin, no game folder and no earlier build.
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using SFSearch.SchemaGen;
using Xunit;

namespace SFSearch.SchemaGen.Tests;

public sealed class SchemaFixture : IDisposable
{
    public string Repo { get; }
    public string GenDir { get; }
    /// <summary>The restored Mutagen.Bethesda.Starfield package the generator was compiled against.</summary>
    public string PackageDir { get; }
    /// <summary>Its record XML (loqui\Mutagen.Bethesda.Starfield\Records).</summary>
    public string RecordsDir { get; }
    public string WorkDir { get; }
    public string Db { get; }
    public string Report { get; }
    public BuildResult Result { get; }
    /// <summary>The Creation Kit's names, the data file in the repo (inputs\ck-names-&lt;CK version&gt;.tsv, to-do F30).</summary>
    public string CkNames { get; }
    SchemaDb? _loaded;

    public SchemaFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SFSearch.SchemaGen", "SFSearch.SchemaGen.csproj"))) dir = dir.Parent;
        Repo = dir?.FullName ?? throw new InvalidOperationException("The repo root (the folder holding SFSearch.SchemaGen) wasn't found above the test folder.");
        GenDir = Path.Combine(Repo, "SFSearch.SchemaGen");
        // The record XML of the Mutagen package the generator was compiled against, as restored
        PackageDir = Inputs.PackageDir(Reflect.AssemblyVersion().Split('+')[0]);
        RecordsDir = Path.Combine(PackageDir, Inputs.RecordsPath);
        if (!Directory.Exists(RecordsDir)) throw new InvalidOperationException($"There is no record XML at {RecordsDir}: restore the compiled Mutagen package.");
        WorkDir = Path.Combine(Path.GetTempPath(), "sfs-schemagen-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(WorkDir);
        Db = Path.Combine(WorkDir, "schema.db");
        Report = Path.Combine(WorkDir, "unmatched-pairs.tsv");
        CkNames = Directory.GetFiles(Path.Combine(GenDir, "inputs"), CkNamesFile.Prefix + "*" + CkNamesFile.Extension).Single();
        Result = Generator.Build(Options(Db, Report));
    }

    public BuildOptions Options(string db, string? report = null, string? annotations = null) => new()
    {
        PackageDir = PackageDir,
        PairsFile = Directory.GetFiles(Path.Combine(GenDir, "inputs"), "xedit-pairs-*.tsv").Single(),
        EvidenceFile = Directory.GetFiles(Path.Combine(GenDir, "inputs"), "unknown-fields-*.tsv").Single(),
        AnnotationsFile = annotations ?? Path.Combine(GenDir, "annotations.tsv"),
        OutDb = db, ReportFile = report, Quiet = true, CkNamesPath = CkNames,
    };

    public (int Exit, string Text) Describe(string what, string? filter = null, bool all = false)
    {
        var w = new StringWriter();
        var db = _loaded ??= SchemaDb.Load(Db);
        int exit = SchemaGen.Describe.Run(db, what, filter, all, w);
        return (exit, w.ToString().Replace("\r\n", "\n"));
    }

    public List<object?[]> Sql(string query, string? db = null)
    {
        using var con = new SqliteConnection($"Data Source={db ?? Db};Mode=ReadOnly;Pooling=False");
        con.Open();
        using var c = con.CreateCommand(); c.CommandText = query;
        using var r = c.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i)).ToArray());
        return rows;
    }

    public long Scalar(string query) => Convert.ToInt64(Sql(query)[0][0]);

    public void Dispose()
    {
        try { Directory.Delete(WorkDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class SchemaTests(SchemaFixture fx) : IClassFixture<SchemaFixture>
{
    // ---- the schema against the compiled records ----

    [Fact]
    public void NoSchemaFieldIsMissingFromTheCompiledRecords()
    {
        var r = fx.Result;
        Assert.True(r.Reflect.Missing.Count == 0, "Fields in the XML with no compiled property: " + string.Join(", ", r.Reflect.Missing.Take(30)));
        Assert.Equal(0, r.RecordRootMissing);
        Assert.Empty(r.Reflect.NoType);
        // The prototype checked 4,569 fields (record roots and condition functions). This checks every object.
        Assert.True(r.Reflect.FieldsChecked >= 4569, $"Only {r.Reflect.FieldsChecked} fields were checked.");
        Assert.True(r.RecordRootChecked >= 4000, $"Only {r.RecordRootChecked} record-root fields were checked.");
        Assert.Equal(207, r.Model.RecordTypes.Count);
    }

    [Fact]
    public void EveryEnumNamedInTheXmlIsFoundWithItsMembers()
    {
        Assert.Empty(fx.Result.Reflect.EnumsNotFound);
        Assert.True(fx.Result.Reflect.EnumMembers > 3000);
        Assert.Equal(4, fx.Scalar("SELECT count(*) FROM Enums WHERE Enum = 'HitBehavior'"));
    }

    // ---- the pairs ----

    [Fact]
    public void AtLeastNinetyPercentOfThePairsMatchAndTheRestSayWhy()
    {
        var p = fx.Result.Pairs;
        Assert.Equal(7260, p.Rows);
        // 7,257 paths: three pairs of SCEN's Player Dialogue action (WED0) name the same path as the FX action's once
        // WED0 is Sound (C218, fixes.17), which the pairs file names on the old names
        Assert.Equal(7257, p.DistinctPaths);
        Assert.Equal(230, p.Renamed);
        Assert.Equal(0, p.Reasons.Values.Sum());
        var renamed = fx.Result.PairRows.Single(x => x.Sig == "SCEN" && x.RenamedFrom == "Actions[*].WED0.Start");
        Assert.Equal(("Actions[*].Sound.StartEvent", "exact"), (renamed.MutagenPath, renamed.Match));
        var tnam = fx.Result.PairRows.Single(x => x.Sig == "AVMD" && x.RenamedFrom == "TNAM");
        Assert.Equal(("Name", "exact"), (tnam.MutagenPath, tnam.Match));
        int byName = p.Exact + p.Flag + p.Component + p.Bytes + p.Count + p.WholeValue;
        Assert.True(byName >= 0.90 * p.Rows, $"Only {byName} of {p.Rows} pairs match by name ({100.0 * byName / p.Rows:F1}%).");
        Assert.True(p.Matched >= 0.90 * p.Rows);
        Assert.Equal(p.Rows, p.Matched + p.NotData.Values.Sum() + p.Reasons.Values.Sum());
        foreach (var row in fx.Result.PairRows.Where(x => x.Match is null or "not data"))
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Reason), $"{row.Sig} {row.MutagenPath} has no reason.");
            Assert.False(string.IsNullOrWhiteSpace(row.Detail), $"{row.Sig} {row.MutagenPath} has no detail.");
        }
    }

    [Fact]
    public void PairsIntoMutagensOwnPropertiesNameTheWholeValueOrNoData()
    {
        PairRow Pair(string sig, string path) => fx.Result.PairRows.Single(p => p.Sig == sig && p.MutagenPath == path);

        // A property of the value's .NET type that is the value itself: the pair names the field
        var root = Pair("RACE", "SkeletalModel.Male.AnimationRoot.DataRelativePath.Path");
        Assert.Equal(("whole value", "DataRelativePath.Path", "NAM6"), (root.Match, root.Part, root.SchemaSubrecord));
        Assert.Equal("whole value", Pair("DOBJ", "Objects[*].Use.Type").Match);
        Assert.Equal("whole value", Pair("QUST", "Event.Type").Match);
        Assert.Equal(new object?[] { "Animation Root", "\\Skeleton Data\\Male Data\\NAM6 - Animation Root" },
            fx.Sql("SELECT Readable, XEdit FROM Fields WHERE Type = 'Race' AND Path = 'SkeletalModel.Male.AnimationRoot'")[0]);
        Assert.Equal("Event", fx.Sql("SELECT Readable FROM Fields WHERE Type = 'Quest' AND Path = 'Event'")[0][0]);

        // Mutagen's own bookkeeping, no data in the file: not counted as unmatched
        var versioning = Pair("WTHR", "AmbientColor.Versioning");
        Assert.Equal(("not data", "Mutagen's versioning marker"), (versioning.Match, versioning.Reason));
        Assert.Equal(20, fx.Result.Pairs.NotData["Mutagen's versioning marker"]);
        var isNull = Pair("DEBR", "Models[*].ModelFilename.IsNull");
        Assert.Equal(("not data", "inside Mutagen's asset link"), (isNull.Match, isNull.Reason));
        Assert.Contains("xEdit's field is in MODT", isNull.Detail);
        Assert.False(fx.Result.Pairs.Reasons.ContainsKey("Mutagen's versioning marker"));
        Assert.False(fx.Result.Pairs.Reasons.ContainsKey("part of a plain value"));
        Assert.Equal(1, fx.Scalar("SELECT count(*) FROM Pairs WHERE Sig = 'DEBR' AND Match = 'not data' AND MutagenPath = 'Models[*].ModelFilename.IsNull'"));

        // xEdit's NULL is no subrecord: such pairs aren't held against the schema's CTDA (the pairs of 2026-11-13, rerun
        // after the xecmp fixes, have none left: the import of fixes.14 dropped 4)
        var nul = fx.Result.PairRows.Where(p => p.Subrecord == "NULL" && p.Match != null).ToList();
        Assert.All(nul, p => Assert.False(PairMatcher.SubrecordComparable(p)));
        Assert.False(PairMatcher.SubrecordComparable(new PairRow { Subrecord = "NULL", MutagenPath = "Activities[*].Data", Match = "exact" }));
        Assert.DoesNotContain(File.ReadLines(fx.Report), l => l.Contains("\tNULL\t") && l.Contains("\tmatched, subrecords differ\t"));
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Subrecord = 'NULL'"));

        // xecmp's "#num" is an enum field's number: the whole field when the subrecord agrees, a coincidence when not
        var tier = Pair("RSPJ", "Tier#num");
        Assert.Equal(("whole value", "#num", "TNAM"), (tier.Match, tier.Part, tier.SchemaSubrecord));
        Assert.Equal("\\TNAM - Tier", fx.Sql("SELECT XEdit FROM Fields WHERE Type = 'ResearchProject' AND Path = 'Tier'")[0][0]);
        // The coincidences (an enum's number equal to a reflection header's) aren't made by xecmp any more
        Assert.False(fx.Result.Pairs.Reasons.ContainsKey("enum number in another subrecord"));

        // A path through a cycle (a script struct's members are script entries again) is found
        var member = Pair("QUST", "VirtualMachineAdapter.Scripts[*].Properties[*].Members[*].Properties[*].Name");
        Assert.Equal(("exact", "VMAD"), (member.Match, member.SchemaSubrecord));
        Assert.NotEmpty(fx.Result.Model.Resolve("Quest", "VirtualMachineAdapter.Scripts[*].Properties[*].Members[*].Properties[*].Flags").Found);

        // The properties Mutagen's code has and its XML doesn't are fields now (GAPS Part 2 row 8): RegionData's DataType
        // matches xEdit's RDAT "Type"; DSTDDataTypeState only notes how far DSTD's data went, so xEdit's DSTD Flags
        // paired with it by a coincidence of values (it is DestructionStage.Flags)
        var dataType = Pair("REGN", "Weather.DataType");
        Assert.Equal(("exact", "RDAT"), (dataType.Match, dataType.SchemaSubrecord));
        Assert.Equal("Type", fx.Sql("SELECT Readable FROM ObjectFields WHERE Object = 'RegionData' AND Path = 'DataType'")[0][0]);
        var dstd = Pair("GBFM", "Components[*].Destructible.Stages[*].DSTDDataTypeState");
        Assert.Equal(("not data", "Mutagen's data type state"), (dstd.Match, dstd.Reason));
        Assert.Contains("xEdit's field is a value in DSTD", dstd.Detail);
        Assert.Equal(1, fx.Result.Pairs.NotData["Mutagen's data type state"]);
        Assert.False(fx.Result.Pairs.Reasons.ContainsKey("property not in Mutagen's XML"));

        // Nothing is left unmatched since the pairs were made again on fixes.14 (2026-11-14) and row 8's properties
        // became fields. The 20 pairs made on C174's names are gone.
        Assert.Equal(0, fx.Result.PairRows.Count(p => p.Match == null && p.Reason == "unknown field since named or split"));
        Assert.Equal(0, fx.Result.PairRows.Count(p => p.Match == null));
        Assert.StartsWith("# xEdit pairs that match no field of Mutagen ", File.ReadLines(fx.Report).First());
        Assert.EndsWith(": 0 of 7260.", File.ReadLines(fx.Report).First());
    }

    [Fact]
    public void PropertiesMutagenAddsBeyondItsXmlAreDerivedFields()
    {
        // GAPS Part 2 row 8: Mutagen's Code-Generation.md says where each comes from; the schema applies those rules to
        // the package's XML, and the build holds them to the compiled getters both ways
        var docs = Directory.EnumerateFiles(fx.RecordsDir, "*.xml", SearchOption.AllDirectories).Select(f => XDocument.Load(f)).ToList();
        var objects = docs.SelectMany(d => d.Root!.Elements().Where(e => e.Name.LocalName == "Object")).ToList();
        static IEnumerable<XElement> Kids(XElement? e, string name) => e?.Elements().Where(x => x.Name.LocalName == name) ?? Enumerable.Empty<XElement>();
        XElement? FieldsOf(XElement e) => Kids(e, "Fields").FirstOrDefault();
        int majorFlag = objects.Count(o => o.Attribute("majorFlag")?.Value == "true");
        int versioning = objects.Count(o => Kids(FieldsOf(o), "Break").Any());
        int dataStates = objects.Sum(o => Kids(FieldsOf(o), "Data").Count(d => Kids(FieldsOf(d), "Break").Any() || Kids(FieldsOf(d), "Range").Any() || d.Attribute("optional")?.Value == "true"));
        Assert.Equal((42, 13, 10), (majorFlag, versioning, dataStates));
        Assert.Equal(majorFlag, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Name = 'MajorFlags' AND Source IS NOT NULL"));
        Assert.Equal(versioning, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Name = 'Versioning' AND Source IS NOT NULL AND Ord = 1"));
        Assert.Equal(dataStates, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Name LIKE '%DataTypeState' AND Source IS NOT NULL"));
        Assert.Equal(68L, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Source IS NOT NULL"));
        Assert.Empty(fx.Result.Reflect.DerivedMissing);
        Assert.Equal(68, fx.Result.Reflect.DerivedFound);

        // Each is a field of the right object, with the compiled enum and its members
        Assert.Equal(new object?[] { "Enum", "Weapon.MajorFlag", null, null }, fx.Sql("SELECT Kind, Enum, Subrecord, Offset FROM ObjectFields WHERE Object = 'Weapon' AND Path = 'MajorFlags'")[0]);
        Assert.True(fx.Scalar("SELECT count(*) FROM Enums WHERE Enum = 'Weapon.MajorFlag'") > 0);
        Assert.Equal(new object?[] { "Enum", "Static.DNAMDataType" }, fx.Sql("SELECT Kind, Enum FROM ObjectFields WHERE Object = 'Static' AND Path = 'DNAMDataTypeState'")[0]);
        Assert.Equal(new object?[] { "Enum", "MagicEffect.DATADataType" }, fx.Sql("SELECT Kind, Enum FROM ObjectFields WHERE Object = 'MagicEffect' AND Path = 'DATADataTypeState'")[0]);
        Assert.Contains("Absent", fx.Sql("SELECT Note FROM ObjectFields WHERE Path = 'DAT2DataTypeState'")[0][0] as string);
        Assert.Equal(new object?[] { "Bool", "Bool" }, fx.Sql("SELECT a.Kind, b.Kind FROM ObjectFields a, ObjectFields b WHERE a.Object = 'MajorRecord' AND a.Path = 'IsCompressed' AND b.Object = 'MajorRecord' AND b.Path = 'IsDeleted'")[0]);
        Assert.Equal(1L, fx.Scalar("SELECT count(*) FROM Fields WHERE Type = 'Npc' AND Path = 'IsCompressed'"));
        Assert.Equal(new object?[] { "Enum", "RegionData.RegionDataType", "RDAT", "0", 4L }, fx.Sql("SELECT Kind, Enum, Subrecord, LayoutOffset, Size FROM ObjectFields WHERE Object = 'RegionData' AND Path = 'DataType'")[0]);
        Assert.Equal(new object?[] { "Weather", 3L }, fx.Sql("SELECT Member, Value FROM Enums WHERE Enum = 'RegionData.RegionDataType' ORDER BY Value")[0]);
        // Read-only (each kind returns its own value), so the serializer never writes it; the others can be set
        Assert.Equal(new object?[] { "RegionData", "DataType" }, fx.Sql("SELECT Object, Path FROM ObjectFields WHERE ReadOnly = 1")[0]);
        Assert.Equal(1L, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE ReadOnly = 1"));

        // They take no bytes: the fields after a Versioning in a plain struct keep their offsets (Weather's colors:
        // Versioning first, then the colors one after another)
        var colors = fx.Sql("SELECT Path, Offset FROM ObjectFields WHERE Object = 'WeatherColor' ORDER BY Ord");
        Assert.Equal("Versioning", colors[0][0]); Assert.Null(colors[0][1]);
        Assert.Equal(0L, colors[1][1]);
    }

    [Fact]
    public void TheImportReadsOneFolderPerRecordType()
    {
        // The inventory of 2026-11-13 still held older exports beside the current ones: xedit-npc-… (padded to NPC_) beside
        // xedit-npc_-…, and samples beside full exports. All were read, and their old pairs showed as unmatched.
        string inv = Path.Combine(fx.WorkDir, "inventory");
        void Folder(string name, params string[] rows)
        {
            Directory.CreateDirectory(Path.Combine(inv, name));
            File.WriteAllText(Path.Combine(inv, name, "pairs.tsv"), "Subrecord\txEditPath\tMutagenPath\tMethod\tRecordsChecked\tAgree\tDisagree\tTranslation\tExample\n"
                + string.Concat(rows.Select(r => $"{r}\t\\{r} - x\t{r}\tvalue\t1\t1\t0\t\t\n")));
        }
        Folder("xedit-npc_-2026-11-08", "EDID", "FULL");
        Folder("xedit-npc-2026-11-08", "FLEE");
        Folder("xedit-cell-2026-11-08", "EDID");
        Folder("xedit-cell-sample-2026-11-08", "XCLL");
        Folder("xedit-navi-sample-2026-11-08", "NVPP");
        Folder("xedit-weap-2026-11-01", "DNAM");
        Folder("xedit-weap-2026-11-08", "WDMG");
        Directory.CreateDirectory(Path.Combine(inv, "xedit-big-types-2026-11-08")); // no pairs.tsv: not a pairs folder
        File.WriteAllText(Path.Combine(inv, "unknown-fields-r4-2026-11-10.csv"), "Rank,RecordType\n");

        var (kept, leftOut) = Inputs.PairFolders(inv);
        Assert.Equal(new[] { "CELL xedit-cell-2026-11-08", "NAVI xedit-navi-sample-2026-11-08", "NPC_ xedit-npc_-2026-11-08", "WEAP xedit-weap-2026-11-08" },
            kept.Select(k => $"{k.Sig} {Path.GetFileName(k.Dir)}"));
        Assert.Equal(new[] { "xedit-cell-sample-2026-11-08", "xedit-npc-2026-11-08", "xedit-weap-2026-11-01" }, leftOut.Select(l => Path.GetFileName(l.Dir)).Order(StringComparer.Ordinal));

        string pairs = Path.Combine(fx.WorkDir, "imported-pairs.tsv");
        var (n, folders, _, _) = Inputs.ImportSnapshot(inv, pairs, Path.Combine(inv, "unknown-fields-r4-2026-11-10.csv"), Path.Combine(fx.WorkDir, "imported-evidence.tsv"));
        Assert.Equal((5, 4), (n, folders));
        Assert.Equal(new[] { "CELL EDID", "NAVI NVPP", "NPC_ EDID", "NPC_ FULL", "WEAP WDMG" }, Inputs.ReadPairs(pairs).Select(p => $"{p.Sig} {p.Subrecord}"));
    }

    [Fact]
    public void TheReportListsEveryUnmatchedPair()
    {
        var lines = File.ReadAllLines(fx.Report).Where(l => !l.StartsWith('#') && !l.StartsWith("Sig\t")).ToList();
        int unmatched = fx.Result.PairRows.Count(p => p.Match == null);
        Assert.Equal(unmatched, lines.Count(l => !l.Contains("\tmatched by subrecord only\t") && !l.Contains("\tmatched, subrecords differ\t") && !l.Contains("\tnot data: ")));
        Assert.Equal(fx.Result.Pairs.NotData.Values.Sum(), lines.Count(l => l.Contains("\tnot data: ")));
        Assert.StartsWith($"# xEdit pairs that match no field of Mutagen {fx.Result.Model.Meta["MutagenVersion"]} (", File.ReadLines(fx.Report).First());
        Assert.EndsWith($": {unmatched} of {fx.Result.Pairs.Rows}.", File.ReadLines(fx.Report).First());
        Assert.Equal(unmatched, fx.Scalar("SELECT count(*) FROM Pairs WHERE Match IS NULL AND Reason IS NOT NULL"));
        Assert.Equal(fx.Result.Pairs.Rows, fx.Scalar("SELECT count(*) FROM Pairs"));
    }

    [Fact]
    public void HalfFloatsHaveTwoBytes()
    {
        // fixes.17 (C221): a half-precision float is a Half of 2 bytes; AreaLight.Unused was a UInt16 at 14
        Assert.Equal(new object?[] { "Half", 14L, 2L }, fx.Sql("SELECT Kind, Offset, Size FROM ObjectFields WHERE Object = 'AreaLight' AND Path = 'Unused'")[0]);
        Assert.Equal(4L, fx.Sql("SELECT count(*) FROM ObjectFields WHERE Object = 'TraversalReference' AND Kind = 'Half' AND Size = 2")[0][0]);
        Assert.Equal("after:Traversal", fx.Sql("SELECT LayoutOffset FROM ObjectFields WHERE Object = 'TraversalReference' AND Path = 'Unknown2'")[0][0]);
    }

    [Fact]
    public void SubrecordSignaturesReachNestedObjects()
    {
        var r = fx.Result;
        // The prototype had a signature on 19,293 of 52,370 leaf fields (37%)
        Assert.True(r.LeavesWithSubrecord >= 0.95 * r.Leaves, $"{r.LeavesWithSubrecord} of {r.Leaves} leaf fields have a signature.");
        // xEdit's own subrecord for each matched pair is an independent check
        Assert.True(r.Pairs.SubrecordAgree >= 0.98 * r.Pairs.SubrecordCompared, $"xEdit agrees on {r.Pairs.SubrecordAgree} of {r.Pairs.SubrecordCompared}.");
        // A struct inside a data block: the seven sounds of WAUD, 40 bytes each, then the sound level
        var rows = fx.Sql("SELECT Path, Subrecord, Offset FROM Fields WHERE Type = 'Weapon' AND Path IN ('PrimedExplosive.StopEvent', 'SoundLevel', 'PickupSound.StartEvent') ORDER BY Path");
        Assert.Equal(new object?[] { "PickupSound.StartEvent", "PUSH", 0L }, rows[0]);
        Assert.Equal(new object?[] { "PrimedExplosive.StopEvent", "WAUD", 56L }, rows[1]);
        Assert.Equal(new object?[] { "SoundLevel", "WAUD", 280L }, rows[2]);
    }

    // ---- markers (GAPS S29) ----

    [Fact]
    public void EveryMarkerTheXmlDeclaresIsInTheSchema()
    {
        // Counted in the XML itself, on any element: an object or a field
        XNamespace ns = "http://tempuri.org/LoquiSource.xsd";
        var docs = Directory.EnumerateFiles(fx.RecordsDir, "*.xml", SearchOption.AllDirectories).Select(f => XDocument.Load(f)).ToList();
        int xmlEnd = docs.Sum(d => d.Descendants().Count(e => e.Attribute("endMarkerType") != null));
        int xmlStart = docs.Sum(d => d.Descendants().Count(e => e.Attribute("markerType") != null));
        int xmlContains = docs.Sum(d => d.Descendants(ns + "Object").Sum(o => o.Elements(ns + "AdditionalContainedRecordType").Select(x => x.Value.Trim()).Distinct().Count()));
        Assert.True(xmlEnd >= 10 && xmlStart >= 10, $"the XML has {xmlEnd} end markers and {xmlStart} start markers");

        long end = fx.Scalar("SELECT count(*) FROM Objects WHERE EndMarker IS NOT NULL") + fx.Scalar("SELECT count(*) FROM ObjectFields WHERE EndMarker IS NOT NULL");
        long start = fx.Scalar("SELECT count(*) FROM Objects WHERE Marker IS NOT NULL") + fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Marker IS NOT NULL");
        long contains = fx.Sql("SELECT Contains FROM Objects WHERE Contains IS NOT NULL").Sum(r => (long)((string)r[0]!).Split(',').Length);
        Assert.Equal(xmlEnd, end);
        Assert.Equal(xmlStart, start);
        Assert.Equal(xmlContains, contains);
        Assert.Equal(xmlEnd, fx.Result.EndMarkers);
        Assert.Equal(xmlStart, fx.Result.Markers);

        // The component's BFCE, a start and end marker on one object, a field's marker, a condition's other subrecords
        Assert.Equal("BFCE", fx.Sql("SELECT EndMarker FROM Objects WHERE Name = 'AComponent'")[0][0]);
        Assert.Equal(new object?[] { "HNAM", "HNAM" }, fx.Sql("SELECT Marker, EndMarker FROM Objects WHERE Name = 'HeadTracking'")[0]);
        Assert.Equal("POBA", fx.Sql("SELECT Marker FROM ObjectFields WHERE Object = 'Package' AND Path = 'OnBegin'")[0][0]);
        Assert.Equal("CITC,CIS1,CIS2", fx.Sql("SELECT Contains FROM Objects WHERE Name = 'Condition'")[0][0]);

        // Read back, a component inherits its base's marker
        var m = SchemaDb.Load(fx.Db).Model;
        Assert.Equal("BFCE", m.EndMarkerOf("ModelComponent"));
        Assert.Null(m.EndMarkerOf("Weapon"));

        // C183 (fixes.14): a scene phase's completion conditions sit between two NEXT; a condition's two texts are CIS1, CIS2
        Assert.Equal(new object?[] { "NEXT", "NEXT" }, fx.Sql("SELECT Marker, EndMarker FROM ObjectFields WHERE Object = 'ScenePhase' AND Path = 'CompletionConditions'")[0]);
        Assert.Equal(new object?[] { "CIS1", "CIS2" }, fx.Sql("SELECT a.Subrecord, b.Subrecord FROM ObjectFields a JOIN ObjectFields b ON b.Object = a.Object AND b.Path = 'SecondUnusedStringParameter' WHERE a.Object = 'GetWantBlockingConditionData' AND a.Path = 'FirstUnusedStringParameter'")[0]);
        Assert.Equal(0L, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Path IN ('FirstUnusedStringParameter', 'SecondUnusedStringParameter') AND Subrecord IS NULL"));
    }

    // ---- Mutagen's layout attributes and hand-read layouts (C186, fixes.14; GAPS Part 2 row 30) ----

    [Fact]
    public void LayoutAttributesAndHandReadLayoutsAreInTheSchema()
    {
        // Every layout attribute the XML has is a column of its field or object
        var docs = Directory.EnumerateFiles(fx.RecordsDir, "*.xml", SearchOption.AllDirectories).Select(f => XDocument.Load(f)).ToList();
        foreach (var (attr, column) in new[] { ("layoutOffset", "LayoutOffset"), ("layoutBits", "LayoutBits"), ("layoutSubrecord", "LayoutSubrecord"), ("layoutEncoding", "LayoutEncoding"),
                     ("layoutDroppedBits", "LayoutDroppedBits"), ("layoutPresentWhen", "LayoutPresentWhen"), ("layoutGroup", "LayoutGroup"), ("layoutMap", "LayoutMap") })
        {
            int xml = docs.Sum(d => d.Descendants().Count(e => e.Name.LocalName != "Object" && e.Attribute(attr) != null));
            Assert.True(xml > 0, $"the XML has no {attr}");
            // RegionData.DataType, a property the XML doesn't declare, is placed by the schema itself (GAPS Part 2 row 8)
            Assert.Equal(xml, fx.Scalar($"SELECT count(*) FROM ObjectFields WHERE {column} IS NOT NULL AND Path NOT LIKE '%[*]' AND Source IS NULL"));
        }
        foreach (var (attr, column) in new[] { ("selectorValue", "SelectorValue"), ("conditionFunction", "ConditionFunction") })
            Assert.Equal(docs.Sum(d => d.Descendants().Count(e => e.Name.LocalName == "Object" && e.Attribute(attr) != null)), fx.Scalar($"SELECT count(*) FROM Objects WHERE {column} IS NOT NULL"));
        Assert.Equal(612L, fx.Scalar("SELECT count(*) FROM Objects WHERE ConditionFunction IS NOT NULL"));
        // C200 (fixes.16, GAPS Part 2 row 33): UnknownConditionData is the catch-all ("*", kept as text) and places its Function itself
        Assert.Equal("*", fx.Sql("SELECT ConditionFunction FROM Objects WHERE Name = 'UnknownConditionData'")[0][0]);
        Assert.Equal(new object?[] { "8", "uint16", 2L }, fx.Sql("SELECT LayoutOffset, LayoutEncoding, Size FROM ObjectFields WHERE Object = 'UnknownConditionData' AND Path = 'Function'")[0]);

        // A condition's CTDA: the compare operator and the flags share byte 0; the data's place; the selector of its kind
        Assert.Equal(new object?[] { "0", "5-7" }, fx.Sql("SELECT LayoutOffset, LayoutBits FROM ObjectFields WHERE Object = 'Condition' AND Path = 'CompareOperator'")[0]);
        Assert.Equal(new object?[] { "0", "0-4" }, fx.Sql("SELECT LayoutOffset, LayoutBits FROM ObjectFields WHERE Object = 'Condition' AND Path = 'Flags'")[0]);
        Assert.Equal("12", fx.Sql("SELECT LayoutOffset FROM ObjectFields WHERE Object = 'Condition' AND Path = 'Data'")[0][0]);
        Assert.Equal("field:Flags:bit=2", fx.Sql("SELECT SelectedBy FROM Objects WHERE Name = 'Condition'")[0][0]);
        Assert.Equal("576", fx.Sql("SELECT ConditionFunction FROM Objects WHERE Name = 'GetEventDataConditionData'")[0][0]);
        Assert.Contains("Mutagen's layout: offset 0, bits 5-7", (string)fx.Sql("SELECT Note FROM ObjectFields WHERE Object = 'Condition' AND Path = 'CompareOperator'")[0][0]!);

        // C196 (fixes.15, GAPS Part 2 row 33): the places the XML's comment gave only in words are attributes now: the
        // comparison value at 4, the function's number at 8 (on the FunctionParse step, 2 bytes), Unknown2 at 10; a
        // ModifyValue perk effect's Modification is the entry point function's number in DATA, mapped
        Assert.Equal(new object?[] { "4", "4" }, fx.Sql("SELECT a.LayoutOffset, b.LayoutOffset FROM ObjectFields a, ObjectFields b WHERE a.Object = 'ConditionFloat' AND a.Path = 'ComparisonValue' AND b.Object = 'ConditionGlobal' AND b.Path = 'ComparisonValue'")[0]);
        Assert.Equal(new object?[] { "CustomLogic", "8", "uint16" }, fx.Sql("SELECT Kind, LayoutOffset, LayoutEncoding FROM ObjectFields WHERE Object = 'Condition' AND Path = 'FunctionParse'")[0]);
        Assert.Equal("10", fx.Sql("SELECT LayoutOffset FROM ObjectFields WHERE Object = 'Condition' AND Path = 'Unknown2'")[0][0]);
        foreach (var o in new[] { "PerkEntryPointModifyValue", "PerkEntryPointModifyValues" })
            Assert.Equal(new object?[] { "DATA", "1", "1:Set,2:Add,3:Multiply" }, fx.Sql($"SELECT LayoutSubrecord, LayoutOffset, LayoutMap FROM ObjectFields WHERE Object = '{o}' AND Path = 'Modification'")[0]);
        Assert.Equal("5:AddAVMult,12:SetToAVMult,13:MultiplyAVMult,14:MultiplyOnePlusAVMult", fx.Sql("SELECT LayoutMap FROM ObjectFields WHERE Object = 'PerkEntryPointModifyActorValue' AND Path = 'Modification'")[0][0]);
        Assert.Contains("not stored as such: the number there gives it (1 Set, 2 Add, 3 Multiply)", (string)fx.Sql("SELECT Note FROM ObjectFields WHERE Object = 'PerkEntryPointModifyValue' AND Path = 'Modification'")[0][0]!);
        Assert.Equal(new Dictionary<long, string> { [1] = "Set", [2] = "Add", [3] = "Multiply" },
            SchemaDb.Load(fx.Db).Model.Objects["PerkEntryPointModifyValue"].Fields.Single(f => f.Name == "Modification").LayoutMapEntries);

        // HandReadLayouts.xml: each Record, AnyRecord and Define, read back by SchemaDb
        var kinds = fx.Sql("SELECT Kind, count(*) FROM HandLayouts GROUP BY Kind ORDER BY Kind").ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
        Assert.Equal(new Dictionary<string, long> { ["AnyRecord"] = 1, ["Define"] = 8, ["Record"] = 20 }, kinds);
        var m = SchemaDb.Load(fx.Db).Model;
        Assert.Equal(29, m.HandLayouts.Count);
        var perk = m.HandLayouts.Single(h => h.Kind == "Record" && h.Sig == "PERK");
        Assert.Equal("Perk", perk.Object);
        Assert.Equal(2, perk.Element.Descendants("Segments").Count());
        foreach (var sig in new[] { "OMOD", "WEAP", "WTHR", "RACE", "PACK", "FSTS", "REGN", "PHZD" }) Assert.Contains(m.HandLayouts, h => h.Kind == "Record" && h.Sig == sig);
        Assert.Contains(m.HandLayouts, h => h.Kind == "AnyRecord" && h.Sig == "VMAD");

        // A layout that names an element the walk doesn't know, or a class the schema doesn't have, fails the build
        string bad = Path.Combine(fx.WorkDir, "bad-layouts.xml");
        File.WriteAllText(bad, "<HandReadLayouts game=\"Starfield\" version=\"1\"><Record sig=\"PERK\" object=\"Perk\"><Sub sig=\"PRKE\"><U24 field=\"Rank\" /></Sub></Record><Record sig=\"XXXX\" object=\"NoSuchClass\" /></HandReadLayouts>");
        var e = Assert.Throws<SchemaException>(() => HandLayouts.Load(bad, fx.Result.Model));
        Assert.Contains("<U24>", e.Message);
        Assert.Contains("NoSuchClass", e.Message);
    }

    [Fact]
    public void DescribeGivesAnObjectsMarkersOnOneLine()
    {
        var (exit, text) = fx.Describe("AComponent");
        Assert.Equal(0, exit);
        Assert.Contains("\nEnds with an empty BFCE subrecord: in a list, it closes each item.\n", text);
        Assert.Contains("\nEnds with an empty BFCE subrecord (from AComponent): in a list, it closes each item.\n", fx.Describe("ModelComponent").Text);
        Assert.Contains("\nStarts with an empty HNAM subrecord and ends with an empty HNAM subrecord.\n", fx.Describe("HeadTracking").Text);
        Assert.Contains("| OnBegin |  | Object PackageEvent |  | may be absent; after an empty POBA marker subrecord |", fx.Describe("PACK").Text);
        Assert.Contains("Also holds CITC, CIS1, CIS2, read by hand-written Mutagen code", fx.Describe("Condition").Text);
        Assert.DoesNotContain("subrecord: in a list", fx.Describe("WEAP").Text);
    }

    // ---- strings tables, counts, text forms, hand-read objects (GAPS S35, S36, S37) ----

    List<XDocument> XmlDocs() => Directory.EnumerateFiles(fx.RecordsDir, "*.xml", SearchOption.AllDirectories)
        .Select(f => XDocument.Load(f)).ToList();

    /// <summary>Rows that stand for one XML element: a gendered value's female row repeats its male row.</summary>
    const string OneRowPerElement = "Path NOT LIKE '%.Female' AND Path NOT LIKE '%.Female.%'";

    [Fact]
    public void EveryTranslatedTextNamesItsStringsTable()
    {
        // Counted in the XML: Mutagen's StringType reads translated as Normal (.strings), DL (.dlstrings) or IL (.ilstrings)
        var xml = XmlDocs().SelectMany(d => d.Descendants()).Where(e => e.Name.LocalName == "String" && e.Attribute("translated") != null)
            .GroupBy(e => e.Attribute("translated")!.Value).ToDictionary(g => g.Key, g => (long)g.Count());
        Assert.Equal(new[] { "DL", "IL", "Normal" }, xml.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (table, n) in xml)
            Assert.Equal(n, fx.Scalar($"SELECT count(*) FROM ObjectFields WHERE StringsTable = '{table}' AND {OneRowPerElement}"));
        // A translated field is a text with a table; the Float with translated="" (SurfacePatternConfig.Common) is neither
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Translated <> (StringsTable IS NOT NULL) OR (StringsTable IS NOT NULL AND Kind <> 'String')"));
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Object = 'SurfacePatternRarityConfig' AND Translated"));
        Assert.Equal(xml.Sum(x => x.Value), fx.Result.StringsTables.Sum(x => x.Value) - fx.Scalar($"SELECT count(*) FROM ObjectFields WHERE StringsTable IS NOT NULL AND NOT ({OneRowPerElement})"));
        Assert.Equal("Normal", fx.Sql("SELECT StringsTable FROM ObjectFields WHERE Object = 'Weapon' AND Path = 'Name'")[0][0]);
        Assert.Equal("DL", fx.Sql("SELECT StringsTable FROM ObjectFields WHERE Object = 'ConstructibleObject' AND Path = 'Description'")[0][0]);
        Assert.Equal("IL", fx.Sql("SELECT StringsTable FROM ObjectFields WHERE StringsTable = 'IL'")[0][0]);
        // Read back, and said by describe
        var m = SchemaDb.Load(fx.Db).Model;
        Assert.Equal("DL", m.Objects["ConstructibleObject"].Fields.Single(f => f.Path == "Description").StringsTable);
        Assert.Contains("- Translated text, from the plugin's .dlstrings file in a localized plugin.", fx.Describe("ConstructibleObject.Description").Text);
        Assert.Contains("| Name | FULL | String | Name | may be absent; translated text (.strings) |", fx.Describe("WEAP").Text);
    }

    [Fact]
    public void EveryListsCountLengthIsInTheSchema()
    {
        // A list's count: counterLength, or 4 for a list counted by a counter subrecord that names no length (the rule of
        // Mutagen's PluginListBinaryTranslationGeneration.Load)
        var lists = XmlDocs().SelectMany(d => d.Descendants()).Where(e => e.Name.LocalName is "List" or "RefList" or "Array"
            && (e.Attribute("counterLength") != null || e.Attribute("counterRecType") != null)).ToList();
        Assert.True(lists.Count > 100, $"the XML has {lists.Count} counted lists");
        Assert.Equal(lists.Count, fx.Scalar($"SELECT count(*) FROM ObjectFields WHERE CounterLength IS NOT NULL AND {OneRowPerElement}"));
        Assert.Equal(lists.Count, fx.Result.CountedLists);
        foreach (int len in new[] { 1, 2, 4, 8 })
        {
            long xml = lists.Count(e => (e.Attribute("counterLength") is { } c ? int.Parse(c.Value) : 4) == len);
            Assert.Equal(xml, fx.Scalar($"SELECT count(*) FROM ObjectFields WHERE CounterLength = {len} AND {OneRowPerElement}"));
        }
        Assert.Equal(4L, fx.Sql("SELECT CounterLength FROM ObjectFields WHERE Object = 'PlacedObject' AND Path = 'SnapLinks'")[0][0]);
        Assert.Equal(4L, fx.Sql("SELECT CounterLength FROM ObjectFields WHERE Object = 'SnapLink' AND Path = 'Links'")[0][0]);
        Assert.Equal(2L, fx.Sql("SELECT CounterLength FROM ObjectFields WHERE Object = 'PlanetModelComponentXMPM' AND Path = 'UnknownStrings'")[0][0]);
        Assert.Equal(4L, fx.Sql("SELECT CounterLength FROM ObjectFields WHERE Object = 'Weapon' AND Path = 'Keywords'")[0][0]); // counted by KSIZ
        Assert.Contains("- Its count: 4 bytes before the entries.", fx.Describe("PlacedObject.SnapLinks").Text);
        Assert.Contains("- Its count is a 4-byte counter subrecord.", fx.Describe("Weapon.Keywords").Text);
    }

    [Fact]
    public void EveryTextSaysHowItIsStored()
    {
        // binaryType on a String or AssetLink; Mutagen's StringType reads NullTerminate when there is none
        var texts = XmlDocs().SelectMany(d => d.Descendants()).Where(e => e.Name.LocalName is "String" or "AssetLink"
            && e.Parent?.Name.LocalName != "Key").ToList();
        var byForm = texts.GroupBy(e => e.Attribute("binaryType")?.Value ?? "NullTerminate").ToDictionary(g => g.Key, g => (long)g.Count());
        Assert.True(byForm.Count >= 7, string.Join(", ", byForm.Keys));
        foreach (var (form, n) in byForm)
            Assert.Equal(n, fx.Scalar($"SELECT count(*) FROM ObjectFields WHERE TextForm = '{form}' AND {OneRowPerElement} AND Object <> 'MajorRecord'")); // its EDID is written by hand
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE (TextForm IS NOT NULL) <> (Kind IN ('String', 'AssetLink'))"));
        Assert.Equal("PrependLengthUShort", fx.Sql("SELECT TextForm FROM ObjectFields WHERE Object = 'PlanetModelComponentXMPMSubItem' AND Path = 'ResourceID'")[0][0]);
        Assert.Equal("NullTerminate", fx.Sql("SELECT TextForm FROM ObjectFields WHERE Object = 'PlanetModelComponentXMPMSubItem' AND Path = 'Extension'")[0][0]);
        Assert.Equal("PrependLengthWithNullIfContent", fx.Sql("SELECT TextForm FROM ObjectFields WHERE Object = 'PlanetDetails' AND Path = 'SpectralClass'")[0][0]);
        Assert.Contains("| ResourceID |  | String @0 | Resource:ID | stored with a 2-byte length before it |", fx.Describe("PlanetModelComponentXMPMSubItem").Text);
    }

    [Fact]
    public void HandReadFieldsHaveNoOffsetAndTheirObjectsNameTheCode()
    {
        // No field Mutagen's generated reader skips (binary="NoGeneration") has an offset
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM ObjectFields f WHERE f.Offset IS NOT NULL AND f.Note LIKE 'read by hand-written Mutagen code%' AND f.Kind <> 'CustomLogic'"));
        // A condition's CTDA: the XML-order offsets (CompareOperator 0, Flags 4, Unused1 (now Unknown1) 5, Data.RunOnType 0, …) are gone
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Object IN ('Condition', 'ConditionFloat', 'ConditionGlobal', 'ConditionData') AND Offset IS NOT NULL"));
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM Fields WHERE Type = 'ConstructibleObject' AND Path LIKE 'Conditions[*].%' AND Offset IS NOT NULL"));
        // A perk effect's PRKE: Rank and Priority (the file has the kind of effect at 0, the rank at 1, the priority at 2)
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Object = 'APerkEffect' AND Offset IS NOT NULL"));
        // An object template's include sits after the keywords in OBTS, where the hand-written reader puts it
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM Fields WHERE Type = 'Weapon' AND Path LIKE 'ObjectTemplates[*].Includes[*].%' AND Offset IS NOT NULL"));
        // Where the generated reader reads, offsets stay: a custom field with a byteLength takes it (Book's DNAM)
        Assert.Equal(5L, fx.Sql("SELECT Offset FROM ObjectFields WHERE Object = 'Book' AND Path = 'TextOffsetX'")[0][0]);
        Assert.Equal(4L, fx.Sql("SELECT Offset FROM ObjectFields WHERE Object = 'SnapLink' AND Path = 'Links'")[0][0]);

        // Every field with binary="Custom" (and every CustomLogic step) inside an object names its compiled reader method
        int custom = 0, found = 0;
        var model = SchemaDb.Load(fx.Db).Model;
        foreach (var d in XmlDocs())
            foreach (var o in d.Descendants().Where(e => e.Name.LocalName == "Object"))
                foreach (var f in o.Descendants().Where(e => e.Attribute("name") != null && (e.Attribute("binary")?.Value == "Custom" || (e.Name.LocalName == "CustomLogic" && e.Attribute("binary") == null))))
                {
                    if (f.Ancestors().Any(a => a.Name.LocalName is "List" or "RefList" or "Array" or "Dict" or "Gendered")) continue; // an item, not a field
                    custom++;
                    if (model.Objects[o.Attribute("name")!.Value].HandRead?.Contains($"BinaryCreateTranslation.FillBinary{f.Attribute("name")!.Value}Custom") == true) found++;
                }
        Assert.True(custom > 60, $"{custom} custom fields in the XML");
        Assert.Equal(custom, found);
        Assert.Equal(fx.Result.HandReadObjects, fx.Scalar("SELECT count(*) FROM Objects WHERE HandRead IS NOT NULL"));
        Assert.Equal("ConditionBinaryCreateTranslation.FillBinaryFlagsCustom; ConditionBinaryCreateTranslation.FillBinaryFunctionParseCustom",
            fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'Condition'")[0][0]);
        Assert.Equal("via PerkRank.Effects (PerkRankBinaryCreateTranslation.FillBinaryEffectsCustom)", fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'APerkEffect'")[0][0]);
        Assert.Equal("via Condition.Data", fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'GetGlobalValueConditionData'")[0][0]);
        Assert.Null(fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'Weapon'")[0][0]); // the record header's flags move nothing

        // describe says it on the object, and for a field gives no offset and names the code
        Assert.Contains("\nRead in part by hand-written Mutagen code: ConditionBinaryCreateTranslation.FillBinaryFlagsCustom; ConditionBinaryCreateTranslation.FillBinaryFunctionParseCustom.", fx.Describe("Condition").Text);
        Assert.Contains("| CompareOperator | CTDA | Enum CompareOperator, 4 B |  | read by hand-written Mutagen code; Mutagen's layout: offset 0, bits 5-7 |", fx.Describe("Condition").Text);
        Assert.Contains("entered through PerkRank.Effects", fx.Describe("APerkEffect").Text);
        Assert.Contains("- File address: CTDA (no offset: Mutagen's hand-written code places it: ConditionFloatBinaryCreateTranslation.CustomBinaryEndImport; ConditionBinaryCreateTranslation.FillBinaryFlagsCustom",
            fx.Describe("ConditionFloat.ComparisonValue").Text);
        Assert.DoesNotContain("Read in part by hand-written", fx.Describe("WEAP").Text);
    }

    // ---- stored once, worked out on request ----

    [Fact]
    public void ObjectsAndTheirFieldsAreStoredOnce()
    {
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM (SELECT Object, Path FROM ObjectFields GROUP BY Object, Path HAVING count(*) > 1)"));
        Assert.Equal(fx.Result.ObjectFields, fx.Scalar("SELECT count(*) FROM ObjectFields"));
        // The prototype spelled 73,687 rows out and still left shared objects as SameAs markers
        Assert.True(fx.Result.ObjectFields < 10000);
        Assert.Equal(fx.Result.Objects, fx.Scalar("SELECT count(*) FROM Objects"));
    }

    [Fact]
    public void TheFieldsViewGivesTheSameRowsAsTheFunction()
    {
        var db = SchemaDb.Load(fx.Db);
        var view = fx.Sql("SELECT Type, Path, Subrecord, Offset, Enum, Object, Ord FROM Fields ORDER BY Type, Sort");
        var byType = view.GroupBy(r => (string)r[0]!).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Equal(207, byType.Count);
        long total = 0;
        foreach (var t in db.Model.RecordTypes)
        {
            var mine = db.Model.Expand(t.Name).ToList();
            var theirs = byType[t.Name];
            Assert.True(mine.Count == theirs.Count, $"{t.Name}: the function gives {mine.Count} rows, the view {theirs.Count}.");
            for (int i = 0; i < mine.Count; i++)
            {
                var (m, v) = (mine[i], theirs[i]);
                Assert.True(m.Path == (string)v[1]! && m.Subrecord == (string?)v[2] && m.Offset == (v[3] == null ? null : (int)(long)v[3]!) && m.Enum == (string?)v[4]
                    && m.Def.Object == (string)v[5]! && m.Def.Ord == (int)(long)v[6]!,
                    $"{t.Name} row {i}: function {m.Path} {m.Subrecord} @{m.Offset} {m.Enum}, view {v[1]} {v[2]} @{v[3]} {v[4]}");
            }
            total += mine.Count;
        }
        Assert.Equal(fx.Result.Paths, total);
    }

    [Fact]
    public void UnknownFieldsAreCountedPerRealField()
    {
        // One row per unknown field of an object, however many paths lead to it
        Assert.Equal(fx.Result.UnknownFields, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Unknown AND Path = Name"));
        long perType = fx.Scalar("SELECT Unknown FROM RecordTypes WHERE Type = 'Weapon'");
        long distinct = fx.Scalar("SELECT count(*) FROM (SELECT DISTINCT Object, Name FROM Fields WHERE Type = 'Weapon' AND Unknown AND Kind NOT IN ('Break', 'Marker'))");
        long paths = fx.Scalar("SELECT count(*) FROM Fields WHERE Type = 'Weapon' AND Unknown");
        Assert.True(perType <= distinct && perType > 0, $"Weapon: {perType} unknown fields, {distinct} distinct, {paths} paths.");
        Assert.True(perType < paths, "The count per real field must be smaller than the count of paths.");
        Assert.True(fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Object = 'Weapon' AND Name IN ('WAMM', 'WRUM') AND Unknown") == 2);
    }

    // ---- describe ----

    [Fact]
    public void DescribeWeapEqualsTheGolden()
    {
        string golden = Path.Combine(fx.Repo, "SFSearch.SchemaGen.Tests", "golden", "describe-WEAP.md");
        // With the Creation Kit's names, which every build has (GAPS S41): the weapon's sound fields carry them
        var (exit, text) = fx.Describe("WEAP");
        Assert.Equal(0, exit);
        if (Environment.GetEnvironmentVariable("SFS_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, text);
        }
        Assert.True(File.Exists(golden), $"{golden} is missing. Run the tests once with SFS_UPDATE_GOLDEN=1 and look at the file before committing it.");
        string want = File.ReadAllText(golden).Replace("\r\n", "\n");
        if (want != text)
        {
            var (a, b) = (want.Split('\n'), text.Split('\n'));
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            Assert.Fail($"describe WEAP differs from the golden at line {i + 1}.\n  golden: {(i < a.Length ? a[i] : "(end)")}\n  now:    {(i < b.Length ? b[i] : "(end)")}\nIf the change is meant, rerun with SFS_UPDATE_GOLDEN=1.");
        }
    }

    [Theory]
    [InlineData("Base Damage")]
    [InlineData("base damage")]
    [InlineData("BASE DAMAGE")]
    public void DescribeFindsAttackDamageWhenAskedForBaseDamage(string asked)
    {
        var (exit, text) = fx.Describe(asked);
        Assert.Equal(0, exit);
        Assert.Contains("| Weapon.AttackDamage | WDMG | Float, 4 B @0 | Base Damage | WEAP |", text);
    }

    [Fact]
    public void DescribeTakesARecordTypeBySignatureOrMutagenNameInAnyCase()
    {
        string want = fx.Describe("WEAP").Text;
        foreach (var asked in new[] { "weap", "Weap", "Weapon", "weapon", "WEAPON" })
            Assert.Equal(want, fx.Describe(asked).Text);
        Assert.StartsWith("# Weapon (WEAP)\n", want);
        Assert.Contains("| AttackDamage | WDMG | Float, 4 B @0 | Base Damage |", want);
    }

    [Fact]
    public void DescribeTakesAFieldByMutagenPath()
    {
        foreach (var asked in new[] { "Weapon.AttackDamage", "weapon.attackdamage", "WEAP.AttackDamage" })
        {
            var (exit, text) = fx.Describe(asked);
            Assert.Equal(0, exit);
            Assert.Contains("- Mutagen path: Weapon.AttackDamage\n", text);
            Assert.Contains("- File address: WEAP/WDMG, offset 0, 4 bytes\n", text);
            Assert.Contains("- Readable name: Base Damage (xEdit's name", text);
        }
        // A path into a shared object, and a part of a field
        Assert.Contains("- File address: WEAP/DAMA, offset 0, 4 bytes (inside a list: per item)", fx.Describe("Weapon.DamageTypes[*].DamageType").Text);
        Assert.Contains("One part (X) of the P3Float value below.", fx.Describe("Weapon.ObjectBounds.First.X").Text);
    }

    [Fact]
    public void DescribeTakesAFileAddress()
    {
        foreach (var asked in new[] { "WEAP/WDMG", "weap/wdmg", "Weapon/WDMG" })
        {
            var (exit, text) = fx.Describe(asked);
            Assert.Equal(0, exit);
            Assert.StartsWith("# Weapon (WEAP), subrecord WDMG: 17 fields\n", text);
            Assert.Contains("| AttackDamage | WDMG | Float, 4 B @0 | Base Damage |", text);
        }
        var at = fx.Describe("WEAP/WDMG@12");
        Assert.Contains(": 1 field\n", at.Text);
        Assert.Contains("| OutOfRangeDamageMult | WDMG | Float, 4 B @12 |", at.Text);
        Assert.Equal(1, fx.Describe("WEAP/ZZZZ").Exit);
    }

    [Theory]
    [InlineData("OMOD", 8)]
    [InlineData("omod", 8)]
    [InlineData("GMST", 5)]
    public void DescribeListsTheSeveralTypesOfASignature(string sig, int types)
    {
        var (exit, text) = fx.Describe(sig);
        Assert.Equal(0, exit);
        Assert.StartsWith($"# {sig.ToUpperInvariant()}: the signature of {types} record types\n", text);
        Assert.Equal(types, text.Split('\n').Count(l => l.StartsWith("| ") && l.Contains(".xml |")));
        Assert.Equal(types, fx.Scalar($"SELECT count(*) FROM RecordTypes WHERE Sig = '{sig.ToUpperInvariant()}'"));
    }

    [Fact]
    public void AWrongFieldNameGetsARealOne()
    {
        var (exit, text) = fx.Describe("Weapon.BaseDamage");
        Assert.Equal(1, exit);
        // The nearest real names, chosen and worded as the SQL layer and the record layer do it (NearNames)
        Assert.Equal("Weapon has no field \"BaseDamage\". Did you mean: AttackDamage (Float, WDMG, xEdit name \"Base Damage\"); MeleeBashDamage (Float, WMEL, xEdit name \"Bash Damage\"); BaseValue (UInt32, WGEN)?", text.Trim());
        Assert.Equal(1, fx.Describe("NoSuchThingAnywhere").Exit);
    }

    [Fact]
    public void TheNearestNamesOfAWrongFieldAreChosenOneWay()
    {
        var db = SchemaDb.Load(fx.Db);
        Assert.Equal(new[] { "AttackDamage (Float, WDMG, xEdit name \"Base Damage\")", "MeleeBashDamage (Float, WMEL, xEdit name \"Bash Damage\")", "BaseValue (UInt32, WGEN)" },
            NearNames.Fields(db, new[] { "Weapon" }, "BaseDamage"));
        // A name that is nowhere near a field gets none: no far-fetched one
        Assert.Empty(NearNames.Fields(db, new[] { "Weapon" }, "zzzz"));
        // A name that isn't near, but shares a word with a real one, still gets that one
        Assert.Contains("AmountProduced", string.Join("; ", NearNames.Fields(db, new[] { "ConstructibleObject" }, "AmountMade")));
        // The fix's wording, and what a library writes for its caller to turn into it
        Assert.Equal("the nearest real names: A; B", NearNames.Fix(new[] { "A", "B" }));
        Assert.Equal(" Did you mean: A; B?", NearNames.DidYouMean(new[] { "A", "B" }));
        Assert.Equal("", NearNames.DidYouMean(Array.Empty<string>()));
        // A filter that fits no field of a type names the nearest fields the same way
        var (exit, text) = fx.Describe("WEAP", "BaseDamag");
        Assert.Equal(1, exit);
        Assert.Contains("Did you mean: AttackDamage (Float, WDMG, xEdit name \"Base Damage\")", text);
    }

    [Fact]
    public void APluginNameIsNearOnlyWhenItIsNear()
    {
        var plugins = new[] { "Starfield.esm", "kgcdoom.esm", "hle.esm", "atom74.esm", "darkstarpatch_atom74.esm" };
        Assert.Equal(new[] { "kgcdoom.esm" }, NearNames.Plugins("kgcdom.esm", plugins));
        Assert.Equal(new[] { "atom74.esm" }, NearNames.Plugins("atom47", plugins));
        Assert.Empty(NearNames.Plugins("nope.esm", plugins));
        Assert.Empty(NearNames.Plugins("x.esm", plugins));
    }

    [Fact]
    public void AWordThatIsARecordTypeGivesTheTypeAndCanStillBeSearched()
    {
        var db = SchemaDb.Load(fx.Db);
        // The word alone is the type
        Assert.StartsWith("# Ammunition (AMMO)\n", fx.Describe("ammo").Text);
        var type = SchemaGen.Describe.TypeNamed(db, "ammo", out int fields);
        Assert.Equal(("AMMO", "Ammunition"), type);
        Assert.True(fields > 0);
        Assert.Null(SchemaGen.Describe.TypeNamed(db, "damage", out _));
        Assert.Null(SchemaGen.Describe.TypeNamed(db, "OMOD", out _));   // a signature of several types names no one type
        // The search of every field name for the word
        var w = new StringWriter();
        Assert.Equal(0, SchemaGen.Describe.Search(db, "ammo", w));
        string text = w.ToString().Replace("\r\n", "\n");
        Assert.StartsWith($"# \"ammo\" is part of the name of {fields} fields\n", text);
        Assert.Contains("| Weapon.AmmoCapacity | WAM2 |", text);
        Assert.Equal(1, SchemaGen.Describe.Search(db, "zzzzqq", new StringWriter()));
        // A word that is a field's whole name lists those fields, and says how many more hold it in a longer name
        string damage = fx.Describe("damage").Text;
        Assert.StartsWith("# \"damage\" is the name of 2 fields\n", damage);
        Assert.Matches(@"\n\d+ more fields hold ""damage"" inside a longer name: describe \*damage\* lists them too\.\n", damage);
    }

    [Fact]
    public void ABroadFilterIsRankedAndItsRepeatsPutTogether()
    {
        // describe PERK rank gave 635 rows: every field inside Ranks[*] matched by its path (fix B9, 2026-11-12)
        var (exit, perk) = fx.Describe("PERK", "rank");
        Assert.Equal(0, exit);
        var rows = perk.Split('\n').Where(l => l.StartsWith("| ") && !l.StartsWith("| Path")).ToList();
        Assert.StartsWith($"# Perk (PERK): {rows.Count} rows matching \"rank\" (635 paths: the ones that repeat are put together)\n", perk);
        Assert.True(rows.Count <= 10, $"PERK rank gives {rows.Count} rows");
        // The field named so first; the one field in 14 kinds of perk effect is one row that says how to see each
        Assert.StartsWith("| Ranks[*].Effects[*]{*}.Rank | PRKE |", rows[0]);
        Assert.Contains("the same field in 14 places", rows[0]);
        Assert.Contains("describe Perk rank --all lists each", rows[0]);
        // What only sits inside Ranks is one row with its count, last
        Assert.Matches(@"^\| Ranks\[\*\]\.… \|  \| \d+ fields \|  \| inside Ranks\[\*\], which holds ""rank"" in its name: describe Perk\.Ranks\[\*\] lists them \|$", rows[^1]);
        // --all still spells out every path
        Assert.StartsWith("# Perk (PERK): 635 fields matching \"rank\"\n", fx.Describe("PERK", "rank", all: true).Text);

        var (_, npc) = fx.Describe("NPC_", "flags");
        var npcRows = npc.Split('\n').Where(l => l.StartsWith("| ") && !l.StartsWith("| Path")).ToList();
        Assert.StartsWith("| Flags | ACBS |", npcRows[0]);
        Assert.True(npcRows.Count < 40, $"NPC_ flags gives {npcRows.Count} rows");
        Assert.Single(npcRows, l => l.StartsWith("| VirtualMachineAdapter.Scripts[*].Properties[*]{*}.Flags |"));
    }

    [Fact]
    public void DescribeWorksForEveryRecordType()
    {
        var db = SchemaDb.Load(fx.Db);
        var failed = new List<string>();
        foreach (var t in db.Model.RecordTypes)
        {
            var byName = fx.Describe(t.Name);
            if (byName.Exit != 0 || !byName.Text.StartsWith($"# {t.Name} ({t.Sig})\n")) failed.Add($"{t.Name}: {byName.Text.Split('\n')[0]}");
            if (fx.Describe(t.Sig!).Exit != 0) failed.Add(t.Sig!);
            // Its last field, by path (the mod header, TES4, is a record type with no EditorID)
            string last = db.Model.Expand(t.Name).Last(r => r.Depth == 0 && r.Def.Path == r.Def.Name).Path;
            var field = fx.Describe($"{t.Name}.{last}");
            if (field.Exit != 0 || !field.Text.StartsWith($"# {t.Name}.{last}\n")) failed.Add($"{t.Name}.{last}: {field.Text.Split('\n')[0]}");
        }
        Assert.True(failed.Count == 0, string.Join("\n", failed.Take(20)));
    }

    // ---- annotations and the version check ----

    [Fact]
    public void AnnotationsGiveUnitsAndAWrongRowFailsTheBuild()
    {
        Assert.Contains("| AttackSeconds | WFIR | Float, 4 B @3 | Attack (Seconds) | unit: seconds |", fx.Describe("WEAP").Text);
        Assert.Equal("seconds", fx.Sql("SELECT Unit FROM ObjectFields WHERE Object = 'Weapon' AND Path = 'AttackSeconds'")[0][0]);
        string bad = Path.Combine(fx.WorkDir, "bad-annotations.tsv");
        File.WriteAllText(bad, "Object\tField\tUnit\tRemark\nWeapon\tNoSuchField\tseconds\t\n");
        var e = Assert.Throws<SchemaException>(() => Generator.Build(fx.Options(Path.Combine(fx.WorkDir, "bad.db"), null, bad)));
        Assert.Contains("Weapon.NoSuchField", e.Message);
    }

    [Fact]
    public void TheRecordXmlIsThePackagesOwn()
    {
        string version = Reflect.AssemblyVersion();
        var (src, problems) = Inputs.CheckPackage(fx.PackageDir, version);
        Assert.Empty(problems);
        Assert.Equal(fx.Result.Model.Meta["MutagenCommit"], src!.Commit);
        Assert.Equal(fx.Result.Model.Meta["XmlSha256"], src.Sha256);
        Assert.True(src.Files >= 238, $"{src.Files} XML files"); // 237 record files and Layouts\HandReadLayouts.xml in fixes.14
        Assert.NotNull(src.LayoutsFile);

        // A copy of the package's folder: as restored it passes; another commit, another version, an XML file edited or
        // missing after the restore, or a package without its record XML, each fails
        string copy = Path.Combine(fx.WorkDir, "package-copy");
        void Fresh()
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
            foreach (var f in Directory.EnumerateFiles(fx.PackageDir, "*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".nuspec", StringComparison.Ordinal) || f.EndsWith(".nupkg", StringComparison.Ordinal) || Path.GetRelativePath(fx.PackageDir, f).StartsWith("loqui", StringComparison.Ordinal)))
            {
                string to = Path.Combine(copy, Path.GetRelativePath(fx.PackageDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
        }
        string nuspec = Path.Combine(copy, Inputs.PackageId + ".nuspec");
        Fresh();
        Assert.Empty(Inputs.CheckPackage(copy, version).Problems);
        File.WriteAllText(nuspec, File.ReadAllText(nuspec).Replace(src.Commit, "0123456789abcdef0123456789abcdef01234567"));
        Assert.Contains(Inputs.CheckPackage(copy, version).Problems, p => p.Contains("0123456789abcdef"));
        Fresh();
        Assert.Contains(Inputs.CheckPackage(copy, version.Replace(src.Version, "0.55.0-fixes.8")).Problems, p => p.Contains("0.55.0-fixes.8"));
        string weapon = Path.Combine(copy, Inputs.RecordsPath, "Major Records", "Weapon.xml");
        File.AppendAllText(weapon, "<!-- edited -->");
        Assert.Contains(Inputs.CheckPackage(copy, version).Problems, p => p.Contains("Weapon.xml differs"));
        File.Delete(weapon);
        Assert.Contains(Inputs.CheckPackage(copy, version).Problems, p => p.Contains("Weapon.xml is in the package's .nupkg but missing"));
        Directory.Delete(Path.Combine(copy, Inputs.LoquiFolder), true);
        Assert.Contains(Inputs.CheckPackage(copy, version).Problems, p => p.Contains("doesn't carry its record definitions"));

        // And the build itself refuses
        var o = fx.Options(Path.Combine(fx.WorkDir, "never.db"));
        o.PackageDir = copy;
        Assert.Throws<SchemaException>(() => Generator.Build(o));
        Assert.False(File.Exists(o.OutDb));
    }

    // ---- the Creation Kit's names (to-do F30): a data file in the repo, read by every build ----

    [Fact]
    public void EveryBuildReadsTheCksNamesFile()
    {
        // Meta: the data file, its hash, and the Creation Kit and dump it was made from (its header)
        string Meta(string key) => (string)fx.Sql($"SELECT Value FROM Meta WHERE Key = '{key}'")[0][0]!;
        Assert.Equal("ck-names-1.16.244.0.tsv", Path.GetFileName(fx.CkNames));
        Assert.Equal(Path.GetFileName(fx.CkNames), Meta("CkNamesFile"));
        Assert.Matches("^[0-9a-f]{64}$", Meta("CkNamesSha256"));
        Assert.Equal("CreationKit.exe 1.16.244.0", Meta("CkReflection"));
        Assert.Matches("^[0-9a-f]{64}$", Meta("CkReflectionSha256"));
        // The project file builds with the same file, and no longer asks for a release flag or the dump
        string proj = File.ReadAllText(Path.Combine(fx.GenDir, "SFSearch.SchemaGen.csproj"));
        Assert.Contains($@"\inputs\{Path.GetFileName(fx.CkNames)}</SchemaCkNames>", proj);
        Assert.DoesNotContain("SfsRelease", proj);
        Assert.DoesNotContain("reflection-dumps", proj);
        // The file reads back to itself: writing what was read gives the same text
        var ck = CkNamesFile.Read(fx.CkNames);
        string again = Path.Combine(fx.WorkDir, "ck-names-again.tsv");
        CkNamesFile.Write(ck, again);
        Assert.Equal(File.ReadAllText(fx.CkNames).Replace("\r\n", "\n"), File.ReadAllText(again));
        Assert.Equal(ck.Types.Count, fx.Scalar("SELECT count(*) FROM CkTypes"));
        Assert.Equal(ck.Types.Values.Sum(t => t.Fields.Count), fx.Scalar("SELECT count(*) FROM CkFields"));
        Assert.Equal(ck.Types.Values.Sum(t => t.Members.Count), fx.Scalar("SELECT count(*) FROM CkMembers"));
        // A tooltip with line feeds comes back with them
        Assert.Contains(ck.Types.Values.SelectMany(t => t.Fields), f => f.ToolTip?.Contains('\n') == true);
        // A bad row is refused with its line
        string bad = Path.Combine(fx.WorkDir, "ck-names-bad.tsv");
        File.WriteAllLines(bad, new[] { "# source: CreationKit.exe 0", "field\tNoSuchType\t1\tx\t\t\t\t\t\t\t" });
        Assert.Contains("line 2", Assert.Throws<SchemaException>(() => CkNamesFile.Read(bad)).Message);
    }

    /// <summary>The checked-in file holds everything the dump gives SFSearch: a schema built from the dump (read by
    /// ck-import's reader) equals the one built from the file, in every CK table and every CK column, row for row; and
    /// ck-import writes the checked-in file again. Needs the dump, which isn't in the repo.</summary>
    [DumpFact]
    public void TheCksNamesFileIsWhatCkImportMakesFromTheDump()
    {
        var fromDump = CkReflection.Load(DumpFactAttribute.Dump);
        string written = Path.Combine(fx.WorkDir, CkNamesFile.FileName(fromDump));
        CkNamesFile.Write(fromDump, written);
        Assert.Equal(Path.GetFileName(fx.CkNames), Path.GetFileName(written));
        Assert.Equal(File.ReadAllText(fx.CkNames).Replace("\r\n", "\n"), File.ReadAllText(written));

        string db = Path.Combine(fx.WorkDir, "schema-from-dump.db");
        var o = fx.Options(db);
        o.CkNamesPath = null;
        o.CkData = fromDump;
        Generator.Build(o);
        static string Cell(object? v) => v is double d ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : v?.ToString() ?? "\u0000";
        foreach (var q in new[]
        {
            "SELECT * FROM CkTypes ORDER BY Type", "SELECT * FROM CkFields ORDER BY Type, Ord", "SELECT * FROM CkMembers ORDER BY Type, Ord",
            "SELECT Name, CkClass, CkName FROM Objects ORDER BY Name", "SELECT Object, Ord, CkName, CkField FROM ObjectFields ORDER BY Object, Ord",
            "SELECT Enum, Member, CkName, CkMember FROM Enums ORDER BY Enum, Member",
        })
        {
            var fromFile = fx.Sql(q).Select(r => string.Join("\u0001", r.Select(Cell))).ToList();
            var fromJson = fx.Sql(q, db).Select(r => string.Join("\u0001", r.Select(Cell))).ToList();
            Assert.True(fromFile.Count > 0, $"{q}: no rows");
            Assert.Equal(fromJson, fromFile);
        }
        string Meta(string key, string? from = null) => (string)fx.Sql($"SELECT Value FROM Meta WHERE Key = '{key}'", from)[0][0]!;
        Assert.Equal(Meta("CkReflectionSha256", db), Meta("CkReflectionSha256"));
        Assert.Equal(Meta("CkReflection", db), Meta("CkReflection"));
    }

    [Fact]
    public void TheCreationKitsNamesAreInEveryBuild()
    {
        var ck = SchemaDb.Load(fx.Db).Model.Ck;
        Assert.True(ck.Present);
        Assert.StartsWith("CreationKit.exe", ck.Source);
        Assert.Equal(64, ((string)fx.Sql("SELECT Value FROM Meta WHERE Key = 'CkReflectionSha256'")[0][0]!).Length);
        // A field, a class and an enum member, by the engine's names
        Assert.Equal("Image Space: Day", ck.Field("BGSAtmosphere::MiscSettings", "pImageSpaceDay")?.DisplayName);
        Assert.Equal("Atmosphere", ck.Type("BGSAtmosphere")?.DisplayName);
        Assert.Equal("<=", ck.Member("BGSActorValueActivity::ComparisonOperatorType", "LesserEqual")?.DisplayName);
        Assert.True(ck.Field("BGSForceData", "Strength")!.Hidden);
        Assert.NotNull(ck.Field("BGSFogVolumeForm", "Anisotropy")!.ToolTip);
        Assert.NotNull(CkData.RangeText(ck.Field("BGSFogVolumeForm", "Anisotropy")!));
        Assert.True(fx.Result.Ck!.FieldNames >= 700 && fx.Result.Ck.TypeNames >= 100 && fx.Result.Ck.MemberNames >= 130, $"{fx.Result.Ck.FieldNames} fields, {fx.Result.Ck.TypeNames} types, {fx.Result.Ck.MemberNames} members named");

        // Mutagen's own objects, fields and enum members that are the engine's
        Assert.Equal("Atmosphere", fx.Sql("SELECT CkName FROM Objects WHERE Name = 'Atmosphere'")[0][0]);
        Assert.Equal("BGSAtmosphere", fx.Sql("SELECT CkClass FROM Objects WHERE Name = 'Atmosphere'")[0][0]);
        Assert.Equal("Spline Radius", fx.Sql("SELECT CkName FROM ObjectFields WHERE Object = 'PlacedObjectSpline' AND Path = 'SplineRadius'")[0][0]);
        Assert.Equal("Condition Form", fx.Sql("SELECT CkName FROM ObjectFields WHERE Object = 'SoundReference' AND Path = 'ConditionForm'")[0][0]);
        Assert.Equal("Fly Grab", fx.Sql("SELECT CkName FROM Enums WHERE Enum = 'BodyPart.PartType' AND Value = 4")[0][0]);

        // describe shows them beside the engine's names
        var (exit, text) = fx.Describe("BGSAtmosphere::MiscSettings.pImageSpaceDay");
        Assert.Equal(0, exit);
        Assert.Contains("- CK name: Image Space: Day", text);
        Assert.Contains("- Engine name: BGSAtmosphere::MiscSettings.pImageSpaceDay", text);
        Assert.Contains("- CK name: Image Space: Day", fx.Describe("MiscSettings.pImageSpaceDay").Text);
        Assert.Contains("the Creation Kit calls it \"Atmosphere\"", fx.Describe("BGSAtmosphere").Text);
        Assert.Contains("| LesserEqual | 1 | <= |", fx.Describe("BGSActorValueActivity::ComparisonOperatorType").Text);
        Assert.Contains("CK: \"Spline Radius\"", fx.Describe("PlacedObjectSpline").Text);
        Assert.Contains("- CK name: Spline Radius (the Creation Kit's display name of ExtraBendableSplineParams::ParamData.fThickness)", fx.Describe("PlacedObjectSpline.SplineRadius").Text);
        Assert.Contains("The Creation Kit calls it \"Atmosphere\"; its reflection data (REFL) is a value of the engine's class BGSAtmosphere", fx.Describe("ATMO").Text);
        Assert.Contains("(CK: \"Fly Grab\")", fx.Describe("BodyPartData.Parts[*].Type").Text);
        Assert.Contains("hidden in the CK", fx.Describe("BGSForceData").Text);
        // A CK name finds the field: a Mutagen field's, and a reflected class's when no Mutagen field has it
        Assert.Contains("| PlacedObject.Spline.SplineRadius | XBSD |", fx.Describe("Spline Radius").Text);
        Assert.Contains("BGSAtmosphere::MiscSettings.pImageSpaceDay", fx.Describe("Image Space: Day").Text);
    }
}

/// <summary>A test that needs the Creation Kit's reflection dump, which isn't in the repo: only the check that the
/// checked-in names file is what ck-import makes from it. The file SFS_CK_DUMP names, else
/// reflection-dumps\CK-BSReflection.json in the folder that holds the repo; skipped when there is none.</summary>
public sealed class DumpFactAttribute : FactAttribute
{
    public static string Dump { get; } = Environment.GetEnvironmentVariable("SFS_CK_DUMP") is { Length: > 0 } p ? p : BesideRepo();

    static string BesideRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SFSearch.SchemaGen", "SFSearch.SchemaGen.csproj"))) dir = dir.Parent;
        string beside = dir?.Parent?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(beside, "reflection-dumps", "CK-BSReflection.json");
    }

    public DumpFactAttribute()
    {
        if (!File.Exists(Dump)) Skip = $"no Creation Kit reflection dump at {Dump} (SFS_CK_DUMP names another): it isn't in the repo";
    }
}
