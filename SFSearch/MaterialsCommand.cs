using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFSearch
{
    /// <summary>
    /// Materials: a plugin's meshes → the materials they use → the textures those use, with what's missing anywhere along the
    /// way. Materials shipped as .mat files (JSON) are followed to their textures. The game's own materials, and mods that ship
    /// a compiled materialsbeta.cdb, aren't .mat files: those are listed as compiled and not checked further.
    /// </summary>
    public static class MaterialsCommand
    {
        static readonly Regex MatInMesh = new(@"[A-Za-z0-9_\\/ .\-]+\.mat\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        sealed class Mat
        {
            public string Path = "";
            public string Where = "";            // archive name, "loose", or "compiled / not found"
            public List<string> UsedBy = new();  // meshes
            public List<(string Texture, string Where)> Textures = new();
            public string? Problem;
        }

        public static void Run(List<string> args)
        {
            bool missingOnly = Sfs.TakeFlag(args, "--missing");
            bool all = Sfs.TakeFlag(args, "--all");
            string? outFile = Sfs.TakeOption(args, "--out");
            if (args.Count < 1) { Console.WriteLine("Usage: SFSearch Materials <plugin> [--missing] [--all] [--out report.md]"); return; }
            string dataDir = Program.GetDataFolderPathPublic();
            string stem = Path.GetFileNameWithoutExtension(args[0]);
            var own = ArchiveCommand.Resolve(args[0], dataDir);

            // Every file in every archive in Data: where a path can be found (first archive that has it)
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in Directory.GetFiles(dataDir, "*.ba2").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                foreach (var p in AssetsCommand.LoadBa2FileList(a)) index.TryAdd(p, Path.GetFileName(a));
            string Where(string rel) =>
                File.Exists(Path.Combine(dataDir, rel)) ? "loose" : index.TryGetValue(rel, out var arch) ? arch : "";

            // The plugin's own meshes and materials
            var ownFiles = ArchiveCommand.Files(own).ToList();
            var mats = new Dictionary<string, Mat>(StringComparer.OrdinalIgnoreCase);
            Mat Get(string rel) => mats.TryGetValue(rel, out var m) ? m : mats[rel] = new Mat { Path = rel };
            int meshes = 0;
            foreach (var f in ownFiles.Where(f => f.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)))
            {
                meshes++;
                string text = Encoding.Latin1.GetString(f.File.GetBytes());
                foreach (Match m in MatInMesh.Matches(text)) Get(Norm(m.Value)).UsedBy.Add(f.Path);
            }
            foreach (var f in ownFiles.Where(f => f.Path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))) Get(f.Path);
            bool shipsCdb = ownFiles.Any(f => f.Path.EndsWith(".cdb", StringComparison.OrdinalIgnoreCase));

            // Each material: where it is, and if it's a .mat file, its textures
            var ownByPath = ownFiles.ToDictionary(f => f.Path, f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var m in mats.Values)
            {
                byte[]? bytes = null;
                string loose = Path.Combine(dataDir, m.Path);
                if (File.Exists(loose)) { m.Where = "loose"; bytes = File.ReadAllBytes(loose); }
                else if (ownByPath.TryGetValue(m.Path, out var f)) { m.Where = f.Archive; bytes = f.File.GetBytes(); }
                else if (index.TryGetValue(m.Path, out var arch))
                {
                    m.Where = arch;
                    bytes = ArchiveCommand.Files(new List<string> { Path.Combine(dataDir, arch) }).FirstOrDefault(x => x.Path.Equals(m.Path, StringComparison.OrdinalIgnoreCase)).File?.GetBytes();
                }
                else { m.Where = shipsCdb ? "compiled (the mod's materialsbeta.cdb or the game's), not checked" : "compiled (the game's material database), not checked"; continue; }

                JsonNode? json = null;
                try { json = JsonNode.Parse(bytes!); } catch (System.Text.Json.JsonException) { m.Problem = "not readable as a material (bad JSON)"; continue; }
                foreach (var tex in TextureNames(json))
                    m.Textures.Add((tex, Where(tex)));
            }

            using var report = Sfs.ReportOut.Begin(outFile);
            Sfs.Say($"### Materials: {stem} ({own.Count} archive(s), {meshes} mesh(es), {mats.Count} material(s))");
            int compiled = mats.Values.Count(m => m.Where.StartsWith("compiled"));
            int missingTex = mats.Values.Sum(m => m.Textures.Count(t => t.Where.Length == 0));
            int bad = mats.Values.Count(m => m.Problem != null);
            Sfs.Summary($"{mats.Count - compiled} material file(s) checked, {compiled} compiled (not checked); {missingTex} missing texture(s); {bad} unreadable material(s)");

            var t = new Sfs.Table("Material", "Found in", "Used by", "Textures", "Missing textures");
            var rows = mats.Values.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
                .Where(m => !missingOnly || m.Problem != null || m.Textures.Any(x => x.Where.Length == 0)).ToList();
            foreach (var m in all ? rows : rows.Take(300))
                t.Add(m.Path, m.Where, m.UsedBy.Count == 0 ? "(no mesh of this mod)" : m.UsedBy.Count == 1 ? m.UsedBy[0] : $"{m.UsedBy.Count} meshes",
                      m.Textures.Count, m.Problem ?? string.Join(", ", m.Textures.Where(x => x.Where.Length == 0).Select(x => x.Texture)));
            if (rows.Count > 0) Sfs.Print(t, annotate: false);
            else Sfs.Say(missingOnly ? "_Nothing missing._" : "_No materials._");
            if (!all && rows.Count > 300) Sfs.Say($"_… {rows.Count - 300} more; add --all._");
            if (compiled > 0) Sfs.Say("\n_Compiled materials: the game keeps its own materials in a compiled database (`Starfield - Materials.ba2`), and some mods ship one too (`materialsbeta.cdb`). SFSearch can't read those yet, so their textures aren't checked._");
        }

        /// <summary>"Data\Textures\x.dds" in a material → "textures\x.dds", the way archives and the Data folder name it.</summary>
        static string Norm(string p)
        {
            p = p.Trim().Replace('/', '\\').TrimStart('\\');
            if (p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
            return p.ToLowerInvariant();
        }

        static IEnumerable<string> TextureNames(JsonNode? n)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(JsonNode? x)
            {
                switch (x)
                {
                    case JsonObject o:
                        foreach (var kv in o)
                        {
                            if (kv.Key.Equals("FileName", StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue v && v.TryGetValue<string>(out var s)
                                && s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                                found.Add(Norm(s));
                            else Walk(kv.Value);
                        }
                        break;
                    case JsonArray a: foreach (var i in a) Walk(i); break;
                }
            }
            Walk(n);
            return found.OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
        }
    }
}
