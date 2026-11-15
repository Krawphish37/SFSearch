using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SFSearch.Service
{
    // Shared by SFSearch (the service, its workers, the command-line client) and SFSearch.Mcp (the MCP adapter).
    // Every message is one line of JSON over a named pipe.
    //
    // Two forms live side by side (2026-11-11, milestone M4 of design\agent-first):
    //   - The first form: a request holds Args (what sfs.cmd would take) and the reply holds Out, Err and Code. A client
    //     that only knows this form gets exactly the replies it always got.
    //   - The structured form: a request that sets Structured, Tool, PageChars or Cursor gets the answer in parts:
    //     Answer (one page of it when PageChars is set), More (the cursor for the next page), Notes, Source, Cost, and
    //     Failure (an error with its fix). Out and Err are then left empty.
    // Every added field is left out of the JSON when it isn't set, so the first form is unchanged on the wire.

    /// <summary>A caller's request to the service.</summary>
    public sealed class Request
    {
        /// <summary>run | job | cancel | status | stop | ping | more</summary>
        public string Op { get; set; } = "run";
        public string[]? Args { get; set; }
        public string? Cwd { get; set; }
        /// <summary>Who is asking: each agent gets its own line in the queue, so one agent can't starve another.</summary>
        public string? Agent { get; set; }
        /// <summary>How long to wait for the answer. -1 waits until it's done; otherwise the reply may say "still running" with a job number.</summary>
        public int WaitMs { get; set; } = -1;
        public string? Job { get; set; }

        // ---- Added for the structured form. All optional.
        /// <summary>A tool by name, with Params, in place of Args. "run" takes {"args": [...]} and runs a command.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Tool { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, JsonElement>? Params { get; set; }
        /// <summary>Continue a long answer from here (the cursor a reply's More gave). With run, more or job.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Cursor { get; set; }
        /// <summary>Hand the answer back in pages of about this many characters, cut between lines. 0 = all of it at once.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int PageChars { get; set; }
        /// <summary>Ask for the reply in parts (Answer, Notes, Source, Cost, Failure) without paging.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Structured { get; set; }
        /// <summary>The caller accepts a heavy run. Without it a command that finds itself heavy answers with its estimate and doesn't run.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AllowHeavy { get; set; }
        /// <summary>The caller already knows this is heavy: the estimate in MB. It goes through the heavy lane from the start.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int HeavyMB { get; set; }
        /// <summary>"index" puts the job in the indexer lane: it runs when no agent is waiting and gives way when one arrives.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Lane { get; set; }
        /// <summary>A name the caller picks for this call, so it can cancel it before it has been told the job number.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Key { get; set; }

        [JsonIgnore] public bool WantsParts => Structured || Tool != null || PageChars > 0 || Cursor != null;
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

        // ---- The structured form's parts. Only in replies to requests that asked for them.
        /// <summary>The answer's text: all of it, or one page.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Answer { get; set; }
        /// <summary>Set when the answer has more than one page.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public MoreInfo? More { get; set; }
        /// <summary>Warnings the agent must read (what a command wrote to stderr, and the service's own).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? Notes { get; set; }
        /// <summary>Which copy of which plugins the answer was read from.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SourceInfo[]? Source { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public CostInfo? Cost { get; set; }
        /// <summary>The error and what to do about it, when the command failed or the service couldn't run it.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FailInfo? Failure { get; set; }
        /// <summary>Set when a heavy command wasn't run: the caller didn't allow it, or memory was short.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public HeavyInfo? Heavy { get; set; }
    }

    public sealed class MoreInfo
    {
        /// <summary>The first row (or line) on this page, counting from 1.</summary>
        public int From { get; set; }
        /// <summary>Rows (or lines) on this page.</summary>
        public int Shown { get; set; }
        /// <summary>Rows (or lines) in the whole answer.</summary>
        public int Total { get; set; }
        /// <summary>"rows" when the answer is one table (its header is repeated on every page), else "lines".</summary>
        public string Unit { get; set; } = "lines";
        /// <summary>Send this back as Request.Cursor for the next page. Null on the last page.</summary>
        public string? Cursor { get; set; }
    }

    public sealed class SourceInfo
    {
        public string Plugin { get; set; } = "";
        public long Bytes { get; set; }
        /// <summary>The file's date (UTC, ISO 8601). Empty when the file wasn't found in Data.</summary>
        public string Modified { get; set; } = "";
        /// <summary>The key of that exact copy: it changes when the file's size or date does.</summary>
        public string Key { get; set; } = "";
        /// <summary>A localized plugin's string files (where its texts are read from): the first 8 digits of their stamp,
        /// "none" when it has none. It changes when a loose string file or the archive one is read from changes size or
        /// date. Null for a plugin that isn't localized.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Strings { get; set; }
        /// <summary>The source as the tool words it, when it isn't one plugin copy with a size and a key (a load order, the
        /// archives read, a file outside Data with its folder). Printed as it is. Added with milestone M8.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; set; }
    }

    public sealed class CostInfo
    {
        /// <summary>Time the command ran, in milliseconds.</summary>
        public long Ms { get; set; }
        /// <summary>Time it waited in the queue first.</summary>
        public long QueuedMs { get; set; }
        /// <summary>The worker's memory when the command ended.</summary>
        public long MemMB { get; set; }
        /// <summary>What had to be read from the plugins for this answer. Empty when everything was already read.</summary>
        public string[]? Read { get; set; }
    }

    public sealed class FailInfo
    {
        public string Message { get; set; } = "";
        public string? Fix { get; set; }
    }

    public sealed class HeavyInfo
    {
        public int EstimateMB { get; set; }
        public string? What { get; set; }
        /// <summary>not_allowed (call again with AllowHeavy) | no_memory (try later)</summary>
        public string Reason { get; set; } = "";
        /// <summary>What Windows had available when the service decided, in MB.</summary>
        public long AvailableMB { get; set; }
    }

    /// <summary>From the coordinator to a worker: run this command (or, mid-command: grant, deny, yield).</summary>
    public sealed class WorkOrder
    {
        /// <summary>run (default) | grant | deny | yield</summary>
        public string Kind { get; set; } = "run";
        public string Job { get; set; } = "";
        public string[] Args { get; set; } = Array.Empty<string>();
        public string? Cwd { get; set; }
        public string? Tool { get; set; }
        public Dictionary<string, JsonElement>? Params { get; set; }
        public bool AllowHeavy { get; set; }
        /// <summary>run: the estimate the coordinator already admitted. deny: the estimate that was refused.</summary>
        public int HeavyMB { get; set; }
        /// <summary>deny: not_allowed | no_memory</summary>
        public string? Reason { get; set; }
        public long AvailableMB { get; set; }
        /// <summary>deny, no_memory: what the job needed with the spare the service keeps free, in MB.</summary>
        public long NeedMB { get; set; }
    }

    /// <summary>From a worker to the coordinator.</summary>
    public sealed class WorkerEvent
    {
        /// <summary>ready | blocked | unblocked | done | heavy</summary>
        public string Evt { get; set; } = "";
        public string? Job { get; set; }
        public string? Out { get; set; }
        public string? Err { get; set; }
        public int Code { get; set; }
        public long Ms { get; set; }
        public long MemMB { get; set; }
        public string? On { get; set; }
        /// <summary>heavy: the estimate the command asks to be admitted with.</summary>
        public int EstimateMB { get; set; }
        public string? What { get; set; }
        // done: the structured parts the command gave
        public SourceInfo[]? Source { get; set; }
        public string[]? Read { get; set; }
        /// <summary>Lines at the top of Out that are a table's header, repeated on every page.</summary>
        public int HeaderLines { get; set; }
        public FailInfo? Failure { get; set; }
        public HeavyInfo? Heavy { get; set; }
        /// <summary>done: the tool cut its own answer and gives the cursor for its next rows.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public MoreInfo? More { get; set; }
        /// <summary>An indexer job that stopped early to let an agent through: it is queued again.</summary>
        public bool Yielded { get; set; }
        /// <summary>done: how many plugins this worker holds open as Mutagen overlays (the new tools' record layer).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int OpenPlugins { get; set; }
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

        /// <summary>A short name for one call, for Request.Key.</summary>
        public static string NewKey() => Guid.NewGuid().ToString("N")[..16];

        /// <summary>
        /// A structured reply as the text of the output contract (design\agent-first\ARCHITECTURE.md section 7): the answer,
        /// then [more], [notes], [source] and [cost] lines for the parts that are set.
        /// </summary>
        public static string Render(Reply r)
        {
            var sb = new StringBuilder(r.Answer ?? r.Out);
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            if (r.Failure != null && sb.Length == 0)
                sb.Append(r.Failure.Message).Append('\n');
            if (r.More is { } m)
            {
                string range = m.From <= 1 ? $"{m.Shown:N0} of {m.Total:N0} {m.Unit} shown" : $"{m.Unit} {m.From:N0} to {m.From + m.Shown - 1:N0} of {m.Total:N0} shown";
                sb.Append("[more] ").Append(range).Append(m.Cursor != null ? $". Next: cursor \"{m.Cursor}\".\n" : ". That is the end.\n");
            }
            foreach (var n in r.Notes ?? Array.Empty<string>()) sb.Append("[notes] ").Append(n).Append('\n');
            if (r.Source is { Length: > 0 } src)
            {
                sb.Append("[source] ");
                for (int i = 0; i < src.Length; i++)
                {
                    if (i > 0) sb.Append("; ");
                    if (src[i].Text != null) { sb.Append(src[i].Text); continue; }
                    sb.Append(src[i].Plugin).Append(' ').Append(Size(src[i].Bytes));
                    if (src[i].Key.Length > 0) sb.Append(", key ").Append(src[i].Key);
                    if (src[i].Strings != null) sb.Append(", strings ").Append(src[i].Strings);
                }
                sb.Append('\n');
            }
            if (r.Cost is { } c)
            {
                sb.Append("[cost] ").Append(c.Ms < 1000 ? $"{c.Ms} ms" : $"{c.Ms / 1000.0:0.##} s");
                if (c.Read is { Length: > 0 }) sb.Append("; read ").Append(string.Join(", ", c.Read));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>
        /// A structured reply as one JSON object (format "json", command line --json): the same parts as fields.
        /// "answer" is the answer's text. When that text holds a Markdown table, "table" has its columns and rows as
        /// lists (cells as text). When the tool itself answered with a JSON object (sfs_query, sfs_saved, sfs_compare
        /// and the reports, asked for json), that object is "data" and there is no "answer".
        /// </summary>
        public static string RenderJson(Reply r)
        {
            var o = new System.Text.Json.Nodes.JsonObject();
            string text = r.Answer ?? r.Out;
            System.Text.Json.Nodes.JsonNode? data = null;
            string t = text.TrimStart();
            if (t.StartsWith('{'))
                try { data = System.Text.Json.Nodes.JsonNode.Parse(t); } catch (JsonException) { }
            if (data != null) o["data"] = data;
            else
            {
                o["answer"] = text;
                if (TableOf(text) is { } table) o["table"] = table;
            }
            if (r.Failure != null) o["error"] = new System.Text.Json.Nodes.JsonObject { ["message"] = r.Failure.Message, ["fix"] = r.Failure.Fix };
            else if (r.Error != null) o["error"] = new System.Text.Json.Nodes.JsonObject { ["message"] = r.Error };
            if (r.Heavy != null) o["heavy"] = new System.Text.Json.Nodes.JsonObject { ["estimateMB"] = r.Heavy.EstimateMB, ["what"] = r.Heavy.What, ["reason"] = r.Heavy.Reason, ["availableMB"] = r.Heavy.AvailableMB };
            if (r.More is { } m) o["more"] = new System.Text.Json.Nodes.JsonObject { ["from"] = m.From, ["shown"] = m.Shown, ["total"] = m.Total, ["unit"] = m.Unit, ["cursor"] = m.Cursor };
            o["notes"] = new System.Text.Json.Nodes.JsonArray((r.Notes ?? Array.Empty<string>()).Select(n => (System.Text.Json.Nodes.JsonNode?)n).ToArray());
            o["source"] = new System.Text.Json.Nodes.JsonArray((r.Source ?? Array.Empty<SourceInfo>()).Select(x => (System.Text.Json.Nodes.JsonNode?)(x.Text != null && x.Bytes == 0 && x.Key.Length == 0
                ? new System.Text.Json.Nodes.JsonObject { ["text"] = x.Text }
                : new System.Text.Json.Nodes.JsonObject { ["plugin"] = x.Plugin, ["bytes"] = x.Bytes, ["key"] = x.Key, ["text"] = x.Text })).ToArray());
            if (r.Cost is { } c)
                o["cost"] = new System.Text.Json.Nodes.JsonObject { ["ms"] = c.Ms, ["queuedMs"] = c.QueuedMs, ["memMB"] = c.MemMB, ["read"] = new System.Text.Json.Nodes.JsonArray((c.Read ?? Array.Empty<string>()).Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray()) };
            if (r.Job != null) o["job"] = r.Job;
            o["exitCode"] = r.Code;
            return o.ToJsonString(Relaxed) + "\n";
        }

        /// <summary>The first Markdown table of a text (a header line, a |---| line, rows) as columns and rows. Null when it has none.</summary>
        static System.Text.Json.Nodes.JsonObject? TableOf(string text)
        {
            var lines = text.Split('\n');
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                string head = lines[i].Trim(), rule = lines[i + 1].Trim();
                if (head.Length < 2 || head[0] != '|' || rule.Length < 3 || rule[0] != '|' || rule.IndexOf('-') < 0 || rule.AsSpan().IndexOfAnyExcept("|-: ") >= 0) continue;
                var rows = new System.Text.Json.Nodes.JsonArray();
                for (int j = i + 2; j < lines.Length && lines[j].TrimStart().StartsWith('|'); j++)
                    rows.Add(new System.Text.Json.Nodes.JsonArray(Cells(lines[j]).Select(c => (System.Text.Json.Nodes.JsonNode?)c).ToArray()));
                return new System.Text.Json.Nodes.JsonObject { ["columns"] = new System.Text.Json.Nodes.JsonArray(Cells(head).Select(c => (System.Text.Json.Nodes.JsonNode?)c).ToArray()), ["rows"] = rows };
            }
            return null;

            static List<string> Cells(string line)
            {
                var cells = new List<string>();
                var cur = new StringBuilder();
                string s = line.Trim();
                for (int k = 1; k < s.Length; k++)
                {
                    if (s[k] == '\\' && k + 1 < s.Length && s[k + 1] == '|') { cur.Append('|'); k++; }
                    else if (s[k] == '|') { cells.Add(cur.ToString().Trim()); cur.Clear(); }
                    else cur.Append(s[k]);
                }
                if (cur.ToString().Trim().Length > 0) cells.Add(cur.ToString().Trim());
                return cells;
            }
        }

        static string Size(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

        /// <summary>Where a running service leaves its marker: state\service.txt under the home.</summary>
        public static string MarkerPath(string home) => Path.Combine(home, "state", "service.txt");

        /// <summary>
        /// The service's marker file: process id | start time | program path. Null when no live service owns it.
        /// A service of an earlier version left its marker in Cache\service.txt: that one is read too (never written),
        /// so a service that was started before an update is still found.
        /// </summary>
        public static Process? RunningService(string home)
        {
            foreach (var marker in new[] { MarkerPath(home), Path.Combine(home, "Cache", "service.txt") })
            {
                try
                {
                    if (!File.Exists(marker)) continue;
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
            }
            return null;
        }
    }
}
