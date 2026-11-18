// GAPS S191: the names of Wwise events by their GUID, from the sound banks' descriptions in the Data folder
// (sound\soundbanks\*.json in the archives, the game's soundbanksinfo.json among them).
using SFSearch.Records;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class WwiseTests
{
    const string SF = "Starfield.esm";

    public static string Run(Ctx c)
    {
        var events = WwiseEvents.For(c.Data);
        Check(events.Count > 10_000, $"only {events.Count:N0} events were read from {string.Join(", ", events.Sources)}");
        Check(events.Sources.Any(s => s.StartsWith("Starfield - WwiseSounds", StringComparison.OrdinalIgnoreCase)), $"the game's own archives aren't among the sources: {string.Join(", ", events.Sources)}");
        // The Eon's equip sound, as xEdit shows it
        Check(events.TryGet("{59A5EC16-836F-4D41-A2D7-FC1B91F934D7}", out var eon) && eon.Name == "Play_WPN_Pistol_Eon_Equip_Up"
            && eon.Path == @"\Events\Starfield_WPN\WPN_Hand\Pistol\Eon\Play_WPN_Pistol_Eon_Equip_Up", $"the Eon's equip event: {eon.Name} {eon.Path}");
        Check(!events.TryGet("{00000000-0000-0000-0000-000000000000}", out _) && !events.TryGet("no guid", out _) && !events.TryGet(null, out _), "an empty GUID, a text that is none and no text name no event");

        // In a query: by the column's own value, and by Wwise's form of it
        var a = c.Ok("SELECT wwise(Equip_StartEvent), wwise_name(Equip_StartEvent), wwise_path(Equip_StartEvent), wwise_name(wwise(Equip_StartEvent)), wwise_name(NULL), wwise_name('x') FROM Weapon WHERE FormKey = '0476C4:Starfield.esm'", SF);
        Check(a.Rows.Count == 1, "the Eon is a weapon of Starfield.esm");
        var r = a.Rows[0];
        Check((string?)r[0] == "{59A5EC16-836F-4D41-A2D7-FC1B91F934D7}" && (string?)r[1] == eon.Name && (string?)r[2] == eon.Path && (string?)r[3] == eon.Name && r[4] == null && r[5] == null,
            $"the Eon's equip event in a query: {string.Join(" | ", r.Select(x => x ?? "NULL"))}");

        // How many of the weapons' events of the game and of two mods have a name
        var said = new List<string>();
        foreach (string plugin in new[] { SF, "kgcdoom.esm", "sfta01.esm" })
        {
            if (c.Index.Plugin(plugin) == null) continue;
            var n = c.Ok("SELECT count(Equip_StartEvent), count(wwise_name(Equip_StartEvent)) FROM Weapon WHERE Equip_StartEvent IS NOT NULL AND wwise(Equip_StartEvent) <> '{00000000-0000-0000-0000-000000000000}'", plugin).Rows[0];
            long have = Convert.ToInt64(n[0]), named = Convert.ToInt64(n[1]);
            if (plugin == SF) Check(have > 100 && named * 10 >= have * 9, $"{SF}: {named:N0} of {have:N0} weapons' equip events have a name");
            said.Add($"{plugin} {named:N0} of {have:N0}");
        }

        // On the card: the name before the GUID and the path after it
        var card = c.Records.Card(c.Records.Load(c.Records.Find(SF, "0476C4:Starfield.esm")));
        string text = card.ToText(int.MaxValue, 64);
        Check(text.Contains("Play_WPN_Pistol_Eon_Equip_Up {59A5EC16-836F-4D41-A2D7-FC1B91F934D7} \"\\Events\\Starfield_WPN\\WPN_Hand\\Pistol\\Eon\\Play_WPN_Pistol_Eon_Equip_Up\"", StringComparison.Ordinal),
            "the Eon's card names its equip event: " + string.Join(" / ", text.Split('\n').Where(l => l.Contains("59A5EC16", StringComparison.Ordinal)).Select(l => l.Trim())));
        return $"S191: {events.Count:N0} Wwise events named by {events.Sources.Count:N0} files of the Data folder (read in {events.ReadSeconds:F1} s); the Eon's equip event by name and path, in a query (wwise_name, wwise_path) and on its card; weapons' equip events with a name: {string.Join(", ", said)}";
    }
}
