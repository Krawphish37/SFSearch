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
    /// (The pretend PC a test could make the service see went in J37: no test code in the program.
    /// The memory settings are real sfsearch.json settings, and the PC-size defaults are tested with numbers.)
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

        /// <summary>Memory Windows can hand out now, in MB. -1 when it can't be read (not Windows): callers then don't hold anything back.</summary>
        public static long AvailableMB()
        {
            return Read(out var m) ? (long)(m.AvailPhys / 1048576) : -1;
        }

        /// <summary>The PC's memory, in MB. -1 when it can't be read.</summary>
        public static long TotalMB() => Read(out var m) ? (long)(m.TotalPhys / 1048576) : -1;

        /// <summary>The PC's logical processors.</summary>
        public static int Cores() => Environment.ProcessorCount;

        /// <summary>
        /// A process's private memory (what it alone holds, the figure Task Manager calls "commit size"), in MB. -1 when it
        /// can't be read. Cheaper than Process.PrivateMemorySize64, which reads every process on the PC.
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
                return mb;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return -1; }
        }

        /// <summary>"7.5" for 7,680 MB: one decimal, none when it is a whole number.</summary>
        public static string GB(long mb) => (mb / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);
    }
}
