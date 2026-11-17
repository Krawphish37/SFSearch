// changes: what changed in the Data folder since last time? Plugins added, removed and updated (records added and
// removed, a change of size between Full, Medium and Small), and patches at risk because a master changed size
// after they were built.
//  - What is in Data now comes from the header index: each plugin's content key, size, masters and their sizes,
//    its records' FormKeys and signatures, and what the index knows is wrong with it.
//  - What was there before comes from a small history log: a kept file outside the Data folder and outside the
//    repo. Each time the report runs, a plugin whose copy isn't the one the log saw last gets a snapshot: its
//    content key, size, masters with their sizes, record counts per type, and a compact list of its FormKeys and
//    signatures. No record contents, and no EditorIDs.
//  - A copy is told from another by its content key, and a reading of it by its masters' sizes. Nothing is judged
//    by a file date. The time on a snapshot is when the report saw the copy.
//  - Snapshots are taken by a look: a changes call, or the SFSearch service by itself at its start and every 10
//    minutes while it is idle (2026-11-13, fix S11). Each look writes when it was and what made it (last-look.json),
//    so an answer says which look it compares with. A look when nothing changed reads the log and every plugin's
//    content key from the header index, and writes nothing else.
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

using static ReportAnswer;

public sealed class ChangesOptions
{
    /// <summary>One plugin: its history, and what its last update added and removed.</summary>
    public string? Plugin { get; init; }
    /// <summary>Changes seen since this moment. Default: the last 30 days.</summary>
    public DateTime? Since { get; init; }
    /// <summary>Every change since the history began.</summary>
    public bool All { get; init; }
    /// <summary>The time to put on new snapshots. Default: now. Internal (J81): only the tests (sfsreport-tests,
    /// through InternalsVisibleTo) set a clock of their own.</summary>
    internal DateTime? Now { get; init; }
}

/// <summary>One copy of a plugin as the log remembers it.</summary>
public sealed class PluginSnapshot
{
    public int Number { get; init; }
    public required string Plugin { get; init; }
    public DateTime Seen { get; init; }
    /// <summary>The plugin was in Data before and isn't now.</summary>
    public bool Removed { get; init; }
    /// <summary>Made when the history began: the plugin was already there.</summary>
    public bool Initial { get; init; }
    /// <summary>The copy's content key (a hash of the whole file).</summary>
    public string Key { get; init; } = "";
    /// <summary>A localized plugin's string files: the stamp of the files its texts are read from ("none" when it has
    /// none). Null for a plugin that isn't localized, and in lines written before string files were recorded.</summary>
    public string? Strings { get; init; }
    public long Length { get; init; }
    /// <summary>Full, Medium or Small.</summary>
    public string Size { get; init; } = "";
    public List<(string Name, string Size)> Masters { get; init; } = new();
    public int Records { get; init; }
    public int New { get; init; }
    public int Overrides { get; init; }
    public int Unresolved { get; init; }
    public Dictionary<string, int> Counts { get; init; } = new(StringComparer.Ordinal);
    public int TypeMismatches { get; init; }
    public int OverridesOfNothing { get; init; }
    public string? Problem { get; init; }
    /// <summary>The file of the compact record list, inside the history folder. Null when none was kept (a very large plugin, an unreadable one).</summary>
    public string? List { get; init; }

    public string MasterSizes => string.Join(", ", Masters.Select(m => $"{m.Name} {m.Size}"));
    /// <summary>What tells one reading of one copy from another: the content and the masters' names and sizes.</summary>
    public string Identity => Removed ? "removed" : Key + "|" + string.Join("|", Masters.Select(m => m.Name.ToLowerInvariant() + "=" + m.Size)) + (Strings != null ? "|strings=" + Strings : "");
    /// <summary>The identity without the string files: for a line written before they were recorded.</summary>
    public string IdentityWithoutStrings => Removed ? "removed" : Key + "|" + string.Join("|", Masters.Select(m => m.Name.ToLowerInvariant() + "=" + m.Size));

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["n"] = Number, ["plugin"] = Plugin, ["seen"] = Seen.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) };
        if (Removed) { o["removed"] = true; return o; }
        if (Initial) o["initial"] = true;
        o["key"] = Key; o["length"] = Length; o["size"] = Size;
        if (Strings != null) o["strings"] = Strings;
        o["masters"] = new JsonArray(Masters.Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["size"] = m.Size }).ToArray());
        o["records"] = Records; o["new"] = New; o["overrides"] = Overrides;
        if (Unresolved > 0) o["unresolved"] = Unresolved;
        o["counts"] = new JsonObject(Counts.Select(c => new KeyValuePair<string, JsonNode?>(c.Key, c.Value)));
        if (TypeMismatches > 0) o["typeMismatches"] = TypeMismatches;
        if (OverridesOfNothing > 0) o["overridesOfNothing"] = OverridesOfNothing;
        if (Problem != null) o["problem"] = Problem;
        if (List != null) o["list"] = List;
        return o;
    }

    public static PluginSnapshot From(JsonObject o) => new()
    {
        Number = o["n"]?.GetValue<int>() ?? 0, Plugin = o["plugin"]!.GetValue<string>(),
        Seen = DateTime.ParseExact(o["seen"]!.GetValue<string>(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        Removed = o["removed"]?.GetValue<bool>() ?? false, Initial = o["initial"]?.GetValue<bool>() ?? false,
        Key = o["key"]?.GetValue<string>() ?? "", Strings = o["strings"]?.GetValue<string>(), Length = o["length"]?.GetValue<long>() ?? 0, Size = o["size"]?.GetValue<string>() ?? "",
        Masters = (o["masters"] as JsonArray)?.OfType<JsonObject>().Select(m => (m["name"]!.GetValue<string>(), m["size"]!.GetValue<string>())).ToList() ?? new(),
        Records = o["records"]?.GetValue<int>() ?? 0, New = o["new"]?.GetValue<int>() ?? 0, Overrides = o["overrides"]?.GetValue<int>() ?? 0, Unresolved = o["unresolved"]?.GetValue<int>() ?? 0,
        Counts = (o["counts"] as JsonObject)?.ToDictionary(k => k.Key, k => k.Value!.GetValue<int>(), StringComparer.Ordinal) ?? new(StringComparer.Ordinal),
        TypeMismatches = o["typeMismatches"]?.GetValue<int>() ?? 0, OverridesOfNothing = o["overridesOfNothing"]?.GetValue<int>() ?? 0,
        Problem = o["problem"]?.GetValue<string>(), List = o["list"]?.GetValue<string>(),
    };
}

/// <summary>The records one copy of a plugin held: FormKey → signature.</summary>
public sealed class RecordList
{
    public Dictionary<string, string> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>From the header index: every record whose FormID could be resolved.</summary>
    public static RecordList From(Segment seg)
    {
        var list = new RecordList();
        for (int i = 0; i < seg.FirstUnresolved; i++)
        {
            var e = seg[i];
            list.Records[$"{e.LocalId:X6}:{seg.Names[e.Owner]}"] = seg.SignatureOf(e);
        }
        return list;
    }

    /// <summary>The compact form: a table of plugin names, a table of signatures, then 6 bytes a record, compressed.</summary>
    public void Write(string path)
    {
        var names = Records.Keys.Select(k => k[7..]).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var sigs = Records.Values.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var nameAt = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
        var sigAt = sigs.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i, StringComparer.Ordinal);
        string tmp = path + "." + Environment.ProcessId + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            w.Write(1); w.Write(names.Count); foreach (var n in names) w.Write(n);
            w.Write(sigs.Count); foreach (var s in sigs) w.Write(s);
            w.Write(Records.Count);
            foreach (var (key, sig) in Records.OrderBy(r => r.Key[7..], StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key[..6], StringComparer.Ordinal))
            {
                uint id = Convert.ToUInt32(key[..6], 16);
                w.Write((ushort)nameAt[key[7..]]); w.Write((byte)(id >> 16)); w.Write((ushort)id); w.Write((byte)sigAt[sig]);
            }
        }
        File.Move(tmp, path, true);
    }

    public static RecordList Read(string path)
    {
        var list = new RecordList();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        if (r.ReadInt32() != 1) throw new InvalidDataException("not a record list of this version");
        var names = new string[r.ReadInt32()]; for (int i = 0; i < names.Length; i++) names[i] = r.ReadString();
        var sigs = new string[r.ReadInt32()]; for (int i = 0; i < sigs.Length; i++) sigs[i] = r.ReadString();
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            int name = r.ReadUInt16(); uint id = (uint)r.ReadByte() << 16; id |= r.ReadUInt16(); int sig = r.ReadByte();
            list.Records[$"{id:X6}:{names[name]}"] = sigs[sig];
        }
        return list;
    }
}

/// <summary>
/// One look at the Data folder (2026-11-13, fix S11): when it was, what made it, and the snapshots it added. The last
/// is kept as last-look.json in the history folder (the one before is in it too), so a changes answer says which look
/// it compares with: the service's at its start, its idle check, or a changes call.
/// </summary>
public sealed record HistoryLook(DateTime Seen, string By, int Plugins, int Added, int Lines)
{
    public const string FileName = "last-look.json";

    /// <summary>What made the look, in words: "the service's idle check", "a changes call".</summary>
    public string ByWords => By switch
    {
        "service-build" => "the service, at its start",
        "service-check" or "service-sweep" => "the service's idle check",
        "call" => "a changes call",
        "log" => "the last snapshot in the history (no look was written down before)",
        _ => By,
    };

    public JsonObject ToJson() => new()
    {
        ["seen"] = Seen.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), ["by"] = By, ["byWords"] = ByWords,
        ["plugins"] = Plugins, ["added"] = Added, ["lines"] = Lines,
    };

    static HistoryLook? From(JsonNode? o) => o is JsonObject j && j["seen"] is { } seen
        ? new HistoryLook(DateTime.ParseExact(seen.GetValue<string>(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), j["by"]?.GetValue<string>() ?? "",
            j["plugins"]?.GetValue<int>() ?? 0, j["added"]?.GetValue<int>() ?? 0, j["lines"]?.GetValue<int>() ?? 0)
        : null;

    /// <summary>The last look written in this history folder, or null.</summary>
    public static HistoryLook? Read(string folder)
    {
        string path = Path.Combine(folder, FileName);
        try { return File.Exists(path) ? From(JsonNode.Parse(File.ReadAllText(path))?["last"]) : null; }
        catch (Exception e) when (e is IOException or JsonException or FormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Writes this look as the last, with the one before it.</summary>
    public void Write(string folder, HistoryLook? before)
    {
        var o = new JsonObject { ["last"] = ToJson() };
        if (before != null) o["before"] = before.ToJson();
        string path = Path.Combine(folder, FileName), tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }
}

/// <summary>The history log: a folder with one line per snapshot (log.jsonl) and one compact record list per copy.</summary>
public sealed class HistoryLog
{
    /// <summary>
    /// Holds the history folder for one look: the service's and a changes call's can't both add the same snapshot.
    /// Waits up to 30 s for another look to end.
    /// </summary>
    public static IDisposable Lock(string folder)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "look.lock");
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 300) { Thread.Sleep(100); }
        }
    }

    /// <summary>A plugin with more records than this gets no record list, only its counts per type (Starfield.esm has 3.8 million).</summary>
    public const int ListLimit = 250_000;

    public string Folder { get; }
    public List<PluginSnapshot> Snapshots { get; } = new();
    string LogFile => Path.Combine(Folder, "log.jsonl");

    public HistoryLog(string folder)
    {
        Folder = folder;
        if (!File.Exists(LogFile)) return;
        foreach (var line in File.ReadLines(LogFile))
        {
            if (line.Length == 0) continue;
            try { if (JsonNode.Parse(line) is JsonObject o) Snapshots.Add(PluginSnapshot.From(o)); }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException) { /* a torn last line: left out */ }
        }
    }

    public Dictionary<string, List<PluginSnapshot>> ByPlugin() =>
        Snapshots.GroupBy(s => s.Plugin, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Number).ToList(), StringComparer.OrdinalIgnoreCase);

    public string ListPath(string list) => Path.Combine(Folder, list);

    public RecordList? ReadList(PluginSnapshot s)
    {
        if (s.List == null || !File.Exists(ListPath(s.List))) return null;
        try { return RecordList.Read(ListPath(s.List)); }
        catch (Exception e) when (e is IOException or InvalidDataException) { return null; }
    }

    public void Append(IReadOnlyList<PluginSnapshot> added, string dataFolder)
    {
        if (added.Count == 0) return;
        Directory.CreateDirectory(Folder);
        string about = Path.Combine(Folder, "about.txt");
        if (!File.Exists(about)) File.WriteAllText(about, $"SFSearch history log of {dataFolder}\nOne line per plugin copy seen (log.jsonl) and a compact list of its FormKeys and signatures (lists\\). No record contents. Deleting this folder only forgets the history.\n");
        var sb = new StringBuilder();
        foreach (var s in added) sb.Append(s.ToJson().ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })).Append('\n');
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.None);
                fs.Write(Encoding.UTF8.GetBytes(sb.ToString()));
                break;
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
        }
        Snapshots.AddRange(added);
    }
}

public sealed class ChangeRow
{
    public required DateTime When { get; init; }
    public required string Plugin { get; init; }
    /// <summary>added, removed, back, updated, masters, strings.</summary>
    public required string Kind { get; init; }
    public required string What { get; init; }
    /// <summary>True when this run of the report found it.</summary>
    public bool FoundNow { get; init; }
    public int? RecordsAdded { get; init; }
    public int? RecordsRemoved { get; init; }
    public int? RecordsRetyped { get; init; }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["when"] = When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), ["plugin"] = Plugin, ["kind"] = Kind, ["what"] = SFSearch.Records.SizeWords.Plain(What), ["foundNow"] = FoundNow };
        if (RecordsAdded != null) { o["recordsAdded"] = RecordsAdded; o["recordsRemoved"] = RecordsRemoved; o["recordsChangedType"] = RecordsRetyped; }
        return o;
    }
}

public sealed record RiskRow(string Plugin, List<string> Why);

public sealed record RecordChange(string Change, string Record, string FormKey, string Signature);

public sealed class ChangesAnswer : ReportAnswer
{
    public override string Kind => "changes";
    public string? Plugin { get; init; }
    public DateTime? HistoryBegan { get; set; }
    public DateTime? LastLook { get; set; }
    /// <summary>The look this answer compares Data with: the last before this call (the service's, or a call's). Null the first time.</summary>
    public HistoryLook? ComparedWith { get; set; }
    public DateTime Since { get; set; }
    public bool FirstRun { get; set; }
    public int PluginsInData { get; set; }
    public int SnapshotsKept { get; set; }
    public int SnapshotsAdded { get; set; }
    public List<ChangeRow> Changes { get; } = new();
    public List<RiskRow> AtRisk { get; } = new();
    /// <summary>Plugins Mutagen refuses (C204, fixes.17), and plugins built on one: left out of queries and of the
    /// winners, as Mutagen's load order leaves them out. Each with Mutagen's error, or the master that is refused.</summary>
    public List<(string Plugin, string Why)> Refused { get; } = new();

    // one plugin
    public List<(PluginSnapshot Snapshot, string What)> History { get; } = new();
    /// <summary>The two snapshots the record lists were compared between.</summary>
    public PluginSnapshot? From { get; set; }
    public PluginSnapshot? To { get; set; }
    public List<RecordChange> RecordChanges { get; } = new();
    public string? NoRecordChanges { get; set; }

    /// <summary>Where the history log is kept (outside Data, in SFSearch's home): the answer says so the first time.</summary>
    public string? HistoryFolder { get; set; }

    public int Added => RecordChanges.Count(r => r.Change == "added");
    public int RemovedRecords => RecordChanges.Count(r => r.Change == "removed");
    public int Retyped => RecordChanges.Count(r => r.Change == "type");

    protected override void Fill(JsonObject json)
    {
        if (Plugin != null) json["plugin"] = Plugin;
        if (HistoryBegan != null) json["historyBegan"] = HistoryBegan.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (HistoryFolder != null) json["historyFolder"] = HistoryFolder;
        if (LastLook != null) json["lastLook"] = LastLook.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (ComparedWith != null) json["comparedWith"] = ComparedWith.ToJson();
        json["firstRun"] = FirstRun; json["pluginsInData"] = PluginsInData; json["snapshotsKept"] = SnapshotsKept; json["snapshotsAdded"] = SnapshotsAdded;
        if (Plugin == null)
        {
            json["since"] = Since == DateTime.MinValue ? null : Since.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            json["changes"] = new JsonArray(Changes.Select(c => (JsonNode)c.ToJson()).ToArray());
            json["atRisk"] = new JsonArray(AtRisk.Select(r => (JsonNode)new JsonObject { ["plugin"] = r.Plugin, ["why"] = new JsonArray(r.Why.Select(w => (JsonNode)SizeWords.Plain(w)).ToArray()) }).ToArray());
        }
        else
        {
            json["history"] = new JsonArray(History.Select(h => { var o = h.Snapshot.ToJson(); o["what"] = SizeWords.Plain(h.What); return (JsonNode)o; }).ToArray());
            if (From != null && To != null)
            {
                json["compared"] = new JsonObject { ["from"] = From.Number, ["to"] = To.Number };
                json["recordChanges"] = new JsonArray(RecordChanges.Select(r => (JsonNode)new JsonObject { ["change"] = r.Change, ["record"] = r.Record, ["formKey"] = r.FormKey, ["signature"] = r.Signature }).ToArray());
            }
            if (NoRecordChanges != null) json["noRecordChanges"] = NoRecordChanges;
            json["atRisk"] = new JsonArray(AtRisk.Select(r => (JsonNode)new JsonObject { ["plugin"] = r.Plugin, ["why"] = new JsonArray(r.Why.Select(w => (JsonNode)SizeWords.Plain(w)).ToArray()) }).ToArray());
        }
        json["refused"] = new JsonArray(Refused.Select(r => (JsonNode)new JsonObject { ["plugin"] = r.Plugin, ["why"] = r.Why }).ToArray());
    }

    static string T(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"; compared with the look of 2026-11-13 10:20 by the service's idle check", or nothing the first time.</summary>
    string Compared => ComparedWith == null ? "" : $"; compared with the look of {T(ComparedWith.Seen)} by {ComparedWith.ByWords}";

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        string began = HistoryBegan == null ? "the history begins now" : $"history began {T(HistoryBegan.Value)}";
        if (Plugin == null)
        {
            body.RowNoun = "rows";
            body.Head.Add($"Changes in Data since {(Since == DateTime.MinValue ? "the history began" : T(Since))} ({began}; {N(PluginsInData)} plugins in Data, {N(SnapshotsKept)} plugin copies remembered{Compared}):");
            if (FirstRun)
                body.Rows.Add($"The history begins now: {N(SnapshotsAdded)} plugins remembered as they are. There is nothing to compare with yet; the next look says what changed."
                    + (HistoryFolder == null ? "" : $" They are remembered in a history log written to {HistoryFolder} (one line per plugin copy and a list of its FormKeys and record types, no record contents; nothing is written to Data). Deleting that folder only forgets the history."));
            else if (Changes.Count == 0)
                body.Rows.Add("No plugin was added, removed or changed in that time.");
            else
            {
                // The header and its separator are one row, so a page never parts them
                body.Rows.Add("| When | Plugin | What changed |\n| :--- | :--- | :--- |");
                foreach (var c in Changes) body.Rows.Add($"| {(c.FoundNow ? "now" : T(c.When))} | {Cell(c.Plugin)} | {Cell(c.What)} |");
            }
            body.Rows.Add($"Patches at risk ({N(AtRisk.Count)}): plugins whose master changed size after they were built, or whose overrides have a different type than their base.");
            if (AtRisk.Count == 0) body.Rows.Add("None.");
            else
            {
                body.Rows.Add("| Plugin | Why |\n| :--- | :--- |");
                foreach (var r in AtRisk) body.Rows.Add($"| {Cell(r.Plugin)} | {Cell(string.Join("; ", r.Why))} |");
            }
            if (Refused.Count > 0)
            {
                body.Rows.Add($"Refused by Mutagen ({N(Refused.Count)}): left out of queries and of the winners, as Mutagen's load order leaves them out; record and check2 still read them.");
                body.Rows.Add("| Plugin | Why |\n| :--- | :--- |");
                foreach (var r in Refused) body.Rows.Add($"| {Cell(r.Plugin)} | {Cell(r.Why)} |");
            }
        }
        else
        {
            body.RowNoun = "rows";
            body.Head.Add($"History of {Plugin} ({began}; {Plural(History.Count, "snapshot")}{Compared}):");
            body.Rows.Add("| Seen | Size | Records | New | Overrides | Different type than base | What changed |\n| :--- | :--- | ---: | ---: | ---: | ---: | :--- |");
            foreach (var (s, what) in History)
                body.Rows.Add(s.Removed ? $"| {T(s.Seen)} | | | | | | removed from Data |"
                    : $"| {T(s.Seen)} | {SizeWords.Marked(s.Size)} | {N(s.Records)} | {N(s.New)} | {N(s.Overrides)} | {N(s.TypeMismatches)} | {Cell(what)} |");
            if (From != null && To != null)
            {
                body.Rows.Add($"Records, from the copy seen {T(From.Seen)} to the copy seen {T(To.Seen)}: {N(Added)} added, {N(RemovedRecords)} removed, {N(Retyped)} changed type.");
                int cap = o.Detail ? int.MaxValue : 40;
                foreach (var kind in new[] { "removed", "added", "type" })
                {
                    var rows = RecordChanges.Where(r => r.Change == kind).ToList();
                    foreach (var r in rows.Take(cap)) body.Rows.Add($"  {(kind == "type" ? "changed type" : kind)}: {r.Record} {r.Signature}");
                    if (rows.Count > cap) body.Rows.Add($"  … {N(rows.Count - cap)} more {(kind == "type" ? "changed type" : kind)} (detail lists every one)");
                }
            }
            else if (NoRecordChanges != null) body.Rows.Add(NoRecordChanges);
            foreach (var r in AtRisk) body.Rows.Add($"At risk: {string.Join("; ", r.Why)}");
            foreach (var r in Refused) body.Rows.Add($"Refused by Mutagen: left out of queries and of the winners (record and check2 still read it): {r.Why}");
        }
        // Sizes are named in the order the answer prints them: "Light (Small)" the first time, "Light" after
        var sizes = new SizeWords();
        for (int i = 0; i < body.Head.Count; i++) body.Head[i] = sizes.Resolve(body.Head[i]);
        for (int i = 0; i < body.Rows.Count; i++) body.Rows[i] = sizes.Resolve(body.Rows[i]);
    }
}

public sealed class ChangesReport
{
    readonly HeaderIndex _index;
    readonly string _folder;
    readonly string _place;

    /// <param name="index">The header index to look at. Default: the session's, over the Data folder.</param>
    /// <param name="historyFolder">Where this index's history log is. Default: a folder under the session's kept folder, named for the Data folder.</param>
    /// <param name="place">What the index is over, for the answer's source line. Default: the session's Data folder.</param>
    public ChangesReport(ReportSession session, HeaderIndex? index = null, string? historyFolder = null, string? place = null)
    {
        _index = index ?? session.Index;
        _place = place ?? Path.GetFullPath(session.DataFolder);
        if (historyFolder != null)
        {
            if (session.ReadOnlyFolderOf(historyFolder) is { } game) throw new ReportException($"the history folder {historyFolder} is inside {game}, and nothing is ever written to the game's folders", "name a folder outside them");
            _folder = Path.GetFullPath(historyFolder);
        }
        else _folder = FolderIn(session.Kept("history"), session.DataFolder);
    }

    /// <summary>
    /// For a look with no report session (the service's, 2026-11-13, fix S11): the header index of the Data folder and
    /// the history folder of that Data folder (<see cref="FolderIn"/>). Opens no schema and no record.
    /// </summary>
    public ChangesReport(HeaderIndex index, string historyFolder, string dataFolder)
    {
        _index = index;
        _folder = Path.GetFullPath(historyFolder);
        _place = Path.GetFullPath(dataFolder);
    }

    /// <summary>The history folder of a Data folder inside the kept folder's "history": named by a hash of its full path, so two Data folders keep two histories.</summary>
    public static string FolderIn(string historyRoot, string dataFolder)
    {
        string data = Path.GetFullPath(dataFolder).TrimEnd('\\').ToLowerInvariant();
        return Path.Combine(Path.GetFullPath(historyRoot), Convert.ToHexString(System.IO.Hashing.XxHash64.Hash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant());
    }

    public string HistoryFolder => _folder;

    static string SizeName(MasterStyle s) => s == MasterStyle.Missing ? "missing" : s.ToString();

    PluginSnapshot Snapshot(IndexedPlugin p, int number, DateTime now, bool initial, HistoryLog log)
    {
        var seg = p.Segment;
        var masters = new List<(string, string)>();
        var names = p.Header?.Masters ?? Array.Empty<string>();
        for (int i = 0; i < names.Count; i++) masters.Add((names[i], i < p.MasterStyles.Count ? SizeName(p.MasterStyles[i]) : "missing"));
        string? list = null;
        int own = 0;
        if (seg != null)
        {
            for (int i = 0; i < seg.FirstUnresolved; i++) if (seg[i].Owner == seg.SelfOwner) own++;
            if (seg.Count <= HistoryLog.ListLimit)
            {
                // One list per reading of one copy: the same copy read with other master sizes gives other FormKeys
                string id = Convert.ToHexString(System.IO.Hashing.XxHash64.Hash(Encoding.UTF8.GetBytes(string.Join("|", masters.Select(m => m.Item1.ToLowerInvariant() + "=" + m.Item2))))).ToLowerInvariant()[..8];
                list = Path.Combine("lists", $"{p.ContentKey}-{id}.bin");
                string path = log.ListPath(list);
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    RecordList.From(seg).Write(path);
                }
            }
        }
        return new PluginSnapshot
        {
            Number = number, Plugin = p.Name, Seen = now, Initial = initial, Key = p.ContentKey.ToString(), Strings = StringsOf(p), Length = p.Stamp.Length,
            Size = p.Header == null ? "" : SizeName(p.Header.Style), Masters = masters,
            Records = seg?.Count ?? 0, New = own, Overrides = seg == null ? 0 : seg.FirstUnresolved - own, Unresolved = seg?.Unresolved ?? 0,
            Counts = seg?.Signatures.Where(x => x.Count > 0).ToDictionary(x => x.Signature, x => x.Count, StringComparer.Ordinal) ?? new(StringComparer.Ordinal),
            TypeMismatches = p.Problems.TypeMismatches.Count, OverridesOfNothing = p.Problems.OverridesOfNothing.Count,
            Problem = p.Problems.ReadError ?? (p.Problems.MissingMasters.Count > 0 ? "missing master: " + string.Join(", ", p.Problems.MissingMasters) : null),
            List = list,
        };
    }

    static string IdentityOf(IndexedPlugin p, bool withStrings = true)
    {
        var names = p.Header?.Masters ?? Array.Empty<string>();
        return p.ContentKey + "|" + string.Join("|", names.Select((n, i) => n.ToLowerInvariant() + "=" + (i < p.MasterStyles.Count ? SizeName(p.MasterStyles[i]) : "missing")))
            + (withStrings && StringsOf(p) is { } strings ? "|strings=" + strings : "");
    }

    /// <summary>The stamp of a localized plugin's string files as the log keeps it; null when it isn't localized.</summary>
    static string? StringsOf(IndexedPlugin p) => !p.Strings.Stamped ? null : p.Strings.Key.IsZero ? "none" : p.Strings.Key.ToString();

    static string SizeChanges(PluginSnapshot before, PluginSnapshot after)
    {
        var old = before.Masters.ToDictionary(m => m.Name, m => m.Size, StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", after.Masters.Where(m => old.TryGetValue(m.Name, out var was) && was != m.Size).Select(m => $"{m.Name} {SizeWords.Marked(old[m.Name])} → {SizeWords.Marked(m.Size)}"));
    }

    /// <summary>What a snapshot changed against the one before it, with the record lists compared when both were kept.</summary>
    (string Kind, string What, int? Added, int? Removed, int? Retyped) Describe(PluginSnapshot? prev, PluginSnapshot s, HistoryLog log)
    {
        if (s.Removed) return ("removed", "removed from Data", null, null, null);
        string shape = $"{SizeWords.Marked(s.Size)}, {Plural(s.Records, "record")} ({N(s.New)} new, {N(s.Overrides)} overrides)" + (s.Problem != null ? $"; {s.Problem}" : "");
        if (prev == null) return ("added", $"new in Data: {shape}", null, null, null);
        if (prev.Removed) return ("back", $"back in Data: {shape}", null, null, null);
        var parts = new List<string>();
        int? added = null, removed = null, retyped = null;
        string kind = "masters";
        if (prev.Key != s.Key)
        {
            kind = "updated";
            var a = log.ReadList(prev); var b = log.ReadList(s);
            if (a != null && b != null)
            {
                added = b.Records.Keys.Count(k => !a.Records.ContainsKey(k));
                removed = a.Records.Keys.Count(k => !b.Records.ContainsKey(k));
                retyped = a.Records.Count(k => b.Records.TryGetValue(k.Key, out var sig) && sig != k.Value);
                parts.Add($"updated: {Plural(added.Value, "record")} added, {N(removed.Value)} removed{(retyped > 0 ? $", {N(retyped.Value)} changed type" : "")} ({N(prev.Records)} → {N(s.Records)} records)");
            }
            else parts.Add($"updated: {N(prev.Records)} → {N(s.Records)} records (new {N(prev.New)} → {N(s.New)}, overrides {N(prev.Overrides)} → {N(s.Overrides)}); no record list was kept for {(a == null ? "the earlier copy" : "this copy")}");
        }
        if (prev.Size != s.Size) parts.Add($"size {SizeWords.Marked(prev.Size)} → {SizeWords.Marked(s.Size)}");
        string before = string.Join(", ", prev.Masters.Select(m => m.Name)), after = string.Join(", ", s.Masters.Select(m => m.Name));
        if (!before.Equals(after, StringComparison.OrdinalIgnoreCase)) parts.Add($"masters now: {(after.Length == 0 ? "none" : after)}");
        string sizes = SizeChanges(prev, s);
        if (sizes.Length > 0) parts.Add($"{(sizes.Contains(',') ? "masters" : "a master")} changed size: {sizes}");
        if (prev.Strings != null && s.Strings != null && prev.Strings != s.Strings)
        {
            parts.Add("strings changed: its string files (where its texts are read from) changed");
            if (prev.Key == s.Key && kind == "masters") kind = "strings";
        }
        if (s.TypeMismatches > 0 && prev.TypeMismatches == 0) parts.Add($"now {Plural(s.TypeMismatches, "override")} of a different type than {(s.TypeMismatches == 1 ? "its" : "their")} base");
        if (parts.Count == 0) parts.Add("read again (the same content and masters)");
        return (kind, string.Join("; ", parts), added, removed, retyped);
    }

    string Label(string formKey)
    {
        var v = _index.Versions(formKey);
        if (v == null || v.Holders.Count == 0) return $"(in no plugin now) ({formKey})";
        return _index.Describe(v.Winner >= 0 ? v.Holders[v.Winner] : v.Holders[0]).Label;
    }

    /// <summary>What one look found: the log after it, the snapshots it added, the look before it, and itself.</summary>
    public sealed record LookResult(HistoryLog Log, List<PluginSnapshot> Added, bool First, HistoryLook? Before, HistoryLook This);

    /// <summary>
    /// One look (2026-11-13, fix S11; before, the start of <see cref="Changes"/>): a snapshot of every plugin whose copy
    /// or masters' sizes aren't the ones the log saw last, and one for every plugin gone from Data; then the look itself
    /// is written down (last-look.json). The first look of a history begins it. Held against another look of the same
    /// history (the service's and a call's) by a lock file.
    /// </summary>
    /// <param name="by">What looks: "call", or the service's "service-build" (its start), "service-check", "service-sweep" (while idle).</param>
    public LookResult Look(string by = "call") => Look(null, by);

    /// <summary>A look at the time given (<see cref="ChangesOptions.Now"/>, the tests' clock), or now when null.</summary>
    internal LookResult Look(DateTime? at, string by)
    {
        _index.Refresh();
        DateTime now = at ?? DateTime.Now;
        now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
        using var held = HistoryLog.Lock(_folder);
        var log = new HistoryLog(_folder);
        bool first = log.Snapshots.Count == 0;
        // The look before this one; a history older than last-look.json has only its last snapshot's time
        var before = HistoryLook.Read(_folder) ?? (first ? null : new HistoryLook(log.Snapshots.Max(s => s.Seen), "log", 0, 0, log.Snapshots.Count));
        var byPlugin = log.ByPlugin();
        var added = new List<PluginSnapshot>();
        int number = log.Snapshots.Count == 0 ? 0 : log.Snapshots.Max(s => s.Number);
        var inData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _index.Plugins.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            inData.Add(p.Name);
            byPlugin.TryGetValue(p.Name, out var have);
            var last = have?[^1];
            if (last != null && !last.Removed && last.Identity == IdentityOf(p)) continue;
            // A line written before string files were recorded, and nothing else changed: the string files are recorded
            // from now on, in a line that isn't listed as a change
            bool recordStrings = last != null && !last.Removed && last.Strings == null && p.Strings.Stamped && last.IdentityWithoutStrings == IdentityOf(p, withStrings: false);
            added.Add(Snapshot(p, ++number, now, first || recordStrings, log));
        }
        foreach (var (name, have) in byPlugin)
            if (!inData.Contains(name) && !have[^1].Removed) added.Add(new PluginSnapshot { Number = ++number, Plugin = have[^1].Plugin, Seen = now, Removed = true });
        log.Append(added, _place);
        var look = new HistoryLook(now, by, _index.Plugins.Count, added.Count, log.Snapshots.Count);
        look.Write(_folder, before?.By == "log" ? null : before);
        return new LookResult(log, added, first, before, look);
    }

    /// <summary>
    /// The service's look (2026-11-13, fix S11), after its header index job: one line for its log and status, such as
    /// "history: looked at 370 plugins at 10:20 (the service's idle check): nothing changed since 10:10; 412 lines in …".
    /// </summary>
    public static string ServiceLook(HeaderIndex index, string historyRoot, string dataFolder, string by)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string folder = FolderIn(historyRoot, dataFolder);
        // The rule a report session keeps (ReportSession.ReadOnlyFolderOf): nothing is written in the Data folder, nor in
        // the game's folder when the Data folder is called Data
        string full = folder.TrimEnd('\\') + "\\", data = Path.GetFullPath(dataFolder).TrimEnd('\\');
        string? game = Path.GetFileName(data).Equals("Data", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(data) : null;
        if (full.StartsWith(data + "\\", StringComparison.OrdinalIgnoreCase) || game != null && Path.GetPathRoot(game) != game && full.StartsWith(game.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new ReportException($"the history folder {folder} is inside the game's folders, and nothing is ever written there", "move the SFSearch home outside the game's folders");
        var r = new ChangesReport(index, folder, dataFolder).Look(null, by);
        string what = r.First ? $"the history begins: {Plural(r.Added.Count, "plugin")} remembered as they are"
            : r.Added.Count == 0 ? $"nothing changed since {r.Before!.Seen:yyyy-MM-dd HH:mm} ({r.Before.ByWords})"
            : $"{Plural(r.Added.Count, "plugin")} changed since {r.Before!.Seen:yyyy-MM-dd HH:mm}: {string.Join(", ", r.Added.Take(5).Select(s => s.Plugin + (s.Removed ? " (gone)" : "")))}{(r.Added.Count > 5 ? ", …" : "")}";
        return $"history: looked at {Plural(index.Plugins.Count, "plugin")} at {r.This.Seen:yyyy-MM-dd HH:mm:ss} ({r.This.ByWords}), {clock.ElapsedMilliseconds:N0} ms: {what}. {Plural(r.Log.Snapshots.Count, "line")} in {r.Log.Folder}.";
    }

    public ChangesAnswer Changes(ChangesOptions? options = null)
    {
        options ??= new ChangesOptions();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var looked = Look(options.Now, "call");
        DateTime now = looked.This.Seen;
        var log = looked.Log;
        bool first = looked.First;
        var added = looked.Added;
        var answer = new ChangesAnswer
        {
            Plugin = options.Plugin, FirstRun = first, PluginsInData = _index.Plugins.Count,
            LastLook = looked.Before?.Seen, ComparedWith = looked.Before, HistoryFolder = _folder,
            Since = options.All ? DateTime.MinValue : options.Since ?? now.AddDays(-30),
        };
        var foundNow = new HashSet<int>(added.Select(s => s.Number));
        answer.SnapshotsAdded = added.Count;
        answer.SnapshotsKept = log.Snapshots.Count(s => !s.Removed);
        answer.HistoryBegan = log.Snapshots.Count == 0 ? null : log.Snapshots.Min(s => s.Seen);
        var byPlugin = log.ByPlugin();

        // ---- plugins Mutagen refuses (C204): its error, or the master that is refused
        static string Refusal(IndexedPlugin p) => p.Problems.Refused ?? p.LeftOut!;

        // ---- patches at risk
        RiskRow? Risk(IndexedPlugin p)
        {
            var why = new List<string>();
            if (p.Problems.TypeMismatches.Count > 0)
                why.Add($"{Plural(p.Problems.TypeMismatches.Count, "override")} of a different type than {(p.Problems.TypeMismatches.Count == 1 ? "its" : "their")} base" +
                    (p.Problems.OverridesOfNothing.Count > 0 ? $", and {N(p.Problems.OverridesOfNothing.Count)} of a record their master doesn't have" : "") + ": built for another version or size of a master");
            if (byPlugin.TryGetValue(p.Name, out var snaps))
            {
                // The first time this copy was seen, against how its masters are now
                var lastCopy = snaps[^1];
                var built = snaps.FirstOrDefault(s => !s.Removed && s.Key == lastCopy.Key);
                if (built != null && !lastCopy.Removed)
                {
                    string sizes = SizeChanges(built, lastCopy);
                    if (sizes.Length > 0) why.Add($"{(sizes.Contains(',') ? "masters" : "a master")} changed size after this copy was first seen: {sizes}. Its FormIDs count its masters by size, so its records now read as other records");
                }
            }
            return why.Count > 0 ? new RiskRow(p.Name, why) : null;
        }

        if (options.Plugin == null)
        {
            foreach (var (name, snaps) in byPlugin.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                for (int i = 0; i < snaps.Count; i++)
                {
                    var s = snaps[i];
                    if (s.Initial || (s.Seen < answer.Since && !foundNow.Contains(s.Number))) continue;
                    var (kind, what, a, r, t) = Describe(i > 0 ? snaps[i - 1] : null, s, log);
                    answer.Changes.Add(new ChangeRow { When = s.Seen, Plugin = s.Plugin, Kind = kind, What = what, FoundNow = foundNow.Contains(s.Number), RecordsAdded = a, RecordsRemoved = r, RecordsRetyped = t });
                }
            answer.Changes.Sort((x, y) => x.FoundNow != y.FoundNow ? (x.FoundNow ? -1 : 1) : y.When != x.When ? y.When.CompareTo(x.When) : StringComparer.OrdinalIgnoreCase.Compare(x.Plugin, y.Plugin));
            if (answer.Changes.Any(c => !c.FoundNow))
                answer.Notes.Add("A change's time is the look that first saw it: a changes call, or the SFSearch service by itself (at its start, and every 10 minutes while it is idle). \"now\" is this call.");
            foreach (var p in _index.Plugins.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (Risk(p) is { } risk) answer.AtRisk.Add(risk);
                if (p.LeftOut != null) answer.Refused.Add((p.Name, Refusal(p)));
            }
            if (answer.AtRisk.Count > 0)
                answer.Notes.Add("A plugin's FormIDs count its masters by size. When a master changes between Full, Medium and Light (Small) after a patch was built, the patch's records land on other records. Don't trust or copy such a patch until it is rebuilt.");
        }
        else
        {
            string name = Path.GetFileName(options.Plugin.Trim().Trim('"'));
            if (first) answer.Notes.Add($"The history began with this call: {N(added.Count)} plugins were remembered in a history log written to {_folder} (no record contents; nothing is written to Data). Deleting that folder only forgets the history.");
            if (!byPlugin.TryGetValue(name, out var snaps))
                throw new ReportException($"the history has no plugin named {name}", $"give the name of a plugin that is or was in Data ({N(byPlugin.Count)} are remembered)");
            for (int i = 0; i < snaps.Count; i++)
                answer.History.Add((snaps[i], snaps[i].Initial ? (i == 0 ? "there when the history began" : "the same copy; its string files are recorded from now on") : Describe(i > 0 ? snaps[i - 1] : null, snaps[i], log).What));
            // The last update: the last two copies with other content
            var copies = snaps.Where(s => !s.Removed).ToList();
            var to = copies.LastOrDefault();
            var from = to == null ? null : copies.LastOrDefault(s => s.Number < to.Number && s.Key != to.Key);
            if (from != null && to != null)
            {
                var a = log.ReadList(from); var b = log.ReadList(to);
                if (a != null && b != null)
                {
                    answer.From = from; answer.To = to;
                    foreach (var (k, sig) in a.Records.OrderBy(r => r.Key[7..], StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key[..6], StringComparer.Ordinal))
                        if (!b.Records.ContainsKey(k)) answer.RecordChanges.Add(new RecordChange("removed", Label(k), k, sig));
                    foreach (var (k, sig) in b.Records.OrderBy(r => r.Key[7..], StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key[..6], StringComparer.Ordinal))
                    {
                        if (!a.Records.TryGetValue(k, out var was)) answer.RecordChanges.Add(new RecordChange("added", Label(k), k, sig));
                        else if (was != sig) answer.RecordChanges.Add(new RecordChange("type", Label(k), k, $"{was} → {sig}"));
                    }
                    if (answer.RemovedRecords > 0) answer.Notes.Add("The history keeps FormKeys and signatures, not EditorIDs. A removed record is named from the plugins that still hold it; one that no plugin holds any more has only its FormKey.");
                }
                else answer.NoRecordChanges = $"The copies seen {from.Seen:yyyy-MM-dd HH:mm} and {to.Seen:yyyy-MM-dd HH:mm} differ, but no record list was kept for {(a == null ? "the earlier one" : "the later one")} (a plugin with more than {N(HistoryLog.ListLimit)} records, or one that couldn't be read): {N(from.Records)} → {N(to.Records)} records.";
            }
            else answer.NoRecordChanges = copies.Count == 0 ? null : $"Only one copy of {snaps[0].Plugin} has been seen: there is no update to compare.";
            if (_index.Plugin(name) is { } p && Risk(p) is { } risk) answer.AtRisk.Add(risk);
            if (_index.Plugin(name) is { LeftOut: not null } left) answer.Refused.Add((left.Name, Refusal(left)));
        }

        answer.Sources.Add($"{N(_index.Plugins.Count)} plugins of {_place} by their content keys");
        answer.Sources.Add($"history log in {_folder}: {Plural(log.Snapshots.Count, "line")}{(added.Count > 0 ? $", {N(added.Count)} added now" : "")}");
        answer.Read.Add($"the header index of {Plural(_index.Plugins.Count, "plugin")}");
        int lists = added.Count(s => s.List != null);
        if (lists > 0) answer.Read.Add($"the record lists of {Plural(lists, "plugin")} (written to the history)");
        answer.Seconds = clock.Elapsed.TotalSeconds;
        return answer;
    }
}
