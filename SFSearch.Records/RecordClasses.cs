// Which record type a record is, where several types share one signature (GAPS S193): a game setting's five types
// (GMST) and an object modification's eight (OMOD). Mutagen's reader decides it before it reads the record; the same
// decision is made here from the record's own bytes, by Mutagen's own rule:
//   - GMST: the first letter of the EditorID, through Mutagen's GameSettingUtility.TryGetGameSettingType (what
//     GameSetting.CreateFromBinary calls);
//   - any other: the record type's hand-read layout (HandReadLayouts.xml), where a Switch at the record's level names
//     the class each value picks (OMOD: the target type's name in DATA).
// A record the bytes don't decide (no EditorID, no DATA, a layout that doesn't follow the bytes) gives null, and
// Mutagen's read says what it is. sfsquery-tests typed-all and lists-all hold every decision against the class Mutagen
// made, for every record of every such signature.
using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

public static class RecordClasses
{
    static readonly ConcurrentDictionary<(RecordSchema, string), Func<RawRecord, string?>?> Made = new();

    /// <summary>
    /// For a signature several record types share: the type a record of it is, from its bytes (null: the bytes don't
    /// say, Mutagen's read does). Null for a signature with one type, and for one no rule is known for.
    /// </summary>
    public static Func<RawRecord, string?>? For(RecordSchema schema, string signature) => Made.GetOrAdd((schema, signature), k => Make(k.Item1, k.Item2));

    static Func<RawRecord, string?>? Make(RecordSchema schema, string sig)
    {
        var types = schema.TypesOf(sig);
        if (types.Count < 2) return null;
        var known = types.ToHashSet(StringComparer.Ordinal);
        if (sig == "GMST")
        {
            return raw =>
            {
                foreach (var s in raw.Subrecords)
                {
                    if (s.Signature != "EDID") continue;
                    var edid = raw.Content(s);
                    // (a letter of the code page Mutagen reads the EditorID in is its own byte; any other byte Mutagen decides)
                    if (edid.Length == 0 || edid[0] >= 0x80 || !GameSettingUtility.TryGetGameSettingType((char)edid[0], out var type)) return null;
                    string name = "GameSetting" + type;
                    return known.Contains(name) ? name : null;
                }
                return null;
            };
        }
        var model = schema.Model;
        var layout = model.HandLayouts.FirstOrDefault(h => h.Kind == "Record" && h.Sig == sig);
        // A Switch whose cases name classes, not inside an object or a list of the record: it picks the record's own class
        if (layout == null || !layout.Element.Descendants("Switch").Any(sw =>
                sw.Elements().Any(c => c.Attribute("type") != null) && !sw.Ancestors().Any(a => a.Name.LocalName is "Object" or "Repeat" or "Segments" or "SubRepeat")))
            return null;
        return raw =>
        {
            var walk = HandLayoutWalk.RunBytes(model, raw, false, _ => null, h => ReferenceEquals(h, layout));
            if (walk.Problems.Count > 0) return null;
            return walk.Marks.TryGetValue("", out var cls) && known.Contains(cls) ? cls : null;
        };
    }
}
