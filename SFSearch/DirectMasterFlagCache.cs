using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Noggog;

namespace SFSearch
{
    public class DirectMasterFlagCache : IReadOnlyCache<IModMasterStyledGetter, ModKey>
    {
        private readonly string _dataPath;
        private readonly Dictionary<ModKey, MasterStyle> _styles = new();

        public DirectMasterFlagCache(string dataPath)
        {
            _dataPath = dataPath;
        }

        public IModMasterStyledGetter TryGetValue(ModKey key)
        {
            if (!_styles.TryGetValue(key, out var style))
            {
                style = MasterStyle.Full;
                try
                {
                    string modPath = Path.Combine(_dataPath, key.FileName);
                    if (File.Exists(modPath))
                    {
                        using var fs = File.OpenRead(modPath);
                        byte[] hdr = new byte[12];
                        if (fs.Read(hdr, 0, 12) == 12)
                        {
                            uint flags = BitConverter.ToUInt32(hdr, 8);
                            if ((flags & 0x0100u) != 0) style = MasterStyle.Small;
                            else if ((flags & 0x0400u) != 0) style = MasterStyle.Medium;
                        }
                    }
                }
                catch { }
                _styles[key] = style;
            }
            return new DummyMasterStyle() { ModKey = key, MasterStyle = style };
        }

        public int Count => _styles.Count;
        public bool ContainsKey(ModKey key) => true;
        public IModMasterStyledGetter this[ModKey key] => TryGetValue(key);
        public IEnumerable<ModKey> Keys => _styles.Keys;
        public IEnumerable<IModMasterStyledGetter> Items => _styles.Select(kvp => (IModMasterStyledGetter)new DummyMasterStyle { ModKey = kvp.Key, MasterStyle = kvp.Value });
        public IEnumerator<IKeyValue<ModKey, IModMasterStyledGetter>> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { yield break; }
    }
}
