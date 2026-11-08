using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SFSearch.Service
{
    /// <summary>
    /// How the service names and guards its databases.
    /// A plugin's database is named after the exact copy of the plugin it was read from:
    /// `darkstar.esm@1A2B3C4D.db`, from the file's size and date and SFSearch's database format. A plugin changed on disk
    /// therefore gets a new database. Commands still reading the old one finish on it, and the coordinator deletes it once
    /// nothing has it open. Only one worker at a time writes a given database (a named mutex, released by Windows if the
    /// worker dies).
    /// </summary>
    public static class ServiceDb
    {
        static readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _changedNoted = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forget the names worked out for the last command: plugins can change between commands.</summary>
        public static void ResetForCommand()
        {
            lock (_names) { _names.Clear(); _used.Clear(); _changedNoted.Clear(); }
        }

        /// <summary>The database files this command touched, for the coordinator's least-recently-used clean-up.</summary>
        public static string[] UsedThisCommand() { lock (_names) return _used.ToArray(); }

        public static string FileName(string plugin)
        {
            lock (_names)
            {
                if (!_names.TryGetValue(plugin, out var name))
                {
                    string path = Path.Combine(Program.GetDataFolderPathPublic(), plugin);
                    name = StampedName(plugin, path) ?? plugin + ".db";
                    _names[plugin] = name;
                }
                _used.Add(name);
                return name;
            }
        }

        /// <summary>`plugin@STAMP.db` for a plugin in Data, null for anything else (a plugin read by path keeps its own stale check).</summary>
        public static string? StampedName(string plugin, string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            string stamp = $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{Program.SchemaVersionPublic}|{Program.FingerprintPublic}";
            string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(stamp)))[..8];
            return $"{plugin}@{hash}.db";
        }

        /// <summary>The plugin name a database file belongs to (`x.esm@ABCD.db` → `x.esm`).</summary>
        public static string PluginOf(string file)
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
            int at = name.LastIndexOf('@');
            return at > 0 ? name[..at] : name;
        }

        /// <summary>When this plugin has an older database in the folder, say once that it changed and is being read again.</summary>
        public static void NoteIfChanged(string plugin, string dbPath)
        {
            string? dir = Path.GetDirectoryName(dbPath);
            if (dir == null || !Directory.Exists(dir)) return;
            lock (_names) if (!_changedNoted.Add(plugin)) return;
            bool older = Directory.EnumerateFiles(dir, plugin + "@*.db").Any(f => !f.Equals(dbPath, StringComparison.OrdinalIgnoreCase));
            if (older) Console.Error.WriteLine($"[session] {plugin} changed on disk since the service last read it, so it was read again for this answer.");
        }

        /// <summary>Holds the right to write one database. Waits for another worker's build of the same plugin, telling the coordinator it's waiting.</summary>
        public sealed class BuildLock : IDisposable
        {
            readonly Mutex _m;
            bool _held;

            public BuildLock(string dbPath)
            {
                string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dbPath).ToLowerInvariant())))[..16];
                _m = new Mutex(false, @"Local\sfsearch_build_" + key);
                try { _held = _m.WaitOne(0); }
                catch (AbandonedMutexException) { _held = true; } // the last holder died mid-build: its transaction never committed
                if (_held) return;
                ServiceWorker.Notify("blocked", Path.GetFileName(dbPath));
                try { _m.WaitOne(); }
                catch (AbandonedMutexException) { }
                _held = true;
                ServiceWorker.Notify("unblocked", Path.GetFileName(dbPath));
            }

            public void Dispose()
            {
                if (_held) { try { _m.ReleaseMutex(); } catch (ApplicationException) { } _held = false; }
                _m.Dispose();
            }
        }
    }
}
