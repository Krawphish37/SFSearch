// The named columns of a type too big to read whole, read straight from Mutagen's object: no JSON (GAPS S5, fix
// g-sql, 2026-11-13; every kind since fix-s4s5-world-reads, GAPS S4). Each record of Starfield.esm's 3.29 million
// placed objects used to be written as JSON, made into SQLite's JSONB and read back through the column's expression,
// to give one field; a surface tree's flags took the tree's 3 MB of JSON.
//
// A column is read this way when what it gives can't differ from what its expression gives over the JSON. The value
// is written as Mutagen.Bethesda.Serialization's JSON writer writes that kind (with its own helpers):
//   - a link, a FormKey, a number, a yes/no, a float (the writer's shortest form, read back as SQLite reads it), a
//     point ("x, y"), a text;
//   - an enum by its name, a flags enum as the JSON list of its flags' names (undefined bits as "0x…"), bytes as
//     "0x" and their hex digits, a color as "#AARRGGBB", a Guid, an asset's path as given, a record type's four letters;
//   - a translated text in the query's language, as sfs_text gives it from the JSON (the text of a plugin with no
//     strings files in every language, NULL for a language a localized text lacks, the Creation Kit's tag taken off);
//   - the kind of an object (ObjectType): the class the writer names, with its declaring class and generic argument.
// The view's rules are the same: the serializer leaves out a null value only, so a value read is the value written;
// a nullable object on the way that is absent gives NULL (the view's guard), else the field's default; a field under a
// kind of object with a default is read only where every object on its way is of a kind that holds it (the view's
// CASE over MutagenObjectType), and one with no default wherever the object has a property of that name (the JSON's
// path). The tests direct-columns and world-reads hold both ways against each other for every column and record.
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Serialization.Utility;
using Mutagen.Bethesda.Strings;
using Noggog;

namespace SFSearch.Query;

/// <summary>One column of a typed view read straight from the record's Mutagen object.</summary>
sealed class DirectColumn
{
    readonly string[] _path;
    readonly string _kind;
    /// <summary>What the view gives when an object on the way is absent: NULL past a nullable object (the view's
    /// guard), otherwise the field's default.</summary>
    readonly bool _absentIsNull;
    readonly object? _default;
    /// <summary>The query's language, for a translated text.</summary>
    readonly string? _language;
    /// <summary>The kinds the objects on the way must be (by how many steps in they sit: 0 is the record or entry
    /// itself), for a field under a kind of object that has a default; otherwise none.</summary>
    readonly (int Depth, HashSet<string> Kinds)[] _guards;
    /// <summary>Walk the path by each object's own class, a step at a time: a field under a kind of object.</summary>
    readonly bool _dynamic;
    /// <summary>For a column under an object the record always has, which the view's guard makes NULL when the JSON
    /// lacks it (ColumnSpec.GuardOmittable): the steps to that object (-1: none). The serializer leaves such an object out
    /// when it has nothing in it to write, so that object reads as absent then.</summary>
    readonly int _omittableAt = -1;
    /// <summary>A column whose field is a plain value in some kinds of the entry and a list of plain values in others
    /// (VaryingColumn.cs: a script property's Data): a list is given as its JSON text.</summary>
    readonly bool _varying;
    readonly ConcurrentDictionary<Type, Func<object, object?>?> _getters = new();

    DirectColumn(string[] path, string kind, bool absentIsNull, object? dflt, string? language = null, (int, HashSet<string>)[]? guards = null, bool dynamic = false, int omittableAt = -1, bool varying = false)
    {
        _path = path; _kind = kind; _absentIsNull = absentIsNull; _default = dflt; _language = language; _omittableAt = omittableAt; _varying = varying;
        _guards = guards ?? Array.Empty<(int, HashSet<string>)>(); _dynamic = dynamic || varying || _guards.Length > 0 || omittableAt >= 0;
    }

    /// <summary>A reader of the entry itself: a flags list's entries are the flags' names.</summary>
    internal static DirectColumn Entry(string kind) => new(Array.Empty<string>(), kind, true, null);

    /// <summary>
    /// The direct reader of a list view's column, read from one entry of the innermost list (GAPS S45): the same rules
    /// as <see cref="For"/>, with the path relative to the entry ("" for a list of plain values: the entry itself).
    /// Null when the column is read from the JSON.
    /// </summary>
    public static DirectColumn? ForEntry(ColumnSpec c, string? language = null) => Make(c, language, entry: true);

    static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "FormLink", "FormKey", "Bool", "Int8", "Int16", "Int32", "Int64", "UInt8", "UInt16", "UInt32", "UInt64", "Float", "Double", "Percent", "Half",
        "Enum", "Flags", FlagsRaw.Kind, "String", "ByteArray", "Color", "Guid", "AssetLink", "RecordType", "TimeOnly",
        "P2Float", "P3Float", "P2Int16", "P3Int16", "P2Int32", "P3Int32", "P2UInt8", "P3UInt8", "P3UInt16",
    };

    /// <summary>The direct reader of a column, or null when the column is read from the JSON. A translated text needs
    /// the query's language.</summary>
    public static DirectColumn? For(ColumnSpec c, string? language = null) => Make(c, language, entry: false);

    static DirectColumn? Make(ColumnSpec c, string? language, bool entry)
    {
        if (c.Kind == "ObjectType")
            return c.ObjectPath is { } op ? new DirectColumn(op.Length == 0 ? Array.Empty<string>() : op.Split('.'), "ObjectType", true, null, dynamic: true) : null;
        if (!Kinds.Contains(c.Kind) || c.RelPath is not { } rel || !entry && rel.Length == 0) return null;
        // A list or an object under the same name in another kind of the entry: the JSON's text of it (ShapeVaries),
        // read here where every kind holds a plain value or a list of plain values (VaryingColumn.cs)
        if (c.ShapeVaries && (!entry || !VaryingColumn.Supported(c))) return null;
        if (!entry && c.SchemaPath.Contains('[')) return null;
        bool translated = c.Kind == "String" && c.Expr.Contains("sfs_text", StringComparison.Ordinal);
        if (translated && language == null) return null;
        // The view's CASE and coalesce apply only to a field that is always there and has a default (ViewBuilder.Guarded)
        bool guarded = !c.Nullable && c.DefaultSql != null;
        object? dflt = null;
        if (guarded && !TryLiteral(c.DefaultSql!, out dflt)) return null;
        var guards = new List<(int, HashSet<string>)>();
        if (guarded && !c.FromBase)
            foreach (var (objPath, kinds) in c.VariantGuards)
                guards.Add((objPath.Length == 0 ? 0 : objPath.Split('.').Length, kinds));
        // A field reached through kinds of object: walked by each object's own class
        bool dynamic = !c.FromBase || c.SchemaPath.Contains('{');
        int omittableAt = c.GuardOmittable && c.NullGuard is { } ng ? ng.Split('.').Length : -1;
        return new DirectColumn(rel.Length == 0 ? Array.Empty<string>() : rel.Split('.'), c.Kind, !guarded || c.NullGuard != null, dflt, translated ? language : null, guards.ToArray(), dynamic, omittableAt, c.ShapeVaries);
    }

    /// <summary>A default as SQL text (<c>0</c>, <c>1.5</c>, <c>'Medium'</c>) as the value SQLite gives for it.</summary>
    internal static bool TryLiteral(string sql, out object? value)
    {
        value = null;
        if (sql.Length >= 2 && sql[0] == '\'' && sql[^1] == '\'') { value = sql[1..^1].Replace("''", "'"); return true; }
        if (long.TryParse(sql, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) { value = l; return true; }
        if (double.TryParse(sql, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) { value = d; return true; }
        return false;
    }

    /// <summary>The column's value for one record: a long, a double, a text or null, as SQLite gives it from the JSON.</summary>
    public object? Read(object record)
    {
        if (_dynamic) return ReadDynamic(record);
        if (_path.Length == 0) return Convert(record);
        // A class without the path (an entry of another kind in a list of several): walked a step at a time
        var get = _getters.GetOrAdd(record.GetType(), Compile);
        if (get == null) return ReadDynamic(record);
        object? raw = get(record);
        if (ReferenceEquals(raw, Absent)) return _absentIsNull ? null : _default;
        return Convert(raw);
    }

    /// <summary>The path walked by each object's own class. An object on the way that is absent, or has no property of
    /// the next name, is an absent object (NULL past a nullable one, else the default); the field itself absent is a
    /// null value (the default where the view has one); an object of a kind the guards don't take gives NULL.</summary>
    object? ReadDynamic(object record)
    {
        object? cur = record;
        for (int i = 0; i < _path.Length; i++)
        {
            if (cur == null) return Missing(i);
            foreach (var (depth, kinds) in _guards)
                if (depth == i && !kinds.Contains(KindOf(cur))) return null;
            bool last = i == _path.Length - 1;
            var get = Steps.GetOrAdd((cur.GetType(), _path[i], last), k => Getter(k.Item1, k.Item2, k.Item3));
            if (get == null) return i == _path.Length - 1 ? Convert(null) : Missing(i + 1);
            cur = get(cur);
            // The object the view's guard needs, left out of the JSON when the serializer has nothing in it to write
            if (i + 1 == _omittableAt && cur != null && NothingToWrite(cur)) return Missing(i + 1);
        }
        if (_kind == "ObjectType") return cur == null ? null : ObjectTypeOf(cur);
        return Convert(cur);
    }

    /// <summary>Whether the serializer writes no field of an object (each at its default, each list empty: its
    /// HasSerializationItems is false), so that the JSON leaves the object out: its JSON holds nothing but its kind.</summary>
    static bool NothingToWrite(object o)
    {
        var json = SFSearch.Records.RecordSerializer.ToUtf8(o);
        var reader = new System.Text.Json.Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartObject) return false;
        while (reader.Read() && reader.TokenType == System.Text.Json.JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals("MutagenObjectType"u8)) return false;
            reader.Read();
        }
        return true;
    }

    /// <summary>The object that many steps in is absent: NULL when a guard needs it or one past it (its kind is
    /// unknown), or past a nullable object; else the default.</summary>
    object? Missing(int depth)
    {
        if (depth == 0 || _absentIsNull) return null;
        foreach (var (d, _) in _guards) if (d >= depth) return null;
        return _default;
    }

    static readonly ConcurrentDictionary<(Type, string, bool), Func<object, object?>?> Steps = new();

    /// <summary>A getter of one property by name on one class (through the interface that declares it). With
    /// <paramref name="omit"/>, a value equal to its type's default gives null, as the serializer leaves it out.</summary>
    internal static Func<object, object?>? Getter(Type t, string name, bool omit = false)
    {
        var p = Find(t, name);
        if (p == null) return null;
        var param = Expression.Parameter(typeof(object));
        Expression value = Expression.Property(Expression.Convert(param, p.DeclaringType!), p);
        return Expression.Lambda<Func<object, object?>>(omit ? OmitDefault(value, name, t) : Expression.Convert(value, typeof(object)), param).Compile();
    }

    /// <summary>
    /// The field's value as the serializer writes it or leaves it out: the generated writer leaves a field out when its
    /// value equals the default it compares with, and the serializer says which default that is (its registry's
    /// FieldDefaults, 1.38.6-fixes.5, C59 S124: default(T), or the class's own {Name}Default such as
    /// BookTeachesNothing.RawContent's uint.MaxValue or ObjectTemplate&lt;T&gt;.AddonIndex's -1). A field it doesn't list
    /// is always written. Left out, the view gives its default (coalesce) or NULL, so a value equal to the default reads as
    /// null here.
    /// </summary>
    static Expression OmitDefault(Expression value, string name, Type holder)
    {
        var t = value.Type;
        if (!t.IsValueType || Nullable.GetUnderlyingType(t) != null) return Expression.Convert(value, typeof(object));
        // The registry lists every class the serializer writes, with its FieldDefaults (1.38.6-fixes.7, C76, GAPS S170:
        // the object mod properties of an object template too)
        if (FieldDefaultsOf(holder) is not { } defaults || !defaults.TryGetValue(name, out var listed) || listed == null || listed.GetType() != t)
            return Expression.Convert(value, typeof(object));
        Expression dflt = Expression.Constant(listed, t);
        // Floats and float points by their bits (FloatSerialization.BitEquals: -0 isn't the default 0), the rest by EqualityComparer
        var bits = typeof(Mutagen.Bethesda.Serialization.FloatSerialization).GetMethods()
            .FirstOrDefault(m => m.Name == "BitEquals" && m.GetParameters() is var ps && ps.Length == 2 && ps[0].ParameterType == t && ps[1].ParameterType == t);
        var comparer = typeof(EqualityComparer<>).MakeGenericType(t);
        Expression equal = bits != null
            ? Expression.Call(bits, value, dflt)
            : Expression.Call(Expression.Property(null, comparer.GetProperty("Default")!), comparer.GetMethod("Equals", new[] { t, t })!, value, dflt);
        return Expression.Condition(equal, Expression.Constant(null, typeof(object)), Expression.Convert(value, typeof(object)));
    }

    /// <summary>
    /// The defaults the serializer leaves the fields of an object of type <paramref name="t"/> out at (a class, a binary
    /// overlay or a getter interface): the registry's FieldDefaults of its class, found through the type itself or the
    /// most derived of its interfaces the registry knows (a closed generic one too: IObjectTemplateGetter&lt;Weapon.Property&gt;,
    /// GAPS S151). Null when the registry has no class for it.
    /// </summary>
    static IReadOnlyDictionary<string, object?>? FieldDefaultsOf(Type t) => FieldDefaults.GetOrAdd(t, x =>
    {
        var registry = SFSearch.Records.RecordSerializer.Registry;
        Type? best = null;
        foreach (var i in x.GetInterfaces().Prepend(x))
            if (registry.TryGetClassType(i, out var cls) && (best == null || cls.IsSubclassOf(best))) best = cls;
        return best != null && registry.TryGetFieldDefaults(best, out var d) ? d : null;
    });

    static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, object?>?> FieldDefaults = new();

    static readonly ConcurrentDictionary<Type, string> TypeNames = new();

    /// <summary>The MutagenObjectType the serializer writes for an object: its class, with the declaring class and the
    /// generic argument (ObjectModFloatProperty&lt;Weapon+Property&gt;).</summary>
    static string ObjectTypeOf(object o) => ObjectTypeName(o.GetType());

    /// <summary>The MutagenObjectType the serializer writes for an object of this class.</summary>
    internal static string ObjectTypeName(Type type) => TypeNames.GetOrAdd(type, t => TypeHelper.GetTypeToSerialize(t).GetNameWithDeclaringType());

    /// <summary>The kind the view's guard compares: the MutagenObjectType up to its generic argument.</summary>
    static string KindOf(object o)
    {
        string n = ObjectTypeOf(o);
        int lt = n.IndexOf('<');
        return lt < 0 ? n : n[..lt];
    }

    /// <summary>What a getter gives when an object on the way is null.</summary>
    static readonly object Absent = new();

    /// <summary>A getter for one class of record: each property through the interface or class that declares it, an
    /// object on the way that is null giving <see cref="Absent"/>. Null when the path isn't there.</summary>
    Func<object, object?>? Compile(Type runtime)
    {
        var param = Expression.Parameter(typeof(object), "r");
        var ret = Expression.Label(typeof(object));
        var vars = new List<ParameterExpression>();
        var body = new List<Expression>();
        Expression cur = Expression.Convert(param, runtime);
        Type holder = runtime;
        for (int i = 0; i < _path.Length; i++)
        {
            if (i > 0) holder = cur.Type;
            var prop = Find(cur.Type, _path[i]);
            if (prop == null) return null;
            if (prop.DeclaringType != null && prop.DeclaringType != cur.Type) cur = Expression.Convert(cur, prop.DeclaringType);
            Expression access = Expression.Property(cur, prop);
            if (i < _path.Length - 1 && !prop.PropertyType.IsValueType)
            {
                var v = Expression.Variable(prop.PropertyType);
                vars.Add(v);
                body.Add(Expression.Assign(v, access));
                body.Add(Expression.IfThen(Expression.Equal(v, Expression.Constant(null, prop.PropertyType)), Expression.Return(ret, Expression.Constant(Absent))));
                cur = v;
            }
            else cur = access;
        }
        body.Add(Expression.Label(ret, OmitDefault(cur, _path[^1], holder)));
        return Expression.Lambda<Func<object, object?>>(Expression.Block(vars, body), param).Compile();
    }

    internal static PropertyInfo? Find(Type t, string name)
    {
        foreach (var type in t.GetInterfaces().Prepend(t))
        {
            PropertyInfo? p;
            try { p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance); }
            catch (AmbiguousMatchException) { p = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name); }
            if (p != null && p.GetIndexParameters().Length == 0 && p.GetMethod != null) return p;
        }
        return null;
    }

    /// <summary>The value as the serializer's JSON gives it to SQLite.</summary>
    object? Convert(object? v)
    {
        // A null value: the view's default where it has one (coalesce), else NULL
        if (v == null) return _kind == "FormLink" ? null : _default;
        // A list where another kind of the entry holds a plain value: its JSON text (nothing for an empty one)
        if (_varying && v is not string && v is System.Collections.IEnumerable list) return VaryingColumn.ArrayText(list);
        switch (_kind)
        {
            case "FormLink":
                return v switch
                {
                    // (FormKey.None, a stored FFFFFFFF in a field of maxIsNone, is null to Mutagen too, but its serializer
                    // writes it, as "None": the JSON's value, so this column's)
                    IFormLinkGetter l => l.IsNull ? (l.FormKey == FormKey.None ? FormKey.NoneStr : null) : Key(l.FormKey),
                    FormKey k => k.IsNull ? (k == FormKey.None ? FormKey.NoneStr : null) : Key(k),
                    _ => throw new InvalidOperationException($"a link column gave a {v.GetType().Name}"),
                };
            case "Bool": return v is bool b ? (b ? 1L : 0L) : Plain(v);
            case "Float" or "Double" or "Percent" or "Half": return v is float or double or Half ? Real(v) : Plain(v);
            case "Enum" or "Flags": return v is Enum e ? EnumText(e) : Plain(v);
            // A flags value as its number (GAPS S168)
            case FlagsRaw.Kind: return v is Enum f ? FlagsRaw.Number(f) : throw new InvalidOperationException($"a flags number column gave a {v.GetType().Name}");
            case "String" when _language != null:
                return v is ITranslatedStringGetter t ? Functions.Display(Text(t, _language)) ?? _default : throw new InvalidOperationException($"a translated text column gave a {v.GetType().Name}");
            case "Int8" or "Int16" or "Int32" or "Int64" or "UInt8" or "UInt16" or "UInt32":
                return v switch
                {
                    Enum en => System.Convert.ToInt64(en, CultureInfo.InvariantCulture),
                    bool x => x ? 1L : 0L,
                    float or double or Half => Real(v),
                    IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException($"a number column gave a {v.GetType().Name}"),
                };
            default: return Plain(v);
        }
    }

    /// <summary>A value by its own type, as the serializer's JSON writer writes that type and SQLite reads it back:
    /// a column of several kinds of object takes whatever the object holds.</summary>
    internal static object? Plain(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b ? 1L : 0L,
        float or double or Half => Real(v),
        // (no link: the serializer leaves it out, and the JSON gives NULL: a condition's ComparisonValue with no global)
        IFormLinkGetter l => l.IsNull ? null : l.FormKey.ToString(),
        FormKey k => k.ToString(),
        Enum e => EnumText(e),
        P2Float or P3Float or P2Int16 or P3Int16 or P2Int or P3Int or P2UInt8 or P3UInt8 or P3UInt16 => Point(v),
        System.Drawing.Color c => c.ToHexString(),
        Guid g => g.ToString(),
        IAssetLinkGetter a => a.GivenPath,
        RecordType rt => rt.TypeInt == RecordType.Null.TypeInt ? "null" : rt.Type,
        ReadOnlyMemorySlice<byte> m => "0x" + System.Convert.ToHexString(m),
        MemorySlice<byte> m => "0x" + System.Convert.ToHexString(m),
        byte[] a => "0x" + System.Convert.ToHexString(a),
        ulong u => u <= long.MaxValue ? (object)(long)u : (double)u,
        // (a percent as the serializer writes it: its value, a double from 0 to 1)
        Noggog.Percent pc => Real(pc.Value),
        // A time of day as the serializer writes it (05:00:00.0000000)
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"a column gave a {v.GetType().Name}, which isn't read straight from the record"),
    };

    static readonly ConcurrentDictionary<Type, Func<Enum, object>> EnumWriters = new();

    /// <summary>An enum as the serializer's writer gives it: its name (ToStringFast), or for a flags enum the JSON list
    /// of the flags it holds, undefined bits included: the serializer's own text of a flags value (FlagsEnumText.ToTexts,
    /// 1.38.6-fixes.6, C72: one item per set bit, by its own bounded loop; GAPS S162).</summary>
    internal static object EnumText(Enum e) => EnumWriters.GetOrAdd(e.GetType(), MakeEnumWriter)(e);

    /// <summary>The names of a flags enum's flags, as its JSON list holds them (a flags list view's entries).</summary>
    internal static IEnumerable<string> FlagNames(Enum e) => (IEnumerable<string>)FlagLists.GetOrAdd(e.GetType(), MakeFlagLister)(e);

    static readonly ConcurrentDictionary<Type, Func<Enum, object>> FlagLists = new();

    static Func<Enum, object> MakeEnumWriter(Type t) =>
        (Func<Enum, object>)typeof(DirectColumn).GetMethod(nameof(EnumWriter), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(t).Invoke(null, null)!;

    static Func<Enum, object> MakeFlagLister(Type t) =>
        (Func<Enum, object>)typeof(DirectColumn).GetMethod(nameof(FlagLister), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(t).Invoke(null, null)!;

    static Func<Enum, object> EnumWriter<TEnum>() where TEnum : struct, Enum, IConvertible
    {
        if (!Mutagen.Bethesda.Serialization.FlagsEnumText.IsFlags<TEnum>()) return e => ((TEnum)e).ToStringFast();
        return e =>
        {
            var sb = new System.Text.StringBuilder("[");
            foreach (var name in Mutagen.Bethesda.Serialization.FlagsEnumText.ToTexts((TEnum)e))
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append(System.Text.Json.JsonSerializer.Serialize(name));
            }
            return sb.Append(']').ToString();
        };
    }

    static Func<Enum, object> FlagLister<TEnum>() where TEnum : struct, Enum, IConvertible =>
        e => Mutagen.Bethesda.Serialization.FlagsEnumText.ToTexts((TEnum)e).ToList();

    /// <summary>The session of the plugin the reading thread's record is of, when that plugin isn't localized: its
    /// own strings tables give a tagged text in another language (GAPS S190). Set by the record reader for each record.</summary>
    [ThreadStatic] internal static SFSearch.Records.PluginSession? TextSession;

    /// <summary>A translated text in one language, as sfs_text reads the serializer's JSON of it (Languages.Text): a
    /// text of one language (a plugin with no strings files) whatever the language asked, else the language's own
    /// text, NULL when it has none.</summary>
    internal static string? Text(ITranslatedStringGetter t, string language)
    {
        if (t.NumLanguages <= 1)
        {
            // A plugin that isn't localized but ships strings tables: the language's table's text for a tagged text,
            // as the JSON's "Tables" give it (GAPS S190, TagTexts.cs)
            string? own = t.String;
            if (TextSession is { } session && own != null && own.StartsWith("<ID=", StringComparison.Ordinal) && !language.Equals("English", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<Mutagen.Bethesda.Strings.Language>(language, true, out var asked) && session.TagTexts?.Translate(own, asked) is { } fromTable) return fromTable;
            return own;
        }
        foreach (var entry in t)
            if (entry.Key.ToStringFast().Equals(language, StringComparison.OrdinalIgnoreCase)) return entry.Value;
        return null;
    }

    /// <summary>A FormKey as the serializer writes it: six hex digits, a colon, the plugin's file name.</summary>
    static string Key(FormKey k) => k.ToString();

    /// <summary>A float as the serializer writes it (its JSON token, JsonScalarText.Format, 1.38.6-fixes.5, C59 W14: the
    /// fewest digits that give it back, or a text for NaN, the infinities and a value no number holds), read back as SQLite
    /// reads it: a number as a double, a text as the text.</summary>
    internal static object Real(object v)
    {
        string token = v switch
        {
            float f => Mutagen.Bethesda.Serialization.Newtonsoft.JsonScalarText.Format(f),
            double x => Mutagen.Bethesda.Serialization.Newtonsoft.JsonScalarText.Format(x),
            Half h => Mutagen.Bethesda.Serialization.Newtonsoft.JsonScalarText.Format(h),
            _ => throw new InvalidOperationException($"a number column gave a {v.GetType().Name}"),
        };
        if (token.Length >= 2 && token[0] == '"') return token[1..^1];
        return Mutagen.Bethesda.Serialization.Newtonsoft.JsonScalarText.TryParseDouble(token, out double d) ? d
            : throw new InvalidOperationException($"the serializer's text of a number, {token}, reads as no number");
    }

    /// <summary>A point as the serializer writes it: its numbers joined by ", ".</summary>
    internal static string Point(object v) => v switch
    {
        P3Float p => Mutagen.Bethesda.Serialization.FloatSerialization.ToString(p, Mutagen.Bethesda.Serialization.TargetCulture.English),
        P2Float p => Mutagen.Bethesda.Serialization.FloatSerialization.ToString(p, Mutagen.Bethesda.Serialization.TargetCulture.English),
        P3Int16 p => FormattableString.Invariant($"{p.X}, {p.Y}, {p.Z}"),
        P2Int16 p => FormattableString.Invariant($"{p.X}, {p.Y}"),
        P2Int p => FormattableString.Invariant($"{p.X}, {p.Y}"),
        P3Int p => FormattableString.Invariant($"{p.X}, {p.Y}, {p.Z}"),
        P2UInt8 p => FormattableString.Invariant($"{p.X}, {p.Y}"),
        P3UInt8 p => FormattableString.Invariant($"{p.X}, {p.Y}, {p.Z}"),
        P3UInt16 p => FormattableString.Invariant($"{p.X}, {p.Y}, {p.Z}"),
        _ => throw new InvalidOperationException($"a point column gave a {v.GetType().Name}"),
    };

}

/// <summary>
/// A list view's named columns read straight from a record's Mutagen object (GAPS S45): each list of the view's path in
/// turn (through the properties of each entry, a kind of entry that hasn't the list giving none, as json_each gives
/// none), and each column from the innermost entry. One row per innermost entry: its places in the lists, then the
/// columns. Only when every named column can be read so and every list is a plain list, or a record's flags (a flags
/// list view: its entries are the flags' names, as the flags' JSON list holds them).
/// </summary>
sealed class DirectList
{
    readonly string[][] _levels;
    readonly DirectColumn[] _columns;
    /// <summary>The innermost list is a flags value: its entries are the flags' names.</summary>
    readonly bool _flags;
    /// <summary>The innermost list is a grid (Array2d): its entries are its rows, each the JSON list of its cells.</summary>
    readonly bool _grid;
    readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), Func<object, object?>?> _getters = new();

    DirectList(string[][] levels, DirectColumn[] columns, bool flags, bool grid) { _levels = levels; _columns = columns; _flags = flags; _grid = grid; }

    public int Levels => _levels.Length;

    /// <summary>The direct reader of a list view's named columns; null when one of them, or one of its lists, is read
    /// from the JSON. A translated text needs the query's language.</summary>
    public static DirectList? For(ViewSpec v, IReadOnlyList<string> columns, string? language = null)
    {
        if (!v.IsList || v.Lists.Any(l => l.Path.Length == 0)) return null;
        bool flags = v.Lists.Count == 1 && v.Lists[0].Kind == "Flags";
        bool grid = v.Lists[^1].Kind == "Array2D";
        if (!flags && v.Lists.Take(v.Lists.Count - (grid ? 1 : 0)).Any(l => l.Kind != "List" && l.Kind != "Array")) return null;
        var cols = new DirectColumn[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            if (!v.ByName.TryGetValue(columns[i], out var c)) return null;
            if (flags || grid && c.Kind == "Array2DRow") { cols[i] = DirectColumn.Entry("String"); continue; }
            if (DirectColumn.ForEntry(c, language) is not { } d) return null;
            cols[i] = d;
        }
        return new DirectList(v.Lists.Select(l => l.Path.Split('.')).ToArray(), cols, flags, grid);
    }

    /// <summary>The record's rows: the list places (Idx, Idx2, …), then the columns.</summary>
    public List<object?[]> Read(object record)
    {
        var rows = new List<object?[]>();
        var idx = new long[_levels.Length];
        Walk(record, 0, idx, rows);
        return rows;
    }

    void Walk(object obj, int level, long[] idx, List<object?[]> rows)
    {
        object? list = obj;
        foreach (string name in _levels[level])
        {
            if (list == null) return;
            var get = _getters.GetOrAdd((list.GetType(), name), k => Getter(k.Item1, k.Item2));
            if (get == null) return;
            list = get(list);
        }
        if (_flags && level == _levels.Length - 1) list = list is Enum e ? DirectColumn.FlagNames(e) : null;
        if (_grid && level == _levels.Length - 1 && list != null) list = GridRows(list);
        if (list is not System.Collections.IEnumerable items || list is string) return;
        long i = 0;
        foreach (var item in items)
        {
            idx[level] = i++;
            if (item == null) continue;
            if (level + 1 < _levels.Length) { Walk(item, level + 1, idx, rows); continue; }
            var row = new object?[_levels.Length + _columns.Length];
            for (int k = 0; k < _levels.Length; k++) row[k] = idx[k];
            for (int k = 0; k < _columns.Length; k++) row[_levels.Length + k] = _columns[k].Read(item);
            rows.Add(row);
        }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (Func<object, object?> Width, Func<object, object?> Height, System.Reflection.MethodInfo Item)?> Grids = new();

    /// <summary>
    /// A grid's rows as its JSON has them: the serializer writes an Array2d row by row (y), each row the list of its
    /// cells (x) through the kernel's writer for the cell's kind (a link as its FormKey, "" when null; a number as it is),
    /// and json_each gives each row as that list's text. Written here with the same writer (Newtonsoft, no indenting).
    /// </summary>
    static List<string>? GridRows(object grid)
    {
        var g = Grids.GetOrAdd(grid.GetType(), t =>
        {
            var w = Getter(t, "Width"); var h = Getter(t, "Height");
            var item = t.GetInterfaces().Prepend(t).SelectMany(i => i.GetMethods()).FirstOrDefault(m => m.Name == "get_Item" && m.GetParameters() is var ps && ps.Length == 2 && ps.All(x => x.ParameterType == typeof(int)));
            return w == null || h == null || item == null ? null : (w, h, item);
        }) ?? throw new InvalidOperationException($"a grid column gave a {grid.GetType().Name}, which has no Width, Height and [x, y]");
        int width = System.Convert.ToInt32(g.Width(grid)), height = System.Convert.ToInt32(g.Height(grid));
        var rows = new List<string>(height);
        var args = new object[2];
        for (int y = 0; y < height; y++)
        {
            using var sw = new StringWriter(CultureInfo.InvariantCulture);
            using (var jw = new Newtonsoft.Json.JsonTextWriter(sw) { Formatting = Newtonsoft.Json.Formatting.None })
            {
                jw.WriteStartArray();
                for (int x = 0; x < width; x++)
                {
                    args[0] = x; args[1] = y;
                    switch (g.Item.Invoke(grid, args))
                    {
                        // An object (a component's block adjustments): written by its own generated serializer, as the
                        // record's writer does, without the indenting (SQLite gives JSON text without it)
                        case Loqui.ILoquiObject o: jw.WriteRawValue(Minified(SFSearch.Records.RecordSerializer.ToUtf8(o))); break;
                        case IFormLinkGetter l: jw.WriteValue(l.FormKeyNullable is { } k ? k.ToString() : ""); break;
                        case sbyte n: jw.WriteValue(n); break;
                        case byte n: jw.WriteValue(n); break;
                        case short n: jw.WriteValue(n); break;
                        case ushort n: jw.WriteValue(n); break;
                        case int n: jw.WriteValue(n); break;
                        case uint n: jw.WriteValue(n); break;
                        case long n: jw.WriteValue(n); break;
                        case bool n: jw.WriteValue(n); break;
                        case var other: throw new InvalidOperationException($"a grid's cell is a {other?.GetType().Name ?? "null"}, which isn't read straight from the record");
                    }
                }
                jw.WriteEndArray();
            }
            rows.Add(sw.ToString());
        }
        return rows;
    }

    /// <summary>JSON text without the whitespace between its tokens; a string's text, escapes and all, as written.</summary>
    internal static string Minified(ReadOnlySpan<byte> utf8)
    {
        var sb = new System.Text.StringBuilder(utf8.Length);
        string text = System.Text.Encoding.UTF8.GetString(utf8);
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inString)
            {
                sb.Append(ch);
                if (ch == '\\' && i + 1 < text.Length) sb.Append(text[++i]);
                else if (ch == '"') inString = false;
            }
            else if (ch == '"') { inString = true; sb.Append(ch); }
            else if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    static Func<object, object?>? Getter(Type t, string name)
    {
        foreach (var type in t.GetInterfaces().Prepend(t))
        {
            System.Reflection.PropertyInfo? p;
            try { p = type.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance); }
            catch (System.Reflection.AmbiguousMatchException) { p = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name); }
            if (p == null || p.GetIndexParameters().Length != 0 || p.GetMethod == null) continue;
            var param = Expression.Parameter(typeof(object));
            var body = Expression.Convert(Expression.Property(Expression.Convert(param, p.DeclaringType!), p), typeof(object));
            return Expression.Lambda<Func<object, object?>>(body, param).Compile();
        }
        return null;
    }
}
