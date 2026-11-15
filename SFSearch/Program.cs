using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SFSearch
{
    /// <summary>
    /// The command line: finds the home, hands the command to the service when one runs, and routes a verb to the engine
    /// (Engine\EngineCommands.cs), an add-on (Extensions.cs) or the service's own commands. The older commands were removed
    /// in 2.0 (design\agent-first\notes\remove-old-commands.md); each old name answers with the verb to use instead.
    /// </summary>
    public class Program
    {
        internal static void Main(string[] args)
        {
            // Numbers are shown the same way whatever language the computer is set to (1.5, never 1,5).
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            // The service runs as "service run <home>" from a copy of the program, and its workers as "service worker <home> <pipe>"
            bool host = !SessionServer.Active && args.Length >= 3 && args[0].Equals("service", StringComparison.OrdinalIgnoreCase)
                && (args[1].Equals("run", StringComparison.OrdinalIgnoreCase) || args[1].Equals("worker", StringComparison.OrdinalIgnoreCase));
            if (host) Environment.SetEnvironmentVariable("SFSEARCH_HOME", args[2]);
            Home.Pin();
            if (host && args[1].Equals("run", StringComparison.OrdinalIgnoreCase)) { Service.ServiceHost.Run(); return; }
            if (host && args.Length >= 4) { Service.ServiceWorker.Run(args[3]); return; }
            // While the service runs, every command goes to it
            if (!SessionServer.Active && args.Length > 0 && !args[0].Equals("service", StringComparison.OrdinalIgnoreCase) && SessionServer.IsRunning())
            {
                Environment.ExitCode = SessionServer.Client(args);
                return;
            }
            if (!SessionServer.Active) try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch (IOException) { } // answers use →, ×, …

            var argList = CommandLine.ParseGlobalOptions(args.ToList());
            if (argList.Count < 1) { PrintHelp(); return; }
            // --allow-heavy: the caller's OK for a command that finds it needs a lot of memory (Service\JobContext.cs)
            if (CommandLine.TakeFlag(argList, "--allow-heavy")) Service.JobContext.AllowHeavyLocal = true;
            string command = argList[0].ToLowerInvariant();
            var rest = argList.Skip(1).ToList();

            try
            {
                // Said once, with the first command that runs after this version is put in: the old databases are no longer used
                if (!command.StartsWith("__", StringComparison.Ordinal) && command is not ("service" or "version" or "--version" or "-v")) Home.OldDataNote();
                switch (command)
                {
                    case "version" or "--version" or "-v":
                        string asmPath = typeof(Program).Assembly.Location;
                        var built = File.Exists(asmPath) ? File.GetLastWriteTime(asmPath) : DateTime.MinValue;
                        string mutagen = (typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "?";
                        Console.WriteLine($"SFSearch {Extensions.DisplayVersion} | Mutagen {mutagen} | Built: {built:yyyy-MM-dd HH:mm:ss} | Home: {Home.Dir}");
                        break;
                    case "help" or "--help" or "-h":
                        PrintHelp();
                        break;
                    case "service":
                        if (SessionServer.Active && !(rest.Count > 0 && rest[0].Equals("config", StringComparison.OrdinalIgnoreCase))) { Console.WriteLine("You're already in the service: this command is for starting and stopping it."); return; }
                        SessionServer.Command(rest);
                        break;
                    case "setdata":
                        if (rest.Count < 1) { Console.WriteLine("Usage: SFSearch SetData <path to Starfield's Data folder>"); return; }
                        Home.SetDataFolder(rest[0]);
                        break;
                    case "extensions":
                        Console.Write(Extensions.Describe());
                        break;
                    case "__test" when Service.TestHooks.Enabled:
                        Service.TestHooks.Run(rest);
                        break;
                    default:
                        // The engine's verbs, then a retired name (SFSearch's own names come before an add-on's), then the add-ons
                        if (Engine.EngineCommands.TryRun(command, rest)) break;
                        if (Retired.TryGetValue(command, out var instead)) { RetiredCommand(argList[0], instead); break; }
                        if (!Extensions.TryRun(command, rest)) UnknownCommand(argList[0]);
                        break;
                }
            }
            catch (FileNotFoundException ex)
            {
                // A plugin or file that isn't there: say so, no stack trace
                Console.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                // A command that stopped on an exception nothing above expected: the error line and exit code 2, never 0
                // (S43: a crash inside an add-on's command exited 0)
                Crashed(ex, null);
            }
        }

        /// <summary>
        /// The answer of a command that stopped on an unexpected exception: "Error: … Fix: …" with the exception's own
        /// text, exit code 2 (an error, as for every verb), then the cause and where it happened.
        /// </summary>
        internal static void Crashed(Exception ex, string? addOnCommand)
        {
            string what = (addOnCommand != null ? $"the add-on command {addOnCommand} stopped: " : "") + ex.Message;
            Service.JobContext.Fail(what, "SFSearch didn't expect this error. Run the command again (a plugin written or removed while it was read can cause it); if it happens again, report it with the lines below", 2);
            // Mutagen's load-order import wraps the real cause (which plugin, what failed) several levels deep.
            var root = ex;
            while (root.InnerException != null) root = root.InnerException;
            if (root != ex) Console.WriteLine($"Cause: {root.GetType().Name}: {root.Message}");
            Console.WriteLine(ex.StackTrace);
            // Parallel.For's AggregateException keeps the place it was thrown in its inner exception only
            if (root != ex && root.StackTrace != null) Console.WriteLine("Thrown at:" + Environment.NewLine + root.StackTrace);
        }

        /// <summary>
        /// The older commands, removed in 2.0, each with what answers it now (the verb, and how to ask). An old script that
        /// still calls one gets one line, Error: ... Fix: use ..., and exit code 2, not "no such command".
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["query"] = "query2 <plugin> \"<SQL>;\" (sfs_query: typed views per record type, 'describe <Type>' lists the columns; Query --loadorder is query2 --scope winners or versions)",
            ["q"] = "saved run <name> plugin=<plugin> (sfs_saved; 'saved list' lists the stored queries)",
            ["fields"] = "describe <Type> (sfs_describe)",
            ["inspect"] = "record <record> --plugin <plugin> (sfs_record; --view fields for every field)",
            ["dump"] = "record <record> --plugin <plugin> --view json (sfs_record)",
            ["read"] = "record <record> --plugin <plugin> --view raw (sfs_record)",
            ["list"] = "find --type <Type> --in <plugin> (sfs_find)",
            ["diff"] = "compare records <plugin a> <record> <plugin b> [<record b>] (sfs_compare; or two FormKeys: compare records <FormKey> <FormKey>)",
            ["check"] = "check2 <plugin> (sfs_check; --what itm, assets, materials, roundtrip or all)",
            ["header"] = "loadorder2 plugin <plugin> (sfs_loadorder)",
            ["stats"] = "loadorder2 plugin <plugin> (sfs_loadorder)",
            ["delta"] = "compare plugin_vs_masters <plugin> [<EditorID text, FormKey or type>] (sfs_compare)",
            ["names"] = "find <FormID or EditorID> (sfs_find)",
            ["refs"] = "refs2 <record> (sfs_refs; --world for placed references and navmeshes)",
            ["conflicts"] = "compare versions <record> (sfs_compare)",
            ["loadorder"] = "loadorder2 (sfs_loadorder; loadorder2 problems for mismatched overrides and late masters)",
            ["leveled"] = "leveled2 <list> (through sfs_run)",
            ["dialogue"] = "dialogue2 <quest or topic> (through sfs_run)",
            ["sources"] = "sources2 <plugin> (through sfs_run)",
            ["scripts"] = "scripts2 <plugin> (through sfs_run)",
            ["changes"] = "changes2 (through sfs_run; also loadorder2 changes)",
            ["archive"] = "archive2 <plugin, .ba2 or all> (through sfs_run)",
            ["materials"] = "materials2 <plugin> (through sfs_run), or check2 <plugin> --what materials",
            ["assets"] = "assets2 <plugin> (through sfs_run), or check2 <plugin> --what assets",
            ["plugindiff"] = "compare plugins <old plugin> <new plugin> (sfs_compare; --match editorid for a mod whose FormIDs moved)",
            ["live"] = "record <record> --plugin <plugin> (sfs_record: --view raw for subrecords, --view versions for the chain; refs2 <record> for --refs and --links)",
            ["livescancheck"] = "nothing: it checked a scan against the older commands' tables, and both are gone. record <record> --view evidence shows a record's bytes",
            ["add"] = "nothing: query2 and record read every record type by themselves",
            ["verifydb"] = "record <record> --view verify (sfs_record), or check2 <plugin> --what roundtrip",
            ["losscheck"] = "check2 <plugin> --what roundtrip (sfs_check)",
            ["buildcache"] = "nothing: SFSearch keeps no databases; every verb reads the plugin files live",
            ["databases"] = "status (sfs_status: the scratch folder and the indexes)",
            ["session"] = "Service start | stop | restart | status | config",
        };

        static void RetiredCommand(string name, string instead) =>
            Service.JobContext.Fail($"{name} was one of SFSearch's older commands, removed in 2.0",
                (instead.StartsWith("nothing:", StringComparison.Ordinal) ? "nothing replaces it" + instead["nothing".Length..] : "use " + instead) + ". 'SFSearch --help' lists the verbs", 2);

        /// <summary>
        /// A name that is no verb, retired command or add-on command: one line, Error: ... Fix: ..., with the nearest names
        /// (verbs first), and exit code 2. Inside sfs_run the tool words its own line (Engine\ServiceTools.cs RunTool), so
        /// there a plain line is printed for it to find.
        /// </summary>
        static void UnknownCommand(string name)
        {
            if (Engine.EngineAnswer.ToolMode) { Console.WriteLine($"Unknown command '{name.ToLowerInvariant()}'. Run 'SFSearch --help' for usage."); return; }
            var verbs = Engine.EngineCommands.Verbs.Select(v => v.Verb).ToList();
            var others = new[] { "Service", "SetData", "Version", "Extensions" };
            var near = SFSearch.SchemaGen.NearNames.Pick(name, verbs.Concat(others).Concat(Extensions.CommandNames()).Distinct(StringComparer.OrdinalIgnoreCase),
                v => new[] { v, v.TrimEnd('2') }, 4, always: false);
            near = near.OrderBy(v => verbs.Contains(v) ? 0 : 1).ToList();
            Service.JobContext.Fail($"there is no verb or command named '{name}'",
                (near.Count > 0 ? SFSearch.SchemaGen.NearNames.Fix(near) + ". " : "") + "'SFSearch --help' lists the verbs", 2);
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"SFSearch - reads Starfield plugins (.esm) live from the game's Data folder. Nothing is stored, so nothing is stale.
Global flags: --json (a verb's answer as one JSON object) | --allow-heavy (run a command that said its cost first)
Service start | stop | restart | status | config   runs SFSearch as a service (settings: sfsearch.json): many commands at once,
  from many agents (the MCP server SFSearch.Mcp is the main way in). While it runs, every command goes to it.

The verbs. Each takes its options by name; <verb> --help shows them:");
            foreach (var v in Engine.EngineCommands.Verbs) Console.WriteLine($"  {v.Verb,-12} {char.ToUpperInvariant(v.What[0])}{v.What[1..]}");
            Console.WriteLine(@"
Other:
  SetData <path>     Remember where Starfield's Data folder is (needed only when it isn't found by itself)
  Version
  Extensions         List the add-ons in the extensions folder and their commands

The older commands (Query, Names, Refs, Check, Inspect, LoadOrder and the rest) were removed in 2.0. Each old name
answers with the verb to use instead; the verbs whose name ends in 2 are the ones that replaced a command of the plain name.");
            foreach (var line in Extensions.HelpLines()) Console.WriteLine(line);
        }
    }
}
