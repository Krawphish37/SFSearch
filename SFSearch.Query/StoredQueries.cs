// The stored queries (milestone M10, ARCHITECTURE.md section 8): find, list, show, check, run and save.
//
//   var stored = new StoredQueries(engine, new StoredOptions { ShippedFolder = …, UserFolder = … });
//   StoredAnswer a = stored.Run(new StoredRunRequest { Name = "flst", Arguments = { ["plugin"] = "Starfield.esm" } });
//   Console.Write(a.ToText());
//
// A stored query runs through QueryEngine.Run like any SQL, so it gets the same answer object and the same checks:
// names, cost, the reason for an empty answer. Its parameters are bound as values. The files are read from disk on
// every call: nothing about them is kept.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SFSearch.Index;

namespace SFSearch.Query;

public sealed class StoredOptions
{
    /// <summary>The queries that ship with SFSearch (<c>queries\</c> of the repo). Their source is <c>shipped</c>.</summary>
    public string? ShippedFolder { get; init; }
    /// <summary>The user's own (<c>&lt;home&gt;\queries\</c>). Their source is <c>user</c>. "save" writes here.</summary>
    public string? UserFolder { get; init; }
    /// <summary>Folders that hold add-ons. An add-on <c>X.dll</c> brings its queries in a folder <c>X.queries\</c>
    /// beside it. Their source is <c>X</c>.</summary>
    public IReadOnlyList<string> AddOnFolders { get; init; } = Array.Empty<string>();
}

public sealed class StoredRunRequest
{
    /// <summary>The query's name, or its full name (<c>shipped.flst</c>) when the name is in several places.</summary>
    public required string Name { get; init; }
    /// <summary>The arguments, by parameter name.</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>();
    /// <summary>The scope. Null: the one the query's header names.</summary>
    public ScopeKind? Scope { get; init; }
    /// <summary>The plugins of a plugins scope. Empty: the query's <c>plugin</c> argument.</summary>
    public IReadOnlyList<string> Plugins { get; init; } = Array.Empty<string>();
    public bool AllowHeavy { get; init; }
    public int PageRows { get; init; } = 40;
    /// <summary>The cursor a former answer gave for one of its tables.</summary>
    public string? Cursor { get; init; }
    public int MaxChars { get; init; } = 24000;
    public string? Language { get; init; }
    public CancellationToken Cancel { get; init; }
}

/// <summary>One table of a stored query's answer: a SELECT of the file, with its heading.</summary>
public sealed record StoredTable(int Number, string? Heading, QueryAnswer Answer);

public sealed class StoredAnswer
{
    /// <summary>The query that was run, with its source: never left unsaid.</summary>
    public StoredQuery? Query { get; set; }
    public ScopeKind Scope { get; set; }
    public List<string> Plugins { get; } = new();
    /// <summary>The arguments as they were bound, defaults included.</summary>
    public Dictionary<string, object?> Arguments { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StoredTable> Tables { get; } = new();
    /// <summary>Notes about the run as a whole (record types left out for their cost, a name found in two places).</summary>
    public List<string> Notes { get; } = new();
    public string? Error { get; set; }
    public string? Fix { get; set; }
    public HeavyEstimate? NotRun { get; set; }
    public bool Ok => Error == null && NotRun == null;
    public long TotalRows => Tables.Sum(t => t.Answer.TotalRows);
    public double Seconds => Tables.Sum(t => t.Answer.Cost.Seconds);

    string QueryLine()
    {
        if (Query == null) return "";
        var args = Arguments.Where(a => a.Value != null).Select(a => $"{a.Key}={Convert.ToString(a.Value, CultureInfo.InvariantCulture)}");
        string scope = Scope == ScopeKind.Plugins ? $"plugins {string.Join(", ", Plugins)}" : Scope.ToString().ToLowerInvariant();
        return $"[query] {Query.FullName} ({scope}{string.Concat(args.Select(a => "; " + a))}): {Query.Answers}\n";
    }

    /// <summary>The answer as text under the output contract: which query ran, then each table with its own notes, source and cost.</summary>
    public string ToText(int maxChars = 24000)
    {
        var sb = new StringBuilder();
        sb.Append(QueryLine());
        if (Error != null)
        {
            sb.Append("Error: ").Append(Error.TrimEnd('.'));
            if (!Error.EndsWith('?')) sb.Append('.');
            if (Fix != null) sb.Append(" Fix: ").Append(Fix.TrimEnd('.')).Append('.');
            sb.Append('\n');
        }
        else if (NotRun != null) sb.Append("Not run. ").Append(NotRun.What).Append('\n');
        else
        {
            int each = Math.Max(2000, maxChars / Math.Max(1, Tables.Count));
            foreach (var t in Tables)
            {
                if (Tables.Count > 1 || t.Heading != null) sb.Append("## ").Append(t.Heading ?? $"Table {t.Number}").Append('\n');
                string text = t.Answer.ToText(each);
                // A table's cursor says which table it belongs to
                if (t.Answer.Cursor != null) text = text.Replace($"cursor \"{t.Answer.Cursor}\"", $"cursor \"t{t.Number}:{t.Answer.Cursor}\"");
                sb.Append(text);
            }
        }
        foreach (string n in Notes) sb.Append("[notes] ").Append(n).Append('\n');
        return sb.ToString();
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (Query != null)
            o["query"] = new JsonObject
            {
                ["name"] = Query.FullName, ["source"] = Query.Source, ["file"] = Query.File, ["answers"] = Query.Answers,
                ["scope"] = Scope.ToString().ToLowerInvariant(),
                ["plugins"] = new JsonArray(Plugins.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["arguments"] = new JsonObject(Arguments.Select(a => new KeyValuePair<string, JsonNode?>(a.Key, a.Value switch
                {
                    null => null, long l => JsonValue.Create(l), double d => JsonValue.Create(d), _ => JsonValue.Create(a.Value.ToString()),
                }))),
            };
        if (Error != null) o["error"] = new JsonObject { ["what"] = Error, ["fix"] = Fix };
        if (NotRun != null) o["notRun"] = new JsonObject { ["what"] = NotRun.What, ["seconds"] = Math.Round(NotRun.Seconds, 1), ["memoryMB"] = NotRun.MemoryMB, ["allow"] = "allow_heavy=true" };
        var tables = new JsonArray();
        foreach (var t in Tables)
        {
            var j = t.Answer.ToJson();
            j["heading"] = t.Heading;
            j["table"] = t.Number;
            if (j["more"] is JsonObject more && t.Answer.Cursor != null) more["cursor"] = $"t{t.Number}:{more["cursor"]}";
            tables.Add(j);
        }
        o["tables"] = tables;
        o["notes"] = new JsonArray(Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        return o;
    }

    public string ToJsonText() => ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>What a check of one stored query found.</summary>
public sealed record StoredCheck(StoredQuery Query, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

public sealed record StoredSaved(bool Ok, string? File, IReadOnlyList<string> Problems, string? Note);

public sealed class StoredQueries
{
    public const string Shipped = "shipped", User = "user";

    readonly QueryEngine _engine;
    readonly StoredOptions _options;

    public StoredQueries(QueryEngine engine, StoredOptions options)
    {
        _engine = engine; _options = options;
    }

    public StoredOptions Options => _options;

    // ------------------------------------------------------------------ Finding them
    /// <summary>Every stored query there is, read from disk now: the shipped ones, the user's, each add-on's.</summary>
    public List<StoredQuery> All()
    {
        var all = new List<StoredQuery>();
        void Folder(string? folder, string source)
        {
            if (folder == null || !Directory.Exists(folder)) return;
            foreach (string file in Directory.EnumerateFiles(folder, "*.sql").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                all.Add(StoredQuery.Parse(text, source, file, Path.GetFileName(file)));
            }
        }
        Folder(_options.ShippedFolder, Shipped);
        Folder(_options.UserFolder, User);
        foreach (string addOns in _options.AddOnFolders)
        {
            if (!Directory.Exists(addOns)) continue;
            foreach (string dir in Directory.EnumerateDirectories(addOns, "*.queries").OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string addOn = Path.GetFileName(dir)[..^".queries".Length];
                if (addOn.Length == 0 || !File.Exists(Path.Combine(addOns, addOn + ".dll"))) continue;
                Folder(dir, addOn);
            }
        }
        return all;
    }

    /// <summary>
    /// One query by its name or its full name (<c>source.name</c>). A name that is in several places is not
    /// guessed at: the error lists the full names. A name that is nowhere gets the nearest real ones.
    /// </summary>
    public StoredQuery Find(string name) => Find(name, All());

    StoredQuery Find(string name, List<StoredQuery> all)
    {
        name = name.Trim();
        var byFull = all.Where(q => q.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byFull.Count == 1) return byFull[0];
        var byName = all.Where(q => q.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
            throw new QueryException($"the stored query \"{name}\" is in {byName.Count} places: {string.Join(", ", byName.Select(q => q.FullName))}",
                $"ask for one by its full name, for example {byName[0].FullName}");
        if (all.Count == 0)
            throw new QueryException("there are no stored queries: no folder with .sql files was found",
                $"the shipped ones are looked for in {_options.ShippedFolder ?? "(no folder given)"}, the user's in {_options.UserFolder ?? "(no home given)"}");
        var near = Nearest.Pick(name, all, q => new[] { q.Name, q.FullName }, 4, always: false);
        throw new QueryException($"there is no stored query named \"{name}\"." + SFSearch.SchemaGen.NearNames.DidYouMean(near.Select(q => q.FullName).Distinct().ToList()), "list them all with: saved list");
    }

    /// <summary>The list, as the answer of "saved list".</summary>
    public string ListText()
    {
        var all = All();
        var sb = new StringBuilder();
        if (all.Count == 0)
        {
            sb.Append($"No stored queries. Shipped ones are looked for in {_options.ShippedFolder ?? "(no folder given)"}, the user's in {_options.UserFolder ?? "(no home given)"}.\n");
            return sb.ToString();
        }
        var twice = all.GroupBy(q => q.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        sb.Append("| Name | From | Scope | Parameters | Answers |\n|---|---|---|---|---|\n");
        foreach (var q in all.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase).ThenBy(q => q.Source, StringComparer.OrdinalIgnoreCase))
        {
            string pars = string.Join(", ", q.Params.Select(p => p.Needed ? p.Name : $"[{p.Name}]"));
            sb.Append("| ").Append(twice.Contains(q.Name) ? q.FullName : q.Name).Append(" | ").Append(q.Source).Append(" | ").Append((q.Scope?.ToString().ToLowerInvariant() ?? "?") + (q.WithMasters ? " + masters" : ""))
                .Append(" | ").Append(pars).Append(" | ").Append(q.Answers.Replace("|", "\\|")).Append(q.Problems.Count > 0 ? $" (FAULTY HEADER: {q.Problems[0]})" : "").Append(" |\n");
        }
        sb.Append($"{all.Count:N0} stored queries ({string.Join(", ", all.GroupBy(q => q.Source).Select(g => $"{g.Count():N0} {g.Key}"))}). A parameter in [brackets] is optional.\n");
        if (twice.Count > 0) sb.Append($"[notes] {twice.Count:N0} name(s) are in more than one place and are listed by their full name ({string.Join(", ", twice.OrderBy(t => t))}): ask for them that way.\n");
        sb.Append("Show one with: saved show <name>. Run one with: saved run <name> parameter=value …\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Checking
    /// <summary>
    /// The header's own faults, then every statement compiled and run against empty tables (no plugin is read): a
    /// table or column that doesn't exist is reported with the nearest real names, and every column a SELECT gives
    /// must be described in the header.
    /// </summary>
    public StoredCheck Check(StoredQuery q)
    {
        var problems = new List<string>(q.Problems);
        if (q.Scope == null || q.Statements.Count == 0) return new StoredCheck(q, problems);
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in q.Params) parameters[p.Name] = null;
        var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The statements that run depend on which parameters are given (when, unless): each case is compiled as it
        // would run: no parameter given, each parameter of a when or unless line given alone, and all of them given.
        var switches = q.Statements.SelectMany(s => s.When.Concat(s.Unless)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cases = new List<HashSet<string>> { new(StringComparer.OrdinalIgnoreCase) };
        foreach (string w in switches) cases.Add(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { w });
        if (switches.Count > 1) cases.Add(new HashSet<string>(switches, StringComparer.OrdinalIgnoreCase));
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var given in cases)
        {
            var setup = new List<string>();
            int table = 0;
            foreach (var s in q.Statements)
            {
                if (!s.Runs(given.Contains)) continue;
                if (!s.GivesRows) { setup.Add(s.Sql); continue; }
                table++;
                string sql = string.Join(";\n", setup.Append(s.Sql));
                if (!done.Add(sql)) continue;
                var a = _engine.Check(new QueryRequest { Sql = sql, Scope = q.Scope.Value, Parameters = parameters });
                if (a.Error != null)
                {
                    string with = given.Count == 0 ? "" : $" (with {string.Join(" and ", given)} given)";
                    problems.Add($"{(s.Heading != null ? $"table \"{s.Heading}\"" : $"SELECT {table}")}{with}: {a.Error.TrimEnd('.')}{(a.Error.TrimEnd('.').EndsWith('?') ? "" : ".")}{(a.Fix != null ? $" Fix: {a.Fix.TrimEnd('.')}." : "")}");
                    continue;
                }
                foreach (string c in a.Columns)
                {
                    returned.Add(c);
                    if (!q.Columns.Any(d => d.Name.Equals(c, StringComparison.OrdinalIgnoreCase)))
                        problems.Add($"the column {c} is returned and the header doesn't describe it (-- column: {c} | what it holds)");
                }
            }
        }
        // Records and Links hold only what was read: a query over them must say what to read
        bool names = q.Statements.Any(NamesReadTables);
        if (names && q.Reads == null) problems.Add("the SQL names Records or Links, which hold only the record types read so far: add a reads line (-- reads: every type, every type with <Field>, or a list of types)");
        if (!names && q.Reads != null) problems.Add("the header has a reads line, and the SQL names neither Records nor Links: name the record types' own tables and drop the line");
        if (problems.Count == q.Problems.Count)
            foreach (var d in q.Columns)
                if (!returned.Contains(d.Name)) problems.Add($"the header describes the column {d.Name}, and no SELECT returns it");
        if (q.Reads?.WithField is { } field && TypesWithField(field).Count == 0)
            problems.Add($"the reads line names the field {field}, and no record type has a field of that name");
        foreach (string t in q.Reads?.Types ?? Array.Empty<string>())
            if (RootOfType(t) == null) problems.Add($"the reads line names {t}, which isn't a record type.{NearTypes(t)}");
        return new StoredCheck(q, problems.Distinct().ToList());
    }

    // ------------------------------------------------------------------ Saving
    /// <summary>
    /// Saves a query into the user's folder, after its header and its SQL passed the check. An existing file of the
    /// user's is replaced only when asked. A shipped query is never changed: a user's query of the same name sits
    /// beside it, and the bare name then needs its full name.
    /// </summary>
    public StoredSaved Save(string name, string text, bool overwrite = false)
    {
        if (_options.UserFolder == null)
            return new StoredSaved(false, null, new[] { "there is no user folder to save into: give the SFSearch home (the queries go to <home>\\queries\\)" }, null);
        name = name.Trim();
        if (name.StartsWith(User + ".", StringComparison.OrdinalIgnoreCase)) name = name[(User.Length + 1)..];
        if (!StoredQuery.ValidName(name))
            return new StoredSaved(false, null, new[] { $"the name \"{name}\" may hold letters, digits, - and _ only" }, null);
        string file = Path.Combine(_options.UserFolder, name + ".sql");
        var q = StoredQuery.Parse(text, User, file, name + ".sql");
        var check = Check(q);
        if (!check.Ok) return new StoredSaved(false, null, check.Problems, null);
        if (File.Exists(file) && !overwrite)
            return new StoredSaved(false, null, new[] { $"{file} is there already: save it under another name, or ask to replace it (overwrite)" }, null);
        Directory.CreateDirectory(_options.UserFolder);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, true);
        var others = All().Where(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && o.Source != User).Select(o => o.FullName).ToList();
        string? note = others.Count > 0 ? $"The name {name} is also {string.Join(", ", others)}: from now on ask for {User}.{name} or {others[0]} by its full name." : null;
        return new StoredSaved(true, file, Array.Empty<string>(), note);
    }

    // ------------------------------------------------------------------ Running
    /// <summary>Runs a stored query. It never throws for a wrong name, a wrong argument or wrong SQL: the answer says what is wrong.</summary>
    public StoredAnswer Run(StoredRunRequest request)
    {
        var answer = new StoredAnswer();
        try { RunInto(request, answer); }
        catch (QueryException e) { answer.Error = e.Message; answer.Fix = e.Fix; }
        return answer;
    }

    void RunInto(StoredRunRequest request, StoredAnswer answer)
    {
        var all = All();
        var q = Find(request.Name, all);
        answer.Query = q;
        if (q.Problems.Count > 0)
            throw new QueryException($"the stored query {q.FullName} has a faulty header: {string.Join("; ", q.Problems)}", $"correct {q.File}");
        var others = all.Where(o => o != q && o.Name.Equals(q.Name, StringComparison.OrdinalIgnoreCase)).Select(o => o.FullName).ToList();
        if (others.Count > 0) answer.Notes.Add($"This is {q.FullName}. There is also {string.Join(", ", others)}, which was not run.");

        // The arguments
        var given = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Arguments)
        {
            string k = key.Trim().TrimStart(':');
            var p = q.Param(k);
            if (p == null)
                throw new QueryException($"{q.FullName} has no parameter \"{k}\"",
                    q.Params.Count == 0 ? "it takes no parameters" : $"its parameters are: {string.Join("; ", q.Params.Select(x => $"{x.Name} ({x.What.TrimEnd('.')})"))}");
            if (!string.IsNullOrWhiteSpace(value)) given[p.Name] = value.Trim();
        }

        // The scope: the caller's, or the header's. A plugins scope takes its plugins from the caller or from the plugin argument.
        // A plugin parameter that the SQL never names chooses the plugins scope: "the copy of this plugin" in a query that reads the load order by itself
        bool pluginPicksScope = q.Param("plugin") != null && given.ContainsKey("plugin") && !q.Statements.Any(s => s.Parameters.Contains("plugin", StringComparer.OrdinalIgnoreCase));
        var scope = request.Scope ?? (pluginPicksScope ? ScopeKind.Plugins : q.Scope!.Value);
        var plugins = request.Plugins.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (scope == ScopeKind.Plugins && plugins.Count == 0 && given.TryGetValue("plugin", out var named))
            plugins = named.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (scope == ScopeKind.Plugins && plugins.Count == 0)
            throw new QueryException($"{q.FullName} reads the plugins it is given, and none was named", "name one or more: plugin=Starfield.esm (several with commas), or ask for the winners or the versions scope");
        if (scope == ScopeKind.Plugins && !given.ContainsKey("plugin") && q.Param("plugin") != null) given["plugin"] = string.Join(",", plugins);
        var index = _engine.Records.Index;
        index.Refresh();
        if (scope == ScopeKind.Plugins && q.WithMasters)
        {
            // One plugin, read together with its masters: the SQL keeps to the plugin with Plugin = :plugin
            if (plugins.Count != 1) throw new QueryException($"{q.FullName} reads one plugin together with its masters, and {plugins.Count} were named", "name one plugin");
            if (index.Plugin(plugins[0]) is { Header: not null } one)
            {
                plugins[0] = one.Name; given["plugin"] = one.Name;
                foreach (string m in one.Header.Masters)
                    if (index.Plugin(m) is { } master && !plugins.Contains(master.Name, StringComparer.OrdinalIgnoreCase)) plugins.Add(master.Name);
            }
        }
        answer.Scope = scope;
        if (scope == ScopeKind.Plugins) answer.Plugins.AddRange(plugins);
        if (request.Scope is { } asked && asked != q.Scope) answer.Notes.Add($"Run in the {asked.ToString().ToLowerInvariant()} scope, as asked; the query's own scope is {q.Scope.ToString()!.ToLowerInvariant()}.");

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in q.Params)
        {
            string? raw = given.GetValueOrDefault(p.Name) ?? p.Default;
            if (raw == null)
            {
                // The plugin of a plugins query isn't needed when the caller asked for the load order instead
                bool scopeParam = p.Name.Equals("plugin", StringComparison.OrdinalIgnoreCase) && q.Scope == ScopeKind.Plugins && scope != ScopeKind.Plugins && !q.WithMasters;
                if (p.Needed && !scopeParam) throw new QueryException($"{q.FullName} needs the parameter {p.Name}: {p.What.TrimEnd('.')}", $"add {p.Name}=…{(q.Examples.Count > 0 ? $" (example: {q.Name} {q.Examples[0]})" : "")}");
                values[p.Name] = null;
                continue;
            }
            values[p.Name] = Convert(q, p, raw);
        }
        foreach (var p in q.Params) answer.Arguments[p.Name] = p.Kind == StoredParamKind.Pattern ? given.GetValueOrDefault(p.Name) ?? p.Default : values[p.Name];

        // Each SELECT is a table of the answer. The statements that only make temporary tables run before each.
        bool Given(string name) => values.GetValueOrDefault(name) != null;
        // What must be read first, when a statement that runs names Records or Links
        // (NamesReadTables is below)
        string? preRead = PreRead(q, scope, plugins, given, values, answer, q.Statements.Any(s => s.Runs(Given) && NamesReadTables(s)));
        int? only = null; string? cursor = null;
        if (!string.IsNullOrWhiteSpace(request.Cursor))
        {
            var cm = Regex.Match(request.Cursor.Trim().Trim('"'), @"^t(\d+):(.+)$");
            if (!cm.Success) throw new QueryException($"the cursor \"{request.Cursor}\" isn't one of a stored query's tables", "pass the cursor as the former answer gave it (t1:…)");
            only = int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture); cursor = cm.Groups[2].Value;
        }
        // The reads are a request of their own: the statements after it then find their types read, and an empty
        // answer's note stays short. What the reads took is added to the first table's cost.
        QueryAnswer? reads = null;
        if (preRead != null)
        {
            reads = _engine.Run(new QueryRequest { Sql = preRead, Scope = scope, Plugins = plugins, AllowHeavy = request.AllowHeavy, Language = request.Language, Cancel = request.Cancel, NoAutoReads = true });
            if (reads.Error != null) { answer.Error = $"the stored query {q.FullName} failed while reading the record types it needs: {reads.Error}"; answer.Fix = reads.Fix; return; }
            if (reads.NotRun != null) { answer.NotRun = reads.NotRun; return; }
        }
        var setup = new List<string>();
        int number = 0;
        foreach (var s in q.Statements)
        {
            if (!s.Runs(Given)) continue;
            if (!s.GivesRows) { setup.Add(s.Sql); continue; }
            number++;
            if (only != null && only != number) continue;
            var a = _engine.Run(new QueryRequest
            {
                Sql = string.Join(";\n", setup.Append(s.Sql)), Scope = scope, Plugins = plugins, AllowHeavy = request.AllowHeavy, PageRows = request.PageRows,
                Cursor = cursor, MaxChars = request.MaxChars, Language = request.Language, Cancel = request.Cancel, Parameters = values,
                NoAutoReads = true,
            });
            if (a.Error != null)
            {
                answer.Tables.Clear();
                answer.Error = $"the stored query {q.FullName} failed{(s.Heading != null ? $" in its table \"{s.Heading}\"" : "")}: {a.Error}";
                answer.Fix = a.Fix;
                return;
            }
            if (a.NotRun != null) { answer.Tables.Clear(); answer.NotRun = a.NotRun; return; }
            if (reads != null)
            {
                if (answer.Tables.Count == 0)
                {
                    a.Cost.Read.InsertRange(0, reads.Cost.Read);
                    a.Cost.Seconds += reads.Cost.Seconds;
                    foreach (string n in reads.Notes) if (!a.Notes.Contains(n)) a.Notes.Add(n);
                    // The types the reads found already there are said once, not for every one of them
                    a.Cost.AlreadyRead.Clear();
                    if (reads.Cost.AlreadyRead.Count > 0) a.Cost.AlreadyRead.Add($"{reads.Cost.AlreadyRead.Count:N0} record type(s)");
                }
                else a.Cost.AlreadyRead.Clear();
                // An empty answer over Records or Links: the engine's hint (name a type's table) doesn't fit a stored query
                for (int i = 0; i < a.Notes.Count; i++)
                    if (a.Notes[i].StartsWith("No rows: Records holds only", StringComparison.Ordinal) || a.Notes[i].StartsWith("No rows: Links holds only", StringComparison.Ordinal))
                        a.Notes[i] = $"No rows: nothing fits among the {_readRoots.Count:N0} record type(s) read for this query ({string.Join(", ", _readRoots.Take(8))}{(_readRoots.Count > 8 ? ", …" : "")}).";
                    else if (a.Notes[i].StartsWith("Links holds the links of the record types read so far", StringComparison.Ordinal))
                        a.Notes[i] = $"The links of {_readRoots.Count:N0} record type(s) of this scope were searched ({string.Join(", ", _readRoots.Take(8))}{(_readRoots.Count > 8 ? ", …" : "")}), and of any type read before.";
            }
            answer.Tables.Add(new StoredTable(number, s.Heading, a));
        }
        if (number == 0) throw new QueryException($"{q.FullName} has no table for these arguments", "see what it takes with: saved show " + q.Name);
        if (only != null && answer.Tables.Count == 0) throw new QueryException($"the cursor names table {only}, and {q.FullName} gave {number} table(s) for these arguments", "pass the cursor with the same arguments that gave it");
    }

    static object Convert(StoredQuery q, StoredParam p, string raw)
    {
        switch (p.Kind)
        {
            case StoredParamKind.Number:
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                throw new QueryException($"{q.FullName}: the parameter {p.Name} is a number, and \"{raw}\" isn't one", $"{p.Name}: {p.What}");
            case StoredParamKind.Pattern:
                return LikePattern(raw);
            default:
                // A FormKey written with fewer digits, or in lower case, is the same record
                var fk = Regex.Match(raw, @"^([0-9A-Fa-f]{1,8}):(.+\.es[mpl])$", RegexOptions.IgnoreCase);
                if (fk.Success && p.Name.Equals("record", StringComparison.OrdinalIgnoreCase))
                {
                    string id = fk.Groups[1].Value.ToUpperInvariant().PadLeft(6, '0');
                    return (id.Length > 6 ? id[^6..] : id) + ":" + fk.Groups[2].Value;
                }
                return raw;
        }
    }

    /// <summary>A pattern argument as the right side of <c>LIKE … ESCAPE '\'</c>: it matches anywhere in the value,
    /// <c>*</c> stands for any text and <c>?</c> for one character; <c>%</c> and <c>_</c> are plain characters.</summary>
    public static string LikePattern(string pattern)
    {
        var sb = new StringBuilder("%");
        foreach (char c in pattern)
            sb.Append(c switch { '\\' => "\\\\", '%' => "\\%", '_' => "\\_", '*' => "%", '?' => "_", _ => c.ToString() });
        return sb.Append('%').ToString();
    }

    static Regex PatternRegex(string pattern) =>
        new(string.Concat(pattern.Select(c => c switch { '*' => ".*", '?' => ".", _ => Regex.Escape(c.ToString()) })), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    // ------------------------------------------------------------------ What is read first
    /// <summary>True when a statement reads from Records or Links (the name stands after FROM or JOIN; a column
    /// that happens to be called Records doesn't count).</summary>
    static bool NamesReadTables(StoredStatement s)
    {
        var toks = SqlText.Tokenize(s.Sql);
        for (int i = 1; i < toks.Count; i++)
            if (toks[i].IsName && (toks[i].Text.Equals("Records", StringComparison.OrdinalIgnoreCase) || toks[i].Text.Equals("Links", StringComparison.OrdinalIgnoreCase))
                && (toks[i - 1].Is("FROM") || toks[i - 1].Is("JOIN"))
                && !(i + 1 < toks.Count && toks[i + 1].IsPunct("(")))
                return true;
        return false;
    }

    string? RootOfType(string type)
    {
        if (_engine.Views.Find(type) is { IsList: false } v) return v.Root;
        string sig = type.ToUpperInvariant();
        return _engine.Records.Schema.TypesOf(sig).Count > 0 ? _engine.LabelOf(sig) : null;
    }

    /// <summary>The record types nearest to a wrong name, as " Did you mean: A; B?" (empty when none is near).</summary>
    string NearTypes(string type) => SFSearch.SchemaGen.NearNames.DidYouMean(Nearest.Pick(type, _engine.Views.RootNames, r => new[] { r }, 4, always: false));

    IReadOnlyList<string> SigsOfRoot(string root) =>
        (_engine.Views.TypesOf(root) ?? Array.Empty<string>()).Select(t => _engine.Records.Schema.SignatureOf(t)!).Distinct(StringComparer.Ordinal).ToList();

    List<string> TypesWithField(string field)
    {
        string first = field.Split('.')[0];
        var schema = _engine.Records.Schema;
        return _engine.Views.RootNames.Where(root => (_engine.Views.TypesOf(root) ?? Array.Empty<string>()).Any(t => schema.Prop(t, first) != null)).ToList();
    }

    /// <summary>
    /// A query over Records or Links sees only the record types read so far, so its header says what must be read
    /// (<c>-- reads:</c>). This makes the statement that has those types read, and narrows them where it can:
    /// to the type the caller named (<c>type</c>), to the types of the record asked for (<c>record</c>, found in the
    /// header index), to the types that have a record whose EditorID fits the pattern. A type that is over the cost
    /// limit by itself is left out, and the answer says so, unless the caller named it.
    /// </summary>
    /// <summary>The record types the last run had read for its query, for the note of an empty answer. One caller at a time, like the engine.</summary>
    List<string> _readRoots = new();

    string? PreRead(StoredQuery q, ScopeKind scope, List<string> plugins, Dictionary<string, string> given, Dictionary<string, object?> values, StoredAnswer answer, bool needed)
    {
        var index = _engine.Records.Index;
        // The type argument is bound as the record signature, whatever way it was written
        string? namedRoot = null;
        if (q.Param("type") != null && given.TryGetValue("type", out var typeArg))
        {
            namedRoot = RootOfType(typeArg) ?? throw new QueryException($"{q.FullName}: \"{typeArg}\" isn't a record type.{NearTypes(typeArg)}",
                "name a record type (Weapon), a signature (WEAP), or leave type out");
            var sigs = SigsOfRoot(namedRoot);
            values["type"] = sigs.Count > 0 ? sigs[0] : typeArg.ToUpperInvariant();
            answer.Arguments["type"] = values["type"];
        }
        _readRoots = new List<string>();
        if (q.Reads == null || !needed) return null;

        var members = scope == ScopeKind.Plugins
            ? plugins.Select(index.Plugin).Where(p => p?.Segment != null).Select(p => p!).ToList()
            : index.Plugins.Where(p => p.Order.Enabled && p.Segment != null).ToList();
        // A plugin that isn't there is the engine's to report, with the nearest name
        if (scope == ScopeKind.Plugins && members.Count < plugins.Count) return null;

        List<string> roots;
        if (namedRoot != null) roots = new List<string> { namedRoot };
        else if (q.Reads.WithField != null) roots = TypesWithField(q.Reads.WithField);
        else if (q.Reads.Every) roots = _engine.Views.RootNames.ToList();
        else roots = q.Reads.Types.Select(t => RootOfType(t) ?? throw new QueryException($"the stored query {q.FullName} reads {t}, which isn't a record type.{NearTypes(t)}", $"correct the reads line of {q.File}")).ToList();

        // One name for each signature: the member types of a union view (WeaponModification, ArmorModification) are read as one
        roots = roots.Select(r => SigsOfRoot(r) is { Count: > 0 } sigs ? _engine.LabelOf(sigs[0]) : r).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Only what the scope has records of
        var have = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in members)
            foreach (var s in p.Segment!.Signatures) have[s.Signature] = have.GetValueOrDefault(s.Signature) + s.Count;
        roots = roots.Where(r => SigsOfRoot(r).Any(s => have.GetValueOrDefault(s) > 0)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // The record asked for, or the pattern: only the types that have such a record
        HashSet<string>? sigsWanted = null;
        if (namedRoot == null && q.Param("record") != null && given.ContainsKey("record") && values["record"] is string record)
        {
            sigsWanted = new HashSet<string>(StringComparer.Ordinal);
            var refs = new List<RecordRef>();
            if (HeaderIndex.TryParseFormKey(record, out _, out _)) { if (index.Versions(record) is { } v) refs.AddRange(v.Holders); }
            else refs.AddRange(index.FindEditorId(record));
            foreach (var r in refs)
                if (members.Contains(index.Plugins[r.Plugin])) sigsWanted.Add(index.Describe(r).Signature);
            if (sigsWanted.Count == 0)
                answer.Notes.Add($"The header index has no record with the EditorID or FormKey \"{record}\" in {(scope == ScopeKind.Plugins ? string.Join(", ", plugins) : "the loaded plugins")}.");
        }
        else if (namedRoot == null && q.Params.FirstOrDefault(p => p.Kind == StoredParamKind.Pattern && given.ContainsKey(p.Name)) is { } pp)
        {
            sigsWanted = new HashSet<string>(StringComparer.Ordinal);
            var rx = PatternRegex(given[pp.Name]);
            var wantedSigs = roots.SelectMany(SigsOfRoot).ToHashSet(StringComparer.Ordinal);
            foreach (var p in members)
            {
                var seg = p.Segment!;
                var open = seg.Signatures.Select(s => wantedSigs.Contains(s.Signature) && !sigsWanted.Contains(s.Signature)).ToArray();
                if (!open.Any(o => o)) continue;
                var entries = seg.Entries;
                for (int i = 0; i < entries.Length; i++)
                {
                    int sig = entries[i].Signature;
                    if (!open[sig] || !entries[i].HasEditorId) continue;
                    if (seg.EditorId(i) is { } id && rx.IsMatch(id)) { sigsWanted.Add(seg.Signatures[sig].Signature); open[sig] = false; }
                }
            }
        }
        if (sigsWanted != null) roots = roots.Where(r => SigsOfRoot(r).Any(sigsWanted.Contains)).ToList();

        // A type that is over the limit by itself isn't read unless it was named
        var tooBig = new List<string>();
        if (namedRoot == null)
        {
            var keep = new List<string>();
            foreach (string root in roots)
            {
                var est = Estimate.None; long records = 0;
                foreach (string sig in SigsOfRoot(root))
                    foreach (var p in members)
                    {
                        var load = CostModel.Load(p, sig);
                        if (load.Count == 0) continue;
                        est += CostModel.Full(load); records += load.Count;
                    }
                if (est.Seconds > _engine.Options.HeavySeconds || est.MemoryMB > _engine.Options.HeavyMemoryMB) tooBig.Add($"{root} ({records:N0} records)");
                else keep.Add(root);
            }
            roots = keep;
        }
        if (tooBig.Count > 0)
            answer.Notes.Add($"Not read, because each is over the cost limit by itself: {string.Join(", ", tooBig)}. Their records are not in this answer. To read one, name it (type=…) and allow heavy work.");
        _readRoots = roots.OrderBy(r => r, StringComparer.Ordinal).ToList();
        if (roots.Count == 0) return null;
        // One statement that names each type: the engine reads what a statement names. It gives no rows.
        return "CREATE TEMP TABLE sfs_stored_reads AS SELECT 0 AS n FROM Records WHERE 0"
            + string.Concat(roots.OrderBy(r => r, StringComparer.Ordinal).Select(r => $" UNION ALL SELECT 0 FROM {SqlText.Name(r)} WHERE 0"));
    }
}
