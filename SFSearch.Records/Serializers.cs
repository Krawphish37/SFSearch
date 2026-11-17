// Every record as Mutagen.Bethesda.Serialization's full-profile JSON, made in memory by the serializers the source
// generator writes into this library. Full profile: an ICustomize that changes nothing, so nothing is sorted and
// nothing is left out but values equal to their default.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Serialization;
using Mutagen.Bethesda.Serialization.Customizations;
using Mutagen.Bethesda.Serialization.Newtonsoft;
using Mutagen.Bethesda.Starfield;

namespace SFSearch.Records;

/// <summary>The full profile: nothing is changed, so nothing is sorted and nothing is left out.</summary>
public class FullProfile : ICustomize
{
    public void Customize(ICustomizationBuilder builder) { }
}

/// <summary>Never called. Its one line makes the source generator write a serializer for every Starfield object, and
/// the registry of them (StarfieldSerializationRegistry).</summary>
static class SerializerTrigger
{
    static Task Unused(IStarfieldModGetter m) => MutagenJsonConverter.Instance.Serialize(m, "x");
}

/// <summary>
/// One object to JSON and back, through the generated serializer of its own class: the generated registry
/// (<c>StarfieldSerializationRegistry</c>, by runtime type, no reflection) and its JSON converter, one per thread, which
/// keeps its writer and buffer from one object to the next (Mutagen.Bethesda.Serialization 1.38.6-fixes.5, C59 W11 and
/// V4).
/// </summary>
public static class RecordSerializer
{
    /// <summary>The serializers the source generator wrote into this library, by class.</summary>
    public static ISerializationRegistry Registry => StarfieldSerializationRegistry.Instance;

    static readonly SerializationMetaData Meta = new(GameRelease.Starfield, null!, null!, null!, CancellationToken.None);

    [ThreadStatic] static JsonObjectConverter? _converter;
    /// <summary>The calling thread's converter: the JSON <see cref="ToUtf8"/> gives is its buffer.</summary>
    static JsonObjectConverter Converter => _converter ??= new JsonObjectConverter(Registry, Meta);

    /// <summary>The Mutagen class name of an object (Weapon, PlacedObject), whether it is an overlay or a full object.</summary>
    public static string TypeName(object o) => SerializationRegistry.ClassTypeOf(o).Name;

    /// <summary>True when the generator wrote a serializer for this Mutagen class.</summary>
    public static bool Knows(string typeName) => Registry.TryGetClassType(typeName, out _);

    /// <summary>The Mutagen class of that name the registry has, or null.</summary>
    public static Type? ClassOf(string typeName) => Registry.TryGetClassType(typeName, out var t) ? t : null;

    /// <summary>The object's JSON as the serializer writes it (indented, fields in Mutagen's order).</summary>
    public static string ToJson(object o) => Converter.ToJson(o);

    /// <summary>The object's JSON, parsed. The text is never held as a string of its own.</summary>
    public static System.Text.Json.Nodes.JsonObject ToJsonObject(object o) =>
        (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(ToUtf8(o))!;

    /// <summary>
    /// The object's JSON as UTF-8: the bytes <see cref="ToJson"/> gives as text. The memory is the calling thread's
    /// converter's, and its next call here writes over it: use it, or copy it, before that.
    /// </summary>
    public static ReadOnlySpan<byte> ToUtf8(object o) => Converter.ToUtf8(o).Span;

    /// <summary>JSON back to a full Mutagen object of the named class.</summary>
    public static object FromJson(string json, string typeName)
        => Converter.FromJson(json, ClassOf(typeName) ?? throw new RecordException($"There is no serializer for the Mutagen type {typeName}."));
}

/// <summary>Something about a record or a plugin that the caller must be told in one line.</summary>
public sealed class RecordException(string message) : Exception(message);
