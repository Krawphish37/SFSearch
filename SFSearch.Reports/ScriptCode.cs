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
/// <param name="Chain">Two steps: a property of the script on the record a property holds (<c>workerContainer of
/// property core</c>: <see cref="Property"/> is core, Chain is workerContainer); the report reads it on that record.
/// On a parameter or a value a function returns, the property to read once that value is known.</param>
/// <param name="ChainType">The declared type of the property the chain starts from (the script whose property Chain is).</param>
/// <param name="CalleeScript">For what a call returns: the script whose function was called (its declared type).</param>
/// <param name="CalleeFunction">For what a call returns: the function called.</param>
/// <param name="CallArgs">For what a call returns: the values the call passes (they give the function's parameters).</param>
/// <param name="On">For what a method returns: the values of what it was called on.</param>
/// <param name="Pending">For a parameter or what the plugin's function returns: what the code takes from it once it is
/// known, in order ("element", "member:replacement", "prop:workerContainer", "call:GetAt", joined by "/").</param>
/// <param name="Entries">For a value got from a property: an entry of the form list the property (its member, its
/// element) holds, got with GetAt (FormList.GetAt(i)).</param>
public sealed record CodeValue(ValueOrigin Kind, string Text, PexValue? Constant = null, ScriptCall? Built = null,
    string? Property = null, string? Script = null, bool Derived = false, string? Function = null, int Parameter = -1,
    bool Element = false, string? Member = null, string? Chain = null, string? ChainType = null,
    string? CalleeScript = null, string? CalleeFunction = null, IReadOnlyList<CodeValue[]>? CallArgs = null, CodeValue[]? On = null,
    string? Pending = null, bool Entries = false)
{
    /// <summary>What makes two values the same value (no string is built: the flow compares values very often).</summary>
    internal (ValueOrigin, string, string?, string?, string?, string?) Key => (Kind, Text, Property, Script, Function, Chain);
}

/// <summary>A call of a function in a script, with the values its arguments can have: what a parameter of that
/// function is given.</summary>
/// <param name="Target">The script whose function is called (its declared type), or "" when it isn't known.</param>
/// <param name="CallerPlugin">The plugin whose script makes the call, when it isn't the plugin asked about.</param>
public sealed record CallSite(string Target, string Function, IReadOnlyList<CodeValue[]> Args, string CallerScript, string CallerFunction, string? CallerPlugin = null);

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
    /// <summary>For a give called on a record (AddForm on a leveled list): the values that record can have (after
    /// <see cref="ScriptCode.ResolveParameters"/>, those of the callers), and the type the code declares for it
    /// (LeveledItem, FormList …; "" when it isn't known). Null for a call on nothing (AddItem on self is on self).</summary>
    public CodeValue[]? On { get; internal set; }
    public string OnType { get; init; } = "";
    /// <summary>For a give called on a record: the record handed out and the record it is called on, together, one
    /// pair per way through the callers (<see cref="ScriptCode.Resolve"/>): <c>Inject(ItemsA, ListsA)</c> and
    /// <c>Inject(ItemsB, ListsB)</c> give two pairs, not every item with every list. Null when there is none.</summary>
    public IReadOnlyList<(CodeValue[] Item, CodeValue[] On)>? Pairs { get; internal set; }

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
    /// <summary>What each function returns ("script|function"; every return of every state's version).</summary>
    public Dictionary<string, CodeValue[]> Returns { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The functions the scripts define ("script|function").</summary>
    public HashSet<string> Defined { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The declared type of each script property ("script|property" → script), for two-step chains.</summary>
    public Dictionary<string, string> PropertyTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The plugin the scripts are of, when the result holds another plugin's scripts.</summary>
    public string? Plugin { get; init; }
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

    /// <summary>A variable keeps at most this many values; past it, it is "one of several values". Only a guard against
    /// a pathological script: was 8; measured on the game's 4,740 compiled scripts and on darkstar.esm,
    /// kinggathcreations_spaceship.esm, apc_galaxysmartcontainers.esm and SFBGS050.esm, no argument has more than 143
    /// values, and the flow takes the same time with no limit at all (notes\fix-j9-scripts.md).</summary>
    const int MaxValues = 1024;
    /// <summary>An instruction is gone through at most this many times while the values settle (loops).</summary>
    const int MaxVisits = 40;
    /// <summary>The code is followed at most this many calls deep, into callers (a parameter) and into the functions
    /// called (what one returns), one into another.</summary>
    const int MaxDepth = 3;
    /// <summary>The values the callers of a function give a parameter, at most (a list of FormIDs passed one by one;
    /// apc_galaxysmartcontainers.esm passes 111 to one function). Was 64; a guard like <see cref="MaxValues"/>.</summary>
    const int MaxCallerValues = 4096;

    // The instructions that write their first value (besides those followed one by one below)
    // (struct_create, 37, too: a new struct isn't None)
    static readonly HashSet<byte> WritesFirst = new() { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15, 16, 17, 18, 19, 27, 30, 31, 34, 35, 36, 37, 38, 40, 41 };

    sealed class Context
    {
        public required PexObject Object;
        public required string Plugin;
        public readonly Dictionary<string, string> AutoVars = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, PexVariable> ScriptVars = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Written = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The values the script's functions write into each script variable that another function can follow:
        /// a record got in code, a property, a constant, a parameter (to its callers), what the plugin's function returns;
        /// anything else stands as "script variable X".</summary>
        public readonly Dictionary<string, CodeValue[]> VarValues = new(StringComparer.OrdinalIgnoreCase);
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
            foreach (var f in o.Functions) result.Defined.Add(o.Name + "|" + f.Name);
            foreach (var p in o.Properties)
            {
                if (PexObject.ScriptOfType(p.Type) is { } pt) result.PropertyTypes[o.Name + "|" + p.Name] = pt;
                if (p.AutoVariable != null) ctx.AutoVars[p.AutoVariable] = p.Name;
            }
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
        var unset = new Dictionary<string, CodeValue[]>(StringComparer.OrdinalIgnoreCase);
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
            // A variable one path doesn't set has its value from before (a local: none yet)
            foreach (var (name, b) in from)
            {
                var a = into.TryGetValue(name, out var x) ? x : Unset(name);
                var u = Union(a, b);
                if (x == null || (!ReferenceEquals(u, x) && !Same(x, u))) { into[name] = u; changed = true; }
            }
            if (into.Count > from.Count)
                foreach (var name in into.Keys.Where(k => !from.ContainsKey(k)).ToList())
                {
                    var x = into[name];
                    var u = Union(x, Unset(name));
                    if (!ReferenceEquals(u, x) && !Same(x, u)) { into[name] = u; changed = true; }
                }
            return changed;
        }

        CodeValue[] Unset(string name)
        {
            if (unset.TryGetValue(name, out var have)) return have;
            return unset[name] = locals.Contains(name) || name.StartsWith("::", StringComparison.Ordinal)
                ? new[] { new CodeValue(ValueOrigin.None, "None") } : Of(new Dictionary<string, CodeValue[]>(), new PexValue(PexValue.Identifier, name, 0, 0, false));
        }

        CodeValue[] Of(Dictionary<string, CodeValue[]> state, PexValue v)
        {
            if (v.Kind == PexValue.None) return new[] { new CodeValue(ValueOrigin.None, "None") };
            if (v.IsLiteral) return new[] { new CodeValue(ValueOrigin.Literal, v.ToString(), v) };
            string name = v.Text ?? "";
            if (state.TryGetValue(name, out var have)) return have;
            if (ctx.AutoVars.TryGetValue(name, out var prop)) return new[] { new CodeValue(ValueOrigin.Property, $"property {prop}", Property: prop, Script: o.Name) };
            if (ctx.VarValues.TryGetValue(name, out var b)) return b;
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
                    // What another function reads from the variable: what can be followed, else the variable by name
                    var kept = values.Select(v => v.Kind is ValueOrigin.Built or ValueOrigin.Property or ValueOrigin.Literal or ValueOrigin.Parameter || (v.Kind == ValueOrigin.Returned && v.CalleeFunction != null)
                        ? v : new CodeValue(ValueOrigin.ScriptVariable, $"script variable {Clean(dest.Text)}")).ToArray();
                    ctx.VarValues[dest.Text] = ctx.VarValues.TryGetValue(dest.Text, out var had) ? Union(had, kept) : kept;
                }
            }


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
                            else Set(a[2], Of(state, a[1]).Select(h => Access(h, "prop:" + a[0].Text, PropertyType)).ToArray());
                    }
                    return;
                case PexFile.OpArrayGet:
                    // An element of a property's array keeps which property; the report reads the array on the records
                    if (a.Length >= 3) Set(a[0], Of(state, a[1]).Select(h => Access(h, "element", PropertyType)).ToArray());
                    return;
                case PexFile.OpStructGet:
                    // A member of a property's struct (or of an element of its array of structs): the report reads that member
                    if (a.Length >= 3) Set(a[0], Of(state, a[1]).Select(h => Access(h, "member:" + a[2].Text, PropertyType)).ToArray());
                    return;
                case PexFile.OpReturn:
                    // What the function returns, every return of it: a call of it in the plugin's scripts takes these (Resolve)
                    if (final && ctx.Emit != null && a.Length > 0)
                    {
                        var values = Of(state, a[0]);
                        string key = o.Name + "|" + f.Name;
                        ctx.Emit.Returns[key] = ctx.Emit.Returns.TryGetValue(key, out var had) ? Union(had, values) : values;
                    }
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
                    var call = new ScriptCall { Plugin = ctx.Plugin, Script = o.Name, Function = f.Shown, Call = shownName, Kind = "give", Values = args, On = on, OnType = on != null ? target : "" };
                    foreach (var x in args) call.Args.Add(Shown(x));
                    Item(call);
                    ctx.Emit.Calls.Add(call);
                }
            }
            // What a method of a script property returns (a form list's GetAt) comes from that property: a link the record
            // holds. What a method of a record got in code returns comes from that record. What a method of a parameter
            // returns waits for the parameter's callers (Method).
            // When the function is one of the plugin's own, Resolve replaces this by what it returns
            var returned = on != null ? on.Select(h => Method(h, shownName)).ToArray() : new[] { new CodeValue(ValueOrigin.Returned, $"what {shownName}() returns") };
            if (target.Length > 0) returned = returned.Select(v => v with { CalleeScript = target, CalleeFunction = name, CallArgs = args, On = on }).ToArray();
            Set(result, returned);
        }

        // The declared type of a property of this script (the script whose property a two-step chain reads)
        string? PropertyType(CodeValue h) => PexObject.ScriptOfType(o.Properties.FirstOrDefault(p => p.Name.Equals(h.Property, StringComparison.OrdinalIgnoreCase))?.Type);
    }

    /// <summary>
    /// What the code takes from a value: "element" (array_get), "member:X" (struct_get), "prop:X" (propget). From a
    /// script property it stays that property's (an element of it, a member of its struct, or, two steps, a property of
    /// the script on the record it holds: the report reads them on the records); from a record got in code, that
    /// record's. From a parameter or what the plugin's own function returns it waits (<see cref="CodeValue.Pending"/>)
    /// until <see cref="Resolve"/> knows the value; anything else is decided at run time.
    /// </summary>
    static CodeValue Access(CodeValue h, string step, Func<CodeValue, string?> propertyType)
    {
        if (step.StartsWith("call:", StringComparison.Ordinal)) return Method(h, step[5..]);
        // A step further into an entry of a form list leaves the entry
        if (h.Entries) h = h with { Entries = false };
        bool element = step == "element", member = step.StartsWith("member:", StringComparison.Ordinal);
        string name = element ? "" : step[(step.IndexOf(':') + 1)..];
        string text = element ? $"an element of {h.Text}" : $"{name} of {h.Text}";
        // From nothing, nothing (the game stops the function there)
        if (h.Kind == ValueOrigin.None) return h;
        if (h.Kind == ValueOrigin.Parameter || (h.Kind == ValueOrigin.Returned && h.CalleeFunction != null))
            return h with { Text = text, Pending = h.Pending == null ? step : h.Pending + "/" + step, Constant = null };
        if (h.Kind == ValueOrigin.Property)
        {
            if (element && !h.Derived) return h with { Text = text, Derived = true, Element = true, Constant = null };
            if (member && h.Member == null && (!h.Derived || h.Element)) return h with { Text = text, Derived = true, Member = name, Constant = null };
            if (!element && !member && !h.Derived && h.Member == null && !h.Element) return h with { Text = text, Derived = true, Chain = name, ChainType = propertyType(h), Constant = null };
            // Further in: the property's record is shown, as the value comes from it
            return !element && !member ? h with { Text = text, Derived = true } : h with { Text = text, Derived = true, Constant = null, Element = false, Member = null };
        }
        if (h.Kind == ValueOrigin.Built && (element || member)) return h with { Text = text, Derived = true, Constant = null, Element = false, Member = null };
        return new CodeValue(ValueOrigin.Returned, text);
    }

    /// <summary>
    /// What a method called on a value returns. From a script property (its member, its element) or a record got in code,
    /// it stays theirs: the report reads the record they hold; GetAt on a property is an entry of the form list it holds
    /// (<see cref="CodeValue.Entries"/>), and keeps which member and element. From a parameter or what the plugin's own
    /// function returns it waits (<see cref="CodeValue.Pending"/> "call:Name") until <see cref="Resolve"/> knows the
    /// value; anything else is decided at run time.
    /// </summary>
    static CodeValue Method(CodeValue h, string method)
    {
        // A method called on nothing: nothing (the game stops the function there)
        if (h.Kind == ValueOrigin.None) return h;
        // On what a call returns: one chain of calls ("what self.Pick().GetAmmo() returns")
        string text = h.Text.StartsWith("what ", StringComparison.Ordinal) && h.Text.EndsWith(" returns", StringComparison.Ordinal)
            ? $"what {h.Text[5..^8]}.{method}() returns" : $"what {h.Text}.{method}() returns";
        if (h.Kind == ValueOrigin.Parameter || (h.Kind == ValueOrigin.Returned && h.CalleeFunction != null))
            return h with { Text = text, Pending = h.Pending == null ? "call:" + method : h.Pending + "/call:" + method, Constant = null };
        if (h.Kind == ValueOrigin.Property && method.Equals("GetAt", StringComparison.OrdinalIgnoreCase) && !h.Entries && h.Chain == null)
            return h with { Text = text, Derived = true, Constant = null, Entries = true };
        if (h.Kind is ValueOrigin.Property or ValueOrigin.Built)
            return h with { Text = text, Derived = true, Constant = null, Element = false, Member = null, Entries = false };
        return new CodeValue(ValueOrigin.Returned, text);
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
        if (b.Length == 0) return a;
        if (a.Length == 0) return b;
        var list = new List<CodeValue>(a.Length + b.Length);
        var keys = new HashSet<(ValueOrigin, string, string?, string?, string?, string?)>();
        foreach (var v in a) if (keys.Add(v.Key)) list.Add(v);
        int had = list.Count;
        foreach (var v in b) if (keys.Add(v.Key)) list.Add(v);
        if (list.Count == had && had == a.Length) return a;
        if (list.Count > MaxValues) return new[] { new CodeValue(ValueOrigin.Computed, "one of several values the code works out") };
        return list.ToArray();
    }

    static bool Same(CodeValue[] a, CodeValue[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        if (a.Length == 1) return a[0].Key == b[0].Key;
        var keys = new HashSet<(ValueOrigin, string, string?, string?, string?, string?)>(a.Select(x => x.Key));
        return b.All(y => keys.Contains(y.Key));
    }

    /// <summary>A value as an argument shows it: one text, or the texts of the paths ("plugin = "a.esm" or "b.esm"").</summary>
    static string Shown(CodeValue[] values)
    {
        var real = values.Length > 1 ? values.Where(v => v.Kind != ValueOrigin.None).ToArray() : values;
        if (real.Length == 0) real = values;
        if (real.Length == 1) return real[0].Text;
        // Constants put in the same variable: "plugin = "a.esm" or "b.esm""
        var named = real.Select(v => v.Kind == ValueOrigin.Literal && v.Text.Contains(" = ", StringComparison.Ordinal) ? v.Text[..v.Text.IndexOf(" = ", StringComparison.Ordinal)] : null).Distinct().ToList();
        if (named.Count == 1 && named[0] != null) return $"{named[0]} = {string.Join(" or ", real.Select(v => v.Constant.ToString()))}";
        return string.Join(" or ", real.Select(v => v.Text).Distinct(StringComparer.Ordinal));
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
    public static void ResolveParameters(ScriptCodeResult result, Func<string, IEnumerable<string>> sameOrChild) =>
        Resolve(result, new ResolveScope { SameOrChild = sameOrChild, ParentOf = _ => null });

    /// <summary>What <see cref="Resolve"/> follows the code with.</summary>
    public sealed class ResolveScope
    {
        /// <summary>A script and the scripts that extend it.</summary>
        public required Func<string, IEnumerable<string>> SameOrChild { get; init; }
        /// <summary>The script a script extends, or null.</summary>
        public required Func<string, string?> ParentOf { get; init; }
        /// <summary>The calls of a function (script, function) in other plugins' scripts, asked for when no script of the
        /// plugin calls it (null when there are none to read). The report reads those of the plugins that have the plugin
        /// as a master, and its masters' when the plugin's script is a copy of a master's.</summary>
        public Func<string, string, IEnumerable<CallSite>>? Others { get; init; }
        /// <summary>What the answer calls the other plugins looked in for a script's callers ("the 14 plugins that have
        /// darkstar.esm as a master").</summary>
        public Func<string, string> OthersText { get; init; } = _ => "another plugin";
    }

    /// <summary>The call a value is in: the function called, the values given for its parameters, and the call around it.</summary>
    sealed record Binding(string Script, string Function, IReadOnlyList<CodeValue[]>? Args, Binding? Outer);

    /// <summary>
    /// Follows the values of each call out of its function: a parameter to the calls of its function in the plugin's
    /// scripts (and, when none calls it, in other plugins' scripts that do: <see cref="ResolveScope.Others"/>), what a
    /// call returns into the plugin's function that is called (its returns, with its parameters as that call gives
    /// them), and a property of what such a value holds as a two-step chain. Up to <see cref="MaxDepth"/> calls deep
    /// each way; at most <see cref="MaxCallerValues"/> values per argument.
    /// </summary>
    public static void Resolve(ScriptCodeResult result, ResolveScope scope)
    {
        static Dictionary<string, List<CallSite>> ByFunction(IEnumerable<CallSite> sites) =>
            sites.GroupBy(s => s.Function, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var byFunction = ByFunction(result.Sites);

        foreach (var call in result.Calls)
        {
            int count = call.Kind == "get" ? 2 : 1;
            // A give called on a record: the record handed out and the record it is called on, per way through the
            // callers (before the two are followed each on its own, below)
            if (call.Kind == "give" && call.On != null && call.Values.Count > 0) call.Pairs = Together(call.Values[0], call.On);
            // The record a give is called on (the list AddForm adds to) is followed the same way; what is said about it
            // isn't part of the call's Via, which is about the record handed out
            if (call.On != null && call.On.Any(NeedsWork)) call.On = Expand(call.On, 0, new List<string>(), null);
            if (!call.Values.Take(count).Any(v => v.Any(NeedsWork))) continue;
            var said = new List<string>();
            var values = call.Values.ToArray();
            for (int k = 0; k < Math.Min(count, values.Length); k++) values[k] = Expand(values[k], 0, said, null);
            call.Values = values;
            call.Via = string.Join("; ", said.Distinct());
            if (call.Kind == "give") { Item(call); }
            else { for (int k = 0; k < Math.Min(count, values.Length); k++) call.Args[k] = Shown(values[k]); Constants(call); }
        }

        bool NeedsWork(CodeValue v) => v.Kind == ValueOrigin.Parameter || Own(v);

        // Two values of one call followed through the callers together: each call of the function gives its parameters in
        // both at once (a call ProcessItemList(Armors, ArmorLists) pairs the armors with the armor lists, not with the
        // weapon lists another call passes). Up to MaxDepth calls deep; then each pair is followed as Expand follows a
        // value (its returns, the callers left).
        List<(CodeValue[] Item, CodeValue[] On)> Together(CodeValue[] item, CodeValue[] on)
        {
            var worlds = new List<CodeValue[][]> { new[] { item, on } };
            var done = new List<CodeValue[][]>();
            for (int depth = 0; worlds.Count > 0; depth++)
            {
                var next = new List<CodeValue[][]>();
                foreach (var w in worlds)
                {
                    var p = w.SelectMany(x => x).FirstOrDefault(v => v.Kind == ValueOrigin.Parameter && v.Function != null && v.Script != null);
                    var sites = p == null || depth >= MaxDepth ? null : SitesOf(p);
                    if (sites == null || sites.Count == 0) { done.Add(w); continue; }
                    foreach (var site in sites) next.Add(w.Select(values => Bind(values, p!, site)).ToArray());
                }
                // A guard like MaxCallerValues: past it, the two are followed each on its own (one pair of every value)
                if (done.Count + next.Count > MaxCallerValues) return new() { (Expand(item, 0, new List<string>(), null), Expand(on, 0, new List<string>(), null)) };
                worlds = next;
            }
            var pairs = new List<(CodeValue[] Item, CodeValue[] On)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in done)
            {
                var a = Expand(w[0], 0, new List<string>(), null);
                var b = Expand(w[1], 0, new List<string>(), null);
                if (seen.Add(string.Join("\u0001", a.Select(x => x.Key.ToString())) + "\u0002" + string.Join("\u0001", b.Select(x => x.Key.ToString())))) pairs.Add((a, b));
            }
            return pairs;
        }

        // The calls of a parameter's function in the plugin's scripts that give that parameter
        List<CallSite> SitesOf(CodeValue p)
        {
            var scripts = scope.SameOrChild(p.Script!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return byFunction.TryGetValue(p.Function!, out var l) ? l.Where(s => scripts.Contains(s.Target) && p.Parameter < s.Args.Count).ToList() : new();
        }

        // The values with each parameter of p's function replaced by what one call gives it (and what the code took from it)
        CodeValue[] Bind(CodeValue[] values, CodeValue p, CallSite site)
        {
            var output = new List<CodeValue>();
            var keys = new HashSet<(ValueOrigin, string, string?, string?, string?, string?)>();
            foreach (var v in values)
            {
                bool mine = v.Kind == ValueOrigin.Parameter && v.Parameter >= 0 && v.Parameter < site.Args.Count
                    && string.Equals(v.Function, p.Function, StringComparison.OrdinalIgnoreCase) && string.Equals(v.Script, p.Script, StringComparison.OrdinalIgnoreCase);
                foreach (var g in mine ? site.Args[v.Parameter].Select(a => v.Pending != null ? Apply(a, v.Pending) : a) : new[] { v })
                    if (keys.Add(g.Key)) output.Add(g);
            }
            return output.ToArray();
        }
        bool Own(CodeValue v) => v.CalleeFunction != null && v.CalleeScript != null && Definers(v.CalleeScript, v.CalleeFunction).Any();

        // The scripts whose version of the function a call of script.function runs: the nearest that defines it, going up
        // the scripts it extends, and every script extending it that defines its own
        IEnumerable<string> Definers(string script, string function)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (string? at = script; at is { Length: > 0 } && seen.Add(at); at = scope.ParentOf(at))
                if (result.Defined.Contains(at + "|" + function)) { yield return at; break; }
            foreach (var child in scope.SameOrChild(script).Skip(1))
                if (seen.Add(child) && result.Defined.Contains(child + "|" + function)) yield return child;
        }

        CodeValue[] Expand(CodeValue[] values, int depth, List<string> said, Binding? bind)
        {
            var output = new List<CodeValue>();
            foreach (var v in values)
            {
                if (v.Kind == ValueOrigin.Parameter && v.Function != null && v.Script != null)
                {
                    IEnumerable<CodeValue> got;
                    // Inside a function the code was followed into: the parameter is what that call passed
                    if (bind != null && bind.Args != null && bind.Function.Equals(v.Function, StringComparison.OrdinalIgnoreCase) && bind.Script.Equals(v.Script, StringComparison.OrdinalIgnoreCase))
                        got = v.Parameter < bind.Args.Count ? Expand(bind.Args[v.Parameter], depth, said, bind.Outer) : new[] { v };
                    else got = Callers(v, depth, said);
                    output.AddRange(v.Pending != null ? got.Select(g => ReferenceEquals(g, v) ? v : Apply(g, v.Pending)) : got);
                }
                else if (Own(v))
                {
                    var got = Returns(v, depth, said, bind);
                    output.AddRange(v.Pending != null ? got.Select(g => ReferenceEquals(g, v) ? v : Apply(g, v.Pending)) : got);
                }
                else output.Add(v);
            }
            var distinct = new List<CodeValue>();
            var keys = new HashSet<(ValueOrigin, string, string?, string?, string?, string?)>();
            foreach (var v in output) if (keys.Add(v.Key)) distinct.Add(v);
            return distinct.Count > MaxCallerValues ? new[] { new CodeValue(ValueOrigin.Computed, "one of several values the callers work out") } : distinct.ToArray();
        }

        // The values the calls of v's function give it, in the plugin's scripts, else in the other plugins' scripts
        IEnumerable<CodeValue> Callers(CodeValue v, int depth, List<string> said)
        {
            var scripts = scope.SameOrChild(v.Script!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sites = byFunction.TryGetValue(v.Function!, out var l) ? l.Where(s => scripts.Contains(s.Target) && v.Parameter < s.Args.Count).ToList() : new();
            int at = v.Text.LastIndexOf("parameter ", StringComparison.Ordinal);
            string param = at >= 0 ? new string(v.Text[(at + "parameter ".Length)..].TakeWhile(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == ':').ToArray()) : v.Text;
            // An event: OnInit, OnActivate …, and the remote events a script registers for (::remote_actor_OnItemEquipped)
            bool evented = v.Function!.StartsWith("On", StringComparison.OrdinalIgnoreCase) || v.Function.StartsWith("::remote_", StringComparison.OrdinalIgnoreCase);
            bool looked = false;
            if (sites.Count == 0 && !evented && scope.Others != null)
            {
                sites = scope.Others(v.Script!, v.Function!).Where(s => scripts.Contains(s.Target) && v.Parameter < s.Args.Count).ToList();
                looked = true;
            }
            if (sites.Count == 0)
            {
                said.Add(evented ? $"{param} of {v.Function} is passed by the game (an event)"
                    : looked ? $"no script of the plugin calls {Short(v.Script!)}.{v.Function}, nor does a script of {scope.OthersText(v.Script!)}: a fragment, the console or another mod's script decides {param}"
                    : $"no script of the plugin calls {Short(v.Script!)}.{v.Function}: another mod, a fragment or the console decides {param}");
                return new[] { v };
            }
            if (depth >= MaxDepth) { said.Add($"{param} of {v.Function} is given by callers more than {MaxDepth} calls deep"); return new[] { v }; }
            said.Add($"{param} of {v.Function} is given by {string.Join(", ", sites.Select(s => $"{Short(s.CallerScript)}.{s.CallerFunction}" + (s.CallerPlugin != null ? $" ({s.CallerPlugin})" : "")).Distinct())}");
            var output = new List<CodeValue>();
            foreach (var s in sites) output.AddRange(Expand(s.Args[v.Parameter], depth + 1, said, null));
            return output;
        }

        // What a call of the plugin's own function returns: its returns, with its parameters as the call gives them
        IEnumerable<CodeValue> Returns(CodeValue v, int depth, List<string> said, Binding? bind)
        {
            string shown = $"{Short(v.CalleeScript!)}.{v.CalleeFunction}";
            if (depth >= MaxDepth) { said.Add($"what {shown} returns is more than {MaxDepth} calls deep"); return new[] { v }; }
            var output = new List<CodeValue>();
            var from = new List<string>();
            foreach (var d in Definers(v.CalleeScript!, v.CalleeFunction!))
            {
                if (!result.Returns.TryGetValue(d + "|" + v.CalleeFunction, out var rets)) continue;
                from.Add($"{Short(d)}.{v.CalleeFunction}");
                foreach (var r in Expand(rets, depth + 1, said, new Binding(d, v.CalleeFunction!, v.CallArgs, bind)))
                {
                    // A property of the function's own script, when the method was called on what a property or a
                    // parameter of the caller holds: that property on the record it holds (two steps)
                    bool ownProperty = r.Kind == ValueOrigin.Property && !r.Derived && r.Member == null && !r.Element && r.Script != null && r.Script.Equals(d, StringComparison.OrdinalIgnoreCase);
                    var holders = v.On?.Where(h => h.Kind is ValueOrigin.Property or ValueOrigin.Parameter && !h.Derived && h.Member == null && !h.Element && h.Pending == null).ToList();
                    if (ownProperty && holders is { Count: > 0 })
                    {
                        foreach (var h in holders)
                        {
                            var chained = Access(h, "prop:" + r.Property, TypeOf);
                            output.AddRange(chained.Kind == ValueOrigin.Parameter ? Expand(new[] { chained }, depth + 1, said, bind) : new[] { chained });
                        }
                        continue;
                    }
                    output.Add(r);
                }
            }
            if (from.Count == 0) { said.Add($"{shown} returns nothing the code sets"); return new[] { v }; }
            // Nothing the function returns can be followed (it works its value out): the call stays as it is
            if (output.All(r => r.Kind is ValueOrigin.None or ValueOrigin.Computed)) { said.Add($"what {shown}() returns is worked out at run time (read from its code)"); return new[] { v }; }
            said.Add($"what {shown}() returns is read from its code" + (from.Count == 1 && from[0] == shown ? "" : $" ({string.Join(", ", from)})"));
            return output;
        }

        // What the code took from a value that is known now, step by step
        CodeValue Apply(CodeValue r, string pending)
        {
            foreach (var step in pending.Split('/')) r = Access(r, step, TypeOf);
            return r;
        }
        string? TypeOf(CodeValue r) => r.Script != null && r.Property != null && result.PropertyTypes.TryGetValue(r.Script + "|" + r.Property, out var t) ? t : null;
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
