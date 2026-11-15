using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SFSearch.Engine
{
    /// <summary>
    /// A verb's command line turned into the tool's named arguments, so a tool is written once (milestone M8).
    /// The option names are the tool's argument names: <c>--max-chars 4000</c> is <c>max_chars: 4000</c>,
    /// <c>--world</c> is <c>world: true</c>. What isn't an option goes to the positional names in order; what is left
    /// over is joined onto the last one. A wrong option is an error that names the real ones.
    /// </summary>
    public sealed class VerbSpec
    {
        /// <summary>The verb as typed (query2), for the error lines.</summary>
        public required string Verb { get; init; }
        /// <summary>Arguments filled from the words that aren't options, in this order.</summary>
        public string[] Positional { get; init; } = Array.Empty<string>();
        /// <summary>Arguments that take a value: --name value.</summary>
        public string[] Options { get; init; } = Array.Empty<string>();
        /// <summary>Arguments that are yes or no: --name.</summary>
        public string[] Flags { get; init; } = Array.Empty<string>();
        /// <summary>Other spellings of an option: --lang for language, --in for plugins.</summary>
        public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>True when the words left over are joined with spaces onto the last positional (an EditorID pattern, SQL typed without quotes).</summary>
        public bool JoinRest { get; init; } = true;

        public IEnumerable<string> All => Positional.Concat(Options).Concat(Flags);

        static string Cli(string name) => "--" + name.Replace('_', '-');

        /// <summary>The named arguments, or null with a failed answer.</summary>
        public Dictionary<string, JsonElement>? Parse(List<string> args, EngineAnswer answer)
        {
            var given = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var words = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal) || a.Length < 3) { words.Add(a); continue; }
                string name = a[2..].Replace('-', '_');
                if (Aliases.TryGetValue(name, out var real)) name = real;
                if (Flags.Contains(name, StringComparer.OrdinalIgnoreCase)) { given[name] = JsonSerializer.SerializeToElement(true); continue; }
                if (Options.Contains(name, StringComparer.OrdinalIgnoreCase) || Positional.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Count) { answer.Fail($"{Verb}: {a} needs a value", $"write {a} <value>", 2); return null; }
                    given[name] = JsonSerializer.SerializeToElement(args[++i]);
                    continue;
                }
                answer.Fail($"{Verb} has no option {a}", $"its options are {string.Join(", ", Options.Concat(Flags).Select(Cli))}{(Positional.Length > 0 ? "; it takes " + string.Join(" ", Positional.Select(p => "<" + p + ">")) + " first" : "")} ({Verb} --help shows them)", 2);
                return null;
            }
            var free = Positional.Where(p => !given.ContainsKey(p)).ToList();
            for (int i = 0; i < words.Count; i++)
            {
                if (free.Count == 0)
                {
                    answer.Fail($"{Verb} got more than it takes: \"{words[i]}\"", Positional.Length == 0 ? $"{Verb} takes options only ({Verb} --help shows them)" : $"it takes {string.Join(" ", Positional.Select(p => "<" + p + ">"))}; put a value with spaces in quotes", 2);
                    return null;
                }
                bool last = i < words.Count - 1 && free.Count == 1 && JoinRest;
                if (last) { given[free[0]] = JsonSerializer.SerializeToElement(string.Join(" ", words.Skip(i))); break; }
                given[free[0]] = JsonSerializer.SerializeToElement(words[i]);
                free.RemoveAt(0);
            }
            return given;
        }
    }
}
