using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Engine
{
    /// <summary>
    /// The links of one world record, for the world link index (J32). The walk (<see cref="RecordLinks.Walk"/>, the JSON
    /// serializer's kernel over Mutagen's object) defines a link. It is slow on four bulky subrecords that hold few links
    /// in a lot of data (GAPS S60: the navmesh info map's walk takes 40 s), so those four are read from their bytes, at
    /// the places Mutagen's own reader takes them from (Mutagen 0.55.0-fixes.17, file named at each reader), and the
    /// rest of the record, with those subrecords left out, is read by Mutagen and walked as always:
    /// <list type="bullet">
    /// <item>CELL XTV2: Traversals[*].Traversal (Common Subrecords\TraversalReference.cs, Parse and HoldsEntry)</item>
    /// <item>NAVM NVNM: NavmeshGeometry.Parent.Parent, .EdgeLinks[*].Mesh, .DoorTriangles[*].Door (NavmeshGeometry.xml and .cs)</item>
    /// <item>SFTR FNAM: SurfacePatterns[*] and SurfacePatterns2[*], the first and second FNAM (SurfaceTree.xml)</item>
    /// <item>NAVI NVMI: MapInfos[*] (NavigationMeshInfoMap.xml, NavigationMapInfo.cs ReadTail)</item>
    /// </list>
    /// Only those subrecords outside a component (between BFCB and BFCE a subrecord is a component's field). When a byte
    /// reader doesn't reach its subrecord's end as Mutagen lays it out, the whole record is read by Mutagen instead. The
    /// dev tool tests\SFSearch.RefsCheck (world) holds this to the walk of the whole record, record by record.
    /// One reader per thread.
    /// </summary>
    public sealed class WorldReader
    {
        /// <summary>Changes whenever a byte reader changes what it finds: part of the world link files' builder key.</summary>
        public const int Version = 1;

        /// <summary>The paths the byte readers give, as the walk names them.</summary>
        public static readonly string[] ReaderPaths =
        {
            "Traversals[*].Traversal",
            "NavmeshGeometry.Parent.Parent", "NavmeshGeometry.Parent.UnusedWorldspaceParent", "NavmeshGeometry.EdgeLinks[*].Mesh", "NavmeshGeometry.DoorTriangles[*].Door",
            "SurfacePatterns[*]", "SurfacePatterns2[*]",
            "MapInfos[*].NavigationMesh", "MapInfos[*].MergedTo[*]", "MapInfos[*].PreferredMerges[*]", "MapInfos[*].LinkedDoors[*].Door",
            "MapInfos[*].Traversals[*].CellOrObject", "MapInfos[*].Traversals[*].Traversal", "MapInfos[*].Traversals[*].FromNavmesh", "MapInfos[*].Traversals[*].ToNavmesh",
            "MapInfos[*].PathingCell.ParentWorld", "MapInfos[*].PathingCell.ParentCell",
        };
        const int FTraversal = 0, FNavParent = 1, FNavUnusedWorld = 2, FNavEdge = 3, FNavDoor = 4, FPatterns = 5, FPatterns2 = 6,
            FMiMesh = 7, FMiMerged = 8, FMiPreferred = 9, FMiDoor = 10, FMiTravCell = 11, FMiTravTrav = 12, FMiTravFrom = 13, FMiTravTo = 14, FMiWorld = 15, FMiCell = 16;

        const uint Xxxx = 0x58585858, Bfcb = 0x42434642, Bfce = 0x45434642;
        const uint SigCell = 0x4C4C4543, SigNavm = 0x4D56414E, SigSftr = 0x52544653, SigNavi = 0x4956414E;
        const uint Xtv2 = 0x32565458, Nvnm = 0x4D4E564E, Fnam = 0x4D414E46, Nvmi = 0x494D564E;

        /// <summary>How a record was read.</summary>
        public enum How { Mutagen, BytesAndMutagen, MutagenWhole, Unread }

        readonly RecordSchema _schema;
        readonly Func<string, int> _fieldId;
        readonly int[] _readerIds;
        readonly Mutagen.Bethesda.Serialization.FormLinkPlaceCollector _sink = RecordLinks.Collector();
        int[] _sinkIds = Array.Empty<int>();
        readonly Dictionary<(ulong Target, int Field), uint> _agg = new();
        readonly List<(int From, int To)> _cuts = new();
        readonly Dictionary<string, string?> _typeOf = new(StringComparer.Ordinal);

        /// <param name="fieldId">A field path's number in the file being made (the same number for the same path on every thread).</param>
        public WorldReader(RecordSchema schema, Func<string, int> fieldId)
        {
            _schema = schema; _fieldId = fieldId;
            _readerIds = new int[ReaderPaths.Length];
            for (int i = 0; i < ReaderPaths.Length; i++) _readerIds[i] = fieldId(ReaderPaths[i]);
        }

        /// <summary>The plugin a record is read for: its session, its FormID numbering and its name table.</summary>
        public sealed class Plugin
        {
            public required IndexedPlugin P { get; init; }
            public required PluginSession Session { get; init; }
            readonly Dictionary<string, int> _owners = new(StringComparer.OrdinalIgnoreCase);
            internal int OwnerOf(string name)
            {
                if (_owners.Count == 0) lock (_owners) if (_owners.Count == 0) { var names = P.Segment!.Names; for (int i = 0; i < names.Count; i++) _owners[names[i]] = i; }
                return _owners.TryGetValue(name, out int o) ? o : -1;
            }
        }

        /// <summary>
        /// Reads one world record's links into <paramref name="rows"/> (one row per target and field, with its count).
        /// <paramref name="head"/> is the record's 24-byte header, <paramref name="data"/> its subrecords, inflated.
        /// </summary>
        public How Read(Plugin plugin, int entry, ReadOnlySpan<byte> head, ReadOnlySpan<byte> data, List<WorldRow> rows, out string? why, out long foreign)
        {
            why = null; foreign = 0;
            _agg.Clear(); _cuts.Clear();
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(head);
            int formVersion = BinaryPrimitives.ReadUInt16LittleEndian(head[20..]);
            uint bulky = sig switch { SigCell => Xtv2, SigNavm => Nvnm, SigSftr => Fnam, SigNavi => Nvmi, _ => 0 };
            var ids = plugin.P.FormIds;
            if (bulky != 0 && !ReadBulky(data, bulky, formVersion, ids))
            {
                _agg.Clear(); _cuts.Clear();
            }
            bool byBytes = _cuts.Count > 0;
            string? type = TypeOf(plugin.P.Segment!.SignatureOf(plugin.P.Segment![entry]));
            if (type == null) { why = "Mutagen has no type for its signature"; return How.Unread; }
            try
            {
                Walk(plugin, head, data, type, ref foreign);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
            {
                if (!byBytes) { why = e is RecordException ? e.Message : e.Message.Split('\n')[0]; return How.Unread; }
                // The record with the bulky subrecords left out didn't read: the whole record, as it is
                _agg.Clear(); _cuts.Clear(); foreign = 0;
                try { Walk(plugin, head, data, type, ref foreign); byBytes = false; }
                catch (Exception e2) when (e2 is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
                {
                    why = e2 is RecordException ? e2.Message : e2.Message.Split('\n')[0];
                    return How.Unread;
                }
                foreach (var (k, n) in _agg) rows.Add(new WorldRow(k.Target, (uint)entry, k.Field, n));
                return How.MutagenWhole;
            }
            foreach (var (k, n) in _agg) rows.Add(new WorldRow(k.Target, (uint)entry, k.Field, n));
            return byBytes ? How.BytesAndMutagen : bulky != 0 && HasTop(data, bulky) ? How.MutagenWhole : How.Mutagen;
        }

        string? TypeOf(string signature)
        {
            if (_typeOf.TryGetValue(signature, out var t)) return t;
            var types = _schema.TypesOf(signature);
            if (types.Count == 1) t = types[0];
            else if (types.Count > 1 && _schema.Model.Objects.TryGetValue(types[0], out var def) && def.Base is { Length: > 0 } shared) t = shared;
            return _typeOf[signature] = t;
        }

        /// <summary>Mutagen reads the record (without the cut subrecords) from the bytes given; the walk notes its links.</summary>
        void Walk(Plugin plugin, ReadOnlySpan<byte> head, ReadOnlySpan<byte> data, string type, ref long foreign)
        {
            int cut = 0;
            foreach (var (from, to) in _cuts) cut += to - from;
            int size = data.Length - cut;
            // A new array each time: Mutagen's object may keep slices of the bytes it was given
            var bytes = new byte[24 + size];
            head.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) & ~RawRecord.CompressedFlag);
            int at = 24, pos = 0;
            foreach (var (from, to) in _cuts)
            {
                data[pos..from].CopyTo(bytes.AsSpan(at)); at += from - pos; pos = to;
            }
            data[pos..].CopyTo(bytes.AsSpan(at));
            var record = plugin.Session.ReadBytes(type, bytes);
            RecordLinks.Walk(record, _sink);
            if (_sinkIds.Length < _sink.Paths.Count)
            {
                int old = _sinkIds.Length;
                Array.Resize(ref _sinkIds, Math.Max(_sink.Paths.Count, old * 2 + 16));
                for (int i = old; i < _sinkIds.Length; i++) _sinkIds[i] = -1;
            }
            foreach (var (field, target) in _sink.Links)
            {
                int owner = plugin.OwnerOf(target.ModKey.FileName.String);
                if (owner < 0) { foreign++; continue; }
                int id = _sinkIds[field];
                if (id < 0) _sinkIds[field] = id = _fieldId(_sink.Paths[field]);
                Add((ulong)owner << 24 | target.ID, id, 1);
            }
        }

        void Add(ulong target, int field, uint n)
        {
            var k = (target, field);
            _agg[k] = _agg.TryGetValue(k, out uint have) ? have + n : n;
        }

        static bool HasTop(ReadOnlySpan<byte> data, uint bulky)
        {
            int pos = 0; long longForm = -1; bool inComponent = false;
            while (pos + 6 <= data.Length)
            {
                uint s = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                long len = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 4)..]);
                if (longForm >= 0) { len = longForm; longForm = -1; }
                else if (s == Xxxx && len == 4 && pos + 10 <= data.Length) { longForm = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 6)..]); pos += 10; continue; }
                if (s == Bfcb) inComponent = true;
                if (s == bulky && !inComponent) return true;
                if (s == Bfce) inComponent = false;
                pos = (int)Math.Min(int.MaxValue, pos + 6 + len);
            }
            return false;
        }

        /// <summary>Reads each bulky subrecord of the record's own (outside components) and notes where it sits, so that
        /// Mutagen is given the record without it. False when one doesn't read to its end as Mutagen lays it out, or the
        /// subrecords can't be walked to the record's end.</summary>
        bool ReadBulky(ReadOnlySpan<byte> data, uint bulky, int formVersion, FormIdResolver ids)
        {
            int pos = 0; long longForm = -1; int longStart = -1; bool inComponent = false; int ordinal = 0;
            while (pos < data.Length)
            {
                if (pos + 6 > data.Length) return false;
                uint s = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                long len = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 4)..]);
                int headerAt = pos;
                if (longForm >= 0) { len = longForm; longForm = -1; headerAt = longStart; }
                else if (s == Xxxx && len == 4 && pos + 10 <= data.Length) { longForm = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 6)..]); longStart = pos; pos += 10; continue; }
                long start = pos + 6, end = start + len;
                if (end > data.Length) return false;
                if (s == Bfcb) inComponent = true;
                if (s == bulky && !inComponent)
                {
                    var c = data[(int)start..(int)end];
                    bool ok = bulky switch
                    {
                        Xtv2 => Traversals(c, ids),
                        Nvnm => Geometry(c, formVersion, ids),
                        Fnam => ordinal < 2 && Patterns(c, ordinal == 0 ? FPatterns : FPatterns2, ids),
                        Nvmi => MapInfo(c, ids),
                        _ => false,
                    };
                    if (!ok) return false;
                    ordinal++;
                    _cuts.Add((headerAt, (int)end));
                }
                if (s == Bfce) inComponent = false;
                pos = (int)end;
            }
            return longForm < 0;
        }

        // ---------------------------------------------------------------- The byte readers (SFSearch.Records\WorldBytes.cs)
        /// <summary>WorldBytes' field numbers as this reader's: the two kinds of navmesh parent are one path to the walk.</summary>
        static readonly int[] FromBytes = { FTraversal, FNavParent, FNavParent, FNavUnusedWorld, FNavEdge, FNavDoor, FPatterns, FPatterns2,
            FMiMesh, FMiMerged, FMiPreferred, FMiDoor, FMiTravCell, FMiTravTrav, FMiTravFrom, FMiTravTo, FMiWorld, FMiCell };

        /// <summary>Takes WorldBytes' links into the record's rows: a null link isn't listed (the walk doesn't list it).</summary>
        sealed class Sink(WorldReader reader) : IWorldLinkSink
        {
            public FormIdResolver Ids = null!;
            public void Link(int field, uint raw, uint count)
            {
                if (raw == 0) return; // a null link (Mutagen: a reference FormID of 0 is FormKey.Null), not listed by the walk
                reader.Add(Ids.Key(raw), reader._readerIds[FromBytes[field]], count);
            }
        }

        Sink? _bytesSink;

        bool Traversals(ReadOnlySpan<byte> c, FormIdResolver ids) => WorldBytes.Traversals(_schema.Model, c, SinkFor(ids));
        bool Geometry(ReadOnlySpan<byte> c, int formVersion, FormIdResolver ids) => WorldBytes.Geometry(_schema.Model, c, formVersion, SinkFor(ids));
        bool Patterns(ReadOnlySpan<byte> c, int field, FormIdResolver ids) => WorldBytes.PatternGrid(_schema.Model, c, field == FPatterns ? WorldBytes.Patterns : WorldBytes.Patterns2, SinkFor(ids));
        bool MapInfo(ReadOnlySpan<byte> c, FormIdResolver ids) => WorldBytes.MapInfo(_schema.Model, c, SinkFor(ids));

        Sink SinkFor(FormIdResolver ids)
        {
            _bytesSink ??= new Sink(this);
            _bytesSink.Ids = ids;
            return _bytesSink;
        }
    }
}
