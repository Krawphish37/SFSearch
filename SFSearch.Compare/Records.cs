// records: two records, in two plugins or in one. "expect" lists the fields that are allowed to differ: any other
// difference is reported as unexpected and makes the answer a failing one.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFSearch.Compare;

public sealed class ChildDiff
{
    public required string Label { get; init; }
    public required string Signature { get; init; }
    public RecordDiff? Diff { get; init; }
    public string? Problem { get; init; }
}

public sealed partial class RecordsAnswer : CompareAnswer
{
    public override string Kind => "records";
    public required string A { get; init; }
    public required string B { get; init; }
    public required string PluginA { get; init; }
    public required string PluginB { get; init; }
    public required RecordDiff Diff { get; init; }
    /// <summary>The fields that were allowed to differ. Null: none were named, so nothing is judged.</summary>
    public List<string>? Expect { get; init; }
    /// <summary>The differences outside the allowed fields.</summary>
    public List<FieldDiff> Unexpected { get; } = new();
    /// <summary>Allowed names that fit no field of the record type: likely a misspelling.</summary>
    public List<string> UnknownExpect { get; } = new();
    /// <summary>For each name in <see cref="UnknownExpect"/> that has near fields: the nearest real names, as every tool lists them.</summary>
    public Dictionary<string, List<string>> NearExpect { get; } = new(StringComparer.Ordinal);
    /// <summary>Records nested in both (the same FormKey on both sides), each compared as a record of its own.</summary>
    public List<ChildDiff> Children { get; } = new();
    public int ChildrenNotCompared { get; set; }

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Brackets();

    /// <summary>True when a difference sits in one of the allowed fields. A name fits by the field's Mutagen name,
    /// its readable name, or its path (with or without what is in brackets), letter case aside.</summary>
    public static bool Allowed(FieldDiff d, IEnumerable<string> expect)
    {
        string plain = Brackets().Replace(d.Path, "");
        foreach (var raw in expect)
        {
            string e = raw.Trim();
            if (e.Length == 0) continue;
            if (e.Equals(d.Top, StringComparison.OrdinalIgnoreCase) || e.Equals(d.Readable, StringComparison.OrdinalIgnoreCase) || e.Equals(d.LeafReadable, StringComparison.OrdinalIgnoreCase)
                || e.Equals(d.Path, StringComparison.OrdinalIgnoreCase) || e.Equals(plain, StringComparison.OrdinalIgnoreCase)
                || plain.StartsWith(e + ".", StringComparison.OrdinalIgnoreCase) || d.Path.StartsWith(e + ".", StringComparison.OrdinalIgnoreCase) || d.Path.StartsWith(e + "[", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal void Judge()
    {
        if (Expect == null) return;
        if (Diff.DifferentTypes) { Failed = true; return; }
        foreach (var d in Diff.Values) if (!Allowed(d, Expect)) Unexpected.Add(d);
        Failed = Unexpected.Count > 0;
    }

    protected override void Fill(JsonObject json)
    {
        json["a"] = A; json["b"] = B; json["pluginA"] = PluginA; json["pluginB"] = PluginB;
        json["comparison"] = Diff.ToJson();
        if (Expect != null)
        {
            json["expect"] = new JsonArray(Expect.Select(e => (JsonNode)e).ToArray());
            json["unexpected"] = new JsonArray(Unexpected.Select(d => (JsonNode)d.ToJson()).ToArray());
            json["unexpectedFields"] = new JsonArray(Unexpected.Select(d => d.Top).Distinct().Select(e => (JsonNode)e).ToArray());
            if (UnknownExpect.Count > 0) json["expectNamesNoField"] = new JsonArray(UnknownExpect.Select(e => (JsonNode)e).ToArray());
        }
        if (Children.Count + ChildrenNotCompared > 0)
            json["nestedRecords"] = new JsonObject
            {
                ["compared"] = Children.Count, ["differ"] = Children.Count(c => c.Diff is { Same: false }), ["notCompared"] = ChildrenNotCompared,
                ["records"] = new JsonArray(Children.Where(c => c.Diff is not { Same: true }).Select(c => (JsonNode)new JsonObject { ["record"] = c.Label, ["signature"] = c.Signature, ["problem"] = c.Problem, ["comparison"] = c.Diff?.ToJson() }).ToArray()),
            };
    }

    protected override void Render(Body body, TextOptions o)
    {
        body.RowNoun = "lines";
        string where = PluginA.Equals(PluginB, StringComparison.OrdinalIgnoreCase) ? $"both in {PluginA}" : $"{PluginA} and {PluginB}";
        body.Head.Add($"a: {A} in {PluginA}");
        body.Head.Add($"b: {B} in {PluginB}");
        if (Diff.DifferentTypes)
            body.Head.Add($"They are records of different types: a is a {Diff.SignatureA} {Diff.TypeA}, b is a {Diff.SignatureB} {Diff.TypeB}. Records of different types aren't compared field by field.");
        else if (Diff.Same)
            body.Head.Add($"No value differs ({Diff.SignatureA} {Diff.TypeA}, {where})." + (Diff.RewrittenFields.Count > 0 ? $" The same value, written another way: {string.Join(", ", Diff.RewrittenFields)}." : "")
                + (Diff.NotValues.Any(d => d.Class == DiffClass.Header) ? $" The record header's own numbers differ{(o.Detail ? ":" : "; detail shows them.")}" : ""));
        else
            body.Head.Add($"{Plural(Diff.TopFields.Count, "field")} differ{(Diff.TopFields.Count == 1 ? "s" : "")} ({Plural(Diff.Values.Count(), "difference")}; {Diff.SignatureA} {Diff.TypeA})" +
                (Diff.RewrittenFields.Count > 0 ? $", and {Plural(Diff.RewrittenFields.Count, "more field")} hold{(Diff.RewrittenFields.Count == 1 ? "s" : "")} the same value written another way" : "") + ", a → b:");
        if (!Diff.DifferentTypes) body.Rows.AddRange(FieldLines(Diff, o));

        int kids = Diff.ChildrenOnlyA.Count + Diff.ChildrenOnlyB.Count + Diff.ChildrenInBoth.Count;
        if (kids > 0)
        {
            int differ = Children.Count(c => c.Diff is { Same: false });
            body.Rows.Add($"Nested records (records of their own, not fields): {N(Diff.ChildrenOnlyA.Count)} only under a, {N(Diff.ChildrenOnlyB.Count)} only under b, {N(Diff.ChildrenInBoth.Count)} under both" +
                (Children.Count > 0 ? $"; of {N(Children.Count)} compared, {N(differ)} differ" : "") + (ChildrenNotCompared > 0 ? $"; {N(ChildrenNotCompared)} not compared (over the limit)" : "") + ".");
            foreach (var c in Children.Where(c => c.Diff is not { Same: true }))
            {
                body.Rows.Add($"  {c.Label} {c.Signature}: " + (c.Problem ?? (o.Detail ? "" : FieldNames(c.Diff!))));
                if (o.Detail && c.Diff != null) body.Rows.AddRange(FieldLines(c.Diff, o, "      "));
            }
        }
        if (Expect != null)
        {
            if (!Failed) body.Rows.Add($"Expect: PASS. Every difference is in the fields allowed to differ ({string.Join(", ", Expect)}).");
            else if (Diff.DifferentTypes) body.Rows.Add("Expect: FAIL. The two records are of different types.");
            else body.Rows.Add($"Expect: FAIL. Unexpected differences in: {string.Join(", ", Unexpected.Select(d => d.Top).Distinct())}. Allowed: {string.Join(", ", Expect)}.");
            foreach (string e in UnknownExpect)
                body.Rows.Add($"  {e}: not a field of {Diff.TypeA}. " + (NearExpect.TryGetValue(e, out var near) && near.Count > 0
                    ? char.ToUpperInvariant(SFSearch.SchemaGen.NearNames.Lead[0]) + SFSearch.SchemaGen.NearNames.Fix(near)[1..] + "."
                    : $"Check the spelling: describe {Diff.TypeA} lists its fields."));
        }
    }
}

public sealed partial class Comparer
{
    /// <summary>Two records.</summary>
    /// <param name="pluginA">A plugin's name in Data, or a file's path.</param>
    /// <param name="recordA">A FormKey, a local ID or an EditorID.</param>
    /// <param name="recordB">The record in the second plugin. Null: the same FormKey (or the same EditorID) as the first.</param>
    /// <param name="expect">The fields allowed to differ. Null: nothing is judged.</param>
    /// <param name="maxChildren">The most nested records (those under both) that are compared as records of their own.</param>
    public RecordsAnswer Records(string pluginA, string recordA, string pluginB, string? recordB = null, IEnumerable<string>? expect = null, int maxChildren = 500)
    {
        Index.Refresh();
        var a = Session.Side(pluginA); var b = Session.Side(pluginB);
        return Run(new[] { a, b }, () =>
        {
            var ra = Session.FindIn(a, recordA);
            var infoA = a.Index.Describe(ra);
            // With no second name: the same record (its FormKey), else the same name
            var rb = recordB != null ? Session.FindIn(b, recordB)
                : CompareSession.Find(b.Index, b.Name, infoA.FormKey) ?? Session.FindIn(b, recordA);
            var va = Session.View(a.Engine, ra); var vb = Session.View(b.Engine, rb);
            var label = Session.Labeller(a, b);
            var options = Options(label);
            var answer = new RecordsAnswer
            {
                A = va.Label, B = vb.Label, PluginA = a.Shown, PluginB = b.Shown, Diff = Diff(va, vb, options),
                Expect = expect?.Select(e => e.Trim()).Where(e => e.Length > 0).ToList(),
            };
            if (answer.Expect != null)
                foreach (var e in answer.Expect)
                    if (Session.Schema.Model.Resolve(va.Type, e, ignoreCase: true).Found.Count == 0
                        && !Session.Schema.Props(va.Type).Any(p => string.Equals(p.Main.Readable, e, StringComparison.OrdinalIgnoreCase))
                        && !answer.Diff.All.Any(d => RecordsAnswer.Allowed(d, new[] { e })))
                    {
                        answer.UnknownExpect.Add(e);
                        answer.NearExpect[e] = SFSearch.SchemaGen.NearNames.Fields(Session.Schema.Db, new[] { va.Type }, e);
                    }
            answer.Judge();
            // Records nested in both are records of their own
            foreach (var key in answer.Diff.ChildrenInBoth)
            {
                if (answer.Children.Count >= maxChildren) { answer.ChildrenNotCompared++; continue; }
                var ca = CompareSession.Find(a.Index, a.Name, key); var cb = CompareSession.Find(b.Index, b.Name, key);
                if (ca == null || cb == null) continue;
                var info = a.Index.Describe(ca.Value);
                try { answer.Children.Add(new ChildDiff { Label = info.Label, Signature = info.Signature, Diff = Diff(Session.View(a.Engine, ca.Value), Session.View(b.Engine, cb.Value), options) }); }
                catch (CompareException e) { answer.Children.Add(new ChildDiff { Label = info.Label, Signature = info.Signature, Problem = e.Message }); }
            }
            answer.Read.Add("2 records" + (answer.Children.Count > 0 ? $" and {answer.Children.Count} nested pairs" : ""));
            return answer;
        });
    }
}
