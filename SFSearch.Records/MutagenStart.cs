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

    /// <summary>Runs Mutagen's Warmup.Init once in this program; later calls return at once.</summary>
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
}
