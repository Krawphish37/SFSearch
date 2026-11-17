// Which subrecords of a record type can hold a link, by the schema (J29, 2026-11-15). The byte search for a FormID finds
// its four bytes anywhere in a record; most hits in world data are numbers that happen to be those bytes (a placed
// object's position, its blueprint part origin, its lighting). A hit that sits only in subrecords with no link field
// is not a link, and needs no read through Mutagen to say so.
using System.Collections.Concurrent;
using SFSearch.Index;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

public static class LinkSubrecords
{
    static readonly ConcurrentDictionary<RecordSchema, NoLinkSubrecords> Cache = new();

    /// <summary>The schema's answer, worked out per signature at its first use and kept for the schema's life.</summary>
    public static NoLinkSubrecords For(RecordSchema schema) => Cache.GetOrAdd(schema, s => new NoLinkSubrecords(sig =>
    {
        // The schema model only reads after load (GAPS S65), so several threads may walk it at once
        (Context outer, Context inComponent) found = Of(s, sig);
        return (found.outer.NoLink.Count > 0 ? found.outer.NoLink.Select(NoLinkSubrecords.Sig).ToArray() : null,
                found.inComponent.NoLink.Count > 0 ? found.inComponent.NoLink.Select(NoLinkSubrecords.Sig).ToArray() : null);
    }));

    /// <summary>The kinds of field that hold one plain value, never a link.</summary>
    static readonly HashSet<string> Plain = new(StringComparer.Ordinal)
    {
        "Int8", "Int16", "Int32", "Int64", "UInt8", "UInt16", "UInt32", "UInt64", "Float", "Double", "Half", "Bool", "Char",
        "Enum", "String", "TranslatedString", "AssetLink", "ByteArray", "Percent", "Color", "Guid", "RecordType", "ModKey",
        "P2Int", "P2Int16", "P2Int32", "P2UInt8", "P2Float", "P3Float", "P3UInt8", "P3Int16", "P3UInt16", "Break", "Marker",
        "TimeOnly", "DateOnly",
    };

    /// <summary>The subrecords of one context: those the schema places fields in that hold no link, and those that may.</summary>
    public sealed class Context
    {
        public SortedSet<string> NoLink { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> MayLink { get; } = new(StringComparer.Ordinal);
        internal readonly SortedSet<string> Seen = new(StringComparer.Ordinal);
        internal void May(string sub) { Seen.Add(sub); MayLink.Add(sub); }
    }

    /// <summary>
    /// For one signature, in two contexts: the record's own subrecords, and those inside a component (between BFCB and
    /// BFCE: a component's DATA is another field than the record's own DATA). In each: the subrecords the schema places
    /// fields in that hold no link (no link field, no object that can hold one, no code Mutagen reads by hand that may
    /// fill one), and the ones that may. A subrecord the schema doesn't name is in neither, so a hit there is read through
    /// Mutagen as before. "*" among the ones that may: a link the schema places in no subrecord, so none is ruled out.
    /// </summary>
    public static (Context Outer, Context InComponent) Of(RecordSchema schema, string signature)
    {
        var model = schema.Model;
        var outer = new Context(); var inComponent = new Context();
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
        var componentObjects = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool InComponent(FieldRow r) => r.Stack.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(o => componentObjects.TryGetValue(o, out bool c) ? c : componentObjects[o] = model.EndMarkerOf(o) == "BFCE");
        bool Nested(FieldRow r) => r.Stack.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Any(o => model.Objects.TryGetValue(o, out var od) && od.ObjType == "Record");
        foreach (var type in schema.TypesOf(signature))
        {
            if (!model.Objects.ContainsKey(type)) { outer.May("*"); inComponent.May("*"); continue; }
            foreach (var r in model.Expand(type))
            {
                var d = r.Def;
                var ctx = InComponent(r) ? inComponent : outer;
                var subs = new List<string>();
                if (r.Subrecord is { Length: 4 } own) subs.Add(own);
                foreach (var c in d.CustomSubrecordList) if (c.Length == 4) subs.Add(c);
                // A record's FormKey is in its header, not in a subrecord. A record nested in this one (a cell's navmeshes) is
                // a record of its own, read and searched apart: its fields say nothing about this record's subrecords
                if (d.Name == "FormKey" && subs.Count == 0 || Nested(r)) continue;
                bool link = RowMayLink(model, d, memo);
                // Subrecords an object holds that the XML doesn't place (a condition's CITC): hand-read, so they may hold anything
                if (d.RefObject != null) foreach (var c in model.ContainsOf(d.RefObject)) if (c.Length == 4) ctx.May(c);
                // A link field that the schema places in no subrecord: it could be in any, so nothing is ruled out
                if (link && subs.Count == 0 && IsLinkKind(d)) { ctx.May("*"); continue; }
                foreach (var sub in subs) { if (link) ctx.May(sub); else ctx.Seen.Add(sub); }
            }
        }
        foreach (var ctx in new[] { outer, inComponent })
            if (!ctx.MayLink.Contains("*"))
                foreach (var sub in ctx.Seen) if (!ctx.MayLink.Contains(sub)) ctx.NoLink.Add(sub);
        return (outer, inComponent);
    }

    static bool IsLinkKind(FieldDef d) => d.Kind is "FormLink" or "FormLinkOrIndex" or "FormKey" || d.LinkTo != null;

    /// <summary>True when the field, or anything it can hold, may be a link.</summary>
    static bool RowMayLink(SchemaModel model, FieldDef d, Dictionary<string, bool> memo)
    {
        if (IsLinkKind(d)) return true;
        if (d.Kind == "CustomLogic")
        {
            // A parsing step: what it fills decides. Unknown, or a field it fills that may link: may link
            if (string.IsNullOrEmpty(d.CustomFills)) return true;
            foreach (var name in d.CustomFills.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                FieldDef? f = null;
                int dot = name.IndexOf('.');
                if (dot > 0 && model.Objects.ContainsKey(name[..dot])) f = model.AllFields(name[..dot]).Select(x => x.F).FirstOrDefault(x => x.Name == name[(dot + 1)..]);
                else if (model.Objects.ContainsKey(d.Object)) f = model.AllFields(d.Object).Select(x => x.F).FirstOrDefault(x => x.Name == name);
                if (f == null || RowMayLink(model, f, memo)) return true;
            }
            return false;
        }
        if (d.RefObject != null) return ObjectMayLink(model, d.RefObject, memo);
        if (d.GenericArg is { Length: > 0 } g && g != "T") return ObjectMayLink(model, g, memo);
        if (Plain.Contains(d.Kind)) return false;
        // A list or a gendered pair: its items (Keywords[*]) or its two parts (WorldModel.Male) are fields of their own,
        // judged where they stand. Without them, its CLR type says what it holds
        if (d.Kind is "List" or "Gendered" && model.Objects.ContainsKey(d.Object)
            && model.AllFields(d.Object).Any(f => f.F.Path.StartsWith(d.Path + (d.Kind == "List" ? "[" : "."), StringComparison.Ordinal)))
            return false;
        if (d.Kind is "List" or "Gendered" or "Dictionary" or "Array")
            return d.ClrType is not { Length: > 0 } clr || !PlainClr(clr);
        return true;
    }

    static bool PlainClr(string clr)
    {
        foreach (var part in clr.Split('<', '>', ',', ' ', '?', '[', ']'))
            if (part.Length > 0 && part is not ("List" or "IReadOnlyList" or "ExtendedList" or "IEnumerable" or "Dictionary" or "IReadOnlyDictionary" or "IGenderedItem" or "GenderedItem"
                or "float" or "Single" or "int" or "Int32" or "uint" or "UInt32" or "short" or "Int16" or "ushort" or "UInt16" or "byte" or "Byte" or "sbyte" or "SByte" or "long" or "Int64" or "ulong" or "UInt64"
                or "bool" or "Boolean" or "double" or "Double" or "string" or "String" or "P2Float" or "P3Float" or "P2Int" or "P3Int16" or "P2Int16" or "Color" or "Percent" or "MemorySlice" or "ReadOnlyMemorySlice"))
                return false;
        return true;
    }

    /// <summary>True when an object (any kind of it, its bases' fields too) has a field that may be a link.</summary>
    static bool ObjectMayLink(SchemaModel model, string obj, Dictionary<string, bool> memo)
    {
        if (memo.TryGetValue(obj, out bool known)) return known;
        if (!model.Objects.TryGetValue(obj, out var o)) return memo[obj] = true;
        memo[obj] = true; // a cycle: assume a link until shown otherwise
        bool any = model.ContainsOf(obj).Any();
        foreach (var (f, _) in model.AllFields(obj)) if (!any && RowMayLink(model, f, memo)) any = true;
        foreach (var v in model.Variants(obj)) if (!any && ObjectMayLink(model, v, memo)) any = true;
        return memo[obj] = any;
    }
}
