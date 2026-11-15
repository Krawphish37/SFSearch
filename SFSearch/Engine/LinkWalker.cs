using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Serialization;
using Mutagen.Bethesda.Serialization.Streams;
using Mutagen.Bethesda.Strings;
using Noggog;
using SFSearch.Records;

namespace SFSearch.Engine
{
    /// <summary>
    /// Every link of one record, with the field it sits in (design\agent-first\ARCHITECTURE.md section 5, "Links").
    /// The record is walked by the serializer that writes its JSON (the one generated into SFSearch.Records), with a
    /// writer that keeps no text: it follows the names, lists and objects the JSON would have, and notes each FormKey
    /// with its path. So a link's field is exactly its path in the record's JSON (<c>Keywords[3]</c>, or
    /// <c>Keywords[*]</c> with the index left out), and nothing else of the record is formatted: no number, no text,
    /// no translated string is looked up. The link index, the confirmation of a byte search and "what does this record
    /// point at" all use this one walk.
    /// A record nested in another (a placed reference in a cell, a topic in a quest) is a record of its own: its links
    /// are its own, never its holder's.
    /// </summary>
    public sealed class LinkSink : IContainStreamPackage
    {
        static readonly StreamPackage NoStream = new(Stream.Null, null);
        public StreamPackage StreamPackage => NoStream;

        struct Frame { public int RestoreLen, NameLen, Count, LinksAt; public bool List, Child; }

        char[] _buf = new char[256];
        int _len;
        Frame[] _frames = new Frame[32];
        int _depth;
        string? _pending;
        readonly Dictionary<string, int> _fieldIds = new(StringComparer.Ordinal);
        readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _lookup;

        /// <summary>True: a list item's path carries its index (<c>Keywords[3]</c>). False: <c>Keywords[*]</c>, so the
        /// set of paths stays small.</summary>
        public bool Indexed { get; }
        /// <summary>Also note the record's in-game name (its top-level <c>Name</c>, in the language the plugin was opened with).</summary>
        public bool WantName { get; init; }

        /// <summary>Every path met so far, by number. It only grows: the numbers stay good over many records.</summary>
        public List<string> Fields { get; } = new();
        /// <summary>The links of the record walked last: the path's number and the target. Null links aren't listed.</summary>
        public List<(int Field, FormKey Target)> Links { get; } = new();
        public string? Name { get; private set; }

        public LinkSink(bool indexed = false)
        {
            Indexed = indexed;
            _lookup = _fieldIds.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public void Reset()
        {
            _len = 0; _depth = 0; _pending = null; Name = null;
            Links.Clear();
        }

        // ---------------------------------------------------------------- The path
        void Append(ReadOnlySpan<char> s)
        {
            if (_len + s.Length > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + s.Length));
            s.CopyTo(_buf.AsSpan(_len));
            _len += s.Length;
        }

        void AppendName(string name)
        {
            if (_len > 0) Append(".");
            Append(name);
        }

        string? Take(string? fieldName)
        {
            string? n = fieldName ?? _pending;
            _pending = null;
            return n;
        }

        bool InList => _depth > 0 && _frames[_depth - 1].List;

        /// <summary>The next item of the list on top: its path is the list's with the item's place.</summary>
        void BeginItem()
        {
            ref var f = ref _frames[_depth - 1];
            _len = f.NameLen;
            if (Indexed)
            {
                Span<char> digits = stackalloc char[12];
                f.Count.TryFormat(digits, out int n, default, CultureInfo.InvariantCulture);
                Append("["); Append(digits[..n]); Append("]");
            }
            else Append("[*]");
            f.Count++;
        }

        void Push(Frame f)
        {
            if (_depth == _frames.Length) Array.Resize(ref _frames, _frames.Length * 2);
            _frames[_depth++] = f;
        }

        // ---------------------------------------------------------------- What the kernel calls
        /// <summary>The name the next value or object is written under (the serializer gives it apart from the value).</summary>
        public void Pending(string? fieldName) { if (fieldName != null) _pending = fieldName; }

        /// <summary>A value that is no link: only its place counts (it may be an item of a list).</summary>
        public string? Value(string? fieldName)
        {
            string? name = Take(fieldName);
            if (name == null && InList) BeginItem();
            return name;
        }

        public void Text(string? fieldName, string? text)
        {
            string? name = Value(fieldName);
            if (WantName && _depth == 0 && name == "Name" && !string.IsNullOrEmpty(text)) Name = text;
        }

        public void Link(string? fieldName, FormKey? item)
        {
            string? name = Take(fieldName);
            if (name == null && InList) BeginItem();
            if (name == "FormKey")
            {
                // A record's own FormKey. Below the top it marks a record nested in this one: what it holds is its own.
                for (int d = _depth - 1; d >= 0; d--)
                    if (!_frames[d].List) { _frames[d].Child = true; break; }
                return;
            }
            if (item == null || item.Value.IsNull) return;
            int keep = _len;
            if (name != null) AppendName(name);
            var path = _buf.AsSpan(0, _len);
            if (!_lookup.TryGetValue(path, out int id))
            {
                string s = new string(path);
                id = Fields.Count;
                Fields.Add(s);
                _fieldIds[s] = id;
            }
            Links.Add((id, item.Value));
            _len = keep;
        }

        public void EnterObject(string? fieldName)
        {
            string? name = Take(fieldName);
            if (name == null && InList) BeginItem();
            int restore = _len;
            if (name != null) AppendName(name);
            Push(new Frame { RestoreLen = restore, LinksAt = Links.Count });
        }

        public void LeaveObject()
        {
            ref var f = ref _frames[--_depth];
            if (f.Child && Links.Count > f.LinksAt) Links.RemoveRange(f.LinksAt, Links.Count - f.LinksAt);
            _len = f.RestoreLen;
            _pending = null;
        }

        public void EnterList(string? fieldName)
        {
            string? name = Take(fieldName);
            if (name == null && InList) BeginItem();
            int restore = _len;
            if (name != null) AppendName(name);
            Push(new Frame { RestoreLen = restore, NameLen = _len, List = true });
        }

        public void LeaveList()
        {
            _len = _frames[--_depth].RestoreLen;
            _pending = null;
        }
    }

    /// <summary>The serializer's writer that writes nothing: it tells the <see cref="LinkSink"/> where it is.</summary>
    public sealed class LinkKernel : ISerializationWriterKernel<LinkSink>
    {
        public string ExpectedExtension => ".links";
        public LinkSink GetNewObject(StreamPackage stream) => new();
        public void Finalize(StreamPackage stream, LinkSink writer) { }
        public Exception ConvertException(Exception ex) => ex;

        public void WriteType(LinkSink writer, Type type) => writer.Value("MutagenObjectType");
        public void WriteChar(LinkSink writer, string? fieldName, char? item) => writer.Value(fieldName);
        public void WriteBool(LinkSink writer, string? fieldName, bool? item) => writer.Value(fieldName);
        public void WriteString(LinkSink writer, string? fieldName, string? item) => writer.Text(fieldName, item);
        public void WriteInt8(LinkSink writer, string? fieldName, sbyte? item) => writer.Value(fieldName);
        public void WriteInt16(LinkSink writer, string? fieldName, short? item) => writer.Value(fieldName);
        public void WriteInt32(LinkSink writer, string? fieldName, int? item) => writer.Value(fieldName);
        public void WriteInt64(LinkSink writer, string? fieldName, long? item) => writer.Value(fieldName);
        public void WriteUInt8(LinkSink writer, string? fieldName, byte? item) => writer.Value(fieldName);
        public void WriteUInt16(LinkSink writer, string? fieldName, ushort? item) => writer.Value(fieldName);
        public void WriteUInt32(LinkSink writer, string? fieldName, uint? item) => writer.Value(fieldName);
        public void WriteUInt64(LinkSink writer, string? fieldName, ulong? item) => writer.Value(fieldName);
        public void WriteFloat(LinkSink writer, string? fieldName, float? item) => writer.Value(fieldName);
        public void WriteDouble(LinkSink writer, string? fieldName, double? item) => writer.Value(fieldName);
        public void WriteHalf(LinkSink writer, string? fieldName, Half? item) => writer.Value(fieldName);
        public void WriteModKey(LinkSink writer, string? fieldName, ModKey? item) => writer.Value(fieldName);
        public void WriteFormKey(LinkSink writer, string? fieldName, FormKey? item) => writer.Link(fieldName, item);
        public void WriteRecordType(LinkSink writer, string? fieldName, RecordType? item) => writer.Value(fieldName);
        public void WriteP2Int(LinkSink writer, string? fieldName, P2Int? item) => writer.Value(fieldName);
        public void WriteP2Int16(LinkSink writer, string? fieldName, P2Int16? item) => writer.Value(fieldName);
        public void WriteP2UInt8(LinkSink writer, string? fieldName, P2UInt8? item) => writer.Value(fieldName);
        public void WriteP2Float(LinkSink writer, string? fieldName, P2Float? item) => writer.Value(fieldName);
        public void WriteP3Float(LinkSink writer, string? fieldName, P3Float? item) => writer.Value(fieldName);
        public void WriteP3UInt8(LinkSink writer, string? fieldName, P3UInt8? item) => writer.Value(fieldName);
        public void WriteP3Int16(LinkSink writer, string? fieldName, P3Int16? item) => writer.Value(fieldName);
        public void WriteP3UInt16(LinkSink writer, string? fieldName, P3UInt16? item) => writer.Value(fieldName);
        public void WritePercent(LinkSink writer, string? fieldName, Percent? item) => writer.Value(fieldName);
        public void WriteColor(LinkSink writer, string? fieldName, Color? item) => writer.Value(fieldName);
        public void WriteTimeOnly(LinkSink writer, string? fieldName, TimeOnly? item) => writer.Value(fieldName);
        public void WriteDateOnly(LinkSink writer, string? fieldName, DateOnly? item) => writer.Value(fieldName);
        public void WriteGuid(LinkSink writer, string? fieldName, Guid? item) => writer.Value(fieldName);
        public void WriteBytes(LinkSink writer, string? fieldName, ReadOnlyMemorySlice<byte>? item) => writer.Value(fieldName);
        public void WriteEnum<TEnum>(LinkSink writer, string? fieldName, TEnum? item) where TEnum : struct, Enum, IConvertible => writer.Value(fieldName);

        public void WriteTranslatedString(LinkSink writer, string? fieldName, ITranslatedStringGetter? item)
        {
            // Only the record's own name is ever looked up, and only in the language the plugin was opened with
            if (writer.WantName && fieldName == "Name") writer.Text(fieldName, item?.String);
            else writer.Value(fieldName);
        }

        static void Wait(Task t)
        {
            if (!t.IsCompletedSuccessfully) t.GetAwaiter().GetResult();
        }

        public void WriteWithName<TKernel, TObject>(MutagenSerializationWriterKernel<TKernel, LinkSink> kernel, LinkSink writer, string? fieldName, TObject item,
            SerializationMetaData serializationMetaData, WriteAsync<TKernel, LinkSink, TObject> writeCall) where TKernel : ISerializationWriterKernel<LinkSink>, new()
        {
            writer.Pending(fieldName);
            Wait(writeCall(writer, item, kernel, serializationMetaData));
        }

        public void WriteLoqui<TKernel, TObject>(MutagenSerializationWriterKernel<TKernel, LinkSink> kernel, LinkSink writer, string? fieldName, TObject item,
            SerializationMetaData serializationMetaData, Write<TKernel, LinkSink, TObject> writeCall) where TKernel : ISerializationWriterKernel<LinkSink>, new()
        {
            writer.EnterObject(fieldName);
            writeCall(writer, item, kernel, serializationMetaData);
            writer.LeaveObject();
        }

        public Task WriteLoqui<TKernel, TObject>(MutagenSerializationWriterKernel<TKernel, LinkSink> kernel, LinkSink writer, string? fieldName, TObject item,
            SerializationMetaData serializationMetaData, WriteAsync<TKernel, LinkSink, TObject> writeCall) where TKernel : ISerializationWriterKernel<LinkSink>, new()
        {
            writer.EnterObject(fieldName);
            Wait(writeCall(writer, item, kernel, serializationMetaData));
            writer.LeaveObject();
            return Task.CompletedTask;
        }

        public void StartListSection(LinkSink writer, string? fieldName) => writer.EnterList(fieldName);
        public void EndListSection(LinkSink writer) => writer.LeaveList();

        public void StartDictionarySection(LinkSink writer, string? fieldName) => writer.EnterList(fieldName);
        public void EndDictionarySection(LinkSink writer) => writer.LeaveList();
        public void StartDictionaryItem(LinkSink writer) => writer.EnterObject(null);
        public void EndDictionaryItem(LinkSink writer) => writer.LeaveObject();
        public void StartDictionaryKey(LinkSink writer) => writer.Pending("Key");
        public void EndDictionaryKey(LinkSink writer) { }
        public void StartDictionaryValue(LinkSink writer) => writer.Pending("Value");
        public void EndDictionaryValue(LinkSink writer) { }

        public void StartArray2dSection(LinkSink writer, string? fieldName) => writer.EnterList(fieldName);
        public void EndArray2dSection(LinkSink writer) => writer.LeaveList();
        public void StartArray2dXItem(LinkSink writer) { }
        public void EndArray2dXItem(LinkSink writer) { }
        public void StartArray2dYSection(LinkSink writer) => writer.EnterList(null);
        public void EndArray2dYSection(LinkSink writer) => writer.LeaveList();
    }

    public static class RecordLinks
    {
        static readonly MutagenSerializationWriterKernel<LinkKernel, LinkSink> Kernel = new();
        static readonly SerializationMetaData Meta = new(GameRelease.Starfield, null!, null!, null!, CancellationToken.None);
        static readonly ConcurrentDictionary<string, MethodInfo> Methods = new(StringComparer.Ordinal);

        static MethodInfo For(string typeName) => Methods.GetOrAdd(typeName, n =>
        {
            var t = typeof(FullProfile).Assembly.GetType($"Mutagen.Bethesda.Starfield.{n}_Serialization", false)
                ?? throw new RecordException($"There is no serializer for the Mutagen type {n}.");
            return t.GetMethods().First(m => m.Name == "Serialize" && m.GetGenericArguments().Length == 2).MakeGenericMethod(typeof(LinkKernel), typeof(LinkSink));
        });

        /// <summary>Walks one record read through Mutagen. Afterwards the sink holds its links (and its name, when asked for).</summary>
        public static void Walk(object record, string typeName, LinkSink sink)
        {
            sink.Reset();
            try { ((Task)For(typeName).Invoke(null, new[] { sink, record, Kernel, Meta })!).GetAwaiter().GetResult(); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
        }

        /// <summary>A path with its list indexes left out: <c>Keywords[3]</c> as <c>Keywords[*]</c>.</summary>
        public static string Plain(string path)
        {
            if (path.IndexOf('[') < 0) return path;
            var sb = new System.Text.StringBuilder(path.Length);
            for (int i = 0; i < path.Length; i++)
            {
                sb.Append(path[i]);
                if (path[i] != '[') continue;
                int close = path.IndexOf(']', i);
                if (close < 0) continue;
                sb.Append('*').Append(']');
                i = close;
            }
            return sb.ToString();
        }
    }
}
