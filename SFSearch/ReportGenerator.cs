using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Environments;

namespace SFSearch
{
    public static class ReportGenerator
    {
        public static void GenerateReport(bool isWeapon, List<string> targets, bool useLive, int quality = 0)
        {
            targets = targets.Where(t => !t.Equals("DarkStar.esm", StringComparison.OrdinalIgnoreCase) && 
                                         !t.StartsWith("DarkStarPatch_", StringComparison.OrdinalIgnoreCase)).ToList();

            if (isWeapon)
            {
                GenerateWeaponReport(targets, useLive, quality);
            }
            else
            {
                GenerateArmorReport(targets, useLive);
            }
        }

        private static void GenerateWeaponReport(List<string> targets, bool useLive, int quality)
        {
            var results = new Dictionary<string, Dictionary<string, Dictionary<string, List<string>>>>();
            var filteredOut = new List<string>();

            if (useLive)
            {
                Console.WriteLine("Loading Game Environment for Report Generation...");
                var env = GameEnvironment.Typical.Starfield(StarfieldRelease.Starfield);
                var linkCache = env.LinkCache;

                foreach (var modName in targets)
                {
                    var mod = env.LoadOrder.ListedOrder.FirstOrDefault(x => x.ModKey.FileName.String.Equals(modName, StringComparison.OrdinalIgnoreCase));
                    if (mod == null || mod.Mod == null) continue;

                    foreach (var wpn in mod.Mod.Weapons)
                    {
                        if (wpn.Name == null || string.IsNullOrWhiteSpace(wpn.Name.String)) continue;
                        if (wpn.IsDeleted) continue;
                        
                        string wpnName = (wpn.Name?.String ?? "Unknown").Replace("\r", "").Replace("\n", "").Trim();
                        
                        if (MetadataExtractor.IsJunkWeapon(wpn, linkCache))
                        {
                            filteredOut.Add($"| <sub>*{wpn.EditorID ?? wpn.FormKey.ToString()}*</sub> | {wpnName} |");
                            continue;
                        }

                        string topGroup = ItemFilter.GetWeaponGroup(wpn).ToString();
                        string subGroup = ItemFilter.GetWeaponClass(wpn, linkCache).ToString() == "Standard" ? $"{ItemFilter.GetWeaponKind(wpn).ToString()}" : $"{ItemFilter.GetWeaponClass(wpn, linkCache).ToString()} - {ItemFilter.GetWeaponKind(wpn).ToString()}";
                        
                        if (topGroup == "Melee")
                        {
                            subGroup = ItemFilter.GetWeaponClass(wpn, linkCache).ToString() == "Polearm" ? "Polearm" : "Standard";
                        }
                        
                        string dmgType = ItemFilter.GetWeaponType(wpn).ToString();

                        if (!results.ContainsKey(topGroup)) results[topGroup] = new Dictionary<string, Dictionary<string, List<string>>>();
                        if (!results[topGroup].ContainsKey(subGroup)) results[topGroup][subGroup] = new Dictionary<string, List<string>>();
                        if (!results[topGroup][subGroup].ContainsKey(dmgType)) results[topGroup][subGroup][dmgType] = new List<string>();

                        float baseDmg = ItemFilter.GetTotalDamage(wpn, linkCache);
                        float sdps = ItemFilter.GetSDPS(wpn, linkCache);
                        
                        if (quality > 0)
                        {
                            IAObjectModificationGetter? qMod = null;
                            var qComp = wpn.Components?.OfType<IQualityUpgradeComponentGetter>().FirstOrDefault();
                            if (qComp != null && qComp.QualityMods != null && qComp.QualityMods.Count >= quality)
                            {
                                var qLink = qComp.QualityMods[quality - 1];
                                if (!qLink.IsNull && qLink.TryResolve(linkCache, out var resolvedMod))
                                {
                                    qMod = resolvedMod;
                                }
                            }

                            if (qMod != null && qMod is IWeaponModificationGetter wMod && wMod.Properties != null) {
                                    float qualityMult = 0f;
                                    float qualityAdd = 0f;
                                    Mutagen.Bethesda.Plugins.IFormLinkGetter<Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter>? projOverrideLink = null;

                                    foreach (var prop in wMod.Properties) {
                                        if (prop is ObjectModFloatProperty<Mutagen.Bethesda.Starfield.Weapon.Property> fProp && fProp.Property == Mutagen.Bethesda.Starfield.Weapon.Property.DamagePhysical) {
                                            if (fProp.FunctionType == ObjectModProperty.FloatFunctionType.MultAndAdd) qualityMult += fProp.Value;
                                            if (fProp.FunctionType == ObjectModProperty.FloatFunctionType.Add) qualityAdd += fProp.Value;
                                        } else if (prop is ObjectModFormLinkIntProperty<Mutagen.Bethesda.Starfield.Weapon.Property> projProp && projProp.Property.ToString() == "Projectile") {
                                            projOverrideLink = projProp.Record;
                                        }
                                    }

                                    if (projOverrideLink != null && !projOverrideLink.IsNull && linkCache.TryResolve<IProjectileGetter>(projOverrideLink.FormKey, out var resProj)) {
                                        if (resProj.Explosion != null && !resProj.Explosion.IsNull && linkCache.TryResolve<IExplosionGetter>(resProj.Explosion.FormKey, out var exp)) {
                                            float expDmg = exp.Damage;
                                            float projCount = wpn.ProjectilesCount > 0 ? wpn.ProjectilesCount : 1f;
                                            float newExpTotal = expDmg * projCount;
                                            
                                            // Subtract old explosion damage
                                            var oldProj = ItemFilter.GetWeaponProjectile(wpn, linkCache);
                                            if (oldProj != null && oldProj.Explosion != null && !oldProj.Explosion.IsNull && linkCache.TryResolve<IExplosionGetter>(oldProj.Explosion.FormKey, out var oldExp)) {
                                                baseDmg -= (oldExp.Damage * projCount);
                                            }
                                            
                                            baseDmg += newExpTotal;
                                            
                                            float ats = wpn.AttackSeconds > 0 ? wpn.AttackSeconds : 0.1f;
                                            float sps = wpn.ShotsPerSecond > 0 ? wpn.ShotsPerSecond : 1.0f;
                                            int cap = wpn.AmmoCapacity > 0 ? (int)wpn.AmmoCapacity : 1;
                                            float rs = wpn.ReloadSpeed > 0 ? wpn.ReloadSpeed : 1.0f;
                                            float realAttackSec = (sps > 0) ? (1.0f / sps) : ats;
                                            if (realAttackSec <= 0) realAttackSec = 0.1f;
                                            
                                            if (topGroup == "Melee") {
                                                sdps = baseDmg / realAttackSec;
                                            } else {
                                                sdps = (cap * baseDmg) / ((cap * realAttackSec) + (1.0f / rs));
                                            }
                                        }
                                    }

                                    if (qualityMult > 0f) {
                                        baseDmg += (baseDmg * qualityMult);
                                        sdps += (sdps * qualityMult);
                                    }
                                    if (qualityAdd > 0f) {
                                        baseDmg += qualityAdd;
                                        
                                        float ats = wpn.AttackSeconds > 0 ? wpn.AttackSeconds : 0.1f;
                                        float sps = wpn.ShotsPerSecond > 0 ? wpn.ShotsPerSecond : 1.0f;
                                        int cap = wpn.AmmoCapacity > 0 ? (int)wpn.AmmoCapacity : 1;
                                        float rs = wpn.ReloadSpeed > 0 ? wpn.ReloadSpeed : 1.0f;
                                        float realAttackSec = (sps > 0) ? (1.0f / sps) : ats;
                                        if (realAttackSec <= 0) realAttackSec = 0.1f;
                                            
                                        if (topGroup == "Melee") {
                                            sdps = baseDmg / realAttackSec;
                                        } else {
                                            sdps = (cap * baseDmg) / ((cap * realAttackSec) + (1.0f / rs));
                                        }
                                    }
                                }
                        }

                        string entry = "";
                        if (topGroup == "Melee")
                        {
                            entry = $"| <sub>*{wpn.EditorID ?? wpn.FormKey.ToString()}*</sub> | {wpnName} | {baseDmg:F1} | {wpn.AttackSeconds:F2} | {sdps:F1} |";
                        }
                        else
                        {
                            entry = $"| <sub>*{wpn.EditorID ?? wpn.FormKey.ToString()}*</sub> | {wpnName} | {baseDmg:F1} | {wpn.ShotsPerSecond:F1} | {wpn.AmmoCapacity} | {wpn.ReloadSpeed} | {sdps:F1} |";
                        }
                        if (!results[topGroup][subGroup][dmgType].Contains(entry))
                        {
                            results[topGroup][subGroup][dmgType].Add(entry);
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine("Weapon reports MUST be generated using --live to correctly calculate damage scaling for Launchers and utilize ItemFilter.");
                return;
            }

            string output = $"# Universal Weapon Categorization (Quality: {quality})\n\n";
            foreach (var tg in results.Keys.OrderBy(k => k))
            {
                output += $"## {tg}\n";
                foreach (var sg in results[tg].Keys.OrderBy(k => k))
                {
                    foreach (var dt in results[tg][sg].Keys.OrderBy(k => k))
                    {
                        output += $"### {sg} - {dt}\n";

                        if (tg == "Melee")
                        {
                            output += "| Editor ID | Name | Dmg | Swing Speed | SDPS |\n| :--- | :--- | :--- | :--- | :--- |\n";
                        }
                        else
                        {
                            output += "| Editor ID | Name | Dmg | Fire Rate | Ammo | Reload | SDPS |\n| :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n";
                        }
                        var list = results[tg][sg][dt].Distinct().OrderBy(x => x).ToList();
                        foreach (var item in list)
                        {
                            output += $"{item}\n";
                        }
                        output += "\n";
                    }
                }
            }
            string qSuffix = quality > 0 ? $"_q{quality}" : "";
            string path = Path.Combine(Environment.CurrentDirectory, $"weapon_report{qSuffix}.md");
            File.WriteAllText(path, output);
            Console.WriteLine($"Wrote weapon report to {path}");

            if (filteredOut.Count > 0)
            {
                string filterOutput = $"# Filtered Weapons (Quality: {quality})\n\n| Editor ID | Name |\n| :--- | :--- |\n";
                foreach(var entry in filteredOut.Distinct().OrderBy(x => x))
                {
                    filterOutput += entry + "\n";
                }
                string filterPath = Path.Combine(Environment.CurrentDirectory, $"filtered_weapons_report{qSuffix}.md");
                File.WriteAllText(filterPath, filterOutput);
                Console.WriteLine($"Wrote filtered weapons report to {filterPath}");
            }
        }

        private static void GenerateArmorReport(List<string> targets, bool useLive)
        {
            var results = new Dictionary<string, List<string>>();
            var filteredOut = new List<string>();

            if (useLive)
            {
                Console.WriteLine("Loading Game Environment for Report Generation...");
                var env = GameEnvironment.Typical.Starfield(StarfieldRelease.Starfield);
                var linkCache = env.LinkCache;
                
                foreach (var modName in targets)
                {
                    var mod = env.LoadOrder.ListedOrder.FirstOrDefault(x => x.ModKey.FileName.String.Equals(modName, StringComparison.OrdinalIgnoreCase));
                    if (mod == null || mod.Mod == null) continue;

                    foreach (var arm in mod.Mod.Armors)
                    {
                        if (arm.Name == null || string.IsNullOrWhiteSpace(arm.Name.String)) continue;
                        if (arm.IsDeleted) continue;
                        
                        if (MetadataExtractor.IsJunkArmor(arm)) 
                        {
                            filteredOut.Add($"| <sub>*{arm.EditorID ?? arm.FormKey.ToString()}*</sub> | {arm.Name?.String ?? "Unknown"} |");
                            continue;
                        }

                        string archetype = MetadataExtractor.GetArmorArchetype(arm);
                        if (archetype == "Unknown") continue;

                        if (!results.ContainsKey(archetype)) results[archetype] = new List<string>();

                        float phys = 0f, engy = 0f, em = 0f;
                        if (arm.Resistances != null)
                        {
                            foreach (var res in arm.Resistances)
                            {
                                if (res.DamageType.FormKey.ID == 0x023190) phys += res.Value;
                                else if (res.DamageType.FormKey.ID == 0x060A81) engy += res.Value;
                                else if (res.DamageType.FormKey.ID == 0x060A87) em += res.Value;
                            }
                        }

                        string entry = $"| <sub>*{arm.EditorID ?? arm.FormKey.ToString()}*</sub> | {arm.Name?.String ?? "Unknown"} | {phys} | {engy} | {em} | {arm.Weight} | {arm.Value} |";
                        if (!results[archetype].Contains(entry))
                        {
                            results[archetype].Add(entry);
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine("Reading from SQLite Databases...");
                foreach (var target in targets)
                {
                    string dbPath = Program.GetCachePath(target);
                    if (!File.Exists(dbPath)) continue;

                    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Data FROM Records WHERE Signature = 'Armor'";
                    using var reader = cmd.ExecuteReader();

                    while (reader.Read())
                    {
                        var json = reader.GetString(0);
                        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
                        
                        bool isJunk = false;
                        if (node?["IsJunk"] != null) isJunk = node?["IsJunk"]?.GetValue<bool>() ?? false;
                        if (isJunk) continue;

                        string edid = node?["EDID - Editor ID"]?.ToString() ?? "";
                        string name = node?["Name"]?.ToString() ?? "";
                        
                        string archetype = node?["Archetype"]?.ToString() ?? "Unknown";

                        if (!results.ContainsKey(archetype)) results[archetype] = new List<string>();

                        float phys = 0f, engy = 0f, em = 0f;
                        var resistances = node?["Resistances"] as System.Text.Json.Nodes.JsonArray;
                        if (resistances != null) {
                            foreach (var item in resistances) {
                                if (item is System.Text.Json.Nodes.JsonObject obj && obj.ContainsKey("DamageType") && obj.ContainsKey("Value")) {
                                    string dt = obj["DamageType"]?.ToString() ?? "";
                                    if (float.TryParse(obj["Value"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float val)) {
                                        if (dt.Contains("023190")) phys += val;
                                        else if (dt.Contains("060A81")) engy += val;
                                        else if (dt.Contains("060A87")) em += val;
                                    }
                                }
                            }
                        }

                        float weight = 0f;
                        if (node?["Weight"] != null && float.TryParse(node?["Weight"]?.ToString() ?? "", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float w)) weight = w;
                        float value = 0f;
                        if (node?["Value"] != null && float.TryParse(node?["Value"]?.ToString() ?? "", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float v)) value = v;

                        string entry = $"| <sub>*{edid}*</sub> | {name} | {phys} | {engy} | {em} | {weight} | {value} |";
                        if (!results[archetype].Contains(entry))
                        {
                            results[archetype].Add(entry);
                        }
                    }
                }
            }

            string output = "# Universal Armor Categorization\n\n";
            foreach (var archetype in results.Keys.OrderBy(k => k))
            {
                output += $"## {archetype}\n";
                output += "| Editor ID | Name | Phys | Engy | EM | Mass | Value |\n| :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n";
                var list = results[archetype].Distinct().OrderBy(x => x).ToList();
                foreach (var item in list)
                {
                    output += $"{item}\n";
                }
                output += "\n";
            }
            string path = Path.Combine(Environment.CurrentDirectory, "armor_report.md");
            File.WriteAllText(path, output);
            Console.WriteLine($"Wrote armor report to {path}");
            
            if (filteredOut.Count > 0)
            {
                string filterOutput = "# Filtered Armors\n\n| Editor ID | Name |\n| :--- | :--- |\n";
                foreach(var entry in filteredOut.Distinct().OrderBy(x => x))
                {
                    filterOutput += entry + "\n";
                }
                string filterPath = Path.Combine(Environment.CurrentDirectory, "filtered_armors_report.md");
                File.WriteAllText(filterPath, filterOutput);
                Console.WriteLine($"Wrote filtered armors report to {filterPath}");
            }
        }
    }
}

