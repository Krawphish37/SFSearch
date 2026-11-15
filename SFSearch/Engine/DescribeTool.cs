using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Engine
{
    /// <summary>
    /// describe (tool sfs_describe, command-line verb describe): what the schema says. A record type's fields, one
    /// field, an enum, or a search over field names, from schema.db (generated from Mutagen's own record definitions,
    /// with xEdit's names beside them). It never reads a plugin.
    /// (design\agent-first\ARCHITECTURE.md sections 4 and 7; milestones M1 and M8.)
    /// </summary>
    public static class DescribeTool
    {
        public const string Help = """
            describe <what> [<filter>] [--all] [--max-chars N] [--cursor C] [--json]
              What the schema says about a record type, a field or an enum. No plugin is read.

              what     a record type by signature or Mutagen name:   WEAP, Weapon      (a signature of several types lists them: OMOD)
                       a field by its Mutagen path:                  Weapon.AttackDamage, WEAP.Keywords
                       a field by its file address:                  WEAP/WDMG, WEAP/WDMG@16
                       a field by its readable (xEdit) name:         "Base Damage"
                       an enum:                                      Weapon.SoundLevel shows its values; an enum's own name works too
                       any other word: the fields, in every record   damage
                       type, that are named so (or, when none is,
                       the fields whose name holds the word)
                       *word*: every field whose name holds the      *ammo*
                       word, also for a word that is itself a record
                       type's signature or name (ammo alone is AMMO)
              filter   with a record type: only the fields whose path, readable name or subrecord holds this text
              --all    spell out every kind of object a field can hold (long)

              Field names are the ones SQL and record use: a view's columns, a card's field names, the paths of the fields view.
              Exit code: 0 answered; 2 an error (one line, Error: ... Fix: ...), such as a name the schema doesn't have.
            """;

        static readonly string[] Known = { "what", "filter", "all", "max_chars", "cursor" };
        static readonly VerbSpec Spec = new() { Verb = "describe", Positional = new[] { "what", "filter" }, Options = new[] { "max_chars", "cursor" }, Flags = new[] { "all" } };

        public static void Tool(IReadOnlyDictionary<string, JsonElement> args)
        {
            var a = new ToolArgs(args);
            var answer = new EngineAnswer { AsTool = true };
            if (a.Unknown(Known) is { Count: > 0 } wrong)
                answer.Fail($"describe has no argument named {string.Join(", ", wrong.Select(w => "'" + w + "'"))}", $"its arguments are: {string.Join(", ", Known)}", 2);
            else Guarded(a, answer);
            answer.Emit();
        }

        public static void Verb(List<string> args)
        {
            if (args.Count == 0 || args.Any(x => x is "--help" or "-h" or "/?")) { Console.WriteLine(Help); return; }
            var answer = new EngineAnswer();
            var given = Spec.Parse(args, answer);
            if (given != null) Guarded(new ToolArgs(given), answer);
            answer.Emit();
        }

        static void Guarded(ToolArgs a, EngineAnswer answer)
        {
            try { Run(a, answer); }
            catch (RecordException e) { answer.Fail(e.Message.Split('\n')[0], "build SFSearch again: the build makes schema.db"); }
            catch (SchemaException e) { answer.Fail(e.Message.Split('\n')[0], "build SFSearch again: the build makes schema.db"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                answer.Fail($"the schema couldn't be read ({e.Message.Split('\n')[0].TrimEnd('.')})", "build SFSearch again: the build makes schema.db");
            }
        }

        static void Run(ToolArgs a, EngineAnswer answer)
        {
            string? what = a.Text("what");
            if (what == null) { answer.Fail("describe got nothing to describe", "give a record type (WEAP, Weapon), a field (Weapon.AttackDamage, WEAP/WDMG, \"Base Damage\"), an enum, or a word of a field's name", 2); return; }
            what = what.Trim().Trim('"');
            string? filter = a.Text("filter");
            // *word*: a search of every field name, also for a word that is a record type's signature or name
            bool search = what.Contains('*');
            if (search)
            {
                what = what.Replace("*", "").Trim();
                if (what.Length == 0) { answer.Fail("describe got only a star to search for", "give the word between the stars: describe *ammo* lists the fields, in every record type, whose name holds \"ammo\"", 2); return; }
                if (filter != null) { answer.Fail($"describe *{what}* searches every record type's field names, and a filter was given too (\"{filter}\")", $"for one type's fields give the type and the word: describe {filter} {what}", 2); return; }
            }
            int maxChars = Math.Max(RecordTool.LeastChars, a.Int("max_chars") ?? RecordTool.DefaultChars);
            string question = $"describe|{a.Text("what")?.Trim().Trim('"')}|{filter}|{a.Flag("all")}";
            if (!Cursors.TryRead(a.Text("cursor"), question, out int offset))
            { answer.Fail($"the cursor \"{a.Text("cursor")}\" doesn't belong to this question", "pass a cursor together with the same arguments that gave it, or leave it out to start at the top", 2); return; }

            var schema = EngineRecords.Schema;
            var db = schema.Db;
            var text = new StringWriter { NewLine = "\n" };
            int code;
            // A name SQL reads that isn't a record type: Links, Forms, Records, a list view, a union view, schema.*
            bool viewDoc = false;
            if (!search && SqlView(what) is { Kind: not "typed view" } view)
            {
                viewDoc = true;
                if (!ViewText(view, filter, text, answer)) return;
                code = 0;
            }
            else if (search) code = Describe.Search(db, what, text);
            else if (EnumNamed(db, what) is { } enumName) { Enum(db, enumName, text); code = 0; }
            else code = Describe.Run(db, what, filter, a.Flag("all"), text);
            // A word that is a record type's signature or name gives that type. When it wasn't typed as the type is
            // written (ammo for AMMO), and fields hold the word too, the answer says how to search field names instead.
            if (!search && code == 0 && filter == null && Describe.TypeNamed(db, what, out int holding) is { } type && holding > 0
                && what != type.Signature && what != type.Name)
                answer.Note($"\"{what}\" was read as the record type {type.Name} ({type.Signature}). {holding:N0} field{(holding == 1 ? "" : "s")} of the schema {(holding == 1 ? "holds" : "hold")} \"{what}\" in a name: to list them, in every record type, ask describe *{what}*; for one type's, give the type and the word as filter (describe WEAP {what}).");
            var lines = text.ToString().TrimEnd('\n').Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (code != 0)
            {
                // The describer's own line says what is wrong and, where it can, the nearest real names
                string line = lines.LastOrDefault(l => l.Trim().Length > 0) ?? $"nothing in the schema is called \"{what}\"";
                bool nothing = line.StartsWith("Nothing in the schema", StringComparison.Ordinal);
                var enums = nothing ? db.Model.Enums.Keys.Where(k => k.Contains(what, StringComparison.OrdinalIgnoreCase)).Take(4).ToList() : new List<string>();
                var (problem, fix) = EngineLibs.ErrorParts(line, null, "describe <type> lists a record type's fields (describe WEAP); one word searches every field name (describe damage, or describe *ammo* for a word that is also a record type)");
                // A name that is nothing at all: the nearest record types, by their letters
                if (nothing && SchemaTypes.Default.Nearest(what) is { Count: > 0 } types)
                    fix = $"{SFSearch.SchemaGen.NearNames.Fix(types)}. describe <type> lists a type's fields; one word searches every field name (describe damage)";
                if (enums.Count > 0) fix += $". Enums with that text in their name: {string.Join(", ", enums)}";
                answer.Fail(problem, fix, 2);
                return;
            }
            // A record type: the names SQL knows it by (its view, its list views, its union view), before its table
            if (!search && !viewDoc && filter == null && SqlLines(db, what) is { Count: > 0 } sql)
            {
                int table = lines.FindIndex(l => l.StartsWith("| ", StringComparison.Ordinal));
                if (table > 0) lines.InsertRange(table, sql.Append(""));
            }
            // The lines before the first table are on every page; a table's own two header lines too when the answer is one table
            int rule = lines.FindIndex(l => l.StartsWith("|---", StringComparison.Ordinal));
            bool oneTable = rule >= 1 && lines.Skip(rule + 1).All(l => l.StartsWith('|'));
            int header = oneTable ? rule + 1 : 0;
            // A filter's rows come a page of at most 40 at a time, the best matches first; [more] gives the cursor
            int pageChars = maxChars;
            if (filter != null && oneTable && lines.Count - header - offset > FilterRows)
                pageChars = Math.Min(maxChars, lines.Take(header).Sum(l => l.Length + 1) + lines.Skip(header + offset).Take(FilterRows).Sum(l => l.Length + 1));
            answer.Lines(lines, header, offset, pageChars, n => Cursors.Make(n, question), oneTable ? "rows" : "lines");
            answer.Source(new SFSearch.Service.SourceInfo { Plugin = "schema", Text = $"schema {schema.Version}, made from Mutagen {schema.MutagenVersion}'s record definitions (no plugin read)" });
        }

        /// <summary>Rows a page of a filtered answer.</summary>
        const int FilterRows = 40;

        static readonly Lazy<SFSearch.Query.ViewBuilder> Views =new(() => new SFSearch.Query.ViewBuilder(EngineRecords.Schema));

        /// <summary>What SQL reads under a name, or null when it reads nothing of that name.</summary>
        static SFSearch.Query.ViewDoc? SqlView(string what)
        {
            if (what.Contains(' ') || what.Contains('/') || what.Contains('.') && !what.StartsWith("schema.", StringComparison.OrdinalIgnoreCase)) return null;
            return SFSearch.Query.ViewDocs.Find(Views.Value, what, SchemaTypes.DefaultPath);
        }

        /// <summary>A view's columns as a table, with what it is and its list views first. False with a failed answer
        /// when a filter matches no column.</summary>
        static bool ViewText(SFSearch.Query.ViewDoc v, string? filter, TextWriter o, EngineAnswer answer)
        {
            var cols = filter == null ? v.Columns
                : v.Columns.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || c.About.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (cols.Count == 0)
            {
                answer.Fail($"{v.Name} has no column with \"{filter}\" in its name or description", $"describe {v.Name} lists its {v.Columns.Count} columns", 2);
                return false;
            }
            o.WriteLine($"# {v.Name}: SQL {v.Kind}{(filter != null ? $", {cols.Count} of {v.Columns.Count} columns matching \"{filter}\"" : $", {v.Columns.Count} columns")}");
            o.WriteLine();
            o.WriteLine(v.About);
            if (v.Kind == "union view") o.WriteLine($"Record types: {string.Join(", ", v.Types)} (describe one of them for its own fields).");
            if (v.Kind is "list view" or "flags view" && v.Related != null) o.WriteLine($"Typed view: {v.Related} (describe {v.Related} lists its fields and all its list views).");
            if (v.ListViews.Count > 0) o.WriteLine($"{(v.Kind is "list view" ? "List views inside its entries" : "List views")} ({v.ListViews.Count}): {string.Join(", ", v.ListViews)}.");
            if (v.Kind == "schema table") o.WriteLine("Read only, the same for every query; it needs no plugin.");
            o.WriteLine();
            o.WriteLine("| Column | Type | What it holds |");
            o.WriteLine("|---|---|---|");
            foreach (var c in cols) o.WriteLine($"| {c.Name} | {c.Type} | {c.About.Replace("|", "\\|")} |");
            return true;
        }

        /// <summary>For a record type: its SQL view, its list views by their exact names, and the union view it is in.</summary>
        static List<string> SqlLines(SchemaDb db, string what)
        {
            var lines = new List<string>();
            var named = Describe.TypeNamed(db, what, out _);
            if (named is { } t && SFSearch.Query.ViewDocs.OfType(Views.Value, t.Name) is { } of)
            {
                lines.Add($"SQL: the view {of.Typed.Name} ({of.Typed.FixedColumns.Count + of.Typed.Columns.Count + 1} columns){(of.Union != null ? $", and the union view {of.Union} with the other types of {t.Signature}" : "")}. describe <view> lists any view's columns.");
                lines.Add(of.Lists.Count == 0 ? "List views: none." : $"List views ({of.Lists.Count}): {string.Join(", ", of.Lists)}.{(of.Inside > 0 ? $" {of.Inside} more are lists inside their entries: describe a list view to see its own (describe {of.Lists[0]})." : "")}");
            }
            else if (named == null && Views.Value.UnionNames.FirstOrDefault(u => u.Value.Equals(what, StringComparison.OrdinalIgnoreCase)) is { Key: not null } union)
                lines.Add($"SQL: the union view {union.Key} holds all of them, with a Type column (describe {union.Key} lists its columns and list views).");
            return lines;
        }

        static string? EnumNamed(SchemaDb db, string what)
        {
            if (what.Contains('.') || what.Contains('/') || what.Contains(' ')) return null;
            // A record type or an object of the same name comes first: the describer handles those
            if (db.Model.Objects.Keys.Any(o => o.Equals(what, StringComparison.OrdinalIgnoreCase)) || db.Model.RecordTypes.Any(r => string.Equals(r.Sig, what, StringComparison.OrdinalIgnoreCase))) return null;
            return db.Model.Enums.Keys.FirstOrDefault(k => k.Equals(what, StringComparison.OrdinalIgnoreCase));
        }

        static void Enum(SchemaDb db, string name, TextWriter o)
        {
            var members = db.Model.Enums[name];
            bool flags = members.Any(m => m.IsFlags);
            var uses = db.Model.Objects.Values.SelectMany(x => x.Fields).Where(f => f.Enum == name).Select(f => $"{f.Object}.{f.Path}").Distinct().ToList();
            o.WriteLine($"# {name}: an enum of {members.Count} {(flags ? "flags (a field holds any number of them)" : "values (a field holds one)")}");
            o.WriteLine(uses.Count == 0 ? "No field of the schema uses it." : $"Used by {uses.Count} field(s): {string.Join(", ", uses.Take(12))}{(uses.Count > 12 ? $" and {uses.Count - 12} more" : "")}.");
            o.WriteLine();
            // The Creation Kit's names, for an enum that is the engine's (to-do F30): a column of its own, only then
            string? ckEnum = members.Select(m => m.CkMember).FirstOrDefault(x => x != null) is { } cm ? cm[..cm.LastIndexOf('.')] : null;
            if (ckEnum != null) o.WriteLine($"It is the engine's enum {ckEnum}: the CK name column is the Creation Kit's display name of the engine's member with the same value.");
            if (ckEnum != null) o.WriteLine();
            o.WriteLine(ckEnum != null ? "| Name | Value | xEdit name | CK name |" : "| Name | Value | xEdit name |");
            o.WriteLine(ckEnum != null ? "|---|---|---|---|" : "|---|---|---|");
            foreach (var m in members)
                o.WriteLine($"| {m.Name} | {(m.IsFlags ? "0x" + m.Value.ToString("X") : m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))} | {(m.XEditName != null && m.XEditName != m.Name ? m.XEditName : "")} |{(ckEnum != null ? $" {m.CkName} |" : "")}");
        }
    }
}
