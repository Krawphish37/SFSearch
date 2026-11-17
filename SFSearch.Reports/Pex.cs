// Compiled Papyrus (.pex) as Starfield writes it, read without decompiling: the string table, each object's parent,
// variables and properties, and every function's instructions. scripts uses it for the code that reaches records
// without a link in a record: Game.GetFormFromFile, Game.GetForm and the like, and AddItem, AddForm, PlaceAtMe … on
// a record the code got that way. The format (version 3.12, game 4, little-endian) was checked against all 4,740
// compiled scripts of Starfield - Misc.ba2: each is read to its last byte, and each object to the size it gives.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SFSearch.Reports;

/// <summary>One value of an instruction or a variable's start value.</summary>
public readonly record struct PexValue(byte Kind, string? Text, int Int, float Float, bool Bool)
{
    public const byte None = 0, Identifier = 1, String = 2, Integer = 3, FloatKind = 4, BoolKind = 5;
    public bool IsIdentifier => Kind == Identifier;
    public bool IsLiteral => Kind is String or Integer or FloatKind or BoolKind;

    /// <summary>The value as the code writes it: 0x0000ABCD for an integer that looks like a FormID, "text" for a string.</summary>
    public override string ToString() => Kind switch
    {
        None => "None",
        Identifier => Text ?? "",
        String => "\"" + Text + "\"",
        Integer => Int is >= 0 and < 4096 ? Int.ToString(CultureInfo.InvariantCulture) : "0x" + Int.ToString("X8", CultureInfo.InvariantCulture),
        FloatKind => Float.ToString(CultureInfo.InvariantCulture),
        BoolKind => Bool ? "true" : "false",
        _ => "?",
    };
}

public sealed record PexInstruction(byte Op, PexValue[] Args);

public sealed class PexFunction
{
    public string State { get; init; } = "";
    public string Name { get; init; } = "";
    public List<string> Parameters { get; } = new();
    public List<string> Locals { get; } = new();
    /// <summary>The type of each parameter and local (int, ObjectReference, DarkStar:Lib, Foo[] …), as the script declares it.</summary>
    public List<string> ParameterTypes { get; } = new();
    public List<string> LocalTypes { get; } = new();
    public string ReturnType { get; init; } = "";
    public List<PexInstruction> Code { get; } = new();
    /// <summary>"OnInit", or "Busy.OnTimer" for a function of a named state.</summary>
    public string Shown => State.Length == 0 ? Name : State + "." + Name;

    /// <summary>The declared type of a parameter or local, or null when the function has none of that name.</summary>
    public string? TypeOf(string name)
    {
        for (int i = 0; i < Locals.Count; i++) if (string.Equals(Locals[i], name, StringComparison.OrdinalIgnoreCase)) return LocalTypes[i];
        for (int i = 0; i < Parameters.Count; i++) if (string.Equals(Parameters[i], name, StringComparison.OrdinalIgnoreCase)) return ParameterTypes[i];
        return null;
    }
}

public sealed record PexVariable(string Name, string Type, PexValue Start);
public sealed record PexProperty(string Name, string Type, string? AutoVariable);

public sealed class PexObject
{
    public string Name { get; init; } = "";
    public string Parent { get; init; } = "";
    public List<PexVariable> Variables { get; } = new();
    public List<PexProperty> Properties { get; } = new();
    public List<PexFunction> Functions { get; } = new();

    /// <summary>The declared type of a name a function of this object uses: its parameter or local, a script
    /// variable, a property, or self (this object). Null when none has that name.</summary>
    public string? TypeOf(PexFunction f, string name)
    {
        if (name.Equals("self", StringComparison.OrdinalIgnoreCase)) return Name;
        if (f.TypeOf(name) is { } t) return t;
        foreach (var v in Variables) if (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) return v.Type;
        foreach (var p in Properties) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Type;
        return null;
    }

    static readonly HashSet<string> Basic = new(StringComparer.OrdinalIgnoreCase) { "", "int", "float", "bool", "string", "var", "none" };

    /// <summary>The script a type names (DarkStar:Lib for DarkStar:Lib[], Lib for Lib#Entry), or null for int, float,
    /// bool, string and var.</summary>
    public static string? ScriptOfType(string? type)
    {
        if (type == null) return null;
        if (type.EndsWith("[]", StringComparison.Ordinal)) type = type[..^2];
        int hash = type.IndexOf('#');
        if (hash >= 0) type = type[..hash];
        return Basic.Contains(type) ? null : type;
    }

    /// <summary>The other scripts this object needs to run, each once, with how: "parent" (it extends it), "calls" (its
    /// code calls a function of it), "type" (it names it as the type of a variable, property, parameter or result).
    /// The game links them all when it loads the script.</summary>
    public List<(string Script, string How)> Uses()
    {
        var how = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? script, string h)
        {
            if (script == null || script.Equals(Name, StringComparison.OrdinalIgnoreCase)) return;
            if (!how.TryGetValue(script, out var had) || Rank(h) < Rank(had)) how[script] = h;
        }
        static int Rank(string h) => h == "parent" ? 0 : h == "calls" ? 1 : 2;
        if (Parent.Length > 0) Add(Parent, "parent");
        foreach (var v in Variables) Add(ScriptOfType(v.Type), "type");
        foreach (var p in Properties) Add(ScriptOfType(p.Type), "type");
        foreach (var f in Functions)
        {
            Add(ScriptOfType(f.ReturnType), "type");
            foreach (var t in f.ParameterTypes.Concat(f.LocalTypes)) Add(ScriptOfType(t), "type");
            foreach (var ins in f.Code)
            {
                if (ins.Op == PexFile.OpCallStatic && ins.Args.Length > 0) Add(ScriptOfType(ins.Args[0].Text), "calls");
                else if (ins.Op == PexFile.OpCallMethod && ins.Args.Length > 1 && ins.Args[1].IsIdentifier) Add(ScriptOfType(TypeOf(f, ins.Args[1].Text ?? "")), "calls");
                else if (ins.Op == PexFile.OpCast && ins.Args.Length > 1 && ins.Args[0].IsIdentifier) Add(ScriptOfType(TypeOf(f, ins.Args[0].Text ?? "")), "type");
            }
        }
        return how.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed class PexFile
{
    public const uint Magic = 0xFA57C0DE;
    public byte Major { get; private init; }
    public byte Minor { get; private init; }
    public ushort Game { get; private init; }
    public string Source { get; private init; } = "";
    public string[] Strings { get; private init; } = Array.Empty<string>();
    public List<PexObject> Objects { get; } = new();

    // The opcodes: how many values each takes, and whether a count and that many more values follow
    public const byte OpJump = 20, OpJumpTrue = 21, OpJumpFalse = 22, OpReturn = 26;
    public const byte OpAssign = 13, OpCast = 14, OpCallMethod = 23, OpCallParent = 24, OpCallStatic = 25, OpPropGet = 28, OpArrayGet = 32, OpStructGet = 38;
    static readonly (int Fixed, bool More)[] Ops = BuildOps();

    static (int, bool)[] BuildOps()
    {
        var o = new (int, bool)[51];
        o[0] = (0, false);
        for (int i = 1; i <= 9; i++) o[i] = (3, false);       // iadd … imod
        for (int i = 10; i <= 14; i++) o[i] = (2, false);     // not, ineg, fneg, assign, cast
        for (int i = 15; i <= 19; i++) o[i] = (3, false);     // the comparisons
        o[20] = (1, false); o[21] = (2, false); o[22] = (2, false); // jmp, jmpt, jmpf
        o[23] = (3, true); o[24] = (2, true); o[25] = (3, true);  // callmethod, callparent, callstatic
        o[26] = (1, false); o[27] = (3, false); o[28] = (3, false); o[29] = (3, false); // return, strcat, propget, propset
        o[30] = (2, false); o[31] = (2, false); o[32] = (3, false); o[33] = (3, false); o[34] = (4, false); o[35] = (4, false); // arrays
        o[36] = (3, false); o[37] = (1, false); o[38] = (3, false); o[39] = (3, false); // is, struct_create, struct_get, struct_set
        o[40] = (5, false); o[41] = (5, false); o[42] = (3, false); o[43] = (3, false); o[44] = (1, false); o[45] = (3, false); o[46] = (1, false);
        // Starfield's own: array_getallmatchingstructs, lock_guards, unlock_guards, try_lock_guards
        o[47] = (6, false); o[48] = (0, true); o[49] = (0, true); o[50] = (1, true);
        return o;
    }

    /// <summary>
    /// True when a compiled script's string table holds one of <paramref name="lowerNames"/> (ASCII lower case; letter
    /// case doesn't count, as in Papyrus). Only the header and the string table are read: every name the script's code
    /// uses (a function it calls, a script it extends or names) is in that table, so a script that doesn't hold the name
    /// can be passed over without reading its code. False for bytes that aren't a Starfield compiled script.
    /// </summary>
    public static bool StringTableHolds(ReadOnlySpan<byte> bytes, IReadOnlyList<byte[]> lowerNames)
    {
        if (bytes.Length < 18 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 4) return false;
        int pos = 16;
        for (int i = 0; i < 3; i++)
        {
            if (pos + 2 > bytes.Length) return false;
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(bytes[pos..]);
        }
        if (pos + 2 > bytes.Length) return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[pos..]);
        pos += 2;
        for (int i = 0; i < count; i++)
        {
            if (pos + 2 > bytes.Length) return false;
            int n = BinaryPrimitives.ReadUInt16LittleEndian(bytes[pos..]);
            pos += 2;
            if (pos + n > bytes.Length) return false;
            var s = bytes.Slice(pos, n);
            pos += n;
            foreach (var name in lowerNames)
            {
                if (name.Length != n) continue;
                int k = 0;
                while (k < n && (s[k] is >= (byte)'A' and <= (byte)'Z' ? (byte)(s[k] + 32) : s[k]) == name[k]) k++;
                if (k == n) return true;
            }
        }
        return false;
    }

    /// <summary>Reads a compiled script. Throws <see cref="InvalidDataException"/> when the bytes aren't one Starfield writes.</summary>
    public static PexFile Read(byte[] bytes)
    {
        var r = new Reader(bytes);
        if (r.U32() != Magic) throw new InvalidDataException("not a compiled Papyrus script (its first bytes aren't 0xFA57C0DE as Starfield writes them)");
        byte major = r.U8(), minor = r.U8();
        ushort game = r.U16();
        if (game != 4) throw new InvalidDataException($"compiled for game {game} (version {major}.{minor}), not for Starfield (game 4)");
        r.U64();
        string source = r.WString(); r.WString(); r.WString();
        var strings = new string[r.U16()];
        for (int i = 0; i < strings.Length; i++) strings[i] = r.WString();
        var file = new PexFile { Major = major, Minor = minor, Game = game, Source = source, Strings = strings };
        r.Strings = strings;

        if (r.U8() != 0)
        {
            r.U64();
            for (int n = r.U16(), i = 0; i < n; i++) { r.U16(); r.U16(); r.U16(); r.U8(); r.Skip(2 * r.U16()); }
            for (int n = r.U16(), i = 0; i < n; i++) { r.U16(); r.U16(); r.U16(); r.U32(); r.Skip(2 * r.U16()); }
            for (int n = r.U16(), i = 0; i < n; i++) { r.U16(); r.U16(); r.Skip(2 * r.U16()); }
        }
        for (int n = r.U16(), i = 0; i < n; i++) { r.U16(); r.U8(); }

        for (int n = r.U16(), i = 0; i < n; i++)
        {
            string name = r.Str();
            uint size = r.U32();
            int start = r.Pos;
            var o = new PexObject { Name = name, Parent = r.Str() };
            r.U16(); r.U8(); r.U32(); r.U16(); // doc, const, user flags, auto state
            for (int s = r.U16(), j = 0; j < s; j++)
            {
                r.U16();
                for (int m = r.U16(), k = 0; k < m; k++) { r.U16(); r.U16(); r.U32(); r.Value(); r.U8(); r.U16(); }
            }
            for (int v = r.U16(), j = 0; j < v; j++)
            {
                string vn = r.Str(), vt = r.Str(); r.U32();
                var start0 = r.Value(); r.U8();
                o.Variables.Add(new PexVariable(vn, vt, start0));
            }
            for (int g = r.U16(), j = 0; j < g; j++) r.U16(); // guards
            for (int p = r.U16(), j = 0; j < p; j++)
            {
                string pn = r.Str(), pt = r.Str(); r.U16(); r.U32();
                byte flags = r.U8();
                string? auto = null;
                if ((flags & 4) != 0) auto = r.Str();
                else
                {
                    if ((flags & 1) != 0) o.Functions.Add(r.Function("", pn + ".Get"));
                    if ((flags & 2) != 0) o.Functions.Add(r.Function("", pn + ".Set"));
                }
                o.Properties.Add(new PexProperty(pn, pt, auto));
            }
            for (int st = r.U16(), j = 0; j < st; j++)
            {
                string state = r.Str();
                for (int f = r.U16(), k = 0; k < f; k++) { string fn = r.Str(); o.Functions.Add(r.Function(state, fn)); }
            }
            // Bethesda's compiler counts the size field itself; others (MissionsGalore.esm's fragments) leave it out
            if (r.Pos - start != size - 4 && r.Pos - start != size) throw new InvalidDataException($"object {name} takes {r.Pos - start + 4} bytes, and its header says {size}");
            file.Objects.Add(o);
        }
        if (r.Pos != bytes.Length) throw new InvalidDataException($"{bytes.Length - r.Pos} bytes follow the last object");
        return file;
    }

    sealed class Reader
    {
        readonly byte[] _b;
        public int Pos;
        public string[] Strings = Array.Empty<string>();
        public Reader(byte[] b) { _b = b; }
        void Need(int n) { if (Pos + n > _b.Length) throw new InvalidDataException($"the file ends at byte {_b.Length:N0}, inside the script"); }
        public byte U8() { Need(1); return _b[Pos++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(Pos)); Pos += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(Pos)); Pos += 4; return v; }
        public ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64LittleEndian(_b.AsSpan(Pos)); Pos += 8; return v; }
        public void Skip(int n) { Need(n); Pos += n; }
        public string WString() { int n = U16(); Need(n); string s = Encoding.UTF8.GetString(_b, Pos, n); Pos += n; return s; }
        public string Str()
        {
            int i = U16();
            if (i >= Strings.Length) throw new InvalidDataException($"string {i} named at byte {Pos - 2:N0}; the table has {Strings.Length}");
            return Strings[i];
        }
        public PexValue Value()
        {
            byte kind = U8();
            switch (kind)
            {
                case PexValue.None: return new PexValue(kind, null, 0, 0, false);
                case PexValue.Identifier:
                case PexValue.String: return new PexValue(kind, Str(), 0, 0, false);
                case PexValue.Integer: return new PexValue(kind, null, (int)U32(), 0, false);
                case PexValue.FloatKind: return new PexValue(kind, null, 0, BitConverter.Int32BitsToSingle((int)U32()), false);
                case PexValue.BoolKind: return new PexValue(kind, null, 0, 0, U8() != 0);
                default: throw new InvalidDataException($"a value of kind {kind} at byte {Pos - 1:N0}");
            }
        }
        public PexFunction Function(string state, string name)
        {
            var f = new PexFunction { State = state, Name = name, ReturnType = Str() };
            U16(); U32(); U8(); // doc, user flags, flags
            for (int n = U16(), i = 0; i < n; i++) { f.Parameters.Add(Str()); f.ParameterTypes.Add(Str()); }
            for (int n = U16(), i = 0; i < n; i++) { f.Locals.Add(Str()); f.LocalTypes.Add(Str()); }
            for (int n = U16(), i = 0; i < n; i++)
            {
                byte op = U8();
                if (op >= Ops.Length) throw new InvalidDataException($"opcode {op} in {name} at byte {Pos - 1:N0}");
                var (fixedCount, more) = Ops[op];
                var args = new List<PexValue>(fixedCount + 2);
                for (int k = 0; k < fixedCount; k++) args.Add(Value());
                if (more)
                {
                    var count = Value();
                    if (count.Kind != PexValue.Integer || count.Int < 0) throw new InvalidDataException($"opcode {op} in {name}: its count of values isn't a number");
                    for (int k = 0; k < count.Int; k++) args.Add(Value());
                }
                f.Code.Add(new PexInstruction(op, args.ToArray()));
            }
            return f;
        }
    }
}
