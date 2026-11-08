using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SFSearch
{
    /// <summary>
    /// Check: a whole plugin checked for errors, like SF1Edit's "Check for Errors". One pass over the plugin file gives every
    /// record, its links with the type each link expects, and its deleted flag; the Forms lists of the plugin and its masters
    /// say what each link really points at. Nothing is written.
    /// </summary>
    public static class CheckCommand
    {
        sealed record Problem(string FormKey, string Signature, string EditorID, string Detail);

        sealed class Finding
        {
            public string Name = "", Level = "ERROR", What = "";
            public List<Problem> Rows = new();
            public string? Note;
        }

        public static void Run(List<string> args)
        {
            bool all = Sfs.TakeFlag(args, "--all");
            string? outFile = Sfs.TakeOption(args, "--out");
            if (args.Count < 1) { Console.WriteLine("Usage: SFSearch Check <plugin|path> [--all] [--out report.md]"); return; }

            string plugin = Program.EnsureCacheForFile(args[0]);
            string? path = Program.CacheSourcePath(plugin, Program.GetCachePath(plugin));
            if (path == null) { Console.WriteLine($"Error: the file of {plugin} wasn't found."); return; }

            List<string> masters; string? missingMasters, style, maxHex;
            using (var conn = Sfs.OpenCache(plugin))
            {
                masters = Sfs.Masters(conn);
                missingMasters = Sfs.Meta(conn, "MissingMasters");
                style = Sfs.Meta(conn, "MasterStyle");
                maxHex = Sfs.Meta(conn, "MaxLocalFormID");
            }
            string pluginName = Path.GetFileName(path);
            var findings = new List<Finding>();
            List<string> notes;

            // ---- masters and FormID room
            var fMasters = new Finding { Name = "Masters present", What = "every master is in the Data folder" };
            if (missingMasters != null)
                foreach (var m in missingMasters.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    fMasters.Rows.Add(new Problem("", "", "", $"{m} isn't in Data: FormIDs and links can land on the wrong plugin"));
            findings.Add(fMasters);

            var fRoom = new Finding { Name = "FormIDs fit the plugin's size", What = "Light up to 0xFFF, Medium up to 0xFFFF" };
            if (maxHex != null && maxHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(maxHex[2..], System.Globalization.NumberStyles.HexNumber, null, out uint maxId))
            {
                uint limit = style switch { "Light" or "Small" => 0xFFF, "Medium" => 0xFFFF, _ => 0xFFFFFF };
                if (maxId > limit) fRoom.Rows.Add(new Problem("", "", "", $"highest new FormID {maxHex} is over the {style} limit 0x{limit:X}"));
            }
            findings.Add(fRoom);

            // ---- one pass over the file: records, links with expected types, deleted flags
            var records = new List<(string FormKey, string Signature, string EditorID, bool Override, bool Deleted)>();
            var links = new List<(string From, string FromSig, string FromEdid, string To, Type Expected)>();
            var readParams = new BinaryReadParameters { MasterFlagsLookup = new SimpleMasterFlagCache(), ThrowOnUnknownSubrecord = false };
            using (var mod = StarfieldMod.CreateFromBinaryOverlay(new ModPath(ModKey.FromFileName(pluginName), path), StarfieldRelease.Starfield, readParams))
            {
                var own = mod.ModKey;
                foreach (var rec in mod.EnumerateMajorRecords())
                {
                    string fk = Key(rec.FormKey);
                    string sig = rec.Registration.Name;
                    records.Add((fk, sig, rec.EditorID ?? "", rec.FormKey.ModKey != own, rec.IsDeleted));
                    foreach (var l in rec.EnumerateFormLinks())
                        if (!l.FormKey.IsNull) links.Add((fk, sig, rec.EditorID ?? "", Key(l.FormKey), l.Type));
                }
            }

            // ---- what each target really is: the Forms lists of the plugin and its masters
            var wanted = new HashSet<string>(links.Select(l => l.To), StringComparer.OrdinalIgnoreCase);
            foreach (var r in records.Where(r => r.Override)) wanted.Add(r.FormKey);
            var newEdids = records.Where(r => !r.Override && r.EditorID.Length > 0).Select(r => r.EditorID).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // FormKey -> signature
            var masterEdids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // EditorID -> FormKey in a master
            var known = new HashSet<string>(masters, StringComparer.OrdinalIgnoreCase) { plugin, pluginName };
            foreach (var r in records) actual[r.FormKey] = r.Signature;
            var forcedBefore = LiveDb.Forced;
            try
            {
                LiveDb.Forced = new LiveDb.Needs { Forms = true, Records = false };
                foreach (var m in masters)
                {
                    if (!Sfs.HasCacheOrBuild(m)) continue;
                    using var mc = Sfs.OpenCache(m, ensureUpToDate: false);
                    using var cmd = mc.CreateCommand();
                    cmd.CommandText = "SELECT FormKey, Signature, EditorID FROM Forms";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                    {
                        string fk = rd.GetString(0);
                        if (wanted.Contains(fk) && !actual.ContainsKey(fk)) actual[fk] = rd.GetString(1);
                        if (!rd.IsDBNull(2) && newEdids.Contains(rd.GetString(2)) && !masterEdids.ContainsKey(rd.GetString(2))) masterEdids[rd.GetString(2)] = fk;
                    }
                }
            }
            finally { LiveDb.Forced = forcedBefore; }

            // ---- links
            var fMissing = new Finding { Name = "Links point at records that exist", What = "the target is in the plugin or one of its masters" };
            var fForeign = new Finding { Name = "Links stay inside the plugin and its masters" };
            var fWrongType = new Finding { Name = "Links point at the right kind of record", What = "e.g. a keyword list holds keywords" };
            int hardcoded = 0, navi = 0;
            foreach (var l in links)
            {
                // The navmesh info map (NAVI) keeps entries for navmeshes that were deleted and whose FormIDs were reused (Starfield.esm's own
                // has 120 such): its links say little about the plugin. Part of it (traversals) isn't read as links at all.
                if (l.FromSig == "NavigationMeshInfoMap") { navi++; continue; }
                string toPlugin = Sfs.PluginOf(l.To);
                if (!known.Contains(toPlugin)) { fForeign.Rows.Add(new Problem(l.From, l.FromSig, l.FromEdid, $"→ {l.To}: {toPlugin} isn't a master")); continue; }
                if (!actual.TryGetValue(l.To, out var sig))
                {
                    // FormIDs below 0x800 in the game's own master are built into the engine (000014 is the player): never in the file
                    if (toPlugin.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase) && Sfs.LocalIdOf(l.To) < 0x800) { hardcoded++; continue; }
                    fMissing.Rows.Add(new Problem(l.From, l.FromSig, l.FromEdid, $"→ {l.To}: no such record in {toPlugin}"));
                    continue;
                }
                var got = GetterOf(sig);
                if (got != null && !l.Expected.IsAssignableFrom(got))
                    fWrongType.Rows.Add(new Problem(l.From, l.FromSig, l.FromEdid, $"→ {l.To} is a {sig}, where {Pretty(l.Expected)} is expected"));
            }
            notes = new List<string>();
            if (hardcoded > 0) notes.Add($"{hardcoded} link(s) to the engine's built-in forms (Starfield.esm below 0x800, such as 000014, the player) aren't in any file and aren't errors.");
            if (navi > 0) notes.Add($"{navi} link(s) inside the navmesh info map (NAVI) weren't checked: it keeps entries for deleted navmeshes whose FormIDs were reused (the game's own does too).");
            findings.Add(fMissing); findings.Add(fWrongType); findings.Add(fForeign);

            // ---- overrides whose type changed (patch built for an older master)
            var fTypes = new Finding { Name = "Overrides keep their base record's type", What = "a different type means the patch was built for an older version of a master" };
            findings.Add(TypeChanges(records, masters, fTypes));

            // ---- deleted records
            var fDeleted = new Finding { Name = "No deleted records", Level = "WARN", What = "deleted placed references can crash the game when another mod still uses them; SF1Edit's \"Undelete and Disable\" is the safe way" };
            foreach (var r in records.Where(r => r.Deleted)) fDeleted.Rows.Add(new Problem(r.FormKey, r.Signature, r.EditorID, r.Override ? "deleted override of a master's record" : "deleted new record"));
            findings.Add(fDeleted);

            // ---- EditorIDs
            var fDupe = new Finding { Name = "EditorIDs are unique in the plugin", Level = "WARN" };
            foreach (var g in records.Where(r => !r.Override && r.EditorID.Length > 0).GroupBy(r => r.EditorID, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var r in g) fDupe.Rows.Add(new Problem(r.FormKey, r.Signature, r.EditorID, $"{g.Count()} new records share this EditorID"));
            findings.Add(fDupe);
            var fClash = new Finding { Name = "New EditorIDs don't repeat a master's", Level = "WARN", What = "scripts and the console find records by EditorID" };
            foreach (var r in records.Where(r => !r.Override && masterEdids.ContainsKey(r.EditorID)))
                fClash.Rows.Add(new Problem(r.FormKey, r.Signature, r.EditorID, $"also {masterEdids[r.EditorID]}"));
            findings.Add(fClash);

            // ---- ITMs: overrides identical to the record they override (stored record types only)
            var fItm = new Finding { Name = "No identical-to-master overrides (ITMs)", Level = "WARN", What = "an override that changes nothing still wins over other mods' changes", Note = "Checked on the record types SFSearch stores in full; `Delta` shows each override's changes." };
            foreach (var r in records.Where(r => r.Override && !r.Deleted))
            {
                var ovr = Sfs.FindRecord(plugin, r.FormKey, r.Signature, ensureUpToDate: false);
                if (ovr == null) continue;
                var baseRow = InspectCommands.FindPrecedingRecord(r.FormKey, masters);
                if (baseRow != null && InspectCommands.GetChangedTopFields(baseRow.Data, ovr.Data).Count == 0)
                    fItm.Rows.Add(new Problem(r.FormKey, r.Signature, r.EditorID, $"identical to {baseRow.Plugin}"));
            }
            findings.Add(fItm);

            Print(pluginName, records.Count, links.Count, findings, notes, all, outFile);
            if (findings.Any(f => f.Level == "ERROR" && f.Rows.Count > 0)) Environment.ExitCode = 1;
        }

        static Finding TypeChanges(List<(string FormKey, string Signature, string EditorID, bool Override, bool Deleted)> records, List<string> masters, Finding f)
        {
            var overrides = records.Where(r => r.Override).ToDictionary(r => r.FormKey, r => r, StringComparer.OrdinalIgnoreCase);
            if (overrides.Count == 0) return f;
            var forcedBefore = LiveDb.Forced;
            try
            {
                LiveDb.Forced = new LiveDb.Needs { Forms = true, Records = false };
                foreach (var m in masters)
                {
                    if (!Sfs.HasCacheOrBuild(m)) continue;
                    using var mc = Sfs.OpenCache(m, ensureUpToDate: false);
                    using var cmd = mc.CreateCommand();
                    cmd.CommandText = "SELECT FormKey, Signature FROM Forms WHERE IsOverride = 0";
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                        if (overrides.TryGetValue(rd.GetString(0), out var r) && rd.GetString(1) != r.Signature)
                            f.Rows.Add(new Problem(r.FormKey, r.Signature, r.EditorID, $"is a {rd.GetString(1)} in {m}"));
                }
            }
            finally { LiveDb.Forced = forcedBefore; }
            return f;
        }

        static string Key(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

        static readonly Dictionary<string, Type?> _getters = new();
        static Type? GetterOf(string signature)
        {
            if (_getters.TryGetValue(signature, out var t)) return t;
            return _getters[signature] = typeof(IWeaponGetter).Assembly.GetType($"Mutagen.Bethesda.Starfield.I{signature}Getter");
        }

        static string Pretty(Type t)
        {
            string n = t.Name;
            if (n.StartsWith('I') && n.EndsWith("Getter")) n = n[1..^6];
            return n;
        }

        static void Print(string plugin, int recordCount, int linkCount, List<Finding> findings, List<string> notes, bool all, string? outFile)
        {
            using var report = Sfs.ReportOut.Begin(outFile);
            Sfs.Say($"### Check: {plugin}");
            Sfs.Say($"{recordCount:N0} records and {linkCount:N0} links read from the file.\n");
            var t = new Sfs.Table("Result", "Check", "Found", "What it means");
            foreach (var f in findings)
                t.Add(f.Rows.Count == 0 ? "PASS" : f.Level, f.Name, f.Rows.Count, f.What);
            Sfs.Print(t, annotate: false);
            foreach (var n in notes) Sfs.Say($"_Note: {n}_");
            int errors = findings.Count(f => f.Level == "ERROR" && f.Rows.Count > 0), warns = findings.Count(f => f.Level == "WARN" && f.Rows.Count > 0);
            Sfs.Summary($"{plugin}: {errors} check(s) with errors, {warns} with warnings");
            foreach (var f in findings.Where(f => f.Rows.Count > 0))
            {
                Sfs.Say($"\n#### {f.Level}: {f.Name} ({f.Rows.Count})");
                if (f.Note != null) Sfs.Say(f.Note);
                var rows = all ? f.Rows : f.Rows.Take(50).ToList();
                if (rows.All(r => r.FormKey.Length == 0))
                    foreach (var r in rows) Sfs.Say($"- {r.Detail}");
                else
                {
                    var dt = new Sfs.Table("Record", "Type", "EditorID", "Problem");
                    foreach (var r in rows) dt.Add(r.FormKey, r.Signature, r.EditorID, r.Detail);
                    Sfs.Print(dt);
                }
                if (rows.Count < f.Rows.Count) Sfs.Say($"_… {f.Rows.Count - rows.Count} more; add --all to list them._");
            }
        }
    }
}
