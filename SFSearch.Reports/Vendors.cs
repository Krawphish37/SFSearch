// Vendors the way the game finds them. A faction with vendor data names its merchant container
// (Faction.MerchantContainer, VENC): a placed chest. What that chest's base container holds is the faction's stock,
// and an NPC in the faction (Npc.Factions) sells it. sources reads this once per run, from the factions of the
// searched plugins and their masters, and only when it first meets a container, or a master's list that nothing
// searched holds, or a list a script adds to.
using System.Diagnostics;
using System.Text.Json.Nodes;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

/// <summary>One vendor: a faction with vendor data and the chest it sells from.</summary>
public sealed class VendorChest
{
    /// <summary>The faction (FormKey).</summary>
    public required string Faction { get; init; }
    /// <summary>The faction's merchant container: a placed chest (FormKey).</summary>
    public required string Chest { get; init; }
    /// <summary>The chest's base container (FormKey).</summary>
    public required string Container { get; init; }
    /// <summary>The cell the chest is placed in (FormKey), when the header index knows it.</summary>
    public string? Cell { get; init; }
    /// <summary>The faction's Vendor flag (DATA 0x4000).</summary>
    public bool VendorFlag { get; init; }
    /// <summary>The NPCs in the faction (FormKeys), filled when first asked for.</summary>
    public List<string> Npcs { get; } = new();

    public JsonObject ToJson() => new()
    {
        ["faction"] = Faction, ["chest"] = Chest, ["container"] = Container, ["cell"] = Cell, ["vendorFlag"] = VendorFlag,
        ["npcs"] = new JsonArray(Npcs.Select(n => (JsonNode)n).ToArray()),
    };
}

/// <summary>The vendors of a set of plugins: which container each faction's chest is, and which lists those containers hold.</summary>
public sealed class VendorIndex
{
    readonly ReportSession _s;
    readonly ReadScope _scope;
    /// <summary>The plugins read, in load order (masters first): a later plugin's version of a record wins.</summary>
    readonly List<(RecordEngine Engine, IndexedPlugin Plugin)> _plugins;
    readonly LinkWalker _walker;
    List<VendorChest>? _all;
    Dictionary<string, List<VendorChest>>? _byContainer;
    Dictionary<string, List<(VendorChest Vendor, List<string> Path)>>? _byList;
    bool _npcsRead;
    int _factions, _factionsRead, _chestsRead, _listsRead, _npcsRead2, _npcsSieved, _unread;
    double _seconds;

    /// <summary>How many lists deep a container's stock is followed.</summary>
    public int Depth { get; init; } = 6;

    public VendorIndex(ReportSession session, ReadScope scope, IEnumerable<(RecordEngine Engine, IndexedPlugin Plugin)> plugins)
    {
        _s = session; _scope = scope; _walker = new LinkWalker(session.Schema);
        _plugins = plugins.ToList();
    }

    /// <summary>
    /// The plugins a sources run reads vendors from: the searched plugins and each one's masters, in load order.
    /// A master is read through the Data folder's engine when it is the file in Data, else through the searched plugin's own.
    /// </summary>
    public static List<(RecordEngine Engine, IndexedPlugin Plugin)> PluginsFor(ReportSession s, IReadOnlyList<(RecordEngine Engine, IndexedPlugin Plugin)> searched)
    {
        var list = new List<(RecordEngine Engine, IndexedPlugin Plugin, long Rank)>();
        void Add(RecordEngine e, IndexedPlugin p, long rank)
        {
            if (list.Any(x => x.Plugin.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) return;
            list.Add((e, p, rank));
        }
        long RankOf(RecordEngine e, IndexedPlugin p) => e == s.Engine && p.Order.Enabled ? p.Order.Position : long.MaxValue;
        foreach (var (engine, p) in searched)
            foreach (string m in p.Header?.Masters ?? (IReadOnlyList<string>)Array.Empty<string>())
            {
                var mine = engine.Index.Plugin(m); var data = s.Index.Plugin(m);
                if (data != null && (mine == null || string.Equals(mine.Path, data.Path, StringComparison.OrdinalIgnoreCase))) Add(s.Engine, data, RankOf(s.Engine, data));
                else if (mine != null) Add(engine, mine, RankOf(engine, mine));
            }
        foreach (var (engine, p) in searched) Add(engine, p, RankOf(engine, p));
        // Masters before the plugins that need them; a plugin outside the load order after every loaded one, in the order given
        return list.Select((x, i) => (x, i)).OrderBy(t => t.x.Rank).ThenBy(t => t.i).Select(t => (t.x.Engine, t.x.Plugin)).ToList();
    }

    /// <summary>What reading the vendors cost, for the answer's notes. Null while nothing was read.</summary>
    public string? Cost => _all == null ? null :
        $"{_factionsRead:N0} of {_factions:N0} factions of {_plugins.Count:N0} plugins had a merchant container: {_all.Count:N0} vendor chests on {_byContainer!.Count:N0} containers" +
        (_byList != null ? $", {_listsRead:N0} containers and lists read for their stock" : "") +
        (_npcsRead ? $", {_npcsSieved:N0} NPCs looked through for a vendor faction's FormID and {_npcsRead2:N0} of them read" : "") +
        (_unread > 0 ? $", {_unread:N0} records couldn't be read" : "") + $" ({_seconds:F1} s)";

    /// <summary>The vendors whose chest is a placed copy of this container.</summary>
    public IReadOnlyList<VendorChest> OfContainer(string formKey)
    {
        Build();
        return _byContainer!.TryGetValue(ReportSession.Canon(formKey), out var v) ? v : Array.Empty<VendorChest>();
    }

    /// <summary>The vendors whose container holds this list, directly or through other lists (the path between, from the container inward, not counting the list itself).</summary>
    public IReadOnlyList<(VendorChest Vendor, List<string> Path)> Holding(string listKey)
    {
        BuildLists();
        return _byList!.TryGetValue(ReportSession.Canon(listKey), out var v) ? v : Array.Empty<(VendorChest, List<string>)>();
    }

    /// <summary>Every vendor found, in the order of the factions' FormKeys.</summary>
    public IReadOnlyList<VendorChest> All { get { Build(); return _all!; } }

    /// <summary>A leveled list a vendor's container holds directly, and that vendor: the first by the lists' FormKeys (for tests).</summary>
    public (string List, VendorChest Vendor)? FirstHeldList()
    {
        BuildLists();
        return _byList!.Where(kv => kv.Value.Any(h => h.Path.Count == 0)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => ((string List, VendorChest Vendor)?)(kv.Key, kv.Value.First(h => h.Path.Count == 0).Vendor)).FirstOrDefault();
    }

    /// <summary>The NPCs in a vendor's faction (FormKeys). The NPCs of the plugins are read once, the first time.</summary>
    public IReadOnlyList<string> Npcs(VendorChest v)
    {
        BuildNpcs();
        return v.Npcs;
    }

    // ------------------------------------------------------------------ Reading
    /// <summary>The version of a record the plugins see: the last of them that holds it, else the one the game uses.</summary>
    Hit? Find(string formKey)
    {
        if (!HeaderIndex.TryParseFormKey(formKey, out var owner, out var id)) return null;
        for (int i = _plugins.Count - 1; i >= 0; i--)
            if (ReportSession.In(_plugins[i].Engine, _plugins[i].Plugin.Name, owner, id) is { } hit) return hit;
        return _s.Winner(owner, id);
    }

    RecordView? Read(Hit hit)
    {
        _scope.Touch(hit.Engine, hit.Plugin.Name);
        try { return _s.Compare.View(hit.Engine, hit.Ref); }
        catch (CompareException) { _unread++; return null; }
    }

    /// <summary>Each FormKey of a signature in the plugins, with the version the last of them holds.</summary>
    Dictionary<string, Hit> Winners(string signature)
    {
        var winners = new Dictionary<string, Hit>(StringComparer.OrdinalIgnoreCase);
        foreach (var (engine, p) in _plugins)
        {
            var seg = p.Segment;
            if (seg == null || p.Problems.MissingMasters.Count > 0) continue;
            foreach (int i in seg.EntriesOf(signature))
            {
                if (i >= seg.FirstUnresolved) continue;
                var r = new RecordRef(p.Id, i);
                winners[ReportSession.Canon(engine.Index.Describe(r).FormKey)] = new Hit(engine, r);
            }
        }
        return winners;
    }

    void Build()
    {
        if (_all != null) return;
        var clock = Stopwatch.StartNew();
        _all = new List<VendorChest>();
        _byContainer = new Dictionary<string, List<VendorChest>>(StringComparer.OrdinalIgnoreCase);
        var factions = Winners("FACT");
        _factions = factions.Count;
        foreach (var (factionKey, hit) in factions.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            var view = Read(hit);
            if (view == null || !ReportSession.IsLink(view.Json["MerchantContainer"], out var chestLink)) continue;
            _factionsRead++;
            string chestKey = ReportSession.Canon(chestLink);
            if (Find(chestKey) is not { } chestHit) continue;
            var chest = Read(chestHit);
            _chestsRead++;
            if (chest == null || !ReportSession.IsLink(chest.Json["Base"], out var baseLink)) continue;
            // Flags is a list of names (["Vendor","CanBeOwner"]); one name may come as a plain value
            var flags = view.Json["Flags"] is JsonArray fa ? fa.Select(ReportSession.Str).ToList() : new List<string?> { ReportSession.Str(view.Json["Flags"]) };
            var v = new VendorChest
            {
                Faction = factionKey, Chest = chestKey, Container = ReportSession.Canon(baseLink),
                Cell = chest.Info.Parent == null ? null : ReportSession.Canon(chest.Info.Parent),
                VendorFlag = flags.Any(f => f != null && f.Split(new[] { ',', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries).Contains("Vendor", StringComparer.Ordinal)),
            };
            _all.Add(v);
            if (!_byContainer.TryGetValue(v.Container, out var list)) _byContainer[v.Container] = list = new List<VendorChest>();
            list.Add(v);
        }
        _seconds += clock.Elapsed.TotalSeconds;
    }

    /// <summary>The lists each vendor container holds, followed down through leveled lists to <see cref="Depth"/>.</summary>
    void BuildLists()
    {
        if (_byList != null) return;
        Build();
        var clock = Stopwatch.StartNew();
        _byList = new Dictionary<string, List<(VendorChest, List<string>)>>(StringComparer.OrdinalIgnoreCase);
        var links = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // The leveled lists a container or a list names: read once each
        List<string> ListsIn(string key)
        {
            if (links.TryGetValue(key, out var have)) return have;
            var found = new List<string>();
            links[key] = found;
            if (Find(key) is not { } hit) return found;
            var view = Read(hit);
            if (view == null) return found;
            _listsRead++;
            _walker.Walk(view.Type, view.Json, link =>
            {
                string to = ReportSession.Canon(link.FormKey);
                if (found.Contains(to, StringComparer.OrdinalIgnoreCase)) return;
                if (Find(to) is { } t && t.Signature == "LVLI") found.Add(to);
            });
            return found;
        }
        foreach (var (container, vendors) in _byContainer!)
        {
            // Breadth first, so each list keeps its shortest path from the container
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string Key, List<string> Path)>();
            foreach (var l in ListsIn(container)) if (seen.Add(l)) queue.Enqueue((l, new List<string>()));
            while (queue.Count > 0)
            {
                var (key, path) = queue.Dequeue();
                if (!_byList.TryGetValue(key, out var holders)) _byList[key] = holders = new List<(VendorChest, List<string>)>();
                foreach (var v in vendors) holders.Add((v, path));
                if (path.Count + 1 >= Depth) continue;
                var inner = path.Append(key).ToList();
                foreach (var l in ListsIn(key)) if (seen.Add(l)) queue.Enqueue((l, inner));
            }
        }
        _seconds += clock.Elapsed.TotalSeconds;
    }

    /// <summary>The NPCs of the plugins that are in a vendor faction (Npc.Factions).</summary>
    void BuildNpcs()
    {
        if (_npcsRead) return;
        Build();
        _npcsRead = true;
        if (_all!.Count == 0) return;
        var clock = Stopwatch.StartNew();
        var byFaction = _all.GroupBy(v => v.Faction, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        // Only an NPC whose bytes hold a vendor faction's FormID (as its plugin writes it) is read through Mutagen:
        // the bytes are a quick sieve, the record's Factions list decides
        var rawIds = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        HashSet<uint> RawIds(RecordEngine engine, string plugin)
        {
            if (rawIds.TryGetValue(plugin, out var have)) return have;
            var set = new HashSet<uint>();
            var ids = engine.Sessions.Get(plugin).FormIds;
            foreach (string f in byFaction.Keys)
                if (HeaderIndex.TryParseFormKey(f, out var owner, out var id) && ids.Raw(owner, id) is uint raw) set.Add(raw);
            return rawIds[plugin] = set;
        }
        foreach (var (npcKey, hit) in Winners("NPC_").OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase))
        {
            _scope.Touch(hit.Engine, hit.Plugin.Name);
            var ids = RawIds(hit.Engine, hit.Plugin.Name);
            if (ids.Count == 0) continue;
            try
            {
                var data = hit.Engine.Raw(hit.Ref).Data;
                bool any = false;
                for (int i = 0; i + 4 <= data.Length && !any; i++) any = ids.Contains(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)));
                _npcsSieved++;
                if (!any) continue;
            }
            catch (RecordException) { }
            var view = Read(hit);
            if (view == null) continue;
            _npcsRead2++;
            if (view.Json["Factions"] is not JsonArray factions) continue;
            foreach (var f in factions)
                if (f is JsonObject o && ReportSession.IsLink(o["Faction"], out var fk) && byFaction.TryGetValue(ReportSession.Canon(fk), out var vendors))
                    foreach (var v in vendors) if (!v.Npcs.Contains(npcKey, StringComparer.OrdinalIgnoreCase)) v.Npcs.Add(npcKey);
        }
        _seconds += clock.Elapsed.TotalSeconds;
    }
}
