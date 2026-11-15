using System.Text.Json.Nodes;
using SFSearch.AddOns;

namespace SFSearch.Example
{
    /// <summary>
    /// An SFSearch add-on: a class with a list of commands and a Run method. SFSearch finds it in the .dll by itself.
    /// Commands use the add-on API (SFSearch\AddOnApi.cs, namespace SFSearch.AddOns): GameData finds plugins and records
    /// through the header index, PluginView reads a plugin's records live through Mutagen (as JSON or as Mutagen's own
    /// objects), Args takes options, Out prints under SFSearch's output contract. Nothing is stored: every run reads the
    /// plugin files as they are now.
    /// </summary>
    public sealed class ExampleCommands : ISfsExtension
    {
        // Name, then the line --help shows. Keep names short; when two add-ons share one, people use Example.KeywordCount.
        public IReadOnlyList<(string Name, string Help)> Commands { get; } =
        [
            ("KeywordCount", "  KeywordCount <plugin> <keyword> [--type T]    How many records of each type in <plugin> carry a keyword (EditorID or FormKey)"),
        ];

        public void Run(string command, List<string> args)
        {
            // The API's errors carry the contract's line (Error: ... Fix: ...): print it and stop
            try { KeywordCount(args); }
            catch (AddOnException e) { Console.WriteLine(e.Line); Environment.ExitCode = 2; }
        }

        static void KeywordCount(List<string> args)
        {
            // Options first: Args takes them out of the list, so what is left are the plain words
            string? onlyType = Args.Option(args, "--type");
            if (args.Count < 2) { Out.Error("KeywordCount needs a plugin and a keyword", "KeywordCount <plugin> <keyword> [--type Weapon], e.g. KeywordCount kgcdoom.esm WeaponTypeShotgun"); return; }

            // The plugin with its masters, as the plugin sees records. A plugin outside Data is named by its path.
            using var view = GameData.Open(args[0]);

            // The keyword: a FormKey as it is; an EditorID is looked up in the header index (a record of the plugin's chain first)
            string? keyword = FormKeys.Is(args[1]) ? FormKeys.Normalize(args[1]) : null;
            if (keyword == null)
            {
                var hits = GameData.FindEditorId(args[1]);
                keyword = hits.Where(h => view.Chain.Contains(h.Plugin, StringComparer.OrdinalIgnoreCase)).Select(h => h.FormKey).FirstOrDefault()
                    ?? hits.Select(h => h.FormKey).FirstOrDefault();
            }
            if (keyword == null) { Out.Error($"no record has the EditorID '{args[1]}'", "give the keyword's EditorID or FormKey ('find <text>' searches for it)"); return; }

            // The plugin's own records (new ones and overrides), read live; one type only with --type (faster on a big plugin)
            var records = onlyType != null ? view.Records(onlyType) : view.AllRecords();

            // A record carries the keyword when a field whose name holds "Keyword" links to it, anywhere in its JSON
            var byType = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var rec in records)
                if (Carries(rec.Json, keyword, inKeywordField: false))
                    byType[rec.Type] = byType.GetValueOrDefault(rec.Type) + 1;

            Out.Say($"Records in {view.Target} that carry {view.Show(keyword)}:");
            if (byType.Count == 0) Out.Say($"None of its {records.Count:N0} record(s){(onlyType != null ? $" of type {onlyType}" : "")}.");
            else
            {
                var table = new Out.Table("Record type", "Records");
                foreach (var (type, n) in byType.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)) table.Add(type, n);
                Out.Print(table);
            }
            foreach (var note in view.Notes) Out.Say("Note: " + note);
        }

        /// <summary>True when a field named like "Keywords" (at any depth) holds the FormKey.</summary>
        static bool Carries(JsonNode? node, string formKey, bool inKeywordField)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (name, value) in o)
                        if (Carries(value, formKey, inKeywordField || name.Contains("Keyword", StringComparison.OrdinalIgnoreCase))) return true;
                    return false;
                case JsonArray a:
                    foreach (var item in a) if (Carries(item, formKey, inKeywordField)) return true;
                    return false;
                case JsonValue v when inKeywordField && v.TryGetValue<string>(out var text):
                    return FormKeys.Is(text) && FormKeys.Normalize(text).Equals(formKey, StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }
    }
}
