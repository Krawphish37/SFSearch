using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SFSearch.Service
{
    /// <summary>
    /// Every setting of sfsearch.json explained where the user meets it (J43, 2026-11-15; the user: "Code in options with
    /// explanations of option plus what default does?"). One table, <see cref="All"/>, gives each setting's text: what it
    /// does, each value or range it takes and what that does, its default and what the default does on this PC (the
    /// memory settings and the garbage collector are worked out from the PC). From it come the comments of a new
    /// sfsearch.json (<see cref="WrittenText"/>) and `Service config`'s table of every setting in effect and where it came
    /// from (<see cref="Describe"/>). README.md's settings table says the same in its own words.
    /// </summary>
    public sealed partial class ServiceConfig
    {
        /// <summary>One setting: its key in sfsearch.json, the environment variable that overrides it (if any), and its text.</summary>
        public sealed record Setting(string Key, string? Env, string What, string Values, string Default, Func<PcFacts, string>? OnThisPc, string Json, Func<ServiceConfig, string> InEffect);

        /// <summary>What this PC's defaults come to, with every memory setting at 0.</summary>
        public sealed record PcFacts(long TotalMB, int Cores, int ServiceMaxMB, int Workers, int WorkerMaxMB, int WorkerRecycleMB, int MemoryFloorMB, string PluginsTxt, string Home)
        {
            public static PcFacts Now(string home)
            {
                long total = MemoryInfo.TotalMB();
                int cores = MemoryInfo.Cores();
                int budget = PcDefaults.ServiceMaxMB(total);
                int max = PcDefaults.WorkerMaxMB(budget);
                return new PcFacts(total, cores, budget, PcDefaults.Workers(budget, cores), max, PcDefaults.WorkerRecycleMB(max), PcDefaults.MemoryFloorMB(total), StarfieldPluginsTxt, home);
            }
            public string Pc => TotalMB > 0 ? $"{MemoryInfo.GB(TotalMB)} GB of memory and {Cores} cores" : $"an unknown amount of memory (taken as 32 GB) and {Cores} cores";
        }

        /// <summary>Starfield's own plugins.txt for the Windows user running this: %LOCALAPPDATA%\Starfield\Plugins.txt (as LoadOrder.DefaultPluginsTxt).</summary>
        public static string StarfieldPluginsTxt =>
            Path.Combine(Environment.GetEnvironmentVariable("LocalAppData") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Starfield", "Plugins.txt");

        static string MB(int mb) => mb.ToString("N0", CultureInfo.InvariantCulture) + " MB" + (mb >= 1024 ? $" ({MemoryInfo.GB(mb)} GB)" : "");

        /// <summary>Every setting of sfsearch.json, in the order the file has them.</summary>
        public static readonly IReadOnlyList<Setting> All = new Setting[]
        {
            new("settingsVersion", null,
                "the version of this file's settings, for SFSearch itself. Leave it as it is.",
                "2: every number in this file is yours. A file without it (written before 2026-11-13) may hold the fixed memory numbers older versions wrote by themselves; SFSearch takes those from this PC instead and rewrites them to 0 once.",
                "2, written with the file.", null, "2", c => c.SettingsVersion.ToString(CultureInfo.InvariantCulture)),
            new("workers", "SFSEARCH_WORKERS",
                "how many commands run at once, each in its own worker program.",
                "0, or a number from 1 up. 0: worked out from this PC: one per 2 GB of serviceMaxMB, at least 2, at most 8 and at most the PC's cores less 2. A number: that many workers (more run more commands side by side, each holding its own memory).",
                "0.", f => $"{f.Workers} workers", "0", c => c.Workers.ToString(CultureInfo.InvariantCulture)),
            new("jobTimeoutMinutes", "SFSEARCH_JOB_MINUTES",
                "a command still running after this many minutes is stopped with a clear error. A heavy command has its own limit (heavyJobTimeoutMinutes).",
                "a number of minutes from 1 up (0 or less counts as 1).",
                "10: a command may run 10 minutes.", null, "10", c => c.JobTimeoutMinutes + " min"),
            new("workerRecycleMB", "SFSEARCH_WORKER_RECYCLE_MB",
                "a worker that a command left holding more memory than this (MB) is replaced by a fresh one, so memory .NET keeps after a big command is given back.",
                "0, or a number of MB from 256 up (a smaller one counts as 256). 0: half of workerMaxMB, at least 1,024 and at most 3,072.",
                "0.", f => MB(f.WorkerRecycleMB), "0", c => MB(c.WorkerRecycleMB)),
            new("workerMaxMB", "SFSEARCH_WORKER_MAX_MB",
                "one command is stopped mid-way, with a clear error, when its worker passes this much memory (MB). A heavy command has its own allowance (heavyMinMB), and the indexer's jobs may use at least 3 GB. A SQL query (query2, saved, sfs_query) is held to the same allowance: a statement that would pass it stops a little under it with \"the query was stopped: it took this command to ... GB of memory\" and what to narrow, and big sorts, groups and temporary tables write what doesn't fit to temporary files in SFSearch\\scratch\\query\\temp (deleted when the query ends) instead of growing in memory, so they finish on any PC; one query's temporary files may take 4 times this, 4 to 32 GB: a query whose sort would keep each record's whole Json past that is answered \"Not run\" with its size before it starts (allow_heavy runs it when the drive has room), and one that writes more while it runs stops with \"its temporary files passed ... GB\". A larger number lets one command, and one query, use more memory; a number as large as this PC's memory lets them use all of it.",
                "0, or a number of MB from 512 up (a smaller one counts as 512). 0: a third of serviceMaxMB, at least 1,536 and at most 6,144 (2.7 GB on a 16 GB PC, 6 GB on a 64 GB PC).",
                "0.", f => MB(f.WorkerMaxMB), "0", c => MB(c.WorkerMaxMB)),
            new("workerRecycleCommands", "SFSEARCH_WORKER_JOBS",
                "a worker is also replaced after this many commands.",
                "a number from 1 up (0 or less counts as 1).",
                "500: a worker is replaced after 500 commands.", null, "500", c => c.WorkerRecycleCommands.ToString("N0", CultureInfo.InvariantCulture)),
            new("serviceMaxMB", "SFSEARCH_SERVICE_MAX_MB",
                "all workers together may hold this much memory (MB). Above it no new command starts until some is given back.",
                "0, or a number of MB from 1,024 up (a smaller one counts as 1,024). 0: half of this PC's memory (16 GB when it can't be read).",
                "0.", f => MB(f.ServiceMaxMB), "0", c => MB(c.ServiceMaxMB)),
            new("memoryFloorMB", "SFSEARCH_MEMORY_FLOOR_MB",
                "while commands run and Windows has less than this much memory available (MB), the indexer gives way, then the biggest command is stopped with a clear error (it can be asked again), before Windows has to page. New commands wait until Windows has this plus 512 MB (at least 2 GB).",
                "0, or a number of MB from 256 up (a smaller one counts as 256). 0: the larger of 1,536 and 5% of this PC's memory.",
                "0.", f => MB(f.MemoryFloorMB), "0", c => MB(c.MemoryFloorMB)),
            new("workerGarbageCollector", null,
                "how a command cleans up the memory Mutagen makes while it reads a plugin, which trades speed against memory. It covers every command: those the service runs (in its workers) and one run without the service (in its own process). The service itself and the MCP server always use workstation.",
                "\"server\": each core cleans up its own share, so a read on many cores goes up to two and a half times as fast, and a process holds more memory (about 0.4 to 0.7 GB more at its peak). \"workstation\": .NET's usual one, less memory, slower when a command reads on many cores. \"auto\": server in the service's workers on every PC: where one command may use less than 2.5 GB (workerMaxMB), a server worker holds .NET's memory under that limit, so it stays under it where it would have passed it; in a command run without the service, which has no such limit, server when one command may use 2.5 GB or more (workerMaxMB in effect), else workstation. Any other word counts as \"auto\". Measured, workstation against server: on a 16-core 64 GB PC every cell of Starfield.esm through the service 63 s against 26 s, refs of XMarker 11 s against 7 s; with an 8 GB PC's settings (1.5 GB a command, 8 cores) refs of XMarker 15 s against 8 s, every cell of Starfield.esm 94 s (and stopped over 1.5 GB in two runs of three) against 38 to 46 s (1.45 GB). A command run without the service where the setting picks workstation starts itself again on it (about 0.2 s more). A change applies to the next command run without the service and to workers started after it ('sfs.cmd Service restart' for all of them).",
                "\"auto\": server in the workers on every PC; a command run without the service server on a PC of about 15 GB or more (2.7 GB a command on a 16 GB PC), workstation below (an 8 GB PC allows 1.5 GB a command). The user's decisions: server for the workers (J44: \"Go with the switch over you suggested for J44.\"), and on every PC with the heap held under a command's limit below 2.5 GB, a command without the service keeping the 2.5 GB rule (J57: \"Yes, accept it\").",
                f => $"server in the workers; {(f.WorkerMaxMB >= ServerGcMinWorkerMB ? "server" : "workstation")} without the service (one command may use {MemoryInfo.GB(f.WorkerMaxMB)} GB)",
                "\"auto\"", c => $"{(c.ServerGcWorkers ? "server" : "workstation")} in the workers, {(c.ServerGcPlain ? "server" : "workstation")} without the service (\"{c.WorkerGarbageCollector}\")"),
            new("heavyMinMB", "SFSEARCH_HEAVY_MIN_MB",
                "a command that estimates it needs this much memory (MB) or more is heavy: heavy commands run one at a time, and each may hold more than workerMaxMB (up to its estimate and half again).",
                "a number of MB from 1 up (0 or less counts as 1). Smaller: more commands are treated as heavy and wait for memory; larger: fewer.",
                "2048: a command estimated at 2 GB or more is heavy.", null, "2048", c => MB(c.HeavyMinMB)),
            new("heavySpareMB", "SFSEARCH_HEAVY_SPARE_MB",
                "a heavy command starts only when Windows has its estimate plus this much available (MB). If not, it fails at once with a clear line: it never waits, and never starts and makes the PC page.",
                "a number of MB from 0 up. 0: its estimate alone must be available; more leaves more room for your other programs.",
                "4096: its estimate plus 4 GB.", null, "4096", c => MB(c.HeavySpareMB)),
            new("heavyJobTimeoutMinutes", "SFSEARCH_HEAVY_JOB_MINUTES",
                "a heavy command's own time limit, in minutes.",
                "a number of minutes from 1 up (0 or less counts as 1).",
                "30: a heavy command may run 30 minutes.", null, "30", c => c.HeavyJobTimeoutMinutes + " min"),
            new("pluginCheckSeconds", null,
                "how often (seconds) the service checks the plugins in Data while it is idle (no command for 30 s), so a plugin you add, remove or change is in its indexes before your next command needs it. A check reads each plugin's size, date and first and last 64 KB: about 30 ms when nothing changed. A command checks the plugins it reads anyway, so nothing is ever read stale.",
                "10 to 86400 (a day); a number outside counts as the nearest end. Shorter: a change shows sooner, and changes2 dates it closer to when it happened. Longer: fewer checks.",
                "600: a check every 10 minutes.", null, "600", c => c.PluginCheckSeconds.ToString("N0", CultureInfo.InvariantCulture) + " s (" + EveryText(c.PluginCheckSeconds) + ")"),
            new("historyLook", null,
                "whether the service looks by itself for what changed in Data, for changes2's history (plugins added, removed, updated; patches at risk). The history (kept\\history) holds each plugin copy's FormKeys and record types, no record contents.",
                "true: the service looks at its start and at each idle check above, so changes2 dates a change near when it happened and compares with the service's last look (a look when nothing changed takes a few milliseconds). false: only changes2 looks, when it is asked, and dates a change by that call.",
                "true.", null, "true", c => c.HistoryLook ? "true" : "false"),
            new("keepStartupIndexes", null,
                "whether the header index (every record's FormID, EditorID, type, who holds it) and the link index (what points at what, in-game names), which the service builds from your plugin files, are kept between service starts in SFSearch\\indexes.",
                "true: kept, and brought up to date plugin by plugin (a new or changed plugin's part is made, every plugin is checked by a full hash at start), so a start has them ready in about 2.5 s instead of about 20 s (370 plugins; they take about 0.8 GB). false: emptied at every start and built again. 'sfs.cmd indexes' shows them and their size; 'sfs.cmd indexes drop all' deletes them.",
                "true.", null, "true", c => c.KeepStartupIndexes ? "true" : "false"),
            new("keepRemovedPluginIndexMB", null,
                "how much (MB) of the kept index parts of plugins no longer in Data to keep: a plugin you took out, or the copy a changed plugin replaced. Used only with keepStartupIndexes true. 'sfs.cmd indexes' lists what is kept.",
                "0: deleted when SFSearch notices (the next idle check, command or service start), so a plugin you put back is indexed again (a mod's part takes a fraction of a second to about 1 s, Starfield.esm's about 15 s). A number from 1 up: kept up to that many MB; over it, the parts of the plugin out of Data longest are deleted first, its segment and link file together (at the service's start and its hourly check), so switching between mod lists doesn't index the same plugins again.",
                "0: deleted, as in SFSearch 2.0 (kept so by the user's decision for 2.1: please say in an issue if switching mod lists makes your starts slow).", null, "0",
                c => c.KeepRemovedPluginIndexMB == 0 ? "0 (deleted)" : MB(c.KeepRemovedPluginIndexMB) + (c.KeepStartupIndexes ? "" : " (not used: keepStartupIndexes is false)")),
            new("askBeforeHeavy", "SFSEARCH_ASK_BEFORE_HEAVY",
                "whether heavy work (a command estimated at over 10 s, or at heavyMinMB of memory or more) answers with what it would cost and runs only when asked again with allow_heavy (--allow-heavy).",
                "false: it runs. A heavy command still runs alone, starts only when Windows has its estimate plus heavySpareMB available and the disk has room for its scratch files, and says what is missing when they aren't there; its cost is in the answer's [cost] line. true: it answers \"Not run. …\" with the cost first, as SFSearch did up to 2.3.",
                "false.", null, "false", c => c.AskBeforeHeavy ? "true" : "false"),
            new("queueLimit", "SFSEARCH_QUEUE_LIMIT",
                "more waiting commands than this are turned away at once with \"busy, try again\".",
                "a number from 1 up (0 or less counts as 1).",
                "500.", null, "500", c => c.QueueLimit.ToString("N0", CultureInfo.InvariantCulture)),
            new("champollion", "SFSEARCH_CHAMPOLLION",
                "Champollion.exe, the Papyrus decompiler scripts2 uses (version 1.3.2 or newer). The environment variable wins over this setting, and this setting over the tools folder.",
                "the full path to Champollion.exe, or empty.",
                "empty: SFSearch looks for it in its tools\\champollion folder.", f => Path.Combine(f.Home, "tools", "champollion", "Champollion.exe"), "\"\"",
                c => string.IsNullOrWhiteSpace(c.Champollion) ? "(empty)" : c.Champollion!),
            new("pluginsTxt", "SFSEARCH_PLUGINS_TXT",
                "the load order (plugins.txt) SFSearch reads: which plugins are active and in what order.",
                "the full path to a plugins.txt (written with \\\\ or /, e.g. C:\\\\Users\\\\Other\\\\AppData\\\\Local\\\\Starfield\\\\Plugins.txt for a second Windows account's), or empty.",
                "empty: Starfield's own, in your Windows user's AppData folder.", f => f.PluginsTxt, "\"\"",
                c => string.IsNullOrWhiteSpace(c.PluginsTxt) ? "(empty)" : c.PluginsTxt!),
        };

        /// <summary>Text wrapped into comment lines of at most about 118 characters, each starting with the indent and "// ".</summary>
        static void Comment(StringBuilder sb, string indent, string text)
        {
            int width = 118 - indent.Length - 3;
            var line = new StringBuilder();
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width) { sb.Append(indent).Append("// ").Append(line).Append('\n'); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) sb.Append(indent).Append("// ").Append(line).Append('\n');
        }

        /// <summary>
        /// The sfsearch.json SFSearch writes when there is none: every setting with what it does, its values, its default and
        /// what the default comes to on this PC (as on the day it was written: 'Service config' gives the numbers now).
        /// </summary>
        public static string WrittenText(string home)
        {
            var f = PcFacts.Now(home);
            var sb = new StringBuilder();
            Comment(sb, "", "SFSearch settings: the service and the commands. Edit and save: SFSearch reads this file again by itself. Delete a line to use its default.");
            Comment(sb, "", "Each setting says what it does, the values it takes and what they do, and its default. 'sfs.cmd Service config' shows every setting in effect and where it came from (this file, an environment variable, the default or this PC).");
            Comment(sb, "", "A setting with an environment variable named below can be set by that variable too; the variable wins over this file.");
            Comment(sb, "", $"Written on {DateTime.Now:yyyy-MM-dd} on a PC with {f.Pc}: \"on this PC\" below means that PC.");
            sb.Append("{\n");
            for (int i = 0; i < All.Count; i++)
            {
                var s = All[i];
                if (s.Key == "workers")
                {
                    // S17Marker: a file with this line was written after the memory defaults came from the PC (fix S20)
                    Comment(sb, "  ", "Memory: 0 means SFSearch works the number out from this PC's memory and cores when it starts, so the same file suits an 8 GB laptop and a 64 GB PC. A number you put in is kept as you set it.");
                }
                Comment(sb, "  ", $"{s.Key}: {s.What}");
                Comment(sb, "  ", "  Values: " + s.Values);
                string def = "  Default: " + s.Default;
                if (s.OnThisPc != null) def += $" On this PC: {s.OnThisPc(f)}.";
                if (s.Env != null) def += $" Environment variable: {s.Env}.";
                Comment(sb, "  ", def);
                sb.Append("  \"").Append(s.Key).Append("\": ").Append(s.Json).Append(i < All.Count - 1 ? ",\n" : "\n");
                if (i < All.Count - 1) sb.Append('\n');
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>The value of each key the file sets, as written (keys compared without case); empty when the file can't be parsed.</summary>
        static Dictionary<string, string> FileValues(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (text.Length == 0) return d;
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            }
            catch (JsonException) { }
            return d;
        }

        /// <summary>The keys of the last file read, with their values as written.</summary>
        static Dictionary<string, string> _fileValues = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where a setting in effect came from: its environment variable, sfsearch.json (and what the file said, when that isn't what is in effect), this PC, or the default.</summary>
        string Source(Setting s)
        {
            switch (s.Key)
            {
                case "workers": return From.Workers;
                case "workerMaxMB": return From.WorkerMaxMB;
                case "workerRecycleMB": return From.WorkerRecycleMB;
                case "serviceMaxMB": return From.ServiceMaxMB;
                case "memoryFloorMB": return From.MemoryFloorMB;
                case "workerGarbageCollector": return WorkerGarbageCollectorWhy;
            }
            string? env = s.Env != null ? Environment.GetEnvironmentVariable(s.Env) : null;
            if (s.Key is "pluginsTxt" or "champollion" ? !string.IsNullOrWhiteSpace(env) : int.TryParse(env, out int e) && e > 0) return $"set by {s.Env}";
            if (_fileValues.TryGetValue(s.Key, out var written))
            {
                // The value in effect against what the file says: a number outside its range is read as the nearest end
                var prop = typeof(ServiceConfig).GetProperty(s.Key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                object? now = prop?.GetValue(this);
                bool same = now switch
                {
                    int n => int.TryParse(written, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w == n,
                    bool b => bool.TryParse(written, out bool w) && w == b,
                    string t => t == written,
                    null => written.Length == 0,
                    _ => true,
                };
                object? standard = s.Key == "settingsVersion" ? 2 : prop?.GetValue(new ServiceConfig());
                bool isDefault = Equals(now is string nowText && nowText.Length == 0 ? null : now, standard is string stdText && stdText.Length == 0 ? null : standard);
                return same ? "set in sfsearch.json" + (isDefault ? " (the default)" : "") : $"sfsearch.json has {(written.Length == 0 ? "\"\"" : written)}, read as this";
            }
            return s.Key == "pluginsTxt" ? $"the default: {StarfieldPluginsTxt}"
                : s.Key == "champollion" ? "the default: SFSearch's tools\\champollion folder"
                : "the default";
        }

        /// <summary>For `Service config`: where the file is, every setting in effect and where it came from, then what they do together.</summary>
        public static string Describe(string home, IEnumerable<(string Setting, string InEffect, string From)>? outside = null)
        {
            var c = Load(home);
            var sb = new StringBuilder();
            sb.Append($"Settings file: {PathFor(home)}{(LastProblem != null ? "\nWARNING: " + LastProblem : "")}\n");
            sb.Append("Every setting in effect, and where it came from (sfsearch.json, an environment variable, the default, or this PC). sfsearch.json explains each one:\n");
            sb.Append("| Setting | In effect | From |\n|---|---|---|\n");
            foreach (var s in All) sb.Append($"| {s.Key} | {s.InEffect(c)} | {c.Source(s).TrimStart(';', ' ')} |\n");
            // Set outside sfsearch.json: the home, the Data folder, the agent's name (the command line gives them)
            foreach (var (name, value, from) in outside ?? Array.Empty<(string, string, string)>()) sb.Append($"| {name} | {value} | {from} |\n");
            sb.Append($@"Workers: {c.Workers} at once. Each command may take {c.JobTimeoutMinutes} min. A worker is replaced after {c.WorkerRecycleCommands} commands or {c.WorkerRecycleMB} MB, and stopped above {c.WorkerMaxMB} MB.
{c.MemoryLines()}
Heavy commands (estimated at {c.HeavyMinMB} MB or more): one at a time, each may take {c.HeavyJobTimeoutMinutes} min, and one starts only when Windows has its estimate plus {c.HeavySpareMB} MB available. {(c.AskBeforeHeavy ? "Heavy work answers with its cost first and runs when asked again with allow_heavy (askBeforeHeavy is true)." : "Heavy work runs without asking first (askBeforeHeavy is false).")}
Indexes: {(c.KeepStartupIndexes ? $"the header index and the link index are kept between service starts in {Path.Combine(home, "indexes")} and brought up to date plugin by plugin (keepStartupIndexes)" : "the header index and the link index are emptied at every service start and built again (keepStartupIndexes is false)")}. {(c.KeepsRemovedParts ? $"The parts of plugins no longer in Data are kept up to {c.KeepRemovedPluginIndexMB:N0} MB, the plugin out of Data longest deleted first (keepRemovedPluginIndexMB)" : "The parts of plugins no longer in Data are deleted at the next start (keepRemovedPluginIndexMB 0)")}. 'indexes' shows them and their size.
Changed plugins: while idle, the service checks the plugins in Data {EveryText(c.PluginCheckSeconds)} (pluginCheckSeconds); changes2's history: {(c.HistoryLook ? "the service looks by itself, at its start and at each of those checks (historyLook)" : "only changes2 looks (historyLook is off)")}.
Queue limit: {c.QueueLimit}.{UnusedLines(home)}");
            return sb.ToString();
        }
    }
}
