using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SFSearch.Service;

namespace SFSearch.ServiceTest;

/// <summary>
/// `settingstext --exe &lt;SFSearch.exe&gt;` (J43, 2026-11-15; the user: "Code in options with explanations of option plus
/// what default does?"). A service started on a fresh home writes sfsearch.json; every setting SFSearch reads from it
/// must be explained there, and `Service config` must say each one's value in effect and where it came from. The list
/// of settings is taken from ServiceConfig itself (every public property the file can set), so a setting added later
/// without its text fails here. Its own home and test Data folder (one small plugin Mutagen writes). Checks:
///   1. the written file has a line for every setting and none it doesn't read, and reads back as the defaults;
///   2. above each setting's line: what it does ("key: ..."), its values ("Values:") and its default ("Default:"), with
///      what the default comes to on this PC for the settings worked out from the PC, and its environment variable;
///   3. Service config has a row for every setting with its value in effect and its source: one set in the file, one by
///      its environment variable, one outside its range (and what it is read as), the default, this PC (the numbers
///      PcDefaults gives for this PC), and the three settings outside the file (home, Data folder, agent).
/// Stops its service at the end.
/// </summary>
public static partial class T
{
    /// <summary>Every setting sfsearch.json can hold: ServiceConfig's public properties with a setter, less those the file never holds.</summary>
    static List<string> SettingKeys() => typeof(ServiceConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite && p.SetMethod!.IsPublic && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
        .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]).ToList();

    /// <summary>The settings whose default is worked out from the PC: their text must say what it comes to here.</summary>
    static readonly string[] FromThePc = { "workers", "workerRecycleMB", "workerMaxMB", "serviceMaxMB", "memoryFloorMB", "workerGarbageCollector", "pluginsTxt", "champollion" };

    static async Task SettingsTextTest()
    {
        if (!File.Exists(Exe)) { Check("settingstext: --exe names SFSearch.exe", false, Exe); return; }
        string root = Path.Combine(Home, "settingstext"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins"), txt = Path.Combine(root, "plugins.txt");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("settingstext: the test Data folder is outside the game's", false, data); return; }
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        WriteMaster(data, small: false);
        File.WriteAllLines(txt, new[] { "*" + KMaster });
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt, ["SFSEARCH_WORKERS"] = "2" };
        string cfgPath = Path.Combine(home, "sfsearch.json");
        string outer = Home;
        Home = home;
        try
        {
            var started = await RunWith(env, "Service", "start");
            if (!started.Out.Contains("Service started")) { Check("settingstext: the service starts on a fresh home", false, started.Out + started.Err); return; }
            string text = File.ReadAllText(cfgPath).Replace("\r\n", "\n");
            var keys = SettingKeys();

            // 1. A line for every setting, none other; it reads back as the defaults
            var lines = text.Split('\n');
            var keyLines = lines.Select((l, i) => (Line: l, At: i, M: Regex.Match(l, @"^\s*""(\w+)""\s*:"))).Where(x => x.M.Success).ToList();
            var written = keyLines.Select(x => x.M.Groups[1].Value).ToList();
            var missing = keys.Except(written, StringComparer.OrdinalIgnoreCase).ToList();
            var extra = written.Except(keys, StringComparer.OrdinalIgnoreCase).ToList();
            var back = System.Text.Json.JsonSerializer.Deserialize<ServiceConfig>(text, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
            var fresh = new ServiceConfig { SettingsVersion = 2 };
            var differ = typeof(ServiceConfig).GetProperties().Where(p => keys.Contains(char.ToLowerInvariant(p.Name[0]) + p.Name[1..]))
                .Where(p => !Equals(p.GetValue(back) is string s1 && s1.Length == 0 ? null : p.GetValue(back), p.GetValue(fresh) is string s2 && s2.Length == 0 ? null : p.GetValue(fresh))).Select(p => p.Name).ToList();
            Check($"settingstext: the written sfsearch.json has a line for each of the {keys.Count} settings and none other, and reads back as the defaults",
                missing.Count == 0 && extra.Count == 0 && differ.Count == 0,
                $"missing: {string.Join(", ", missing)}; not read by SFSearch: {string.Join(", ", extra)}; not the default: {string.Join(", ", differ)}");

            // 2. Above each line: what, values, default (and this PC's number, the environment variable)
            var poor = new List<string>();
            int from = 0;
            foreach (var (line, at, m) in keyLines)
            {
                string key = m.Groups[1].Value;
                string comment = string.Join(" ", lines[from..at].Select(l => l.Trim()).Where(l => l.StartsWith("//")).Select(l => l[2..].Trim()));
                from = at + 1;
                var why = new List<string>();
                if (!Regex.IsMatch(comment, $@"\b{key}: \w")) why.Add("what it does");
                if (!Regex.IsMatch(comment, @"\bValues: \S")) why.Add("Values:");
                if (!Regex.IsMatch(comment, @"\bDefault: \S")) why.Add("Default:");
                if (FromThePc.Contains(key) && !comment.Contains("On this PC: ")) why.Add("On this PC:");
                var envName = Regex.Match(comment, @"Environment variable: (SFSEARCH_\w+)");
                if (envName.Success && !KnownSettingVariables.Contains(envName.Groups[1].Value)) why.Add("an environment variable SFSearch doesn't read: " + envName.Groups[1].Value);
                if (why.Count > 0) poor.Add($"{key} ({string.Join(", ", why)})");
            }
            var f = PcDefaultsHere();
            string flat = string.Join(" ", lines.Select(l => l.Trim()).Where(l => l.StartsWith("//")).Select(l => l[2..].Trim()));
            bool numbers = flat.Contains($"On this PC: {f.Workers} workers.") && flat.Contains($"On this PC: {f.WorkerMaxMB:N0} MB") && flat.Contains($"On this PC: {f.ServiceMaxMB:N0} MB") && flat.Contains($"On this PC: {f.MemoryFloorMB:N0} MB");
            Check("settingstext: every setting is explained above its line: what it does, its values, its default, this PC's number for a default worked out from the PC",
                poor.Count == 0 && numbers, poor.Count > 0 ? "missing: " + string.Join("; ", poor) : $"this PC's numbers in the text: {numbers} ({f.Workers} workers, workerMaxMB {f.WorkerMaxMB}, serviceMaxMB {f.ServiceMaxMB}, memoryFloorMB {f.MemoryFloorMB})");

            // 3. Service config: each setting's value in effect and its source
            await RunWith(env, "Service", "stop");
            File.WriteAllText(cfgPath, Regex.Replace(Regex.Replace(Regex.Replace(File.ReadAllText(cfgPath), @"(?m)^\s*""heavySpareMB"".*\r?\n", ""), @"""jobTimeoutMinutes"":\s*\d+", "\"jobTimeoutMinutes\": 12"), @"""pluginCheckSeconds"":\s*\d+", "\"pluginCheckSeconds\": 5"));
            var withEnv = new Dictionary<string, string>(env) { ["SFSEARCH_QUEUE_LIMIT"] = "77", ["SFSEARCH_AGENT"] = "j43-test" };
            var cfg = (await RunWith(withEnv, "Service", "config")).Out;
            var rows = cfg.Replace("\r", "").Split('\n').Select(l => Regex.Match(l, @"^\| (\S[^|]*?) \| ([^|]*?) \| ([^|]*?) \|$")).Where(m => m.Success)
                .ToDictionary(m => m.Groups[1].Value.Split(' ')[0], m => (Value: m.Groups[2].Value, From: m.Groups[3].Value), StringComparer.OrdinalIgnoreCase);
            var noRow = keys.Where(k => !rows.ContainsKey(k) || rows[k].Value.Length == 0 || rows[k].From.Length == 0).ToList();
            Check($"settingstext: Service config has a row for each of the {keys.Count} settings with its value in effect and where it came from", noRow.Count == 0, "no row or no value: " + string.Join(", ", noRow));
            (string Value, string From) R(string k) => rows.TryGetValue(k, out var r) ? r : ("", "");
            var expect = new (string Key, bool Ok)[]
            {
                ("jobTimeoutMinutes 12 set in sfsearch.json", R("jobTimeoutMinutes") is ("12 min", "set in sfsearch.json")),
                ("queueLimit 77 set by SFSEARCH_QUEUE_LIMIT", R("queueLimit") is ("77", "set by SFSEARCH_QUEUE_LIMIT")),
                ("pluginCheckSeconds 5 read as 10", R("pluginCheckSeconds").Value.StartsWith("10 s") && R("pluginCheckSeconds").From == "sfsearch.json has 5, read as this"),
                ("heavyMinMB the default", R("heavyMinMB") is ("2,048 MB (2 GB)", "set in sfsearch.json (the default)")),
                ("heavySpareMB the default, its line deleted", R("heavySpareMB") is ("4,096 MB (4 GB)", "the default")),
                ($"workers 2 set by SFSEARCH_WORKERS", R("workers") is ("2", "set by SFSEARCH_WORKERS")),
                ($"workerMaxMB {f.WorkerMaxMB} from this PC", R("workerMaxMB").Value.StartsWith($"{f.WorkerMaxMB:N0} MB") && R("workerMaxMB").From.StartsWith("from this PC: ")),
                ($"serviceMaxMB {f.ServiceMaxMB} from this PC", R("serviceMaxMB").Value.StartsWith($"{f.ServiceMaxMB:N0} MB") && R("serviceMaxMB").From.StartsWith("from this PC: ")),
                ("pluginsTxt set by SFSEARCH_PLUGINS_TXT", R("pluginsTxt").From == "set by SFSEARCH_PLUGINS_TXT" && R("pluginsTxt").Value == txt),
                ("SFSEARCH_HOME set", R("SFSEARCH_HOME").From == "set by SFSEARCH_HOME" && R("SFSEARCH_HOME").Value.Equals(home, StringComparison.OrdinalIgnoreCase)),
                ("SFSEARCH_DATA set", R("SFSEARCH_DATA").From == "set by SFSEARCH_DATA" && R("SFSEARCH_DATA").Value == data),
                ("SFSEARCH_AGENT set", R("SFSEARCH_AGENT") is ("j43-test", "set by SFSEARCH_AGENT")),
            };
            Check("settingstext: Service config says where each came from: sfsearch.json, an environment variable, read as the nearest end, the default, this PC, and the three outside the file",
                expect.All(e => e.Ok), "wrong: " + string.Join("; ", expect.Where(e => !e.Ok).Select(e => $"{e.Key} (got {R(e.Key.Split(' ')[0]).Value} / {R(e.Key.Split(' ')[0]).From})")));
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }

    /// <summary>The environment variables that override a setting of sfsearch.json (ServiceConfig reads them).</summary>
    static readonly HashSet<string> KnownSettingVariables = new()
    {
        "SFSEARCH_WORKERS", "SFSEARCH_JOB_MINUTES", "SFSEARCH_WORKER_RECYCLE_MB", "SFSEARCH_WORKER_MAX_MB", "SFSEARCH_WORKER_JOBS", "SFSEARCH_SERVICE_MAX_MB",
        "SFSEARCH_MEMORY_FLOOR_MB", "SFSEARCH_HEAVY_MIN_MB", "SFSEARCH_HEAVY_SPARE_MB", "SFSEARCH_HEAVY_JOB_MINUTES", "SFSEARCH_QUEUE_LIMIT", "SFSEARCH_CHAMPOLLION", "SFSEARCH_PLUGINS_TXT",
    };

    /// <summary>What PcDefaults gives on this PC with every memory setting at 0.</summary>
    static (int Workers, int WorkerMaxMB, int ServiceMaxMB, int MemoryFloorMB) PcDefaultsHere()
    {
        long total = MemoryInfo.TotalMB();
        int budget = ServiceConfig.PcDefaults.ServiceMaxMB(total);
        return (ServiceConfig.PcDefaults.Workers(budget, MemoryInfo.Cores()), ServiceConfig.PcDefaults.WorkerMaxMB(budget), budget, ServiceConfig.PcDefaults.MemoryFloorMB(total));
    }
}
