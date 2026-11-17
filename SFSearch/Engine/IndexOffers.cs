using System;
using System.Collections.Generic;
using System.Linq;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// The third answer at a permission point (J33, step 7 of the J1/J12 plan): "Yes, but save an index to speed up future
    /// queries". Today the one index that is made only when asked is the world link index (J32), so it is the one offered:
    /// where a question has to search or read world data (cells, placed objects, navmeshes, terrain) of plugins that have
    /// no world link file, the answer gives its size, the time to make it and the time the same kind of question then
    /// takes. Taking it (allow_heavy="save_index", refs' world: "save_index") runs the question as a yes would, records
    /// the plugins in indexes\world\wanted.txt (WorldLinks.Want) and has the index made after the answer: by the service's
    /// indexer lane (JobContext.SaveIndexAfter), or right after the answer on a plain command line. Where no index would
    /// help, the answers say so (HeavyAnswers).
    /// </summary>
    public static class IndexOffers
    {
        /// <summary>A world link file's size per world record, measured (J32 note): Starfield.esm 77 MB for 3,394,276 world
        /// records (24 bytes each), all 119 plugins with world data 275 MB for 8,752,362 (33). Between the two.</summary>
        const double BytesPerWorldRecord = 28;

        /// <summary>
        /// The world link index of those of the plugins that hold world data and have no world link file yet, with its
        /// size, the time to make it, and <paramref name="later"/> (the same question with it, and its time). Null when
        /// every one of them has its file, or none holds world data: nothing to offer.
        /// </summary>
        public static IndexOffer? World(IEnumerable<IndexedPlugin> plugins, double laterSeconds, string later)
        {
            var need = plugins.Distinct().Where(p => EngineLinks.Walkable(p) && !p.ReadingKey.IsZero && WorldLinks.HasWorld(p) && WorldLinks.For(p) == null).ToList();
            if (need.Count == 0) return null;
            var (records, _, seconds, _) = WorldLinks.Cost(need);
            long mb = Math.Max(1, (long)Math.Round(records * BytesPerWorldRecord / 1048576));
            string what = need.Count == 1 ? $"the world link index of {need[0].Name}" : $"the world link index of {need.Count:N0} plugins";
            return new IndexOffer("world", what, need.Select(p => p.Name).ToList(), mb, seconds, laterSeconds, later);
        }

        /// <summary>The offer on the wire, for the JSON answer's heavy.saveIndex.</summary>
        public static SaveIndexInfo? Info(IndexOffer? offer, double nowSeconds, string answer = "allow_heavy=\"save_index\"") =>
            offer == null ? null : new SaveIndexInfo
            {
                Index = offer.Index, What = offer.What, Plugins = offer.Plugins.Count, SizeMB = offer.SizeMB, BuildSeconds = Math.Round(offer.BuildSeconds, 1),
                LaterSeconds = Math.Round(offer.LaterSeconds, 2), SavesSeconds = Math.Round(Math.Max(0, nowSeconds - offer.LaterSeconds), 1), Answer = answer,
            };

        /// <summary>
        /// Takes the third answer: the plugins are added to what the user asked to keep (every plugin with world data, new
        /// ones too, when the offer covers them all), the index is made after the answer, and a note says so, with how to
        /// see and drop it.
        /// </summary>
        public static void Take(EngineAnswer answer, IndexOffer offer)
        {
            var ix = EngineIndex.Get().Index;
            var all = ix.Plugins.Where(p => EngineLinks.Walkable(p) && WorldLinks.HasWorld(p) && WorldLinks.For(p) == null).Select(p => p.Name).ToList();
            bool every = all.Count > 0 && all.All(n => offer.Plugins.Contains(n, StringComparer.OrdinalIgnoreCase));
            WorldLinks.Want(every ? new[] { "*" } : offer.Plugins);
            JobContext.SaveIndexAfter(offer.Index);
            answer.Note(HeavyAnswers.Saving(offer, ServiceWorker.Connected));
        }

        /// <summary>The caller answered "save_index" where no index would make the question faster: it ran as a yes, and the note says so.</summary>
        public static void NothingToSave(EngineAnswer answer, string why)
        {
            if (JobContext.SaveIndexAsked) answer.Note(HeavyAnswers.NothingToSave(why));
        }
    }
}
