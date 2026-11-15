using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Mutagen.Bethesda.Strings;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Query;
using SFSearch.Records;
using SFSearch.Reports;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// The libraries of the agent-first design inside SFSearch (milestone M8): the SQL layer (SFSearch.Query), the
    /// comparisons (SFSearch.Compare) and the reports (SFSearch.Reports). They all stand on the engine's one header
    /// index and its record engines, so a worker holds one copy of each however many tools it has answered.
    /// Their scratch files go under the home's scratch folder: scratch\query for the SQL tables, scratch\copies for a
    /// file read under another name. Files that must outlive a service start go under kept\ in the home.
    /// </summary>
    public static class EngineLibs
    {
        static readonly object _gate = new();
        static EngineIndex? _for;
        static readonly Dictionary<(HeaderIndex Ix, string Language), QueryEngine> _query = new();
        static bool _tuned;

        /// <summary>Over these a tool answers with its cost first and runs only when the caller allows heavy work (design section 7).</summary>
        public const double HeavySeconds = 10;

        /// <summary>
        /// Once, before this program opens its first SQLite connection: SQLite's memory statistics off. With them on,
        /// every allocation in every connection takes one lock, which the SQL layer's reading threads then queue for.
        /// </summary>
        public static void TuneSqlite()
        {
            if (_tuned) return;
            _tuned = true;
            try { SqliteTuning.NoMemoryStatistics(); }
            catch (Exception e) when (e is DllNotFoundException or InvalidOperationException or TypeInitializationException) { }
        }

        static EngineLibs()
        {
            // A comparison's or a report's session takes the engine's own index and record engine (SFSearch.Compare\CompareSession.cs)
            CompareSession.Host = (data, language) =>
            {
                var eng = EngineIndex.Get();
                if (!string.Equals(Path.GetFullPath(data).TrimEnd('\\', '/'), eng.DataFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
                return (eng.Index, EngineRecords.For(eng, eng.Index, language));
            };
            // A plugin's problems are worded in one place, whichever tool or library read the plugin
            CompareSession.ProblemNote = ProblemNotes.ForLibrary;
            // A relative path is the caller's: read from the folder the caller is in, not the worker's
            CompareSession.UserPath = Home.UserPath;
        }

        /// <summary>The home's scratch folder: emptied at every service start.</summary>
        public static string ScratchRoot => EngineRecords.ScratchRoot;

        /// <summary>Files that are kept between service starts (decompiled scripts, the history log): kept\ under the home. Safe to delete; made again when needed.</summary>
        public static string KeptRoot => Path.Combine(Home.Dir, "kept");

        /// <summary>The user's own stored queries: queries\ under the home.</summary>
        public static string UserQueries => Path.Combine(Home.Dir, "queries");

        static void ForIndex(EngineIndex eng)
        {
            if (ReferenceEquals(_for, eng)) return;
            foreach (var q in _query.Values) q.Dispose();
            _query.Clear();
            _for = eng;
        }

        // ---------------------------------------------------------------- The SQL layer
        /// <summary>The SQL engine over a header index: the main one, or the index of a plugin file outside Data.</summary>
        public static QueryEngine Query(EngineIndex eng, HeaderIndex ix, Language language)
        {
            TuneSqlite();
            lock (_gate)
            {
                ForIndex(eng);
                // Side indexes come and go (a file outside Data): engines of indexes that were closed are dropped
                if (_query.Count > 6)
                    foreach (var k in _query.Keys.Where(k => !ReferenceEquals(k.Ix, eng.Index) && !ReferenceEquals(k.Ix, ix)).ToList()) { _query[k].Dispose(); _query.Remove(k); }
                if (_query.TryGetValue((ix, language.ToString()), out var have)) return have;
                var records = EngineRecords.For(eng, ix, language);
                string scratch = Path.Combine(ScratchRoot, "query");
                Directory.CreateDirectory(scratch);
                var made = new QueryEngine(records, new QueryOptions
                {
                    ScratchFolder = scratch, SchemaDbPath = SchemaTypes.DefaultPath, Language = language.ToString(), PluginsTxt = eng.PluginsTxt,
                    HeavyGate = (mb, what) => JobContext.RequireHeavy((int)Math.Min(int.MaxValue, mb), what) ? null : RefusedMark,
                });
                return _query[(ix, language.ToString())] = made;
            }
        }

        /// <summary>What a library's error holds when the heavy lane said no: JobContext has already printed the line.</summary>
        public const string RefusedMark = "\u0001the heavy lane refused it";

        public static StoredQueries Stored(QueryEngine engine) => new(engine, new StoredOptions
        {
            ShippedFolder = Path.Combine(AppContext.BaseDirectory, "queries"),
            UserFolder = UserQueries,
            AddOnFolders = new[] { Path.Combine(Home.Dir, "extensions") },
        });

        // ---------------------------------------------------------------- Comparisons and reports
        /// <summary>A comparison session for one command, on the engine's index. Dispose it when the command ends: that closes the files outside Data it opened.</summary>
        public static CompareSession Compare(EngineIndex eng, Language language)
        {
            TuneSqlite();
            return new CompareSession(eng.DataFolder, ScratchRoot, EngineRecords.Schema, eng.VersionText, language);
        }

        /// <summary>A report session for one command, on the engine's index.</summary>
        public static ReportSession Reports(EngineIndex eng, Language language)
        {
            TuneSqlite();
            return new ReportSession(eng.DataFolder, ScratchRoot, EngineRecords.Schema, eng.VersionText, language);
        }

        // ---------------------------------------------------------------- A library's answer as the engine's
        /// <summary>
        /// The lines of a library's text answer up to its own [more], [notes], [source] and [cost] lines. The engine
        /// prints those parts itself, from the answer's fields.
        /// </summary>
        public static List<string> BodyLines(string contractText)
        {
            var lines = contractText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
            int end = lines.Count;
            while (end > 0 && IsTail(lines[end - 1])) end--;
            return lines.GetRange(0, end);

            static bool IsTail(string l) => l.StartsWith("[more] ", StringComparison.Ordinal) || l.StartsWith("[notes] ", StringComparison.Ordinal)
                || l.StartsWith("[source] ", StringComparison.Ordinal) || l.StartsWith("[cost] ", StringComparison.Ordinal);
        }

        /// <summary>Notes, sources and reads of a library's answer. A note about a plugin that reads wrong or can't be read leads the notes.</summary>
        public static void Parts(EngineAnswer answer, IEnumerable<string> notes, IEnumerable<string> sources, IEnumerable<string> read)
        {
            foreach (var n in notes)
            {
                if (IsProblem(n)) answer.Problem(n); else answer.Note(n);
            }
            foreach (var s in sources) answer.Source(s);
            // The cost line reads "read A, B": each item a library gives names a thing that was read
            foreach (var r in read) answer.Read(r);
        }

        /// <summary>
        /// A library's error line as the contract's two parts: "X has no column "Y". Did you mean: A; B?" becomes the
        /// error "X has no column "Y"" and a fix that starts with the nearest real names.
        /// </summary>
        public static (string What, string Fix) ErrorParts(string what, string? fix, string fallback)
        {
            what = what.Replace("\r", "").Replace("\n", "; ").Trim();
            int at = what.IndexOf(" Did you mean", StringComparison.Ordinal);
            if (at < 0) return (what, fix ?? fallback);
            string near = what[(at + 1)..].Trim().TrimEnd('.').TrimEnd('?');
            near = SFSearch.SchemaGen.NearNames.Lead + near[(near.IndexOf(':') >= 0 && near.StartsWith("Did you mean:", StringComparison.Ordinal) ? "Did you mean:".Length : "Did you mean".Length)..].Trim();
            return (what[..at].TrimEnd('.'), fix == null ? near : near + ". " + char.ToUpperInvariant(fix[0]) + fix[1..]);
        }

        static bool IsProblem(string note) =>
            ProblemNotes.IsOurs(note) || note.Contains("another type than", StringComparison.OrdinalIgnoreCase) || note.Contains("different type than", StringComparison.OrdinalIgnoreCase)
            || note.Contains("can't be read", StringComparison.OrdinalIgnoreCase) || note.Contains("isn't in Data", StringComparison.OrdinalIgnoreCase)
            || note.Contains("more than once", StringComparison.OrdinalIgnoreCase) || note.Contains("override of nothing", StringComparison.OrdinalIgnoreCase)
            || note.Contains("a record their master doesn't have", StringComparison.OrdinalIgnoreCase) || note.Contains("loads late", StringComparison.OrdinalIgnoreCase)
            || note.Contains("isn't loaded", StringComparison.OrdinalIgnoreCase) && note.Contains("master", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A library's answer that pages by rows, as the engine's: the body, and a [more] with a cursor of this question.
        /// </summary>
        /// <param name="next">The row the next page starts at, or -1 when this page ends the answer.</param>
        public static void Paged(EngineAnswer answer, string contractText, int start, int next, string question)
        {
            var body = BodyLines(contractText);
            foreach (var l in body) answer.Line(l);
            if (next >= 0 || start > 0)
            {
                // The library's own [more] line has the counts: "40 of 312 records shown (1 to 40). Next: cursor "40"."
                var more = contractText.Replace("\r\n", "\n").Split('\n').LastOrDefault(l => l.StartsWith("[more] ", StringComparison.Ordinal));
                int total = 0, shown = 0; string unit = "rows";
                if (more != null)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(more, @"^\[more\] ([\d,]+) of ([\d,]+) (.+?) shown");
                    if (m.Success)
                    {
                        shown = int.Parse(m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
                        total = int.Parse(m.Groups[2].Value.Replace(",", ""), CultureInfo.InvariantCulture);
                        unit = m.Groups[3].Value;
                    }
                }
                if (more == null) { shown = Math.Max(0, (next >= 0 ? next : start) - start); total = start + shown; }
                answer.More = new MoreInfo { From = start + 1, Shown = shown, Total = Math.Max(total, start + shown), Unit = unit, Cursor = next >= 0 ? Cursors.Make(next, question) : null };
            }
        }

        // ---------------------------------------------------------------- Heavy work
        /// <summary>
        /// Before work that is heavy by time (over 10 s) or memory (the service's limit, 2 GB): not allowed, the answer
        /// is what it would cost, and nothing runs. Allowed, it goes through the heavy lane: one at a time, and only
        /// when Windows has the memory. False: the answer is already said (call answer.Refused() has been done).
        /// </summary>
        public static bool Heavy(EngineAnswer answer, bool allowed, double seconds, long memoryMB, string what)
        {
            var cfg = ServiceConfig.Load(Home.Dir);
            if (seconds <= HeavySeconds && memoryMB < cfg.HeavyMinMB) return true;
            if (!allowed && !JobContext.HeavyAllowed)
            {
                JobContext.NotRun($"Not run. {what.TrimEnd('.', ' ')}: about {(seconds < 100 ? seconds.ToString("0", CultureInfo.InvariantCulture) : (seconds / 60).ToString("0.#", CultureInfo.InvariantCulture) + " min")}{(seconds < 100 ? " s" : "")} and {MemoryInfo.GB(memoryMB)} GB of memory. Call again with allow_heavy=true (command line: --allow-heavy).", memoryMB, what);
                answer.Refused();
                return false;
            }
            if (!JobContext.RequireHeavy((int)Math.Min(int.MaxValue, memoryMB), what)) { answer.Refused(); return false; }
            return true;
        }

        /// <summary>
        /// After work that held a lot of memory for a moment (a plugin read whole for its round trip): asks .NET to
        /// give it back. One collection isn't enough (M5a's note): two, with the finalizers run between.
        /// </summary>
        public static void GiveBackMemory()
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        }

        /// <summary>The language a caller named, or English. False with a failed answer for a name Mutagen doesn't know.</summary>
        public static bool Language(string? text, EngineAnswer answer, out Language language)
        {
            if (EngineRecords.TryLanguage(text, out language)) return true;
            answer.Fail($"\"{text}\" isn't a language SFSearch knows", $"use one of the languages Starfield ships: {SFSearch.Query.TextLanguages.ShippedText}", 2);
            return false;
        }

        /// <summary>A size on disk of a folder and everything in it, in bytes; 0 when it isn't there.</summary>
        public static (long Bytes, int Files) FolderSize(string folder)
        {
            long bytes = 0; int files = 0;
            try
            {
                if (!Directory.Exists(folder)) return (0, 0);
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { bytes += f.Length; files++; }
                    catch (IOException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return (bytes, files);
        }
    }
}
