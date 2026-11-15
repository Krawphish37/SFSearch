// The named columns of a type too big to read whole, read straight from Mutagen's object: no JSON (GAPS S5, fix
// g-sql, 2026-11-13). Each record of Starfield.esm's 3.29 million placed objects used to be written as JSON, made
// into SQLite's JSONB and read back through the column's expression, to give one field.
//
// A column is read this way only when what it gives can't differ from what its expression gives over the JSON:
//   - a plain field of the record or of an object in it (no list, no "kind of object" guard),
//   - of a kind whose JSON is one plain value: a link, a number, a yes/no, a name of an enum that isn't flags, a text
//     that isn't translated, a point (P2/P3),
//   - with the same default rule the view uses: the serializer leaves out a value equal to its default, and the view
//     gives the default back for it (the default is the value itself, so the two agree); a nullable object on the way
//     that is absent gives no value (NULL), as the view's guard does.
// Every other column (translated text, flags, a field under a "kind of object", a whole object as JSON) is read from
// the JSON as before. The test direct-columns holds both ways against each other for every column and every record.
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
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
    readonly ConcurrentDictionary<Type, Func<object, object?>?> _getters = new();

    DirectColumn(string[] path, string kind, bool absentIsNull, object? dflt) { _path = path; _kind = kind; _absentIsNull = absentIsNull; _default = dflt; }

    static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "FormLink", "Bool", "Int8", "Int16", "Int32", "Int64", "UInt8", "UInt16", "UInt32", "Float", "Double", "Percent", "Half", "Enum", "String",
        "P2Float", "P3Float", "P2Int16", "P3Int16",
    };

    /// <summary>The direct reader of a column, or null when the column is read from the JSON.</summary>
    public static DirectColumn? For(ColumnSpec c)
    {
        if (!Kinds.Contains(c.Kind) || c.RelPath is not { Length: > 0 } rel) return null;
        // A field under a kind of object, or one name for fields of several kinds: the JSON says which kind it is
        if (!c.FromBase || c.VariantGuards.Count > 0) return null;
        if (c.SchemaPath.Contains('[') || c.SchemaPath.Contains('{')) return null;
        // Translated text goes through sfs_text and the language
        if (c.Kind == "String" && c.Expr.Contains("sfs_text", StringComparison.Ordinal)) return null;
        // A field that is always there and has no default written down reads as NULL when the JSON leaves it out,
        // which this can't know: from the JSON
        if (!c.Nullable && c.DefaultSql == null && c.Kind != "FormLink") return null;
        object? dflt = null;
        if (!c.Nullable && c.DefaultSql != null && !TryLiteral(c.DefaultSql, out dflt)) return null;
        return new DirectColumn(rel.Split('.'), c.Kind, c.NullGuard != null || c.Nullable, dflt);
    }

    /// <summary>A default as SQL text (<c>0</c>, <c>1.5</c>, <c>'Medium'</c>) as the value SQLite gives for it.</summary>
    static bool TryLiteral(string sql, out object? value)
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
        var get = _getters.GetOrAdd(record.GetType(), Compile)
            ?? throw new InvalidOperationException($"{record.GetType().Name} has no field {string.Join('.', _path)}");
        object? raw = get(record);
        if (ReferenceEquals(raw, Absent)) return _absentIsNull ? null : _default;
        return Convert(raw);
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
        for (int i = 0; i < _path.Length; i++)
        {
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
        body.Add(Expression.Label(ret, Expression.Convert(cur, typeof(object))));
        return Expression.Lambda<Func<object, object?>>(Expression.Block(vars, body), param).Compile();
    }

    static PropertyInfo? Find(Type t, string name)
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
        if (v == null) return _absentIsNull || _kind == "FormLink" ? null : _default;
        switch (_kind)
        {
            case "FormLink":
                return v switch
                {
                    IFormLinkGetter l => l.IsNull ? null : Key(l.FormKey),
                    FormKey k => k.IsNull ? null : Key(k),
                    _ => throw new InvalidOperationException($"a link column gave a {v.GetType().Name}"),
                };
            case "Bool": return v is bool b ? (b ? 1L : 0L) : throw new InvalidOperationException($"a yes/no column gave a {v.GetType().Name}");
            case "Float" or "Double" or "Percent" or "Half": return Real(v);
            case "Enum": return v is Enum named ? named.ToString() : v.ToString();
            case "String": return v as string ?? v.ToString();
            case "P2Float" or "P3Float" or "P2Int16" or "P3Int16": return Point(v);
            default:
                return v switch
                {
                    Enum e => System.Convert.ToInt64(e, CultureInfo.InvariantCulture),
                    IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException($"a number column gave a {v.GetType().Name}"),
                };
        }
    }

    /// <summary>A FormKey as the serializer writes it: six hex digits, a colon, the plugin's file name.</summary>
    static string Key(FormKey k) => k.ToString();

    /// <summary>A float as the serializer writes it (the fewest digits that give it back, and NaN and the infinities as
    /// texts), read back as SQLite reads a JSON number.</summary>
    static object Real(object v)
    {
        double d;
        string text;
        switch (v)
        {
            case float f:
                if (float.IsNaN(f)) return "NaN";
                if (float.IsPositiveInfinity(f)) return "Infinity";
                if (float.IsNegativeInfinity(f)) return "-Infinity";
                text = f.ToString("R", CultureInfo.InvariantCulture);
                break;
            case double x:
                if (double.IsNaN(x)) return "NaN";
                if (double.IsPositiveInfinity(x)) return "Infinity";
                if (double.IsNegativeInfinity(x)) return "-Infinity";
                text = x.ToString("R", CultureInfo.InvariantCulture);
                break;
            case Half h:
                // As the serializer writes it (C222): the double that holds it, "NaN:" and its bits for a NaN not .NET's own
                if (Half.IsNaN(h)) return BitConverter.HalfToUInt16Bits(h) == BitConverter.HalfToUInt16Bits(Half.NaN) ? "NaN" : "NaN:" + BitConverter.HalfToUInt16Bits(h).ToString("X4", CultureInfo.InvariantCulture);
                if (Half.IsPositiveInfinity(h)) return "Infinity";
                if (Half.IsNegativeInfinity(h)) return "-Infinity";
                text = ((double)h).ToString("R", CultureInfo.InvariantCulture);
                break;
            default: throw new InvalidOperationException($"a number column gave a {v.GetType().Name}");
        }
        d = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return d;
    }

    /// <summary>A point as the serializer writes it: its numbers joined by ", ".</summary>
    static string Point(object v) => v switch
    {
        P3Float p => $"{F(p.X)}, {F(p.Y)}, {F(p.Z)}",
        P2Float p => $"{F(p.X)}, {F(p.Y)}",
        P3Int16 p => FormattableString.Invariant($"{p.X}, {p.Y}, {p.Z}"),
        P2Int16 p => FormattableString.Invariant($"{p.X}, {p.Y}"),
        _ => throw new InvalidOperationException($"a point column gave a {v.GetType().Name}"),
    };

    static string F(float f) => f.ToString(CultureInfo.InvariantCulture);
}
