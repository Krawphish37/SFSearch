using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SFSearch.Service
{
    /// <summary>
    /// The structured form's tools (2026-11-11, milestone M4): a name and named arguments in place of a command line.
    /// For now there is one, "run", which takes {"args": [...]} and runs a command as sfs.cmd would. The engine's own
    /// tools (find, record, describe, query, ...) are added here by the later milestones with Register.
    /// </summary>
    public static class ToolCalls
    {
        public delegate void Handler(IReadOnlyDictionary<string, JsonElement> args);

        static readonly Dictionary<string, Handler> _tools = new(StringComparer.OrdinalIgnoreCase)
        {
            ["run"] = RunCommand,
            // The new engine's tools (milestone M5): they read the header index, no database
            ["sfs_find"] = Engine.FindTool.Tool,
            ["sfs_loadorder"] = Engine.LoadOrderTool.Tool,
            // Milestone M5, second half: they read records live through Mutagen (SFSearch.Records), no database
            ["sfs_record"] = Engine.RecordTool.Tool,
            ["sfs_refs"] = Engine.RefsTool.Tool,
            // Milestone M8: the rest of the 12 tools. describe reads schema.db; query and saved go through the SQL layer
            // (SFSearch.Query); compare through SFSearch.Compare; check through SFSearch.Reports. No database is kept.
            ["sfs_describe"] = Engine.DescribeTool.Tool,
            ["sfs_query"] = Engine.QueryTool.Tool,
            ["sfs_saved"] = Engine.SavedTool.Tool,
            ["sfs_compare"] = Engine.CompareTool.Tool,
            ["sfs_check"] = Engine.ReportTools.CheckTool,
            ["sfs_run"] = Engine.RunTool.Tool,
            ["sfs_status"] = Engine.StatusTool.Tool,
            ["sfs_job"] = Engine.JobTool.Tool,
        };

        public static void Register(string name, Handler handler) { lock (_tools) _tools[name] = handler; }

        public static IReadOnlyList<string> Names { get { lock (_tools) return _tools.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(); } }

        internal static void Run(string tool, Dictionary<string, JsonElement>? args)
        {
            Handler? h;
            lock (_tools) _tools.TryGetValue(tool, out h);
            if (h == null) { JobContext.Fail($"there is no tool named '{tool}'", $"use one of: {string.Join(", ", Names)}", 2); return; }
            var given = args ?? new Dictionary<string, JsonElement>();
            // Every tool takes format: "json" asks for the structured form (a tool with rows as values then gives those)
            var format = given.FirstOrDefault(kv => kv.Key.Equals("format", StringComparison.OrdinalIgnoreCase));
            if (format.Key != null && !tool.Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                string f = format.Value.ValueKind == JsonValueKind.String ? format.Value.GetString() ?? "" : format.Value.GetRawText();
                if (f.Equals("json", StringComparison.OrdinalIgnoreCase)) Engine.EngineAnswer.JsonMode = true;
                else if (f.Length > 0 && !f.Equals("text", StringComparison.OrdinalIgnoreCase)) { JobContext.Fail($"there is no format '{f}'", "use text (the default) or json", 2); return; }
                given = given.Where(kv => !kv.Key.Equals("format", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            Engine.EngineLibs.TuneSqlite();
            h(given);
        }

        static void RunCommand(IReadOnlyDictionary<string, JsonElement> args)
        {
            var given = args.FirstOrDefault(kv => kv.Key.Equals("args", StringComparison.OrdinalIgnoreCase)).Value;
            if (given.ValueKind != JsonValueKind.Array || given.GetArrayLength() == 0)
            {
                JobContext.Fail("the run tool got no command", "pass args as a list, one item per argument, e.g. {\"args\": [\"Names\", \"000CF2\"]}", 2);
                return;
            }
            Program.Main(given.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText()).ToArray());
        }
    }
}
