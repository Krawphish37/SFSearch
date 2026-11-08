using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Masters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Noggog.StructuredStrings;

namespace SFSearch
{
    /// <summary>
    /// Checks a plugin's database against the plugin file: presence, record headers, every form link, parents,
    /// read-back against a fresh read, and every field against Mutagen's own printout. Only checks rows that are stored,
    /// so it works on a normal database and on one with types added. Writes nothing.
    /// </summary>
    public static class VerifyDbCommand
    {
        const string Usage = @"Usage: SFSearch VerifyDb <plugin|path> [--report <file>] [--samples N]
  Checks every stored row against the plugin: present, EditorID, record header, form links, parents.
  Up to N records of each type (default 200) are also read fresh, and compared field by field with Mutagen's own printout.
  A field counts as found if its value is anywhere in that record's stored text, so this catches missing data better than misplaced data.";

        public static void Run(List<string> args)
        {
            string? reportPath = Sfs.TakeOption(args, "--report");
            int samplesPerType = int.TryParse(Sfs.TakeOption(args, "--samples"), out var n) ? n : 200;
            if (args.Count == 0)
            {
                Console.WriteLine(Usage);
                return;
            }
            string cacheKey = Program.EnsureCacheForFile(args[0]);
            string db = Program.GetCachePath(cacheKey);
            string? path = Program.CacheSourcePath(cacheKey, db);
            if (path == null)
            {
                Console.WriteLine($"Can't find the plugin behind {cacheKey}.");
                return;
            }
            string plugin = Path.GetFileName(path);

            var report = new StringBuilder();
            void Out(string s)
            {
                Console.WriteLine(s);
                if (reportPath == null) return;
                report.AppendLine(s);
                File.WriteAllText(reportPath, report.ToString());
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long failures = 0;

            var flags = new LossCheckCommand.HeaderFlagCache(Program.GetDataFolderPathPublic());
            var key = ModKey.FromFileName(plugin);
            var package = SeparatedMasterPackage.Factory(GameRelease.Starfield, new ModPath(key, path), flags, new System.IO.Abstractions.FileSystem());
            var added = new HashSet<string>(AddCommand.ReadAddedTypes(db), StringComparer.OrdinalIgnoreCase);

            // ---- The plugin file itself
            var raw = new Dictionary<string, RawRec>(StringComparer.OrdinalIgnoreCase);
            int repeated = 0;
            var rawBytes = File.ReadAllBytes(path);
            void Walk(int pos, int end)
            {
                while (pos + 24 <= end)
                {
                    bool group = rawBytes[pos] == (byte)'G' && rawBytes[pos + 1] == (byte)'R' && rawBytes[pos + 2] == (byte)'U' && rawBytes[pos + 3] == (byte)'P';
                    int size = BitConverter.ToInt32(rawBytes, pos + 4);
                    if (group) { Walk(pos + 24, pos + size); pos += size; continue; }
                    uint id = BitConverter.ToUInt32(rawBytes, pos + 12);
                    var fk = Sfs.NormalizeFormKey(package.GetFormKey(FormID.Factory(id), reference: false).ToString());
                    if (!raw.TryAdd(fk, new RawRec { Sig = Encoding.ASCII.GetString(rawBytes, pos, 4), Offset = pos, Size = size })) repeated++;
                    pos += 24 + size;
                }
            }
            Walk(24 + BitConverter.ToInt32(rawBytes, 4), rawBytes.Length);
            Out($"# Database check: {plugin}");
            Out($"Plugin: {raw.Count} records, {repeated} repeated FormIDs. Types added in full: {(added.Count == 0 ? "none" : added.Count.ToString())}.");

            using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
            conn.Open();

            // ---- Presence and parents
            var formParent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT FormKey, Parent FROM Forms";
                cmd.CommandTimeout = 0;
                using var r = cmd.ExecuteReader();
                while (r.Read()) formParent[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
            }
            int formsMissing = raw.Keys.Count(k => !formParent.ContainsKey(k));
            int formsExtra = formParent.Keys.Count(k => !raw.ContainsKey(k));
            failures += formsMissing + formsExtra;
            Out($"Forms: {formParent.Count} rows; plugin records with no row {formsMissing}; rows with no plugin record {formsExtra}.");

            var stored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long edidBad = 0, flagBad = 0, versionBad = 0, formVersionBad = 0, headerMissing = 0, junkError = 0, junkType = 0, charsTotal = 0;
            var headerSamples = new List<string>();
            var junkKinds = new Dictionary<string, long>();
            var junkSamples = new Dictionary<string, string>();
            var rand = new Random(12345);
            var sampleSeen = new Dictionary<string, int>();
            var samples = new Dictionary<string, List<(string Fk, string Data)>>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT FormKey, Signature, EditorID, Data FROM Records ORDER BY FormKey";
                cmd.CommandTimeout = 0;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string fk = r.GetString(0);
                    string sig = r.GetString(1);
                    string edid = r.IsDBNull(2) ? "" : r.GetString(2);
                    string data = r.GetString(3);
                    stored.Add(fk);
                    charsTotal += data.Length;

                    if (raw.TryGetValue(fk, out var rr))
                    {
                        string rawEdid = RawEdid(rawBytes, rr);
                        if (rawEdid.Length > 0 && rawEdid != edid) edidBad++;
                        uint rawFlags = BitConverter.ToUInt32(rawBytes, rr.Offset + 8);
                        uint rawVc = BitConverter.ToUInt32(rawBytes, rr.Offset + 16);
                        ushort rawFv = BitConverter.ToUInt16(rawBytes, rr.Offset + 20);
                        string? f = FieldValue(data, "MajorRecordFlagsRaw"), v = FieldValue(data, "VersionControl"), fv = FieldValue(data, "FormVersion");
                        // A quest's row holds its nested topics and scenes, so the first match can be a child's. Read the record's own.
                        if ((f != null && (uint)long.Parse(f) != rawFlags) || (v != null && uint.Parse(v) != rawVc) || (fv != null && ushort.Parse(fv) != rawFv))
                        {
                            var root = System.Text.Json.Nodes.JsonNode.Parse(data);
                            f = root?["MajorRecordFlagsRaw"]?.ToString();
                            v = root?["VersionControl"]?.ToString();
                            fv = root?["FormVersion"]?.ToString();
                        }
                        if (f == null || v == null || fv == null)
                        {
                            headerMissing++;
                            if (headerSamples.Count < 5) headerSamples.Add($"{sig} {fk}");
                        }
                        if (f != null && (uint)long.Parse(f) != rawFlags) flagBad++;
                        if (v != null && uint.Parse(v) != rawVc) versionBad++;
                        if (fv != null && ushort.Parse(fv) != rawFv) formVersionBad++;
                    }

                    // The JSON writer stores < as \u003C
                    int e = data.IndexOf("<error:", StringComparison.Ordinal);
                    if (e < 0) e = data.IndexOf("\\u003Cerror:", StringComparison.Ordinal);
                    if (e >= 0)
                    {
                        junkError++;
                        Note(junkKinds, junkSamples, $"{sig}: {FieldBefore(data, e)} = {Snip(data, e, 90)}", fk);
                    }
                    foreach (var marker in new[] { "Mutagen.Bethesda.", "System.Collections", "Noggog." })
                    {
                        int t = data.IndexOf(marker, StringComparison.Ordinal);
                        if (t < 0) continue;
                        junkType++;
                        Note(junkKinds, junkSamples, $"{sig}: {FieldBefore(data, t)} = {Snip(data, t, 70)}", fk);
                        break;
                    }

                    int seen = sampleSeen.GetValueOrDefault(sig) + 1;
                    sampleSeen[sig] = seen;
                    if (!samples.TryGetValue(sig, out var list)) samples[sig] = list = new();
                    if (list.Count < samplesPerType) list.Add((fk, data));
                    else
                    {
                        int slot = rand.Next(seen);
                        if (slot < samplesPerType) list[slot] = (fk, data);
                    }
                }
            }
            int rowsExtra = stored.Count(k => !raw.ContainsKey(k));
            failures += rowsExtra + edidBad + flagBad + versionBad + formVersionBad + headerMissing + junkError + junkType;
            Out($"Records: {stored.Count} rows stored in full of {raw.Count}, {charsTotal / (1024 * 1024)} MB of text; rows with no plugin record {rowsExtra}; EditorIDs that differ {edidBad}.");
            Out($"Record headers: flags that differ {flagBad}; version control {versionBad}; form version {formVersionBad}; rows missing one {headerMissing}. {string.Join(", ", headerSamples)}");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM Records WHERE NOT json_valid(Data)";
                cmd.CommandTimeout = 0;
                long invalid = Convert.ToInt64(cmd.ExecuteScalar());
                failures += invalid;
                Out($"Rows that aren't valid JSON: {invalid}.");
            }
            Out($"Stored read errors: {junkError}. Stored .NET type names: {junkType}.");
            foreach (var kv in junkKinds.OrderByDescending(k => k.Value).Take(40)) Out($"  {kv.Value} x {kv.Key} (e.g. {junkSamples[kv.Key]})");

            // ---- Links, parents, read-back and fields: Mutagen reading the plugin separately
            long leftOutLinks = 0, linkTotal = 0, linkMissing = 0, viaChildren = 0, parentChecked = 0, parentBad = 0, backSame = 0, backDiff = 0;
            var missKinds = new Dictionary<string, long>();
            var missSamples = new Dictionary<string, string>();
            var parentSamples = new List<string>();
            var backSamples = new List<string>();
            void CheckParent(FormKey child, FormKey parent)
            {
                parentChecked++;
                string c = Sfs.NormalizeFormKey(child.ToString()), p = Sfs.NormalizeFormKey(parent.ToString());
                if (formParent.TryGetValue(c, out var value) && string.Equals(value, p, StringComparison.OrdinalIgnoreCase)) return;
                parentBad++;
                if (parentSamples.Count < 8) parentSamples.Add($"{c}: stored {value}, should be {p}");
            }

            var wanted = samples.Values.SelectMany(x => x).ToDictionary(x => x.Fk, x => x.Data, StringComparer.OrdinalIgnoreCase);
            var fieldStats = new Dictionary<string, (long Found, long All, string? Eg)>();
            long leaves = 0, leavesFound = 0, notComparable = 0, printSkipped = 0, printed = 0, notChecked = 0;
            var readProblems = new List<string>();
            var notCheckedSamples = new List<string>();

            using (var mod = StarfieldMod.CreateFromBinaryOverlay(new ModPath(key, path), StarfieldRelease.Starfield, new BinaryReadParameters { MasterFlagsLookup = flags }))
            using (var cmd = conn.CreateCommand())
            {
                var linkCache = mod.ToUntypedImmutableLinkCache();
                cmd.CommandText = "SELECT Data FROM Records WHERE FormKey = @k";
                var p = cmd.Parameters.Add("@k", SqliteType.Text);
                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
                // The safe walk: one unreadable record doesn't end the check
                foreach (var rec in Program.WalkRecordsInternal(mod, readProblems))
                {
                    string fk = Sfs.NormalizeFormKey(rec.FormKey.ToString());
                    try
                    {
                    if (rec is ICellGetter cell)
                    {
                        foreach (var x in cell.Persistent) CheckParent(x.FormKey, cell.FormKey);
                        foreach (var x in cell.Temporary) CheckParent(x.FormKey, cell.FormKey);
                    }
                    if (rec is IDialogTopicGetter topic) foreach (var x in topic.Responses) CheckParent(x.FormKey, topic.FormKey);
                    if (!stored.Contains(fk)) continue;
                    string sig = rec.Registration.Name;
                    bool full = added.Contains(sig);

                    var links = rec.EnumerateFormLinks().Where(l => !l.FormKey.IsNull).Select(l => l.FormKey).Distinct().ToList();
                    if (links.Count > 0)
                    {
                        p.Value = fk;
                        string data = cmd.ExecuteScalar() as string ?? "";
                        HashSet<FormKey>? children = null;
                        foreach (var link in links)
                        {
                            linkTotal++;
                            string want = Sfs.NormalizeFormKey(link.ToString());
                            if (data.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            // A cell's, worldspace's, topic's or quest's link list includes its children's, which can have their own rows.
                            if (children == null)
                            {
                                children = new HashSet<FormKey>();
                                if (rec is IMajorRecordGetterEnumerable en)
                                {
                                    foreach (var child in en.EnumerateMajorRecords())
                                    {
                                        if (child.FormKey == rec.FormKey) continue;
                                        children.Add(child.FormKey);
                                        foreach (var cl in child.EnumerateFormLinks()) children.Add(cl.FormKey);
                                    }
                                }
                            }
                            if (children.Contains(link)) { viaChildren++; continue; }
                            // A field this build always leaves out, because Mutagen misreads it
                            if (InAlwaysLeftOutField(rec, link)) { leftOutLinks++; continue; }
                            // A normal row leaves out face data and unknown weapon blocks on purpose
                            if (!full && Program.RecordToJson(rec, linkCache, out _, full: true).ToJsonString(options).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) { leftOutLinks++; continue; }
                            linkMissing++;
                            Note(missKinds, missSamples, sig, $"{fk} -> {want}");
                        }
                    }

                    if (!wanted.TryGetValue(fk, out var sampleData)) continue;
                    var fresh = Program.RecordToJson(rec, linkCache, out _, full: full, skipChildren: full).ToJsonString(options);
                    if (fresh == sampleData) backSame++;
                    else
                    {
                        backDiff++;
                        if (backSamples.Count < 8) backSamples.Add($"{sig} {fk}: stored {sampleData.Length} chars, fresh {fresh.Length}");
                    }

                    // A worldspace or big cell prints everything under it
                    if (rec is IWorldspaceGetter || (rec is ICellGetter bigCell && bigCell.Persistent.Count + bigCell.Temporary.Count > 500))
                    {
                        printSkipped++;
                        continue;
                    }
                    var skip = (full
                        ? rec.GetType().GetProperties().Where(Program.IsChildRecords)
                        : rec.GetType().GetProperties().Where(x => Program.IsCacheSkippedField(x.Name))).Select(x => x.Name).ToHashSet();
                    foreach (var x in rec.GetType().GetProperties()) if (Program.IsAlwaysLeftOut(x.Name)) skip.Add(x.Name);
                    var sb = new StructuredStringBuilder();
                    ((IPrintable)rec).Print(sb, null);
                    printed++;
                    var tokens = new Tokens(sampleData);
                    string jsonLower = sampleData.ToLowerInvariant();
                    foreach (var (field, value) in PrintLeaves(sb.ToString(), skip))
                    {
                        if (field == "FormKey" || field == "EditorID") continue;
                        // Print shows only a .NET type name for these, or None for an empty link. Their links are checked above.
                        if (value.Contains("FormLinkOrIndex`1[", StringComparison.Ordinal) || value.Contains("BinaryOverlayList", StringComparison.Ordinal) || value == "None")
                        {
                            notComparable++;
                            continue;
                        }
                        // Left out of a normal row on purpose: face data and unknown weapon blocks
                        if (!full && Program.IsCacheSkippedField(field))
                        {
                            notComparable++;
                            continue;
                        }
                        leaves++;
                        string kind = $"{sig}.{field}";
                        var (found, all, eg) = fieldStats.GetValueOrDefault(kind);
                        all++;
                        if (LeafFound(value, jsonLower, tokens)) { found++; leavesFound++; }
                        else eg ??= $"{fk}: {value[..Math.Min(value.Length, 70)]}";
                        fieldStats[kind] = (found, all, eg);
                    }
                    }
                    catch (Exception ex)
                    {
                        notChecked++;
                        if (notCheckedSamples.Count < 8) notCheckedSamples.Add($"{fk}: {(ex.InnerException ?? ex).Message}");
                    }
                }
            }
            failures += linkMissing + parentBad + backDiff + (leaves - leavesFound) + notChecked + readProblems.Count;
            if (Program.ReadCacheMetaInternal(db, "MissingMasters") is string missingMasters)
            {
                failures += missingMasters.Split(',').Length;
                Out($"Masters not in Data: {missingMasters}. This check reads the plugin the same way the database did, so it can't see FormIDs put on the wrong plugin.");
            }
            if (readProblems.Count > 0 || notChecked > 0)
            {
                Out($"Mutagen couldn't read part of this plugin: {readProblems.Count} read problem(s), {notChecked} record(s) not checked.");
                foreach (var x in readProblems.Take(8)) Out($"  {x}");
                foreach (var x in notCheckedSamples) Out($"  {x}");
            }
            Out($"Form links: {linkTotal} checked; not stored {linkMissing}; held by a child row {viaChildren}; in a field a normal row leaves out on purpose {leftOutLinks}.");
            foreach (var kv in missKinds.OrderByDescending(k => k.Value).Take(30)) Out($"  {kv.Value} x {kv.Key}, e.g. {missSamples[kv.Key]}");
            Out($"Parents: {parentChecked} checked against the cell and topic lists; wrong {parentBad}.");
            foreach (var x in parentSamples) Out($"  {x}");
            Out($"Read back: {backSame + backDiff} sampled records; equal to a fresh read {backSame}; different {backDiff}.");
            foreach (var x in backSamples) Out($"  {x}");
            Out($"Fields: {printed} records printed, {printSkipped} too big to print; {leaves} values; not in the stored text {leaves - leavesFound}; not comparable {notComparable}.");
            foreach (var kv in fieldStats.Where(c => c.Value.Found < c.Value.All).OrderByDescending(c => c.Value.All - c.Value.Found).Take(60))
                Out($"  {kv.Key}: {kv.Value.All - kv.Value.Found} of {kv.Value.All}, e.g. {kv.Value.Eg}");
            Out("");
            Out($"{(failures == 0 ? "PASS" : $"{failures} PROBLEM(S)")} in {clock.Elapsed.TotalMinutes:F1} min.");
        }

        static bool InAlwaysLeftOutField(IMajorRecordGetter rec, FormKey link)
        {
            foreach (var prop in rec.GetType().GetProperties())
            {
                if (!Program.IsAlwaysLeftOut(prop.Name) || prop.GetValue(rec) is not System.Collections.IEnumerable items) continue;
                foreach (var item in items)
                    if (item is IFormLinkContainerGetter c && c.EnumerateFormLinks().Any(l => l.FormKey == link)) return true;
            }
            return false;
        }

        static void Note(Dictionary<string, long> kinds, Dictionary<string, string> samples, string kind, string sample)
        {
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            samples.TryAdd(kind, sample);
        }

        // Leaf values of Mutagen's Print: "Name => value" and bare list items, under the nearest field name.
        static IEnumerable<(string Field, string Value)> PrintLeaves(string printedText, HashSet<string> skip)
        {
            int skipIndent = -1;
            var names = new List<(int Indent, string Name)>();
            foreach (var line in printedText.Split('\n'))
            {
                string t = line.TrimEnd('\r');
                int indent = t.Length - t.TrimStart().Length;
                string body = t.Trim();
                if (body.Length == 0) continue;
                if (skipIndent >= 0)
                {
                    if (indent > skipIndent) continue;
                    if (indent == skipIndent && (body == "[" || body == "]")) continue;
                    if (indent < skipIndent && body == "]") { skipIndent = -1; continue; }
                    // Text with line breaks prints its later lines at the left edge: still inside the skipped block
                    if (indent != skipIndent || !Regex.IsMatch(body, @"^\w+( \(\w+\))? =>")) continue;
                    skipIndent = -1;
                }
                if (body == "[" || body == "]") continue;
                while (names.Count > 0 && names[^1].Indent >= indent) names.RemoveAt(names.Count - 1);
                int arrow = body.IndexOf(" =>", StringComparison.Ordinal);
                if (arrow > 0 && (!body.Contains(':', StringComparison.Ordinal) || arrow < body.IndexOf(':')))
                {
                    string name = body[..arrow];
                    int paren = name.IndexOf(" (", StringComparison.Ordinal);
                    if (paren > 0) name = name[..paren];
                    string value = body[(arrow + 3)..].Trim();
                    if (value.Length == 0)
                    {
                        if (skip.Contains(name)) { skipIndent = indent; continue; }
                        names.Add((indent, name));
                        continue;
                    }
                    if (value == "Null") continue;
                    yield return (name, value);
                }
                else
                {
                    if (body == "Null") continue;
                    yield return (names.Count > 0 ? names[^1].Name + "[]" : "[]", body);
                }
            }
        }

        static bool LeafFound(string value, string jsonLower, Tokens tokens)
        {
            string lower = value.ToLowerInvariant();
            int typeTag = lower.IndexOf('<');
            if (typeTag > 0 && lower.EndsWith('>')) lower = lower[..typeTag];
            if (jsonLower.Contains(lower, StringComparison.Ordinal)) return true;
            string escaped = System.Text.Json.JsonSerializer.Serialize(value)[1..^1].ToLowerInvariant();
            if (jsonLower.Contains(escaped, StringComparison.Ordinal)) return true;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            if (TimeOnly.TryParse(value, invariant, out var time) && jsonLower.Contains(time.ToString("HH:mm:ss", invariant), StringComparison.Ordinal)) return true;
            // Print rounds a percent
            if (lower.EndsWith('%') && double.TryParse(lower.TrimEnd('%').Trim(), System.Globalization.NumberStyles.Float, invariant, out var pct))
            {
                if (pct == 0) return true;
                foreach (var d in tokens.Numbers) if (Math.Abs(d * 100 - pct) < 1) return true;
                return false;
            }
            // Numbers in any layout: "0, 0, 0.05"
            var parts = lower.Split(new[] { ',', ' ', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts.All(x => double.TryParse(x, System.Globalization.NumberStyles.Float, invariant, out _)))
                return parts.All(x => tokens.Text.Contains(x) || tokens.Floats.Contains(BitConverter.SingleToInt32Bits((float)double.Parse(x, invariant))));
            return false;
        }

        // Each stored record is scanned once, so a big record can't make this slow.
        sealed class Tokens
        {
            static readonly Regex Number = new(@"-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?", RegexOptions.Compiled);
            public readonly HashSet<string> Text = new();
            public readonly HashSet<int> Floats = new();
            public readonly List<double> Numbers = new();

            public Tokens(string json)
            {
                foreach (Match m in Number.Matches(json))
                {
                    Text.Add(m.Value);
                    if (double.TryParse(m.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        Floats.Add(BitConverter.SingleToInt32Bits((float)d));
                        Numbers.Add(d);
                    }
                }
            }
        }

        static string? FieldValue(string data, string field)
        {
            string marker = $"\"{field}\":\"";
            int at = data.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;
            at += marker.Length;
            int end = data.IndexOf('"', at);
            return end < 0 ? null : data.Substring(at, end - at);
        }

        static string FieldBefore(string data, int at)
        {
            int q = data.LastIndexOf("\":", at, StringComparison.Ordinal);
            if (q <= 0) return "?";
            int s = data.LastIndexOf('"', q - 1);
            return s < 0 ? "?" : data.Substring(s + 1, q - s - 1);
        }

        static string Snip(string data, int at, int len)
        {
            int end = Math.Min(data.Length, at + len);
            int quote = data.IndexOf('"', at);
            if (quote > 0 && quote < end) end = quote;
            return data.Substring(at, end - at);
        }

        // EDID comes first in a record, so nothing else needs unpacking
        static string RawEdid(byte[] b, RawRec r)
        {
            if (r.Size < 6) return "";
            uint flags = BitConverter.ToUInt32(b, r.Offset + 8);
            byte[] data;
            int start;
            if ((flags & 0x40000) != 0)
            {
                using var z = new ZLibStream(new MemoryStream(b, r.Offset + 28, r.Size - 4), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                var head = new byte[4096];
                int got = z.Read(head, 0, head.Length);
                ms.Write(head, 0, got);
                data = ms.ToArray();
                start = 0;
            }
            else
            {
                data = b;
                start = r.Offset + 24;
            }
            if (data.Length < start + 6 || data[start] != (byte)'E' || data[start + 1] != (byte)'D' || data[start + 2] != (byte)'I' || data[start + 3] != (byte)'D') return "";
            int len = BitConverter.ToUInt16(data, start + 4);
            if (data.Length < start + 6 + len) return "";
            return Encoding.ASCII.GetString(data, start + 6, len).TrimEnd('\0');
        }

        sealed class RawRec
        {
            public string Sig = "";
            public int Offset;
            public int Size;
        }
    }
}
