using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SFSearch
{
    /// <summary>
    /// Opens one plugin and the masters it needs (read from its header, transitively) as lazy binary overlays and builds
    /// a link cache over just those files. No plugins.txt and no GameEnvironment: the full load order is never touched.
    /// </summary>
    public sealed class SlimLoader : IDisposable
    {
        public ModKey Target { get; private set; }
        public string TargetPath { get; private set; } = "";
        /// <summary>Masters first, target, then any --with extras (last one wins).</summary>
        public List<IStarfieldModDisposableGetter> Mods { get; } = new();
        public Dictionary<ModKey, string> Paths { get; } = new();
        public List<string> MissingMasters { get; } = new();

        public void WarnMissingMasters()
        {
            if (MissingMasters.Count > 0)
                Console.WriteLine($"**WARNING: missing masters (not in Data): {string.Join(", ", MissingMasters)}. A missing Small or Medium master can put FormIDs and links on the wrong plugin.**");
        }
        public ILinkCache LinkCache { get; private set; } = null!;

        public static SlimLoader Open(string targetOrPath, IEnumerable<string> extras)
        {
            string dataFolder = Program.GetDataFolderPathPublic();
            var loader = new SlimLoader();
            var readParams = new BinaryReadParameters { MasterFlagsLookup = new SimpleMasterFlagCache(), ThrowOnUnknownSubrecord = false };
            var opened = new HashSet<ModKey>();

            void Add(ModKey key, string path)
            {
                if (!opened.Add(key)) return;
                var mod = StarfieldMod.CreateFromBinaryOverlay(new ModPath(key, path), StarfieldRelease.Starfield, readParams);
                foreach (var m in mod.ModHeader.MasterReferences)
                {
                    string mp = Path.Combine(dataFolder, m.Master.FileName);
                    if (File.Exists(mp)) Add(m.Master, mp);
                    else if (!loader.MissingMasters.Contains(m.Master.FileName.String)) loader.MissingMasters.Add(m.Master.FileName.String);
                }
                loader.Mods.Add(mod); // after its masters
                loader.Paths[key] = path;
            }

            (loader.Target, loader.TargetPath) = ResolvePlugin(targetOrPath, dataFolder);
            Add(loader.Target, loader.TargetPath);
            foreach (var e in extras)
            {
                var (k, p) = ResolvePlugin(e, dataFolder);
                Add(k, p);
            }
            loader.LinkCache = loader.Mods.ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>();
            return loader;
        }

        /// <summary>A plugin name in Data (".esm" optional) or a path to any plugin file (including .bak copies).</summary>
        internal static (ModKey, string) ResolvePlugin(string input, string dataFolder)
        {
            string resolved = Sfs.ResolveUserPath(input);
            string path;
            if (input.IndexOfAny(new[] { '\\', '/' }) >= 0 && File.Exists(resolved)) path = resolved;
            else if (File.Exists(Path.Combine(dataFolder, input))) path = Path.Combine(dataFolder, input);
            else if (File.Exists(Path.Combine(dataFolder, input + ".esm"))) path = Path.Combine(dataFolder, input + ".esm");
            else if (File.Exists(resolved)) path = resolved;
            else throw new FileNotFoundException($"Plugin not found in Data or on disk: {input}");

            string fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^4];
            return (ModKey.FromFileName(fileName), path);
        }

        public void Dispose()
        {
            foreach (var m in Mods) m.Dispose();
        }
    }

    /// <summary>
    /// Live: reads one record straight from the plugin files through <see cref="SlimLoader"/>, as a fallback when the
    /// SQLite cache doesn't hold what is needed (uncached record types, fields the cache skips, a stale cache).
    /// The cache's Forms table (every record, every type) is used as an index to find a record's type quickly.
    /// </summary>
    public static class LiveCommand
    {
        const string Usage = @"Usage: SFSearch Live <plugin|path> <FormKey|EDID> [--sig Type]
         [--field Path.To[0].Field] [--chain] [--links] [--vs-cache] [--subrecords] [--with p1,p2] [--all] [--timing]
         [--loader slim|gameenv]   (gameenv = the full load order via GameEnvironment, for benchmarking)
       SFSearch Live <plugin|path> <FormKey|EDID> --refs [--with p1,p2] [--timing]
         What points at this record: searches every record's raw bytes in the plugin (and --with plugins), then confirms each hit.
       SFSearch Live <plugin|path> --sig Type --field ""Components[*].SpaceshipAIActor"" [--filter ""EDID*""] [--value text] [--new] [--empty] [--all] [--timing]
         Reads one field from every record of a type. In --field, * matches anything. --filter = EditorID pattern, --value = value contains,
         --new = skip overrides, --empty = also list records with no match, --all = no 1,000-row limit.";

        public static void Run(List<string> args)
        {
            string? sigOpt = Sfs.TakeOption(args, "--sig");
            string? field = Sfs.TakeOption(args, "--field");
            string? withOpt = Sfs.TakeOption(args, "--with");
            string loaderOpt = (Sfs.TakeOption(args, "--loader") ?? "slim").ToLowerInvariant();
            bool chain = Sfs.TakeFlag(args, "--chain");
            bool links = Sfs.TakeFlag(args, "--links");
            bool vsCache = Sfs.TakeFlag(args, "--vs-cache");
            bool subrecords = Sfs.TakeFlag(args, "--subrecords");
            bool all = Sfs.TakeFlag(args, "--all");
            bool timing = Sfs.TakeFlag(args, "--timing");
            bool refs = Sfs.TakeFlag(args, "--refs");
            bool onlyNew = Sfs.TakeFlag(args, "--new");
            bool showEmpty = Sfs.TakeFlag(args, "--empty");
            string? filter = Sfs.TakeOption(args, "--filter");
            string? valueText = Sfs.TakeOption(args, "--value");
            if (args.Count == 1 && sigOpt != null && field != null)
            {
                var clock = Stopwatch.StartNew();
                RunFieldSearch(args[0], Program.NormalizeRecordTypePublic(sigOpt), field, filter, valueText, onlyNew, showEmpty, all);
                if (timing) Console.Error.WriteLine($"[timing] total {clock.ElapsedMilliseconds} ms");
                return;
            }
            if (args.Count < 2) { Console.WriteLine(Usage); return; }

            var total = Stopwatch.StartNew();
            var phase = Stopwatch.StartNew();
            var times = new List<(string, long)>();
            void Mark(string name) { times.Add((name, phase.ElapsedMilliseconds)); phase.Restart(); }

            string? sig = sigOpt == null ? null : Program.NormalizeRecordTypePublic(sigOpt);
            var extras = withOpt == null ? new List<string>() : Program.SplitPluginList(withOpt);

            ILinkCache linkCache;
            ModKey target;
            int fileCount;
            List<string> loadOrderNames;
            SlimLoader? slim = null;
            IDisposable? envHandle = null;
            try
            {
                if (loaderOpt == "gameenv")
                {
                    var env = GameEnvironment.Typical.Starfield(StarfieldRelease.Starfield);
                    envHandle = env;
                    linkCache = env.LinkCache;
                    string t = args[0].EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || args[0].EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || args[0].EndsWith(".esl", StringComparison.OrdinalIgnoreCase) ? args[0] : args[0] + ".esm";
                    target = ModKey.FromFileName(t);
                    loadOrderNames = env.LoadOrder.ListedOrder.Select(x => x.ModKey.FileName.String).ToList();
                    fileCount = loadOrderNames.Count;
                }
                else
                {
                    slim = SlimLoader.Open(args[0], extras);
                    linkCache = slim.LinkCache;
                    target = slim.Target;
                    loadOrderNames = slim.Mods.Select(m => m.ModKey.FileName.String).ToList();
                    fileCount = slim.Mods.Count;
                }
                Mark("load");

                var hit = FindKey(args[1], target, loadOrderNames, sig, slim, linkCache, out string via);
                Mark("index");
                if (hit == null)
                {
                    Console.WriteLine($"Could not find '{args[1]}' in {target.FileName} or its masters{(sig == null ? " (try --sig <Type> for an EditorID that no cache knows)" : "")}.");
                    return;
                }
                var (formKey, type, parent) = hit.Value;
                if (refs)
                {
                    if (slim == null) { Console.WriteLine("--refs needs the normal loader (leave out --loader gameenv)."); return; }
                    PrintLiveRefs(slim, formKey, extras);
                    Mark("refs");
                    return;
                }

                // Cells and placed references: the link cache would index every cell / reference of every loaded file
                // (seconds for Starfield.esm), so walk straight to the cell instead. Winner first, like the link cache.
                List<Version>? contexts = slim != null ? FindInCells(slim, formKey, type, parent) : null;
                string resolvedBy = contexts != null ? "cell walk" : "link cache";
                contexts ??= linkCache.ResolveAllSimpleContexts(formKey, type).Select(c => new Version(c.ModKey, c.Record)).ToList();
                Mark("resolve");
                if (contexts.Count == 0) { Console.WriteLine($"{formKey} was indexed ({via}) but did not resolve as {type.Name}."); return; }
                var mine = contexts.FirstOrDefault(c => c.ModKey == target) ?? contexts[0];
                via += $", resolved by {resolvedBy}";
                var rec = mine.Record;
                string recSig = rec.Registration.Name;
                string fkStr = Sfs.NormalizeFormKey(formKey.ToString());

                Console.WriteLine($"## Live [{recSig}] {rec.EditorID ?? "(no EditorID)"} [{fkStr}]");
                Console.WriteLine($"Version from: {mine.ModKey.FileName}{(mine.ModKey == target ? "" : $" ({target.FileName} does not contain this record)")} | Versions: {string.Join(" → ", contexts.AsEnumerable().Reverse().Select(c => c.ModKey.FileName.String))} (last wins)");
                Console.WriteLine($"Loaded {fileCount} file(s) via {loaderOpt}; record type found via {via}.");
                slim?.WarnMissingMasters();
                Console.WriteLine();

                var json = Program.RecordToJson(rec, linkCache, out _, full: true);

                if (field != null)
                {
                    var node = Navigate(json, field);
                    Console.WriteLine(node == null ? $"(field '{field}' not present)" : Pretty(node));
                }
                else if (!chain && !links && !vsCache && !subrecords)
                {
                    Console.WriteLine(Pretty(json));
                }

                if (chain) PrintChain(contexts, linkCache);
                if (links) PrintLinks(json, linkCache, slim, all);
                if (vsCache) PrintVsCache(json, mine.ModKey.FileName.String, fkStr, recSig, all);
                if (subrecords) PrintSubrecords(rec, all);
                Mark("render");
            }
            finally
            {
                slim?.Dispose();
                envHandle?.Dispose();
                if (timing)
                    Console.Error.WriteLine($"[timing] {string.Join(" | ", times.Select(t => $"{t.Item1} {t.Item2} ms"))} | total {total.ElapsedMilliseconds} ms");
            }
        }

        // ---------------------------------------------------------------- Finding the record

        public sealed record Version(ModKey ModKey, IMajorRecordGetter Record);

        /// <summary>
        /// Every loaded file's version of a cell, or of a placed reference whose cell is known (Forms.Parent), winner first.
        /// Null for other types, or when nothing was found (the caller then uses the link cache).
        /// </summary>
        static List<Version>? FindInCells(SlimLoader slim, FormKey formKey, Type type, FormKey? parent)
        {
            bool isCell = type == typeof(ICellGetter);
            if (!isCell && (parent == null || !typeof(IPlacedGetter).IsAssignableFrom(type))) return null;
            var cellKey = isCell ? formKey : parent!.Value;
            var found = new List<Version>();
            foreach (var mod in Enumerable.Reverse(slim.Mods))
            {
                // A file can only hold its own records and overrides of its masters' records.
                if (mod.ModKey != cellKey.ModKey && !mod.ModHeader.MasterReferences.Any(m => m.Master == cellKey.ModKey)) continue;
                var cell = FindCell(mod, cellKey);
                if (cell == null) continue;
                if (isCell) { found.Add(new Version(mod.ModKey, cell)); continue; }
                var placed = cell.Persistent.Concat(cell.Temporary).FirstOrDefault(p => p.FormKey == formKey);
                if (placed != null) found.Add(new Version(mod.ModKey, placed));
            }
            return found.Count > 0 ? found : null;
        }

        /// <summary>
        /// Interior cells sit in block (ID % 10) / sub-block ((ID / 10) % 10), so those are searched first; the rest and the
        /// worldspaces follow in case a file was saved with other grouping.
        /// </summary>
        static ICellGetter? FindCell(IStarfieldModGetter mod, FormKey cellKey)
        {
            int block = (int)(cellKey.ID % 10), sub = (int)(cellKey.ID / 10 % 10);
            foreach (var b in mod.Cells.Records.OrderBy(x => x.BlockNumber == block ? 0 : 1))
                foreach (var s in b.SubBlocks.OrderBy(x => x.BlockNumber == sub ? 0 : 1))
                    foreach (var c in s.Cells)
                        if (c.FormKey == cellKey) return c;
            foreach (var ws in mod.Worldspaces)
            {
                if (ws.TopCell?.FormKey == cellKey) return ws.TopCell;
                foreach (var b in ws.SubCells)
                    foreach (var s in b.Items)
                        foreach (var c in s.Items)
                            if (c.FormKey == cellKey) return c;
            }
            return null;
        }

        /// <summary>
        /// Turns a FormKey or EditorID into (FormKey, getter type, cell of a placed reference). Order: the caches' Records then
        /// Forms tables (target first, then its masters; no rebuilds), then a live scan of one record group when --sig is
        /// given, then (FormKey only) an untyped live resolve.
        /// </summary>
        static (FormKey, Type, FormKey?)? FindKey(string key, ModKey target, List<string> loadOrder, string? sig, SlimLoader? slim, ILinkCache linkCache, out string via)
        {
            via = "";
            bool isFk = Sfs.TryParseKey(key, target.FileName, out string fkStr);
            var searchOrder = new List<string> { target.FileName.String };
            if (isFk) searchOrder.Add(Sfs.PluginOf(fkStr));
            searchOrder.AddRange(Enumerable.Reverse(loadOrder));

            foreach (var plugin in searchOrder.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Sfs.HasCache(plugin)) continue;
                try
                {
                    using var conn = new SqliteConnection($"Data Source={Sfs.CachePath(plugin)};Mode=ReadOnly;Pooling=False");
                    conn.Open();
                    foreach (var table in new[] { "Records", "Forms" })
                    {
                        if (!Sfs.TableExists(conn, table)) continue;
                        using var cmd = conn.CreateCommand();
                        string sigClause = sig == null ? "" : " AND Signature = @sig";
                        if (isFk)
                        {
                            cmd.CommandText = $"SELECT FormKey, Signature FROM {table} WHERE {Sfs.FormKeyMatch("FormKey")}{sigClause} LIMIT 1";
                            Sfs.AddFormKeyParams(cmd, fkStr);
                        }
                        else
                        {
                            cmd.CommandText = $"SELECT FormKey, Signature FROM {table} WHERE EditorID = @e{sigClause} LIMIT 1";
                            cmd.Parameters.AddWithValue("@e", key);
                        }
                        if (sig != null) cmd.Parameters.AddWithValue("@sig", sig);
                        string foundFk, foundSig;
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) continue;
                            (foundFk, foundSig) = (r.GetString(0), r.GetString(1));
                        }
                        var type = GetterType(foundSig);
                        if (type == null) continue;
                        via = $"{plugin} cache ({table})";
                        return (FormKey.Factory(foundFk), type, CellOf(conn, foundFk));
                    }
                }
                catch { }
            }

            // No cache knew it: one header-only pass over the file (type + cell), unless --sig names an ordinary type
            // (then the typed lookups below are already fast).
            var sigGetter = sig == null ? null : GetterType(sig);
            bool headerScanned = false;
            if (slim != null && (sigGetter == null || IsCellOrPlaced(sigGetter)))
            {
                headerScanned = true;
                if (isFk)
                {
                    var fk = FormKey.Factory(fkStr);
                    var owner = slim.Paths.ContainsKey(fk.ModKey) ? fk.ModKey : target;
                    if (HeaderScanner.FindFormKey(owner, slim.Paths[owner], fk) is var (getter, s4, cell))
                    {
                        via = $"header scan of {owner.FileName} ({s4})";
                        return (fk, getter, cell);
                    }
                }
                else
                {
                    foreach (var mod in Enumerable.Reverse(slim.Mods))
                    {
                        if (HeaderScanner.FindEditorId(mod.ModKey, slim.Paths[mod.ModKey], key) is var (fk, getter, s4, cell))
                        {
                            via = $"header scan of {mod.ModKey.FileName} ({s4})";
                            return (fk, getter, cell);
                        }
                    }
                }
            }

            if (sig != null && sigGetter is Type sigType)
            {
                if (isFk) { via = "--sig"; return (FormKey.Factory(fkStr), sigType, null); }
                if (slim != null)
                {
                    foreach (var mod in Enumerable.Reverse(slim.Mods))
                    {
                        var match = mod.EnumerateMajorRecords(sigType).FirstOrDefault(m => string.Equals(m.EditorID, key, StringComparison.OrdinalIgnoreCase));
                        if (match != null) { via = $"live scan of {sig} in {mod.ModKey.FileName}"; return (match.FormKey, sigType, null); }
                    }
                }
                else if (linkCache.TryResolve(key, sigType, out var byEdid))
                {
                    via = "live EditorID index"; return (byEdid.FormKey, sigType, null);
                }
                return null;
            }

            // A FormKey the header scan missed doesn't exist: the scan reads every record header of the file that owns it
            // (checked against the caches with LiveScanCheck), and the untyped fallback below costs ~15 s on Starfield.esm.
            // An EditorID can still be in a compressed record, which the scan can't read: the --sig lookups below can.
            if (headerScanned && (isFk || sig == null)) return null;

            // The record type isn't known here, so the untyped lookup (obsolete since Mutagen 0.55) is the right call.
#pragma warning disable CS0618
            if (isFk && linkCache.TryResolve(FormKey.Factory(fkStr), out IMajorRecordGetter? any))
#pragma warning restore CS0618
            {
                via = "untyped live resolve (no cache knew it)";
                return (any.FormKey, any.Registration.GetterType, null);
            }
            return null;
        }

        static bool IsCellOrPlaced(Type getter) => getter == typeof(ICellGetter) || typeof(IPlacedGetter).IsAssignableFrom(getter);

        /// <summary>
        /// LiveScanCheck &lt;plugin&gt;: one full header scan of the plugin, checked record by record against its cache's Forms
        /// table (type and cell). Run it after a Mutagen upgrade or on a new kind of plugin before trusting the scan.
        /// </summary>
        public static void ScanCheck(List<string> args)
        {
            if (args.Count < 1) { Console.WriteLine("Usage: SFSearch LiveScanCheck <plugin>"); return; }
            var (modKey, path) = SlimLoader.ResolvePlugin(args[0], Program.GetDataFolderPathPublic());
            var masters = HeaderScanner.MasterTable(modKey, path);
            var sw = Stopwatch.StartNew();
            var scanned = new Dictionary<FormKey, (string Sig, FormKey? Cell)>();
            long records = 0;
            HeaderScanner.Scan(path, (h, _) =>
            {
                scanned[masters.GetFormKey(FormID.Factory(h.FormId), reference: false)] = (h.Signature, HeaderScanner.CellKey(masters, h));
                records++;
                return false;
            });
            Console.WriteLine($"{modKey.FileName}: header scan read {records} records ({scanned.Count} distinct FormKeys) in {sw.ElapsedMilliseconds} ms.");

            string plugin = modKey.FileName.String;
            if (!Sfs.HasCache(plugin)) { Console.WriteLine("No cache to check against."); return; }
            using var conn = new SqliteConnection($"Data Source={Sfs.CachePath(plugin)};Mode=ReadOnly;Pooling=False");
            conn.Open();
            if (!Sfs.TableExists(conn, "Forms")) { Console.WriteLine("Cache has no Forms table to check against."); return; }

            int rows = 0, missing = 0, typeBad = 0, cellBad = 0;
            var examples = new List<string>();
            var seen = new HashSet<FormKey>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FormKey, Signature, Parent FROM Forms";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows++;
                var fk = FormKey.Factory(r.GetString(0));
                seen.Add(fk);
                string sigName = r.GetString(1);
                string? parent = r.IsDBNull(2) ? null : Sfs.NormalizeFormKey(r.GetString(2));
                if (!scanned.TryGetValue(fk, out var s))
                {
                    missing++;
                    if (examples.Count < 15) examples.Add($"missing from scan: {fk} {sigName}");
                    continue;
                }
                var cacheType = GetterType(sigName);
                var scanType = HeaderScanner.GetterFor(s.Sig);
                if (cacheType == null || scanType == null || !(cacheType == scanType || scanType.IsAssignableFrom(cacheType)))
                {
                    typeBad++;
                    if (examples.Count < 15) examples.Add($"type: {fk} cache {sigName}, scan {s.Sig} -> {scanType?.Name ?? "?"}");
                }
                string? scanCell = s.Cell == null ? null : Sfs.NormalizeFormKey(s.Cell.Value.ToString());
                // Parent is also a record's world, topic or quest; the scan only knows cells
                if (parent != null && scanCell != null && !string.Equals(parent, scanCell, StringComparison.OrdinalIgnoreCase))
                {
                    cellBad++;
                    if (examples.Count < 15) examples.Add($"cell: {fk} cache {parent}, scan {scanCell ?? "(none)"}");
                }
            }
            int scanOnly = scanned.Keys.Count(k => !seen.Contains(k));
            Console.WriteLine($"Forms rows {rows}: {missing} missing from scan, {typeBad} type mismatches, {cellBad} cell mismatches; {scanOnly} scanned records not in Forms.");
            foreach (var e in examples) Console.WriteLine("  " + e);
        }

        static FormKey? CellOf(SqliteConnection conn, string formKey)
        {
            if (!Sfs.TableExists(conn, "Forms")) return null;
            using var cmd = conn.CreateCommand();
            // Parent can also be a world, topic or quest: only a cell counts here
            cmd.CommandText = $"SELECT Parent FROM Forms WHERE {Sfs.FormKeyMatch("FormKey")} AND Parent IN (SELECT FormKey FROM Forms WHERE Signature = 'Cell')";
            Sfs.AddFormKeyParams(cmd, formKey);
            return cmd.ExecuteScalar() is string p && FormKey.TryFactory(p, out var fk) ? fk : null;
        }

        /// <summary>"Weapon" / "WEAP" / "PlacedObject" -> IWeaponGetter etc.</summary>
        static Type? GetterType(string name)
        {
            name = Program.NormalizeRecordTypePublic(name);
            return typeof(IWeaponGetter).Assembly.GetType($"Mutagen.Bethesda.Starfield.I{name}Getter", false, true);
        }

        // ---------------------------------------------------------------- Modes

        static void PrintChain(List<Version> contexts, ILinkCache linkCache)
        {
            Console.WriteLine("### Override chain (first = defines, last = wins among loaded files)");
            var versions = Enumerable.Reverse(contexts).Select(c => (c.ModKey, Json: Program.RecordToJson(c.Record, linkCache, out _, full: true))).ToList();
            var t = new Sfs.Table("#", "Plugin", "Fields changed vs previous version");
            for (int i = 0; i < versions.Count; i++)
            {
                string changed = i == 0 ? "(defines)" : string.Join(", ", ChangedTopLevel(versions[i - 1].Json, versions[i].Json).DefaultIfEmpty("(identical: ITM)"));
                t.Add(i + 1, versions[i].ModKey.FileName, changed);
            }
            Sfs.Print(t, annotate: false);
        }

        static IEnumerable<string> ChangedTopLevel(JsonObject a, JsonObject b)
        {
            foreach (var k in a.Select(p => p.Key).Union(b.Select(p => p.Key)))
            {
                if (k == "Record Header") continue;
                string? va = a[k]?.ToJsonString(), vb = b[k]?.ToJsonString();
                if (va != vb) yield return k;
            }
        }

        static void PrintLinks(JsonObject json, ILinkCache linkCache, SlimLoader? slim, bool all)
        {
            Console.WriteLine("### Outbound links (resolved through the loaded files)");
            var t = new Sfs.Table("Path", "FormKey", "Type", "EditorID", "From");
            int n = 0;
            var rows = new List<(string Path, string Fk)>();
            bool cut = false;
            foreach (var (path, fk) in Leaves(json).SelectMany(l => Sfs.EmbeddedFormKey.Matches(l.Value).Select(m => (l.Path, Sfs.NormalizeFormKey(m.Value)))))
            {
                if (path.StartsWith("Record Header") || fk.StartsWith("000000:")) continue;
                if (!all && ++n > 200) { cut = true; break; }
                rows.Add((path, fk));
            }
            var names = slim != null ? ResolveLinkNames(slim, rows.Select(r => r.Fk).Distinct(StringComparer.OrdinalIgnoreCase).ToList()) : null;
            foreach (var (path, fk) in rows)
            {
                string type = "", edid = "", from = "unresolved";
                if (names != null)
                {
                    if (names.TryGetValue(fk, out var v)) (type, edid, from) = v;
                }
                else
                {
                    try
                    {
                        // Only for --loader gameenv. This untyped lookup indexes every record type of every loaded file: about
                        // 15 s and 11 GB. The normal loader uses ResolveLinkNames instead.
#pragma warning disable CS0618
                        if (linkCache.TryResolveSimpleContext(FormKey.Factory(fk), out var ctx))
#pragma warning restore CS0618
                        {
                            type = ctx.Record.Registration.Name;
                            edid = ctx.Record.EditorID ?? "";
                            from = ctx.ModKey.FileName;
                        }
                    }
                    catch { }
                }
                t.Add(path, fk, type, edid, from);
            }
            if (cut) t.Add("…", "(more; use --all)", "", "", "");
            Sfs.Print(t, annotate: false);
        }

        /// <summary>
        /// Type, EditorID and winning file of each FormKey, without Mutagen's all-types index. Each loaded file answers from its
        /// cache's Forms table when that cache is fresh and was built from this same file, otherwise from one header pass over
        /// the file. Later files overwrite earlier ones (last wins, like the load order).
        /// </summary>
        static Dictionary<string, (string Type, string EditorID, string From)> ResolveLinkNames(SlimLoader slim, List<string> formKeys)
        {
            var result = new Dictionary<string, (string Type, string EditorID, string From)>(StringComparer.OrdinalIgnoreCase);
            string dataFolder = Program.GetDataFolderPathPublic();
            foreach (var mod in slim.Mods)
            {
                var want = formKeys.Where(k => CanHold(mod, FormKey.Factory(k))).ToList();
                if (want.Count == 0) continue;
                string plugin = mod.ModKey.FileName.String;
                string path = slim.Paths[mod.ModKey];
                bool sameFile = string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(dataFolder, plugin)), StringComparison.OrdinalIgnoreCase);
                if (sameFile && Sfs.HasCache(plugin) && !Program.IsCacheStale(plugin) && ReadNamesFromForms(plugin, want, result)) continue;
                ReadNamesFromHeaders(mod, path, want, result);
            }
            return result;
        }

        /// <summary>A file can only hold its own records and overrides of records from its masters.</summary>
        static bool CanHold(IStarfieldModGetter mod, FormKey key) =>
            key.ModKey == mod.ModKey || mod.ModHeader.MasterReferences.Any(m => m.Master == key.ModKey);

        static bool ReadNamesFromForms(string plugin, List<string> want, Dictionary<string, (string Type, string EditorID, string From)> result)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={Sfs.CachePath(plugin)};Mode=ReadOnly;Pooling=False");
                conn.Open();
                if (!Sfs.TableExists(conn, "Forms")) return false;
                foreach (var fk in want)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT Signature, EditorID FROM Forms WHERE {Sfs.FormKeyMatch("FormKey")} LIMIT 1";
                    Sfs.AddFormKeyParams(cmd, fk);
                    using var r = cmd.ExecuteReader();
                    if (r.Read()) result[fk] = (r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), plugin);
                }
                return true;
            }
            catch (SqliteException) { return false; }
        }

        static void ReadNamesFromHeaders(IStarfieldModGetter mod, string path, List<string> want, Dictionary<string, (string Type, string EditorID, string From)> result)
        {
            var masters = HeaderScanner.MasterTable(mod.ModKey, path);
            var raw = new Dictionary<uint, string>();
            foreach (var fk in want)
                foreach (var form in HeaderScanner.RawForms(masters, mod.ModKey, path, FormKey.Factory(fk)))
                    raw[form] = fk;
            var found = new List<(string Fk, string Sig, string? EditorID)>();
            HeaderScanner.Scan(path, (h, v) =>
            {
                if (raw.TryGetValue(h.FormId, out var fk)) found.Add((fk, h.Signature, HeaderScanner.EditorIdOf(h, v)));
                return found.Count == want.Count;
            });
            foreach (var (fk, sig, editorId) in found)
            {
                var getter = HeaderScanner.GetterFor(sig);
                string? edid = editorId;
                if (edid == null && getter != null) edid = FindInGroup(mod, FormKey.Factory(fk), getter)?.EditorID; // compressed record
                string type = getter == null ? sig : getter.Name.Substring(1, getter.Name.Length - "IGetter".Length);
                result[fk] = (type, edid ?? "", mod.ModKey.FileName.String);
            }
        }

        /// <summary>A record from its file's top-level group. Null for records nested in cells, topics and so on.</summary>
        static IMajorRecordGetter? FindInGroup(IStarfieldModGetter mod, FormKey key, Type getter)
        {
            try
            {
                var group = mod.TryGetTopLevelGroup(getter);
                return group != null && group.ContainsKey(key) ? group.RecordCache[key] : null;
            }
            catch (ArgumentException) { return null; }
        }

        // ---------------------------------------------------------------- Field search (--sig + --field with no record)

        static Regex PatternRegex(string pattern) =>
            new("^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static void RunFieldSearch(string plugin, string sig, string fieldPattern, string? editorIdPattern, string? valueText, bool onlyNew, bool showEmpty, bool all)
        {
            var getter = GetterType(sig);
            if (getter == null) { Console.WriteLine($"Unknown record type: {sig}"); return; }
            using var slim = SlimLoader.Open(plugin, Array.Empty<string>());
            slim.WarnMissingMasters();
            var mod = slim.Mods.First(m => m.ModKey == slim.Target);
            var field = PatternRegex(fieldPattern);
            var edid = editorIdPattern == null ? null : PatternRegex(editorIdPattern);
            const int RowLimit = 1000;
            var t = new Sfs.Table("FormKey", "EditorID", "Override", "Path", "Value");
            int read = 0, matched = 0, values = 0;
            bool cut = false;
            void Row(params object?[] cols)
            {
                if (!all && t.Rows.Count >= RowLimit) { cut = true; return; }
                t.Add(cols);
            }
            foreach (var rec in mod.EnumerateMajorRecords(getter))
            {
                bool isOverride = rec.FormKey.ModKey != slim.Target;
                if (onlyNew && isOverride) continue;
                if (edid != null && !edid.IsMatch(rec.EditorID ?? "")) continue;
                read++;
                string fk = Sfs.NormalizeFormKey(rec.FormKey.ToString());
                var json = Program.RecordToJson(rec, slim.LinkCache, out _, full: true);
                var hits = Leaves(json).Where(l => field.IsMatch(l.Path) && (valueText == null || l.Value.Contains(valueText, StringComparison.OrdinalIgnoreCase))).ToList();
                if (hits.Count > 0) matched++;
                values += hits.Count;
                foreach (var (path, value) in hits) Row(fk, rec.EditorID ?? "", isOverride ? "yes" : "", path, value);
                if (hits.Count == 0 && showEmpty) Row(fk, rec.EditorID ?? "", isOverride ? "yes" : "", "(no match)", "");
            }
            Console.WriteLine($"## Live search: {sig} in {slim.Target.FileName}, field {fieldPattern}");
            Console.WriteLine($"Read {read} record(s); {matched} matched, {read - matched} did not, {values} value(s).{(cut ? $" Showing the first {RowLimit} rows; use --all for the rest." : "")}");
            Console.WriteLine();
            if (t.Rows.Count > 0) Sfs.Print(t, annotate: true);
        }

        // ---------------------------------------------------------------- Reverse lookup (--refs)

        /// <summary>
        /// What points at a record, without a cache: finds the FormID's raw bytes in every record of the plugin (and any --with
        /// plugins), then confirms each hit through Mutagen. Bytes that only look like the FormID, or that sit in a record
        /// Mutagen can't check, are listed apart and never counted as links.
        /// </summary>
        static void PrintLiveRefs(SlimLoader slim, FormKey target, List<string> extras)
        {
            slim.WarnMissingMasters();
            string targetStr = Sfs.NormalizeFormKey(target.ToString());
            string dataFolder = Program.GetDataFolderPathPublic();
            var searchKeys = new HashSet<ModKey> { slim.Target };
            foreach (var e in extras) searchKeys.Add(SlimLoader.ResolvePlugin(e, dataFolder).Item1);

            var links = new Sfs.Table("Plugin", "FromFormKey", "Signature", "EditorID", "Path");
            var unconfirmed = new Sfs.Table("Plugin", "FromFormKey", "Signature", "Why");
            long records = 0, compressed = 0, unreadable = 0;
            var linkRecords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in slim.Mods.Where(m => searchKeys.Contains(m.ModKey)))
            {
                string plugin = mod.ModKey.FileName.String;
                if (!CanHold(mod, target)) { Console.WriteLine($"{plugin} can't point at it: {target.ModKey.FileName} isn't one of its masters."); continue; }
                string path = slim.Paths[mod.ModKey];
                var masters = HeaderScanner.MasterTable(mod.ModKey, path);
                var nested = new Dictionary<Type, Dictionary<FormKey, IMajorRecordGetter>>();
                var (hits, read, zipped, bad) = FindRawFormId(path, HeaderScanner.RawForms(masters, mod.ModKey, path, target));
                records += read; compressed += zipped; unreadable += bad;
                foreach (var h in hits.OrderBy(x => x.Offset))
                {
                    var key = masters.GetFormKey(FormID.Factory(h.FormId), reference: false);
                    string keyStr = Sfs.NormalizeFormKey(key.ToString());
                    var getter = HeaderScanner.GetterFor(h.Signature);
                    IMajorRecordGetter? rec = null;
                    if (getter != null)
                    {
                        rec = FindInGroup(mod, key, getter);
                        if (rec == null && IsCellOrPlaced(getter))
                            rec = FindInCells(slim, key, getter, HeaderScanner.CellKey(masters, h))?.FirstOrDefault(v => v.ModKey == mod.ModKey)?.Record;
                        else if (rec == null && h.CellFormId == 0)
                        {
                            // Nested in a quest or topic (scenes, topics, responses). One walk per type: walking cell contents is the slow case, so it stays out.
                            if (!nested.TryGetValue(getter, out var byKey))
                                nested[getter] = byKey = mod.EnumerateMajorRecords(getter).GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First());
                            byKey.TryGetValue(key, out rec);
                        }
                    }
                    if (rec == null) { unconfirmed.Add(plugin, keyStr, h.Signature, h.CellFormId != 0 ? "bytes match; it sits inside a cell, and only placed references are checked there" : "bytes match; this record type can't be checked here"); continue; }
                    if (!rec.EnumerateFormLinks().Any(l => l.FormKey == target)) { unconfirmed.Add(plugin, keyStr, rec.Registration.Name, "bytes match, but it isn't a link Mutagen reads"); continue; }
                    linkRecords.Add(keyStr);
                    var paths = Leaves(Program.RecordToJson(rec, slim.LinkCache, out _, full: true))
                        .Where(l => !l.Path.StartsWith("Record Header") && Sfs.EmbeddedFormKey.Matches(l.Value).Any(m => string.Equals(Sfs.NormalizeFormKey(m.Value), targetStr, StringComparison.OrdinalIgnoreCase)))
                        .Select(l => l.Path).ToList();
                    if (paths.Count == 0) paths.Add("(link; field not shown in this record's fields)");
                    foreach (var p in paths) links.Add(plugin, keyStr, rec.Registration.Name, rec.EditorID ?? "", p);
                }
            }
            Console.WriteLine($"## Live references to {targetStr}");
            Console.WriteLine($"Searched {records} records ({compressed} compressed{(unreadable > 0 ? $", {unreadable} COULD NOT BE READ" : "")}): {linkRecords.Count} record(s) link to it, {links.Rows.Count} link(s).");
            Console.WriteLine();
            if (links.Rows.Count > 0) Sfs.Print(links, annotate: false);
            if (unconfirmed.Rows.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"### Not confirmed as links ({unconfirmed.Rows.Count})");
                Sfs.Print(unconfirmed, annotate: false);
            }
        }

        /// <summary>Every record whose (decompressed) data contains the 4 bytes of a raw FormID. Reads the file in parallel batches.</summary>
        /// <summary>Does any record in this file hold these bytes? A quick check before building a plugin's database.</summary>
        internal static bool FileMentions(string plugin, FormKey target)
        {
            string path = Path.Combine(Program.GetDataFolderPathPublic(), plugin);
            if (!File.Exists(path)) return false;
            var key = ModKey.FromFileName(Path.GetFileName(path));
            var forms = HeaderScanner.RawForms(HeaderScanner.MasterTable(key, path), key, path, target);
            return FindRawFormId(path, forms).Hits.Count > 0;
        }

        /// <summary>Does this file define or override the record? Record headers only.</summary>
        internal static bool FileHasRecord(string plugin, FormKey target)
        {
            string path = Path.Combine(Program.GetDataFolderPathPublic(), plugin);
            if (!File.Exists(path)) return false;
            var key = ModKey.FromFileName(Path.GetFileName(path));
            var forms = HeaderScanner.RawForms(HeaderScanner.MasterTable(key, path), key, path, target);
            bool found = false;
            HeaderScanner.Scan(path, (h, _) => { if (forms.Contains(h.FormId)) { found = true; return true; } return false; });
            return found;
        }

        internal static (List<HeaderScanner.Hit> Hits, long Records, long Compressed, long Unreadable) FindRawFormIdPublic(string path, uint[] rawForms) => FindRawFormId(path, rawForms);
        static (List<HeaderScanner.Hit> Hits, long Records, long Compressed, long Unreadable) FindRawFormId(string path, uint[] rawForms)
        {
            const int BatchSize = 50000;
            const uint CompressedFlag = 0x00040000;
            const int HeaderLength = 24;
            byte[][] needles = rawForms.Select(x => BitConverter.GetBytes(x)).ToArray();
            var hits = new ConcurrentBag<HeaderScanner.Hit>();
            long records = 0, compressed = 0, unreadable = 0;
            using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var batch = new List<HeaderScanner.Hit>(BatchSize);
            void Flush()
            {
                if (batch.Count == 0) return;
                var current = batch;
                Parallel.ForEach(Partitioner.Create(0, current.Count, 2000), range =>
                {
                    var data = new byte[1 << 16];
                    var plain = new byte[1 << 16];
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        var h = current[i];
                        int size = (int)h.DataSize;
                        if (size < 4) continue;
                        if (data.Length < size) data = new byte[size * 2];
                        view.ReadArray(h.Offset + HeaderLength, data, 0, size);
                        ReadOnlySpan<byte> bytes = data.AsSpan(0, size);
                        if ((h.Flags & CompressedFlag) != 0)
                        {
                            Interlocked.Increment(ref compressed);
                            try
                            {
                                int plainSize = BitConverter.ToInt32(data, 0);
                                if (plain.Length < plainSize) plain = new byte[plainSize * 2];
                                using var zip = new ZLibStream(new MemoryStream(data, 4, size - 4), CompressionMode.Decompress);
                                zip.ReadExactly(plain, 0, plainSize);
                                bytes = plain.AsSpan(0, plainSize);
                            }
                            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException)
                            {
                                Interlocked.Increment(ref unreadable);
                                continue;
                            }
                        }
                        foreach (var needle in needles)
                        {
                            if (bytes.IndexOf(needle) < 0) continue;
                            hits.Add(h);
                            break;
                        }
                    }
                });
                batch.Clear();
            }
            HeaderScanner.Scan(path, (h, _) =>
            {
                records++;
                batch.Add(h);
                if (batch.Count >= BatchSize) Flush();
                return false;
            });
            Flush();
            return (hits.ToList(), records, compressed, unreadable);
        }

        static void PrintVsCache(JsonObject live, string plugin, string fk, string sig, bool all)
        {
            Console.WriteLine($"### Live vs cache ({plugin})");
            string dbPath = Sfs.CachePath(plugin);
            if (!File.Exists(dbPath)) { Console.WriteLine("No cache exists for this plugin: every field above is live-only."); return; }

            string dataPath = Path.Combine(Program.GetDataFolderPathPublic(), plugin);
            if (File.Exists(dataPath) && Program.IsCacheStale(plugin))
                Console.WriteLine($"Cache is STALE: it was not built from the {plugin} now in Data (size, date or SFSearch format changed; plugin written {File.GetLastWriteTime(dataPath):yyyy-MM-dd HH:mm}, cache {File.GetLastWriteTime(dbPath):yyyy-MM-dd HH:mm}).");

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            var row = Sfs.FindRecord(conn, plugin, fk);
            if (row == null)
            {
                string why = "not in Records";
                if (Sfs.TableExists(conn, "SignatureCounts"))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Cached FROM SignatureCounts WHERE Signature = @s";
                    cmd.Parameters.AddWithValue("@s", sig);
                    var c = cmd.ExecuteScalar();
                    why = c == null ? "type not in this cache at all" : Convert.ToInt32(c) switch { 0 => $"{sig} is a Forms-only type (cache keeps no fields)", 2 => sig.Equals("GenericBaseForm", StringComparison.OrdinalIgnoreCase) ? "ship-builder parts (SMS_, SMX_ and so on) aren't cached, only whole ships" : $"{sig} only partly cached (placed references with scripts)", _ => "type is cached but this record is missing" };
                }
                Console.WriteLine($"Cache has no field data for this record: {why}.");
                return;
            }

            var a = Leaves(row.Data).GroupBy(l => l.Path).ToDictionary(g => g.Key, g => g.First().Value);
            var b = Leaves(live).GroupBy(l => l.Path).ToDictionary(g => g.Key, g => g.First().Value);
            var t = new Sfs.Table("Path", "Cache", "Live", "Note");
            int shown = 0, changed = 0, liveOnly = 0, cacheOnly = 0;
            foreach (var path in a.Keys.Union(b.Keys).OrderBy(p => p, StringComparer.Ordinal))
            {
                a.TryGetValue(path, out var va);
                b.TryGetValue(path, out var vb);
                if (va == vb) continue;
                string top = path.Split('.', '[')[0];
                string note;
                if (va == null) { liveOnly++; note = Program.IsCacheSkippedProperty(top) ? "cache skips this field" : "missing from cache"; }
                else if (vb == null) { cacheOnly++; note = "not in live record"; }
                else { changed++; note = "differs"; }
                if (all || shown < 200) { t.Add(path, va ?? "", vb ?? "", note); shown++; }
            }
            Console.WriteLine($"{a.Count} cached leaf values, {b.Count} live: {changed} differ, {liveOnly} live-only, {cacheOnly} cache-only.");
            if (t.Rows.Count > 0) Sfs.Print(t, annotate: false);
            if (!all && changed + liveOnly + cacheOnly > shown) Console.WriteLine($"(showing {shown}; use --all)");
        }

        /// <summary>
        /// Raw subrecords of the record as Mutagen read it (decompressed), straight from the overlay's byte slice.
        /// Shows data Mutagen doesn't decode (Unknown fields) and subrecords it skips.
        /// </summary>
        static void PrintSubrecords(IMajorRecordGetter rec, bool all)
        {
            Console.WriteLine("### Raw subrecords");
            var bytes = RawRecordBytes(rec);
            if (bytes == null) { Console.WriteLine("(this record's raw bytes are not reachable through the overlay)"); return; }
            if (rec is IStarfieldMajorRecordGetter sr)
                Console.WriteLine($"Header: flags 0x{rec.MajorRecordFlagsRaw:X8}, form version {sr.FormVersion}, version control 0x{sr.VersionControl:X8}");

            // The slice may or may not start with the 24-byte record header: use the offset where the subrecords parse cleanly.
            int start = ParsesCleanly(bytes, 0) ? 0 : ParsesCleanly(bytes, 24) ? 24 : -1;
            if (start < 0) { Console.WriteLine($"(could not parse {bytes.Length} bytes as subrecords)"); return; }

            var t = new Sfs.Table("#", "Sig", "Len", "Value");
            int pos = start, i = 0;
            uint bigLen = 0;
            while (pos + 6 <= bytes.Length)
            {
                string s = Encoding.ASCII.GetString(bytes, pos, 4);
                int len = BitConverter.ToUInt16(bytes, pos + 4);
                pos += 6;
                if (s == "XXXX") { bigLen = BitConverter.ToUInt32(bytes, pos); pos += len; continue; }
                if (bigLen > 0) { len = (int)bigLen; bigLen = 0; }
                t.Add(++i, s, len, Preview(bytes, pos, len, all));
                pos += len;
            }
            Sfs.Print(t, annotate: false);
        }

        static byte[]? RawRecordBytes(IMajorRecordGetter rec)
        {
            for (var type = rec.GetType(); type != null; type = type.BaseType)
            {
                foreach (var f in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (f.Name != "_recordData" && f.Name != "_data") continue;
                    var v = f.GetValue(rec);
                    if (v is Noggog.ReadOnlyMemorySlice<byte> slice) return slice.ToArray();
                    if (v is ReadOnlyMemory<byte> mem) return mem.ToArray();
                    if (v is byte[] arr) return arr;
                }
            }
            return null;
        }

        static bool ParsesCleanly(byte[] b, int pos)
        {
            uint bigLen = 0;
            if (pos >= b.Length) return false;
            while (pos + 6 <= b.Length)
            {
                for (int k = 0; k < 4; k++) { byte c = b[pos + k]; if (!(c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_')) return false; }
                int len = BitConverter.ToUInt16(b, pos + 4);
                bool isX = b[pos] == 'X' && b[pos + 1] == 'X' && b[pos + 2] == 'X' && b[pos + 3] == 'X';
                pos += 6;
                if (isX) { if (pos + 4 > b.Length) return false; bigLen = BitConverter.ToUInt32(b, pos); pos += len; continue; }
                if (bigLen > 0) { len = (int)bigLen; bigLen = 0; }
                pos += len;
            }
            return pos == b.Length;
        }

        static string Preview(byte[] b, int pos, int len, bool all)
        {
            if (pos + len > b.Length) len = Math.Max(0, b.Length - pos);
            if (len == 0) return "";
            // Zero-terminated text
            if (len > 1 && b[pos + len - 1] == 0 && Enumerable.Range(pos, len - 1).All(k => b[k] >= 0x20 && b[k] < 0x7F))
                return "\"" + Encoding.ASCII.GetString(b, pos, len - 1) + "\"";
            string hex = BitConverter.ToString(b, pos, all ? len : Math.Min(len, 32)).Replace("-", " ") + (!all && len > 32 ? " …" : "");
            if (len == 4)
            {
                uint u = BitConverter.ToUInt32(b, pos);
                float f = BitConverter.ToSingle(b, pos);
                string fs = float.IsFinite(f) && Math.Abs(f) > 1e-6 && Math.Abs(f) < 1e9 ? $" | float {f:G7}" : "";
                return $"{hex} | uint {u} (0x{u:X8}){fs}";
            }
            return hex;
        }

        // ---------------------------------------------------------------- JSON helpers

        static string Pretty(JsonNode n) => n is JsonValue ? n.ToString() : n.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        /// <summary>"Properties[2].Value" style navigation, case-insensitive keys.</summary>
        static JsonNode? Navigate(JsonNode root, string path)
        {
            JsonNode? cur = root;
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                string name = part;
                var idx = new List<int>();
                while (name.EndsWith("]") && name.LastIndexOf('[') is int lb and >= 0)
                {
                    idx.Insert(0, int.Parse(name[(lb + 1)..^1]));
                    name = name[..lb];
                }
                if (name.Length > 0)
                {
                    if (cur is not JsonObject o) return null;
                    cur = o.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
                }
                foreach (int i in idx)
                {
                    if (cur is not JsonArray a || i >= a.Count) return null;
                    cur = a[i];
                }
            }
            return cur;
        }

        static IEnumerable<(string Path, string Value)> Leaves(JsonNode? node, string path = "")
        {
            if (node is JsonObject o)
            {
                foreach (var kv in o)
                    foreach (var l in Leaves(kv.Value, path.Length == 0 ? kv.Key : $"{path}.{kv.Key}")) yield return l;
            }
            else if (node is JsonArray a)
            {
                for (int i = 0; i < a.Count; i++)
                    foreach (var l in Leaves(a[i], $"{path}[{i}]")) yield return l;
            }
            else if (node != null) yield return (path, node.ToString());
        }
    }
}
