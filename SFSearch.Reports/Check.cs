// check: is this plugin built right? Modelled on xEdit's "Check for Errors". One PASS, WARN or ERROR line per check,
// then the records each check found.
//  - From the header index alone (every record, world data included): missing masters, masters that load late, a
//    header with both size flags, FormIDs too big for the plugin's size, overrides of another type than their base,
//    overrides of nothing, repeated FormKeys, deleted records, EditorIDs used twice.
//  - From the records (read live as JSON, walked beside the schema): links to records that don't exist, links to a
//    record of a kind the schema doesn't allow there, links to a plugin that isn't a master.
//  - From the diff engine: overrides that change nothing (ITMs).
//  - From the bytes: how the plugin stores its own FormIDs (record headers, and the links the walk found, placed in the
//    record's bytes): the full index, FE (Small) or FD (Medium); a plugin that mixes forms, or uses one its size hasn't.
// World data (cells and what they hold, navmeshes, terrain) is left out of the checks that read records unless asked.
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

using static ReportAnswer;

public sealed class CheckOptions
{
    /// <summary>Read world data too: cells and what they hold (placed references, navmeshes, terrain), the navmesh
    /// info map, surface trees. Off by default: the answer says how many records that leaves out.</summary>
    public bool World { get; init; }
    /// <summary>Called now and then with the number of records read so far (for a progress line).</summary>
    public Action<int, int>? Progress { get; init; }
}

public enum CheckLevel { Error, Warn }

/// <summary>One thing a check found.</summary>
public sealed class CheckRow
{
    /// <summary>The record the problem is in, as EditorID (FormID:plugin). Null for a problem of the plugin itself.</summary>
    public string? Record { get; init; }
    public string? FormKey { get; init; }
    public string? Signature { get; init; }
    public string? EditorId { get; init; }
    /// <summary>For a link: where in the record it sits.</summary>
    public string? Field { get; init; }
    /// <summary>For a link: the record it names (FormKey), and the same labelled.</summary>
    public string? Target { get; init; }
    public string? TargetLabel { get; init; }
    public required string Problem { get; init; }

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (Record != null) o["record"] = Record;
        if (FormKey != null) o["formKey"] = FormKey;
        if (Signature != null) o["signature"] = Signature;
        if (Field != null) o["field"] = Field;
        if (Target != null) { o["target"] = Target; o["targetRecord"] = TargetLabel; }
        o["problem"] = Problem;
        return o;
    }

    public string Line() =>
        Record == null ? $"  {Problem}" : Field == null ? $"  {Record} {Signature}: {Problem}" : Target == null ? $"  {Record} {Signature}: {Field}: {Problem}"
        : $"  {Record} {Signature}: {Field} → {TargetLabel ?? Target}: {Problem}";
}

/// <summary>One check: its line in the table and what it found.</summary>
public sealed class CheckFinding
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public CheckLevel Level { get; init; } = CheckLevel.Error;
    public string What { get; init; } = "";
    public List<CheckRow> Rows { get; } = new();
    /// <summary>Printed under the check's heading when it found something.</summary>
    public string? Note { get; set; }
    /// <summary>Set when the check couldn't be made at all (the plugin's records can't be read): why.</summary>
    public string? NotRun { get; set; }
    /// <summary>PASS, WARN, ERROR, or NOT RUN.</summary>
    public string Result => NotRun != null ? "NOT RUN" : Rows.Count == 0 ? "PASS" : Level == CheckLevel.Error ? "ERROR" : "WARN";
}

/// <summary>How a plugin stores its own FormIDs, by form (<see cref="StoredForm"/>): in its record headers, and in its
/// records' links to its own records (GAPS S39).</summary>
public sealed class OwnFormCounts
{
    /// <summary>Each form as a pattern of the plugin's own numbers: 03xxxxxx, FE001xxx, FD01xxxx.</summary>
    public string[] Patterns { get; init; } = new string[FormIdForms.Forms];
    /// <summary>The form the plugin's header gives (Small: FE, Medium: FD, Full: the full index).</summary>
    public StoredForm Implied { get; init; }
    public string Size { get; init; } = "Full";
    public long[] Headers { get; } = new long[FormIdForms.Forms];
    public long[] Links { get; } = new long[FormIdForms.Forms];
    /// <summary>Own FormIDs written with a number past the plugin's own (read as its own all the same).</summary>
    public long HeadersPast { get; set; }
    public long LinksPast { get; set; }
    /// <summary>Links the walk found to the plugin's own records that no stored FormID in their record names.</summary>
    public long LinksNotFound { get; set; }
    /// <summary>Null when the links were counted; otherwise why not.</summary>
    public string? LinksNotCounted { get; set; }
    /// <summary>Null when the forms were counted at all; otherwise why not.</summary>
    public string? NotCounted { get; set; }
    /// <summary>The form most of the plugin's own FormIDs use (the header's own on a tie, or when there are none).</summary>
    public StoredForm Main { get; set; }
    public int FormsUsed => Enumerable.Range(0, FormIdForms.Forms).Count(f => Headers[f] + Links[f] > 0);

    public JsonObject ToJson()
    {
        JsonObject Counts(long[] c, long past) => new() { ["fullIndex"] = c[0], ["small"] = c[1], ["medium"] = c[2], ["pastOwnNumber"] = past };
        var o = new JsonObject { ["headerGives"] = Patterns[(int)Implied], ["size"] = Size };
        if (NotCounted != null) { o["notCounted"] = NotCounted; return o; }
        o["headers"] = Counts(Headers, HeadersPast);
        var links = Counts(Links, LinksPast);
        links["notFound"] = LinksNotFound;
        if (LinksNotCounted != null) links["notCounted"] = LinksNotCounted;
        o["links"] = links;
        o["mostUsed"] = Patterns[(int)Main];
        return o;
    }

    /// <summary>The answer's line: "Own FormIDs as the file stores them: …".</summary>
    public string Line()
    {
        const string head = "Own FormIDs as the file stores them";
        if (NotCounted != null) return $"{head}: not counted ({NotCounted}).";
        string Part(long[] c) => string.Join(", ", Enumerable.Range(0, FormIdForms.Forms).Where(f => c[f] > 0).Select(f => $"{ReportAnswer.N(c[f])} {Patterns[f]}"));
        long headers = Headers.Sum(), links = Links.Sum();
        string text = $"{head}: ";
        text += headers == 0 ? "no record of its own" : $"{ReportAnswer.Plural(headers, "record header")} ({Part(Headers)}{(HeadersPast > 0 ? $"; {ReportAnswer.N(HeadersPast)} with a number past the plugin's own" : "")})";
        if (LinksNotCounted != null) text += $"; links not counted ({LinksNotCounted})";
        else
        {
            text += links == 0 ? "; no link to its own records" : $"; {ReportAnswer.Plural(links, "link")} to its own records ({Part(Links)}{(LinksPast > 0 ? $"; {ReportAnswer.N(LinksPast)} with a number past the plugin's own" : "")})";
            if (LinksNotFound > 0) text += $"; {ReportAnswer.Plural(LinksNotFound, "more link")} to its own records that the walk found aren't stored as a FormID in their record";
        }
        var others = Enumerable.Range(0, FormIdForms.Forms).Where(f => f != (int)Implied && Headers[f] + Links[f] > 0).Select(f => $"; {Patterns[f]} is {FormIdForms.Name((StoredForm)f)}");
        text += $". Its header ({Size}) gives {Patterns[(int)Implied]}, {FormIdForms.Name(Implied)}{string.Concat(others)}.";
        return text;
    }
}

public sealed class CheckAnswer : ReportAnswer
{
    public override string Kind => "check";
    public required string Plugin { get; init; }
    public required IReadOnlyList<string> Masters { get; init; }
    public string Size { get; init; } = "Full";
    public bool World { get; init; }
    public int Records { get; set; }
    /// <summary>Records read through Mutagen and walked for links.</summary>
    public int RecordsRead { get; set; }
    public int Overrides { get; set; }
    public int OverridesCompared { get; set; }
    /// <summary>Links found that aren't null (the navmesh info map's left out).</summary>
    public long Links { get; set; }
    /// <summary>World records left out of the checks that read records, by record type.</summary>
    public Dictionary<string, int> WorldLeftOut { get; } = new(StringComparer.Ordinal);
    public long WorldBytesLeftOut { get; set; }
    public int BuiltInLinks { get; set; }
    public int NavmeshInfoLinks { get; set; }
    public int NavmeshInfoMaps { get; set; }
    /// <summary>Links in fields the schema can't judge (it doesn't know the field, or the target name).</summary>
    public int LinksNotJudged { get; set; }
    /// <summary>Text fields stored in the plugin's own bytes that the stored-text rule read.</summary>
    public long StoredTexts { get; set; }
    /// <summary>How the plugin stores its own FormIDs (the "FormID forms" rules).</summary>
    public OwnFormCounts? OwnForms { get; set; }
    public List<CheckFinding> Findings { get; } = new();

    public CheckFinding Finding(string id) => Findings.First(f => f.Id == id);
    public int Errors => Findings.Count(f => f.Result == "ERROR");
    public int Warnings => Findings.Count(f => f.Result == "WARN");
    public int NotRun => Findings.Count(f => f.Result == "NOT RUN");
    /// <summary>True when a check with the level ERROR found something.</summary>
    public bool Failed => Errors > 0;

    protected override void Fill(JsonObject json)
    {
        json["plugin"] = Plugin; json["masters"] = new JsonArray(Masters.Select(m => (JsonNode)m).ToArray()); json["size"] = Size; json["world"] = World;
        json["counts"] = new JsonObject
        {
            ["records"] = Records, ["recordsRead"] = RecordsRead, ["links"] = Links, ["overrides"] = Overrides, ["overridesCompared"] = OverridesCompared,
            ["worldLeftOut"] = WorldLeftOut.Values.Sum(), ["builtInLinks"] = BuiltInLinks, ["navmeshInfoLinks"] = NavmeshInfoLinks, ["linksNotJudged"] = LinksNotJudged, ["storedTexts"] = StoredTexts,
            ["errors"] = Errors, ["warnings"] = Warnings, ["notRun"] = NotRun,
        };
        if (OwnForms != null) json["ownFormIds"] = OwnForms.ToJson();
        if (WorldLeftOut.Count > 0) json["worldLeftOut"] = new JsonObject(WorldLeftOut.OrderByDescending(k => k.Value).Select(k => new KeyValuePair<string, JsonNode?>(k.Key, k.Value)));
        json["checks"] = new JsonArray(Findings.Select(f =>
        {
            var o = new JsonObject { ["id"] = f.Id, ["result"] = f.Result, ["check"] = f.Name, ["found"] = f.Rows.Count, ["what"] = f.What };
            if (f.NotRun != null) o["notRun"] = f.NotRun;
            if (f.Note != null && f.Rows.Count > 0) o["note"] = f.Note;
            o["rows"] = new JsonArray(f.Rows.Select(r => (JsonNode)r.ToJson()).ToArray());
            return (JsonNode)o;
        }).ToArray());
    }

    /// <summary>Compact mode: the most rows printed for one check.</summary>
    public const int CompactRows = 25;

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "lines";
        int left = WorldLeftOut.Values.Sum();
        body.Head.Add($"Check of {Plugin} ({Size}; masters: {(Masters.Count == 0 ? "none" : string.Join(", ", Masters))}):");
        body.Head.Add($"{Plural(Records, "record")}; {N(RecordsRead)} read and {Plural(Links, "link")} checked" + (left > 0 ? $"; {Plural(left, "world record")} left out of the link and ITM checks" : "") +
            $"; {N(OverridesCompared)} of {Plural(Overrides, "override")} compared with their master.");
        if (OwnForms != null) body.Head.Add(OwnForms.Line());
        body.Head.Add("| Result | Check | Found | What it means |");
        body.Head.Add("| :--- | :--- | ---: | :--- |");
        foreach (var f in Findings) body.Head.Add($"| {f.Result} | {f.Name} | {(f.NotRun != null ? "" : N(f.Rows.Count))} | {Cell(f.NotRun ?? f.What)} |");
        body.Head.Add($"{Plugin}: {Plural(Errors, "check")} with errors, {N(Warnings)} with warnings" + (NotRun > 0 ? $", {N(NotRun)} not run" : "") + ".");
        foreach (var f in Findings.Where(f => f.Rows.Count > 0))
        {
            body.Rows.Add($"{f.Result}: {f.Name} ({N(f.Rows.Count)}){(f.Note == null ? "" : "\n  " + f.Note)}");
            int show = o.Detail ? f.Rows.Count : Math.Min(f.Rows.Count, CompactRows);
            for (int i = 0; i < show; i++) body.Rows.Add(f.Rows[i].Line());
            if (show < f.Rows.Count) body.Rows.Add($"  … {N(f.Rows.Count - show)} more (detail lists every one)");
        }
        if (body.Rows.Count == 0)
            body.Empty = NotRun > 0 ? "Nothing more can be listed: the checks that read records weren't run." : "No check found anything.";
    }
}

public sealed class CheckReport
{
    readonly ReportSession _s;
    readonly LinkWalker _walker;

    public CheckReport(ReportSession session) { _s = session; _walker = new LinkWalker(session.Schema); }

    /// <summary>What reading one world record costs, for the estimate printed when they are left out. Measured on
    /// Starfield.esm: 3,394,276 world records took 240 s more (M11a note).</summary>
    public const double WorldSecondsPerRecord = 0.00007;

    sealed class Target
    {
        public bool Foreign, Exists, BuiltIn, Own;
        public uint Id;
        public string Signature = "", Owner = "";
        /// <summary>The types of the other records with this FormKey, when the plugin holds it more than once.</summary>
        public List<string>? OtherSignatures;
    }

    /// <param name="plugin">A plugin's name in Data, or a file's path.</param>
    public CheckAnswer Check(string plugin, CheckOptions? options = null)
    {
        options ??= new CheckOptions();
        return _s.Run(scope =>
        {
            var side = _s.Side(plugin);
            var p = side.Plugin;
            if (p.Header == null || p.Segment == null)
                throw new ReportException($"{side.Shown} can't be read: {p.Problems.ReadError ?? "it has no header index"}", "give a plugin file");
            var seg = p.Segment; var header = p.Header;
            var answer = new CheckAnswer { Plugin = side.Shown, Masters = header.Masters.ToList(), Size = header.Style.ToString(), World = options.World, Records = seg.Count };
            ReportSession.AddSource(answer, side);
            var label = _s.Compare.Labeller(side);
            // Why the plugin's records can't be read, when they can't: a master is missing, or Mutagen refuses the file
            string? unreadable = p.Problems.MissingMasters.Count > 0
                ? $"{p.Name}'s records can't be read: its master {string.Join(", ", p.Problems.MissingMasters)} isn't there, so FormIDs can't be told apart"
                : null;
            if (unreadable == null)
                try { scope.Touch(side.Engine, side.Name); }
                catch (ReportException e) { unreadable = e.Message.TrimEnd('.'); }
            bool readable = unreadable == null;

            // ---- the plugin itself: masters, size
            var fMasters = Add(answer, "masters-present", "Masters present", CheckLevel.Error, "every master's file is there; without one, FormIDs and links can't be told apart");
            foreach (var m in p.Problems.MissingMasters) fMasters.Rows.Add(new CheckRow { Problem = $"{m} isn't there" });
            var fOrder = Add(answer, "masters-order", "Masters load before the plugin", CheckLevel.Error, "a master that loads later, or not at all, leaves the plugin's overrides and links without their records");
            foreach (var m in p.Problems.MastersLoadedAfter) fOrder.Rows.Add(new CheckRow { Problem = $"{m} loads after {p.Name}" });
            foreach (var m in p.Problems.MastersNotLoaded) fOrder.Rows.Add(new CheckRow { Problem = $"{m} is in Data but isn't loaded, while {p.Name} is" });
            var fFlags = Add(answer, "size-flags", "The header has one size", CheckLevel.Error, "Small (0x100) and Medium (0x400) can't both be set");
            if (p.Problems.BothSizeFlags) fFlags.Rows.Add(new CheckRow { Problem = $"the header's flags are 0x{header.Flags:X}: both Small and Medium; read here as Small" });
            uint limit = header.Style switch { MasterStyle.Small => 0xFFF, MasterStyle.Medium => 0xFFFF, _ => 0xFFFFFF };
            var fRoom = Add(answer, "formid-size", "FormIDs fit the plugin's size", CheckLevel.Error, "Small: up to 0xFFF, Medium: up to 0xFFFF");
            var fPrefix = Add(answer, "formid-master", "FormIDs name the plugin or one of its masters", CheckLevel.Error, "a FormID whose master number is past the plugin's masters reads as the plugin's own record: the plugin was written for another list of masters");
            var fForms = Add(answer, "formid-forms", "Own FormIDs are stored one way", CheckLevel.Warn, "a plugin names its own records by its full index (03000866) or by its size's form (FE001866 Small, FD010866 Medium); the game reads both alike, but a file that mixes them was written by two tools, and a tool that looks for one form misses the other");
            fForms.Note = "The rows are the records that store an own FormID in another form than most of the plugin's. The Creation Kit writes a Small or Medium plugin's own FormIDs as its full index, xEdit and Mutagen in the size's form (FE, FD); the game reads both as the same record. Mutagen 0.55.0-fixes.14 and later writes each one back as stored.";
            var fFormSize = Add(answer, "formid-form-size", "Own FormIDs use a form the plugin's size has", CheckLevel.Warn, "FE (the Small form) belongs to a plugin with the Small flag, FD (the Medium form) to one with the Medium flag; elsewhere Mutagen reads it as the plugin's own record only because its number is past the plugin's Small or Medium masters, and how the game reads it isn't known");

            var fMissing = Add(answer, "links-exist", "Links point at records that exist", CheckLevel.Error, "the target is in the plugin or one of its masters");
            var fKind = Add(answer, "links-kind", "Links point at the right kind of record", CheckLevel.Error, "the schema says which record types a field may name (a keyword list holds keywords)");
            var fForeign = Add(answer, "links-masters", "Links stay inside the plugin and its masters", CheckLevel.Error, "a link to a plugin that isn't a master can't be written to the file as that record");
            var fTypes = Add(answer, "override-type", "Overrides keep their base record's type", CheckLevel.Error, "a different type means the plugin was built for an older version of a master");
            var fNothing = Add(answer, "override-nothing", "Overrides have a record to override", CheckLevel.Warn, "the FormKey belongs to a master that has no such record: the game adds it as a new record under the master's name");
            var fRepeat = Add(answer, "formkey-repeat", "No FormKey is used twice", CheckLevel.Error, "two records with one FormKey: the game and Mutagen keep one");
            var fDeleted = Add(answer, "deleted", "No deleted records", CheckLevel.Warn, "a deleted placed reference can crash the game when another mod still uses it; xEdit's \"Undelete and Disable\" is the safe way");
            var fDupe = Add(answer, "editorid-unique", "EditorIDs are unique in the plugin", CheckLevel.Warn, "scripts and the console find records by EditorID");
            var fClash = Add(answer, "editorid-master", "New EditorIDs don't repeat a master's", CheckLevel.Warn, "scripts and the console find records by EditorID");
            var fItm = Add(answer, "itm", "No identical-to-master overrides (ITMs)", CheckLevel.Warn, "an override that changes nothing still wins over other mods' changes");
            fItm.Note = "An override is identical only when every value is: the EditorID, the record flags, and each text in every language. An override that renames a record or gives it an EditorID, drops or adds a language of a text, or holds an empty text where the master has none (or the reverse) changes something and isn't listed. Record headers' own numbers (form version, version control) don't count. A row says when the same value is written another way.";
            var fUnread = Add(answer, "readable", "Every record can be read", CheckLevel.Warn, "a record Mutagen can't read wasn't checked for links or compared");
            var fText = Add(answer, "stored-text", "Stored text reads one way", CheckLevel.Warn, "text stored in the plugin (not in strings files) outside plain ASCII reads by the code page it's read with: Mutagen reads code page 1252, UTF-8 readers read it otherwise");
            fText.Note = "Each row gives the text as Mutagen reads it (code page 1252; record, SQL and find show this), the bytes outside ASCII, and how the text reads as UTF-8, the encoding of Starfield's strings files. " +
                "Which one the game uses for text stored in a plugin isn't known. Plain ASCII reads the same everywhere: write ’ as ', — as -, or move the text to strings files.";

            CheckRow Row(int entry, string problem, string? field = null, string? target = null)
            {
                var info = side.Index.Describe(new RecordRef(p.Id, entry));
                return new CheckRow { Record = info.Label, FormKey = info.FormKey, Signature = info.Signature, EditorId = info.EditorId, Problem = problem, Field = field, Target = target, TargetLabel = target == null ? null : label(target) };
            }

            // ---- from the header index: every record, world data included
            var typeMismatch = new HashSet<int>(p.Problems.TypeMismatches); var ofNothing = new HashSet<int>(p.Problems.OverridesOfNothing);
            foreach (int i in p.Problems.TypeMismatches)
            {
                var e = seg[i];
                string owner = seg.Names[e.Owner];
                var theirs = ReportSession.In(_s.Compare.EngineFor(side, owner), owner, owner, e.LocalId);
                fTypes.Rows.Add(Row(i, theirs is { } t ? $"is a {t.Signature} in {owner}" : $"is of another type in {owner}"));
            }
            foreach (int i in p.Problems.OverridesOfNothing) fNothing.Rows.Add(Row(i, $"{seg.Names[seg[i].Owner]} has no record with this ID"));

            // The FormID as the file writes it, for the records that share a FormKey: one of two such records is often
            // written with a FormID past the plugin's masters (which reads as the plugin's own)
            string RawId(int entry)
            {
                if (!readable) return "";
                try
                {
                    scope.Touch(side.Engine, side.Name);
                    uint raw = side.Engine.Raw(new RecordRef(p.Id, entry)).FormId;
                    return $"; the file writes its FormID as {raw:X8}";
                }
                catch (RecordException) { return ""; }
            }
            var ownByEdid = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var masterSegments = header.Masters.Select(m => _s.Compare.EngineFor(side, m).Index.Plugin(m)).Where(m => m?.Segment != null).Select(m => m!).ToList();
            var hits = new List<int>();
            for (int i = 0; i < seg.Count; i++)
            {
                var e = seg[i];
                if (e.IsUnresolved) continue;
                bool own = e.Owner == seg.SelfOwner;
                if (!own) answer.Overrides++;
                if (i > 0 && seg[i - 1].Key == e.Key)
                {
                    if (i < 2 || seg[i - 2].Key != e.Key) fRepeat.Rows.Add(Row(i - 1, "the first record with this FormKey" + RawId(i - 1)));
                    fRepeat.Rows.Add(Row(i, "repeats a FormKey the plugin already holds" + RawId(i)));
                }
                if ((e.Flags & 0x20) != 0) fDeleted.Rows.Add(Row(i, own ? "deleted new record" : "deleted override of a master's record"));
                if (!own) continue;
                if (e.LocalId > limit) fRoom.Rows.Add(Row(i, $"its ID 0x{e.LocalId:X} is over the {header.Style} limit 0x{limit:X}"));
                if (seg.EditorId(i) is { Length: > 0 } edid)
                {
                    (ownByEdid.TryGetValue(edid, out var l) ? l : ownByEdid[edid] = new()).Add(i);
                    foreach (var m in masterSegments)
                    {
                        hits.Clear();
                        m.Segment!.FindEditorId(edid, hits);
                        if (hits.Count == 0) continue;
                        var also = _s.Compare.EngineFor(side, m.Name).Index.Describe(new RecordRef(m.Id, hits[0]));
                        fClash.Rows.Add(Row(i, $"{m.Name} has {also.Label} {also.Signature} with the same EditorID"));
                        break;
                    }
                }
            }
            foreach (var g in ownByEdid.Values.Where(g => g.Count > 1))
                foreach (int i in g) fDupe.Rows.Add(Row(i, $"{g.Count} new records share this EditorID"));

            // ---- how the plugin stores its own FormIDs: the record headers (every record, world data included)
            var ids = p.FormIds;
            var forms = new OwnFormCounts
            {
                Patterns = Enumerable.Range(0, FormIdForms.Forms).Select(f => FormIdForms.Pattern((StoredForm)f, ids)).ToArray(),
                Implied = FormIdForms.Implied(header.Style), Size = header.Style.ToString(), Main = FormIdForms.Implied(header.Style),
            };
            answer.OwnForms = forms;
            uint[]? headerIds = null;
            if (p.Problems.MissingMasters.Count > 0) forms.NotCounted = "a master is missing, so the plugin's own numbers can't be told";
            else
            {
                var ownEntries = new List<int>(); var offsets = new List<long>();
                for (int i = 0; i < seg.Count; i++)
                    if (!seg[i].IsUnresolved && seg[i].Owner == seg.SelfOwner) { ownEntries.Add(i); offsets.Add(seg[i].Offset); }
                var raws = FormIdForms.HeaderFormIds(p.Path, offsets);
                headerIds = new uint[seg.Count];
                for (int k = 0; k < raws.Length; k++)
                {
                    if (!FormIdForms.IsOwn(raws[k], ids, out _, out bool atOwn)) continue;
                    headerIds[ownEntries[k]] = raws[k];
                    forms.Headers[(int)FormIdForms.FormOf(raws[k])]++;
                    if (!atOwn) forms.HeadersPast++;
                }
            }
            var ownLinks = new List<OwnLinks>();

            // ---- stored text, from the records' bytes (every record, readable through Mutagen or not)
            using (var read = readable ? RecordTexts.Reader(p, Mutagen.Bethesda.Strings.Language.English, null) : null)
                StoredText(side, answer, options, Row, fText, read == null ? null : read.Read);

            // ---- from the records
            if (!readable)
            {
                bool missing = p.Problems.MissingMasters.Count > 0;
                // Overrides can't be told from new records without every master; a file Mutagen refuses still has its header index
                foreach (var f in missing ? new[] { fMissing, fKind, fForeign, fItm, fUnread, fTypes, fNothing, fPrefix, fForms, fFormSize } : new[] { fMissing, fKind, fForeign, fItm, fUnread, fPrefix }) f.NotRun = unreadable;
                forms.LinksNotCounted = "the records can't be read";
                answer.Notes.Add(missing ? $"{unreadable}. {N(seg.Unresolved)} records have a FormID that can't be resolved. Put the master back, then check again."
                    : $"{unreadable}. The checks made from record headers were made; the ones that read records weren't.");
            }
            else ReadRecords(side, answer, options, scope, label, Row, typeMismatch, ofNothing, fMissing, fKind, fForeign, fItm, fUnread, fPrefix, forms, ownLinks);
            if (headerIds != null) FormRows(seg, forms, headerIds, ownLinks, Row, fForms, fFormSize, header.Style);
            if (fPrefix.Rows.Count > 0 && fRepeat.Rows.Count > 0)
                fRepeat.Note = "Some records of the plugin are written with a FormID past the plugin's masters (the check \"FormIDs name the plugin or one of its masters\"). Such a FormID reads as the plugin's own, and can then meet a record the plugin really has under that ID.";
            // Mutagen refuses such a plugin (fixes.17, C204): left out of queries and of the winners, read here for diagnosis
            if (p.Problems.Refused != null)
                fRepeat.Note = (fRepeat.Note == null ? "" : fRepeat.Note + " ") + "Mutagen refuses the plugin for these records (DuplicateFormKeyException, 0.55.0-fixes.17): queries and the winners leave it out, as Mutagen's load order does. This check read it with Mutagen's read for diagnosis (DuplicateFormKeys = KeepLater: of each pair, the later record).";

            // ---- notes
            int left = answer.WorldLeftOut.Values.Sum();
            if (left > 0)
                answer.Notes.Add(WorldData.LeftOut("was left out of the link and ITM checks", left, answer.WorldBytesLeftOut, 0, $"about {2 + left * WorldSecondsPerRecord:F0} s more",
                    string.Join(", ", answer.WorldLeftOut.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {N(k.Value)}")) + (answer.WorldLeftOut.Count > 6 ? ", …" : "")) +
                    " The checks made from record headers covered them.");
            if (answer.BuiltInLinks > 0)
                answer.Notes.Add($"{Plural(answer.BuiltInLinks, "link")} to the engine's built-in forms (Starfield.esm below 0x800, such as 000014, the player) aren't in any file and aren't errors.");
            if (answer.NavmeshInfoMaps > 0)
                answer.Notes.Add($"{Plural(answer.NavmeshInfoLinks, "link")} inside the navmesh info map (NAVI) weren't checked: it keeps entries for deleted navmeshes whose FormIDs were reused (the game's own does too).");
            if (answer.LinksNotJudged > 0)
                answer.Notes.Add($"{Plural(answer.LinksNotJudged, "link")} sit in fields whose allowed record types the schema doesn't give: they were checked for existing, not for their kind.");
            ReportSession.AddProblemNotes(answer, p, missingMasters: false);
            // A missing master is said once, by the check itself
            if (p.Problems.MissingMasters.Count > 0) answer.Notes.RemoveAll(n => n.StartsWith(p.Name + ": missing master", StringComparison.Ordinal) || n.Contains("can't be resolved without the missing master", StringComparison.Ordinal));
            answer.Read.Add($"{N(answer.RecordsRead)} of {N(answer.Records)} records of {p.Name}");
            if (answer.OverridesCompared > 0) answer.Read.Add($"the masters' versions of {Plural(answer.OverridesCompared, "override")} (to compare them)");
            return answer;
        });
    }

    /// <summary>
    /// The stored-text rule (GAPS S19): every text field stored in the plugin's own bytes (in a localized plugin, the
    /// fields that are never translated) with a byte over 0x7F. Such a text reads by the code page it is read with:
    /// Mutagen reads code page 1252 (RecordNames' encoding, the one record and SQL use), Starfield's strings files are
    /// UTF-8. A row says how it reads each way, which languages Mutagen would read differently, and a byte code page 1252
    /// has no character for. Plain ASCII is never listed. World data is read with World, cells' and worldspaces' own
    /// records always.
    /// </summary>
    void StoredText(PluginSide side, CheckAnswer answer, CheckOptions options, Func<int, string, string?, string?, CheckRow> row, CheckFinding fText, RecordTexts.ReadRecord? read)
    {
        var p = side.Plugin; var seg = side.Segment;
        bool localized = RecordTexts.IsLocalized(p);
        var mask = new bool[seg.Signatures.Count];
        var seen = new bool[seg.Signatures.Count];
        for (int i = 0; i < seg.Count; i++)
        {
            var e = seg[i];
            if (seen[e.Signature]) continue;
            seen[e.Signature] = true;
            mask[e.Signature] = options.World || seg.SignatureOf(e) is "CELL" or "WRLD" || !ReportSession.IsWorld(seg, e);
        }
        // A subrecord that text and other values both fill (a perk's entry point data, a component's DATA: GAPS S32)
        // is offered when it holds a byte over 0x7F anywhere, and told by reading its record through Mutagen
        var walked = RecordTexts.Walk(p, mask, _s.Schema, place => !localized || !place.Translated || place.Shared == RecordTexts.SharedKind.Typed, (place, data) =>
        {
            bool shared = place.Shared == RecordTexts.SharedKind.Typed;
            foreach (byte b in data) { if (b == 0 && !shared) return false; if (b > 0x7F) return true; }
            return false;
        });
        var typed = walked.Found.Where(f => f.Place.Shared == RecordTexts.SharedKind.Typed).ToList();
        var confirmed = typed.Count == 0 || read == null ? null
            : RecordTexts.Confirm(typed, read, Mutagen.Bethesda.Strings.Language.English, t => !(localized && t.Translated) && t.StringId == null);
        answer.StoredTexts = walked.Texts + (confirmed?.Texts.Count ?? 0);
        if (confirmed is { Read: > 0 })
            answer.Read.Add($"{Plural(confirmed.Read, "record")} through Mutagen's reader, whose subrecords that text and other values both fill hold a byte over 0x7F ({confirmed.Ms / 1000:0.0#} s)");
        var rows = new List<(int Entry, int Order, CheckRow Row)>();
        foreach (var f in walked.Found)
        {
            if (f.Place.Shared == RecordTexts.SharedKind.Typed) continue;
            var r = RecordTexts.ReadingsOf(f.Data, f.Place.Translated, RecordTexts.Shipped);
            if (r == null) continue;
            rows.Add((f.Entry, rows.Count, row(f.Entry, StoredTextProblem(r), f.Place.Field, null)));
        }
        foreach (var c in confirmed?.Texts ?? new())
        {
            var r = RecordTexts.ReadingsOf(c.Raw, c.Text.Translated, RecordTexts.Shipped);
            if (r == null) continue;
            rows.Add((c.Found.Entry, rows.Count, row(c.Found.Entry, StoredTextProblem(r), c.Text.Path, null)));
        }
        fText.Rows.AddRange(rows.OrderBy(x => x.Entry).ThenBy(x => x.Order).Select(x => x.Row));
        foreach (int i in walked.NotInflated) fText.Rows.Add(row(i, "a compressed record that couldn't be inflated: its text wasn't read", null, null));
        foreach (int i in confirmed?.NotReadEntries ?? new()) fText.Rows.Add(row(i, "Mutagen couldn't read this record, so its subrecords that text and other values both fill weren't checked", null, null));
        if (typed.Count > 0 && read == null)
            foreach (int i in typed.Select(f => f.Entry).Distinct()) fText.Rows.Add(row(i, "the plugin's records can't be read through Mutagen, so this record's subrecords that text and other values both fill weren't checked", null, null));
    }

    /// <summary>"Samson’s Art" as Mutagen reads it, the bytes outside ASCII, and the UTF-8 reading. A long text is
    /// quoted around its first byte outside ASCII, the same bytes in each reading.</summary>
    static string StoredTextProblem(RecordTexts.Readings r)
    {
        const int Before = 30, After = 50;
        int first = r.High[0].At, from = Math.Max(0, first - Before), to = Math.Min(r.Raw.Length, first + After);
        bool whole = r.Raw.Length <= Before + After;
        if (whole) { from = 0; to = r.Raw.Length; }
        // Code page 1252 is one byte a character, so Mutagen's text is cut at the same places as the bytes
        string Cut(string t, bool bytesAreChars) => bytesAreChars && t.Length == r.Raw.Length ? t[from..to] : t;
        string Quote(string t) => "\"" + (from > 0 ? "…" : "") + t.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ') + (to < r.Raw.Length ? "…" : "") + "\"";
        string Window(Func<byte[], string> read) => Quote(read(r.Raw[from..to]));
        var bytes = r.High.Take(6).Select(h => $"0x{h.Byte:X2} at {h.At}").ToList();
        string high = (r.High.Count == 1 ? "byte " : "bytes ") + string.Join(", ", bytes) + (r.High.Count > 6 ? $" and {r.High.Count - 6} more" : "");
        string text = $"{Quote(Cut(r.Mutagen, true))} as Mutagen reads it (code page 1252; {high})";
        text += r.Utf8 != null
            ? $"; its bytes are UTF-8 too, and read as UTF-8 it is {Window(b => System.Text.Encoding.UTF8.GetString(b))}"
            : $"; its bytes aren't UTF-8 (0x{r.High.Where(h => h.At >= r.Utf8BadAt).Select(h => h.Byte).DefaultIfEmpty(r.High[0].Byte).First():X2} at {Math.Max(0, r.Utf8BadAt)}), so a reader that takes UTF-8 shows {Window(b => System.Text.Encoding.UTF8.GetString(b))}";
        if (r.Not1252.Count > 0) text += $"; {string.Join(", ", r.Not1252.Select(b => $"0x{b:X2}"))} {(r.Not1252.Count == 1 ? "has" : "have")} no character in code page 1252";
        if (r.OtherLanguages.Count > 0)
            text += "; Mutagen reads it differently in " + string.Join(", ", r.OtherLanguages.GroupBy(o => o.Text).Select(g => $"{string.Join(", ", g.Select(o => o.Language))} ({Quote(Cut(g.Key, false))})"));
        return text;
    }

    /// <summary>The Mutagen type of a signature: one name, or the first with a count for a signature that has several (OMOD, GMST).</summary>
    string TypeNames(string signature)
    {
        var types = _s.Schema.TypesOf(signature);
        return types.Count == 0 ? "no Mutagen type" : types.Count == 1 ? types[0] : $"{types[0]} or one of {types.Count - 1} more types";
    }

    /// <summary>A record's links to the plugin's own records, by stored form: how many, and the first of each.</summary>
    sealed class OwnLinks(int entry)
    {
        public readonly int Entry = entry;
        public readonly int[] Count = new int[FormIdForms.Forms];
        public readonly OwnLinkPlace?[] First = new OwnLinkPlace?[FormIdForms.Forms];
    }

    /// <summary>
    /// The "FormID forms" rows (GAPS S39). The plugin's most used form among its own FormIDs (headers and links; the
    /// header's own on a tie) is the plugin's; when it uses more than one, each record storing an own FormID in another
    /// form is a row of formid-forms. Each record storing one in a form its header's size doesn't give (FE outside a
    /// Small plugin, FD outside a Medium one) is a row of formid-form-size.
    /// </summary>
    static void FormRows(Segment seg, OwnFormCounts forms, uint[] headerIds, List<OwnLinks> links, Func<int, string, string?, string?, CheckRow> row,
        CheckFinding fForms, CheckFinding fFormSize, MasterStyle self)
    {
        var total = new long[FormIdForms.Forms];
        for (int f = 0; f < total.Length; f++) total[f] = forms.Headers[f] + forms.Links[f];
        var main = forms.Implied;
        for (int f = 0; f < total.Length; f++) if (total[f] > total[(int)main]) main = (StoredForm)f;
        forms.Main = main;
        bool mixed = forms.FormsUsed > 1;
        string LinkText(OwnLinks l, int f) =>
            $"{Plural(l.Count[f], "link")} to its own records stored as {forms.Patterns[f]} ({l.First[f]!.Value.Subrecord} at {l.First[f]!.Value.Offset}: {l.First[f]!.Value.Raw:X8}{(l.Count[f] > 1 ? ", …" : "")})";
        int next = 0;
        for (int i = 0; i < seg.Count; i++)
        {
            uint head = headerIds[i];
            OwnLinks? l = next < links.Count && links[next].Entry == i ? links[next++] : null;
            if (head == 0 && l == null) continue;
            var headForm = FormIdForms.FormOf(head);
            if (mixed)
            {
                var parts = new List<string>();
                if (head != 0 && headForm != main) parts.Add($"its header stores {head:X8} ({FormIdForms.Name(headForm)})");
                if (l != null)
                    for (int f = 0; f < FormIdForms.Forms; f++)
                        if (f != (int)main && l.Count[f] > 0) parts.Add(LinkText(l, f));
                if (parts.Count > 0) fForms.Rows.Add(row(i, $"{string.Join("; ", parts)}; most of the plugin's own FormIDs are {forms.Patterns[(int)main]}", null, null));
            }
            {
                var parts = new List<string>();
                if (head != 0 && !FormIdForms.Allowed(headForm, self)) parts.Add($"its header stores {head:X8} ({FormIdForms.Name(headForm)})");
                if (l != null)
                    for (int f = 0; f < FormIdForms.Forms; f++)
                        if (l.Count[f] > 0 && !FormIdForms.Allowed((StoredForm)f, self)) parts.Add(LinkText(l, f));
                if (parts.Count > 0) fFormSize.Rows.Add(row(i, $"{string.Join("; ", parts)}; a {self} plugin's header doesn't give that form (its own records are {forms.Patterns[(int)forms.Implied]}{(forms.Implied == StoredForm.FullIndex ? "" : $" or {forms.Patterns[0]}")})", null, null));
            }
        }
    }

    static CheckFinding Add(CheckAnswer answer, string id, string name, CheckLevel level, string what)
    {
        var f = new CheckFinding { Id = id, Name = name, Level = level, What = what };
        answer.Findings.Add(f);
        return f;
    }

    void ReadRecords(PluginSide side, CheckAnswer answer, CheckOptions options, ReadScope scope, Func<string, string> label, Func<int, string, string?, string?, CheckRow> row,
        HashSet<int> typeMismatch, HashSet<int> ofNothing, CheckFinding fMissing, CheckFinding fKind, CheckFinding fForeign, CheckFinding fItm, CheckFinding fUnread, CheckFinding fPrefix,
        OwnFormCounts forms, List<OwnLinks> ownLinks)
    {
        var p = side.Plugin; var seg = side.Segment;
        var known = new HashSet<string>(side.Masters, StringComparer.OrdinalIgnoreCase) { side.Name };
        var targets = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        var diffOptions = new DiffOptions { Label = label, Language = _s.Language };
        var usedMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        scope.Touch(side.Engine, side.Name);
        var file = side.Engine.Sessions.Get(side.Name).File;
        var idBytes = new byte[4];
        int headerDiffers = 0;
        // How many masters of each size the plugin has: a FormID names one of them, or the plugin itself (the next number)
        int full = p.MasterStyles.Count(m => m == MasterStyle.Full), medium = p.MasterStyles.Count(m => m == MasterStyle.Medium), small = p.MasterStyles.Count(m => m == MasterStyle.Small);
        // The links of a record to the plugin's own records, by local ID, for the "FormID forms" rules
        var ids = p.FormIds;
        var wanted = new Dictionary<uint, int>();
        bool countLinks = forms.NotCounted == null;

        Target Resolve(string formKey)
        {
            if (targets.TryGetValue(formKey, out var have)) return have;
            var t = new Target();
            if (HeaderIndex.TryParseFormKey(formKey, out var owner, out var id))
            {
                t.Owner = owner; t.Id = id;
                t.Own = owner.Equals(side.Name, StringComparison.OrdinalIgnoreCase);
                if (!known.Contains(owner)) t.Foreign = true;
                else if (_s.Seen(side, owner, id) is { } hit)
                {
                    t.Exists = true; t.Signature = hit.Signature;
                    // A FormKey the plugin holds twice: the link can mean either record
                    var hs = hit.Plugin.Segment!;
                    for (int j = hit.Ref.Entry + 1; j < hs.Count && hs[j].Key == hit.Entry.Key; j++) (t.OtherSignatures ??= new()).Add(hs.SignatureOf(hs[j]));
                }
                // FormIDs below 0x800 in the game's own master are built into the engine (000014 is the player): never in a file
                else if (owner.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase) && id < 0x800) t.BuiltIn = true;
            }
            return targets[formKey] = t;
        }

        for (int i = 0; i < seg.FirstUnresolved; i++)
        {
            var e = seg[i];
            string sig = seg.SignatureOf(e);
            bool world = ReportSession.IsWorld(seg, e);
            if (world && !options.World)
            {
                answer.WorldLeftOut[sig] = answer.WorldLeftOut.GetValueOrDefault(sig) + 1;
                answer.WorldBytesLeftOut += e.Size + 24;
                continue;
            }
            var hit = new Hit(side.Engine, new RecordRef(p.Id, i));

            // The FormID as the file writes it: a top byte past the plugin's masters reads as the plugin's own record
            if (RandomAccess.Read(file, idBytes, e.Offset + 12) == 4)
            {
                uint raw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(idBytes);
                uint top = raw >> 24;
                bool past = top == 0xFE ? ((raw >> 12) & 0xFFF) > small : top == 0xFD ? ((raw >> 16) & 0xFF) > medium : top > full;
                if (past)
                    fPrefix.Rows.Add(row(i, $"the file writes its FormID as {raw:X8}: {(top == 0xFE ? $"Small master number {(raw >> 12) & 0xFFF}" : top == 0xFD ? $"Medium master number {(raw >> 16) & 0xFF}" : $"the top byte {top:X2}")} names no master of the plugin, so the record reads as the plugin's own", null, null));
            }
            if (options.Progress != null && (answer.RecordsRead & 0x3FFF) == 0) options.Progress(answer.RecordsRead, seg.Count);

            // The navmesh info map: its links aren't checked. They are counted through Mutagen, without making its JSON.
            if (sig == "NAVI")
            {
                answer.NavmeshInfoMaps++;
                bool heavy = side.Engine.AllowHeavy;
                try
                {
                    side.Engine.AllowHeavy = true;
                    var navi = side.Engine.Load(hit.Ref);
                    answer.NavmeshInfoLinks += navi.Record.EnumerateFormLinks().Count(l => !l.FormKey.IsNull);
                    answer.RecordsRead++;
                }
                catch (RecordException x) { fUnread.Rows.Add(row(i, x.Message, null, null)); }
                finally { side.Engine.AllowHeavy = heavy; }
                continue;
            }

            RecordView mine;
            try { mine = _s.Compare.View(side.Engine, hit.Ref); }
            catch (CompareException x) { fUnread.Rows.Add(row(i, x.Message, null, null)); continue; }
            answer.RecordsRead++;

            int at = i;
            wanted.Clear();
            _walker.Walk(mine.Type, mine.Json, link =>
            {
                answer.Links++;
                var t = Resolve(link.FormKey);
                if (t.Own) wanted[t.Id] = wanted.GetValueOrDefault(t.Id) + 1;
                string shown = ReportSession.Canon(link.FormKey);
                if (t.Foreign) { fForeign.Rows.Add(row(at, $"{t.Owner} isn't a master of {side.Name}", link.Path, shown)); return; }
                if (!t.Exists)
                {
                    if (t.BuiltIn) answer.BuiltInLinks++;
                    else fMissing.Rows.Add(row(at, $"no such record in {t.Owner}", link.Path, shown));
                    return;
                }
                if (link.LinkTo == null) { answer.LinksNotJudged++; return; }
                bool? allowed = _s.Links.Allows(link.LinkTo, t.Signature);
                // The plugin holds the target FormKey twice (reported as that): the link is right when either record fits
                if (allowed == false && t.OtherSignatures != null && t.OtherSignatures.Any(o => _s.Links.Allows(link.LinkTo, o) != false)) allowed = true;
                if (allowed == null) answer.LinksNotJudged++;
                else if (allowed == false)
                    fKind.Rows.Add(row(at, $"it is a {t.Signature} ({TypeNames(t.Signature)}), where {_s.Links.Describe(link.LinkTo)} is expected", link.Path, shown));
            });

            // ---- where the links to the plugin's own records are stored, and in which form
            if (countLinks && wanted.Count > 0)
            {
                try
                {
                    // Mutagen says where each FormID of the record is stored, and in which form (GetStoredFormIds, C203)
                    var places = FormIdForms.OwnLinks(side.Engine.Load(hit.Ref), side.Engine.Raw(hit.Ref), out int notFound);
                    forms.LinksNotFound += notFound;
                    if (places.Count > 0)
                    {
                        var mineLinks = new OwnLinks(i);
                        foreach (var pl in places)
                        {
                            int f = (int)pl.Form;
                            mineLinks.Count[f]++; mineLinks.First[f] ??= pl;
                            forms.Links[f]++;
                            if (FormIdForms.IsOwn(pl.Raw, ids, out _, out bool atOwn) && !atOwn) forms.LinksPast++;
                        }
                        ownLinks.Add(mineLinks);
                    }
                }
                catch (RecordException) { forms.LinksNotFound += wanted.Values.Sum(); }
            }

            // ---- overrides that change nothing
            if (e.Owner == seg.SelfOwner || typeMismatch.Contains(i) || ofNothing.Contains(i) || (e.Flags & 0x20) != 0) continue;
            if (i > 0 && seg[i - 1].Key == e.Key) continue; // a second copy of a FormKey: reported as that
            if (_s.Seen(side, seg.Names[e.Owner], e.LocalId, mastersOnly: true) is not { } before) continue;
            try
            {
                scope.Touch(before.Engine, before.Plugin.Name);
                var theirs = _s.Compare.View(before.Engine, before.Ref);
                usedMasters.Add(before.Plugin.Name);
                var diff = _s.Compare.Differ.Diff(theirs.Type, theirs.Json, mine.Type, mine.Json, diffOptions);
                answer.OverridesCompared++;
                if (diff.Same)
                {
                    // The header's own numbers aren't data, but the row names them when they differ: xEdit may not call it an ITM
                    string? header = HeaderNumbers.Differences(side.Engine, hit.Ref, before.Engine, before.Ref);
                    if (header != null) headerDiffers++;
                    fItm.Rows.Add(row(i, HeaderNumbers.ItmText(before.Plugin.Name, diff.RewrittenFields, header), null, null));
                }
            }
            catch (CompareException x) { fUnread.Rows.Add(row(i, $"its version in {before.Plugin.Name} couldn't be read: {x.Message}", null, null)); }
        }
        foreach (var m in usedMasters)
            if (_s.Compare.EngineFor(side, m).Index.Plugin(m) is { } mp) ReportSession.AddSource(answer, mp);
        if (headerDiffers > 0) fItm.Note += " " + HeaderNumbers.ItmExplanation;
    }
}
