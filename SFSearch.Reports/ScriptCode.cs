// The code in a compiled script that reaches records without a link in a record, and the records it hands out:
//  - Game.GetFormFromFile(FormID, "Plugin.esm"), Game.GetForm(FormID) and the like build a record from a number;
//  - Game.IsPluginInstalled("Plugin.esm") names a plugin;
//  - AddItem, AddForm, PlaceAtMe, AddPerk … hand out a record: one the code got with the calls above, one held in a
//    script property (its value is on the records that attach the script), or one decided at run time.
// Read from the bytecode (Pex.cs), one function at a time, along its branches: each variable keeps every value it can
// have at each instruction (an if whose two paths assign two constants gives both), and a script variable set in one
// function is known in the others. A parameter of a function is followed to the calls of that function in the plugin's
// scripts (ResolveParameters), and a value held in a script property to the records that attach the script (the report
// does that: it reads the records).
namespace SFSearch.Reports;

/// <summary>Where a value the code uses comes from.</summary>
public enum ValueOrigin { Literal, None, Property, Built, Parameter, ScriptVariable, Returned, Computed }

/// <summary>One value a variable can have at a point of the code.</summary>
/// <param name="Text">The value as the answer writes it: 0x00001234, plugin = "x.esm", property Foo, parameter akItem …</param>
/// <param name="Constant">The constant, for a literal.</param>
/// <param name="Built">For a record got in code: the call that got it.</param>
/// <param name="Property">For a value held in a script property, or got from one (an element, a member, what a method
/// of it returns: <paramref name="Derived"/>): the property's name.</param>
/// <param name="Script">The script that owns the property, or the function with the parameter.</param>
/// <param name="Function">For a parameter: the function's name.</param>
/// <param name="Parameter">For a parameter: its place among the function's parameters.</param>
/// <param name="Element">For a value got from a property: an element of the property's array.</param>
/// <param name="Member">For a value got from a property: a member of the property's struct (or of an element of its array of structs).</param>
public sealed record CodeValue(ValueOrigin Kind, string Text, PexValue? Constant = null, ScriptCall? Built = null,
    string? Property = null, string? Script = null, bool Derived = false, string? Function = null, int Parameter = -1,
    bool Element = false, string? Member = null)
{
    internal string Key => $"{(int)Kind}|{Text}|{Property}|{Script}";
}

/// <summary>A call of a function in a script, with the values its arguments can have: what a parameter of that
/// function is given.</summary>
/// <param name="Target">The script whose function is called (its declared type), or "" when it isn't known.</param>
public sealed record CallSite(string Target, string Function, IReadOnlyList<CodeValue[]> Args, string CallerScript, string CallerFunction);

/// <summary>One record (or plugin) a call reaches, on one path of the code or on one record that attaches the script.</summary>
public sealed class CallTarget
{
    /// <summary>The record whose script property gives the value ("Foo (000801:x.esm) QUST"), and its FormKey.</summary>
    public string? Carrier { get; set; }
    public string? CarrierFormKey { get; set; }
    /// <summary>How the value comes about: "FormIdProp = 0x00001234", "plugin = "a.esm"", "Item = Bar (001234:x.esm)".</summary>
    public string Via { get; set; } = "";
    public uint? FormId { get; set; }
    public string? NamedPlugin { get; set; }
    public string? EditorId { get; set; }
    /// <summary>For a record handed out: the FormKey the property holds (and the alias, for a quest alias).</summary>
    public string? ItemFormKey { get; set; }
    public int Alias { get; set; } = -1;
    public string? FormKey { get; set; }
    public string? Record { get; set; }
    public string? Signature { get; set; }
    public string? NotFound { get; set; }

    /// <summary>"on Foo (000801:x.esm) QUST: Item = …", or the path's values.</summary>
    public string Where => Carrier != null ? $"on {Carrier}: {Via}" : Via;
    /// <summary>The record, or why there is none.</summary>
    public string Result => Record != null ? $"{Record} {Signature}" + (Alias >= 0 ? $" (the reference in its alias {Alias})" : "")
        : NotFound != null ? (FormKey != null ? FormKey + ": " : "") + NotFound : "";
}

/// <summary>One call that reaches a record (or a plugin) without a link in a record, or hands one out.</summary>
public sealed class ScriptCall
{
    public required string Plugin { get; init; }
    public required string Script { get; init; }
    /// <summary>The function the call is in: OnInit, or Busy.OnTimer for a function of a named state.</summary>
    public required string Function { get; init; }
    /// <summary>Game.GetFormFromFile, AddItem, …</summary>
    public required string Call { get; init; }
    /// <summary>get (builds a record from a FormID or an EditorID), plugin (names a plugin), give (hands out a record).</summary>
    public required string Kind { get; init; }
    /// <summary>The call's arguments as the code writes them; a constant the code put in a variable first is shown as the constant.</summary>
    public List<string> Args { get; } = new();
    /// <summary>The plugin the call names, when the code gives it as one text.</summary>
    public string? NamedPlugin { get; set; }
    /// <summary>The FormID the call names, when the code gives it as one number.</summary>
    public uint? FormId { get; set; }
    /// <summary>The EditorID the call names (GetFormByEditorID and the like).</summary>
    public string? EditorId { get; set; }
    /// <summary>For give: where the record handed out comes from, and for one built in code, the call that built it.</summary>
    public ValueOrigin? ItemOrigin { get; set; }
    public string? ItemText { get; set; }
    public ScriptCall? BuiltBy { get; set; }
    /// <summary>For a value given as a parameter: the calls in the plugin's scripts that give it, or why there are none.</summary>
    public string? Via { get; set; }
    /// <summary>The records the call reaches when there is more than one value: one per path of the code with a
    /// constant of its own, one per record that attaches the script for a value held in a script property.</summary>
    public List<CallTarget> Targets { get; } = new();
    /// <summary>Filled by the report: the record the call names (label, FormKey, signature), or why there is none.</summary>
    public string? FormKey { get; set; }
    public string? Record { get; set; }
    public string? Signature { get; set; }
    public string? NotFound { get; set; }
    /// <summary>The values each argument can have (after <see cref="ScriptCode.ResolveParameters"/>, those of the callers).</summary>
    public IReadOnlyList<CodeValue[]> Values { get; internal set; } = Array.Empty<CodeValue[]>();

    /// <summary>"Game.GetFormFromFile(0x0000ABCD, "x.esm")".</summary>
    public string Shown => $"{Call}({string.Join(", ", Args)})";

    /// <summary>The scripts whose properties hold a value this call uses (their records give the values).</summary>
    public IEnumerable<string> PropertyScripts => Used.SelectMany(v => v).Where(v => v.Kind == ValueOrigin.Property && v.Script != null).Select(v => v.Script!).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The arguments that say which record or plugin: the first for give and plugin, the first two for get.</summary>
    internal IEnumerable<CodeValue[]> Used => Values.Take(Kind == "get" ? 2 : 1);
}

/// <summary>The calls of a plugin's compiled scripts, and the calls between its functions.</summary>
public sealed class ScriptCodeResult
{
    public List<ScriptCall> Calls { get; } = new();
    public List<CallSite> Sites { get; } = new();
    /// <summary>AddItem, AddForm … whose record is a script property (listed too), or nothing (not listed).</summary>
    public int PropertyGives => Calls.Count(c => c.Kind == "give" && c.ItemOrigin == ValueOrigin.Property);
    public int NothingGives { get; set; }
    public int LinkedGives => PropertyGives + NothingGives;
}

public static class ScriptCode
{
    /// <summary>Static calls that build a record from a number or a name.</summary>
    public static readonly HashSet<string> Getters = new(StringComparer.OrdinalIgnoreCase) { "GetFormFromFile", "GetForm", "GetFormByEditorID", "GetFormFromEditorID" };
    /// <summary>Calls that name a plugin.</summary>
    public static readonly HashSet<string> PluginChecks = new(StringComparer.OrdinalIgnoreCase) { "IsPluginInstalled" };
    /// <summary>Calls that hand out the record given as their first argument.</summary>
    public static readonly HashSet<string> Givers = new(StringComparer.OrdinalIgnoreCase) { "AddItem", "AddAliasedItem", "AddForm", "PlaceAtMe", "PlaceActorAtMe", "PlaceShipAtMe", "AddPerk", "AddSpell", "EquipItem" };

    /// <summary>A variable keeps at most this many values; past it, it is "one of several values".</summary>
    const int MaxValues = 8;
    /// <summary>An instruction is gone through at most this many times while the values settle (loops).</summary>
    const int MaxVisits = 40;
    /// <summary>A parameter is followed through at most this many callers, one into another.</summary>
    const int MaxDepth = 3;
    /// <summary>The values the callers of a function give a parameter, at most (a list of FormIDs passed one by one).</summary>
    const int MaxCallerValues = 64;

    // The instructions that write their first value (besides those followed one by one below)
    static readonly HashSet<byte> WritesFirst = new() { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15, 16, 17, 18, 19, 27, 30, 31, 34, 35, 36, 38, 40, 41 };

    /// <summary>The calls of one compiled script (kept for the tests and older callers).</summary>
    public static List<ScriptCall> Calls(PexFile file, string plugin, out int linkedGives)
    {
        var r = new ScriptCodeResult();
        Read(file, plugin, r);
        linkedGives = r.LinkedGives;
        return r.Calls;
    }

    sealed class Context
    {
        public required PexObject Object;
        public required string Plugin;
        public readonly Dictionary<string, string> AutoVars = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, PexVariable> ScriptVars = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Written = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, CodeValue[]> BuiltVars = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>One ScriptCall per getter instruction, the same object on every pass.</summary>
        public readonly Dictionary<(PexFunction, int), ScriptCall> Getters = new();
        public ScriptCodeResult? Emit;
    }

    /// <summary>Reads one compiled script's calls into <paramref name="result"/>.</summary>
    public static void Read(PexFile file, string plugin, ScriptCodeResult result)
    {
        foreach (var o in file.Objects)
        {
            var ctx = new Context { Object = o, Plugin = plugin };
            foreach (var p in o.Properties) if (p.AutoVariable != null) ctx.AutoVars[p.AutoVariable] = p.Name;
            foreach (var v in o.Variables) if (!ctx.AutoVars.ContainsKey(v.Name)) ctx.ScriptVars[v.Name] = v;
            // First pass: the script variables some function writes, and those it sets to a record got in code
            foreach (var f in o.Functions) Flow(f, ctx);
            // Second pass: the calls
            ctx.Emit = result;
            foreach (var f in o.Functions) Flow(f, ctx);
        }
    }

    static void Flow(PexFunction f, Context ctx)
    {
        var o = ctx.Object;
        var parameters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < f.Parameters.Count; i++) parameters.TryAdd(f.Parameters[i], i);
        var locals = new HashSet<string>(f.Locals, StringComparer.OrdinalIgnoreCase);
        int n = f.Code.Count;

        // The values each variable can have before each instruction, along the branches
        var before = new Dictionary<string, CodeValue[]>?[n];
        if (n > 0)
        {
            before[0] = new Dictionary<string, CodeValue[]>(StringComparer.OrdinalIgnoreCase);
            var visits = new int[n];
            var work = new Stack<int>();
            work.Push(0);
            while (work.Count > 0)
            {
                int i = work.Pop();
                if (++visits[i] > MaxVisits) continue;
                var state = new Dictionary<string, CodeValue[]>(before[i]!, StringComparer.OrdinalIgnoreCase);
                Step(i, state, false);
                foreach (int next in Next(f.Code[i], i))
                    if (next >= 0 && next < n && Merge(ref before[next], state)) work.Push(next);
            }
        }
        // Then once through in order, each instruction with the values it can see (one the branches never reach, with none)
        for (int i = 0; i < n; i++) Step(i, new Dictionary<string, CodeValue[]>(before[i] ?? new(), StringComparer.OrdinalIgnoreCase), true);

        bool Merge(ref Dictionary<string, CodeValue[]>? into, Dictionary<string, CodeValue[]> from)
        {
            if (into == null) { into = new Dictionary<string, CodeValue[]>(from, StringComparer.OrdinalIgnoreCase); return true; }
            bool changed = false;
            foreach (var name in into.Keys.Union(from.Keys, StringComparer.OrdinalIgnoreCase).ToList())
            {
                // A variable one path doesn't set has its value from before (a local: none yet)
                var a = into.TryGetValue(name, out var x) ? x : Unset(name);
                var b = from.TryGetValue(name, out var y) ? y : Unset(name);
                var u = Union(a, b);
                if (!into.TryGetValue(name, out var had) || !Same(had, u)) { into[name] = u; changed = true; }
            }
            return changed;
        }

        CodeValue[] Unset(string name) => locals.Contains(name) || name.StartsWith("::", StringComparison.Ordinal)
            ? new[] { new CodeValue(ValueOrigin.None, "None") } : Of(new Dictionary<string, CodeValue[]>(), new PexValue(PexValue.Identifier, name, 0, 0, false));

        CodeValue[] Of(Dictionary<string, CodeValue[]> state, PexValue v)
        {
            if (v.Kind == PexValue.None) return new[] { new CodeValue(ValueOrigin.None, "None") };
            if (v.IsLiteral) return new[] { new CodeValue(ValueOrigin.Literal, v.ToString(), v) };
            string name = v.Text ?? "";
            if (state.TryGetValue(name, out var have)) return have;
            if (ctx.AutoVars.TryGetValue(name, out var prop)) return new[] { new CodeValue(ValueOrigin.Property, $"property {prop}", Property: prop, Script: o.Name) };
            if (ctx.BuiltVars.TryGetValue(name, out var b)) return b;
            if (ctx.ScriptVars.TryGetValue(name, out var sv))
                return new[] { sv.Start.IsLiteral && !ctx.Written.Contains(name)
                    ? new CodeValue(ValueOrigin.ScriptVariable, $"{Clean(name)} = {sv.Start}", sv.Start)
                    : new CodeValue(ValueOrigin.ScriptVariable, $"script variable {Clean(name)}") };
            if (parameters.TryGetValue(name, out int at)) return new[] { new CodeValue(ValueOrigin.Parameter, $"parameter {name}", Script: o.Name, Function: f.Name, Parameter: at) };
            if (name.Equals("self", StringComparison.OrdinalIgnoreCase)) return new[] { new CodeValue(ValueOrigin.Computed, "self") };
            if (locals.Contains(name) || name.StartsWith("::", StringComparison.Ordinal)) return new[] { new CodeValue(ValueOrigin.None, "None") };
            return new[] { new CodeValue(ValueOrigin.Computed, Clean(name)) };
        }

        void Step(int i, Dictionary<string, CodeValue[]> state, bool final)
        {
            var ins = f.Code[i];
            var a = ins.Args;

            void Set(PexValue dest, CodeValue[] values)
            {
                if (!dest.IsIdentifier || dest.Text == null) return;
                // A constant put in a named variable keeps the name: the source writes the name, the bytecode shows the value
                if (!dest.Text.StartsWith("::", StringComparison.Ordinal))
                    values = values.Select(v => v.Kind == ValueOrigin.Literal ? v with { Text = $"{Clean(dest.Text)} = {v.Constant}" } : v).ToArray();
                state[dest.Text] = values;
                if (final && ctx.Emit == null && ctx.ScriptVars.ContainsKey(dest.Text))
                {
                    ctx.Written.Add(dest.Text);
                    var built = values.Where(v => v.Kind == ValueOrigin.Built).ToArray();
                    if (built.Length > 0) ctx.BuiltVars[dest.Text] = ctx.BuiltVars.TryGetValue(dest.Text, out var had) ? Union(had, built) : built;
                }
            }

            // What is got from a value: from a property or a record got in code, it stays theirs; otherwise it is decided at run time
            CodeValue[] From(CodeValue[] holder, Func<CodeValue, string> text) => holder.Select(h => h.Kind is ValueOrigin.Property or ValueOrigin.Built
                ? h with { Text = text(h), Derived = true, Constant = null, Element = false, Member = null }
                : new CodeValue(ValueOrigin.Returned, text(h))).ToArray();

            switch (ins.Op)
            {
                case PexFile.OpAssign:
                case PexFile.OpCast:
                    if (a.Length >= 2) Set(a[0], Of(state, a[1]));
                    return;
                case PexFile.OpPropGet:
                    if (a.Length >= 3)
                    {
                        bool self = a[1].IsIdentifier && string.Equals(a[1].Text, "self", StringComparison.OrdinalIgnoreCase);
                        if (self) Set(a[2], new[] { new CodeValue(ValueOrigin.Property, $"property {a[0].Text}", Property: a[0].Text, Script: o.Name) });
                        else Set(a[2], Of(state, a[1]).Select(h => h.Kind == ValueOrigin.Property
                            ? h with { Text = $"{a[0].Text} of {h.Text}", Derived = true }
                            : new CodeValue(ValueOrigin.Returned, $"{a[0].Text} of {h.Text}")).ToArray());
                    }
                    return;
                case PexFile.OpArrayGet:
                    // An element of a property's array keeps which property; the report reads the array on the records
                    if (a.Length >= 3) Set(a[0], Of(state, a[1]).Select(h => h.Kind == ValueOrigin.Property && !h.Derived
                        ? h with { Text = $"an element of {h.Text}", Derived = true, Element = true, Constant = null }
                        : From(new[] { h }, x => $"an element of {x.Text}")[0]).ToArray());
                    return;
                case PexFile.OpStructGet:
                    // A member of a property's struct (or of an element of its array of structs): the report reads that member
                    if (a.Length >= 3) Set(a[0], Of(state, a[1]).Select(h => h.Kind == ValueOrigin.Property && h.Member == null && (!h.Derived || h.Element)
                        ? h with { Text = $"{a[2].Text} of {h.Text}", Derived = true, Member = a[2].Text, Constant = null }
                        : From(new[] { h }, x => $"{a[2].Text} of {x.Text}")[0]).ToArray());
                    return;
                case PexFile.OpCallMethod:
                case PexFile.OpCallStatic:
                case PexFile.OpCallParent:
                    break;
                default:
                    if (WritesFirst.Contains(ins.Op) && a.Length > 0) Set(a[0], new[] { new CodeValue(ValueOrigin.Computed, "a value the code works out") });
                    return;
            }

            // A call: callmethod name, object, result, args…; callstatic class, name, result, args…; callparent name, result, args…
            string name, shownName, target;
            PexValue result;
            int first;
            CodeValue[]? on = null;
            if (ins.Op == PexFile.OpCallStatic) { if (a.Length < 3) return; name = a[1].Text ?? ""; shownName = $"{Capital(a[0].Text)}.{name}"; result = a[2]; first = 3; target = a[0].Text ?? ""; }
            else if (ins.Op == PexFile.OpCallMethod)
            {
                if (a.Length < 3) return;
                name = a[0].Text ?? ""; shownName = name; result = a[2]; first = 3; on = Of(state, a[1]);
                target = a[1].IsIdentifier ? PexObject.ScriptOfType(o.TypeOf(f, a[1].Text ?? "")) ?? "" : "";
            }
            else { if (a.Length < 2) return; name = a[0].Text ?? ""; shownName = "Parent." + name; result = a[1]; first = 2; target = o.Parent; }
            var args = a.Skip(first).Select(x => Of(state, x)).ToArray();
            if (final && ctx.Emit != null && target.Length > 0) ctx.Emit.Sites.Add(new CallSite(target, name, args, o.Name, f.Shown));

            // The getters are global functions (Game.GetFormFromFile): a call of them is a static call
            if (ins.Op == PexFile.OpCallStatic && (Getters.Contains(name) || PluginChecks.Contains(name)))
            {
                bool getter = Getters.Contains(name);
                if (!ctx.Getters.TryGetValue((f, i), out var call))
                    ctx.Getters[(f, i)] = call = new ScriptCall { Plugin = ctx.Plugin, Script = o.Name, Function = f.Shown, Call = shownName, Kind = getter ? "get" : "plugin" };
                if (final && ctx.Emit != null)
                {
                    call.Values = args;
                    foreach (var x in args) call.Args.Add(Shown(x));
                    Constants(call);
                    ctx.Emit.Calls.Add(call);
                }
                Set(result, getter ? new[] { new CodeValue(ValueOrigin.Built, $"{call.Call} in {f.Shown}", Built: call) } : new[] { new CodeValue(ValueOrigin.Computed, $"{shownName}()") });
                return;
            }
            if (final && ctx.Emit != null && Givers.Contains(name) && ins.Op != PexFile.OpCallStatic && args.Length > 0)
            {
                var item = args[0];
                if (item.All(v => v.Kind is ValueOrigin.None or ValueOrigin.Literal)) ctx.Emit.NothingGives++;
                if (!item.All(v => v.Kind is ValueOrigin.None or ValueOrigin.Literal))
                {
                    var call = new ScriptCall { Plugin = ctx.Plugin, Script = o.Name, Function = f.Shown, Call = shownName, Kind = "give", Values = args };
                    foreach (var x in args) call.Args.Add(Shown(x));
                    Item(call);
                    ctx.Emit.Calls.Add(call);
                }
            }
            // What a method of a script property returns (a form list's GetAt) comes from that property: a link the record
            // holds. What a method of a record got in code returns comes from that record.
            if (on != null) Set(result, From(on, h => $"what {h.Text}.{shownName}() returns"));
            else Set(result, new[] { new CodeValue(ValueOrigin.Returned, $"what {shownName}() returns") });
        }
    }

    /// <summary>The instructions that may come after instruction <paramref name="i"/>.</summary>
    static IEnumerable<int> Next(PexInstruction ins, int i)
    {
        switch (ins.Op)
        {
            case PexFile.OpReturn: yield break;
            case PexFile.OpJump:
                if (ins.Args.Length > 0 && ins.Args[0].Kind == PexValue.Integer) yield return i + ins.Args[0].Int;
                else yield return i + 1;
                yield break;
            case PexFile.OpJumpTrue:
            case PexFile.OpJumpFalse:
                yield return i + 1;
                if (ins.Args.Length > 1 && ins.Args[1].Kind == PexValue.Integer) yield return i + ins.Args[1].Int;
                yield break;
            default: yield return i + 1; yield break;
        }
    }

    static CodeValue[] Union(CodeValue[] a, CodeValue[] b)
    {
        if (ReferenceEquals(a, b)) return a;
        var list = new List<CodeValue>(a);
        foreach (var v in b) if (!list.Any(x => x.Key == v.Key)) list.Add(v);
        if (list.Count > MaxValues) return new[] { new CodeValue(ValueOrigin.Computed, "one of several values the code works out") };
        return list.ToArray();
    }

    static bool Same(CodeValue[] a, CodeValue[] b) => a.Length == b.Length && a.All(x => b.Any(y => y.Key == x.Key));

    /// <summary>A value as an argument shows it: one text, or the texts of the paths ("plugin = "a.esm" or "b.esm"").</summary>
    static string Shown(CodeValue[] values)
    {
        var real = values.Length > 1 ? values.Where(v => v.Kind != ValueOrigin.None).ToArray() : values;
        if (real.Length == 0) real = values;
        if (real.Length == 1) return real[0].Text;
        // Constants put in the same variable: "plugin = "a.esm" or "b.esm""
        var named = real.Select(v => v.Kind == ValueOrigin.Literal && v.Text.Contains(" = ", StringComparison.Ordinal) ? v.Text[..v.Text.IndexOf(" = ", StringComparison.Ordinal)] : null).Distinct().ToList();
        if (named.Count == 1 && named[0] != null) return $"{named[0]} = {string.Join(" or ", real.Select(v => v.Constant.ToString()))}";
        return string.Join(" or ", real.Select(v => v.Text));
    }

    /// <summary>The FormID, plugin or EditorID a getter's arguments give as constants: one each, or one target per
    /// path when the paths give different constants.</summary>
    internal static void Constants(ScriptCall call)
    {
        call.FormId = null; call.NamedPlugin = null; call.EditorId = null;
        call.Targets.RemoveAll(t => t.Carrier == null);
        var args = call.Values;
        static PexValue[] Of(CodeValue[] values, byte kind) => values.Where(v => v.Constant is { } c && c.Kind == kind).Select(v => v.Constant!.Value).ToArray();
        static bool AllConstant(CodeValue[] values) => values.All(v => v.Kind is ValueOrigin.Literal or ValueOrigin.None || (v.Kind == ValueOrigin.ScriptVariable && v.Constant != null));
        string name = call.Call[(call.Call.LastIndexOf('.') + 1)..];
        if (call.Kind == "plugin" || (call.Kind == "get" && !name.Equals("GetFormFromFile", StringComparison.OrdinalIgnoreCase) && !name.Equals("GetForm", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Count == 0) return;
            var texts = Of(args[0], PexValue.String);
            if (texts.Length == 1 && AllConstant(args[0])) { if (call.Kind == "plugin") call.NamedPlugin = texts[0].Text; else call.EditorId = texts[0].Text; }
            else if (texts.Length > 1)
                foreach (var t in args[0].Where(v => v.Constant is { Kind: PexValue.String }))
                    call.Targets.Add(call.Kind == "plugin" ? new CallTarget { Via = "", NamedPlugin = t.Constant!.Value.Text } : new CallTarget { Via = "", EditorId = t.Constant!.Value.Text });
            return;
        }
        var ids = args.Count > 0 ? Of(args[0], PexValue.Integer) : Array.Empty<PexValue>();
        var plugins = args.Count > 1 ? Of(args[1], PexValue.String) : Array.Empty<PexValue>();
        if (ids.Length <= 1 && plugins.Length <= 1)
        {
            if (ids.Length == 1) call.FormId = (uint)ids[0].Int;
            if (plugins.Length == 1) call.NamedPlugin = plugins[0].Text;
            return;
        }
        // The paths give several constants: one target for each pair
        var idValues = args[0].Where(v => v.Constant is { Kind: PexValue.Integer }).ToList();
        var plValues = args.Count > 1 ? args[1].Where(v => v.Constant is { Kind: PexValue.String }).ToList() : new List<CodeValue>();
        foreach (var id in idValues.Count > 0 ? idValues : new List<CodeValue> { null! })
            foreach (var pl in plValues.Count > 0 ? plValues : new List<CodeValue> { null! })
            {
                var via = new List<string>();
                if (id != null && idValues.Count > 1) via.Add(id.Text);
                if (pl != null && plValues.Count > 1) via.Add(pl.Text);
                call.Targets.Add(new CallTarget { Via = string.Join(", ", via), FormId = id != null ? (uint)id.Constant!.Value.Int : null, NamedPlugin = pl?.Constant!.Value.Text });
            }
    }

    /// <summary>For a give: where its record comes from, from the values of its first argument.</summary>
    internal static void Item(ScriptCall call)
    {
        var item = call.Values[0].Where(v => v.Kind != ValueOrigin.None).ToArray();
        if (item.Length == 0) item = call.Values[0];
        var main = item.FirstOrDefault(v => v.Kind == ValueOrigin.Built) ?? item.FirstOrDefault(v => v.Kind == ValueOrigin.Property) ?? item[0];
        call.ItemOrigin = main.Kind;
        call.ItemText = Shown(item);
        call.BuiltBy = main.Built;
        call.Args[0] = Shown(call.Values[0]);
    }

    /// <summary>
    /// A parameter is given by the calls of its function: each value of a parameter in the calls' arguments is replaced
    /// by the values the calls in <paramref name="result"/> give it (through up to three callers), and the call says
    /// which calls gave them, or why none does (an event the game sends, or a function no script of the plugin calls).
    /// <paramref name="sameOrChild"/> gives a script and the plugin's scripts that extend it.
    /// </summary>
    public static void ResolveParameters(ScriptCodeResult result, Func<string, IEnumerable<string>> sameOrChild)
    {
        // The calls of each function, by its name
        var byFunction = result.Sites.GroupBy(s => s.Function, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var call in result.Calls)
        {
            int count = call.Kind == "get" ? 2 : 1;
            if (!call.Values.Take(count).Any(v => v.Any(x => x.Kind == ValueOrigin.Parameter))) continue;
            var said = new List<string>();
            var values = call.Values.ToArray();
            for (int k = 0; k < Math.Min(count, values.Length); k++) values[k] = Expand(values[k], 0, said);
            call.Values = values;
            call.Via = string.Join("; ", said.Distinct());
            if (call.Kind == "give") { Item(call); }
            else { for (int k = 0; k < Math.Min(count, values.Length); k++) call.Args[k] = Shown(values[k]); Constants(call); }
        }

        CodeValue[] Expand(CodeValue[] values, int depth, List<string> said)
        {
            var output = new List<CodeValue>();
            foreach (var v in values)
            {
                if (v.Kind != ValueOrigin.Parameter || v.Function == null || v.Script == null) { output.Add(v); continue; }
                var scripts = sameOrChild(v.Script).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var sites = byFunction.TryGetValue(v.Function, out var l) ? l.Where(s => scripts.Contains(s.Target) && v.Parameter < s.Args.Count).ToList() : new();
                string param = v.Text.StartsWith("parameter ", StringComparison.Ordinal) ? v.Text["parameter ".Length..] : v.Text;
                if (sites.Count == 0)
                {
                    said.Add(v.Function.StartsWith("On", StringComparison.OrdinalIgnoreCase)
                        ? $"{param} of {v.Function} is passed by the game (an event)"
                        : $"no script of the plugin calls {Short(v.Script)}.{v.Function}: another mod, a fragment or the console decides {param}");
                    output.Add(v);
                    continue;
                }
                if (depth >= MaxDepth) { said.Add($"{param} of {v.Function} is given by callers more than {MaxDepth} calls deep"); output.Add(v); continue; }
                said.Add($"{param} of {v.Function} is given by {string.Join(", ", sites.Select(s => $"{Short(s.CallerScript)}.{s.CallerFunction}").Distinct())}");
                foreach (var s in sites) output.AddRange(Expand(s.Args[v.Parameter], depth + 1, said));
            }
            var distinct = new List<CodeValue>();
            foreach (var v in output) if (!distinct.Any(x => x.Key == v.Key)) distinct.Add(v);
            return distinct.Count > MaxCallerValues ? new[] { new CodeValue(ValueOrigin.Computed, "one of several values the callers work out") } : distinct.ToArray();
        }
    }

    /// <summary>DarkStar:TGO:Lib → Lib.</summary>
    static string Short(string script) => script[(script.LastIndexOf(':') + 1)..];

    static string Capital(string? name) => string.IsNullOrEmpty(name) ? "" : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>A compiler-made name (::temp12, ::Foo_var) as the source would write it.</summary>
    static string Clean(string name)
    {
        if (name.StartsWith("::", StringComparison.Ordinal)) name = name[2..];
        if (name.EndsWith("_var", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }
}
