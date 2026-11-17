using System.Runtime.CompilerServices;
using SFSearch.AddOns;
using SFSearch.Service;

namespace SFSearch.AddOn201
{
    /// <summary>
    /// Calls JobContext's methods as an add-on built for SFSearch 2.0.1 calls them (J81). 2.1 gave RequireHeavy, NotRun
    /// and NotAllowedLine a new optional last parameter; built against 2.0.1, these calls name the old signatures, and
    /// SFSearch 2.1 keeps them as overloads. Without them every command of this add-on is refused with "Method not found".
    /// </summary>
    public sealed class ApiCommands : ISfsExtension
    {
        public IReadOnlyList<(string Name, string Help)> Commands { get; } =
        [
            ("RequireHeavy", "  RequireHeavy         RequireHeavy(estimateMB, what): a small part (true) and a part too big to be allowed (false, Not run.)"),
            ("NotRun", "  NotRun               NotRun(line, estimateMB, what): the Not run. line and its exit code"),
            ("NotAllowedLine", "  NotAllowedLine       NotAllowedLine(what, estimateMB): the line, printed; exits 0"),
        ];

        // Each call sits in its own method, so a missing signature fails only the command that calls it (the runtime
        // looks a method's calls up when it first runs that method)
        public void Run(string command, List<string> args)
        {
            switch (command)
            {
                case "RequireHeavy": CallRequireHeavy(); break;
                case "NotRun": CallNotRun(); break;
                case "NotAllowedLine": CallNotAllowedLine(); break;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void CallRequireHeavy()
        {
            Console.WriteLine($"small part: {JobContext.RequireHeavy(100, "The probe's small part")}");
            Console.WriteLine($"big part: {JobContext.RequireHeavy(1_000_000, "The probe's big part")}");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void CallNotRun() => JobContext.NotRun("Not run. The probe's report would read about 3 GB.", 3000, "The probe's report");

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void CallNotAllowedLine() => Console.WriteLine(JobContext.NotAllowedLine("The probe's table", 3000));
    }
}
