using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Strings;
using SFSearch.Records;

namespace SFSearch.Engine
{
    /// <summary>
    /// record with a plugin's name as the record (record darkstar.esm): the plugin header, TES4 (GAPS S88). The header has
    /// no FormID and no EditorID, so the plugin's own name is what names it: one word, no option, and no clash with a
    /// record's name (a FormKey has a colon, an EditorID no file extension). The views are the record views: the card, the
    /// fields, the JSON, the raw bytes and evidence (its flags and versions in the record header, HEDR's version, record
    /// count and next object ID, each master's MAST and DATA, the overridden forms), each from Mutagen's own object and the
    /// file's bytes. versions and verify have nothing to give for a header, and say why.
    /// </summary>
    public static class RecordHeaderView
    {
        /// <summary>True when the record asked for is a plugin's name or a plugin file's path: its header.</summary>
        public static bool Names(string record)
        {
            string text = record.Trim().Trim('"', '\'');
            bool pathLike = text.Contains('/') || text.Contains('\\');
            if (text.Contains(':') && !pathLike) return false; // a FormKey (0003C2:darkstar.esm)
            return text.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
        }

        public static void Run(EngineIndex eng, string record, string? plugin, string view, List<string> fields, Language language, int offset, int maxChars, string question, EngineAnswer answer)
        {
            string text = record.Trim().Trim('"', '\'');
            var named = RecordPick.Plugin(eng, text, answer);
            if (named == null) return;
            var (ix, p, byPath) = named.Value;
            if (!string.IsNullOrWhiteSpace(plugin))
            {
                var other = RecordPick.Plugin(eng, plugin, answer);
                if (other == null) return;
                if (!ReferenceEquals(other.Value.Plugin, p))
                {
                    answer.Fail($"the record names {p.Name}'s plugin header, and plugin names {other.Value.Plugin.Name}", $"leave plugin out: the header is {p.Name}'s own (record {p.Name})", 2);
                    return;
                }
            }
            string shown = byPath ? p.Path : p.Name;
            if (view == "versions")
            {
                answer.Fail($"a plugin header has no versions: each plugin has its own, and no other plugin overrides it", $"the card view shows {p.Name}'s header; loadorder2 {p.Name} gives its place in the load order and its masters' sizes", 2);
                return;
            }
            if (view == "verify")
            {
                answer.Fail("the verify view doesn't take a plugin header: Mutagen writes a header from the whole plugin it writes (its record count, its masters, its overridden forms), so the header alone isn't written back and compared",
                    "the evidence view checks each of its fields against the file's bytes (fields \"Flags, Stats.NumRecords, MasterReferences[0].Master\")", 2);
                return;
            }
            // A master missing from Data: Mutagen reads a header's FormIDs (its overridden forms, its transient types) with
            // every master's size, and none is taken by guess. loadorder2 lists the masters in the header's order
            if (p.Problems.ReadError == null && p.Problems.MissingMasters.Count > 0)
            {
                string masters = string.Join(", ", p.Problems.MissingMasters);
                answer.Fail($"{shown}'s plugin header isn't read: it lists {(p.Problems.MissingMasters.Count == 1 ? "a master that isn't" : "masters that aren't")} in Data ({masters}), and Mutagen reads the header's FormIDs with every master's size",
                    $"loadorder2 {shown} lists its masters in the header's order, the missing one marked; put {masters} in the Data folder to read the header");
                return;
            }
            if (!ProblemNotes.Add(answer, new[] { (ix, p, byPath ? (string?)p.Path : null) }, required: true)) return;

            var engine = EngineRecords.For(eng, ix, language);
            var use = engine.Sessions.Use(p.Name);
            foreach (var n in use.Notes) answer.Note(n);
            var lines = new List<string>();
            int head;
            {
                var h = engine.LoadHeader(p.Name);
                string readFrom = $"Read from {p.Name}: {(byPath ? "the file you named, outside Data" : "the plugin you named")}.{(!byPath && !p.Order.Enabled ? $" {p.Name} isn't in the load order: the game doesn't load it." : "")}";
                if (view == "raw")
                {
                    // As the raw view of a record: the label, what the file holds, then where it was read from
                    lines.AddRange(Split(engine.HeaderRawText(h)));
                    lines.Insert(Math.Min(2, lines.Count), readFrom);
                }
                else
                {
                    lines.Add(h.Label);
                    lines.Add($"TES4 {LoadedHeader.Type} of {shown}: the plugin header, at 0x0, {h.Raw.StoredSize.ToString("N0", CultureInfo.InvariantCulture)} bytes");
                    lines.Add(readFrom);
                }
                head = 3;
                switch (view)
                {
                    case "card":
                    {
                        var json = engine.HeaderJson(h);
                        var card = engine.HeaderCard(h, json, withDefaults: true);
                        lines.AddRange(Split(card.ToText(int.MaxValue, 64)).Skip(2));
                        break;
                    }
                    case "json":
                        lines.AddRange(Split(engine.HeaderJson(h).ToJsonString(Pretty)));
                        break;
                    case "fields":
                        foreach (var v in engine.HeaderFields(h, fields))
                            foreach (var l in Split(v.ToText())) lines.Add("  " + l);
                        break;
                    case "evidence":
                        foreach (var f in fields)
                            lines.AddRange(Split(engine.HeaderEvidence(h, f).ToText()));
                        break;
                }
                answer.Read($"the TES4 header of {p.Name}");
                // A plugin that changed while it was read makes the answer untrustworthy: the command fails
                engine.Sessions.Confirm(use);
            }
            if (offset > 0 && offset >= lines.Count - head)
            {
                answer.Fail($"the cursor points past the end ({lines.Count - head:N0} lines)", "leave the cursor out to start over", 2);
                return;
            }
            answer.Lines(lines, head, offset, maxChars, next => Cursors.Make(next, question));
            answer.Source(EngineIndex.Source(p, byPath ? p.Path : null));
        }

        static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        static IEnumerable<string> Split(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }
}
