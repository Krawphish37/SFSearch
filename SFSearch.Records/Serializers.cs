// Every record as Mutagen.Bethesda.Serialization's full-profile JSON, made in memory by the serializers the source
// generator writes into this library. Full profile: an ICustomize that changes nothing, so nothing is sorted and
// nothing is left out but values equal to their default.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Loqui;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Serialization;
using Mutagen.Bethesda.Serialization.Customizations;
using Mutagen.Bethesda.Serialization.Newtonsoft;
using Mutagen.Bethesda.Serialization.Streams;
using Mutagen.Bethesda.Starfield;

namespace SFSearch.Records;

/// <summary>The full profile: nothing is changed, so nothing is sorted and nothing is left out.</summary>
public class FullProfile : ICustomize
{
    public void Customize(ICustomizationBuilder builder) { }
}

/// <summary>Never called. Its one line makes the source generator write a serializer for every Starfield object.</summary>
static class SerializerTrigger
{
    static Task Unused(IStarfieldModGetter m) => MutagenJsonConverter.Instance.Serialize(m, "x");
}

/// <summary>One object to JSON and back, through the generated serializer of its own type.</summary>
public static class RecordSerializer
{
    static readonly Assembly Asm = typeof(FullProfile).Assembly;
    static readonly MutagenSerializationWriterKernel<NewtonsoftJsonSerializationWriterKernel, JsonWritingUnit> WriterKernel = new();
    static readonly NewtonsoftJsonSerializationReaderKernel ReaderKernel = new();
    static readonly SerializationMetaData Meta = new(GameRelease.Starfield, null!, null!, null!, CancellationToken.None);
    static readonly ConcurrentDictionary<string, (MethodInfo Ser, MethodInfo Des)> Methods = new(StringComparer.Ordinal);

    /// <summary>The Mutagen class name of an object (Weapon, PlacedObject), whether it is an overlay or a full object.</summary>
    public static string TypeName(object o) => ((ILoquiObject)o).Registration.ClassType.Name;

    /// <summary>True when the generator wrote a serializer for this Mutagen class.</summary>
    public static bool Knows(string typeName) => Asm.GetType($"Mutagen.Bethesda.Starfield.{typeName}_Serialization", false) != null;

    static (MethodInfo Ser, MethodInfo Des) For(string typeName) => Methods.GetOrAdd(typeName, n =>
    {
        var t = Asm.GetType($"Mutagen.Bethesda.Starfield.{n}_Serialization", false)
            ?? throw new RecordException($"There is no serializer for the Mutagen type {n}.");
        var s = t.GetMethods().First(m => m.Name == "Serialize" && m.GetGenericArguments().Length == 2)
            .MakeGenericMethod(typeof(NewtonsoftJsonSerializationWriterKernel), typeof(JsonWritingUnit));
        var d = t.GetMethods().First(m => m.Name == "Deserialize" && m.GetGenericArguments().Length == 1)
            .MakeGenericMethod(typeof(JsonReadingUnit));
        return (s, d);
    });

    /// <summary>The object's JSON as the serializer writes it (indented, fields in Mutagen's order).</summary>
    public static string ToJson(object o, string? typeName = null) => Encoding.UTF8.GetString(ToUtf8(o, typeName));

    /// <summary>The object's JSON, parsed. The text is never held as a string of its own.</summary>
    public static System.Text.Json.Nodes.JsonObject ToJsonObject(object o, string? typeName = null) =>
        (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(ToUtf8(o, typeName))!;

    /// <summary>
    /// A thread's own JSON writer and the memory it writes into, kept from one object to the next. The serializer
    /// package makes a new writer for every object (<c>GetNewObject</c>), and a new writer is 5 KB of buffers: for
    /// a placed object whose JSON is 400 bytes that was most of what the garbage collector had to clear away.
    /// What is written is the same: <c>GetNewObject</c> is a new writer and the object's opening brace,
    /// <c>Finalize</c> is the closing brace and the writer's end, which here is a flush.
    /// </summary>
    sealed class Scribe
    {
        public readonly MemoryStream Stream = new(4096);
        public readonly JsonWritingUnit Unit;
        public Scribe() { Unit = new JsonWritingUnit(new StreamPackage(Stream, null)); }
    }

    [ThreadStatic] static Scribe? _scribe;
    /// <summary>Memory that one large record made larger than this isn't kept.</summary>
    const int KeepBytes = 1 << 20;

    /// <summary>
    /// The object's JSON as UTF-8: the bytes <see cref="ToJson"/> gives as text. The memory is the calling thread's
    /// own, and its next call here writes over it: use it, or copy it, before that.
    /// </summary>
    public static ReadOnlySpan<byte> ToUtf8(object o, string? typeName = null)
    {
        // Taken out while it is in use: a call inside a call gets a writer of its own, and a writer that an error
        // left in the middle of an object is never used again
        var scribe = _scribe ?? new Scribe();
        _scribe = null;
        scribe.Stream.SetLength(0);
        var w = scribe.Unit;
        w.Writer.WriteStartObject();
        Serialize(w, o, typeName);
        w.Writer.WriteEndObject();
        w.Writer.Flush();
        int length = (int)scribe.Stream.Length;
        if (scribe.Stream.Capacity <= KeepBytes) _scribe = scribe;
        return new ReadOnlySpan<byte>(scribe.Stream.GetBuffer(), 0, length);
    }

    /// <summary>The same JSON through a new writer, the way the serializer package itself starts and ends an object.
    /// The tests hold <see cref="ToUtf8"/> against it.</summary>
    internal static byte[] ToUtf8WithNewWriter(object o, string? typeName = null)
    {
        using var ms = new MemoryStream();
        var pkg = new StreamPackage(ms, null);
        var w = WriterKernel.GetNewObject(pkg);
        Serialize(w, o, typeName);
        WriterKernel.Finalize(pkg, w);
        return ms.ToArray();
    }

    static void Serialize(JsonWritingUnit w, object o, string? typeName)
    {
        try { ((Task)For(typeName ?? TypeName(o)).Ser.Invoke(null, new object[] { w, o, WriterKernel, Meta })!).GetAwaiter().GetResult(); }
        catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
    }

    /// <summary>JSON back to a full Mutagen object of the named class.</summary>
    public static object FromJson(string json, string typeName)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var r = ReaderKernel.GetNewObject(new StreamPackage(ms, null));
        Task task;
        try { task = (Task)For(typeName).Des.Invoke(null, new object[] { r, ReaderKernel, Meta })!; }
        catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
}

/// <summary>Something about a record or a plugin that the caller must be told in one line.</summary>
public sealed class RecordException(string message) : Exception(message);
