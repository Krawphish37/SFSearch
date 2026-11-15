namespace SFSearch.ServiceTest;

public static partial class T
{
    /// <summary>
    /// addoncrash --exe SFSearch.exe: an add-on command that throws exits 2 with the error line and the exception's text,
    /// never 0 (GAPS S43). Needs the CrashTest add-on in the test home (no service running there):
    ///   dotnet build tests\SFSearch.CrashTest -c Release -p:SfsExtensionsDir=&lt;test home&gt;\extensions
    /// </summary>
    static async Task AddOnCrash()
    {
        if (Exe.Length == 0) { Check("addoncrash needs --exe <SFSearch.exe>", false); return; }
        if (!File.Exists(Path.Combine(Home, "extensions", "SFSearch.CrashTest.dll")))
        {
            Check("the CrashTest add-on is in the test home", false, @"build it: dotnet build tests\SFSearch.CrashTest -c Release -p:SfsExtensionsDir=<test home>\extensions");
            return;
        }
        var cases = new (string Command, int Code, string[] Says)[]
        {
            ("Fine", 0, ["fine"]),
            ("Throw", 2, ["Error: the add-on command CrashTest.Throw stopped: the CrashTest add-on threw on purpose. Fix: ", "at SFSearch.CrashTest.CrashCommands.Run"]),
            ("CrashTest.ParallelThrow", 2, ["Error: the add-on command CrashTest.ParallelThrow stopped: One or more errors occurred.", "Cause: ArgumentOutOfRangeException: Index was out of range", "Thrown at:"]),
            ("ApiError", 2, ["Error: the CrashTest add-on's API error. Fix: nothing to fix: the test asks for it."]),
        };
        foreach (var (command, code, says) in cases)
        {
            var (o, e, c) = await RunExe(Exe, [command], 120000);
            var missing = says.Where(s => !o.Contains(s, StringComparison.Ordinal)).ToList();
            Check($"{command}: exit code {code} and its text", c == code && missing.Count == 0,
                $"exit code {c}{(missing.Count > 0 ? "; missing: " + string.Join(" | ", missing) : "")}; first line: {o.Split('\n')[0].Trim()}{(e.Trim().Length > 0 ? "; stderr: " + e.Trim().Split('\n')[0] : "")}");
        }
    }
}
