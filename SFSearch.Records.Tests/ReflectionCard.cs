// To-do F30: a reflection blob (REFL, RDIF, ...) is decoded on the card below its bytes, each field by the Creation
// Kit's name (every schema has them, from SFSearch.SchemaGen\inputs\ck-names-*.tsv), and the evidence view slices each
// decoded value out of its subrecord.
using System.Buffers.Binary;
using System.Globalization;
using SFSearch.Records;

namespace SFSearch.Records.Tests;

static class ReflectionCard
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    public static string Run(Ctx ctx)
    {
        var e = ctx.Engine;
        Check(ctx.Schema.Model.Ck.Present, "the schema has no Creation Kit names: every build reads them from SFSearch.SchemaGen\\inputs\\ck-names-*.tsv");
        var parts = new List<string>();

        // AtmoOrangePaleDense01 (0EC2B1:Starfield.esm): its RDIF is a diff of BGSAtmosphere with three FormIDs
        e.Sessions.Use("Starfield.esm");
        var atmo = e.Load("Starfield.esm", "0EC2B1:Starfield.esm");
        var card = e.Card(atmo);
        var holder = card.Fields.First(f => f.Name == "ReflectionDiff");
        var decoded = holder.Children!.FirstOrDefault(f => f.IsDecodedReflection);
        Check(decoded != null, "the card has no Decoded under ReflectionDiff");
        Check(decoded!.Variant!.StartsWith("BGSAtmosphere", StringComparison.Ordinal), $"Decoded says {decoded.Variant}");
        // The image space: a FormID inside the blob, labelled from the record's own links
        var day = e.Fields(atmo, new[] { "ReflectionDiff.Decoded.Settings.Misc.pImageSpaceDay" })[0];
        Check(day.IsSet && day.Text == "IS_Atmo_Orange_LUT_Day (13DAAF:Starfield.esm)", $"pImageSpaceDay is {day.Text}");
        var ev = e.Evidence(atmo, "ReflectionDiff.Decoded.Settings.Misc.pImageSpaceDay");
        Check(ev.Subrecord == "RDIF" && ev.Size == 4 && ev.Slices.Count == 1 && ev.Slices[0].FieldBytes == "AF DA 13 00" && ev.Slices[0].Reads == "IS_Atmo_Orange_LUT_Day (13DAAF:Starfield.esm)",
            $"the evidence gives {ev.Subrecord} offset {ev.Offset}, {ev.Size} bytes: {ev.Slices.FirstOrDefault()?.FieldBytes} → {ev.Slices.FirstOrDefault()?.Reads}");
        // Every decoded number of the blob is its slice of the subrecord, read back
        var raw = e.Raw(atmo.Ref);
        var blob = raw.Content(raw.Subrecords.First(s => s.Signature == "RDIF")).ToArray();
        int numbers = 0;
        foreach (var f in Leaves(decoded))
        {
            if (f.Position is not int pos || f.Size is not int size) continue;
            Check(pos + size <= blob.Length, $"{f.Path} sits past the end of the blob ({pos}+{size} of {blob.Length})");
            string? back = f.Kind switch
            {
                "Float" => BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(pos)).ToString("R", CultureInfo.InvariantCulture),
                "Int" when size == 4 => BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(pos)).ToString(CultureInfo.InvariantCulture),
                "Bool" => blob[pos] != 0 ? "true" : "false",
                _ => null,
            };
            if (back == null) continue;
            numbers++;
            Check(back == f.Text || f.Kind == "Int" && uint.TryParse(f.Text, out uint u) && unchecked((int)u).ToString(CultureInfo.InvariantCulture) == back, $"{f.Path} shows {f.Text}, its bytes at {pos} read {back}");
        }
        Check(numbers >= 8, $"only {numbers} decoded numbers were checked against the blob");
        string text = card.ToText();
        foreach (var want in new[] { "Decoded: BGSAtmosphere (CK: \"Atmosphere\")", "Image Space: Day (pImageSpaceDay): IS_Atmo_Orange_LUT_Day (13DAAF:Starfield.esm)", "Other (Misc): BGSAtmosphere::MiscSettings" })
            Check(text.Contains(want, StringComparison.Ordinal), $"the card lacks \"{want}\"");
        // The CK's name finds the field too
        var byCk = e.Fields(atmo, new[] { "ReflectionDiff.Decoded.Settings.Other.Image Space: Day" })[0];
        Check(byCk.IsSet && byCk.Path == "ReflectionDiff.Decoded.Settings.Misc.pImageSpaceDay", $"the CK's names lead to {byCk.Path}");
        parts.Add($"{atmo.Label}: RDIF decoded ({Leaves(decoded).Count()} values, {numbers} numbers read back from their bytes), pImageSpaceDay {day.Text} at RDIF offset {ev.Offset}");

        // DarkStar_Mk3_MagShear (0003C2:darkstar.esm): a component's REFL; the card still holds every JSON value
        e.Sessions.Use("darkstar.esm");
        var weap = e.Load("darkstar.esm", "0003C2:darkstar.esm");
        var json = e.Json(weap);
        var wcard = e.Card(weap, json);
        var refl = wcard.Fields.First(f => f.Name == "Components").Children![1].Children!.First(f => f.Name == "Reflection");
        var wdec = refl.Children!.FirstOrDefault(f => f.IsDecodedReflection);
        Check(wdec != null, "the weapon's EffectSequenceComponent.Reflection has no Decoded");
        var res = CardCheck.Check(json, wcard);
        Check(res.Problems.Count == 0, "the card no longer holds the JSON: " + string.Join("; ", res.Problems));
        parts.Add($"{weap.Label}: Components[1].Reflection decoded as {wdec!.Variant} ({Leaves(wdec).Count()} values); the card still holds all {res.Values} JSON values");
        parts.Add("the Creation Kit's names were checked");
        return string.Join("; ", parts) + ".";
    }

    static IEnumerable<CardField> Leaves(CardField f) => f.Children == null ? new[] { f } : f.Children.SelectMany(Leaves);
}
