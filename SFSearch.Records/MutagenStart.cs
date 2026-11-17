// Mutagen's one-time start-up, paid once at the start of a program that will read records instead of inside the
// first read (fix g-sql, 2026-11-13, from the Mutagen session's measurement on fixes.13): a Starfield.esm overlay's
// first EnumerateMajorRecords costs 1.07 to 1.43 s, of which Mutagen.Bethesda.Plugins.Warmup.Init() (Loqui's
// registration of every record type) is about 0.93 s; after it, open and enumerate Weapon is 84 to 87 ms.
namespace SFSearch.Records;

public static class MutagenStart
{
    static readonly object _gate = new();
    static bool _done;

    /// <summary>How long Warmup.Init took in this program, in ms (0 before it ran).</summary>
    public static double Ms { get; private set; }

    /// <summary>Runs Mutagen's Warmup.Init once in this program; later calls return at once. A call while
    /// <see cref="Begin"/>'s run is under way waits for it.</summary>
    public static void Run()
    {
        lock (_gate)
        {
            if (_done) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Mutagen.Bethesda.Plugins.Warmup.Init();
            Ms = clock.Elapsed.TotalMilliseconds;
            _done = true;
        }
    }

    static int _begun;

    /// <summary>
    /// Starts <see cref="Run"/> on another thread and returns at once, for a command that may need Mutagen only late
    /// or not at all: assets2 reads most records from their bytes (GAPS S30), and the header index asks Mutagen only
    /// about a plugin that repeats a FormKey. The start-up then runs beside the work that needs no Mutagen. Mutagen's
    /// own code that needs it (Warmup.Init, which holds a lock) waits for it to finish.
    /// </summary>
    public static void Begin()
    {
        if (System.Threading.Interlocked.Exchange(ref _begun, 1) == 1) return;
        lock (_gate) { if (_done) return; }
        System.Threading.Tasks.Task.Run(Run);
    }
}
