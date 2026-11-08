using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace SFSearch
{
    public static class AssetsCommand
    {
        public class AssetCheckResult
        {
            public string RecordFormKey = "";
            public string Signature = "";
            public string EditorID = "";
            public string Field = "";
            public string AssetPath = "";
            public string Status = ""; // OK (Loose), OK (BA2), MISSING
            public string Source = "";
        }

        public static void Run(List<string> args)
        {
            bool missingOnly = Sfs.TakeFlag(args, "--missing");
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch Assets <plugin> [--missing]");
                return;
            }

            string plugin = Program.EnsureCacheForFile(args[0]);
            var results = AuditPluginAssets(plugin, out var modBa2Files);

            Sfs.Say($"### Asset Audit: {plugin}");
            if (modBa2Files.Count > 0)
            {
                Sfs.Say($"\n#### Associated BA2 Archives ({modBa2Files.Count})");
                var bt = new Sfs.Table("Archive", "TotalFiles", "Meshes (.nif/.mesh)", "Materials (.mat)", "Textures (.dds)", "Scripts (.pex)");
                foreach (var (ba2Name, files) in modBa2Files)
                {
                    int meshes = files.Count(f => f.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase));
                    int mats = files.Count(f => f.EndsWith(".mat", StringComparison.OrdinalIgnoreCase));
                    int tex = files.Count(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
                    int pex = files.Count(f => f.EndsWith(".pex", StringComparison.OrdinalIgnoreCase));
                    bt.Add(ba2Name, files.Count, meshes, mats, tex, pex);
                }
                Sfs.Print(bt, annotate: false);
            }

            var filtered = missingOnly ? results.Where(r => r.Status == "MISSING").ToList() : results;
            Sfs.Say($"\n#### Referenced Models & Assets ({filtered.Count} shown, {results.Count(r => r.Status == "MISSING")} MISSING)");
            var t = new Sfs.Table("FormKey", "Signature", "EditorID", "Field", "AssetPath", "Status", "Source");
            foreach (var r in filtered)
            {
                t.Add(r.RecordFormKey, r.Signature, r.EditorID, r.Field, r.AssetPath, r.Status, r.Source);
            }
            Sfs.Print(t, annotate: false);
        }

        public static List<AssetCheckResult> AuditPluginAssets(string plugin, out List<(string ArchiveName, HashSet<string> Files)> modBa2Archives)
        {
            string dataDir = Program.GetDataFolderPathPublic();
            using var conn = Sfs.OpenCache(plugin, ensureUpToDate: false);
            var masters = Sfs.Masters(conn);

            // Build BA2 lookup index for the plugin and its masters (excluding huge Starfield - Meshes*.ba2 unless needed; vanilla Starfield.esm assets assumed present in vanilla BA2s)
            var candidatePlugins = new List<string> { Sfs.Meta(conn, "FileName") ?? plugin }; // the real name: a plugin read by path has a _file1_ cache name
            candidatePlugins.AddRange(masters);

            var ba2Index = new List<(string ArchiveName, HashSet<string> Files)>();
            modBa2Archives = new List<(string ArchiveName, HashSet<string> Files)>();

            foreach (var p in candidatePlugins.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string prefix = Path.GetFileNameWithoutExtension(p);
                if (prefix.Equals("Starfield", StringComparison.OrdinalIgnoreCase)) continue; // Vanilla BA2s handled via vanilla fallback
                var archives = FindBa2ArchivesForPrefix(dataDir, prefix);
                foreach (var arch in archives)
                {
                    var set = LoadBa2FileList(arch);
                    string archName = Path.GetFileName(arch);
                    ba2Index.Add((archName, set));
                    if (p.Equals(plugin, StringComparison.OrdinalIgnoreCase))
                        modBa2Archives.Add((archName, set));
                }
            }

            bool hasStarfieldMaster = masters.Any(m => m.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase))
                                   || plugin.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase);

            var results = new List<AssetCheckResult>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FormKey, Signature, EditorID, Data FROM Records ORDER BY FormKey"; // a fixed order (service databases are filled type by type)
            using var reader = cmd.ExecuteReader();

            var addonQueue = new List<(string ArmorFk, string ArmorEdid, string AddonFk)>();
            var seenAddons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (reader.Read())
            {
                string fk = reader.GetString(0);
                string sig = reader.GetString(1);
                string edid = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var data = (JsonNode.Parse(reader.GetString(3)) as JsonObject) ?? new JsonObject();

                if (sig == "ArmorAddon") seenAddons.Add(fk);

                ExtractModelFiles(data, "", (fieldPath, rawAsset) =>
                {
                    var (status, source) = ResolveAsset(rawAsset, fk, dataDir, ba2Index, hasStarfieldMaster);
                    results.Add(new AssetCheckResult
                    {
                        RecordFormKey = fk,
                        Signature = sig,
                        EditorID = edid,
                        Field = fieldPath,
                        AssetPath = rawAsset,
                        Status = status,
                        Source = source
                    });
                });

                if (sig == "Armor" && data["Armatures"] is JsonArray arms)
                {
                    foreach (var a in arms.OfType<JsonObject>())
                    {
                        string addonFk = Sfs.NormalizeFormKey((a["ArmorAddon"] ?? a["AddonModel"])?.ToString() ?? "");
                        if (!string.IsNullOrEmpty(addonFk))
                            addonQueue.Add((fk, edid, addonFk));
                    }
                }
            }

            // Also check linked ArmorAddons that live in master plugins (e.g. when a patch or mod references an external ARMA)
            foreach (var (armorFk, armorEdid, addonFk) in addonQueue)
            {
                if (seenAddons.Contains(addonFk)) continue;
                seenAddons.Add(addonFk);
                string addonPlugin = Sfs.PluginOf(addonFk);
                if (!Sfs.EnsureMasterCache(addonPlugin)) continue;
                var addonRow = Sfs.FindRecord(addonPlugin, addonFk, "ArmorAddon", ensureUpToDate: false);
                if (addonRow == null) continue;

                // Ensure the addon plugin's BA2s are also indexed
                string addonPrefix = Path.GetFileNameWithoutExtension(addonPlugin);
                if (!addonPrefix.Equals("Starfield", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var arch in FindBa2ArchivesForPrefix(dataDir, addonPrefix))
                    {
                        string archName = Path.GetFileName(arch);
                        if (!ba2Index.Any(b => b.ArchiveName.Equals(archName, StringComparison.OrdinalIgnoreCase)))
                            ba2Index.Add((archName, LoadBa2FileList(arch)));
                    }
                }

                ExtractModelFiles(addonRow.Data, $"ARMA({addonRow.EditorID}).", (fieldPath, rawAsset) =>
                {
                    var (status, source) = ResolveAsset(rawAsset, addonFk, dataDir, ba2Index, hasStarfieldMaster);
                    results.Add(new AssetCheckResult
                    {
                        RecordFormKey = armorFk,
                        Signature = "Armor->ARMA",
                        EditorID = armorEdid,
                        Field = fieldPath,
                        AssetPath = rawAsset,
                        Status = status,
                        Source = source
                    });
                });
            }

            return results;
        }

        static void ExtractModelFiles(JsonNode? node, string prefix, Action<string, string> onAsset)
        {
            if (node is JsonObject obj)
            {
                foreach (var kvp in obj)
                {
                    string path = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}.{kvp.Key}";
                    if (kvp.Key == "File")
                    {
                        string? s = null;
                        if (kvp.Value is JsonValue v) s = v.ToString().Trim();
                        else if (kvp.Value is JsonObject fObj)
                            s = (fObj["DataRelativePath"]?["Path"] ?? fObj["GivenPath"])?.ToString().Trim();

                        if (!string.IsNullOrEmpty(s) && (s.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)))
                        {
                            onAsset(prefix, s);
                        }
                    }
                    else
                    {
                        ExtractModelFiles(kvp.Value, path, onAsset);
                    }
                }
            }
            else if (node is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                    ExtractModelFiles(arr[i], $"{prefix}[{i}]", onAsset);
            }
        }

        static List<(string ArchiveName, HashSet<string> Files)>? _vanillaMeshBa2s;

        static List<(string ArchiveName, HashSet<string> Files)> GetVanillaMeshBa2s(string dataDir)
        {
            if (_vanillaMeshBa2s != null) return _vanillaMeshBa2s;
            _vanillaMeshBa2s = new List<(string, HashSet<string>)>();
            foreach (var name in new[] { "Starfield - Meshes01.ba2", "Starfield - Meshes02.ba2", "Starfield - MeshesPatch.ba2" })
            {
                string full = Path.Combine(dataDir, name);
                if (File.Exists(full))
                    _vanillaMeshBa2s.Add((name, LoadBa2FileList(full)));
            }
            return _vanillaMeshBa2s;
        }

        static (string Status, string Source) ResolveAsset(
            string rawAsset,
            string recordFormKey,
            string dataDir,
            List<(string ArchiveName, HashSet<string> Files)> ba2Index,
            bool hasStarfieldMaster)
        {
            string norm = rawAsset.Replace('/', '\\').TrimStart('\\');
            var candidates = new List<string> { norm };
            if (!norm.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) && norm.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                candidates.Add("meshes\\" + norm);

            // 1. Loose file check in Data
            foreach (var c in candidates)
            {
                string full = Path.Combine(dataDir, c);
                if (File.Exists(full)) return ("OK (Loose)", c);
            }

            // 2. Mod BA2 check
            foreach (var c in candidates)
            {
                string slash = c.Replace('\\', '/');
                foreach (var (archName, files) in ba2Index)
                {
                    if (files.Contains(c) || files.Contains(slash))
                        return ("OK (BA2)", archName);
                }
            }

            // 3. Vanilla Starfield - Meshes*.ba2 check
            if (hasStarfieldMaster)
            {
                foreach (var c in candidates)
                {
                    string slash = c.Replace('\\', '/');
                    foreach (var (archName, files) in GetVanillaMeshBa2s(dataDir))
                    {
                        if (files.Contains(c) || files.Contains(slash))
                            return ("OK (Vanilla BA2)", archName);
                    }
                }
            }

            return ("MISSING", "Not found in loose files, mod BA2s, or vanilla BA2s");
        }

        public static List<string> FindBa2ArchivesForPrefix(string dataDir, string prefix)
        {
            if (!Directory.Exists(dataDir)) return new List<string>();
            return Directory.GetFiles(dataDir, "*.ba2")
                .Where(f =>
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    return name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(prefix + " - ", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static readonly Dictionary<string, HashSet<string>> _ba2Lists = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every file path inside one archive, in the form "meshes\\x\\y.nif". Reading the file list takes milliseconds, so it isn't stored on disk.</summary>
        public static HashSet<string> LoadBa2FileList(string ba2FullPath)
        {
            if (_ba2Lists.TryGetValue(ba2FullPath, out var known)) return known;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(ba2FullPath))
            {
                var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.Starfield, ba2FullPath);
                foreach (var f in reader.Files)
                    set.Add(f.Path.ToString().Trim().Replace('/', '\\').TrimStart('\\'));
            }
            return _ba2Lists[ba2FullPath] = set;
        }
    }
}
