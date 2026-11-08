using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Masters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SFSearch
{
    /// <summary>
    /// Finds a record in a plugin file by reading only the 24-byte record and group headers (record contents are skipped).
    /// Gives a record's type and, for anything inside a cell, the cell's FormID, in one pass over the file and without
    /// building Mutagen's per-type indexes. Raw FormIDs are converted with Mutagen's own master table
    /// (SeparatedMasterPackage), so light and medium masters map exactly as Mutagen reads them.
    /// </summary>
    public static class HeaderScanner
    {
        public readonly record struct Hit(string Signature, uint FormId, uint CellFormId, long Offset, uint Flags, uint DataSize);

        const int HeaderLength = 24;     // Starfield record and group headers
        const uint CompressedFlag = 0x00040000;
        static readonly uint Grup = BitConverter.ToUInt32(Encoding.ASCII.GetBytes("GRUP"));
        static readonly uint Edid = BitConverter.ToUInt32(Encoding.ASCII.GetBytes("EDID"));

        /// <summary>Calls onRecord for every record in file order until it returns true.</summary>
        public static void Scan(string path, Func<Hit, MemoryMappedViewAccessor, bool> onRecord)
        {
            long length = new FileInfo(path).Length;
            using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            long pos = HeaderLength + view.ReadUInt32(4); // skip the TES4 header record
            var groups = new Stack<(long End, int Type, uint Label)>();
            while (pos + HeaderLength <= length)
            {
                while (groups.Count > 0 && pos >= groups.Peek().End) groups.Pop();
                uint sig = view.ReadUInt32(pos);
                if (sig == Grup)
                {
                    uint size = view.ReadUInt32(pos + 4);
                    if (size < HeaderLength) throw new InvalidDataException($"Bad group size {size} at 0x{pos:X} in {Path.GetFileName(path)}");
                    groups.Push((pos + size, view.ReadInt32(pos + 12), view.ReadUInt32(pos + 8)));
                    pos += HeaderLength;
                    continue;
                }
                uint dataSize = view.ReadUInt32(pos + 4);
                // Group types 6/8/9 = cell children / persistent / temporary; their label is the cell's FormID.
                uint cell = 0;
                foreach (var g in groups) // innermost first
                    if (g.Type is 6 or 8 or 9) { cell = g.Label; break; }
                var hit = new Hit(SigString(sig), view.ReadUInt32(pos + 12), cell, pos, view.ReadUInt32(pos + 8), dataSize);
                if (onRecord(hit, view)) return;
                pos += HeaderLength + dataSize;
            }
        }

        /// <summary>The EditorID when it is the record's first subrecord and the record isn't compressed; otherwise null.</summary>
        public static string? EditorIdOf(Hit hit, MemoryMappedViewAccessor view)
        {
            if ((hit.Flags & CompressedFlag) != 0 || hit.DataSize < 7) return null;
            long p = hit.Offset + HeaderLength;
            if (view.ReadUInt32(p) != Edid) return null;
            int len = view.ReadUInt16(p + 4);
            if (len < 1 || len > hit.DataSize - 6) return null;
            var bytes = new byte[len - 1]; // drop the terminating zero
            view.ReadArray(p + 6, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Raw FormID and EditorID of every record that has one, compressed records included (read in parallel).</summary>
        public static List<(uint Raw, string EditorId)> EditorIds(string path)
        {
            var hits = new List<Hit>();
            Scan(path, (h, _) => { hits.Add(h); return false; });
            var result = new System.Collections.Concurrent.ConcurrentBag<(uint, string)>();
            if (hits.Count == 0) return new List<(uint Raw, string EditorId)>();
            using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            System.Threading.Tasks.Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, hits.Count, 2000), range =>
            {
                var data = new byte[1 << 16];
                var plain = new byte[1 << 16];
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    var h = hits[i];
                    int size = (int)h.DataSize;
                    if (size < 7) continue;
                    if ((h.Flags & CompressedFlag) == 0)
                    {
                        long p = h.Offset + HeaderLength;
                        if (view.ReadUInt32(p) != Edid) continue;
                        int len = view.ReadUInt16(p + 4);
                        if (len < 2 || len > size - 6) continue;
                        var bytes = new byte[len - 1];
                        view.ReadArray(p + 6, bytes, 0, bytes.Length);
                        result.Add((h.FormId, Encoding.UTF8.GetString(bytes)));
                        continue;
                    }
                    try
                    {
                        if (data.Length < size) data = new byte[size * 2];
                        view.ReadArray(h.Offset + HeaderLength, data, 0, size);
                        int plainSize = BitConverter.ToInt32(data, 0);
                        if (plain.Length < plainSize) plain = new byte[plainSize * 2];
                        using var z = new System.IO.Compression.ZLibStream(new MemoryStream(data, 4, size - 4), System.IO.Compression.CompressionMode.Decompress);
                        z.ReadExactly(plain, 0, plainSize);
                        if (plainSize >= 7 && BitConverter.ToUInt32(plain, 0) == Edid)
                        {
                            int len = BitConverter.ToUInt16(plain, 4);
                            if (len >= 2 && len <= plainSize - 6) result.Add((h.FormId, Encoding.UTF8.GetString(plain, 6, len - 1)));
                        }
                    }
                    catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException) { }
                }
            });
            return result.ToList();
        }

        public static IReadOnlySeparatedMasterPackage MasterTable(ModKey modKey, string path) =>
            SeparatedMasterPackage.Factory(GameRelease.Starfield, new ModPath(modKey, path), new SimpleMasterFlagCache(), new System.IO.Abstractions.FileSystem());

        /// <summary>
        /// The ways a record's FormID can be written in this plugin's file. A plugin's own record can be written by its place in the
        /// master list (0100083A) or, in a Small or Medium plugin, by size (FE00083A in atom74.esm, FD0076BA in kgcdoom.esm).
        /// Mutagen's GetFormID only gives the first.
        /// </summary>
        public static uint[] RawForms(IReadOnlySeparatedMasterPackage masters, ModKey plugin, string path, FormKey key)
        {
            uint plain = masters.GetFormID(key).Raw;
            if (key.ModKey != plugin) return new[] { plain };
            var header = new byte[12];
            using (var file = File.OpenRead(path)) file.ReadExactly(header);
            var flags = (StarfieldModHeader.HeaderFlag)BitConverter.ToInt32(header, 8);
            MasterStyle? own = flags.HasFlag(StarfieldModHeader.HeaderFlag.Light) ? MasterStyle.Small
                : flags.HasFlag(StarfieldModHeader.HeaderFlag.Medium) ? MasterStyle.Medium : null;
            if (own == null) return new[] { plain };
            uint index = (uint)masters.Raw.Masters.Count(m => masters.TryLookupModKey(m.Master, true, out var style, out _) && style == own);
            return new[] { plain, FormID.Factory(own.Value, index, key.ID).Raw };
        }

        /// <summary>Finds one record by FormKey in one file. Returns its type and its cell (placed references), or null.</summary>
        public static (Type Getter, string Signature, FormKey? Cell)? FindFormKey(ModKey modKey, string path, FormKey formKey)
        {
            var masters = MasterTable(modKey, path);
            var forms = RawForms(masters, modKey, path, formKey);
            Hit? found = null;
            Scan(path, (h, _) => { if (!forms.Contains(h.FormId)) return false; found = h; return true; });
            if (found is not Hit hit || GetterFor(hit.Signature) is not Type getter) return null;
            return (getter, hit.Signature, CellKey(masters, hit));
        }

        /// <summary>Finds a record by EditorID in one file (uncompressed records only). Returns FormKey, type and cell.</summary>
        public static (FormKey FormKey, Type Getter, string Signature, FormKey? Cell)? FindEditorId(ModKey modKey, string path, string editorId)
        {
            Hit? found = null;
            Scan(path, (h, v) =>
            {
                if (!string.Equals(EditorIdOf(h, v), editorId, StringComparison.OrdinalIgnoreCase)) return false;
                found = h;
                return true;
            });
            if (found is not Hit hit || GetterFor(hit.Signature) is not Type getter) return null;
            var masters = MasterTable(modKey, path);
            return (masters.GetFormKey(FormID.Factory(hit.FormId), reference: false), getter, hit.Signature, CellKey(masters, hit));
        }

        public static FormKey? CellKey(IReadOnlySeparatedMasterPackage masters, Hit hit) =>
            hit.CellFormId == 0 ? null : masters.GetFormKey(FormID.Factory(hit.CellFormId), reference: true);

        static string SigString(uint sig) => Encoding.ASCII.GetString(BitConverter.GetBytes(sig));

        static Dictionary<string, Type>? _getters;

        /// <summary>4-character record signature (WEAP, REFR, GMST...) to the Mutagen getter interface, from the generated registrations.</summary>
        public static Type? GetterFor(string signature)
        {
            if (_getters == null)
            {
                _getters = new Dictionary<string, Type>(StringComparer.Ordinal);
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                foreach (var reg in typeof(IWeaponGetter).Assembly.GetTypes().Where(t => t.Name.EndsWith("_Registration")))
                {
                    object? trig = reg.GetField("TriggeringRecordType", flags)?.GetValue(null) ?? reg.GetProperty("TriggeringRecordType", flags)?.GetValue(null);
                    var getter = (reg.GetField("GetterType", flags)?.GetValue(null) ?? reg.GetProperty("GetterType", flags)?.GetValue(null)) as Type;
                    if (trig is not RecordType rt || getter == null || !typeof(IMajorRecordGetter).IsAssignableFrom(getter)) continue;
                    string key = rt.ToString();
                    // Several classes share a signature (GMST -> GameSettingFloat/Int/...): keep the common base.
                    if (_getters.TryGetValue(key, out var existing) && (existing.IsAssignableFrom(getter) || existing.Name.Length <= getter.Name.Length)) continue;
                    _getters[key] = getter;
                }
            }
            return _getters.TryGetValue(signature, out var t) ? t : null;
        }
    }
}
