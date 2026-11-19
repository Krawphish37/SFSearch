using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// One answer of a new tool, in the parts of the output contract (design\agent-first\ARCHITECTURE.md section 7):
    /// the answer, [more] with a cursor, [notes], [source], [cost].
    /// Called as a tool (the service's structured form), the parts go to the service through JobContext and the reply
    /// carries them as fields. Called as a command-line verb, the same parts are printed as text.
    /// Problems of the plugins an answer was read from always come first in the notes.
    /// </summary>
    public sealed class EngineAnswer
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly StringBuilder _body = new();
        readonly List<string> _problems = new(), _notes = new(), _reads = new();
        readonly List<SourceInfo> _source = new();

        /// <summary>
        /// Set while a verb runs inside a tool call (sfs_run running a new verb): its answer then goes through JobContext
        /// in parts, as a tool's does, and not into the text. Reset by the worker before every command.
        /// </summary>
        public static bool ToolMode;
        /// <summary>
        /// The caller asked for the structured form (a tool's format "json", a verb's --json). A verb then prints the
        /// parts as one JSON object. A tool that has rows as values (sfs_query, sfs_compare, the reports) writes them as
        /// a JSON object in place of its text. Reset by the worker before every command.
        /// </summary>
        public static bool JsonMode;

        /// <summary>True when the caller is a tool call: the parts go through JobContext, not into the text.</summary>
        public bool AsTool { get; init; } = ToolMode;
        /// <summary>Lines at the top of the answer that every page repeats (a table's header).</summary>
        public int HeaderLines { get; set; }
        public MoreInfo? More { get; set; }
        public bool Failed { get; private set; }

        public void Line(string text = "") => _body.Append(text).Append('\n');

        /// <summary>Text that already has its line ends (a library's answer).</summary>
        public void Text(string text)
        {
            _body.Append(text);
            if (text.Length > 0 && text[^1] != '\n') _body.Append('\n');
        }

        /// <summary>A problem of a plugin the answer was read from. These lead the notes.</summary>
        public void Problem(string text) { if (!_problems.Contains(text)) _problems.Add(text); }
        public void Note(string text) { if (!_notes.Contains(text)) _notes.Add(text); }
        public void Read(string what) { if (!_reads.Contains(what)) _reads.Add(what); }

        public void Source(SourceInfo s)
        {
            if (!_source.Any(x => x.Plugin.Equals(s.Plugin, StringComparison.OrdinalIgnoreCase) && x.Key == s.Key)) _source.Add(s);
        }

        static readonly System.Text.RegularExpressions.Regex PluginSource = new(@"^(?<name>.+?) (?<n>[\d.,]+) (?<u>bytes?|KB|MB|GB), key (?<key>[0-9A-Fa-f]{6,})…?(?:, read from (?<path>.+))?$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>A source as a library words it ("darkstar.esm 25.2 MB, key 1e110de6", a load order, the archives read).</summary>
        public void Source(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || _source.Any(x => x.Text == text)) return;
            var m = PluginSource.Match(text);
            if (!m.Success) { _source.Add(new SourceInfo { Plugin = text, Text = text }); return; }
            string name = m.Groups["name"].Value, key = m.Groups["key"].Value.ToLowerInvariant();
            key = key[..Math.Min(8, key.Length)];
            string? path = m.Groups["path"].Success ? m.Groups["path"].Value.Trim() : null;
            // The same copy may already be there from the engine's own index (the same key): once is enough
            if (_source.Any(x => x.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase) && (path != null ? x.Plugin.Equals(path, StringComparison.OrdinalIgnoreCase) : x.Plugin.Equals(name, StringComparison.OrdinalIgnoreCase)))) return;
            // The size in bytes: the file's own when it can be found (the text has it rounded)
            double n = double.Parse(m.Groups["n"].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);
            long bytes = (long)(n * m.Groups["u"].Value switch { "GB" => 1L << 30, "MB" => 1L << 20, "KB" => 1024, _ => 1 });
            string? strings = null;
            try
            {
                if (path != null) { if (System.IO.File.Exists(path)) bytes = new System.IO.FileInfo(path).Length; }
                else if (EngineIndex.Get().Index.Plugin(name) is { } p && p.ContentKey.Short.StartsWith(key, StringComparison.OrdinalIgnoreCase)) { bytes = p.Stamp.Length; strings = p.Strings.Stamped ? p.Strings.Short : null; }
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException) { }
            // A file outside Data is named by its path, as the engine's own sources name it
            _source.Add(new SourceInfo { Plugin = path ?? name, Bytes = bytes, Key = key, Strings = strings });
        }

        /// <summary>
        /// One source as it is printed: a plugin copy as "name size, key k", with the size written by the one function
        /// every new tool uses (<see cref="EngineIndex.Mb"/>), so the same file never shows as two sizes.
        /// </summary>
        static SourceInfo Shown(SourceInfo s) => s.Text != null ? s
            : new SourceInfo { Plugin = s.Plugin, Bytes = s.Bytes, Modified = s.Modified, Key = s.Key, Strings = s.Strings, Text = $"{s.Plugin} {EngineIndex.Mb(s.Bytes)}{(s.Key.Length > 0 ? ", key " + s.Key : "")}{(s.Strings != null ? ", strings " + s.Strings : "")}" };

        /// <summary>What the validity check found: its notes after the problems, its reads on the cost line.</summary>
        public void Checked(Validity v)
        {
            foreach (var n in v.Notes) Note(n);
            foreach (var r in v.Reads) Read(r);
        }

        /// <summary>
        /// The exit codes of every verb of the new engine (the same numbers are "exitCode" in the JSON form):
        /// 0 answered. 1 answered, and the answer is a failure of what was asked: a check that found errors, a round
        /// trip that differs, a comparison whose expect didn't hold. 2 no answer: the one line "Error: … Fix: …" says
        /// what was wrong with the call, or with what it named. 7 heavy work that wasn't allowed ("Not run. …").
        /// 8 not enough memory free for heavy work that was allowed. (7 and 8 are JobContext's.)
        /// </summary>
        public const int FailedCode = 1, ErrorCode = 2;

        /// <summary>The answer is an error: nothing of the body is printed, only the one line with its fix (and the notes). The exit code is 2.</summary>
        public void Fail(string what, string fix, int code = ErrorCode)
        {
            // One scheme for every verb: an error line is always exit code 2
            code = ErrorCode;
            Failed = true;
            _body.Clear();
            HeaderLines = 0; More = null;
            _failWhat = what; _failFix = fix; _failCode = code;
            // An add-on has a command of this verb's name (the patcher add-on's Profile beside the verb profile): the verb
            // comes first, so the caller may have meant the other one (GAPS S212)
            if (EngineCommands.CurrentVerb is { } verb && Extensions.SameName(verb) is { Count: > 0 } addOns)
                _failFix = fix.TrimEnd('.', ' ') + $". If you meant the add-on's command of the same name, call it by its full name: {string.Join(", ", addOns)}";
        }
        string _failWhat = "", _failFix = "";
        int _failCode = ErrorCode;

        /// <summary>
        /// Something that happened after the reading and belongs on the cost line without being a thing read (a
        /// worker that closed its plugins to give memory back). It is added in brackets to the last thing read.
        /// </summary>
        public void After(string what) { _after = what; }
        string? _after;

        string[] Reads()
        {
            var reads = _reads.ToList();
            if (_after != null)
            {
                if (reads.Count == 0) reads.Add($"nothing more ({_after})");
                else reads[^1] += $" ({_after})";
            }
            return reads.ToArray();
        }

        /// <summary>
        /// The answer is "not run": a heavy part the caller didn't allow, or memory was short. JobContext.RequireHeavy has
        /// already printed the one line that says so and set the exit code; nothing of the body is added to it.
        /// </summary>
        public void Refused()
        {
            _refused = true;
            _body.Clear();
            HeaderLines = 0; More = null;
        }
        bool _refused;

        /// <summary>
        /// Cuts an answer of lines to a size budget, never silently: the first <paramref name="headerLines"/> lines are
        /// on every page, then as many whole lines from <paramref name="offset"/> on as fit in <paramref name="maxChars"/>
        /// (one at least), and the [more] line gives the cursor for the rest. A single line longer than the budget is
        /// cut too, and says how much of it was left out.
        /// </summary>
        public void Lines(IReadOnlyList<string> lines, int headerLines, int offset, int maxChars, Func<int, string> cursor, string unit = "lines")
        {
            headerLines = Math.Min(headerLines, lines.Count);
            int used = 0;
            for (int i = 0; i < headerLines; i++) { Line(lines[i]); used += lines[i].Length + 1; }
            int body = lines.Count - headerLines, shown = 0;
            for (int i = headerLines + offset; i < lines.Count; i++)
            {
                string l = lines[i];
                if (shown > 0 && used + l.Length + 1 > maxChars) break;
                if (l.Length + 1 > maxChars)
                {
                    int keep = Math.Max(200, maxChars - used - 120);
                    if (keep < l.Length) l = l[..keep] + $" … [this line is cut: {lines[i].Length - keep:N0} more characters. A larger max_chars shows them]";
                }
                Line(l); used += l.Length + 1; shown++;
            }
            HeaderLines = headerLines;
            int next = offset + shown;
            Page(offset + 1, shown, body, next < body ? cursor(next) : null, unit);
        }

        /// <summary>"40 of 312 rows", and the cursor for the next page when there is one.</summary>
        public void Page(int from, int shown, int total, string? nextCursor, string unit = "rows")
        {
            if (shown < total || from > 1) More = new MoreInfo { From = from, Shown = shown, Total = total, Unit = unit, Cursor = nextCursor };
        }

        /// <summary>
        /// A record with no EditorID is written "[no EditorID] (FormID:plugin)" by every tool, and what stands in a
        /// name's place is always in square brackets: [no such record], [plugin not in Data], [built into the game].
        /// The libraries under the tools (the header index's own label, the record layer, the comparisons, the reports)
        /// write the first three in round brackets: they are put into the one form here, where every answer passes.
        /// </summary>
        static string OneForm(string text) => text.Replace("(no EditorID) (", "[no EditorID] (", StringComparison.Ordinal)
            .Replace("(no such record) (", "[no such record] (", StringComparison.Ordinal)
            .Replace("(plugin not in Data) (", "[plugin not in Data] (", StringComparison.Ordinal);

        public void Emit()
        {
            var notes = _problems.Concat(_notes).Select(OneForm).Distinct().ToList();
            string body = OneForm(_body.ToString());
            _failWhat = OneForm(_failWhat); _failFix = OneForm(_failFix);
            if (AsTool)
            {
                if (Failed) JobContext.Fail(_failWhat, _failFix, _failCode);
                else if (!_refused) Console.Out.Write(body);
                foreach (var n in notes) Console.Error.WriteLine(n);
                foreach (var s in _source) JobContext.AddSource(Shown(s));
                foreach (var r in Reads()) JobContext.AddRead(r);
                if (HeaderLines > 0) JobContext.TableHeader(HeaderLines);
                if (More != null) JobContext.SetMore(More);
                return;
            }
            // A command-line verb: the same parts, as the text of the contract
            string what = _failWhat.TrimEnd('.', ' '), fix = _failFix.TrimEnd('.', ' ');
            var reply = new Reply
            {
                Answer = Failed ? $"Error: {what}. Fix: {fix}.\n" : _refused ? "" : body,
                More = More,
                Notes = notes.Count > 0 ? notes.ToArray() : null,
                Source = _source.Count > 0 ? _source.Select(Shown).ToArray() : null,
                Cost = new CostInfo { Ms = _clock.ElapsedMilliseconds, Read = Reads() is { Length: > 0 } reads ? reads : null },
            };
            if (JsonMode)
            {
                reply.Code = Failed ? _failCode : Environment.ExitCode;
                if (Failed) { reply.Failure = new FailInfo { Message = $"Error: {what}. Fix: {fix}.", Fix = fix + "." }; reply.Answer = ""; }
                Console.Out.Write(Wire.RenderJson(reply));
            }
            else Console.Out.Write(Wire.Render(reply));
            if (Failed) Environment.ExitCode = _failCode;
        }
    }
}
