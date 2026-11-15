using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SFSearch.Service
{
    /// <summary>
    /// `__test`: small commands the service's own tests drive it with (tests\SFSearch.ServiceTest). They read no plugin.
    /// They only exist when the service was started with SFSEARCH_TEST_HOOKS=1; otherwise `__test` is an unknown command.
    /// </summary>
    public static class TestHooks
    {
        public static bool Enabled => Environment.GetEnvironmentVariable("SFSEARCH_TEST_HOOKS") == "1";

        public static void Run(List<string> args)
        {
            string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "";
            int N(int i, int fallback) => args.Count > i && int.TryParse(args[i], out int v) ? v : fallback;
            switch (sub)
            {
                case "echo":
                    Console.WriteLine(string.Join(' ', args.GetRange(1, args.Count - 1)));
                    break;
                case "sleep":
                    {
                        // sleep <ms>: stays busy for that long
                        int ms = N(1, 1000);
                        var clock = Stopwatch.StartNew();
                        while (clock.ElapsedMilliseconds < ms) Thread.Sleep(Math.Min(50, Math.Max(1, ms - (int)clock.ElapsedMilliseconds)));
                        Console.WriteLine($"slept {ms} ms from {Stamp(DateTime.UtcNow.AddMilliseconds(-clock.ElapsedMilliseconds))} to {Stamp(DateTime.UtcNow)}");
                        break;
                    }
                case "rows":
                    {
                        // rows <n> [width] [header 0|1]: a table of n rows, to page through
                        int n = N(1, 100), width = N(2, 40);
                        bool declare = N(3, 0) == 1;
                        Console.WriteLine("| Row | Text |");
                        Console.WriteLine("|---|---|");
                        if (declare) JobContext.TableHeader(2);
                        for (int i = 1; i <= n; i++) Console.WriteLine($"| {i} | {new string((char)('a' + i % 26), width)} |");
                        break;
                    }
                case "lines":
                    {
                        // lines <n>: plain lines, no table, with a note on stderr
                        int n = N(1, 100);
                        for (int i = 1; i <= n; i++) Console.WriteLine($"line {i} of {n}");
                        Console.Error.WriteLine("[test] a note from the command");
                        break;
                    }
                case "heavy":
                    {
                        // heavy <estimateMB> <holdMs> [allocMB]: asks for the heavy lane, then holds it (and, when asked, real memory)
                        int est = N(1, 4096), hold = N(2, 1000), alloc = N(3, 0);
                        if (!JobContext.RequireHeavy(est, $"The test's heavy part ({est} MB)")) return;
                        var from = DateTime.UtcNow;
                        var blocks = Allocate(alloc);
                        Thread.Sleep(hold);
                        Console.WriteLine($"heavy ran: estimate {est} MB, held {blocks.Count * 64} MB from {Stamp(from)} to {Stamp(DateTime.UtcNow)}");
                        GC.KeepAlive(blocks);
                        break;
                    }
                case "alloc":
                    {
                        // alloc <MB> <holdMs>: takes real memory without asking the heavy lane (for the worker limits)
                        var blocks = Allocate(N(1, 256));
                        Thread.Sleep(N(2, 1000));
                        Console.WriteLine($"held {blocks.Count * 64} MB");
                        GC.KeepAlive(blocks);
                        break;
                    }
                case "keep":
                    {
                        // keep <MB>: takes memory and keeps it after the command, like a worker with plugins open
                        _kept.AddRange(Allocate(N(1, 256)));
                        Console.WriteLine($"keeping {_kept.Count * 64} MB");
                        break;
                    }
                case "yieldable":
                    {
                        // yieldable <ms>: an indexer job that stops early when the coordinator asks
                        int ms = N(1, 5000);
                        var clock = Stopwatch.StartNew();
                        while (clock.ElapsedMilliseconds < ms && !JobContext.YieldRequested) Thread.Sleep(20);
                        if (JobContext.YieldRequested && clock.ElapsedMilliseconds < ms) { JobContext.Yielded = true; Console.WriteLine($"gave way after {clock.ElapsedMilliseconds} ms"); }
                        else Console.WriteLine($"indexed for {ms} ms, finished at {Stamp(DateTime.UtcNow)}");
                        break;
                    }
                case "scratch":
                    {
                        // scratch [file]: the scratch folder's path; with a name, also leaves a file there
                        string? dir = JobContext.ScratchDir;
                        Console.WriteLine(dir ?? "(no scratch folder outside the service)");
                        if (dir != null && args.Count > 1) File.WriteAllText(Path.Combine(dir, Path.GetFileName(args[1])), "left by a test\n");
                        break;
                    }
                case "source":
                    // source: a command that says where its answer came from and what it read
                    JobContext.AddSource("example.esm", 26474052, new DateTime(2026, 9, 2, 3, 51, 0, DateTimeKind.Utc), "5e1c0000");
                    JobContext.AddRead("Weapon (456) from example.esm");
                    Console.WriteLine("an answer");
                    break;
                case "fail":
                    JobContext.Fail("the test asked for a failure", "ask for something else");
                    break;
                case "crash":
                    Environment.Exit(99);
                    break;
                default:
                    Console.WriteLine("Usage: __test echo|sleep|rows|lines|heavy|alloc|keep|yieldable|scratch|source|fail|crash ...");
                    break;
            }
        }

        static readonly List<byte[]> _kept = new();

        static string Stamp(DateTime utc) => utc.ToString("HH:mm:ss.fff");

        /// <summary>Takes memory in 64 MB blocks and writes to every page, so Windows really hands it over.</summary>
        static List<byte[]> Allocate(int mb)
        {
            var blocks = new List<byte[]>();
            for (int got = 0; got < mb; got += 64)
            {
                var b = new byte[64 * 1048576];
                for (int i = 0; i < b.Length; i += 4096) b[i] = 1;
                blocks.Add(b);
            }
            return blocks;
        }
    }
}
