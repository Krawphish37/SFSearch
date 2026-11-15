// What the records layer needs from schema.db: which Mutagen types a signature has, each object's properties in file
// order, enums, and the checked xEdit paths. Everything comes from SFSearch.SchemaGen's model; nothing is hand-kept.
using SFSearch.SchemaGen;

namespace SFSearch.Records;

/// <summary>One Mutagen property of an object, with the schema rows that describe its parts.</summary>
public sealed class SchemaProp
{
    public required string Name { get; init; }
    /// <summary>The row whose path is the property itself.</summary>
    public required FieldDef Main { get; init; }
    /// <summary>The row for one item of a list, array, grid or dictionary (<c>Name[*]</c>).</summary>
    public FieldDef? Item { get; set; }
    /// <summary>Rows below the property that aren't items: the two halves of a gendered value (<c>Name.Male</c>).</summary>
    public Dictionary<string, FieldDef> Parts { get; } = new(StringComparer.Ordinal);
    /// <summary>The item rows of the parts (<c>Name.Male[*]</c>).</summary>
    public Dictionary<string, FieldDef> PartItems { get; } = new(StringComparer.Ordinal);
}

public sealed class RecordSchema
{
    public SchemaDb Db { get; }
    public SchemaModel Model => Db.Model;
    public string Version => Model.Meta.GetValueOrDefault("SchemaVersion", "");
    public string MutagenVersion => Model.Meta.GetValueOrDefault("MutagenVersion", "");

    readonly Dictionary<string, List<string>> _typesBySig = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _sigByType = new(StringComparer.Ordinal);
    readonly Dictionary<string, IReadOnlyList<SchemaProp>> _props = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<string, SchemaProp>> _propsByName = new(StringComparer.Ordinal);
    readonly HashSet<string> _holders = new(StringComparer.Ordinal);
    readonly object _lock = new();

    public RecordSchema(string schemaDbPath)
    {
        Db = SchemaDb.Load(schemaDbPath);
        foreach (var t in Model.RecordTypes)
        {
            (_typesBySig.TryGetValue(t.Sig!, out var l) ? l : _typesBySig[t.Sig!] = new()).Add(t.Name);
            _sigByType[t.Name] = t.Sig!;
        }
        // Record types that hold other records: a field (their own or a base's) refers to a record type
        var recordObjects = Model.Objects.Values.Where(o => o.ObjType == "Record").Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var t in Model.RecordTypes)
            if (HoldsRecords(t.Name, recordObjects, new HashSet<string>(StringComparer.Ordinal), 0)) _holders.Add(t.Name);
    }

    bool HoldsRecords(string obj, HashSet<string> records, HashSet<string> seen, int depth)
    {
        if (depth > 3 || !seen.Add(obj)) return false;
        foreach (var (f, _) in Model.AllFields(obj))
        {
            // A list of placed references names no single object; its Mutagen type says what it holds
            if (f.ClrType != null && f.ClrType.Contains("IPlacedGetter", StringComparison.Ordinal)) return true;
            if (f.RefObject == null) continue;
            if (records.Contains(f.RefObject)) return true;
            // Blocks and sub-blocks of a worldspace hold cells
            if (f.RefObject.EndsWith("Block", StringComparison.Ordinal) && HoldsRecords(f.RefObject, records, seen, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>The Mutagen record types of a signature: one for most, 8 for OMOD, 5 for GMST.</summary>
    public IReadOnlyList<string> TypesOf(string signature) => _typesBySig.TryGetValue(signature, out var l) ? l : Array.Empty<string>();
    public string? SignatureOf(string type) => _sigByType.GetValueOrDefault(type);
    public IEnumerable<string> Signatures => _typesBySig.Keys;

    /// <summary>True for a record type that holds other records (Cell, Worldspace, Quest, DialogTopic).</summary>
    public bool HoldsRecords(string type) => _holders.Contains(type);
    public IReadOnlyCollection<string> HolderTypes => _holders;

    /// <summary>An object's properties, bases first, in the order the schema gives (file order).</summary>
    public IReadOnlyList<SchemaProp> Props(string obj)
    {
        lock (_lock)
        {
            if (_props.TryGetValue(obj, out var have)) return have;
            var list = new List<SchemaProp>();
            var byName = new Dictionary<string, SchemaProp>(StringComparer.Ordinal);
            foreach (var (f, _) in Model.AllFields(obj))
            {
                if (!f.Property) continue; // breaks and markers aren't values
                if (f.Path == f.Name)
                {
                    if (byName.ContainsKey(f.Name)) continue;
                    var p = new SchemaProp { Name = f.Name, Main = f };
                    byName[f.Name] = p; list.Add(p);
                    continue;
                }
                if (!byName.TryGetValue(f.Name, out var owner)) continue;
                string rest = f.Path[f.Name.Length..];
                if (rest == "[*]") owner.Item = f;
                else if (rest.StartsWith('.') && rest.EndsWith("[*]", StringComparison.Ordinal)) owner.PartItems[rest[1..^3]] = f;
                else if (rest.StartsWith('.')) owner.Parts[rest[1..]] = f;
            }
            _propsByName[obj] = byName;
            return _props[obj] = list;
        }
    }

    public SchemaProp? Prop(string obj, string name)
    {
        Props(obj);
        lock (_lock) return _propsByName[obj].GetValueOrDefault(name);
    }

    /// <summary>The object a JSON object is, when a field can hold several kinds: its <c>MutagenObjectType</c>, or the one the field names.</summary>
    public string? ObjectFor(string? refObject, string? mutagenObjectType)
    {
        if (mutagenObjectType != null)
        {
            if (Model.Objects.ContainsKey(mutagenObjectType)) return mutagenObjectType;
            // A generic kind is written with its argument: ObjectModFloatProperty<Weapon+Property>
            int generic = mutagenObjectType.IndexOf('<');
            if (generic > 0 && Model.Objects.ContainsKey(mutagenObjectType[..generic])) return mutagenObjectType[..generic];
        }
        return refObject != null && Model.Objects.ContainsKey(refObject) ? refObject : null;
    }

    /// <summary>True when a place that holds an object can hold several kinds, so the serializer writes which one.</summary>
    public bool IsPolymorphic(string? refObject) => refObject == null || !Model.Objects.TryGetValue(refObject, out var o) || o.Abstract || Model.Variants(refObject).Count > 0;

    public IReadOnlyList<EnumMember>? Enum(string? name) => name != null && Model.Enums.TryGetValue(name, out var l) ? l : null;

    /// <summary>xEdit's full path for a field of one record type, where a checked pair names exactly that path.</summary>
    public string? XEditPath(string signature, string plainPath) =>
        Db.PairNames.TryGetValue((signature, plainPath), out var p) && p.XEditPath.Length > 0 ? p.XEditPath : null;

    public string? ReadablePair(string signature, string plainPath) =>
        Db.PairNames.TryGetValue((signature, plainPath), out var p) && p.Readable.Length > 0 ? p.Readable : null;

    public string? Evidence(string signature, string obj, string field) => Db.Evidence.GetValueOrDefault((signature, obj, field));
}
