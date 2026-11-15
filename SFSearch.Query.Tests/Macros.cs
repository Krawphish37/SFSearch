// The ten Q macros of today's SFSearch held against the stored queries that take their place (milestone M10).
// The recorded runs of the golden replay (tests\SFSearch.Harness\replay\commands.jsonl) have their answers in the
// M12 baseline folder; --baseline names it. Macros with no recorded run (omod, new) are run here through today's
// SFSearch.exe with a test home (--today, --today-home). Both sides are compared on what both give: the same
// records and the same values. Field names and layout differ, and each kind of difference that is left is listed
// with its reason; one that has no reason fails the test.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Index;
using SFSearch.Query;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class Macros
{
    public static string? Baseline, TodayExe, TodayHome;

    sealed record Recorded(string Id, string[] Args, string Out);

    /// <summary>One row of either side: what identifies it, and the values held against each other.</summary>
    sealed record Row(string Key, Dictionary<string, string> Values);

    static readonly Regex CutMark = new(@"…\(\+\d+\)\s*$", RegexOptions.CultureInvariant);
    static readonly Regex Annotation = new(@" \[[^\[\]]*\]", RegexOptions.CultureInvariant);
    static readonly Regex FormKeyText = new(@"^[0-9A-Fa-f]{6}:[^,\[\]]+\.es[mpl]$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ Today's answers
    static List<Recorded> ReadBaseline()
    {
        var list = new List<Recorded>();
        foreach (string file in Directory.EnumerateFiles(Baseline!, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (!root.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array) continue;
            var a = args.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (a.Length < 3 || !a[0].Equals("q", StringComparison.OrdinalIgnoreCase)) continue;
            string text = root.GetProperty("answer").TryGetProperty("out", out var o) ? o.GetString() ?? "" : "";
            list.Add(new Recorded(root.GetProperty("id").GetString() ?? Path.GetFileNameWithoutExtension(file), a, text));
        }
        return list.OrderBy(r => int.TryParse(r.Id, out int n) ? n : int.MaxValue).ToList();
    }

    static Recorded RunToday(string id, params string[] args)
    {
        var psi = new ProcessStartInfo(TodayExe!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["SFSEARCH_HOME"] = TodayHome!; psi.Environment["SFSEARCH_WARMUP"] = "0"; psi.Environment["SFSEARCH_WORKERS"] = "2";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new TestFailed($"today's SFSearch ended with code {p.ExitCode} for {string.Join(" ", args)}: {Cut(err.Result)}");
        return new Recorded(id, args, text);
    }

    /// <summary>Today's table as rows by column name: a Markdown table, or CSV when the run asked for it.</summary>
    static List<Dictionary<string, string>> Parse(Recorded r)
    {
        var rows = new List<Dictionary<string, string>>();
        var lines = r.Out.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
        bool csv = r.Args.Contains("--csv", StringComparer.OrdinalIgnoreCase);
        List<string>? header = null;
        foreach (string line in lines)
        {
            List<string> cells;
            if (csv) cells = CsvCells(line);
            else
            {
                if (!line.StartsWith("| ", StringComparison.Ordinal)) continue;
                if (line.StartsWith("| :-", StringComparison.Ordinal) || line.StartsWith("| ---", StringComparison.Ordinal)) continue;
                string inner = line[2..].TrimEnd();
                if (inner.EndsWith(" |", StringComparison.Ordinal)) inner = inner[..^2];
                cells = Regex.Split(inner, @"(?<!\\) \| ").Select(x => x.Replace("\\|", "|").Trim()).ToList();
            }
            if (header == null) { header = cells; continue; }
            if (cells.Count != header.Count) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++) row[header[i]] = cells[i];
            rows.Add(row);
        }
        return rows;
    }

    static List<string> CsvCells(string line)
    {
        var cells = new List<string>(); var sb = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; }
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    // ------------------------------------------------------------------ Making both sides the same text
    static bool IsCut(string cell) => CutMark.IsMatch(cell);
    static string Head(string cell) => CutMark.Replace(cell, "");
    /// <summary>A FormKey cell of today's output without the EditorID Print adds in brackets, in lower case.</summary>
    static string Fk(string cell) => Annotation.Replace(cell, "").Trim().Trim('"').ToLowerInvariant();

    static string EdidOf(Ctx c, string formKey)
    {
        var v = c.Index.Versions(formKey.Trim());
        if (v == null) return formKey.Trim();
        var r = v.Winner >= 0 ? v.Holders[v.Winner] : v.Holders[0];
        return c.Index.Plugins[r.Plugin].Segment!.EditorId(r.Entry) ?? formKey.Trim();
    }

    static string LabelOfType(Ctx c, string type) => c.Schema.SignatureOf(type) is { } sig ? c.Engine.LabelOf(sig) : type;

    static string Norm(string? v)
    {
        string s = (v ?? "").Trim();
        if (s.Equals("True", StringComparison.OrdinalIgnoreCase)) return "1";
        if (s.Equals("False", StringComparison.OrdinalIgnoreCase)) return "0";
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !s.Contains(':')) return Math.Round(d, 4).ToString("R", CultureInfo.InvariantCulture);
        return s.ToLowerInvariant();
    }

    static string Cell(object? v) => v switch
    {
        null => "",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    static List<Dictionary<string, string>> Mine(QueryAnswer a) =>
        a.Rows.Select(r => a.Columns.Select((col, i) => (col, Cell(r[i]))).ToDictionary(x => x.col, x => x.Item2, StringComparer.OrdinalIgnoreCase)).ToList();

    static string MeshPath(string p)
    {
        string s = p.Trim().Replace('/', '\\').ToLowerInvariant();
        return s.StartsWith("meshes\\", StringComparison.Ordinal) ? s[7..] : s;
    }

    static string FlagSet(string flags) => string.Join(",", flags.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim().ToLowerInvariant()).Where(f => f.Length > 0 && f != "0").OrderBy(f => f, StringComparer.Ordinal));

    static readonly Regex IdTag = new(@"^<ID=[0-9A-Fa-f]+>", RegexOptions.CultureInvariant);

    static void Count(Outcome o, string what) => o.Explained[what] = o.Explained.GetValueOrDefault(what) + 1;

    /// <summary>Flags as one number: names can't be added up, so only a list made of numbers alone has a value.</summary>
    static string Bits(string flags)
    {
        ulong sum = 0;
        foreach (string f in flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (f.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(f[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex)) sum |= hex;
            else if (ulong.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out ulong dec)) sum |= dec;
            else return "names:" + flags;
        }
        return sum.ToString(CultureInfo.InvariantCulture);
    }

    // The record types today's database of a plugin holds: the types of every row of today's "Q new" and "Q overrides"
    static readonly Dictionary<string, HashSet<string>?> TodayTypeCache = new(StringComparer.OrdinalIgnoreCase);
    static HashSet<string>? TodayTypes(Ctx c, string plugin)
    {
        if (TodayTypeCache.TryGetValue(plugin, out var have)) return have;
        HashSet<string>? types = null;
        // Only for a small plugin: today's SFSearch builds its database of the plugin for this
        if (TodayExe != null && TodayHome != null && c.Index.Plugin(plugin) is { } p && p.Stamp.Length < 8 << 20)
        {
            types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string macro in new[] { "new", "overrides" })
                foreach (var row in Parse(RunToday("types", "q", macro, plugin, "--csv")))
                    types.Add(row.GetValueOrDefault("Signature", ""));
        }
        return TodayTypeCache[plugin] = types;
    }

    // A record's own type, as Mutagen has it (WeaponModification, where the header index only knows OMOD)
    static readonly Dictionary<string, Dictionary<string, string>> TypeCache = new(StringComparer.OrdinalIgnoreCase);
    static string? ConcreteType(Ctx c, string plugin, string formKey)
    {
        if (!TypeCache.TryGetValue(plugin, out var types))
        {
            types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in c.Mod(plugin).EnumerateMajorRecords()) types[r.FormKey.ToString()] = Ctx.Type(r);
            TypeCache[plugin] = types;
        }
        return types.GetValueOrDefault(formKey);
    }

    // ------------------------------------------------------------------ One run
    sealed class Outcome
    {
        public string Macro = "", Id = "", Args = "", MineArgs = "";
        public int Today, Mine, Same;
        public List<string> OnlyToday = new(), OnlyMine = new();
        /// <summary>For each compared value: how many rows differ, with one example.</summary>
        public Dictionary<string, (int N, string Example)> Differs = new();
        public List<string> Remarks = new();
        /// <summary>The differences that have a reason, each with how many rows it covers.</summary>
        public Dictionary<string, int> Explained = new(StringComparer.Ordinal);
        public string? Skipped;
        /// <summary>The filter holds a star: today's macro takes it as a plain character and finds nothing, the stored query takes it as any text.</summary>
        public bool StarFilter;
    }

    static Outcome Compare(Ctx c, StoredQueries lib, Recorded r)
    {
        string macro = r.Args[1].ToLowerInvariant(), target = r.Args[2];
        string? filter = r.Args.Length > 3 && !r.Args[3].StartsWith("--", StringComparison.Ordinal) ? r.Args[3] : null;
        var o = new Outcome { Macro = macro, Id = r.Id, Args = string.Join(" ", r.Args.Skip(1)) };
        if (c.Index.Plugin(target) == null) { o.Skipped = $"{target} isn't in the Data folder now"; return o; }
        var today = Parse(r);

        // The stored query's arguments for the same question
        string args = $"plugin={target}";
        bool idFilter = filter != null && Regex.IsMatch(filter, "^[0-9A-Fa-f]{6}$");
        bool typeFilter = false;
        if (filter != null)
        {
            if (macro is "overrides" or "new")
            {
                typeFilter = c.Schema.SignatureOf(filter) != null || c.Schema.TypesOf(filter.ToUpperInvariant()).Count > 0;
                args += typeFilter ? $" type={filter}" : $" pattern={filter}";
            }
            else args += idFilter ? $" record={filter}:{target}" : $" pattern={filter}";
            if (filter.Contains('*')) { o.StarFilter = true; o.Remarks.Add("the filter holds a star: today's macro takes it as a plain character, the stored query as any text"); }
        }
        // Today's macro takes * as a plain character; a ? in the stored query's pattern stands for exactly one character, the star among them
        o.MineArgs = args;
        var answer = Stored.Run(lib, macro, args);
        if (answer.Error != null) throw new TestFailed($"{macro} {args}: {answer.Error} {answer.Fix}");
        if (answer.NotRun != null) { o.Skipped = $"not run: {answer.NotRun.What}"; return o; }

        // Today's filter also takes a record whose name or FormKey holds the text; the stored query's pattern is the EditorID alone
        string edidColumn = macro == "flst" && filter != null ? "FlstEditorID" : macro == "arma" ? "ArmorEDID" : "EditorID";
        string fkColumn = macro == "arma" ? "ArmorFormKey" : "FormKey";
        if (filter != null && !typeFilter && macro is not ("overrides" or "new"))
        {
            int before = today.Count;
            today = today.Where(t => idFilter
                ? t.TryGetValue(fkColumn, out var fk) && Fk(fk).StartsWith(filter.ToLowerInvariant() + ":" + target.ToLowerInvariant(), StringComparison.Ordinal)
                : t.GetValueOrDefault(edidColumn, "").Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (today.Count < before) o.Remarks.Add($"{before - today.Count} of today's rows matched \"{filter}\" in the record's name or FormKey only, not in its EditorID; they are left out");
        }
        if (typeFilter)
        {
            string label = LabelOfType(c, c.Schema.SignatureOf(filter!) != null ? filter! : c.Schema.TypesOf(filter!.ToUpperInvariant())[0]);
            int before = today.Count;
            today = today.Where(t => LabelOfType(c, t.GetValueOrDefault("Signature", "")) == label).ToList();
            if (today.Count < before) o.Remarks.Add($"{before - today.Count} of today's rows are of another type and matched \"{filter}\" in their EditorID; they are left out");
        }

        var (a, b) = Rows(c, macro, filter != null, today, answer, o);
        o.Today = a.Count; o.Mine = b.Count;
        var mineByKey = new Dictionary<string, Queue<Row>>(StringComparer.Ordinal);
        foreach (var row in b)
        {
            if (!mineByKey.TryGetValue(row.Key, out var q)) mineByKey[row.Key] = q = new Queue<Row>();
            q.Enqueue(row);
        }
        foreach (var t in a)
        {
            if (!mineByKey.TryGetValue(t.Key, out var q) || q.Count == 0) { o.OnlyToday.Add(t.Key); continue; }
            var m = q.Dequeue();
            o.Same++;
            foreach (var (name, tv0) in t.Values)
            {
                if (!m.Values.TryGetValue(name, out var mv0)) continue;
                string tv = tv0, mv = mv0;
                bool Equal() => IsCut(tv) ? Norm(mv).StartsWith(Norm(Head(tv)), StringComparison.Ordinal) : Norm(tv) == Norm(mv);
                if (Equal()) continue;
                // 1. Today's database writes the FormKey into EditorID for a record that has none
                if (name == "EditorID" && mv.Length == 0 && FormKeyText.IsMatch(tv.Trim()) && t.Key.StartsWith(Fk(tv), StringComparison.Ordinal)) { Count(o, "today's macro prints the FormKey as the EditorID of a record that has none"); continue; }
                // 2. Today's Name column has the Creation Kit's <ID=…> tag taken off; the typed views give the text as the plugin stores it
                if (name is "Name" or "ItemName" && IdTag.IsMatch(mv)) { mv = IdTag.Replace(mv, ""); if (Equal()) { Count(o, "the name starts with a Creation Kit tag (<ID=…>), which today's macro takes off"); continue; } }
                // 3. An item of a list is named as the game names it (the winning copy); today's macro names it as the plugin that made it does
                if (name == "ItemName" && t.Values.TryGetValue("Item", out var item) && c.Index.Versions(item) is { Winner: >= 0 } v
                    && !c.Index.Plugins[v.Holders[v.Winner].Plugin].Name.Equals(item[(item.IndexOf(':') + 1)..], StringComparison.OrdinalIgnoreCase)
                    && !c.Index.Plugins[v.Holders[v.Winner].Plugin].Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                { Count(o, $"the item's name is the winning copy's ({c.Index.Plugins[v.Holders[v.Winner].Plugin].Name}), today's macro gives the name its own plugin has"); continue; }
                // 4. A flag with no name: the same bit as a hexadecimal number here, as a decimal number today
                if (name == "Slots" && Bits(tv) == Bits(mv)) { Count(o, "a slot with no name is written as a hexadecimal number, today as a decimal one"); continue; }
                var have = o.Differs.GetValueOrDefault(name);
                o.Differs[name] = (have.N + 1, have.Example ?? $"{t.Key}: today \"{Cut(tv, 80)}\", stored query \"{Cut(mv, 80)}\"");
            }
        }
        foreach (var (key, q) in mineByKey)
            foreach (var _ in q)
            {
                // A record of a type today's database of the plugin doesn't hold at all
                string fk = key.Split('|')[0];
                if (FormKeyText.IsMatch(fk) && TodayTypes(c, target) is { } types && ConcreteType(c, target, fk) is { } type && !types.Contains(type))
                { Count(o, $"today's database of {target} holds no {type} record"); continue; }
                o.OnlyMine.Add(key);
            }
        foreach (var (what, n) in o.Explained) o.Remarks.Add($"{n} rows: {what}");
        return o;
    }

    /// <summary>Both sides of one macro as rows with the same keys and value names.</summary>
    static (List<Row> Today, List<Row> Mine) Rows(Ctx c, string macro, bool filtered, List<Dictionary<string, string>> today, StoredAnswer answer, Outcome o)
    {
        var a = new List<Row>(); var b = new List<Row>();
        static Dictionary<string, string> V(params (string, string)[] values) => values.ToDictionary(v => v.Item1, v => v.Item2);
        var mine = Mine(answer.Tables[0].Answer);
        string T(Dictionary<string, string> t, string col) => t.GetValueOrDefault(col, "");
        string EdidList(string fks) => string.Join(", ", fks.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(k => EdidOf(c, Fk(k))));
        switch (macro)
        {
            case "flst" when !filtered:
                foreach (var t in today) a.Add(new Row(Fk(T(t, "FormKey")), V(("EditorID", T(t, "EditorID")), ("Name", T(t, "Name")), ("Items", T(t, "Count")), ("IsOverride", T(t, "IsOverride")))));
                foreach (var m in mine) b.Add(new Row(Fk(m["FormKey"]), V(("EditorID", m["EditorID"]), ("Name", m["Name"]), ("Items", m["Items"]), ("IsOverride", m["IsOverride"]))));
                break;
            case "flst":
                foreach (var t in today) a.Add(new Row($"{T(t, "FlstEditorID").ToLowerInvariant()}|{T(t, "Idx")}", V(("Item", Fk(T(t, "ItemFormKey"))), ("ItemEditorID", T(t, "ItemEditorID")), ("ItemType", LabelOfType(c, T(t, "ItemSig"))), ("ItemName", T(t, "ItemName")))));
                foreach (var m in mine) b.Add(new Row($"{m["List"].ToLowerInvariant()}|{m["Idx"]}", V(("Item", Fk(m["Item"])), ("ItemEditorID", m["ItemEditorID"]), ("ItemType", m["ItemType"]), ("ItemName", m["ItemName"]))));
                break;
            case "vmad":
                foreach (var t in today)
                {
                    string value = T(t, "Value");
                    // A property with no Object and no Data in today's JSON is printed whole
                    bool whole = value.StartsWith('{') || value.StartsWith('[');
                    var v = V(("EditorID", T(t, "EditorID")), ("Type", LabelOfType(c, T(t, "Signature"))));
                    if (whole) Count(o, "today's macro prints the whole property where the plugin gives it no value; the stored query gives the default (0, false, empty)");
                    if (!whole) v["Value"] = FormKeyText.IsMatch(Annotation.Replace(Head(value), "").Trim()) ? Fk(Head(value)) + (IsCut(value) ? value[Head(value).Length..] : "") : value;
                    a.Add(new Row($"{Fk(T(t, "FormKey"))}|{T(t, "Script").ToLowerInvariant()}|{T(t, "Property").ToLowerInvariant()}", v));
                }
                int elsewhere = mine.Count(m => m["Attached"] != "record");
                if (elsewhere > 0) o.Remarks.Add($"{elsewhere} rows of the stored query are fragment or alias scripts, which today's macro doesn't list; they are left out");
                foreach (var m in mine.Where(m => m["Attached"] == "record")) b.Add(new Row($"{Fk(m["FormKey"])}|{m["Script"].ToLowerInvariant()}|{m["Property"].ToLowerInvariant()}", V(("EditorID", m["EditorID"]), ("Type", LabelOfType(c, m["Type"])), ("Value", m["Value"].ToLowerInvariant()))));
                break;
            case "appr" or "kw":
            {
                string col = macro == "appr" ? "SlotKeyword" : "Keyword", mineCol = macro == "appr" ? "Slot" : "Keyword";
                foreach (var t in today) a.Add(new Row($"{Fk(T(t, "FormKey"))}|{T(t, "Idx")}", V(("Value", Fk(T(t, col))), ("EditorID", T(t, "EditorID")), ("Type", LabelOfType(c, T(t, "Signature"))))));
                foreach (var m in mine) b.Add(new Row($"{Fk(m["FormKey"])}|{m["Idx"]}", V(("Value", Fk(m[mineCol])), ("EditorID", m["EditorID"]), ("Type", LabelOfType(c, m["Type"])))));
                break;
            }
            case "templates":
            {
                // The mods of each template come from the second table, when there is one; otherwise from the EditorIDs
                var mods = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (answer.Tables.Count > 1)
                    foreach (var m in Mine(answer.Tables[1].Answer))
                    {
                        string k = $"{Fk(m["FormKey"])}|{m["Template"]}";
                        if (!mods.TryGetValue(k, out var l)) mods[k] = l = new List<string>();
                        l.Add(Fk(m["Mod"]));
                    }
                foreach (var t in today)
                {
                    string cell = T(t, "IncludedMods");
                    string list = string.Join(", ", Head(cell).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Fk));
                    // A cut cell ends inside a FormKey: only the whole ones before it are compared
                    if (IsCut(cell)) list = (list.Contains(", ") ? list[..list.LastIndexOf(", ", StringComparison.Ordinal)] : "") + "…(+1)";
                    a.Add(new Row($"{Fk(T(t, "FormKey"))}|{T(t, "TemplateIdx")}", answer.Tables.Count > 1
                        ? V(("IsDefault", T(t, "Default")), ("Mods", list))
                        : V(("IsDefault", T(t, "Default")), ("ModEditorIDs", IsCut(cell) ? EdidList(list[..^5]) + "…(+1)" : EdidList(list)))));
                }
                foreach (var m in mine)
                {
                    string k = $"{Fk(m["FormKey"])}|{m["Template"]}";
                    b.Add(new Row(k, V(("IsDefault", m["IsDefault"]), ("Mods", string.Join(", ", mods.GetValueOrDefault(k) ?? new List<string>())), ("ModEditorIDs", m["ModEditorIDs"]))));
                }
                break;
            }
            case "cobj":
                foreach (var t in today) a.Add(new Row(Fk(T(t, "FormKey")), V(("EditorID", T(t, "EditorID")), ("Makes", Fk(T(t, "CreatedObject"))), ("Workbench", EdidOf(c, Fk(T(t, "WorkbenchKeyword")))),
                    ("Filters", T(t, "RecipeFilters")), ("Components", T(t, "Components")))));
                foreach (var m in mine) b.Add(new Row(Fk(m["FormKey"]), V(("EditorID", m["EditorID"]), ("Makes", Fk(m["MakesKey"])), ("Workbench", m["Workbench"]),
                    ("Filters", m["Filters"].Length == 0 ? "0" : m["Filters"].Split(", ").Length.ToString(CultureInfo.InvariantCulture)), ("Components", m["Needs"].Length == 0 ? "0" : m["Needs"].Split(", ").Length.ToString(CultureInfo.InvariantCulture)))));
                break;
            case "overrides" or "new":
                foreach (var t in today) a.Add(new Row(Fk(T(t, "FormKey")), V(("EditorID", T(t, "EditorID")), ("Type", LabelOfType(c, T(t, "Signature"))), ("Origin", T(t, "Origin")))));
                foreach (var m in mine) b.Add(new Row(Fk(m["FormKey"]), V(("EditorID", m["EditorID"]), ("Type", m["Type"]), ("Origin", m.GetValueOrDefault("Origin") ?? m["Plugin"]))));
                break;
            case "arma":
                foreach (var t in today) a.Add(new Row($"{Fk(T(t, "ArmorFormKey"))}|{Fk(T(t, "AddonFormKey"))}", V(("EditorID", T(t, "ArmorEDID")), ("MaleMesh", MeshPath(T(t, "MaleWorldModel"))), ("FemaleMesh", MeshPath(T(t, "FemaleWorldModel"))), ("Slots", FlagSet(T(t, "FirstPersonFlags"))))));
                foreach (var m in mine) b.Add(new Row($"{Fk(m["Armor"])}|{Fk(m["Addon"])}", V(("EditorID", m["EditorID"]), ("MaleMesh", MeshPath(m["MaleMesh"])), ("FemaleMesh", MeshPath(m["FemaleMesh"])), ("Slots", FlagSet(m["AddonFrom"].Length > 0 ? m["AddonSlots"] : m["ArmorSlots"])))));
                break;
            case "omod":
                foreach (var t in today) a.Add(new Row(Fk(T(t, "FormKey")), V(("Type", T(t, "Signature")), ("EditorID", T(t, "EditorID")), ("Name", T(t, "Name")), ("AttachPoint", T(t, "AttachPoint").Length == 0 ? "" : EdidOf(c, Fk(T(t, "AttachPoint")))),
                    ("TargetKeywords", EdidList(T(t, "TargetOmodKeywords"))), ("Includes", T(t, "Includes")), ("Properties", T(t, "Properties")), ("Mesh", MeshPath(T(t, "Mesh"))))));
                foreach (var m in mine) b.Add(new Row(Fk(m["FormKey"]), V(("Type", m["Type"]), ("EditorID", m["EditorID"]), ("Name", m["Name"]), ("AttachPoint", m["AttachPoint"]), ("TargetKeywords", m["TargetKeywords"]),
                    ("Includes", m["Includes"]), ("Properties", m["Properties"]), ("Mesh", MeshPath(m["Mesh"])))));
                break;
            default:
                throw new TestFailed($"there is no comparison for the macro {macro}");
        }
        return (a, b);
    }

    // ------------------------------------------------------------------ The test
    public static string Run(Ctx c)
    {
        if (Baseline == null || !Directory.Exists(Baseline))
            throw new TestFailed("this test needs the recorded answers of today's SFSearch: --baseline <folder> (the M12 baseline's cli\\recorded folder)");
        var lib = Stored.Lib(c);
        var runs = ReadBaseline();
        int recorded = runs.Count;
        if (TodayExe != null && TodayHome != null)
        {
            // The macros no recorded command used, and one more of each kind on a second plugin
            runs.Add(RunToday("own-1", "q", "omod", "kgcdoom.esm", "--csv"));
            runs.Add(RunToday("own-2", "q", "omod", "atom74.esm", "--csv"));
            runs.Add(RunToday("own-3", "q", "omod", "kgcdoom.esm", "Quality", "--csv"));
            runs.Add(RunToday("own-4", "q", "new", "kgcdoom.esm", "--csv"));
            runs.Add(RunToday("own-5", "q", "new", "atom74.esm", "Weapon", "--csv"));
            runs.Add(RunToday("own-6", "q", "new", "atom74.esm", "--csv"));
            runs.Add(RunToday("own-7", "q", "overrides", "kgcdoom.esm", "--csv"));
            runs.Add(RunToday("own-8", "q", "arma", "kgcdoom.esm", "--csv"));
            runs.Add(RunToday("own-9", "q", "appr", "atom74.esm", "--csv"));
            runs.Add(RunToday("own-10", "q", "kw", "atom74.esm", "--csv"));
            runs.Add(RunToday("own-11", "q", "cobj", "atom74.esm", "--csv"));
            runs.Add(RunToday("own-12", "q", "templates", "atom74.esm", "--csv"));
            runs.Add(RunToday("own-13", "q", "vmad", "kgcdoom.esm", "--csv"));
            runs.Add(RunToday("own-14", "q", "flst", "kgcdoom.esm", "--csv"));
        }
        var outcomes = new List<Outcome>();
        foreach (var r in runs)
        {
            try { outcomes.Add(Compare(c, lib, r)); }
            catch (TestFailed e) { outcomes.Add(new Outcome { Macro = r.Args[1].ToLowerInvariant(), Id = r.Id, Args = string.Join(" ", r.Args.Skip(1)), Skipped = "FAILED: " + e.Message }); }
        }

        var lines = new List<string> { "Id\tMacro\tToday's arguments\tStored query's arguments\tToday's rows\tStored query's rows\tSame records\tOnly today\tOnly the stored query\tValues that differ\tRemarks" };
        foreach (var o in outcomes)
            lines.Add($"{o.Id}\t{o.Macro}\t{o.Args}\t{o.MineArgs}\t{o.Today}\t{o.Mine}\t{o.Same}\t{o.OnlyToday.Count}\t{o.OnlyMine.Count}\t"
                + string.Join("; ", o.Differs.Select(d => $"{d.Key} in {d.Value.N} rows ({d.Value.Example})")) + "\t" + string.Join("; ", o.Remarks.Append(o.Skipped ?? "").Where(x => x.Length > 0))
                + (o.OnlyToday.Count > 0 ? $"\tonly today: {Cut(string.Join(" , ", o.OnlyToday.Take(4)), 300)}" : "") + (o.OnlyMine.Count > 0 ? $"\tonly stored: {Cut(string.Join(" , ", o.OnlyMine.Take(4)), 300)}" : ""));
        c.Save("stored-macros.tsv", lines);

        var summary = new List<string>();
        foreach (var g in outcomes.GroupBy(o => o.Macro).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ok = g.Where(o => o.Skipped == null).ToList();
            var diff = ok.SelectMany(o => o.Differs).GroupBy(d => d.Key).Select(d => $"{d.Key} {d.Sum(x => x.Value.N)}");
            summary.Add($"{g.Key}: {ok.Count} runs, {ok.Sum(o => o.Same):N0} of {ok.Sum(o => o.Today):N0} rows the same records"
                + (ok.Sum(o => o.OnlyToday.Count) + ok.Sum(o => o.OnlyMine.Count) > 0 ? $", {ok.Sum(o => o.OnlyToday.Count)} only today, {ok.Sum(o => o.OnlyMine.Count)} only the stored query" : "")
                + (diff.Any() ? $", values that differ: {string.Join(", ", diff)}" : "") + (g.Count() > ok.Count ? $", {g.Count() - ok.Count} not compared" : ""));
        }
        foreach (string s in summary) Console.WriteLine("      " + s);
        var explained = outcomes.SelectMany(o => o.Explained.Select(e => (o.Macro, What: Regex.Replace(e.Key, @"today's database of \S+ holds no \S+ record", "today's database of the plugin holds no record of that type"), N: e.Value)))
            .GroupBy(x => (x.Macro, x.What)).OrderBy(g => g.Key.Macro, StringComparer.Ordinal).Select(g => $"{g.Key.Macro}: {g.Sum(x => x.N):N0} rows: {g.Key.What}").ToList();
        foreach (string e in explained) Console.WriteLine("      explained, " + e);
        c.Save("stored-macros-explained.txt", explained);
        foreach (var o in outcomes.Where(o => o.Skipped != null)) Console.WriteLine($"      not compared: {o.Id} {o.Args}: {Cut(o.Skipped!, 200)}");
        var unexplained = Explain.Unexplained(outcomes.Where(o => o.Skipped == null || o.Skipped.StartsWith("FAILED", StringComparison.Ordinal)).ToList());
        if (unexplained.Count > 0) throw new TestFailed($"{unexplained.Count} difference(s) with no reason: {Cut(string.Join(" || ", unexplained), 2500)}");
        return $"{recorded} recorded runs and {runs.Count - recorded} run now through today's SFSearch, {outcomes.Count(o => o.Skipped == null)} compared: {string.Join("; ", summary)}";
    }

    /// <summary>The differences that are known and have a reason. Anything else fails the test.</summary>
    static class Explain
    {
        public static List<string> Unexplained(List<Outcome> outcomes)
        {
            var left = new List<string>();
            foreach (var o in outcomes)
            {
                if (o.Skipped != null) { left.Add($"{o.Id} {o.Args}: {o.Skipped}"); continue; }
                if (o.OnlyToday.Count > 0 && !Known(o.Macro, "only-today")) left.Add($"{o.Id} {o.Args}: {o.OnlyToday.Count} rows only today's macro gives ({Cut(string.Join(" , ", o.OnlyToday.Take(3)), 200)})");
                if (o.OnlyMine.Count > 0 && !o.StarFilter && !Known(o.Macro, "only-mine")) left.Add($"{o.Id} {o.Args}: {o.OnlyMine.Count} rows only the stored query gives ({Cut(string.Join(" , ", o.OnlyMine.Take(3)), 200)})");
                foreach (var d in o.Differs)
                    if (!Known(o.Macro, d.Key)) left.Add($"{o.Id} {o.Args}: {d.Key} differs in {d.Value.N} rows ({d.Value.Example})");
            }
            return left;
        }

        /// <summary>Filled in from what the comparison found; each entry is explained in design\agent-first\notes\M10.md.</summary>
        static readonly HashSet<string> KnownDifferences = new(StringComparer.Ordinal)
        {
        };

        static bool Known(string macro, string what) => KnownDifferences.Contains($"{macro}:{what}");
    }
}
