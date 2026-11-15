// assets: are the files a plugin's records name there? Every file path on its records (a model on an item, an
// armor addon's male and female models, a skeleton, a rig, a material, a texture) is held against the loose files in
// Data, the plugin's own archives, its masters' archives and the other archives in Data.
//  - Which fields are paths comes from the schema (the fields Mutagen types as file paths), and from the records
//    themselves: a text that ends in a mesh's, material's or texture's file extension in a field the schema calls
//    plain text. The answer says how many paths came each way.
//  - A path is found when Data holds it as a loose file or an archive holds it, as written or under the folder its
//    kind lives in (a model "Weapons\x.nif" is the file "meshes\weapons\x.nif").
//  - A missing path that the master's version of the record names too is inherited: the game's own records name
//    about 2,000 files that aren't in its archives.
//  - An armor's addons that live in a master are what the armor shows: each is read once and its paths listed once,
//    with every record of the plugin that uses it. What is missing on them is the master's to bring, not the plugin's.
//  - An armor addon link (an armor's Armatures) that names a record of another type is a problem of the plugin: the
//    report says which record it names and its real type, from the header index. Its files aren't checked as an addon's.
//  - A material (.mat) that is no file anywhere can't be judged: the game's own materials, and some mods', are
//    compiled into a material database (materialsbeta.cdb). It is counted apart, not called missing.
// World data (cells and what they hold) is left out unless asked, as in check.
using System.Text.Json.Nodes;
using SFSearch.Compare;
using SFSearch.Index;

namespace SFSearch.Reports;

using static ReportAnswer;

public sealed class AssetsOptions
{
    /// <summary>Read world data too (cells and worldspaces and what they hold). Off by default.</summary>
    public bool World { get; init; }
    /// <summary>For a plugin file outside Data: read the archives beside it, not the ones in Data named for it.</summary>
    public bool Beside { get; init; }
}

public enum AssetStatus
{
    /// <summary>A loose file in Data or a file in an archive the game loads.</summary>
    Found,
    /// <summary>No loose file and no archive in Data holds it.</summary>
    Missing,
    /// <summary>Only an archive the game doesn't load holds it: its plugin isn't in the load order, or isn't there at all.</summary>
    NotLoaded,
    /// <summary>A material that is no file anywhere: compiled into a material database, or missing. Not judged.</summary>
    NoFile,
    /// <summary>Not a file name as written: it holds a variable ("$(Subgraph)\…"), or it has no file extension (an icon's name).</summary>
    NotChecked,
}

/// <summary>One file path of one record.</summary>
public sealed class AssetRow
{
    public required string Record { get; init; }
    public required string FormKey { get; init; }
    public required string Signature { get; init; }
    /// <summary>Where the path sits in the record: Model.File, WorldModel.Male.File.</summary>
    public required string Field { get; init; }
    /// <summary>The path as the record writes it.</summary>
    public required string Path { get; init; }
    /// <summary>True when the schema types the field as a file path; false when the path was found by its value.</summary>
    public bool Typed { get; init; }
    /// <summary>The record the path is on, when it isn't the plugin's own: a master's armor addon one of its armors uses,
    /// as "EditorID (FormKey) ARMA, read from master.esm". Such a path is listed once: <see cref="Record"/> is then the
    /// first record of the plugin (in file order) that uses the addon, and <see cref="UsedBy"/> names all of them.</summary>
    public string? Via { get; init; }
    /// <summary>For a path on a master's armor addon: the addon's FormKey.</summary>
    public string? ViaFormKey { get; init; }
    /// <summary>For a path on a master's armor addon: the master whose version of the addon was read.</summary>
    public string? ViaPlugin { get; init; }
    /// <summary>For a path on a master's armor addon: every record of the plugin that uses the addon, as "EditorID (FormKey)", in file order.</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = Array.Empty<string>();
    public AssetStatus Status { get; init; }
    public FileOrigin Origin { get; init; }
    /// <summary>"loose", or the archive's name.</summary>
    public string? Where { get; init; }
    /// <summary>The Data-relative path it was found under.</summary>
    public string? FoundAs { get; init; }
    /// <summary>For a path that isn't found: the master whose version of the record names the same path. The plugin didn't add it.</summary>
    public string? InheritedFrom { get; set; }

    public bool IsProblem => Status is AssetStatus.Missing or AssetStatus.NotLoaded;

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["record"] = Record, ["formKey"] = FormKey, ["signature"] = Signature, ["field"] = Field, ["path"] = Path, ["status"] = Status.ToString() };
        if (!Typed) o["foundByValue"] = true;
        if (Via != null)
        {
            o["via"] = Via; o["viaFormKey"] = ViaFormKey; o["viaPlugin"] = ViaPlugin;
            o["usedBy"] = new JsonArray(UsedBy.Select(u => (JsonNode)u).ToArray());
        }
        if (Where != null) { o["foundIn"] = Where; o["foundAs"] = FoundAs; o["origin"] = Origin.ToString(); }
        if (InheritedFrom != null) o["inheritedFrom"] = InheritedFrom;
        return o;
    }
}

/// <summary>An armor addon link (an armor's Armatures) that names a record of another type. The armor has no model
/// there; most often the FormID names the wrong master.</summary>
public sealed class AssetLinkProblem
{
    /// <summary>The plugin's record that holds the link, as "EditorID (FormKey)".</summary>
    public required string Record { get; init; }
    public required string FormKey { get; init; }
    public required string Signature { get; init; }
    /// <summary>Where the link sits: Armatures[0].ArmorAddon.</summary>
    public required string Field { get; init; }
    /// <summary>The record the link names, as "EditorID (FormKey)".</summary>
    public required string Target { get; init; }
    public required string TargetFormKey { get; init; }
    /// <summary>The named record's real type, from the header index: FURN, MISC, AVMD.</summary>
    public required string TargetSignature { get; init; }
    /// <summary>The plugin whose version of the named record the plugin sees.</summary>
    public required string TargetReadFrom { get; init; }
    /// <summary>True when the named record is in a master, not in the plugin itself.</summary>
    public bool InMaster { get; init; }
    /// <summary>Armor addons with the same ID in the plugin's other masters (or the plugin itself): likely the one meant.</summary>
    public IReadOnlyList<string> SameIdAddons { get; init; } = Array.Empty<string>();

    public string Text =>
        $"{Record} {Signature}: {Field} points at {TargetSignature} {Target}, not an armor addon: the FormID likely names the wrong {(InMaster ? "master" : "record")}" +
        (SameIdAddons.Count > 0 ? $" ({(SameIdAddons.Count == 1 ? "an armor addon has" : "armor addons have")} the same ID: {string.Join(", ", SameIdAddons)})" : "") + ".";

    public JsonObject ToJson() => new()
    {
        ["record"] = Record, ["formKey"] = FormKey, ["signature"] = Signature, ["field"] = Field,
        ["target"] = Target, ["targetFormKey"] = TargetFormKey, ["targetSignature"] = TargetSignature, ["targetReadFrom"] = TargetReadFrom,
        ["sameIdAddons"] = new JsonArray(SameIdAddons.Select(a => (JsonNode)a).ToArray()),
    };
}

public sealed class AssetsAnswer : ReportAnswer
{
    public override string Kind => "assets";
    public required string Plugin { get; init; }
    public int Records { get; set; }
    public int RecordsRead { get; set; }
    public int RecordsWithPaths { get; set; }
    public int Unreadable { get; set; }
    /// <summary>The fields the schema types as file paths: how many, at how many places in how many record types.</summary>
    public int TypedFields { get; init; }
    public int TypedPlaces { get; init; }
    public int TypedRecordTypes { get; init; }
    /// <summary>The file extensions a text must end in to be taken as a path when its field isn't typed as one.</summary>
    public IReadOnlyList<string> ByValueExtensions { get; init; } = Array.Empty<string>();
    public List<string> OwnArchives { get; } = new();
    public List<AssetRow> Rows { get; } = new();
    /// <summary>Armor addon links that name a record of another type: problems of the plugin.</summary>
    public List<AssetLinkProblem> WrongLinks { get; } = new();
    public Dictionary<string, int> WorldLeftOut { get; } = new(StringComparer.Ordinal);

    public int Count(AssetStatus s) => Rows.Count(r => r.Status == s);
    public int Count(FileOrigin o) => Rows.Count(r => r.Status == AssetStatus.Found && r.Origin == o);
    public IEnumerable<AssetRow> Problems => Rows.Where(r => r.IsProblem);
    /// <summary>Problems the plugin brought itself: not the ones a master's version of the record has too.</summary>
    public IEnumerable<AssetRow> OwnProblems => Rows.Where(r => r.IsProblem && r.InheritedFrom == null);
    /// <summary>True when a path the plugin brought is missing, or found only in an archive the game doesn't load, or an
    /// armor addon link names a record of another type.</summary>
    public bool Failed => OwnProblems.Any() || WrongLinks.Count > 0;

    protected override void Fill(JsonObject json)
    {
        json["plugin"] = Plugin;
        json["records"] = Records; json["recordsRead"] = RecordsRead; json["recordsWithPaths"] = RecordsWithPaths;
        json["schemaPathFields"] = new JsonObject { ["fields"] = TypedFields, ["places"] = TypedPlaces, ["recordTypes"] = TypedRecordTypes };
        json["byValueExtensions"] = new JsonArray(ByValueExtensions.Select(e => (JsonNode)e).ToArray());
        json["ownArchives"] = new JsonArray(OwnArchives.Select(e => (JsonNode)e).ToArray());
        json["counts"] = new JsonObject
        {
            ["paths"] = Rows.Count, ["typed"] = Rows.Count(r => r.Typed), ["byValue"] = Rows.Count(r => !r.Typed),
            ["found"] = Count(AssetStatus.Found), ["loose"] = Count(FileOrigin.Loose), ["ownArchives"] = Count(FileOrigin.Own), ["mastersArchives"] = Count(FileOrigin.Master), ["otherArchives"] = Count(FileOrigin.OtherLoaded),
            ["missing"] = Count(AssetStatus.Missing), ["missingInherited"] = Rows.Count(r => r.Status == AssetStatus.Missing && r.InheritedFrom != null),
            ["missingByValue"] = Rows.Count(r => r.Status == AssetStatus.Missing && !r.Typed),
            ["notLoaded"] = Count(AssetStatus.NotLoaded), ["noFile"] = Count(AssetStatus.NoFile), ["notChecked"] = Count(AssetStatus.NotChecked),
            ["masterAddons"] = Rows.Where(r => r.ViaFormKey != null).Select(r => r.ViaFormKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ["wrongLinks"] = WrongLinks.Count,
        };
        json["wrongLinks"] = new JsonArray(WrongLinks.Select(w => (JsonNode)w.ToJson()).ToArray());
        json["rows"] = new JsonArray(Rows.Select(r => (JsonNode)r.ToJson()).ToArray());
    }

    static string StatusText(AssetRow r) => r.Status switch
    {
        AssetStatus.Found => r.Origin switch
        {
            FileOrigin.Loose => "found, loose",
            FileOrigin.Own => $"found in {r.Where}",
            FileOrigin.Master => $"found in a master's {r.Where}",
            _ => $"found in {r.Where}, which isn't the plugin's or a master's",
        },
        AssetStatus.Missing => "MISSING" + (r.InheritedFrom != null ? $" (inherited: {r.InheritedFrom}'s version names it too)" : ""),
        AssetStatus.NotLoaded => $"NOT LOADED: only in {r.Where}, which the game doesn't load" + (r.InheritedFrom != null ? $" (inherited: {r.InheritedFrom}'s version names it too)" : ""),
        AssetStatus.NoFile => "no file (compiled material, or missing)",
        _ => "not checked (not a file name as written)",
    };

    static string Line(AssetRow r) => r.Via != null
        ? $"  {r.Via}{UsedByText(r)}: {r.Field} = {r.Path}: {StatusText(r)}{(r.Typed ? "" : " [by value]")}"
        : $"  {r.Record} {r.Signature}: {r.Field} = {r.Path}: {StatusText(r)}{(r.Typed ? "" : " [by value]")}";

    /// <summary>", used by A (…) ARMO, B (…) ARMO and 2 more": the plugin's records that use a master's armor addon.</summary>
    static string UsedByText(AssetRow r)
    {
        var users = r.UsedBy.Count > 0 ? r.UsedBy : new[] { r.Record };
        const int shown = 3;
        return $", used by {string.Join(", ", users.Take(shown))}{(users.Count > shown ? $" and {users.Count - shown} more" : "")}";
    }

    /// <summary>The record a path is on: a master's armor addon, or the plugin's own record.</summary>
    static (string Record, string Key, string Signature) OnRecord(AssetRow r) =>
        r.Via != null ? (r.Via.Split(" ARMA, ", 2)[0], r.ViaFormKey ?? r.FormKey, "ARMA") : (r.Record, r.FormKey, r.Signature);

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = WrongLinks.Count > 0 ? "lines" : "paths";
        int missing = Count(AssetStatus.Missing), inherited = Rows.Count(r => r.Status == AssetStatus.Missing && r.InheritedFrom != null);
        int ownTyped = Rows.Count(r => r.Status == AssetStatus.Missing && r.InheritedFrom == null && r.Typed), ownByValue = missing - inherited - ownTyped;
        body.Head.Add($"Assets of {Plugin}: {Plural(Rows.Count, "file path")} on {N(RecordsWithPaths)} of {N(RecordsRead)} records read ({N(Records)} in the plugin).");
        body.Head.Add($"Path fields: the schema types {TypedFields} fields as file paths, at {N(TypedPlaces)} places in {TypedRecordTypes} record types: {Plural(Rows.Count(r => r.Typed), "path")}. " +
            $"{Plural(Rows.Count(r => !r.Typed), "more path")} found by value (a text ending in {string.Join(", ", ByValueExtensions)} in a field the schema calls plain text).");
        body.Head.Add("| Result | Paths |");
        body.Head.Add("| :--- | :--- |");
        body.Head.Add($"| Found | {N(Count(AssetStatus.Found))}: loose {N(Count(FileOrigin.Loose))}, the plugin's archives {N(Count(FileOrigin.Own))}, its masters' archives {N(Count(FileOrigin.Master))}, other loaded archives {N(Count(FileOrigin.OtherLoaded))} |");
        body.Head.Add($"| MISSING | {N(missing)}: the plugin's own {N(ownTyped + ownByValue)} ({N(ownTyped)} in fields typed as paths, {N(ownByValue)} by value), inherited from a master's version of the record {N(inherited)} |");
        body.Head.Add($"| NOT LOADED (only in an archive the game doesn't load) | {N(Count(AssetStatus.NotLoaded))} |");
        body.Head.Add($"| Materials that are no file (compiled, or missing: not judged) | {N(Count(AssetStatus.NoFile))} |");
        if (Count(AssetStatus.NotChecked) > 0) body.Head.Add($"| Not checked (a variable in the path, or no file extension) | {N(Count(AssetStatus.NotChecked))} |");
        if (WrongLinks.Count > 0) body.Head.Add($"| Armor addon links that name another type of record (no model there) | {N(WrongLinks.Count)} |");
        // A link to the wrong type of record is the plugin's own problem: each one first, compact or not
        if (WrongLinks.Count > 0) body.Rows.Add($"Armor addon links that don't name an armor addon ({N(WrongLinks.Count)}):");
        foreach (var w in WrongLinks) body.Rows.Add("  " + w.Text);
        if (o.Detail) { foreach (var r in Rows) body.Rows.Add(Line(r)); }
        else
        {
            // The plugin's own problems first, each one; then the inherited ones, folded by record type and field
            var own = OwnProblems.ToList();
            if (own.Count > 0) body.Rows.Add($"Missing or not loaded, the plugin's own ({N(own.Count)}):");
            foreach (var r in own) body.Rows.Add(Line(r));
            var rest = Problems.Where(r => r.InheritedFrom != null).ToList();
            if (rest.Count > 0) body.Rows.Add($"Missing, inherited from a master's version of the record ({N(rest.Count)}; detail lists every one):");
            foreach (var g in rest.GroupBy(r => (OnRecord(r).Signature, Field: PathWalker.Plain(r.Field))))
            {
                var list = g.ToList();
                body.Rows.Add($"  {g.Key.Signature} {g.Key.Field}: {Plural(list.Count, "path")} on {Plural(list.Select(r => OnRecord(r).Key).Distinct().Count(), "record")}, such as {OnRecord(list[0]).Record}: {list[0].Path}");
            }
        }
        if (body.Rows.Count == 0)
            body.Empty = Rows.Count == 0 ? EmptyReason
                : $"Nothing is missing: every one of the {N(Count(AssetStatus.Found))} paths that can be judged is a loose file or in a loaded archive. Detail lists every path.";
    }
}

public sealed class AssetsReport
{
    /// <summary>What a text must end in to be taken as a path when its field isn't typed as one: meshes, the files
    /// beside them (rigs, animation graphs), materials and textures.</summary>
    public static readonly string[] ByValueExtensions = { ".nif", ".mesh", ".mat", ".rig", ".agx", ".dds" };
    /// <summary>The folders of Data that file paths on records are relative to.</summary>
    static readonly string[] Roots = { "meshes\\", "materials\\", "textures\\" };

    readonly ReportSession _s;
    readonly PathWalker _walker;
    readonly LinkWalker _links;
    readonly Dictionary<string, int> _places;

    public AssetsReport(ReportSession session)
    {
        _s = session;
        _walker = new PathWalker(session.Schema, ByValueExtensions);
        _links = new LinkWalker(session.Schema);
        _places = _walker.TypedPlaces();
    }

    public PathWalker Walker => _walker;

    /// <summary>The spellings a path on a record is looked for under: as written (a leading "Data\" taken off), then under each asset folder.</summary>
    public static List<string> Candidates(string value)
    {
        string n = ArchiveStore.DataRelative(value);
        var list = new List<string> { n };
        foreach (var root in Roots)
            if (!n.StartsWith(root, StringComparison.OrdinalIgnoreCase)) list.Add(root + n);
        return list;
    }

    /// <summary>The armor addons with a given ID in the plugin's masters and the plugin itself, other than in
    /// <paramref name="owner"/>: where a FormID aimed at the wrong master likely meant to point.</summary>
    List<string> SameIdAddons(PluginSide side, string owner, uint id, Func<string, string> label)
    {
        var list = new List<string>();
        foreach (var m in side.Masters.Append(side.Name))
        {
            if (m.Equals(owner, StringComparison.OrdinalIgnoreCase)) continue;
            var engine = m.Equals(side.Name, StringComparison.OrdinalIgnoreCase) ? side.Engine : _s.Compare.EngineFor(side, m);
            if (ReportSession.In(engine, m, m, id) is { } hit && hit.Signature == "ARMA")
                list.Add(label(ReportSession.Canon($"{id:X6}:{m}")));
        }
        return list;
    }

    public AssetsAnswer Assets(string plugin, AssetsOptions? options = null)
    {
        options ??= new AssetsOptions();
        return _s.Run(scope =>
        {
            var side = _s.Side(plugin);
            var p = side.Plugin;
            var own = ArchiveReport.Resolve(_s, side.InData ? side.Name : side.Path, options.Beside);
            var finder = new FileFinder(_s, side.Masters, own);
            var store = _s.Archives;
            var label = _s.Compare.Labeller(side);
            var answer = new AssetsAnswer
            {
                Plugin = side.Shown, Records = p.Segment?.Count ?? 0, TypedFields = _walker.TypedFields.Count, TypedPlaces = _places.Values.Sum(), TypedRecordTypes = _places.Count,
                ByValueExtensions = ByValueExtensions,
            };
            foreach (var a in own.Archives) answer.OwnArchives.Add(a.Name);
            foreach (var n in own.Notes) answer.Notes.Add(n);
            ReportSession.AddSource(answer, side);
            foreach (var line in own.SourceLines($"archives of {side.Name}")) answer.Sources.Add(line);
            answer.Sources.Add(finder.SourceLine());
            answer.Sources.Add(_s.Compare.LoadOrderLine());

            if (p.Segment == null || p.Problems.MissingMasters.Count > 0 || p.Problems.ReadError != null)
            {
                ReportSession.AddProblemNotes(answer, p);
                answer.EmptyReason = p.Problems.MissingMasters.Count > 0
                    ? $"{side.Shown} can't be read without its missing master{(p.Problems.MissingMasters.Count == 1 ? "" : "s")} ({string.Join(", ", p.Problems.MissingMasters)}): put {(p.Problems.MissingMasters.Count == 1 ? "it" : "them")} back, then ask again."
                    : $"{side.Shown} couldn't be read: {p.Problems.ReadError ?? "it has no header index"}.";
                return answer;
            }
            var seg = side.Segment;

            (AssetStatus Status, FileOrigin Origin, string? Where, string? As) Locate(PathFound found)
            {
                string value = found.Value;
                if (value.Contains("$(", StringComparison.Ordinal) || PathWalker.ExtensionOf(value).Length == 0) return (AssetStatus.NotChecked, FileOrigin.None, null, null);
                var hit = finder.Find(Candidates(value));
                if (hit.Origin == FileOrigin.None) return (PathWalker.ExtensionOf(value) == ".mat" ? AssetStatus.NoFile : AssetStatus.Missing, FileOrigin.None, null, null);
                return (hit.Origin == FileOrigin.NotLoaded ? AssetStatus.NotLoaded : AssetStatus.Found, hit.Origin, hit.Where, hit.FoundAs);
            }

            List<AssetRow> Paths(RecordView view, string record, string formKey, string signature, string? via = null, string? viaKey = null, string? viaPlugin = null, IReadOnlyList<string>? usedBy = null)
            {
                var rows = new List<AssetRow>();
                _walker.Walk(view.Type, view.Json, found =>
                {
                    var (status, origin, where, foundAs) = Locate(found);
                    rows.Add(new AssetRow
                    {
                        Record = record, FormKey = formKey, Signature = signature, Field = found.Path, Path = found.Value, Typed = found.Typed,
                        Via = via, ViaFormKey = viaKey, ViaPlugin = viaPlugin, UsedBy = usedBy ?? Array.Empty<string>(),
                        Status = status, Origin = origin, Where = where, FoundAs = foundAs,
                    });
                });
                answer.Rows.AddRange(rows);
                return rows;
            }

            // A master's armor addon: the records of the plugin that use it, in file order. Its paths are listed once.
            var masterAddons = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var wrongSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            scope.Touch(side.Engine, side.Name);
            for (int i = 0; i < seg.FirstUnresolved; i++)
            {
                var e = seg[i];
                string sig = seg.SignatureOf(e);
                if (!options.World && ReportSession.IsWorld(seg, e)) { answer.WorldLeftOut[sig] = answer.WorldLeftOut.GetValueOrDefault(sig) + 1; continue; }
                // The navmesh info map names no files and is very large
                if (sig == "NAVI") continue;
                var hit = new Hit(side.Engine, new RecordRef(p.Id, i));
                RecordView view;
                try { view = _s.Compare.View(side.Engine, hit.Ref); }
                catch (CompareException) { answer.Unreadable++; continue; }
                answer.RecordsRead++;
                string key = ReportSession.Canon(view.Info.FormKey);
                string name = label(key);
                var rows = Paths(view, name, key, sig);
                if (rows.Count > 0) answer.RecordsWithPaths++;

                // A path that isn't found on an override: does the master's version of the record name it too?
                if (rows.Any(r => r.IsProblem) && HeaderIndex.TryParseFormKey(key, out var ownerName, out var localId)
                    && !ownerName.Equals(side.Name, StringComparison.OrdinalIgnoreCase) && _s.Seen(side, ownerName, localId, mastersOnly: true) is { } before)
                {
                    try
                    {
                        var theirs = _s.View(scope, before);
                        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _walker.Walk(theirs.Type, theirs.Json, f => named.Add(ArchiveStore.DataRelative(f.Value)));
                        foreach (var r in rows.Where(r => r.IsProblem))
                            if (named.Contains(ArchiveStore.DataRelative(r.Path))) r.InheritedFrom = before.Plugin.Name;
                    }
                    catch (ReportException) { }
                }

                // An armor's addons. The schema says which links name an armor addon; the header index says what the
                // named record really is. One of another type is the plugin's problem (most often a FormID aimed at
                // the wrong master). One in a master is what the armor shows: read once, its paths listed once.
                _links.Walk(view.Type, view.Json, link =>
                {
                    if (link.LinkTo != "ArmorAddon" || !HeaderIndex.TryParseFormKey(link.FormKey, out var owner, out var id)) return;
                    var mine = ReportSession.In(side.Engine, side.Name, owner, id);
                    // A link to a record that neither the plugin nor a master holds: check's broken links say so
                    if ((mine ?? _s.Seen(side, owner, id, mastersOnly: true)) is not { } target) return;
                    string addonKey = ReportSession.Canon(link.FormKey);
                    if (target.Signature != "ARMA")
                    {
                        if (!wrongSeen.Add(key + ">" + link.Path)) return;
                        answer.WrongLinks.Add(new AssetLinkProblem
                        {
                            Record = name, FormKey = key, Signature = sig, Field = link.Path,
                            Target = label(addonKey), TargetFormKey = addonKey, TargetSignature = target.Signature, TargetReadFrom = target.Plugin.Name,
                            InMaster = mine == null, SameIdAddons = SameIdAddons(side, owner, id, label),
                        });
                        return;
                    }
                    // The plugin's own addon: its paths are listed as its own record's
                    if (mine != null) return;
                    if (masterAddons.TryGetValue(addonKey, out var users))
                    {
                        if (!users.Contains(name)) users.Add(name);
                        return;
                    }
                    users = new List<string> { name };
                    masterAddons[addonKey] = users;
                    RecordView av;
                    try { av = _s.View(scope, target); } catch (ReportException) { return; }
                    // The addon is the master's record: what it names is the master's to bring
                    foreach (var r in Paths(av, name, key, sig, $"{label(addonKey)} ARMA, read from {target.Plugin.Name}", addonKey, target.Plugin.Name, users))
                        if (r.IsProblem) r.InheritedFrom = target.Plugin.Name;
                });
            }

            int left = answer.WorldLeftOut.Values.Sum();
            if (left > 0)
                answer.Notes.Add(SFSearch.Records.WorldData.LeftOut("was left out", left, -1, 0, null,
                    string.Join(", ", answer.WorldLeftOut.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {N(k.Value)}")) + (answer.WorldLeftOut.Count > 6 ? ", …" : "")) +
                    " Placed references name no files; cells and worldspaces can name a material.");
            if (answer.Unreadable > 0) answer.Notes.Add($"{Plural(answer.Unreadable, "record")} couldn't be read and {(answer.Unreadable == 1 ? "was" : "were")} left out. check says which.");
            int byValueMissing = answer.Rows.Count(r => r.Status == AssetStatus.Missing && !r.Typed);
            if (byValueMissing > 0)
                answer.Notes.Add($"{Plural(byValueMissing, "missing path")} {(byValueMissing == 1 ? "was" : "were")} found by value, in fields the schema calls plain text. Such a text may not be a file name as written (a body part's \"BASE Meshes\\…\" is one): judge by the field.");
            int noFile = answer.Count(AssetStatus.NoFile);
            if (noFile > 0)
                answer.Notes.Add($"{Plural(noFile, "material path")} ({Plural(answer.Rows.Where(r => r.Status == AssetStatus.NoFile).Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "material")}) {(noFile == 1 ? "names" : "name")} no file in Data. The game's own materials, and some mods', are compiled into a material database (materialsbeta.cdb), which isn't read: such a material can be there or missing.");
            int other = answer.Count(FileOrigin.OtherLoaded);
            if (other > 0)
                answer.Notes.Add($"{Plural(other, "path")} {(other == 1 ? "is" : "are")} found only in archives of plugins that aren't masters of {side.Name} ({string.Join(", ", answer.Rows.Where(r => r.Origin == FileOrigin.OtherLoaded).Select(r => r.Where!).Distinct(StringComparer.OrdinalIgnoreCase).Take(5))}). They are there while those plugins stay installed and loaded.");
            int via = answer.Rows.Count(r => r.Via != null);
            if (via > 0)
            {
                int users = masterAddons.Values.SelectMany(u => u).Distinct(StringComparer.Ordinal).Count();
                answer.Notes.Add($"{Plural(via, "path")} {(via == 1 ? "is" : "are")} on {Plural(masterAddons.Count, "armor addon")} of masters that {Plural(users, "record")} of the plugin use{(users == 1 ? "s" : "")}. Each addon's paths are listed once, with the records that use it; what is missing there is the master's to bring.");
            }
            if (answer.WrongLinks.Count > 0)
                answer.Notes.Add($"{Plural(answer.WrongLinks.Count, "armor addon link")} {(answer.WrongLinks.Count == 1 ? "names" : "name")} a record that isn't an armor addon ({string.Join(", ", answer.WrongLinks.GroupBy(w => w.TargetSignature).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}): the game shows no model there. Each is listed first." +
                    (answer.WrongLinks.Any(w => w.InMaster && w.SameIdAddons.Count > 0) ? $" {answer.WrongLinks.Count(w => w.InMaster && w.SameIdAddons.Count > 0)} of them have an armor addon with the same ID in another master: the plugin was likely built for an older version of a master, whose FormIDs read differently now." : ""));
            if (answer.Rows.Count == 0)
                answer.EmptyReason = $"No record of {side.Shown} names a file: {N(answer.RecordsRead)} records read, none with a path in a field the schema types as a file path or a text ending in {string.Join(", ", ByValueExtensions)}.";
            ReportSession.AddProblemNotes(answer, p);
            answer.Read.Add($"{N(answer.RecordsRead)} of {N(answer.Records)} records of {p.Name}");
            answer.Read.Add($"the file names of {Plural(store.NamesUsed, "archive")}{(store.ListsRead < store.NamesUsed ? $" ({N(store.NamesUsed - store.ListsRead)} from memory)" : "")}");
            return answer;
        });
    }
}
