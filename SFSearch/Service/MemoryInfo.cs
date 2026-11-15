using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace SFSearch.Service
{
    /// <summary>
    /// The PC's memory as Windows reports it. "Available" is the figure Task Manager and the "Available MBytes" counter
    /// show: memory a program can have at once without anything being paged out (free plus standby). It is not "free",
    /// which leaves out the standby list and reads far too low on a PC that has been up for a while.
    /// <para>
    /// A test can make the service see a smaller PC (2026-11-12, fix S17): with SFSEARCH_TEST_HOOKS=1, the environment
    /// variable SFSEARCH_TEST_MEMORY names a file such as <c>total=8192 others=1500 cores=16 scale=4</c>. The service then
    /// sees a PC of <c>total</c> MB where other programs hold <c>others</c> MB and its own workers what they really hold
    /// times <c>scale</c> (so a test takes little real memory); available is what is left. The file is read again when it
    /// changes, so a test can make memory run short while commands run.
    /// </para>
    /// </summary>
    public static class MemoryInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessMemoryCountersEx { public uint Cb, PageFaultCount; public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage; }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint cb);

        static bool Read(out MemoryStatusEx m)
        {
            m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref m);
        }

        /// <summary>
        /// The service's own workers' memory, in MB (the coordinator sets this). Only the test's pretend PC uses it: on a
        /// real PC, Windows' figure already counts them.
        /// </summary>
        public static Func<long>? OwnMB;

        /// <summary>Memory Windows can hand out now, in MB. -1 when it can't be read (not Windows): callers then don't hold anything back.</summary>
        public static long AvailableMB()
        {
            if (Pretend() is { } p) return Math.Max(0, p.Total - p.Others - (OwnMB?.Invoke() ?? 0));
            return Read(out var m) ? (long)(m.AvailPhys / 1048576) : -1;
        }

        /// <summary>The PC's memory, in MB. -1 when it can't be read.</summary>
        public static long TotalMB() => Pretend() is { } p ? p.Total : Read(out var m) ? (long)(m.TotalPhys / 1048576) : -1;

        /// <summary>The PC's logical processors (the test's pretend PC may say fewer).</summary>
        public static int Cores() => Pretend() is { Cores: > 0 } p ? p.Cores : Environment.ProcessorCount;

        /// <summary>
        /// A process's private memory (what it alone holds, the figure Task Manager calls "commit size"), in MB, times the
        /// test's scale. -1 when it can't be read. Cheaper than Process.PrivateMemorySize64, which reads every process on the PC.
        /// </summary>
        public static long PrivateMB(Process p)
        {
            try
            {
                long mb;
                if (OperatingSystem.IsWindows())
                {
                    var c = new ProcessMemoryCountersEx { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
                    if (!K32GetProcessMemoryInfo(p.Handle, ref c, c.Cb)) return -1;
                    mb = (long)(c.PrivateUsage.ToUInt64() / 1048576);
                }
                else { p.Refresh(); mb = p.PrivateMemorySize64 / 1048576; }
                return mb * Scale;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return -1; }
        }

        /// <summary>What a worker said it holds, in MB, as the service counts it (times the test's scale).</summary>
        public static long Scaled(long mb) => mb * Scale;

        static int Scale => Pretend() is { Scale: > 1 } p ? p.Scale : 1;

        /// <summary>"7.5" for 7,680 MB: one decimal, none when it is a whole number.</summary>
        public static string GB(long mb) => (mb / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- The test's pretend PC
        sealed record PretendPc(long Total, long Others, int Cores, int Scale);

        static PretendPc? _pretend;
        static string? _pretendPath;
        static DateTime _pretendStamp, _pretendChecked;
        static readonly object PretendLock = new();

        /// <summary>True when the service sees the test's pretend PC, not the real one.</summary>
        public static bool Pretending => Pretend() != null;

        static PretendPc? Pretend()
        {
            if (Environment.GetEnvironmentVariable("SFSEARCH_TEST_HOOKS") != "1") return null;
            if (Environment.GetEnvironmentVariable("SFSEARCH_TEST_MEMORY") is not { Length: > 0 } path) return null;
            lock (PretendLock)
            {
                // Looked at again at most every 50 ms: the coordinator asks many times a second
                if (path == _pretendPath && DateTime.UtcNow - _pretendChecked < TimeSpan.FromMilliseconds(50)) return _pretend;
                _pretendChecked = DateTime.UtcNow;
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(path);
                    if (path == _pretendPath && stamp == _pretendStamp) return _pretend;
                    long total = 0, others = 0; int cores = 0, scale = 1;
                    foreach (var part in File.ReadAllText(path).Split(new[] { ' ', '\t', '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = part.Split('=', 2);
                        if (kv.Length != 2 || !long.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) continue;
                        switch (kv[0].Trim().ToLowerInvariant())
                        {
                            case "total": total = v; break;
                            case "others": others = v; break;
                            case "cores": cores = (int)v; break;
                            case "scale": scale = (int)Math.Max(1, v); break;
                        }
                    }
                    _pretendPath = path;
                    _pretendStamp = stamp;
                    _pretend = total > 0 ? new PretendPc(total, others, cores, scale) : null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // a test writing the file just now: keep the last reading
                return _pretend;
            }
        }
    }
}
