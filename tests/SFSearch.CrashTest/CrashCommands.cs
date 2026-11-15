using SFSearch.AddOns;

namespace SFSearch.CrashTest
{
    /// <summary>
    /// Commands that fail on purpose, each in another way, to check that a failing add-on command never exits 0 (S43).
    /// </summary>
    public sealed class CrashCommands : ISfsExtension
    {
        public IReadOnlyList<(string Name, string Help)> Commands { get; } =
        [
            ("Fine", "  Fine                 Prints one line and exits 0 (the control)"),
            ("Throw", "  Throw                Throws an InvalidOperationException"),
            ("ParallelThrow", "  ParallelThrow        Reads past the end of a list inside Parallel.For, as HeaderIndex.Refresh did in S43"),
            ("ApiError", "  ApiError             Throws the add-on API's own AddOnException without catching it"),
        ];

        public void Run(string command, List<string> args)
        {
            switch (command)
            {
                case "Fine":
                    Console.WriteLine("fine");
                    break;
                case "Throw":
                    throw new InvalidOperationException("the CrashTest add-on threw on purpose");
                case "ParallelThrow":
                    IReadOnlyList<int> sizes = new int[2];
                    Parallel.For(0, 4, i => { _ = sizes[i]; });
                    break;
                case "ApiError":
                    throw new AddOnException("the CrashTest add-on's API error", "nothing to fix: the test asks for it");
            }
        }
    }
}
