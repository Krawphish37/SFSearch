// What SQL sees, made from schema.db alone: one typed view per record type (one column per plain field), one list
// view per list, and a union view where one signature has several record types. Nothing here is hand-kept per type.
//
// A column's expression reads the record's JSON. Where the schema says a field is always there, a value the JSON
// leaves out reads as the field's default (SFSearch.Records' own rule, FieldLookup.DefaultOf).
using System.Text;
using System.Text.Json.Nodes;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

/// <summary>One column of a view.</summary>
public sealed class ColumnSpec
{
    public required string Name { get; init; }
    /// <summary>The SQL expression. <c>{J}</c> stands for the JSON the column reads (the record's, or a list entry's),
    /// <c>{E}</c> for a list entry itself, <c>{LANG}</c> for the language of text columns.</summary>
    public required string Expr { get; set; }
    public required string Kind { get; init; }
    /// <summary>The field's path in the record, in the schema's form (<c>ObjectTemplates[*].Includes[*].Mod</c>).</summary>
    public required string SchemaPath { get; init; }
    public bool IsLink { get; init; }
    public string? LinkTo { get; init; }
    public string? Readable { get; init; }
    public string? XEdit { get; init; }
    public string? Subrecord { get; init; }
    internal string? DefaultSql;
    internal string? RelPath;
    internal bool Nullable;
    internal List<(string ObjPath, HashSet<string> Variants)> VariantGuards = new();
    internal string? NullGuard;
    internal bool FromBase;
    /// <summary>Another kind of the entry holds a list or an object under the same name (a script property's Data: a
    /// number on ScriptIntProperty, a list on ScriptIntListProperty), which the JSON gives as its JSON text: the column is
    /// read from the JSON, never straight from Mutagen's object or its bytes.</summary>
    internal bool ShapeVaries;
    /// <summary>The object <see cref="NullGuard"/> names is one the record always has (not nullable), absent from the JSON
    /// only when the serializer has nothing in it to write (every field at its default, every list empty: its
    /// HasSerializationItems): a dialogue scene action's Headtracking with no aliases and neither flag.</summary>
    internal bool GuardOmittable;
    /// <summary>The schema's row of the field (one record type's): where its bytes sit, for a column read from the
    /// record's own bytes (RawColumn, J30). Null for a column of several types or kinds.</summary>
    internal FieldRow? Row;
    /// <summary>For an ObjectType column: the path of the object whose kind it gives, relative to the record or the
    /// list entry ("" for the entry itself).</summary>
    internal string? ObjectPath;
    /// <summary>The schema's enum of an enum or flags column (its name in schema.Enums); null when the column is of
    /// several enums (one name reached through kinds of object or record types that differ).</summary>
    internal string? Enum;
    /// <summary>The schema's first row of the field, kept when <see cref="Row"/> is cleared for a column of several types.</summary>
    internal FieldRow? FirstRow;
}

/// <summary>A field of a record type that is not a column of the view asked: where it can be read instead.</summary>
public sealed record FieldHint(string Name, string SchemaPath, string Kind, string? Readable, string? XEdit, string? Subrecord, string Where);

/// <summary>One view: a record type's typed view, or one of its lists.</summary>
public sealed class ViewSpec
{
    public required string Name { get; init; }
    /// <summary>The typed view this one belongs to (itself, for a typed view).</summary>
    public required string Root { get; init; }
    /// <summary>The Mutagen types whose records feed the view: one, or all of a signature's for a union view.</summary>
    public required IReadOnlyList<string> Types { get; init; }
    public bool IsUnion { get; init; }
    /// <summary>For a list view: the path of each list, outermost first, each relative to the entry it sits in.</summary>
    public List<ListLevel> Lists { get; } = new();
    public bool IsList => Lists.Count > 0;
    /// <summary>The entries are plain values (a list of links, of numbers, of flag names): the view has a <c>Value</c> column.</summary>
    public bool ScalarEntries { get; set; }
    public List<ColumnSpec> Columns { get; } = new();
    /// <summary>The columns every view of this shape has before its field columns.</summary>
    public List<string> FixedColumns { get; } = new();
    internal Dictionary<string, ColumnSpec> ByName = new(StringComparer.OrdinalIgnoreCase);
    internal string Key = "";
}

public sealed record ListLevel(string Path, string Kind);

/// <summary>Everything SQL can name for one typed view: the view, its list views, and hints for fields that are elsewhere.</summary>
public sealed class TypeCatalog
{
    public required ViewSpec Typed { get; init; }
    public Dictionary<string, ViewSpec> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FieldHint> Hints { get; } = new();
    /// <summary>Fields left out of the typed view because the view would have more columns than SQLite allows.</summary>
    public int ColumnsCut { get; internal set; }
    public IEnumerable<ViewSpec> AllViews => Lists.Values.Prepend(Typed);
}

public sealed class ViewBuilder
{
    /// <summary>SQLite allows 2,000 columns in a table or a result. The typed view stops below that.</summary>
    public const int MaxColumns = 1900;
    /// <summary>List views are made for lists nested up to this deep (Idx, Idx2, Idx3).</summary>
    public const int MaxListDepth = 3;
    /// <summary>A plain field this many objects deep still gets a column (<c>A_B_C_D</c>). Deeper ones are read from Json.</summary>
    public const int MaxObjectDepth = 4;

    public static readonly string[] TypedFixed = { "FormKey", "EditorID", "Plugin", "IsOverride" };

    readonly RecordSchema _schema;
    readonly Dictionary<string, TypeCatalog> _catalogs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string[]> _roots = new(StringComparer.OrdinalIgnoreCase);
    readonly object _lock = new();

    public ViewBuilder(RecordSchema schema)
    {
        _schema = schema;
        var model = schema.Model;
        foreach (var t in model.RecordTypes) _roots[t.Name] = new[] { t.Name };
        // A signature with several record types gets a union view named after what they share:
        // AObjectModification's eight types are ObjectModification, GameSetting's five are GameSetting.
        foreach (var sig in schema.Signatures)
        {
            var types = schema.TypesOf(sig);
            if (types.Count < 2) continue;
            string? common = CommonBase(types);
            if (common == null) continue;
            string name = common.Length > 1 && common[0] == 'A' && char.IsUpper(common[1]) ? common[1..] : common;
            _roots[name] = types.ToArray();
            UnionNames[name] = sig;
        }
    }

    /// <summary>Union views: name to signature.</summary>
    public Dictionary<string, string> UnionNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every typed view name: each record type, and each union view.</summary>
    public IEnumerable<string> RootNames => _roots.Keys;
    public IReadOnlyList<string>? TypesOf(string root) => _roots.TryGetValue(root, out var t) ? t : null;

    string? CommonBase(IReadOnlyList<string> types)
    {
        var first = _schema.Model.Ancestry(types[0]).Select(o => o.Name).ToList();
        foreach (var name in first.Skip(1).Prepend(first[0]))
            if (types.All(t => _schema.Model.Ancestry(t).Any(o => o.Name == name)) && !types.Contains(name)) return name;
        return null;
    }

    /// <summary>The typed view a name belongs to: "Weapon" for "Weapon" and for "Weapon_Keywords". Null when the
    /// name starts with no record type.</summary>
    public string? RootOf(string viewName)
    {
        if (_roots.ContainsKey(viewName)) return _roots.Keys.First(k => k.Equals(viewName, StringComparison.OrdinalIgnoreCase));
        for (int i = viewName.Length - 1; i > 0; i--)
            if (viewName[i] == '_' && _roots.ContainsKey(viewName[..i])) return _roots.Keys.First(k => k.Equals(viewName[..i], StringComparison.OrdinalIgnoreCase));
        return null;
    }

    public ViewSpec? Find(string viewName)
    {
        string? root = RootOf(viewName);
        if (root == null) return null;
        var cat = Catalog(root);
        if (cat.Typed.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase)) return cat.Typed;
        return cat.Lists.GetValueOrDefault(viewName);
    }

    public TypeCatalog Catalog(string root)
    {
        lock (_lock)
        {
            if (_catalogs.TryGetValue(root, out var c)) return c;
            string name = _roots.Keys.First(k => k.Equals(root, StringComparison.OrdinalIgnoreCase));
            return _catalogs[name] = Build(name, _roots[name]);
        }
    }

    // ------------------------------------------------------------------ Building
    sealed class Parsed
    {
        public List<string> Lists = new();              // each list's path relative to its scope
        public string Rest = "";                        // the path inside the innermost scope ("" = the entry itself)
        public List<(string ObjPath, string Variant)> Guards = new();   // inside the innermost scope
        public bool VariantAnywhere;
    }

    static Parsed Parse(string path)
    {
        var p = new Parsed();
        var rel = new List<string>();
        int i = 0, n = path.Length;
        var name = new StringBuilder();
        void Flush() { if (name.Length > 0) { rel.Add(name.ToString()); name.Clear(); } }
        while (i < n)
        {
            char c = path[i];
            if (c == '.') { Flush(); i++; }
            else if (c == '[') { Flush(); int e = path.IndexOf(']', i); p.Lists.Add(string.Join('.', rel)); rel.Clear(); p.Guards.Clear(); i = e + 1; }
            else if (c == '{') { Flush(); int e = path.IndexOf('}', i); p.Guards.Add((string.Join('.', rel), path[(i + 1)..e])); p.VariantAnywhere = true; i = e + 1; }
            else { name.Append(c); i++; }
        }
        Flush();
        p.Rest = string.Join('.', rel);
        return p;
    }

    static string JsonPath(string rel) => rel.Length == 0 ? "$" : "$." + rel;

    TypeCatalog Build(string root, string[] types)
    {
        bool union = types.Length > 1 || !types[0].Equals(root, StringComparison.Ordinal);
        var typed = new ViewSpec { Name = root, Root = root, Types = types, IsUnion = union, Key = "" };
        typed.FixedColumns.AddRange(TypedFixed);
        if (union) typed.FixedColumns.Add("Type");
        var cat = new TypeCatalog { Typed = typed };
        var views = new Dictionary<string, ViewSpec>(StringComparer.Ordinal) { [""] = typed };
        var listKind = new Dictionary<string, string>(StringComparer.Ordinal);       // scope key + "|" + rest -> kind of that list
        var objNullable = new Dictionary<string, bool>(StringComparer.Ordinal);      // scope key + "|" + object path -> may be absent
        var objAlways = new HashSet<string>(StringComparer.Ordinal);                 // … an object the record always has (not nullable)
        var cutObjects = new HashSet<string>(StringComparer.Ordinal);
        var hinted = new HashSet<string>(StringComparer.Ordinal);
        var heldHinted = new HashSet<string>(StringComparer.Ordinal);

        foreach (string type in types)
        {
            foreach (var row in _schema.Model.Expand(type))
            {
                var def = row.Def;
                if (!def.Property) continue;
                var p = Parse(row.Path);
                string scopeKey = string.Join("|", p.Lists);
                string kind = def.Kind;
                if (p.Lists.Count == 0 && p.Rest is "FormKey" or "EditorID") continue;
                // A record held in this one (a cell's navmeshes, a worldspace's top cell, a topic's responses) is a record
                // of its own: the holder's JSON keeps only a marker for it (RecordSplit), so a column or list view of it
                // here would give no value. Its fields are in its own type's view; Forms.Parent names its holder.
                if (HoldsRecords(row.Sig, row.PlainPath))
                {
                    string top = TopField(row.PlainPath);
                    if (heldHinted.Add(top))
                        cat.Hints.Add(new FieldHint(top, top, def.Kind, def.Readable, null, row.Subrecord,
                            $"records held in this one, each a record of its own: read them in their own type's view (Forms.Parent is the record that holds each one, e.g. SELECT … FROM Forms WHERE Parent = '<FormKey>')"));
                    continue;
                }

                bool isContainer = kind is "List" or "Array" or "Array2D" or "Dictionary";
                if (kind is "Group" or "CustomLogic" or "Break" or "Marker") continue;
                // A read-only property Mutagen adds beyond its XML (RegionData.DataType): the record's JSON never holds
                // it, so a column would show a default, not the value (GAPS Part 2 row 8)
                if (def.ReadOnly) { AddHint(cat, hinted, row, p, "not in the record's JSON (Mutagen's serializer doesn't write this read-only property): read it with record --fields"); continue; }
                if (p.Lists.Count > MaxListDepth) { if (!isContainer && kind is not ("Object" or "Gendered")) AddHint(cat, hinted, row, p, "inside a list nested deeper than the list views go: read it from the Json column of the list view above it"); continue; }
                // A grid is a list of rows, each a list of cells: its list view gives each row as JSON
                int grid = GridLevel(p, listKind);
                if (grid >= 0)
                {
                    if (grid == p.Lists.Count - 1 && EnsureList(cat, views, root, types, union, p, listKind, scalar: true) is { } gv && !gv.ByName.ContainsKey("Value"))
                        AddColumn(cat, gv, row, p, "Array2DRow", "{E}.value", null, objNullable, scopeKey, name: "Value");
                    continue;
                }
                if (isContainer) { listKind[scopeKey + "|" + p.Rest] = kind; AddHint(cat, hinted, row, p, $"a list: use the view {ListName(root, p.Lists.Append(p.Rest))}"); continue; }
                if (kind is "Object" or "Gendered")
                {
                    if (p.Rest.Length == 0) { EnsureList(cat, views, root, types, union, p, listKind, scalar: false); continue; }
                    objNullable[scopeKey + "|" + p.Rest] = def.Nullable || p.Guards.Count > 0;
                    // (an object of several kinds is never left out of the JSON: it always holds its kind, as a condition's
                    // Data does, {"MutagenObjectType":"IsActorConditionData"}, so its fields read their defaults: GAPS S166)
                    if (!def.Nullable && !(def.RefObject is { } always && _schema.IsPolymorphic(always))) objAlways.Add(scopeKey + "|" + p.Rest);
                    if (IsUnder(cutObjects, scopeKey, p.Rest)) continue;
                    // An object too deep for columns of its own becomes one column holding its JSON
                    if (p.Rest.Count(ch => ch == '.') + 1 >= MaxObjectDepth && GetView(cat, views, root, types, union, p, listKind) is { } owner)
                    {
                        cutObjects.Add(scopeKey + "|" + p.Rest);
                        AddColumn(cat, owner, row, p, "Json", $"{{J}} -> '{JsonPath(p.Rest)}'", null, objNullable, scopeKey);
                    }
                    continue;
                }
                if (IsUnder(cutObjects, scopeKey, p.Rest)) { AddHint(cat, hinted, row, p, $"deep inside an object: read it with Json ->> '$.{row.PlainPath.Replace("[*]", "[0]")}'"); continue; }

                var view = p.Rest.Length == 0
                    ? EnsureList(cat, views, root, types, union, p, listKind, scalar: true)
                    : GetView(cat, views, root, types, union, p, listKind);
                if (view == null) continue;
                var (expr, dflt) = Expression(def, row.Enum, p.Rest);
                AddColumn(cat, view, row, p, kind, expr, dflt, objNullable, scopeKey, objAlways: objAlways);
            }
        }

        // A flags field of the record itself also reads as a list of names: Npc_Flags
        foreach (var col in typed.Columns.ToList())
        {
            if (col.Kind != "Flags" || col.RelPath == null) continue;
            string name = root + "_" + col.Name;
            if (cat.Lists.ContainsKey(name) || _roots.ContainsKey(name)) continue;
            var v = new ViewSpec { Name = name, Root = root, Types = types, IsUnion = union, ScalarEntries = true, Key = "flags:" + col.Name };
            v.Lists.Add(new ListLevel(col.RelPath, "Flags"));
            v.FixedColumns.AddRange(new[] { "FormKey", "EditorID", "Plugin", "Idx" });
            var value = new ColumnSpec { Name = "Value", Expr = "{E}.value", Kind = "Enum", SchemaPath = col.SchemaPath, Readable = col.Readable, XEdit = col.XEdit, Subrecord = col.Subrecord };
            v.Columns.Add(value); v.ByName["Value"] = value;
            cat.Lists[name] = v;
        }

        // Each flags column also as its number, right after it: EntryTypes, EntryTypesRaw (GAPS S168)
        foreach (var v in cat.AllViews)
            for (int i = v.Columns.Count - 1; i >= 0; i--)
                if (FlagsNumber(cat, v, v.Columns[i]) is { } raw) { v.Columns.Insert(i + 1, raw); v.ByName[raw.Name] = raw; }

        // A column whose name is a list or an object in another kind of the same entry (ShapeVaries)
        foreach (var v in cat.AllViews)
            foreach (var c in v.Columns)
                if (c.RelPath is { Length: > 0 } rel && (listKind.ContainsKey(v.Key + "|" + rel) || objNullable.ContainsKey(v.Key + "|" + rel))) c.ShapeVaries = true;

        foreach (var v in cat.AllViews)
            foreach (var c in v.Columns) c.Expr = Guarded(c);
        return cat;
    }

    /// <summary>
    /// The number column of a flags column (<c>EntryTypesRaw</c> beside <c>EntryTypes</c>, GAPS S168): the same flags as
    /// one integer, so a bit is tested by its number, <c>(EntryTypesRaw &amp; 0x80000000) &lt;&gt; 0</c>. The same field,
    /// guards, NULLs and default as the flags column; its value from the JSON is the names' bits (sfs_flagbits), from
    /// Mutagen's object and the bytes the enum's value (FlagsRaw.cs). None for a column of several enums, for a name the
    /// view has already, and for the record header's flags, which the view has as a number already
    /// (MajorRecordFlagsRaw; Forms.Flags too).
    /// </summary>
    ColumnSpec? FlagsNumber(TypeCatalog cat, ViewSpec v, ColumnSpec c)
    {
        if (c.Kind != "Flags" || c.Enum is not { } enumName || FlagsRaw.EnumOf(enumName) == null) return null;
        string name = c.Name + FlagsRaw.Suffix;
        if (Reserved.Contains(name) || v.FixedColumns.Contains(name, StringComparer.OrdinalIgnoreCase) || v.ByName.ContainsKey(name)) return null;
        if (c.FirstRow is { } fr && fr.Def.Subrecord == null && HandLayouts.HeaderPlace(_schema.Model, fr.Def) is { Bit: 0 } at
            && v.Columns.Any(o => o != c && o.Kind is "Int32" or "UInt32" && o.FirstRow is { } or && or.Def.Subrecord == null && HandLayouts.HeaderPlace(_schema.Model, or.Def) is { Bit: 0 } oat && oat.Offset == at.Offset && oat.Size == at.Size))
            return null;
        if (v.Columns.Count + v.FixedColumns.Count + 4 >= MaxColumns) { cat.ColumnsCut++; return null; }
        string? dflt = null;
        if (c.DefaultSql != null)
        {
            if (!DirectColumn.TryLiteral(c.DefaultSql, out var d) || d is not string list) return null;
            dflt = FlagsRaw.FromNames(list, enumName)?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return new ColumnSpec
        {
            Name = name, Expr = $"sfs_flagbits({c.Expr}, {SqlText.Literal(enumName)})", Kind = FlagsRaw.Kind, SchemaPath = c.SchemaPath,
            Readable = c.Readable, XEdit = c.XEdit, Subrecord = c.Subrecord, DefaultSql = dflt, RelPath = c.RelPath, Nullable = c.Nullable,
            VariantGuards = c.VariantGuards.Select(g => (g.ObjPath, new HashSet<string>(g.Variants, StringComparer.Ordinal))).ToList(),
            NullGuard = c.NullGuard, GuardOmittable = c.GuardOmittable, FromBase = c.FromBase, Row = c.Row, FirstRow = c.FirstRow, Enum = enumName,
        };
    }

    /// <summary>The list level that is a grid, or -1.</summary>
    static int GridLevel(Parsed p, Dictionary<string, string> listKind)
    {
        for (int i = 0; i < p.Lists.Count; i++)
            if (listKind.GetValueOrDefault(string.Join("|", p.Lists.Take(i)) + "|" + p.Lists[i]) == "Array2D") return i;
        return -1;
    }

    static bool IsUnder(HashSet<string> cut, string scopeKey, string rest)
    {
        if (cut.Count == 0) return false;
        for (int i = rest.LastIndexOf('.'); i > 0; i = rest.LastIndexOf('.', i - 1))
            if (cut.Contains(scopeKey + "|" + rest[..i])) return true;
        return false;
    }

    static string ListName(string root, IEnumerable<string> lists) => root + "_" + string.Join("_", lists.Select(l => l.Replace('.', '_')));

    ViewSpec? GetView(TypeCatalog cat, Dictionary<string, ViewSpec> views, string root, string[] types, bool union, Parsed p, Dictionary<string, string> listKind)
    {
        if (p.Lists.Count == 0) return cat.Typed;
        return EnsureList(cat, views, root, types, union, p, listKind, scalar: false);
    }

    ViewSpec? EnsureList(TypeCatalog cat, Dictionary<string, ViewSpec> views, string root, string[] types, bool union, Parsed p, Dictionary<string, string> listKind, bool scalar)
    {
        if (p.Lists.Count == 0 || p.Lists.Count > MaxListDepth) return null;
        string key = string.Join("|", p.Lists);
        if (views.TryGetValue(key, out var have)) { if (!scalar) have.ScalarEntries = false; return have; }
        string name = ListName(root, p.Lists);
        // A list view can't take the name of a record type (none does today)
        if (_roots.ContainsKey(name) || cat.Lists.ContainsKey(name)) name += "_List";
        var v = new ViewSpec { Name = name, Root = root, Types = types, IsUnion = union, ScalarEntries = scalar, Key = key };
        for (int i = 0; i < p.Lists.Count; i++)
        {
            string parent = string.Join("|", p.Lists.Take(i)) + "|" + p.Lists[i];
            v.Lists.Add(new ListLevel(p.Lists[i], listKind.GetValueOrDefault(parent, "List")));
        }
        v.FixedColumns.AddRange(new[] { "FormKey", "EditorID", "Plugin", "Idx" });
        for (int i = 1; i < p.Lists.Count; i++) v.FixedColumns.Add("Idx" + (i + 1));
        if (v.Lists[^1].Kind == "Dictionary") v.FixedColumns.Add("Key");
        views[key] = v;
        cat.Lists[name] = v;
        return v;
    }

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "Json", "LoadOrder", "IsWinner", "rowid" };

    void AddColumn(TypeCatalog cat, ViewSpec view, FieldRow row, Parsed p, string kind, string expr, string? dflt,
        Dictionary<string, bool> objNullable, string scopeKey, string? name = null, HashSet<string>? objAlways = null)
    {
        var def = row.Def;
        name ??= p.Rest.Length == 0 ? "Value" : p.Rest.Replace('.', '_');
        // A field named like one of the view's own columns gets an underscore after its name
        if (Reserved.Contains(name) || view.FixedColumns.Contains(name, StringComparer.OrdinalIgnoreCase)) name += "_";
        if (view.ByName.TryGetValue(name, out var have))
        {
            if (have.Enum != row.Enum) have.Enum = null;
            // A text translated in one kind and not in another (a perk effect's Text): the JSON's text of each (ShapeVaries)
            if (have.FirstRow is { } firstRow && firstRow.Def.Translated != def.Translated) have.ShapeVaries = true;
            // The same field reached through another kind of object: both kinds read it
            if (have.FromBase) return;
            have.Row = null;
            if (p.Guards.Count == 0) { have.FromBase = true; have.VariantGuards.Clear(); return; }
            foreach (var g in p.Guards)
            {
                var slot = have.VariantGuards.FirstOrDefault(x => x.ObjPath == g.ObjPath);
                if (slot.Variants != null) slot.Variants.Add(g.Variant);
            }
            // Two kinds with one name and different types: the value is read as it is, with no default
            if (have.Kind != (kind == "Enum" && IsFlags(row.Enum) ? "Flags" : kind)) { have.DefaultSql = null; have.Nullable = true; }
            return;
        }
        if (view.Columns.Count + view.FixedColumns.Count + 4 >= MaxColumns) { cat.ColumnsCut++; return; }
        // The innermost object on the way that may be absent: when it is, the field has no value, not its default
        string? nullGuard = null;
        for (int i = p.Rest.LastIndexOf('.'); i > 0; i = p.Rest.LastIndexOf('.', i - 1))
            if (objNullable.GetValueOrDefault(scopeKey + "|" + p.Rest[..i])) { nullGuard = p.Rest[..i]; break; }
        var col = new ColumnSpec
        {
            Name = name, Expr = expr, Kind = kind == "Enum" && IsFlags(row.Enum) ? "Flags" : kind, SchemaPath = row.PlainPath,
            IsLink = kind == "FormLink", LinkTo = def.LinkTo, Readable = def.Readable,
            XEdit = _schema.XEditPath(row.Sig, row.PlainPath) ?? def.XEdit, Subrecord = row.Subrecord,
            DefaultSql = dflt, RelPath = p.Rest, Nullable = def.Nullable, NullGuard = nullGuard, FromBase = p.Guards.Count == 0,
            GuardOmittable = nullGuard != null && objAlways != null && objAlways.Contains(scopeKey + "|" + nullGuard),
            Row = row, FirstRow = row, Enum = row.Enum,
        };
        foreach (var g in p.Guards)
        {
            col.VariantGuards.Add((g.ObjPath, new HashSet<string>(StringComparer.Ordinal) { g.Variant }));
            // What kind of object this one is, once per place
            string typeCol = g.ObjPath.Length == 0 ? "ObjectType" : g.ObjPath.Replace('.', '_') + "_ObjectType";
            if (!view.ByName.ContainsKey(typeCol) && view.Columns.Count + view.FixedColumns.Count + 4 < MaxColumns)
            {
                var tc = new ColumnSpec { Name = typeCol, Expr = $"{{J}} ->> '{JsonPath(g.ObjPath)}.MutagenObjectType'", Kind = "ObjectType", SchemaPath = row.PlainPath, FromBase = true, Nullable = true, ObjectPath = g.ObjPath };
                view.Columns.Add(tc); view.ByName[typeCol] = tc;
            }
        }
        view.Columns.Add(col);
        view.ByName[name] = col;
    }

    readonly Dictionary<(string, string), bool> _holds = new();

    /// <summary>True when the record type's top-level field on this path can hold records of its own (RecordSchema.FieldHoldsRecords).</summary>
    static string TopField(string path)
    {
        int end = path.IndexOfAny(new[] { '.', '[', '{' });
        return end < 0 ? path : path[..end];
    }

    bool HoldsRecords(string sig, string path)
    {
        string field = TopField(path);
        var types = _schema.TypesOf(sig);
        if (types.Count == 0) return false;
        if (!_holds.TryGetValue((sig, field), out bool holds)) _holds[(sig, field)] = holds = types.Any(t => _schema.HoldsRecords(t) && _schema.FieldHoldsRecords(t, field));
        return holds;
    }

    void AddHint(TypeCatalog cat, HashSet<string> hinted, FieldRow row, Parsed p, string where)
    {
        if (!hinted.Add(row.PlainPath)) return;
        string leaf = row.PlainPath[(row.PlainPath.LastIndexOf('.') + 1)..].Replace("[*]", "");
        cat.Hints.Add(new FieldHint(leaf, row.PlainPath, row.Def.Kind, row.Def.Readable, _schema.XEditPath(row.Sig, row.PlainPath) ?? row.Def.XEdit, row.Subrecord, where));
    }

    bool IsFlags(string? enumName) => _schema.Enum(enumName) is { } m && m.Any(x => x.IsFlags);

    /// <summary>The expression that reads one plain field, and the SQL text of its default (null: none).</summary>
    (string Expr, string? Default) Expression(FieldDef def, string? enumName, string rest)
    {
        string get = rest.Length == 0 ? "{E}.value" : $"{{J}} ->> '{JsonPath(rest)}'";
        string raw = rest.Length == 0 ? "{E}.value" : $"{{J}} -> '{JsonPath(rest)}'";
        string kind = def.Kind;
        string? dflt = null;
        if (!def.Nullable)
        {
            // The same default the records layer gives in "fields" mode (held against Mutagen there)
            var probe = enumName == def.Enum ? def : Clone(def, enumName);
            var (json, _) = FieldLookup.DefaultOf(_schema, probe);
            dflt = json switch
            {
                null => null,
                JsonValue v when v.TryGetValue<bool>(out bool b) => b ? "1" : "0",
                JsonValue v when v.TryGetValue<string>(out string? s) => SqlText.Literal(s),
                JsonValue v => v.ToJsonString(),
                JsonArray a => SqlText.Literal(a.ToJsonString()),
                _ => null,
            };
        }
        switch (kind)
        {
            case "FormLink": return ($"nullif({get}, 'Null') COLLATE NOCASE", null);
            case "String" when def.Translated: return ($"sfs_text({raw}, {{LANG}})", def.Nullable ? null : "''");
            case "Enum" when IsFlags(enumName): return (raw, dflt);
            default: return (get, dflt);
        }
    }

    static FieldDef Clone(FieldDef d, string? enumName) => new()
    {
        Object = d.Object, Ord = d.Ord, Path = d.Path, Name = d.Name, Kind = d.Kind, Subrecord = d.Subrecord, Offset = d.Offset, Size = d.Size,
        Nullable = d.Nullable, InList = d.InList, Translated = d.Translated, Unknown = d.Unknown, Custom = d.Custom, Property = d.Property,
        RefObject = d.RefObject, LinkTo = d.LinkTo, Enum = enumName, Default = d.Default, ClrType = d.ClrType, Readable = d.Readable, XEdit = d.XEdit,
    };

    /// <summary>The column's expression with its default and its guards: an object on the way that is absent or of
    /// another kind gives no value, not the default.</summary>
    static string Guarded(ColumnSpec c)
    {
        // No default to give: an absent value reads as NULL whatever the reason
        if (c.DefaultSql == null || c.Nullable || c.Kind is "ObjectType" or "Array2DRow" or "Json") return c.Expr;
        // A JSON value's default is JSON too ('[]' for flags), so that the column gives JSON either way (GAPS S80)
        string expr = $"coalesce({c.Expr}, {(IsJson(c) ? $"json({c.DefaultSql})" : c.DefaultSql)})";
        var conds = new List<string>();
        if (c.NullGuard != null) conds.Add($"json_type({{J}}, '{JsonPath(c.NullGuard)}') IS NOT NULL");
        if (!c.FromBase)
            foreach (var (objPath, variants) in c.VariantGuards)
            {
                string t = $"{{J}} ->> '{JsonPath(objPath)}.MutagenObjectType'";
                // A generic kind is written with its argument: ObjectModFloatProperty<Weapon+Property>
                conds.Add($"substr({t}, 1, instr({t} || '<', '<') - 1) IN ({string.Join(", ", variants.OrderBy(v => v, StringComparer.Ordinal).Select(SqlText.Literal))})");
            }
        return conds.Count == 0 ? expr : $"CASE WHEN {string.Join(" AND ", conds)} THEN {expr} END";
    }

    // ------------------------------------------------------------------ SQL
    /// <summary>One source of rows for a view: a scratch file attached under a name.</summary>
    /// <remarks><c>Table</c>: the table of records to read instead of the file's <c>Rec</c> (records read by key for one
    /// statement, Session.Keyed.cs); it has Rec's columns.</remarks>
    public sealed record Member(string Db, string? PluginLiteral, string? Where, bool HasLoadOrder, int? LoadOrder, string? Table = null);

    static string Fill(string expr, string j, string e, string lang) => expr.Replace("{J}", j).Replace("{E}", e).Replace("{LANG}", lang);

    /// <summary>The names of a view's columns in order, as a query sees them.</summary>
    public static List<string> ColumnNames(ViewSpec v, bool versions)
    {
        var names = new List<string>(v.FixedColumns);
        if (versions && !v.IsList) names.InsertRange(names.IndexOf("IsOverride") + 1, new[] { "LoadOrder", "IsWinner" });
        names.AddRange(v.Columns.Select(c => c.Name));
        if (!v.IsList || !v.ScalarEntries) names.Add("Json");
        return names;
    }

    /// <summary>One SELECT that gives a view's rows from one scratch file. A view over several files is these joined by UNION ALL.</summary>
    public static string SelectSql(ViewSpec v, Member mem, string language, bool versions, IReadOnlyCollection<string>? onlyColumns = null, bool withJson = true)
    {
        string lang = SqlText.Literal(language);
        var sb = new StringBuilder();
        string types = string.Join(", ", v.Types.Select(SqlText.Literal));
        {
            string plugin = mem.PluginLiteral != null ? SqlText.Literal(mem.PluginLiteral) + " COLLATE NOCASE" : "r.Plugin";
            sb.Append("SELECT r.FormKey AS FormKey, r.EditorID AS EditorID, ").Append(plugin).Append(" AS Plugin");
            string j, e;
            if (!v.IsList)
            {
                sb.Append(", r.IsOverride AS IsOverride");
                if (versions) sb.Append(mem.HasLoadOrder ? ", r.LoadOrder AS LoadOrder, r.IsWinner AS IsWinner" : $", {(mem.LoadOrder is int lo ? lo.ToString() : "NULL")} AS LoadOrder, sfs_iswinner(r.FormKey, {plugin}) AS IsWinner");
                if (v.IsUnion) sb.Append(", r.Type AS Type");
                j = "r.Json"; e = "r";
            }
            else
            {
                int k = v.Lists.Count;
                for (int i = 0; i < k; i++) sb.Append($", e{i + 1}.key AS Idx{(i == 0 ? "" : (i + 1).ToString())}");
                // A dictionary is a list of {"Key": …, "Value": …}: the entry's own value is under Value
                bool dict = v.Lists[^1].Kind == "Dictionary";
                if (dict) sb.Append($", e{k}.value ->> '$.Key' AS Key");
                e = $"e{k}";
                j = dict ? $"(e{k}.value -> '$.Value')" : $"e{k}.value";
            }
            var json = new JsonJoins();
            foreach (var c in v.Columns)
            {
                if (onlyColumns != null && !onlyColumns.Contains(c.Name)) continue;
                string expr = Fill(v.IsList && v.Lists[^1].Kind == "Dictionary" ? c.Expr.Replace("{E}.value", $"({e}.value ->> '$.Value')") : c.Expr, j, e, lang);
                sb.Append(", ").Append(IsJson(c) ? json.Column(expr, c.Name) : expr + " AS " + SqlText.Name(c.Name));
            }
            if (!v.IsList) { if (withJson) sb.Append(", ").Append(json.Column("r.Json", "Json")); }
            else if (!v.ScalarEntries && withJson) sb.Append(", ").Append(json.Column(j, "Json"));
            sb.Append(" FROM ").Append(mem.Table ?? mem.Db + ".Rec").Append(" r");
            for (int i = 0; i < v.Lists.Count; i++)
            {
                string src = i == 0 ? "r.Json" : v.Lists[i - 1].Kind == "Dictionary" ? $"(e{i}.value -> '$.Value')" : $"e{i}.value";
                sb.Append($", json_each({src}, '{JsonPath(v.Lists[i].Path)}') e{i + 1}");
            }
            sb.Append(json.From);
            sb.Append(" WHERE r.Type IN (").Append(types).Append(')');
            sb.Append(ListsOnly(v.Lists.Count, " AND "));
            if (mem.Where != null) sb.Append(" AND ").Append(mem.Where);
        }
        return sb.ToString();
    }

    /// <summary>The expressions of some typed columns over a column named Json: what a projection of a heavy type is read with.</summary>
    public static List<string> ProjectionExprs(ViewSpec v, IEnumerable<string> columns, string language)
    {
        string lang = SqlText.Literal(language);
        return columns.Select(n => Fill(v.ByName[n].Expr, "Json", "Json", lang)).ToList();
    }

    /// <summary>The column of a list projection read through the records' JSON that holds each entry (GAPS S58).</summary>
    public const string EntryColumn = "sfs_entry";

    /// <summary>
    /// The statement that gives one record's rows of a list view from its JSON (GAPS S58): over one value named Json
    /// (the record's JSONB, <see cref="JsonbMaker.ProjectRows"/>), the list places (Idx, Idx2, …), the named plain
    /// columns with the view's own expressions, then the entry itself as JSONB (a one-item array of it, which keeps a
    /// plain value plain). The view gives every other column from that entry with its own expressions over json_each,
    /// as <see cref="SelectSql"/> does over the record (<see cref="ListEntrySql"/>): the same values, JSON as JSON.
    /// </summary>
    public static string ListProjectSql(ViewSpec v, IReadOnlyList<string> plain, string language)
    {
        string lang = SqlText.Literal(language);
        int k = v.Lists.Count;
        bool dict = v.Lists[^1].Kind == "Dictionary";
        string e = $"e{k}", j = dict ? $"(e{k}.value -> '$.Value')" : $"e{k}.value";
        var parts = new List<string>();
        for (int i = 0; i < k; i++) parts.Add($"e{i + 1}.key");
        foreach (string name in plain)
            parts.Add(Fill(dict ? v.ByName[name].Expr.Replace("{E}.value", $"({e}.value ->> '$.Value')") : v.ByName[name].Expr, j, e, lang));
        parts.Add($"jsonb(json_array({e}.value))");
        var sb = new StringBuilder("SELECT ").Append(string.Join(", ", parts)).Append(" FROM (SELECT $j AS Json) r");
        for (int i = 0; i < k; i++)
        {
            string src = i == 0 ? "r.Json" : v.Lists[i - 1].Kind == "Dictionary" ? $"(e{i}.value -> '$.Value')" : $"e{i}.value";
            sb.Append($", json_each({src}, '{JsonPath(v.Lists[i].Path)}') e{i + 1}");
        }
        sb.Append(ListsOnly(k, " WHERE "));
        return sb.ToString();
    }

    /// <summary>
    /// Only a list's own entries: every list is a JSON array, whose entries json_each numbers 0, 1, … A field of the
    /// same name that is a plain value or an object in another kind of entry (a script property's Data is a list in a
    /// ScriptFloatListProperty and one number in a ScriptIntProperty) would give json_each one row with no number, or a
    /// row for each of the object's fields: not entries of the list (fix-s4s5-world-reads).
    /// </summary>
    static string ListsOnly(int levels, string lead) =>
        levels == 0 ? "" : lead + string.Join(" AND ", Enumerable.Range(1, levels).Select(i => $"typeof(e{i}.key) = 'integer'"));

    /// <summary>
    /// A list view's columns over a list projection (alias <c>x</c>): a column the table holds as it is, any other
    /// from the entry it holds (<see cref="EntryColumn"/>), with the view's own expressions over json_each of it, as
    /// <see cref="SelectSql"/> gives them over the record. Each item is "expression AS name"; <c>From</c> follows the table.
    /// </summary>
    public static (List<string> Columns, string From) ListEntrySql(ViewSpec v, IEnumerable<string> columns, ISet<string> stored, string language)
    {
        string lang = SqlText.Literal(language);
        bool dict = v.Lists[^1].Kind == "Dictionary";
        const string e = "sfs_e";
        string j = dict ? $"({e}.value -> '$.Value')" : $"{e}.value";
        var list = new List<string>();
        var json = new JsonJoins();
        bool entry = false;
        foreach (string name in columns)
        {
            string expr;
            bool isJson = name.Equals("Json", StringComparison.OrdinalIgnoreCase) && !v.ByName.ContainsKey(name) || v.ByName.TryGetValue(name, out var spec) && IsJson(spec);
            if (stored.Contains(name)) expr = isJson ? $"json(x.{SqlText.Name(name)})" : $"x.{SqlText.Name(name)}";
            else
            {
                entry = true;
                if (IsListKey(v, name)) expr = $"{e}.value ->> '$.Key'";
                else if (name.Equals("Json", StringComparison.OrdinalIgnoreCase)) expr = j;
                else expr = Fill(dict ? v.ByName[name].Expr.Replace("{E}.value", $"({e}.value ->> '$.Value')") : v.ByName[name].Expr, j, e, lang);
            }
            list.Add(isJson ? json.Column(expr, name) : $"{expr} AS {SqlText.Name(name)}");
        }
        return (list, (entry ? $", json_each(x.{EntryColumn}) {e}" : "") + json.From);
    }

    /// <summary>A column whose values are JSON (a flags list, an object too deep for columns of its own).</summary>
    public static bool IsJson(ColumnSpec c) => c.Kind is "Flags" or "Json";

    /// <summary>
    /// A view's JSON columns given so that they stay JSON inside SQLite's JSON functions (GAPS S80). A value leaving a
    /// view loses SQLite's JSON mark when the view's column is a function (json(r.Json), r.Json -> '$.Flags', a
    /// coalesce or CASE around one): json_object('j', Json) over Quest embedded a string, not an object. A column of
    /// json_each keeps it, so each JSON column is the value of json_each over a one-item array of it, which gives
    /// exactly one row: the same text, the mark kept (SQLite 3.53.3, tested in views-all).
    /// </summary>
    public sealed class JsonJoins
    {
        readonly StringBuilder _from = new();
        int _n;

        /// <summary>The column "sfs_jN.value AS name" for the expression; its join goes into <see cref="From"/>.</summary>
        public string Column(string expr, string name)
        {
            _n++;
            _from.Append($", json_each(json_array({expr})) sfs_j{_n}");
            return $"sfs_j{_n}.value AS {SqlText.Name(name)}";
        }

        /// <summary>The joins, each ", json_each(…) sfs_jN", to follow the view's tables.</summary>
        public string From => _from.ToString();
    }

    /// <summary>True for a dictionary list view's own Key column (not a field named Key).</summary>
    public static bool IsListKey(ViewSpec v, string name) =>
        v.IsList && v.Lists[^1].Kind == "Dictionary" && name.Equals("Key", StringComparison.OrdinalIgnoreCase) && !v.ByName.ContainsKey(name);

    /// <summary>A table with the view's columns and no rows: what a statement is prepared against to learn which
    /// views and columns it reads.</summary>
    public static string StubSql(string name, IEnumerable<string> columns) =>
        $"CREATE TEMP TABLE {SqlText.Name(name)} ({string.Join(", ", columns.Select(SqlText.Name))})";
}
