// The Conditions view (GAPS S44): every condition (CTDA) of the records read so far in a scope, one row each, with its
// function by index and name, its parameters, comparison, value and run-on. "Which condition functions do the plugins
// use, and how often" needed a Python scan of the raw CTDA subrecords; now it is SQL:
//   SELECT Function, FunctionName, count(*) FROM Conditions GROUP BY 1, 2 ORDER BY 3 DESC
//
// The rows come from the records' own JSON (Mutagen's): every object whose MutagenObjectType is ConditionFloat or
// ConditionGlobal, wherever it sits (Conditions, Stages[*].LogEntries[*].Conditions, Components[*].Conditions, …).
// They are made the first time a statement names Conditions, for the record types read so far (CondRows, beside
// LinkRows), and a statement that narrows it (WHERE Type = 'Perk', FormKey = '…', EditorID = '…') or narrows to
// nothing has the types it needs read first, as Records and Links do: here, only the types whose records can hold a
// condition (by the schema).
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

/// <summary>The Conditions view's columns, and one condition's JSON taken apart into them.</summary>
static class ConditionRows
{
    public static readonly string[] Columns =
    {
        "FormKey", "Type", "EditorID", "Plugin", "Field", "Idx", "Path", "Function", "FunctionName", "RunOn", "Reference",
        "Comparison", "Value", "Global", "Flags", "FlagsRaw", "Param1", "Param2", "Param3", "Json",
    };

    /// <summary>The columns whose values are JSON, given as JSON through the view (GAPS S80).</summary>
    public static readonly HashSet<string> JsonColumns = new(StringComparer.Ordinal) { "Flags", "Json" };

    /// <summary>The columns the view makes from another one, by their expression over CondRows (alias r): the flags as
    /// their number, as a typed view's FlagsRaw (GAPS S168).</summary>
    public static readonly Dictionary<string, string> Computed = new(StringComparer.Ordinal) { ["FlagsRaw"] = "sfs_flagbits(r.Flags, 'Condition.Flag')" };

    /// <summary>What one condition data class (GetLevelConditionData) says: its function index and name, and the
    /// fields that are its first and second parameters.</summary>
    internal sealed record DataClass(int? Function, string Name, FieldDef? First, FieldDef? Second);
}

public sealed partial class QueryEngine
{
    HashSet<string>? _conditionSigs;
    readonly Dictionary<string, ConditionRows.DataClass> _dataClasses = new(StringComparer.Ordinal);
    Dictionary<long, string>? _functionNames;

    static readonly HashSet<string> ConditionObjects = new(StringComparer.Ordinal) { "Condition", "ConditionFloat", "ConditionGlobal" };

    /// <summary>The signatures whose records can hold a condition, by the schema: a field of any of their types, at any
    /// depth, holds a Condition.</summary>
    internal HashSet<string> ConditionSigs
    {
        get
        {
            lock (_dataClasses)
            {
                if (_conditionSigs != null) return _conditionSigs;
                var model = _schema.Model;
                var sigs = new HashSet<string>(StringComparer.Ordinal);
                foreach (string sig in _schema.Signatures)
                    foreach (string type in _schema.TypesOf(sig))
                    {
                        if (sigs.Contains(sig)) break;
                        var seen = new HashSet<FieldDef>();
                        foreach (var row in model.Expand(type))
                            if (seen.Add(row.Def) && row.Def.RefObject is { } o && ConditionObjects.Contains(o)) { sigs.Add(sig); break; }
                    }
                return _conditionSigs = sigs;
            }
        }
    }

    /// <summary>A condition function's name by its index (Mutagen's Condition.Function), or null.</summary>
    internal string? FunctionName(long index)
    {
        lock (_dataClasses)
        {
            _functionNames ??= (_schema.Enum("Condition.Function") ?? (IReadOnlyList<EnumMember>)Array.Empty<EnumMember>())
                .GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.First().Name);
            return _functionNames.GetValueOrDefault(index);
        }
    }

    internal ConditionRows.DataClass DataClassOf(string cls)
    {
        lock (_dataClasses)
        {
            if (_dataClasses.TryGetValue(cls, out var have)) return have;
            var model = _schema.Model;
            int? fn = model.Objects.TryGetValue(cls, out var def) && int.TryParse(def.ConditionFunction, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) ? f : null;
            // The parameters are the class's own fields in file order, but those Mutagen names unused: a CTDA has two
            // parameter slots, each a 4-byte value (CTDA) and a string (CIS1, CIS2), and one of the two is unused
            // (GetVMQuestVariable's second parameter is the string; GetIsVoiceType's first is VoiceTypeOrList). As
            // Mutagen's own Parameter1 and Parameter2 (StringParameter1, StringParameter2 for a string).
            var own = model.Shallow(cls).Select(r => r.Def).Where(d => d.Object == cls && !d.Name.Contains("Unused", StringComparison.Ordinal)).OrderBy(d => d.Ord).ToList();
            string name = fn is int i && FunctionName(i) is { } n ? n : cls.EndsWith("ConditionData", StringComparison.Ordinal) ? cls[..^"ConditionData".Length] : cls;
            return _dataClasses[cls] = new ConditionRows.DataClass(fn, name, own.ElementAtOrDefault(0), own.ElementAtOrDefault(1));
        }
    }

    FieldDef? _parameterThree;
    /// <summary>ConditionData.ParameterThree, whose default (-1) a condition that leaves it out has.</summary>
    internal FieldDef? ParameterThree
    {
        get { lock (_dataClasses) return _parameterThree ??= _schema.Model.Shallow("ConditionData").Select(r => r.Def).FirstOrDefault(d => d.Name == "ParameterThree"); }
    }

    sealed partial class Session
    {
        /// <summary>Takes the conditions out of the records read so far into CondRows: the record types that can hold one
        /// and aren't done yet. SQLite's json_tree finds each condition object by its MutagenObjectType; its JSON is then
        /// taken apart here.</summary>
        void FillConditions(SqliteConnection w, Dictionary<string, DoneRow> done)
        {
            var clock = Stopwatch.StartNew();
            var sigs = done.Values.Where(d => !d.CondDone && d.Sig != FormsDone).Select(d => d.Sig).ToList();
            if (sigs.Count == 0) return;
            var holding = sigs.Where(_e.ConditionSigs.Contains).ToList();
            long rows = 0;
            using var tx = w.BeginTransaction();
            if (holding.Count > 0)
            {
                using var ins = new BulkInsert(w, "INSERT INTO CondRows(FormKey, Type, EditorID, Plugin, LoadOrder, IsWinner, Field, Idx, Path, Function, FunctionName, RunOn, Reference, Comparison, Value, Global, Flags, Param1, Param2, Param3, Json) "
                    + "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)");
                using var read = w.CreateCommand();
                // The atom MutagenObjectType of the condition object: its parent (t.path) is the condition
                read.CommandText = "SELECT r.FormKey, r.Type, r.EditorID, r.Plugin, r.LoadOrder, r.IsWinner, t.path, json_extract(r.Json, t.path) FROM Rec r, json_tree(r.Json) t "
                    + $"WHERE r.Sig IN ({string.Join(", ", holding.Select(SqlText.Literal))}) AND t.key = 'MutagenObjectType' AND t.atom IN ('ConditionFloat', 'ConditionGlobal') ORDER BY r.Seq, t.id";
                read.CommandTimeout = 0;
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    _req.Cancel.ThrowIfCancellationRequested();
                    string path = JsonPathToField(r.GetString(6));
                    var (field, idx) = FieldOf(path);
                    var v = Take(r.GetString(7));
                    ins.Text(1, r.GetString(0)); ins.Text(2, r.GetString(1)); ins.Text(3, r.IsDBNull(2) ? null : r.GetString(2));
                    ins.Text(4, r.IsDBNull(3) ? null : r.GetString(3)); ins.Int(5, r.IsDBNull(4) ? null : r.GetInt64(4)); ins.Int(6, r.IsDBNull(5) ? null : r.GetInt64(5));
                    ins.Text(7, field); ins.Int(8, idx); ins.Text(9, path);
                    ins.Int(10, v.Function); ins.Text(11, v.FunctionName); ins.Text(12, v.RunOn); ins.Text(13, v.Reference); ins.Text(14, v.Comparison);
                    ins.Value(15, v.Value); ins.Text(16, v.Global); ins.Text(17, v.Flags);
                    ins.Value(18, v.Param1); ins.Value(19, v.Param2); ins.Value(20, v.Param3); ins.Text(21, v.Json);
                    ins.Step();
                    rows++;
                }
            }
            ScratchFile.Exec(w, $"UPDATE Done SET CondDone = 1 WHERE Sig IN ({string.Join(", ", sigs.Select(SqlText.Literal))})");
            tx.Commit();
            _answer.Cost.Read.Add($"Conditions ({rows:N0}) of {holding.Count:N0} record type(s) taken out in {Took(clock.Elapsed.TotalSeconds)}");
        }

        /// <summary>json_tree's path ($.Stages[2].LogEntries[0].Conditions[1]) as the views write a place
        /// (Stages[2].LogEntries[0].Conditions[1]).</summary>
        static string JsonPathToField(string jsonPath)
        {
            string p = jsonPath.StartsWith("$", StringComparison.Ordinal) ? jsonPath[1..] : jsonPath;
            if (p.StartsWith('.')) p = p[1..];
            return p.Replace("\"", "", StringComparison.Ordinal);
        }

        /// <summary>The field of a place, every list position written [*] and the last one dropped (Conditions,
        /// Stages[*].LogEntries[*].Conditions), and the innermost list position: as the Links view writes them.</summary>
        static (string Field, int? Idx) FieldOf(string path)
        {
            var (field, idx) = LinkWalker.Lines(System.Text.Encoding.UTF8.GetBytes(path + "\t0\n")).Select(l => (l.Field, l.Idx)).First();
            return (field, idx);
        }

        sealed record CondValues(long? Function, string? FunctionName, string? RunOn, string? Reference, string? Comparison, object? Value, string? Global, string? Flags,
            object? Param1, object? Param2, object? Param3, string Json);

        CondValues Take(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var c = doc.RootElement;
            string kind = Str(c, "MutagenObjectType") ?? "";
            JsonElement data = c.TryGetProperty("Data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            string cls = data.ValueKind == JsonValueKind.Object ? Str(data, "MutagenObjectType") ?? "" : "";
            var dc = cls.Length > 0 ? _e.DataClassOf(cls) : new ConditionRows.DataClass(null, "", null, null);
            long? fn = dc.Function;
            string? fnName = dc.Name.Length > 0 ? dc.Name : null;
            // A function no class claims (UnknownConditionData): the data holds its number
            if (fn == null && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("Function", out var f))
            {
                if (f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out long n)) { fn = n; fnName = _e.FunctionName(n) ?? n.ToString(CultureInfo.InvariantCulture); }
                else if (f.ValueKind == JsonValueKind.String && f.GetString() is { } s)
                {
                    fnName = s;
                    var m = _e._schema.Enum("Condition.Function")?.FirstOrDefault(x => x.Name.Equals(s, StringComparison.Ordinal));
                    fn = m?.Value ?? (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long sn) ? sn : null);
                }
            }
            string? runOn = data.ValueKind == JsonValueKind.Object ? Str(data, "RunOnType") ?? EnumDefault("Condition.RunOnType") : null;
            string? reference = data.ValueKind == JsonValueKind.Object ? Link(data, "Reference") : null;
            string? comparison = Str(c, "CompareOperator") ?? EnumDefault("CompareOperator");
            object? value = null; string? global = null;
            if (kind == "ConditionGlobal") global = Link(c, "ComparisonValue");
            else value = !c.TryGetProperty("ComparisonValue", out var cv) ? 0.0 : cv.ValueKind == JsonValueKind.Number ? cv.GetDouble() : Scalar(cv);
            string? flags = c.TryGetProperty("Flags", out var fl) ? fl.ValueKind == JsonValueKind.Array ? fl.GetRawText() : fl.ValueKind == JsonValueKind.String ? JsonSerializer.Serialize(new[] { fl.GetString() }) : fl.GetRawText() : "[]";
            object? p1 = Param(data, dc.First), p2 = Param(data, dc.Second);
            object? p3 = Param(data, _e.ParameterThree);
            return new CondValues(fn, fnName, runOn, reference, comparison, value, global, flags, p1, p2, p3, json);
        }

        object? Param(JsonElement data, FieldDef? def)
        {
            if (def == null || data.ValueKind != JsonValueKind.Object) return null;
            if (data.TryGetProperty(def.Name, out var v)) return Scalar(v);
            // Left out of the JSON: the field's default, as the views read it
            var (dflt, _) = FieldLookup.DefaultOf(_e._schema, def);
            if (dflt == null) return null;
            using var doc = JsonDocument.Parse(dflt.ToJsonString());
            return Scalar(doc.RootElement);
        }

        string? EnumDefault(string enumName) => _e._schema.Enum(enumName)?.FirstOrDefault(m => m.Value == 0)?.Name;

        static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static string? Link(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s != "Null" ? s : null;

        /// <summary>A JSON value as SQLite reads it from JSON: integers as integers, other numbers as reals, a null link
        /// ("Null") as NULL, objects and lists as their JSON text.</summary>
        static object? Scalar(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long l) ? (object)l : v.GetDouble(),
            JsonValueKind.String => v.GetString() is "Null" ? null : v.GetString(),
            JsonValueKind.True => 1L,
            JsonValueKind.False => 0L,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText(),
        };
    }
}
