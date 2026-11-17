namespace SFSearch.ServiceTest;

public static partial class T
{
    /// <summary>
    /// addon201 --exe SFSearch.exe: an add-on built for SFSearch 2.0.1 that calls JobContext.RequireHeavy, NotRun and
    /// NotAllowedLine by their 2.0.1 signatures loads and runs in this build (J81: 2.1 keeps those signatures as
    /// overloads). Needs the AddOn201 add-on in the test home (no service running there):
    ///   dotnet build tests\SFSearch.AddOn201 -c Release -p:SfsExtensionsDir=&lt;test home&gt;\extensions
    /// It is built against the tag v2.0.1's SFSearch.dll; the first check holds it to that.
    /// </summary>
    static async Task AddOn201()
    {
        if (Exe.Length == 0) { Check("addon201 needs --exe <SFSearch.exe>", false); return; }
        if (!File.Exists(Path.Combine(Home, "extensions", "SFSearch.AddOn201.dll")))
        {
            Check("the AddOn201 add-on is in the test home", false, @"build it: dotnet build tests\SFSearch.AddOn201 -c Release -p:SfsExtensionsDir=<test home>\extensions");
            return;
        }
        var (list, _, listCode) = await RunExe(Exe, ["Extensions"], 120000);
        Check("Extensions: the add-on loads, built for SFSearch 2.0.1", listCode == 0 && list.Contains("SFSearch.AddOn201.dll, built for SFSearch 2.0.1", StringComparison.Ordinal),
            $"exit code {listCode}; {list.Split('\n').FirstOrDefault(l => l.Contains("AddOn201", StringComparison.Ordinal))?.Trim() ?? "no line names it"}");

        // Exit code 7 is JobContext.NotAllowedCode: a heavy part the caller didn't allow
        var cases = new (string Command, int Code, string[] Says)[]
        {
            ("AddOn201.RequireHeavy", 7, ["small part: True", "Not run. The probe's big part needs about ", "big part: False"]),
            ("AddOn201.NotRun", 7, ["Not run. The probe's report would read about 3 GB."]),
            ("AddOn201.NotAllowedLine", 0, ["Not run. The probe's table needs about ", "allow_heavy=true"]),
        };
        foreach (var (command, code, says) in cases)
        {
            var (o, e, c) = await RunExe(Exe, [command], 120000);
            var missing = says.Where(s => !o.Contains(s, StringComparison.Ordinal)).ToList();
            Check($"{command}: exit code {code} and its text", c == code && missing.Count == 0 && !o.Contains("Method not found", StringComparison.Ordinal),
                $"exit code {c}{(missing.Count > 0 ? "; missing: " + string.Join(" | ", missing) : "")}; first line: {o.Split('\n')[0].Trim()}{(e.Trim().Length > 0 ? "; stderr: " + e.Trim().Split('\n')[0] : "")}");
        }
    }
}
