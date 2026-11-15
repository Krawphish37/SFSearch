// The GRUP headers of plugins (GAPS S40): each group's label (a record type, a record's FormID, a block number or a
// grid), its group type, stamp, version, the Unknown at bytes 20-23 (Mutagen's group Unknown) and its size, read from
// the file's bytes. Records are stepped over, never read. For a set of plugins: each group type's counts, the values its
// Unknown takes and the plugins that set it; for one plugin (or a type, or one group): every group, one row each.
using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json.Nodes;
using SFSearch.Index;

namespace SFSearch.Reports;

public sealed class GroupsOptions
{
    /// <summary>The plugins (<see cref="PluginSet.Parse"/>). Default: @loaded.</summary>
    public string? In { get; init; }
    /// <summary>Only the top group of this record type (its signature) and the groups inside it.</summary>
    public string? Type { get; init; }
    /// <summary>One group, by its position in the file (0x2A3F0 or a decimal number), with the groups right inside it.</summary>
    public string? Group { get; init; }
    /// <summary>One row per group even when several plugins are read.</summary>
    public bool List { get; init; }
}

/// <summary>One GRUP header.</summary>
public sealed class GroupRow
{
    public required string Plugin { get; init; }
    public long Offset { get; init; }
    public int Depth { get; init; }
    public uint Label { get; init; }
    public int GroupType { get; init; }
    public ushort Stamp { get; init; }
    public ushort Version { get; init; }
    public uint Unknown { get; init; }
    public uint Size { get; init; }
    /// <summary>The record type of the top group it is in.</summary>
    public string Top { get; init; } = "";
    public int Records { get; set; }
    public int Groups { get; set; }
    /// <summary>The label in words: the record type, the record, the block or the grid.</summary>
    public string LabelText { get; set; } = "";
}

/// <summary>A group type's counts over the plugins read.</summary>
public sealed class GroupTypeStats
{
    public int GroupType;
    public long N, NonZero, StampZero, VersionZero, UnknownIsStamp;
    public readonly Dictionary<uint, long> Values = new();
    public readonly Dictionary<string, long> Plugins = new(StringComparer.OrdinalIgnoreCase), PluginsNonZero = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<ushort, long> Versions = new();
}

public sealed class GroupsReport(ReportSession session)
{
    /// <summary>The group types as Mutagen names them (Starfield's GroupTypeEnum).</summary>
    public static string TypeName(int t) => Enum.IsDefined(typeof(Mutagen.Bethesda.Starfield.GroupTypeEnum), t) ? ((Mutagen.Bethesda.Starfield.GroupTypeEnum)t).ToString() : "type " + t.ToString(CultureInfo.InvariantCulture);

    /// <summary>Group types whose label is a record's FormID (the world, cell, topic or quest the group belongs to).</summary>
    public static bool LabelIsRecord(int t) => t is 1 or 6 or 7 or 8 or 9 or 10;

    const int Head = 24;
    const uint Grup = 0x50555247;

    public GroupsAnswer Groups(GroupsOptions o) => session.Run(scope =>
    {
        var set = PluginSet.Parse(session.Index, o.In, "@loaded");
        if (set.WinnersOnly) throw new ReportException("@winners picks records, and a group isn't a record", "ask for @loaded (every loaded plugin) or name the plugins");
        long? only = null;
        if (o.Group is { Length: > 0 } g)
        {
            string t = g.Trim();
            bool hex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!long.TryParse(hex ? t[2..] : t, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out long at))
                throw new ReportException($"\"{g}\" isn't a group's position", "give the position in the file the list shows, as 0x2A3F0 or a decimal number");
            if (set.Plugins.Count != 1) throw new ReportException("a group's position belongs to one plugin, and several were named", "name one plugin with in");
            only = at;
        }
        string? type = o.Type?.Trim().ToUpperInvariant();
        if (type != null && type.Length != 4) throw new ReportException($"\"{o.Type}\" isn't a record type's signature", "give the 4 letters of the top group: CELL, WRLD, DIAL, QUST, WEAP");
        bool list = o.List || set.Plugins.Count == 1 || only != null;
        var answer = new GroupsAnswer { Set = set, Type = type, Only = only, Listed = list };
        foreach (var p in set.Plugins)
        {
            if (p.Segment == null && p.Problems.ReadError != null) { answer.Notes.Add($"{p.Name} can't be read ({p.Problems.ReadError}): left out."); continue; }
            ReportSession.AddSource(answer, p);
            var rows = Walk(p);
            answer.PluginsRead++;
            if (type != null) rows = rows.Where(r => r.Top == type).ToList();
            if (only is { } at)
            {
                var one = rows.FirstOrDefault(r => r.Offset == at) ?? throw new ReportException($"{p.Name} has no group at 0x{at:X}", "take a position from the list of its groups (profile --groups --in <plugin>)");
                rows = rows.Where(r => r.Offset == at || r.Offset > at && r.Offset < at + one.Size && r.Depth == one.Depth + 1).ToList();
            }
            foreach (var r in rows)
            {
                var s = answer.ByType.TryGetValue(r.GroupType, out var have) ? have : answer.ByType[r.GroupType] = new GroupTypeStats { GroupType = r.GroupType };
                s.N++;
                s.Plugins[p.Name] = s.Plugins.GetValueOrDefault(p.Name) + 1;
                if (r.Unknown != 0) { s.NonZero++; s.PluginsNonZero[p.Name] = s.PluginsNonZero.GetValueOrDefault(p.Name) + 1; }
                s.Values[r.Unknown] = s.Values.GetValueOrDefault(r.Unknown) + 1;
                if (r.Stamp == 0) s.StampZero++;
                if (r.Version == 0) s.VersionZero++;
                if (r.Unknown == r.Stamp) s.UnknownIsStamp++;
                s.Versions[r.Version] = s.Versions.GetValueOrDefault(r.Version) + 1;
            }
            if (list)
            {
                foreach (var r in rows) r.LabelText = Label(p, r);
                answer.Rows.AddRange(rows);
            }
        }
        answer.Read.Add($"the group headers of {Plural(answer.PluginsRead, "plugin")} (records stepped over, not read)");
        return answer;
    });

    static string Plural(long n, string one) => ReportAnswer.Plural(n, one);

    /// <summary>Every GRUP header of a plugin, in file order, with the records and groups right inside each.</summary>
    internal static unsafe List<GroupRow> Walk(IndexedPlugin p)
    {
        var rows = new List<GroupRow>();
        long length = new FileInfo(p.Path).Length;
        if (length < Head) return rows;
        using var stream = new FileStream(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        using var view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            byte* b = basePtr + view.PointerOffset;
            long pos = Head + *(uint*)(b + 4);
            var stack = new List<(long End, GroupRow Row)>();
            while (pos + Head <= length)
            {
                while (stack.Count > 0 && pos >= stack[^1].End) stack.RemoveAt(stack.Count - 1);
                long limit = stack.Count > 0 ? stack[^1].End : length;
                byte* h = b + pos;
                uint sig = *(uint*)h, size = *(uint*)(h + 4);
                if (sig == Grup)
                {
                    if (size < Head || pos + size > limit) throw new ReportException($"{p.Name} has a group of size {size} at 0x{pos:X} that doesn't fit its place", "the file may be damaged; check2 reads it");
                    int gtype = *(int*)(h + 12);
                    uint label = *(uint*)(h + 8);
                    var row = new GroupRow
                    {
                        Plugin = p.Name, Offset = pos, Depth = stack.Count, Label = label, GroupType = gtype,
                        Stamp = *(ushort*)(h + 16), Version = *(ushort*)(h + 18), Unknown = *(uint*)(h + 20), Size = size,
                        Top = stack.Count > 0 ? stack[0].Row.Top : gtype == 0 ? Encoding.ASCII.GetString(h + 8, 4) : "",
                    };
                    if (stack.Count > 0) stack[^1].Row.Groups++;
                    rows.Add(row);
                    stack.Add((pos + size, row));
                    pos += Head;
                    continue;
                }
                if (pos + Head + size > limit) throw new ReportException($"{p.Name} has a record of size {size} at 0x{pos:X} that runs past its group", "the file may be damaged; check2 reads it");
                if (stack.Count > 0) stack[^1].Row.Records++;
                pos += Head + size;
            }
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        return rows;
    }

    /// <summary>A group's label in words.</summary>
    string Label(IndexedPlugin p, GroupRow r)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, r.Label);
        switch (r.GroupType)
        {
            case 0: return Encoding.ASCII.GetString(b);
            case 2 or 3: return $"block {unchecked((int)r.Label)}";
            case 4 or 5: return $"grid {BinaryPrimitives.ReadInt16LittleEndian(b[2..])}, {BinaryPrimitives.ReadInt16LittleEndian(b)} (x, y)";
        }
        if (!LabelIsRecord(r.GroupType)) return $"0x{r.Label:X8}";
        if (r.Label == 0) return "00000000 (null)";
        if (!p.FormIds.TryResolve(r.Label, out string owner, out uint local)) return $"{r.Label:X8} (a master is missing)";
        string key = $"{local:X6}:{owner}";
        string? edid = null;
        var v = session.Index.Versions(owner, local);
        if (v != null)
        {
            int mine = -1;
            for (int i = 0; i < v.Holders.Count; i++) if (v.Holders[i].Plugin == p.Id) { mine = i; break; }
            var info = session.Index.Describe(v.Holders[mine >= 0 ? mine : 0]);
            edid = info.EditorId;
            return $"{edid ?? "[no EditorID]"} ({key}) {info.Signature}";
        }
        return $"[no such record] ({key})";
    }
}

public sealed class GroupsAnswer : ReportAnswer
{
    public override string Kind => "groups";
    public required PluginSet Set { get; init; }
    public string? Type { get; init; }
    public long? Only { get; init; }
    public bool Listed { get; init; }
    public int PluginsRead { get; set; }
    public SortedDictionary<int, GroupTypeStats> ByType { get; } = new();
    public List<GroupRow> Rows { get; } = new();
    /// <summary>Rows the structured form holds at most; the text pages through all of them.</summary>
    public const int JsonRows = 5000;

    static string Pct(long part, long whole) => whole == 0 ? "0%" : (100.0 * part / whole).ToString(part == whole || part == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + "%";

    protected override void Render(ReportBody body, ReportTextOptions options)
    {
        long n = ByType.Values.Sum(s => s.N);
        string scope = $"{Set.Shown}{(Type != null ? $", the {Type} top group" : "")}{(Only is { } at ? $", the group at 0x{at:X} and the groups right inside it" : "")}";
        body.Head.Add($"GRUP headers of {scope}: {Plural(n, "group")} in {Plural(PluginsRead, "plugin")}; the Unknown (bytes 20-23) is set in {N(ByType.Values.Sum(s => s.NonZero))}.");
        body.Head.Add("");
        body.Head.Add("| Group type | Groups | Plugins | Unknown set | Unknown values | Plugins that set it | Stamp 0 | Version 0 |");
        body.Head.Add("|---|---|---|---|---|---|---|---|");
        foreach (var s in ByType.Values)
        {
            var values = s.Values.OrderByDescending(v => v.Value).ThenBy(v => v.Key).Take(6).Select(v => $"{v.Key} ×{N(v.Value)}");
            var setters = s.PluginsNonZero.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Take(options.Detail ? 40 : 4).Select(v => $"{v.Key} {N(v.Value)}").ToList();
            if (s.PluginsNonZero.Count > setters.Count) setters.Add($"… {s.PluginsNonZero.Count - setters.Count} more");
            body.Head.Add($"| {s.GroupType} {GroupsReport.TypeName(s.GroupType)} | {N(s.N)} | {N(s.Plugins.Count)} | {N(s.NonZero)} ({Pct(s.NonZero, s.N)}) | {string.Join("; ", values)}{(s.Values.Count > 6 ? $"; … {s.Values.Count - 6} more" : "")} | {Cell(string.Join("; ", setters))} | {N(s.StampZero)} | {N(s.VersionZero)} |");
        }
        if (!Listed)
        {
            body.Head.Add("");
            body.Head.Add("Name one plugin (or add list) for one row per group.");
            return;
        }
        body.Head.Add("");
        body.Head.Add("| Position | Depth | Group type | Label | Stamp | Version | Unknown | Size | Records | Groups |");
        body.Head.Add("|---|---|---|---|---|---|---|---|---|---|");
        body.RowNoun = "groups";
        bool many = Set.Plugins.Count > 1;
        foreach (var r in Rows)
            body.Rows.Add($"| {(many ? r.Plugin + " " : "")}0x{r.Offset:X} | {r.Depth} | {r.GroupType} {GroupsReport.TypeName(r.GroupType)} | {Cell(r.LabelText)} | {r.Stamp} | {r.Version} | {r.Unknown}{(r.Unknown != 0 ? $" (0x{r.Unknown:X8})" : "")} | {N(r.Size)} | {N(r.Records)} | {N(r.Groups)} |");
        if (Rows.Count == 0) body.Empty = Type != null ? $"No plugin read has a {Type} top group." : "The plugins read have no groups.";
    }

    protected override void Fill(JsonObject json)
    {
        json["plugins"] = Set.Shown;
        json["pluginsRead"] = PluginsRead;
        if (Type != null) json["type"] = Type;
        if (Only is { } at) json["group"] = at;
        json["groupTypes"] = new JsonArray(ByType.Values.Select(s => (JsonNode)new JsonObject
        {
            ["groupType"] = s.GroupType, ["name"] = GroupsReport.TypeName(s.GroupType), ["groups"] = s.N, ["plugins"] = s.Plugins.Count,
            ["unknownSet"] = s.NonZero, ["stampZero"] = s.StampZero, ["versionZero"] = s.VersionZero, ["unknownIsStamp"] = s.UnknownIsStamp,
            ["unknownValues"] = new JsonArray(s.Values.OrderByDescending(v => v.Value).ThenBy(v => v.Key).Take(30).Select(v => (JsonNode)new JsonArray(v.Key, v.Value)).ToArray()),
            ["distinctUnknown"] = s.Values.Count,
            ["pluginsThatSetIt"] = new JsonObject(s.PluginsNonZero.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v => KeyValuePair.Create(v.Key, (JsonNode?)v.Value))),
        }).ToArray());
        if (Listed)
        {
            json["groupsTotal"] = Rows.Count;
            json["groups"] = new JsonArray(Rows.Take(JsonRows).Select(r => (JsonNode)new JsonObject
            {
                ["plugin"] = r.Plugin, ["offset"] = r.Offset, ["depth"] = r.Depth, ["groupType"] = r.GroupType, ["name"] = GroupsReport.TypeName(r.GroupType),
                ["label"] = r.Label, ["labelText"] = r.LabelText, ["top"] = r.Top, ["stamp"] = r.Stamp, ["version"] = r.Version, ["unknown"] = r.Unknown,
                ["size"] = r.Size, ["records"] = r.Records, ["groups"] = r.Groups,
            }).ToArray());
            if (Rows.Count > JsonRows) json["groupsCut"] = $"the first {JsonRows:N0} of {Rows.Count:N0} groups; narrow with type or group, or page through the text form";
        }
    }
}
