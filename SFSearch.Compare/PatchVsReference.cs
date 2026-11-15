// patch_vs_reference: two patches for the same masters. For every record either patch overrides: the base value,
// patch a's value and patch b's value for each field one of them changes, classed as
//   same change   both change it, to the same value
//   only in a     a changes it, b leaves the base's value
//   only in b
//   different     both change it, to different values
// A record both hold is "the same" exactly when a direct comparison of the two finds no difference.
// This answers "how does my patch differ from the reference patch for the same mod".
using System.Text.Json.Nodes;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Compare;

public enum FieldClass { SameChange, OnlyInA, OnlyInB, Different }
public enum PatchRecordClass { Same, Differ, OnlyA, OnlyB, NotRead }

/// <summary>One field (or one entry of a set) that at least one of the two patches changes.</summary>
public sealed class PatchField
{
    public required FieldClass Class { get; set; }
    /// <summary>The difference between the base and patch a, when a changes it.</summary>
    public FieldDiff? A { get; init; }
    public FieldDiff? B { get; init; }
    /// <summary>Set when the classes above couldn't settle the record: a difference between the two overrides themselves (a → b).</summary>
    public FieldDiff? Direct { get; init; }
    public FieldDiff Any => A ?? B ?? Direct!;
    public string Top => Any.Top;
    public string Path => Any.Path;
    public string Shown => Any.Shown;
    public bool IsEntry => Direct == null && Any.Element != null;
    /// <summary>The base's value as text. Null when the base has none there.</summary>
    public string? Base => Direct != null ? "(not settled)" : Any.A;
    /// <summary>Patch a's value: its own, or "(as base)" when it doesn't change the field.</summary>
    public string ValueA => Direct != null ? Direct.A ?? "(not there)" : Value(A);
    public string ValueB => Direct != null ? Direct.B ?? "(not there)" : Value(B);
    static string Value(FieldDiff? d) => d == null ? "(as base)" : d.Change == Change.OnlyA ? "(removed)" : d.Change == Change.Order ? "(another order)" : d.B ?? "(not there)";

    public JsonObject ToJson() => new()
    {
        ["field"] = Top, ["path"] = Path, ["shown"] = Shown, ["readable"] = Any.Readable, ["subrecord"] = Any.Signature,
        ["class"] = Class switch { FieldClass.SameChange => "same_change", FieldClass.OnlyInA => "only_in_a", FieldClass.OnlyInB => "only_in_b", _ => "different" },
        ["base"] = Base, ["a"] = ValueA, ["b"] = ValueB,
        ["entry"] = IsEntry ? (Any.Change == Change.OnlyA ? "removed" : "added") : null,
    };
}

public sealed class PatchRow
{
    public required string FormKey { get; init; }
    public required string Label { get; init; }
    public required string Signature { get; init; }
    public PatchRecordClass Class { get; set; }
    public bool InA { get; init; }
    public bool InB { get; init; }
    /// <summary>The master whose version each patch's override was held against.</summary>
    public string? BaseA { get; set; }
    public string? BaseB { get; set; }
    public List<PatchField> Fields { get; } = new();
    /// <summary>The override changes nothing against its base.</summary>
    public bool ItmInA { get; set; }
    public bool ItmInB { get; set; }
    /// <summary>The two overrides compared with each other, when both patches hold the record.</summary>
    public RecordDiff? Direct { get; set; }
    public string? Problem { get; set; }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["record"] = Label, ["formKey"] = FormKey, ["signature"] = Signature,
            ["class"] = Class switch { PatchRecordClass.Same => "same", PatchRecordClass.Differ => "differ", PatchRecordClass.OnlyA => "only_in_a", PatchRecordClass.OnlyB => "only_in_b", _ => "not_read" },
            ["inA"] = InA, ["inB"] = InB, ["baseA"] = BaseA, ["baseB"] = BaseB,
        };
        if (ItmInA) o["itmInA"] = true;
        if (ItmInB) o["itmInB"] = true;
        if (Problem != null) o["problem"] = Problem;
        o["fields"] = new JsonArray(Fields.Select(f => (JsonNode)f.ToJson()).ToArray());
        return o;
    }
}

/// <summary>The two patches' own (new) records of one type.</summary>
public sealed record NewRecordCount(string Signature, int InA, int InB);

public sealed class PatchVsReferenceAnswer : CompareAnswer
{
    public override string Kind => "patch_vs_reference";
    public required string A { get; init; }
    public required string B { get; init; }
    public required IReadOnlyList<string> MastersA { get; init; }
    public required IReadOnlyList<string> MastersB { get; init; }
    public required MatchBy Match { get; init; }
    public List<PatchRow> Rows { get; } = new();
    public List<NewRecordCount> NewRecords { get; } = new();
    /// <summary>With EditorID pairing: the patches' own records, paired by record type and EditorID.</summary>
    public List<PairRow> NewPairs { get; } = new();
    public int NewSame { get; set; }
    public List<RepeatedEditorId> Repeated { get; } = new();
    /// <summary>Records where the field classes and the direct comparison disagreed (the direct comparison decides).</summary>
    public int Reconciled { get; set; }

    public int Both => Rows.Count(r => r.InA && r.InB);
    public int Count(PatchRecordClass c) => Rows.Count(r => r.Class == c);
    public int Count(FieldClass c) => Rows.Sum(r => r.Fields.Count(f => f.Class == c));

    /// <summary>The summary line.</summary>
    public string Summary =>
        $"{Plural(Rows.Count, "record")} overridden by at least one of the two: {N(Both)} by both ({N(Count(PatchRecordClass.Same))} the same, {N(Count(PatchRecordClass.Differ))} differ), " +
        $"{N(Count(PatchRecordClass.OnlyA))} only in a, {N(Count(PatchRecordClass.OnlyB))} only in b" + (Count(PatchRecordClass.NotRead) > 0 ? $", {N(Count(PatchRecordClass.NotRead))} couldn't be read" : "") + ". " +
        $"Field changes: {N(Count(FieldClass.SameChange))} the same change, {N(Count(FieldClass.OnlyInA))} only in a, {N(Count(FieldClass.OnlyInB))} only in b, {N(Count(FieldClass.Different))} different.";

    protected override void Fill(JsonObject json)
    {
        json["a"] = A; json["b"] = B;
        json["mastersA"] = new JsonArray(MastersA.Select(m => (JsonNode)m).ToArray()); json["mastersB"] = new JsonArray(MastersB.Select(m => (JsonNode)m).ToArray());
        json["summary"] = Summary;
        json["counts"] = new JsonObject
        {
            ["records"] = Rows.Count, ["both"] = Both, ["same"] = Count(PatchRecordClass.Same), ["differ"] = Count(PatchRecordClass.Differ),
            ["onlyInA"] = Count(PatchRecordClass.OnlyA), ["onlyInB"] = Count(PatchRecordClass.OnlyB), ["notRead"] = Count(PatchRecordClass.NotRead),
            ["itmInA"] = Rows.Count(r => r.ItmInA), ["itmInB"] = Rows.Count(r => r.ItmInB),
        };
        json["fieldCounts"] = new JsonObject
        {
            ["sameChange"] = Count(FieldClass.SameChange), ["onlyInA"] = Count(FieldClass.OnlyInA), ["onlyInB"] = Count(FieldClass.OnlyInB), ["different"] = Count(FieldClass.Different),
        };
        json["records"] = new JsonArray(Rows.Select(r => (JsonNode)r.ToJson()).ToArray());
        json["newRecords"] = new JsonArray(NewRecords.Select(n => (JsonNode)new JsonObject { ["signature"] = n.Signature, ["inA"] = n.InA, ["inB"] = n.InB }).ToArray());
        if (Match == MatchBy.EditorId)
        {
            json["newRecordsPaired"] = new JsonObject
            {
                ["same"] = NewSame, ["differ"] = NewPairs.Count(p => p.Class is PairClass.Changed or PairClass.AnotherType),
                ["onlyInA"] = NewPairs.Count(p => p.Class == PairClass.Removed), ["onlyInB"] = NewPairs.Count(p => p.Class == PairClass.Added),
                ["records"] = new JsonArray(NewPairs.Select(p => (JsonNode)p.ToJson()).ToArray()),
            };
            json["repeatedEditorIds"] = new JsonArray(Repeated.Select(r => (JsonNode)new JsonObject
            {
                ["signature"] = r.Signature, ["editorId"] = r.EditorId,
                ["inA"] = new JsonArray(r.InA.Select(x => (JsonNode)x).ToArray()), ["inB"] = new JsonArray(r.InB.Select(x => (JsonNode)x).ToArray()),
            }).ToArray());
        }
    }

    protected override void Render(Body body, TextOptions o)
    {
        body.RowNoun = "records";
        body.Head.Add($"a: {A} (masters {string.Join(", ", MastersA)})");
        body.Head.Add($"b: {B} (masters {string.Join(", ", MastersB)})");
        body.Head.Add("Summary: " + Summary);
        int itmA = Rows.Count(r => r.ItmInA), itmB = Rows.Count(r => r.ItmInB);
        if (itmA + itmB > 0) body.Head.Add($"Overrides that change nothing against their base (ITM): {N(itmA)} in a, {N(itmB)} in b.");
        if (NewRecords.Count > 0)
        {
            int na = NewRecords.Sum(n => n.InA), nb = NewRecords.Sum(n => n.InB);
            string byType = string.Join(", ", NewRecords.Take(o.Detail ? int.MaxValue : 12).Select(n => $"{n.Signature} {n.InA}/{n.InB}")) + (!o.Detail && NewRecords.Count > 12 ? ", …" : "");
            body.Head.Add($"New records of their own (not overrides): {N(na)} in a, {N(nb)} in b (by type, a/b: {byType})." +
                (Match == MatchBy.EditorId
                    ? $" Paired by record type and EditorID: {N(NewSame)} the same, {N(NewPairs.Count(p => p.Class is PairClass.Changed or PairClass.AnotherType))} differ, {N(NewPairs.Count(p => p.Class == PairClass.Removed))} only in a, {N(NewPairs.Count(p => p.Class == PairClass.Added))} only in b."
                    : " They have different FormKeys in the two patches, so they aren't compared; match=\"editorid\" pairs them by record type and EditorID."));
        }
        foreach (var r in Repeated.Take(o.Detail ? int.MaxValue : 20))
            body.Head.Add($"WARNING: repeated EditorID {r.EditorId} ({r.Signature}), not paired: a has {(r.InA.Count == 0 ? "none" : string.Join(", ", r.InA))}; b has {(r.InB.Count == 0 ? "none" : string.Join(", ", r.InB))}");

        int max = o.Detail ? int.MaxValue : o.MaxValueChars;
        static string Cut(string? s, int max) => s == null ? "(not there)" : s.Length <= max ? s : s[..max] + "…";
        string Line(PatchField f)
        {
            var d = f.Any;
            string where = d.Path == d.Top ? "" : " " + (d.Shown.StartsWith(d.Top, StringComparison.Ordinal) ? d.Shown[d.Top.Length..].TrimStart('.') : d.Shown);
            string cls = f.Class switch { FieldClass.SameChange => "same change", FieldClass.OnlyInA => "only in a", FieldClass.OnlyInB => "only in b", _ => "different" };
            if (f.IsEntry) return $"      {Head(d)}: {(d.Change == Change.OnlyA ? "− " + Cut(d.A, max) : "+ " + Cut(d.B, max))}   [{cls}]";
            return $"      {Head(d)}{where}: base {Cut(f.Base, max)} | a {Cut(f.ValueA, max)} | b {Cut(f.ValueB, max)}   [{cls}]";
        }
        void Add(PatchRow r, string word)
        {
            string bases = r.BaseA != null && r.BaseB != null && !r.BaseA.Equals(r.BaseB, StringComparison.OrdinalIgnoreCase) ? $"base {r.BaseA} for a, {r.BaseB} for b" : $"base {r.BaseA ?? r.BaseB ?? "(none)"}";
            var lines = new List<string> { $"{word,-8} {r.Label} {r.Signature} ({bases})" + (r.Problem != null ? ": " + r.Problem : r.Fields.Count == 0 ? ": changes nothing against the base" : ":") };
            var show = o.Detail ? r.Fields : r.Fields.Where(f => f.Class != FieldClass.SameChange || r.Class != PatchRecordClass.Differ).ToList();
            int limit = o.Detail ? int.MaxValue : 12;
            foreach (var f in show.Take(limit)) lines.Add(Line(f));
            if (show.Count > limit) lines.Add($"      … {N(show.Count - limit)} more (detail shows all)");
            int hidden = r.Fields.Count - show.Count;
            if (hidden > 0) lines.Add($"      and {Plural(hidden, "field change")} that {(hidden == 1 ? "is" : "are")} the same in both");
            body.Rows.Add(string.Join('\n', lines));
        }
        foreach (var r in Rows.Where(r => r.Class == PatchRecordClass.Differ)) Add(r, "DIFFER");
        foreach (var r in Rows.Where(r => r.Class == PatchRecordClass.OnlyA)) Add(r, "ONLY A");
        foreach (var r in Rows.Where(r => r.Class == PatchRecordClass.OnlyB)) Add(r, "ONLY B");
        foreach (var r in Rows.Where(r => r.Class == PatchRecordClass.NotRead)) Add(r, "NOT READ");
        var same = Rows.Where(r => r.Class == PatchRecordClass.Same).ToList();
        if (o.Detail) foreach (var r in same) Add(r, "SAME");
        else if (same.Count > 0)
            foreach (var chunk in same.Chunk(40))
                body.Rows.Add($"SAME     ({N(same.Count)} in all; detail shows their fields) " + string.Join("; ", chunk.Select(r => $"{r.Label} {r.Signature}")));
        if (Match == MatchBy.EditorId)
            foreach (var p in NewPairs)
                body.Rows.Add(p.Class switch
                {
                    PairClass.Added => $"NEW, ONLY B  {p.B} {p.Signature}",
                    PairClass.Removed => $"NEW, ONLY A  {p.A} {p.Signature}",
                    PairClass.NotRead => $"NEW, NOT READ  {p.Label} {p.Signature}: {p.Problem}",
                    _ => $"NEW, DIFFER  a {p.A} / b {p.B} {p.Signature}" + (o.Detail ? ", a → b:\n" + string.Join('\n', FieldLines(p.Diff!, o, "      ")) : ": " + FieldNames(p.Diff!)),
                });
    }
}

public sealed partial class Comparer
{
    /// <summary>Two patches for the same masters.</summary>
    /// <param name="a">The patch being checked ("mine"): a plugin's name in Data, or a path.</param>
    /// <param name="b">The reference patch.</param>
    /// <param name="match">EditorId also pairs the two patches' own (new) records by record type and EditorID, and
    /// compares a link to such a record by its EditorID. FormKey (the default) leaves new records uncompared.</param>
    public PatchVsReferenceAnswer PatchVsReference(string a, string b, MatchBy match = MatchBy.FormKey)
    {
        Index.Refresh();
        var sa = Session.Side(a); var sb = Session.Side(b);
        if (string.Equals(Path.GetFullPath(sa.Path), Path.GetFullPath(sb.Path), StringComparison.OrdinalIgnoreCase))
            throw new CompareException($"both names are the same file, {sa.Path}", "name two patches");
        return Run(new[] { sa, sb }, () =>
        {
            var answer = new PatchVsReferenceAnswer { A = sa.Shown, B = sb.Shown, MastersA = sa.Masters.ToList(), MastersB = sb.Masters.ToList(), Match = match };
            var common = sa.Masters.Where(m => sb.HasMaster(m)).ToList();
            if (common.Count == 0)
                throw new CompareException($"{sa.Name} and {sb.Name} have no master in common, so no record can be overridden by both",
                    "name two patches for the same masters; for two versions of one plugin use the kind \"plugins\"");
            var onlyA = sa.Masters.Where(m => !sb.HasMaster(m)).ToList(); var onlyB = sb.Masters.Where(m => !sa.HasMaster(m)).ToList();
            if (onlyA.Count + onlyB.Count > 0)
                answer.Notes.Add($"The two patches don't have the same masters: " + string.Join("; ", new[] { onlyA.Count > 0 ? $"only {sa.Name} masters {string.Join(", ", onlyA)}" : null, onlyB.Count > 0 ? $"only {sb.Name} masters {string.Join(", ", onlyB)}" : null }.Where(x => x != null)) +
                    ". Each override is held against the version before it in its own patch's masters; where those differ, the record's row names both.");

            var recsA = RecordsOf(sa); var recsB = RecordsOf(sb);
            var label = Session.Labeller(sa, sb);
            Func<string, string>? canonA = null, canonB = null;
            var ownA = recsA.Where(r => r.Own).ToList(); var ownB = recsB.Where(r => r.Own).ToList();
            foreach (var sig in ownA.Concat(ownB).Select(r => r.Info.Signature).Distinct().OrderBy(s => s, StringComparer.Ordinal))
                answer.NewRecords.Add(new NewRecordCount(sig, ownA.Count(r => r.Info.Signature == sig), ownB.Count(r => r.Info.Signature == sig)));
            Dictionary<string, List<Rec>>? groupsA = null, groupsB = null;
            var repeated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (match == MatchBy.EditorId)
            {
                static string Edid(Rec r) => r.Info.Signature + "|" + (r.Info.EditorId ?? "");
                groupsA = ownA.Where(r => r.Info.EditorId != null).GroupBy(Edid, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                groupsB = ownB.Where(r => r.Info.EditorId != null).GroupBy(Edid, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (var key in groupsA.Keys.Union(groupsB.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    var ga = groupsA.GetValueOrDefault(key); var gb = groupsB.GetValueOrDefault(key);
                    if ((ga?.Count ?? 0) <= 1 && (gb?.Count ?? 0) <= 1) continue;
                    repeated.Add(key);
                    var first = (ga ?? gb!)[0];
                    answer.Repeated.Add(new RepeatedEditorId(first.Info.Signature, first.Info.EditorId!, (ga ?? new()).Select(r => r.Info.FormKey).ToList(), (gb ?? new()).Select(r => r.Info.FormKey).ToList()));
                }
                // A link to one of a's own records is compared as its partner in b, where the EditorID pairs the two
                canonA = PartnerKeys(groupsA.Where(g => !repeated.Contains(g.Key) && groupsB.ContainsKey(g.Key)).Select(g => (g.Value[0], groupsB[g.Key][0])));
            }

            // Every record either overrides, in a's order, then what only b overrides
            var byKeyB = new Dictionary<string, Rec>(StringComparer.Ordinal);
            foreach (var r in recsB.Where(r => !r.Own)) byKeyB.TryAdd(r.Key, r);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pairs = new List<(Rec? A, Rec? B)>();
            foreach (var r in recsA.Where(r => !r.Own)) if (seen.Add(r.Key)) pairs.Add((r, byKeyB.GetValueOrDefault(r.Key)));
            foreach (var r in recsB.Where(r => !r.Own)) if (seen.Add(r.Key)) pairs.Add((null, r));

            var usedMastersA = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var usedMastersB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ra, rb) in pairs)
            {
                var any = (rb ?? ra)!;
                var row = new PatchRow { FormKey = any.Info.FormKey, Label = any.Info.Label, Signature = any.Info.Signature, InA = ra != null, InB = rb != null };
                answer.Rows.Add(row);
                HeaderIndex.TryParseFormKey(any.Info.FormKey, out var owner, out var localId);
                try
                {
                    RecordDiff? dA = null, dB = null;
                    RecordView? va = null, vb = null;
                    if (ra != null)
                    {
                        va = Session.View(sa.Engine, ra.Ref);
                        if (Before(sa, owner, localId) is { } before) { row.BaseA = before.Plugin; usedMastersA.Add(before.Plugin); dA = Diff(Session.View(before.Engine, before.Ref), va, Options(label, null, canonA)); }
                    }
                    if (rb != null)
                    {
                        vb = Session.View(sb.Engine, rb.Ref);
                        if (Before(sb, owner, localId) is { } before) { row.BaseB = before.Plugin; usedMastersB.Add(before.Plugin); dB = Diff(Session.View(before.Engine, before.Ref), vb, Options(label, null, canonB)); }
                    }
                    if ((ra != null && dA == null) || (rb != null && dB == null)) { row.Class = PatchRecordClass.NotRead; row.Problem = "no master holds a record with this FormKey (an override of nothing)"; continue; }
                    if (dA is { DifferentTypes: true } || dB is { DifferentTypes: true })
                    {
                        row.Class = PatchRecordClass.NotRead;
                        var d = dA is { DifferentTypes: true } ? dA : dB!;
                        row.Problem = $"the override is a {d.SignatureB} {d.TypeB}, its base a {d.SignatureA} {d.TypeA}: a patch built for an older master?";
                        continue;
                    }
                    row.ItmInA = dA is { Same: true }; row.ItmInB = dB is { Same: true };
                    var inA = new Dictionary<string, FieldDiff>(StringComparer.Ordinal); var inB = new Dictionary<string, FieldDiff>(StringComparer.Ordinal);
                    if (dA != null) foreach (var d in dA.Values) inA.TryAdd(d.Identity, d);
                    if (dB != null) foreach (var d in dB.Values) inB.TryAdd(d.Identity, d);
                    foreach (var (id, d) in inA)
                    {
                        if (!inB.TryGetValue(id, out var other)) { row.Fields.Add(new PatchField { Class = FieldClass.OnlyInA, A = d }); continue; }
                        bool same = d.Change == other.Change && string.Equals(d.CanonB, other.CanonB, StringComparison.Ordinal);
                        row.Fields.Add(new PatchField { Class = same ? FieldClass.SameChange : FieldClass.Different, A = d, B = other });
                    }
                    foreach (var (id, d) in inB) if (!inA.ContainsKey(id)) row.Fields.Add(new PatchField { Class = FieldClass.OnlyInB, B = d });
                    if (va == null || vb == null) { row.Class = va != null ? PatchRecordClass.OnlyA : PatchRecordClass.OnlyB; continue; }

                    // Both hold it: the direct comparison of the two decides whether they are the same
                    row.Direct = Diff(va, vb, Options(label, canonA, canonB));
                    bool allSame = row.Fields.All(f => f.Class == FieldClass.SameChange);
                    if (row.Direct.Same)
                    {
                        row.Class = PatchRecordClass.Same;
                        if (!allSame) { answer.Reconciled++; foreach (var f in row.Fields) f.Class = FieldClass.SameChange; }
                    }
                    else
                    {
                        row.Class = PatchRecordClass.Differ;
                        if (allSame)
                        {
                            // The two don't agree though each field change looks the same (different bases, or a list paired another way): show the direct differences
                            answer.Reconciled++;
                            foreach (var d in row.Direct.Values) row.Fields.Add(new PatchField { Class = FieldClass.Different, Direct = d });
                        }
                    }
                }
                catch (CompareException e) { row.Class = PatchRecordClass.NotRead; row.Problem = e.Message; }
            }

            if (match == MatchBy.EditorId)
            {
                var options = Options(label, canonA, canonB);
                foreach (var key in groupsA!.Keys.Union(groupsB!.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    if (repeated.Contains(key)) continue;
                    var ra = groupsA.GetValueOrDefault(key)?[0]; var rb = groupsB.GetValueOrDefault(key)?[0];
                    var any = (rb ?? ra)!;
                    if (ra == null || rb == null) { answer.NewPairs.Add(new PairRow { Class = ra == null ? PairClass.Added : PairClass.Removed, Signature = any.Info.Signature, A = ra?.Info.Label, B = rb?.Info.Label }); continue; }
                    try
                    {
                        var diff = Diff(Session.View(sa.Engine, ra.Ref), Session.View(sb.Engine, rb.Ref), options);
                        if (diff.Same) { answer.NewSame++; continue; }
                        answer.NewPairs.Add(new PairRow { Class = diff.DifferentTypes ? PairClass.AnotherType : PairClass.Changed, Signature = any.Info.Signature, A = ra.Info.Label, B = rb.Info.Label, Diff = diff });
                    }
                    catch (CompareException e) { answer.NewPairs.Add(new PairRow { Class = PairClass.NotRead, Signature = any.Info.Signature, A = ra.Info.Label, B = rb.Info.Label, Problem = e.Message }); }
                }
            }
            if (answer.Reconciled > 0)
                answer.Notes.Add($"For {answer.Reconciled} record(s) the field classes didn't settle whether the two overrides are the same (different bases, or a list paired differently against each base). The direct comparison of the two overrides decided.");
            AddMasterSources(answer, sa, usedMastersA); AddMasterSources(answer, sb, usedMastersB);
            int overA = recsA.Count(r => !r.Own), overB = recsB.Count(r => !r.Own);
            answer.Read.Add($"{overA:N0} override{(overA == 1 ? "" : "s")} of {sa.Name} and {overB:N0} of {sb.Name}, each with its base");
            return answer;
        });
    }
}
