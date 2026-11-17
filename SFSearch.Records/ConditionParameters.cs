// A condition's function parameters as links (GAPS S156). A CTDA holds, after its compare value (4) and its function
// index (a uint16 at 8, two bytes after it unknown), its function's parameters from byte 12 to 20 (Condition.Data's
// layoutOffset): Mutagen's condition reader (ConditionBinaryCreateTranslation.CreateDataFromBinaryInternal) picks the
// ConditionData kind by the function index (the XML's conditionFunction on each kind), and that kind's generated reader
// reads its fields one after another from byte 12: those of the CTDA (Int32, Float, an Enum of its byteLength, a FormLink,
// a FormLinkOrIndex), skipping its texts, which come from CIS1 and CIS2. A FormLink is a link; a FormLinkOrIndex is one
// unless the condition's flags (CTDA byte 0) say UseAliases (0x02) or UsePackageData (0x08), when it is an alias or a
// package data index (FormLinkOrIndex.UsesLink, which Mutagen's EnumerateFormLinks follows). A function no kind names is
// read as UnknownConditionData: two Int32, no link.
// The parameter fields are named as the kinds name them (FirstParameter, SecondParameter; GetIsVoiceType's
// VoiceTypeOrList, GetEventData's Record), one link field a condition place each: <c>Conditions[*]{ConditionFloat}.Data.FirstParameter</c>
// counts every kind's FirstParameter that is a link there. Mutagen's property of that name, on whichever kind the
// condition is, holds the same link.
// Mutagen 0.55.0-fixes.20 (C58 W19, C60) maps the parameters of UnknownConditionData and five string-parameter kinds
// (GetGraphVariableFloat, GetGraphVariableInt, HasVMScript, GetVMScriptVariable, GetVMQuestVariable) right through the
// interfaces; their record XML is unchanged, so the layout read here is too: GetVMQuestVariable's FirstParameter, its one
// link (a FormLinkOrIndex), stays first at CTDA byte 12 (move-fixes20.md; sfsreport-tests link-census holds every link
// against Mutagen's).
using System.Buffers.Binary;
using System.Globalization;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

public static class ConditionParameters
{
    /// <summary>One link parameter of a kind: its field's name, its offset in the CTDA, and whether it is a FormLinkOrIndex.</summary>
    public readonly record struct Param(string Name, int Offset, bool OrIndex);

    sealed class Table
    {
        public readonly Dictionary<int, Param[]> ByFunction = new();
        public readonly Dictionary<int, string> KindByFunction = new();
        public string? CatchAll;
        public readonly List<string> Names = new();
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchemaModel, Table> Tables = new();

    static Table Get(SchemaModel m) => Tables.GetValue(m, Make);

    /// <summary>The flags (CTDA byte 0) that make a FormLinkOrIndex parameter an index: UseAliases, UsePackageData.</summary>
    public const byte IndexFlags = 0x02 | 0x08;

    /// <summary>The names of the parameter fields that are a link in some kind, in the kinds' order (FirstParameter,
    /// SecondParameter, VoiceTypeOrList, Record): each a link field at every condition place.</summary>
    public static IReadOnlyList<string> LinkNames(SchemaModel m) => Get(m).Names;

    /// <summary>The link parameters of the function a CTDA names (none for a function no kind names).</summary>
    public static IReadOnlyList<Param> Of(SchemaModel m, int function) => Get(m).ByFunction.TryGetValue(function, out var p) ? p : Array.Empty<Param>();

    /// <summary>The kind of ConditionData a function index picks (the XML's conditionFunction on each kind; "*" on the one
    /// that takes every index no other claims); null when the schema has none for it.</summary>
    public static string? KindOf(SchemaModel m, int function) { var t = Get(m); return t.KindByFunction.TryGetValue(function, out var k) ? k : t.CatchAll; }

    /// <summary>The links of one CTDA's parameters: field name, the FormID as stored, its offset.</summary>
    public static void Links(SchemaModel m, ReadOnlySpan<byte> ctda, Action<string, uint, int> link)
    {
        if (ctda.Length < 10) return;
        int function = BinaryPrimitives.ReadUInt16LittleEndian(ctda[8..]);
        bool index = (ctda[0] & IndexFlags) != 0;
        foreach (var p in Of(m, function))
        {
            if (p.Offset + 4 > ctda.Length || p.OrIndex && index) continue;
            link(p.Name, BinaryPrimitives.ReadUInt32LittleEndian(ctda[p.Offset..]), p.Offset);
        }
    }

    static Table Make(SchemaModel m)
    {
        var t = new Table();
        int start = m.Objects.TryGetValue("Condition", out var cond) ? cond.Fields.FirstOrDefault(f => f.Name == "Data")?.LayoutOffsetNumber ?? 12 : 12;
        foreach (var kind in m.Variants("ConditionData"))
        {
            var od = m.Objects[kind];
            if (od.ConditionFunction == "*") t.CatchAll = kind;
            if (!int.TryParse(od.ConditionFunction, NumberStyles.Integer, CultureInfo.InvariantCulture, out int function)) continue;
            t.KindByFunction[function] = kind;
            var links = new List<Param>();
            int at = start;
            foreach (var f in od.Fields.OrderBy(f => f.Ord))
            {
                // A text comes from CIS1 or CIS2; a field placed by hand-written code (the function itself) isn't read here
                if (f.Kind == "String" || f.Subrecord is "CIS1" or "CIS2" || f.LayoutOffsetNumber != null || f.Path != f.Name) continue;
                int? size = f.Kind switch
                {
                    "FormLink" or "FormLinkOrIndex" or "Int32" or "UInt32" or "Float" => 4,
                    "Int16" or "UInt16" => 2,
                    "Int8" or "UInt8" => 1,
                    "Enum" => f.Size ?? 4,
                    _ => null,
                };
                if (size is not int sz) break;
                if (f.Kind is "FormLink" or "FormLinkOrIndex")
                {
                    links.Add(new Param(f.Name, at, f.Kind == "FormLinkOrIndex"));
                    if (!t.Names.Contains(f.Name)) t.Names.Add(f.Name);
                }
                at += sz;
            }
            if (links.Count > 0) t.ByFunction[function] = links.ToArray();
        }
        return t;
    }
}
