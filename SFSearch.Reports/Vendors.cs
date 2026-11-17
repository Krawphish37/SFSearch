// Vendors the way the game finds them. A faction with vendor data names its merchant container
// (Faction.MerchantContainer, VENC): a placed chest. What that chest's base container holds is the faction's stock,
// and an NPC in the faction (Npc.Factions) sells it. sources reads this once per run, from the factions of the
// searched plugins and their masters, and only when it first meets a container, or a master's list that nothing
// searched holds, or a list a script adds to.
// The factions' merchant containers and the chests' bases are first read from the files' bytes (the VENC and NAME
// subrecords, all cores); Mutagen then reads only the factions and chests a question needs, and decides (GAPS S70).
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
    /// <summary>Its place among the factions, by their FormKeys.</summary>
    internal int Order { get; init; }

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
    readonly Dictionary<string, List<VendorChest>> _byContainer = new(StringComparer.OrdinalIgnoreCase);
    // The factions with a merchant container and their chests' bases, told from the bytes (see Map)
    List<Candidate>? _candidates;
    Dictionary<string, List<Candidate>>? _byBase;
    readonly List<Candidate> _baseUnknown = new();
    int _withVenc, _chests, _bases;
    // Internal (J81): sfsreport-tests' SourcesTests.FirstHeldList reads it through InternalsVisibleTo
    internal Dictionary<string, List<(VendorChest Vendor, List<string> Path)>>? _byList;
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
    public string? Cost => _candidates == null ? null :
        $"{_withVenc:N0} of {_factions:N0} factions of {_plugins.Count:N0} plugins had a merchant container: {_chests:N0} vendor chests on {_bases:N0} containers (from the files' bytes; {_factionsRead:N0} factions and {_chestsRead:N0} chests read through Mutagen)" +
        (_byList != null ? $", {_listsRead:N0} containers and lists read for their stock" : "") +
        (_npcsRead ? $", {_npcsSieved:N0} NPCs looked through for a vendor faction's FormID and {_npcsRead2:N0} of them read" : "") +
        (_unread > 0 ? $", {_unread:N0} records couldn't be read" : "") + $" ({_seconds:F1} s)";

    /// <summary>The vendors whose chest is a placed copy of this container.</summary>
    public IReadOnlyList<VendorChest> OfContainer(string formKey)
    {
        string key = ReportSession.Canon(formKey);
        Map();
        // Only the factions whose chest's base is this container (by the bytes), and any whose base the bytes didn't tell
        Confirm((_byBase!.TryGetValue(key, out var some) ? some : Enumerable.Empty<Candidate>()).Concat(_baseUnknown));
        return _byContainer.TryGetValue(key, out var v) ? v : Array.Empty<VendorChest>();
    }

    /// <summary>The vendors whose container holds this list, directly or through other lists (the path between, from the container inward, not counting the list itself).</summary>
    public IReadOnlyList<(VendorChest Vendor, List<string> Path)> Holding(string listKey)
    {
        BuildLists();
        return _byList!.TryGetValue(ReportSession.Canon(listKey), out var v) ? v : Array.Empty<(VendorChest, List<string>)>();
    }

    /// <summary>Every vendor found, in the order of the factions' FormKeys.</summary>
    public IReadOnlyList<VendorChest> All { get { Build(); return _all!; } }

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

    /// <summary>A faction with a merchant container, as its bytes tell it, and its chest's base container.</summary>
    sealed class Candidate
    {
        public required int Order { get; init; }
        public required string Faction { get; init; }
        public required Hit FactionHit { get; init; }
        public bool Confirmed { get; set; }
    }

    /// <summary>
    /// The factions with a merchant container (a VENC subrecord) and each chest's base container (its NAME), read from
    /// the files' bytes on all cores; nothing through Mutagen. A faction without VENC has no merchant container (Mutagen
    /// reads MerchantContainer from it), so it is left out; one whose bytes don't tell it plainly is kept, base unknown.
    /// </summary>
    void Map()
    {
        if (_candidates != null) return;
        var clock = Stopwatch.StartNew();
        var factions = Winners("FACT").OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
        _factions = factions.Count;
        var venc = Subrecord(factions.Select(f => f.Value).ToList(), "VENC");
        var candidates = new List<Candidate>();
        var chests = new List<(Candidate Candidate, Hit Chest)>();
        for (int i = 0; i < factions.Count; i++)
        {
            var (told, chestKey) = venc[i];
            if (told && chestKey == null) continue; // no merchant container
            var c = new Candidate { Order = i, Faction = factions[i].Key, FactionHit = factions[i].Value };
            candidates.Add(c);
            if (chestKey == null) { _baseUnknown.Add(c); continue; }
            _withVenc++;
            // No plugin holds the chest: no vendor, as the read through Mutagen finds too
            if (Find(chestKey) is not { } chest) { c.Confirmed = true; continue; }
            chests.Add((c, chest));
        }
        var bases = Subrecord(chests.Select(x => x.Chest).ToList(), "NAME");
        _byBase = new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);
        for (int j = 0; j < chests.Count; j++)
        {
            var c = chests[j].Candidate;
            var (told, baseKey) = bases[j];
            if (!told || baseKey == null) { _baseUnknown.Add(c); continue; }
            _chests++;
            if (!_byBase.TryGetValue(baseKey, out var list)) _byBase[baseKey] = list = new List<Candidate>();
            list.Add(c);
        }
        _bases = _byBase.Count;
        _candidates = candidates;
        _seconds += clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// One subrecord of each record, read from the files' bytes (a plain parallel loop over the plugins): its FormID as
    /// a FormKey, by the FormIDs of the record's plugin. Told true with null: the record holds no such subrecord. Told
    /// false: the record couldn't be read, or holds the subrecord in another form than one FormID that resolves (a null
    /// FormID, two of them, another length); Mutagen then decides. (The shared record pipeline of J29/J35 can take this
    /// loop over when it lands.)
    /// </summary>
    List<(bool Told, string? Link)> Subrecord(List<Hit> hits, string sub)
    {
        var result = new (bool, string?)[hits.Count];
        foreach (var p in hits.Select(h => h.Plugin).Distinct()) _scope.TouchBytes(p);
        var byPlugin = Enumerable.Range(0, hits.Count).GroupBy(i => hits[i].Plugin.Path, StringComparer.OrdinalIgnoreCase).ToList();
        Parallel.ForEach(byPlugin, group =>
        {
            var p = hits[group.First()].Plugin;
            using var file = File.OpenHandle(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (int i in group)
            {
                var e = hits[i].Entry;
                RawRecord raw;
                try { raw = RawRecord.Read(file, e.Offset); }
                catch (Exception ex) when (ex is RecordException or IOException or InvalidDataException or ArgumentException) { result[i] = (false, null); continue; }
                if (raw.StoredSize != e.Size || raw.Signature != hits[i].Signature) { result[i] = (false, null); continue; }
                var found = raw.Subrecords.Where(x => x.Signature == sub).ToList();
                if (found.Count == 0) { result[i] = (true, null); continue; }
                if (found.Count > 1 || found[0].Length != 4) { result[i] = (false, null); continue; }
                uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw.Content(found[0]));
                result[i] = id != 0 && p.FormIds.TryResolve(id, out string owner, out uint local) ? (true, $"{local:X6}:{owner}") : (false, null);
            }
        });
        return result.ToList();
    }

    /// <summary>Reads the factions and chests of these candidates through Mutagen (each once): Mutagen decides each vendor.</summary>
    void Confirm(IEnumerable<Candidate> candidates)
    {
        var todo = candidates.Where(c => !c.Confirmed).Distinct().OrderBy(c => c.Order).ToList();
        if (todo.Count == 0) return;
        var clock = Stopwatch.StartNew();
        foreach (var c in todo)
        {
            c.Confirmed = true;
            var view = Read(c.FactionHit);
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
                Faction = c.Faction, Chest = chestKey, Container = ReportSession.Canon(baseLink), Order = c.Order,
                Cell = chest.Info.Parent == null ? null : ReportSession.Canon(chest.Info.Parent),
                VendorFlag = flags.Any(f => f != null && f.Split(new[] { ',', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries).Contains("Vendor", StringComparer.Ordinal)),
            };
            if (!_byContainer.TryGetValue(v.Container, out var list)) _byContainer[v.Container] = list = new List<VendorChest>();
            list.Add(v);
            // In the order of the factions' FormKeys, as when every faction was read in turn
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        _seconds += clock.Elapsed.TotalSeconds;
    }

    /// <summary>Every vendor, read through Mutagen: the vendors' stock and NPCs need them all.</summary>
    void Build()
    {
        if (_all != null) return;
        Map();
        Confirm(_candidates!);
        _all = _byContainer.Values.SelectMany(v => v).OrderBy(v => v.Order).ToList();
    }

    /// <summary>The lists each vendor container holds, followed down through leveled lists to <see cref="Depth"/>.</summary>
    internal void BuildLists()
    {
        if (_byList != null) return;
        Build();
        var clock = Stopwatch.StartNew();
        _byList = new Dictionary<string, List<(VendorChest, List<string>)>>(StringComparer.OrdinalIgnoreCase);
        var links = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // The leveled lists a container or a list names: read once each
        List<string> ListsIn(string key)
        {
            if (!links.ContainsKey(key)) ReadLists(new[] { key });
            return links[key];
        }
        // Reads these containers or lists through Mutagen on all cores (a record engine reads open plugins on many
        // threads), then keeps, in each one's order, the links that name a leveled list
        void ReadLists(IReadOnlyCollection<string> keys)
        {
            var todo = new List<(string Key, Hit Hit)>();
            foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (links.ContainsKey(key)) continue;
                links[key] = new List<string>();
                if (Find(key) is not { } hit) continue;
                _scope.Touch(hit.Engine, hit.Plugin.Name);
                todo.Add((key, hit));
            }
            var targets = new List<string>?[todo.Count];
            try
            {
                Parallel.For(0, todo.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, i =>
                {
                    RecordView view;
                    try { view = _s.Compare.View(todo[i].Hit.Engine, todo[i].Hit.Ref); }
                    catch (CompareException) { return; }
                    var to = new List<string>();
                    _walker.Walk(view.Type, view.Json, link => to.Add(ReportSession.Canon(link.FormKey)));
                    targets[i] = to;
                });
            }
            catch (AggregateException e) when (e.InnerExceptions.Count > 0)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
            for (int i = 0; i < todo.Count; i++)
            {
                if (targets[i] is not { } to) { _unread++; continue; }
                _listsRead++;
                var found = links[todo[i].Key];
                foreach (var t in to)
                    if (!found.Contains(t, StringComparer.OrdinalIgnoreCase) && Find(t) is { Signature: "LVLI" }) found.Add(t);
            }
        }
        // Every container and the lists within Depth of one, read a level at a time: the walk below then reads nothing
        var containers = _byContainer.OrderBy(kv => kv.Value[0].Order).Select(kv => kv.Key).ToList();
        ReadLists(containers);
        var level = containers.SelectMany(c => links[c]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (int d = 0; d + 1 < Depth && level.Count > 0; d++)
        {
            ReadLists(level);
            level = level.SelectMany(l => links[l]).Where(l => !links.ContainsKey(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        foreach (var (container, vendors) in _byContainer.OrderBy(kv => kv.Value[0].Order))
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
