// What every name SQL can read is, column by column: the typed views, the union views, the list views, Records,
// Forms, Links and the schema's tables. describe answers from this (fix B8, 2026-11-12: describe Links listed record
// fields named Links, and list views' names had to be guessed).
using Microsoft.Data.Sqlite;

namespace SFSearch.Query;

/// <summary>One column of a view: its name, its type as SQL sees it, and one line on what it holds.</summary>
public sealed record ViewColumnDoc(string Name, string Type, string About);

/// <summary>One name SQL can read.</summary>
public sealed class ViewDoc
{
    public required string Name { get; init; }
    /// <summary>"typed view", "union view", "list view", "flags view", "special view", "schema table".</summary>
    public required string Kind { get; init; }
    public required string About { get; init; }
    public List<ViewColumnDoc> Columns { get; } = new();
    /// <summary>For a typed or union view: its list views, by their exact names.</summary>
    public List<string> ListViews { get; } = new();
    /// <summary>For a typed view of a type in a union: the union view. For a list view: its typed view.</summary>
    public string? Related { get; init; }
    /// <summary>For a union view: the record types it holds.</summary>
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();
}

public static class ViewDocs
{
    static readonly Dictionary<string, (string Type, string About)> Fixed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FormKey"] = ("text", "the record's FormKey (0476C4:Starfield.esm)"),
        ["EditorID"] = ("text", "the record's EditorID"),
        ["Plugin"] = ("text", "the plugin this copy of the record comes from"),
        ["IsOverride"] = ("integer 0/1", "1 when the record overrides a record of a master"),
        ["LoadOrder"] = ("integer", "the plugin's place in the load order (versions scope only)"),
        ["IsWinner"] = ("integer 0/1", "1 for the copy the game uses (versions scope only)"),
        ["Type"] = ("text", "the record type of the row (Mutagen's name)"),
        ["Idx"] = ("integer", "the entry's place in the list, from 0"),
        ["Idx2"] = ("integer", "the entry's place in the list inside the Idx entry, from 0"),
        ["Idx3"] = ("integer", "the entry's place in the list one deeper still, from 0"),
        ["Key"] = ("text", "the entry's key (the list is a dictionary)"),
        ["Json"] = ("JSON", "the whole record as Mutagen writes it; read any field with Json ->> '$.Path'"),
    };

    /// <summary>The three views that aren't made from a record type.</summary>
    static readonly Dictionary<string, (string About, (string Name, string Type, string About)[] Columns)> Special = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Records"] = ("Every record read so far in this scope, of every type, one row each. A statement that narrows it (WHERE Type = 'Weapon', FormKey = '…', EditorID = '…') has that type read first; one that narrows to nothing reads every type of the scope when nothing is read yet (its cost is said first). Forms has every record's header, read or not.", new[]
        {
            ("FormKey", "text", "the record's FormKey"), ("Type", "text", "the record type (Mutagen's name: Weapon, WeaponModification)"), ("Sig", "text", "the signature (WEAP)"),
            ("EditorID", "text", "the record's EditorID"), ("Name", "text", "the in-game name, in the query's language, without the Creation Kit's tag"),
            ("Plugin", "text", "the plugin this copy comes from"), ("IsOverride", "integer 0/1", "1 when it overrides a record of a master"),
            ("LoadOrder", "integer", "the plugin's place in the load order (versions scope only)"), ("IsWinner", "integer 0/1", "1 for the copy the game uses (versions scope only)"),
            ("Json", "JSON", "the whole record as Mutagen writes it"),
        }),
        ["Forms"] = ("Every record of the scope from the header index, read or not, one row each: no record is opened.", new[]
        {
            ("FormKey", "text", "the record's FormKey"), ("Sig", "text", "the signature (WEAP)"), ("Type", "text", "the record type, or the union view of a signature with several (ObjectModification)"),
            ("EditorID", "text", "the record's EditorID"), ("Plugin", "text", "the plugin this copy comes from"),
            ("Parent", "text", "the FormKey of the record it sits in (a cell for a placed object, a topic for a dialogue line); NULL at the top"),
            ("IsWinner", "integer 0/1", "1 for the copy the game uses"), ("IsOverride", "integer 0/1", "1 when it overrides a record of a master"),
            ("Pos", "integer", "where the record starts in the plugin file, in bytes"), ("LoadOrder", "integer", "the plugin's place in the load order (NULL when it isn't loaded)"),
        }),
        ["Links"] = ("The links (FormKeys) inside the records read so far in this scope, one row per link. A statement that narrows it (WHERE ToKey = '…', FromKey = '…', FromType = '…') has what that needs read first: for ToKey, the types whose records link to it (the link index), or that can (the schema).", new[]
        {
            ("FromKey", "text", "the FormKey of the record that holds the link"), ("FromType", "text", "that record's type (Mutagen's name)"),
            ("Field", "text", "the field, every list place written [*] and a last one dropped (Keywords, ObjectTemplates[*].Includes[*].Mod)"),
            ("Idx", "integer", "the place in the innermost list (NULL outside a list)"), ("ToKey", "text", "the FormKey the link points at"),
            ("Path", "text", "the exact place (ObjectTemplates[0].Includes[2].Mod)"), ("Plugin", "text", "the plugin the linking record comes from"),
        }),
    };

    /// <summary>The names of the views that aren't made from a record type.</summary>
    public static IEnumerable<string> SpecialNames => Special.Keys;

    /// <summary>
    /// What SQL sees under a name: a typed view (Weapon), a union view (ObjectModification), a list view
    /// (Weapon_Keywords), Records, Forms, Links, or a schema table (schema.Fields). Null when SQL has no such name.
    /// </summary>
    public static ViewDoc? Find(ViewBuilder views, string name, string? schemaDbPath = null)
    {
        name = name.Trim();
        if (Special.TryGetValue(name, out var sp))
        {
            var doc = new ViewDoc { Name = Special.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)), Kind = "special view", About = sp.About };
            doc.Columns.AddRange(sp.Columns.Select(c => new ViewColumnDoc(c.Name, c.Type, c.About)));
            return doc;
        }
        if (name.StartsWith("schema.", StringComparison.OrdinalIgnoreCase)) return schemaDbPath == null ? null : SchemaTable(schemaDbPath, name[7..]);
        var v = views.Find(name);
        return v == null ? null : Of(views, v);
    }

    /// <summary>The doc of one view of the schema's record types.</summary>
    public static ViewDoc Of(ViewBuilder views, ViewSpec v)
    {
        var cat = views.Catalog(v.Root);
        string kind = v.IsList ? (v.Key.StartsWith("flags:", StringComparison.Ordinal) ? "flags view" : "list view") : v.IsUnion ? "union view" : "typed view";
        string about = kind switch
        {
            "union view" => $"One row per record of the {v.Types.Count} record types of one signature ({string.Join(", ", v.Types)}), their shared fields as columns and Type to tell them apart.",
            "typed view" => $"One row per {v.Name} record of the scope, one column per plain field. Lists are list views (below); a field that isn't a column is read from Json.",
            "flags view" => $"One row per flag set in {v.Root}.{v.Lists[0].Path}: Value is the flag's name.",
            _ => $"One row per entry of {v.Root}.{string.Join("[*].", v.Lists.Select(l => l.Path))}[*]{(v.Lists.Count > 1 ? " (a list inside a list: Idx, then Idx2)" : "")}; join it to {v.Root} on FormKey (and Plugin when several plugins are read).",
        };
        string? related = v.IsList ? v.Root : views.UnionNames.Keys.FirstOrDefault(u => !u.Equals(v.Name, StringComparison.OrdinalIgnoreCase) && (views.TypesOf(u) ?? Array.Empty<string>()).Contains(v.Types[0]));
        var doc = new ViewDoc { Name = v.Name, Kind = kind, About = about, Related = related, Types = v.Types };
        foreach (string f in v.FixedColumns)
        {
            var (t, a) = Fixed.GetValueOrDefault(f, ("", ""));
            if (v.IsList && f == "Plugin") a = "the plugin of the record the list is in";
            if (v.IsUnion && f == "Type") a = $"which of the {v.Types.Count} record types the row is ({string.Join(", ", v.Types)})";
            doc.Columns.Add(new ViewColumnDoc(f, t, a));
            if (f == "IsOverride") { doc.Columns.Add(new ViewColumnDoc("LoadOrder", Fixed["LoadOrder"].Type, Fixed["LoadOrder"].About)); doc.Columns.Add(new ViewColumnDoc("IsWinner", Fixed["IsWinner"].Type, Fixed["IsWinner"].About)); }
        }
        foreach (var c in v.Columns)
            doc.Columns.Add(kind == "flags view" ? new ViewColumnDoc(c.Name, "text", $"the name of one flag that is set ({AboutOf(c)})") : new ViewColumnDoc(c.Name, TypeOf(c), AboutOf(c)));
        if (!v.IsList || !v.ScalarEntries)
            doc.Columns.Add(new ViewColumnDoc("Json", "JSON", v.IsList ? "the entry as Mutagen writes it; read a part with Json ->> '$.Path'" : Fixed["Json"].About));
        // A typed view: its lists; a list view: the lists inside its entries
        if (!v.IsList) doc.ListViews.AddRange(cat.Lists.Keys.OrderBy(n => n, StringComparer.Ordinal));
        else doc.ListViews.AddRange(cat.Lists.Values.Where(l => l.Lists.Count > v.Lists.Count && l.Key.StartsWith(v.Key + "|", StringComparison.Ordinal)).Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal));
        return doc;
    }

    /// <summary>For a record type: its typed view, its list views by their exact names (the lists of the record
    /// itself, and how many more sit inside their entries), and the union view it is in.</summary>
    public static (ViewSpec Typed, IReadOnlyList<string> Lists, int Inside, string? Union)? OfType(ViewBuilder views, string type)
    {
        if (views.Find(type) is not { IsList: false } v) return null;
        var cat = views.Catalog(v.Root);
        string? union = views.UnionNames.Keys.FirstOrDefault(u => !u.Equals(v.Name, StringComparison.OrdinalIgnoreCase) && (views.TypesOf(u) ?? Array.Empty<string>()).Contains(v.Types[0]));
        var top = cat.Lists.Values.Where(l => l.Lists.Count == 1).Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return (v, top, cat.Lists.Count - top.Count, union);
    }

    static string TypeOf(ColumnSpec c) => c.Kind switch
    {
        "FormLink" => c.LinkTo != null ? $"FormKey → {c.LinkTo}" : "FormKey",
        "String" when c.Expr.Contains("sfs_text(", StringComparison.Ordinal) => "text (translated)",
        "String" or "ObjectType" or "Enum" => "text",
        "Bool" => "integer 0/1",
        "Flags" => "JSON list of flag names",
        "Json" or "Array2DRow" => "JSON",
        "Float" or "Double" or "Percent" or "Half" => "real",
        var k when k.StartsWith("Int", StringComparison.Ordinal) || k.StartsWith("UInt", StringComparison.Ordinal) || k is "Byte" or "SByte" or "Short" or "UShort" or "Long" or "ULong" => "integer",
        var k => k,
    };

    static string AboutOf(ColumnSpec c)
    {
        if (c.Kind == "ObjectType") return "the kind of object at that place (Mutagen's class); the columns after it that belong to one kind are NULL for the others";
        var parts = new List<string>();
        // xEdit's name is a path (\Leveled List Entries\Leveled List Entry\LVLO - Base Data\Level): its last part
        string name = c.XEdit is { } x ? x[(x.LastIndexOf('\\') + 1)..] : c.Readable ?? "";
        if (name.Length > 0) parts.Add(c.XEdit != null ? $"xEdit: {name}" : name);
        if (c.Subrecord != null) parts.Add(c.Subrecord);
        string path = c.SchemaPath;
        parts.Add("field " + path);
        if (c.Kind == "String" && c.Expr.Contains("sfs_text(", StringComparison.Ordinal)) parts.Add("in the query's language; text(Col, 'ja') gives another");
        if (c.Kind == "Json") parts.Add("an object too deep for columns of its own");
        return string.Join("; ", parts);
    }

    // ------------------------------------------------------------------ The schema's tables
    static readonly Dictionary<string, string> TableAbout = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fields"] = "Every field of every record type, one row per path (the rows describe <type> shows).",
        ["RecordTypes"] = "Every record type: signature, Mutagen name and counts.",
        ["Enums"] = "Every enum's members, with their values and xEdit's names.",
        ["Objects"] = "Every Mutagen object (record types and the objects inside them).",
        ["ObjectFields"] = "The fields of each object, before they are spread over record types (Fields has them per type).",
        ["ObjectVariants"] = "The kinds of object a base can be (a condition's 612 kinds of data).",
        ["ObjectAncestors"] = "Each object's bases, nearest first.",
        ["ObjectTargets"] = "Which objects a reference to another object can hold.",
        ["LinkInterfaces"] = "Which record types a link interface (a link that takes several types) accepts.",
        ["PairNames"] = "xEdit's name for a field, by signature and Mutagen path.",
        ["Pairs"] = "How each xEdit field was matched to a Mutagen field, and how many records agreed.",
        ["UnknownEvidence"] = "What is known from real plugins about fields Mutagen has no real name for.",
        ["Meta"] = "The schema's version, the Mutagen version it was made from, and the like.",
    };

    static readonly Dictionary<string, string> ColumnAbout = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sig"] = "the record signature (WEAP)", ["Type"] = "the record type (Mutagen's name)", ["Path"] = "the field's path ([*] for each list)",
        ["Object"] = "the Mutagen object the field is defined in", ["Ord"] = "the field's place in its object", ["Name"] = "the name (Mutagen's)",
        ["Kind"] = "what the value is: FormLink, String, Float, Enum, List, Object, …", ["Subrecord"] = "the subrecord it is read from (WDMG)",
        ["Offset"] = "where in the subrecord it starts, in bytes", ["OffsetRel"] = "where it starts after the field before it", ["Size"] = "its size in bytes",
        ["StartsSubrecord"] = "1 when it starts a subrecord of its own", ["Nullable"] = "1 when it may be absent", ["InList"] = "1 when it sits inside a list",
        ["RefObject"] = "the object it holds (for an object or a list of objects)", ["LinkTo"] = "what a link may point at (a record type, a base or an interface)",
        ["Enum"] = "the enum of its values", ["DefaultValue"] = "its default value", ["Translated"] = "1 for translated text", ["Unknown"] = "1 when Mutagen has no real name for it",
        ["Custom"] = "1 when hand-written Mutagen code reads it", ["Property"] = "1 when it is a property of the object (a column)", ["Readable"] = "the name spaced out for people",
        ["XEdit"] = "xEdit's name for it", ["XEditPairs"] = "how many xEdit fields were matched to it", ["Unit"] = "its unit, where known", ["Note"] = "a note on it",
        ["Evidence"] = "what real plugins show of it", ["Depth"] = "how deep in the record it is", ["PlainPath"] = "the path without kinds of object", ["Sort"] = "the order describe shows",
        ["SigMap"] = "the subrecord for each kind of object", ["GenericArg"] = "the type a generic field is made for", ["ClrType"] = "the compiled .NET type",
        ["Member"] = "the member's name", ["Value"] = "its value", ["IsFlags"] = "1 when the enum is flags", ["XEditName"] = "xEdit's name for it", ["Key"] = "the setting's name",
        ["File"] = "the Mutagen file it is defined in", ["Fields"] = "how many fields", ["Paths"] = "how many paths", ["Named"] = "how many have xEdit's name",
        ["Base"] = "the object it is based on", ["Abstract"] = "1 for a base no record is of", ["ObjType"] = "Record, Subrecord or Object", ["Struct"] = "1 for a plain struct",
        ["FixedSize"] = "its size when it is always the same", ["Variants"] = "how many kinds of it there are", ["Variant"] = "one kind of the base", ["Level"] = "1 for the nearest base",
        ["Ancestor"] = "a base of the object", ["Ref"] = "the object a reference names", ["Idx"] = "its place", ["Target"] = "an object it can hold", ["Marker"] = "the subrecord that marks it",
        ["Interface"] = "the link interface", ["XEditPath"] = "xEdit's path for it", ["Id"] = "a number for the row", ["MutagenPath"] = "Mutagen's path for it", ["Method"] = "how it was matched",
        ["Checked"] = "records checked", ["Agree"] = "records that agree", ["Disagree"] = "records that don't", ["Translation"] = "how a value is turned from one form to the other",
        ["Match"] = "how it matched (NULL: it didn't)", ["Part"] = "the part of the value", ["SchemaSubrecord"] = "the subrecord the schema has", ["Reason"] = "why it didn't match",
        ["Detail"] = "more on it", ["Field"] = "the field", ["RecordClass"] = "the record class", ["XmlType"] = "its type in Mutagen's XML", ["Kinds"] = "the kinds of value seen",
        ["RecordsWithField"] = "records that have it", ["RecordsNonDefault"] = "records where it isn't its default", ["Instances"] = "how often it occurs",
        ["InstancesNonDefault"] = "how often it isn't its default", ["DistinctValues"] = "how many values it takes", ["RecordsNonDefaultOfficial"] = "the same in the official plugins",
        ["RecordTypes.Unknown"] = "how many of its fields have no real name in Mutagen", ["RecordTypes.Fields"] = "how many fields it has", ["Enums.Value"] = "the member's number",
        ["Meta.Value"] = "the setting's value", ["ObjectTargets.Idx"] = "the target's place in the list", ["UnknownEvidence.Field"] = "the field without a real name",
        ["NeverSet"] = "values never seen", ["Priority"] = "how much naming it matters", ["TopValues"] = "its most common values", ["Matched"] = "1 when it was matched to a field",
    };

    /// <summary>The names of the schema's tables, as SQL names them (schema.Fields).</summary>
    public static List<string> SchemaTables(string schemaDbPath)
    {
        using var con = new SqliteConnection($"Data Source={schemaDbPath};Mode=ReadOnly;Pooling=False");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite%' ORDER BY name";
        var names = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add("schema." + r.GetString(0));
        return names;
    }

    static ViewDoc? SchemaTable(string schemaDbPath, string table)
    {
        using var con = new SqliteConnection($"Data Source={schemaDbPath};Mode=ReadOnly;Pooling=False");
        con.Open();
        string? real;
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') AND name = $n COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$n", table);
            real = cmd.ExecuteScalar() as string;
        }
        if (real == null) return null;
        var doc = new ViewDoc { Name = "schema." + real, Kind = "schema table", About = TableAbout.GetValueOrDefault(real, "A table of the schema (read only, the same for every query).") };
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"SELECT name, type FROM pragma_table_info({SqlText.Literal(real)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string col = r.GetString(0), type = r.IsDBNull(1) ? "" : r.GetString(1);
                doc.Columns.Add(new ViewColumnDoc(col, type.Length == 0 ? "any" : type.ToLowerInvariant(), ColumnAbout.GetValueOrDefault(real + "." + col) ?? ColumnAbout.GetValueOrDefault(col, "")));
            }
        }
        return doc;
    }
}
