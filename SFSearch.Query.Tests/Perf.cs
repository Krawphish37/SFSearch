// sfsquery-tests --perf: what one read into the scratch tables takes, the first time in a process and once the
// code is warm, and where the time goes. The numbers of design\agent-first\notes\perf-reads.md are made here.
//
//   sfsquery-tests --perf <Data folder> [--sql "<sql>"] [--plugins Starfield.esm] [--rounds 3] [--threads 8]
//                  [--direct <n>] [--heavy] [--work <folder>]
//
// Each round runs the same query on a query engine of its own with empty scratch tables, in the same process: the
// plugins stay open, so round 1 has the code's start-up in it and the later rounds don't. For each round: the
// seconds, what was read, the garbage collector's pauses, and the processor time of the whole process, all measured
// from outside the library. (Until J81 the reading threads' time by step and the inserting thread's wait came from
// timers inside RecordReader; they left the library with the rule that no test code goes into the program. A
// profiler gives that split now.)
// The Data folder is only read. Run it through heavy.sh, with HEAVY_FULL=1 for a world type (--heavy).
using System.Diagnostics;
using SFSearch.Index;
using SFSearch.Query;
using SFSearch.Records;

namespace SFSearch.Query.Tests;

static class Perf
{
    public static int Run(string[] args)
    {
        var rest = args.Skip(2).ToList();
        string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
        string data = Path.GetFullPath(args[1]);
        string sql = Opt("--sql") ?? "SELECT count(*) FROM DialogResponses";
        string[] plugins = (Opt("--plugins") ?? "Starfield.esm").Split(',');
        int rounds = int.Parse(Opt("--rounds") ?? "3"), threads = int.Parse(Opt("--threads") ?? "8"), direct = int.Parse(Opt("--direct") ?? "0");
        bool heavy = rest.Remove("--heavy");
        string work = Path.GetFullPath(Opt("--work") ?? Path.Combine(Path.GetTempPath(), "sfsquery-perf"));
        Directory.CreateDirectory(work);

        var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
        using var index = new HeaderIndex(PluginSource.Folder(data), new IndexOptions { ScratchFolder = Path.Combine(work, "index"), VersionText = $"sfsquery-tests 1 / mutagen {schema.MutagenVersion} / schema {schema.Version}" });
        index.Refresh();
        using var records = new RecordEngine(index, schema, Path.Combine(work, "verify"), data);
        using var me = Process.GetCurrentProcess();
        Console.WriteLine($"{sql}   on {string.Join(", ", plugins)}; {threads} reading threads{(direct > 0 ? $", {direct} of them for records read from their own bytes" : "")}; {Environment.ProcessorCount} logical processors");
        Console.WriteLine("Round\tSeconds\tGcPauseSeconds\tGen0\tGen1\tGen2\tAllocatedMB\tProcessorSeconds\tAnswer\tRead");
        for (int round = 1; round <= rounds; round++)
        {
            string folder = Path.Combine(work, "perf-sql");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var pause = GC.GetTotalPauseDuration(); long alloc = GC.GetTotalAllocatedBytes(true);
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            me.Refresh(); var cpu = me.TotalProcessorTime;
            QueryAnswer a;
            using (var engine = new QueryEngine(records, new QueryOptions { ScratchFolder = folder, Threads = threads, DirectReaders = direct }))
                a = engine.Run(new QueryRequest { Sql = sql, Plugins = plugins, AllowHeavy = heavy, PageRows = 5 });
            me.Refresh();
            string answer = a.Error != null ? "ERROR " + a.Error : a.NotRun != null ? "NOT RUN " + a.NotRun.What : string.Join(" ; ", a.Rows.Take(3).Select(r => string.Join(" | ", r.Select(Ctx.Cell))));
            Console.WriteLine($"{round}\t{a.Cost.Seconds:F2}\t{(GC.GetTotalPauseDuration() - pause).TotalSeconds:F2}\t{GC.CollectionCount(0) - g0}\t{GC.CollectionCount(1) - g1}\t{GC.CollectionCount(2) - g2}\t{(GC.GetTotalAllocatedBytes(true) - alloc) / 1048576.0:F0}\t{(me.TotalProcessorTime - cpu).TotalSeconds:F2}\t{answer}\t{string.Join("; ", a.Cost.Read)}");
            Console.Out.Flush();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
        Console.WriteLine($"peak working set {me.PeakWorkingSet64 >> 20:N0} MB");
        return 0;
    }
}
