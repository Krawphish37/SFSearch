// Evidence for fields inside list items (GAPS row S6, fixes F6 and F6r): for each shape of list, the field's exact
// bytes, and those bytes decoded here, on their own, must be Mutagen's value read through Mutagen's own typed record.
// F6r adds a localized plugin's texts (a 4-byte string ID: the ID must be the one Mutagen read, and the text the one it
// names), conditions (CTDA by Mutagen's condition reader), perk effects (PRKE … PRKF by Mutagen's perk effect reader)
// and gendered values (male and female halves as Mutagen's gendered reader splits them). Fix S35-S37 adds entries and
// fields of variable size inside one subrecord (a list's own count, texts with a length before them or a zero byte
// after them, walked as Mutagen read them), and a translated text looked up in the one strings table the schema names.
using System.Buffers.Binary;
using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using Noggog;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Records.Tests;

static class EvidenceItems
{
    /// <summary>Every difference is listed before the test fails (the first one alone hides the rest).</summary>
    static readonly List<string> Failed = new();
    static void Check(bool ok, string what) { if (!ok) Failed.Add(what); }

    sealed record Case(string Plugin, string Record, string Field, string Shape, string Subrecord, int Offset, int Size, Func<IMajorRecordGetter, byte[], (bool Same, string Mutagen)> Same);

    static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    static float F(byte[] b) => BinaryPrimitives.ReadSingleLittleEndian(b);
    static uint U32(byte[] b) => BinaryPrimitives.ReadUInt32LittleEndian(b);

    /// <summary>A link's slice against Mutagen's FormKey: the label the slice reads as names that FormKey (a master's
    /// name in any letter case: a DLC's master list writes "starfield.esm").</summary>
    static (bool, string) Link(string? reads, Mutagen.Bethesda.Plugins.FormKey key) => (reads != null && reads.EndsWith("(" + key + ")", StringComparison.OrdinalIgnoreCase), key.ToString());

    /// <summary>A localized text's slice against Mutagen's text: the 4 bytes are the string ID Mutagen read the text by
    /// (its StringsKey), the slice reads as that text, in the strings file of the table Mutagen read it from.</summary>
    static (bool, string) Text(string? reads, ITranslatedStringGetter? text, byte[] b)
    {
        if (text is not TranslatedString { StringsKey: uint key } t) return (false, "(Mutagen kept no string ID for the text)");
        string file = "starfield_en." + StringsUtility.GetSourceString(t.StringsKeySource).ToLowerInvariant();
        bool same = b.Length == 4 && U32(b) == key && reads != null && reads.Contains($"\"{t.String}\"", StringComparison.Ordinal) && reads.Contains(file, StringComparison.Ordinal);
        return (same, $"string ID {key:X8} in {file}: \"{t.String}\"");
    }

    public static string Run(Ctx ctx)
    {
        Failed.Clear();
        string said;
        try { said = RunAll(ctx); }
        catch (Exception x) when (Failed.Count > 0) { Failed.Add($"stopped: {x}"); said = ""; }
        Ctx.Check(Failed.Count == 0, $"{Failed.Count} differences:" + Environment.NewLine + string.Join(Environment.NewLine, Failed));
        return said;
    }

    static string RunAll(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use("darkstar.esm", "darkstarpatch_atom74.esm", "Starfield.esm", "SFBGS003.esm", "ShatteredSpace.esm");
        string? reads = null;
        var cases = new List<Case>
        {
            // A keyword list: an array of FormIDs in one subrecord
            new("darkstar.esm", "0003C2", "Keywords[3]", "keyword list (array in KWDA)", "KWDA", 12, 4,
                (r, b) => Link(reads, ((IWeaponGetter)r).Keywords![3].FormKey)),
            // A list of fixed-size structs in one subrecord
            new("darkstar.esm", "0003C2", "DamageTypes[1].Value", "damage types (12-byte entries in DAMA)", "DAMA", 16, 4,
                (r, b) => { uint m = ((IWeaponGetter)r).DamageTypes![1].Value; return (U32(b) == m, m.ToString(CultureInfo.InvariantCulture)); }),
            // A component: a run of subrecords from BFCB to BFCE
            new("darkstar.esm", "0003C2", "Components[0].RigFile", "component (BFCB … BFCE)", "BNAM", 0, 46,
                (r, b) => { string m = ((IAnimationGraphComponentGetter)((IWeaponGetter)r).Components[0]).RigFile!; return (System.Text.Encoding.ASCII.GetString(b, 0, b.Length - 1) == m && b[^1] == 0, m); }),
            // An object template's include, in OBTS as Mutagen's hand-written reader lays it out
            new("darkstar.esm", "0003C2", "ObjectTemplates[0].Includes[6].Mod", "object template include (7-byte entries in OBTS)", "OBTS", 60, 4,
                (r, b) => Link(reads, ((IWeaponGetter)r).ObjectTemplates![0].Includes[6].Mod.FormKey)),
            // An OMOD's property, in DATA (24-byte entries after the name, slots and includes)
            new("darkstarpatch_atom74.esm", "00081F", "Properties[2].Value", "OMOD property (24-byte entries in DATA)", "DATA", -1, 4,
                (r, b) => { float m = ((IObjectModFormLinkFloatPropertyGetter<Weapon.Property>)((IWeaponModificationGetter)r).Properties[2]).Value; return (F(b) == m, m.ToString("R", CultureInfo.InvariantCulture)); }),
            new("darkstarpatch_atom74.esm", "00081F", "Properties[2].Record", "OMOD property's link", "DATA", -1, 4,
                (r, b) => Link(reads, ((IObjectModFormLinkFloatPropertyGetter<Weapon.Property>)((IWeaponModificationGetter)r).Properties[2]).Record.FormKey)),
            // A leveled list entry: LVLO and what follows it, counted by LLCT
            new("Starfield.esm", "0032ED", "Entries[4].Count", "leveled list entry (LVLO, consecutive)", "LVLO", 8, 2,
                (r, b) => { ushort m = ((ILeveledItemGetter)r).Entries![4].Count; return (BinaryPrimitives.ReadUInt16LittleEndian(b) == m, m.ToString(CultureInfo.InvariantCulture)); }),
            new("Starfield.esm", "001C93", "Entries[4].Reference", "leveled list entry with conditions (LVLO CTDA…)", "LVLO", 4, 4,
                (r, b) => Link(reads, ((ILeveledItemGetter)r).Entries![4].Reference.FormKey)),
            // A counted list with each entry's length after its count (expectedLengthLength, J83): a planet biome's flora
            new("ShatteredSpace.esm", "000801", "Biomes[0].Flora[0].Flora", "planet biome flora (count, entry length, 9-byte entries in PPBD)", "PPBD", 68, 4,
                (r, b) => Link(reads, ((IPlanetGetter)r).Biomes[0].Flora[0].Flora.FormKey)),
            // An object template's property, in the template's OBTS
            new("Starfield.esm", "0028A5", "ObjectTemplates[0].Properties[0].Record", "object template property (24-byte entries in OBTS)", "OBTS", 51, 4,
                (r, b) => Link(reads, ((IObjectModFormLinkIntPropertyGetter<Armor.Property>)((IArmorGetter)r).ObjectTemplates![0].Properties[0]).Record.FormKey)),
            // A value the JSON leaves out at its default, inside an item: Mutagen writes it all the same
            new("Starfield.esm", "0028A5", "ObjectTemplates[0].Properties[0].Value", "a default inside a template property", "OBTS", 55, 4,
                (r, b) => { uint m = ((IObjectModFormLinkIntPropertyGetter<Armor.Property>)((IArmorGetter)r).ObjectTemplates![0].Properties[0]).Value; return (U32(b) == m, m.ToString(CultureInfo.InvariantCulture)); }),
            // A translated name in a plugin with no strings files holds its text: checked against Mutagen's text
            new("darkstar.esm", "0003C2", "ObjectTemplates[1].Name", "object template name (FULL between OBTE and OBTS)", "FULL", 0, -1,
                (r, b) => { string m = ((IWeaponGetter)r).ObjectTemplates![1].Name!.String!; return (System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0') == m, m); }),

            // ---- F6r: texts of a localized plugin (a 4-byte string ID), at the top of the record and in a list item
            new("Starfield.esm", "0476C4", "Name", "localized text (string ID in FULL, .strings)", "FULL", 0, 4,
                (r, b) => Text(reads, ((IWeaponGetter)r).Name, b)),
            new("Starfield.esm", "0476C4", "ObjectTemplates[0].Name", "localized text in a list item (FULL of a template)", "FULL", 0, 4,
                (r, b) => Text(reads, ((IWeaponGetter)r).ObjectTemplates![0].Name, b)),
            new("Starfield.esm", "00423B", "Description", "localized text (string ID in DESC, .dlstrings)", "DESC", 0, 4,
                (r, b) => Text(reads, ((IConstructibleObjectGetter)r).Description, b)),
            // ---- F6r: conditions (CTDA, by Mutagen's condition reader)
            new("Starfield.esm", "00423B", "Conditions[0].Data.FirstParameter", "condition's function parameter (CTDA offset 12)", "CTDA", 12, 4,
                (r, b) => Link(reads, ((IGetGlobalValueConditionDataGetter)((IConstructibleObjectGetter)r).Conditions![0].Data).FirstParameter.Link.FormKey)),
            new("Starfield.esm", "00423B", "Conditions[0].ComparisonValue", "condition's comparison value (CTDA offset 4)", "CTDA", 4, 4,
                (r, b) => { float m = ((IConditionFloatGetter)((IConstructibleObjectGetter)r).Conditions![0]).ComparisonValue; return (F(b) == m, m.ToString("R", CultureInfo.InvariantCulture)); }),
            new("Starfield.esm", "00423B", "Conditions[0].Data.ParameterThree", "condition's parameter 3 (CTDA offset 28, a default)", "CTDA", 28, 4,
                (r, b) => { int m = ((IConstructibleObjectGetter)r).Conditions![0].Data.ParameterThree; return (BinaryPrimitives.ReadInt32LittleEndian(b) == m, m.ToString(CultureInfo.InvariantCulture)); }),
            new("Starfield.esm", "0EA114", "Ranks[0].Effects[0].Conditions[0].Conditions[0].CompareOperator", "condition's compare operator (top 3 bits of CTDA byte 0)", "CTDA", 0, 1,
                (r, b) => { var m = PerkEffect(r, 0).Conditions[0].Conditions[0].CompareOperator; return ((b[0] >> 5) == (int)m, m.ToString()); }),
            // ---- F6r: perk effects (PRKE … PRKF, by Mutagen's perk effect reader)
            new("Starfield.esm", "0EA114", "Ranks[0].Effects[0].Value", "perk effect's value (EPFD)", "EPFD", 0, 4,
                (r, b) => { float m = ((IPerkEntryPointModifyValueGetter)PerkEffect(r, 0)).Value!.Value; return (F(b) == m, m.ToString("R", CultureInfo.InvariantCulture)); }),
            new("Starfield.esm", "0EA114", "Ranks[0].Effects[5].Priority", "perk effect's priority (PRKE offset 2)", "PRKE", 2, 1,
                (r, b) => { byte m = PerkEffect(r, 5).Priority; return (b[0] == m, m.ToString(CultureInfo.InvariantCulture)); }),
            new("Starfield.esm", "0EA114", "Ranks[0].Effects[1].Modification", "perk effect's modification (the function's number in DATA)", "DATA", 1, 1,
                (r, b) => { var m = ((IPerkEntryPointModifyValueGetter)PerkEffect(r, 1)).Modification; return (b[0] == 3 && m == PerkEntryPointModifyValue.ModificationType.Multiply, m.ToString()); }),
            // ---- fixes.15 (C196): the places the XML gave only in a comment, now its attributes; a modification's layoutMap
            new("Starfield.esm", "078756", "Conditions[1].ComparisonValue", "a global as the comparison value (ConditionGlobal, CTDA offset 4)", "CTDA", 4, 4,
                (r, b) => Link(reads, ((IConditionGlobalGetter)((IConstructibleObjectGetter)r).Conditions![1]).ComparisonValue.FormKey)),
            new("Starfield.esm", "078756", "Conditions[1].Unknown2", "a condition's Unknown2 (CTDA offset 10)", "CTDA", 10, 2,
                (r, b) => { ushort m = ((IConstructibleObjectGetter)r).Conditions![1].Unknown2; return (BinaryPrimitives.ReadUInt16LittleEndian(b) == m && m != 0, m.ToString(CultureInfo.InvariantCulture)); }),
            new("Starfield.esm", "00B964", "Ranks[0].Effects[0].Modification", "an actor value modification (the function's number in DATA, layoutMap)", "DATA", 1, 1,
                (r, b) => { var m = ((IPerkEntryPointModifyActorValueGetter)PerkEffect(r, 0)).Modification; return (b[0] == 14 && m == PerkEntryPointModifyActorValue.ModificationType.MultiplyOnePlusAVMult, m.ToString()); }),
            new("Starfield.esm", "0EA114", "Ranks[0].Effects[1].Conditions[1].Conditions[0].Data.FirstParameter", "condition inside a perk effect (PRKC CTDA)", "CTDA", 12, 4,
                (r, b) => Link(reads, ((IHasKeywordConditionDataGetter)PerkEffect(r, 1).Conditions[1].Conditions[0].Data).FirstParameter.Link.FormKey)),
            // ---- fixes.14: scene phases cut at their markers, the completion conditions between two NEXT (C183)
            new("Starfield.esm", "005636", "Phases[7].CompletionConditions[1].Data.RunOnType", "a scene phase's completion condition (between two NEXT, C183)", "CTDA", 20, 4,
                (r, b) => { var m = ((ISceneGetter)r).Phases[7].CompletionConditions[1].Data.RunOnType; return (U32(b) == (uint)m, m.ToString()); }),
            new("Starfield.esm", "005636", "Phases[7].CompletionConditions[0].Data.FirstParameter", "a link in a scene phase's completion condition", "CTDA", 12, 4,
                (r, b) => Link(reads, ((IIsSceneActionCompleteConditionDataGetter)((ISceneGetter)r).Phases[7].CompletionConditions[0].Data).FirstParameter.Link.FormKey)),
            new("Starfield.esm", "005636", "Phases[6].StartConditions[0].Data.FirstParameter", "a scene phase's start condition (before the first NEXT)", "CTDA", 12, 4,
                (r, b) => Link(reads, ((IGetGlobalValueConditionDataGetter)((ISceneGetter)r).Phases[6].StartConditions[0].Data).FirstParameter.Link.FormKey)),
            new("Starfield.esm", "000856", "Phases[2].Name", "a scene phase's name (phases cut at HNAM ... HNAM)", "NAM0", 0, 14,
                (r, b) => { string m = ((ISceneGetter)r).Phases[2].Name!; return (System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0') == m, m); }),
            // ---- fixes.14: Mutagen's hand-read layouts (HandReadLayouts.xml, C186), walked from the schema
            new("Starfield.esm", "00015E", "Clouds[0].YSpeed", "a weather cloud's speed (RNAM, the layout's encoding (b - 127) / 1270)", "RNAM", 0, 1,
                (r, b) => { float m = ((IWeatherGetter)r).Clouds[0].YSpeed.GetValueOrDefault(); return (Math.Abs((b[0] - 127) / 1270.0 - m) < 1e-5, m.ToString("R", CultureInfo.InvariantCulture)); }),
            new("Starfield.esm", "00015E", "Clouds[5].Enabled", "a weather cloud's enabled bit (NAM1: bit 5 of its uint32, inverted)", "NAM1", 0, 4,
                (r, b) => { bool m = ((IWeatherGetter)r).Clouds[5].Enabled.GetValueOrDefault(); return (((b[0] & 0x20) == 0) == m, m.ToString()); }),
            new("Starfield.esm", "00015E", "DirectionalAmbientLightingColors.Day.DirectionalXPlus", "a weather's ambient color (the 2nd DALC, R G B A)", "DALC", 0, 4,
                (r, b) => { var m = ((IWeatherGetter)r).DirectionalAmbientLightingColors!.Day.DirectionalXPlus; return (m.R == b[0] && m.G == b[1] && m.B == b[2] && m.A == b[3], m.ToString()); }),
            new("Starfield.esm", "03E091", "WalkFootsteps[0]", "a footstep set's walk list (last in DATA, the counts in XCNT)", "DATA", 76, 4,
                (r, b) => Link(reads, ((IFootstepSetGetter)r).WalkFootsteps[0].FormKey)),
            new("Starfield.esm", "092CAC", "IdleAnimations.Animations[0]", "a package's idles (IDLA, after IDLF/IDLC/IDLT in any order)", "IDLA", 0, 4,
                (r, b) => Link(reads, ((IPackageGetter)r).IdleAnimations!.Animations![0].FormKey)),
            new("Starfield.esm", "00118F", "VirtualMachineAdapter.Fragments[0].Stage", "a quest script fragment's stage (VMAD, after the scripts)", "VMAD", 66, 4,
                (r, b) => { uint m = ((IQuestGetter)r).VirtualMachineAdapter!.Fragments[0].Stage; return (U32(b) == m, m.ToString(CultureInfo.InvariantCulture)); }),
            new("darkstarpatch_atom74.esm", "00081F", "Properties[2].FunctionType", "OMOD property's function type (byte 4 of the 24)", "DATA", -1, 1,
                (r, b) => { var m = ((IObjectModFormLinkFloatPropertyGetter<Weapon.Property>)((IWeaponModificationGetter)r).Properties[2]).FunctionType; return (b[0] == (byte)m, m.ToString()); }),
            // ---- F6r: gendered values (male and female halves)
            new("Starfield.esm", "01D1D7", "WorldModel.Female.File", "an armor's female model (MOD4, split by signature)", "MOD4", 0, -1,
                (r, b) => { string m = ((IArmorGetter)r).WorldModel!.Female!.File!.GivenPath; return (System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0') == m, m); }),
            new("Starfield.esm", "001C64", "FirstPersonModel.Male.LightLayer", "an armor addon's male first-person model, a shared FLLD", "FLLD", 0, 4,
                (r, b) => { var m = ((IArmorAddonGetter)r).FirstPersonModel!.Male!.LightLayer!.Value; return (U32(b) == (uint)m, m.ToString()); }),
            new("Starfield.esm", "00347D", "Voices.Female", "a race's female voice (VTCK offset 4)", "VTCK", 4, 4,
                (r, b) => Link(reads, ((IRaceGetter)r).Voices.Female.FormKey)),
            new("Starfield.esm", "00347D", "DefaultWeight.Male.Thin", "a gendered pair inside a data block (DAT2 offset 8)", "DAT2", 8, 4,
                (r, b) => { float m = ((IRaceGetter)r).DefaultWeight.Male.Thin; return (F(b) == m, m.ToString("R", CultureInfo.InvariantCulture)); }),
            // ---- fixes.17 (C221): a half-precision float, held to Mutagen's Half bit for bit (XALD offset 14, 00 3E = 1.5)
            new("SFBGS003.esm", "168F5F:Starfield.esm", "LightArea.Unused", "a Half (2 bytes, C221) in a placed light's area data", "XALD", 14, 2,
                (r, b) => { Half m = ((IPlacedObjectGetter)r).LightArea!.Unused; return (BinaryPrimitives.ReadUInt16LittleEndian(b) == BitConverter.HalfToUInt16Bits(m) && m == (Half)1.5, ((double)m).ToString("R", CultureInfo.InvariantCulture)); }),
            // ---- S37: entries of variable size in one subrecord, walked as Mutagen read them
            // A placed object's snap links (XSL1): a 4-byte count, then entries of a link and a counted list of node pairs
            new("SFBGS003.esm", "00A924", "SnapLinks[3].LinkedReference", "snap link after three of variable size (XSL1 count, entries with a counted list)", "XSL1", 52, 4,
                (r, b) => Link(reads, ((IPlacedObjectGetter)r).SnapLinks![3].LinkedReference.FormKey)),
            new("SFBGS003.esm", "00A924", "SnapLinks[2].Links[0].ParentNode", "a list inside a variable entry (its own 4-byte count)", "XSL1", 44, 4,
                (r, b) => { uint m = ((IPlacedObjectGetter)r).SnapLinks![2].Links[0].ParentNode; return (U32(b) == m, m.ToString(CultureInfo.InvariantCulture)); }),
            // A planet's model component (XMPM): a counted list of texts with a 2-byte length, then entries holding texts
            new("Starfield.esm", "03F59A", "Components[0].XMPM.UnknownStrings[0]", "a text entry with a 2-byte length (XMPM)", "XMPM", 4, 7,
                (r, b) => { string m = Xmpm(r).UnknownStrings[0]; return (System.Text.Encoding.UTF8.GetString(b) == m, m); }),
            new("Starfield.esm", "03F59A", "Components[0].XMPM.UnknownSubItems[0].Extension", "a zero-ended text after a length-prefixed one", "XMPM", 32, 4,
                (r, b) => { string m = Xmpm(r).UnknownSubItems[0].Extension; return (System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0') == m && b[^1] == 0, m); }),
            new("Starfield.esm", "03F59A", "Components[0].XMPM.UnknownSubItems[0].FolderHash", "a value after texts in an entry", "XMPM", 36, 4,
                (r, b) => { uint m = Xmpm(r).UnknownSubItems[0].FolderHash; return (U32(b) == m, m.ToString(CultureInfo.InvariantCulture)); }),
            // A planet's details (HNAM): eight texts with a 4-byte length, then fixed fields the schema gives no offset for
            new("Starfield.esm", "03F59A", "Details.Perihelion", "a value after eight length-prefixed texts (HNAM)", "HNAM", 66, 8,
                (r, b) => { double m = ((IPlanetGetter)r).Details!.Perihelion; return (BinaryPrimitives.ReadDoubleLittleEndian(b) == m, m.ToString("R", CultureInfo.InvariantCulture)); }),
            new("Starfield.esm", "03F59A", "Details.CatalogueId", "a text with a 4-byte length counting its zero byte", "HNAM", 15, 4,
                (r, b) => { string m = ((IPlanetGetter)r).Details!.CatalogueId; return (System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0') == m, m); }),
        };
        var lines = new List<string>();
        foreach (var c in cases)
        {
            var rec = e.Load(c.Plugin, c.Record);
            var ev = e.Evidence(rec, c.Field);
            // A field in no list (the Half's) is placed by the schema's offset: no item to locate
            Check(ev.Item is { Located: true } || ev.Item == null && !c.Field.Contains('[') && ev.Offset != null, $"{rec.Label} {c.Field} ({c.Shape}): no exact address: {ev.Item?.Reason ?? "the field wasn't located"}\n{ev.ToText()}");
            Check(ev.Slices.Count == 1 && ev.Slices[0].FieldBytes != null, $"{rec.Label} {c.Field}: {ev.Slices.Count} slices");
            Check(ev.Subrecord == c.Subrecord && (c.Offset < 0 || ev.Offset == c.Offset) && (c.Size < 0 || ev.Size == c.Size), $"{rec.Label} {c.Field}: {ev.Subrecord} offset {ev.Offset}, {ev.Size} bytes; expected {c.Subrecord} offset {c.Offset}, {c.Size} bytes");
            if (ev.Slices.Count != 1 || ev.Slices[0].FieldBytes == null) continue;
            var bytes = Bytes(ev.Slices[0].FieldBytes!);
            // The slice is the file's own: the same bytes at that offset of that occurrence of the subrecord
            var raw = e.Raw(rec.Ref);
            var sub = raw.Subrecords.Where(s => s.Signature == ev.Subrecord).ElementAt(ev.Slices[0].Occurrence);
            Check(raw.Content(sub).Slice(ev.Offset!.Value, ev.Size!.Value).SequenceEqual(bytes), $"{rec.Label} {c.Field}: the slice isn't the file's bytes at that place");
            reads = ev.Slices[0].Reads;
            var (same, mutagen) = c.Same(rec.Record, bytes);
            Check(same, $"{rec.Label} {c.Field}: the slice {ev.Slices[0].FieldBytes} (reads {reads}) isn't Mutagen's value {mutagen}");
            // A field in no list has no item check of its own: the test's own comparison with Mutagen above holds it
            // Read back and held against Mutagen's value, or, for a value Mutagen converts, said to be computed from these bytes
            if (ev.Item != null) Check(ev.ToText().Contains("Checked: the bytes read", StringComparison.Ordinal) || ev.ToText().Contains("is computed from these bytes by its reader", StringComparison.Ordinal),
                $"{rec.Label} {c.Field}: the text doesn't say the slice was checked:\n{ev.ToText()}");
            // Since fixes.15 (C196) no place is SFSearch's own: none is named as written in code (GAPS Part 2 row 33)
            Check(!ev.ToText().Contains("row 33", StringComparison.Ordinal), $"{rec.Label} {c.Field}: the text still names a place written in code:\n{ev.ToText()}");
            // Since fixes.24 (C83, GAPS S173) Mutagen notes a perk effect's Modification as a converted value at DATA 1: the
            // record XML's layoutMap isn't read for evidence any more
            if (c.Field.EndsWith(".Modification", StringComparison.Ordinal))
                Check(ev.ToText().Contains("is computed from these bytes by its reader", StringComparison.Ordinal) && !ev.ToText().Contains("layoutMap", StringComparison.Ordinal), $"{rec.Label} {c.Field}: not Mutagen's converted value at its position:\n{ev.ToText()}");
            lines.Add($"{c.Shape}: {rec.Label} {c.Field} at {ev.Subrecord} offset {ev.Offset} ({ev.Slices[0].FieldBytes}) = {mutagen}");
        }

        // S35: a translated text is looked up in the one strings table the schema names (the XML's translated), not all three
        foreach (var (record, field, table) in new[] { ("0476C4", "Name", StringsSource.Normal), ("00423B", "Description", StringsSource.DL) })
        {
            var rec = e.Load("Starfield.esm", record);
            var asked = new List<StringsSource?>();
            var ev = FieldLookup.Evidence(e.Schema, e.Card(rec), e.Raw(rec.Ref), field, id => id.ToString("X8", CultureInfo.InvariantCulture),
                textOf: (id, language, t) => { asked.Add(t); return rec.Session.StringsFor(id, language, t); }, positionsOf: rec.Session.Positions);
            Check(ev.Item is { Located: true } && asked.Count == 1 && asked[0] == table, $"{rec.Label} {field}: the strings tables asked were [{string.Join(", ", asked)}], not {table} alone; {ev.Item?.Reason}");
        }

        // A race's biped object names: a dictionary on the card (BipedObjects[3].Value.Name), laid out by HandReadLayouts.xml
        // as 64 NAME subrecords in key order, are checked in HandLists (S67). Without .Value the path names no value
        var race = e.Evidence(e.Load("Starfield.esm", "00347D"), "BipedObjects[3].Name");
        Check(race.Item is not { Located: true } && race.Slices.All(s => s.FieldBytes == null), $"a race's biped object name without its Value was located:\n{race.ToText()}");
        // A field outside any list is answered as before
        var damage = e.Evidence(e.Load("darkstar.esm", "0003C2"), "AttackDamage");
        Check(damage.Item is { Located: true } && damage.Slices.Count == 1 && damage.Slices[0].FieldBytes == "00 00 40 41", $"AttackDamage's evidence changed:\n{damage.ToText()}");
        // C200 (fixes.16, GAPS Part 2 row 33): the data of a function Mutagen doesn't know (UnknownConditionData, the
        // XML's conditionFunction="*") places its Function by its own layoutOffset (8, uint16), no longer tied by its enum
        // to the FunctionParse step. No official plugin has such a condition (a scan of every CTDA of Starfield.esm, its
        // DLCs, darkstar.esm and kgcdoom.esm), so a plugin made here holds one: function 2, which no kind of data claims
        var made = new StarfieldMod(ModKey.FromFileName("sfs_c200_unknownfunction.esm"), StarfieldRelease.Starfield);
        var cobj = made.ConstructibleObjects.AddNew("sfs_c200_UnknownFunction");
        cobj.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1,
            Data = new UnknownConditionData { Function = (Condition.Function)2, ParameterOne = 7, ParameterTwo = 9 } });
        string madePath = Path.Combine(ctx.Folder("c200"), made.ModKey.FileName);
        var madeOrder = new LoadOrder<IModMasterStyledGetter>(new[] { new KeyedMasterStyle(made.ModKey, Mutagen.Bethesda.Plugins.MasterStyle.Full) });
        made.WriteToBinary(madePath, new BinaryWriteParameters { MasterFlagsLookup = madeOrder, ModKey = ModKeyOption.CorrectToPath });
        using (var madeIndex = new HeaderIndex(PluginSource.FromFiles(new[] { madePath }), ctx.Options()))
        {
            madeIndex.Refresh();
            using var madeEngine = new RecordEngine(madeIndex, ctx.Schema, Path.Combine(ctx.Work, "verify-c200"), ctx.Data);
            madeEngine.Sessions.Use(made.ModKey.FileName);
            var rec = madeEngine.Load(made.ModKey.FileName, cobj.FormKey.ID.ToString("X6", CultureInfo.InvariantCulture));
            var data = ((IConstructibleObjectGetter)rec.Record).Conditions![0].Data;
            Check(data is IUnknownConditionDataGetter { Function: (Condition.Function)2 }, $"{rec.Label}: Mutagen reads the condition's data as {data.GetType().Name}, not UnknownConditionData with function 2");
            foreach (var (field, offset, want) in new[] { ("Conditions[0].Data.Function", 8, 2u), ("Conditions[0].Data.ParameterOne", 12, 7u), ("Conditions[0].Data.ParameterTwo", 16, 9u) })
            {
                var ev = madeEngine.Evidence(rec, field);
                Check(ev.Item is { Located: true } && ev.Subrecord == "CTDA" && ev.Offset == offset && ev.Slices.Count == 1 && ev.Slices[0].FieldBytes != null,
                    $"{rec.Label} {field}: {ev.Subrecord} offset {ev.Offset}; expected CTDA offset {offset}: {ev.Item?.Reason}\n{ev.ToText()}");
                var b = Bytes(ev.Slices[0].FieldBytes!);
                uint got = b.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b) : U32(b);
                Check(got == want && b.Length == (offset == 8 ? 2 : 4), $"{rec.Label} {field}: the slice {ev.Slices[0].FieldBytes} isn't {want}");
                // The catch-all's "*" is no function number to hold the CTDA against
                Check(!ev.ToText().Contains("conditionFunction of UnknownConditionData", StringComparison.Ordinal), $"{rec.Label} {field}: the catch-all was held against a function number:\n{ev.ToText()}");
                if (offset == 8)
                    Check(ev.ToText().Contains("Mutagen's reader read Conditions[0].Data.Function at offset 8 of CTDA", StringComparison.Ordinal), $"{rec.Label} {field}: not placed by Mutagen's value positions:\n{ev.ToText()}");
                lines.Add($"unknown function (made plugin): {rec.Label} {field} at CTDA offset {ev.Offset} ({ev.Slices[0].FieldBytes}) = {got}");
            }
        }

        int containerItems = ContainerItems(ctx, lines);
        int handLists = HandLists(ctx, lines);
        int header = Header(ctx, lines);
        int readerWins = ReaderWins(ctx, lines);
        int packageLayouts = PackageLayouts(ctx, lines);
        int componentShared = ComponentShared(ctx, lines);

        ctx.WriteReport("evidence-items.txt", lines);
        return $"{cases.Count} fields of {cases.Select(c => c.Shape).Distinct().Count()} shapes: each slice is the file's bytes and decodes to Mutagen's value ({string.Join("; ", lines.Skip(11).Take(3))} …); a race's biped name refused with its reason; AttackDamage unchanged; an unknown function's Function and parameters placed by its own layoutOffset (made plugin); {containerItems} fields of container items (CONT, FURN, NPC_; CNTO and COED) in their own item's subrecord (S59); {handLists} fields of lists Mutagen reads by hand (S67: quest aliases and unused conditions, instance naming rules, LNAM entries, scene actions, gameplay option groups, package trees, data inputs and topics, traversals, region weathers, volumes, pattern swaps, biped objects, navigation map infos; a made plugin for the rest) and a child-group scene named as its record; {header} fields of plugin headers and record headers (S88: flags, versions, HEDR, masters, overridden forms, transient types; Starfield.esm, a light, a medium, a blueprint and a 10-master plugin, a record's own header and the views of its flags (S106: MajorFlags, StarfieldMajorRecordFlags, IsCompressed, IsDeleted at offset 8 of three records' headers), and a made plugin whose masters have a DATA); {readerWins} fields where Mutagen's reader corrected SFSearch's own layouts (J66: a package's value blocks a prefix of its Data, a patrol without XTV2, a cell's traversal fluff, a pathing grid; a made plugin's event topics, topic input and unknown target); {packageLayouts} fields placed by Mutagen's HandReadLayouts.xml (C47, fixes.18), where SFSearch's own copies were until the move; {componentShared} fields whose subrecord a component holds too (S102: the record's own named, the component's left out, and the reverse)";
    }

    /// <summary>
    /// GAPS S59: a container entry is a CNTO and, when set, a COED, so a list of them (a container's Items, a furniture's
    /// ContainerItems, an NPC's Items) is cut into items at each CNTO, and a field of the n-th item is in the n-th CNTO
    /// (or that item's COED), not "every CNTO". Starfield.esm's containers and NPCs give no item a COED and no furniture
    /// holds items, so a plugin made here holds one of each kind with a COED on some items. Each slice must be one
    /// subrecord, the item's own occurrence of it, the file's bytes there, and Mutagen's value.
    /// </summary>
    static int ContainerItems(Ctx ctx, List<string> lines)
    {
        int n = 0;
        void Item(RecordEngine e, LoadedRecord rec, string field, string sub, int offset, int size, int occurrence, Func<byte[], string?, (bool Same, string Mutagen)> same)
        {
            var ev = e.Evidence(rec, field);
            Check(ev.Item is { Located: true } && ev.Slices.Count == 1 && ev.Slices[0].FieldBytes != null,
                $"{rec.Label} {field}: not one located slice ({ev.Slices.Count} slices): {ev.Item?.Reason}\n{ev.ToText()}");
            if (ev.Slices.Count != 1 || ev.Slices[0].FieldBytes == null) return;
            Check(ev.Subrecord == sub && ev.Offset == offset && ev.Size == size && ev.Slices[0].Occurrence == occurrence,
                $"{rec.Label} {field}: {ev.Subrecord} #{ev.Slices[0].Occurrence} offset {ev.Offset}, {ev.Size} bytes; expected {sub} #{occurrence} offset {offset}, {size} bytes");
            var bytes = Bytes(ev.Slices[0].FieldBytes!);
            var raw = e.Raw(rec.Ref);
            var s = raw.Subrecords.Where(x => x.Signature == ev.Subrecord).ElementAt(ev.Slices[0].Occurrence);
            Check(raw.Content(s).Slice(ev.Offset!.Value, ev.Size!.Value).SequenceEqual(bytes), $"{rec.Label} {field}: the slice isn't the file's bytes at that place");
            var (ok, mutagen) = same(bytes, ev.Slices[0].Reads);
            Check(ok, $"{rec.Label} {field}: the slice {ev.Slices[0].FieldBytes} (reads {ev.Slices[0].Reads}) isn't Mutagen's value {mutagen}");
            lines.Add($"container item (S59): {rec.Label} {field} at {sub} #{occurrence + 1} offset {offset} ({ev.Slices[0].FieldBytes}) = {mutagen}");
            n++;
        }
        static (bool, string) Int(byte[] b, int m) => (BinaryPrimitives.ReadInt32LittleEndian(b) == m, m.ToString(CultureInfo.InvariantCulture));
        static (bool, string) Float(byte[] b, float m) => (F(b) == m, m.ToString("R", CultureInfo.InvariantCulture));

        // Starfield.esm: DR014_Loot_Storage_BossChest_Industrial_Rare's 2nd and 4th items, LC107_Crew_Liang_Corpse's 2nd
        var e = ctx.Engine;
        e.Sessions.Use("Starfield.esm");
        var cont = e.Load("Starfield.esm", "398071");
        var items = ((IContainerGetter)cont.Record).Items!;
        Item(e, cont, "Items[1].Item.Item", "CNTO", 0, 4, 1, (b, reads) => Link(reads, items[1].Item.Item.FormKey));
        Item(e, cont, "Items[3].Item.Count", "CNTO", 4, 4, 3, (b, _) => Int(b, items[3].Item.Count));
        var npc = e.Load("Starfield.esm", "001E3D");
        var npcItems = ((INpcGetter)npc.Record).Items!;
        Item(e, npc, "Items[1].Item.Item", "CNTO", 0, 4, 1, (b, reads) => Link(reads, npcItems[1].Item.Item.FormKey));

        // A made plugin: a container, a furniture and an NPC, each with three items, the 2nd with a COED
        var made = new StarfieldMod(ModKey.FromFileName("sfs_s59_containeritems.esm"), StarfieldRelease.Starfield);
        var misc = made.MiscItems.AddNew("sfs_s59_Item");
        var faction = made.Factions.AddNew("sfs_s59_Faction");
        ExtendedList<ContainerEntry> Entries() => new()
        {
            new ContainerEntry { Item = new ContainerItem { Item = misc.ToLink<IItemGetter>(), Count = 3 } },
            new ContainerEntry { Item = new ContainerItem { Item = misc.ToLink<IItemGetter>(), Count = 5 }, Data = new ExtraData { Owner = new FactionOwner { Faction = faction.ToLink<IFactionGetter>(), RequiredRank = 2 }, ItemCondition = 0.75f } },
            new ContainerEntry { Item = new ContainerItem { Item = misc.ToLink<IItemGetter>(), Count = 7 } },
        };
        var madeCont = made.Containers.AddNew("sfs_s59_Container"); madeCont.Items = Entries();
        var madeFurn = made.Furniture.AddNew("sfs_s59_Furniture"); madeFurn.ContainerItems = Entries();
        var madeNpc = made.Npcs.AddNew("sfs_s59_Npc"); madeNpc.Items = Entries();
        string madePath = Path.Combine(ctx.Folder("s59"), made.ModKey.FileName);
        var madeOrder = new LoadOrder<IModMasterStyledGetter>(new[] { new KeyedMasterStyle(made.ModKey, Mutagen.Bethesda.Plugins.MasterStyle.Full) });
        made.WriteToBinary(madePath, new BinaryWriteParameters { MasterFlagsLookup = madeOrder, ModKey = ModKeyOption.CorrectToPath });
        using var madeIndex = new HeaderIndex(PluginSource.FromFiles(new[] { madePath }), ctx.Options());
        madeIndex.Refresh();
        using var me = new RecordEngine(madeIndex, ctx.Schema, Path.Combine(ctx.Work, "verify-s59"), ctx.Data);
        me.Sessions.Use(made.ModKey.FileName);
        foreach (var (key, list) in new (FormKey, string)[] { (madeCont.FormKey, "Items"), (madeFurn.FormKey, "ContainerItems"), (madeNpc.FormKey, "Items") })
        {
            var rec = me.Load(made.ModKey.FileName, key.ID.ToString("X6", CultureInfo.InvariantCulture));
            IReadOnlyList<IContainerEntryGetter> got = rec.Record switch
            {
                IContainerGetter c => c.Items!,
                IFurnitureGetter f => f.ContainerItems!,
                INpcGetter p => p.Items!,
                _ => throw new InvalidOperationException(rec.Label),
            };
            Check(got.Count == 3 && got[1].Data != null && got[0].Data == null, $"{rec.Label}: Mutagen reads {got.Count} items, the 2nd {(got.Count > 1 && got[1].Data != null ? "with" : "without")} a COED");
            if (got.Count != 3 || got[1].Data == null) continue;
            Item(me, rec, $"{list}[1].Item.Count", "CNTO", 4, 4, 1, (b, _) => Int(b, got[1].Item.Count));
            Item(me, rec, $"{list}[2].Item.Count", "CNTO", 4, 4, 2, (b, _) => Int(b, got[2].Item.Count));
            Item(me, rec, $"{list}[2].Item.Item", "CNTO", 0, 4, 2, (b, reads) => Link(reads, got[2].Item.Item.FormKey));
            Item(me, rec, $"{list}[1].Data.ItemCondition", "COED", 8, 4, 0, (b, _) => Float(b, got[1].Data!.ItemCondition));
        }
        return n;
    }

    /// <summary>
    /// GAPS S67: fields inside lists (and objects) Mutagen reads with hand-written code, laid out by SFSearch's own
    /// hand-read layouts until fixes.18 and by the package's HandReadLayouts.xml since (C47): each slice must be one subrecord, its own occurrence, the file's bytes there and Mutagen's value read
    /// through its typed record. Real records of Starfield.esm and the official plugins, and a made plugin for what
    /// Data lacks (a pattern swap list of several items, a navigation map info's CRCs, a topic subtype).
    /// </summary>
    static int HandLists(Ctx ctx, List<string> lines)
    {
        int n = 0;
        void Item(RecordEngine e, LoadedRecord rec, string field, string sub, int offset, int size, int occurrence, Func<byte[], string?, (bool Same, string Mutagen)> same, string? said = null)
        {
            var ev = e.Evidence(rec, field);
            Check(ev.Item is { Located: true } && ev.Slices.Count == 1 && ev.Slices[0].FieldBytes != null,
                $"{rec.Label} {field}: not one located slice ({ev.Slices.Count} slices): {ev.Item?.Reason}\n{ev.ToText()}");
            if (ev.Slices.Count != 1 || ev.Slices[0].FieldBytes == null) return;
            Check(ev.Subrecord == sub && ev.Offset == offset && ev.Size == size && ev.Slices[0].Occurrence == occurrence,
                $"{rec.Label} {field}: {ev.Subrecord} #{ev.Slices[0].Occurrence} offset {ev.Offset}, {ev.Size} bytes; expected {sub} #{occurrence} offset {offset}, {size} bytes");
            var bytes = Bytes(ev.Slices[0].FieldBytes!);
            var raw = e.Raw(rec.Ref);
            var s = raw.Subrecords.Where(x => x.Signature == ev.Subrecord).ElementAt(ev.Slices[0].Occurrence);
            Check(raw.Content(s).Slice(ev.Offset!.Value, ev.Size!.Value).SequenceEqual(bytes), $"{rec.Label} {field}: the slice isn't the file's bytes at that place");
            var (ok, mutagen) = same(bytes, ev.Slices[0].Reads);
            Check(ok, $"{rec.Label} {field}: the slice {ev.Slices[0].FieldBytes} (reads {ev.Slices[0].Reads}) isn't Mutagen's value {mutagen}");
            if (said != null) Check(ev.ToText().Contains(said, StringComparison.Ordinal), $"{rec.Label} {field}: the answer doesn't say \"{said}\":\n{ev.ToText()}");
            lines.Add($"hand-read list (S67): {rec.Label} {field} at {sub} #{occurrence + 1} offset {offset} ({ev.Slices[0].FieldBytes}) = {mutagen}");
            n++;
        }
        static (bool, string) Int(long have, long m) => (have == m, m.ToString(CultureInfo.InvariantCulture));
        static (bool, string) Float(byte[] b, float m) => (F(b) == m, m.ToString("R", CultureInfo.InvariantCulture));
        static (bool, string) Text8(byte[] b, string? m) => (System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0') == m && b[^1] == 0, m ?? "(null)");
        static (bool, string) P3(byte[] b, P3Float m) => (F(b) == m.X && F(b[4..]) == m.Y && F(b[8..]) == m.Z, m.ToString());

        var e = ctx.Engine;
        e.Sessions.Use("Starfield.esm", "SFBGS00D.esm", "SFBGS006.esm", "SFBGS007.esm");

        // A quest's aliases (QuestBinaryCreateTranslation.FillBinaryAliasParseCustom): ALST, ALLS, ALCS or ALMI to ALED
        var tutorial = e.Load("Starfield.esm", "00118F");
        Item(e, tutorial, "Aliases[1].Name", "ALID", 0, 18, 1, (b, _) => Text8(b, ((IQuestReferenceAliasGetter)((IQuestGetter)tutorial.Record).Aliases![1]).Name), "Aliases[1] is subrecords ALST ALID FNAM ALFG ALFA ALRT VTCK (#28 to #34)");
        var lc024 = e.Load("Starfield.esm", "2733E2");
        var lcq = (IQuestGetter)lc024.Record;
        Item(e, lc024, "NextAliasID", "ANAM", 0, 4, 0, (b, _) => Int(U32(b), lcq.NextAliasID ?? 0));
        Item(e, lc024, "Aliases[1].SpecificLocation", "ALFL", 0, 4, 0, (b, reads) => Link(reads, ((IQuestLocationAliasGetter)lcq.Aliases![1]).SpecificLocation.FormKey));
        var collection = (IQuestCollectionAliasGetter)lcq.Aliases![2];
        Item(e, lc024, "Aliases[2].Collection[0].ID", "ALCS", 0, 4, 0, (b, _) => Int(U32(b), collection.Collection[0].ID ?? 0));
        Item(e, lc024, "Aliases[2].Collection[0].ReferenceAlias.Name", "ALID", 0, 13, 2, (b, _) => Text8(b, collection.Collection[0].ReferenceAlias.Name));
        // A quest's unused conditions: NEXT, then the conditions
        var patrol = e.Load("Starfield.esm", "0013A8");
        Item(e, patrol, "UnusedConditions[1].Data.RunOnType", "CTDA", 20, 4, 1, (b, _) => Int(U32(b), (long)((IQuestGetter)patrol.Record).UnusedConditions![1].Data.RunOnType));
        // An instance naming rule's rule sets: the subrecords after UNAM, read by the generated reader of the class UNAM picks
        var innr = e.Load("Starfield.esm", "184BC1");
        var names = ((IArmorInstanceNamingRulesGetter)((IInstanceNamingRulesGetter)innr.Record).Rules!).Rules[0].Names;
        Item(e, innr, "Rules.Rules[0].Names[2].Text", "WNAM", 0, 4, 2, (b, reads) => Text(reads, names![2].Text!, b), "(its value positions)");
        // A legendary item's LNAM entries: 8 bytes each in LNAM, then one CITC block of conditions each
        var lgdi = e.Load("SFBGS00D.esm", "07ADE1");
        var lnam = ((ILegendaryItemGetter)lgdi.Record).LnamEntries!;
        Item(e, lgdi, "LnamEntries[1].ReferenceMod", "LNAM", 12, 4, 0, (b, _) => Int(U32(b), lnam[1].ReferenceMod));
        Item(e, lgdi, "LnamEntries[1].Conditions[1].ComparisonValue", "CTDA", 4, 4, 3, (b, reads) => Link(reads, ((IConditionGlobalGetter)lnam[1].Conditions![1]).ComparisonValue.FormKey), "(its value positions)");
        // A scene's actions: ANAM (the kind), the action, an empty ANAM
        var scene = e.Load("Starfield.esm", "005636");
        Item(e, scene, "Actions[1].Index", "INAM", 0, 4, 1, (b, _) => Int(U32(b), ((ISceneGetter)scene.Record).Actions![1].Index ?? 0), "(its value positions)");
        // A gameplay options group: BNAM picks root or leaf, whose generated reader reads on
        var gpog = e.Load("SFBGS007.esm", "000016");
        Item(e, gpog, "Options.GameplayOptionGroups[0]", "GOGL", 0, 4, 0, (b, reads) => Link(reads, ((IGameplayOptionsGroupRootGetter)((IGameplayOptionsGroupGetter)gpog.Record).Options).GameplayOptionGroups[0].FormKey));
        // A package: its procedure tree (ANAM after XNAM), its data inputs (value blocks, then names and flags by key), its topics
        var useIdle = e.Load("Starfield.esm", "004DF6");
        var pack = (IPackageGetter)useIdle.Record;
        Item(e, useIdle, "ProcedureTree[1].ProcedureType", "PNAM", 0, 14, 0, (b, _) => Text8(b, pack.ProcedureTree[1].ProcedureType), "(its value positions)");
        Item(e, useIdle, "ProcedureTree[0].Root.BranchCount", "PRCB", 0, 4, 0, (b, _) => Int(U32(b), pack.ProcedureTree[0].Root!.BranchCount));
        Item(e, useIdle, "Data[0].Value.Name", "BNAM", 0, 7, 0, (b, _) => Text8(b, pack.Data.Values.First().Name));
        Item(e, useIdle, "Data[0].Value.Flags", "PNAM", 0, 4, 1, (b, _) => Int(U32(b), (long)(pack.Data.Values.First().Flags ?? 0)));
        var heatleech = e.Load("Starfield.esm", "1AC0C5");
        var target = (IPackageDataTargetGetter)((IPackageGetter)heatleech.Record).Data.Values.First();
        // Since fixes.24 (C83, GAPS S173 fixed): a package target's fields are at their places in PTDA (Reference at 4), and
        // an input's stored key (UNAM) is a Key position, the entry's key read as an sbyte
        var heatPack = (IPackageGetter)heatleech.Record;
        Item(e, heatleech, "Data[0].Value.Target.Reference", "PTDA", 4, 4, 0, (b, reads) => Link(reads, ((IPackageTargetSpecificReferenceGetter)target.Target!).Reference.FormKey));
        Item(e, heatleech, "Data[0].Key", "UNAM", 0, 1, 0, (b, _) => { sbyte k = heatPack.Data.Keys.First(); return ((sbyte)b[0] == k, k.ToString(CultureInfo.InvariantCulture)); }, "the entry's stored key");
        Item(e, heatleech, "OnEnd.Topics[0].Reference", "PDTO", 4, 4, 1, (b, reads) =>
        {
            // A null link: the four bytes are zero, as Mutagen's link is null
            var topic = ((ITopicReferenceGetter)((IPackageGetter)heatleech.Record).OnEnd!.Topics[0]).Reference;
            return topic.IsNull ? (U32(b) == 0, "Null") : Link(reads, topic.FormKey);
        });
        // A placed object's traversals in XTV2 (52 or 56 bytes each), flags kept without bit 2
        var refr = e.Load("SFBGS00D.esm", "07C785");
        var trav = ((IPlacedObjectGetter)refr.Record).Traversals!;
        Item(e, refr, "Traversals[1].Traversal", "XTV2", 100, 4, 0, (b, reads) => Link(reads, trav[1].Traversal.FormKey));
        Item(e, refr, "Traversals[1].To", "XTV2", 72, 12, 0, (b, _) => P3(b, trav[1].To));
        Item(e, refr, "Traversals[1].Flags", "XTV2", 96, 4, 0, (b, _) => Int(U32(b) & ~4u, (long)trav[1].Flags));
        // A region's weather list: RDWT after the RDAT that picks the weather
        var region = e.Load("Starfield.esm", "249FA5");
        Item(e, region, "Weather.Weathers[0].Weather", "RDWT", 0, 4, 0, (b, reads) => Link(reads, ((IRegionGetter)region.Record).Weather!.Weathers![0].Weather.FormKey));
        // Components: a volume's items (VLMS) and a surface tree's pattern swap items (DAT2)
        var vol = e.Load("Starfield.esm", "13C557");
        Item(e, vol, "Components[0].Items[0].Ender.Unknown2", "VLMS", 88, 4, 0, (b, _) => Float(b, ((IVolumesUnknownEnderTrioGetter)((IVolumesComponentGetter)((IStaticGetter)vol.Record).Components[0]).Items![0].Ender).Unknown2));
        var sftr = e.Load("Starfield.esm", "0478AD");
        Item(e, sftr, "Components[0].Data.Data[0].SurfacePattern", "DAT2", 4, 4, 0, (b, reads) => Link(reads, ((ISurfaceTreePatternSwapInfoComponentGetter)((ISurfaceTreeGetter)sftr.Record).Components[0]).Data!.Data[0].SurfacePattern.FormKey));
        // A race's biped objects: a dictionary, its entries in key order, as the package's RACE layout counts them
        var race = e.Load("Starfield.esm", "00347D");
        Item(e, race, "BipedObjects[3].Value.Name", "NAME", 0, 5, 3, (b, _) => Text8(b, ((IRaceGetter)race.Record).BipedObjects[(BipedObject)3].Name));
        // A navigation mesh info map's map infos: island data and the pathing cell after the generated part
        var navi = e.Load("SFBGS006.esm", "000FF1:Starfield.esm");
        var infos = ((INavigationMeshInfoMapGetter)navi.Record).MapInfos;
        Item(e, navi, "MapInfos[4].IslandData.Triangles[1]", "NVMI", 319, 6, 4, (b, _) =>
        {
            var m = infos[4].IslandData!.Triangles[1];
            return (BinaryPrimitives.ReadInt16LittleEndian(b) == m.X && BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(2)) == m.Y && BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(4)) == m.Z, m.ToString());
        });
        Item(e, navi, "MapInfos[1].PathingCell.ParentCell", "NVMI", 49, 4, 1, (b, reads) => Link(reads, infos[1].PathingCell.ParentCell.FormKey));

        // A record of the child group (a quest's scenes): named, no bytes of this record given
        var audio = e.Evidence(e.Load("Starfield.esm", "195398"), "Scenes[0]");
        Check(audio.Slices.Count == 0 && audio.Notes.Any(x => x.Contains("is a record of its own, AudioLogsQuest_LC024_ClosingMessage01 (195397:Starfield.esm)", StringComparison.Ordinal)),
            $"a quest's scene wasn't named as a record of the child group:\n{audio.ToText()}");
        lines.Add("child group (S67): AudioLogsQuest_JM (195398:Starfield.esm) Scenes[0] is the record AudioLogsQuest_LC024_ClosingMessage01 (195397:Starfield.esm), no bytes of the quest");

        // A reflection's FormLinks: the FormIDs Mutagen's reflection reader finds in the blob, in order
        var ffkw = e.Load("Starfield.esm", "000AFA");
        var refl = e.Evidence(ffkw, "Reflection.FormLinks[0]");
        var reflLink = ((IFormFolderKeywordListGetter)ffkw.Record).Reflection!.FormLinks[0].FormKey;
        Check(refl.Subrecord == "REFL" && refl.Offset == 184 && refl.Slices.Count == 1 && refl.Slices[0].Reads is { } rr && rr.EndsWith("(" + reflLink + ")", StringComparison.OrdinalIgnoreCase)
            && e.Raw(ffkw.Ref).Content(e.Raw(ffkw.Ref).Subrecords.First(x => x.Signature == "REFL")).Slice(184, 4).SequenceEqual(Bytes(refl.Slices[0].FieldBytes!)),
            $"{ffkw.Label} Reflection.FormLinks[0]: {refl.Subrecord} offset {refl.Offset}:\n{refl.ToText()}");
        lines.Add($"reflection link (S67): {ffkw.Label} Reflection.FormLinks[0] at REFL offset 184 ({refl.Slices.FirstOrDefault()?.FieldBytes}) = {reflLink}");
        n++;

        // A made plugin: a surface tree with three pattern swap items (the last without Unknown2), a navigation mesh info
        // map whose map info has two CRCs, a package whose OnBegin holds a topic subtype
        var made = new StarfieldMod(ModKey.FromFileName("sfs_s67_handlists.esm"), StarfieldRelease.Starfield);
        var pattern = made.SurfacePatterns.AddNew("sfs_s67_Pattern");
        var tree = made.SurfaceTrees.AddNew("sfs_s67_Tree");
        var swap = new SurfaceTreePatternSwapInfoComponentData();
        swap.Data.Add(new SurfaceTreePatternSwapInfoItem { SurfacePattern = pattern.ToLink<ISurfacePatternGetter>(), Unknown = 5, Unknown2 = 7 });
        swap.Data.Add(new SurfaceTreePatternSwapInfoItem { SurfacePattern = pattern.ToLink<ISurfacePatternGetter>(), Unknown = -3, Unknown2 = 11 });
        swap.Data.Add(new SurfaceTreePatternSwapInfoItem { SurfacePattern = pattern.ToLink<ISurfacePatternGetter>(), Unknown = 9 });
        tree.Components.Add(new SurfaceTreePatternSwapInfoComponent { Data = swap });
        var map = made.NavigationMeshInfoMaps.AddNew("sfs_s67_Navi");
        var info = new NavigationMapInfo { PathingCell = new NavmeshPathingCell { Crc = 1 } };
        info.UnknownCrcs.Add(0xA1B2C3D4); info.UnknownCrcs.Add(0x01020304);
        map.MapInfos.Add(info);
        var madePack = made.Packages.AddNew("sfs_s67_Package");
        madePack.OnBegin = new PackageEvent();
        madePack.OnBegin.Topics.Add(new TopicReferenceSubtype { Subtype = new Mutagen.Bethesda.Plugins.RecordType("ABCD") });
        string madePath = Path.Combine(ctx.Folder("s67"), made.ModKey.FileName);
        var madeOrder = new LoadOrder<IModMasterStyledGetter>(new[] { new KeyedMasterStyle(made.ModKey, Mutagen.Bethesda.Plugins.MasterStyle.Full) });
        made.WriteToBinary(madePath, new BinaryWriteParameters { MasterFlagsLookup = madeOrder, ModKey = ModKeyOption.CorrectToPath });
        using var madeIndex = new HeaderIndex(PluginSource.FromFiles(new[] { madePath }), ctx.Options());
        madeIndex.Refresh();
        using var me = new RecordEngine(madeIndex, ctx.Schema, Path.Combine(ctx.Work, "verify-s67"), ctx.Data);
        me.Sessions.Use(made.ModKey.FileName);
        string Id(FormKey k) => k.ID.ToString("X6", CultureInfo.InvariantCulture);
        var madeTree = me.Load(made.ModKey.FileName, Id(tree.FormKey));
        var items = ((ISurfaceTreePatternSwapInfoComponentGetter)((ISurfaceTreeGetter)madeTree.Record).Components[0]).Data!.Data;
        Check(items.Count == 3 && items[0].Unknown2 == 7 && items[2].Unknown2 == null, $"{madeTree.Label}: Mutagen reads {items.Count} pattern swap items");
        // DAT2: count (4), then 9 + 9 + 5 bytes: item 2 starts at 22, its Unknown at 26
        Item(me, madeTree, "Components[0].Data.Data[1].Unknown2", "DAT2", 18, 4, 0, (b, _) => Int(U32(b), items[1].Unknown2 ?? 0));
        Item(me, madeTree, "Components[0].Data.Data[2].Unknown", "DAT2", 26, 1, 0, (b, _) => Int((sbyte)b[0], items[2].Unknown));
        var madeNavi = me.Load(made.ModKey.FileName, Id(map.FormKey));
        var crcs = ((INavigationMeshInfoMapGetter)madeNavi.Record).MapInfos[0].UnknownCrcs;
        var naviEv = me.Evidence(madeNavi, "MapInfos[0].UnknownCrcs[1]");
        int crcAt = naviEv.Offset ?? -1;
        Item(me, madeNavi, "MapInfos[0].UnknownCrcs[1]", "NVMI", crcAt, 4, 0, (b, _) => Int(U32(b), crcs[1]));
        Check(crcAt > 0 && Bytes(naviEv.Slices.FirstOrDefault()?.FieldBytes ?? "").SequenceEqual(new byte[] { 0x04, 0x03, 0x02, 0x01 }), $"{madeNavi.Label}: UnknownCrcs[1] at offset {crcAt}");
        var madePackRec = me.Load(made.ModKey.FileName, Id(madePack.FormKey));
        Item(me, madePackRec, "OnBegin.Topics[0].Subtype", "PDTO", 4, 4, 0, (b, _) => (System.Text.Encoding.ASCII.GetString(b) == "ABCD", "ABCD"));
        return n;
    }

    /// <summary>
    /// J66: where the next package's layouts (Mutagen C47, read against Mutagen's readers) found SFSearch's own copy
    /// wrong, the reader wins, and each case below gave no place or a wrong one before (the branch point 6ff79ff):
    /// a package's value blocks are a prefix of its Data (Sandbox 002CB1: 15 blocks, 19 entries; before: "cuts the
    /// record's subrecords into 15", no place); a placed object's patrol without XTV2 (before: the walk stopped at "no
    /// XTV2", no place for its topics); a cell's traversal fluff (NumTraversalFluffBytes: no place before); a navigation
    /// map info's pathing grid, Y then X (no place before); in a made
    /// plugin, an event with no topics before one with topics (before: the first took the second's PDTO), a topic data
    /// input whose PDTO comes before its TPIC, and an unknown package target's Type.
    /// </summary>
    static int ReaderWins(Ctx ctx, List<string> lines)
    {
        int n = 0;
        void Item(RecordEngine e, LoadedRecord rec, string field, string sub, int offset, int size, int occurrence, Func<byte[], string?, (bool Same, string Mutagen)> same, string? said = null)
        {
            var ev = e.Evidence(rec, field);
            Check(ev.Item is { Located: true } && ev.Slices.Count == 1 && ev.Slices[0].FieldBytes != null,
                $"{rec.Label} {field}: not one located slice ({ev.Slices.Count} slices): {ev.Item?.Reason}\n{ev.ToText()}");
            if (ev.Slices.Count != 1 || ev.Slices[0].FieldBytes == null) return;
            var raw = e.Raw(rec.Ref);
            // size -1: the whole subrecord; occurrence -1: whichever of the record's subrecords of that signature
            int want = size >= 0 ? size : ev.Slices[0].Occurrence >= 0 ? raw.Subrecords.Where(x => x.Signature == sub).ElementAt(ev.Slices[0].Occurrence).Length : -2;
            Check(ev.Subrecord == sub && ev.Offset == offset && ev.Size == want && (occurrence < 0 || ev.Slices[0].Occurrence == occurrence),
                $"{rec.Label} {field}: {ev.Subrecord} #{ev.Slices[0].Occurrence} offset {ev.Offset}, {ev.Size} bytes; expected {sub} #{occurrence} offset {offset}, {want} bytes");
            var bytes = Bytes(ev.Slices[0].FieldBytes!);
            var s = raw.Subrecords.Where(x => x.Signature == ev.Subrecord).ElementAt(ev.Slices[0].Occurrence);
            Check(raw.Content(s).Slice(ev.Offset!.Value, ev.Size!.Value).SequenceEqual(bytes), $"{rec.Label} {field}: the slice isn't the file's bytes at that place");
            var (ok, mutagen) = same(bytes, ev.Slices[0].Reads);
            Check(ok, $"{rec.Label} {field}: the slice {ev.Slices[0].FieldBytes} (reads {ev.Slices[0].Reads}) isn't Mutagen's value {mutagen}");
            if (said != null) Check(ev.ToText().Contains(said, StringComparison.Ordinal), $"{rec.Label} {field}: the answer doesn't say \"{said}\":\n{ev.ToText()}");
            lines.Add($"reader wins (J66): {rec.Label} {field} at {sub} #{ev.Slices[0].Occurrence + 1} offset {offset} ({ev.Slices[0].FieldBytes}) = {mutagen}");
            n++;
        }
        static (bool, string) Text8(byte[] b, string? m) => (System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0') == m && b[^1] == 0, m ?? "(null)");

        var e = ctx.Engine;
        e.Sessions.Use("Starfield.esm", "SFBGS006.esm");
        // A package's value blocks are the first entries of its Data: Sandbox has 15 blocks and 19 entries (the template's
        // input names add 4). Data[0] is the block of key 2 (a location), Data[2] of key 5 (a bool), Data[13] of key 24
        // (a target selector, its Type derived from ANAM's text); Data[16] (key 1) has no block, only its name and flags
        var sandbox = e.Load("Starfield.esm", "002CB1");
        var sp = (IPackageGetter)sandbox.Record;
        Check(sp.Data.Count == 19, $"{sandbox.Label}: Mutagen has {sp.Data.Count} data inputs, not 19");
        Item(e, sandbox, "Data[0].Value.Location.Radius", "PLDT", 8, 4, 0, (b, _) => { float m = ((IPackageDataLocationGetter)sp.Data[2]).Location!.Radius; return (F(b) == m, m.ToString("R", CultureInfo.InvariantCulture)); },
            "(its value positions)");
        Item(e, sandbox, "Data[2].Value.Data", "CNAM", 0, 1, 1, (b, _) => { bool m = ((IPackageDataBoolGetter)sp.Data[5]).Data; return ((b[0] != 0) == m, m.ToString()); });
        // Since fixes.24 (C83, GAPS S173 fixed): a target input's Type is converted from its ANAM text (the whole subrecord),
        // an input's key is its UNAM byte (a Key position)
        var sandboxType = ((IPackageDataTargetGetter)sp.Data[24]).Type;
        Item(e, sandbox, "Data[13].Value.Type", "ANAM", 0, -1, -1, (b, _) => (System.Text.Encoding.ASCII.GetString(b).TrimEnd('\0').Length > 0, sandboxType.ToString()), "is computed from these bytes by its reader");
        Item(e, sandbox, "Data[16].Key", "UNAM", 0, 1, -1, (b, _) => ((sbyte)b[0] == 1, "1"), "the entry's stored key");
        Item(e, sandbox, "Data[16].Value.Name", "BNAM", 0, 12, 1, (b, _) => Text8(b, sp.Data[1].Name));

        // A placed object's patrol with no XTV2 before it (Starfield.esm has 4,678 patrols): XPRD, XPPA, INAM, its topics
        var patrolRef = e.Load("Starfield.esm", "3A6ABF");
        var patrol = ((IPlacedObjectGetter)patrolRef.Record).Patrol!;
        Item(e, patrolRef, "Patrol.Topics[0].Reference", "PDTO", 4, 4, 0, (b, reads) =>
        {
            var topic = ((ITopicReferenceGetter)patrol.Topics[0]).Reference;
            return topic.IsNull ? (U32(b) == 0, "Null") : Link(reads, topic.FormKey);
        });

        // A cell's traversal fluff: what XTV2 holds after its last whole traversal, kept as its length
        var cell = e.Load("Starfield.esm", "29EF0E");
        uint fluff = ((ICellGetter)cell.Record).NumTraversalFluffBytes;
        // Since fixes.24 (C83): Mutagen notes the trailing bytes as the converted value (its count of them)
        Item(e, cell, "NumTraversalFluffBytes", "XTV2", 104, 8, 0, (b, _) => (b.Length == fluff && b.All(x => x == 0), fluff.ToString(CultureInfo.InvariantCulture)), "is computed from these bytes by its reader");

        // A navigation map info's pathing grid, stored Y then X (before: a Bytes run with no fields, no place)
        var navi = e.Load("SFBGS006.esm", "000FF1:Starfield.esm");
        var grid = ((INavigationMeshInfoMapGetter)navi.Record).MapInfos[0].PathingCell.Grid;
        Item(e, navi, "MapInfos[0].PathingCell.Grid", "NVMI", 53, 4, 0, (b, _) => (BinaryPrimitives.ReadInt16LittleEndian(b) == grid.Y && BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(2)) == grid.X, grid.ToString()), "Mutagen converts these bytes to its value (layoutEncoding int16:Y,X)");

        // A made plugin: a package whose OnBegin has no topics and whose OnEnd has one (a subtype); a topic data input
        // (Mutagen writes its PDTO before its TPIC); a target Mutagen doesn't know (type 9), which keeps its number
        var made = new StarfieldMod(ModKey.FromFileName("sfs_j66_layouts.esm"), StarfieldRelease.Starfield);
        var pack = made.Packages.AddNew("sfs_j66_Package");
        pack.OnBegin = new PackageEvent();
        pack.OnEnd = new PackageEvent();
        pack.OnEnd.Topics.Add(new TopicReferenceSubtype { Subtype = new RecordType("WXYZ") });
        var topicData = new PackageDataTopic { Name = "Topic Input", TPIC = new byte[] { 1, 2, 3, 4 } };
        topicData.Topics.Add(new TopicReferenceSubtype { Subtype = new RecordType("EFGH") });
        pack.Data[0] = topicData;
        pack.Data[1] = new PackageDataTarget { Name = "Odd Target", Type = PackageDataTarget.Types.SingleRef, Target = new PackageTargetUnknown { Type = 9, Data = 5 } };
        string madePath = Path.Combine(ctx.Folder("j66"), made.ModKey.FileName);
        var madeOrder = new LoadOrder<IModMasterStyledGetter>(new[] { new KeyedMasterStyle(made.ModKey, Mutagen.Bethesda.Plugins.MasterStyle.Full) });
        made.WriteToBinary(madePath, new BinaryWriteParameters { MasterFlagsLookup = madeOrder, ModKey = ModKeyOption.CorrectToPath });
        using var madeIndex = new HeaderIndex(PluginSource.FromFiles(new[] { madePath }), ctx.Options());
        madeIndex.Refresh();
        using var me = new RecordEngine(madeIndex, ctx.Schema, Path.Combine(ctx.Work, "verify-j66"), ctx.Data);
        me.Sessions.Use(made.ModKey.FileName);
        var mp = me.Load(made.ModKey.FileName, pack.FormKey.ID.ToString("X6", CultureInfo.InvariantCulture));
        var rp = (IPackageGetter)mp.Record;
        Check(rp.OnBegin!.Topics.Count == 0 && rp.OnEnd!.Topics.Count == 1, $"{mp.Label}: Mutagen reads {rp.OnBegin?.Topics.Count} OnBegin and {rp.OnEnd?.Topics.Count} OnEnd topics");
        var raw = me.Raw(mp.Ref);
        string order = string.Join(" ", raw.Subrecords.Select(x => x.Signature));
        Check(order.Contains("ANAM PDTO TPIC", StringComparison.Ordinal) && order.Contains("POBA INAM POEA INAM PDTO", StringComparison.Ordinal), $"{mp.Label}: Mutagen wrote {order}");
        Item(me, mp, "OnEnd.Topics[0].Subtype", "PDTO", 4, 4, 1, (b, _) => (System.Text.Encoding.ASCII.GetString(b) == "WXYZ", ((ITopicReferenceSubtypeGetter)rp.OnEnd!.Topics[0]).Subtype.Type));
        var rTopic = (IPackageDataTopicGetter)rp.Data[0];
        Item(me, mp, "Data[0].Value.TPIC", "TPIC", 0, 4, 0, (b, _) => (b.SequenceEqual(rTopic.TPIC!.Value.ToArray()), Convert.ToHexString(rTopic.TPIC!.Value.ToArray())));
        Item(me, mp, "Data[0].Value.Topics[0].Subtype", "PDTO", 4, 4, 0, (b, _) => (System.Text.Encoding.ASCII.GetString(b) == "EFGH", ((ITopicReferenceSubtypeGetter)rTopic.Topics[0]).Subtype.Type));
        var odd = (IPackageTargetUnknownGetter)((IPackageDataTargetGetter)rp.Data[1]).Target!;
        // Since fixes.24 (C83): an unknown package target's Type at 0 of PTDA, its Data at 4
        Check(odd.Type == 9, $"the made plugin's unknown package target has Type {odd.Type}, not 9");
        Item(me, mp, "Data[1].Value.Target.Type", "PTDA", 0, 4, 0, (b, _) => (U32(b) == 9, "9"));
        Item(me, mp, "Data[1].Value.Target.Data", "PTDA", 4, 4, 0, (b, _) => (U32(b) == 5, "5"));
        return n;
    }

    /// <summary>
    /// J66, since fixes.18: the lists SFSearch's own copies laid out until the move (S67, and the J66 cases) are found by
    /// Mutagen's own Layouts/HandReadLayouts.xml (C47; since fixes.20, C57, TES4's too: SFSearch has no file), each checked against
    /// Mutagen's value; IDLM, which SFSearch's file never laid out, too; and ten of these records walk under Mutagen's file
    /// without a problem (every Null, count and class held).
    /// </summary>
    /// <summary>A value Mutagen's value positions don't note (GAPS S173): no slice, and the reason says what Mutagen did note.</summary>
    static void NotNoted(RecordEngine e, LoadedRecord rec, string field, string said)
    {
        var ev = e.Evidence(rec, field);
        Check(ev.Item is { Located: false } && ev.Slices.All(x => x.FieldBytes == null) && ev.Item.Reason?.Contains(said, StringComparison.Ordinal) == true,
            $"{rec.Label} {field}: expected no slice and the reason \"{said}\" (S173):\n{ev.ToText()}");
    }

    static int PackageLayouts(Ctx ctx, List<string> lines)
    {
        int n = 0;
        var m = ctx.Schema.Model;
        // Since fixes.20 (C57) every layout is Mutagen's, TES4's master list and the record header included
        Check(m.HandLayouts.Any(h => h.Kind == "Record" && h.Sig == "TES4") && m.HandLayouts.Count(h => h.Kind == "Header") == 2, "package layouts: Mutagen's file doesn't lay out TES4 and the record header");
        var e = ctx.Engine;
        e.Sessions.Use("Starfield.esm", "SFBGS00D.esm", "SFBGS006.esm", "SFBGS007.esm");
        void Item(string plugin, string record, string field, string sub, int offset, int size, int occurrence, string layout)
        {
            var rec = e.Load(plugin, record);
            var ev = e.Evidence(rec, field);
            string text = ev.ToText();
            Check(ev.Item is { Located: true } && ev.Slices.Count == 1 && ev.Slices[0].FieldBytes != null && ev.Subrecord == sub && ev.Offset == offset && ev.Size == size && ev.Slices[0].Occurrence == occurrence,
                $"package layouts: {rec.Label} {field}: {ev.Subrecord} #{ev.Slices.FirstOrDefault()?.Occurrence} offset {ev.Offset}, {ev.Size} bytes; expected {sub} #{occurrence} offset {offset}, {size} bytes: {ev.Item?.Reason}\n{text}");
            Check(text.Contains("Checked:", StringComparison.Ordinal) || text.Contains("checked", StringComparison.Ordinal), $"package layouts: {rec.Label} {field}: not checked against Mutagen's value:\n{text}");
            Check(text.Contains("(its value positions)", StringComparison.Ordinal) || text.Contains("Mutagen's value positions note no bytes of its own", StringComparison.Ordinal),
                $"package layouts: {rec.Label} {field}: not placed by Mutagen's value positions:\n{text}");
            lines.Add($"package layouts (J66): {rec.Label} {field} at {sub} #{occurrence + 1} offset {offset} by Mutagen's {layout}");
            n++;
        }
        // The S67 lists (HandLists) and the J66 cases (ReaderWins), by Mutagen's file
        Item("Starfield.esm", "00118F", "Aliases[1].Name", "ALID", 0, 18, 1, "Record QUST");
        Item("Starfield.esm", "2733E2", "NextAliasID", "ANAM", 0, 4, 0, "Record QUST");
        Item("Starfield.esm", "2733E2", "Aliases[2].Collection[0].ID", "ALCS", 0, 4, 0, "Record QUST");
        Item("Starfield.esm", "0013A8", "UnusedConditions[1].Data.RunOnType", "CTDA", 20, 4, 1, "Record QUST");
        Item("Starfield.esm", "184BC1", "Rules.Rules[0].Names[2].Text", "WNAM", 0, 4, 2, "Record INNR");
        Item("SFBGS00D.esm", "07ADE1", "LnamEntries[1].ReferenceMod", "LNAM", 12, 4, 0, "Record LGDI");
        Item("SFBGS00D.esm", "07ADE1", "LnamEntries[1].Conditions[1].ComparisonValue", "CTDA", 4, 4, 3, "Record LGDI");
        Item("Starfield.esm", "005636", "Actions[1].Index", "INAM", 0, 4, 1, "Record SCEN");
        Item("SFBGS007.esm", "000016", "Options.GameplayOptionGroups[0]", "GOGL", 0, 4, 0, "Record GPOG");
        Item("Starfield.esm", "004DF6", "ProcedureTree[1].ProcedureType", "PNAM", 0, 14, 0, "Record PACK");
        Item("Starfield.esm", "004DF6", "Data[0].Value.Name", "BNAM", 0, 7, 0, "Record PACK");
        Item("Starfield.esm", "004DF6", "Data[0].Value.Flags", "PNAM", 0, 4, 1, "Record PACK");
        Item("Starfield.esm", "1AC0C5", "OnEnd.Topics[0].Reference", "PDTO", 4, 4, 1, "Record PACK");
        Item("Starfield.esm", "002CB1", "Data[0].Value.Location.Radius", "PLDT", 8, 4, 0, "Record PACK");
        Item("Starfield.esm", "002CB1", "Data[2].Value.Data", "CNAM", 0, 1, 1, "Record PACK");
        Item("Starfield.esm", "002CB1", "Data[16].Value.Name", "BNAM", 0, 12, 1, "Record PACK");
        Item("SFBGS00D.esm", "07C785", "Traversals[1].Traversal", "XTV2", 100, 4, 0, "Record REFR");
        Item("SFBGS00D.esm", "07C785", "Traversals[1].Flags", "XTV2", 96, 4, 0, "Record REFR");
        Item("Starfield.esm", "3A6ABF", "Patrol.Topics[0].Reference", "PDTO", 4, 4, 0, "Record REFR");
        Item("Starfield.esm", "29EF0E", "NumTraversalFluffBytes", "XTV2", 104, 8, 0, "Record CELL");
        Item("Starfield.esm", "249FA5", "Weather.Weathers[0].Weather", "RDWT", 0, 4, 0, "Record REGN");
        Item("Starfield.esm", "13C557", "Components[0].Items[0].Ender.Unknown2", "VLMS", 88, 4, 0, "AnyRecord BFCB");
        Item("Starfield.esm", "0478AD", "Components[0].Data.Data[0].SurfacePattern", "DAT2", 4, 4, 0, "AnyRecord BFCB");
        Item("Starfield.esm", "00347D", "BipedObjects[3].Value.Name", "NAME", 0, 5, 3, "Record RACE");
        Item("SFBGS006.esm", "000FF1:Starfield.esm", "MapInfos[4].IslandData.Triangles[1]", "NVMI", 319, 6, 4, "Record NAVI");
        Item("SFBGS006.esm", "000FF1:Starfield.esm", "MapInfos[1].PathingCell.ParentCell", "NVMI", 49, 4, 1, "Record NAVI");
        Item("SFBGS006.esm", "000FF1:Starfield.esm", "MapInfos[0].PathingCell.Grid", "NVMI", 53, 4, 0, "Record NAVI");
        // IDLM, which SFSearch's file never laid out: an idle marker's animations (IDLA FormIDs)
        Item("Starfield.esm", "027107", "Animations[1]", "IDLA", 4, 4, 0, "Record IDLM");
        // Each of these records walks under Mutagen's file without a problem, and every value the walk places from the bytes
        // alone (as the byte link and value walks read them) is where Mutagen's own value positions (C60) say Mutagen read it
        foreach (var (plugin, record) in new[] { ("Starfield.esm", "002CB1"), ("Starfield.esm", "1AC0C5"), ("Starfield.esm", "3A6ABF"), ("Starfield.esm", "29EF0E"),
            ("Starfield.esm", "2733E2"), ("SFBGS00D.esm", "07ADE1"), ("Starfield.esm", "005636"), ("SFBGS007.esm", "000016"), ("Starfield.esm", "13C557"), ("Starfield.esm", "0478AD") })
        {
            var rec = e.Load(plugin, record);
            var raw = e.Raw(rec.Ref);
            var classes = new Dictionary<string, string>(StringComparer.Ordinal);
            void Classes(IEnumerable<CardField> fs) { foreach (var f in fs) { if (f.Object != null) classes.TryAdd(f.Path, f.Object); if (f.Children != null) Classes(f.Children); } }
            Classes(e.Card(rec).Fields);
            var walk = HandLayoutWalk.RunBytes(m, raw, rec.Session.Localized, p => classes.GetValueOrDefault(p), h => h.Kind == "Record" && h.Sig == raw.Signature || h.Kind == "AnyRecord" && HandLayoutWalk.Holds(h, raw));
            Check(walk.Problems.Count == 0 && walk.Used.Count > 0, $"package layouts: {rec.Label}: the walk of {string.Join(", ", walk.Used)} met {string.Join("; ", walk.Problems)}");
            var pos = rec.Session.Positions(raw);
            int held = 0;
            var off = new List<string>();
            int notNoted = 0;
            foreach (var v in walk.Values.Values.Where(v => v.Kind is not ("Const" or "View") && v.Size > 0))
            {
                // Held where both place a value: Mutagen noted a value at the walk's bytes (by whatever path: a dictionary's
                // entries are named by key, the walk's by order), and nothing of the walk's path elsewhere
                // (a text with its length before it: Mutagen's value covers the length too)
                bool there = pos.Positions.Any(p => p.Kind == ValuePositionKind.Value && p.SubrecordIndex == v.Sub && p.Offset <= v.Offset && v.Offset + v.Size <= p.Offset + p.Length);
                bool overlaps = pos.Positions.Any(p => p.Kind == ValuePositionKind.Value && p.SubrecordIndex == v.Sub && p.Offset < v.Offset + v.Size && v.Offset < p.Offset + p.Length);
                if (there) held++;
                else if (!overlaps) notNoted++;
                else off.Add($"{v.Path} at #{v.Sub + 1} {v.Offset} ({v.Size}); Mutagen there: {string.Join(", ", pos.Positions.Where(p => p.Kind == ValuePositionKind.Value && p.SubrecordIndex == v.Sub && p.Offset < v.Offset + v.Size && v.Offset < p.Offset + p.Length).Select(p => $"{p.Path} {p.Offset} ({p.Length})"))}");
            }
            Check(off.Count == 0, $"package layouts: {rec.Label}: {off.Count} of the layout walk's values aren't where Mutagen's value positions say: {string.Join("; ", off.Take(8))}");
            lines.Add($"package layouts: {rec.Label}: {held} values of the layout walk where Mutagen's value positions say{(notNoted > 0 ? $"; {notNoted} at bytes Mutagen's positions don't note (S173)" : "")}");
        }
        return n;
    }

    /// <summary>
    /// GAPS S102: a field of the record's own whose signature a component holds too (a world's LOD Water Height NAM4 and
    /// its planet content manager component's Debug Color NAM4). Evidence names only the subrecords outside BFCB … BFCE,
    /// each with its ordinal among all the record's subrecords of that signature, and says the others are a component's;
    /// a component's field names only its own component's. Every such shape the schema has (a record type's own field
    /// and a component's field on one signature) is looked for in Starfield.esm, and the first record of each that holds
    /// the signature both inside and outside a component is checked.
    /// </summary>
    static int ComponentShared(Ctx ctx, List<string> lines)
    {
        var e = ctx.Engine;
        var m = ctx.Schema.Model;
        var sf = ctx.DataIndex.Plugin("Starfield.esm")!;
        var seg = sf.Segment!;
        // Signatures a component's own fields use (not nested records)
        var componentSigs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in m.Variants("AComponent"))
            foreach (var r in m.Expand(kind)) if (r.Subrecord is { Length: 4 } s && s != "BFCB" && s != "BFCE") componentSigs.Add(s);
        int n = 0;
        var shapes = new List<string>();
        foreach (var t in m.RecordTypes)
        {
            if (!m.Expand(t.Name).Any(r => r.Path.StartsWith("Components[*]", StringComparison.Ordinal))) continue;
            var own = m.Expand(t.Name).Where(r => !r.Path.StartsWith("Components[*]", StringComparison.Ordinal) && !r.Path.Contains('[') && r.Def.Kind is not ("Object" or "List" or "Marker" or "Break")
                && r.Subrecord is { } s && componentSigs.Contains(s) && r.Stack.Count(c => c == '/') <= 3).GroupBy(r => r.Subrecord!).Select(g => g.First()).ToList();
            if (own.Count == 0) continue;
            var entries = seg.EntriesOf(t.Sig!).Take(4000).ToList();
            foreach (var row in own)
            {
                foreach (int i in entries)
                {
                    var raw = e.Raw(new RecordRef(sf.Id, i));
                    int depth = 0; var inside = new List<int>(); var outside = new List<int>(); int ordinal = 0;
                    var ordinals = new Dictionary<int, int>();
                    for (int j = 0; j < raw.Subrecords.Count; j++)
                    {
                        string s = raw.Subrecords[j].Signature;
                        if (s == "BFCB") depth++;
                        if (s == row.Subrecord) { ordinals[j] = ordinal++; (depth > 0 ? inside : outside).Add(j); }
                        if (s == "BFCE" && depth > 0) depth--;
                    }
                    if (inside.Count == 0 || outside.Count == 0) continue;
                    var rec = e.Load(new RecordRef(sf.Id, i));
                    var ev = e.Evidence(rec, row.Path);
                    if (ev.Item is { Located: true }) break; // placed exactly by the layouts: not the shape
                    var shown = ev.Slices.Select(x => x.Occurrence).ToList();
                    var want = outside.Take(4).Select(j => ordinals[j]).ToList();
                    Check(ev.Subrecord == row.Subrecord && shown.SequenceEqual(want) && ev.Candidates == outside.Count && ev.Occurrences == inside.Count + outside.Count,
                        $"{rec.Label} {row.Path}: evidence shows {row.Subrecord} #{string.Join(", #", shown)} of {ev.Occurrences} (candidates {ev.Candidates}); the record's own are #{string.Join(", #", want)}:\n{ev.ToText()}");
                    Check(ev.ToText().Contains("inside a component (between BFCB and BFCE)", StringComparison.Ordinal) && !ev.ToText().Contains($"more {row.Subrecord} subrecords", StringComparison.Ordinal),
                        $"{rec.Label} {row.Path}: the note on the component's {row.Subrecord} is missing, or the component's are counted as more:\n{ev.ToText()}");
                    foreach (var (sl, j) in ev.Slices.Zip(outside))
                        Check(Bytes(sl.SubrecordBytes.Split(" …")[0]).SequenceEqual(raw.Content(raw.Subrecords[j]).ToArray()[..Math.Min(64, raw.Subrecords[j].Length)]), $"{rec.Label} {row.Path}: a slice isn't the bytes of the record's own {row.Subrecord}");
                    lines.Add($"component shares the signature (S102): {rec.Label} {row.Path}: {row.Subrecord} #{string.Join(", #", shown.Select(x => x + 1))} of {ev.Occurrences} shown; the component's #{string.Join(", #", inside.Select(j => ordinals[j] + 1))} left out");
                    shapes.Add($"{t.Sig} {row.Subrecord}");
                    n++;
                    break;
                }
            }
        }
        // The case the gap was found on, and its component's field the other way round
        var world = e.Load("Starfield.esm", "OEAH002World");
        var lod = e.Evidence(world, "LodWaterHeight");
        Check(lod.Slices.Count == 1 && lod.Slices[0].Occurrence == 1 && lod.Slices[0].SubrecordBytes == "00 00 00 00" && lod.Candidates == 1 && lod.Occurrences == 2,
            $"OEAH002World LodWaterHeight: not the 2nd NAM4 alone:\n{lod.ToText()}");
        Check(((IWorldspaceGetter)world.Record).LodWaterHeight == 0f, "OEAH002World: Mutagen's LodWaterHeight isn't 0");
        var comps = ((IWorldspaceGetter)world.Record).Components;
        int k = comps.ToList().FindIndex(c => c is IPlanetContentManagerContentPropertiesComponentGetter);
        Check(k >= 0, "OEAH002World has no planet content manager component");
        var debug = e.Evidence(world, $"Components[{k}].DebugColor");
        Check(debug.Slices.Count == 1 && debug.Slices[0].Occurrence == 0 && debug.Slices[0].SubrecordBytes == "00 FF 00 00",
            $"OEAH002World Components[{k}].DebugColor: not the 1st NAM4 alone:\n{debug.ToText()}");
        lines.Add($"component shares the signature (S102): OEAH002World (399C3E:Starfield.esm) LodWaterHeight is NAM4 #2 (00 00 00 00) alone; Components[{k}].DebugColor is NAM4 #1 (00 FF 00 00) alone");
        Check(n > 0, "no record type of Starfield.esm has a field of its own on a signature a component holds too");
        return n + 2;
    }

    /// <summary>
    /// GAPS S88: the plugin header (TES4), named by its plugin. Each field's bytes are found where Mutagen reads them (the
    /// record header for the flags and versions, HEDR, a master's MAST and DATA cut at each MAST as SFSearch's TES4
    /// layout gives them, ONAM, TNAM), are the file's own bytes there, and decode to Mutagen's value read through its
    /// typed IStarfieldModHeaderGetter. Plugins: Starfield.esm (no masters), Constellation.esm (light), SFBGS006.esm
    /// (medium), BlueprintShips-SFBGS050.esm (flag 0x800, Blueprint since fixes.18, C48), df_weaponmod_tapatch.esm
    /// (10 masters), and a plugin made here whose masters each have a DATA (Starfield's plugins write none). A record's own
    /// header (its flags, FormKey, versions) is held the same way.
    /// </summary>
    static int Header(Ctx ctx, List<string> lines)
    {
        int n = 0;
        // GAPS S90: each header field's xEdit path, from SF1Edit's export of the 370 headers paired with Mutagen's
        // ModHeader (xedit-tes4-2026-11-15). DATA (FileSize) and INTV have none: xEdit defines no DATA in a master file,
        // and no plugin in Data has either, so nothing was there to pair
        var xedit = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Flags"] = @"\Record Header\Record Flags", ["FormID"] = @"\Record Header\FormID", ["Version"] = @"\Record Header\Version Control Info 1",
            ["FormVersion"] = @"\Record Header\Form Version", ["Version2"] = @"\Record Header\Version Control Info 2",
            ["Stats.Version"] = @"\HEDR - Header\Version", ["Stats.NumRecords"] = @"\HEDR - Header\Number of Records", ["Stats.NextFormID"] = @"\HEDR - Header\Next Object ID",
            ["Author"] = @"\CNAM - Author", ["Description"] = @"\SNAM - Description", ["Branch"] = @"\BNAM - Branch", ["InteriorCellCount"] = @"\INCC - Interior Cell Count", ["CHGL"] = @"\CHGL - Unknown",
            ["MasterReferences[*].Master"] = @"\Master Files\Master File\MAST - FileName", ["OverriddenForms[*]"] = @"\ONAM - Overridden Forms\Form #*",
            ["TransientTypes[*].FormType"] = @"\Transient Types (CK only)\TNAM - Transient Type\FormType", ["TransientTypes[*].Links[*]"] = @"\Transient Types (CK only)\TNAM - Transient Type\Unknown\Unknown #*",
        };
        var named = new List<string>();
        void XEditName(LoadedHeader h, string field, Evidence ev)
        {
            string plain = System.Text.RegularExpressions.Regex.Replace(field, @"\[\d+\]", "[*]");
            string? want = xedit.GetValueOrDefault(plain);
            Check(ev.XEdit == want, $"{h.Label} {field}: evidence gives the xEdit path {ev.XEdit ?? "(none)"}, not {want ?? "(none)"}");
            if (want != null) named.Add(plain);
        }
        void Field(RecordEngine e, LoadedHeader h, string field, string? sub, int offset, int size, int occurrence, Func<byte[], string?, (bool Same, string Mutagen)> same, bool located = true)
        {
            var ev = e.HeaderEvidence(h, field);
            Check(ev.Slices.Count == 1 && ev.Slices[0].FieldBytes != null, $"{h.Label} {field}: not one slice ({ev.Slices.Count}):\n{ev.ToText()}");
            Check(!located || ev.Item is { Located: true }, $"{h.Label} {field}: not located and checked: {ev.Item?.Reason}\n{ev.ToText()}");
            if (ev.Slices.Count != 1 || ev.Slices[0].FieldBytes == null) return;
            var bytes = Bytes(ev.Slices[0].FieldBytes!);
            if (sub == null)
            {
                Check(ev.InHeader && ev.Subrecord == null && ev.Offset == offset && ev.Size == size, $"{h.Label} {field}: header {ev.InHeader}, {ev.Subrecord} offset {ev.Offset}, {ev.Size} bytes; expected the record header at {offset}, {size} bytes");
                Check(h.Raw.Header.AsSpan(offset, size).SequenceEqual(bytes), $"{h.Label} {field}: the slice isn't the file's header bytes at {offset}");
            }
            else
            {
                Check(!ev.InHeader && ev.Subrecord == sub && ev.Offset == offset && ev.Size == size && ev.Slices[0].Occurrence == occurrence,
                    $"{h.Label} {field}: {ev.Subrecord} #{ev.Slices[0].Occurrence} offset {ev.Offset}, {ev.Size} bytes; expected {sub} #{occurrence} offset {offset}, {size} bytes");
                var s = h.Raw.Subrecords.Where(x => x.Signature == sub).ElementAt(occurrence);
                Check(h.Raw.Content(s).Slice(offset, size).SequenceEqual(bytes), $"{h.Label} {field}: the slice isn't the file's bytes at that place");
            }
            var (ok, mutagen) = same(bytes, ev.Slices[0].Reads);
            Check(ok, $"{h.Label} {field}: the slice {ev.Slices[0].FieldBytes} (reads {ev.Slices[0].Reads}) isn't Mutagen's value {mutagen}");
            XEditName(h, field, ev);
            lines.Add($"header (S88): {h.Label} {field} at {(sub == null ? "the record header" : $"{sub} #{occurrence + 1}")} offset {offset} ({ev.Slices[0].FieldBytes}) = {mutagen}");
            n++;
        }
        // A list item of the plugin header beyond its master list: since fixes.24 (C82, GAPS S172 fixed) Mutagen's value
        // positions cover the plugin header, so the item has its exact slice (its value positions)
        void Placed(RecordEngine e, LoadedHeader h, string field, string sub, int offset, int size, int occurrence, Func<byte[], string?, (bool Same, string Mutagen)> same)
        {
            Field(e, h, field, sub, offset, size, occurrence, same);
            var ev = e.HeaderEvidence(h, field);
            Check(ev.ToText().Contains("(its value positions)", StringComparison.Ordinal), $"{h.Label} {field}: not placed by Mutagen's value positions:\n{ev.ToText()}");
        }
        // A value that is its whole subrecord (the schema gives such a field no offset: the answer shows the subrecord)
        void Whole(RecordEngine e, LoadedHeader h, string field, string sub, Func<byte[], (bool Same, string Mutagen)> same)
        {
            var ev = e.HeaderEvidence(h, field);
            var s = h.Raw.Subrecords.Single(x => x.Signature == sub);
            var content = h.Raw.Content(s).ToArray();
            Check(ev.Subrecord == sub && ev.Slices.Count == 1 && Bytes(ev.Slices[0].SubrecordBytes) is var shown && shown.SequenceEqual(content), $"{h.Label} {field}: not the whole {sub}:\n{ev.ToText()}");
            var (ok, mutagen) = same(content);
            Check(ok, $"{h.Label} {field}: {sub} ({RawRecord.Hex(content)}) isn't Mutagen's value {mutagen}");
            XEditName(h, field, ev);
            lines.Add($"header (S88): {h.Label} {field} is the whole {sub} ({RawRecord.Hex(content)}) = {mutagen}");
            n++;
        }
        static (bool, string) Num(ulong have, ulong m) => (have == m, m.ToString(CultureInfo.InvariantCulture));
        static ulong U(byte[] b) => b.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b) : b.Length == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(b) : U32(b);
        static (bool, string) Text0(byte[] b, string? m) => (b.Length > 0 && b[^1] == 0 && System.Text.Encoding.UTF8.GetString(b, 0, b.Length - 1) == m, m ?? "(null)");

        var e = ctx.Engine;
        e.Sessions.Use("Starfield.esm", "Constellation.esm", "SFBGS006.esm", "BlueprintShips-SFBGS050.esm", "df_weaponmod_tapatch.esm", "darkstar.esm");

        // Starfield.esm: no masters; flags, versions, HEDR, author, a transient type's link
        {
            var h = e.LoadHeader("Starfield.esm");
            var m = h.Header;
            Check(m.MasterReferences.Count == 0, $"{h.Label}: Mutagen reads {m.MasterReferences.Count} masters");
            Field(e, h, "Flags", null, 8, 4, 0, (b, _) => Num(U(b), (ulong)(int)m.Flags));
            Field(e, h, "Version", null, 16, 4, 0, (b, _) => Num(U(b), (ulong)(uint)m.Version));
            Field(e, h, "FormVersion", null, 20, 2, 0, (b, _) => Num(U(b), m.FormVersion));
            Field(e, h, "Version2", null, 22, 2, 0, (b, _) => Num(U(b), m.Version2));
            Field(e, h, "FormID", null, 12, 4, 0, (b, _) => Num(U(b), m.FormID));
            Field(e, h, "Stats.Version", "HEDR", 0, 4, 0, (b, _) => (F(b) == m.Stats.Version, m.Stats.Version.ToString("R", CultureInfo.InvariantCulture)));
            Field(e, h, "Stats.NumRecords", "HEDR", 4, 4, 0, (b, _) => Num(U(b), m.Stats.NumRecords));
            Field(e, h, "Stats.NextFormID", "HEDR", 8, 4, 0, (b, _) => Num(U(b), m.Stats.NextFormID));
            var tt = m.TransientTypes[1];
            Placed(e, h, "TransientTypes[1].FormType", "TNAM", 0, 4, 1, (b, _) => (U32(b) == (uint)tt.FormType, tt.FormType.ToString()));
            Placed(e, h, "TransientTypes[1].Links[5]", "TNAM", 4 + 5 * 4, 4, 1, (b, reads) => Link(reads, tt.Links[5].FormKey));
            Whole(e, h, "Author", "CNAM", b => Text0(b, m.Author));
            // No master to name: the answer says so, with no slice
            try
            {
                var none = e.HeaderEvidence(h, "MasterReferences[0].Master");
                Check(none.Slices.All(x => x.FieldBytes == null), $"{h.Label} MasterReferences[0].Master: a slice for a master Starfield.esm doesn't have:\n{none.ToText()}");
                lines.Add($"header (S88): {h.Label} MasterReferences[0].Master: no slice ({none.ToText().Split('\n')[1].Trim()})");
            }
            catch (RecordException x) { lines.Add($"header (S88): {h.Label} MasterReferences[0].Master refused: {x.Message}"); }
        }
        // Constellation.esm: light, one master, INCC and CHGL
        {
            var h = e.LoadHeader("Constellation.esm");
            var m = h.Header;
            Check((m.Flags & StarfieldModHeader.HeaderFlag.Light) != 0, $"{h.Label}: not light ({m.Flags})");
            Field(e, h, "Flags", null, 8, 4, 0, (b, _) => Num(U(b), (ulong)(int)m.Flags));
            Field(e, h, "MasterReferences[0].Master", "MAST", 0, 14, 0, (b, _) => Text0(b, m.MasterReferences[0].Master.FileName));
            Whole(e, h, "InteriorCellCount", "INCC", b => Num(U32(b), (ulong)(m.InteriorCellCount ?? -1)));
            Whole(e, h, "CHGL", "CHGL", b => (b.AsSpan().SequenceEqual(m.CHGL!.Value.Span), RawRecord.Hex(m.CHGL!.Value.Span)));
        }
        // SFBGS006.esm: medium; its overridden forms (ONAM)
        {
            var h = e.LoadHeader("SFBGS006.esm");
            var m = h.Header;
            Check((m.Flags & StarfieldModHeader.HeaderFlag.Medium) != 0, $"{h.Label}: not medium ({m.Flags})");
            Field(e, h, "Flags", null, 8, 4, 0, (b, _) => Num(U(b), (ulong)(int)m.Flags));
            Placed(e, h, "OverriddenForms[3]", "ONAM", 12, 4, 0, (b, reads) => Link(reads, m.OverriddenForms![3].FormKey));
            Field(e, h, "MasterReferences[0].Master", "MAST", 0, 14, 0, (b, _) => Text0(b, m.MasterReferences[0].Master.FileName));
            Field(e, h, "Stats.NumRecords", "HEDR", 4, 4, 0, (b, _) => Num(U(b), m.Stats.NumRecords));
        }
        // BlueprintShips-SFBGS050.esm: flag 0x800, which Mutagen names Blueprint (xEdit's name) since fixes.18 (C48; it was
        // an unnamed bit, shown as 0x800, before)
        {
            var h = e.LoadHeader("BlueprintShips-SFBGS050.esm");
            var m = h.Header;
            Check((m.Flags & StarfieldModHeader.HeaderFlag.Blueprint) != 0 && (int)StarfieldModHeader.HeaderFlag.Blueprint == 0x800, $"{h.Label}: no flag Blueprint (0x800) ({m.Flags})");
            Field(e, h, "Flags", null, 8, 4, 0, (b, _) => Num(U(b), (ulong)(int)m.Flags));
            string? shown = e.HeaderCard(h).Fields.First(f => f.Name == "Flags").Text;
            Check(shown?.Contains("Blueprint", StringComparison.Ordinal) == true && !shown.Contains("0x800", StringComparison.Ordinal), $"{h.Label}: the card doesn't show the flag Blueprint by name: {shown}");
            Field(e, h, "MasterReferences[2].Master", "MAST", 0, 13, 2, (b, _) => Text0(b, m.MasterReferences[2].Master.FileName));
        }
        // df_weaponmod_tapatch.esm: 10 masters, each its own MAST
        {
            var h = e.LoadHeader("df_weaponmod_tapatch.esm");
            var m = h.Header;
            Check(m.MasterReferences.Count == 10, $"{h.Label}: Mutagen reads {m.MasterReferences.Count} masters");
            foreach (int k in new[] { 0, 4, 9 })
            {
                string want = m.MasterReferences[k].Master.FileName;
                Field(e, h, $"MasterReferences[{k}].Master", "MAST", 0, want.Length + 1, k, (b, _) => Text0(b, want));
            }
            var ev = e.HeaderEvidence(h, "MasterReferences[9].Master");
            // Since fixes.24 (C82) the master's place is Mutagen's value position (its masters noted as a List: MasterReferences[i])
            Check(ev.ToText().Contains("Mutagen's reader read MasterReferences[9].Master at offset 0 of MAST", StringComparison.Ordinal), $"{h.Label}: the master isn't placed by Mutagen's value positions:\n{ev.ToText()}");
        }
        // A record's own header: a weapon's flags, FormKey and versions
        {
            var rec = e.Load("darkstar.esm", "0003C2");
            var w = rec.Record;
            var raw = e.Raw(rec.Ref);
            void Rec(string field, int offset, int size, Func<byte[], string?, (bool Same, string Mutagen)> same)
            {
                var ev = e.Evidence(rec, field);
                Check(ev.InHeader && ev.Offset == offset && ev.Size == size && ev.Item is { Located: true } && ev.Slices.Count == 1, $"{rec.Label} {field}: not the record header at {offset}:\n{ev.ToText()}");
                var bytes = Bytes(ev.Slices[0].FieldBytes!);
                Check(raw.Header.AsSpan(offset, size).SequenceEqual(bytes), $"{rec.Label} {field}: the slice isn't the file's header bytes");
                var (ok, mutagen) = same(bytes, ev.Slices[0].Reads);
                Check(ok, $"{rec.Label} {field}: the slice {ev.Slices[0].FieldBytes} isn't Mutagen's value {mutagen}");
                // GAPS S97: a record's own header fields have xEdit's names now, as the plugin header's do (S90)
                var wantX = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MajorRecordFlagsRaw"] = @"\Record Header\Record Flags", ["FormKey"] = @"\Record Header\FormID", ["VersionControl"] = @"\Record Header\Version Control Info 1",
                    ["FormVersion"] = @"\Record Header\Form Version", ["Version2"] = @"\Record Header\Version Control Info 2",
                }[field];
                Check(ev.XEdit == wantX, $"{rec.Label} {field}: evidence gives the xEdit path {ev.XEdit ?? "(none)"}, not {wantX}");
                lines.Add($"record header (S88, S97): {rec.Label} {field} at offset {offset} ({ev.Slices[0].FieldBytes}) = {mutagen}; xEdit {ev.XEdit}");
                n++;
            }
            Rec("MajorRecordFlagsRaw", 8, 4, (b, _) => Num(U(b), (ulong)(uint)w.MajorRecordFlagsRaw));
            Rec("FormKey", 12, 4, (b, reads) => Link(reads, w.FormKey));
            Rec("VersionControl", 16, 4, (b, _) => Num(U(b), w.VersionControl));
            Rec("FormVersion", 20, 2, (b, _) => Num(U(b), ((IStarfieldMajorRecordGetter)w).FormVersion));
            Rec("Version2", 22, 2, (b, _) => Num(U(b), ((IStarfieldMajorRecordGetter)w).Version2));
        }
        // GAPS S106: the views Mutagen derives from the header's flags are the same 4 bytes at offset 8, for every record
        // type: a type's MajorFlags, the base's StarfieldMajorRecordFlags, Core's IsCompressed and IsDeleted (one bit
        // each). A weapon with bit 0x40000000 (CreatureRangedAttack_Mouth1_Oxygen), a compressed record and a darkstar
        // weapon, each checked against the file's bytes and Mutagen's own value.
        foreach (var (plugin, id) in new[] { ("Starfield.esm", "3FA258"), ("darkstar.esm", "0003C2"), ("Starfield.esm", "0476C4") })
        {
            var rec = e.Load(plugin, id);
            var m = (IStarfieldMajorRecordGetter)rec.Record;
            var raw = e.Raw(rec.Ref);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(raw.Header.AsSpan(8, 4));
            var views = new List<(string Field, ulong Mutagen, uint Mask)>
            {
                ("StarfieldMajorRecordFlags", unchecked((uint)(int)m.StarfieldMajorRecordFlags), 0xFFFF_FFFF),
                ("IsCompressed", m.IsCompressed ? 0x40000u : 0u, 0x40000),
                ("IsDeleted", m.IsDeleted ? 0x20u : 0u, 0x20),
            };
            if (rec.Record.GetType().GetProperty("MajorFlags") is { } mf) views.Insert(0, ("MajorFlags", unchecked((uint)Convert.ToInt64(mf.GetValue(rec.Record), CultureInfo.InvariantCulture)), 0xFFFF_FFFF));
            foreach (var (field, mutagen, mask) in views)
            {
                var ev = e.Evidence(rec, field);
                string text = ev.ToText();
                Check(ev.InHeader && ev.Offset == 8 && ev.Size == 4 && ev.Item is { Located: true } && ev.Slices.Count == 1 && !text.Contains("doesn't know which subrecord"),
                    $"{rec.Label} {field}: not placed in the record header at offset 8, 4 bytes:\n{text}");
                Check(raw.Header.AsSpan(8, 4).SequenceEqual(Bytes(ev.Slices[0].FieldBytes!)), $"{rec.Label} {field}: the slice isn't the header's flag bytes");
                Check((flags & mask) == mutagen, $"{rec.Label} {field}: the file's flags 0x{flags:X8} (mask 0x{mask:X}) aren't Mutagen's value 0x{mutagen:X}");
                Check(ev.XEdit == @"\Record Header\Record Flags", $@"{rec.Label} {field}: evidence gives the xEdit path {ev.XEdit ?? "(none)"}, not \Record Header\Record Flags");
                n++;
            }
            lines.Add($"record header flags (S106): {rec.Label} {string.Join(", ", views.Select(v => v.Field))} at offset 8 (0x{flags:X8}), each Mutagen's value; xEdit " + @"\Record Header\Record Flags");
        }

        // A made plugin whose two masters each have a DATA (FileSize). Mutagen writes none for Starfield
        // (ModHeaderWriteLogic.ConstructWriteMasters: IncludesMasterReferenceDataSubrecords is false), so the DATA are put
        // in after Mutagen wrote it, as the older games' plugins have them; Mutagen reads them as FileSize
        string folder = ctx.Folder("s88");
        var a = new StarfieldMod(ModKey.FromFileName("sfs_s88_a.esm"), StarfieldRelease.Starfield);
        a.Keywords.AddNew("sfs_s88_A");
        var b2 = new StarfieldMod(ModKey.FromFileName("sfs_s88_b.esm"), StarfieldRelease.Starfield);
        b2.Keywords.AddNew("sfs_s88_B");
        var made = new StarfieldMod(ModKey.FromFileName("sfs_s88_header.esm"), StarfieldRelease.Starfield);
        made.ModHeader.Author = "J62";
        made.ModHeader.Description = "a made header";
        made.ModHeader.INTV = 7;
        made.ModHeader.MasterReferences.Add(new MasterReference { Master = a.ModKey });
        made.ModHeader.MasterReferences.Add(new MasterReference { Master = b2.ModKey });
        var sizes = new ulong[] { 0x1122334455667788, 4242 };
        made.Keywords.AddNew("sfs_s88_Own");
        var order = new LoadOrder<IModMasterStyledGetter>(new[]
        {
            new KeyedMasterStyle(a.ModKey, Mutagen.Bethesda.Plugins.MasterStyle.Full), new KeyedMasterStyle(b2.ModKey, Mutagen.Bethesda.Plugins.MasterStyle.Full),
            new KeyedMasterStyle(made.ModKey, Mutagen.Bethesda.Plugins.MasterStyle.Full),
        });
        var paths = new List<string>();
        foreach (var mod in new[] { a, b2, made })
        {
            string path = Path.Combine(folder, mod.ModKey.FileName);
            mod.WriteToBinary(path, new BinaryWriteParameters { MasterFlagsLookup = order, ModKey = ModKeyOption.CorrectToPath, MastersListContent = MastersListContentOption.NoCheck });
            paths.Add(path);
        }
        WithMasterData(paths[2], sizes);
        using var madeIndex = new HeaderIndex(PluginSource.FromFiles(paths), ctx.Options());
        madeIndex.Refresh();
        using var me = new RecordEngine(madeIndex, ctx.Schema, Path.Combine(ctx.Work, "verify-s88"), ctx.Data);
        me.Sessions.Use(made.ModKey.FileName);
        {
            var h = me.LoadHeader(made.ModKey.FileName);
            var m = h.Header;
            Check(h.Raw.Subrecords.Count(x => x.Signature == "DATA") == 2 && m.MasterReferences.Count == 2, $"{h.Label}: {h.Raw.Subrecords.Count(x => x.Signature == "DATA")} DATA, {m.MasterReferences.Count} masters");
            Field(me, h, "MasterReferences[1].Master", "MAST", 0, 14, 1, (b, _) => Text0(b, m.MasterReferences[1].Master.FileName));
            Check(m.MasterReferences[0].FileSize == sizes[0] && m.MasterReferences[1].FileSize == sizes[1], $"{h.Label}: Mutagen reads the sizes {m.MasterReferences[0].FileSize}, {m.MasterReferences[1].FileSize}");
            Field(me, h, "MasterReferences[0].FileSize", "DATA", 0, 8, 0, (b, _) => Num(U(b), m.MasterReferences[0].FileSize ?? 0));
            Field(me, h, "MasterReferences[1].FileSize", "DATA", 0, 8, 1, (b, _) => Num(U(b), m.MasterReferences[1].FileSize ?? 0));
            Whole(me, h, "Description", "SNAM", b => Text0(b, m.Description));
            Whole(me, h, "INTV", "INTV", b => Num(U32(b), (ulong)(m.INTV ?? -1)));
            var ev = me.HeaderEvidence(h, "MasterReferences[1].FileSize");
            // Since fixes.24 (C82): the item and its value are Mutagen's value positions
            Check(ev.ToText().Contains("MasterReferences[1] is subrecords MAST DATA (#6 to #7); Mutagen's reader read MasterReferences[1].FileSize at offset 0 of DATA", StringComparison.Ordinal), $"{h.Label}: the item isn't MAST DATA:\n{ev.ToText()}");
        }
        // S90: the fields above that xEdit names gave its path in evidence, the main ones among them
        var missing = new[] { "Flags", "FormVersion", "Stats.NumRecords", "Stats.NextFormID", "MasterReferences[*].Master", "OverriddenForms[*]", "TransientTypes[*].Links[*]", "Author", "InteriorCellCount" }.Where(x => !named.Contains(x)).ToList();
        Check(missing.Count == 0, $"no header field checked for its xEdit path: {string.Join(", ", missing)}");
        lines.Add($"header (S90): {named.Count} header fields gave xEdit's path in evidence ({string.Join(", ", named.Distinct())})");
        return n;
    }

    /// <summary>Puts a DATA of 8 bytes (the master's size) after each MAST of a plugin's header, in the file.</summary>
    static void WithMasterData(string path, IReadOnlyList<ulong> sizes)
    {
        var b = File.ReadAllBytes(path);
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        var body = new List<byte>();
        int at = 24, k = 0;
        while (at < 24 + size)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at + 4));
            body.AddRange(b.AsSpan(at, 6 + len).ToArray());
            if (System.Text.Encoding.ASCII.GetString(b, at, 4) == "MAST")
            {
                body.AddRange("DATA"u8.ToArray()); body.AddRange(BitConverter.GetBytes((ushort)8)); body.AddRange(BitConverter.GetBytes(sizes[k++]));
            }
            at += 6 + len;
        }
        Check(k == sizes.Count, $"{path}: {k} MAST subrecords, {sizes.Count} sizes");
        var head = b.AsSpan(0, 24).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)body.Count);
        File.WriteAllBytes(path, head.Concat(body).Concat(b.Skip(24 + size)).ToArray());
    }

    static IAPerkEffectGetter PerkEffect(IMajorRecordGetter r, int i) => ((IPerkGetter)r).Ranks[0].Effects[i];

    static IPlanetModelComponentXMPMGetter Xmpm(IMajorRecordGetter r) => ((IPlanetModelComponentGetter)((IPlanetGetter)r).Components[0]).XMPM!;
}
