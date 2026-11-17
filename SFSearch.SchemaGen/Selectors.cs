// The kind of an abstract object that a selector value picks, as Mutagen's XML says it (selectedBy on the object or
// field, selectorValue on each kind, "*" for the kind that takes any other value). A component's kind is picked by its
// BFCB text: since fixes.20 Mutagen's reader takes a BFCB only when it is exactly a ComponentType member's name and its
// zero, and reads anything else as UnknownComponent (C63, C57 W4), so the pick is this plain lookup (WORKAROUNDS W4).
using System.Runtime.CompilerServices;

namespace SFSearch.SchemaGen;

public static class Selectors
{
    /// <summary>The base of every component (BFCB ... BFCE).</summary>
    public const string Component = "AComponent";

    static readonly ConditionalWeakTable<SchemaModel, Dictionary<string, (Dictionary<string, string> ByValue, string? Other)>> Tables = new();

    /// <summary>The kind of <paramref name="baseObj"/> whose selectorValue is <paramref name="value"/>, else the kind with
    /// selectorValue "*" (null value included); null when neither.</summary>
    public static string? KindOf(SchemaModel model, string baseObj, string? value)
    {
        var all = Tables.GetValue(model, _ => new Dictionary<string, (Dictionary<string, string>, string?)>(StringComparer.Ordinal));
        (Dictionary<string, string> ByValue, string? Other) t;
        lock (all)
        {
            if (!all.TryGetValue(baseObj, out t))
            {
                var by = new Dictionary<string, string>(StringComparer.Ordinal);
                string? other = null;
                foreach (var k in model.Variants(baseObj))
                    if (model.Objects[k].SelectorValue is { } v)
                    {
                        if (v == "*") other = k;
                        else foreach (var one in v.Split(',')) by.TryAdd(one.Trim(), k);
                    }
                all[baseObj] = t = (by, other);
            }
        }
        return value != null && t.ByValue.TryGetValue(value, out var kind) ? kind : t.Other;
    }

    /// <summary>The kind of component a BFCB's content picks: its text when the content is exactly the text and one zero
    /// byte at its end (C63), looked up as a selector value; any other content picks the "*" kind (UnknownComponent).</summary>
    public static string? ComponentKind(SchemaModel model, ReadOnlySpan<byte> bfcb)
    {
        int zero = bfcb.IndexOf((byte)0);
        string? text = zero >= 0 && zero == bfcb.Length - 1 ? System.Text.Encoding.Latin1.GetString(bfcb[..zero]) : null;
        return KindOf(model, Component, text);
    }
}
