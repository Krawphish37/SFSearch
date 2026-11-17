// The file paths and armor addon links of a record, read from its own bytes where the schema says which subrecord is
// which field: what PathWalker and LinkWalker find in Mutagen's JSON of the record, without Mutagen's read of the whole
// record (GAPS S30). The assets report reads every record this way first; a record this reader can't place a
// subrecord of goes through Mutagen as before, so the answer is the same either way.
//  - A text field is placed when the schema gives it a subrecord of its own that holds the whole text (a model's
//    file, a furniture's marker model, an animation graph component's rig file), its signature names no other field
//    at its level, and the subrecord comes once there: the record's own field, a field of an object in it (a model, a
//    gendered model's male or female part), a field of a component (between its BFCB and BFCE, the kind picked by
//    the BFCB text as Mutagen picks it), or a field of an entry of a simple list (an armor's armatures).
//  - A field whose JSON can't be a text (a number, a flag, a link to anything but an armor addon, bytes) names no path.
//  - Any other subrecord that holds a file extension's text, or that the schema says may hold a file path or an armor
//    addon link, sends the record to Mutagen.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Reports;

/// <summary>An armor addon link found in a record's bytes: where it sits (Armatures[0].ArmorAddon) and the record it names.</summary>
public readonly record struct AddonLink(string Path, string FormKey);

/// <summary>What <see cref="AssetFields.Read"/> found in one record: its file paths in the order Mutagen's JSON gives
/// them, and its armor addon links in file order.</summary>
public sealed class RecordAssets
{
    public List<PathFound> Paths { get; } = new();
    public List<AddonLink> AddonLinks { get; } = new();
}

public sealed class AssetFields
{
    /// <summary>Kinds of field whose value in Mutagen's JSON is never a text that ends in a file extension. A link is
    /// one unless it may name an armor addon.</summary>
    static readonly HashSet<string> Inert = new(StringComparer.Ordinal)
    {
        "Float", "Double", "Half", "Percent", "Int8", "Int16", "Int32", "Int64", "UInt8", "UInt16", "UInt32", "UInt64", "Bool", "Enum",
        "FormLink", "FormLinkOrIndex", "FormKey", "ByteArray", "Color", "Guid", "P2Float", "P2Int16", "P2Int32", "P3Float", "P3Int16",
        "Break", "Marker", "TimeOnly",
    };

    const string Bfcb = "BFCB", Bfce = "BFCE";

    readonly RecordSchema _schema;
    readonly byte[][] _extensions;
    readonly HashSet<string> _extensionSet;
    /// <summary>The plans of each schema, made once per signature and kept for the process (they come from the
    /// schema alone, never from a plugin).</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RecordSchema, ConcurrentDictionary<string, Lazy<Plan?>>> _bySchema = new();
    readonly ConcurrentDictionary<string, Lazy<Plan?>> _plans;

    public AssetFields(RecordSchema schema, IEnumerable<string> extensions)
    {
        _schema = schema;
        _plans = _bySchema.GetValue(schema, _ => new ConcurrentDictionary<string, Lazy<Plan?>>(StringComparer.Ordinal));
        _extensionSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        _extensions = _extensionSet.Select(e => System.Text.Encoding.ASCII.GetBytes(e.ToLowerInvariant())).ToArray();
    }

    // ------------------------------------------------------------------ What the schema says
    enum LeafKind { Text, Link }

    /// <summary>A field the reader reads: a whole-subrecord text, or an armor addon link. In a list entry or a
    /// component, <see cref="Path"/> is the part after the entry (".RigFile").</summary>
    sealed record Leaf(LeafKind Kind, string Path, bool Typed, string Sort);

    /// <summary>The fields of one level: the record's own (with its objects), or one kind of component.</summary>
    sealed class Scope
    {
        /// <summary>Signature → the one field it holds, outside any list.</summary>
        public readonly Dictionary<string, Leaf> Leaves = new(StringComparer.Ordinal);
        /// <summary>Signatures whose every field is of a kind that can't name a file.</summary>
        public readonly HashSet<string> Quiet = new(StringComparer.Ordinal);
        /// <summary>Signatures that may hold a file path or an armor addon link the reader can't place.</summary>
        public readonly HashSet<string> Bearing = new(StringComparer.Ordinal);
        /// <summary>Signature → the simple list it belongs to.</summary>
        public readonly Dictionary<string, FlatList> Lists = new(StringComparer.Ordinal);
    }

    /// <summary>A list whose entries are an object of fields that each take a subrecord of their own, the first always
    /// there: each entry starts at the first field's subrecord.</summary>
    sealed class FlatList
    {
        public required string Path { get; init; }
        public required string Sort { get; init; }
        public required string Trigger { get; init; }
        public required int FieldCount { get; init; }
        /// <summary>Signature → its place among the entry's fields.</summary>
        public required Dictionary<string, int> Orders { get; init; }
        /// <summary>Signature → its place, and its field when the reader reads it.</summary>
        public readonly Dictionary<string, (int Order, Leaf? Leaf)> Members = new(StringComparer.Ordinal);
    }

    sealed class Plan
    {
        public required Scope Top { get; init; }
        /// <summary>The record type's component list ("Components"), or null.</summary>
        public string? ComponentsPath { get; init; }
        public string ComponentsSort { get; init; } = "";
        /// <summary>The component kinds by Mutagen's class name.</summary>
        public Dictionary<string, Scope> Components { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The plan of a record signature, or null when its records always go through Mutagen. A signature with
    /// several Mutagen types (OMOD) has a plan when every type's plan is the same.</summary>
    Plan? PlanFor(string signature) => _plans.GetOrAdd(signature, sig => new Lazy<Plan?>(() => BuildFor(sig))).Value;

    /// <summary>Makes the plans of these signatures now.</summary>
    public void Prepare(IEnumerable<string> signatures) { foreach (var sig in signatures) PlanFor(sig); }

    /// <summary>The plan of a signature. The report's threads (and <see cref="AssetsReport.Prepare"/>) make plans at once:
    /// the schema's model is made whole at load and safe to walk from many threads (GAPS S65).</summary>
    Plan? BuildFor(string sig)
    {
        var types = _schema.TypesOf(sig);
        if (types.Count == 0) return null;
        var plans = types.Select(BuildPlan).ToList();
        if (plans.Any(p => p == null)) return null;
        return plans.Skip(1).All(p => SamePlan(plans[0]!, p!)) ? plans[0] : Merge(plans.Select(p => p!).ToList());
    }

    /// <summary>The plan of several types read under one signature (a GMST's kinds, an OMOD's): a signature is read
    /// only where every type's plan reads it the same way, quiet only where every one calls it so, and one that may
    /// hold a typed path or an armor addon link in any of them sends the record to Mutagen. Lists and components
    /// only when all agree.</summary>
    static Plan? Merge(List<Plan> plans)
    {
        var first = plans[0];
        bool sameComponents = plans.All(p => p.ComponentsPath == first.ComponentsPath && p.ComponentsSort == first.ComponentsSort && p.Components.Count == first.Components.Count
            && p.Components.All(kv => first.Components.TryGetValue(kv.Key, out var o) && SameScope(kv.Value, o)));
        var top = new Scope();
        foreach (var (sig, leaf) in first.Top.Leaves)
            if (plans.All(p => p.Top.Leaves.TryGetValue(sig, out var l) && l == leaf)) top.Leaves[sig] = leaf;
        foreach (var sig in first.Top.Quiet)
            if (plans.All(p => p.Top.Quiet.Contains(sig))) top.Quiet.Add(sig);
        foreach (var p in plans)
        {
            foreach (var sig in p.Top.Bearing) top.Bearing.Add(sig);
            // A field one type reads that another doesn't, or a list: Mutagen decides
            foreach (var sig in p.Top.Leaves.Keys.Concat(p.Top.Lists.Keys))
                if (!top.Leaves.ContainsKey(sig)) top.Bearing.Add(sig);
        }
        var merged = new Plan { Top = top, ComponentsPath = sameComponents ? first.ComponentsPath : null, ComponentsSort = first.ComponentsSort };
        if (sameComponents) foreach (var kv in first.Components) merged.Components[kv.Key] = kv.Value;
        return merged;
    }

    static bool SameScope(Scope a, Scope b) =>
        a.Leaves.Count == b.Leaves.Count && a.Leaves.All(kv => b.Leaves.TryGetValue(kv.Key, out var l) && l == kv.Value)
        && a.Quiet.SetEquals(b.Quiet) && a.Bearing.SetEquals(b.Bearing)
        && a.Lists.Count == b.Lists.Count && a.Lists.All(kv => b.Lists.TryGetValue(kv.Key, out var l) && l.Path == kv.Value.Path && l.Sort == kv.Value.Sort
            && l.Trigger == kv.Value.Trigger && l.Members.Count == kv.Value.Members.Count && l.Members.All(m => kv.Value.Members.TryGetValue(m.Key, out var o) && o == m.Value));

    static bool SamePlan(Plan a, Plan b) =>
        SameScope(a.Top, b.Top) && a.ComponentsPath == b.ComponentsPath && a.ComponentsSort == b.ComponentsSort && a.Components.Count == b.Components.Count
        && a.Components.All(kv => b.Components.TryGetValue(kv.Key, out var o) && SameScope(kv.Value, o));

    Plan? BuildPlan(string type)
    {
        var rows = _schema.Model.Expand(type).ToList();
        // The component list: a list of AComponent at the top of the record
        var compRows = rows.Where(r => r.Def.RefObject == "AComponent" && r.Parent == null && r.Def.Path.EndsWith("[*]", StringComparison.Ordinal)).ToList();
        if (compRows.Count > 1) return null;
        string? compPath = compRows.Count == 1 ? compRows[0].Path[..^3] : null;
        var plan = new Plan { Top = new Scope(), ComponentsPath = compPath, ComponentsSort = compRows.Count == 1 ? compRows[0].Sort : "" };
        var topRows = new List<FieldRow>();
        var byVariant = new Dictionary<string, List<FieldRow>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (compPath != null && r.Path.StartsWith(compPath + "[*]{", StringComparison.Ordinal))
            {
                int close = r.Path.IndexOf('}', compPath.Length + 4);
                string variant = r.Path[(compPath.Length + 4)..close];
                (byVariant.TryGetValue(variant, out var l) ? l : byVariant[variant] = new()).Add(r);
                continue;
            }
            if (compPath != null && r.Path.StartsWith(compPath + "[*]", StringComparison.Ordinal)) continue; // AComponent's own step (BFCB)
            topRows.Add(r);
        }
        Fill(plan.Top, topRows, null);
        foreach (var (variant, list) in byVariant)
        {
            var scope = new Scope();
            Fill(scope, list, compPath + "[*]{" + variant + "}");
            plan.Components[variant] = scope;
        }
        return plan;
    }

    /// <summary>Sorts the rows of one level by signature. <paramref name="prefix"/>: for a component kind, the path of
    /// its rows up to its own fields ("Components[*]{AnimationGraphComponent}").</summary>
    void Fill(Scope scope, List<FieldRow> rows, string? prefix)
    {
        // A field belongs to its own subrecord and to every subrecord hand-written code reads it from
        var bySig = new Dictionary<string, List<FieldRow>>(StringComparer.Ordinal);
        foreach (var r in rows)
            foreach (var sig in r.Def.CustomSubrecordList.Prepend(r.Subrecord).Distinct())
                if (sig != null) (bySig.TryGetValue(sig, out var l) ? l : bySig[sig] = new()).Add(r);
        foreach (var (sig, list) in bySig)
        {
            // Rows that only hold others (an object, a list, a gendered pair) take no bytes of their own
            var leaves = list.Where(r => r.Def.Kind is not ("Object" or "List" or "Gendered")).ToList();
            var containers = list.Where(r => r.Def.Kind is "Object" or "List" or "Gendered").ToList();
            var leaf = leaves.Count == 1 && containers.Count == 0 ? Placeable(leaves[0], prefix) : null;
            var flat = leaves.Count == 1 && containers.Count == 0 && prefix == null ? ListOf(leaves[0]) : null;
            // A hand-written reading step (CustomLogic) is no value; the fields it fills are rows of their own here
            bool quiet = leaves.All(r => (Inert.Contains(r.Def.Kind) || r.Def.Kind == "CustomLogic") && r.Def.LinkTo != "ArmorAddon");
            bool bearing = list.Any(r => r.Def.Kind == "AssetLink" || r.Def.LinkTo == "ArmorAddon");
            if (flat != null && (leaf != null || quiet))
            {
                AddToList(scope, flat, sig, leaf == null ? null : leaf with { Path = leaf.Path[(flat.Path.Length + 3)..] });
                continue;
            }
            if (leaf != null && !leaf.Path.Contains("[*]", StringComparison.Ordinal)) { scope.Leaves[sig] = leaf; continue; }
            if (quiet) { scope.Quiet.Add(sig); continue; }
            // Anything else (a translated text, a text in a list or at an offset, code Mutagen reads by hand) sends
            // the record to Mutagen when the subrecord holds a file extension's text; one that may hold a typed file
            // path or an armor addon link, whenever it is there
            if (bearing || leaf != null && (leaf.Typed || leaf.Kind == LeafKind.Link)) scope.Bearing.Add(sig);
        }
        // A list some of whose fields share a signature with another field isn't simple: Mutagen reads it
        foreach (var l in scope.Lists.Values.Distinct().Where(l => l.Members.Count != l.FieldCount).ToList())
            foreach (var m in l.Members)
            {
                scope.Lists.Remove(m.Key);
                if (m.Value.Leaf != null) scope.Bearing.Add(m.Key); else scope.Quiet.Add(m.Key);
            }
    }

    /// <summary>A whole-subrecord text or an armor addon link, with the path the JSON gives it (for a component kind's
    /// field, the part after the kind: ".RigFile"). Null for any other field.</summary>
    Leaf? Placeable(FieldRow r, string? prefix)
    {
        var d = r.Def;
        if (d.Custom || d.Offset != null || r.Offset != null) return null;
        // Every object on the way down must be read by Mutagen's generated code
        for (var p = r.Parent; p != null; p = p.Parent)
            if (p.Def.Custom || p.Def.RefObject != null && _schema.Model.HandReadOf(p.Def.RefObject) != null && p.Def.RefObject != "AComponent") return null;
        string path = prefix != null ? r.Path[prefix.Length..] : r.Path;
        if (path.Contains('{')) return null; // a field of one kind of a polymorphic object
        if (d.Kind is "AssetLink" or "String")
        {
            if (d.Translated || d.TextForm is not (null or "NullTerminate")) return null;
            return new Leaf(LeafKind.Text, path, d.Kind == "AssetLink", r.Sort);
        }
        if (d.LinkTo == "ArmorAddon" && d.Kind == "FormLink") return new Leaf(LeafKind.Link, path, false, r.Sort);
        return null;
    }

    /// <summary>The simple list a row sits in directly ("Armatures[*].ArmorAddon" in "Armatures"), or null.</summary>
    FlatList? ListOf(FieldRow r)
    {
        var item = r.Parent;
        if (item == null || item.Parent != null || !item.Def.Path.EndsWith("[*]", StringComparison.Ordinal) || item.Def.Kind != "Object") return null;
        if (item.Def.RefObject is not { } obj || item.Def.Custom || _schema.IsPolymorphic(obj) || _schema.Model.HandReadOf(obj) != null) return null;
        // Each field of the entry takes a subrecord of its own; the first is always there and starts the entry
        var fields = _schema.Model.AllFields(obj).Select(f => f.F).ToList();
        if (fields.Count == 0 || fields.Any(f => f.Subrecord == null || f.Custom || f.Offset != null || f.Kind is "Object" or "List" or "Gendered" or "CustomLogic")) return null;
        if (fields.Select(f => f.Subrecord).Distinct(StringComparer.Ordinal).Count() != fields.Count || fields[0].Nullable) return null;
        var orders = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < fields.Count; i++) orders[fields[i].Subrecord!] = i;
        return new FlatList { Path = item.Path[..^3], Sort = item.Sort, Trigger = fields[0].Subrecord!, FieldCount = fields.Count, Orders = orders };
    }

    static void AddToList(Scope scope, FlatList flat, string sig, Leaf? leaf)
    {
        // One FlatList per list, shared by its members
        var have = scope.Lists.Values.FirstOrDefault(l => l.Path == flat.Path) ?? flat;
        if (!have.Orders.TryGetValue(sig, out int order)) return;
        have.Members[sig] = (order, leaf);
        scope.Lists[sig] = have;
    }

    // ------------------------------------------------------------------ Reading
    /// <summary>
    /// The file paths and armor addon links of a record from its bytes, or null when a subrecord can't be placed
    /// without Mutagen (the caller reads the record through Mutagen then).
    /// </summary>
    /// <param name="ids">The FormID resolver of the plugin the record is in.</param>
    public RecordAssets? Read(RawRecord raw, FormIdResolver ids)
    {
        if (raw.Trailing != 0) return null; // bytes after the last subrecord
        var plan = PlanFor(raw.Signature);
        if (plan == null) return null; // the type's fields can't be placed by signature
        var state = new ReadState(this, plan, ids);
        foreach (var s in raw.Subrecords)
            if (!state.Next(s.Signature, raw.Content(s))) return null;
        return state.Finish();
    }

    sealed class ReadState(AssetFields owner, Plan plan, FormIdResolver ids)
    {
        readonly List<(string Sort, int Index, string Within, PathFound Path)> _found = new();
        readonly RecordAssets _result = new();
        readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        readonly HashSet<string> _componentSeen = new(StringComparer.Ordinal);
        readonly HashSet<string> _listsDone = new(StringComparer.Ordinal);
        /// <summary>The bytes aren't enough here: the caller reads the record through Mutagen. The reason is written for
        /// whoever reads this code; nothing keeps it.</summary>
        static bool Fail(string why) => false;

        int _components = -1;           // the component being read, counted from 0
        Scope? _component;              // inside BFCB … BFCE: that kind's fields
        FlatList? _list; int _item = -1, _lastOrder = -1;
        string? _last;

        public bool Next(string sig, ReadOnlySpan<byte> content)
        {
            bool ok = Step(sig, content);
            _last = sig;
            return ok;
        }

        bool Step(string sig, ReadOnlySpan<byte> content)
        {
            // ---- components: one run of BFCB … BFCE blocks
            if (sig == Bfcb)
            {
                if (plan.ComponentsPath == null || _component != null) return Fail("a component where the type has none, or inside another");
                if (_components >= 0 && _last != Bfce) return Fail("a second run of components");
                EndList();
                string? kind = Selectors.ComponentKind(owner._schema.Model, content);
                if (kind == null || !plan.Components.TryGetValue(kind, out var scope)) return Fail($"a component kind the schema doesn't spell out ({kind ?? "unknown to Mutagen"})");
                _components++; _component = scope; _componentSeen.Clear();
                return true;
            }
            if (sig == Bfce)
            {
                if (_component == null) return Fail("BFCE outside a component");
                _component = null;
                return true;
            }
            if (_component != null)
            {
                if (!_componentSeen.Add(sig)) return !Matters(_component, sig, content) || Fail($"{sig} twice in a component");
                return Place(_component, sig, content, plan.ComponentsSort, _components, plan.ComponentsPath);
            }
            // ---- simple lists
            if (plan.Top.Lists.TryGetValue(sig, out var flat))
            {
                var (order, leaf) = flat.Members[sig];
                if (_list != flat)
                {
                    // An entry starts the list; a list met a second time goes to Mutagen
                    if (sig != flat.Trigger || _listsDone.Contains(flat.Path)) return Fail($"{flat.Path} not in one run");
                    EndList(); _list = flat; _item = -1;
                }
                if (sig == flat.Trigger) { _item++; _lastOrder = 0; }
                else if (order <= _lastOrder) return Fail($"{flat.Path}: {sig} out of order");
                else _lastOrder = order;
                return leaf == null || Emit(leaf, content, flat.Sort, _item, flat.Path);
            }
            EndList();
            // ---- the record's own fields
            if (!_seen.Add(sig)) return !Matters(plan.Top, sig, content) || Fail($"{sig} twice");
            return Place(plan.Top, sig, content, "", -1, null);
        }

        void EndList()
        {
            if (_list != null) _listsDone.Add(_list.Path);
            _list = null;
        }

        // A subrecord of a level: read its field, or check that it can't name a file
        bool Place(Scope scope, string sig, ReadOnlySpan<byte> content, string listSort, int index, string? listPath)
        {
            if (scope.Leaves.TryGetValue(sig, out var leaf)) return Emit(leaf, content, listSort, index, listPath);
            if (scope.Quiet.Contains(sig)) return true;
            if (scope.Bearing.Contains(sig) || scope.Lists.ContainsKey(sig)) return Fail($"{sig} may hold a path or an armor addon link the bytes can't place");
            return !owner.HasExtension(content) || Fail($"{sig} holds a file extension's text and isn't placed");
        }

        // A signature met again at its level: harmless only when nothing in it can name a file
        bool Matters(Scope scope, string sig, ReadOnlySpan<byte> content) =>
            scope.Leaves.ContainsKey(sig) || scope.Bearing.Contains(sig) || scope.Lists.ContainsKey(sig) || !scope.Quiet.Contains(sig) && owner.HasExtension(content);

        bool Emit(Leaf leaf, ReadOnlySpan<byte> content, string listSort, int index, string? listPath)
        {
            string path = listPath != null ? listPath + "[" + index.ToString(CultureInfo.InvariantCulture) + "]" + leaf.Path : leaf.Path;
            if (leaf.Kind == LeafKind.Link)
            {
                if (content.Length != 4) return Fail($"{leaf.Path}: a link of {content.Length} bytes");
                uint formId = BinaryPrimitives.ReadUInt32LittleEndian(content);
                if (formId == 0) return true;
                if (!ids.TryResolve(formId, out string plugin, out uint local)) return Fail("a link that can't be resolved");
                _result.AddonLinks.Add(new AddonLink(path, $"{local:X6}:{plugin}"));
                return true;
            }
            // Mutagen reads the text up to its first zero byte
            int end = content.IndexOf((byte)0);
            var text = end < 0 ? content : content[..end];
            foreach (byte b in text) if (b >= 0x80) return Fail("a text outside ASCII"); // a letter outside ASCII: the plugin's encoding decides, Mutagen's read says
            if (text.Length == 0) return true;
            string value = System.Text.Encoding.ASCII.GetString(text);
            if (!leaf.Typed && !owner.ByValue(value)) return true;
            _found.Add(listPath != null ? (listSort, index, leaf.Sort, new PathFound(path, value, leaf.Typed)) : (leaf.Sort, -1, "", new PathFound(path, value, leaf.Typed)));
            return true;
        }

        public RecordAssets? Finish()
        {
            if (_component != null) { Fail("a component without its BFCE"); return null; }
            foreach (var f in _found.OrderBy(f => f.Sort, StringComparer.Ordinal).ThenBy(f => f.Index).ThenBy(f => f.Within, StringComparer.Ordinal))
                _result.Paths.Add(f.Path);
            return _result;
        }
    }

    bool ByValue(string s) => s.Length > 4 && _extensionSet.Contains(PathWalker.ExtensionOf(s));

    /// <summary>True when the bytes hold one of the extensions' text, in any letter case.</summary>
    public bool HasExtension(ReadOnlySpan<byte> bytes)
    {
        int i = bytes.IndexOf((byte)'.');
        while (i >= 0)
        {
            foreach (var ext in _extensions)
            {
                if (i + ext.Length > bytes.Length) continue;
                bool same = true;
                for (int j = 1; j < ext.Length && same; j++) same = (bytes[i + j] | 0x20) == ext[j];
                if (same) return true;
            }
            int next = bytes[(i + 1)..].IndexOf((byte)'.');
            i = next < 0 ? -1 : i + 1 + next;
        }
        return false;
    }
}
