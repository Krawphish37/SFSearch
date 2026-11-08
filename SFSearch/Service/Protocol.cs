using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SFSearch.Service
{
    // Shared by SFSearch (the service, its workers, the command-line client) and SFSearch.Mcp (the MCP adapter).
    // Every message is one line of JSON over a named pipe.

    /// <summary>A caller's request to the service.</summary>
    public sealed class Request
    {
        /// <summary>run | job | cancel | status | stop | ping</summary>
        public string Op { get; set; } = "run";
        public string[]? Args { get; set; }
        public string? Cwd { get; set; }
        /// <summary>Who is asking: each agent gets its own line in the queue, so one agent can't starve another.</summary>
        public string? Agent { get; set; }
        /// <summary>How long to wait for the answer. -1 waits until it's done; otherwise the reply may say "still running" with a job number.</summary>
        public int WaitMs { get; set; } = -1;
        public string? Job { get; set; }
    }

    /// <summary>The service's answer.</summary>
    public sealed class Reply
    {
        public bool Done { get; set; }
        public string? Job { get; set; }
        public string Out { get; set; } = "";
        public string Err { get; set; } = "";
        public int Code { get; set; }
        public long Ms { get; set; }
        /// <summary>Set when the service couldn't run the request at all (busy, unknown job, stopped).</summary>
        public string? Error { get; set; }
        /// <summary>Jobs ahead of this one when it isn't done yet.</summary>
        public int Ahead { get; set; }
        public string? State { get; set; }
    }

    /// <summary>From the coordinator to a worker: run this command.</summary>
    public sealed class WorkOrder
    {
        public string Job { get; set; } = "";
        public string[] Args { get; set; } = Array.Empty<string>();
        public string? Cwd { get; set; }
    }

    /// <summary>From a worker to the coordinator.</summary>
    public sealed class WorkerEvent
    {
        /// <summary>ready | blocked | unblocked | done</summary>
        public string Evt { get; set; } = "";
        public string? Job { get; set; }
        public string? Out { get; set; }
        public string? Err { get; set; }
        public int Code { get; set; }
        public long Ms { get; set; }
        public long MemMB { get; set; }
        public string? On { get; set; }
        public string[]? Used { get; set; }
    }

    public static class Wire
    {
        static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        static readonly UTF8Encoding Utf8 = new(false);

        public static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Utf8.GetBytes(text.ToLowerInvariant())))[..12];

        /// <summary>The service's pipe for one SFSearch home folder.</summary>
        public static string PipeName(string home) => "sfsearch_service_" + Hash(Path.GetFullPath(home).TrimEnd('\\', '/'));

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
        public static T? Deserialize<T>(string line) => JsonSerializer.Deserialize<T>(line, Options);

        public static StreamReader Reader(Stream s) => new(s, Utf8, false, 1 << 16, leaveOpen: true);
        public static StreamWriter Writer(Stream s) => new(s, Utf8, 1 << 16, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };

        /// <summary>Sends one request and waits for its reply. Throws IOException or TimeoutException when the service can't be reached.</summary>
        public static async Task<Reply> CallAsync(string home, Request request, int connectMs, CancellationToken ct)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(home), PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectMs, ct).ConfigureAwait(false);
            using var w = Writer(pipe);
            await w.WriteLineAsync(Serialize(request).AsMemory(), ct).ConfigureAwait(false);
            await w.FlushAsync(ct).ConfigureAwait(false);
            using var r = Reader(pipe);
            string? line = await r.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) throw new IOException("The service closed the connection without answering.");
            return Deserialize<Reply>(line) ?? throw new IOException("The service sent an answer that couldn't be read.");
        }

        /// <summary>The service's marker file: process id | start time | program path. Null when no live service owns it.</summary>
        public static Process? RunningService(string home)
        {
            string marker = Path.Combine(home, "Cache", "service.txt");
            try
            {
                if (!File.Exists(marker)) return null;
                var parts = File.ReadAllText(marker).Split('|');
                if (int.TryParse(parts[0], out int pid))
                {
                    try
                    {
                        var p = Process.GetProcessById(pid);
                        if (!p.HasExited && p.ProcessName.StartsWith("SFSearch", StringComparison.OrdinalIgnoreCase)) return p;
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
    }
}
