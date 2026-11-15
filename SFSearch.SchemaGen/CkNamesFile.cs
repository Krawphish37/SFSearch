// The data file every build reads the Creation Kit's names from (to-do F30, GAPS S41): inputs\ck-names-<CK version>.tsv,
// checked into the repo. `schemagen ck-import <dump.json> <inputs folder>` makes it from a dump of the CK's reflection
// registry (CkReflection.Load); nothing else reads the dump. See the SchemaGen README.
using System.Globalization;
using System.Text;

namespace SFSearch.SchemaGen;

/// <summary>
/// The CK names file. Lines starting with <c>#</c> are the header: the source (<c># source: CreationKit.exe
/// 1.16.244.0</c>) and the dump's SHA-256 (<c># dump-sha256: …</c>), then what the columns are. Then one row per class
/// or enum, field and member, tab-separated, keyed by the engine's names, in the order the schema keeps them:
/// <code>
/// type    Type  Kind  Base  DisplayName  ToolTip  Flags
/// field   Type  Ord   Field FieldType    DisplayName  ToolTip  Flags  RangeMin  RangeMax  RangeStep
/// member  Type  Ord   Member Value       DisplayName  ToolTip  Flags
/// </code>
/// An empty cell is none; <c>\e</c> is an empty text; <c>\\</c>, <c>\t</c>, <c>\n</c>, <c>\r</c> stand for a
/// backslash, tab, line feed, carriage return. Flags: <c>hidden</c>, <c>deprecated</c>, or both with a comma. Numbers
/// are invariant; a range is written in the shortest form that reads back to the same double.
/// </summary>
public static class CkNamesFile
{
    public const string Prefix = "ck-names-", Extension = ".tsv";
    const string SourceKey = "# source: ", DumpKey = "# dump-sha256: ";

    /// <summary>The file's name for a dump's source: <c>ck-names-1.16.244.0.tsv</c> for "CreationKit.exe 1.16.244.0".</summary>
    public static string FileName(CkData ck)
    {
        string version = ck.Source?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (version.Length == 0 || version == "?" || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new SchemaException($"The dump doesn't say which Creation Kit it is from (generated_from gives \"{ck.Source}\").");
        return Prefix + version + Extension;
    }

    /// <summary>Writes the file: everything <see cref="CkReflection.Load"/> keeps, nothing else.</summary>
    public static void Write(CkData ck, string file)
    {
        var w = new StringBuilder();
        var types = ck.Types.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        w.Append("# The Creation Kit's names for the engine's reflected classes, fields and enum members, for SFSearch's schema.\n");
        w.Append("# Made by `schemagen ck-import <dump.json> <inputs folder>` from a dump of the CK's reflection registry. Don't edit by hand: rerun the import.\n");
        w.Append(SourceKey).Append(ck.Source).Append('\n');
        w.Append(DumpKey).Append(ck.DumpSha256).Append('\n');
        w.Append($"# {types.Count(t => t.Kind == "Class")} classes, {types.Count(t => t.Kind != "Class")} enums, {types.Sum(t => t.Fields.Count)} fields, {types.Sum(t => t.Members.Count)} enum members.\n");
        w.Append("# type\tType\tKind\tBase\tDisplayName\tToolTip\tFlags\n");
        w.Append("# field\tType\tOrd\tField\tFieldType\tDisplayName\tToolTip\tFlags\tRangeMin\tRangeMax\tRangeStep\n");
        w.Append("# member\tType\tOrd\tMember\tValue\tDisplayName\tToolTip\tFlags\n");
        w.Append("# An empty cell is none; \\e is an empty text; \\\\ \\t \\n \\r are a backslash, tab, line feed, carriage return.\n");
        foreach (var t in types)
        {
            Row(w, "type", t.Name, t.Kind, t.Base, t.DisplayName, t.ToolTip, Flags(t.Hidden, t.Deprecated));
            foreach (var f in t.Fields)
                Row(w, "field", t.Name, Int(f.Ord), f.Name, f.FieldType, f.DisplayName, f.ToolTip, Flags(f.Hidden, f.Deprecated), Num(f.RangeMin), Num(f.RangeMax), Num(f.RangeStep));
            foreach (var m in t.Members)
                Row(w, "member", t.Name, Int(m.Ord), m.Name, m.Value.ToString(CultureInfo.InvariantCulture), m.DisplayName, m.ToolTip, Flags(m.Hidden, m.Deprecated));
        }
        File.WriteAllText(file, w.ToString(), new UTF8Encoding(false));

        static string Int(int i) => i.ToString(CultureInfo.InvariantCulture);
        static string? Num(double? d) => d?.ToString("R", CultureInfo.InvariantCulture);
        static string? Flags(bool hidden, bool deprecated) => hidden && deprecated ? "hidden,deprecated" : hidden ? "hidden" : deprecated ? "deprecated" : null;
    }

    static void Row(StringBuilder w, params string?[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) w.Append('\t');
            string? c = cells[i];
            if (c == null) continue;
            if (c.Length == 0) { w.Append(@"\e"); continue; }
            foreach (char ch in c)
                switch (ch)
                {
                    case '\\': w.Append(@"\\"); break;
                    case '\t': w.Append(@"\t"); break;
                    case '\n': w.Append(@"\n"); break;
                    case '\r': w.Append(@"\r"); break;
                    default: w.Append(ch); break;
                }
        }
        w.Append('\n');
    }

    /// <summary>Reads the file back into what <see cref="CkReflection.Load"/> gives from the dump.</summary>
    public static CkData Read(string file)
    {
        if (!File.Exists(file)) throw new SchemaException($"There is no CK names file at {file}. It is in the repo (SFSearch.SchemaGen\\inputs\\{Prefix}<CK version>{Extension}); `schemagen ck-import` makes it from a dump.");
        var ck = new CkData();
        int line = 0;
        foreach (string text in File.ReadLines(file))
        {
            line++;
            if (text.Length == 0) continue;
            if (text.StartsWith('#'))
            {
                if (text.StartsWith(SourceKey, StringComparison.Ordinal)) ck.Source = text[SourceKey.Length..].Trim();
                else if (text.StartsWith(DumpKey, StringComparison.Ordinal)) ck.DumpSha256 = text[DumpKey.Length..].Trim();
                continue;
            }
            var c = text.Split('\t').Select(Unescape).ToArray();
            switch (c[0])
            {
                case "type" when c.Length == 7:
                {
                    var t = new CkType { Name = c[1] ?? throw Bad("a type without a name"), Kind = c[2] ?? throw Bad("a type without a kind"), Base = c[3], DisplayName = c[4], ToolTip = c[5] };
                    (t.Hidden, t.Deprecated) = Flags(c[6]);
                    if (!ck.Types.TryAdd(t.Name, t)) throw Bad($"{t.Name} is listed twice");
                    break;
                }
                case "field" when c.Length == 11:
                {
                    var t = Owner();
                    var f = new CkField { Type = t.Name, Ord = (int)Whole(c[2]), Name = c[3] ?? throw Bad("a field without a name"), FieldType = c[4], DisplayName = c[5], ToolTip = c[6],
                        RangeMin = Real(c[8]), RangeMax = Real(c[9]), RangeStep = Real(c[10]) };
                    (f.Hidden, f.Deprecated) = Flags(c[7]);
                    t.Fields.Add(f);
                    break;
                }
                case "member" when c.Length == 8:
                {
                    var t = Owner();
                    var m = new CkMember { Type = t.Name, Ord = (int)Whole(c[2]), Name = c[3] ?? throw Bad("a member without a name"), Value = Whole(c[4]), DisplayName = c[5], ToolTip = c[6] };
                    (m.Hidden, m.Deprecated) = Flags(c[7]);
                    t.Members.Add(m);
                    break;
                }
                default:
                    throw Bad($"a \"{c[0]}\" row with {c.Length} cells isn't one this reader knows");
            }

            SchemaException Bad(string why) => new($"{file}, line {line}: {why}. Rerun `schemagen ck-import` rather than editing the file.");
            CkType Owner() => c[1] != null && ck.Types.TryGetValue(c[1]!, out var o) ? o : throw Bad($"a {c[0]} of \"{c[1]}\", which no type row above names");
            long Whole(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : throw Bad($"\"{s}\" isn't a whole number");
            double? Real(string? s) => s == null ? null : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : throw Bad($"\"{s}\" isn't a number");
            (bool, bool) Flags(string? s)
            {
                var parts = (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(p => p is not ("hidden" or "deprecated"))) throw Bad($"unknown flags \"{s}\"");
                return (parts.Contains("hidden"), parts.Contains("deprecated"));
            }
        }
        if (ck.Source == null || ck.Types.Count == 0)
            throw new SchemaException($"{file} has no \"{SourceKey.Trim()}\" line or no type row: it isn't a CK names file. Make it with `schemagen ck-import`.");
        return ck;
    }

    static string? Unescape(string cell)
    {
        if (cell.Length == 0) return null;
        if (cell == @"\e") return "";
        if (!cell.Contains('\\')) return cell;
        var sb = new StringBuilder(cell.Length);
        for (int i = 0; i < cell.Length; i++)
        {
            if (cell[i] != '\\' || i + 1 == cell.Length) { sb.Append(cell[i]); continue; }
            char n = cell[++i];
            sb.Append(n switch
            {
                't' => '\t', 'n' => '\n', 'r' => '\r', '\\' => '\\',
                _ => throw new SchemaException($"The escape \\{n} in \"{cell}\" isn't one the CK names file uses."),
            });
        }
        return sb.ToString();
    }
}
