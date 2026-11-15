// What the diff engine gives back: one RecordDiff for a pair of records, made of FieldDiffs.
using System.Text.Json.Nodes;

namespace SFSearch.Compare;

/// <summary>How two sides differ at one place.</summary>
public enum Change
{
    /// <summary>Both sides have a value there, and the values differ.</summary>
    Changed,
    /// <summary>Only the first record has it (a field that is set, an entry of a list).</summary>
    OnlyA,
    /// <summary>Only the second record has it.</summary>
    OnlyB,
    /// <summary>A list holds the same entries on both sides, in another order.</summary>
    Order,
}

/// <summary>What kind of thing differs. Only <see cref="Value"/> counts as a difference between two records.</summary>
public enum DiffClass
{
    /// <summary>A value of the record: this is a difference.</summary>
    Value,
    /// <summary>The record header's own numbers (form version, version control) and how the file stores the record
    /// (compressed or not). Not a value of the record: an identical copy saved by another tool differs here.</summary>
    Header,
    /// <summary>How long a data block is written (a version break): one side spells out trailing fields that the
    /// other leaves at their defaults. The values are the same.</summary>
    Layout,
    /// <summary>A translated text that every language shows the same on both sides, written another way: localized
    /// strings on one side and the text itself on the other, or the same strings in another order. The same value:
    /// it is still listed with the record's fields, under a heading that says so. A text whose languages were added
    /// or dropped, whose text differs in any language, or that is missing on one side and empty on the other is a
    /// <see cref="Value"/> difference.</summary>
    WrittenAnotherWay,
}

/// <summary>One difference between two records.</summary>
public sealed class FieldDiff
{
    /// <summary>The Mutagen name of the record's own field the difference sits in (<c>Keywords</c>, <c>ObjectTemplates</c>).</summary>
    public required string Top { get; init; }
    /// <summary>Where the difference is, in a form that is the same whenever the same thing differs:
    /// <c>AttackDamage</c>, <c>Items[Item=0003E3:darkstar.esm].Count</c>, <c>Conditions[2].ComparisonValue</c>.</summary>
    public required string Path { get; init; }
    /// <summary>The same place for people: links labelled as EditorID (FormID:plugin).</summary>
    public required string Shown { get; init; }
    /// <summary>For an entry of a list compared as a set: the entry (a FormKey). Null otherwise.</summary>
    public string? Element { get; init; }
    /// <summary>The readable name of the record's own field (xEdit's where a checked pair exists).</summary>
    public required string Readable { get; init; }
    /// <summary>The readable name of the place itself, when it is deeper than the record's own field.</summary>
    public string? LeafReadable { get; init; }
    /// <summary>The subrecord the place is read from, when the schema knows it.</summary>
    public string? Signature { get; init; }
    /// <summary>The subrecord of the record's own field.</summary>
    public string? TopSignature { get; init; }
    public required Change Change { get; init; }
    public DiffClass Class { get; init; } = DiffClass.Value;
    /// <summary>The first record's value as text (links labelled). Null when it has none there.</summary>
    public string? A { get; init; }
    public string? B { get; init; }
    public JsonNode? AJson { get; init; }
    public JsonNode? BJson { get; init; }
    public string? Note { get; init; }
    /// <summary>The rule by which SF1Edit's "identical to master" leaves this difference aside ("keywords (KWDA) in another
    /// order: the game sorts them"), or null when it counts it. Used only by the opt-in xEdit-style verdict (GAPS S49).</summary>
    public string? XEditRule { get; init; }
    /// <summary>The two values as texts that are equal exactly when the values are (links by what they are compared by).</summary>
    public string? CanonA { get; init; }
    public string? CanonB { get; init; }

    /// <summary>What names this difference whichever pair of records it was found in: the place, and for a set the entry and its direction.</summary>
    public string Identity => Element == null ? Path : $"{Path}{{{(Change == Change.OnlyA ? "-" : "+")}{Element}}}";

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["field"] = Top, ["path"] = Path, ["shown"] = Shown, ["readable"] = Readable,
            ["change"] = Change switch { Change.Changed => "changed", Change.OnlyA => "only_a", Change.OnlyB => "only_b", _ => "order" },
        };
        if (LeafReadable != null) o["leafReadable"] = LeafReadable;
        if (Signature != null) o["subrecord"] = Signature;
        if (Class != DiffClass.Value) o["class"] = Class switch { DiffClass.Header => "header", DiffClass.Layout => "layout", _ => "written_another_way" };
        if (Element != null) o["element"] = Element;
        o["a"] = A; o["b"] = B;
        if (AJson != null) o["aValue"] = AJson.DeepClone();
        if (BJson != null) o["bValue"] = BJson.DeepClone();
        if (Note != null) o["note"] = Note;
        if (XEditRule != null) o["xeditRule"] = XEditRule;
        return o;
    }
}

/// <summary>How the entries of one list were paired.</summary>
/// <param name="Path">The list.</param>
/// <param name="How">"links as a set", "by Item", "by position".</param>
public sealed record ListMatch(string Path, string How);

/// <summary>The result of comparing two records.</summary>
public sealed class RecordDiff
{
    public required string TypeA { get; init; }
    public required string TypeB { get; init; }
    public string? SignatureA { get; init; }
    public string? SignatureB { get; init; }
    /// <summary>The two records are of different types. They are reported as that, and no field is compared.</summary>
    public bool DifferentTypes => !string.Equals(TypeA, TypeB, StringComparison.Ordinal);
    /// <summary>Every difference found, in the schema's (file) order.</summary>
    public List<FieldDiff> All { get; } = new();
    /// <summary>How each list that differs was paired.</summary>
    public List<ListMatch> Lists { get; } = new();
    /// <summary>Records nested in the two (placed references of a cell, topics of a quest): they are records of their
    /// own and are compared as such, never as fields of the record that holds them.</summary>
    public List<string> ChildrenOnlyA { get; } = new();
    public List<string> ChildrenOnlyB { get; } = new();
    public List<string> ChildrenInBoth { get; } = new();
    /// <summary>The first (second) record is a partial form (record flag 0x4000): it holds only part of the record.
    /// The fields it leaves out are compared as not there; the game keeps the master's values for them.</summary>
    public bool PartialFormA { get; set; }
    public bool PartialFormB { get; set; }

    /// <summary>The differences that count: values of the record.</summary>
    public IEnumerable<FieldDiff> Values => All.Where(d => d.Class == DiffClass.Value);
    /// <summary>What differs without being a value: header numbers, a data block's length, a text written another way.</summary>
    public IEnumerable<FieldDiff> NotValues => All.Where(d => d.Class != DiffClass.Value);
    /// <summary>The same value written another way (a data block's length, a text held another way): listed with the
    /// record's fields, under a heading that says it is the same value. The record header's own numbers aren't.</summary>
    public IEnumerable<FieldDiff> Rewritten => All.Where(d => d.Class is DiffClass.Layout or DiffClass.WrittenAnotherWay);
    /// <summary>The record's own fields that hold only a difference that is the same value written another way, each once.</summary>
    public List<string> RewrittenFields
    {
        get
        {
            var values = TopFields.ToHashSet(StringComparer.Ordinal);
            return Rewritten.Select(d => d.Top).Where(t => !values.Contains(t)).Distinct(StringComparer.Ordinal).ToList();
        }
    }
    /// <summary>True when every difference that counts is one SF1Edit's "identical to master" leaves aside (GAPS S49).</summary>
    public bool SameLikeXEdit => !DifferentTypes && Values.All(d => d.XEditRule != null);
    /// <summary>The rules that made the record equal to SF1Edit, each once.</summary>
    public List<string> XEditRules => Values.Select(d => d.XEditRule).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
    /// <summary>True when the two records are of one type and no value differs.</summary>
    public bool Same => !DifferentTypes && !All.Any(d => d.Class == DiffClass.Value);
    /// <summary>The record's own fields that differ, each once, in order.</summary>
    public List<string> TopFields => Values.Select(d => d.Top).Distinct(StringComparer.Ordinal).ToList();
    public string? HowMatched(string listPath) => Lists.FirstOrDefault(l => l.Path == listPath)?.How;

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["typeA"] = TypeA, ["typeB"] = TypeB, ["differentTypes"] = DifferentTypes, ["same"] = Same };
        o["fields"] = new JsonArray(TopFields.Select(f => (JsonNode)f).ToArray());
        var rewritten = RewrittenFields;
        if (rewritten.Count > 0) o["sameValueWrittenAnotherWay"] = new JsonArray(rewritten.Select(f => (JsonNode)f).ToArray());
        o["differences"] = new JsonArray(Values.Select(d => (JsonNode)d.ToJson()).ToArray());
        var rest = NotValues.ToList();
        if (rest.Count > 0) o["notValues"] = new JsonArray(rest.Select(d => (JsonNode)d.ToJson()).ToArray());
        if (Lists.Count > 0) o["lists"] = new JsonArray(Lists.Select(l => (JsonNode)new JsonObject { ["path"] = l.Path, ["matched"] = l.How }).ToArray());
        if (ChildrenOnlyA.Count + ChildrenOnlyB.Count + ChildrenInBoth.Count > 0)
            o["nestedRecords"] = new JsonObject { ["onlyA"] = ChildrenOnlyA.Count, ["onlyB"] = ChildrenOnlyB.Count, ["inBoth"] = ChildrenInBoth.Count };
        return o;
    }
}
