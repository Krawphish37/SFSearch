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
        RecordNamesFile = Directory.GetFiles(Path.Combine(GenDir, "inputs"), "xedit-record-names-*.tsv").Single(),
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
        // 8,380: 7,260 for the record types until J73, 20 for the plugin header (TES4, xedit-tes4-2026-11-15; GAPS S90),
        // since J73/J77 (2026-11-15) 992 for every record's own header (GAPS S97), 87 changed by J77's fixes; since J108
        // (GAPS S136) the whole fixes.19 rerun: 76 more, 3 fewer (TheFixes19RerunsChangesAreRight), 4 of them header flags
        Assert.Equal(8380, p.Rows);
        Assert.Equal(996, fx.Result.PairRows.Count(x => x.Sig != "TES4" && x.XEditPath.StartsWith(@"\Record Header\", StringComparison.Ordinal)));
        // Fewer paths than rows: each kind of a list's items has its own xEdit field for a field the kinds share (SCEN's
        // FX and Player Dialogue Sound since C218; a quest's reference and location aliases, J108)
        Assert.Equal(8352, p.DistinctPaths);
        // One pair is left unmatched: MGEF's 1st Assoc. Item on a hand-written property (PairsIntoMutagensOwnProperties...)
        Assert.Equal(1, p.Reasons.Values.Sum());
        // The pairs file gives Mutagen's current names (J95: the pairing rerun on fixes.19). Until J95, 229 pairs named
        // fields Mutagen had renamed since (C207, C218, C220) and matched only through a rename table in the matcher.
        PairRow Pair(string sig, string xedit) => fx.Result.PairRows.Single(x => x.Sig == sig && x.XEditPath == xedit);
        var pdStart = Pair("SCEN", @"\Actions\Action\Player Dialogue\WED0 - Sound\Event Set\Start Event/Form");
        Assert.Equal(("Actions[*].Sound.StartEvent", "exact"), (pdStart.MutagenPath, pdStart.Match));
        Assert.Equal("Actions[*].Sound.EventForm", Pair("SCEN", @"\Actions\Action\Player Dialogue\WED0 - Sound\Form Only\Start Form").MutagenPath);
        Assert.Equal("Actions[*].Sound.ConditionForm", Pair("SCEN", @"\Actions\Action\FX\WED0 - Sound\Event Set\Condition").MutagenPath);
        var tnam = Pair("AVMD", @"\TNAM - Name");
        Assert.Equal(("Name", "exact"), (tnam.MutagenPath, tnam.Match));
        Assert.Equal("GravityScale", Pair("WRLD", @"\GNAM - Gravity Scale").MutagenPath);
        Assert.Equal("Spline.SplineRadius", Pair("REFR", @"\XBSD - Bendable Spline\Thickness").MutagenPath);
        // every sound reference's Start Form (71 in 35 record types, EventMapping until C218), SCEN's Player Dialogue sound
        // (WED0 until C218) and its Radio voice override (J108)
        Assert.Equal(73, fx.Result.PairRows.Count(x => x.MutagenPath.EndsWith(".EventForm", StringComparison.Ordinal) && x.Match == "exact"));
        Assert.DoesNotContain(fx.Result.PairRows, x => x.MutagenPath is "TNAM" or "GNAM" || x.MutagenPath.Contains(".WED0.", StringComparison.Ordinal)
            || x.MutagenPath.EndsWith(".EventMapping", StringComparison.Ordinal) || x.MutagenPath.EndsWith("Spline.Thickness", StringComparison.Ordinal));
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
    public void TheFixes19RerunsChangesAreRight()
    {
        // J108 (GAPS S136): the pairs rerun on Mutagen 0.55.0-fixes.19 with xecmp's J108 fixes, taken whole
        PairRow? Pair(string sig, string xedit) => fx.Result.PairRows.SingleOrDefault(x => x.Sig == sig && x.XEditPath == xedit);
        string? Named(string sig, string path) => fx.Sql($"SELECT XEditPath FROM PairNames WHERE Sig = '{sig}' AND Path = '{path}'") is { Count: > 0 } r ? r[0][0] as string : null;
        // Gains: NAVI's navmesh infos, the record flags fixes.19 named (C54), each kind's own fields
        Assert.Equal(36, fx.Result.PairRows.Count(x => x.Sig == "NAVI" && x.MutagenPath.StartsWith("MapInfos[*].", StringComparison.Ordinal) && x.Match != null));
        Assert.Equal("MajorFlags.Restricted", Pair("KYWD", @"\Record Header\Record Flags\Restricted")!.MutagenPath);
        Assert.Equal("MajorFlags.HighResFirstPersonOnly", Pair("WEAP", @"\Record Header\Record Flags\High-Res 1st Person Only")!.MutagenPath);
        Assert.Equal("Actions[*].VoiceOverride.EventForm", Pair("SCEN", @"\Actions\Action\Radio\WED0 - Voice Override\Form Only\Start Form")!.MutagenPath);
        var loc = Pair("QUST", @"\Aliases\Location Alias\ALID - Alias Name")!;
        Assert.Equal(("Aliases[*].Name", 981, 981), (loc.MutagenPath, loc.Checked, loc.Agree));
        Assert.Equal("Aliases[*].Flags.IsCompanion", Pair("QUST", @"\Aliases\Location Alias\FNAM - Flags\Discard on shutdown if unused")!.MutagenPath);
        Assert.Equal("Aliases[*].Flags.IsCompanion", Pair("QUST", @"\Aliases\Reference Alias\FNAM - Flags\Is Companion")!.MutagenPath);
        // A path several kinds' fields name takes the one checked on the most records (Generator, PairNames)
        Assert.Equal(@"\Aliases\Reference Alias\ALID - Alias Name", Named("QUST", "Aliases[*].Name"));
        Assert.Equal(@"\Actions\Action\Dialogue\DATA - Topic", Named("SCEN", "Actions[*].Topic"));
        // Wrong old pairs gone: a condition's Function never pairs with FirstParameter.Index (ALCH's agreed on 0 of 291);
        // QUST's VMAD script flags each pair with their own script (1,051 and 1,012 of as many; before 742 and 484)
        Assert.DoesNotContain(fx.Result.PairRows, x => x.XEditPath.EndsWith(@"CTDA - CTDA\Function", StringComparison.Ordinal) && x.MutagenPath.EndsWith("FirstParameter.Index", StringComparison.Ordinal));
        Assert.Equal("VirtualMachineAdapter.Scripts[*].Flags", Pair("QUST", @"\VMAD - Virtual Machine Adapter\Scripts\Script\Flags")!.MutagenPath);
        Assert.Equal("VirtualMachineAdapter.Script.Flags", Pair("QUST", @"\VMAD - Virtual Machine Adapter\Script Fragments\Script\Script Data\Flags")!.MutagenPath);
        // XALD's Unused read as a half (C221)
        Assert.Contains("003e=1.5", Pair("REFR", @"\XALD - Area Light\Unused")!.Translation, StringComparison.Ordinal);
        // A condition list's names don't move with another record type's pairs (ContainerNames weighs each type's fields)
        Assert.Equal(@"\Conditions\Condition", Named("BMMO", "Conditions[*]"));
        Assert.Equal(@"\Conditions\Condition\CTDA - CTDA", Named("CAMS", "Conditions[*].Data"));
        Assert.Equal(@"\Conditions\Conditions\Condition", Named("ACTI", "Conditions[*]"));
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
        // One pair names a property Mutagen writes by hand beside its XML field (J77, 2026-11-15): MGEF's 1st "Assoc.
        // Item" is the archetype's association, which xecmp read as MagicEffectArchetype.AssociationKey (a FormKey view
        // of the XML's Association)
        var key = Assert.Single(fx.Result.PairRows, p => p.Match == null);
        Assert.Equal(("MGEF", "Archetype.AssociationKey", "property not in Mutagen's XML"), (key.Sig, key.MutagenPath, key.Reason));

        // Nothing else is left unmatched since the pairs were made again on fixes.14 (2026-11-14) and row 8's properties
        // became fields. The 20 pairs made on C174's names are gone.
        Assert.Equal(0, fx.Result.PairRows.Count(p => p.Match == null && p.Reason == "unknown field since named or split"));
        Assert.StartsWith("# xEdit pairs that match no field of Mutagen ", File.ReadLines(fx.Report).First());
        Assert.EndsWith($": 1 of {fx.Result.PairRows.Count}.", File.ReadLines(fx.Report).First());
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
        // fixes.20 (C55) gave Key, Static, StaticCollection, LeveledBaseForm, Region, BiomeMarker, GenericBaseForm, Star and Projectile their MajorFlags: 42 -> 51
        Assert.Equal((51, 13, 10), (majorFlag, versioning, dataStates));
        Assert.Equal(majorFlag, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Name = 'MajorFlags' AND Source IS NOT NULL"));
        Assert.Equal(versioning, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Name = 'Versioning' AND Source IS NOT NULL AND Ord = 1"));
        Assert.Equal(dataStates, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Name LIKE '%DataTypeState' AND Source IS NOT NULL"));
        Assert.Equal(77L, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Source IS NOT NULL"));
        // From Mutagen's property manifest (C60 W6), not the generator's rules applied again. Since fixes.24 (C80, GAPS S171
        // fixed) each Loqui field of a block has its own place: MovementData's speeds and BiomeBTPS's items run one after
        // another (the build fails on places that overlap)
        foreach (var obj in new[] { "MovementData", "BiomeBTPS" })
        {
            var at = fx.Sql($"SELECT Offset FROM ObjectFields WHERE Object = '{obj}' AND Offset IS NOT NULL AND Kind <> 'Break' ORDER BY Ord").Select(r => Convert.ToInt64(r[0], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Assert.True(at.Count >= 6 && at.Distinct().Count() == at.Count, $"{obj}: offsets {string.Join(",", at)}");
        }
        Assert.Equal(1L, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Object = 'MovementData' AND Path = 'MaxSpeedLeft' AND Offset IS NOT NULL"));
        // C81: an object hand-written code enters names its readers (enteredBy, Objects.HandRead), its fields their filler
        // (filledBy) and no offset. TraversalReference.Traversal (binary="Custom", its reader reads Flags at 40 and the link at
        // 44) no longer takes its reader's start, 40, as its offset: the record XML's layoutOffset 44 places it
        Assert.Equal(new object?[] { "APackageTarget.CreateFromBinary", null }, fx.Sql("SELECT FilledBy, Offset FROM ObjectFields WHERE Object = 'PackageTargetSpecificReference' AND Path = 'Reference'")[0]);
        Assert.Contains("APackageTarget.CreateFromBinary", fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'PackageTargetSpecificReference'")[0][0] as string);
        Assert.Equal(new object?[] { null, "44" }, fx.Sql("SELECT Offset, LayoutOffset FROM ObjectFields WHERE Object = 'TraversalReference' AND Path = 'Traversal'")[0]);

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
        // Since fixes.24 (C80, GAPS S171 fixed) each of the seven sounds (a SoundReference) has its own place: the sixth
        // (PrimedExplosive) at 200, its StopEvent at 56 in it, counted from the struct's start
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

    // ---- GAPS S59: an object that begins with a subrecord of its own is no struct ----

    [Fact]
    public void AnObjectWhoseFirstFieldIsASubrecordIsNoStruct()
    {
        // A container entry is a CNTO (ContainerItem) and a COED (ExtraData): each item of a container's list begins at its CNTO
        Assert.Equal(0L, fx.Scalar("SELECT Struct FROM Objects WHERE Name = 'ContainerEntry'"));
        Assert.Equal(new object?[] { 1L, null, 1L, null }, fx.Sql("SELECT a.StartsSubrecord, a.Offset, b.StartsSubrecord, b.Offset FROM ObjectFields a JOIN ObjectFields b ON b.Object = a.Object AND b.Path = 'Data' WHERE a.Object = 'ContainerEntry' AND a.Path = 'Item'")[0]);
        // The others of the kind: lists of subrecords (BODS/BODV items behind BODC, naming rules behind VNAM, collection aliases' ALCS ...)
        foreach (var o in new[] { "BlueprintComponentBODSRow", "InstanceNamingRuleSet", "QuestCollectionAlias", "WeaponInstanceNamingRules" })
            Assert.Equal(0L, fx.Scalar($"SELECT Struct FROM Objects WHERE Name = '{o}'"));
        // Objects whose bytes start inside their user's subrecord and that hold subrecords after them stay structs:
        // a condition (CTDA, then the CIS1 and CIS2 some kinds of its data hold), a legendary item's LNAM entry (its
        // conditions' CITC and CTDA after all the entries), both read by hand-written code
        Assert.Equal(new object?[] { 1L, 1L }, fx.Sql("SELECT a.Struct, b.Struct FROM Objects a, Objects b WHERE a.Name = 'Condition' AND b.Name = 'LegendaryItemLnamEntry'")[0]);
        // No struct is left whose first field (its bases' first) is an object or list of objects with a signature of its own
        var left = fx.Sql(@"WITH first AS (
                SELECT a.Object AS Obj, f.Object AS Owner, f.Path, f.RefObject,
                       row_number() OVER (PARTITION BY a.Object ORDER BY a.Level DESC, f.Ord) AS n
                FROM ObjectAncestors a JOIN ObjectFields f ON f.Object = a.Ancestor
                WHERE f.Kind <> 'Break' AND f.Source IS NULL AND f.Path NOT LIKE '%[*]')
            SELECT o.Name FROM Objects o JOIN first ON first.Obj = o.Name AND first.n = 1
            JOIN ObjectFields i ON i.Object = first.Owner AND (i.Path = first.Path OR i.Path = first.Path || '[*]') AND i.RefObject IS NOT NULL
            JOIN Objects r ON r.Name = i.RefObject
            WHERE o.Struct = 1 AND r.Sig IS NOT NULL");
        Assert.Empty(left);
    }

    // ---- GAPS S64: which BFCB text picks which component kind ----

    [Fact]
    public void EachComponentKindHasTheBfcbTextMutagenReadsItBy()
    {
        // Mutagen's XML says it since fixes.20 (C57 W4): selectedBy and selectorEnum on AComponent, selectorValue on each kind,
        // "*" on UnknownComponent, the kind Mutagen's reader makes of any other BFCB text (C63)
        Assert.Equal(new object?[] { "subrecord:BFCB:string", "AComponent.ComponentType" }, fx.Sql("SELECT SelectedBy, SelectorEnum FROM Objects WHERE Name = 'AComponent'")[0]);
        Assert.Equal("*", fx.Sql("SELECT SelectorValue FROM Objects WHERE Name = 'UnknownComponent'")[0][0]);
        var kinds = fx.Sql("SELECT o.Name, o.SelectorValue FROM Objects o JOIN ObjectAncestors a ON a.Object = o.Name AND a.Ancestor = 'AComponent' AND a.Level > 0 WHERE o.Abstract = 0 AND o.Name <> 'UnknownComponent'")
            .ToDictionary(r => (string)r[0]!, r => (string?)r[1]);
        var members = Enum.GetNames<Mutagen.Bethesda.Starfield.AComponent.ComponentType>();
        Assert.Equal(66, kinds.Count);
        Assert.Equal(members.Length, kinds.Count);
        // The enum Mutagen parses the text as, member by member, with its values
        Assert.Equal(members.Select(m => new object?[] { m, (long)(int)Enum.Parse<Mutagen.Bethesda.Starfield.AComponent.ComponentType>(m) }),
            fx.Sql("SELECT Member, Value FROM Enums WHERE Enum = 'AComponent.ComponentType' ORDER BY Value"));
        // Every kind's text is one member, and Mutagen's own reader makes that kind of component from it
        var key = Mutagen.Bethesda.Plugins.ModKey.FromFileName("SchemaTestsComponents.esm");
        var meta = new Mutagen.Bethesda.Plugins.Binary.Streams.ParsingMeta(Mutagen.Bethesda.Plugins.Meta.GameConstants.Starfield, key,
            Mutagen.Bethesda.Plugins.Masters.SeparatedMasterPackage.Factory(Mutagen.Bethesda.GameRelease.Starfield, key, Mutagen.Bethesda.Plugins.MasterStyle.Full,
                new Mutagen.Bethesda.Plugins.Masters.MasterReferenceCollection(key), null));
        foreach (var (kind, text) in kinds)
        {
            Assert.NotNull(text);
            Assert.Contains(text, members);
            var bytes = new List<byte>();
            bytes.AddRange("BFCB"u8.ToArray()); bytes.AddRange(BitConverter.GetBytes((ushort)(text!.Length + 1)));
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(text)); bytes.Add(0);
            bytes.AddRange("BFCE"u8.ToArray()); bytes.Add(0); bytes.Add(0);
            var frame = new Mutagen.Bethesda.Plugins.Binary.Streams.MutagenFrame(new Mutagen.Bethesda.Plugins.Binary.Streams.MutagenMemoryReadStream(bytes.ToArray(), meta));
            Assert.Equal(kind, Mutagen.Bethesda.Starfield.AComponent.CreateFromBinary(frame, default).GetType().Name);
        }
        Assert.Equal(kinds.Count, kinds.Values.Distinct().Count());
    }

    // ---- GAPS S65: the model is safe to walk from many threads at once ----

    [Fact]
    public void TheModelWalkedFromEveryCoreAtOnceIsTheModelWalkedByOneThread()
    {
        // One thread: every record type's fields spelled out, every object's tables, every path looked up (a fresh model)
        static List<string> Walk(SchemaModel m, string type)
        {
            var lines = new List<string>();
            foreach (var r in m.Expand(type))
                lines.Add($"{r.Path}|{r.Sort}|{r.Subrecord}|{r.Offset}|{r.Enum}|{r.InList}");
            foreach (var o in m.Ancestry(type))
            {
                lines.Add(o.Name + ": " + string.Join(",", m.AllFields(o.Name).Select(x => x.F.Path + "/" + x.Level)));
                lines.Add(o.Name + " kinds: " + string.Join(",", m.Variants(o.Name)) + " targets: " + string.Join(",", m.Targets(o.Name).Select(t => t.Name + t.Marker)));
            }
            return lines;
        }
        var one = SchemaDb.Load(fx.Db).Model;
        var types = one.RecordTypes.Select(t => t.Name).ToList();
        var want = types.ToDictionary(t => t, t => Walk(one, t), StringComparer.Ordinal);

        // Every core at once, on fresh models, each type walked by several threads together, the big ones first
        int cores = Environment.ProcessorCount;
        for (int round = 0; round < 3; round++)
        {
            var many = SchemaDb.Load(fx.Db).Model;
            var jobs = types.OrderByDescending(t => want[t].Count).SelectMany(t => Enumerable.Repeat(t, 4)).ToList();
            var got = new System.Collections.Concurrent.ConcurrentBag<(string Type, List<string> Lines)>();
            var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
            using var start = new ManualResetEventSlim(false);
            var threads = Enumerable.Range(0, cores).Select(c => new Thread(() =>
            {
                start.Wait();
                // Before S65's fix the unlocked tables threw here ("Operations that change non-concurrent collections
                // must have exclusive access") and took the test host down
                try { for (int i = c; i < jobs.Count; i += cores) got.Add((jobs[i], Walk(many, jobs[i]))); }
                catch (Exception e) { errors.Add(e.GetType().Name + ": " + e.Message); }
            })).ToList();
            threads.ForEach(t => t.Start());
            start.Set();
            threads.ForEach(t => t.Join());
            Assert.Empty(errors);
            Assert.Equal(jobs.Count, got.Count);
            foreach (var (type, lines) in got)
                Assert.True(want[type].SequenceEqual(lines), $"round {round}: {type} walked on {cores} threads differs from one thread's walk");
        }
    }

    // ---- Mutagen's layout attributes and hand-read layouts (C186, fixes.14; GAPS Part 2 row 30) ----

    [Fact]
    public void LayoutAttributesAndHandReadLayoutsAreInTheSchema()
    {
        // Every layout attribute the XML has is a column of its field or object (Core's XML too: since fixes.20, C57 W2, it
        // places the record header's fields with layoutSubrecord="header")
        var docs = Directory.EnumerateFiles(fx.RecordsDir, "*.xml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Inputs.CheckPackage(fx.PackageDir, Reflect.AssemblyVersion()).Item1!.CoreRecordsDir, "*.xml", SearchOption.AllDirectories)).Select(f => XDocument.Load(f)).ToList();
        foreach (var (attr, column) in new[] { ("layoutOffset", "LayoutOffset"), ("layoutBits", "LayoutBits"), ("layoutSubrecord", "LayoutSubrecord"), ("layoutEncoding", "LayoutEncoding"),
                     ("layoutDroppedBits", "LayoutDroppedBits"), ("layoutPresentWhen", "LayoutPresentWhen"), ("layoutGroup", "LayoutGroup"), ("layoutMap", "LayoutMap") })
        {
            int xml = docs.Sum(d => d.Descendants().Count(e => e.Name.LocalName != "Object" && e.Attribute(attr) != null));
            Assert.True(xml > 0, $"the XML has no {attr}");
            // RegionData.DataType, a property the XML doesn't declare, is placed by the schema itself (GAPS Part 2 row 8)
            Assert.Equal(xml, fx.Scalar($"SELECT count(*) FROM ObjectFields WHERE {column} IS NOT NULL AND Path NOT LIKE '%[*]' AND Source IS NULL"));
        }
        // Every object's selector value is the XML's, the component kinds' included since fixes.20 (C57 W4, GAPS S64)
        foreach (var (attr, column) in new[] { ("selectorValue", "SelectorValue"), ("conditionFunction", "ConditionFunction"), ("selectorEnum", "SelectorEnum") })
            Assert.Equal(docs.Sum(d => d.Descendants().Count(e => e.Name.LocalName == "Object" && e.Attribute(attr) != null)),
                fx.Scalar($"SELECT count(*) FROM Objects WHERE {column} IS NOT NULL"));
        Assert.Equal(docs.Sum(d => d.Descendants().Count(e => e.Name.LocalName != "Object" && e.Attribute("selectorEnum") != null)),
            fx.Scalar("SELECT count(*) FROM ObjectFields WHERE SelectorEnum IS NOT NULL AND Path NOT LIKE '%[*]'"));
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

        // HandReadLayouts.xml: each Header, Record, AnyRecord and Define, read back by SchemaDb
        var kinds = fx.Sql("SELECT Kind, count(*) FROM HandLayouts GROUP BY Kind ORDER BY Kind").ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
        // fixes.18 (C47) added the hand-read lists SFSearch had copied; fixes.20 (C57) the record header and TES4's (two
        // Headers and a TES4 Record) and NPC_'s race overrides with a SoundReference define
        // fixes.24 (C84, C86): AnyRecord NVNM, XTRV, XCZR/XCZC/XCZA and XNSE, Record SFTR, Define Reflection
        // fixes.27 (dev): Define PackageFlagsOverride (a package branch's PFO2)
        Assert.Equal(new Dictionary<string, long> { ["AnyRecord"] = 6, ["Define"] = 14, ["Header"] = 2, ["Record"] = 32 }, kinds);
        var m = SchemaDb.Load(fx.Db).Model;
        Assert.Equal(54, m.HandLayouts.Count);
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

    /// <summary>
    /// GAPS S67, S88, WORKAROUNDS W2: since fixes.20 (C57) Mutagen's HandReadLayouts.xml lays out every hand-read list
    /// SFSearch walks, the plugin header's master list and the record header included, so SFSearch keeps no layout file
    /// of its own. The record header's fields are placed from its Header entries, and the views of its flags that Core's
    /// hand-written MajorRecord adds come from the Header's Bits.
    /// </summary>
    [Fact]
    public void TheRecordHeaderIsPlacedFromMutagensLayoutFile()
    {
        Assert.False(File.Exists(Path.Combine(fx.GenDir, "inputs", "hand-read-layouts.xml")));
        var m = SchemaDb.Load(fx.Db).Model;
        HandLayouts.HeaderField? At(string obj, string field) => HandLayouts.HeaderPlace(m, m.Objects[obj].Fields.Single(f => f.Name == field && f.Path == field));
        // A record's header: MajorRecord's and StarfieldMajorRecord's fields, the views of its flags, a type's MajorFlags
        Assert.Equal(new HandLayouts.HeaderField(8, 4, 0, null, false, null), At("MajorRecord", "MajorRecordFlagsRaw"));
        Assert.Equal(new HandLayouts.HeaderField(12, 4, 0, null, false, null), At("MajorRecord", "FormKey"));
        Assert.Equal(new HandLayouts.HeaderField(16, 4, 0, null, false, null), At("MajorRecord", "VersionControl"));
        Assert.Equal(new HandLayouts.HeaderField(20, 2, 0, null, false, null), At("StarfieldMajorRecord", "FormVersion"));
        Assert.Equal(new HandLayouts.HeaderField(22, 2, 0, null, false, null), At("StarfieldMajorRecord", "Version2"));
        Assert.Equal(new HandLayouts.HeaderField(8, 4, 0x20, "MajorRecordFlagsRaw", false, null), At("MajorRecord", "IsDeleted"));
        Assert.Equal(new HandLayouts.HeaderField(8, 4, 0x40000, "MajorRecordFlagsRaw", false, null), At("MajorRecord", "IsCompressed"));
        Assert.Equal(new HandLayouts.HeaderField(8, 4, 0, "MajorRecordFlagsRaw", false, null), At("StarfieldMajorRecord", "StarfieldMajorRecordFlags"));
        Assert.Equal(new HandLayouts.HeaderField(8, 4, 0, "MajorRecordFlagsRaw", true, null), At("Weapon", "MajorFlags"));
        // The plugin header's (TES4)
        Assert.Equal(new HandLayouts.HeaderField(8, 4, 0, null, false, "TES4"), At("StarfieldModHeader", "Flags"));
        Assert.Equal(new HandLayouts.HeaderField(12, 4, 0, null, false, "TES4"), At("StarfieldModHeader", "FormID"));
        Assert.Equal(new HandLayouts.HeaderField(16, 4, 0, null, false, "TES4"), At("StarfieldModHeader", "Version"));
        Assert.Equal(new HandLayouts.HeaderField(20, 2, 0, null, false, "TES4"), At("StarfieldModHeader", "FormVersion"));
        // A field of the same name elsewhere is no header field
        Assert.Null(HandLayouts.HeaderPlace(m, m.Objects["Weapon"].Fields.First(f => f.Subrecord != null)));
        // The record XML places the same fields (layoutSubrecord="header"), and the two hand-written views are fields
        // derived from the Header's Bits
        Assert.Equal(new object?[] { "header", "8" }, fx.Sql("SELECT LayoutSubrecord, LayoutOffset FROM ObjectFields WHERE Object = 'MajorRecord' AND Path = 'MajorRecordFlagsRaw'")[0]);
        Assert.Equal(new object?[] { "Bool", "Derived" }, fx.Sql("SELECT Kind, (SELECT 'Derived' WHERE Source IS NOT NULL) FROM ObjectFields WHERE Object = 'MajorRecord' AND Path = 'IsCompressed'")[0]);
        Assert.Contains("0x40000", (string)fx.Sql("SELECT Note FROM ObjectFields WHERE Object = 'MajorRecord' AND Path = 'IsCompressed'")[0][0]!);
        Assert.Equal(Generator.Version, m.Meta["Generator"]);
        Assert.False(m.Meta.ContainsKey("OwnHandLayoutsSha256"));

        // The lists SFSearch's copies laid out until fixes.18 are the package's own (C47): a quest's aliases from ALST,
        // ALLS, ALCS or ALMI to ALED, and the rest
        foreach (var sig in new[] { "CELL", "GPOG", "IDLM", "INNR", "LGDI", "NAVI", "PACK", "PERS", "QUST", "REFR", "REGN", "SCEN", "TES4", "NPC_" })
            Assert.Contains(m.HandLayouts, h => h.Kind == "Record" && h.Sig == sig);
        Assert.Contains(m.HandLayouts, h => h.Kind == "AnyRecord" && h.Sig == "BFCB");
        Assert.Contains(m.HandLayouts.Single(h => h.Kind == "Record" && h.Sig == "QUST").Element.Descendants("Segments"), x => x.Attribute("field")?.Value == "Aliases" && x.Attribute("start")?.Value == "ALST,ALLS,ALCS,ALMI" && x.Attribute("end")?.Value == "ALED");
        // The quest's aliases list names ALED, the end marker its hand-written reader passes over
        Assert.Contains("ALED", (string)fx.Sql("SELECT CustomSubrecords FROM ObjectFields WHERE Object = 'Quest' AND Path = 'Aliases'")[0][0]!);
        // The forms fixes.20 added are known: Header, U64, Guid, View, Object class=
        string forms = Path.Combine(fx.WorkDir, "new-forms.xml");
        File.WriteAllText(forms, "<HandReadLayouts game=\"Starfield\" version=\"1\"><Header object=\"StarfieldMajorRecord\"><I32 field=\"MajorRecordFlagsRaw\" name=\"f\" /><View of=\"f\" field=\"MajorFlags\" optional=\"true\" /></Header>"
            + "<Define name=\"X\"><U64 field=\"A\" /><Guid field=\"B\" /><Object field=\"C\" class=\"NoSuchClass\" /></Define></HandReadLayouts>");
        var e = Assert.Throws<SchemaException>(() => HandLayouts.Load(forms, fx.Result.Model));
        Assert.DoesNotContain("<U64>", e.Message);
        Assert.DoesNotContain("<Guid>", e.Message);
        Assert.DoesNotContain("<View>", e.Message);
        Assert.Contains("Object class NoSuchClass", e.Message);
    }

    [Fact]
    public void ItemOrderAndEntryLengthTheLinkWalkNeedsAreKept()
    {
        // J83 (GAPS S105): a race's subgraph reads its fields in any order until its last field that is never null
        // (shortCircuitToLastRequired); a planet biome's flora has the length of an entry after its count
        // (expectedLengthLength). Both are in the schema, so the link walk lays them out as Mutagen reads them.
        Assert.Equal("ToLastRequired", (string)fx.Sql("SELECT ShortCircuit FROM Objects WHERE Name = 'Subgraph'")[0][0]!);
        Assert.Equal(1, fx.Scalar("SELECT count(*) FROM Objects WHERE ShortCircuit IS NOT NULL"));
        Assert.Contains("then the length of an entry (4 bytes)", (string)fx.Sql("SELECT Note FROM ObjectFields WHERE Object = 'PlanetBiome' AND Path = 'Flora'")[0][0]!);
    }

    [Fact]
    public void ThePluginHeadersMastersAreFieldsAndItsMasterListIsLaidOut()
    {
        // MasterReference comes from Mutagen.Bethesda.Core's XML (in Core's package since fixes.18, C49; written by hand
        // before), checked against the compiled IMasterReferenceGetter like every other object (GAPS S88)
        Assert.Equal(new object?[] { "String", "MAST", "NullTerminate" }, fx.Sql("SELECT Kind, Subrecord, TextForm FROM ObjectFields WHERE Object = 'MasterReference' AND Path = 'Master'")[0]);
        Assert.Equal(new object?[] { "UInt64", "DATA", 8L, 1L }, fx.Sql("SELECT Kind, Subrecord, Size, Nullable FROM ObjectFields WHERE Object = 'MasterReference' AND Path = 'FileSize'")[0]);
        Assert.Equal("MasterReference", fx.Sql("SELECT RefObject FROM ObjectFields WHERE Object = 'StarfieldModHeader' AND Path = 'MasterReferences[*]'")[0][0]);
        Assert.DoesNotContain("MasterReference", fx.Result.Reflect.NoType);
        Assert.DoesNotContain(fx.Result.Reflect.Missing, x => x.StartsWith("MasterReference.", StringComparison.Ordinal));
        Assert.Equal(1L, fx.Scalar("SELECT count(*) FROM Fields WHERE Type = 'StarfieldModHeader' AND Path = 'MasterReferences[*].Master'"));
        // Its list is cut at each MAST by Mutagen's TES4 layout (C57), each item a MAST and the DATA after it when there is one
        var tes4 = SchemaDb.Load(fx.Db).Model.HandLayouts.Single(h => h.Kind == "Record" && h.Sig == "TES4");
        Assert.Equal("StarfieldModHeader", tes4.Object);
        Assert.Contains(tes4.Element.Descendants("Segments"), x => x.Attribute("field")?.Value == "MasterReferences" && x.Attribute("start")?.Value == "MAST" && x.Attribute("reader")?.Value == "generated");
    }

    [Fact]
    public void ThePluginHeadersFieldsHaveXEditsNames()
    {
        // GAPS S90: SF1Edit 4.1.5q's export of the 370 plugin headers in Data, paired with Mutagen's ModHeader of each
        // (xedit-tes4-2026-11-15): every field Mutagen reads has its xEdit path, as every record type's fields do
        string? XEdit(string path) => fx.Sql($"SELECT XEditPath FROM PairNames WHERE Sig = 'TES4' AND Path = '{path}'") is { Count: > 0 } r ? r[0][0] as string : null;
        Assert.Equal(@"\HEDR - Header\Version", XEdit("Stats.Version"));
        Assert.Equal(@"\HEDR - Header\Number of Records", XEdit("Stats.NumRecords"));
        Assert.Equal(@"\HEDR - Header\Next Object ID", XEdit("Stats.NextFormID"));
        Assert.Equal(@"\ONAM - Overridden Forms\Form #*", XEdit("OverriddenForms[*]"));
        Assert.Equal(@"\Transient Types (CK only)\TNAM - Transient Type\FormType", XEdit("TransientTypes[*].FormType"));
        Assert.Equal(@"\Record Header\Form Version", XEdit("FormVersion"));
        Assert.Equal(@"\Record Header\Version Control Info 1", XEdit("Version"));
        Assert.Equal(@"\CNAM - Author", XEdit("Author"));
        Assert.Equal(@"\INCC - Interior Cell Count", XEdit("InteriorCellCount"));
        // A master's MAST: xEdit's file name is the whole of Mutagen's ModKey (the field's compiled type), not a part of it
        Assert.Equal(@"\Master Files\Master File\MAST - FileName", XEdit("MasterReferences[*].Master"));
        var mast = fx.Result.PairRows.Single(p => p.Sig == "TES4" && p.Subrecord == "MAST");
        Assert.Equal(("whole value", "FileName.String"), (mast.Match, mast.Part));
        // The flags: each flag pairs with its member (xEdit's Small is Mutagen's Light), and the set of flags takes the
        // name of the xEdit element they sit in, as every set of flags whose flags all sit in one element now does
        Assert.Equal(@"\Record Header\Record Flags", XEdit("Flags"));
        Assert.Equal("Record Flags", fx.Sql("SELECT XEdit FROM ObjectFields WHERE Object = 'StarfieldModHeader' AND Path = 'Flags'")[0][0]);
        Assert.Equal("Small", fx.Sql("SELECT XEditName FROM Enums WHERE Enum = 'StarfieldModHeader.HeaderFlag' AND Member = 'Light'")[0][0]);
        Assert.Equal(@"\XALG - Flags", fx.Sql("SELECT XEditPath FROM PairNames WHERE Sig = 'ACHR' AND Path = 'XalgFlags'")[0][0]);
        Assert.True(fx.Result.Pairs.FlagFieldsNamed >= 141, $"{fx.Result.Pairs.FlagFieldsNamed} sets of flags named after their element");
    }

    [Fact]
    public void EveryRecordsHeaderFieldsHaveXEditsNames()
    {
        // GAPS S97 (J73): xecmp pairs xEdit's \Record Header\... in every record type now (it did for TES4 only), and the
        // pairs come in through the pairs file like any other: the four whole fields on every record of every type
        string? XEdit(string sig, string path) => fx.Sql($"SELECT XEditPath FROM PairNames WHERE Sig = '{sig}' AND Path = '{path}'") is { Count: > 0 } r ? r[0][0] as string : null;
        var whole = new (string Path, string XEdit)[]
        {
            ("FormKey", @"\Record Header\FormID"), ("VersionControl", @"\Record Header\Version Control Info 1"),
            ("FormVersion", @"\Record Header\Form Version"), ("Version2", @"\Record Header\Version Control Info 2"),
        };
        var sigs = fx.Sql("SELECT DISTINCT Sig FROM Pairs WHERE XEditPath LIKE '\\Record Header%' AND Sig <> 'TES4'").Select(r => (string)r[0]!).ToList();
        Assert.True(sigs.Count >= 175, $"{sigs.Count} record types have record header pairs");
        foreach (var sig in sigs)
            foreach (var (path, xedit) in whole)
                Assert.Equal((sig, xedit), (sig, XEdit(sig, path)));
        // Each pair agrees on every record it was checked on, and matches the schema's field exactly
        var header = fx.Sql("SELECT Sig, MutagenPath, Checked, Agree, Match FROM Pairs WHERE XEditPath LIKE '\\Record Header\\%' AND Sig <> 'TES4' AND MutagenPath IN ('FormKey', 'VersionControl', 'FormVersion', 'Version2')");
        Assert.Equal(sigs.Count * 4, header.Count);
        Assert.All(header, r => Assert.Equal((r[0], r[1], r[2], "exact"), (r[0], r[1], r[3], r[4])));
        // The flags: the whole field where xEdit shows it (records with none set), each flag by its name, and the set of
        // flags named after the element it sits in
        Assert.Equal(@"\Record Header\Record Flags", XEdit("WEAP", "MajorRecordFlagsRaw"));
        Assert.Equal(@"\Record Header\Record Flags", XEdit("WEAP", "MajorFlags"));
        Assert.Equal("Non-Playable", fx.Sql("SELECT XEditName FROM Enums WHERE Enum = 'Weapon.MajorFlag' AND Member = 'NonPlayable'")[0][0]);
        // Once per field, the readable names are xEdit's
        object?[] Names(string obj, string path) => fx.Sql($"SELECT Readable, XEdit FROM ObjectFields WHERE Object = '{obj}' AND Path = '{path}'")[0];
        Assert.Equal(new object?[] { "FormID", "FormID" }, Names("MajorRecord", "FormKey"));
        Assert.Equal(new object?[] { "Version Control Info 1", "Version Control Info 1" }, Names("MajorRecord", "VersionControl"));
        Assert.Equal(new object?[] { "Record Flags", "Record Flags" }, Names("MajorRecord", "MajorRecordFlagsRaw"));
        Assert.Equal(new object?[] { "Form Version", "Form Version" }, Names("StarfieldMajorRecord", "FormVersion"));
        Assert.Equal(new object?[] { "Version Control Info 2", "Version Control Info 2" }, Names("StarfieldMajorRecord", "Version2"));
        // TES4 keeps its own (J64's) header pairs
        Assert.Equal(@"\Record Header\Version Control Info 1", XEdit("TES4", "Version"));
        // describe gives them
        var (exit, text) = fx.Describe("WEAP", "Record Flags");
        Assert.Equal(0, exit);
        Assert.Contains("MajorRecordFlagsRaw", text);
        (exit, text) = fx.Describe("Weapon.FormKey");
        Assert.Equal(0, exit);
        Assert.Contains(@"- Readable name: FormID (xEdit's name; xEdit path \Record Header\FormID", text);
    }

    [Fact]
    public void RecordTypesObjectsAndListsHaveXEditsNames()
    {
        const string BS = "\\";
        // GAPS S98 (J74): xEdit's name for every record type (inputs/xedit-record-names-*.tsv), and for each record
        // type's objects and lists the xEdit element their paired fields sit in
        Assert.Equal(0L, fx.Scalar("SELECT count(*) FROM RecordTypes WHERE XEdit IS NULL"));
        string Type(string sig) => (string)fx.Sql($"SELECT XEdit FROM RecordTypes WHERE Sig = '{sig}' LIMIT 1")[0][0]!;
        Assert.Equal("Weapon", Type("WEAP"));
        Assert.Equal("Main File Header", Type("TES4"));
        Assert.Equal("Placed Object", Type("REFR"));
        Assert.Equal("Non-Player Character", Type("NPC_"));
        Assert.Equal(fx.Result.Model.RecordTypes.Count, fx.Result.RecordTypesNamed);

        string? XEdit(string sig, string path) => fx.Sql($"SELECT XEditPath FROM PairNames WHERE Sig = '{sig}' AND Path = '{path}'") is { Count: > 0 } r ? r[0][0] as string : null;
        // The plugin header's (the gap's own examples)
        Assert.Equal(@"\HEDR - Header", XEdit("TES4", "Stats"));
        Assert.Equal(@"\Master Files", XEdit("TES4", "MasterReferences"));
        Assert.Equal(@"\Master Files\Master File", XEdit("TES4", "MasterReferences[*]"));
        Assert.Equal(@"\ONAM - Overridden Forms", XEdit("TES4", "OverriddenForms"));
        Assert.Equal(@"\Transient Types (CK only)\TNAM - Transient Type", XEdit("TES4", "TransientTypes[*]"));
        // A list is the element its items sit in; an object the element its fields share
        Assert.Equal(@"\Keywords\KWDA - Keywords", XEdit("WEAP", "Keywords"));
        Assert.Equal(@"\OBND - Object Bounds", XEdit("WEAP", "ObjectBounds"));
        Assert.Equal(@"\PUSH - Pickup Sound", XEdit("WEAP", "PickupSound"));
        Assert.Equal(@"\SPED - Movement Data\Max Speed Right", XEdit("MOVT", "MovementData.MaxSpeedRight"));
        // Components: each kind's data sits in a "Component Data - <kind>" element of its own, so the item is the element
        // the kinds share, in a record type with one kind only too (COBJ)
        foreach (var sig in new[] { "WEAP", "COBJ" })
        {
            Assert.Equal(@"\Base Form Components", XEdit(sig, "Components"));
            Assert.Equal(@"\Base Form Components\Component", XEdit(sig, "Components[*]"));
        }
        // A few fields that nest differently on the two sides (a condition's CIS2) don't cut their containers short
        Assert.Equal(@"\Objectives\Objective\Targets\Target", XEdit("QUST", "Objectives[*].Targets[*]"));
        Assert.Equal(@"\Objectives\Objective\Targets", XEdit("QUST", "Objectives[*].Targets"));
        // Every container's element holds three quarters of its paired fields at least (the rule), and is never the same
        // as the element of the container it sits in
        var rows = fx.Sql(@"SELECT DISTINCT pn.Sig, pn.Path, pn.XEditPath FROM PairNames pn JOIN Fields f ON f.Sig = pn.Sig AND f.PlainPath = pn.Path
            WHERE f.Kind IN ('Object', 'List', 'Array', 'Array2D', 'Dictionary', 'Gendered') OR f.RefObject IS NOT NULL");
        Assert.True(rows.Count >= 1400, $"{rows.Count} objects and lists of record types have xEdit's name");
        // Each paired field's own element: the pair's path, or for a component (X, Y, Z) or a flag the element above it
        static string Own(PairRow p) => p.Match is "component" or "flag" ? p.XEditPath[..p.XEditPath.LastIndexOf(BS)] : p.XEditPath;
        static string Field(PairRow p) => p.Match is "component" or "flag" ? p.MutagenPath[..p.MutagenPath.LastIndexOf('.')] : p.MutagenPath.Split('#')[0];
        var pairs = fx.Result.PairRows.Where(p => p.Match is "exact" or "whole value" or "component" or "flag").GroupBy(p => p.Sig).ToDictionary(g => g.Key, g => g.ToList());
        int checkedRows = 0;
        foreach (var r in rows)
        {
            string sig = (string)r[0]!, path = (string)r[1]!, el = (string)r[2]!;
            var below = pairs.GetValueOrDefault(sig, new()).Where(p => Field(p).StartsWith(path + ".", StringComparison.Ordinal) || Field(p).StartsWith(path + "[", StringComparison.Ordinal))
                .GroupBy(Field).Select(g => g.Any(p => Own(p).StartsWith(el + BS, StringComparison.Ordinal) || Own(p) == el)).ToList();
            if (below.Count == 0) continue;
            checkedRows++;
            // A list's element comes from its items' (one level up), so its fields sit in it whenever its items' do
            Assert.True(below.Count(x => x) * 4 >= below.Count * 3, $"{sig} {path} -> {el}: {below.Count(x => x)} of {below.Count} fields below it sit in it");
        }
        Assert.True(checkedRows >= 1000, $"{checkedRows} containers checked");
        // Once per field (ObjectFields), and in the readable names
        Assert.True(fx.Result.Pairs.ContainerFieldsNamed >= 1100, $"{fx.Result.Pairs.ContainerFieldsNamed} container fields named");
        Assert.Equal("Base Form Components", fx.Sql("SELECT Readable FROM Fields WHERE Sig = 'WEAP' AND PlainPath = 'Components' LIMIT 1")[0][0]);
        Assert.Equal("Links", fx.Sql("SELECT Readable FROM Fields WHERE Sig = 'TES4' AND PlainPath = 'TransientTypes[*].Links' LIMIT 1")[0][0]);
        // describe: the record type's xEdit name, and where a list's name comes from
        var (exit, text) = fx.Describe("TES4");
        Assert.Equal(0, exit);
        Assert.Contains("Record type. Signature TES4, Mutagen name StarfieldModHeader, xEdit name \"Main File Header\"", text);
        Assert.Contains("| MasterReferences | ", text);
        (exit, text) = fx.Describe("TES4", "Master");
        Assert.Contains("Master Files", text);
        (exit, text) = fx.Describe("StarfieldModHeader.MasterReferences");
        Assert.Equal(0, exit);
        Assert.Contains(@"- Readable name: Master Files (xEdit's name; xEdit path \Master Files; the xEdit element its items' paired fields sit in)", text);
    }

    [Fact]
    public void XEditsUnknownIsNoReadableName()
    {
        // The user, 2026-11-15: "Keep SFSearch's name where xEdit's is Unknown". xEdit's name is the readable name
        // everywhere except where it is "Unknown": there the readable name stays SFSearch's own (Mutagen's name spaced
        // out), and xEdit's stays in the xEdit name.
        // 1. Once per field: a readable name is "Unknown" only where Mutagen's own name is Unknown
        var fields = fx.Sql("SELECT Object, Path, Name, Readable, XEdit FROM ObjectFields WHERE XEdit IS NOT NULL");
        Assert.True(fields.Count > 3000);
        var unknownXEdit = fields.Where(r => PairMatcher.IsUnknownName((string)r[4]!)).ToList();
        Assert.True(unknownXEdit.Count >= 43, $"{unknownXEdit.Count} fields xEdit calls Unknown");
        foreach (var r in unknownXEdit)
            Assert.Equal(PairMatcher.SpacedOut((string)r[2]!), r[3]);
        Assert.Empty(fields.Where(r => PairMatcher.IsUnknownName((string)r[3]!) && !PairMatcher.IsUnknownName(PairMatcher.SpacedOut((string)r[2]!)))
            .Select(r => $"{r[0]}.{r[1]}"));
        // 2. Per record type and path: the same, and xEdit's path stays whole
        var pairs = fx.Sql("SELECT pn.Sig, pn.Path, pn.XEditPath, pn.Readable, f.Name FROM PairNames pn JOIN Fields f ON f.Sig = pn.Sig AND f.PlainPath = pn.Path GROUP BY pn.Sig, pn.Path");
        var unknownPairs = pairs.Where(r => PairMatcher.IsUnknownName(PairMatcher.LastName((string)r[2]!))).ToList();
        Assert.True(unknownPairs.Count >= 46, $"{unknownPairs.Count} paths xEdit calls Unknown");
        foreach (var r in unknownPairs)
            Assert.Equal(PairMatcher.SpacedOut((string)r[4]!), r[3]);
        Assert.Empty(pairs.Where(r => PairMatcher.IsUnknownName((string)r[3]!) && !PairMatcher.IsUnknownName(PairMatcher.SpacedOut((string)r[4]!)))
            .Select(r => $"{r[0]} {r[1]}"));
        // 3. Fields the decision was made on: COBJ's RECF (J64's sets of flags), INFO's INTV (before J64), TES4's CHGL
        object?[] Row(string sig, string path) => fx.Sql($"SELECT Readable, XEdit FROM Fields WHERE Sig = '{sig}' AND PlainPath = '{path}' LIMIT 1")[0];
        Assert.Equal(new object?[] { "Flags", @"\RECF - Unknown" }, Row("COBJ", "Flags"));
        Assert.Equal(new object?[] { "Start Scene Phase Index", @"\INTV - Unknown" }, Row("INFO", "StartScenePhaseIndex"));
        Assert.Equal(new object?[] { "CHGL", @"\CHGL - Unknown" }, Row("TES4", "CHGL"));
        Assert.Equal(new object?[] { "Flags", "Unknown" }, fx.Sql("SELECT Readable, XEdit FROM ObjectFields WHERE Object = 'ConstructibleObject' AND Path = 'Flags'")[0]);
        // A real xEdit name stays the readable name: J64's sets of flags (other non-names: XEditsNonNamesGiveWayToABetterOwnName)
        Assert.Equal("Flags", Row("ACHR", "XalgFlags")[0]);
        Assert.Equal("General Flags", Row("PACK", "Flags")[0]);
        // "Unknown": in any case, with or without the element's signature before it; nothing else
        Assert.True(PairMatcher.IsUnknownName("RECF - Unknown"));
        Assert.True(PairMatcher.IsUnknownName("unknown"));
        Assert.False(PairMatcher.IsUnknownName("Unknown #*"));
        Assert.False(PairMatcher.IsUnknownName("Unused"));
        // describe gives both names
        var (exit, text) = fx.Describe("ConstructibleObject.Flags");
        Assert.Equal(0, exit);
        Assert.Contains("- Readable name: Flags (Mutagen's name spaced out: xEdit calls it \"Unknown\"; xEdit path \\RECF - Unknown", text);
    }

    [Fact]
    public void XEditsNonNamesGiveWayToABetterOwnName()
    {
        // The user, 2026-11-15, "Yes, as recommended": the first rule (XEditsUnknownIsNoReadableName) for every non-name.
        // A non-name from xEdit ("Unknown", numbered "Unknown #n", "Unused", only a signature like "CTDA") never replaces a
        // real SFSearch name; xEdit's stays in its xEdit field. Where SFSearch's own name is no better (Mutagen's "Unknown"
        // beside "Unused", "NAM4" beside "Unknown #*"), xEdit's is kept. A list item's placeholder ("Keyword #*") is a name.
        // 1. Once per field: every readable name by the rule, and no xEdit non-name where SFSearch's own name says more
        var fields = fx.Sql("SELECT Object, Path, Name, Readable, XEdit FROM ObjectFields WHERE XEdit IS NOT NULL");
        Assert.Empty(fields.Where(r => (string)r[3]! != PairMatcher.ReadableName((string)r[4]!, (string)r[2]!)).Select(r => $"{r[0]}.{r[1]}"));
        Assert.Empty(fields.Where(r => r[3]!.Equals(r[4]) && PairMatcher.NameStrength((string)r[2]!) > PairMatcher.NameStrength((string)r[4]!))
            .Select(r => $"{r[0]}.{r[1]}"));
        var ownOverNonName = fields.Where(r => PairMatcher.IsNonName((string)r[4]!) && !PairMatcher.IsUnknownName((string)r[4]!) && !r[3]!.Equals(r[4])).ToList();
        Assert.True(ownOverNonName.Count >= 219, $"{ownOverNonName.Count} fields with an xEdit non-name other than Unknown take their own name");
        // The 215 conditions' FirstParameter: xEdit names the element, CTDA
        var ctda = fields.Where(r => (string)r[1]! == "FirstParameter" && (string)r[4]! == "CTDA").ToList();
        Assert.True(ctda.Count >= 215, $"{ctda.Count} conditions' FirstParameter");
        Assert.All(ctda, r => Assert.Equal("First Parameter", r[3]));
        // 2. Per record type and path: the same, with the Mutagen name of the field the path names
        var pairs = fx.Sql("SELECT pn.Sig, pn.Path, pn.XEditPath, pn.Readable, f.Name FROM PairNames pn JOIN Fields f ON f.Sig = pn.Sig AND f.PlainPath = pn.Path GROUP BY pn.Sig, pn.Path");
        Assert.Empty(pairs.Where(r => (string)r[3]! == PairMatcher.LastName((string)r[2]!) && PairMatcher.NameStrength((string)r[4]!) > PairMatcher.NameStrength((string)r[3]!))
            .Select(r => $"{r[0]} {r[1]}"));
        object?[] Row(string sig, string path) => fx.Sql($"SELECT Readable, XEdit FROM Fields WHERE Sig = '{sig}' AND PlainPath = '{path}' LIMIT 1")[0];
        // Numbered "Unknown #*": a real own name wins; a signature ("NAM4", "QDU Ps" from QDUPs) is no better
        Assert.Equal(new object?[] { "Connections", @"\FNAM - Connections\Unknown #*" }, Row("SFBK", "Connections[*]"));
        Assert.Equal("Links", Row("TES4", "TransientTypes[*].Links[*]")[0]);
        Assert.Equal("Unknown Strings", Row("PNDT", "Components[*].XMPM.UnknownStrings[*]")[0]);
        Assert.Equal("Unknown #*", Row("WTHR", "NAM4[*]")[0]);
        Assert.Equal("Unknown #*", Row("QUST", "QDUPs[*]")[0]);
        // "Unused": MOVT's Sprint wins; Mutagen's "Unknown" is no better
        Assert.Equal(new object?[] { "Sprint", @"\SPED - Movement Data\Max Speed Back\Unused" }, Row("MOVT", "MovementData.MaxSpeedBack.Sprint"));
        Assert.Equal("Unused", Row("MOVT", "MovementData.MaxSpeedForward.Unknown")[0]);
        Assert.Equal("Unused", Row("FACT", "VendorValues.Unknown")[0]);
        Assert.Equal(new object?[] { "Sprint", "Unused" }, fx.Sql("SELECT Readable, XEdit FROM ObjectFields WHERE Object = 'MovementSpeeds' AND Path = 'Sprint'")[0]);
        // WRLD's LodWaterHeight is the record's NAM4, xEdit's LOD Water Height: no longer paired with the planet content
        // manager component's NAM4 "Debug Color\Unused" (a wrong pair, fixed in the coverage project's xecmp, 2026-11-15)
        Assert.Equal(new object?[] { "LOD Water Height", @"\LOD Data\NAM4 - LOD Water Height" }, Row("WRLD", "LodWaterHeight"));
        Assert.Equal(0L, fx.Scalar(@"SELECT count(*) FROM Pairs WHERE XEditPath LIKE '\Base Form Components\%' AND MutagenPath NOT LIKE 'Components%'"));
        // A list item's placeholder stays
        Assert.Equal("Keyword #*", fx.Sql("SELECT Readable FROM ObjectFields WHERE Object = 'AObjectModification' AND Path = 'AttachParentSlots[*]'")[0][0]);
        // 3. The rule's edges
        Assert.Equal(0, PairMatcher.NameStrength("RECF - unknown"));
        foreach (var n in new[] { "Unknown #*", "Unknown #2", "Unknown 1", "Unknown2", "Unused", "unused 2", "CTDA", "NAM4", "QDUPs", "FNAM - CTDA" })
            Assert.True(PairMatcher.NameStrength(n) == 1, n);
        foreach (var n in new[] { "First Parameter", "Unknown Strings", "Keyword #*", "Name", "Type", "Sprint", "Unused Male" })
            Assert.True(PairMatcher.NameStrength(n) == 2, n);
        Assert.Equal("First Parameter", PairMatcher.ReadableName("CTDA", "FirstParameter"));
        Assert.Equal("Unused", PairMatcher.ReadableName("Unused", "Unknown"));
        Assert.Equal("Unknown #*", PairMatcher.ReadableName("Unknown #*", "NAM4"));
        Assert.Equal("XILS", PairMatcher.ReadableName("Unknown", "XILS"));
        Assert.Equal("Unknown", PairMatcher.ReadableName("Unknown", "Unknown"));
        Assert.Equal("Base Damage", PairMatcher.ReadableName("Base Damage", "Unknown"));
        // describe gives both names
        Assert.Contains("- Readable name: Sprint (Mutagen's name spaced out: xEdit calls it \"Unused\"", fx.Describe("MovementSpeeds.Sprint").Text);
        Assert.Contains("- Readable name: First Parameter (Mutagen's name spaced out: xEdit calls it \"CTDA\"", fx.Describe("BiomeHasKeywordConditionData.FirstParameter").Text);
        Assert.Contains("- Readable name: Unused (xEdit's name", fx.Describe("VendorValues.Unknown").Text);
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
            Assert.Equal(n, fx.Scalar($"SELECT count(*) FROM ObjectFields WHERE TextForm = '{form}' AND {OneRowPerElement} AND Object NOT IN ('MajorRecord', 'MasterReference')")); // their EDID and MAST are written by hand
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
        // A condition's CTDA: the places Mutagen's property manifest gives (C60 W6): the byte its Flags reader reads (0), Unknown1
        // (1-3) and the step that reads the rest (4); none of the XML-order ones (CompareOperator 0, Flags 4, Data.RunOnType 0, …)
        Assert.Equal(new object?[] { "Flags", 0L, "FunctionParse", 4L, "Unknown1", 1L }, fx.Sql("SELECT Path, Offset FROM ObjectFields WHERE Object IN ('Condition', 'ConditionFloat', 'ConditionGlobal', 'ConditionData') AND Offset IS NOT NULL ORDER BY Path").SelectMany(r => r).ToArray());
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM Fields WHERE Type = 'ConstructibleObject' AND Path LIKE 'Conditions[*].%' AND Offset IS NOT NULL"));
        // A perk effect's PRKE: Rank and Priority (the file has the kind of effect at 0, the rank at 1, the priority at 2)
        Assert.Equal(0, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Object = 'APerkEffect' AND Offset IS NOT NULL"));
        // An object template's include sits after the keywords in OBTS, where the hand-written reader puts it; since fixes.24
        // (C81) the manifest says the include's own generated reader reads it from its start: its fields' offsets count from
        // the include (Mod 0, AttachPointIndex 4, Optional 5, DontUseAll 6), not from OBTS
        Assert.Equal(new object?[] { "AttachPointIndex", 4L, "DontUseAll", 6L, "Mod", 0L, "Optional", 5L }, fx.Sql("SELECT Path, Offset FROM ObjectFields WHERE Object = 'ObjectTemplateInclude' AND Offset IS NOT NULL ORDER BY Path").SelectMany(r => r).ToArray());
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
                    if (model.Objects[o.Attribute("name")!.Value].HandRead?.Contains($"{o.Attribute("name")!.Value}'s hand-written reader of {f.Attribute("name")!.Value}") == true) found++;
                }
        Assert.True(custom > 60, $"{custom} custom fields in the XML");
        Assert.Equal(custom, found);
        Assert.Equal(fx.Result.HandReadObjects, fx.Scalar("SELECT count(*) FROM Objects WHERE HandRead IS NOT NULL"));
        Assert.Equal("Condition's hand-written reader of Flags; Condition's hand-written reader of FunctionParse",
            fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'Condition'")[0][0]);
        Assert.Equal("via PerkRank.Effects (PerkRank's hand-written reader of Effects)", fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'APerkEffect'")[0][0]);
        // Since fixes.24 (C81) which objects hand-written code enters is the manifest's enteredBy: a condition's data kind is
        // made by Condition's reader, which then hands it to the kind's generated reader from its start, so it isn't one;
        // ConditionData's own fields read by hand (its RunOnType, Reference ...) still name the code
        Assert.Null(fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'GetGlobalValueConditionData'")[0][0]);
        Assert.Equal("hand-written Mutagen code", fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'ConditionData'")[0][0]);
        Assert.Equal("via APackageTarget.CreateFromBinary", fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'PackageTargetSpecificReference'")[0][0]);
        Assert.Null(fx.Sql("SELECT HandRead FROM Objects WHERE Name = 'Weapon'")[0][0]); // the record header's flags move nothing

        // describe says it on the object, and for a field gives no offset and names the code
        Assert.Contains("\nRead in part by hand-written Mutagen code: Condition's hand-written reader of Flags; Condition's hand-written reader of FunctionParse.", fx.Describe("Condition").Text);
        Assert.Contains("| CompareOperator | CTDA | Enum CompareOperator, 4 B |  | read by hand-written Mutagen code; Mutagen's layout: offset 0, bits 5-7; filled by Mutagen's hand-written reader of Flags |", fx.Describe("Condition").Text);
        Assert.Contains("entered through PerkRank.Effects", fx.Describe("APerkEffect").Text);
        Assert.Contains("- File address: CTDA (no offset: Mutagen's hand-written code places it: ConditionFloat's customBinaryEnd (Normal), filling Data.StringParameter1, Data.StringParameter2; Condition's hand-written reader of Flags",
            fx.Describe("ConditionFloat.ComparisonValue").Text);
        Assert.DoesNotContain("Read in part by hand-written", fx.Describe("WEAP").Text);
    }

    /// <summary>
    /// GAPS S38: every CustomLogic step names the subrecords it reads (the XML's recordType and &lt;RecordType&gt; items;
    /// a step inside a subrecord, that subrecord), and the fields its compiled reader fills carry them. A PERS's Items
    /// (from DAT2, with XXXX long forms) had no subrecord in the schema.
    /// </summary>
    [Fact]
    public void EveryCustomLogicStepNamesItsSubrecordsAndTheFieldsItFills()
    {
        var model = SchemaDb.Load(fx.Db).Model;
        int steps = 0, own = 0;
        foreach (var d in XmlDocs())
            foreach (var o in d.Descendants().Where(e => e.Name.LocalName == "Object"))
                foreach (var step in o.Descendants().Where(e => e.Name.LocalName == "CustomLogic"))
                {
                    steps++;
                    var sigs = new[] { step.Attribute("recordType")?.Value }.Concat(step.Elements().Where(e => e.Name.LocalName == "RecordType").Select(e => e.Value.Trim()))
                        .Where(s => s is { Length: > 0 }).Distinct().ToList();
                    var row = model.Objects[o.Attribute("name")!.Value].Fields.Single(f => f.Kind == "CustomLogic" && f.Name == step.Attribute("name")!.Value);
                    if (sigs.Count > 0) { own++; Assert.Equal(string.Join(",", sigs), row.CustomSubrecords); }
                    else if (model.Objects[row.Object].ObjType == "Group") Assert.Null(row.CustomSubrecords);
                    else Assert.NotNull(row.CustomSubrecords); // inside its object's subrecord
                }
        var hf = fx.Result.HandFills;
        Assert.Equal(40, steps);
        Assert.Equal((40, 29, 9, 2), (hf.Steps, hf.OwnSubrecords, hf.InSubrecord, hf.GroupHeader));
        Assert.Equal(own, hf.OwnSubrecords);
        Assert.Equal(steps - hf.GroupHeader, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Kind = 'CustomLogic' AND CustomSubrecords IS NOT NULL"));
        // The steps whose reader fills no field of its object: Mutagen drops what they read (BFCB, TIFC, IDLC, XPPA,
        // NAM3), or other code reads it (EPFT by the perk rank's effects, PTDA by the package data, MGEF's associated
        // item by the archetype). Any other step fills fields
        Assert.Equal(new[] { "AComponent.BFCBString (BFCB)", "APackageTarget.DataParse (PTDA)", "APerkEffect.FunctionParameters (EPFT)", "DialogTopic.InfoListCount (TIFC)",
            "IdleMarker.AnimationCount (IDLC)", "MagicEffect.AssociatedItem (DATA)", "Patrol.PatrolScriptMarker (XPPA)", "Race.NAM3 (NAM3)" }, hf.FillNothing);
        Assert.Equal(30, hf.StepsFilling);
        Assert.Equal(steps - hf.GroupHeader - hf.FillNothing.Count, fx.Scalar("SELECT count(*) FROM ObjectFields WHERE Kind = 'CustomLogic' AND CustomFills IS NOT NULL"));
        // Every field Mutagen reads by hand outside a subrecord's bytes has a reader that fills it, or its subrecord, or a
        // layout attribute: Mutagen's files (and the compiled readers) leave none unsaid. C63's UnknownComponent says so
        // since fixes.22 (C73, GAPS S163): its ReadRaw fills TypeName (the BFCB text) and Data (the subrecords up to BFCE)
        Assert.Empty(hf.FieldsNoStep);

        // Fields and their subrecords
        string? Row(string obj, string field, string col) => fx.Sql($"SELECT {col} FROM ObjectFields WHERE Object = '{obj}' AND Path = '{field}'")[0][0] as string;
        Assert.Equal(("XXXX,DAT2", "ItemsParse"), (Row("PERS", "Items", "CustomSubrecords"), Row("PERS", "Items", "FilledBy")));
        Assert.Equal("Items", Row("PERS", "ItemsParse", "CustomFills"));
        // The step names five; Mutagen's QUST layout adds ALED, the end marker the reader passes over (GAPS S67; SFSearch's
        // own copy until fixes.18)
        Assert.Equal(("ANAM,ALST,ALLS,ALCS,ALMI,ALED", "AliasParse"), (Row("Quest", "Aliases", "CustomSubrecords"), Row("Quest", "Aliases", "FilledBy")));
        Assert.Equal(("QNAM,PNAM,JNAM,NAM1", "CloudXSpeeds,CloudColors,CloudAlphas,DisabledCloudLayers"), (Row("Weather", "Clouds", "CustomSubrecords"), Row("Weather", "Clouds", "FilledBy")));
        Assert.Equal("Clouds", Row("Weather", "CloudColors", "CustomFills")); // reads FormVersion to pick its layout, doesn't fill it
        Assert.Equal(("DNAM,SNAM,PNAM", "TypeParse,DataParse"), (Row("Note", "Data", "CustomSubrecords"), Row("Note", "Data", "FilledBy")));
        Assert.Equal(("NAME", "TrapForm"), (Row("PlacedArrow", "Projectile", "CustomSubrecords"), Row("PlacedArrow", "Projectile", "FilledBy"))); // a kind of the step's object
        Assert.Equal(("ONA2", "FlagParse"), (Row("NpcRaceOverride", "Movement", "CustomSubrecords"), Row("NpcRaceOverride", "Movement", "FilledBy"))); // inside ONA2
        Assert.Equal(("PKCU", "PackageTemplate"), (Row("Package", "DataInputVersion", "CustomSubrecords"), Row("Package", "DataInputVersion", "FilledBy"))); // a binary="Custom" field's reader
        Assert.Equal(("ReadRaw", "ReadRaw"), (Row("UnknownComponent", "TypeName", "FilledBy"), Row("UnknownComponent", "Data", "FilledBy"))); // the XML's handReadFills (C73)
        // FSTS's Count step names XCNT and DATA itself since fixes.18 (C36): the five lists are in DATA. (Before, only
        // HandReadLayouts.xml named DATA.) So does a package's DataInputNames step name UNAM, BNAM and PNAM (C38)
        Assert.Equal(("XCNT,DATA", "Count"), (Row("FootstepSet", "WalkFootsteps", "CustomSubrecords"), Row("FootstepSet", "WalkFootsteps", "FilledBy")));
        Assert.Equal("XCNT,DATA", Row("FootstepSet", "Count", "CustomSubrecords"));
        Assert.Equal("UNAM,BNAM,PNAM", Row("Package", "DataInputNames", "CustomSubrecords"));
        // Mutagen's layouts (C47; SFSearch's own copies until fixes.18, GAPS S67) add the subrecords its hand-written
        // readers go on to read: a package's data input blocks (a target's CITC, as FillPackageData reads it), a quest's
        // unused conditions' CTDA and its aliases' ALED
        Assert.Equal(new[] { "ANAM", "CNAM", "TPIC", "STSC", "PTDA", "CITC", "PDTO" }.Select(x => $"Package.Data {x}")
            .Concat(new[] { "Quest.UnusedConditions CTDA", "Quest.Aliases ALED" }), hf.FromLayouts);
        // A step's fills= may name a property Mutagen's hand-written code adds beyond its XML (C51)
        Assert.Equal(new[] { "Package.DataInputNames: ProcedureTreeDataOrder" }, hf.FillsBeyondXml);
        Assert.Equal(HandFills.CustomEnd, Row("Cell", "Temporary", "FilledBy")); // the record's child group
        Assert.Null(Row("Cell", "Temporary", "CustomSubrecords"));

        // describe: the step, the field, and the field by its subrecord
        var pers = fx.Describe("PERS").Text;
        Assert.Contains("| ItemsParse | (XXXX DAT2) | CustomLogic |  | read by hand-written Mutagen code; a parsing step, not a value: no property; reads XXXX, DAT2; fills Items |", pers);
        Assert.Contains("| Items | (XXXX DAT2) | List |  | **unknown**; read by hand-written Mutagen code; filled by Mutagen's hand-written code (ItemsParse) from XXXX, DAT2;", pers);
        Assert.Contains("- File address: PERS/XXXX, PERS/DAT2, read by Mutagen's hand-written code (ItemsParse)", fx.Describe("PERS.Items").Text);
        var (exit, dat2) = fx.Describe("PERS/DAT2");
        Assert.Equal(0, exit);
        Assert.Contains("\n| Items | (XXXX DAT2) | List |", dat2);
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

        // Core's record XML is read from the Core package beside it (fixes.18, C49): MajorRecord and MasterReference
        Assert.EndsWith($"mutagen.bethesda.core/{src.Version}/loqui/Mutagen.Bethesda.Core/Plugins/Records", src.CoreRecordsDir.Replace(Path.DirectorySeparatorChar, '/'));
        Assert.Equal("Mutagen.Bethesda.Core/Plugins/Records/MajorRecord.xml", fx.Sql("SELECT File FROM Objects WHERE Name = 'MajorRecord'")[0][0]);

        // A copy of the packages' folders (NuGet's layout: <id>\<version>): as restored it passes; another commit, another
        // version, an XML file edited or missing after the restore, a package without its record XML, or Core's package
        // missing, each fails
        string root = Path.Combine(fx.WorkDir, "packages-copy");
        string copy = Path.Combine(root, Inputs.PackageId, src.Version);
        string coreDir = Path.GetFullPath(Path.Combine(src.CoreRecordsDir, "..", "..", "..", ".."));
        void CopyPackage(string from, string to)
        {
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".nuspec", StringComparison.Ordinal) || f.EndsWith(".nupkg", StringComparison.Ordinal) || Path.GetRelativePath(from, f).StartsWith("loqui", StringComparison.Ordinal)))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target);
            }
        }
        void Fresh()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            CopyPackage(fx.PackageDir, copy);
            CopyPackage(coreDir, Path.Combine(root, Inputs.CorePackageId, src.Version));
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
        Fresh();
        File.AppendAllText(Path.Combine(root, Inputs.CorePackageId, src.Version, Inputs.CoreRecordsPath, "MajorRecord.xml"), "<!-- edited -->");
        Assert.Contains(Inputs.CheckPackage(copy, version).Problems, p => p.Contains("MajorRecord.xml differs"));
        Directory.Delete(Path.Combine(root, Inputs.CorePackageId), true);
        Assert.Contains(Inputs.CheckPackage(copy, version).Problems, p => p.Contains("There is no restored Mutagen.Bethesda.Core"));

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
