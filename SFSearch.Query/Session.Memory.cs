// A statement's memory and temporary files, watched while it runs (GAPS S104). The host gives the memory one command
// may hold (QueryOptions.MemoryLimitMB: the service's allowance for the command, which the user's workerMaxMB sets).
// SQLite's own heap limit (sqlite3_hard_heap_limit64) does nothing here: it counts only while SQLite's memory statistics
// are on, and they are off because they put every allocation of every reading thread behind one lock (notes\M6.md,
// item 14; measured again in notes\fix-j82-sqlite-memory.md). So the statement's own connection looks at the process's
// private bytes, the figure the service's limit is held against, every 1,000 steps of SQLite's machine, at most every
// 10 ms, and stops the statement (as a cancel does) once they pass the limit less a small margin, before the service's
// watchdog would stop the whole command. It also stops a statement whose temporary files pass what one statement may
// write (TempFilesLimit: 4 times the command's memory, 4 to 32 GB; GAPS S110), take half the free space of their drive,
// or leave less than the engine keeps free (QueryOptions.ScratchSpareMB).
using System.Diagnostics;
using SQLitePCL;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    sealed partial class Session
    {
        /// <summary>Why the watch stopped the statement: memory (the MB it reached) or disk (the MB left), else null.</summary>
        string? _stoppedBy;
        /// <summary>The watch has had .NET collect its garbage once in this statement.</summary>
        bool _collected;
        long _stopMB, _stoppedAtMB, _limitMB, _tempCapMB;
        long _nextLook, _nextDiskLook, _diskAtStartMB;
        string? _tempRoot;
        delegate_progress? _watch;
        static readonly long LookEvery = Stopwatch.Frequency / 100;      // 10 ms
        static readonly long DiskLookEvery = Stopwatch.Frequency / 10;   // 100 ms

        /// <summary>Starts watching the statement about to run on the query's connection.</summary>
        void WatchStart()
        {
            _stoppedBy = null; _collected = false;
            _limitMB = MemoryLimit();
            // Stop a little under the limit: the service's watchdog stops the whole command at the limit itself
            _stopMB = _limitMB > 0 ? _limitMB - Math.Min(256, _limitMB / 16) : 0;
            _tempRoot = SqliteTuning.TempFolderInUse;
            _diskAtStartMB = FreeMB(_tempRoot);
            // allow_heavy let a statement whose estimate passed the limit write its estimate and half again (Session.TempFiles.cs)
            _tempCapMB = Math.Max(TempFilesLimit(_e._options, _limitMB), _tempAllowedMB);
            _nextLook = _nextDiskLook = 0;
            if (_stopMB <= 0 && _diskAtStartMB < 0) return;
            _watch ??= _ => Look();
            raw.sqlite3_progress_handler(_con.Handle!, 1000, _watch, null);
        }

        void WatchStop()
        {
            try { if (_con.Handle is { } h) raw.sqlite3_progress_handler(h, 0, null, null); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>SQLite's progress callback: non-zero stops the statement (SQLITE_INTERRUPT).</summary>
        int Look()
        {
            long now = Stopwatch.GetTimestamp();
            if (now < _nextLook) return 0;
            _nextLook = now + LookEvery;
            if (_stopMB > 0)
            {
                long mb = MemoryNow.PrivateMB();
                if (mb > _stopMB && !_collected)
                {
                    // Garbage .NET hasn't collected yet counts in the private bytes too, and the reads before the
                    // statement (in the same command) leave some. It isn't the statement's: it is collected once, and
                    // the statement is stopped only if the process is still past the line (notes/fix-j87-j88-sqlite.md:
                    // query-memory's "after the stop" was stopped for a read's garbage)
                    _collected = true;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    mb = MemoryNow.PrivateMB();
                }
                if (mb > _stopMB) { _stoppedBy = "memory"; _stoppedAtMB = mb; return 1; }
            }
            if (_diskAtStartMB >= 0 && now >= _nextDiskLook)
            {
                _nextDiskLook = now + DiskLookEvery;
                long free = FreeMB(_tempRoot);
                if (free < 0) return 0;
                // What the statement has written since it started, as the drive's free space went down
                long written = _diskAtStartMB - free;
                if (written >= _tempCapMB) { _stoppedBy = "tempcap"; _stoppedAtMB = written; return 1; }
                // The outer guard: a statement may take half the drive's free space, and never the room the engine keeps
                // free for its scratch files (ScratchSpareMB, 10 GB); a statement that wrote under 1 GB is never stopped for it
                if (written >= 1024 && (free < _e._options.ScratchSpareMB || free < _diskAtStartMB / 2)) { _stoppedBy = "disk"; _stoppedAtMB = free; return 1; }
            }
            return 0;
        }

        static long FreeMB(string? folder)
        {
            if (folder == null) return -1;
            try { return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace >> 20; }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return -1; }
        }

        /// <summary>
        /// What one statement's temporary files may take (MB; GAPS S110, the user's "Yes, do that", 2026-11-15): the
        /// host's QueryOptions.TempFilesLimitMB, else 4 times the command's memory limit, at least 4 GB and at most 32 GB
        /// (6 GB on an 8 GB PC's 1.5 GB, 24 GB at 6 GB a command); 16 GB with no memory limit. Why: the biggest spills of
        /// real questions are a few GB (every Form of Starfield.esm sorted writes under 1 GB; every link of the load
        /// order, 17 million rows, about 3 GB), so 4 GB is room on any PC; the bound grows with what the user lets a
        /// command use; and 32 GB is written in under a minute, so a statement that would write terabytes (a grouped join
        /// that keeps each record's whole Json, S110) stops soon with the error instead of filling the drive. Settings
        /// that allow all memory (a workerMaxMB as large as the PC's memory) get the 32 GB.
        /// </summary>
        internal static long TempFilesLimit(QueryOptions o, long limitMB)
        {
            if (o.TempFilesLimitMB?.Invoke() is long mb and > 0) return mb;
            return limitMB > 0 ? Math.Clamp(limitMB * 4, 4096, 32768) : 16384;
        }

        /// <summary>True when the host gives its own limit for a statement's temporary files (not the rule of 4 times the memory).</summary>
        static bool HostTempLimit(QueryOptions o) => o.TempFilesLimitMB?.Invoke() is long mb && mb > 0;

        static string GB(long mb) => (mb / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The error of a statement the watch stopped, or that SQLite couldn't give memory or disk to.</summary>
        QueryException MemoryStop(bool outOfMemory = false)
        {
            const string narrow = "narrow it: fewer rows into the join or the GROUP BY (a WHERE on a plugin, a FormKey or a column), fewer columns, and no Json (or a column of it) in a GROUP BY over a join";
            string raise = _e._options.MemoryLimitSetting is { } s ? $"; or raise {s} if this PC has the memory to spare" : "";
            if (_stoppedBy == "tempcap" && _tempAllowedMB >= _tempCapMB)
                return new QueryException($"the query was stopped: its temporary files passed {GB(_stoppedAtMB)} GB, more than allow_heavy let it write ({GB(_tempCapMB)} GB, its estimate and half again)", narrow);
            if (_stoppedBy == "tempcap")
                return new QueryException($"the query was stopped: its temporary files passed {GB(_stoppedAtMB)} GB, more than one query may write ({GB(_tempCapMB)} GB{(_limitMB > 0 && !HostTempLimit(_e._options) ? ", 4 times what one command may use" : "")})",
                    narrow + raise);
            if (_stoppedBy == "disk")
                return new QueryException($"the query was stopped: its temporary files left {GB(_stoppedAtMB)} GB free on the drive of {_tempRoot}", narrow + "; or free some disk space there");
            if (outOfMemory)
                return new QueryException(_limitMB > 0 ? $"SQLite ran out of memory for this query (this command may use {GB(_limitMB)} GB)" : "SQLite ran out of memory for this query", narrow + raise);
            return new QueryException($"the query was stopped: it took this command to {GB(_stoppedAtMB)} GB of memory, and one command may use {GB(_limitMB)} GB", narrow + raise);
        }
    }
}
