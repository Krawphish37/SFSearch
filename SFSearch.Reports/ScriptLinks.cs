// Links that sit in script properties (a record's VirtualMachineAdapter): which script and which property hold one,
// and what a leveled-list injector script adds the linked record to. sources uses it to tell a quest that sells an
// item (its script adds the item's list to a vendor's list) from one that only names it; refs uses it to say the same
// in plain words beside a script-property row. Both read the record's one JSON form (RecordEngine.Json).
// An injector is told by what its compiled code does (ScriptInjections, below): a call of AddForm on a leveled list or a
// form list held in a script property, with a record held in a script property. The records that attach the script
// give both values; a value that is an entry of a form list held in a property (FormList.GetAt) gives the form list's
// entries, read on the form list as the game uses it. When the code can't be read, or shows no such call for the link's
// property, the older rules decide: a struct that pairs the item with a list, or a script whose name says it injects.
// Each answer says which.
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFSearch.Reports;

/// <summary>One link in a script property.</summary>
/// <param name="Path">The link's path in the record's JSON, list indexes included.</param>
/// <param name="Script">The script's name ("" when the record gives none, e.g. an empty quest fragment script).</param>
/// <param name="Property">The property's name.</param>
/// <param name="Member">For a struct property: the struct member that holds the link.</param>
/// <param name="Alias">For a script on a quest alias: the alias's ID, when the record gives it.</param>
/// <param name="Targets">For a leveled-list injector script: the leveled lists it adds the linked record to (FormKeys).</param>
/// <param name="FoundBy">How the injector was told, when there are targets: "code", "struct" or "name" (<see cref="ListTargets"/>).</param>
/// <param name="How">The same in words: "found in its code: LeveledItem.AddForm in OnQuestInit".</param>
/// <param name="Entries">True when the code adds the entries of the form list the property holds, not the form list itself.</param>
/// <param name="AddedBy">The form lists that add their entries, through AddToList, to the form lists whose entries are the targets (FormKeys).</param>
public sealed record ScriptLink(string Path, string Script, string Property, string? Member, int? Alias, IReadOnlyList<string> Targets, string? FoundBy = null, string? How = null, bool Entries = false,
    IReadOnlyList<string>? AddedBy = null);

/// <summary>What an injector script adds a linked record to.</summary>
/// <param name="Script">The script's name.</param>
/// <param name="Targets">The lists (FormKeys).</param>
/// <param name="FoundBy">"code": its compiled code calls AddForm on a list held in a property, with the linked
/// property's record. "struct": a struct pairs the record with a list. "name": the script's name says it injects. Null
/// when there are no targets.</param>
/// <param name="How">The same in words, with why the code didn't decide when it didn't.</param>
/// <param name="Entries">True when the code adds the entries of the form list the property holds (GetAt, GetArray), not the form list itself.</param>
/// <param name="ListsRead">The form lists whose entries are the target lists (a list the code takes as a form list's
/// entry), read with the entries callback.</param>
/// <param name="AddedBy">The form lists that add their entries, through AddToList, to those form lists (FormKeys): some
/// of the targets are theirs. Empty when none does.</param>
public sealed record ListTargets(string Script, List<string> Targets, string? FoundBy, string? How, bool Entries = false, int ListsRead = 0, IReadOnlyList<string>? AddedBy = null);

/// <summary>Where a value a script's code uses is held: a script property, a member of its struct (of each element,
/// for an array of structs), an element of its array, or (<paramref name="Entries"/>) an entry of the form list it
/// holds, got with GetAt or GetArray.</summary>
public sealed record CodeHolder(string Property, string? Member, bool Element, bool Entries = false);

/// <summary>One call in a compiled script that adds a record to a list: AddForm on a leveled list or a form list,
/// with the properties the list and the record can come from.</summary>
/// <param name="Script">The script whose code makes the call (the attached script, or a script it extends).</param>
/// <param name="Call">LeveledItem.AddForm, FormList.AddForm …</param>
public sealed record Injection(string Script, string Function, string Call, IReadOnlyList<CodeHolder> Lists, IReadOnlyList<CodeHolder> Items)
{
    public string Shown => $"{Call} in {Function}";
}

/// <summary>A script's injections, as its compiled code and the scripts it extends give them.</summary>
/// <param name="Where">"loose", or the archive that holds the compiled script.</param>
/// <param name="Problem">Why the code couldn't be read (no compiled script found, a file that isn't one). The
/// injections are then empty and the older rules decide.</param>
public sealed record CodeLookup(IReadOnlyList<Injection> Injections, string? Where, string? Problem);

public static partial class ScriptLinks
{
    [GeneratedRegex(@"^(VirtualMachineAdapter\.(?:Scripts\[\d+\]|Script|Aliases\[(\d+)\]\.Scripts\[\d+\]))\.Properties\[(\d+)\]")] private static partial Regex ScriptProperty();
    [GeneratedRegex(@"^(.*\.Structs\[\d+\])\.Members\[(\d+)\]")] private static partial Regex StructMember();
    [GeneratedRegex("AddItemToLeveledList|LLInjector|Inject", RegexOptions.IgnoreCase)] private static partial Regex InjectorName();
    [GeneratedRegex(@"^(\w+)(?:\[(\d+)\])?$")] private static partial Regex PathPart();
    [GeneratedRegex(@"\[\d+\]")] private static partial Regex ListIndex();
    [GeneratedRegex(@"^(?:what property (?<p>\w+)\.GetAt\(\) returns|an element of what property (?<p>\w+)\.GetArray\(\) returns)$", RegexOptions.IgnoreCase)] private static partial Regex FormListEntry();

    /// <summary>True when a link's path (with or without list indexes) is inside a script property.</summary>
    public static bool IsScriptProperty(string path) =>
        path.StartsWith("VirtualMachineAdapter", StringComparison.Ordinal) && path.Contains(".Properties[", StringComparison.Ordinal);

    /// <summary>A path with its list indexes written [*], as refs and the link index write fields.</summary>
    public static string Unindexed(string path) => ListIndex().Replace(path, "[*]");

    /// <summary>
    /// Every link to <paramref name="targetFormKey"/> in the record's script properties: the script, the property,
    /// the struct member, and for an injector script the leveled lists it adds the target to.
    /// </summary>
    /// <param name="signatureOf">A FormKey's record type (its signature), or null when it isn't known.</param>
    /// <param name="code">A script's injections from its compiled code (<see cref="ScriptInjections.Of"/>); null: only the older rules.</param>
    /// <param name="isList">Which record types count as the lists an injector adds to (default: leveled item lists).</param>
    /// <param name="entriesOf">A form list's entries (FormKeys), as the game uses the form list; null when it can't be read.
    /// For an injector whose target lists are a form list's entries; without it those lists aren't named.</param>
    public static List<ScriptLink> Into(JsonObject record, string targetFormKey, Func<string, string?> signatureOf, Func<string, CodeLookup?>? code = null, Func<string?, bool>? isList = null,
        Func<string, ListEntries?>? entriesOf = null)
    {
        var found = new List<ScriptLink>();
        string target = ReportSession.Canon(targetFormKey);
        void Walk(JsonNode? n, string path)
        {
            switch (n)
            {
                case JsonValue when ReportSession.IsLink(n, out var key):
                    if (ReportSession.Canon(key).Equals(target, StringComparison.OrdinalIgnoreCase) && Describe(record, path, signatureOf, code, isList, entriesOf) is { } link) found.Add(link);
                    break;
                case JsonObject o: foreach (var (name, x) in o) Walk(x, path + "." + name); break;
                case JsonArray a: for (int i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]"); break;
            }
        }
        Walk(record["VirtualMachineAdapter"], "VirtualMachineAdapter");
        return found;
    }

    /// <summary>The script and property that hold the link at <paramref name="path"/>; null when it isn't in a script property.</summary>
    public static ScriptLink? Describe(JsonObject record, string path, Func<string, string?> signatureOf, Func<string, CodeLookup?>? code = null, Func<string?, bool>? isList = null,
        Func<string, ListEntries?>? entriesOf = null)
    {
        var m = ScriptProperty().Match(path);
        if (!m.Success) return null;
        var script = Navigate(record, m.Groups[1].Value);
        var property = script?["Properties"] is JsonArray props && int.Parse(m.Groups[3].Value) is var pi && pi < props.Count ? props[pi] : null;
        string? member = null;
        if (StructMember().Match(path) is { Success: true } sm && Navigate(record, sm.Groups[1].Value)?["Members"] is JsonArray members && int.Parse(sm.Groups[2].Value) is var mi && mi < members.Count)
            member = ReportSession.Str(members[mi]?["Name"]);
        int? alias = null;
        if (m.Groups[2].Success && Navigate(record, $"VirtualMachineAdapter.Aliases[{m.Groups[2].Value}].Property")?["Alias"] is JsonValue av && av.TryGetValue<int>(out int id)) alias = id;
        var t = AddToListTargets(record, path, signatureOf, code, isList, entriesOf);
        return new ScriptLink(path, t.Script, ReportSession.Str(property?["Name"]) ?? "", member, alias, t.Targets, t.FoundBy, t.How, t.Entries, t.AddedBy);
    }

    /// <summary>The record types an injector adds to by default: leveled item lists (what sources follows to vendors).</summary>
    public static bool LeveledItemList(string? signature) => signature == "LVLI";
    /// <summary>Leveled lists of any kind and form lists: what AddForm can add to (refs names them all).</summary>
    public static bool AnyList(string? signature) => signature is "LVLI" or "LVLN" or "LVSP" or "LVLP" or "LVLB" or "LVSC" or "FLST";

    /// <summary>
    /// What a leveled-list injector script adds the record linked at <paramref name="path"/> to, and how that was told.
    /// First its compiled code (<paramref name="code"/>): a call of AddForm on a list held in a property, whose record
    /// is held in the link's property (and struct member). The lists are that property's values on this record; for a
    /// struct pairing (an array of structs whose members hold the list and the record) the same struct's member. When
    /// the code can't be read, or shows no such call for this property, the older rules: a struct that pairs the
    /// record with a list (the other members of the same struct), or a script whose name says it adds to a leveled list
    /// (its other properties). A list the code takes as an entry of a form list held in a property is each entry of that
    /// form list (<paramref name="entriesOf"/>), paired with the record by the call that passes both.
    /// </summary>
    public static ListTargets AddToListTargets(JsonObject record, string path, Func<string, string?> signatureOf, Func<string, CodeLookup?>? code = null, Func<string?, bool>? isList = null,
        Func<string, ListEntries?>? entriesOf = null)
    {
        isList ??= LeveledItemList;
        var result = new List<string>();
        var m = ScriptProperty().Match(path);
        if (!m.Success) return new ListTargets("", result, null, null);
        var script = Navigate(record, m.Groups[1].Value);
        string name = ReportSession.Str(script?["Name"]) ?? "";
        var props = script?["Properties"] as JsonArray;
        int own = int.Parse(m.Groups[3].Value);
        string? ownName = props != null && own < props.Count ? ReportSession.Str(props[own]?["Name"]) : null;
        var sm = StructMember().Match(path);
        string? ownMember = null;
        JsonObject? ownStruct = null;
        if (sm.Success && Navigate(record, sm.Groups[1].Value) is JsonObject so && so["Members"] is JsonArray ms && int.Parse(sm.Groups[2].Value) is var mi && mi < ms.Count)
        {
            ownStruct = so;
            ownMember = ReportSession.Str(ms[mi]?["Name"]);
        }

        // The lists a property's value names; with entries, the lists that are entries of the form lists it names
        var formLists = new List<string>();
        var addedBy = new List<string>();
        void AddLists(JsonNode? n, bool entries = false)
        {
            switch (n)
            {
                case JsonValue when ReportSession.IsLink(n, out var key):
                    string k = ReportSession.Canon(key);
                    if (entries)
                    {
                        if (entriesOf == null || signatureOf(k) != "FLST" || entriesOf(k) is not { } inside) break;
                        if (!formLists.Contains(k, StringComparer.OrdinalIgnoreCase)) formLists.Add(k);
                        foreach (var a in inside.AddedBy) if (!addedBy.Contains(a, StringComparer.OrdinalIgnoreCase)) addedBy.Add(a);
                        foreach (var e in inside.Entries)
                        {
                            string ek = ReportSession.Canon(e);
                            if (isList(signatureOf(ek)) && !result.Contains(ek, StringComparer.OrdinalIgnoreCase)) result.Add(ek);
                        }
                    }
                    else if (isList(signatureOf(k)) && !result.Contains(k, StringComparer.OrdinalIgnoreCase)) result.Add(k);
                    break;
                case JsonObject o: foreach (var (_, x) in o) AddLists(x, entries); break;
                case JsonArray a: foreach (var x in a) AddLists(x, entries); break;
            }
        }
        // The members of that name in a struct, or in each struct of a property
        void AddMembers(JsonNode? holder, string member, bool entries = false)
        {
            if (holder?["Members"] is JsonArray members)
            {
                foreach (var x in members) if (string.Equals(ReportSession.Str(x?["Name"]), member, StringComparison.OrdinalIgnoreCase)) AddLists(x, entries);
                return;
            }
            if (holder?["Structs"] is JsonArray structs) foreach (var st in structs) AddMembers(st, member, entries);
        }
        JsonNode? Property(string propertyName) => props?.FirstOrDefault(x => string.Equals(ReportSession.Str(x?["Name"]), propertyName, StringComparison.OrdinalIgnoreCase));

        // 1. The code
        string why = "";
        if (code != null && ownName != null && name.Length > 0)
        {
            var lookup = code(name);
            if (lookup == null || lookup.Problem != null) why = lookup?.Problem is { } problem ? $" ({problem})" : "";
            else
            {
                var used = new List<Injection>();
                bool entries = false;
                foreach (var inj in lookup.Injections)
                {
                    var holds = inj.Items.Where(h => string.Equals(h.Property, ownName, StringComparison.OrdinalIgnoreCase)
                        && (h.Member == null ? ownMember == null : string.Equals(h.Member, ownMember, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (holds.Count == 0) continue;
                    // The entries of the form list the property holds: what the record links is the form list
                    entries |= holds.All(h => h.Entries);
                    used.Add(inj);
                    foreach (var list in inj.Lists)
                    {
                        // A struct that holds both: the same struct's member (the element the code pairs them by)
                        if (list.Member != null && ownStruct != null && string.Equals(list.Property, ownName, StringComparison.OrdinalIgnoreCase)) AddMembers(ownStruct, list.Member, list.Entries);
                        else if (list.Member != null) AddMembers(Property(list.Property), list.Member, list.Entries);
                        else AddLists(Property(list.Property), list.Entries);
                    }
                }
                // The code's lists are the entries of form lists that were read: the answer, even when they hold no list
                // (the script then adds it to nothing; the older rules would name lists the code doesn't add to)
                if (result.Count > 0 || (formLists.Count > 0 && used.SelectMany(u => u.Lists).All(l => l.Entries)))
                {
                    var shown = used.Select(u => u.Shown).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    // The target lists that are a form list's entries: in which properties' form lists
                    var inLists = used.SelectMany(u => u.Lists).Where(l => l.Entries).Select(l => l.Member != null ? $"{l.Property}.{l.Member}" : l.Property).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    return new ListTargets(name, result, "code", $"found in its code: {string.Join(", ", shown.Take(2))}{(shown.Count > 2 ? $" and {shown.Count - 2} more calls" : "")}"
                        + (entries ? $", for each entry of the form list in {ownName}{(ownMember != null ? "." + ownMember : "")}" : "")
                        + (formLists.Count > 0 ? $", to each list in the form list{(formLists.Count == 1 ? "" : "s")} in {string.Join(", ", inLists)}" : "")
                        + (addedBy.Count > 0 ? $" (with the entries {(addedBy.Count == 1 ? "another form list adds" : $"{addedBy.Count} other form lists add")} to {(formLists.Count == 1 ? "it" : "them")} through AddToList)" : "")
                        + (result.Count == 0 ? $" ({(formLists.Count == 1 ? "it holds" : "they hold")} no list)" : ""), entries, formLists.Count, addedBy);
                }
                why = used.Count > 0 ? $" (its code adds it to a list in {string.Join(", ", used.SelectMany(u => u.Lists).Select(l => l.Property).Distinct(StringComparer.OrdinalIgnoreCase))}, which this record leaves empty{(used.SelectMany(u => u.Lists).Any(l => l.Entries) && entriesOf == null ? " or holds as a form list's entries, which weren't read" : "")})"
                    : $" (its code adds nothing held in {ownName}{(ownMember != null ? "." + ownMember : "")} to a list)";
            }
        }

        // 2. A struct that pairs the two: the other members of the same struct
        if (ownStruct?["Members"] is JsonArray members && members.Count > 1)
        {
            int self = int.Parse(sm.Groups[2].Value);
            for (int i = 0; i < members.Count; i++) if (i != self) AddLists(members[i]);
            if (result.Count > 0) return new ListTargets(name, result, "struct", "found by a struct that pairs it with the list" + why);
        }
        // 3. The whole script: only for scripts that say what they do, so a big quest script's other properties don't count
        if (!InjectorName().IsMatch(name) || props == null) return new ListTargets(name, result, null, null);
        for (int i = 0; i < props.Count; i++) if (i != own) AddLists(props[i]);
        return result.Count > 0 ? new ListTargets(name, result, "name", "found by the script's name" + why) : new ListTargets(name, result, null, null);
    }

    // ------------------------------------------------------------------ The code: what a compiled script adds to lists
    /// <summary>The Papyrus types whose AddForm adds to a list (LeveledItem.AddForm, FormList.AddForm …).</summary>
    public static readonly HashSet<string> ListTypes = new(StringComparer.OrdinalIgnoreCase) { "LeveledItem", "LeveledActor", "LeveledSpaceshipBase", "LeveledPackIn", "LeveledBaseForm", "FormList" };

    /// <summary>
    /// The injections of each object of a compiled script, with its parent: every AddForm called on a list (a value
    /// the code declares as LeveledItem, FormList …) whose list and record come from script properties: the property,
    /// a member of its struct, an element of its array. They are followed along the code's branches and through the
    /// parameters of the script's own functions (<see cref="ScriptCode"/>). A list or record got any other way
    /// (GetFormFromFile, what a function returns) isn't held by the attaching record and isn't taken.
    /// </summary>
    public static Dictionary<string, (string Parent, List<Injection> Injections)> Injections(PexFile file) => Injections(file, Array.Empty<PexFile>());

    /// <summary>
    /// The same, with the compiled scripts <paramref name="file"/>'s code calls (<see cref="CalledScripts"/>): an AddForm
    /// in a function of one of them, on values the file's script passes in from its properties, is the file's script's
    /// injection ("LeveledItem.AddForm in Lib.Inject"). Each AddForm's list and record are paired by the calls that pass
    /// them (<see cref="ScriptCall.Pairs"/>): one injection per pair.
    /// </summary>
    public static Dictionary<string, (string Parent, List<Injection> Injections)> Injections(PexFile file, IReadOnlyList<PexFile> called)
    {
        var result = new ScriptCodeResult();
        ScriptCode.Read(file, "", result);
        foreach (var f in called) ScriptCode.Read(f, "", result);
        return Injections(result, file, called);
    }

    /// <summary>The same, over <paramref name="result"/>, which already holds <paramref name="file"/>'s code and then
    /// each of <paramref name="called"/>'s, read in that order (<see cref="ScriptCode.Read"/>): each script's code is
    /// read once.</summary>
    internal static Dictionary<string, (string Parent, List<Injection> Injections)> Injections(ScriptCodeResult result, PexFile file, IReadOnlyList<PexFile> called)
    {
        var objects = file.Objects.Concat(called.SelectMany(f => f.Objects)).Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ScriptCode.ResolveParameters(result, s => objects.Contains(s) ? objects : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { s });
        var by = new Dictionary<string, (string Parent, List<Injection> Injections)>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in file.Objects) by.TryAdd(o.Name, (o.Parent, new List<Injection>()));
        foreach (var c in result.Calls)
        {
            if (c.Kind != "give" || !c.Call.Equals("AddForm", StringComparison.OrdinalIgnoreCase) || c.On == null || c.Values.Count == 0) continue;
            string? type = PexObject.ScriptOfType(c.OnType);
            if (type == null || !ListTypes.TryGetValue(type, out var listType)) continue;
            foreach (var (item, on) in c.Pairs ?? new[] { (c.Values[0], c.On) })
            {
                // The script whose properties hold the record: the call's own, or (an AddForm in a script the code calls)
                // the script that passes them in. Its records give the values; another script's properties don't count.
                string? owner = item.FirstOrDefault(v => HolderOf(v) != null)?.Script;
                if (owner == null || !by.TryGetValue(owner, out var o)) continue;
                var lists = on.Where(v => string.Equals(v.Script, owner, StringComparison.OrdinalIgnoreCase)).Select(HolderOf).OfType<CodeHolder>().Distinct().ToList();
                var items = item.Where(v => string.Equals(v.Script, owner, StringComparison.OrdinalIgnoreCase)).Select(HolderOf).OfType<CodeHolder>().Distinct().ToList();
                if (lists.Count == 0 || items.Count == 0) continue;
                string function = c.Script.Equals(owner, StringComparison.OrdinalIgnoreCase) ? c.Function : $"{c.Script[(c.Script.LastIndexOf(':') + 1)..]}.{c.Function}";
                var inj = new Injection(c.Script, function, listType + ".AddForm", lists, items);
                if (!o.Injections.Any(x => x.Function == inj.Function && x.Lists.SequenceEqual(inj.Lists) && x.Items.SequenceEqual(inj.Items))) o.Injections.Add(inj);
            }
        }
        return by;
    }

    /// <summary>
    /// The scripts whose functions <paramref name="file"/>'s code calls with a value from a script property (its own, or a
    /// parameter of its function, which its callers may give one): a library function that may hold the AddForm.
    /// Calls of the file's own scripts, of the list types (AddForm itself) and the give calls aren't counted.
    /// </summary>
    public static List<string> CalledScripts(PexFile file)
    {
        var result = new ScriptCodeResult();
        ScriptCode.Read(file, "", result);
        return CalledScripts(file, result.Sites);
    }

    /// <summary>The same, from the call sites <see cref="ScriptCode.Read"/> gave for <paramref name="file"/>'s code.</summary>
    internal static List<string> CalledScripts(PexFile file, IEnumerable<CallSite> sites)
    {
        var own = file.Objects.SelectMany(o => new[] { o.Name, o.Parent }).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sites.Where(s => !own.Contains(s.Target) && !ListTypes.Contains(s.Target) && !ScriptCode.Givers.Contains(s.Function)
                && s.Args.Any(a => a.Any(v => v.Kind is ValueOrigin.Property or ValueOrigin.Parameter)))
            .Select(s => s.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// What a compiled script's instructions show without following any value: whether it calls a function named
    /// AddForm at all, and every script it calls a function of (a callmethod's declared type, a callstatic's script, a
    /// callparent's parent), less its own scripts, the list types and the give calls. Every injection
    /// (<see cref="Injections(PexFile, IReadOnlyList{PexFile})"/>) is an AddForm call in the script or in a script it
    /// calls (<see cref="CalledScripts(PexFile)"/>), and those are among these calls: a script none of whose reachable
    /// code calls AddForm adds nothing to a list, and its code needn't be followed.
    /// </summary>
    internal static (bool AddForm, List<string> Calls) Shape(PexFile file)
    {
        bool addForm = false;
        var own = file.Objects.SelectMany(o => new[] { o.Name, o.Parent }).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var calls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in file.Objects)
            foreach (var f in o.Functions)
                foreach (var ins in f.Code)
                {
                    var a = ins.Args;
                    string? name, target;
                    switch (ins.Op)
                    {
                        case PexFile.OpCallMethod when a.Length >= 3:
                            name = a[0].Text;
                            target = a[1].IsIdentifier ? PexObject.ScriptOfType(o.TypeOf(f, a[1].Text ?? "")) : null;
                            break;
                        case PexFile.OpCallStatic when a.Length >= 3: name = a[1].Text; target = a[0].Text; break;
                        case PexFile.OpCallParent when a.Length >= 2: name = a[0].Text; target = o.Parent; break;
                        default: continue;
                    }
                    if (string.Equals(name, "AddForm", StringComparison.OrdinalIgnoreCase)) addForm = true;
                    if (string.IsNullOrEmpty(target) || own.Contains(target) || ListTypes.Contains(target) || (name != null && ScriptCode.Givers.Contains(name))) continue;
                    if (seen.Add(target)) calls.Add(target);
                }
        return (addForm, calls);
    }

    /// <summary>The property a value comes from, when it is the property's own value, a member of its struct or an element of its array.</summary>
    static CodeHolder? HolderOf(CodeValue v)
    {
        if (v.Kind != ValueOrigin.Property || v.Property == null) return null;
        if (!v.Derived) return new CodeHolder(v.Property, null, false);
        if (v.Entries) return new CodeHolder(v.Property, v.Member, v.Element, true);
        if (v.Member != null) return new CodeHolder(v.Property, v.Member, v.Element);
        if (v.Element && v.Text.StartsWith("an element of property ", StringComparison.Ordinal)) return new CodeHolder(v.Property, null, true);
        // An entry of a form list held in the property: FormList.GetAt(i), or an element of FormList.GetArray()
        if (FormListEntry().Match(v.Text) is { Success: true } fe && string.Equals(fe.Groups["p"].Value, v.Property, StringComparison.OrdinalIgnoreCase)) return new CodeHolder(v.Property, null, false, true);
        return null;
    }

    static JsonNode? Navigate(JsonNode? node, string dottedPath)
    {
        foreach (var part in dottedPath.Split('.'))
        {
            var pm = PathPart().Match(part);
            if (!pm.Success) return null;
            node = node?[pm.Groups[1].Value];
            if (pm.Groups[2].Success) node = node is JsonArray a && int.Parse(pm.Groups[2].Value) < a.Count ? a[int.Parse(pm.Groups[2].Value)] : null;
        }
        return node;
    }
}

/// <summary>
/// The compiled scripts of a Data folder, read for their injections (<see cref="ScriptLinks.Injections"/>). A script is
/// found where the game finds it: a loose file under Data\Scripts first, then an archive. Only archives of general
/// files are looked in (a texture archive is passed over by its header), and their name tables are read once per
/// process (<see cref="ArchiveStore.Names"/>). The order is the attaching record's plugin's archives first, then the
/// game's, then the rest, a Main or Misc archive first in each: it changes how soon a script is found, and which copy
/// only when two archives hold the same script. A script it extends is read too (up to 8 deep): its injections are the
/// script's. So are the scripts its code calls with a value from its properties (a library function that may hold the
/// AddForm; <see cref="ScriptLinks.CalledScripts(PexFile)"/>), up to 3 calls deep. Each compiled script is read once per
/// process while its file, and the files of the scripts it calls, keep their size and date.
/// The code is followed only when it can matter: first the instructions alone (<see cref="ScriptLinks.Shape"/>) of the
/// script and of every script it calls, the same depth; when none of them calls AddForm, the script adds nothing to a
/// list, and its values aren't followed (none of the 24 scripts on the records that hold Eon, 0476C4:Starfield.esm, nor
/// any script they call, calls AddForm). What each script's instructions show is kept for the process like the rest,
/// so a script many others call is read once. When some do, the script and the scripts it passes a value into are read
/// into one code reading, each once.
/// </summary>
public sealed class ScriptInjections
{
    static readonly ConcurrentDictionary<string, (string Stamp, string Parent, List<Injection> Injections, string? Error, List<(string Script, string Stamp)> Called)> Parsed = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What each compiled script's instructions show (<see cref="ScriptLinks.Shape"/>), while its file keeps its size and date.</summary>
    static readonly ConcurrentDictionary<string, (string Stamp, bool AddForm, List<string> Calls)> Shapes = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, (long Length, DateTime Written, bool General)> Kinds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>For callers without a report session: one archive store per Data folder, with the lock its users share
    /// (an <see cref="ArchiveStore"/> isn't safe to use from two threads at once).</summary>
    static readonly ConcurrentDictionary<string, (ArchiveStore Store, object Gate)> Shared = new(StringComparer.OrdinalIgnoreCase);
    const int MaxParents = 8;
    /// <summary>Scripts the code calls, followed this many calls deep (as ScriptCode follows calls).</summary>
    const int MaxCalled = 3;

    readonly ArchiveStore _store;
    readonly bool _ownStore;
    readonly object _gate;
    List<string>? _archives;
    readonly Dictionary<string, CodeLookup> _now = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Where each script was found in this answer (by script and plugin), and the archives' order per plugin.</summary>
    readonly Dictionary<(string Script, string Plugin), (string Where, string Stamp, Func<byte[]> Bytes)?> _located = new();
    readonly Dictionary<string, List<string>> _order = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Compiled scripts read from their files since <see cref="Begin"/> (the others came from memory).</summary>
    public int ScriptsRead { get; private set; }
    /// <summary>Scripts looked up since <see cref="Begin"/>.</summary>
    public int ScriptsLookedUp { get { lock (_gate) return _now.Count; } }

    /// <param name="store">A report session's archives (<see cref="ReportSession.Archives"/>).</param>
    public ScriptInjections(ArchiveStore store) : this(store, false, new object()) { }
    ScriptInjections(ArchiveStore store, bool own, object gate) { _store = store; _ownStore = own; _gate = gate; }

    /// <summary>For one answer of a caller without a report session (refs): the Data folder's archive store is shared by
    /// every call in the process, so archives' lists stay in memory between calls; the counts are this answer's.</summary>
    public static ScriptInjections For(string dataFolder)
    {
        var (store, gate) = Shared.GetOrAdd(Path.GetFullPath(dataFolder), d => (new ArchiveStore(d), new object()));
        var made = new ScriptInjections(store, true, gate);
        made.Begin();
        return made;
    }

    /// <summary>At the start of an answer: the Data folder's files are looked at again, and the counts start over.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            if (_ownStore) _store.Begin();
            _archives = null; _now.Clear(); _located.Clear(); _order.Clear(); ScriptsRead = 0;
        }
    }

    /// <summary>The injections of a script by its name (DarkStar:TGO:X), with those of the scripts it extends.</summary>
    /// <param name="plugin">The attaching record's plugin: its archives are looked in first.</param>
    public CodeLookup Of(string script, string? plugin = null)
    {
        lock (_gate)
        {
            if (_now.TryGetValue(script, out var have)) return have;
            var injections = new List<Injection>();
            string? where = null, problem = null;
            string current = script;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int depth = 0; depth < MaxParents && current.Length > 0 && seen.Add(current); depth++)
            {
                var (w, parent, found, error) = Read(current, plugin);
                if (depth == 0) { where = w; problem = error; }
                // A parent that can't be read leaves what the script's own code gives
                if (error != null) break;
                injections.AddRange(found);
                current = parent;
            }
            return _now[script] = new CodeLookup(injections, where, problem);
        }
    }

    static readonly ConcurrentDictionary<string, (string Stamp, string? Parent)> Parents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The script a script extends, from its compiled script where the game finds it ("" for none); null when
    /// it is in no file or can't be read. Read once per process while its file keeps its size and date.</summary>
    public string? ParentOf(string script, string? plugin = null)
    {
        lock (_gate)
        {
            if (Locate(script, plugin) is not { } at) return null;
            string key = _store.DataFolder + "|" + ScriptsReport.PathOfScript(script);
            if (Parents.TryGetValue(key, out var p) && p.Stamp == at.Stamp) return p.Parent;
            string? parent;
            try
            {
                var file = PexFile.Read(at.Bytes());
                parent = (file.Objects.FirstOrDefault(o => o.Name.Equals(script, StringComparison.OrdinalIgnoreCase)) ?? file.Objects.FirstOrDefault())?.Parent;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { parent = null; }
            Parents[key] = (at.Stamp, parent);
            return parent;
        }
    }

    /// <summary>Where the game finds a compiled script: loose, or the first archive that holds it; its stamp and its bytes.
    /// Null when it is in no loose file or archive.</summary>
    (string Where, string Stamp, Func<byte[]> Bytes)? Locate(string script, string? plugin)
    {
        var at = (script, plugin ?? "");
        if (_located.TryGetValue(at, out var have)) return have;
        return _located[at] = Find(script, plugin);
    }

    (string Where, string Stamp, Func<byte[]> Bytes)? Find(string script, string? plugin)
    {
        string rel = ScriptsReport.PathOfScript(script);
        string full = Path.Combine(_store.DataFolder, rel);
        var fi = new FileInfo(full);
        if (fi.Exists) return ("loose", $"loose|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}", () => File.ReadAllBytes(full));
        string? archive = Archives(plugin).FirstOrDefault(a => _store.Names(a).Has(rel));
        if (archive == null) return null;
        var ai = new FileInfo(archive);
        return (Path.GetFileName(archive), $"{archive}|{ai.Length}|{ai.LastWriteTimeUtc.Ticks}",
            () => _store.Open(archive).Find(rel)?.Bytes() ?? throw new IOException($"{rel} is no longer in {Path.GetFileName(archive)}"));
    }

    (string? Where, string Parent, List<Injection> Injections, string? Error) Read(string script, string? plugin)
    {
        string rel = ScriptsReport.PathOfScript(script);
        if (Locate(script, plugin) is not { } at) return (null, "", new List<Injection>(), $"its compiled script {rel} is in no loose file or archive in Data");
        string where = at.Where;
        string key = _store.DataFolder + "|" + rel;
        if (Parsed.TryGetValue(key, out var p) && p.Stamp == at.Stamp && p.Called.All(c => Locate(c.Script, plugin)?.Stamp == c.Stamp))
            return (where, p.Parent, p.Injections, p.Error);
        string parent = "";
        var found = new List<Injection>();
        string? error = null;
        var called = new List<(string Script, string Stamp)>();
        try
        {
            var file = PexFile.Read(at.Bytes());
            ScriptsRead++;
            // The scripts its code calls, and the ones those call, read as the game finds them, each once; one that is in
            // no file, or can't be read, is left out (its functions run no code SFSearch can see)
            var loaded = new Dictionary<string, PexFile?>(StringComparer.OrdinalIgnoreCase);
            var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PexFile? Load(string name)
            {
                if (loaded.TryGetValue(name, out var have)) return have;
                if (Locate(name, plugin) is not { } cat) return loaded[name] = null;
                stamps[name] = cat.Stamp;
                try
                {
                    var cf = PexFile.Read(cat.Bytes());
                    ScriptsRead++;
                    return loaded[name] = cf;
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return loaded[name] = null; }
            }
            (bool AddForm, List<string> Calls)? ShapeOf(string name)
            {
                if (Locate(name, plugin) is not { } cat) return null;
                string shapeKey = _store.DataFolder + "|" + ScriptsReport.PathOfScript(name);
                stamps[name] = cat.Stamp;
                if (Shapes.TryGetValue(shapeKey, out var known) && known.Stamp == cat.Stamp) return (known.AddForm, known.Calls);
                if (Load(name) is not { } cf) return null;
                var made = ScriptLinks.Shape(cf);
                Shapes[shapeKey] = (cat.Stamp, made.AddForm, made.Calls);
                return made;
            }
            // First what its instructions alone show: when neither its code nor the code it can call (the same depth)
            // calls AddForm, it adds nothing to a list, and its values needn't be followed (ScriptLinks.Shape)
            var (addForm, calls) = ScriptLinks.Shape(file);
            if (!addForm)
            {
                var seen = new HashSet<string>(file.Objects.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
                var next = calls;
                for (int depth = 0; depth < MaxCalled && next.Count > 0 && !addForm; depth++)
                {
                    var more = new List<string>();
                    foreach (var name in next)
                    {
                        if (!seen.Add(name) || ShapeOf(name) is not { } shape) continue;
                        if (shape.AddForm) { addForm = true; break; }
                        more.AddRange(shape.Calls);
                    }
                    next = more;
                }
            }
            if (addForm)
            {
                // The code followed: the scripts it calls with a property's value (or a parameter's), up to MaxCalled
                // deep, each read into one result once, in the order they are found
                var result = new ScriptCodeResult();
                ScriptCode.Read(file, "", result);
                var calledFiles = new List<PexFile>();
                var seen = new HashSet<string>(file.Objects.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
                var next = ScriptLinks.CalledScripts(file, result.Sites);
                for (int depth = 0; depth < MaxCalled && next.Count > 0; depth++)
                {
                    var more = new List<string>();
                    foreach (var name in next)
                    {
                        if (!seen.Add(name)) continue;
                        var cf = Load(name);
                        if (stamps.TryGetValue(name, out var stamp)) called.Add((name, stamp));
                        if (cf == null) continue;
                        int from = result.Sites.Count;
                        ScriptCode.Read(cf, "", result);
                        calledFiles.Add(cf);
                        more.AddRange(ScriptLinks.CalledScripts(cf, result.Sites.Skip(from)));
                    }
                    next = more;
                }
                var by = ScriptLinks.Injections(result, file, calledFiles);
                var mine = by.TryGetValue(script, out var x) ? x : by.Values.FirstOrDefault();
                if (mine.Injections != null) { parent = mine.Parent ?? ""; found = mine.Injections; }
            }
            else
            {
                // Kept with the stamps of every script looked at, so a change in any of them reads it again
                called.AddRange(stamps.Select(st => (st.Key, st.Value)));
                parent = (file.Objects.FirstOrDefault(o => o.Name.Equals(script, StringComparison.OrdinalIgnoreCase)) ?? file.Objects.FirstOrDefault())?.Parent ?? "";
            }
        }
        catch (InvalidDataException e) { error = $"its compiled script {rel} ({where}) can't be read: {e.Message}"; }
        catch (IOException e) { error = $"its compiled script {rel} ({where}) can't be read: {e.Message}"; }
        catch (UnauthorizedAccessException e) { error = $"its compiled script {rel} ({where}) can't be read: {e.Message}"; }
        Parsed[key] = (at.Stamp, parent, found, error, called);
        return (where, parent, found, error);
    }

    /// <summary>The archives of general files in Data, in the order a script is looked for in them.</summary>
    IEnumerable<string> Archives(string? plugin)
    {
        if (_order.TryGetValue(plugin ?? "", out var have)) return have;
        _archives ??= _store.AllInData().Where(General).ToList();
        string stem = plugin == null ? "" : Path.GetFileNameWithoutExtension(plugin) + " - ";
        int Rank(string a)
        {
            string n = Path.GetFileName(a);
            int r = stem.Length > 3 && n.StartsWith(stem, StringComparison.OrdinalIgnoreCase) ? 0 : n.StartsWith("Starfield - ", StringComparison.OrdinalIgnoreCase) ? 2 : 4;
            return n.Contains(" - Main", StringComparison.OrdinalIgnoreCase) || n.Contains(" - Misc", StringComparison.OrdinalIgnoreCase) ? r - 1 : r;
        }
        return _order[plugin ?? ""] = _archives.OrderBy(Rank).ToList();
    }

    /// <summary>False for a texture archive (its header says DX10): it holds no script.</summary>
    static bool General(string path)
    {
        var fi = new FileInfo(path);
        if (Kinds.TryGetValue(path, out var k) && k.Length == fi.Length && k.Written == fi.LastWriteTimeUtc) return k.General;
        bool general = true;
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[12];
            if (fs.ReadAtLeast(head, 12, throwOnEndOfStream: false) == 12 && head[8..12].SequenceEqual("DX10"u8)) general = false;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Kinds[path] = (fi.Length, fi.LastWriteTimeUtc, general);
        return general;
    }
}
