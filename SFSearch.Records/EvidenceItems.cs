// Evidence for a field inside a list item (GAPS row S6): which subrecord holds the item, and where the field sits in it.
//
// The schema gives a field's offset inside its own subrecord, or inside the object it belongs to. A list item has no
// address of its own there: Keywords[3] is the fourth FormID of KWDA, Entries[2] is the third LVLO (and what follows
// it), ObjectTemplates[1].Properties[0] is 24 bytes somewhere in the second OBTS. This file works that address out
// the way Mutagen lays the record out:
//  - Subrecords are handed to the record's fields in file order, as Mutagen's generated reader does: each one goes to
//    the field whose signatures include it, the next field in order first. A list whose items are several subrecords
//    (components, leveled list entries, object templates) is cut into items: a counted list takes as many items as its
//    counter says, an item ends at its end marker (BFCE for a component), at a subrecord its kind doesn't have, or at
//    a field it already had. Start and end markers come from the schema (the XML's markerType and endMarkerType, on
//    objects and on fields), and so do the other subrecords an item holds (AdditionalContainedRecordType: a
//    condition's CIS1 and CIS2, which follow its CTDA).
//  - Inside one subrecord, an item of a list of fixed-size entries starts at its index times the entry's size, after
//    the list's own count when it has one (the schema's CounterLength). Entries and fields of variable size (a list,
//    a text with a length before it or a zero byte after it: the schema's TextForm) are walked as Mutagen's generated
//    reader reads them, each count and length read from the file and checked against Mutagen's (GAPS S37).
//  - The layouts Mutagen reads with hand-written code come from the schema too: a condition's CTDA from the XML's
//    layout attributes (C186: layoutOffset, layoutBits, selectedBy, conditionFunction), and a perk's effects, an object
//    template's OBTS, an object modification's DATA with its 24-byte properties, VMAD and the rest from Mutagen's
//    HandReadLayouts.xml, walked over the record by HandLayoutWalk. Since fixes.15 (C196) the XML also places the
//    comparison value, the function's number and Unknown2, and maps a ModifyValue effect's kind of modification
//    (layoutMap): no offset or mapping is written here (GAPS Part 2 row 33).
// Nothing is guessed. Every count found in the file must equal Mutagen's count of the list, and the slice must decode
// to Mutagen's own value of the field; otherwise the address isn't given, and the reason is.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Strings;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

/// <summary>The texts a string ID names in a localized plugin's strings files of one language: the file (as the
/// stamp names it) and its text, for each file that holds the ID. With a table (the schema's StringsTable, GAPS S35),
/// only that one: .strings, .dlstrings or .ilstrings; without one, all three.</summary>
public delegate IReadOnlyList<(string File, string Text)> StringLookup(uint id, Language language, StringsSource? table);

/// <summary>Where a field inside a list item sits in the record's bytes, or why that can't be told exactly.</summary>
public sealed class ItemPlace
{
    public bool Located { get; init; }
    /// <summary>The index of the subrecord in <see cref="RawRecord.Subrecords"/>.</summary>
    public int Subrecord { get; init; } = -1;
    /// <summary>The field's offset from the start of that subrecord's content.</summary>
    public int Offset { get; init; }
    public int Size { get; init; }
    /// <summary>How the address was worked out, step by step.</summary>
    public string? Where { get; init; }
    /// <summary>What the slice reads as, and that it is Mutagen's value.</summary>
    public string? Checked { get; init; }
    /// <summary>For a link: the record the slice's FormID names.</summary>
    public string? Reads { get; init; }
    /// <summary>When not located: why.</summary>
    public string? Reason { get; init; }
}

internal sealed class ItemLocator
{
    readonly RecordSchema _schema;
    readonly SchemaModel _m;
    readonly RawRecord _raw;
    readonly Func<string, uint?>? _formIdOf;
    readonly Func<uint, string> _label;
    readonly StringLookup? _textOf;
    readonly List<string> _where = new();

    ItemLocator(RecordSchema schema, RawRecord raw, Func<string, uint?>? formIdOf, Func<uint, string> label, StringLookup? textOf)
    {
        _schema = schema; _m = schema.Model; _raw = raw; _formIdOf = formIdOf; _label = label; _textOf = textOf;
    }

    sealed class NotFound(string reason) : Exception(reason);
    static NotFound Fail(string reason) => new(reason);

    /// <summary>Where <paramref name="leaf"/>, a value of the card below at least one list item (or a translated text
    /// anywhere), sits in the record.</summary>
    /// <param name="textOf">For a localized plugin: the texts a string ID names, so that a text's 4-byte ID can be
    /// checked against Mutagen's text. Null for a plugin that isn't localized (its texts are in the subrecords).</param>
    public static ItemPlace Locate(RecordSchema schema, Card card, CardField leaf, RawRecord raw, Func<string, uint?>? formIdOf, Func<uint, string> label, StringLookup? textOf = null)
    {
        var l = new ItemLocator(schema, raw, formIdOf, label, textOf);
        try { return l.Walk(card, leaf); }
        catch (NotFound e)
        {
            return new ItemPlace { Located = false, Reason = e.Message, Where = l._where.Count > 0 ? string.Join("; ", l._where) : null };
        }
    }

    /// <summary>Where <paramref name="leaf"/> sits when Mutagen's hand-read layouts (HandReadLayouts.xml, in the
    /// schema) name it: a field outside any list item that Mutagen reads by hand (an object mod's MaxRank, a weather's
    /// ambient colors). Null when no hand-read layout names the field: the caller answers as before.</summary>
    public static ItemPlace? LocateHand(RecordSchema schema, Card card, CardField leaf, RawRecord raw, Func<string, uint?>? formIdOf, Func<uint, string> label, StringLookup? textOf = null)
    {
        if (leaf.Children != null || leaf.Json == null || !HandLayoutWalk.Applies(schema.Model, raw, card.Signature)) return null;
        var l = new ItemLocator(schema, raw, formIdOf, label, textOf) { _card = card };
        if (!l.Hand.Values.ContainsKey(leaf.Path)) return null;
        return Locate(schema, card, leaf, raw, formIdOf, label, textOf);
    }

    // ------------------------------------------------------------------ The walk down the path
    Card _card = null!;
    HandLayoutWalk? _hand;
    /// <summary>Mutagen's hand-read layouts walked over this record (once, when first needed).</summary>
    HandLayoutWalk Hand => _hand ??= HandLayoutWalk.Run(_m, _raw, _card, _textOf != null);

    ItemPlace Walk(Card card, CardField leaf)
    {
        _card = card;
        if (leaf.Children != null) throw Fail($"{leaf.Path} is a whole {(leaf.Kind is "List" or "Array" ? "list" : "object")}, not one value: name one of its fields to get its bytes");
        if (leaf.Json == null) throw Fail($"{leaf.Path} has no value in the record's JSON");
        var chain = new List<CardField>();
        for (var c = leaf; c != null; c = c.Up) chain.Add(c);
        chain.Reverse();

        // A value Mutagen's hand-read layouts name (a perk effect's PRKE, DATA and EPFD; an object template's OBTS; an
        // object mod's DATA with its 24-byte properties; VMAD; …): its place comes from the layout, checked as any other
        if (HandLayoutWalk.Applies(_m, _raw, card.Signature) && Hand.Values.TryGetValue(leaf.Path, out var hv))
            return HandLeaf(hv, leaf, DefOf(chain) ?? throw Fail($"{leaf.Path}: the schema has no row for it"));

        string obj = card.Type;
        string? map = null, gen = null;
        int from = 0, to = _raw.Subrecords.Count;
        int sub = -1, b = 0;       // inside one subrecord: its index and the object's start in it
        int cond = -1;           // the object is a condition: its CTDA, laid out by Mutagen's hand-written reader
        bool condData = false;    // … and the walk is in the condition's Data (its function's parameters)
        (int S, int E)? effect = null; // the object is a perk effect: its subrecords, read by Mutagen's hand-written reader
        for (int i = 0; i < chain.Count; i++)
        {
            var c = chain[i];
            if (c.Name.StartsWith('[')) throw Fail($"{c.Path} is an item of something that isn't a list");
            bool listStep = i + 1 < chain.Count && chain[i + 1].Name.StartsWith('[');
            var itemCf = listStep ? chain[i + 1] : null;
            int k = listStep ? int.Parse(itemCf!.Name[1..^1], CultureInfo.InvariantCulture) : -1;
            int count = c.Children?.Count ?? 0;
            bool last = (listStep ? i + 1 : i) == chain.Count - 1;
            string name = listStep ? $"{c.Name}[{k}]" : c.Name;
            var group = Groups(obj, map).FirstOrDefault(g => g.Name == c.Name)
                ?? throw Fail($"the schema has no field {c.Name} on {obj}");
            var def = group.Main;
            string? childGen = def.GenericArg == "T" ? gen : def.GenericArg ?? gen;

            // ---------------------------------------------------------------- a perk effect's subrecords
            if (effect is { } fx)
            {
                if (c.Name == "Conditions")
                {
                    // Its conditions: each a PRKC (the tab it runs on) and the CTDA subrecords after it
                    Must(listStep, c);
                    var tabs = new List<(int, int)>();
                    for (int x = fx.S; x < fx.E; x++)
                    {
                        if (Sig(x) != "PRKC") continue;
                        int e = x + 1;
                        while (e < fx.E && Sig(e) is "CTDA" or "CIS1" or "CIS2") e++;
                        tabs.Add((x, e));
                        x = e - 1;
                    }
                    if (tabs.Count != count) throw Fail($"{c.Path}: Mutagen has {count} items, and the effect has {tabs.Count} PRKC subrecords");
                    var (ts, te) = tabs[k];
                    _where.Add($"{name} is the effect's {Ordinal(k + 1)} PRKC and what follows it: {Run(ts, te)}");
                    from = ts; to = te; obj = itemCf!.Object ?? "PerkCondition"; effect = null;
                    i++;
                    continue;
                }
                Must(last && !listStep, c);
                throw Fail($"{leaf.Path} is read by Mutagen's hand-written perk effect reader, and its hand-read layout (PERK in HandReadLayouts.xml){HandSaid()} gives no place for {obj}.{c.Name}");
            }

            // ---------------------------------------------------------------- inside one subrecord
            if (sub >= 0)
            {
                var content = _raw.Content(_raw.Subrecords[sub]);
                if (cond >= 0)
                {
                    if (!condData && c.Name == "Data")
                    {
                        Must(!listStep && !last, c);
                        obj = c.Object ?? throw Fail($"{c.Path}: the record doesn't say which function's data it is");
                        condData = true;
                        continue;
                    }
                    Must(last && !listStep, c);
                    return ConditionLeaf(cond, obj, c.Name, def, condData, leaf);
                }
                int at;
                if (def.Offset != null) at = def.OffsetRel ? b + def.Offset.Value : def.Offset.Value;
                else
                {
                    // No offset in the schema: a field before it takes a size of its own in each record (a list, a
                    // text), or the object is entered by hand-written code that the walk above has followed. The
                    // fields before it are walked as Mutagen's generated reader reads them, and checked (GAPS S37)
                    at = FieldStart(sub, b, obj, map, gen, c.Up?.Children ?? _card.Fields, c.Name, c.Path);
                    _where.Add($"{c.Name} starts at offset {at} of {Sig(sub)}{(at > b ? $", after the {at - b} bytes of the fields before it, as Mutagen read them" : "")}");
                }
                if (listStep)
                {
                    var (entries, counted) = ListStart(sub, at, def, count, c.Path);
                    int start;
                    if (ItemSize(group) is int itemSize)
                    {
                        if (!counted && def.Size == null && entries + count * itemSize != content.Length)
                            throw Fail($"{c.Path}: {count} entries of {itemSize} bytes from offset {entries} don't end where {Sig(sub)} ends ({content.Length} bytes), so the entries' layout isn't certain");
                        start = entries + k * itemSize;
                        _where.Add($"{name} is entry {k + 1} of {count} ({itemSize} bytes each) from offset {entries} of {Sig(sub)}, so at offset {start}");
                    }
                    else
                    {
                        // Entries of variable size: a list without a count of its own runs to the end of the subrecord
                        start = EntryStart(sub, entries, group, c, k, count, runsToEnd: !counted && def.Size == null, gen);
                        _where.Add($"{name} is entry {k + 1} of {count} from offset {entries} of {Sig(sub)}{(k > 0 ? $"; the {k} before it take {start - entries} bytes as Mutagen read them" : "")}, so at offset {start}");
                    }
                    if (group.Item!.RefObject == null) { Must(last, c); return ItemLeaf(sub, start, group.Item, Enum(group.Item, childGen), leaf); }
                    obj = itemCf!.Object ?? group.Item.RefObject; b = start; gen = group.Item.GenericArg == "T" ? gen : group.Item.GenericArg ?? gen;
                    i++;
                    continue;
                }
                if (def.Kind == "Object")
                {
                    obj = c.Object ?? def.RefObject ?? throw Fail($"{c.Path}: the schema doesn't say what object it is");
                    b = def.StartsSubrecord ? 0 : at; gen = childGen;
                    continue;
                }
                Must(last, c);
                if (def.Kind is "String" or "AssetLink" && def.Size == null) return ItemLeaf(sub, at, def, null, leaf);
                if (def.Offset != null) _where.Add($"{c.Name} at offset {at} of {Sig(sub)}");
                // Bytes with no size of their own run to the end of the subrecord; the check below compares every byte
                int? fieldSize = def.Size ?? (def.Kind == "ByteArray" ? content.Length - at : null);
                return Leaf(sub, at, fieldSize ?? throw Fail($"{c.Path}: the schema gives no size for a {def.Kind}"), def, Enum(def, gen), leaf);
            }

            // ---------------------------------------------------------------- among the subrecords of an object
            if (group.Kind == GroupKind.InCustom)
            {
                // A field inside a subrecord Mutagen reads by hand (an object template's OBTS, an object mod's DATA): its
                // place comes from the hand-read layouts (checked above). Here none names it.
                throw Fail($"{c.Path} is read by hand-written Mutagen code ({_m.HandReadOf(obj) ?? obj}), and Mutagen's hand-read layouts (HandReadLayouts.xml){HandSaid()} give no place for {leaf.Path}");
            }

            var claims = Claim(obj, map, from, to);
            int gi = Groups(obj, map).IndexOf(group);
            switch (group.Kind)
            {
                case GroupKind.Single or GroupKind.Marker:
                {
                    Must(last && !listStep, c);
                    int s = One(claims, group.Own, from, to, c.Path);
                    var content = _raw.Content(_raw.Subrecords[s]);
                    int off = def.Offset ?? 0;
                    int? size = def.Size;
                    if (def.Offset == null && def.Kind is "String" or "ByteArray" or "AssetLink") size = content.Length;
                    if (size == null) throw Fail($"{c.Path}: the schema gives no size for a {def.Kind}");
                    if (def.Offset == null && size != content.Length) throw Fail($"{c.Path}: {Sig(s)} is {content.Length} bytes and the field is {size}; the schema gives no offset");
                    _where.Add(def.Offset == null ? $"{c.Name} is the whole {Sig(s)}" : $"{c.Name} at offset {off} of {Sig(s)}");
                    return Leaf(s, off, size.Value, def, Enum(def, gen), leaf);
                }
                case GroupKind.StructObj:
                {
                    Must(!listStep, c);
                    sub = One(claims, group.Own, from, to, c.Path);
                    b = def.Offset ?? 0;
                    obj = c.Object ?? def.RefObject!; gen = childGen; map = def.SigMap ?? map;
                    continue;
                }
                case GroupKind.MultiObj:
                {
                    Must(!listStep, c);
                    var mine = Enumerable.Range(from, to - from).Where(x => claims[x - from] == gi).ToList();
                    if (mine.Count == 0) throw Fail($"no subrecord of the record is {c.Path}'s");
                    from = mine[0]; to = mine[^1] + 1;
                    obj = c.Object ?? def.RefObject!; gen = childGen; map = def.SigMap ?? map;
                    continue;
                }
                case GroupKind.ListArray:
                {
                    Must(listStep, c);
                    int s = One(claims, group.Own, from, to, c.Path, gi);
                    var content = _raw.Content(_raw.Subrecords[s]);
                    int at = def.Offset ?? 0;
                    // A count of its own before the entries (the schema's CounterLength, the XML's counterLength: GAPS S37)
                    var (entries, _) = ListStart(s, at, def, count, c.Path);
                    int start;
                    if (ItemSize(group) is int itemSize)
                    {
                        if (entries + count * itemSize != content.Length)
                            throw Fail($"{c.Path}: Mutagen has {count} entries of {itemSize} bytes, and {Sig(s)} holds {content.Length - entries} bytes for them");
                        start = entries + k * itemSize;
                        _where.Add($"{name} is entry {k + 1} of {count} in {Sig(s)} ({itemSize} bytes each{(entries > at ? $", after the count" : "")}), so at offset {start}");
                    }
                    else
                    {
                        // Entries of variable size: each one's size is read as Mutagen reads it, and all of them must fill the subrecord
                        start = EntryStart(s, entries, group, c, k, count, runsToEnd: true, gen);
                        _where.Add($"{name} is entry {k + 1} of {count} in {Sig(s)}{(entries > at ? ", after the count" : "")}{(k > 0 ? $"; the {k} before it take {start - entries} bytes as Mutagen read them" : "")}, so at offset {start}; the {count} entries fill {Sig(s)}");
                    }
                    var item = group.Item!;
                    if (item.RefObject == null) { Must(last, c); return ItemLeaf(s, start, item, Enum(item, childGen), leaf); }
                    sub = s; b = start; obj = itemCf!.Object ?? item.RefObject;
                    gen = item.GenericArg == "T" ? gen : item.GenericArg ?? gen;
                    i++;
                    continue;
                }
                case GroupKind.ListSub:
                {
                    Must(listStep, c);
                    var mine = Enumerable.Range(from, to - from).Where(x => claims[x - from] == gi && Sig(x) == group.ItemSig).ToList();
                    if (mine.Count != count) throw Fail($"{c.Path}: Mutagen has {count} items, and {mine.Count} {group.ItemSig} subrecords of the record belong to the list");
                    // A page of a heavy record's list (HeavyRecord.cs): the record read holds only the page's items,
                    // and the item's name is its index in the whole list
                    var page = c.Up == null ? _card.Pages?.GetValueOrDefault(c.Name) : null;
                    int onPage = page == null ? k : page.Place(k);
                    if (onPage < 0 || onPage >= mine.Count) throw Fail($"{name} isn't among the items read");
                    int s = mine[onPage];
                    _where.Add($"{name} is the {Ordinal(k + 1)} of {(page?.Total ?? count).ToString("N0", CultureInfo.InvariantCulture)} {group.ItemSig} subrecords of the list");
                    var item = group.Item!;
                    if (item.RefObject == null)
                    {
                        Must(last, c);
                        var content = _raw.Content(_raw.Subrecords[s]);
                        int size = item.Size ?? (item.Kind is "String" or "ByteArray" or "AssetLink" ? content.Length : throw Fail($"{c.Path}: the schema gives no size for a {item.Kind}"));
                        return Leaf(s, 0, size, item, Enum(item, childGen), leaf);
                    }
                    sub = s; b = 0; obj = itemCf!.Object ?? item.RefObject;
                    gen = item.GenericArg == "T" ? gen : item.GenericArg ?? gen;
                    if (IsCondition(obj)) cond = s;
                    i++;
                    continue;
                }
                case GroupKind.GenderedSplit:
                {
                    // Two halves told apart by their signatures (MOD2 … for the male model, MOD4 … for the female)
                    var (half, male, part) = Half(chain, i, group, c);
                    var runs = _halves.TryGetValue(gi, out var h) ? (male ? h.Male : h.Female) : new List<(int, int)>();
                    if (runs.Count == 0) throw Fail($"no subrecord of the record is {half.Path}'s");
                    if (runs.Count > 1) throw Fail($"{half.Path} is read from {runs.Count} separate runs of subrecords (Mutagen adds a later run to the half it has); the cut follows one run");
                    (from, to) = runs[0];
                    _where.Add($"{half.Path} is subrecords {Run(from, to)}, as Mutagen's gendered reader splits the pair{(part.SigMap != null ? $" (this half's signatures: {part.SigMap})" : "")}");
                    obj = half.Object ?? part.RefObject!; map = part.SigMap ?? map;
                    gen = part.GenericArg == "T" ? gen : part.GenericArg ?? gen;
                    i++;
                    continue;
                }
                case GroupKind.GenderedMarkers:
                {
                    // Each half is the content of its own marker subrecord (MNAM and FNAM for a rank's title)
                    var (half, male, part) = Half(chain, i, group, c);
                    Must(i + 1 == chain.Count - 1, half);
                    string sig = male ? group.MaleMarker! : group.FemaleMarker!;
                    var hits = Enumerable.Range(from, to - from).Where(x => claims[x - from] == gi && Sig(x) == sig).ToList();
                    if (hits.Count != 1) throw Fail($"{half.Path}: {hits.Count} {sig} subrecords of the record are this field's");
                    var content = _raw.Content(_raw.Subrecords[hits[0]]);
                    int size = part.Size ?? (part.Kind is "String" or "ByteArray" or "AssetLink" ? content.Length : throw Fail($"{half.Path}: the schema gives no size for a {part.Kind}"));
                    _where.Add($"{half.Path} is the whole {sig} subrecord, which Mutagen's gendered reader reads the {(male ? "male" : "female")} value from");
                    return Leaf(hits[0], 0, size, part, Enum(part, gen), leaf);
                }
                case GroupKind.GenderedOne:
                {
                    // Both halves in one subrecord: the male value, then the female one
                    var (half, male, part) = Half(chain, i, group, c);
                    int s = One(claims, group.Own, from, to, c.Path);
                    var content = _raw.Content(_raw.Subrecords[s]);
                    int? SizeOf(FieldDef f) => f.Size ?? (f.RefObject != null && _m.Objects.TryGetValue(f.RefObject, out var ro) ? ro.FixedSize : null);
                    int ms = SizeOf(group.Male!) ?? throw Fail($"{c.Path}: the schema gives no size for the male value");
                    int fs = SizeOf(group.Female!) ?? throw Fail($"{c.Path}: the schema gives no size for the female value");
                    int start = def.Offset ?? (ms + fs == content.Length ? 0 : BlockOffset(obj, map, group, SizeOf) ?? throw Fail($"{c.Path}: {Sig(s)} is {content.Length} bytes, and the two values take {ms + fs}; the schema gives no offset, and the fields before it in {Sig(s)} aren't all of a known size"));
                    int at = start + (male ? 0 : ms);
                    _where.Add($"{half.Path} at offset {at} of {Sig(s)} (the male value, {ms} bytes, then the female one{(def.Offset == null && start > 0 ? $", after {start} bytes of the fields before it in {Sig(s)}" : "")})");
                    if (part.RefObject == null) { Must(i + 1 == chain.Count - 1, half); return Leaf(s, at, male ? ms : fs, part, Enum(part, gen), leaf); }
                    sub = s; b = at; obj = half.Object ?? part.RefObject;
                    gen = part.GenericArg == "T" ? gen : part.GenericArg ?? gen;
                    i++;
                    continue;
                }
                case GroupKind.PerkEffects:
                {
                    Must(listStep, c);
                    var items = _items.GetValueOrDefault(gi) ?? new List<(int, int)>();
                    if (items.Count != count) throw Fail($"{c.Path}: Mutagen has {count} effects, and the rank's subrecords cut into {items.Count}");
                    var (s, e) = items[k];
                    // The same run as the hand-read layout's (Segments PRKE … PRKF), when the layout walked this effect
                    if (Hand.Segments.TryGetValue(itemCf!.Path, out var run) && run != (s, e))
                        throw Fail($"{itemCf.Path}: the rank's subrecords cut it as {Run(s, e)}, and Mutagen's hand-read layout as {Run(run.S, run.E)}");
                    _where.Add($"{name} is effect {k + 1} of {count}, subrecords {Run(s, e)}, as Mutagen's hand-read layout (PERK, PRKE … PRKF) cuts them");
                    effect = (s, e);
                    obj = itemCf!.Object ?? throw Fail($"{c.Path}: the record doesn't say which kind of effect it is");
                    i++;
                    continue;
                }
                case GroupKind.ListMulti:
                {
                    Must(listStep, c);
                    // A scene phase's completion conditions sit between two NEXT subrecords: the XML names both since
                    // fixes.14 (C183: markerType and endMarkerType NEXT on ScenePhase.CompletionConditions), so the cut
                    // below follows them; the count of items is checked against Mutagen's (GAPS Part 2 row 28)
                    var items = _items.GetValueOrDefault(gi) ?? new List<(int, int)>();
                    if (items.Count != count) throw Fail($"{c.Path}: Mutagen has {count} items, and the record's subrecords cut into {items.Count}: {string.Join(" | ", items.Take(6).Select(x => Run(x.Item1, x.Item2)))}");
                    var (s, e) = items[k];
                    _where.Add($"{name} is item {k + 1} of {count}, subrecords {Run(s, e)}");
                    from = s; to = e;
                    var item = group.Item!;
                    obj = itemCf!.Object ?? item.RefObject!; map = item.SigMap ?? def.SigMap ?? map;
                    gen = item.GenericArg == "T" ? gen : item.GenericArg ?? gen;
                    i++;
                    continue;
                }
                default:
                    throw Fail($"{c.Path}: a {def.Kind} isn't laid out in a way this reader follows");
            }
        }
        throw Fail("the path ends before a value");
    }

    /// <summary>
    /// Where a field of a data block starts when the schema gives no offset for it: Mutagen's generated reader reads
    /// the block's fields one after another, so it starts where the field before it with a known offset ends, when
    /// every field in between has a known size (a gendered pair counts both its values). Null when one hasn't.
    /// </summary>
    int? BlockOffset(string obj, string? map, Group target, Func<FieldDef, int?> sizeOf)
    {
        int? at = null;
        foreach (var g in Groups(obj, map))
        {
            if (g == target) return at;
            if (g.Own != target.Own || g.Kind is not (GroupKind.Single or GroupKind.StructObj or GroupKind.GenderedOne)) continue;
            int? size = g.Kind == GroupKind.GenderedOne ? sizeOf(g.Male!) + sizeOf(g.Female!) : sizeOf(g.Main);
            at = g.Main.Offset is int o ? o + size : at + size;
            if (at == null) return null;
        }
        return null;
    }

    /// <summary>The half of a gendered value the path goes on with: Male or Female.</summary>
    static (CardField Half, bool Male, FieldDef Part) Half(List<CardField> chain, int i, Group g, CardField c)
    {
        if (i + 1 >= chain.Count || chain[i + 1].Name is not ("Male" or "Female")) throw Fail($"{c.Path} is a male and a female value: name one of them ({c.Path}.Male or {c.Path}.Female)");
        bool male = chain[i + 1].Name == "Male";
        var part = (male ? g.Male : g.Female) ?? throw Fail($"{c.Path}: the schema has no {chain[i + 1].Name} row for it");
        return (chain[i + 1], male, part);
    }

    static void Must(bool ok, CardField c)
    {
        if (!ok) throw Fail($"{c.Path}: the path goes on in a way the schema's layout doesn't");
    }

    string Sig(int index) => _raw.Subrecords[index].Signature;

    /// <summary>The signatures of subrecords [s, e): all of them up to 12, else the first and last few and how many.</summary>
    string Run(int s, int e) => e - s <= 12
        ? string.Join(" ", Enumerable.Range(s, e - s).Select(Sig))
        : $"{string.Join(" ", Enumerable.Range(s, 5).Select(Sig))} … {string.Join(" ", Enumerable.Range(e - 2, 2).Select(Sig))} ({e - s} subrecords)";

    string? Enum(FieldDef def, string? gen) => def.Enum == "T" ? gen : def.Enum;

    // ------------------------------------------------------------------ Entries and fields of variable size (GAPS S37)
    // Mutagen's generated reader reads an object's fields one after another. A field whose size isn't fixed takes, in
    // each record, what the file says: a list its count (CounterLength bytes before the entries, the XML's
    // counterLength) and its entries, a text its length (TextForm, the XML's binaryType: a length before it, or a zero
    // byte after it). The walk reads those counts and lengths from the file and checks each one against Mutagen's value
    // (its count of the list, its text), so a walk that isn't the one Mutagen read stops with the reason.

    static bool CountedBySubrecord(FieldDef f) => f.Note != null && f.Note.Contains("counted by ", StringComparison.Ordinal);

    static CardField? Child(CardField? parent, string name) => parent?.Children?.FirstOrDefault(x => x.Name == name);

    /// <summary>Where a list's entries start: after its own count when it has one (checked against Mutagen's count).</summary>
    (int Entries, bool Counted) ListStart(int sub, int at, FieldDef list, int count, string path)
    {
        if (list.CounterLength is not int cl || CountedBySubrecord(list)) return (at, false);
        var content = _raw.Content(_raw.Subrecords[sub]);
        long n = ReadCount(content, at, cl, path);
        if (n != count) throw Fail($"{path}: the count at offset {at} of {Sig(sub)} ({cl} bytes) reads {n}, and Mutagen has {count} entries");
        _where.Add($"{path} has its count ({cl} byte{(cl == 1 ? "" : "s")}) at offset {at}: {n}");
        return (at + cl, true);
    }

    static long ReadCount(ReadOnlySpan<byte> content, int at, int length, string path)
    {
        if (at + length > content.Length) throw Fail($"{path}: its count or length at offset {at} ({length} bytes) is past the end of the subrecord ({content.Length} bytes)");
        var c = content.Slice(at, length);
        return length switch
        {
            1 => c[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(c), 4 => BinaryPrimitives.ReadUInt32LittleEndian(c), 8 => (long)BinaryPrimitives.ReadUInt64LittleEndian(c),
            _ => throw Fail($"{path}: a count of {length} bytes"),
        };
    }

    /// <summary>Where entry <paramref name="k"/> of a list of entries of variable size starts. With
    /// <paramref name="runsToEnd"/>, every entry is walked and they must end where the subrecord ends.</summary>
    int EntryStart(int sub, int from, Group g, CardField listCard, int k, int count, bool runsToEnd, string? gen)
    {
        var content = _raw.Content(_raw.Subrecords[sub]);
        var item = g.Item ?? throw Fail($"{listCard.Path}: the schema has no row for its entries");
        int p = from;
        for (int j = 0; j < k; j++) p = ItemEnd(content, p, item, Child(listCard, $"[{j}]"), g.Map, gen, $"{listCard.Path}[{j}]");
        if (runsToEnd)
        {
            int e = p;
            for (int j = k; j < count; j++) e = ItemEnd(content, e, item, Child(listCard, $"[{j}]"), g.Map, gen, $"{listCard.Path}[{j}]");
            if (e != content.Length) throw Fail($"{listCard.Path}: its {count} entries, walked as Mutagen read them, end at offset {e}, and {Sig(sub)} is {content.Length} bytes");
        }
        return p;
    }

    /// <summary>Where field <paramref name="target"/> of an object starts, the object starting at <paramref name="b"/>
    /// of the subrecord: where the fields before it end.</summary>
    int FieldStart(int sub, int b, string obj, string? map, string? gen, IReadOnlyList<CardField> fields, string target, string path)
    {
        var content = _raw.Content(_raw.Subrecords[sub]);
        int p = b;
        foreach (var g in Groups(obj, map))
        {
            if (g.Name == target) return p;
            p = GroupEnd(content, p, g, fields.FirstOrDefault(x => x.Name == g.Name), gen, Sibling(path, g.Name));
        }
        throw Fail($"{path}: the schema has no field {target} on {obj}");

        static string Sibling(string path, string name) => path.LastIndexOf('.') is int dot and >= 0 ? path[..(dot + 1)] + name : name;
    }

    int ItemEnd(ReadOnlySpan<byte> content, int p, FieldDef item, CardField? ic, string? map, string? gen, string path)
    {
        if (item.RefObject != null)
            return ObjectEnd(content, p, ic?.Object ?? item.RefObject, item.SigMap ?? map, item.GenericArg == "T" ? gen : item.GenericArg ?? gen, ic, path);
        if (item.Kind is "String" or "AssetLink") return Text(content, p, item, ic?.Json, path).End;
        return p + (item.Size ?? throw Fail($"{path}: the schema gives no size for a {item.Kind}"));
    }

    int ObjectEnd(ReadOnlySpan<byte> content, int p, string obj, string? map, string? gen, CardField? oc, string path)
    {
        if (_m.Objects.TryGetValue(obj, out var od) && od.FixedSize is int fs && _m.Variants(obj).Count == 0) return p + fs;
        foreach (var g in Groups(obj, map)) p = GroupEnd(content, p, g, Child(oc, g.Name), gen, $"{path}.{g.Name}");
        return p;
    }

    /// <summary>Where a field of an object ends, from <paramref name="p"/>: its fixed size, or its count and entries,
    /// or its text, as the file has them and checked against Mutagen's.</summary>
    int GroupEnd(ReadOnlySpan<byte> content, int p, Group g, CardField? cf, string? gen, string path)
    {
        var f = g.Main;
        var notes = (f.Note ?? "").Split("; ");
        if (f.Kind == "CustomLogic" || notes.Contains("read by hand-written Mutagen code") || notes.Contains("binary=Custom"))
            throw Fail($"{path} is read by hand-written Mutagen code ({_m.HandReadOf(f.Object) ?? "its own"}), so where it and the fields after it stand isn't known");
        switch (f.Kind)
        {
            case "List" or "Array":
            {
                if (f.Size is int whole) return p + whole;
                var item = g.Item ?? throw Fail($"{path}: the schema has no row for its entries");
                int have = cf?.Children?.Count ?? 0, n = have;
                if (f.CounterLength is int cl && !CountedBySubrecord(f))
                {
                    long file = ReadCount(content, p, cl, path);
                    if (file != have) throw Fail($"{path}: the count at offset {p} ({cl} bytes) reads {file}, and Mutagen has {have} entries");
                    p += cl;
                }
                else if (f.Note != null && Regex.Match(f.Note, @"always (\d+) items") is { Success: true } fixedCount) n = int.Parse(fixedCount.Groups[1].Value, CultureInfo.InvariantCulture);
                // else: no count of its own, so the list runs to the end of its subrecord; the caller checks that the
                // entries walked end there
                for (int j = 0; j < n; j++) p = ItemEnd(content, p, item, Child(cf, $"[{j}]"), g.Map, gen, $"{path}[{j}]");
                return p;
            }
            case "String" or "AssetLink":
                return Text(content, p, f, cf?.Json, path).End;
            case "Object":
            {
                string o = cf?.Object ?? f.RefObject ?? throw Fail($"{path}: the schema doesn't say what object it is");
                return ObjectEnd(content, p, o, f.SigMap ?? g.Map, f.GenericArg == "T" ? gen : f.GenericArg ?? gen, cf, path);
            }
            case "Gendered" or "Dictionary" or "Array2D":
                throw Fail($"{path}: a {f.Kind} inside an entry isn't laid out in a way this reader follows");
            default:
                return p + (f.Size ?? throw Fail($"{path}: the schema gives no size for a {f.Kind}"));
        }
    }

    /// <summary>
    /// A text inside a subrecord, from <paramref name="p"/>, as Mutagen's StringBinaryTranslation reads it by the
    /// schema's TextForm (the XML's binaryType): a 4-, 2- or 1-byte length before it, a slot of a fixed size, or the
    /// text and the zero byte after it (NullTerminate, and Plain when it isn't the whole subrecord). Checked: the text
    /// is Mutagen's. Gives where the text's own bytes start, how many there are, where the field ends, and the length's size.
    /// </summary>
    (int Body, int Length, int End, int Prefix) Text(ReadOnlySpan<byte> content, int p, FieldDef f, JsonNode? json, string path)
    {
        if (f.Translated) throw Fail($"{path}: a translated text inside an entry (a string ID in a localized plugin), which this reader doesn't walk");
        string form = f.TextForm ?? "NullTerminate";
        int prefix = form switch { "PrependLength" or "PrependLengthWithNullIfContent" => 4, "PrependLengthUShort" => 2, "PrependLengthUInt8" => 1, _ => 0 };
        int body = p + prefix, length;
        if (prefix > 0) length = checked((int)ReadCount(content, p, prefix, path));
        else if (form == "NullPaddedFixedLength") length = f.Size ?? throw Fail($"{path}: a text in a slot of a size the schema doesn't give");
        else if (form == "NullTerminateIfNotEmpty" && p == content.Length) length = 0;
        else
        {
            int z = p <= content.Length ? content[p..].IndexOf((byte)0) : -1;
            if (z < 0) throw Fail($"{path}: no zero byte ends the text that starts at offset {p}");
            length = z + 1;
        }
        int end = body + length;
        if (end > content.Length) throw Fail($"{path}: the text at offset {p} ({length} bytes) runs past the end of the subrecord ({content.Length} bytes)");
        var bytes = content.Slice(body, length);
        while (bytes.Length > 0 && bytes[^1] == 0) bytes = bytes[..^1];
        string want = json is JsonValue v && v.TryGetValue<string>(out var w) ? w : "";
        if (!bytes.SequenceEqual(Encoding.UTF8.GetBytes(want)) && !bytes.SequenceEqual(Encoding.Latin1.GetBytes(want)))
            throw Fail($"{path}: the text read at offset {body} is \"{Encoding.Latin1.GetString(bytes)}\", and Mutagen's is \"{want}\": the walk isn't the one Mutagen read");
        return (body, length, end, prefix);
    }

    /// <summary>The leaf that is one entry of a list (or a text): a fixed-size value, or a text's own bytes.</summary>
    ItemPlace ItemLeaf(int sub, int at, FieldDef def, string? enumName, CardField leaf)
    {
        if (def.Kind is not ("String" or "AssetLink") || def.Size != null)
            return Leaf(sub, at, def.Size ?? throw Fail($"{leaf.Path}: the schema gives no size for a {def.Kind}"), def, enumName, leaf);
        var content = _raw.Content(_raw.Subrecords[sub]);
        var t = Text(content, at, def, leaf.Json, leaf.Path);
        string form = def.TextForm ?? "NullTerminate";
        string named = leaf.Name.StartsWith('[') && leaf.Up != null ? leaf.Up.Name + leaf.Name : leaf.Name;
        _where.Add(t.Prefix > 0
            ? $"{named} is a text with a {t.Prefix}-byte length before it ({form}): the length at offset {at} reads {t.Length}, the text is at offset {t.Body}"
            : $"{named} is a text at offset {at} ({form}: {(form == "NullPaddedFixedLength" ? $"a slot of {t.Length} bytes" : "it ends with a zero byte")})");
        if (t.Length == 0)
            return new ItemPlace
            {
                Located = true, Subrecord = sub, Offset = at, Size = t.Prefix, Where = string.Join("; ", _where),
                Checked = "checked: the length reads 0, and Mutagen's text is empty",
            };
        return Leaf(sub, t.Body, t.Length, def, enumName, leaf);
    }

    static int? ItemSize(Group g) => g.Item == null ? null : g.Item.RefObject == null ? g.Item.Size : g.ItemObject?.FixedSize;

    /// <summary>The one subrecord with this signature among those an object's own plain fields hold.</summary>
    int One(int[] claims, string? sig, int from, int to, string path, int listGroup = -1)
    {
        if (sig == null) throw Fail($"{path}: the schema doesn't say which subrecord holds it");
        var hits = new List<int>();
        for (int x = from; x < to; x++)
        {
            if (Sig(x) != sig) continue;
            int g = claims[x - from];
            if (listGroup >= 0 ? g == listGroup : g >= 0 && _lastGroups![g].Kind is GroupKind.Single or GroupKind.StructObj or GroupKind.Marker or GroupKind.GenderedOne) hits.Add(x);
        }
        if (hits.Count == 0) throw Fail($"{path}: the record has no {sig} subrecord for it here");
        if (hits.Count > 1) throw Fail($"{path}: {hits.Count} {sig} subrecords could hold it here");
        return hits[0];
    }

    // ------------------------------------------------------------------ The value check
    /// <param name="mask">Not 0: the field is some bits of the one byte at <paramref name="offset"/>; its value is
    /// <c>(byte &amp; mask) &gt;&gt; shift</c>.</param>
    /// <param name="map">Not null: the value the field has, worked out from the byte at <paramref name="offset"/> the
    /// way Mutagen's reader works it out (a perk effect's kind of modification is its function's number).</param>
    ItemPlace Leaf(int sub, int offset, int size, FieldDef def, string? enumName, CardField leaf, byte mask = 0, int shift = 0, byte? map = null)
    {
        var content = _raw.Content(_raw.Subrecords[sub]);
        if (size <= 0) throw Fail($"{leaf.Path} has no bytes of its own (a {def.Kind} that is true when its subrecord is there)");
        if (offset < 0 || offset + size > content.Length)
            throw Fail($"{leaf.Path}: offset {offset}, {size} bytes, is past the end of {Sig(sub)} ({content.Length} bytes)");
        var bytes = map is byte mapped ? new[] { mapped } : mask == 0 ? content.Slice(offset, size) : new[] { (byte)((content[offset] & mask) >> shift) };
        var (ok, reads, said) = Decode(def, enumName, bytes, leaf.Json);
        if (ok == null) throw Fail($"{leaf.Path}: the bytes at offset {offset} of {Sig(sub)} can't be checked against Mutagen's value for a {def.Kind}{(said != null ? $" ({said})" : "")}, so no slice is given");
        if (ok == false) throw Fail($"{leaf.Path}: the bytes at offset {offset} of {Sig(sub)} ({RawRecord.Hex(content.Slice(offset, size))}{(mask != 0 ? $", bits {mask:X2}" : "")}) read as {said}, and Mutagen's value is {leaf.Text}: the layout worked out isn't the one Mutagen read");
        return new ItemPlace
        {
            Located = true, Subrecord = sub, Offset = offset, Size = size, Where = string.Join("; ", _where), Reads = reads,
            Checked = $"checked: the bytes read {said}, Mutagen's value",
        };
    }

    /// <summary>The slice as its kind: (true, …) when it is Mutagen's value, (false, …) when it isn't, (null, …) when the kind can't be checked.</summary>
    (bool? Ok, string? Reads, string? Said) Decode(FieldDef def, string? enumName, ReadOnlySpan<byte> b, JsonNode? json)
    {
        if (def.Kind == "Enum" && json is JsonArray flags && _schema.Enum(enumName) is { } fm)
        {
            // Flags: the JSON lists the names of the bits that are set
            ulong want = 0;
            foreach (var f in flags)
            {
                string n = f?.GetValue<JsonElement>().ToString() ?? "";
                var m = fm.FirstOrDefault(x => x.Name == n);
                if (m != null) want |= (ulong)m.Value;
                else if (ulong.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong bit)) want |= bit;
                else return (null, null, $"a flag named {n} that the enum doesn't have");
            }
            ulong have = Unsigned(b);
            var named = fm.Where(m => m.Value != 0 && (have & (ulong)m.Value) == (ulong)m.Value).Select(m => m.Name).ToList();
            return (have == want, null, $"0x{have:X} ({(named.Count == 0 ? "none" : string.Join(", ", named))})");
        }
        if (def.Kind == "String" && json is JsonObject text && text.ContainsKey("TargetLanguage"))
        {
            // A translated text. Mutagen's value is the text in the language read (TargetLanguage): one "Value", or
            // one of the "Values" of every language a localized plugin's strings files have
            string target = text["TargetLanguage"]?.GetValue<string>() ?? "";
            string? want = text["Value"] is JsonValue tv && tv.TryGetValue<string>(out var one) ? one
                : text["Values"] is JsonArray all
                    ? all.OfType<JsonObject>().FirstOrDefault(v => string.Equals(v["Language"]?.GetValue<string>(), target, StringComparison.OrdinalIgnoreCase))?["String"]?.GetValue<string>()
                    : null;
            if (_textOf != null)
            {
                // A localized plugin: the subrecord holds a 4-byte string ID, and the text is in the plugin's strings
                // files. The ID is looked up the way Mutagen's read looked it up, and the text must be Mutagen's.
                if (b.Length != 4) return (null, null, $"a text of a localized plugin in {b.Length} bytes, where a 4-byte string ID was expected");
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(b);
                if (!System.Enum.TryParse<Language>(target, true, out var language)) return (null, null, $"string ID {id:X8}, in a language Mutagen doesn't know ({target})");
                if (want == null) return (null, null, $"string ID {id:X8}, and Mutagen has no {target} text for it");
                if (id == 0) return (want.Length == 0, null, "string ID 00000000 (no text)");
                // The one table the field is read from, as the schema has it from the XML (translated="Normal|DL|IL")
                StringsSource? table = System.Enum.TryParse<StringsSource>(def.StringsTable, out var t) ? t : null;
                var held = _textOf(id, language, table);
                if (held.Count == 0) return (false, null, $"string ID {id:X8}, which {(table is { } tb ? $"the plugin's {target} {StringsExtension(tb)} file doesn't hold" : $"none of the plugin's {target} strings files holds")}");
                var same = held.Where(h => h.Text == want).ToList();
                var shown = same.Count > 0 ? same : held;
                string files = string.Join(" and ", shown.Select(h => h.File));
                string reads = $"string ID {id:X8}: \"{CardBuilder.NoTag(shown[0].Text)}\" in {files}";
                return (same.Count > 0, reads, same.Count > 0 ? $"string ID {id:X8} (its {target} text in {files})" : reads);
            }
            if (want == null) return (null, null, $"a translated text with no {target} text stored in the plugin");
            json = JsonNode.Parse(JsonSerializer.Serialize(want));
        }
        if (json is not JsonValue jv) return (null, null, null);
        var el = jv.GetValue<JsonElement>();
        string? note = def.Note;
        if (note != null && (note.Contains("divided by", StringComparison.Ordinal) || note.Contains("multiplied by", StringComparison.Ordinal))) return (null, null, "the file stores it scaled");
        switch (def.Kind)
        {
            case "FormLink" or "FormLinkOrIndex" when b.Length == 4 && el.ValueKind == JsonValueKind.String:
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(b);
                string key = el.GetString()!;
                uint? want = key == "Null" ? 0u : _formIdOf?.Invoke(key);
                string reads = _label(id);
                if (want == null) return (null, reads, $"FormID {id:X8}");
                return (want == id, id == 0 ? null : reads, id == 0 ? "a null link" : $"FormID {id:X8} → {reads}");
            }
            case "Int8" or "Int16" or "Int32" or "Int64" when el.ValueKind == JsonValueKind.Number:
            {
                long v = b.Length switch { 1 => (sbyte)b[0], 2 => BinaryPrimitives.ReadInt16LittleEndian(b), 4 => BinaryPrimitives.ReadInt32LittleEndian(b), 8 => BinaryPrimitives.ReadInt64LittleEndian(b), _ => long.MinValue };
                if (v == long.MinValue && b.Length != 8) return (null, null, null);
                return (el.TryGetInt64(out long j) && j == v, null, v.ToString(CultureInfo.InvariantCulture));
            }
            case "UInt8" or "UInt16" or "UInt32" or "UInt64" when el.ValueKind == JsonValueKind.Number:
            {
                ulong v = Unsigned(b);
                return (el.TryGetUInt64(out ulong j) && j == v, null, v.ToString(CultureInfo.InvariantCulture));
            }
            case "Float" when b.Length == 4 && el.ValueKind == JsonValueKind.Number:
            {
                float v = BinaryPrimitives.ReadSingleLittleEndian(b);
                return (el.GetSingle().Equals(v), null, v.ToString("R", CultureInfo.InvariantCulture));
            }
            case "Double" when b.Length == 8 && el.ValueKind == JsonValueKind.Number:
            {
                double v = BinaryPrimitives.ReadDoubleLittleEndian(b);
                return (el.GetDouble().Equals(v), null, v.ToString("R", CultureInfo.InvariantCulture));
            }
            case "Half" when b.Length == 2:
            {
                // Held bit for bit, as the serializer keeps it (C222): the 16 bits, NaN payloads and -0 included
                System.Half v = BinaryPrimitives.ReadHalfLittleEndian(b);
                bool same = FieldLookup.TryReadHalf(el, out System.Half j) && BitConverter.HalfToUInt16Bits(j) == BitConverter.HalfToUInt16Bits(v);
                return (same, null, System.Half.IsNaN(v) || System.Half.IsInfinity(v) ? v.ToString(CultureInfo.InvariantCulture) : ((double)v).ToString("R", CultureInfo.InvariantCulture));
            }
            case "Percent" when el.ValueKind == JsonValueKind.Number:
            {
                double v;
                if (b.Length == 1 && note != null && note.Contains("ByteHundred", StringComparison.Ordinal)) v = b[0] / 100.0;
                else if (b.Length == 4 && (note == null || !note.Contains("stored as", StringComparison.Ordinal))) v = BinaryPrimitives.ReadSingleLittleEndian(b);
                else return (null, null, null);
                return (Math.Abs(el.GetDouble() - v) < 1e-6, null, v.ToString("R", CultureInfo.InvariantCulture));
            }
            case "Bool" when el.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool v = false;
                foreach (var x in b) v |= x != 0;
                return (el.GetBoolean() == v, null, v ? "true" : "false");
            }
            case "Enum":
            {
                var members = _schema.Enum(enumName);
                ulong v = Unsigned(b);
                if (members == null) return (null, null, $"{v} (the enum isn't known)");
                string said = members.FirstOrDefault(m => (ulong)m.Value == v)?.Name ?? v.ToString(CultureInfo.InvariantCulture);
                if (el.ValueKind == JsonValueKind.String)
                {
                    string s = el.GetString()!;
                    var m = members.FirstOrDefault(x => x.Name == s);
                    if (m != null) return ((ulong)m.Value == v, null, said);
                    return (ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong n) ? n == v : null, null, said);
                }
                if (el.ValueKind == JsonValueKind.Number) return (el.TryGetUInt64(out ulong n) && n == v, null, said);
                return (null, null, said);
            }
            case "ByteArray" when el.ValueKind == JsonValueKind.String:
            {
                string hex = el.GetString()!;
                if (!hex.StartsWith("0x", StringComparison.Ordinal)) return (null, null, null);
                byte[] want;
                try { want = Convert.FromHexString(hex.AsSpan(2)); } catch (FormatException) { return (null, null, null); }
                return (b.SequenceEqual(want), null, $"{b.Length} bytes{(b.SequenceEqual(want) ? ", every one equal to" : ", not those of")} Mutagen's {want.Length}");
            }
            case "String" or "AssetLink" when el.ValueKind == JsonValueKind.String:
            {
                var body = b.Length > 0 && b[^1] == 0 ? b[..^1] : b;
                string s = el.GetString()!;
                bool same = body.SequenceEqual(Encoding.UTF8.GetBytes(s)) || body.SequenceEqual(Encoding.Latin1.GetBytes(s));
                // A 4-byte text of an object-mod property: Mutagen stops it at the first zero
                if (!same) { int z = body.IndexOf((byte)0); if (z >= 0) same = body[..z].SequenceEqual(Encoding.Latin1.GetBytes(s)); }
                return (same, null, "\"" + Encoding.Latin1.GetString(body).TrimEnd('\0') + "\"");
            }
        }
        return (null, null, null);
    }

    static string StringsExtension(StringsSource s) => s switch { StringsSource.DL => ".dlstrings", StringsSource.IL => ".ilstrings", _ => ".strings" };

    static ulong Unsigned(ReadOnlySpan<byte> b) => b.Length switch
    {
        1 => b[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(b), 4 => BinaryPrimitives.ReadUInt32LittleEndian(b), 8 => BinaryPrimitives.ReadUInt64LittleEndian(b), _ => 0,
    };

    // ------------------------------------------------------------------ Mutagen's hand-read layouts, from the schema
    HashSet<string>? _perkEffectSigs;
    /// <summary>The subrecords a perk effect holds, from its hand-read layout (PERK's Segments PRKE … PRKF: the
    /// subrecords it describes and the generated ones it names).</summary>
    HashSet<string> PerkEffectSigs => _perkEffectSigs ??= HandLayoutWalk.SegmentSigsOf(_m, "PERK", "PRKE")
        ?? throw Fail("the schema has no hand-read layout of a perk's effects (PERK in HandReadLayouts.xml)");

    bool IsCondition(string obj) => _m.Ancestry(obj).Any(o => o.Name == "Condition");

    /// <summary>The problems the hand-read layout walk met, for a reason: " (they stopped at: …)".</summary>
    string HandSaid() => Hand.Problems.Count == 0 ? (Hand.Used.Count == 0 ? " (none applies to this record)" : "") : $" (the walk of {string.Join(", ", Hand.Used)} stopped at: {string.Join("; ", Hand.Problems.Take(3))})";

    /// <summary>The schema's row for the leaf of a card path, as the walk would find it.</summary>
    FieldDef? DefOf(List<CardField> chain)
    {
        string? obj = _card.Type, map = null;
        Group? g = null;
        for (int i = 0; i < chain.Count; i++)
        {
            var c = chain[i];
            bool lastStep = i == chain.Count - 1;
            if (c.Name.StartsWith('['))
            {
                var item = g?.Item;
                if (item == null) return null;
                if (lastStep) return item;
                obj = c.Object ?? item.RefObject; map = item.SigMap ?? map; g = null;
                continue;
            }
            if (obj == null) return null;
            g = Groups(obj, map).FirstOrDefault(x => x.Name == c.Name);
            if (g == null) return null;
            if (lastStep) return g.Main;
            if (!chain[i + 1].Name.StartsWith('[')) { obj = c.Object ?? g.Main.RefObject; map = g.Main.SigMap ?? map; }
        }
        return null;
    }

    /// <summary>
    /// A value Mutagen's hand-read layouts (HandReadLayouts.xml, C186, kept in the schema) name: its slice, how Mutagen
    /// reads it (a number, one bit of a value read earlier, a value derived from another, a stored number that maps to
    /// Mutagen's by an encoding), checked against Mutagen's value like every slice.
    /// </summary>
    ItemPlace HandLeaf(HandLayoutWalk.Value v, CardField leaf, FieldDef def)
    {
        foreach (var (p, run) in Hand.Segments.Where(s => leaf.Path.StartsWith(s.Key + ".", StringComparison.Ordinal)).OrderBy(s => s.Key.Length))
            _where.Add($"{p} is subrecords {Run(run.S, run.E)}");
        string named = leaf.Path.Contains('.') ? leaf.Path[(leaf.Path.LastIndexOf('.') + 1)..] : leaf.Path;
        string by = $"Mutagen's hand-read layout ({v.Layout} in HandReadLayouts.xml)";
        string? en = def.Enum == "T" ? null : def.Enum;
        switch (v.Kind)
        {
            case "Const":
            {
                // No bytes of its own: Mutagen derives it from the value the layout chose by
                _where.Add($"{named} has no bytes of its own: {by} gives it as {v.Const} when the value at offset {v.Offset} of {Sig(v.Sub)} is the one read here");
                byte mapped;
                if (def.Kind == "Enum" && _schema.Enum(en)?.FirstOrDefault(m => m.Name == v.Const) is { } member) mapped = (byte)member.Value;
                else if (def.Kind == "Bool" && bool.TryParse(v.Const, out bool flag)) mapped = flag ? (byte)1 : (byte)0;
                else throw Fail($"{leaf.Path}: {by} gives it as {v.Const}, which can't be held against a {def.Kind}");
                if (def.LayoutMap != null)
                {
                    // The record XML says it too (layoutMap, C196): the same place, and the same value for the number there
                    var content = _raw.Content(_raw.Subrecords[v.Sub]);
                    long stored = v.Size switch { 1 => content[v.Offset], 2 => BinaryPrimitives.ReadUInt16LittleEndian(content.Slice(v.Offset, 2)), _ => BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(v.Offset, 4)) };
                    if (def.LayoutSubrecord is { } ls && ls != Sig(v.Sub) || def.LayoutOffsetNumber is { } lo && lo != v.Offset)
                        throw Fail($"{leaf.Path}: the XML places {def.Name} at offset {def.LayoutOffset} of {def.LayoutSubrecord}, and {by} derives it from offset {v.Offset} of {Sig(v.Sub)}");
                    if (!def.LayoutMapEntries.TryGetValue(stored, out var said) || said != v.Const)
                        throw Fail($"{leaf.Path}: the number at offset {v.Offset} of {Sig(v.Sub)} is {stored}; {by} gives {v.Const}, and the XML's layoutMap ({def.LayoutMap}) {(said == null ? "has no entry for it" : $"gives {said}")}");
                    _where.Add($"the XML's layoutMap on {def.Object}.{def.Name} ({def.LayoutMap}) gives {said} for {stored} too");
                }
                return Leaf(v.Sub, v.Offset, v.Size, def, en, leaf, map: mapped);
            }
            case "Bit":
            {
                var content = _raw.Content(_raw.Subrecords[v.Sub]);
                bool set = (content[v.Offset] & v.Mask) != 0;
                _where.Add($"{named} is bit {v.Shift} of the byte at offset {v.Offset} of {Sig(v.Sub)}{(v.Invert ? ", Mutagen's value being the bit cleared" : "")}, as {by} lays it out");
                return Leaf(v.Sub, v.Offset, 1, def, en, leaf, map: (byte)(set ^ v.Invert ? 1 : 0));
            }
        }
        _where.Add($"{named} is at offset {v.Offset} of {Sig(v.Sub)} ({v.Size} byte{(v.Size == 1 ? "" : "s")}), as {by} lays it out");
        if (v.Kind == "Color")
        {
            // R G B A in the file; Mutagen's JSON writes #AARRGGBB
            var b = _raw.Content(_raw.Subrecords[v.Sub]).Slice(v.Offset, 4);
            string color = $"#{b[3]:X2}{b[0]:X2}{b[1]:X2}{b[2]:X2}";
            string? want = leaf.Json is JsonValue cj && cj.TryGetValue<string>(out var cs) ? cs : null;
            if (!string.Equals(want, color, StringComparison.OrdinalIgnoreCase))
                throw Fail($"{leaf.Path}: the bytes at offset {v.Offset} of {Sig(v.Sub)} ({RawRecord.Hex(b)}) read {color} (R G B A), and Mutagen's value is {leaf.Text}: the layout isn't the one Mutagen read");
            return new ItemPlace { Located = true, Subrecord = v.Sub, Offset = v.Offset, Size = 4, Where = string.Join("; ", _where), Checked = $"checked: the bytes read R G B A {color}, Mutagen's value" };
        }
        if (v.Encoding == "speed" || v.Encoding?.StartsWith("div:", StringComparison.Ordinal) == true)
        {
            // The stored number maps to Mutagen's: speed is (b - 127) / 1270, div:N is the stored number / N
            var content = _raw.Content(_raw.Subrecords[v.Sub]);
            var b = content.Slice(v.Offset, v.Size);
            double stored = v.Size switch { 1 => b[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(b), _ => BinaryPrimitives.ReadUInt32LittleEndian(b) };
            double value = v.Encoding == "speed" ? (stored - 127) / 1270.0 : stored / double.Parse(v.Encoding[4..], CultureInfo.InvariantCulture);
            if (leaf.Json is not JsonValue jv || !jv.TryGetValue<double>(out double want)) throw Fail($"{leaf.Path}: Mutagen's value isn't a number");
            bool same = Math.Abs(want - value) < 1e-5;
            string said = $"{stored.ToString(CultureInfo.InvariantCulture)}, which is {value.ToString("R", CultureInfo.InvariantCulture)} by the encoding {v.Encoding}";
            if (!same) throw Fail($"{leaf.Path}: the byte{(v.Size == 1 ? "" : "s")} at offset {v.Offset} of {Sig(v.Sub)} read {said}, and Mutagen's value is {leaf.Text}: the layout isn't the one Mutagen read");
            return new ItemPlace { Located = true, Subrecord = v.Sub, Offset = v.Offset, Size = v.Size, Where = string.Join("; ", _where), Checked = $"checked: the bytes read {said}, Mutagen's value" };
        }
        return Leaf(v.Sub, v.Offset, v.Size, def, en, leaf);
    }

    /// <summary>
    /// A field of a condition, in its CTDA or the CIS1/CIS2 after it, placed by the XML's layout attributes (C186): the
    /// compare operator in bits 5-7 of byte 0 and the flags in bits 0-4 (less the bits other fields and the kind of
    /// condition take: UseAliases' bit 1, UsePackageData's bit 3, and bit 2, which picks ConditionGlobal or
    /// ConditionFloat by Condition's selectedBy), run on at 20, the reference at 24, parameter 3 at 28, the function's
    /// own parameters from Data's offset 12 in their order, the two text parameters in CIS1 and CIS2 (C183), and since
    /// fixes.15 (C196, GAPS Part 2 row 33) the comparison value at 4, Unknown2 at 10 and the function's number at 8 (on
    /// the FunctionParse step, layoutEncoding uint16). Since fixes.16 (C200) the data of a function Mutagen doesn't know
    /// (UnknownConditionData, the XML's conditionFunction="*") places its Function at 8 too, by its own layoutOffset.
    /// </summary>
    ItemPlace ConditionLeaf(int s, string obj, string name, FieldDef def, bool inData, CardField leaf)
    {
        var content = _raw.Content(_raw.Subrecords[s]);
        string? en = Enum(def, null);
        // Every field of the condition and of its data with a layout place
        var placed = _m.Ancestry(IsCondition(obj) ? obj : "Condition").Concat(_m.Ancestry(inData ? obj : "ConditionData"))
            .SelectMany(o => o.Fields).Where(f => f.LayoutOffsetNumber != null).ToList();
        int ctda = placed.Max(f => f.LayoutOffsetNumber!.Value + (f.LayoutBits != null ? 1 : PlacedSize(f) ?? 0));
        if (content.Length != ctda) throw Fail($"{leaf.Path}: CTDA is {content.Length} bytes, and the XML's layout of a condition takes {ctda}");

        // The function's number: the place of the step Mutagen reads it in (FunctionParse: layoutOffset, layoutEncoding)
        var fnStep = _m.Ancestry("Condition").SelectMany(o => o.Fields).FirstOrDefault(f => f.Kind == "CustomLogic" && f.LayoutOffsetNumber != null && PlacedSize(f) != null)
            ?? throw Fail($"{leaf.Path}: the XML places no step of the condition that reads the function's number");
        int fnAt = fnStep.LayoutOffsetNumber!.Value, fnSize = PlacedSize(fnStep)!.Value;
        if (def.Subrecord is "CIS1" or "CIS2" && inData)
        {
            // A text parameter: the CIS1 or CIS2 subrecord after the CTDA (the XML's recordType on the field, C183)
            string sig = def.Subrecord;
            var trailing = TrailingOf("Condition");
            int at = -1;
            for (int j = s + 1; j < _raw.Subrecords.Count && trailing.Contains(Sig(j)); j++) if (Sig(j) == sig) { at = j; break; }
            if (at < 0) throw Fail($"{leaf.Path}: no {sig} subrecord follows the condition's CTDA");
            _where.Add($"{name} is the {sig} subrecord after the condition's CTDA (the XML's recordType on it)");
            return Leaf(at, 0, _raw.Subrecords[at].Length, def, en, leaf);
        }
        if (def.LayoutOffsetNumber is int lo)
        {
            if (def.LayoutMask is { } bits)
            {
                // Bits of a byte: less those another field (or the choice of the condition's kind) takes
                byte mask = bits.Mask;
                foreach (var other in placed.Where(f => f != def && f.LayoutOffsetNumber == lo && f.LayoutMask != null))
                    if (other.LayoutMask!.Value.Mask is var om && (om & bits.Mask) == om && om != bits.Mask) mask &= (byte)~om;
                foreach (var o in _m.Ancestry(IsCondition(obj) ? obj : "Condition"))
                    if (o.SelectedBy is { } sel && sel.StartsWith($"field:{def.Name}:bit=", StringComparison.Ordinal))
                        foreach (var bit in sel[($"field:{def.Name}:bit=").Length..].Split(',')) mask &= (byte)~(1 << int.Parse(bit, CultureInfo.InvariantCulture));
                int shift = bits.Shift;
                _where.Add($"{name} is {(mask == bits.Mask ? $"bits {def.LayoutBits}" : $"the bits 0x{mask:X2}")} of byte {lo} of the condition's CTDA (the XML's layoutOffset and layoutBits{(mask == bits.Mask ? "" : ", less the bits other fields and the kind of condition take")})");
                return Leaf(s, lo, 1, def, en, leaf, mask, mask == bits.Mask ? shift : 0);
            }
            _where.Add($"{name} is at offset {lo} of the condition's CTDA (the XML's layoutOffset)");
            return Leaf(s, lo, PlacedSize(def) ?? throw Fail($"{leaf.Path}: the schema gives no size for {def.Name}"), def, en, leaf);
        }
        if (!inData)
        {
            // A field the generated reader reads right after one the layout places (Unknown1 after Flags' byte)
            var fields = _m.AllFields(obj).Select(x => x.F).Where(f => f.Path == f.Name).ToList();
            int i = fields.FindIndex(f => f.Name == name);
            if (i > 0 && fields[i - 1].LayoutOffsetNumber is int po && fields[i - 1].Size is int ps && def.Size is int size && !def.Custom)
            {
                _where.Add($"{name} is at offset {po + ps} of the condition's CTDA, right after {fields[i - 1].Name} (offset {po}, {ps} byte{(ps == 1 ? "" : "s")}), as Mutagen's generated reader reads it");
                return Leaf(s, po + ps, size, def, en, leaf);
            }
            throw Fail($"{leaf.Path}: the XML's layout of a condition has no place for {name}");
        }
        // The function's own parameters: from the layout offset of Condition.Data (12), in the order of the function's
        // fields as Mutagen's generated reader reads them, up to the next placed field (run on, at 20)
        var dataField = _m.Ancestry("Condition").SelectMany(o => o.Fields).FirstOrDefault(f => f.Name == "Data" && f.LayoutOffsetNumber != null)
            ?? throw Fail($"{leaf.Path}: the XML gives Condition.Data no layout offset");
        int start = dataField.LayoutOffsetNumber!.Value, off = start;
        int end = placed.Where(f => f.LayoutOffsetNumber > start).Select(f => f.LayoutOffsetNumber!.Value).DefaultIfEmpty(content.Length).Min();
        (int, int)? mine = null;
        _m.Objects.TryGetValue(obj, out var od);
        foreach (var f in od?.Fields ?? new List<FieldDef>())
        {
            if (f.Path != f.Name || f.Custom || f.Kind == "Break") continue;
            if (f.Size is not int size) throw Fail($"{leaf.Path}: the schema gives no size for {obj}.{f.Name}, so the parameters' layout isn't certain");
            if (f.Name == name) mine = (off, size);
            off += size;
        }
        if (off != end) throw Fail($"{leaf.Path}: {obj}'s parameters take {off - start} bytes, where the XML's layout leaves {end - start} (offset {start} to {end})");
        if (mine is not { } p) throw Fail($"{leaf.Path}: {obj} has no parameter {name} that Mutagen reads from the CTDA");
        // The function the record has is the one the XML's conditionFunction gives this kind of data
        if (od?.ConditionFunction is { } cf && int.TryParse(cf, out int fnWant))
        {
            var fnBytes = content.Slice(fnAt, fnSize);
            long fnHave = fnSize switch { 1 => fnBytes[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(fnBytes), _ => BinaryPrimitives.ReadUInt32LittleEndian(fnBytes) };
            if (fnHave != fnWant) throw Fail($"{leaf.Path}: the CTDA's function number is {fnHave}, and the XML gives {obj} the function {fnWant}");
            _where.Add($"the CTDA's function number (offset {fnAt}, {fnSize} bytes, Condition.{fnStep.Name}'s layout) is {fnHave}, the XML's conditionFunction of {obj}");
        }
        _where.Add($"{name} is at offset {p.Item1} of the condition's CTDA ({obj}'s parameters from Condition.Data's layout offset {start}, in their order)");
        return Leaf(s, p.Item1, p.Item2, def, en, leaf);
    }

    /// <summary>A placed field's size: the schema's, else the XML's layoutEncoding of a hand-read value ("uint16": 2).</summary>
    static int? PlacedSize(FieldDef f) => f.Size ?? f.LayoutEncoding switch
    {
        "uint8" or "int8" => 1,
        "uint16" or "int16" => 2,
        "uint32" or "int32" or "float" => 4,
        _ => null,
    };

    // ------------------------------------------------------------------ Which field of an object each subrecord is
    enum GroupKind { Single, Marker, StructObj, MultiObj, ListArray, ListSub, ListMulti, InCustom, PerkEffects, GenderedSplit, GenderedMarkers, GenderedOne, Other }

    /// <summary>One property of an object (its rows: the property, its item) and the subrecords it can take.</summary>
    sealed class Group
    {
        public required string Name;
        public required FieldDef Main;
        public FieldDef? Item;
        public ObjDef? ItemObject;
        public GroupKind Kind;
        public string? Own, Counter, ItemSig;
        public string? Ref;
        public string? Map;
        /// <summary>The field's own markers (the XML's markerType and endMarkerType on the field): empty subrecords
        /// written before and after it.</summary>
        public string? Marker, EndMarker;
        /// <summary>A gendered value: its two rows, and the markers before each half when it has them.</summary>
        public FieldDef? Male, Female;
        public string? MaleMarker, FemaleMarker;
        public HashSet<string> Sigs = new(StringComparer.Ordinal);
    }

    static readonly Regex GenderMarkers = new(@"male after marker ([A-Z0-9_]{4}), female after marker ([A-Z0-9_]{4})", RegexOptions.Compiled);

    static readonly Regex Counted = new(@"counted by ([A-Z0-9_]{4})", RegexOptions.Compiled);

    HashSet<string>? _counters;

    readonly Dictionary<(string, string?), List<Group>> _groups = new();
    readonly Dictionary<(string, string?), HashSet<string>> _objSigs = new();
    readonly Dictionary<(string, string?), HashSet<string>> _triggers = new();
    readonly HashSet<(string, string?)> _visiting = new();
    readonly HashSet<(string, string?)> _visitingTriggers = new();

    /// <summary>The object's end marker, from the schema (the XML's endMarkerType, its own or a base's).</summary>
    string? EndMarker(string obj) => _m.EndMarkerOf(obj);
    /// <summary>The object's start marker, from the schema (the XML's markerType, its own or a base's).</summary>
    string? StartMarker(string obj) => _m.MarkerOf(obj);

    /// <summary>
    /// The subrecords that follow an item's own subrecord as part of it: the other subrecords its object holds, as the
    /// XML lists them (AdditionalContainedRecordType), less any list's counter (a condition holds CITC, CIS1 and CIS2;
    /// CITC counts the list, CIS1 and CIS2 follow the CTDA). Only for an item that is one subrecord of its own. That
    /// they follow the item is how Mutagen's hand-written condition reader takes them; the XML doesn't say where they
    /// stand (GAPS Part 2).
    /// </summary>
    string[] TrailingOf(string obj)
    {
        if (!_m.Objects.TryGetValue(obj, out var od) || od.ObjType != "Subrecord" || OwnSig(obj) is not { } own) return Array.Empty<string>();
        _counters ??= _m.Objects.Values.SelectMany(o => o.Fields).Select(f => f.Note != null ? Counted.Match(f.Note) : null)
            .Where(x => x is { Success: true }).Select(x => x!.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return _m.ContainsOf(obj).Where(s => s != own && !_counters.Contains(s)).ToArray();
    }
    string? OwnSig(string obj) => _m.Ancestry(obj).Select(o => o.Sig).FirstOrDefault(x => x != null);

    List<Group> Groups(string obj, string? map)
    {
        if (_groups.TryGetValue((obj, map), out var have)) return have;
        var list = new List<Group>();
        var byName = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var (f, _) in _m.AllFields(obj))
        {
            if (f.Kind == "Break") continue;
            if (!byName.TryGetValue(f.Name, out var g))
            {
                if (f.Path != f.Name) continue;
                g = new Group { Name = f.Name, Main = f, Map = f.SigMap ?? map };
                byName[f.Name] = g; list.Add(g);
                continue;
            }
            if (f.Path == f.Name + "[*]") g.Item = f;
            else
            {
                if (f.Path == f.Name + ".Male") g.Male = f;
                else if (f.Path == f.Name + ".Female") g.Female = f;
                foreach (var s in RowSigs(f, g.Map)) g.Sigs.Add(s);
            }
        }
        var customSigs = list.Where(g => g.Main.Kind == "CustomLogic" && g.Main.Subrecord != null).Select(g => g.Main.Subrecord!).ToHashSet(StringComparer.Ordinal);
        foreach (var g in list) Classify(g, customSigs);
        _groups[(obj, map)] = list;
        return list;
    }

    void Classify(Group g, HashSet<string> customSigs)
    {
        var f = g.Main;
        string? own = SchemaModel.MapSig(g.Map, f.Subrecord);
        // A perk rank's effects: read by hand-written code from PRKE to PRKF (PerkRankBinaryCreateTranslation.ParseEffects)
        if (f.Custom && f.Kind == "List" && g.Item?.RefObject == "APerkEffect") { g.Kind = GroupKind.PerkEffects; g.Sigs.Clear(); g.Sigs.UnionWith(PerkEffectSigs); return; }
        // A list of conditions Mutagen reads by hand is still one CTDA (and its CIS1, CIS2) per item, in the record's own
        // subrecords: cut like any list (a scene phase's start and completion conditions, the latter between two NEXT)
        bool conditions = f.Kind == "List" && g.Item?.RefObject is { } ro0 && IsCondition(ro0);
        if (f.Custom && !conditions && f.Kind != "CustomLogic" && (f.Subrecord == null || customSigs.Contains(f.Subrecord))) { g.Kind = GroupKind.InCustom; g.Sigs.Clear(); return; }
        g.Marker = f.Marker; g.EndMarker = f.EndMarker;
        if (g.Marker != null) g.Sigs.Add(g.Marker);
        if (g.EndMarker != null) g.Sigs.Add(g.EndMarker);
        if (f.Note != null && Counted.Match(f.Note) is { Success: true } cm) g.Counter = cm.Groups[1].Value;
        if (g.Counter != null) g.Sigs.Add(g.Counter);
        if (f.Kind == "Marker") { g.Kind = GroupKind.Marker; g.Own = own; }
        else if (f.Kind is "List" or "Array")
        {
            var item = g.Item;
            if (item == null) { g.Kind = GroupKind.Other; return; }
            string? itemMap = item.SigMap ?? g.Map;
            if (item.RefObject == null)
            {
                string? itemSig = SchemaModel.MapSig(itemMap, item.Subrecord);
                if (own != null && itemSig == own) { g.Kind = GroupKind.ListArray; g.Own = own; }
                else if (own == null && itemSig != null) { g.Kind = GroupKind.ListSub; g.ItemSig = itemSig; }
                else g.Kind = GroupKind.Other;
            }
            else if (_m.Objects.TryGetValue(item.RefObject, out var ro))
            {
                g.ItemObject = ro; g.Ref = ro.Name;
                string? roSig = SchemaModel.MapSig(itemMap, OwnSig(ro.Name));
                if (ro.Struct && own == null && roSig != null) { g.Kind = GroupKind.ListSub; g.ItemSig = roSig; }
                else if (ro.Struct && own != null) { g.Kind = GroupKind.ListArray; g.Own = own; }
                else if (!ro.Struct) g.Kind = GroupKind.ListMulti;
                else g.Kind = GroupKind.Other;
                foreach (var s in ObjSigs(ro.Name, itemMap)) g.Sigs.Add(s);
            }
            else g.Kind = GroupKind.Other;
            if (g.Own != null) g.Sigs.Add(g.Own);
            if (g.ItemSig != null) g.Sigs.Add(g.ItemSig);
            return;
        }
        else if (f.Kind == "Object" && f.RefObject != null && _m.Objects.TryGetValue(f.RefObject, out var ro))
        {
            g.Ref = ro.Name;
            if (ro.Struct) { g.Kind = GroupKind.StructObj; g.Own = own ?? SchemaModel.MapSig(g.Map, OwnSig(ro.Name)); }
            else g.Kind = GroupKind.MultiObj;
            foreach (var s in ObjSigs(ro.Name, g.Map)) g.Sigs.Add(s);
        }
        else if (f.Kind == "Gendered" && g.Male != null && g.Female != null && !f.Custom)
        {
            // How Mutagen's gendered reader takes the pair (GenderedItemBinaryTranslation):
            //  - after a marker each: each half is the content of its marker subrecord (a plain value);
            //  - in one subrecord of its own: the male value, then the female one;
            //  - otherwise each half is an object read with its own signatures (MaleTypeOverrides, FemaleTypeOverrides)
            var mm = f.Note != null ? GenderMarkers.Match(f.Note) : null;
            if (mm is { Success: true })
            {
                g.MaleMarker = mm.Groups[1].Value; g.FemaleMarker = mm.Groups[2].Value;
                g.Sigs.Clear(); g.Sigs.Add(g.MaleMarker); g.Sigs.Add(g.FemaleMarker);
                g.Kind = g.Male.RefObject == null && g.Female.RefObject == null ? GroupKind.GenderedMarkers : GroupKind.Other;
            }
            else if (own != null) { g.Kind = GroupKind.GenderedOne; g.Own = own; }
            else if (g.Male.RefObject != null && g.Female.RefObject != null && (f.Note == null || !f.Note.Contains("gender given by", StringComparison.Ordinal))) g.Kind = GroupKind.GenderedSplit;
            else g.Kind = GroupKind.Other;
        }
        else if (f.Kind is "Gendered" or "Dictionary" or "Array2D") g.Kind = GroupKind.Other;
        else { g.Kind = GroupKind.Single; g.Own = own; }
        if (g.Own != null) g.Sigs.Add(g.Own);
    }

    IEnumerable<string> RowSigs(FieldDef f, string? map)
    {
        string? m = f.SigMap ?? map;
        if (SchemaModel.MapSig(m, f.Subrecord) is { } s) yield return s;
        if (f.RefObject != null && _m.Objects.ContainsKey(f.RefObject))
            foreach (var x in ObjSigs(f.RefObject, m)) yield return x;
    }

    /// <summary>Every signature an object (or any kind of it) can hold.</summary>
    HashSet<string> ObjSigs(string obj, string? map)
    {
        if (_objSigs.TryGetValue((obj, map), out var have)) return have;
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!_visiting.Add((obj, map))) return set;
        try
        {
            foreach (var o in new[] { obj }.Concat(_m.Variants(obj)))
            {
                if (SchemaModel.MapSig(map, OwnSig(o)) is { } own) set.Add(own);
                if (StartMarker(o) is { } start) set.Add(start);
                if (EndMarker(o) is { } end) set.Add(end);
                foreach (var t in TrailingOf(o)) set.Add(t);
                foreach (var g in Groups(o, map)) set.UnionWith(g.Sigs);
            }
        }
        finally { _visiting.Remove((obj, map)); }
        return _objSigs[(obj, map)] = set;
    }

    /// <summary>The signatures an item of an object can start with.</summary>
    HashSet<string> Triggers(string obj, string? map)
    {
        if (_triggers.TryGetValue((obj, map), out var have)) return have;
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!_visitingTriggers.Add((obj, map))) return set;
        if (_m.Objects.TryGetValue(obj, out var od) && od.Struct) { if (SchemaModel.MapSig(map, OwnSig(obj)) is { } s) set.Add(s); }
        else
            foreach (var o in new[] { obj }.Concat(_m.Variants(obj)))
            {
                // An object with a start marker starts with it
                if (StartMarker(o) is { } start) { set.Add(start); continue; }
                bool marker = EndMarker(o) != null;
                foreach (var g in Groups(o, map))
                {
                    if (g.Sigs.Count == 0) continue;
                    if (g.Marker != null) set.Add(g.Marker);
                    else if (g.Kind is GroupKind.ListMulti or GroupKind.MultiObj && g.Ref != null) set.UnionWith(g.Counter != null ? new[] { g.Counter } : Triggers(g.Ref, g.Map));
                    else set.UnionWith(g.Sigs);
                    // An object with an end marker starts with its first subrecord; any other with its first one that is always there
                    if (marker || (!g.Main.Nullable && !(g.Main.Kind == "Bool" && g.Main.Size == 0))) break;
                }
            }
        _visitingTriggers.Remove((obj, map));
        return _triggers[(obj, map)] = set;
    }

    List<Group>? _lastGroups;
    readonly Dictionary<int, List<(int, int)>> _items = new();
    /// <summary>For a gendered value read in halves: the runs of subrecords each half was read from.</summary>
    readonly Dictionary<int, (List<(int, int)> Male, List<(int, int)> Female)> _halves = new();

    /// <summary>Which property of the object takes each subrecord in [from, to): an index into <see cref="Groups"/>, or -1.</summary>
    int[] Claim(string obj, string? map, int from, int to)
    {
        var groups = Groups(obj, map);
        _lastGroups = groups;
        _items.Clear();
        _halves.Clear();
        var claims = new int[to - from];
        Array.Fill(claims, -1);
        int cur = -1, i = from;
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (i < to)
        {
            int g = Choose(groups, Sig(i), cur, done);
            if (g < 0) { i++; continue; }
            int end = Consume(groups[g], i, to, _items, g);
            Done(groups, g, done);
            for (int x = i; x < end; x++) claims[x - from] = g;
            cur = g; i = end;
        }
        return claims;
    }

    /// <summary>Notes that group <paramref name="g"/> took its subrecord: a plain field by its signature (the fields of
    /// one data block share it), a marker field by itself.</summary>
    static void Done(List<Group> groups, int g, HashSet<string> done)
    {
        var x = groups[g];
        if (x.Kind == GroupKind.Marker) done.Add("#" + g.ToString(CultureInfo.InvariantCulture));
        else if (x.Kind is GroupKind.Single or GroupKind.StructObj or GroupKind.GenderedOne && x.Own != null) done.Add(x.Own);
    }

    /// <param name="done">Signatures a plain field of this object has had: the fields of one data block share their
    /// subrecord, so a second one is another item's (or no field's), not the next field of the block.</param>
    int Choose(List<Group> groups, string s, int cur, HashSet<string> done)
    {
        int first = -1;
        for (int g = 0; g < groups.Count; g++)
        {
            if (!Accepts(groups[g], s)) continue;
            if (groups[g].Kind is GroupKind.Single or GroupKind.StructObj or GroupKind.GenderedOne && done.Contains(s)) continue;
            // A marker field is taken once; another marker field with the same signature takes the next one (a scene
            // phase's StartMarker and EndMarker are both HNAM)
            if (groups[g].Kind == GroupKind.Marker && done.Contains("#" + g.ToString(CultureInfo.InvariantCulture))) continue;
            if (g > cur) return g;
            if (first < 0) first = g;
        }
        return first;
    }

    bool Accepts(Group g, string s) => (g.Marker != null && s == g.Marker) || AcceptsBody(g, s);

    bool AcceptsBody(Group g, string s) => g.Kind switch
    {
        GroupKind.Single or GroupKind.Marker or GroupKind.StructObj => s == g.Own,
        GroupKind.ListArray => s == g.Own || s == g.Counter,
        GroupKind.ListSub => s == g.ItemSig || s == g.Counter,
        GroupKind.ListMulti => s == g.Counter || (g.Ref != null && Triggers(g.Ref, g.Item?.SigMap ?? g.Map).Contains(s)),
        GroupKind.MultiObj => g.Sigs.Contains(s),
        GroupKind.PerkEffects => s == "PRKE",
        GroupKind.GenderedOne => s == g.Own,
        GroupKind.GenderedMarkers => s == g.MaleMarker || s == g.FemaleMarker,
        GroupKind.GenderedSplit => g.Sigs.Contains(s),
        GroupKind.Other => g.Sigs.Contains(s),
        _ => false,
    };

    /// <summary>Takes the subrecords a property reads from position <paramref name="i"/>, with its markers; gives where it stops.</summary>
    int Consume(Group g, int i, int to, Dictionary<int, List<(int, int)>> items, int gi)
    {
        if (g.Marker != null && Sig(i) == g.Marker)
        {
            // The field's start marker, then the field itself when it follows
            if (i + 1 >= to || !AcceptsBody(g, Sig(i + 1)))
            {
                // No body: the field's end marker may follow at once (a scene phase's empty completion conditions: NEXT NEXT)
                return g.EndMarker != null && i + 1 < to && Sig(i + 1) == g.EndMarker ? i + 2 : i + 1;
            }
            i++;
        }
        int end = ConsumeBody(g, i, to, items, gi);
        if (g.EndMarker != null && end < to && Sig(end) == g.EndMarker) end++;
        return end;
    }

    int ConsumeBody(Group g, int i, int to, Dictionary<int, List<(int, int)>> items, int gi)
    {
        switch (g.Kind)
        {
            case GroupKind.ListArray:
                if (Sig(i) == g.Counter) return i + 1 < to && Sig(i + 1) == g.Own ? i + 2 : i + 1;
                return i + 1;
            case GroupKind.ListSub:
            {
                int p = i;
                if (Sig(p) == g.Counter) p++;
                var list = items.TryGetValue(gi, out var l) ? l : items[gi] = new();
                var trailing = g.Ref != null ? TrailingOf(g.Ref) : Array.Empty<string>();
                while (p < to && Sig(p) == g.ItemSig)
                {
                    int e = p + 1;
                    while (e < to && trailing.Contains(Sig(e))) e++;
                    list.Add((p, e)); p = e;
                }
                return Math.Max(p, i + 1);
            }
            case GroupKind.ListMulti:
            {
                int p = i;
                long? n = null;
                if (Sig(p) == g.Counter) { n = CounterValue(p); p++; }
                var list = items.TryGetValue(gi, out var l) ? l : items[gi] = new();
                string? itemMap = g.Item?.SigMap ?? g.Map;
                var triggers = Triggers(g.Ref!, itemMap);
                while (p < to && (n != null ? list.Count < n : triggers.Contains(Sig(p))))
                {
                    int e = ParseItem(g.Ref!, itemMap, p, to);
                    if (e <= p) break;
                    list.Add((p, e)); p = e;
                }
                return Math.Max(p, i + 1);
            }
            case GroupKind.MultiObj:
                return Math.Max(ParseItem(g.Ref!, g.Map, i, to), i + 1);
            case GroupKind.GenderedMarkers:
            {
                // At most a male and a female marker subrecord, each holding its half
                int p = i;
                for (int n = 0; n < 2 && p < to && (Sig(p) == g.MaleMarker || Sig(p) == g.FemaleMarker); n++) p++;
                return Math.Max(p, i + 1);
            }
            case GroupKind.GenderedSplit:
                return Math.Max(ConsumeHalves(g, i, to, gi), i + 1);
            case GroupKind.PerkEffects:
            {
                // PerkRankBinaryCreateTranslation.ParseEffects: each effect is a PRKE, then what ReadPayload takes (any
                // of DATA, EPF2, EPF3, EPFD, EPFT, EPFB and the conditions' PRKC, CTDA, CIS1, CIS2), then an EPFT and a
                // PRKF when they are there
                var list = items.TryGetValue(gi, out var l) ? l : items[gi] = new();
                int p = i;
                while (p < to && Sig(p) == "PRKE")
                {
                    // An effect runs to its PRKF, or to the next PRKE or a subrecord no effect holds (the layout's Segments)
                    int e = p + 1;
                    while (e < to && Sig(e) != "PRKE" && PerkEffectSigs.Contains(Sig(e))) { e++; if (Sig(e - 1) == "PRKF") break; }
                    list.Add((p, e)); p = e;
                }
                return Math.Max(p, i + 1);
            }
            default:
                return i + 1;
        }
    }

    /// <summary>
    /// One call of Mutagen's reader of a gendered pair split by signatures (<c>GenderedItemBinaryTranslation.Parse</c>
    /// with record type converters, and <c>ParseHalf</c> as changed in fixes.12, C155/C156): the male half, then the
    /// female one. A half not read yet reads what follows (nothing, when what follows isn't its own); a half already
    /// read takes what follows when it starts with one of the half's own signatures, or, once both halves are read,
    /// whatever follows. The record's reader calls it again each time a signature of the pair comes up later.
    /// </summary>
    int ConsumeHalves(Group g, int p, int to, int gi)
    {
        if (!_halves.TryGetValue(gi, out var h)) _halves[gi] = h = (new List<(int, int)>(), new List<(int, int)>());
        p = ReadHalf(g.Male!, h.Male, h.Female, p, to);
        p = ReadHalf(g.Female!, h.Female, h.Male, p, to);
        return p;
    }

    int ReadHalf(FieldDef part, List<(int, int)> held, List<(int, int)> other, int p, int to)
    {
        if (p >= to) return p;
        // The half's own signatures: those its converter maps to (none when it has no converter)
        bool own = part.SigMap != null && part.SigMap.Split(',').Any(m => m.EndsWith("=" + Sig(p), StringComparison.Ordinal));
        if (held.Count == 0 || own || other.Count > 0)
        {
            int e = ParseItem(part.RefObject!, part.SigMap, p, to);
            if (e > p) { held.Add((p, e)); return e; }
        }
        return p;
    }

    long? CounterValue(int index)
    {
        var c = _raw.Content(_raw.Subrecords[index]);
        return c.Length switch { 1 => c[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(c), 4 => BinaryPrimitives.ReadUInt32LittleEndian(c), 8 => (long)BinaryPrimitives.ReadUInt64LittleEndian(c), _ => null };
    }

    /// <summary>Where one item of an object that is several subrecords ends, starting at <paramref name="p"/>.</summary>
    int ParseItem(string obj, string? map, int p, int to)
    {
        var sigs = ObjSigs(obj, map);
        // An item that starts with a marker (XWPK before a grouped pack-in) begins after it; its end marker may be the same signature
        if (StartMarker(obj) is { } start && p < to && Sig(p) == start) p++;
        if (EndMarker(obj) is { } end)
        {
            for (int j = p; j < to; j++)
            {
                if (Sig(j) == end) return j + 1;
                if (!sigs.Contains(Sig(j))) return j;
            }
            return to;
        }
        // Without an end marker: the item's fields in order; it ends at a subrecord it can't hold or a field it already had
        var groups = new List<Group>();
        foreach (var o in new[] { obj }.Concat(_m.Variants(obj))) groups.AddRange(Groups(o, map).Where(g => !groups.Any(x => x.Name == g.Name)));
        var scratch = new Dictionary<int, List<(int, int)>>();
        int cur = -1, i = p;
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (i < to)
        {
            string s = Sig(i);
            if (!sigs.Contains(s)) break;
            int g = Choose(groups, s, cur, done);
            if (g < 0 || g <= cur) break;
            i = Consume(groups[g], i, to, scratch, g);
            Done(groups, g, done);
            cur = g;
        }
        return i;
    }

    static string Ordinal(int n) => (n % 100) is 11 or 12 or 13 ? n + "th" : (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };
}
