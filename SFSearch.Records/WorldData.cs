// World data (cells and what they hold: placed references, navmeshes, terrain) is left out of some answers because
// reading it costs seconds. Every tool that leaves it out says so the same way, through this one sentence: what was
// left out, how much, and the exact argument that brings it in. (design\agent-first\notes\fix-tools.md, item B6.)
using System.Globalization;

namespace SFSearch.Records;

public static class WorldData
{
    /// <summary>The words for world data, the same in every answer.</summary>
    public const string What = "cells, placed references, navmeshes and terrain";

    /// <summary>
    /// "World data wasn't searched: 8,752,362 records (cells, placed references, navmeshes and terrain), 2.73 GB in
    /// 119 plugins. Add world: true (command line: --world) to include it: about 3 s more."
    /// </summary>
    /// <param name="leftOutOf">What it was left out of: "wasn't searched", "was left out of the link and ITM checks".</param>
    /// <param name="records">How many world records were left out.</param>
    /// <param name="bytes">Their size, or a negative number when it isn't known.</param>
    /// <param name="plugins">In how many plugins, or 0 for one plugin that the answer already names.</param>
    /// <param name="cost">What including them would cost ("about 3 s more"), or null.</param>
    /// <param name="byType">The largest types left out ("REFR 30, NAVM 4"), or null.</param>
    public static string LeftOut(string leftOutOf, long records, long bytes = -1, int plugins = 0, string? cost = null, string? byType = null)
    {
        string n = records.ToString("N0", CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        sb.Append("World data ").Append(leftOutOf).Append(": ").Append(n).Append(records == 1 ? " record (" : " records (").Append(byType ?? What).Append(')');
        if (bytes >= 0) sb.Append(", ").Append(Sizes.Text(bytes));
        if (plugins > 0) sb.Append(" in ").Append(plugins.ToString("N0", CultureInfo.InvariantCulture)).Append(plugins == 1 ? " plugin" : " plugins");
        sb.Append(". Add world: true (command line: --world) to include it");
        if (cost != null) sb.Append(": ").Append(cost);
        sb.Append('.');
        return sb.ToString();
    }
}
