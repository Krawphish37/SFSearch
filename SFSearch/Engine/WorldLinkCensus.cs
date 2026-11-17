using System;
using System.Collections.Generic;
using System.Linq;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Reports;

namespace SFSearch.Engine
{
    /// <summary>
    /// The world link index (J32) as profile reads it (GAPS S99): a world type's link field counted from the postings of
    /// the plugins' world link files. Only plugins whose file holds every world record's links (none unread) answer.
    /// </summary>
    public sealed class WorldLinkCensus : IWorldLinkIndex
    {
        public bool IsWorld(IndexedPlugin p, string signature)
        {
            if (p.Segment is not { } seg) return false;
            var world = WorldTypes.Of(seg);
            for (int i = 0; i < seg.Signatures.Count; i++)
                if (seg.Signatures[i].Signature == signature) return world[i];
            return false;
        }

        public bool Has(IndexedPlugin p) => WorldLinks.For(p) is { UnreadCount: 0 };

        public void Each(IndexedPlugin p, string field, Action<ulong, int, long> each)
        {
            if (WorldLinks.For(p) is not { } file) return;
            int f = -1;
            for (int i = 0; i < file.Fields.Count; i++) if (file.Fields[i] == field) { f = i; break; }
            if (f < 0) return;
            var targets = file.Targets;
            var postings = file.Postings;
            for (int t = 0; t < targets.Length; t++)
            {
                var target = targets[t];
                for (int j = (int)target.First; j < target.First + target.Count; j++)
                    if (postings[j].Field == f) each(target.Target, (int)postings[j].From, file.CountAt(j));
            }
        }

        public string Source(IReadOnlyCollection<IndexedPlugin> plugins)
        {
            long bytes = plugins.Select(WorldLinks.For).Where(x => x != null).Sum(x => x!.FileBytes);
            return $"the world link index of {(plugins.Count == 1 ? plugins.First().Name : $"{plugins.Count:N0} plugins")} ({Sizes.Text(bytes)}, indexes\\world)";
        }
    }
}
