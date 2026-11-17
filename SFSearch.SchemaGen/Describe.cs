// describe: what the schema says about a record type, an object, a field, a file address or a readable name.
using Microsoft.Data.Sqlite;

namespace SFSearch.SchemaGen;

public static class Describe
{
    /// <returns>0 when something was found, 1 when nothing was.</returns>
    public static int Run(string dbFile, string what, string? filter, bool all, TextWriter o)
        => Run(SchemaDb.Load(dbFile), what, filter, all, o);

    public static int Run(SchemaDb db, string what, string? filter, bool all, TextWriter o)
        => new Describer(db, o).Run(what.Trim(), filter, all);

    /// <summary>
    /// The fields, in every record type, whose Mutagen name, readable name or xEdit name holds a word. It is what a
    /// word gives that is no record type; this entry gives it for a word that is one too ("ammo", "weapon").
    /// </summary>
    /// <returns>0 when a field was found, 1 when none was.</returns>
    public static int Search(SchemaDb db, string word, TextWriter o)
        => new Describer(db, o).ByName(word.Trim(), anyPart: true);

    /// <summary>
    /// The record type a word names by itself, as <see cref="Run(SchemaDb, string, string?, bool, TextWriter)"/> reads
    /// it: a signature of one type, or a type's Mutagen name. Null when the word is neither. <paramref name="fields"/>
    /// is how many fields of the schema hold the word in a name, which is what <see cref="Search"/> would list.
    /// </summary>
    public static (string Signature, string Name)? TypeNamed(SchemaDb db, string word, out int fields)
    {
        const StringComparison NoCase = StringComparison.OrdinalIgnoreCase;
        fields = 0;
        word = word.Trim();
        if (word.Length == 0 || word.Contains('.') || word.Contains('/') || word.Contains(' ')) return null;
        var bySig = db.Model.RecordTypes.Where(r => r.Sig!.Equals(word, NoCase)).ToList();
        var type = bySig.Count == 1 ? bySig[0] : bySig.Count > 1 ? null : db.Model.RecordTypes.FirstOrDefault(r => r.Name.Equals(word, NoCase));
        if (type == null) return null;
        fields = db.Model.Objects.Values.SelectMany(x => x.Fields).Count(f => f.Kind is not ("Break" or "Marker") && f.Path == f.Name
            && (f.Name.Contains(word, NoCase) || (f.Readable ?? "").Contains(word, NoCase) || (f.XEdit ?? "").Contains(word, NoCase)));
        return (type.Sig!, type.Name);
    }

    sealed class Describer(SchemaDb db, TextWriter o)
    {
        readonly SchemaModel m = db.Model;
        const StringComparison NoCase = StringComparison.OrdinalIgnoreCase;

        List<ObjDef> TypesOfSig(string sig) => m.RecordTypes.Where(r => r.Sig!.Equals(sig, NoCase)).ToList();
        ObjDef? ObjectNamed(string name) => m.Objects.Values.FirstOrDefault(x => x.Name.Equals(name, NoCase));
        string Label(ObjDef t) => t.ObjType == "Record" && t.Sig != null && !t.Abstract ? $"{t.Name} ({t.Sig})" : t.Name;

        public int Run(string what, string? filter, bool all)
        {
            // 1. A file address: WEAP/WDMG, Weapon/WDMG, WEAP/WDMG@12
            if (what.Contains('/'))
            {
                var parts = what.Split('/', 2);
                var types = TypesOfSig(parts[0]);
                if (types.Count == 0 && ObjectNamed(parts[0]) is { } named) types.Add(named);
                if (types.Count == 0) return Fail($"There is no record type \"{parts[0]}\".{Nearest(parts[0])}");
                return Address(types, parts[1]);
            }
            // 2. A signature: one record type, or several
            var bySig = TypesOfSig(what);
            if (bySig.Count == 1) return Type(bySig[0], filter, all);
            if (bySig.Count > 1) return Several(what.ToUpperInvariant(), bySig, filter, all);
            // 3. A record type or object by its Mutagen name
            if (ObjectNamed(what) is { } obj) return Type(obj, filter, all);
            // 4. A field by its Mutagen path: Weapon.AttackDamage, WEAP.AttackDamage
            int dot = what.IndexOf('.');
            if (dot > 0)
            {
                string head = what[..dot], rest = what[(dot + 1)..];
                var types = TypesOfSig(head);
                if (types.Count == 0 && ObjectNamed(head) is { } owner) types.Add(owner);
                if (types.Count > 0) return FieldByPath(types, rest);
            }
            // 5. A class or enum of the engine's reflection, or one of its fields, by the engine's whole name
            //    (BGSAtmosphere, BGSAtmosphere::MiscSettings.pImageSpaceDay): from the Creation Kit's dump (to-do F30).
            //    A short name (MiscSettings) is taken only when no Mutagen field has the word as a name (ByName).
            if (m.Ck.Present && (what.Contains("::", StringComparison.Ordinal) || m.Ck.Types.ContainsKey(what.Split('.')[0])) && CkDescribe(what) is int ck) return ck;
            // 6. A field by its readable name, its Mutagen name, xEdit's name or the Creation Kit's name
            return ByName(what);
        }

        // ---- the Creation Kit's names (to-do F30) ----

        /// <summary>The lines of a Mutagen field that is a reflected class's field: the CK's name, tooltip, range, Hidden.</summary>
        void CkFieldLines(string? ckName, string? ckRef, CkField? ck)
        {
            string from = ckRef != null ? $"the Creation Kit's display name of {ckRef}" : "the Creation Kit's display name, from Mutagen's XML";
            if (ckName != null) o.WriteLine($"- CK name: {ckName} ({from})");
            else if (ckRef != null) o.WriteLine($"- CK field: {ckRef} (the Creation Kit gives it no display name)");
            if (ck == null) return;
            if (ck.ToolTip != null) o.WriteLine($"- CK tooltip: {ck.ToolTip}");
            if (CkData.RangeText(ck) is { } range) o.WriteLine($"- CK range: {range}");
            if (ck.Hidden) o.WriteLine("- Hidden in the CK: its editor doesn't show this field.");
            if (ck.Deprecated) o.WriteLine("- Deprecated in the CK.");
        }

        /// <summary>One line on a Mutagen object that holds a reflected class (its REFL is a value of it). Null otherwise.</summary>
        string? CkObjectLine(ObjDef t)
        {
            if (t.CkClass == null && t.CkName == null) return null;
            string named = t.CkName != null ? $"The Creation Kit calls it \"{t.CkName}\"" : "The Creation Kit gives it no display name";
            return t.CkClass == null ? named + "."
                : $"{named}; its reflection data (REFL) is a value of the engine's class {t.CkClass}, whose fields the record card decodes with the CK's names: describe {t.CkClass} lists them.";
        }

        /// <summary>A class or enum of the CK's reflection data, or a field of one. Null when the word names none.</summary>
        int? CkDescribe(string what)
        {
            var whole = m.Ck.Type(what);
            if (whole != null) return CkType(whole);
            var shortly = what.Contains("::", StringComparison.Ordinal) ? new List<CkType>() : m.Ck.ByShortName(what);
            if (shortly.Count == 1) return CkType(shortly[0]);
            if (shortly.Count > 1)
            {
                o.WriteLine($"# \"{what}\" is the short name of {shortly.Count} classes or enums of the engine's reflection");
                o.WriteLine();
                foreach (var t in shortly) o.WriteLine($"- {t.Name} ({t.Kind}{(t.DisplayName != null ? $", CK: \"{t.DisplayName}\"" : "")})");
                o.WriteLine();
                o.WriteLine($"Describe one by its whole name: `describe {shortly[0].Name}`.");
                return 0;
            }
            int dot = what.LastIndexOf('.');
            if (dot <= 0 || dot == what.Length - 1) return null;
            string head = what[..dot], field = what[(dot + 1)..];
            var owners = m.Ck.Type(head) is { } one ? new List<CkType> { one } : head.Contains("::", StringComparison.Ordinal) ? new List<CkType>() : m.Ck.ByShortName(head);
            if (owners.Count == 0) return null;
            int found = 0;
            foreach (var t in owners)
            {
                var f = m.Ck.Field(t.Name, field);
                if (f != null) { CkFieldView(t, f); found++; continue; }
                var mem = m.Ck.Member(t.Name, field);
                if (mem != null) { CkMemberView(t, mem); found++; }
            }
            if (found > 0) return 0;
            var near = owners.SelectMany(t => t.Fields.Select(f => f.Name).Concat(t.Members.Select(x => x.Name)))
                .Where(n => n.Contains(field, NoCase) || field.Contains(n, NoCase)).Distinct().Take(6).ToList();
            return Fail($"{string.Join(", ", owners.Select(t => t.Name))} has no field or member \"{field}\".{(near.Count > 0 ? $" Did you mean: {string.Join(", ", near)}?" : "")}");
        }

        /// <summary>The Mutagen objects, fields and enums that are this reflected class's or enum's.</summary>
        List<string> MutagenUses(CkType t)
        {
            var uses = new List<string>();
            uses.AddRange(m.Objects.Values.Where(x => x.CkClass == t.Name).Select(x => x.ObjType == "Record" && x.Sig != null ? $"{x.Name} ({x.Sig}), its REFL" : x.Name + ", its REFL"));
            uses.AddRange(m.Objects.Values.SelectMany(x => x.Fields).Where(f => f.CkField != null && f.CkField.StartsWith(t.Name + ".", StringComparison.Ordinal) && f.Path == f.Name).Select(f => $"{f.Object}.{f.Name}"));
            uses.AddRange(m.Enums.Where(e => e.Value.Any(x => x.CkMember != null && x.CkMember.StartsWith(t.Name + ".", StringComparison.Ordinal))).Select(e => "the enum " + e.Key));
            return uses.Distinct().ToList();
        }

        string Source => m.Ck.Source != null ? $"the Creation Kit's own reflection data ({m.Ck.Source}); the game strips these names" : "the Creation Kit's own reflection data";

        int CkType(CkType t)
        {
            o.WriteLine($"# {t.Name}");
            o.WriteLine();
            string kind = t.Kind == "Enumeration" ? "An enum" : "A class";
            o.WriteLine($"{kind} of the engine's reflection{(t.DisplayName != null ? $"; the Creation Kit calls it \"{t.DisplayName}\"" : "")}{(t.Base != null ? $"; based on {t.Base}" : "")}. From {Source}.");
            if (t.ToolTip != null) o.WriteLine($"CK tooltip: {t.ToolTip}");
            if (t.Hidden) o.WriteLine("Hidden in the CK.");
            var uses = MutagenUses(t);
            if (uses.Count > 0) o.WriteLine($"In Mutagen: {string.Join("; ", uses)}.");
            o.WriteLine();
            if (t.Kind == "Enumeration")
            {
                o.WriteLine($"{t.Members.Count} members, {t.Members.Count(x => x.DisplayName != null)} with a CK name.");
                o.WriteLine();
                o.WriteLine("| Member | Value | CK name | Notes |");
                o.WriteLine("|---|---|---|---|");
                foreach (var x in t.Members) o.WriteLine($"| {x.Name} | {x.Value} | {x.DisplayName} | {CkNotes(x.ToolTip, x.Hidden, x.Deprecated, null)} |");
                return 0;
            }
            var all = new List<CkField>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var b = t; b != null && seen.Add(b.Name); b = b.Base == null ? null : m.Ck.Type(b.Base)) all.InsertRange(0, b.Fields);
            int inherited = all.Count - t.Fields.Count;
            o.WriteLine($"{(all.Count == 1 ? "1 field" : $"{all.Count} fields")}{(inherited > 0 ? $" ({inherited} from its bases)" : "")}, {all.Count(x => x.DisplayName != null)} with a CK name, {all.Count(x => x.Hidden)} hidden in the CK.");
            o.WriteLine();
            o.WriteLine("| Field | Type | CK name | Notes |");
            o.WriteLine("|---|---|---|---|");
            foreach (var f in all)
                o.WriteLine($"| {f.Name} | {f.FieldType} | {f.DisplayName} | {CkNotes(f.ToolTip, f.Hidden, f.Deprecated, CkData.RangeText(f), f.Type != t.Name ? f.Type : null)} |");
            return 0;
        }

        static string CkNotes(string? tip, bool hidden, bool deprecated, string? range, string? from = null)
        {
            var notes = new List<string>();
            if (hidden) notes.Add("hidden in the CK");
            if (deprecated) notes.Add("deprecated");
            if (range != null) notes.Add("range " + range);
            if (from != null) notes.Add("from " + from);
            if (tip != null) notes.Add("tooltip: " + tip);
            return string.Join("; ", notes).Replace("|", "\\|").Replace("\n", " ");
        }

        void CkFieldView(CkType t, CkField f)
        {
            o.WriteLine($"# {f.Type}.{f.Name}");
            o.WriteLine();
            o.WriteLine($"- Engine name: {f.Type}.{f.Name} (a field of a class of the engine's reflection{(f.Type != t.Name ? $", inherited by {t.Name}" : "")})");
            o.WriteLine(f.DisplayName != null ? $"- CK name: {f.DisplayName} (from {Source})" : "- CK name: none (the Creation Kit gives it no display name)");
            if (f.FieldType != null) o.WriteLine($"- Type: {f.FieldType}{(m.Ck.Type(f.FieldType) is { Kind: "Enumeration" } en ? $" (an enum: describe {en.Name})" : "")}");
            if (f.ToolTip != null) o.WriteLine($"- CK tooltip: {f.ToolTip}");
            if (CkData.RangeText(f) is { } range) o.WriteLine($"- CK range: {range}");
            if (f.Hidden) o.WriteLine("- Hidden in the CK: its editor doesn't show this field.");
            if (f.Deprecated) o.WriteLine("- Deprecated in the CK.");
            var mut = m.Objects.Values.SelectMany(x => x.Fields).Where(x => x.CkField == $"{f.Type}.{f.Name}" && x.Path == x.Name).Select(x => $"{x.Object}.{x.Name}").ToList();
            if (mut.Count > 0) o.WriteLine($"- In Mutagen: {string.Join(", ", mut)}");
            o.WriteLine();
        }

        void CkMemberView(CkType t, CkMember x)
        {
            o.WriteLine($"# {t.Name}.{x.Name}");
            o.WriteLine();
            o.WriteLine($"- Engine name: {t.Name}.{x.Name} = {x.Value} (a member of an enum of the engine's reflection)");
            o.WriteLine(x.DisplayName != null ? $"- CK name: {x.DisplayName} (from {Source})" : "- CK name: none (the Creation Kit gives it no display name)");
            if (x.ToolTip != null) o.WriteLine($"- CK tooltip: {x.ToolTip}");
            if (x.Hidden) o.WriteLine("- Hidden in the CK.");
            if (x.Deprecated) o.WriteLine("- Deprecated in the CK.");
            o.WriteLine();
        }

        int Fail(string message) { o.WriteLine(message); return 1; }

        static string Count(int n) => n == 1 ? "1 field" : $"{n} fields";

        string Nearest(string name)
        {
            var near = m.RecordTypes.Select(r => r.Name).Concat(m.RecordTypes.Select(r => r.Sig!)).Distinct()
                .Where(n => n.Contains(name, NoCase) || name.Contains(n, NoCase)).Take(6).ToList();
            return near.Count > 0 ? $" Did you mean: {string.Join(", ", near)}?" : "";
        }

        // ---- cells ----

        string XEditName(FieldRow r)
            => r.Sig.Length > 0 && db.PairNames.TryGetValue((r.Sig, r.PlainPath), out var pn) ? PairMatcher.LastName(pn.XEditPath) : r.Def.XEdit ?? "";

        /// <summary>The Creation Kit's field a Mutagen field is (to-do F30), when the schema knows it and has the CK's names.</summary>
        CkField? CkOf(FieldDef f)
        {
            if (f.CkField is not { } cf) return null;
            int dot = cf.LastIndexOf('.');
            return dot > 0 ? m.Ck.Field(cf[..dot], cf[(dot + 1)..]) : null;
        }

        /// <summary>The CK's name of a field for a table's notes: <c>CK: "Spline Radius"</c>, and whether the CK hides it.</summary>
        string? CkNote(FieldDef f)
        {
            var ck = CkOf(f);
            string? name = f.CkName != null ? CkData.Shown(f.CkName, f.Name) : null;
            var parts = new List<string>();
            if (name != null) parts.Add($"CK: \"{name}\"");
            if (ck is { Hidden: true }) parts.Add("hidden in the CK");
            return parts.Count > 0 ? string.Join("; ", parts) : null;
        }

        static string TypeCell(FieldRow r)
        {
            var f = r.Def;
            string t = f.Kind;
            if (f.LinkTo != null) t += $" → {f.LinkTo}";
            if (f.RefObject != null) t += $" {f.RefObject}";
            if (r.Enum != null) t += $" {r.Enum}";
            if (f.Size != null) t += $", {f.Size} B";
            if (r.Offset != null && f.Kind != "Break") t += $" @{r.Offset}";
            return t;
        }

        string NotesCell(FieldRow r, bool cut)
        {
            var f = r.Def;
            var notes = new List<string>();
            if (CkNote(f) is { } ck) notes.Add(ck);
            if (f.Unknown) notes.Add("**unknown**");
            if (f.Nullable) notes.Add("may be absent");
            if (f.Unit != null) notes.Add($"unit: {f.Unit}");
            if (f.Translated) notes.Add($"translated text{(f.StringsTable != null ? $" ({StringsFile(f.StringsTable)})" : "")}");
            if (TextFormSays(f) is { } form) notes.Add(form);
            if (f.Default != null) notes.Add($"default {f.Default}");
            if (f.RefObject != null && cut)
            {
                int v = m.Variants(f.RefObject).Count;
                if (v > 0) notes.Add($"one of {v} kinds of {f.RefObject}: `describe {f.RefObject}` lists them");
            }
            if (f.RefObject != null && r.Stack.Contains("/" + f.RefObject + "/", StringComparison.Ordinal)) notes.Add($"{f.RefObject} again (it holds itself): not spelled out twice");
            if (f.Note != null) notes.Add(f.Note);
            if (f.Marker != null) notes.Add($"after an empty {f.Marker} marker subrecord");
            if (f.EndMarker != null) notes.Add($"ends with an empty {f.EndMarker} subrecord");
            if (f.Path == f.Name && r.Sig.Length > 0 && db.Evidence.TryGetValue((r.Sig, f.Object, f.Name), out var ev)) notes.Add("evidence: " + ev);
            return string.Join("; ", notes).Replace("|", "\\|");
        }

        void Table(IEnumerable<(FieldRow Row, bool Cut)> rows, string? firstColumn = null)
        {
            o.WriteLine($"| {firstColumn ?? "Path"} | Sig | Type | xEdit name | Notes |");
            o.WriteLine("|---|---|---|---|---|");
            foreach (var (r, cut) in rows)
                o.WriteLine($"| {r.Path} | {SigCell(r)} | {TypeCell(r)} | {XEditName(r)} | {NotesCell(r, cut)} |");
        }

        // ---- a filter: ranked, the repeats put together ----

        bool Matches(FieldRow r, string filter) => r.Path.Contains(filter, NoCase) || XEditName(r).Contains(filter, NoCase)
            || (r.Def.Readable ?? "").Contains(filter, NoCase) || (r.Subrecord ?? "").Equals(filter, NoCase);

        /// <summary>A path segment's own name: <c>Ranks[*]</c> is Ranks, <c>Data{ConditionGlobal}</c> is Data.</summary>
        static string Bare(string segment)
        {
            int cut = segment.IndexOfAny(new[] { '[', '{' });
            return cut < 0 ? segment : segment[..cut];
        }

        static string[] Segments(string path) => System.Text.RegularExpressions.Regex.Split(path, @"\.(?![^{]*\})");

        /// <summary>The path with every kind of object written {*}: the rows of one field in many kinds of object have one.</summary>
        static string Collapsed(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]*\}", "{*}");

        sealed record RankedLine(int Rank, string Path, string Sig, string Type, string XEdit, string Notes);

        /// <summary>
        /// The rows a filter matched, in three ranks: fields whose own name is the filter; fields whose own name, readable
        /// name, xEdit name or subrecord holds it; fields that only sit inside one that does (put together under it, one
        /// row with how many). One field reached through many kinds of object (a condition's 612 kinds of data, a script
        /// property's kinds) is one row that says how many and how to see each.
        /// </summary>
        List<RankedLine> Ranked(ObjDef t, List<FieldRow> rows, string filter)
        {
            var groups = new List<(int Rank, string Key, List<FieldRow> Rows)>();
            var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                var segs = Segments(r.Path);
                string leaf = Bare(segs[^1]);
                int rank = leaf.Equals(filter, NoCase) ? 0
                    : leaf.Contains(filter, NoCase) || XEditName(r).Contains(filter, NoCase) || (r.Def.Readable ?? "").Contains(filter, NoCase) || (r.Subrecord ?? "").Equals(filter, NoCase) ? 1 : 2;
                string key;
                if (rank == 2)
                {
                    // The first segment on the way whose name holds the filter: everything inside it is one row
                    int at = Array.FindIndex(segs, s => Bare(s).Contains(filter, NoCase));
                    key = Collapsed(string.Join(".", segs.Take(at + 1)));
                }
                else key = Collapsed(r.Path);
                string full = rank + "|" + key;
                if (!byKey.TryGetValue(full, out int g)) { byKey[full] = g = groups.Count; groups.Add((rank, key, new List<FieldRow>())); }
                groups[g].Rows.Add(r);
            }
            var lines = new List<RankedLine>();
            // Within a rank, the record's own fields before the ones deeper inside
            foreach (var (rank, key, list) in groups.OrderBy(g => g.Rank).ThenBy(g => Segments(g.Key).Length))
            {
                var r = list[0];
                if (rank == 2)
                {
                    string plain = string.Join(".", Segments(key).Select(s => s.Replace("{*}", "")));
                    lines.Add(new RankedLine(2, key + ".…", "", list.Count == 1 ? "1 field" : $"{list.Count} fields", "",
                        $"inside {key}, which holds \"{filter}\" in its name: describe {t.Name}.{plain} lists them"));
                }
                else if (list.Count == 1)
                    lines.Add(new RankedLine(rank, r.Path, r.Subrecord ?? "", TypeCell(r), XEditName(r), NotesCell(r, false)));
                else
                {
                    var kinds = list.SelectMany(x => System.Text.RegularExpressions.Regex.Matches(x.Path, @"\{([^}]*)\}").Select(m => m.Groups[1].Value)).Distinct().ToList();
                    var typesSeen = list.Select(TypeCell).Distinct().ToList();
                    var sigs = list.Select(x => x.Subrecord ?? "").Distinct().ToList();
                    lines.Add(new RankedLine(rank, key, sigs.Count == 1 ? sigs[0] : string.Join(" ", sigs.Where(s => s.Length > 0).Take(4)),
                        typesSeen.Count == 1 ? typesSeen[0] : "differs by kind", XEditName(r),
                        $"the same field in {list.Count} places, one per kind of object ({string.Join(", ", kinds.Take(3))}{(kinds.Count > 3 ? ", …" : "")}): describe {t.Name} {filter} --all lists each"));
                }
            }
            return lines;
        }

        void RankedTable(List<RankedLine> lines)
        {
            o.WriteLine("| Path | Sig | Type | xEdit name | Notes |");
            o.WriteLine("|---|---|---|---|---|");
            foreach (var l in lines) o.WriteLine($"| {l.Path} | {l.Sig} | {l.Type} | {l.XEdit} | {l.Notes.Replace("|", "\\|")} |");
        }

        /// <summary>A type's rows. Unless everything is asked for, the kinds of a base with variants aren't spelled out.</summary>
        IEnumerable<(FieldRow Row, bool Cut)> Rows(ObjDef t, bool all)
        {
            var skipBelow = new List<string>();
            foreach (var r in m.Expand(t.Name))
            {
                if (skipBelow.Any(s => r.Path.StartsWith(s, StringComparison.Ordinal))) continue;
                bool cut = r.Def.RefObject != null && m.Variants(r.Def.RefObject).Count > 0 && (!all || m.Variants(r.Def.RefObject).Count > SchemaModel.BigVariantLimit);
                if (cut && !all) skipBelow.Add(r.Path + "{");
                // The base's own fields still follow (they have no marker); only the {Variant} rows are skipped
                yield return (r, cut);
            }
        }

        // ---- a record type or an object ----

        int Type(ObjDef t, string? filter, bool all)
        {
            bool record = t.ObjType == "Record" && !t.Abstract && t.Sig != null;
            var rows = Rows(t, all || filter != null).ToList();
            if (filter != null)
            {
                rows = rows.Where(x => Matches(x.Row, filter)).ToList();
                if (rows.Count == 0) return Fail($"{Label(t)} has no field with \"{filter}\" in its path, its xEdit name or its signature.{Suggest(new[] { t.Name }, filter)}");
                if (all)
                {
                    o.WriteLine($"# {Label(t)}: {Count(rows.Count)} matching \"{filter}\"");
                    o.WriteLine();
                    Table(rows);
                    return 0;
                }
                var lines = Ranked(t, rows.Select(x => x.Row).ToList(), filter);
                o.WriteLine(lines.Count < rows.Count ? $"# {Label(t)}: {lines.Count} row{(lines.Count == 1 ? "" : "s")} matching \"{filter}\" ({rows.Count} paths: the ones that repeat are put together)"
                    : $"# {Label(t)}: {Count(rows.Count)} matching \"{filter}\"");
                o.WriteLine("Fields named so come first, then fields whose readable or xEdit name holds it, then fields that only sit inside one that does.");
                o.WriteLine();
                RankedTable(lines);
                return 0;
            }
            var real = rows.Select(x => x.Row.Def).Where(d => d.Path == d.Name && d.Kind is not ("Break" or "Marker")).Distinct().ToList();
            o.WriteLine($"# {Label(t)}");
            o.WriteLine();
            o.WriteLine(record
                ? $"Record type. Signature {t.Sig}, Mutagen name {t.Name}{(t.XEditName != null ? $", xEdit name \"{t.XEditName}\"" : "")}, defined in {t.File}."
                : $"{(t.Abstract ? "Base object (abstract)" : "Object")}{(t.Sig != null ? $", subrecord {t.Sig}" : "")}{(t.Base != null ? $", based on {t.Base}" : "")}, defined in {t.File}.{(t.FixedSize != null ? $" Always {t.FixedSize} bytes." : "")}");
            o.WriteLine($"{rows.Count} rows below; {real.Count} fields, {real.Count(d => d.Unknown)} of them unknown, {real.Count(d => d.XEdit != null)} with a name from xEdit.");
            if (t.LinkInterfaces.Count > 0) o.WriteLine($"Links to these also accept it: {string.Join(", ", t.LinkInterfaces)}.");
            if (CkObjectLine(t) is { } ckLine) o.WriteLine(ckLine);
            if (Markers(t) is { } markers) o.WriteLine(markers);
            if (HandReadSays(t) is { } hand) o.WriteLine(hand);
            var variants = m.Variants(t.Name);
            if (variants.Count > 0)
                o.WriteLine($"{variants.Count} kinds: {string.Join(", ", variants.Take(all ? int.MaxValue : 80))}{(variants.Count > 80 && !all ? $" and {variants.Count - 80} more (--all lists them)" : "")}.");
            if (!all && rows.Any(x => x.Cut)) o.WriteLine("Fields that can hold several kinds of object show one row. Add --all to spell every kind out, or describe the kind.");
            o.WriteLine();
            Table(rows);
            return 0;
        }

        /// <summary>The strings file a translated text is read from, by the XML's translated (Normal, DL, IL).</summary>
        static string StringsFile(string table) => table switch { "DL" => ".dlstrings", "IL" => ".ilstrings", _ => ".strings" };

        /// <summary>How a text is stored, when it isn't Mutagen's default (the text and a zero byte). Null otherwise.</summary>
        static string? TextFormSays(FieldDef f) => f.TextForm switch
        {
            "Plain" => "stored without an end mark",
            "NullTerminateIfNotEmpty" => "stored with a zero byte after it when not empty",
            "PrependLength" => "stored with a 4-byte length before it",
            "PrependLengthWithNullIfContent" => "stored with a 4-byte length before it (counting a zero byte after a text that isn't empty)",
            "PrependLengthUShort" => "stored with a 2-byte length before it",
            "PrependLengthUInt8" => "stored with a 1-byte length before it",
            "NullPaddedFixedLength" => $"stored in {f.Size?.ToString() ?? "a fixed number of"} bytes, padded with zeros",
            _ => null,
        };

        /// <summary>One line on an object Mutagen reads (in part) with hand-written code: which code, and that the
        /// schema gives no offset where that code decides the place (GAPS S36). Null when none.</summary>
        string? HandReadSays(ObjDef t)
        {
            string? code = HandCode(t.Name);
            return code == null ? null : $"Read in part by hand-written Mutagen code: {code}. Where that code decides a field's place, the schema gives no offset: the real layout is that code's, not the XML's field order.";
        }

        /// <summary>The hand-written code that reads an object, its own and its bases' (Objects.HandRead), in words.</summary>
        string? HandCode(string obj) => m.HandReadOf(obj);

        /// <summary>One line on an object's markers (the XML's markerType and endMarkerType, its own or a base's) and the
        /// other subrecords it holds (AdditionalContainedRecordType). Null when it has none.</summary>
        string? Markers(ObjDef t)
        {
            string? start = m.MarkerOf(t.Name), end = m.EndMarkerOf(t.Name);
            var contains = m.ContainsOf(t.Name).ToList();
            var parts = new List<string>();
            string From(Func<ObjDef, string?> get, string sig) => get(t) == sig ? "" : $" (from {m.Ancestry(t.Name).First(a => get(a) == sig).Name})";
            if (start != null && end != null)
                parts.Add($"Starts with an empty {start} subrecord{From(a => a.Marker, start)} and ends with an empty {end} subrecord{From(a => a.EndMarker, end)}.");
            else if (start != null) parts.Add($"Starts with an empty {start} subrecord{From(a => a.Marker, start)}.");
            else if (end != null) parts.Add($"Ends with an empty {end} subrecord{From(a => a.EndMarker, end)}: in a list, it closes each item.");
            if (contains.Count > 0)
                parts.Add($"Also holds {string.Join(", ", contains.Take(12))}{(contains.Count > 12 ? $" and {contains.Count - 12} more" : "")}, read by hand-written Mutagen code; the XML doesn't say where they stand.");
            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }

        /// <summary>Why a field inside a subrecord has no offset, when hand-written Mutagen code places it: that code,
        /// as the field's object (or the object above it) names it. Null when the schema just can't sum the sizes.</summary>
        string? PlacedBy(FieldRow r)
        {
            for (var x = r; x != null; x = x.Parent)
                if (HandCode(x.Def.Object) is { } code)
                    return $"Mutagen's hand-written code places it: {code}";
            return null;
        }

        int Several(string sig, List<ObjDef> types, string? filter, bool all)
        {
            o.WriteLine($"# {sig}: the signature of {types.Count} record types");
            o.WriteLine();
            o.WriteLine("| Mutagen name | Fields | Unknown | Defined in |");
            o.WriteLine("|---|---|---|---|");
            foreach (var t in types)
            {
                var real = m.Expand(t.Name).Select(r => r.Def).Where(d => d.Path == d.Name && d.Kind is not ("Break" or "Marker")).Distinct().ToList();
                o.WriteLine($"| {t.Name} | {real.Count} | {real.Count(d => d.Unknown)} | {t.File} |");
            }
            o.WriteLine();
            if (filter == null) { o.WriteLine($"Describe one by its Mutagen name: `describe {types[0].Name}`. A filter after the signature searches all {types.Count}."); return 0; }
            int found = 0;
            foreach (var t in types)
            {
                var rows = Rows(t, true).Where(x => Matches(x.Row, filter)).ToList();
                if (rows.Count == 0) continue;
                found += rows.Count;
                if (all)
                {
                    o.WriteLine($"## {t.Name}: {Count(rows.Count)} matching \"{filter}\"");
                    o.WriteLine();
                    Table(rows);
                }
                else
                {
                    var lines = Ranked(t, rows.Select(x => x.Row).ToList(), filter);
                    o.WriteLine(lines.Count < rows.Count ? $"## {t.Name}: {lines.Count} row{(lines.Count == 1 ? "" : "s")} matching \"{filter}\" ({rows.Count} paths: the ones that repeat are put together)"
                        : $"## {t.Name}: {Count(rows.Count)} matching \"{filter}\"");
                    o.WriteLine();
                    RankedTable(lines);
                }
                o.WriteLine();
            }
            return found > 0 ? 0 : Fail($"None of the {types.Count} has a field with \"{filter}\" in its path, its xEdit name or its signature.");
        }

        // The Sig column: the field's own subrecord, or the subrecords Mutagen's hand-written code fills it from (GAPS S38)
        // (in brackets: the XML names them on the step, not on the field), on the field's own row and on the step's
        static string SigCell(FieldRow r) => r.Subrecord
            ?? (r.Def.CustomSubrecords != null && (r.Def.Kind == "CustomLogic" || r.Def.FilledBy != null) && r.Def.Path == r.Def.Name
                ? $"({string.Join(" ", r.Def.CustomSubrecordList)})" : "");

        // ---- a file address ----

        int Address(List<ObjDef> types, string address)
        {
            string sub = address; int? offset = null;
            int at = address.IndexOf('@');
            if (at > 0) { sub = address[..at]; if (int.TryParse(address[(at + 1)..], out int n)) offset = n; }
            int found = 0;
            foreach (var t in types)
            {
                // A field Mutagen's hand-written code fills from that subrecord (its CustomSubrecords, GAPS S38) is found too
                var rows = m.Expand(t.Name).Where(r => (offset == null || r.Offset == offset) && ((r.Subrecord ?? "").Equals(sub, NoCase)
                    || offset == null && r.Subrecord == null && r.Def.Kind != "CustomLogic" && r.Def.FilledBy != null && r.Def.CustomSubrecordList.Contains(sub, StringComparer.OrdinalIgnoreCase))).ToList();
                if (rows.Count == 0) continue;
                found += rows.Count;
                o.WriteLine($"# {Label(t)}, subrecord {sub.ToUpperInvariant()}{(offset != null ? $", offset {offset}" : "")}: {Count(rows.Count)}");
                o.WriteLine();
                Table(rows.Select(r => (r, false)));
                o.WriteLine();
            }
            if (found > 0) return 0;
            var subs = types.SelectMany(t => m.Expand(t.Name)).SelectMany(r => r.Def.Kind != "CustomLogic" && r.Def.FilledBy != null ? r.Def.CustomSubrecordList.Append(r.Subrecord) : new[] { r.Subrecord })
                .Where(s => s != null).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            return Fail($"{string.Join(", ", types.Select(Label))} has no field in a subrecord \"{sub}\"{(offset != null ? $" at offset {offset}" : "")}. Its subrecords: {string.Join(" ", subs)}.");
        }

        // ---- a field by path ----

        int FieldByPath(List<ObjDef> types, string path)
        {
            int found = 0;
            SchemaModel.Lookup? firstMiss = null; ObjDef? missType = null;
            foreach (var t in types)
            {
                var look = m.Resolve(t.Name, path, ignoreCase: true);
                if (look.Found.Count == 0) { firstMiss ??= look; missType ??= t; continue; }
                foreach (var f in look.Found.Take(12)) { Field(t, f); found++; }
                if (look.Found.Count > 12) o.WriteLine($"…and {look.Found.Count - 12} more places with the same path (through other kinds of object).");
            }
            if (found > 0) return 0;
            var miss = firstMiss!;
            string where = miss.PathSoFar.Length == 0 ? missType!.Name : $"{missType!.Name}.{miss.PathSoFar}";
            if (miss.StoppedAtLeaf is { } leaf)
                return Fail($"{where} is one {leaf.Def.Kind} value{(leaf.Enum != null ? $" (enum {leaf.Enum})" : "")}: it has no part \"{miss.StoppedAtSegment}\".");
            return Fail($"{where} has no field \"{miss.StoppedAtSegment}\".{Suggest(miss.StoppedAtObjects.Count > 0 ? miss.StoppedAtObjects : new List<string> { missType.Name }, miss.StoppedAtSegment)}");
        }

        void Field(ObjDef t, SchemaModel.Found found)
        {
            var r = found.Row; var f = r.Def;
            o.WriteLine($"# {t.Name}.{r.Path}{(found.Match is "flag" or "component" ? "." + found.Part : found.Part ?? "")}");
            o.WriteLine();
            if (found.Match == "flag") o.WriteLine($"A member of the enum {r.Enum}, held by the field below.");
            if (found.Match == "component") o.WriteLine($"One part ({found.Part}) of the {f.Kind} value below.");
            o.WriteLine($"- Mutagen path: {t.Name}.{r.Path}");
            // A field Mutagen's hand-written code fills from subrecords the XML names on its CustomLogic step (GAPS S38)
            string address = r.Subrecord == null
                ? f.CustomSubrecords != null && f.FilledBy != null
                    ? $"{string.Join(", ", f.CustomSubrecordList.Select(s => (r.Sig.Length > 0 ? r.Sig + "/" : "") + s))}, read by Mutagen's hand-written code ({f.FilledBy.Replace(",", ", ")})"
                    : "no subrecord of its own is known"
                : $"{(r.Sig.Length > 0 ? r.Sig + "/" : "")}{r.Subrecord}";
            if (r.Subrecord != null && r.Offset != null) address += $", offset {r.Offset}";
            string? placedBy = r.Offset == null && r.Subrecord != null && f.Size != null && !f.Translated ? PlacedBy(r) : null;
            if (placedBy != null) address += $" (no offset: {placedBy})";
            if (r.Subrecord != null && f.Size != null) address += $", {f.Size} bytes";
            if (r.InList) address += " (inside a list: per item)";
            o.WriteLine($"- File address: {address}");
            string xe = XEditName(r);
            if (xe.Length > 0)
            {
                // xEdit's non-names ("Unknown", "Unused", "CTDA") give way to a better own name (PairMatcher.ReadableName)
                string readable = r.Sig.Length > 0 && db.PairNames.TryGetValue((r.Sig, r.PlainPath), out var own) ? own.Readable : f.Readable ?? PairMatcher.SpacedOut(f.Name);
                bool ownName = !readable.Equals(xe, StringComparison.Ordinal);
                string line = ownName ?$"- Readable name: {readable} (Mutagen's name spaced out: xEdit calls it \"{xe}\"" : $"- Readable name: {xe} (xEdit's name";
                if (r.Sig.Length > 0 && db.PairNames.TryGetValue((r.Sig, r.PlainPath), out var pn)) line += $"; xEdit path {pn.XEditPath}";
                if (db.PairChecks.TryGetValue((f.Object, f.Ord), out var chk)) line += $"; checked on {chk.Checked} records, {chk.Agree} agree";
                // An object or a list no pair names: xEdit's element its paired fields sit in (GAPS S98)
                else if (PairMatcher.IsContainer(r)) line += r.Def.Kind is "List" or "Array" or "Array2D" or "Dictionary"
                    ? "; the xEdit element its items' paired fields sit in" : "; the xEdit element its paired fields sit in";
                o.WriteLine(line + ")");
            }
            else o.WriteLine($"- Readable name: {f.Readable} (Mutagen's name spaced out; no checked xEdit pair)");
            if (f.CkName != null || f.CkField != null) CkFieldLines(f.CkName, f.CkField, CkOf(f));
            o.WriteLine($"- Type: {TypeCell(r)}");
            o.WriteLine(f.Nullable ? "- May be absent." : "- Always there: a value the JSON leaves out is the default.");
            if (f.Unknown) o.WriteLine("- Unknown: Mutagen has no real name for it yet.");
            if (f.Unit != null) o.WriteLine($"- Unit: {f.Unit}");
            if (f.Translated) o.WriteLine(f.StringsTable != null ? $"- Translated text, from the plugin's {StringsFile(f.StringsTable)} file in a localized plugin." : "- Translated text.");
            if (TextFormSays(f) is { } form) o.WriteLine($"- Text {form}.");
            if (f.CounterLength is int cl) o.WriteLine(f.Note != null && f.Note.Contains("counted by", StringComparison.Ordinal)
                ? $"- Its count is a {cl}-byte counter subrecord." : $"- Its count: {cl} bytes before the entries.");
            if (f.Default != null) o.WriteLine($"- Default: {f.Default}");
            if (f.Custom) o.WriteLine($"- Read by hand-written Mutagen code{(f.ClrType != null ? $" (compiled type {f.ClrType})" : "")}.");
            if (f.Note != null) o.WriteLine($"- Note: {f.Note}");
            if (f.Path == f.Name && r.Sig.Length > 0 && db.Evidence.TryGetValue((r.Sig, f.Object, f.Name), out var ev)) o.WriteLine($"- Evidence: {ev}");
            o.WriteLine($"- Defined in: {f.Object} ({m.Objects[f.Object].File})");
            if (r.Enum != null && m.Enums.TryGetValue(r.Enum, out var members))
            {
                o.WriteLine($"- {(members.Any(x => x.IsFlags) ? "Flags" : "Values")} of {r.Enum}: " +
                    string.Join(", ", members.Take(40).Select(x => $"{x.Name}={(x.IsFlags ? "0x" + x.Value.ToString("X") : x.Value.ToString())}{(x.XEditName != null && x.XEditName != x.Name ? $" (xEdit: {x.XEditName})" : "")}{(CkData.Shown(x.CkName, x.Name) is { } ckm ? $" (CK: \"{ckm}\")" : "")}")) +
                    (members.Count > 40 ? $" and {members.Count - 40} more" : ""));
            }
            // What sits below an object or a list. Where several kinds of object can stand, only what all of them share.
            var below = Rows(t, false).Where(x => x.Row.Path.Length > r.Path.Length && x.Row.Path.StartsWith(r.Path, StringComparison.Ordinal)
                && x.Row.Path[r.Path.Length] is '.' or '[' or '{').ToList();
            if (below.Count > 0)
            {
                o.WriteLine();
                o.WriteLine("Below it:");
                o.WriteLine();
                Table(below);
            }
            o.WriteLine();
        }

        // ---- a field by name ----

        /// <summary>"Did you mean": the fields nearest to a wrong name, at the level of the path where it stopped. The
        /// choice and the wording are the ones the SQL layer and the record layer use (<see cref="NearNames"/>).</summary>
        string Suggest(IEnumerable<string> objects, string wanted) => NearNames.DidYouMean(NearNames.Fields(db, objects, wanted));

        /// <param name="anyPart">Asked for as a search: every field with the word in a name, not only the fields named exactly so.</param>
        public int ByName(string name, bool anyPart = false)
        {
            // Where each object field is used: the first path in every record type that reaches it
            var uses = new Dictionary<FieldDef, List<FieldRow>>();
            foreach (var t in m.RecordTypes)
            {
                var seen = new HashSet<FieldDef>();
                foreach (var r in m.Expand(t.Name))
                    if (seen.Add(r.Def)) (uses.TryGetValue(r.Def, out var l) ? l : uses[r.Def] = new()).Add(r);
            }
            bool Exact(FieldDef f) => f.Name.Equals(name, NoCase) || (f.Readable ?? "").Equals(name, NoCase) || (f.XEdit ?? "").Equals(name, NoCase) || (f.CkName ?? "").Equals(name, NoCase);
            bool Loose(FieldDef f) => f.Name.Contains(name, NoCase) || (f.Readable ?? "").Contains(name, NoCase) || (f.XEdit ?? "").Contains(name, NoCase) || (f.CkName ?? "").Contains(name, NoCase);
            var fields = m.Objects.Values.SelectMany(x => x.Fields).Where(f => f.Kind is not ("Break" or "Marker")).ToList();
            // A record type's own xEdit name for a path counts too ("Base Damage" may be the name only in WEAP)
            var byPair = db.PairNames.Where(p => p.Value.Readable.Equals(name, NoCase) || PairMatcher.LastName(p.Value.XEditPath).Equals(name, NoCase)).Select(p => p.Key).ToHashSet();
            var hits = fields.Where(f => Exact(f) || (uses.TryGetValue(f, out var u) && u.Any(r => byPair.Contains((r.Sig, r.PlainPath))))).ToList();
            string how = "is the name of";
            if (hits.Count == 0 || anyPart) { hits = fields.Where(Loose).ToList(); how = "is part of the name of"; }
            if (hits.Count == 0 && m.Ck.Present && !anyPart && CkDescribe(name) is int ck) return ck;
            if (hits.Count == 0 && CkByName(name, anyPart)) return 0;
            if (hits.Count == 0) return Fail($"Nothing in the schema is called \"{name}\": no record type, signature, object, field, or xEdit name{(m.Ck.Present ? ", nor a class, field or CK name of the engine's reflection" : "")}.{Nearest(name)}");

            var used = hits.Where(uses.ContainsKey).ToList();
            o.WriteLine($"# \"{name}\" {how} {hits.Count} {(hits.Count == 1 ? "field" : "fields")}");
            // Fields named exactly so are listed; the ones that hold the word in a longer name are one more call away
            int longer = how == "is the name of" ? fields.Count(Loose) - hits.Count : 0;
            if (longer > 0) o.WriteLine($"{longer} more {(longer == 1 ? "field holds" : "fields hold")} \"{name}\" inside a longer name: describe *{name}* lists them too.");
            o.WriteLine();
            o.WriteLine("| Field | Sig | Type | xEdit name | Used in | Notes |");
            o.WriteLine("|---|---|---|---|---|---|");
            int shown = 0;
            foreach (var f in hits.OrderByDescending(f => uses.ContainsKey(f)).ThenBy(f => f.Object, StringComparer.Ordinal).ThenBy(f => f.Ord))
            {
                if (++shown > 60) { o.WriteLine($"| …and {hits.Count - 60} more | | | | | |"); break; }
                if (uses.TryGetValue(f, out var u))
                {
                    var r = u[0];
                    string usedIn = u.Count == 1 ? r.Sig : $"{u.Count} record types: {string.Join(" ", u.Take(8).Select(x => x.Sig))}{(u.Count > 8 ? " …" : "")}";
                    o.WriteLine($"| {r.Type}.{r.Path} | {r.Subrecord} | {TypeCell(r)} | {XEditName(r)} | {usedIn} | {NotesCell(r, false)} |");
                }
                else
                {
                    var r = m.Shallow(f.Object).First(x => x.Def == f);
                    o.WriteLine($"| {f.Object}.{f.Path} | {r.Subrecord} | {TypeCell(r)} | {f.XEdit} | not reached from a record type | {NotesCell(r, false)} |");
                }
            }
            if (m.Ck.Present)
            {
                var ckHits = CkMatches(name, how != "is the name of");
                if (ckHits.Count > 0)
                    o.WriteLine($"\nIn the engine's reflected classes (inside REFL data, which the record card decodes), {ckHits.Count} {(ckHits.Count == 1 ? "field has" : "fields have")} \"{name}\" in {(ckHits.Count == 1 ? "its" : "their")} Creation Kit or engine name: "
                        + string.Join(", ", ckHits.Take(4).Select(f => $"{f.Type}.{f.Name}{(f.DisplayName != null ? $" (\"{f.DisplayName}\")" : "")}")) + (ckHits.Count > 4 ? $" and {ckHits.Count - 4} more" : "") + ".");
            }
            return 0;
        }

        /// <summary>Fields of the reflected classes whose CK name or engine name is (or holds) the word.</summary>
        List<CkField> CkMatches(string name, bool loose) => m.Ck.Types.Values.SelectMany(t => t.Fields)
            .Where(f => loose ? (f.DisplayName ?? "").Contains(name, NoCase) || f.Name.Contains(name, NoCase) : (f.DisplayName ?? "").Equals(name, NoCase) || f.Name.Equals(name, NoCase))
            .OrderBy(f => f.Type, StringComparer.Ordinal).ThenBy(f => f.Ord).ToList();

        /// <summary>The reflected classes' fields named so, when no Mutagen field is: a table of them. False when none is.</summary>
        bool CkByName(string name, bool anyPart)
        {
            if (!m.Ck.Present) return false;
            var hits = CkMatches(name, false);
            string how = "is the name of";
            if (hits.Count == 0 || anyPart) { hits = CkMatches(name, true); how = "is part of the name of"; }
            if (hits.Count == 0) return false;
            o.WriteLine($"# \"{name}\" {how} {hits.Count} {(hits.Count == 1 ? "field" : "fields")} of the engine's reflected classes");
            o.WriteLine($"No Mutagen field is called so. These are inside REFL data (the record card decodes it) or in material databases; the names are from {Source}.");
            o.WriteLine();
            o.WriteLine("| Field | Type | CK name | Notes |");
            o.WriteLine("|---|---|---|---|");
            foreach (var f in hits.Take(60)) o.WriteLine($"| {f.Type}.{f.Name} | {f.FieldType} | {f.DisplayName} | {CkNotes(f.ToolTip, f.Hidden, f.Deprecated, CkData.RangeText(f))} |");
            if (hits.Count > 60) o.WriteLine($"| …and {hits.Count - 60} more | | | |");
            return true;
        }
    }

    // ---- stats and sql ----

    public static void Stats(string dbFile, TextWriter o)
    {
        using var con = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly;Pooling=False");
        con.Open();
        foreach (var q in new[]
        {
            "SELECT Key, Value FROM Meta ORDER BY Key",
            "SELECT count(*) AS record_types, count(DISTINCT Sig) AS signatures, sum(Fields) AS fields, sum(Paths) AS paths, sum(Unknown) AS unknown, sum(Named) AS with_xedit_name FROM RecordTypes",
            "SELECT count(*) AS objects, sum(Abstract) AS abstract, sum(Struct) AS plain_structs, sum(FixedSize IS NOT NULL) AS with_fixed_size FROM Objects",
            "SELECT count(*) AS object_fields, sum(Path = Name) AS real_fields, sum(Unknown AND Path = Name) AS unknown, sum(XEdit IS NOT NULL) AS with_xedit_name, sum(Custom) AS hand_written, sum(Subrecord IS NOT NULL) AS with_own_signature, sum(Offset IS NOT NULL) AS with_offset, sum(Unit IS NOT NULL) AS with_unit FROM ObjectFields",
            "SELECT Kind, count(*) AS n FROM ObjectFields GROUP BY Kind ORDER BY n DESC",
            "SELECT count(DISTINCT Enum) AS enums, count(*) AS members, sum(XEditName IS NOT NULL) AS with_xedit_name FROM Enums",
            "SELECT count(*) AS link_fields, count(DISTINCT LinkTo) AS link_targets FROM ObjectFields WHERE LinkTo IS NOT NULL",
            "SELECT coalesce(Match, 'unmatched: ' || Reason) AS pairs, count(*) AS n FROM Pairs GROUP BY 1 ORDER BY Match IS NULL, n DESC",
            "SELECT count(*) AS evidence_rows, sum(Matched) AS matched_to_a_field FROM UnknownEvidence",
            "SELECT (SELECT count(*) FROM CkTypes) AS ck_types, (SELECT count(*) FROM CkTypes WHERE DisplayName IS NOT NULL) AS ck_types_named, (SELECT count(*) FROM CkFields) AS ck_fields, (SELECT count(*) FROM CkFields WHERE DisplayName IS NOT NULL) AS ck_fields_named, (SELECT count(*) FROM CkMembers WHERE DisplayName IS NOT NULL) AS ck_members_named, (SELECT count(*) FROM ObjectFields WHERE CkName IS NOT NULL) AS mutagen_fields_with_ck_name",
        })
        {
            Sql(con, q, o);
            o.WriteLine();
        }
    }

    public static void Sql(string dbFile, string query, TextWriter o)
    {
        using var con = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly;Pooling=False");
        con.Open();
        Sql(con, query, o);
    }

    static void Sql(SqliteConnection con, string query, TextWriter o)
    {
        using var c = con.CreateCommand(); c.CommandText = query;
        using var r = c.ExecuteReader();
        o.WriteLine(string.Join('\t', Enumerable.Range(0, r.FieldCount).Select(r.GetName)));
        while (r.Read()) o.WriteLine(string.Join('\t', Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "" : r.GetValue(i).ToString())));
    }
}
