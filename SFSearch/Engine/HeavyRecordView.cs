using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using SFSearch.Records;

namespace SFSearch.Engine
{
    /// <summary>
    /// record on a heavy record (over 16 MB of data: Starfield.esm's navigation mesh info map), without its full card
    /// (GAPS row S7, fix G2). The record's bytes are read once; only the subrecords a view needs go through Mutagen
    /// (SFSearch.Records\HeavyRecord.cs).
    ///  - card (the default): the header, the subrecords from the bytes with their counts and sizes, the fields that
    ///    are small, read through Mutagen, and for each big field how to ask for it.
    ///  - fields and evidence: only the fields named. A big list whose items are one subrecord each is read a page at a
    ///    time: the cursor counts its items. One item ([5]) reads that one subrecord.
    /// The full card is still there with allow_heavy, at its cost.
    /// </summary>
    static class HeavyRecordView
    {
        /// <summary>Items read in the first pass of a page, to learn how long an item's text is.</summary>
        const int Probe = 16;
        /// <summary>The most items of one page.</summary>
        const int MostItems = 20_000;
        /// <summary>The most bytes of items one page reads through Mutagen (a PERS item is a megabyte).</summary>
        const long PageBytes = 4 << 20;
        /// <summary>The most bytes of items the first pass reads (one item at least).</summary>
        const long ProbeBytes = 256 << 10;
        /// <summary>The memory of the full card, by megabyte of the record's data (RecordTool's estimate).</summary>
        const int CardMBPerMB = 250;

        public static void Run(RecordEngine engine, EngineIndex eng, Picked pick, string view, List<string> fields, int offset, int maxChars, string question, EngineAnswer answer, bool justOpened)
        {
            var info = pick.Info;
            var layout = engine.Layout(pick.Ref);
            var lines = new List<string>
            {
                ProblemNotes.Label(info),
                $"{info.Signature} {layout.Type} in {info.Plugin}{(info.IsOverride ? " (an override)" : " (its own record)")}{(info.Parent == null ? "" : ", inside " + engine.Label(info.Parent))}, at 0x{info.Offset:X}, {info.Size.ToString("N0", CultureInfo.InvariantCulture)} bytes{(layout.Raw.Compressed ? ", compressed" : "")}",
                RecordTool.ReadFrom(eng, pick),
            };
            long cardMB = (long)(layout.DataBytes / 1048576.0 * CardMBPerMB);
            string heavyLine = RecordEngine.HeavyLine(layout) + $" Its full card isn't made here: it needs about {EngineIndex.Mb(cardMB << 20)} of memory (allow_heavy).";
            string readBytes = $"the bytes of 1 {info.Signature} of {pick.Plugin.Name} ({EngineIndex.Mb(layout.DataBytes)}{(layout.Raw.Compressed ? ", inflated once" : "")}{(justOpened ? $"; {pick.Plugin.Name} was opened for it" : "")})";

            if (view == "card")
            {
                lines.Add(heavyLine);
                int head = lines.Count;
                lines.Add($"Subrecords, from the bytes ({layout.Raw.Subrecords.Count.ToString("N0", CultureInfo.InvariantCulture)}):");
                lines.AddRange(layout.SubrecordTable());
                answer.Read(readBytes);
                PartRead? whole = null;
                string? why = null;
                try { whole = engine.ReadPart(layout, new Dictionary<string, IReadOnlyCollection<int>?>()); }
                catch (RecordException e) { why = e.Message; }
                if (whole == null)
                {
                    // The subrecords from the bytes are still an answer; the fields can't be read in part
                    lines.Add($"Fields: not read. {why}");
                    answer.Note("The subrecords above come from the record's bytes. The raw view gives each one's bytes; allow_heavy reads the whole record through Mutagen.");
                }
                else
                {
                    var json = engine.Json(whole.Record);
                    var card = engine.PartCard(whole, json, withDefaults: true);
                    foreach (var note in engine.TextNotes(whole.Record, new JsonNode?[] { json })) answer.Note(note);
                    lines.Add(whole.Left.Count == 0 ? "Fields, read through Mutagen:" : "Fields, read through Mutagen without the big ones:");
                    lines.AddRange(Split(card.ToText(int.MaxValue, 64)).Skip(2));
                    if (whole.Left.Count > 0)
                    {
                        lines.Add("Big fields, not read here:");
                        foreach (var f in whole.Left)
                            lines.Add($"  {Head(f)}: {What(f)}. {HowToAsk(f)}");
                    }
                    answer.Read(PartLine(whole, layout));
                }
                if (offset > 0 && offset >= lines.Count - head) { answer.Fail($"the cursor points past the end ({lines.Count - head:N0} lines)", "leave the cursor out to start over", 2); return; }
                answer.Lines(lines, head, offset, maxChars, next => Cursors.Make(next, question));
                return;
            }

            // ---- fields and evidence: the fields named, a big list a page at a time
            var asks = fields.Select(Parse).ToList();
            var asked = new Dictionary<string, IReadOnlyCollection<int>?>(StringComparer.Ordinal);
            HeavyField? paged = null;
            foreach (var a in asks)
            {
                var f = layout.Field(a.Top);
                if (f == null || !f.Big) continue;   // a small field is always read
                a.Field = f;
                if (f.ItemSignature == null)
                {
                    if (f.Bytes > engine.HeavyRecordBytes)
                    {
                        answer.Fail($"{f.Name} of {ProblemNotes.Label(info)} is {EngineIndex.Mb(f.Bytes)} in {f.Subrecords:N0} {string.Join(", ", f.Signatures)} subrecord(s), over the {EngineIndex.Mb(engine.HeavyRecordBytes)} of a heavy record, and it isn't a list of one subrecord an item, so it can't be read a page at a time",
                            "ask for the record with allow_heavy (its full card), or use the raw view for its bytes");
                        return;
                    }
                    asked[f.Name] = null;
                    continue;
                }
                if (a.Index is int k)
                {
                    if (k >= f.Items) { answer.Fail($"{f.Name} of {ProblemNotes.Label(info)} has {f.Items:N0} item(s); there is no [{k}]", $"give an index from 0 to {f.Items - 1:N0}, or leave the index out to read the list a page at a time"); return; }
                    if (asked.TryGetValue(f.Name, out var have) && have == null) continue;
                    var set = have as HashSet<int> ?? new HashSet<int>();
                    set.Add(k);
                    asked[f.Name] = set;
                    continue;
                }
                if (paged != null && paged != f)
                {
                    answer.Fail($"{paged.Name} and {f.Name} are both big lists, and one call reads one of them a page at a time", "ask for them in two calls, or name single items such as " + f.Name + "[0]");
                    return;
                }
                paged = f;
            }

            if (paged == null)
            {
                // Single items and small fields: one read, the answer cut by lines as always
                var single = engine.ReadPart(layout, asked);
                var card = engine.PartCard(single);
                var body = Render(engine, single, card, view, fields, answer);
                lines.Add(heavyLine + " Only the fields named were read.");
                int head = lines.Count;
                lines.AddRange(body);
                answer.Read(readBytes);
                answer.Read(PartLine(single, layout));
                if (offset > 0 && offset >= lines.Count - head) { answer.Fail($"the cursor points past the end ({lines.Count - head:N0} lines)", "leave the cursor out to start over", 2); return; }
                answer.Lines(lines, head, offset, maxChars, next => Cursors.Make(next, question));
                return;
            }

            // ---- A page of a big list: the cursor is the index of the page's first item
            if (offset >= paged.Items) { answer.Fail($"the cursor points past the end ({paged.Items:N0} items of {paged.Name})", "leave the cursor out to start over", 2); return; }
            var others = asks.Where(a => a.Field != paged).Select(a => a.Asked).ToList();
            var itemAsks = asks.Where(a => a.Field == paged).ToList();
            List<string> NamesFor(int k) => itemAsks.Select(a => $"{paged.Name}[{k}]{a.Rest}").ToList();

            lines.Add(heavyLine + $" Only the fields named were read; {paged.Name} a page at a time.");
            int header = lines.Count;
            int budget = Math.Max(1_000, maxChars - lines.Sum(l => l.Length + 1) - 400);

            // First pass: a few items, to learn how long an item's text is
            int left = paged.Items - offset;
            // The items' own sizes, from the bytes: a page reads at most PageBytes of them (one item at least)
            var sizes = layout.Raw.Subrecords.Where(x => x.Signature == paged.ItemSignature).Select(x => 6L + x.Length).ToArray();
            int ByBytes(int most, long cap) { long sum = 0; int k = 0; while (k < most && (k == 0 || sum + sizes[offset + k] <= cap)) sum += sizes[offset + k++]; return k; }
            int n = ByBytes(Math.Min(Probe, left), ProbeBytes);
            var (part1, otherLines, items) = Page(engine, layout, asked, paged, offset, n, view, others, NamesFor, answer);
            int perItem = Math.Max(1, (int)Math.Ceiling(items.Average(i => i.Sum(l => l.Length + 1))));
            int fit = ByBytes(Math.Clamp((budget - otherLines.Sum(l => l.Length + 1)) / perItem, 1, Math.Min(left, MostItems)), PageBytes);
            var part = part1;
            if (fit > n) (part, otherLines, items) = Page(engine, layout, asked, paged, offset, fit, view, others, NamesFor, answer);

            lines.AddRange(otherLines);
            int used = lines.Skip(header).Sum(l => l.Length + 1), shown = 0;
            foreach (var item in items)
            {
                int size = item.Sum(l => l.Length + 1);
                if (shown > 0 && used + size > budget) break;
                if (used + size > budget)
                {
                    // One item longer than the whole page: its first lines, and how to ask for the rest of it
                    int keep = 0, chars = used;
                    while (keep < item.Count && chars + item[keep].Length + 1 <= budget) chars += item[keep++].Length + 1;
                    keep = Math.Max(1, keep);
                    lines.AddRange(item.Take(keep));
                    lines.Add($"  … [this item is cut: {item.Count - keep:N0} more lines. Name one of its fields ({paged.Name}[{offset}].<field>), or give a larger max_chars]");
                    shown = 1;
                    break;
                }
                lines.AddRange(item); used += size; shown++;
            }
            foreach (var l in lines.Take(header)) answer.Line(l);
            foreach (var l in lines.Skip(header)) answer.Line(l);
            answer.HeaderLines = header;
            int next = offset + shown;
            answer.Page(offset + 1, shown, paged.Items, next < paged.Items ? Cursors.Make(next, question) : null, $"{paged.Name} items");
            answer.Read(readBytes);
            answer.Read(PartLine(part, layout));
        }

        static (PartRead Part, List<string> Others, List<List<string>> Items) Page(RecordEngine engine, HeavyLayout layout, Dictionary<string, IReadOnlyCollection<int>?> asked, HeavyField paged, int from, int count,
            string view, List<string> others, Func<int, List<string>> namesFor, EngineAnswer answer)
        {
            var ask = new Dictionary<string, IReadOnlyCollection<int>?>(asked, StringComparer.Ordinal) { [paged.Name] = Enumerable.Range(from, count).ToArray() };
            var part = engine.ReadPart(layout, ask);
            var card = engine.PartCard(part);
            var otherLines = others.Count == 0 ? new List<string>() : Render(engine, part, card, view, others, answer);
            var items = new List<List<string>>(count);
            for (int k = from; k < from + count; k++) items.Add(Render(engine, part, card, view, namesFor(k), answer));
            return (part, otherLines, items);
        }

        /// <summary>The lines of the fields named, as the fields or evidence view prints them for any record.</summary>
        static List<string> Render(RecordEngine engine, PartRead part, Card card, string view, List<string> names, EngineAnswer answer)
        {
            var lines = new List<string>();
            var values = FieldLookup.Fields(engine.Schema, card, names);
            if (view == "fields")
                foreach (var v in values)
                    foreach (var l in Split(v.ToText())) lines.Add("  " + l);
            else
                foreach (var f in names)
                    lines.AddRange(Split(engine.PartEvidence(part, card, f).ToText()));
            foreach (var n in engine.TextNotes(part.Record, values.Select(v => v.Json))) answer.Note(n);
            return lines;
        }

        static string PartLine(PartRead part, HeavyLayout layout) =>
            $"Mutagen's read of {part.KeptSubrecords.ToString("N0", CultureInfo.InvariantCulture)} of its {layout.Raw.Subrecords.Count.ToString("N0", CultureInfo.InvariantCulture)} subrecords ({EngineIndex.Mb(part.KeptBytes)})";

        static string Head(HeavyField f) => string.Equals(f.Readable.Replace(" ", ""), f.Name, StringComparison.OrdinalIgnoreCase) ? f.Name : $"{f.Readable} ({f.Name})";

        static string What(HeavyField f) => f.ItemSignature != null
            ? $"a list of {f.Items.ToString("N0", CultureInfo.InvariantCulture)} items, one {f.ItemSignature} subrecord each, {EngineIndex.Mb(f.Bytes)}"
            : $"{f.Subrecords.ToString("N0", CultureInfo.InvariantCulture)} {string.Join(", ", f.Signatures)} subrecord{(f.Subrecords == 1 ? "" : "s")}, {EngineIndex.Mb(f.Bytes)}";

        static string HowToAsk(HeavyField f) => f.ItemSignature != null
            ? $"One item: fields \"{f.Name}[0]\" (or one field of every item: \"{f.Name}[*].<field>\"); the whole list a page at a time: fields \"{f.Name}\", then the cursor."
            : $"Read it with fields \"{f.Name}\".";

        sealed class Ask
        {
            public string Asked = "", Top = "", Rest = "";
            public int? Index;
            public bool All;
            public HeavyField? Field;
        }

        /// <summary>A field name cut at its first step: <c>MapInfos[5].NavigationMesh</c> → MapInfos, 5, <c>.NavigationMesh</c>.</summary>
        static Ask Parse(string asked)
        {
            string s = asked.Trim();
            int dot = s.IndexOf('.'), open = s.IndexOf('[');
            int end = dot < 0 ? s.Length : dot;
            var a = new Ask { Asked = s };
            if (open >= 0 && open < end)
            {
                int close = s.IndexOf(']', open);
                string inside = close < 0 ? "" : s[(open + 1)..close].Trim();
                a.Top = s[..open];
                if (inside is "*" or "") a.All = true;
                else if (int.TryParse(inside, NumberStyles.None, CultureInfo.InvariantCulture, out int k)) a.Index = k;
                a.Rest = close < 0 ? "" : s[(close + 1)..];
            }
            else { a.Top = s[..end]; a.Rest = s[end..]; }
            int brace = a.Top.IndexOf('{');
            if (brace >= 0) a.Top = a.Top[..brace];
            return a;
        }

        static IEnumerable<string> Split(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }
}
