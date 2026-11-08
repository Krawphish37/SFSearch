using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    public static class ProfileCommand
    {
        public static void Run(List<string> args)
        {
            string against = Sfs.TakeOption(args, "--against") ?? "darkstar.esm";
            string? outFile = Sfs.TakeOption(args, "--out");
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch Profile <plugin> [--against darkstar.esm] [--out dossier.md]");
                return;
            }

            string plugin = Program.EnsureCacheForFile(args[0]);
            TextWriter origOut = Console.Out;
            StreamWriter? fileWriter = null;
            if (!string.IsNullOrWhiteSpace(outFile))
            {
                string fullOut = Sfs.ResolveUserPath(outFile);
                Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);
                fileWriter = new StreamWriter(fullOut, append: false, System.Text.Encoding.UTF8);
                Console.SetOut(fileWriter);
            }

            try
            {
                GenerateDossier(plugin, against);
            }
            finally
            {
                if (fileWriter != null)
                {
                    fileWriter.Flush();
                    fileWriter.Dispose();
                    Console.SetOut(origOut);
                    Console.WriteLine($"Profile dossier written to: {Sfs.ResolveUserPath(outFile!)}");
                }
            }
        }

        static void GenerateDossier(string plugin, string against)
        {
            Sfs.Say($"# Compatibility Engineering Dossier: `{plugin}`\n");

            // 1. Header & BA2 Inventory
            Sfs.Say("## 1. Envelope, Header & Archives");
            InspectCommands.Header(new List<string> { plugin });

            var assetChecks = AssetsCommand.AuditPluginAssets(plugin, out var modBa2s);
            if (modBa2s.Count > 0)
            {
                Sfs.Say($"\n#### Shipped BA2 Archives ({modBa2s.Count})");
                var bt = new Sfs.Table("Archive", "TotalFiles", "Meshes", "Materials", "Textures", "Scripts (.pex)");
                foreach (var (arch, files) in modBa2s)
                {
                    int m = files.Count(f => f.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase));
                    int mat = files.Count(f => f.EndsWith(".mat", StringComparison.OrdinalIgnoreCase));
                    int tex = files.Count(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
                    int pex = files.Count(f => f.EndsWith(".pex", StringComparison.OrdinalIgnoreCase));
                    bt.Add(arch, files.Count, m, mat, tex, pex);
                }
                Sfs.Print(bt, annotate: false);
            }

            // 2. Record Census & Naming Prefixes
            Sfs.Say("\n## 2. Record Census & EditorID Prefixes");
            InspectCommands.Stats(new List<string> { plugin });

            using var conn = Sfs.OpenCache(plugin, ensureUpToDate: false);
            var masters = Sfs.Masters(conn);
            var records = new List<Sfs.RecordRow>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT FormKey, Signature, EditorID, Name, Data, IsOverride FROM Records ORDER BY FormKey";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    records.Add(new Sfs.RecordRow
                    {
                        Plugin = plugin,
                        FormKey = r.GetString(0),
                        Signature = r.GetString(1),
                        EditorID = r.IsDBNull(2) ? "" : r.GetString(2),
                        Name = r.IsDBNull(3) ? "" : r.GetString(3),
                        Data = (JsonNode.Parse(r.GetString(4)) as JsonObject) ?? new JsonObject(),
                        IsOverride = !r.IsDBNull(5) && r.GetInt64(5) == 1
                    });
                }
            }

            // IsOverride column, not "FormKey plugin != cache name" (a copy read by path has a "_file1_<name>.esm" cache).
            var newRecs = records.Where(r => !r.IsOverride).ToList();
            var overrides = records.Where(r => r.IsOverride).ToList();

            var prefixGroups = newRecs
                .Select(r =>
                {
                    int u = r.EditorID.IndexOf('_');
                    return u > 1 ? r.EditorID.Substring(0, u + 1) : "(no_prefix)";
                })
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToList();

            if (prefixGroups.Count > 0)
            {
                Sfs.Say("\n#### Dominant EditorID Prefixes (New Records)");
                var pt = new Sfs.Table("Prefix", "NewRecordCount");
                foreach (var g in prefixGroups) pt.Add(g.Key, g.Count());
                Sfs.Print(pt, annotate: false);
            }

            // 3. Override Intent (Delta)
            Sfs.Say("\n## 3. Override Intent & Delta Classification");
            if (overrides.Count == 0)
            {
                Sfs.Say("_Plugin contains 0 override records._");
            }
            else
            {
                InspectCommands.Delta(new List<string> { plugin });
            }

            // 4. Entry-Points, Playable Gear & Inbound Reference Graph
            Sfs.Say("\n## 4. Playable Equipment & Distribution Topology");
            var inboundCounts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (Sfs.TableExists(conn, "Refs"))
            {
                using var rcmd = conn.CreateCommand();
                rcmd.CommandText = "SELECT ToFormKey, FromSignature || ':' || FromEditorID FROM Refs";
                using var rr = rcmd.ExecuteReader();
                while (rr.Read())
                {
                    string to = rr.GetString(0);
                    string from = rr.GetString(1);
                    if (!inboundCounts.TryGetValue(to, out var list))
                    {
                        list = new List<string>();
                        inboundCounts[to] = list;
                    }
                    if (!list.Contains(from)) list.Add(from);
                }
            }

            var gear = records.Where(r => r.Signature is "Weapon" or "Armor").ToList();
            if (gear.Count > 0)
            {
                var gt = new Sfs.Table("FormKey", "Role", "Signature", "Archetype", "EditorID", "Name", "IsJunk", "InboundRefsInPlugin");
                foreach (var g in gear)
                {
                    bool isOvr = g.IsOverride;
                    string arch = g.Data["Archetype"]?.ToString() ?? "";
                    string junk = g.Data["IsJunk"]?.ToString() ?? "False";
                    inboundCounts.TryGetValue(g.FormKey, out var refs);
                    string refSummary = refs == null || refs.Count == 0 ? "0 (Unreferenced locally)" : $"{refs.Count} ({string.Join(", ", refs.Take(3))}{(refs.Count > 3 ? "..." : "")})";
                    gt.Add(g.FormKey, isOvr ? "OVERRIDE" : "NEW", g.Signature, arch, g.EditorID, g.Name, junk, refSummary);
                }
                Sfs.Print(gt, annotate: false);
            }
            else
            {
                Sfs.Say("_No Weapon or Armor records in this plugin._");
            }

            // 5. Asset Verification Summary
            Sfs.Say("\n## 5. Asset & Mesh Verification");
            int okLoose = assetChecks.Count(a => a.Status == "OK (Loose)");
            int okBa2 = assetChecks.Count(a => a.Status == "OK (BA2)");
            int okVan = assetChecks.Count(a => a.Status == "OK (Vanilla BA2)");
            var missing = assetChecks.Where(a => a.Status == "MISSING").ToList();
            Sfs.Say($"- **Total Model Paths Checked**: {assetChecks.Count} (`OK (BA2)`: {okBa2}, `OK (Loose)`: {okLoose}, `OK (Vanilla BA2)`: {okVan}, `MISSING`: {missing.Count})");
            if (missing.Count > 0)
            {
                var mt = new Sfs.Table("FormKey", "Signature", "EditorID", "Field", "AssetPath");
                foreach (var m in missing) mt.Add(m.RecordFormKey, m.Signature, m.EditorID, m.Field, m.AssetPath);
                Sfs.Print(mt, annotate: false);
            }

            // 6. Conflict & Patch Surface vs target (e.g. darkstar.esm)
            if (!string.IsNullOrWhiteSpace(against) && !against.Equals(plugin, StringComparison.OrdinalIgnoreCase))
            {
                Sfs.Say($"\n## 6. Compatibility & Conflict Surface vs `{against}`");
                if (!Sfs.HasCache(against))
                {
                    try { Program.EnsureCacheUpToDatePublic(against); } catch { }
                }

                if (Sfs.HasCache(against))
                {
                    using var aConn = Sfs.OpenCache(against, ensureUpToDate: false);
                    var collisions = new Sfs.Table("FormKey", "Signature", "EditorID", $"{plugin} Changes", $"{against} Changes");

                    foreach (var r in records)
                    {
                        var aRow = Sfs.FindRecord(aConn, against, r.FormKey, "*");
                        if (aRow == null) continue;

                        var baseRow = InspectCommands.FindPrecedingRecord(r.FormKey, masters);
                        var pDiff = baseRow != null ? InspectCommands.GetChangedTopFields(baseRow.Data, r.Data) : new List<string> { "(origin)" };
                        var aDiff = baseRow != null ? InspectCommands.GetChangedTopFields(baseRow.Data, aRow.Data) : InspectCommands.GetChangedTopFields(r.Data, aRow.Data);

                        collisions.Add(
                            r.FormKey,
                            r.Signature,
                            r.EditorID,
                            pDiff.Count == 0 ? "NONE" : string.Join(", ", pDiff),
                            aDiff.Count == 0 ? "NONE" : string.Join(", ", aDiff));
                    }

                    Sfs.Say($"#### Direct Record Collisions with `{against}` ({collisions.Rows.Count})");
                    if (collisions.Rows.Count > 0) Sfs.Print(collisions, annotate: false);
                    else Sfs.Say($"_No overlapping FormKeys between `{plugin}` and `{against}`._");

                    // Also check if overridden Weapons/Armors have DarkStar_Mk2_* counterparts in against (e.g. darkstar.esm)
                    var gearOverrides = overrides.Where(r => r.Signature is "Armor" or "Weapon").ToList();
                    if (gearOverrides.Count > 0)
                    {
                        var mk2Table = new Sfs.Table("OverriddenRecord", "EditorID", "ModChangesVsVanilla", $"CounterpartIn({against})", "VisualSyncNeeded?");
                        foreach (var ovr in gearOverrides)
                        {
                            var baseRow = InspectCommands.FindPrecedingRecord(ovr.FormKey, masters);
                            var changed = baseRow != null ? InspectCommands.GetChangedTopFields(baseRow.Data, ovr.Data) : new List<string>();
                            bool touchesVisuals = changed.Any(f => f is "Armatures" or "WorldModel" or "FirstPersonModel" or "Model");

                            Sfs.RecordRow? mk2 = FindDarkStarMk2Counterpart(aConn, against, ovr.EditorID);
                            mk2Table.Add(
                                ovr.FormKey,
                                ovr.EditorID,
                                changed.Count == 0 ? "NONE (ITM)" : string.Join(", ", changed),
                                mk2 != null ? $"{mk2.FormKey} ({mk2.EditorID})" : "(none found)",
                                (mk2 != null && touchesVisuals) ? "YES (WorldModel/Armatures)" : "No");
                        }
                        Sfs.Say($"\n#### Overridden Equipment -> `{against}` Mk.II Counterparts ({gearOverrides.Count})");
                        Sfs.Print(mk2Table, annotate: false);
                    }

                    var playableNewGear = newRecs.Where(r =>
                        (r.Signature == "Weapon" || r.Signature == "Armor") &&
                        !string.Equals(r.Data["IsJunk"]?.ToString(), "True", StringComparison.OrdinalIgnoreCase)).ToList();

                    Sfs.Say($"\n#### New Playable Equipment Requiring Patch Integration ({playableNewGear.Count})");
                    if (playableNewGear.Count > 0)
                    {
                        var nt = new Sfs.Table("FormKey", "Signature", "Archetype", "EditorID", "Name");
                        foreach (var g in playableNewGear)
                            nt.Add(g.FormKey, g.Signature, g.Data["Archetype"]?.ToString() ?? "", g.EditorID, g.Name);
                        Sfs.Print(nt, annotate: false);
                    }
                    else
                    {
                        Sfs.Say("_No new non-junk Weapons or Armors defined in this plugin._");
                    }
                }
            }
        }

        static Sfs.RecordRow? FindDarkStarMk2Counterpart(SqliteConnection dsConn, string dsPlugin, string baseEdid)
        {
            var candidates = new List<string> { $"DarkStar_Mk2_{baseEdid}" };
            if (baseEdid.StartsWith("Spacesuit_", StringComparison.OrdinalIgnoreCase))
            {
                string core = baseEdid.Substring("Spacesuit_".Length);
                if (core.EndsWith("_Helmet", StringComparison.OrdinalIgnoreCase))
                    candidates.Add($"DarkStar_Mk2_Helmet_{core.Substring(0, core.Length - "_Helmet".Length)}");
                else if (core.EndsWith("_Backpack", StringComparison.OrdinalIgnoreCase))
                    candidates.Add($"DarkStar_Mk2_Backpack_{core.Substring(0, core.Length - "_Backpack".Length)}");
                else
                    candidates.Add($"DarkStar_Mk2_Spacesuit_{core}");
            }
            foreach (var c in candidates)
            {
                var row = Sfs.FindRecord(dsConn, dsPlugin, c, "*");
                if (row != null) return row;
            }
            return null;
        }
    }
}
