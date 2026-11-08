namespace SFSearch.Example
{
    /// <summary>
    /// An SFSearch add-on: a class with a list of commands and a Run method. SFSearch finds it in the .dll by itself.
    /// Commands get SFSearch's helpers (the Sfs class): open a plugin's database, find records, print tables. Read
    /// agent-docs\skills\sfsearch\references\database.md for the tables you can query.
    /// </summary>
    public sealed class ExampleCommands : ISfsExtension
    {
        // Name, then the line --help shows. Keep names short; when two add-ons share one, people use Example.KeywordCount.
        public IReadOnlyList<(string Name, string Help)> Commands { get; } =
        [
            ("KeywordCount", "  KeywordCount <plugin> <keyword>               How many records of each type in <plugin> carry a keyword (EditorID or FormKey)"),
        ];

        public void Run(string command, List<string> args)
        {
            if (args.Count < 2) { Console.WriteLine("Usage: SFSearch KeywordCount <plugin> <keyword>"); return; }

            // The plugin's database: built or refreshed as needed, from the Data folder or a path
            string plugin = Program.EnsureCacheForFile(args[0]);
            using var conn = Sfs.OpenCache(plugin);

            // The keyword may live in the plugin or any of its masters
            var plugins = new List<string> { plugin };
            plugins.AddRange(Sfs.Masters(conn));
            string? keyword = Sfs.ResolveUserKey(args[1], plugins);
            if (keyword == null) { Console.WriteLine($"Error: no record '{args[1]}' in {plugin} or its masters."); return; }

            // Refs holds every link from a record to another; a keyword link's path names the keyword list
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT FromSignature, COUNT(DISTINCT FromFormKey) FROM Refs WHERE {Sfs.FormKeyMatch("ToFormKey")} AND Path LIKE '%Keyword%' GROUP BY FromSignature ORDER BY 2 DESC, 1";
            Sfs.AddFormKeyParams(cmd, keyword);
            var table = new Sfs.Table("Record type", "Records");
            using (var r = cmd.ExecuteReader())
                while (r.Read()) table.Add(r.GetString(0), r.GetInt64(1).ToString());

            Sfs.Say($"Records in {plugin} that carry {Sfs.AnnotateFormKeys(keyword)}:");   // adds the EditorID
            if (table.Rows.Count == 0) Sfs.Say("_None._");
            else Sfs.Print(table);   // Markdown table; FormKeys get their EditorIDs added
        }
    }
}
