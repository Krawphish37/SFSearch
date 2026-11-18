// A list view's column whose field is a plain value in some kinds of its entry and a list of plain values in others
// (ColumnSpec.ShapeVaries): a script property's Data, a number on ScriptIntProperty and a list of numbers on
// ScriptIntListProperty. The JSON gives a plain value as itself and a list as its JSON text ([0.0,0.1,0.3,0.5]), and
// so does the view. Such a column is read straight from Mutagen's object (DirectColumn) and from the record's own
// bytes (RawList's walked column) where every kind of the entry holds a plain value or a list of plain values: a
// yes/no, a whole number, a float, a text that isn't translated. The list's text is written by the writer the
// serializer writes it with (Newtonsoft's, no indenting; a float through the serializer's own JsonScalarText), so it
// is the JSON's text. sfsquery-tests varying-columns holds the column, read both ways, against each record's JSON.
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Plugins.Meta;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

static class VaryingColumn
{
    static readonly System.Reflection.Assembly Starfield = typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly;
    static readonly ConcurrentDictionary<ColumnSpec, bool> Checked = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether the column is one this file reads: its shape varies, and in every class of the list's entry that
    /// has the field it is a plain value or a list of plain values.</summary>
    public static bool Supported(ColumnSpec c) => Checked.GetOrAdd(c, Check);

    static bool Check(ColumnSpec c)
    {
        if (!c.ShapeVaries || c.RelPath is not { Length: > 0 } rel || rel.Contains('.')) return false;
        // The list's entry: the object the field's row stands in
        if ((c.Row ?? c.FirstRow)?.Parent?.Def.RefObject is not { } entry) return false;
        var baseType = Starfield.GetType("Mutagen.Bethesda.Starfield." + entry);
        if (baseType == null) return false;
        bool any = false;
        foreach (var t in Starfield.GetTypes())
        {
            if (!t.IsClass || t.IsAbstract || !t.IsPublic || t.IsGenericTypeDefinition || !baseType.IsAssignableFrom(t)) continue;
            if (DirectColumn.Find(t, rel) is not { } p) continue;
            var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (IsPlain(pt)) { any = true; continue; }
            var item = pt.GetInterfaces().Prepend(pt).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
            if (item == null || !IsPlain(item)) return false;
            any = true;
        }
        return any;
    }

    static bool IsPlain(Type t) => t == typeof(bool) || t == typeof(float) || t == typeof(string)
        || t == typeof(int) || t == typeof(uint) || t == typeof(short) || t == typeof(ushort) || t == typeof(sbyte) || t == typeof(byte);

    /// <summary>A list's value as the JSON gives it: its JSON text; null for a list with nothing in it, which the
    /// serializer leaves out.</summary>
    public static string? ArrayText(IEnumerable items)
    {
        using var sw = new StringWriter(CultureInfo.InvariantCulture);
        int n = 0;
        using (var jw = new Newtonsoft.Json.JsonTextWriter(sw) { Formatting = Newtonsoft.Json.Formatting.None })
        {
            jw.WriteStartArray();
            foreach (object? item in items)
            {
                n++;
                switch (item)
                {
                    case bool b: jw.WriteValue(b); break;
                    // (a float as the serializer's kernel writes it: 1 is 1.0, a NaN a text)
                    case float f: jw.WriteRawValue(Mutagen.Bethesda.Serialization.Newtonsoft.JsonScalarText.Format(f)); break;
                    case string s: jw.WriteValue(s); break;
                    case int i: jw.WriteValue(i); break;
                    case uint i: jw.WriteValue(i); break;
                    case short i: jw.WriteValue(i); break;
                    case ushort i: jw.WriteValue(i); break;
                    case sbyte i: jw.WriteValue(i); break;
                    case byte i: jw.WriteValue(i); break;
                    default: throw new InvalidOperationException($"a list column's item is a {item?.GetType().Name ?? "null"}, which isn't a plain value");
                }
            }
            jw.WriteEndArray();
        }
        return n == 0 ? null : sw.ToString();
    }

    /// <summary>One item of such a list from its bytes, as Mutagen reads it: by the item's kind, stored as one of
    /// Mutagen's hand-read layouts lays it out (<paramref name="laid"/>: U8, I32, F32, WString) or as the generated
    /// reader does (null). False when the bytes aren't that.</summary>
    public static bool TryItem(FieldDef item, string? laid, ReadOnlySpan<byte> b, out object value)
    {
        value = "";
        switch (item.Kind)
        {
            case "Bool" when b.Length == 1 && laid is null or "U8": value = b[0] != 0; return true;
            case "Int32" when b.Length == 4 && laid is null or "I32": value = BinaryPrimitives.ReadInt32LittleEndian(b); return true;
            case "UInt32" when b.Length == 4 && laid is null or "U32": value = BinaryPrimitives.ReadUInt32LittleEndian(b); return true;
            case "Float" when b.Length == 4 && laid is null or "F32": value = BinaryPrimitives.ReadSingleLittleEndian(b); return true;
            case "String" when !item.Translated:
            {
                var encoding = GameConstants.Starfield.Encodings.NonTranslated;
                // A text with its length before it (the layout's WString, the place holding the length too), or as the
                // field's own form has it
                if (laid == "WString") { if (b.Length < 2) return false; value = BinaryStringUtility.ProcessWholeToZString(b[2..], encoding); return true; }
                if (laid != null) return false;
                string form = item.TextForm ?? "NullTerminate";
                if (form is not ("NullTerminate" or "PrependLength" or "PrependLengthUShort" or "PrependLengthUInt8" or "Plain")) return false;
                int need = form switch { "PrependLength" => 4, "PrependLengthUShort" => 2, "PrependLengthUInt8" => 1, _ => 0 };
                if (b.Length < need) return false;
                value = RawPlugin.Decode(b, form, encoding);
                return true;
            }
            default: return false;
        }
    }
}
