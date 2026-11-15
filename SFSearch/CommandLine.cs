using System;
using System.Collections.Generic;

namespace SFSearch
{
    public enum OutputFormat { Markdown, Json, Csv }

    /// <summary>
    /// The command line's options: the global ones (--json, --csv, --format, --maxwidth, --raw) and how a command takes its
    /// own (--name value, --flag). The verbs, the service and the add-on API's Args and Out read them here.
    /// </summary>
    public static class CommandLine
    {
        /// <summary>The output form asked for: Markdown tables (the default), --json or --csv.</summary>
        public static OutputFormat Format { get; set; } = OutputFormat.Markdown;
        /// <summary>--maxwidth N: cut a table's cells at N characters. 0 = no limit.</summary>
        public static int MaxWidth { get; set; } = 0;
        /// <summary>False with --raw: FormKeys in an add-on's tables are left without their EditorIDs.</summary>
        public static bool ResolveNames { get; set; } = true;

        /// <summary>Takes the global options out of the arguments and applies them. Returns what is left.</summary>
        public static List<string> ParseGlobalOptions(List<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--format" && i + 1 < args.Count)
                {
                    Format = args[i + 1].ToLowerInvariant() switch { "json" => OutputFormat.Json, "csv" => OutputFormat.Csv, _ => OutputFormat.Markdown };
                    args.RemoveRange(i, 2); i--;
                }
                else if (a == "--json") { Format = OutputFormat.Json; args.RemoveAt(i); i--; }
                else if (a == "--csv") { Format = OutputFormat.Csv; args.RemoveAt(i); i--; }
                else if (a == "--maxwidth" && i + 1 < args.Count && int.TryParse(args[i + 1], out int w))
                {
                    MaxWidth = Math.Max(0, w); args.RemoveRange(i, 2); i--;
                }
                else if (a == "--raw") { ResolveNames = false; args.RemoveAt(i); i--; }
            }
            return args;
        }

        /// <summary>Takes "--name value" out of the arguments (both words). Null when it isn't there.</summary>
        public static string? TakeOption(List<string> args, string name)
        {
            int i = args.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Count) return null;
            string v = args[i + 1];
            args.RemoveRange(i, 2);
            return v;
        }

        /// <summary>Takes a "--flag" out of the arguments: true when it was there.</summary>
        public static bool TakeFlag(List<string> args, string name) =>
            args.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
    }
}
