// plugins: two versions of one mod (two files; one may be a .bak or sit outside Data): records added, removed and
// changed, paired by FormKey. Or a patch against the plugin it masters: each record of the patch against the record
// with the same FormKey.
//  - EditorID pairing only when asked. EditorIDs a file holds more than once are listed and counted, never
//    collapsed: those records are paired by FormKey (trap 8).
//  - Two files whose FormKeys can't pair (different names, and neither masters the other) stop with an error that
//    names the plugin name expected; as="<name>" reads a file under that name on purpose (trap 12).
using System.Text.Json.Nodes;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Compare;

public enum PairClass { Added, Removed, Changed, AnotherType, NotRead }

public sealed class PairRow
{
    public required PairClass Class { get; init; }
    public required string Signature { get; init; }
    /// <summary>The record in the first file, EditorID (FormID:plugin). Null for an added record.</summary>
    public string? A { get; init; }
    public string? B { get; init; }
    /// <summary>True when the record belongs to another plugin (an override).</summary>
    public bool IsOverride { get; init; }
    /// <summary>Paired by EditorID with different FormKeys: the record moved.</summary>
    public bool Moved { get; init; }
    public RecordDiff? Diff { get; init; }
    public string? Problem { get; init; }
    public string Label => B ?? A ?? "";

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["class"] = Class switch { PairClass.Added => "added", PairClass.Removed => "removed", PairClass.Changed => "changed", PairClass.AnotherType => "another_type", _ => "not_read" },
            ["signature"] = Signature, ["a"] = A, ["b"] = B, ["override"] = IsOverride,
        };
        if (Moved) o["moved"] = true;
        if (Problem != null) o["problem"] = Problem;
        if (Diff != null) o["changes"] = Diff.ToJson();
        return o;
    }
}

/// <summary>An EditorID that one of the two files holds more than once among its own records of one type.</summary>
public sealed record RepeatedEditorId(string Signature, string EditorId, IReadOnlyList<string> InA, IReadOnlyList<string> InB);

public sealed class PluginsAnswer : CompareAnswer
{
    public override string Kind => "plugins";
    public required string A { get; init; }
    public required string B { get; init; }
    /// <summary>"versions": two versions of one plugin. "patch": one file masters the other.</summary>
    public required string Mode { get; init; }
    /// <summary>In patch mode: the patch and the plugin it masters.</summary>
    public string? Patch { get; init; }
    public string? Mastered { get; init; }
    public required MatchBy Match { get; init; }
    public List<PairRow> Rows { get; } = new();
    public int Unchanged { get; set; }
    /// <summary>Of the unchanged: pairs with the same values where a field is written another way (a text every language
    /// shows the same, held another way; a data block written shorter or longer). Listed, not counted as changed.</summary>
    public List<PairRow> SameValue { get; } = new();
    /// <summary>Of the unchanged: paired by EditorID under different FormKeys.</summary>
    public int MovedOnly { get; set; }
    /// <summary>Of the unchanged: told equal from the file's bytes, without reading the record.</summary>
    public int EqualBytes { get; set; }
    /// <summary>In patch mode: records of the plugin that the patch doesn't hold (they aren't removals), and
    /// records of the patch that aren't the plugin's (its own, and overrides of other masters).</summary>
    public int NotInPatch { get; set; }
    public int NotOfPlugin { get; set; }
    public int RecordsA { get; set; }
    public int RecordsB { get; set; }
    public List<RepeatedEditorId> Repeated { get; } = new();
    public int RepeatedRecords { get; set; }

    public int Added => Rows.Count(r => r.Class == PairClass.Added);
    public int Removed => Rows.Count(r => r.Class == PairClass.Removed);
    public int Changed => Rows.Count(r => r.Class is PairClass.Changed or PairClass.AnotherType);

    protected override void Fill(JsonObject json)
    {
        json["a"] = A; json["b"] = B; json["mode"] = Mode; json["match"] = Match == MatchBy.FormKey ? "formkey" : "editorid";
        if (Patch != null) { json["patch"] = Patch; json["mastered"] = Mastered; }
        json["counts"] = new JsonObject
        {
            ["recordsA"] = RecordsA, ["recordsB"] = RecordsB, ["added"] = Added, ["removed"] = Removed, ["changed"] = Changed, ["unchanged"] = Unchanged,
            ["movedOnly"] = MovedOnly, ["notRead"] = Rows.Count(r => r.Class == PairClass.NotRead), ["notInPatch"] = NotInPatch, ["notOfPlugin"] = NotOfPlugin,
            ["repeatedEditorIds"] = Repeated.Count, ["recordsWithRepeatedEditorIds"] = RepeatedRecords,
        };
        if (Repeated.Count > 0)
            json["repeatedEditorIds"] = new JsonArray(Repeated.Select(r => (JsonNode)new JsonObject
            {
                ["signature"] = r.Signature, ["editorId"] = r.EditorId,
                ["inA"] = new JsonArray(r.InA.Select(x => (JsonNode)x).ToArray()), ["inB"] = new JsonArray(r.InB.Select(x => (JsonNode)x).ToArray()),
            }).ToArray());
        json["records"] = new JsonArray(Rows.Select(r => (JsonNode)r.ToJson()).ToArray());
        if (SameValue.Count > 0) json["sameValueWrittenAnotherWay"] = new JsonArray(SameValue.Select(r => (JsonNode)new JsonObject { ["signature"] = r.Signature, ["a"] = r.A, ["b"] = r.B, ["fields"] = new JsonArray(r.Diff!.RewrittenFields.Select(f => (JsonNode)f).ToArray()) }).ToArray());
    }

    protected override void Render(Body body, TextOptions o)
    {
        body.RowNoun = "records";
        body.Head.Add($"a: {A} ({N(RecordsA)} records)");
        body.Head.Add($"b: {B} ({N(RecordsB)} records)");
        if (Mode == "patch")
            body.Head.Add($"{Patch} masters {Mastered}, so each record of the patch is held against the record with the same FormKey in {Mastered}. " +
                $"{Plural(NotInPatch, "record")} of {Mastered} that the patch doesn't hold {(NotInPatch == 1 ? "is" : "are")} left out (not removals), and so {(NotOfPlugin == 1 ? "is" : "are")} {Plural(NotOfPlugin, "record")} of the patch that {(NotOfPlugin == 1 ? "isn't" : "aren't")} {Mastered}'s.");
        else
            body.Head.Add($"Two versions of one plugin, records paired by {(Match == MatchBy.FormKey ? "FormKey" : "FormKey for overrides, and by record type and EditorID for the plugin's own records")}.");
        body.Head.Add($"Added: {N(Added)}. Removed: {N(Removed)}. Changed: {N(Changed)}. Unchanged: {N(Unchanged)}" + (MovedOnly > 0 ? $" ({N(MovedOnly)} of them under another FormKey)" : "") +
            (SameValue.Count > 0 ? $" ({N(SameValue.Count)} of them the same values with a field written another way, listed below)" : "") +
            (Rows.Any(r => r.Class == PairClass.NotRead) ? $". Couldn't be read: {N(Rows.Count(r => r.Class == PairClass.NotRead))}" : "") + ".");
        if (Repeated.Count > 0)
        {
            body.Head.Add($"WARNING: {Plural(Repeated.Count, "EditorID")} {(Repeated.Count == 1 ? "is" : "are")} repeated in a file ({N(RepeatedRecords)} records share an EditorID with another record of the same type). They can't be paired by EditorID, so they are paired by FormKey:");
            foreach (var r in Repeated.Take(o.Detail ? int.MaxValue : 20))
                body.Head.Add($"  repeated EditorID {r.EditorId} ({r.Signature}): a has {(r.InA.Count == 0 ? "none" : string.Join(", ", r.InA))}; b has {(r.InB.Count == 0 ? "none" : string.Join(", ", r.InB))}");
            if (!o.Detail && Repeated.Count > 20) body.Head.Add($"  … {N(Repeated.Count - 20)} more repeated EditorIDs (detail lists them)");
        }
        var changed = Rows.Where(r => r.Class == PairClass.Changed).ToList();
        if (changed.Count >= 10)
        {
            body.Head.Add("Changed records by type and field:");
            body.Head.Add("| Records | Type | Fields |");
            body.Head.Add("| ---: | :--- | :--- |");
            var tally = changed.GroupBy(r => (r.Signature, Fields: string.Join(", ", r.Diff!.TopFields.OrderBy(f => f, StringComparer.Ordinal)))).OrderByDescending(g => g.Count()).ToList();
            foreach (var g in tally.Take(o.Detail ? int.MaxValue : 15)) body.Head.Add($"| {N(g.Count())} | {g.Key.Signature} | {g.Key.Fields} |");
            if (!o.Detail && tally.Count > 15) body.Head.Add($"| … | | {N(tally.Count - 15)} more combinations |");
        }
        foreach (var r in Rows.OrderBy(r => r.Class switch { PairClass.Removed => 0, PairClass.Added => 1, PairClass.AnotherType => 2, PairClass.NotRead => 3, _ => 4 }))
        {
            string what = r.IsOverride ? "override" : "own record";
            switch (r.Class)
            {
                case PairClass.Added: body.Rows.Add($"ADDED    {r.B} {r.Signature} ({what}): only in b"); break;
                case PairClass.Removed: body.Rows.Add($"REMOVED  {r.A} {r.Signature} ({what}): only in a"); break;
                case PairClass.NotRead: body.Rows.Add($"NOT READ {r.Label} {r.Signature}: {r.Problem}"); break;
                case PairClass.AnotherType: body.Rows.Add($"CHANGED  {r.Label}: a {r.Diff!.SignatureA} {r.Diff.TypeA} in a, a {r.Diff.SignatureB} {r.Diff.TypeB} in b"); break;
                default:
                    string head = $"CHANGED  {r.Label} {r.Signature}" + (r.Moved ? $" (was {r.A})" : "");
                    if (!o.Detail) body.Rows.Add($"{head}: {FieldNames(r.Diff!)}");
                    else body.Rows.Add($"{head}, a → b:\n" + string.Join('\n', FieldLines(r.Diff!, o, "      ")));
                    break;
            }
        }
        foreach (var r in SameValue)
        {
            string head = $"SAME     {r.Label} {r.Signature}" + (r.Moved ? $" (was {r.A})" : "");
            if (!o.Detail) body.Rows.Add($"{head}: no value differs; {FieldNames(r.Diff!)}");
            else body.Rows.Add($"{head}, no value differs:\n" + string.Join('\n', FieldLines(r.Diff!, o, "      ")));
        }
    }
}

public sealed partial class Comparer
{
    sealed class Rec(RecordRef r, RecordInfo info, bool own)
    {
        public RecordRef Ref { get; } = r;
        public RecordInfo Info { get; } = info;
        /// <summary>True for the plugin's own record, false for an override.</summary>
        public bool Own { get; } = own;
        /// <summary>The FormKey, plugin names letter case aside.</summary>
        public string Key { get; } = info.FormKey.ToUpperInvariant();
    }

    static List<Rec> RecordsOf(PluginSide side)
    {
        var seg = side.Segment;
        var list = new List<Rec>(seg.Count);
        for (int i = 0; i < seg.FirstUnresolved; i++)
        {
            var r = new RecordRef(side.Plugin.Id, i);
            list.Add(new Rec(r, side.Index.Describe(r), i >= seg.FirstOwn));
        }
        return list;
    }

    /// <summary>Two versions of one mod, or a patch against the plugin it masters.</summary>
    /// <param name="a">The first (older) file: a plugin's name in Data, or a path.</param>
    /// <param name="b">The second (newer) file.</param>
    /// <param name="match">How a plugin's own records are paired. Overrides are always paired by FormKey.</param>
    /// <param name="readAs">Read a file whose name doesn't fit under this plugin name, on purpose.</param>
    public PluginsAnswer Plugins(string a, string b, MatchBy match = MatchBy.FormKey, string? readAs = null)
    {
        Index.Refresh();
        var sa = Session.Side(a); var sb = Session.Side(b);
        if (readAs != null)
        {
            // The name is for the file that neither has it nor masters it
            if (!sa.Name.Equals(readAs, StringComparison.OrdinalIgnoreCase) && !sa.HasMaster(readAs)) sa = Session.Side(a, readAs);
            if (!sb.Name.Equals(readAs, StringComparison.OrdinalIgnoreCase) && !sb.HasMaster(readAs)) sb = Session.Side(b, readAs);
        }
        bool sameName = sa.Name.Equals(sb.Name, StringComparison.OrdinalIgnoreCase);
        bool bIsPatch = !sameName && sb.HasMaster(sa.Name), aIsPatch = !sameName && sa.HasMaster(sb.Name);
        if (!sameName && !bIsPatch && !aIsPatch) throw CannotPair(sa, sb);
        if (string.Equals(Path.GetFullPath(sa.Path), Path.GetFullPath(sb.Path), StringComparison.OrdinalIgnoreCase))
            throw new CompareException($"both names are the same file, {sa.Path}", "name two files");

        return Run(new[] { sa, sb }, () =>
        {
            var answer = new PluginsAnswer
            {
                A = sa.Shown, B = sb.Shown, Mode = sameName ? "versions" : "patch", Match = sameName ? match : MatchBy.FormKey,
                Patch = bIsPatch ? sb.Shown : aIsPatch ? sa.Shown : null, Mastered = bIsPatch ? sa.Name : aIsPatch ? sb.Name : null,
            };
            var recsA = RecordsOf(sa); var recsB = RecordsOf(sb);
            answer.RecordsA = recsA.Count; answer.RecordsB = recsB.Count;
            var label = Session.Labeller(sa, sb);

            // Pairs. By FormKey first; a FormKey a file holds twice pairs its copies in order.
            var pairs = new List<(Rec? A, Rec? B)>();
            Func<string, string>? canonA = null, canonB = null;
            var byKeyB = new Dictionary<string, Queue<Rec>>(StringComparer.Ordinal);
            foreach (var r in recsB) (byKeyB.TryGetValue(r.Key, out var q) ? q : byKeyB[r.Key] = new Queue<Rec>()).Enqueue(r);

            if (answer.Match == MatchBy.EditorId)
            {
                // The plugin's own records by type and EditorID, where that names one record in each file
                static string Edid(Rec r) => r.Info.Signature + "|" + (r.Info.EditorId ?? "");
                var groupsA = recsA.Where(r => r.Own && r.Info.EditorId != null).GroupBy(Edid, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                var groupsB = recsB.Where(r => r.Own && r.Info.EditorId != null).GroupBy(Edid, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                var repeated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in groupsA.Keys.Union(groupsB.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    var ga = groupsA.GetValueOrDefault(key); var gb = groupsB.GetValueOrDefault(key);
                    if ((ga?.Count ?? 0) <= 1 && (gb?.Count ?? 0) <= 1) continue;
                    repeated.Add(key);
                    var first = (ga ?? gb!)[0];
                    answer.Repeated.Add(new RepeatedEditorId(first.Info.Signature, first.Info.EditorId!, (ga ?? new()).Select(r => r.Info.FormKey).ToList(), (gb ?? new()).Select(r => r.Info.FormKey).ToList()));
                    answer.RepeatedRecords += (ga?.Count > 1 ? ga.Count : 0) + (gb?.Count > 1 ? gb.Count : 0);
                }
                var usedB = new HashSet<Rec>();
                var byEdidB = groupsB.Where(g => !repeated.Contains(g.Key)).ToDictionary(g => g.Key, g => g.Value[0], StringComparer.OrdinalIgnoreCase);
                var later = new List<Rec>();
                var byEditorId = new List<(Rec A, Rec B)>();
                foreach (var r in recsA)
                {
                    if (r.Own && r.Info.EditorId != null && !repeated.Contains(Edid(r)) && byEdidB.TryGetValue(Edid(r), out var other)) { pairs.Add((r, other)); usedB.Add(other); byEditorId.Add((r, other)); }
                    else later.Add(r);
                }
                // The rest by FormKey: overrides, records with no EditorID, records with a repeated one, and renamed records
                foreach (var r in later)
                {
                    Rec? other = null;
                    if (byKeyB.TryGetValue(r.Key, out var q))
                        while (q.Count > 0 && other == null) { var c = q.Dequeue(); if (!usedB.Contains(c)) other = c; }
                    if (other != null) usedB.Add(other);
                    pairs.Add((r, other));
                }
                foreach (var r in recsB) if (!usedB.Contains(r)) pairs.Add((null, r));
                // A link to a record that was paired by EditorID is compared as its partner in the other file
                canonA = PartnerKeys(byEditorId);
            }
            else
            {
                foreach (var r in recsA) pairs.Add((r, byKeyB.TryGetValue(r.Key, out var q) && q.Count > 0 ? q.Dequeue() : null));
                foreach (var q in byKeyB.Values) foreach (var r in q) pairs.Add((null, r));
            }

            var options = Options(label, canonA, canonB);
            bool bytesTell = TrustEqualBytes && sameName && CompareSession.SameFormIdSpace(sa, sb);
            int read = 0;
            foreach (var (ra, rb) in pairs)
            {
                if (ra == null || rb == null)
                {
                    var only = (ra ?? rb)!;
                    if (!sameName)
                    {
                        // A patch and its plugin: what one doesn't hold isn't an addition or a removal
                        bool inPatch = ra == null ? bIsPatch : aIsPatch;
                        if (inPatch) answer.NotOfPlugin++; else answer.NotInPatch++;
                        continue;
                    }
                    answer.Rows.Add(new PairRow { Class = ra == null ? PairClass.Added : PairClass.Removed, Signature = only.Info.Signature, A = ra?.Info.Label, B = rb?.Info.Label, IsOverride = !only.Own });
                    continue;
                }
                bool moved = ra.Key != rb.Key;
                if (bytesTell && !moved && ra.Info.Size == rb.Info.Size && CompareSession.RawBytes(sa.Engine, ra.Ref).AsSpan().SequenceEqual(CompareSession.RawBytes(sb.Engine, rb.Ref)))
                {
                    answer.Unchanged++; answer.EqualBytes++;
                    continue;
                }
                try
                {
                    var diff = Diff(Session.View(sa.Engine, ra.Ref), Session.View(sb.Engine, rb.Ref), options);
                    read++;
                    if (diff.Same)
                    {
                        answer.Unchanged++; if (moved) answer.MovedOnly++;
                        if (diff.RewrittenFields.Count > 0)
                            answer.SameValue.Add(new PairRow { Class = PairClass.Changed, Signature = rb.Info.Signature, A = ra.Info.Label, B = rb.Info.Label, IsOverride = !rb.Own, Moved = moved, Diff = diff });
                        continue;
                    }
                    answer.Rows.Add(new PairRow
                    {
                        Class = diff.DifferentTypes ? PairClass.AnotherType : PairClass.Changed, Signature = rb.Info.Signature, A = ra.Info.Label, B = rb.Info.Label,
                        IsOverride = !rb.Own, Moved = moved, Diff = diff,
                    });
                }
                catch (CompareException e)
                {
                    answer.Rows.Add(new PairRow { Class = PairClass.NotRead, Signature = rb.Info.Signature, A = ra.Info.Label, B = rb.Info.Label, IsOverride = !rb.Own, Problem = e.Message });
                }
            }
            answer.Read.Add($"{recsA.Count:N0} and {recsB.Count:N0} record headers");
            answer.Read.Add($"{read:N0} pair{(read == 1 ? "" : "s")} of records to compare" + (answer.EqualBytes > 0 ? $" ({answer.EqualBytes:N0} more told equal from the file's bytes)" : ""));
            return answer;
        });
    }

    /// <summary>For EditorID pairing: the FormKey of a record of the first file that was paired by EditorID is
    /// compared as its partner's FormKey in the second file. Then a link to a record that moved compares equal,
    /// and a link to a record that wasn't paired by EditorID is compared as it is.</summary>
    static Func<string, string> PartnerKeys(IEnumerable<(Rec A, Rec B)> pairedByEditorId)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (a, b) in pairedByEditorId) if (a.Key != b.Key) map[a.Key] = b.Key;
        return map.Count == 0 ? key => key : formKey => map.TryGetValue(formKey.ToUpperInvariant(), out var partner) ? partner : formKey;
    }

    /// <summary>Trap 12: two files whose FormKeys can't pair. The error names the plugin name that is expected.</summary>
    CompareException CannotPair(PluginSide a, PluginSide b)
    {
        // Is one of them a master of the other under another name? The same content as that master in Data settles it;
        // else a master's name inside the file's name is a hint.
        (PluginSide File, PluginSide Patch, string Master, string Why)? guess = null;
        foreach (var (file, patch) in new[] { (a, b), (b, a) })
        {
            foreach (var m in patch.Masters)
            {
                var inData = Session.EngineFor(patch, m).Index.Plugin(m);
                if (inData != null && inData.ContentKey == file.Plugin.ContentKey && !inData.ContentKey.IsZero) { guess = (file, patch, inData.Name, $"{file.Name} has the same content as {inData.Name}"); break; }
            }
            if (guess != null) break;
        }
        if (guess == null)
            foreach (var (file, patch) in new[] { (a, b), (b, a) })
            {
                string stemOfFile = Path.GetFileNameWithoutExtension(file.Name);
                var m = patch.Masters.Where(m => stemOfFile.Contains(Path.GetFileNameWithoutExtension(m), StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(m).Contains(stemOfFile, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(m => m.Length).FirstOrDefault();
                if (m != null) { guess = (file, patch, m, $"{file.Name} may be {m} under another name"); break; }
            }
        string what = $"{a.Name} and {b.Name} can't be paired: they have different names and neither masters the other, so no FormKey of one names a record of the other";
        if (guess is { } g)
            return new CompareException($"{what}. {g.Patch.Name} expects its masters under the names {string.Join(", ", g.Patch.Masters)}, and {g.Why}",
                $"give the plugin under the name {g.Master}, or add as=\"{g.Master}\" to read {g.File.Name} under that name on purpose");
        string mastersA = a.Masters.Count == 0 ? "none" : string.Join(", ", a.Masters), mastersB = b.Masters.Count == 0 ? "none" : string.Join(", ", b.Masters);
        return new CompareException($"{what}. {a.Name} masters {mastersA}; {b.Name} masters {mastersB}",
            $"if they are two versions of one plugin, add as=\"<the plugin's name>\" (for example as=\"{b.Name}\"); if one is a patch for the other, add as=\"<the master's name the patch expects>\"");
    }
}
