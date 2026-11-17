using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SFSearch.Service;

namespace SFSearch.ServiceTest;

/// <summary>
/// `historylook --exe &lt;SFSearch.exe&gt; [--data &lt;Data folder to copy from&gt;]` (2026-11-13, fix S11 of
/// design\agent-first\GAPS.md): the service looks for changed plugins by itself, so changes2 tells what changed since
/// the service last looked, each change dated by that look, and says which look it compares with. It makes its own
/// home and test Data folder under the test home (three small plugins copied from Data, which is only read), starts its
/// own service there with the idle check every 10 s (pluginCheckSeconds in its sfsearch.json), changes the copies (one updated in
/// place, one removed, one added), waits for the service's look, and asks changes2. Stops its service at the end.
/// </summary>
public static partial class T
{
    static readonly string[] HistoryPlugins = { "weap_starlight.esm", "atom74.esm", "shipselfdestruct.esm" };
    const string HistoryAdded = "aa50_longwatch.esm";

    static async Task HistoryLook(string? dataFrom)
    {
        if (!File.Exists(Exe)) { Check("historylook: --exe names SFSearch.exe", false, Exe); return; }
        dataFrom ??= Environment.GetEnvironmentVariable("SFSEARCH_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";
        string root = Path.Combine(Home, "historylook"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins"), txt = Path.Combine(root, "plugins.txt");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("historylook: the test Data folder is outside the game's", false, data); return; }
        string outer = Home;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        foreach (var p in HistoryPlugins) { File.Copy(Path.Combine(dataFrom, p), Path.Combine(data, p)); File.SetAttributes(Path.Combine(data, p), FileAttributes.Normal); }
        File.WriteAllLines(txt, HistoryPlugins.Select(p => "*" + p));
        var env = new Dictionary<string, string>
        {
            ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt, ["SFSEARCH_WORKERS"] = "2",
        };
        // The service's idle check every 10 s: a real setting (J37; it was the test switch SFSEARCH_TEST_INDEX_EVERY)
        string cfgPath = Path.Combine(home, "sfsearch.json");
        File.WriteAllText(cfgPath, "{ \"settingsVersion\": 2, \"pluginCheckSeconds\": 10 }");
        Home = home;
        try
        {
            var started = await RunWith(env, "Service", "start");
            if (!started.Out.Contains("Service started")) { Check("historylook: the service starts", false, started.Out + started.Err); return; }

            // 1. The service begins the history by itself, at its start
            long ms = await UntilStatus(s => Regex.IsMatch(s, @"^History \(changes2\): .*the last: looked at 3 plugins at .*\(the service, at its start\).*the history begins: 3 plugins remembered", RegexOptions.Multiline), 60000);
            string status = await Status();
            Check("historylook: the service begins the history at its start", ms >= 0, $"{(ms >= 0 ? $"after {ms / 1000.0:0.0} s" : "not within 60 s")}: {Line(status, "History (changes2):")}");

            // 2. changes2 compares with that look, and nothing changed
            var r = await Call(Cmd("changes2"));
            Check("historylook: changes2 says which look it compares with (the service's, at its start)", r.Code == 0 && Regex.IsMatch(r.Out, @"compared with the look of \d{4}-\d\d-\d\d \d\d:\d\d by the service, at its start\)") && r.Out.Contains("No plugin was added, removed or changed") && !r.Out.Contains("The history begins now"),
                Line(r.Out, "Changes in Data since") is { Length: > 0 } head ? head : $"exit {r.Code}: {r.Out.Trim()} {r.Err.Trim()}");

            // 3. The copies change: one updated in place (one EditorID letter), one removed, one added
            string upd = Path.Combine(data, HistoryPlugins[0]);
            byte[] bytes = File.ReadAllBytes(upd);
            int at = bytes.AsSpan().IndexOf("EDID"u8);
            int len = bytes[at + 4] | bytes[at + 5] << 8;
            int last = at + 6 + len - 2; // the last letter before the closing zero
            bytes[last] = bytes[last] == (byte)'Q' ? (byte)'R' : (byte)'Q';
            File.WriteAllBytes(upd, bytes);
            File.Delete(Path.Combine(data, HistoryPlugins[2]));
            File.Copy(Path.Combine(dataFrom, HistoryAdded), Path.Combine(data, HistoryAdded));
            File.SetAttributes(Path.Combine(data, HistoryAdded), FileAttributes.Normal);
            File.WriteAllLines(txt, HistoryPlugins.Take(2).Append(HistoryAdded).Select(p => "*" + p));
            var changedAt = DateTime.Now;
            Say($"at {changedAt:HH:mm:ss}: {HistoryPlugins[0]} changed in place (one EditorID letter), {HistoryPlugins[2]} removed, {HistoryAdded} added");

            // 4. The service's idle check sees it, with no call
            ms = await UntilStatus(s => Regex.IsMatch(s, @"^History \(changes2\): .*\(the service's idle check\).*: 3 plugins changed since", RegexOptions.Multiline), 90000);
            status = await Status();
            Check("historylook: the service's idle check sees the changes by itself", ms >= 0, $"{(ms >= 0 ? $"{ms / 1000.0:0.0} s after the change" : "not within 90 s")}: {Line(status, "History (changes2):")}");

            // 5. changes2 afterwards: each change dated by the service's look, not "now"; the answer says which look
            await Task.Delay(1100); // so this call's own time can't be the look's second
            r = await Call(Cmd("changes2", "--json"));
            string json = r.Out.Trim();
            int brace = json.IndexOf('{');
            if (brace < 0) { Check("historylook: changes2 --json answers", false, $"exit {r.Code}: {r.Out.Trim()} {r.Err.Trim()}"); return; }
            using var outerDoc = JsonDocument.Parse(json[brace..]);
            // --json: the reply's parts as one object; its "answer" is the report's own JSON, as text
            using var doc = outerDoc.RootElement.TryGetProperty("answer", out var inner) && inner.ValueKind == JsonValueKind.String ? JsonDocument.Parse(inner.GetString()!) : JsonDocument.Parse(json[brace..]);
            // The verb's --json form: the report's fields under "data", beside "notes", "source" and "cost"
            var root2 = doc.RootElement.TryGetProperty("data", out var data2) ? data2 : doc.RootElement;
            var cw = root2.GetProperty("comparedWith");
            var rows = root2.GetProperty("changes").EnumerateArray().Select(c => (Plugin: c.GetProperty("plugin").GetString(), Kind: c.GetProperty("kind").GetString(), When: c.GetProperty("when").GetString(), Now: c.GetProperty("foundNow").GetBoolean())).ToList();
            bool rightRows = rows.Count == 3 && rows.All(x => !x.Now && x.When == cw.GetProperty("seen").GetString())
                && rows.Any(x => x.Plugin == HistoryPlugins[0] && x.Kind == "updated") && rows.Any(x => x.Plugin == HistoryPlugins[2] && x.Kind == "removed") && rows.Any(x => x.Plugin == HistoryAdded && x.Kind == "added");
            Check("historylook: changes2 lists the 3 changes, each dated by the service's look (not \"now\")",
                cw.GetProperty("by").GetString() == "service-check" && rightRows,
                $"compared with {cw.GetProperty("seen").GetString()} by {cw.GetProperty("byWords").GetString()}; rows: {string.Join("; ", rows.Select(x => $"{x.When} {x.Plugin} {x.Kind}{(x.Now ? " (now)" : "")}"))}");
            r = await Call(Cmd("changes2", HistoryPlugins[0]));
            Check("historylook: one plugin's history says its look too, and what the update did", Regex.IsMatch(r.Out, @"History of weap_starlight\.esm \(.*2 snapshots; compared with the look of .* by a changes call\)") && r.Out.Contains("0 added, 0 removed"),
                Line(r.Out, "History of") + " / " + Line(r.Out, "Records, from") + $" (exit {r.Code}{(r.Out.Contains("History of") ? "" : ": " + Cut(r.Out + r.Err, 400))})");
            r = await Call(Cmd("changes2"));
            Check("historylook: the text form names the look it compares with", Regex.IsMatch(r.Out, @"compared with the look of \d{4}-\d\d-\d\d \d\d:\d\d by a changes call\)") && r.Out.Contains("A change's time is the look that first saw it"),
                Line(r.Out, "Changes in Data since") + $" (exit {r.Code}{(r.Out.Contains("Changes in Data since") ? "" : ": " + Cut(r.Out + r.Err, 400))})");

            // 6. No look of the service with historyLook off in its sfsearch.json, for a second service start (J37: the
            //    environment override SFSEARCH_HISTORY=0 is gone)
            await RunWith(env, "Service", "stop");
            File.WriteAllText(cfgPath, "{ \"settingsVersion\": 2, \"pluginCheckSeconds\": 10, \"historyLook\": false }");
            await RunWith(env, "Service", "start");
            status = await Status();
            string cfg = (await RunWith(env, "Service", "config")).Out;
            Check("historylook: with historyLook off the service doesn't look", Line(status, "History (changes2):").Contains("no look by the service (historyLook is off") && cfg.Contains("only changes2 looks (historyLook is off)"),
                Line(status, "History (changes2):") + " / " + Line(cfg, "Changed plugins:"));
            await RunWith(env, "Service", "stop");
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }

    static async Task<(string Out, string Err, int Code)> RunWith(Dictionary<string, string> env, params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await o, await e, p.ExitCode);
    }
}
