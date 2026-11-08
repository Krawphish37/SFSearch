using System;
using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace DarkStarPatchScanner
{
    public class DummyMasterStyle : IModMasterStyledGetter
    {
        public MasterStyle MasterStyle { get; set; } = MasterStyle.Full;
        public ModKey ModKey { get; set; }
    }

    public class NativeMasterFlagCache : Noggog.IReadOnlyCache<IModMasterStyledGetter, ModKey>
    {
        private string _dataFolder;
        private Dictionary<ModKey, IModMasterStyledGetter> _cache = new Dictionary<ModKey, IModMasterStyledGetter>();

        public NativeMasterFlagCache(string dataFolder)
        { 
            _dataFolder = dataFolder; 
        }

        public IModMasterStyledGetter TryGetValue(ModKey key)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var style = new DummyMasterStyle() { ModKey = key, MasterStyle = MasterStyle.Full };
            try 
            {
                string path = System.IO.Path.Combine(_dataFolder, key.ToString());
                if (System.IO.File.Exists(path)) 
                {
                    var dummyCache = new SimpleMasterFlagCache();
                    var readParams = new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryReadParameters { MasterFlagsLookup = dummyCache, ThrowOnUnknownSubrecord = false };
                    using var mod = Mutagen.Bethesda.Starfield.StarfieldMod.CreateFromBinaryOverlay(path, Mutagen.Bethesda.Starfield.StarfieldRelease.Starfield, readParams);
                    if (mod.ModHeader.Flags.HasFlag(Mutagen.Bethesda.Starfield.StarfieldModHeader.HeaderFlag.Light)) 
                        style.MasterStyle = MasterStyle.Small;
                    else if (mod.ModHeader.Flags.HasFlag(Mutagen.Bethesda.Starfield.StarfieldModHeader.HeaderFlag.Medium)) 
                        style.MasterStyle = MasterStyle.Medium;
                }
            }
            catch { }

            _cache[key] = style;
            return style;
        }
            
        public int Count => _cache.Count;
        public bool ContainsKey(ModKey key) => true;
        public IModMasterStyledGetter this[ModKey key] => TryGetValue(key);
        
        public IEnumerator<Noggog.IKeyValue<ModKey, IModMasterStyledGetter>> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { yield break; }
        public IEnumerable<ModKey> Keys { get { yield break; } }
        public IEnumerable<IModMasterStyledGetter> Items { get { yield break; } }
    }

    public class SimpleMasterFlagCache : Noggog.IReadOnlyCache<IModMasterStyledGetter, ModKey>
    {
        public IModMasterStyledGetter TryGetValue(ModKey key)
        {
            return new DummyMasterStyle() { ModKey = key, MasterStyle = MasterStyle.Full };
        }
        
        public int Count => 0;
        public bool ContainsKey(ModKey key) => true;
        public IModMasterStyledGetter this[ModKey key] => TryGetValue(key);
        
        public IEnumerator<Noggog.IKeyValue<ModKey, IModMasterStyledGetter>> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { yield break; }
        public IEnumerable<ModKey> Keys { get { yield break; } }
        public IEnumerable<IModMasterStyledGetter> Items { get { yield break; } }
    }
}
