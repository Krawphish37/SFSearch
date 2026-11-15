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
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Records.Tests;

static class EvidenceItems
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    sealed record Case(string Plugin, string Record, string Field, string Shape, string Subrecord, int Offset, int Size, Func<IMajorRecordGetter, byte[], (bool Same, string Mutagen)> Same);

    static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    static float F(byte[] b) => BinaryPrimitives.ReadSingleLittleEndian(b);
    static uint U32(byte[] b) => BinaryPrimitives.ReadUInt32LittleEndian(b);

    /// <summary>A link's slice against Mutagen's FormKey: the label the slice reads as names that FormKey.</summary>
    static (bool, string) Link(string? reads, Mutagen.Bethesda.Plugins.FormKey key) => (reads != null && reads.EndsWith("(" + key + ")", StringComparison.Ordinal), key.ToString());

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
        var e = ctx.Engine;
        e.Sessions.Use("darkstar.esm", "darkstarpatch_atom74.esm", "Starfield.esm", "SFBGS003.esm");
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
            new("Starfield.esm", "00015E", "Clouds[5].Enabled", "a weather cloud's enabled bit (NAM1, inverted)", "NAM1", 0, 1,
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
                (r, b) => { string m = Xmpm(r).UnknownSubItems[0].Extension; return (System.Text.Encoding.UTF8.GetString(b).TrimEnd(' ') == m && b[^1] == 0, m); }),
            new("Starfield.esm", "03F59A", "Components[0].XMPM.UnknownSubItems[0].FolderHash", "a value after texts in an entry", "XMPM", 36, 4,
                (r, b) => { uint m = Xmpm(r).UnknownSubItems[0].FolderHash; return (U32(b) == m, m.ToString(CultureInfo.InvariantCulture)); }),
            // A planet's details (HNAM): eight texts with a 4-byte length, then fixed fields the schema gives no offset for
            new("Starfield.esm", "03F59A", "Details.Perihelion", "a value after eight length-prefixed texts (HNAM)", "HNAM", 66, 8,
                (r, b) => { double m = ((IPlanetGetter)r).Details!.Perihelion; return (BinaryPrimitives.ReadDoubleLittleEndian(b) == m, m.ToString("R", CultureInfo.InvariantCulture)); }),
            new("Starfield.esm", "03F59A", "Details.CatalogueId", "a text with a 4-byte length counting its zero byte", "HNAM", 15, 4,
                (r, b) => { string m = ((IPlanetGetter)r).Details!.CatalogueId; return (System.Text.Encoding.UTF8.GetString(b).TrimEnd(' ') == m, m); }),
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
            var bytes = Bytes(ev.Slices[0].FieldBytes!);
            // The slice is the file's own: the same bytes at that offset of that occurrence of the subrecord
            var raw = e.Raw(rec.Ref);
            var sub = raw.Subrecords.Where(s => s.Signature == ev.Subrecord).ElementAt(ev.Slices[0].Occurrence);
            Check(raw.Content(sub).Slice(ev.Offset!.Value, ev.Size!.Value).SequenceEqual(bytes), $"{rec.Label} {c.Field}: the slice isn't the file's bytes at that place");
            reads = ev.Slices[0].Reads;
            var (same, mutagen) = c.Same(rec.Record, bytes);
            Check(same, $"{rec.Label} {c.Field}: the slice {ev.Slices[0].FieldBytes} (reads {reads}) isn't Mutagen's value {mutagen}");
            // A field in no list has no item check of its own: the test's own comparison with Mutagen above holds it
            if (ev.Item != null) Check(ev.ToText().Contains("Checked: the bytes read", StringComparison.Ordinal), $"{rec.Label} {c.Field}: the text doesn't say the slice was checked:\n{ev.ToText()}");
            // Since fixes.15 (C196) no place is SFSearch's own: none is named as written in code (GAPS Part 2 row 33)
            Check(!ev.ToText().Contains("row 33", StringComparison.Ordinal), $"{rec.Label} {c.Field}: the text still names a place written in code:\n{ev.ToText()}");
            if (c.Field.EndsWith(".Modification", StringComparison.Ordinal))
                Check(ev.ToText().Contains("the XML's layoutMap on ", StringComparison.Ordinal), $"{rec.Label} {c.Field}: the record XML's layoutMap wasn't held against the layout:\n{ev.ToText()}");
            lines.Add($"{c.Shape}: {rec.Label} {c.Field} at {ev.Subrecord} offset {ev.Offset} ({ev.Slices[0].FieldBytes}) = {mutagen}");
        }

        // S35: a translated text is looked up in the one strings table the schema names (the XML's translated), not all three
        foreach (var (record, field, table) in new[] { ("0476C4", "Name", StringsSource.Normal), ("00423B", "Description", StringsSource.DL) })
        {
            var rec = e.Load("Starfield.esm", record);
            var asked = new List<StringsSource?>();
            var ev = FieldLookup.Evidence(e.Schema, e.Card(rec), e.Raw(rec.Ref), field, id => id.ToString("X8", CultureInfo.InvariantCulture),
                textOf: (id, language, t) => { asked.Add(t); return rec.Session.StringsFor(id, language, t); });
            Check(ev.Item is { Located: true } && asked.Count == 1 && asked[0] == table, $"{rec.Label} {field}: the strings tables asked were [{string.Join(", ", asked)}], not {table} alone; {ev.Item?.Reason}");
        }

        // Where the layout can't be followed exactly, no slice is given and the reason is: a race's biped object names,
        // which HandReadLayouts.xml lays out as 64 NAME subrecords, and which Mutagen keeps in a form the card doesn't
        // index that way
        var race = e.Evidence(e.Load("Starfield.esm", "00347D"), "BipedObjects[3].Name");
        Check(race.Item is not { Located: true } && race.Slices.All(s => s.FieldBytes == null), $"a race's biped object name was located:\n{race.ToText()}");
        // A field outside any list is answered as before
        var damage = e.Evidence(e.Load("darkstar.esm", "0003C2"), "AttackDamage");
        Check(damage.Item == null && damage.Slices[0].FieldBytes == "00 00 40 41", "AttackDamage's evidence changed");
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
                    Check(ev.ToText().Contains("Function is at offset 8 of the condition's CTDA (the XML's layoutOffset)", StringComparison.Ordinal), $"{rec.Label} {field}: not placed by its own layoutOffset:\n{ev.ToText()}");
                lines.Add($"unknown function (made plugin): {rec.Label} {field} at CTDA offset {ev.Offset} ({ev.Slices[0].FieldBytes}) = {got}");
            }
        }

        ctx.WriteReport("evidence-items.txt", lines);
        return $"{cases.Count} fields of {cases.Select(c => c.Shape).Distinct().Count()} shapes: each slice is the file's bytes and decodes to Mutagen's value ({string.Join("; ", lines.Skip(11).Take(3))} …); a race's biped name refused with its reason; AttackDamage unchanged; an unknown function's Function and parameters placed by its own layoutOffset (made plugin)";
    }

    static IAPerkEffectGetter PerkEffect(IMajorRecordGetter r, int i) => ((IPerkGetter)r).Ranks[0].Effects[i];

    static IPlanetModelComponentXMPMGetter Xmpm(IMajorRecordGetter r) => ((IPlanetModelComponentGetter)((IPlanetGetter)r).Components[0]).XMPM!;
}
