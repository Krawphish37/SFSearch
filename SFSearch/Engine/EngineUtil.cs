using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>A tool's named arguments, read as text whatever JSON kind the caller sent.</summary>
    public sealed class ToolArgs
    {
        readonly Dictionary<string, JsonElement> _args = new(StringComparer.OrdinalIgnoreCase);

        public ToolArgs(IReadOnlyDictionary<string, JsonElement> args)
        {
            foreach (var kv in args) _args[kv.Key] = kv.Value;
        }

        /// <summary>The argument as text: a list is joined with commas. Null when it wasn't given or is empty.</summary>
        public string? Text(string name)
        {
            if (!_args.TryGetValue(name, out var v)) return null;
            string? s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.Array => string.Join(",", v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())),
                _ => v.GetRawText(),
            };
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        /// <summary>The argument as the caller sent it (an object of named values, a list). Null when it wasn't given.</summary>
        public JsonElement? Element(string name) => _args.TryGetValue(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

        public int? Int(string name) => Text(name) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

        /// <summary>A yes: true, 1, yes, or (J33) "save_index", the third answer of allow_heavy and of refs' world, which is a
        /// yes that also saves an index (<see cref="Service.JobContext.SaveIndexAsked"/>).</summary>
        public bool Flag(string name) => Text(name) is { } s && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || SFSearch.Records.HeavyAnswers.IsSave(s));

        /// <summary>Arguments the tool doesn't have. A wrong name is an error with the real names, never ignored.</summary>
        public List<string> Unknown(params string[] known) =>
            _args.Keys.Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A line of text cut into words the way a command line is: blanks part them, "double quotes" keep blanks together.</summary>
    public static class Words
    {
        public static List<string> Split(string line)
        {
            var words = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool quoted = false, any = false;
            foreach (char c in line)
            {
                if (c == '"') { quoted = !quoted; any = true; continue; }
                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any || cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); any = false; }
                    continue;
                }
                cur.Append(c);
            }
            if (any || cur.Length > 0) words.Add(cur.ToString());
            return words;
        }
    }

    /// <summary>An EditorID pattern: * stands for any text, ? for one character. Letter case doesn't count.</summary>
    public sealed class Glob
    {
        readonly string[]? _parts;      // the text between stars, when the pattern has no '?'
        readonly bool _open, _close;    // a star at the start, at the end
        readonly Regex? _regex;
        public string Pattern { get; }

        public Glob(string pattern)
        {
            Pattern = pattern;
            if (pattern.Contains('?'))
                _regex = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);
            else
            {
                _parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
                _open = pattern.StartsWith('*'); _close = pattern.EndsWith('*');
            }
        }

        public static bool IsPattern(string text) => text.Contains('*') || text.Contains('?');

        public bool IsMatch(string text)
        {
            if (_regex != null) return _regex.IsMatch(text);
            var parts = _parts!;
            if (parts.Length == 0) return true; // only stars
            int pos = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                bool first = i == 0, last = i == parts.Length - 1;
                if (first && !_open)
                {
                    if (!text.StartsWith(part, StringComparison.OrdinalIgnoreCase)) return false;
                    pos = part.Length;
                    if (last && !_close) return text.Length == part.Length;
                    continue;
                }
                if (last && !_close)
                    return text.Length - part.Length >= pos && text.EndsWith(part, StringComparison.OrdinalIgnoreCase);
                int at = text.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return false;
                pos = at + part.Length;
            }
            return true;
        }
    }

    /// <summary>A tool's own cursor: where the next rows start, and a mark of the question it belongs to.</summary>
    public static class Cursors
    {
        public static string Make(int offset, string question) => $"{offset}.{Mark(question)}";

        /// <summary>The offset a cursor stands for. False when it isn't a cursor of this question.</summary>
        public static bool TryRead(string? cursor, string question, out int offset)
        {
            offset = 0;
            if (string.IsNullOrWhiteSpace(cursor)) return true;
            var parts = cursor.Trim().Trim('"').Split('.');
            return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset) && offset >= 0 && parts[1] == Mark(question);
        }

        static string Mark(string question) => Wire.Hash(question)[..4].ToLowerInvariant();
    }
}
