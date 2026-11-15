using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace SFSearch.ServiceTest;

/// <summary>
/// An MCP server driven with the protocol's own JSON lines, so a test decides exactly what is sent and when:
/// a tools/call, then notifications/cancelled for it, as a real client does when the user stops a call.
/// </summary>
public sealed class RawMcp : IDisposable
{
    readonly Process _p;
    public readonly ConcurrentQueue<string> Lines = new();

    RawMcp(Process p) { _p = p; }

    public static async Task<RawMcp> Start(string exe, string home)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--home"); psi.ArgumentList.Add(home);
        psi.Environment["SFSEARCH_HOME"] = home;
        var m = new RawMcp(Process.Start(psi)!);
        m._p.OutputDataReceived += (_, e) => { if (e.Data != null) m.Lines.Enqueue(e.Data); };
        m._p.ErrorDataReceived += (_, _) => { };
        m._p.BeginOutputReadLine(); m._p.BeginErrorReadLine();
        m.Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"sfsearch-servicetest\",\"version\":\"1\"}}}");
        if (await m.WaitFor(l => l.Contains("\"id\":1"), 15000) == null) throw new IOException("the MCP server didn't answer initialize");
        m.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        return m;
    }

    public void Send(string json) { _p.StandardInput.Write(json + "\n"); _p.StandardInput.Flush(); }

    /// <summary>The first line from the server that matches, or null when none came in time.</summary>
    public async Task<string?> WaitFor(Func<string, bool> match, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var l in Lines) if (match(l)) return l;
            await Task.Delay(20);
        }
        return null;
    }

    public void Dispose()
    {
        try { _p.StandardInput.Close(); if (!_p.WaitForExit(3000)) _p.Kill(true); } catch (Exception) { }
        _p.Dispose();
    }
}
