using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    public static class SavedQueries
    {
        public static readonly (string Name, string Args, string Description)[] Catalog = new[]
        {
            ("flst",      "<target> [FormKey|EDID]",        "List FormLists in <target>, or expand one FormList with 0-based index & resolved item details"),
            ("vmad",      "<target> [FormKey|EDID|filter]", "Unpack VirtualMachineAdapter scripts & properties across records"),
            ("appr",      "<target> [FormKey|EDID|filter]", "Unpack AttachParentSlots (APPR) on Weapons/Armors"),
            ("templates", "<target> [FormKey|EDID|filter]", "Unpack ObjectTemplates and their included OMODs on Weapons/Armors"),
            ("kw",        "<target> [FormKey|EDID|filter]", "Unpack Keywords on records"),
            ("cobj",      "<target> [filter]",              "Summary table of ConstructibleObject (COBJ) recipes"),
            ("overrides", "<target> [Signature]",           "List all override records (IsOverride = 1) in <target>"),
            ("new",       "<target> [Signature]",           "List all new/original records (IsOverride = 0) in <target>"),
            ("arma",      "<target> [FormKey|EDID|filter]", "Map Armor -> Armatures (ARMA) -> Male/Female WorldModel & FirstPersonFlags"),
            ("omod",      "<target> [FormKey|EDID|filter]", "Summary table of ObjectModification (OMOD) records (AttachPoint, TargetOmodKeywords, Mesh, etc.)")
        };

        public static void Run(List<string> args, bool useLive)
        {
            if (args.Count == 0 || Sfs.TakeFlag(args, "--list") || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var t = new Sfs.Table("Macro", "Usage", "Description");
                foreach (var c in Catalog) t.Add(c.Name, $"Q {c.Name} {c.Args}", c.Description);
                Sfs.Print(t, annotate: false);
                return;
            }

            string macro = args[0].ToLowerInvariant();
            if (args.Count < 2)
            {
                Console.WriteLine($"Usage: SFSearch Q {macro} <target> [filter]");
                return;
            }

            var targets = Program.TargetResolver(args[1], useLive);
            string? filter = args.Count > 2 ? args[2] : null;
            bool multi = targets.Count > 1;

            switch (macro)
            {
                case "flst": RunFlst(targets, filter, multi); break;
                case "vmad": RunVmad(targets, filter, multi); break;
                case "appr": RunArrayUnpack(targets, filter, multi, "AttachParentSlots", "SlotKeyword"); break;
                case "kw":   RunArrayUnpack(targets, filter, multi, "Keywords", "Keyword"); break;
                case "templates": RunTemplates(targets, filter, multi); break;
                case "cobj": RunCobj(targets, filter, multi); break;
                case "overrides": RunOriginFilter(targets, filter, multi, wantOverride: true); break;
                case "new":       RunOriginFilter(targets, filter, multi, wantOverride: false); break;
                case "arma": RunArma(targets, filter, multi); break;
                case "omod": RunOmod(targets, filter, multi); break;
                default:
                    Console.WriteLine($"Unknown saved query '{macro}'. Run 'SFSearch Q --list' for available macros.");
                    break;
            }
        }

        static bool MatchesFilter(Sfs.RecordRow r, string? filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            if (Sfs.TryParseKey(filter, r.Plugin, out string fk) && r.FormKey.Equals(fk, StringComparison.OrdinalIgnoreCase)) return true;
            return r.EditorID.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.FormKey.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<Sfs.RecordRow> ScanRecords(string plugin, string? signatureClause, string? filter)
        {
            using var conn = Sfs.OpenCache(plugin);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrEmpty(signatureClause)
                ? "SELECT FormKey, Signature, EditorID, Name, Data, IsOverride FROM Records ORDER BY FormKey" // a fixed order: databases filled type by type (the service) store rows in another order
                : $"SELECT FormKey, Signature, EditorID, Name, Data, IsOverride FROM Records WHERE {signatureClause} ORDER BY FormKey";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Sfs.RecordRow
                {
                    Plugin = plugin,
                    FormKey = reader.GetString(0),
                    Signature = reader.GetString(1),
                    EditorID = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Name = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Data = (JsonNode.Parse(reader.GetString(4)) as JsonObject) ?? new JsonObject(),
                    IsOverride = !reader.IsDBNull(5) && reader.GetInt64(5) == 1
                };
                if (MatchesFilter(row, filter)) yield return row;
            }
        }

        static void RunFlst(List<string> targets, string? filter, bool multi)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                var cols = new List<string>();
                if (multi) cols.Add("Plugin");
                cols.AddRange(new[] { "FormKey", "EditorID", "Name", "Count", "IsOverride" });
                var t = new Sfs.Table(cols.ToArray());

                foreach (var p in targets)
                {
                    foreach (var r in ScanRecords(p, "Signature = 'FormList'", null))
                    {
                        int cnt = (r.Data["Items"] as JsonArray)?.Count ?? 0;
                        bool isOvr = r.IsOverride;
                        if (multi) t.Add(p, r.FormKey, r.EditorID, r.Name, cnt, isOvr ? "1" : "0");
                        else t.Add(r.FormKey, r.EditorID, r.Name, cnt, isOvr ? "1" : "0");
                    }
                }
                Sfs.Print(t, annotate: false);
                return;
            }

            var itemCols = new List<string>();
            if (multi) itemCols.Add("Plugin");
            itemCols.AddRange(new[] { "FlstEditorID", "Idx", "ItemFormKey", "ItemEditorID", "ItemSig", "ItemName" });
            var table = new Sfs.Table(itemCols.ToArray());

            foreach (var p in targets)
            {
                foreach (var r in ScanRecords(p, "Signature = 'FormList'", filter))
                {
                    var items = r.Data["Items"] as JsonArray;
                    if (items == null) continue;
                    for (int i = 0; i < items.Count; i++)
                    {
                        string itemFk = Sfs.NormalizeFormKey(items[i]?.ToString() ?? "");
                        string itemPlugin = Sfs.PluginOf(itemFk);
                        var resolved = Sfs.FindRecord(p, itemFk, "*", ensureUpToDate: false)
                                    ?? (Sfs.HasCacheOrBuild(itemPlugin) ? Sfs.FindRecord(itemPlugin, itemFk, "*", ensureUpToDate: false) : null);
                        string itemEdid = resolved?.EditorID ?? Sfs.EditorIdOf(itemFk) ?? "";
                        string itemSig = resolved?.Signature ?? "";
                        string itemName = resolved?.Name ?? "";
                        if (multi) table.Add(p, r.EditorID, i, itemFk, itemEdid, itemSig, itemName);
                        else table.Add(r.EditorID, i, itemFk, itemEdid, itemSig, itemName);
                    }
                }
            }
            Sfs.Print(table, annotate: false);
        }

        static void RunVmad(List<string> targets, string? filter, bool multi)
        {
            var cols = new List<string>();
            if (multi) cols.Add("Plugin");
            cols.AddRange(new[] { "FormKey", "Signature", "EditorID", "Script", "Property", "Value" });
            var t = new Sfs.Table(cols.ToArray());

            foreach (var p in targets)
            {
                foreach (var r in ScanRecords(p, "Data LIKE '%VirtualMachineAdapter%'", filter))
                {
                    var scripts = r.Data["VirtualMachineAdapter"]?["Scripts"] as JsonArray;
                    if (scripts == null) continue;
                    foreach (var s in scripts.OfType<JsonObject>())
                    {
                        string sName = s["Name"]?.ToString() ?? "";
                        var props = s["Properties"] as JsonArray;
                        if (props == null || props.Count == 0)
                        {
                            if (multi) t.Add(p, r.FormKey, r.Signature, r.EditorID, sName, "", "");
                            else t.Add(r.FormKey, r.Signature, r.EditorID, sName, "", "");
                            continue;
                        }
                        foreach (var prop in props.OfType<JsonObject>())
                        {
                            string pName = prop["Name"]?.ToString() ?? "";
                            string pVal = prop["Object"]?.ToString() ?? prop["Data"]?.ToString() ?? Sfs.Compact(prop);
                            if (multi) t.Add(p, r.FormKey, r.Signature, r.EditorID, sName, pName, pVal);
                            else t.Add(r.FormKey, r.Signature, r.EditorID, sName, pName, pVal);
                        }
                    }
                }
            }
            Sfs.Print(t);
        }

        static void RunArrayUnpack(List<string> targets, string? filter, bool multi, string fieldName, string colLabel)
        {
            var cols = new List<string>();
            if (multi) cols.Add("Plugin");
            cols.AddRange(new[] { "FormKey", "Signature", "EditorID", "Idx", colLabel });
            var t = new Sfs.Table(cols.ToArray());

            foreach (var p in targets)
            {
                foreach (var r in ScanRecords(p, $"Data LIKE '%\"{fieldName}\"%'", filter))
                {
                    var arr = r.Data[fieldName] as JsonArray;
                    if (arr == null) continue;
                    for (int i = 0; i < arr.Count; i++)
                    {
                        string val = Sfs.Compact(arr[i]);
                        if (multi) t.Add(p, r.FormKey, r.Signature, r.EditorID, i, val);
                        else t.Add(r.FormKey, r.Signature, r.EditorID, i, val);
                    }
                }
            }
            Sfs.Print(t);
        }

        static void RunTemplates(List<string> targets, string? filter, bool multi)
        {
            var cols = new List<string>();
            if (multi) cols.Add("Plugin");
            cols.AddRange(new[] { "FormKey", "Signature", "EditorID", "TemplateIdx", "Default", "IncludedMods" });
            var t = new Sfs.Table(cols.ToArray());

            foreach (var p in targets)
            {
                foreach (var r in ScanRecords(p, "Data LIKE '%ObjectTemplates%'", filter))
                {
                    var arr = r.Data["ObjectTemplates"] as JsonArray;
                    if (arr == null) continue;
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var tmpl = arr[i] as JsonObject;
                        if (tmpl == null) continue;
                        string isDef = tmpl["Default"]?.ToString() ?? "False";
                        var incs = (tmpl["Includes"] as JsonArray)?
                            .OfType<JsonObject>()
                            .Select(x => x["Mod"]?.ToString() ?? "")
                            .Where(x => x.Length > 0)
                            .ToList() ?? new List<string>();
                        string modsJoined = string.Join(", ", incs);
                        if (multi) t.Add(p, r.FormKey, r.Signature, r.EditorID, i, isDef, modsJoined);
                        else t.Add(r.FormKey, r.Signature, r.EditorID, i, isDef, modsJoined);
                    }
                }
            }
            Sfs.Print(t);
        }

        static void RunCobj(List<string> targets, string? filter, bool multi)
        {
            var cols = new List<string>();
            if (multi) cols.Add("Plugin");
            cols.AddRange(new[] { "FormKey", "EditorID", "Description", "CreatedObject", "WorkbenchKeyword", "RecipeFilters", "Components" });
            var t = new Sfs.Table(cols.ToArray());

            foreach (var p in targets)
            {
                foreach (var r in ScanRecords(p, "Signature = 'ConstructibleObject'", filter))
                {
                    string desc = Sfs.Compact(r.Data["Description"]);
                    string created = r.Data["CreatedObject"]?.ToString() ?? "";
                    string bench = r.Data["WorkbenchKeyword"]?.ToString() ?? "";
                    int cats = ((r.Data["RecipeFilters"] ?? r.Data["Categories"]) as JsonArray)?.Count ?? 0;
                    int comps = ((r.Data["ConstructibleComponents"] ?? r.Data["ConstructableComponents"]) as JsonArray)?.Count ?? 0;
                    if (multi) t.Add(p, r.FormKey, r.EditorID, desc, created, bench, cats, comps);
                    else t.Add(r.FormKey, r.EditorID, desc, created, bench, cats, comps);
                }
            }
            Sfs.Print(t);
        }

        static void RunOriginFilter(List<string> targets, string? sigFilter, bool multi, bool wantOverride)
        {
            var cols = new List<string>();
            if (multi) cols.Add("Plugin");
            cols.AddRange(new[] { "FormKey", "Signature", "EditorID", "Name", "Origin" });
            var t = new Sfs.Table(cols.ToArray());

            string? normSig = string.IsNullOrWhiteSpace(sigFilter) ? null : Program.NormalizeRecordTypePublic(sigFilter);
            foreach (var p in targets)
            {
                using var conn = Sfs.OpenCache(p);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT FormKey, Signature, EditorID, Name FROM Records ORDER BY FormKey";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string fk = reader.GetString(0);
                    string sig = reader.GetString(1);
                    string edid = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string name = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    string origin = Sfs.PluginOf(fk);
                    bool isOvr = !origin.Equals(p, StringComparison.OrdinalIgnoreCase);
                    if (isOvr != wantOverride) continue;
                    if (normSig != null && !sig.Equals(normSig, StringComparison.OrdinalIgnoreCase) && !edid.Contains(sigFilter!, StringComparison.OrdinalIgnoreCase)) continue;
                    if (multi) t.Add(p, fk, sig, edid, name, origin);
                    else t.Add(fk, sig, edid, name, origin);
                }
            }
            Sfs.Print(t, annotate: false);
        }

        static string ExtractModelPath(JsonNode? modelNode)
        {
            if (modelNode == null) return "";
            var f = modelNode["File"];
            if (f is JsonValue v) return v.ToString();
            if (f is JsonObject o) return (o["DataRelativePath"]?["Path"] ?? o["GivenPath"])?.ToString() ?? "";
            return "";
        }

        static void RunArma(List<string> targets, string? filter, bool multi)
        {
            var cols = new List<string>();
            if (multi) cols.Add("Plugin");
            cols.AddRange(new[] { "ArmorFormKey", "ArmorEDID", "AddonFormKey", "MaleWorldModel", "FemaleWorldModel", "FirstPersonFlags" });
            var t = new Sfs.Table(cols.ToArray());

            foreach (var p in targets)
            {
                foreach (var r in ScanRecords(p, "Signature = 'Armor'", filter))
                {
                    var arms = r.Data["Armatures"] as JsonArray;
                    if (arms == null) continue;
                    foreach (var a in arms.OfType<JsonObject>())
                    {
                        string addonFk = Sfs.NormalizeFormKey((a["ArmorAddon"] ?? a["AddonModel"])?.ToString() ?? "");
                        var addon = Sfs.FindRecord(p, addonFk, "ArmorAddon", ensureUpToDate: false)
                                 ?? (Sfs.HasCacheOrBuild(Sfs.PluginOf(addonFk)) ? Sfs.FindRecord(Sfs.PluginOf(addonFk), addonFk, "ArmorAddon", ensureUpToDate: false) : null);
                        var wm = addon?.Data["WorldModel"] as JsonArray;
                        string maleModel = wm != null && wm.Count > 0 ? ExtractModelPath(wm[0]) : ExtractModelPath(addon?.Data["WorldModel"]?["Male"]);
                        string femaleModel = wm != null && wm.Count > 1 ? ExtractModelPath(wm[1]) : ExtractModelPath(addon?.Data["WorldModel"]?["Female"]);
                        string fpFlags = addon?.Data["FirstPersonFlags"]?.ToString() ?? r.Data["FirstPersonFlags"]?.ToString() ?? "";
                        if (multi) t.Add(p, r.FormKey, r.EditorID, addonFk, maleModel, femaleModel, fpFlags);
                        else t.Add(r.FormKey, r.EditorID, addonFk, maleModel, femaleModel, fpFlags);
                    }
                }
            }
            Sfs.Print(t);
        }

        static void RunOmod(List<string> targets, string? filter, bool multi)
        {
            var cols = new List<string>();
            if (multi) cols.Add("Plugin");
            cols.AddRange(new[] { "FormKey", "Signature", "EditorID", "Name", "AttachPoint", "TargetOmodKeywords", "Includes", "Properties", "Mesh" });
            var t = new Sfs.Table(cols.ToArray());

            foreach (var p in targets)
            {
                foreach (var r in ScanRecords(p, "Signature LIKE '%Modification%' OR Signature = 'ObjectMod'", filter))
                {
                    string ap = r.Data["AttachPoint"]?.ToString() ?? "";
                    var tk = (r.Data["TargetOmodKeywords"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new List<string>();
                    int inc = (r.Data["Includes"] as JsonArray)?.Count ?? 0;
                    int props = (r.Data["Properties"] as JsonArray)?.Count ?? 0;
                    string mesh = ExtractModelPath(r.Data["Model"]);
                    if (multi) t.Add(p, r.FormKey, r.Signature, r.EditorID, r.Name, ap, string.Join(", ", tk), inc, props, mesh);
                    else t.Add(r.FormKey, r.Signature, r.EditorID, r.Name, ap, string.Join(", ", tk), inc, props, mesh);
                }
            }
            Sfs.Print(t);
        }
    }
}
