using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Serialization;
using SFSearch.Records;

namespace SFSearch.Engine
{
    /// <summary>
    /// Every link of one record, with the field it sits in (design\agent-first\ARCHITECTURE.md section 5, "Links"):
    /// Mutagen.Bethesda.Serialization's <see cref="FormLinkPlaceCollector"/> (1.38.6-fixes.5, C59 W12), which walks the
    /// record with the serializer that writes its JSON (the one generated into SFSearch.Records, through its registry)
    /// and writes nothing. So a link's field is exactly its path in the record's JSON (<c>Keywords[3]</c>, or
    /// <c>Keywords[*]</c> with the index left out), and nothing else of the record is formatted. The walk leaves the
    /// serializer's round-trip notes out (GAPS S60). The link index, the confirmation of a byte search and "what does
    /// this record point at" all use this one walk. A record nested in another (a placed reference in a cell, a topic in
    /// a quest) is a record of its own: its links are its own, never its holder's.
    /// </summary>
    public static class RecordLinks
    {
        /// <summary>A collector for one thread's walks. <paramref name="indexed"/>: a list item's path carries its index
        /// (<c>Keywords[3]</c>); otherwise <c>Keywords[*]</c>, so the set of paths stays small. Its paths are numbered
        /// and kept from walk to walk (<see cref="FormLinkPlaceCollector.Paths"/>).</summary>
        public static FormLinkPlaceCollector Collector(bool indexed = false) => new(GameRelease.Starfield, indexedPaths: indexed, includeNestedRecords: false);

        /// <summary>Walks one record read through Mutagen. Afterwards the collector holds its links: each path's number
        /// and the target (<see cref="FormLinkPlaceCollector.Links"/>). Null links aren't listed.</summary>
        public static void Walk(object record, FormLinkPlaceCollector collector) => collector.Collect(RecordSerializer.Registry, record);

        /// <summary>The record's in-game name (in the language the plugin was opened with), or null.</summary>
        public static string? NameOf(object record) => (record as INamedGetter)?.Name is { Length: > 0 } name ? name : null;

        /// <summary>A path with its list indexes left out: <c>Keywords[3]</c> as <c>Keywords[*]</c>.</summary>
        public static string Plain(string path)
        {
            if (path.IndexOf('[') < 0) return path;
            var sb = new System.Text.StringBuilder(path.Length);
            for (int i = 0; i < path.Length; i++)
            {
                sb.Append(path[i]);
                if (path[i] != '[') continue;
                int close = path.IndexOf(']', i);
                if (close < 0) continue;
                sb.Append('*').Append(']');
                i = close;
            }
            return sb.ToString();
        }
    }
}
