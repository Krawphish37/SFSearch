using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SFSearch.Index;

namespace SFSearch.Engine
{
    /// <summary>One record of one plugin, as the record and refs tools were asked for it.</summary>
    public sealed class Picked
    {
        /// <summary>The index the plugin is in: the engine's own, or the side index of a plugin file outside Data.</summary>
        public required HeaderIndex Ix;
        public required IndexedPlugin Plugin;
        public required RecordRef Ref;
        public required RecordInfo Info;
        /// <summary>A plugin file outside Data, given by path.</summary>
        public bool ByPath;
        /// <summary>The record's owner and local ID: its FormKey, whatever plugin's version this is.</summary>
        public string Owner = "";
        public uint LocalId;
        /// <summary>Every plugin in Data that holds it, in load order, and the winner. Null when its owner isn't in Data.</summary>
        public RecordVersions? Versions;
        /// <summary>Why this plugin's version: "the plugin that wins it", "the plugin you named".</summary>
        public string Why = "";
        public bool IsWinner;
        public string FormKey => Info.FormKey;
    }

    /// <summary>
    /// Turns what a caller wrote (an EditorID, a FormKey, a bare FormID with a plugin) into one record of one plugin,
    /// from the header index alone. A name that fits more than one record lists them all and picks none, as find does.
    /// With no plugin named, the version read is the one the game uses: the last loaded plugin that holds the record.
    /// </summary>
    public static class RecordPick
    {
        static readonly Regex Hex = new("^[0-9A-Fa-f]{1,8}$", RegexOptions.Compiled);

        /// <summary>The plugin a caller named: a name in Data, or the path of a plugin file. Null with a failed answer when it can't be used.</summary>
        public static (HeaderIndex Ix, IndexedPlugin Plugin, bool ByPath)? Plugin(EngineIndex eng, string text, EngineAnswer answer)
        {
            var main = eng.Index;
            string item = text.Trim().Trim('"');
            bool pathLike = item.Contains('\\') || item.Contains('/') || Path.IsPathRooted(item);
            string full = pathLike ? Home.UserPath(item) : "";
            if (pathLike && Path.GetDirectoryName(full) is { } dir && string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(eng.DataFolder), StringComparison.OrdinalIgnoreCase))
            { item = Path.GetFileName(full); pathLike = false; }
            if (!pathLike)
            {
                var p = main.Plugin(item) ?? main.Plugin(item + ".esm");
                if (p != null) return (main, p, false);
                bool listed = main.LoadOrderEntries.Any(e => e.Name.Equals(item, StringComparison.OrdinalIgnoreCase));
                var near = EngineIndex.NearPlugins(main, item);
                answer.Fail(listed ? $"{item} is in the load order (plugins.txt) but its file isn't in Data" : $"{item} isn't in Data ({main.Plugins.Count} plugins are there)",
                    near.Count > 0 ? $"the nearest real names: {string.Join("; ", near)}. A plugin outside Data can be given by its full path" : "check the name (loadorder2 lists the plugins), or give the full path of a plugin file outside Data");
                return null;
            }
            if (!File.Exists(full)) { answer.Fail($"there is no file {full}", "check the path, or name a plugin that is in Data"); return null; }
            var side = eng.ByPath(full);
            if (side == null) { answer.Fail($"{full} isn't a Starfield plugin (it doesn't start with a TES4 record)", "give a .esm, .esp or .esl file"); return null; }
            return (side.Value.Ix, side.Value.Plugin, true);
        }

        /// <summary>
        /// The record. Null when the answer already holds why not: an error, or the list of the records that fit.
        /// </summary>
        /// <param name="tool">The tool's verb, for the fix lines ("record", "refs2").</param>
        public static Picked? Resolve(EngineIndex eng, string record, string? plugin, EngineAnswer answer, string tool)
        {
            var main = eng.Index;
            string text = record.Trim().Trim('"', '\'');
            if (text.Length == 0) { answer.Fail($"{tool} got no record to look for", "give a FormKey (0476C4:Starfield.esm), an EditorID, or a FormID with plugin", 2); return null; }

            (HeaderIndex Ix, IndexedPlugin Plugin, bool ByPath)? named = null;
            if (!string.IsNullOrWhiteSpace(plugin))
            {
                named = Plugin(eng, plugin, answer);
                if (named == null) return null;
                // A plugin that was named and can't be read is an error: nothing is mapped by guess (trap 14)
                if (ProblemNotes.Blocking(named.Value.Plugin) is { } block) { answer.Fail(block.What, block.Fix); return null; }
            }

            // ---- With a plugin named: its own entries
            if (named is { } np)
            {
                var seg = np.Plugin.Segment!;
                var hits = new List<int>();
                if (HeaderIndex.TryParseFormKey(text, out var owner, out var id) && LooksLikePlugin(owner))
                {
                    int o = seg.OwnerOf(owner);
                    if (o >= 0) Copies(seg, seg.Find(o, id), hits);
                    if (hits.Count == 0)
                    {
                        var v = main.Plugin(owner) != null ? main.Versions(owner, id) : null;
                        answer.Fail(v == null ? $"{np.Plugin.Name} holds no record {id:X6}:{owner}, and no plugin in Data does" : $"{np.Plugin.Name} doesn't hold {LabelOf(main, v)}: it is held by {string.Join(", ", v.Holders.Select(h => main.Plugins[h.Plugin].Name))}",
                            v == null ? "check the FormKey: find shows what an ID or EditorID is" : "name one of those plugins, or leave plugin out to read the version the game uses");
                        return null;
                    }
                }
                else if (Hex.IsMatch(text) && text.Length <= 6)
                {
                    uint local = uint.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                    for (int o = 0; o <= seg.SelfOwner; o++) Copies(seg, seg.Find(o, local), hits);
                    if (hits.Count == 0) seg.FindEditorId(text, hits);
                    if (hits.Count == 0) { answer.Fail($"{np.Plugin.Name} has no record with the ID {local:X6} ({seg.Count:N0} records looked at)", $"check the ID: find \"{text}\" lists every plugin that has a record with it"); return null; }
                }
                else if (Hex.IsMatch(text))
                {
                    var fk = Runtime(main, text, answer);
                    if (fk == null) return null;
                    int o = seg.OwnerOf(fk.Value.Owner);
                    if (o >= 0) Copies(seg, seg.Find(o, fk.Value.Id), hits);
                    if (hits.Count == 0) { answer.Fail($"{text} is {fk.Value.Id:X6}:{fk.Value.Owner} in today's load order, and {np.Plugin.Name} doesn't hold it", "leave plugin out to read the version the game uses"); return null; }
                }
                else
                {
                    seg.FindEditorId(text, hits);
                    hits.RemoveAll(h => h >= seg.FirstUnresolved);
                    if (hits.Count == 0)
                    {
                        var elsewhere = main.FindEditorId(text);
                        answer.Fail($"{np.Plugin.Name} has no record with the EditorID \"{text}\" ({seg.EditorIdCount:N0} EditorIDs looked at)"
                            + (elsewhere.Count > 0 ? $"; {string.Join(", ", elsewhere.Select(r => main.Plugins[r.Plugin].Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))} {(elsewhere.Count == 1 ? "has" : "have")} one" : ""),
                            elsewhere.Count > 0 ? "name that plugin, or leave plugin out" : $"check the name: find \"*{text}*\" searches EditorIDs by pattern");
                        return null;
                    }
                }
                if (hits.Count > 1)
                {
                    answer.Line($"\"{text}\" fits {hits.Count} records of {np.Plugin.Name}. None was picked: name one by its FormKey (FormID:plugin){(hits.Select(h => np.Ix.Describe(new RecordRef(np.Plugin.Id, h)).FormKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() < hits.Count ? ", or by its EditorID where two records share a FormKey" : "")}.");
                    Table(answer, np.Ix, hits.Select(h => new RecordRef(np.Plugin.Id, h)), main);
                    answer.Source(EngineIndex.Source(np.Plugin, np.ByPath ? np.Plugin.Path : null));
                    return null;
                }
                var r = new RecordRef(np.Plugin.Id, hits[0]);
                var info = np.Ix.Describe(r);
                HeaderIndex.TryParseFormKey(info.FormKey, out var ownerName, out var localId);
                var versions = main.Plugin(ownerName) != null ? main.Versions(ownerName, localId) : null;
                return new Picked
                {
                    Ix = np.Ix, Plugin = np.Plugin, Ref = r, Info = info, ByPath = np.ByPath, Owner = ownerName, LocalId = localId, Versions = versions,
                    Why = np.ByPath ? "the file you named, outside Data" : "the plugin you named",
                    IsWinner = !np.ByPath && main.IsWinner(r),
                };
            }

            // ---- No plugin named: every plugin in Data, then the version the game uses
            var keys = new List<(string Owner, uint Id)>();
            string what;
            if (HeaderIndex.TryParseFormKey(text, out var fo, out var fid) && LooksLikePlugin(fo))
            {
                what = "FormKey";
                var p = main.Plugin(fo);
                if (p == null)
                {
                    answer.Fail(main.LoadOrderEntries.Any(e => e.Name.Equals(fo, StringComparison.OrdinalIgnoreCase)) ? $"{fo} is in the load order (plugins.txt) but its file isn't in Data, so {fid:X6}:{fo} can't be read" : $"{fo} isn't in Data, so {fid:X6}:{fo} can't be read ({main.Plugins.Count} plugins are there)",
                        "check the plugin's name (loadorder2 lists the plugins)");
                    return null;
                }
                if (main.Versions(p.Name, fid) == null)
                {
                    var seg = p.Segment;
                    string top = seg != null && seg.FirstUnresolved > seg.FirstOwn ? $", its own IDs go up to {seg[seg.FirstUnresolved - 1].LocalId:X6}" : "";
                    answer.Fail($"no plugin holds {fid:X6}:{p.Name} ({p.Name} has {seg?.Count ?? 0:N0} records{top})", "check the ID: find shows what an ID or EditorID is");
                    return null;
                }
                keys.Add((p.Name, fid));
            }
            else if (Hex.IsMatch(text) && text.Length <= 6)
            {
                what = "ID";
                uint local = uint.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                foreach (var r in main.FindLocalId(local)) keys.Add((main.Plugins[r.Plugin].Name, local));
                if (keys.Count == 0) foreach (var k in ByEditorId(main, text)) keys.Add(k);
                if (keys.Count == 0)
                {
                    answer.Fail($"no plugin has a record of its own with the ID {local:X6} ({main.TotalRecords:N0} records in the {main.Plugins.Count} plugins in Data looked at)", "check the ID, or give the record's FormKey (FormID:plugin) or EditorID");
                    return null;
                }
            }
            else if (Hex.IsMatch(text))
            {
                what = "FormID";
                var fk = Runtime(main, text, answer);
                if (fk == null) return null;
                if (main.Versions(fk.Value.Owner, fk.Value.Id) == null) { answer.Fail($"{text} was read as a runtime FormID: its slot is {fk.Value.Owner}, which has no record {fk.Value.Id:X6}", "check the ID. A FormID with no slot has at most 6 digits"); return null; }
                keys.Add(fk.Value);
            }
            else
            {
                what = "EditorID";
                keys.AddRange(ByEditorId(main, text));
                if (keys.Count == 0)
                {
                    answer.Fail($"no record has the EditorID \"{text}\" ({main.Plugins.Sum(p => (long)(p.Segment?.EditorIdCount ?? 0)):N0} EditorIDs in the {main.Plugins.Count} plugins in Data looked at)",
                        $"check the name: find \"*{text}*\" searches EditorIDs by pattern");
                    return null;
                }
            }
            if (keys.Count > 1 && what == "EditorID")
            {
                // One of them is in a loaded plugin and the others only in plugins that aren't loaded: the loaded one is
                // the record the game has, so it is picked, and a note names the others. Two loaded ones are still listed.
                var loaded = keys.Where(k => main.Versions(k.Owner, k.Id) is { Winner: >= 0 }).ToList();
                if (loaded.Count == 1)
                {
                    var others = keys.Where(k => k != loaded[0]).Select(k => main.Versions(k.Owner, k.Id)).Where(v => v != null).Select(v => LabelOf(main, v!)).ToList();
                    answer.Note($"{keys.Count} records have the EditorID \"{text}\". The one in a loaded plugin was picked. The other{(others.Count == 1 ? " is" : "s are")} only in plugins that aren't loaded: {string.Join(", ", others.Take(6))}{(others.Count > 6 ? $" and {others.Count - 6} more" : "")}. Name one by its FormKey (FormID:plugin) to read it.");
                    keys = loaded;
                }
            }
            if (keys.Count > 1)
            {
                answer.Line(what == "ID"
                    ? $"{keys.Count} records have the ID {text.ToUpperInvariant().PadLeft(6, '0')}. They are different records, and none was picked: name the plugin (FormID:plugin, or plugin)."
                    : $"{keys.Count} records have the EditorID \"{text}\". None was picked: name one by its FormKey (FormID:plugin).");
                var rows = keys.Select(k => main.Versions(k.Owner, k.Id)).Where(v => v != null).Select(v => v!.Winner >= 0 ? v.Holders[v.Winner] : v.Holders[0]).ToList();
                Table(answer, main, rows, main);
                answer.Source(eng.AllSource());
                answer.Source(eng.LoadOrderSource());
                return null;
            }

            var versionsOf = main.Versions(keys[0].Owner, keys[0].Id)!;
            RecordRef pick; string why;
            if (versionsOf.Winner >= 0)
            {
                pick = versionsOf.Holders[versionsOf.Winner];
                why = versionsOf.Holders.Count == 1 ? "the only plugin that holds it" : $"the plugin that wins it (the last loaded of the {versionsOf.Holders.Count} that hold it)";
            }
            else
            {
                // No loaded plugin holds it: its owner's own version, else the first holder
                int own = -1;
                for (int i = 0; i < versionsOf.Holders.Count; i++) if (main.Plugins[versionsOf.Holders[i].Plugin].Name.Equals(keys[0].Owner, StringComparison.OrdinalIgnoreCase)) own = i;
                pick = versionsOf.Holders[own >= 0 ? own : 0];
                why = "no loaded plugin holds it; this is " + (own >= 0 ? "its own plugin" : "the first plugin that holds it");
            }
            var pp = main.Plugins[pick.Plugin];
            if (ProblemNotes.Blocking(pp) is { } blocked) { answer.Fail(blocked.What, blocked.Fix); return null; }
            return new Picked
            {
                Ix = main, Plugin = pp, Ref = pick, Info = main.Describe(pick), Owner = keys[0].Owner, LocalId = keys[0].Id, Versions = versionsOf,
                Why = why, IsWinner = versionsOf.Winner >= 0,
            };
        }

        static bool LooksLikePlugin(string name) =>
            name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);

        /// <summary>The records that have this EditorID in some plugin's version, each once.</summary>
        static List<(string Owner, uint Id)> ByEditorId(HeaderIndex main, string editorId)
        {
            var seen = new HashSet<(string, uint)>();
            var list = new List<(string, uint)>();
            foreach (var r in main.FindEditorId(editorId))
            {
                var p = main.Plugins[r.Plugin];
                if (r.Entry >= p.Segment!.FirstUnresolved) continue;
                var info = main.Describe(r);
                if (!HeaderIndex.TryParseFormKey(info.FormKey, out var owner, out var id)) continue;
                if (seen.Add((owner.ToLowerInvariant(), id))) list.Add((owner, id));
            }
            return list;
        }

        /// <summary>A FormID with its runtime slot (7 or 8 digits), read with today's load order.</summary>
        static (string Owner, uint Id)? Runtime(HeaderIndex main, string text, EngineAnswer answer)
        {
            uint value = uint.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            uint top = value >> 24;
            string slot = top == 0xFE ? $"FE:{(value >> 12) & 0xFFF:X3}" : top == 0xFD ? $"FD:{(value >> 16) & 0xFF:X2}" : $"{top:X2}";
            uint local = top == 0xFE ? value & 0xFFF : top == 0xFD ? value & 0xFFFF : value & 0xFFFFFF;
            var e = main.LoadOrderEntries.FirstOrDefault(x => x.Enabled && x.Slot == slot);
            if (e == null || main.Plugin(e.Name) == null)
            {
                answer.Fail($"{value:X8} was read as a runtime FormID, and no loaded plugin has its slot ({slot})", "a FormID with no slot has at most 6 digits; or give the FormKey (FormID:plugin)");
                return null;
            }
            answer.Note($"{text} was read as a runtime FormID: its slot is {e.Name} in today's load order.");
            return (main.Plugin(e.Name)!.Name, local);
        }

        /// <summary>A plugin can hold a FormKey more than once (MissionsGalore.esm does): every copy is a hit, so none is picked silently.</summary>
        static void Copies(Segment seg, int at, List<int> hits)
        {
            if (at < 0) return;
            var first = seg[at];
            for (int i = at; i < seg.FirstUnresolved && seg[i].Key == first.Key; i++) hits.Add(i);
        }

        static string LabelOf(HeaderIndex main, RecordVersions v) => ProblemNotes.Label(main.Describe(v.Winner >= 0 ? v.Holders[v.Winner] : v.Holders[0]));

        /// <summary>The records that fit, as find lists them: the record, its type, who holds it.</summary>
        static void Table(EngineAnswer answer, HeaderIndex ix, IEnumerable<RecordRef> rows, HeaderIndex main)
        {
            var schema = SchemaTypes.Default;
            answer.Line("| Record | Type | Held by (winner first) |");
            answer.Line("| :--- | :--- | :--- |");
            foreach (var r in rows.Take(200))
            {
                var info = ix.Describe(r);
                string held = ix.Plugins[r.Plugin].Name;
                if (HeaderIndex.TryParseFormKey(info.FormKey, out var owner, out var id) && main.Plugin(owner) != null && main.Versions(owner, id) is { } v)
                {
                    var order = new List<int>();
                    if (v.Winner >= 0) order.Add(v.Winner);
                    for (int i = v.Holders.Count - 1; i >= 0; i--) if (i != v.Winner) order.Add(i);
                    held = string.Join(" > ", order.Take(8).Select(i => main.Plugins[v.Holders[i].Plugin].Name + (main.Plugins[v.Holders[i].Plugin].Order.Enabled ? "" : " (not loaded)"))) + (order.Count > 8 ? $" > +{order.Count - 8} more" : "");
                }
                answer.Line($"| {ProblemNotes.Label(info)} | {schema.Label(info.Signature)} | {held} |");
            }
        }
    }
}
