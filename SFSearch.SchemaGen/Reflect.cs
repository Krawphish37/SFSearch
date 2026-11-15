// Checks the schema against the compiled Mutagen assembly: every field must be a property of the compiled record or
// object. Also takes from the assembly what the XML doesn't say: enum members, and whether a property may be null.
using System.Reflection;

namespace SFSearch.SchemaGen;

public sealed class ReflectStats
{
    public int ObjectsChecked, ObjectsWithoutType, FieldsChecked, RootFieldsChecked, RootFieldsMissing, NullableChanged, LogicOnly;
    public List<string> Missing = new(), NoType = new();
    public int EnumsNamed, EnumsFound, EnumMembers;
    public List<string> EnumsNotFound = new();
    /// <summary>Properties of the compiled getters that Mutagen adds beyond the XML (MajorFlags, IsCompressed, IsDeleted,
    /// Versioning, &lt;Sig&gt;DataTypeState; GAPS Part 2 row 8) and the schema has no field for: should be none.</summary>
    public List<string> DerivedMissing = new();
    /// <summary>How many such properties the compiled getters have, each matched by a field derived from the XML.</summary>
    public int DerivedFound;
}

public static class Reflect
{
    const string Namespace = "Mutagen.Bethesda.Starfield.";
    static readonly Assembly Starfield = typeof(Mutagen.Bethesda.Starfield.Weapon).Assembly;
    static readonly Assembly Core = typeof(Mutagen.Bethesda.Plugins.FormKey).Assembly;

    /// <summary>The package version and source commit, as written into the compiled assembly ("0.55.0-fixes.11+1ded…").</summary>
    public static string AssemblyVersion()
        => Starfield.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Starfield.GetName().Version?.ToString() ?? "";

    static Type? GetterOf(string obj)
        => Starfield.GetType($"{Namespace}I{obj}Getter") ?? Starfield.GetType($"{Namespace}I{obj}Getter`1")
           ?? (obj == "MajorRecord" ? Core.GetType("Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter") : null);

    static PropertyInfo? Property(Type t, string name)
        => t.GetProperty(name) ?? t.GetInterfaces().Select(i => i.GetProperty(name)).FirstOrDefault(p => p != null);

    /// <summary>Whether the compiled object has a property of that name (also one the XML doesn't declare: hand-written, or added by the generator).</summary>
    public static bool HasProperty(string obj, string name)
        => GetterOf(obj) is { } t && Property(t, name) != null;

    static Type? EnumIn(Type t, int guard = 0)
    {
        if (t.IsEnum) return t;
        if (guard > 4) return null;
        if (t.IsArray && t.GetElementType() is { } el) return EnumIn(el, guard + 1);
        if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) if (EnumIn(a, guard + 1) is { } e) return e;
        return null;
    }

    static string EnumName(Type t) => (t.FullName ?? t.Name).Replace('+', '.') is var n && n.StartsWith(Namespace, StringComparison.Ordinal) ? n[Namespace.Length..] : n;

    static string Friendly(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        string n = t.Name[..t.Name.IndexOf('`')];
        return $"{n}<{string.Join(", ", t.GetGenericArguments().Select(Friendly))}>";
    }

    /// <summary>A static method of an object's binary reader (<c>ConditionBinaryCreateTranslation</c>), when the compiled
    /// package has it: the generator declares one for each field read by hand-written code, and that code fills it.</summary>
    static string? ReaderMethod(string obj, string method)
    {
        var t = Starfield.GetType($"{Namespace}{obj}BinaryCreateTranslation") ?? Starfield.GetType($"{Namespace}{obj}BinaryCreateTranslation`1");
        if (t == null) return null;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return t.GetMethods(all).Any(m => m.Name == method) ? $"{t.Name.Split('`')[0]}.{method}" : null;
    }

    /// <summary>
    /// Where the hand-written code that reads an object lives (GAPS S36): the reader methods of its own fields with
    /// binary="Custom" (<c>FillBinary&lt;Field&gt;Custom</c>) and of a customBinaryEnd (<c>CustomBinaryEndImport</c>), as the
    /// compiled package has them, and the field of another object whose hand-written code enters it. Null when the
    /// generated reader reads it all. Returns how many objects are marked.
    /// </summary>
    public static int HandRead(SchemaModel model)
    {
        int marked = 0;
        foreach (var o in model.Objects.Values)
        {
            var parts = new List<string>();
            var own = o.Fields.Where(f => f.Path == f.Name).ToList();
            foreach (var f in own.Where(f => f.Binary == "Custom"))
                if (ReaderMethod(o.Name, $"FillBinary{f.Name}Custom") is { } m && !parts.Contains(m)) parts.Add(m);
            if (o.CustomEnd != null && ReaderMethod(o.Name, "CustomBinaryEndImport") is { } end) parts.Add(end);
            foreach (var via in o.EnteredVia)
            {
                int dot = via.IndexOf('.');
                string? m = ReaderMethod(via[..dot], $"FillBinary{via[(dot + 1)..].Split('.')[0]}Custom");
                parts.Add(m != null ? $"via {via} ({m})" : $"via {via}");
            }
            // No method of its own, but a field read by hand sits among others in one subrecord, so their places
            // aren't the XML's order (a field in a subrecord of its own, or in the record header, moves nothing)
            if (parts.Count == 0 && own.Any(f => f.Binary == "NoGeneration" && !f.IsSub && (f.Block > 0 || o.Struct))) parts.Add("hand-written Mutagen code");
            o.HandRead = parts.Count > 0 ? string.Join("; ", parts) : null;
            if (o.HandRead != null) marked++;
        }
        return marked;
    }

    public static ReflectStats Apply(SchemaModel model)
    {
        var st = new ReflectStats();
        var nullCtx = new NullabilityInfoContext();
        var enumTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        var allEnums = new[] { Starfield, Core }.SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; } })
            .Where(t => t!.IsEnum).Select(t => t!).ToList();
        var roots = new HashSet<string>(model.RecordTypes.SelectMany(r => model.Ancestry(r.Name)).Select(o => o.Name), StringComparer.Ordinal);

        Type? ByName(string xmlName)
        {
            string want = xmlName.Replace('.', '+');
            return allEnums.FirstOrDefault(t => t.FullName == Namespace + want)
                ?? allEnums.FirstOrDefault(t => t.FullName!.EndsWith("." + want, StringComparison.Ordinal) || t.FullName!.EndsWith("+" + want, StringComparison.Ordinal))
                ?? allEnums.FirstOrDefault(t => t.Name == xmlName.Split('.').Last());
        }

        // The other way round: every property of a compiled getter of the kinds Mutagen adds beyond the XML has a field
        // (declared on the object's own getter, or on Core's IMajorRecordGetter for the hand-written header views)
        foreach (var o in model.Objects.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            if (GetterOf(o.Name) is not { } gt) continue;
            var own = gt.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.Name)
                .Where(n => n is "MajorFlags" or "Versioning" or "IsCompressed" or "IsDeleted" || n.EndsWith("DataTypeState", StringComparison.Ordinal)
                    || o.Name == "RegionData" && n == "DataType");
            foreach (var n in own)
            {
                if (model.Ancestry(o.Name).Any(a => a.Fields.Any(f => f.Name == n && f.Path == n && f.Source != null))) st.DerivedFound++;
                else st.DerivedMissing.Add($"{o.Name}.{n}");
            }
        }

        foreach (var o in model.Objects.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            var t = GetterOf(o.Name);
            if (t == null) { st.ObjectsWithoutType++; st.NoType.Add(o.Name); }
            else st.ObjectsChecked++;
            PropertyInfo? last = null;
            foreach (var f in o.Fields)
            {
                if (f.Kind is "Break" or "Marker") continue;
                PropertyInfo? p = null;
                if (!f.IsSub)
                {
                    p = last = t == null ? null : Property(t, f.Name);
                    if (t != null)
                    {
                        if (p == null && f.Kind == "CustomLogic") { st.LogicOnly++; f.Note = (f.Note == null ? "" : f.Note + "; ") + "a parsing step, not a value: no property"; }
                        else
                        {
                            st.FieldsChecked++;
                            if (roots.Contains(o.Name)) st.RootFieldsChecked++;
                            if (p == null) { st.Missing.Add($"{o.Name}.{f.Name}"); if (roots.Contains(o.Name)) st.RootFieldsMissing++; }
                        }
                    }
                    f.Property = p != null;
                    // A property Mutagen adds beyond its XML that its record class can't set: the serializer never writes it
                    if (p != null && f.Source != null && Starfield.GetType($"{Namespace}{o.Name}")?.GetProperty(f.Name) is { CanWrite: false }) f.ReadOnly = true;
                    if (p != null)
                    {
                        var pt = p.PropertyType;
                        bool nullable = Nullable.GetUnderlyingType(pt) != null
                            || pt.Name.Contains("FormLinkNullable", StringComparison.Ordinal)
                            || (!pt.IsValueType && nullCtx.Create(p).ReadState == NullabilityState.Nullable);
                        if (nullable != f.Nullable) { st.NullableChanged++; f.Nullable = nullable; }
                        if (f.Custom || f.Kind == "CustomLogic") f.ClrType = Friendly(pt);
                    }
                }
                else { p = last; f.Property = p != null && f.Name == p.Name; }

                // The enum: from the compiled property when there is one, else by the name the XML gives
                if (f.Enum == null || f.Enum == "T") continue;
                var et = (p != null && p.Name == f.Name ? EnumIn(p.PropertyType) : null) ?? ByName(f.Enum);
                if (et == null) { if (!st.EnumsNotFound.Contains(f.Enum)) st.EnumsNotFound.Add(f.Enum); continue; }
                f.Enum = EnumName(et);
                enumTypes[f.Enum] = et;
            }
        }
        // Enums handed to generic objects (Weapon.Property into ObjectTemplate<T>)
        foreach (var f in model.Objects.Values.SelectMany(o => o.Fields).Where(f => f.GenericArg != null && f.GenericArg != "T"))
        {
            var et = ByName(f.GenericArg!);
            if (et == null) continue; // a class handed to a generic object, not an enum
            f.GenericArg = EnumName(et);
            enumTypes[f.GenericArg] = et;
        }

        foreach (var (name, t) in enumTypes.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            bool flags = t.GetCustomAttribute<FlagsAttribute>() != null;
            bool unsigned64 = Enum.GetUnderlyingType(t) == typeof(ulong);
            var members = new List<EnumMember>();
            foreach (var m in Enum.GetNames(t))
            {
                object v = Enum.Parse(t, m);
                members.Add(new EnumMember(m, unsigned64 ? unchecked((long)Convert.ToUInt64(v)) : Convert.ToInt64(v), flags));
            }
            model.Enums[name] = members;
            st.EnumMembers += members.Count;
        }
        st.EnumsFound = enumTypes.Count;
        st.EnumsNamed = enumTypes.Count + st.EnumsNotFound.Count;
        return st;
    }
}
