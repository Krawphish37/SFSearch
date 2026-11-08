using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Masters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Noggog;

namespace SFSearch
{
    /// <summary>
    /// Reads a plugin with Mutagen, saves it to a temp folder through both readers and compares raw bytes.
    /// Never writes to Data. Lists whatever a read and save would lose in that plugin.
    /// </summary>
    public static class LossCheckCommand
    {
        const string Usage = @"Usage: SFSearch LossCheck <plugin|path> [<plugin|path> ...] [--all] [--report <file>] [--samples N]
  Reads each plugin, saves a copy to a temp folder (never to Data) and compares the bytes:
    LOST     a record or subrecord is missing, or holds fewer bytes
    CHANGED  same length, other bytes
    ADDED    the save holds something the original didn't
    NOTE     a deleted record saved as its header only, which isn't live data
  --all             every .esm and .esp in Data
  --report <file>   also write the results to a file
  --samples N       examples listed per kind (default 3)
  Order, FormID style (FD/FE or master number) and empty groups aren't counted: they hold no data.";

        public static void Run(List<string> args)
        {
            bool all = Sfs.TakeFlag(args, "--all");
            string? report = Sfs.TakeOption(args, "--report");
            int samples = int.TryParse(Sfs.TakeOption(args, "--samples"), out var n) ? n : 3;
            if (!all && args.Count == 0)
            {
                Console.WriteLine(Usage);
                return;
            }

            string dataFolder = Program.GetDataFolderPathPublic();
            var targets = new List<string>();
            if (all)
            {
                targets.AddRange(Directory.GetFiles(dataFolder, "*.esm").Concat(Directory.GetFiles(dataFolder, "*.esp")).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            }
            foreach (var arg in args)
            {
                string path = File.Exists(Sfs.ResolveUserPath(arg)) ? Sfs.ResolveUserPath(arg) : Path.Combine(dataFolder, arg);
                targets.Add(path);
            }

            var flags = new HeaderFlagCache(dataFolder);
            StreamWriter? reportWriter = report != null ? new StreamWriter(report, append: false) : null;
            void Out(string line)
            {
                Console.WriteLine(line);
                if (reportWriter != null)
                {
                    reportWriter.WriteLine(line);
                    reportWriter.Flush();
                }
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "sfs_losscheck_" + Guid.NewGuid().ToString("N"));
            int clean = 0, lossy = 0, unreadable = 0;
            long records = 0;
            var totals = new Dictionary<string, long>();
            var unreadableList = new List<string>();
            var lossyList = new List<string>();
            try
            {
                int index = 0;
                foreach (var path in targets)
                {
                    index++;
                    string name = Path.GetFileName(path);
                    if (!File.Exists(path))
                    {
                        Out($"{name}: file not found");
                        unreadable++;
                        unreadableList.Add(name + " (not found)");
                        continue;
                    }
                    var plugin = new Tally(samples);
                    string? failure = null;
                    foreach (bool overlay in new[] { false, true })
                    {
                        string dir = Path.Combine(tempRoot, overlay ? "overlay" : "full");
                        Directory.CreateDirectory(dir);
                        string saved = Path.Combine(dir, name);
                        string readerName = overlay ? "overlay" : "full read";
                        try
                        {
                            SaveCopy(path, saved, overlay, flags);
                            var compared = Compare(path, saved, readerName, flags, plugin, 0);
                            if (!overlay) records += compared;
                        }
                        catch (Exception ex)
                        {
                            var root = ex;
                            while (root.InnerException != null) root = root.InnerException;
                            failure = $"{readerName}: {root.GetType().Name}: {OneLine(root.Message)}";
                        }
                        finally
                        {
                            GC.Collect();
                            TryDeleteDirectory(dir);
                        }
                        if (failure != null) break;
                    }

                    if (failure != null)
                    {
                        unreadable++;
                        unreadableList.Add($"{name}: {failure}");
                        Out($"[{index}/{targets.Count}] {name}: COULD NOT READ OR SAVE - {failure}");
                    }
                    bool lost = plugin.Lost > 0;
                    if (lost)
                    {
                        lossy++;
                        lossyList.Add($"{name}: {plugin.Lost}");
                    }
                    else if (failure == null)
                    {
                        clean++;
                    }
                    if (failure == null || plugin.Any)
                    {
                        string status = lost ? "LOST DATA" : "nothing lost";
                        Out($"[{index}/{targets.Count}] {name}: {status}");
                        foreach (var line in plugin.Lines()) Out("  " + line);
                    }
                    foreach (var kv in plugin.Kinds) totals[kv.Key] = totals.GetValueOrDefault(kv.Key) + kv.Value;
                }

                Out("");
                Out($"Plugins checked {targets.Count}: nothing lost {clean}; lost data {lossy}; could not read or save {unreadable}. Records compared per reader: {records}");
                foreach (var line in lossyList) Out("  LOST DATA  " + line);
                foreach (var line in unreadableList) Out("  UNREADABLE " + line);
                foreach (var kv in totals.OrderBy(k => Rank(k.Key)).ThenByDescending(k => k.Value)) Out($"{kv.Value,9}  {kv.Key}");
            }
            finally
            {
                reportWriter?.Dispose();
                TryDeleteDirectory(tempRoot);
            }
        }

        static string OneLine(string text)
        {
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length > 200 ? text[..200] : text;
        }

        static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // A temp copy still open; Windows cleans its temp folder eventually
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static void SaveCopy(string path, string saved, bool overlay, HeaderFlagCache flags)
        {
            var modKey = ModKey.FromFileName(Path.GetFileName(path));
            var read = new BinaryReadParameters { MasterFlagsLookup = flags };
            if (overlay)
            {
                using var mod = StarfieldMod.CreateFromBinaryOverlay(new ModPath(modKey, path), StarfieldRelease.Starfield, read);
                mod.WriteToBinary(saved, WriteParams(mod.ModHeader.MasterReferences.Select(m => m.Master).ToList(), flags));
            }
            else
            {
                var mod = StarfieldMod.CreateFromBinary(new ModPath(modKey, path), StarfieldRelease.Starfield, read);
                mod.WriteToBinary(saved, WriteParams(mod.ModHeader.MasterReferences.Select(m => m.Master).ToList(), flags));
            }
        }

        static BinaryWriteParameters WriteParams(List<ModKey> masters, HeaderFlagCache flags) => new()
        {
            MasterFlagsLookup = flags,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = new MastersListOrderingByLoadOrder(masters),
            RecordCount = RecordCountOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck,
        };

        // ---- Comparing raw bytes

        sealed class Entry
        {
            public bool IsGroup;
            public string Type = "";
            public uint Id;
            public byte[] Header = Array.Empty<byte>();
            public byte[] Stored = Array.Empty<byte>();
        }

        sealed class Tally
        {
            readonly int _samples;
            public readonly Dictionary<string, long> Kinds = new();
            readonly Dictionary<string, List<string>> _examples = new();
            public long Lost;
            public bool Any => Kinds.Count > 0;
            public Tally(int samples) => _samples = samples;

            public void Add(string kind, string example)
            {
                Kinds[kind] = Kinds.GetValueOrDefault(kind) + 1;
                if (kind.StartsWith("LOST", StringComparison.Ordinal)) Lost++;
                if (!_examples.TryGetValue(kind, out var list)) _examples[kind] = list = new List<string>();
                if (list.Count < _samples) list.Add(example);
            }

            public IEnumerable<string> Lines()
            {
                foreach (var kv in Kinds.OrderBy(k => Rank(k.Key)).ThenByDescending(k => k.Value))
                {
                    yield return $"{kv.Value,8}  {kv.Key}   e.g. {string.Join(" ; ", _examples[kv.Key])}";
                }
            }
        }

        static int Rank(string kind) => kind.StartsWith("LOST", StringComparison.Ordinal) ? 0 : kind.StartsWith("CHANGED", StringComparison.Ordinal) ? 1 : kind.StartsWith("ADDED", StringComparison.Ordinal) ? 2 : 3;

        static long Compare(string originalPath, string savedPath, string reader, HeaderFlagCache flags, Tally tally, long runningTotal)
        {
            var original = Flatten(originalPath);
            var saved = Flatten(savedPath);
            var key = ModKey.FromFileName(Path.GetFileName(originalPath));
            var packageA = SeparatedMasterPackage.Factory(GameRelease.Starfield, new ModPath(key, originalPath), flags, new System.IO.Abstractions.FileSystem());
            var packageB = SeparatedMasterPackage.Factory(GameRelease.Starfield, new ModPath(key, savedPath), flags, new System.IO.Abstractions.FileSystem());

            int masters = Subrecords(Unpack(saved[0])).Count(x => x.Sig == "MAST");
            CompareRecord("TES4", original[0], saved[0], reader, tally, masters);
            var lookup = new Dictionary<(string, FormKey), Entry>();
            foreach (var entry in saved.Skip(1))
            {
                if (!entry.IsGroup) lookup.TryAdd((entry.Type, packageB.GetFormKey(FormID.Factory(entry.Id), reference: false)), entry);
            }
            long count = 0;
            foreach (var entry in original.Skip(1))
            {
                if (entry.IsGroup) continue;
                count++;
                var formKey = packageA.GetFormKey(FormID.Factory(entry.Id), reference: false);
                // Mutagen writes a deleted record as its header only. It isn't live data, so it isn't counted as lost.
                if ((BitConverter.ToUInt32(entry.Header, 8) & 0x20) != 0 && lookup.ContainsKey((entry.Type, formKey)))
                {
                    if (entry.Stored.Length > 0) tally.Add($"NOTE {entry.Type} deleted record saved as its header only", $"{reader} {formKey} {Edid(entry)}");
                    continue;
                }
                if (!lookup.TryGetValue((entry.Type, formKey), out var other))
                {
                    tally.Add($"LOST {entry.Type} whole record", $"{reader} {formKey} {Edid(entry)}");
                    continue;
                }
                CompareRecord(entry.Type, entry, other, reader, tally, masters, $"{formKey} {Edid(entry)}");
            }
            return runningTotal + count;
        }

        static string Edid(Entry entry)
        {
            foreach (var (sig, data) in Subrecords(Unpack(entry)))
            {
                if (sig == "EDID") return Encoding.ASCII.GetString(data).TrimEnd('\0');
            }
            return "[no EditorID]";
        }

        static void CompareRecord(string type, Entry x, Entry y, string reader, Tally tally, int masters, string name = "")
        {
            var a = Subrecords(Unpack(x)).GroupBy(s => s.Sig).ToDictionary(g => g.Key, g => g.Select(v => v.Data).ToList());
            var b = Subrecords(Unpack(y)).GroupBy(s => s.Sig).ToDictionary(g => g.Key, g => g.Select(v => v.Data).ToList());
            foreach (var (sig, originals) in a)
            {
                b.TryGetValue(sig, out var savedList);
                var left = new List<byte[]>(originals);
                var right = new List<byte[]>(savedList ?? new List<byte[]>());
                // Same bytes anywhere in the record: only the order changed
                for (int i = left.Count - 1; i >= 0; i--)
                {
                    var item = left[i];
                    int j = right.FindIndex(v => v.AsSpan().SequenceEqual(item));
                    if (j >= 0)
                    {
                        left.RemoveAt(i);
                        right.RemoveAt(j);
                    }
                }
                // The same FormIDs in the other style (FD/FE or master number)
                for (int i = left.Count - 1; i >= 0; i--)
                {
                    var item = left[i];
                    int j = right.FindIndex(v => OnlyStyleDiffers(item, v, masters));
                    if (j >= 0)
                    {
                        left.RemoveAt(i);
                        right.RemoveAt(j);
                    }
                }
                if (left.Count == 0 && right.Count == 0) continue;
                var sortedLeft = left.OrderBy(v => v.Length).ToList();
                var sortedRight = right.OrderBy(v => v.Length).ToList();
                int pairs = Math.Min(sortedLeft.Count, sortedRight.Count);
                for (int i = 0; i < pairs; i++)
                {
                    var p = sortedLeft[i];
                    var q = sortedRight[i];
                    if (q.Length < p.Length) tally.Add($"LOST {type} {sig} shorter", $"{reader} {name} {p.Length}->{q.Length}");
                    else if (q.Length > p.Length) tally.Add($"ADDED {type} {sig} longer", $"{reader} {name} {p.Length}->{q.Length}");
                    else tally.Add($"CHANGED {type} {sig}", $"{reader} {name} {Hex(p)} -> {Hex(q)}");
                }
                if (sortedLeft.Count > pairs) tally.Add($"LOST {type} {sig} subrecord", $"{reader} {name} {Hex(sortedLeft[pairs])}");
                if (sortedRight.Count > pairs) tally.Add($"ADDED {type} {sig} subrecord", $"{reader} {name} {Hex(sortedRight[pairs])}");
            }
            foreach (var (sig, savedList) in b)
            {
                if (!a.ContainsKey(sig)) tally.Add($"ADDED {type} {sig} subrecord", $"{reader} {name} {Hex(savedList[0])}");
            }
            if (!x.Header.AsSpan(8, 4).SequenceEqual(y.Header.AsSpan(8, 4))) tally.Add($"CHANGED {type} record flags", $"{reader} {name} {Hex(x.Header.AsSpan(8, 4).ToArray())} -> {Hex(y.Header.AsSpan(8, 4).ToArray())}");
            if (!x.Header.AsSpan(16, 8).SequenceEqual(y.Header.AsSpan(16, 8))) tally.Add($"CHANGED {type} record version", $"{reader} {name}");
        }

        static string Hex(byte[] bytes) => Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16)));

        // Differences only where an FD or FE own-FormID is written as a master number: the low ID bytes must match
        static bool OnlyStyleDiffers(byte[] p, byte[] q, int masters)
        {
            if (p.Length != q.Length) return false;
            bool any = false;
            for (int k = 0; k < p.Length; k++)
            {
                if (p[k] == q[k]) continue;
                bool covered = false;
                for (int top = k; top <= Math.Min(k + 2, p.Length - 1) && !covered; top++)
                {
                    if (top < 3 || (p[top] != 0xFD && p[top] != 0xFE)) continue;
                    if (q[top] > masters) continue;
                    if (p[top - 3] != q[top - 3]) continue;
                    if (p[top] == 0xFD ? p[top - 2] == q[top - 2] : (p[top - 2] & 0x0F) == (q[top - 2] & 0x0F)) covered = true;
                }
                if (!covered) return false;
                any = true;
            }
            return any;
        }

        static List<Entry> Flatten(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var list = new List<Entry>();
            void Walk(int pos, int end)
            {
                while (pos + 24 <= end)
                {
                    string type = Encoding.ASCII.GetString(bytes, pos, 4);
                    int size = BitConverter.ToInt32(bytes, pos + 4);
                    var header = bytes.AsSpan(pos, 24).ToArray();
                    if (type == "GRUP")
                    {
                        list.Add(new Entry { IsGroup = true, Type = Encoding.ASCII.GetString(bytes, pos + 8, 4), Id = BitConverter.ToUInt32(bytes, pos + 8), Header = header });
                        Walk(pos + 24, pos + size);
                        pos += size;
                    }
                    else
                    {
                        list.Add(new Entry { Type = type, Id = BitConverter.ToUInt32(bytes, pos + 12), Header = header, Stored = bytes.AsSpan(pos + 24, size).ToArray() });
                        pos += 24 + size;
                    }
                }
            }
            Walk(0, bytes.Length);
            return list;
        }

        static byte[] Unpack(Entry entry)
        {
            uint flags = BitConverter.ToUInt32(entry.Header, 8);
            if ((flags & 0x40000) == 0) return entry.Stored;
            using var z = new ZLibStream(new MemoryStream(entry.Stored, 4, entry.Stored.Length - 4), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }

        static List<(string Sig, byte[] Data)> Subrecords(byte[] data)
        {
            var list = new List<(string, byte[])>();
            int p = 0;
            int? big = null;
            while (p + 6 <= data.Length)
            {
                string t = Encoding.ASCII.GetString(data, p, 4);
                int len = BitConverter.ToUInt16(data, p + 4);
                if (t == "XXXX" && len == 4)
                {
                    big = BitConverter.ToInt32(data, p + 6);
                    p += 10;
                    continue;
                }
                if (big != null)
                {
                    len = big.Value;
                    big = null;
                }
                if (p + 6 + len > data.Length) break;
                list.Add((t, data.AsSpan(p + 6, len).ToArray()));
                p += 6 + len;
            }
            return list;
        }

        /// <summary>
        /// Each master's real Small/Medium/Full size, from its own header.
        /// </summary>
        internal sealed class HeaderFlagCache : IReadOnlyCache<IModMasterStyledGetter, ModKey>
        {
            readonly string _dataFolder;
            readonly Dictionary<ModKey, IModMasterStyledGetter> _cache = new();
            public HeaderFlagCache(string dataFolder) => _dataFolder = dataFolder;

            public IModMasterStyledGetter TryGetValue(ModKey key)
            {
                lock (_cache)
                {
                    if (_cache.TryGetValue(key, out var cached)) return cached;
                    var style = MasterStyle.Full;
                    string path = Path.Combine(_dataFolder, key.FileName);
                    if (File.Exists(path))
                    {
                        using var fs = File.OpenRead(path);
                        var header = new byte[12];
                        fs.ReadExactly(header);
                        var flags = (StarfieldModHeader.HeaderFlag)BitConverter.ToInt32(header, 8);
                        if (flags.HasFlag(StarfieldModHeader.HeaderFlag.Light)) style = MasterStyle.Small;
                        else if (flags.HasFlag(StarfieldModHeader.HeaderFlag.Medium)) style = MasterStyle.Medium;
                    }
                    return _cache[key] = new MasterStyled(key, style);
                }
            }

            record MasterStyled(ModKey ModKey, MasterStyle MasterStyle) : IModMasterStyledGetter;

            public int Count => _cache.Count;
            public bool ContainsKey(ModKey key) => true;
            public IModMasterStyledGetter this[ModKey key] => TryGetValue(key);
            public IEnumerator<Noggog.IKeyValue<ModKey, IModMasterStyledGetter>> GetEnumerator() { yield break; }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { yield break; }
            public IEnumerable<ModKey> Keys { get { yield break; } }
            public IEnumerable<IModMasterStyledGetter> Items { get { yield break; } }
        }
    }
}
