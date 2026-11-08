using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace SFSearch
{
    public static class PluginDiffCommand
    {
        public static void Run(List<string> args)
        {
            bool detail = Sfs.TakeFlag(args, "--detail");
            bool showShifted = Sfs.TakeFlag(args, "--shifts");
            if (args.Count < 2)
            {
                Console.WriteLine("Usage: SFSearch PluginDiff <oldPlugin|path> <newPlugin|path> [--detail] [--shifts]");
                return;
            }

            string oldCache = Program.EnsureCacheForFile(args[0], isSecondArg: false);
            string newCache = Program.EnsureCacheForFile(args[1], isSecondArg: true);

            var oldRecs = LoadAllRecords(oldCache, out string oldModFile, out var oldMasters);
            var newRecs = LoadAllRecords(newCache, out string newModFile, out var newMasters);

            // A patch and the plugin it masters aren't two versions of one plugin. Every record is matched by its FormKey, so an
            // override is compared with the record it overrides, and what the patch doesn't contain isn't a removal.
            bool newIsPatch = newMasters.Contains(oldModFile, StringComparer.OrdinalIgnoreCase);
            bool oldIsPatch = oldMasters.Contains(newModFile, StringComparer.OrdinalIgnoreCase);
            bool patchMode = newIsPatch || oldIsPatch;

            // Map local FormKeys -> EditorIDs in each plugin so internal FormID shifts don't trigger false field diffs.
            // Not for a patch: FormKeys already mean the same record on both sides, and the maps would see only one side's.
            var noMap = new Dictionary<string, string>();
            var oldFkToEdid = patchMode ? noMap : oldRecs.ToDictionary(r => r.FormKey, r => r.EditorID, StringComparer.OrdinalIgnoreCase);
            var newFkToEdid = patchMode ? noMap : newRecs.ToDictionary(r => r.FormKey, r => r.EditorID, StringComparer.OrdinalIgnoreCase);

            string MatchKey(Sfs.RecordRow r, string modFile)
            {
                if (patchMode) return $"FK|{r.FormKey.ToUpperInvariant()}";
                bool isOvr = !Sfs.PluginOf(r.FormKey).Equals(modFile, StringComparison.OrdinalIgnoreCase);
                return isOvr ? $"OVR|{r.FormKey.ToUpperInvariant()}" : $"NEW|{r.Signature}|{r.EditorID}";
            }

            var oldMap = new Dictionary<string, Sfs.RecordRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in oldRecs) oldMap[MatchKey(r, oldModFile)] = r;

            var newMap = new Dictionary<string, Sfs.RecordRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in newRecs) newMap[MatchKey(r, newModFile)] = r;

            var allKeys = oldMap.Keys.Union(newMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

            int added = 0, removed = 0, modified = 0, shiftedOnly = 0, unchanged = 0, notInPatch = 0, unmatchedOverrides = 0;
            var diffTable = new Sfs.Table("Status", "Signature", "EditorID", "OldFormKey", "NewFormKey", "ChangedFields");
            var modifiedDetails = new List<(Sfs.RecordRow Old, Sfs.RecordRow New, List<string> Fields)>();

            foreach (var k in allKeys)
            {
                bool hasOld = oldMap.TryGetValue(k, out var o);
                bool hasNew = newMap.TryGetValue(k, out var n);

                if (!hasOld && n != null)
                {
                    if (oldIsPatch) { notInPatch++; continue; }
                    added++;
                    bool isOverride = !Sfs.PluginOf(n.FormKey).Equals(newModFile, StringComparison.OrdinalIgnoreCase);
                    if (isOverride && !patchMode) unmatchedOverrides++;
                    diffTable.Add("ADDED", n.Signature, n.EditorID, "-", n.FormKey, isOverride ? "(override: no such record in the old file)" : "(new record)");
                }
                else if (hasOld && !hasNew && o != null)
                {
                    if (newIsPatch) { notInPatch++; continue; }
                    removed++;
                    diffTable.Add("REMOVED", o.Signature, o.EditorID, o.FormKey, "-", oldIsPatch ? "(only in the patch)" : "(deleted record)");
                }
                else if (o != null && n != null)
                {
                    var changedFields = CompareNormalizedRecords(o.Data, n.Data, oldModFile, newModFile, oldFkToEdid, newFkToEdid);
                    bool fkChanged = !Sfs.LocalIdOf(o.FormKey).Equals(Sfs.LocalIdOf(n.FormKey));

                    if (changedFields.Count > 0)
                    {
                        modified++;
                        string status = fkChanged ? "MODIFIED+SHIFT" : "MODIFIED";
                        diffTable.Add(status, n.Signature, n.EditorID, o.FormKey, n.FormKey, string.Join(", ", changedFields));
                        modifiedDetails.Add((o, n, changedFields));
                    }
                    else if (fkChanged)
                    {
                        shiftedOnly++;
                        if (showShifted)
                            diffTable.Add("SHIFTED_ID", n.Signature, n.EditorID, o.FormKey, n.FormKey, "(identical content)");
                    }
                    else
                    {
                        unchanged++;
                    }
                }
            }

            Sfs.Say($"### PluginDiff: `{oldCache}` vs `{newCache}`");
            if (patchMode)
            {
                string patchFile = newIsPatch ? newModFile : oldModFile, plugin = newIsPatch ? oldModFile : newModFile;
                Sfs.Say($"Patch comparison: `{patchFile}` masters `{plugin}`, so each record is compared with the same FormID on the other side. {notInPatch} record(s) the patch doesn't contain are left out: they aren't removals.");
            }
            Sfs.Say($"- **Added**: {added} | **Removed**: {removed} | **Modified**: {modified} | **FormID-Shifted Only**: {shiftedOnly} | **Unchanged**: {unchanged}");
            if (unmatchedOverrides > 0)
                Sfs.Say($"**WARNING: {unmatchedOverrides} override(s) in the new file have no matching record, so their values weren't compared.** These two files may not be versions of one plugin. To check a patch against a plugin, give the plugin under the file name the patch masters.");
            Sfs.Print(diffTable, annotate: false);

            if (modified >= 10)
            {
                Sfs.Say("\n#### Modified records by type and changed fields");
                var tally = new Sfs.Table("Count", "Signature", "ChangedFields");
                foreach (var g in modifiedDetails.GroupBy(d => (d.New.Signature, Fields: string.Join(", ", d.Fields.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)))).OrderByDescending(g => g.Count()))
                    tally.Add(g.Count().ToString(), g.Key.Signature, g.Key.Fields);
                Sfs.Print(tally, annotate: false);
            }

            if (detail && modifiedDetails.Count > 0)
            {
                foreach (var (o, n, fields) in modifiedDetails)
                {
                    Sfs.Say($"\n#### `{n.EditorID}` (`{n.Signature}`)");
                    var ft = new Sfs.Table("Field", "Old", "New");
                    foreach (var f in fields)
                    {
                        ft.Add(f, Sfs.Compact(o.Data[f]), Sfs.Compact(n.Data[f]));
                    }
                    Sfs.Print(ft);
                }
            }
        }

        static List<Sfs.RecordRow> LoadAllRecords(string cacheKey, out string modFileName, out List<string> masters)
        {
            using var conn = Sfs.OpenCache(cacheKey, ensureUpToDate: false);
            modFileName = Sfs.Meta(conn, "FileName") ?? cacheKey;
            masters = Sfs.Masters(conn);
            var list = new List<Sfs.RecordRow>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FormKey, Signature, EditorID, Name, Data FROM Records ORDER BY FormKey";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Sfs.RecordRow
                {
                    Plugin = cacheKey,
                    FormKey = r.GetString(0),
                    Signature = r.GetString(1),
                    EditorID = r.IsDBNull(2) ? "" : r.GetString(2),
                    Name = r.IsDBNull(3) ? "" : r.GetString(3),
                    Data = (JsonNode.Parse(r.GetString(4)) as JsonObject) ?? new JsonObject()
                });
            }
            return list;
        }

        static List<string> CompareNormalizedRecords(
            JsonObject oldObj,
            JsonObject newObj,
            string oldModFile,
            string newModFile,
            Dictionary<string, string> oldFkToEdid,
            Dictionary<string, string> newFkToEdid)
        {
            var keys = oldObj.Select(x => x.Key).Union(newObj.Select(x => x.Key)).Distinct().ToList();
            var changed = new List<string>();
            foreach (var k in keys)
            {
                if (k is "Signature" or "Record Header" or "EDID - Editor ID" or "FormVersion" or "Version2" or "VersionControl") continue;
                string s1 = NormalizeLocalRefs(oldObj[k]?.ToJsonString() ?? "", oldModFile, oldFkToEdid);
                string s2 = NormalizeLocalRefs(newObj[k]?.ToJsonString() ?? "", newModFile, newFkToEdid);
                if (s1 != s2) changed.Add(k);
            }
            return changed;
        }

        static string NormalizeLocalRefs(string json, string modFile, Dictionary<string, string> fkToEdid)
        {
            if (string.IsNullOrEmpty(json)) return "";
            return Sfs.EmbeddedFormKey.Replace(json, m =>
            {
                string fk = Sfs.NormalizeFormKey(m.Value);
                if (Sfs.PluginOf(fk).Equals(modFile, StringComparison.OrdinalIgnoreCase) && fkToEdid.TryGetValue(fk, out var edid))
                    return $"@local({edid})";
                return fk;
            });
        }
    }
}
