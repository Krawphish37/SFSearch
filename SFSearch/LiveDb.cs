using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SFSearch
{
    /// <summary>
    /// Databases built only for what a command needs, straight from the plugin files, for `--live` (one command, then
    /// deleted) and the agent session (kept while the session is open; see SessionServer). A record type is read the first
    /// time a command needs it. The stored databases in Cache are never touched. Tested 2026-11-07 against 455 real past
    /// commands, with the same answers as the stored databases.
    /// </summary>
    public static class LiveDb
    {
        public static bool Enabled;
        static readonly bool Timing = Environment.GetEnvironmentVariable("SFSEARCH_LIVE_TIMING") == "1";
        public static string? CurrentSql;

        static void T(string what, Stopwatch w)
        {
            if (Timing) Console.Error.WriteLine($"[live] {what}: {w.Elapsed.TotalSeconds:F2} s");
            w.Restart();
        }

        // ---------------------------------------------------------------- What does this SQL need?
        public sealed class Needs
        {
            public bool Forms;
            public bool Records;
            public HashSet<string>? Types;          // null = every stored type
            public List<Regex>? EditorIds;          // lookup by EditorID: the raw pass tells which types hold them
            public HashSet<string>? FormKeys;       // lookup by FormKey
            public Filter? FormsFilter;             // rows of Forms the query can see
            /// <summary>Each plugin narrows its own copy: one plugin's types must not carry over to the next.</summary>
            public Needs Copy() => new() { Forms = Forms, Records = Records, Types = Types, EditorIds = EditorIds, FormKeys = FormKeys, FormsFilter = FormsFilter };
            public override string ToString() => $"forms={Forms}{(FormsFilter != null ? "(filtered)" : "")} records={Records} types={(Types == null ? "all" : string.Join(",", Types))}{(EditorIds != null ? $" edids={EditorIds.Count}" : "")}{(FormKeys != null ? $" formkeys={FormKeys.Count}" : "")}";
        }

        /// <summary>What a WHERE clause restricts: one kind of column, any of these values.</summary>
        public sealed class Filter
        {
            public HashSet<string>? Types;
            public List<Regex>? EditorIds;
            public HashSet<string>? FormKeys;
            public bool Either; // FormKeys or EditorIds, not both
            public bool Allows(string sig, string? edid, string fk) => Either
                ? (FormKeys != null && FormKeys.Contains(fk)) || (EditorIds != null && edid != null && EditorIds.Any(x => x.IsMatch(edid)))
                : (Types == null || Types.Contains(sig)) && (EditorIds == null || edid != null && EditorIds.Any(x => x.IsMatch(edid))) && (FormKeys == null || FormKeys.Contains(fk));
        }

        static readonly Regex Literal = new(@"'((?:[^']|'')*)'", RegexOptions.Compiled);

        static Regex LikeRegex(string pattern) => new("^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Splits text on a keyword at parenthesis depth 0, outside quotes.</summary>
        static List<string> SplitTop(string text, string word)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            bool quote = false;
            for (int k = 0; k < text.Length; k++)
            {
                char ch = text[k];
                if (ch == '\'') quote = !quote;
                if (quote) continue;
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (depth == 0 && k + word.Length + 2 <= text.Length && char.IsWhiteSpace(ch)
                    && string.Compare(text, k + 1, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0 && char.IsWhiteSpace(text[k + word.Length + 1]))
                {
                    parts.Add(text[start..k]);
                    start = k + word.Length + 1;
                }
            }
            parts.Add(text[start..]);
            return parts;
        }

        static string StripParens(string t)
        {
            t = t.Trim();
            while (t.StartsWith('(') && t.EndsWith(')'))
            {
                int depth = 0; bool whole = true;
                for (int k = 0; k < t.Length; k++)
                {
                    if (t[k] == '(') depth++;
                    else if (t[k] == ')') { depth--; if (depth == 0 && k < t.Length - 1) { whole = false; break; } }
                }
                if (!whole) break;
                t = t[1..^1].Trim();
            }
            return t;
        }

        /// <summary>The top-level WHERE of a single SELECT, read as a filter on one column kind. Null when it isn't that simple.</summary>
        static Filter? WhereFilter(string sql, HashSet<string> knownTypes)
        {
            var m = Regex.Match(sql, @"\bwhere\b(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            string where = m.Groups[1].Value;
            // stop at the clauses after WHERE (at depth 0)
            foreach (var stop in new[] { "group", "order", "limit", "having", "union" })
                where = SplitTop(" " + where + " ", stop)[0];
            var f = new Filter();
            bool any = false;
            foreach (var conj in SplitTop(" " + where + " ", "and"))
            {
                var atoms = SplitTop(" " + StripParens(conj) + " ", "or").Select(StripParens).ToList();
                string? kind = null;
                var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var edids = new List<Regex>();
                var fks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in atoms)
                {
                    Match x;
                    string k;
                    if ((x = Regex.Match(a, @"^(\w+\.)?signature\s*=\s*'([^']*)'$", RegexOptions.IgnoreCase)).Success && knownTypes.Contains(x.Groups[2].Value)) { k = "sig"; types.Add(x.Groups[2].Value); }
                    else if ((x = Regex.Match(a, @"^(\w+\.)?signature\s+in\s*\(([^)]*)\)$", RegexOptions.IgnoreCase)).Success) { k = "sig"; foreach (Match l in Literal.Matches(x.Groups[2].Value)) types.Add(l.Groups[1].Value); }
                    else if ((x = Regex.Match(a, @"^(\w+\.)?editorid\s*=\s*'((?:[^']|'')*)'$", RegexOptions.IgnoreCase)).Success) { k = "edid"; edids.Add(new Regex("^" + Regex.Escape(x.Groups[2].Value.Replace("''", "'")) + "$", RegexOptions.IgnoreCase)); }
                    else if ((x = Regex.Match(a, @"^(\w+\.)?editorid\s+like\s*'((?:[^']|'')*)'$", RegexOptions.IgnoreCase)).Success) { k = "edid"; edids.Add(LikeRegex(x.Groups[2].Value.Replace("''", "'"))); }
                    else if ((x = Regex.Match(a, @"^(\w+\.)?editorid\s+in\s*\(([^)]*)\)$", RegexOptions.IgnoreCase)).Success) { k = "edid"; foreach (Match l in Literal.Matches(x.Groups[2].Value)) edids.Add(new Regex("^" + Regex.Escape(l.Groups[1].Value) + "$", RegexOptions.IgnoreCase)); }
                    else if ((x = Regex.Match(a, @"^(\w+\.)?formkey\s*=\s*'([0-9A-Fa-f]{6}:[^']+)'$", RegexOptions.IgnoreCase)).Success) { k = "fk"; fks.Add(Sfs.NormalizeFormKey(x.Groups[2].Value)); }
                    else if ((x = Regex.Match(a, @"^(\w+\.)?formkey\s+in\s*\(([^)]*)\)$", RegexOptions.IgnoreCase)).Success && !x.Groups[2].Value.Contains("select", StringComparison.OrdinalIgnoreCase)) { k = "fk"; foreach (Match l in Literal.Matches(x.Groups[2].Value)) fks.Add(Sfs.NormalizeFormKey(l.Groups[1].Value)); }
                    else { kind = "other"; break; }
                    if (kind != null && kind != k) { kind = "other"; break; }
                    kind = k;
                }
                if (kind == "sig") { f.Types = f.Types == null ? types : f.Types.Intersect(types, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase); any = true; }
                else if (kind == "edid") { f.EditorIds ??= new(); if (f.EditorIds.Count == 0) f.EditorIds.AddRange(edids); else return null; any = true; }
                else if (kind == "fk") { f.FormKeys ??= fks; any = true; }
            }
            return any ? f : null;
        }

        /// <summary>Set by commands that look up given records, so only those are read.</summary>
        public static Needs? Forced;
        /// <summary>Set when a service build failed during this command (the worker then runs it again once).</summary>
        public static bool BuildFailed;
        public static string? RefsTarget;

        static readonly Regex SignatureLiterals = new(@"\bSignature\s*(?:=|==|IN\s*\(|LIKE)\s*((?:'[^']*'\s*,?\s*)+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The record types a query names with Signature = '…' or Signature IN ('…', …), anywhere in it. Null when none.</summary>
        public static HashSet<string>? TypesNamedIn(string sql)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in SignatureLiterals.Matches(sql))
                foreach (Match lit in Literal.Matches(m.Groups[1].Value))
                {
                    string t = lit.Groups[1].Value.Replace("''", "'");
                    if (!t.Contains('%') && !t.Contains('_')) found.Add(t);
                }
            return found.Count == 0 ? null : found;
        }

        /// <summary>Refs and Conflicts, once they know the record: read only what can point at it or hold it.</summary>
        public static void FocusOn(string command, string formKey)
        {
            if (!Enabled) return;
            if (command == "refs") { RefsTarget = formKey; Forced = null; return; }
            var fks = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { formKey };
            Forced = new Needs { Records = true, Forms = true, FormKeys = fks, FormsFilter = new Filter { FormKeys = fks, Either = true } };
        }

        public static void HintFromArgs(string command, List<string> args)
        {
            if (!Enabled) return;
            var fks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var edids = new List<Regex>();
            var all = args.SelectMany(a => a.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
            string? inList = null;
            for (int k = 0; k < all.Count; k++)
            {
                string a = all[k];
                if (a.StartsWith("--")) { if (a == "--in" && k + 1 < all.Count) inList = all[k + 1]; k++; continue; }
                if (Regex.IsMatch(a, @"^[0-9A-Fa-f]{6}:\S+\.es[mpl]$")) fks.Add(Sfs.NormalizeFormKey(a));
                else if (Regex.IsMatch(a, @"\.es[mpl]$", RegexOptions.IgnoreCase) || Program.AllStoredTypes.Contains(a) || Regex.IsMatch(a, @"^[0-9A-Fa-f]{1,6}$")) { }
                else edids.Add(new Regex("^" + Regex.Escape(a) + "$", RegexOptions.IgnoreCase));
            }
            // A bare FormID: every plugin --in names, or Names' defaults
            var bare = all.Where(a => Regex.IsMatch(a, @"^[0-9A-Fa-f]{6}$")).ToList();
            if (bare.Count > 0)
            {
                var firstPlugin = command is "inspect" or "dump" or "read" ? all.FirstOrDefault(x => Regex.IsMatch(x, @"\.es[mpl]$", RegexOptions.IgnoreCase)) : null;
                var plugins = inList?.Split(',') ?? (firstPlugin != null ? new[] { firstPlugin } : new[] { "darkstar.esm", "Starfield.esm", "DarkStar_Manufacturing.esm" });
                foreach (var b in bare) foreach (var pl in plugins) fks.Add(Sfs.NormalizeFormKey(b + ":" + pl));
            }
            if (command == "refs" && fks.Count == 1) { RefsTarget = fks.First(); return; }
            Forced = new Needs { Records = true, Forms = true, FormKeys = fks.Count > 0 ? fks : null, EditorIds = edids.Count > 0 ? edids : null,
                FormsFilter = new Filter { FormKeys = fks.Count > 0 ? fks : null, EditorIds = edids.Count > 0 ? edids : null, Either = true } };
            if (fks.Count == 0 && edids.Count == 0) Forced = null;
        }

        /// <summary>The plugins in Data that really have this record: header pass over the ones that can hold it.</summary>
        public static List<string> PluginsContaining(string formKey)
        {
            var key = FormKey.Factory(formKey);
            var result = new System.Collections.Concurrent.ConcurrentBag<string>();
            string data = Program.GetDataFolderPathPublic();
            Parallel.ForEach(PluginsThatCanHold(formKey), name =>
            {
                try
                {
                    string path = Path.Combine(data, name);
                    var mk = ModKey.FromFileName(name);
                    var forms = HeaderScanner.RawForms(HeaderScanner.MasterTable(mk, path), mk, path, key);
                    bool found = false;
                    HeaderScanner.Scan(path, (h, _) => { if (forms.Contains(h.FormId)) { found = true; return true; } return false; });
                    if (found) result.Add(name);
                }
                catch { }
            });
            return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Every plugin in Data that defines or overrides a record with this EditorID (one raw pass per plugin, kept for the session).</summary>
        public static List<(string Plugin, string FormKey)> FindEditorIdInData(string editorId)
        {
            string data = Program.GetDataFolderPathPublic();
            var hits = new System.Collections.Concurrent.ConcurrentBag<(string, string)>();
            var files = Directory.GetFiles(data).Where(f => Regex.IsMatch(f, @"\.es[mpl]$", RegexOptions.IgnoreCase)).ToList();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, f =>
            {
                try
                {
                    var name = Path.GetFileName(f);
                    var mk = ModKey.FromFileName(name);
                    var raw = RawScan(f);
                    if (!raw.Any(r => r.EditorId != null && r.EditorId.Equals(editorId, StringComparison.OrdinalIgnoreCase))) return;
                    var mt = HeaderScanner.MasterTable(mk, f);
                    foreach (var r in raw.Where(r => r.EditorId != null && r.EditorId.Equals(editorId, StringComparison.OrdinalIgnoreCase)))
                        hits.Add((name, Sfs.NormalizeFormKey(mt.GetFormKey(FormID.Factory(r.Raw), reference: false).ToString())));
                }
                catch { }
            });
            return hits.ToList();
        }

        public static List<string> PluginsThatCanHold(string formKey)
        {
            string plugin = formKey.Contains(':') ? formKey[(formKey.IndexOf(':') + 1)..] : "";
            string data = Program.GetDataFolderPathPublic();
            var result = new List<string>();
            foreach (var f in Directory.GetFiles(data).Where(f => Regex.IsMatch(f, @"\.es[mpl]$", RegexOptions.IgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(f);
                if (name.Equals(plugin, StringComparison.OrdinalIgnoreCase)) { result.Add(name); continue; }
                try
                {
                    var b = new byte[24];
                    using var fs = File.OpenRead(f);
                    if (fs.Read(b, 0, 24) < 24 || Encoding.ASCII.GetString(b, 0, 4) != "TES4") continue;
                    var head = new byte[BitConverter.ToInt32(b, 4)];
                    fs.ReadExactly(head);
                    int pos = 0;
                    while (pos + 6 <= head.Length)
                    {
                        string sig = Encoding.ASCII.GetString(head, pos, 4);
                        int len = BitConverter.ToUInt16(head, pos + 4);
                        if (sig == "MAST" && Encoding.UTF8.GetString(head, pos + 6, Math.Max(0, len - 1)).Equals(plugin, StringComparison.OrdinalIgnoreCase)) { result.Add(name); break; }
                        pos += 6 + len;
                    }
                }
                catch { }
            }
            return result;
        }

        public static Needs Analyze(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return new Needs { Forms = true, Records = true, Types = null };
            string s = Regex.Replace(sql, @"--[^\n]*", " ");
            string l = s.ToLowerInvariant();
            bool has(string word) => Regex.IsMatch(l, $@"\b{word}\b");
            int recordUses = Regex.Matches(l, @"\b(from|join)\s+(\w+\.)?(records|allrecords)\b").Count + Regex.Matches(l, @",\s*(\w+\.)?records\b").Count;
            int formUses = Regex.Matches(l, @"\b(from|join)\s+(\w+\.)?forms\b").Count + Regex.Matches(l, @",\s*(\w+\.)?forms\b").Count;
            int selects = Regex.Matches(l, @"\bselect\b").Count;
            var n = new Needs
            {
                Forms = formUses > 0 || has("signaturecounts"),
                Records = recordUses > 0 || has("refs"),
            };
            var known = Program.AllStoredTypes;
            bool simple = selects == 1 && !has("refs") && !has("allrecords") && !has("union");
            if (n.Records && simple && recordUses == 1)
            {
                var f = WhereFilter(s, known);
                if (f?.Types != null) n.Types = f.Types;
                else if (f?.EditorIds != null) n.EditorIds = f.EditorIds;
                else if (f?.FormKeys != null) n.FormKeys = f.FormKeys;
            }
            if (!n.Records) n.Types = new();
            if (n.Forms && simple && formUses == 1 && recordUses == 0 && !has("signaturecounts")) n.FormsFilter = WhereFilter(s, known);
            if (n.Forms && formUses == 0) n.FormsFilter = new Filter { Either = true }; // counts come from the raw pass; no rows needed
            return n;
        }

        /// <summary>A group's records in file order (the database's own order); a record that can't be read is skipped.</summary>
        static IEnumerable<IMajorRecordGetter?> SafeRecords(IGroupGetter group)
        {
            using var e = group.Records.GetEnumerator();
            while (true)
            {
                IMajorRecordGetter? rec = null;
                try { if (!e.MoveNext()) yield break; rec = e.Current; } catch { yield break; }
                yield return rec;
            }
        }

        // ---------------------------------------------------------------- One pass over the raw file
        public sealed record RawRow(uint Raw, string Sig4, string? EditorId, bool Scripts, uint BaseRaw, bool Placed);

        static readonly ConcurrentDictionary<string, List<RawRow>> _rawCache = new(StringComparer.OrdinalIgnoreCase);

        public static List<RawRow> RawScan(string path)
        {
            return _rawCache.GetOrAdd(StampKey(path), _ => ScanRaw(path));
        }

        /// <summary>The same pass without keeping the result (the load-order index reads every plugin once).</summary>
        public static List<RawRow> RawScanOnce(string path) => _rawCache.TryGetValue(StampKey(path), out var rows) ? rows : ScanRaw(path);

        static List<RawRow> ScanRaw(string path)
        {
            {
                string p = path;
                const uint Compressed = 0x00040000;
                const int Head = 24;
                var hits = new List<HeaderScanner.Hit>();
                using var mmf = MemoryMappedFile.CreateFromFile(p, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                HeaderScanner.Scan(p, (h, _) => { hits.Add(h); return false; });
                var rows = new RawRow[hits.Count];
                if (hits.Count == 0) return new List<RawRow>();
                Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, hits.Count, 2000), range =>
                {
                    var data = new byte[1 << 16];
                    var plain = new byte[1 << 16];
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        var h = hits[i];
                        string? edid = null; bool scripts = false; uint baseRaw = 0;
                        int size = (int)h.DataSize;
                        bool placed = h.Signature is "REFR" or "ACHR";
                        if (size >= 6)
                        {
                            if (data.Length < size) data = new byte[size * 2];
                            view.ReadArray(h.Offset + Head, data, 0, size);
                            byte[] b = data; int end = size;
                            bool ok = true;
                            if ((h.Flags & Compressed) != 0)
                            {
                                try
                                {
                                    int plainSize = BitConverter.ToInt32(data, 0);
                                    if (plain.Length < plainSize) plain = new byte[plainSize * 2];
                                    using var z = new ZLibStream(new MemoryStream(data, 4, size - 4), CompressionMode.Decompress);
                                    z.ReadExactly(plain, 0, plainSize);
                                    b = plain; end = plainSize;
                                }
                                catch { ok = false; }
                            }
                            int pos = 0; uint extended = 0;
                            while (ok && pos + 6 <= end)
                            {
                                uint sig = BitConverter.ToUInt32(b, pos);
                                int len = BitConverter.ToUInt16(b, pos + 4);
                                if (extended != 0) { len = (int)extended; extended = 0; }
                                if (sig == 0x58585858 /* XXXX */) { extended = BitConverter.ToUInt32(b, pos + 6); pos += 6 + len; continue; }
                                if (pos + 6 + len > end) break;
                                if (sig == 0x44494445 /* EDID */ && len > 1) edid = Encoding.UTF8.GetString(b, pos + 6, len - 1);
                                else if (sig == 0x44414D56 /* VMAD */ && len >= 6) scripts = BitConverter.ToUInt16(b, pos + 6 + 4) > 0;
                                else if (placed && sig == 0x454D414E /* NAME */ && len >= 4) baseRaw = BitConverter.ToUInt32(b, pos + 6);
                                pos += 6 + len;
                            }
                        }
                        rows[i] = new RawRow(h.FormId, h.Signature, edid, scripts, baseRaw, h.CellFormId != 0 && h.Signature is not ("NAVM" or "LAND" or "CELL"));
                    }
                });
                return rows.ToList();
            }
        }

        /// <summary>Drops what this process remembers of plugin files (raw passes, EditorID maps). A service worker does this
        /// after a command when it holds more than about 2 GB, so idle workers don't each keep a copy of Starfield.esm's records.</summary>
        public static void TrimMemory()
        {
            _rawCache.Clear(); // the EditorID maps (_edids) stay: they are small, and labels need them on every command
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        }

        /// <summary>A path plus its size and date: a plugin changed on disk is never answered from what was read before.</summary>
        static string StampKey(string path)
        {
            var fi = new FileInfo(path);
            return fi.Exists ? $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : path;
        }

        // ---------------------------------------------------------------- EditorIDs with no database
        static readonly ConcurrentDictionary<string, Dictionary<string, string>> _edids = new(StringComparer.OrdinalIgnoreCase);

        public static string? EditorIdOf(string formKey)
        {
            int colon = formKey.IndexOf(':');
            if (colon < 0) return null;
            string plugin = formKey[(colon + 1)..];
            var map = _edids.GetOrAdd(StampKey(Path.Combine(Program.GetDataFolderPathPublic(), plugin)) + "|" + plugin, _ =>
            {
                string pl = plugin;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string path = Path.Combine(Program.GetDataFolderPathPublic(), pl);
                if (!File.Exists(path)) return d;
                var key = ModKey.FromFileName(pl);
                var masters = HeaderScanner.MasterTable(key, path);
                foreach (var r in RawScan(path))
                {
                    if (r.EditorId == null) continue;
                    var fk = masters.GetFormKey(FormID.Factory(r.Raw), reference: false);
                    if (fk.ModKey == key) d[fk.ID.ToString("X6")] = r.EditorId;
                }
                return d;
            });
            return map.TryGetValue(formKey[..colon], out var e) ? e : null;
        }

        // ---------------------------------------------------------------- The throwaway database
        sealed record Row(string Fk, string Sig, string Edid, string Name, string Origin, int Ovr, string Data, List<(string Path, string To)> Refs);

        // ------------------------------------------------ The service's databases (Service\ServiceHost.cs)
        // What a database holds is written in it (table Built), not kept in memory, so every worker sees the same thing:
        // a record type's name, "*" for every stored type, "#forms" for the list of every record.
        static readonly HashSet<string> _primary = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Null only when there is no database. A database that can't be read for a moment (another process is
        /// opening or closing it) is tried again; one that still can't be read throws, so it is never mistaken for a missing
        /// one and written over.</summary>
        static HashSet<string>? ReadBuilt(string dbPath)
        {
            for (int attempt = 0; ; attempt++)
            {
                if (!File.Exists(dbPath)) return null;
                try
                {
                    using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT Kind FROM Built";
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) set.Add(r.GetString(0));
                    return set;
                }
                catch (SqliteException) when (attempt < 6) { System.Threading.Thread.Sleep(100 * (attempt + 1)); }
            }
        }

        static bool Holds(HashSet<string> built, HashSet<string>? want, bool needForms) =>
            (!needForms || built.Contains("#forms")) && (built.Contains("*") || (want != null && want.All(built.Contains)));

        public static void SetPrimary(IEnumerable<string> targets) { _primary.Clear(); foreach (var t in targets) _primary.Add(t); }

        public static void ResetForCommand()
        {
            CurrentSql = null; Forced = null; RefsTarget = null; BuildFailed = false; _primary.Clear();
            Sfs.Format = OutputFormat.Markdown; Sfs.MaxWidth = 0; Sfs.ResolveNames = true;
        }

        /// <summary>A new service database: built under a temporary name and renamed when complete, so no one ever opens half of one.</summary>
        static void CreateServiceDb(string dbPath, IStarfieldModGetter mod, string path, string modFileName)
        {
            string tmp = $"{dbPath}.tmp-{Environment.ProcessId}";
            foreach (var f in new[] { tmp, tmp + "-wal", tmp + "-shm", tmp + "-journal" }) if (File.Exists(f)) File.Delete(f);
            using (var c = new SqliteConnection($"Data Source={tmp};Pooling=False"))
            {
                c.Open();
                void Sql(string q) { using var x = c.CreateCommand(); x.CommandText = q; x.ExecuteNonQuery(); }
                Sql("PRAGMA journal_mode = WAL;");
                using var tx = c.BeginTransaction();
                using (var x = c.CreateCommand())
                {
                    x.Transaction = tx;
                    x.CommandText = @"CREATE TABLE Records (FormKey TEXT PRIMARY KEY, Signature TEXT, EditorID TEXT, Name TEXT, Origin TEXT, IsOverride INTEGER, Data TEXT);
                      CREATE TABLE Masters (Idx INTEGER, MasterName TEXT PRIMARY KEY);
                      CREATE TABLE Meta (Key TEXT PRIMARY KEY, Value TEXT);
                      CREATE TABLE SignatureCounts (Signature TEXT PRIMARY KEY, TotalCount INTEGER, OverrideCount INTEGER, Cached INTEGER);
                      CREATE TABLE Refs (FromFormKey TEXT, FromSignature TEXT, FromEditorID TEXT, Path TEXT, ToFormKey TEXT);
                      CREATE TABLE Forms (FormKey TEXT PRIMARY KEY, Signature TEXT, EditorID TEXT, IsOverride INTEGER, Base TEXT, Parent TEXT, HasScripts INTEGER);
                      CREATE TABLE Built (Kind TEXT PRIMARY KEY);
                      CREATE INDEX idx_signature ON Records(Signature); CREATE INDEX idx_editorid ON Records(EditorID); CREATE INDEX idx_isoverride ON Records(IsOverride);
                      CREATE INDEX idx_refs_to ON Refs(ToFormKey); CREATE INDEX idx_refs_from ON Refs(FromFormKey);
                      CREATE INDEX idx_forms_sig ON Forms(Signature); CREATE INDEX idx_forms_base ON Forms(Base); CREATE INDEX idx_forms_parent ON Forms(Parent);";
                    x.ExecuteNonQuery();
                }
                void Meta(string k, string v) { using var x = c.CreateCommand(); x.Transaction = tx; x.CommandText = "INSERT OR REPLACE INTO Meta VALUES (@k,@v)"; x.Parameters.AddWithValue("@k", k); x.Parameters.AddWithValue("@v", v); x.ExecuteNonQuery(); }
                int idx = 0;
                foreach (var m in mod.MasterReferences)
                {
                    using var x = c.CreateCommand(); x.Transaction = tx;
                    x.CommandText = "INSERT OR IGNORE INTO Masters VALUES (@i,@n)"; x.Parameters.AddWithValue("@i", idx++); x.Parameters.AddWithValue("@n", m.Master.FileName.String); x.ExecuteNonQuery();
                }
                var flags = mod.ModHeader.Flags;
                Meta("SchemaVersion", Program.SchemaVersionPublic.ToString());
                Meta("CachedTypes", Program.FingerprintPublic);
                Meta("SourceStamp", Program.SourceStampPublic(path));
                Meta("FileName", modFileName);
                Meta("MasterStyle", flags.HasFlag(StarfieldModHeader.HeaderFlag.Light) ? "Light (Small)" : flags.HasFlag(StarfieldModHeader.HeaderFlag.Medium) ? "Medium" : "Full");
                Meta("HeaderFlags", flags.ToString());
                Meta("Author", mod.ModHeader.Author ?? "");
                Meta("Description", mod.ModHeader.Description ?? "");
                // The counts the database keeps, from the record headers only
                var mt = HeaderScanner.MasterTable(mod.ModKey, path);
                int total = 0, overrides = 0, placed = 0; uint maxLocal = 0;
                HeaderScanner.Scan(path, (h, _) =>
                {
                    total++;
                    if (h.CellFormId != 0 && h.Signature is not ("NAVM" or "LAND" or "CELL")) placed++;
                    var k = mt.GetFormKey(FormID.Factory(h.FormId), reference: false);
                    if (!k.ModKey.FileName.String.Equals(modFileName, StringComparison.OrdinalIgnoreCase)) overrides++;
                    else if (k.ID > maxLocal) maxLocal = k.ID;
                    return false;
                });
                Meta("RecordCount", total.ToString());
                Meta("OverrideCount", overrides.ToString());
                Meta("MaxLocalFormID", $"0x{maxLocal:X6}");
                Meta("PlacedMode", placed <= Program.PlacedLimit ? $"full ({placed} placed references)" : $"scripted only, except PlacedNpc in full ({placed} placed references)");
                Meta("CachedRecordCount", "0");
                tx.Commit();
            }
            File.Move(tmp, dbPath, overwrite: false); // never over an existing database: other processes may be reading it
        }

        static IEnumerable<string> TypesHolding(string path, ModKey pluginKey, Needs needs)
        {
            var hold = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Records asked for by FormKey only: a plugin can hold a record only from itself or one of its masters, and the
            // load-order index already knows each record's type. Either answers without scanning the file.
            if (needs.EditorIds == null && needs.FormKeys != null)
            {
                string self = pluginKey.FileName.String;
                var masters = LoadOrderIndex.MastersOf(path);
                var relevant = needs.FormKeys.Where(fk => { string p = Sfs.PluginOf(fk); return p.Equals(self, StringComparison.OrdinalIgnoreCase) || masters.Contains(p); }).ToList();
                if (relevant.Count == 0) return hold;
                if (LoadOrderIndex.Sig4sIfFresh(self, relevant) is { } sig4s)
                {
                    foreach (var s4 in sig4s) AddType(hold, s4);
                    return hold;
                }
            }
            var tableM = HeaderScanner.MasterTable(pluginKey, path);
            foreach (var r in RawScan(path))
            {
                bool hit = r.EditorId != null && needs.EditorIds != null && needs.EditorIds.Any(x => x.IsMatch(r.EditorId));
                if (!hit && needs.FormKeys != null) hit = needs.FormKeys.Contains(Sfs.NormalizeFormKey(tableM.GetFormKey(FormID.Factory(r.Raw), reference: false).ToString()));
                if (!hit) continue;
                AddType(hold, r.Sig4);
            }
            return hold;
        }

        static void AddType(HashSet<string> hold, string sig4)
        {
            if (sig4 == "OMOD") { hold.Add("WeaponModification"); hold.Add("ArmorModification"); hold.Add("ObjectModification"); hold.Add("ObjectMod"); }
            else if (sig4 == "GMST") { foreach (var g in Program.AllStoredTypes.Where(t => t.StartsWith("GameSetting"))) hold.Add(g); }
            else hold.Add(Program.TypeNameOf(sig4));
        }

        static void SessionBuild(string target)
        {
            var clock = Stopwatch.StartNew();
            string path = Path.Combine(Program.GetDataFolderPathPublic(), target);
            if (!File.Exists(path)) { Console.Error.WriteLine($"Error: {target} not found in load order or Data folder."); return; }
            var pluginKey = ModKey.FromFileName(Path.GetFileName(path));
            var needs = Forced is { } f0 ? f0.Copy() : (_primary.Count == 0 || _primary.Contains(target) ? Analyze(CurrentSql) : new Needs { Records = true, Forms = false });
            // Which types: named in the SQL, found from the EditorIDs or FormKeys asked for, the records a raw search found, or all
            HashSet<string>? want;
            if (!needs.Records) want = new();
            else if (RefsTarget != null && Forced == null && CurrentSql == null)
            {
                // Which record types mention the target: remembered per copy of the plugin (RefScanCache), so a repeat of
                // Refs doesn't search the file's raw bytes again
                if (RefScanCache.Get(target, RefsTarget) is { Types: { } known })
                    want = known.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                else
                {
                    var mt0 = HeaderScanner.MasterTable(pluginKey, path);
                    var cands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var (hits, _, _, _) = LiveCommand.FindRawFormIdPublic(path, HeaderScanner.RawForms(mt0, pluginKey, path, FormKey.Factory(RefsTarget)));
                        foreach (var h in hits) cands.Add(Sfs.NormalizeFormKey(mt0.GetFormKey(FormID.Factory(h.FormId), reference: false).ToString()));
                    }
                    catch { }
                    want = TypesHolding(path, pluginKey, new Needs { FormKeys = cands }).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    RefScanCache.SetTypes(target, RefsTarget, string.Join(",", want));
                }
            }
            else if (needs.Types != null) want = needs.Types;
            else if (needs.EditorIds != null || needs.FormKeys != null) want = TypesHolding(path, pluginKey, needs).ToHashSet(StringComparer.OrdinalIgnoreCase);
            else want = null;
            bool needForms = needs.Forms;

            // Already there? Most commands stop here, reading only.
            string dbPath = Program.GetCachePath(target);
            var built = ReadBuilt(dbPath);
            if (built != null && Holds(built, want, needForms)) return;
            // One worker writes a database at a time; the others wait, then use what it built.
            using var buildLock = new Service.ServiceDb.BuildLock(dbPath);
            built = ReadBuilt(dbPath);
            if (built != null && Holds(built, want, needForms)) return;
            if (built == null) Service.ServiceDb.NoteIfChanged(target, dbPath);

            Sfs.CloseResolver();
            using var slim = SlimLoader.Open(target, Array.Empty<string>()); // closed when this build ends: no plugin stays locked
            var mod = slim.Mods.Last(m => m.ModKey.FileName.String.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
            string modFileName = mod.ModKey.FileName.String;
            if (built == null) { CreateServiceDb(dbPath, mod, path, modFileName); built = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
            var have = built;
            var missing = want == null ? (have.Contains("*") ? new HashSet<string>() : null) : want.Where(t => !have.Contains("*") && !have.Contains(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool doForms = needForms && !have.Contains("#forms");

            using var c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            c.Open();
            void Sql(string q) { using var x = c.CreateCommand(); x.CommandText = q; x.ExecuteNonQuery(); }
            Sql("PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA busy_timeout = 60000;");
            void MarkBuilt(SqliteTransaction tx, IEnumerable<string> kinds)
            {
                using var x = c.CreateCommand(); x.Transaction = tx;
                x.CommandText = "INSERT OR IGNORE INTO Built VALUES (@k)";
                var k = x.Parameters.Add("@k", SqliteType.Text);
                foreach (var kind in kinds) { k.Value = kind; x.ExecuteNonQuery(); }
            }

            int added = 0;
            if (missing == null || missing.Count > 0)
            {
                var list = new List<IMajorRecordGetter>();
                foreach (var prop in mod.GetType().GetProperties().Where(p => typeof(IGroupGetter).IsAssignableFrom(p.PropertyType)))
                {
                    if (prop.Name is "Cells" or "Worldspaces") continue;
                    if (missing != null)
                    {
                        var arg = prop.PropertyType.GetGenericArguments().FirstOrDefault();
                        if (arg != null && !missing.Any(w => typeof(IWeaponGetter).Assembly.GetType($"Mutagen.Bethesda.Starfield.I{w}Getter", false, true) is Type g && arg.IsAssignableFrom(g))) continue;
                    }
                    IGroupGetter? group;
                    try { group = prop.GetValue(mod) as IGroupGetter; if (group == null) continue; } catch { continue; }
                    foreach (var rec in SafeRecords(group))
                    {
                        if (rec == null) continue;
                        string sig = rec.Registration.Name;
                        if (!Program.StoredType(sig) || (missing != null && !missing.Contains(sig)) || have.Contains(sig) || Program.IsShipPart(rec)) continue;
                        list.Add(rec);
                    }
                }
                if (missing == null || missing.Any(Program.PlacedTypes.Contains))
                {
                    int count = 0;
                    HeaderScanner.Scan(path, (h, _) => { if (h.CellFormId != 0 && h.Signature is not ("NAVM" or "LAND" or "CELL")) count++; return false; });
                    bool full = count <= Program.PlacedLimit;
                    foreach (var type in new[] { typeof(IPlacedObjectGetter), typeof(IPlacedNpcGetter) })
                    {
                        string sigName = type == typeof(IPlacedObjectGetter) ? "PlacedObject" : "PlacedNpc";
                        if ((missing != null && !missing.Contains(sigName)) || have.Contains(sigName)) continue;
                        foreach (var rec in mod.EnumerateMajorRecords(type))
                        {
                            bool scripts = rec is IHaveVirtualMachineAdapterGetter vm && vm.VirtualMachineAdapter is { } v && v.Scripts.Count > 0;
                            if (full || scripts || Program.AlwaysFull(sigName)) list.Add(rec);
                        }
                    }
                }
                var rows = new Row[list.Count];
                var options = new JsonSerializerOptions { WriteIndented = false };
                var linkCache = slim.LinkCache;
                if (list.Count > 0) Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, list.Count, 50), range =>
                {
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        var rec = list[i];
                        string fk = Sfs.NormalizeFormKey(rec.FormKey.ToString());
                        string origin = Sfs.CanonicalPluginName(rec.FormKey.ModKey.FileName.String);
                        var obj = Program.RecordToJson(rec, linkCache, out string name);
                        var refs = new List<(string, string)>();
                        foreach (var kvp in obj)
                        {
                            if (kvp.Key is "Record Header" or "EDID - Editor ID" or "Signature") continue;
                            Program.CollectRefsFromJsonInternal(kvp.Value, kvp.Key, refs);
                        }
                        rows[i] = new Row(fk, rec.Registration.Name, !string.IsNullOrEmpty(rec.EditorID) ? rec.EditorID : fk, Program.StripStringIdTagInternal(name),
                            origin, origin.Equals(modFileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1, obj.ToJsonString(options), refs);
                    }
                });
                using (var tx = c.BeginTransaction())
                {
                    using var ins = c.CreateCommand(); ins.Transaction = tx;
                    ins.CommandText = "INSERT OR IGNORE INTO Records VALUES (@a,@b,@c,@d,@e,@f,@g)";
                    var ps = new[] { "@a", "@b", "@c", "@d", "@e", "@f", "@g" }.Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();
                    using var rf = c.CreateCommand(); rf.Transaction = tx;
                    rf.CommandText = "INSERT INTO Refs VALUES (@a,@b,@c,@d,@e)";
                    var pr = new[] { "@a", "@b", "@c", "@d", "@e" }.Select(n => rf.Parameters.Add(n, SqliteType.Text)).ToArray();
                    foreach (var r in rows)
                    {
                        ps[0].Value = r.Fk; ps[1].Value = r.Sig; ps[2].Value = r.Edid; ps[3].Value = r.Name; ps[4].Value = r.Origin; ps[5].Value = r.Ovr; ps[6].Value = r.Data;
                        if (ins.ExecuteNonQuery() == 0) continue;
                        foreach (var (rp, to) in r.Refs) { pr[0].Value = r.Fk; pr[1].Value = r.Sig; pr[2].Value = r.Edid; pr[3].Value = rp; pr[4].Value = to; rf.ExecuteNonQuery(); }
                    }
                    MarkBuilt(tx, missing == null ? new[] { "*" } : missing);
                    tx.Commit();
                }
                added = rows.Length;
            }

            if (doForms)
            {
                var raw = RawScan(path);
                var masters = HeaderScanner.MasterTable(mod.ModKey, path);
                var subtype = new Dictionary<FormKey, string>();
                foreach (var prop in mod.GetType().GetProperties().Where(p => typeof(IGroupGetter).IsAssignableFrom(p.PropertyType) && p.Name is "ObjectModifications" or "GameSettings"))
                    if (prop.GetValue(mod) is IGroupGetter g)
                        foreach (var key in g.FormKeys) try { subtype[key] = g[key].Registration.Name; } catch { }
                var parents = Program.RawParents(path);
                var counts = new Dictionary<string, (int T, int O)>(StringComparer.OrdinalIgnoreCase);
                int shipParts = raw.Count(r => r.Sig4 == "GBFM" && r.EditorId != null && Regex.IsMatch(r.EditorId, "^SM[A-Z]{1,3}_"));
                int placedTotal = raw.Count(r => r.Placed);
                using var tx = c.BeginTransaction();
                using var ins = c.CreateCommand(); ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO Forms VALUES (@a,@b,@c,@d,@e,@f,@g)";
                var p = new[] { "@a", "@b", "@c", "@d", "@e", "@f", "@g" }.Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();
                var keys = new List<FormKey>(raw.Count);
                foreach (var r in raw)
                {
                    var key = masters.GetFormKey(FormID.Factory(r.Raw), reference: false);
                    keys.Add(key);
                    string fk = Sfs.NormalizeFormKey(key.ToString());
                    string sig = subtype.TryGetValue(key, out var stp) ? stp : Program.TypeNameOf(r.Sig4);
                    bool ovr = !key.ModKey.FileName.String.Equals(modFileName, StringComparison.OrdinalIgnoreCase);
                    counts.TryGetValue(sig, out var ct);
                    counts[sig] = (ct.T + 1, ct.O + (ovr ? 1 : 0));
                    p[0].Value = fk; p[1].Value = sig; p[2].Value = (object?)r.EditorId ?? DBNull.Value; p[3].Value = ovr ? 1 : 0;
                    p[4].Value = r.BaseRaw != 0 ? Sfs.NormalizeFormKey(masters.GetFormKey(FormID.Factory(r.BaseRaw), reference: true).ToString()) : DBNull.Value;
                    p[5].Value = parents.TryGetValue(fk, out var par) ? par : DBNull.Value;
                    p[6].Value = r.Scripts ? 1 : 0;
                    ins.ExecuteNonQuery();
                }
                using var sc = c.CreateCommand(); sc.Transaction = tx;
                sc.CommandText = "INSERT OR REPLACE INTO SignatureCounts VALUES (@a,@b,@c,@d)";
                var q = new[] { "@a", "@b", "@c", "@d" }.Select(n => sc.Parameters.Add(n, SqliteType.Text)).ToArray();
                foreach (var kv in counts)
                {
                    q[0].Value = kv.Key; q[1].Value = kv.Value.T; q[2].Value = kv.Value.O;
                    q[3].Value = kv.Key == "GenericBaseForm" && shipParts > 0 ? (shipParts == kv.Value.T ? 0 : 2) : Program.StoredType(kv.Key) ? 1 : Program.PlacedTypes.Contains(kv.Key) ? (placedTotal <= Program.PlacedLimit || Program.AlwaysFull(kv.Key) ? 1 : 2) : 0;
                    sc.ExecuteNonQuery();
                }
                void Meta(string k, string v) { using var x = c.CreateCommand(); x.Transaction = tx; x.CommandText = "INSERT OR REPLACE INTO Meta VALUES (@k,@v)"; x.Parameters.AddWithValue("@k", k); x.Parameters.AddWithValue("@v", v); x.ExecuteNonQuery(); }
                Meta("RecordCount", raw.Count.ToString());
                Meta("OverrideCount", keys.Count(k => !k.ModKey.FileName.String.Equals(modFileName, StringComparison.OrdinalIgnoreCase)).ToString());
                Meta("MaxLocalFormID", $"0x{keys.Where(k => k.ModKey.FileName.String.Equals(modFileName, StringComparison.OrdinalIgnoreCase)).Select(k => k.ID).DefaultIfEmpty(0u).Max():X6}");
                Meta("PlacedMode", placedTotal <= Program.PlacedLimit ? $"full ({placedTotal} placed references)" : $"scripted only, except PlacedNpc in full ({placedTotal} placed references)");
                MarkBuilt(tx, new[] { "#forms" });
                tx.Commit();
            }
            using (var cnt = c.CreateCommand()) { cnt.CommandText = "INSERT OR REPLACE INTO Meta VALUES ('CachedRecordCount', (SELECT count(*) FROM Records))"; cnt.ExecuteNonQuery(); }
            if (Timing) Console.Error.WriteLine($"[live] service {target}: +{added} records ({(missing == null ? "all types" : string.Join(",", missing))}){(doForms ? " + forms" : "")} in {clock.Elapsed.TotalSeconds:F2} s");
        }

        public static void Build(string target)
        {
            if (SessionServer.Active)
            {
                // A build that fails must fail the command: answering from part of the data would look like a real answer.
                // The worker runs a command once more when it sees this line.
                try { SessionBuild(target); }
                catch (Exception e) { Console.Error.WriteLine($"Error: couldn't read {target} for this command ({e.Message})."); Environment.ExitCode = 1; BuildFailed = true; }
                return;
            }
            var clock = Stopwatch.StartNew();
            var total = Stopwatch.StartNew();
            var needs = Forced is { } f0 ? f0.Copy() : (_primary.Count == 0 || _primary.Contains(target) ? Analyze(CurrentSql) : new Needs { Records = true, Forms = false });
            if (Timing) Console.Error.WriteLine($"[live] {target}: {needs}");
            string path = Path.Combine(Program.GetDataFolderPathPublic(), target);
            if (!File.Exists(path)) { Console.Error.WriteLine($"Error: {target} not found in load order or Data folder."); return; }
            var pluginKey = ModKey.FromFileName(Path.GetFileName(path));


            Directory.CreateDirectory(Sfs.DbFolder);
            Sfs.CloseResolver();
            string dbPath = Program.GetCachePath(target);
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" }) if (File.Exists(f)) File.Delete(f);
            using var c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            c.Open();
            void Sql(string q) { using var x = c.CreateCommand(); x.CommandText = q; x.ExecuteNonQuery(); }
            Sql("PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA temp_store = MEMORY;");
            Sql(@"CREATE TABLE Records (FormKey TEXT PRIMARY KEY, Signature TEXT, EditorID TEXT, Name TEXT, Origin TEXT, IsOverride INTEGER, Data TEXT);
                  CREATE TABLE Masters (Idx INTEGER, MasterName TEXT PRIMARY KEY);
                  CREATE TABLE Meta (Key TEXT PRIMARY KEY, Value TEXT);
                  CREATE TABLE SignatureCounts (Signature TEXT PRIMARY KEY, TotalCount INTEGER, OverrideCount INTEGER, Cached INTEGER);
                  CREATE TABLE Refs (FromFormKey TEXT, FromSignature TEXT, FromEditorID TEXT, Path TEXT, ToFormKey TEXT);
                  CREATE TABLE Forms (FormKey TEXT, Signature TEXT, EditorID TEXT, IsOverride INTEGER, Base TEXT, Parent TEXT, HasScripts INTEGER);");

            if (RefsTarget != null && Forced == null && CurrentSql == null)
            {
                var targetKey = FormKey.Factory(RefsTarget);
                var mt0 = HeaderScanner.MasterTable(pluginKey, path);
                var cands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var (hits, _, _, _) = LiveCommand.FindRawFormIdPublic(path, HeaderScanner.RawForms(mt0, pluginKey, path, targetKey));
                    foreach (var h in hits) cands.Add(Sfs.NormalizeFormKey(mt0.GetFormKey(FormID.Factory(h.FormId), reference: false).ToString()));
                }
                catch (Exception) { }
                needs = new Needs { Records = true, Forms = true, FormKeys = cands, FormsFilter = new Filter { FormKeys = cands, Either = true } };
                if (cands.Count == 0) needs.Types = new();
                T($"raw search: {cands.Count} candidate records", clock);
            }

            // A lookup by EditorID or FormKey only needs the types that hold it.
            if (needs.Records && needs.Types == null && (needs.EditorIds != null || needs.FormKeys != null) && !(needs.FormKeys?.Count == 0 && needs.EditorIds == null))
            {
                var hold = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var tableM = HeaderScanner.MasterTable(pluginKey, path);
                foreach (var r in RawScan(path))
                {
                    bool hit = r.EditorId != null && needs.EditorIds != null && needs.EditorIds.Any(x => x.IsMatch(r.EditorId));
                    if (!hit && needs.FormKeys != null) hit = needs.FormKeys.Contains(Sfs.NormalizeFormKey(tableM.GetFormKey(FormID.Factory(r.Raw), reference: false).ToString()));
                    if (!hit) continue;
                    if (r.Sig4 == "OMOD") { hold.Add("WeaponModification"); hold.Add("ArmorModification"); hold.Add("ObjectModification"); hold.Add("ObjectMod"); }
                    else if (r.Sig4 == "GMST") { foreach (var g in Program.AllStoredTypes.Where(t => t.StartsWith("GameSetting"))) hold.Add(g); }
                    else hold.Add(Program.TypeNameOf(r.Sig4));
                }
                needs.Types = hold;
                T($"raw pass found the types: {string.Join(",", hold)}", clock);
            }

            var needsLinks = (needs.Types == null && needs.Records) || (needs.Types?.Contains("Weapon") ?? false);
            using var slim = needsLinks ? SlimLoader.Open(target, Array.Empty<string>()) : null;
            using var alone = needsLinks ? null : StarfieldMod.CreateFromBinaryOverlay(path, StarfieldRelease.Starfield, new BinaryReadParameters { MasterFlagsLookup = new SimpleMasterFlagCache(), ThrowOnUnknownSubrecord = false, StringsParam = new Mutagen.Bethesda.Strings.StringsReadParameters { BsaFolderOverride = Program.GetDataFolderPathPublic() } });
            IStarfieldModGetter mod = slim != null ? slim.Mods.Last(m => m.ModKey.FileName.String.Equals(target, StringComparison.OrdinalIgnoreCase)) : alone!;
            string modFileName = mod.ModKey.FileName.String;
            T("open", clock);

            // Records, in parallel
            var wanted = needs.Types;
            var list = new List<IMajorRecordGetter>();
            int placedCount = 0;
            bool anyPlaced = wanted == null || wanted.Any(Program.PlacedTypes.Contains);
            if (needs.Records)
            {
                foreach (var prop in mod.GetType().GetProperties().Where(p => typeof(IGroupGetter).IsAssignableFrom(p.PropertyType)))
                {
                    if (prop.Name is "Cells" or "Worldspaces") continue;
                    if (wanted != null)
                    {
                        var arg = prop.PropertyType.GetGenericArguments().FirstOrDefault();
                        if (arg != null && !wanted.Any(w => typeof(IWeaponGetter).Assembly.GetType($"Mutagen.Bethesda.Starfield.I{w}Getter", false, true) is Type g && arg.IsAssignableFrom(g))) continue;
                    }
                    IGroupGetter? group;
                    try { group = prop.GetValue(mod) as IGroupGetter; if (group == null) continue; } catch { continue; }
                    foreach (var rec in SafeRecords(group))
                    {
                        if (rec == null) continue;
                        string sig = rec.Registration.Name;
                        if (!Program.StoredType(sig)) continue;
                        if (wanted != null && !wanted.Contains(sig)) continue;
                        if (Program.IsShipPart(rec)) continue;
                        if ((Forced != null && (needs.FormKeys != null || needs.EditorIds != null)) || RefsTarget != null) // named records only; a whole type when only the type was asked for
                        {
                            string fkx = Sfs.NormalizeFormKey(rec.FormKey.ToString());
                            if (!((needs.FormKeys?.Contains(fkx) ?? false) || (needs.EditorIds?.Any(x => rec.EditorID != null && x.IsMatch(rec.EditorID)) ?? false))) continue;
                        }
                        list.Add(rec);
                    }
                }
                T($"collect {list.Count} records", clock);
                if (anyPlaced)
                {
                    int count = 0;
                    HeaderScanner.Scan(path, (h, _) => { if (h.CellFormId != 0 && h.Signature is not ("NAVM" or "LAND" or "CELL")) count++; return false; });
                    placedCount = count;
                    bool full = placedCount <= Program.PlacedLimit;
                    foreach (var type in new[] { typeof(IPlacedObjectGetter), typeof(IPlacedNpcGetter) })
                    {
                        string sigName = type == typeof(IPlacedObjectGetter) ? "PlacedObject" : "PlacedNpc";
                        if (wanted != null && !wanted.Contains(sigName)) continue;
                        foreach (var rec in mod.EnumerateMajorRecords(type))
                        {
                            bool scripts = rec is IHaveVirtualMachineAdapterGetter vm && vm.VirtualMachineAdapter is { } v && v.Scripts.Count > 0;
                            if (full || scripts || Program.AlwaysFull(sigName)) list.Add(rec);
                        }
                    }
                    T($"collect placed, now {list.Count} records", clock);
                }
                if (list.Count == 0) T("nothing to read", clock);
                var rows = new Row[list.Count];
                var options = new JsonSerializerOptions { WriteIndented = false };
                var linkCache = slim?.LinkCache ?? mod.ToUntypedImmutableLinkCache();
                if (list.Count > 0) Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, list.Count, 50), range =>
                {
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        var rec = list[i];
                        string fk = Sfs.NormalizeFormKey(rec.FormKey.ToString());
                        string origin = Sfs.CanonicalPluginName(rec.FormKey.ModKey.FileName.String);
                        var obj = Program.RecordToJson(rec, linkCache, out string name);
                        var refs = new List<(string, string)>();
                        foreach (var kvp in obj)
                        {
                            if (kvp.Key is "Record Header" or "EDID - Editor ID" or "Signature") continue;
                            Program.CollectRefsFromJsonInternal(kvp.Value, kvp.Key, refs);
                        }
                        rows[i] = new Row(fk, rec.Registration.Name, !string.IsNullOrEmpty(rec.EditorID) ? rec.EditorID : fk, Program.StripStringIdTagInternal(name),
                            origin, origin.Equals(modFileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1, obj.ToJsonString(options), refs);
                    }
                });
                T($"flatten {rows.Length}", clock);
                using (var tx = c.BeginTransaction())
                {
                    using var ins = c.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT OR IGNORE INTO Records VALUES (@a,@b,@c,@d,@e,@f,@g)";
                    var ps = new[] { "@a", "@b", "@c", "@d", "@e", "@f", "@g" }.Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();
                    using var rf = c.CreateCommand();
                    rf.Transaction = tx;
                    rf.CommandText = "INSERT INTO Refs VALUES (@a,@b,@c,@d,@e)";
                    var pr = new[] { "@a", "@b", "@c", "@d", "@e" }.Select(n => rf.Parameters.Add(n, SqliteType.Text)).ToArray();
                    foreach (var r in rows)
                    {
                        ps[0].Value = r.Fk; ps[1].Value = r.Sig; ps[2].Value = r.Edid; ps[3].Value = r.Name; ps[4].Value = r.Origin; ps[5].Value = r.Ovr; ps[6].Value = r.Data;
                        ins.ExecuteNonQuery();
                        foreach (var (rp, to) in r.Refs)
                        {
                            pr[0].Value = r.Fk; pr[1].Value = r.Sig; pr[2].Value = r.Edid; pr[3].Value = rp; pr[4].Value = to;
                            rf.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
                T("insert records", clock);
            }

            // Forms, from one pass over the raw file
            if (needs.Forms)
            {
                var raw = RawScan(path);
                T($"raw pass {raw.Count}", clock);
                var masters = HeaderScanner.MasterTable(mod.ModKey, path);
                // OMODs and game settings have subtypes that only Mutagen can name.
                var subtype = new Dictionary<FormKey, string>();
                foreach (var prop in mod.GetType().GetProperties().Where(p => typeof(IGroupGetter).IsAssignableFrom(p.PropertyType) && p.Name is "ObjectModifications" or "GameSettings"))
                    if (prop.GetValue(mod) is IGroupGetter g)
                        foreach (var key in g.FormKeys) try { subtype[key] = g[key].Registration.Name; } catch { }
                var parents = Program.RawParents(path);
                T("subtypes and parents", clock);
                var counts = new Dictionary<string, (int T, int O)>(StringComparer.OrdinalIgnoreCase);
                int shipParts = raw.Count(r => r.Sig4 == "GBFM" && r.EditorId != null && Regex.IsMatch(r.EditorId, "^SM[A-Z]{1,3}_"));
                int placedTotal = raw.Count(r => r.Placed);
                using (var tx = c.BeginTransaction())
                {
                    using var ins = c.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO Forms VALUES (@a,@b,@c,@d,@e,@f,@g)";
                    var seenForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var p = new[] { "@a", "@b", "@c", "@d", "@e", "@f", "@g" }.Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();
                    foreach (var r in raw)
                    {
                        var key = masters.GetFormKey(FormID.Factory(r.Raw), reference: false);
                        string fk = Sfs.NormalizeFormKey(key.ToString());
                        string sig = subtype.TryGetValue(key, out var st) ? st : Program.TypeNameOf(r.Sig4);
                        bool ovr = !key.ModKey.FileName.String.Equals(modFileName, StringComparison.OrdinalIgnoreCase);
                        counts.TryGetValue(sig, out var ct0);
                        counts[sig] = (ct0.T + 1, ct0.O + (ovr ? 1 : 0));
                        if (needs.FormsFilter != null && !needs.FormsFilter.Allows(sig, r.EditorId, fk)) continue;
                        if (!seenForms.Add(fk)) continue;
                        p[0].Value = fk; p[1].Value = sig; p[2].Value = (object?)r.EditorId ?? DBNull.Value; p[3].Value = ovr ? 1 : 0;
                        p[4].Value = r.BaseRaw != 0 ? Sfs.NormalizeFormKey(masters.GetFormKey(FormID.Factory(r.BaseRaw), reference: true).ToString()) : DBNull.Value;
                        p[5].Value = parents.TryGetValue(fk, out var par) ? par : DBNull.Value;
                        p[6].Value = r.Scripts ? 1 : 0;
                        ins.ExecuteNonQuery();
                    }
                    using var sc = c.CreateCommand();
                    sc.Transaction = tx;
                    sc.CommandText = "INSERT INTO SignatureCounts VALUES (@a,@b,@c,@d)";
                    var q = new[] { "@a", "@b", "@c", "@d" }.Select(n => sc.Parameters.Add(n, SqliteType.Text)).ToArray();
                    foreach (var kv in counts)
                    {
                        q[0].Value = kv.Key; q[1].Value = kv.Value.T; q[2].Value = kv.Value.O;
                        q[3].Value = kv.Key == "GenericBaseForm" && shipParts > 0 ? (shipParts == kv.Value.T ? 0 : 2) : Program.StoredType(kv.Key) ? 1 : Program.PlacedTypes.Contains(kv.Key) ? (placedTotal <= Program.PlacedLimit || Program.AlwaysFull(kv.Key) ? 1 : 2) : 0;
                        sc.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                T("insert forms", clock);
            }

            // Header facts
            using (var tx = c.BeginTransaction())
            {
                void Meta(string k, string v) { using var x = c.CreateCommand(); x.Transaction = tx; x.CommandText = "INSERT OR REPLACE INTO Meta VALUES (@k,@v)"; x.Parameters.AddWithValue("@k", k); x.Parameters.AddWithValue("@v", v); x.ExecuteNonQuery(); }
                int idx = 0;
                foreach (var m in mod.MasterReferences)
                {
                    using var x = c.CreateCommand(); x.Transaction = tx;
                    x.CommandText = "INSERT OR IGNORE INTO Masters VALUES (@i,@n)"; x.Parameters.AddWithValue("@i", idx++); x.Parameters.AddWithValue("@n", m.Master.FileName.String); x.ExecuteNonQuery();
                }
                var flags = mod.ModHeader.Flags;
                Meta("SchemaVersion", Program.SchemaVersionPublic.ToString());
                Meta("CachedTypes", Program.FingerprintPublic);
                Meta("SourceStamp", Program.SourceStampPublic(path));
                Meta("FileName", modFileName);
                Meta("MasterStyle", flags.HasFlag(StarfieldModHeader.HeaderFlag.Light) ? "Light (Small)" : flags.HasFlag(StarfieldModHeader.HeaderFlag.Medium) ? "Medium" : "Full");
                Meta("HeaderFlags", flags.ToString());
                Meta("Author", mod.ModHeader.Author ?? "");
                Meta("Description", mod.ModHeader.Description ?? "");
                Meta("CachedRecordCount", list.Count.ToString());
                // The counts the database keeps, from the record headers only
                var mt = HeaderScanner.MasterTable(mod.ModKey, path);
                int totalRecs = 0, overrideRecs = 0, placedRecs = 0; uint maxLocalId = 0;
                HeaderScanner.Scan(path, (h, _) =>
                {
                    totalRecs++;
                    if (h.CellFormId != 0 && h.Signature is not ("NAVM" or "LAND" or "CELL")) placedRecs++;
                    var k = mt.GetFormKey(FormID.Factory(h.FormId), reference: false);
                    if (!k.ModKey.FileName.String.Equals(modFileName, StringComparison.OrdinalIgnoreCase)) overrideRecs++;
                    else if (k.ID > maxLocalId) maxLocalId = k.ID;
                    return false;
                });
                Meta("RecordCount", totalRecs.ToString());
                Meta("OverrideCount", overrideRecs.ToString());
                Meta("MaxLocalFormID", $"0x{maxLocalId:X6}");
                Meta("PlacedMode", placedRecs <= Program.PlacedLimit ? $"full ({placedRecs} placed references)" : $"scripted only, except PlacedNpc in full ({placedRecs} placed references)");
                tx.Commit();
            }
            Sql("CREATE INDEX idx_signature ON Records(Signature); CREATE INDEX idx_editorid ON Records(EditorID); CREATE INDEX idx_isoverride ON Records(IsOverride);");
            Sql("CREATE INDEX idx_refs_to ON Refs(ToFormKey); CREATE INDEX idx_refs_from ON Refs(FromFormKey);");
            if (needs.Forms) Sql("CREATE INDEX idx_forms_fk ON Forms(FormKey); CREATE INDEX idx_forms_sig ON Forms(Signature); CREATE INDEX idx_forms_base ON Forms(Base); CREATE INDEX idx_forms_parent ON Forms(Parent);");
            T("meta and indexes", clock);
            if (Timing) Console.Error.WriteLine($"[live] {target} built in {total.Elapsed.TotalSeconds:F2} s");
        }
    }
}
