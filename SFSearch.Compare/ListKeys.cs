// Which field of a list's entries says what an entry is "about", so that entries can be paired by it instead of by
// their position. Worked out from the schema alone, per object, once:
//  1. a whole number named Index or ID (a quest stage's Index, an alias's ID);
//  2. else the first link among the entry's own fields (a container entry's Item, a faction rank's Faction);
//  3. else a text named Name (a script's Name);
//  4. else, for a list that can hold several kinds of object, the kind itself (a record's Components), used only
//     when no kind is in the list twice;
//  5. else nothing: the entries are paired by position.
using SFSearch.Records;

namespace SFSearch.Compare;

/// <param name="Field">The JSON key of the key field.</param>
/// <param name="Shown">How the key is named in a path and in "matched by …".</param>
/// <param name="Def">The schema's row for the key field, when it is a field of the entry (it gives the key's default).</param>
public sealed record ListKey(string Field, string Shown, SFSearch.SchemaGen.FieldDef? Def = null);

public sealed class ListKeys
{
    readonly RecordSchema _schema;
    readonly Dictionary<string, ListKey?> _known = new(StringComparer.Ordinal);
    static readonly HashSet<string> IndexNames = new(StringComparer.Ordinal) { "Index", "ID", "Id" };
    static readonly HashSet<string> Whole = new(StringComparer.Ordinal) { "Int8", "Int16", "Int32", "Int64", "UInt8", "UInt16", "UInt32", "UInt64" };

    public ListKeys(RecordSchema schema) { _schema = schema; }

    /// <summary>The key field of a list whose entries are this object. Null: pair by position.</summary>
    public ListKey? For(string? itemObject)
    {
        if (itemObject == null) return null;
        lock (_known)
        {
            if (_known.TryGetValue(itemObject, out var have)) return have;
            return _known[itemObject] = Find(itemObject);
        }
    }

    ListKey? Find(string obj)
    {
        if (!_schema.Model.Objects.ContainsKey(obj)) return null;
        var props = _schema.Props(obj);
        var index = props.FirstOrDefault(p => IndexNames.Contains(p.Name) && Whole.Contains(p.Main.Kind));
        if (index != null) return new ListKey(index.Name, index.Name, index.Main);
        var link = props.FirstOrDefault(p => p.Main.Kind == "FormLink" && !p.Main.Nullable) ?? props.FirstOrDefault(p => p.Main.Kind == "FormLink");
        if (link != null) return new ListKey(link.Name, link.Name, link.Main);
        var name = props.FirstOrDefault(p => p.Name == "Name" && p.Main.Kind == "String" && !p.Main.Translated);
        if (name != null) return new ListKey(name.Name, name.Name, name.Main);
        if (_schema.IsPolymorphic(obj)) return new ListKey(RecordSplit.TypeKey, "kind");
        return null;
    }

    /// <summary>Every list of objects in the schema with the way its entries are paired: for checking the rule.</summary>
    public IEnumerable<(string Object, string List, string ItemObject, string How)> Table()
    {
        foreach (var o in _schema.Model.Objects.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
            foreach (var p in _schema.Props(o.Name))
            {
                if (p.Item?.RefObject is not { } item || p.Main.Object != o.Name) continue;
                var key = For(item);
                yield return (o.Name, p.Name, item, key == null ? "position" : key.Shown);
            }
    }
}
