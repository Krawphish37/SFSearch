// The Plugins view: every plugin's header (TES4) in one table, read from each file's header bytes (PluginHeader), which
// the header index already holds. No record is read and Mutagen isn't asked: the header is the file's first record.
// (GAPS S91, fix J65.)
using System.Globalization;
using System.Text.Json;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    /// <summary>The Plugins view's columns, in order.</summary>
    internal static readonly string[] PluginColumns =
    {
        "Plugin", "LoadOrder", "Slot", "Size", "Flags", "FlagNames", "FormVersion", "Version", "NumRecords", "NextFormID",
        "MasterCount", "Masters", "Author", "Description", "OverriddenCount", "InteriorCellCount", "Subrecords", "HeaderSize", "FileSize",
    };

    /// <summary>
    /// A header's flags by name: Mutagen's names for the bits it knows (StarfieldModHeader.HeaderFlag: Master,
    /// Localized, Light, Medium, Blueprint, ...; Blueprint and Optimized since fixes.18, C48), and any other bit as its number.
    /// </summary>
    internal static string FlagNames(uint flags)
    {
        var names = new List<string>();
        uint left = flags;
        foreach (var f in Enum.GetValues<StarfieldModHeader.HeaderFlag>())
        {
            uint bit = (uint)f;
            if (bit == 0 || (bit & (bit - 1)) != 0 || (flags & bit) == 0) continue;
            names.Add(f.ToString());
            left &= ~bit;
        }
        for (int b = 0; b < 32; b++)
            if ((left & (1u << b)) != 0) names.Add("0x" + (1u << b).ToString("X", CultureInfo.InvariantCulture));
        return string.Join(", ", names);
    }

    /// <summary>The masters as JSON with their names as they are: an accent or an ampersand isn't written as an escape.</summary>
    static readonly JsonSerializerOptions MastersJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    sealed partial class Session
    {
        bool _pluginRowsMade;

        /// <summary>The temporary table behind the Plugins view: one row for each plugin in Data, made once a query.</summary>
        void MakePluginRows()
        {
            if (_pluginRowsMade) return;
            _pluginRowsMade = true;
            ScratchFile.Exec(_con, "CREATE TEMP TABLE sfs_plugins (Plugin TEXT, LoadOrder INTEGER, Slot TEXT, Size TEXT, Flags INTEGER, FlagNames TEXT, FormVersion INTEGER, "
                + "Version REAL, NumRecords INTEGER, NextFormID INTEGER, MasterCount INTEGER, Masters TEXT, Author TEXT, Description TEXT, OverriddenCount INTEGER, "
                + "InteriorCellCount INTEGER, Subrecords TEXT, HeaderSize INTEGER, FileSize INTEGER)");
            using var ins = new BulkInsert(_con, $"INSERT INTO temp.sfs_plugins VALUES ({string.Join(", ", PluginColumns.Select((_, i) => "?" + (i + 1)))})");
            foreach (var p in Index.Plugins.OrderBy(p => p.Order.Enabled ? p.Order.Position : int.MaxValue).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var h = p.Header;
                ins.Text(1, p.Name);
                ins.Int(2, p.Order.Enabled ? p.Order.Position : null);
                ins.Text(3, p.Order.Enabled && p.Order.Slot.Length > 0 ? p.Order.Slot : null);
                if (h == null)
                    for (int i = 4; i <= 18; i++) ins.Null(i);
                else
                {
                    ins.Text(4, h.Style switch { MasterStyle.Small => "Light", MasterStyle.Medium => "Medium", _ => "Full" });
                    ins.Int(5, h.Flags);
                    ins.Text(6, FlagNames(h.Flags));
                    ins.Int(7, h.FormVersion);
                    if (h.Version is float v) ins.Value(8, double.Parse(v.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)); else ins.Null(8);
                    ins.Int(9, h.NumRecords);
                    ins.Int(10, h.NextObjectId);
                    ins.Int(11, h.Masters.Count);
                    ins.Text(12, JsonSerializer.Serialize(h.Masters, MastersJson));
                    ins.Text(13, h.Author);
                    ins.Text(14, h.Description);
                    ins.Int(15, h.OverriddenForms);
                    ins.Int(16, h.InteriorCellCount);
                    ins.Text(17, string.Join(" ", h.Subrecords));
                    ins.Int(18, h.DataSize);
                }
                ins.Int(19, p.Stamp.Length);
                ins.Step();
            }
        }
    }
}
