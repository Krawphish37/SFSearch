using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    /// <summary>
    /// Blueprint research commands: MkDiff (upgraded item vs its base), OmodCensus (quality OMOD sets) and
    /// RecipeCensus (COBJ classes). Each one replaces a long series of hand-written census SQL queries.
    /// </summary>
    public static class ResearchCommands
    {
        // ================================================================ Record store
        public class Rec
        {
            public string FormKey = "";
            public string Signature = "";
            public string EditorID = "";
            public string Name = "";
            public string Plugin = "";
            public bool IsOverride;
            public JsonObject Data = new();
        }

        /// <summary>
        /// Read-only view of a plugin and its masters (+ extra plugins). Get(fk) returns the version the plugin sees:
        /// its own record or override first, then its masters (later masters first), then the extra plugins.
        /// </summary>
        public sealed class RecordStore : IDisposable
        {
            public string Target = "";
            /// <summary>The plugin's own file name (cache Meta FileName). Differs from Target for a copy read by path ("_file1_<name>.esm").</summary>
            public string TargetFile = "";
            public List<string> Chain = new();
            readonly List<(string Plugin, SqliteConnection Conn, Dictionary<string, long>? Rows)> _chain = new();
            readonly Dictionary<string, Rec?> _cache = new(StringComparer.OrdinalIgnoreCase);

            public static RecordStore Open(string targetArg, IEnumerable<string> extra)
            {
                var s = new RecordStore { Target = Program.EnsureCacheForFile(targetArg) };
                var main = Sfs.OpenCache(s.Target, false);
                s.TargetFile = Sfs.Meta(main, "FileName") ?? s.Target;
                s._chain.Add((s.Target, main, null));
                var order = Enumerable.Reverse(Sfs.Masters(main)).Concat(extra).ToList();
                foreach (var p in order.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (s._chain.Any(c => c.Plugin.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
                    Program.EnsureCacheUpToDatePublic(p);
                    if (!Sfs.HasCache(p)) { Console.Error.WriteLine($"[sfs] No cache for {p}: records from it can't be resolved."); continue; }
                    s._chain.Add((p, Sfs.OpenCache(p, false), null));
                }
                s.Chain = s._chain.Select(c => c.Plugin).ToList();
                return s;
            }

            public void Dispose()
            {
                foreach (var c in _chain) c.Conn.Dispose();
                _chain.Clear();
            }

            static Rec Read(string plugin, SqliteDataReader r) => new Rec
            {
                FormKey = r.GetString(0),
                Signature = r.GetString(1),
                EditorID = r.IsDBNull(2) ? "" : r.GetString(2),
                Name = r.IsDBNull(3) ? "" : r.GetString(3),
                IsOverride = !r.IsDBNull(4) && r.GetInt64(4) == 1,
                Data = (JsonNode.Parse(r.GetString(5)) as JsonObject) ?? new JsonObject(),
                Plugin = plugin
            };

            /// <summary>All records of the given signatures in the target plugin itself (new records and overrides).</summary>
            public List<Rec> TargetRecords(params string[] sigs)
            {
                var list = new List<Rec>();
                var (plugin, conn, _) = _chain[0];
                using var cmd = conn.CreateCommand();
                var names = sigs.Select((s, i) => "@s" + i).ToList();
                cmd.CommandText = $"SELECT FormKey, Signature, EditorID, Name, IsOverride, Data FROM Records WHERE Signature IN ({string.Join(",", names)})";
                for (int i = 0; i < sigs.Length; i++) cmd.Parameters.AddWithValue(names[i], sigs[i]);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var rec = Read(plugin, r);
                    list.Add(rec);
                    _cache.TryAdd(rec.FormKey, rec);
                }
                return list;
            }

            public Rec? Get(string? fk)
            {
                if (string.IsNullOrEmpty(fk) || fk.IndexOf(':') < 0) return null;
                fk = Sfs.NormalizeFormKey(fk);
                if (_cache.TryGetValue(fk, out var hit)) return hit;
                Rec? found = null;
                for (int i = 0; i < _chain.Count && found == null; i++)
                {
                    var (plugin, conn, rows) = _chain[i];
                    if (rows == null)
                    {
                        // FormKey spelling of plugin names can differ in case between plugins, so index rowids case-insensitively.
                        rows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        using (var all = conn.CreateCommand())
                        {
                            all.CommandText = "SELECT rowid, FormKey FROM Records";
                            using var ar = all.ExecuteReader();
                            while (ar.Read()) rows[ar.GetString(1)] = ar.GetInt64(0);
                        }
                        _chain[i] = (plugin, conn, rows);
                    }
                    if (!rows.TryGetValue(fk, out long rowid)) continue;
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT FormKey, Signature, EditorID, Name, IsOverride, Data FROM Records WHERE rowid = @r";
                    cmd.Parameters.AddWithValue("@r", rowid);
                    using var r = cmd.ExecuteReader();
                    if (r.Read()) found = Read(plugin, r);
                }
                _cache[fk] = found;
                return found;
            }

            public string Edid(string? fk)
            {
                if (string.IsNullOrEmpty(fk)) return "";
                var r = Get(fk);
                if (r != null && r.EditorID.Length > 0 && !r.EditorID.Equals(r.FormKey, StringComparison.OrdinalIgnoreCase)) return r.EditorID;
                return Sfs.EditorIdOf(Sfs.NormalizeFormKey(fk)) ?? "";
            }

            /// <summary>"dn_DSMArmor (0016F6:DarkStar_Manufacturing)", or the short FormKey when the EditorID is unknown.</summary>
            public string Show(string? fk)
            {
                if (string.IsNullOrEmpty(fk)) return "none";
                if (fk.IndexOf(':') < 0) return fk;
                string e = Edid(fk);
                return e.Length > 0 ? $"{e} ({ShortFk(fk)})" : ShortFk(fk);
            }
        }

        // ================================================================ Small helpers
        static string? Str(JsonNode? n) => n is JsonValue v ? v.ToString() : null;

        static string ShortFk(string fk)
        {
            fk = Sfs.NormalizeFormKey(fk);
            return Regex.Replace(fk, @"\.(esm|esp|esl)$", "", RegexOptions.IgnoreCase);
        }

        static bool LooksLikeFk(string? s) => s != null && Regex.IsMatch(s, @"^[0-9A-Fa-f]{6}:.+\.(esm|esp|esl)$", RegexOptions.IgnoreCase);

        static double? Num(JsonNode? n) =>
            Str(n) is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

        static string Fmt(double d) => Math.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture);
        static string Delta(double d) => (d > 0 ? "+" : "") + Fmt(d);

        static string Clip(string s, int max = 70)
        {
            s = s.Replace("\r", "").Replace("\n", "⏎");
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        static void Inc<TKey>(Dictionary<TKey, int> d, TKey k, int by = 1) where TKey : notnull => d[k] = d.TryGetValue(k, out int v) ? v + by : by;

        /// <summary>"a ×5; b ×2 (+3 more)" from a count dictionary.</summary>
        static string Dist(Dictionary<string, int> d, int top = 4)
        {
            var ordered = d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            string s = string.Join("; ", ordered.Take(top).Select(kv => $"{(kv.Key.Length == 0 ? "(empty)" : kv.Key)} ×{kv.Value}"));
            if (ordered.Count > top) s += $" (+{ordered.Count - top} more)";
            return s;
        }

        static List<string> FkList(JsonNode? n) =>
            n is JsonArray a ? a.Select(Str).Where(LooksLikeFk).Select(x => Sfs.NormalizeFormKey(x!)).ToList() : new List<string>();

        static bool IsFkArray(JsonNode? n) => n is JsonArray a && a.Count > 0 && a.All(x => LooksLikeFk(Str(x)));

        static List<string> ReadExtra(List<string> args)
        {
            string? w = Sfs.TakeOption(args, "--with");
            return w == null ? new List<string>() : Program.TargetResolver(w);
        }

        // ================================================================ MkDiff
        static readonly HashSet<string> NoiseFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "Record Header", "EDID - Editor ID", "Signature", "FormVersion", "Version2", "VersionControl", "IsCompressed", "IsDeleted",
            "StarfieldMajorRecordFlags", "MajorRecordFlagsRaw", "Unused"
        };

        static readonly string[] DefaultNumericFields = { "ArmorRating", "Weight", "Value", "Health", "Resistances", "AttackDamage", "DamageTypes", "AmmoCapacity", "BaseValue", "BaseWeight" };

        class Pair
        {
            public Rec Created = null!;
            public Rec Base = null!;
            public Rec Recipe = null!;
            public string Category = "";
            public JsonObject C = new();
            public JsonObject B = new();
        }

        class SetCount { public int Add, Del, Keep; }

        public static void MkDiff(List<string> args)
        {
            string? outFile = Sfs.TakeOption(args, "--out");
            var extra = ReadExtra(args);
            string? sigOpt = Sfs.TakeOption(args, "--sig");
            string? byOpt = Sfs.TakeOption(args, "--by");
            string? listField = Sfs.TakeOption(args, "--list");
            string? numOpt = Sfs.TakeOption(args, "--num");
            bool showPairs = Sfs.TakeFlag(args, "--pairs");
            bool all = Sfs.TakeFlag(args, "--all");
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch MkDiff <plugin> [--sig Armor|Weapon] [--by prefix:N,kind,archetype,bench,none] [--with p1,p2]\n" +
                                  "                      [--num f1,f2] [--list Field] [--pairs] [--all] [--out report.md]");
                return;
            }

            using var store = RecordStore.Open(args[0], extra);
            using var report = Sfs.ReportOut.Begin(outFile);
            var numeric = DefaultNumericFields.Concat((numOpt ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sigs = sigOpt == null ? new[] { "Armor", "Weapon" } : new[] { Program.NormalizeRecordTypePublic(sigOpt) };

            Sfs.Say($"# MkDiff: `{store.Target}`\n");
            Sfs.Say($"Records resolved from: {string.Join(", ", store.Chain)}. A pair = a new item + the one item of the same type in its recipe's components.\n");

            var allCreated = store.TargetRecords(sigs).Where(r => !r.IsOverride).ToList();
            var cobjs = store.TargetRecords("ConstructibleObject");
            foreach (var sig in sigs)
            {
                var created = allCreated.Where(r => r.Signature == sig).ToList();
                if (created.Count == 0) continue;
                MkDiffSignature(store, sig, created, cobjs, byOpt, numeric, listField, showPairs, all);
            }
        }

        static void MkDiffSignature(RecordStore store, string sig, List<Rec> created, List<Rec> cobjs, string? byOpt,
                                    HashSet<string> numeric, string? listField, bool showPairs, bool all)
        {
            var byCreated = created.ToDictionary(c => c.FormKey, StringComparer.OrdinalIgnoreCase);
            var cands = new Dictionary<string, List<(Rec Recipe, string BaseFk, List<string> Others)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var cobj in cobjs)
            {
                string? co = Str(cobj.Data["CreatedObject"]);
                if (co == null || !byCreated.ContainsKey(Sfs.NormalizeFormKey(co))) continue;
                co = Sfs.NormalizeFormKey(co);
                var items = new List<string>();
                var others = new List<string>();
                if ((cobj.Data["ConstructibleComponents"] ?? cobj.Data["ConstructableComponents"]) is JsonArray comps)
                    foreach (var c in comps)
                    {
                        string? fk = Str(c?["Component"]);
                        if (fk == null) continue;
                        fk = Sfs.NormalizeFormKey(fk);
                        if (!fk.Equals(co, StringComparison.OrdinalIgnoreCase) && store.Get(fk)?.Signature == sig) items.Add(fk);
                        else others.Add($"{store.Edid(fk)}×{Str(c?["RequiredCount"]) ?? "1"}");
                    }
                if (items.Count != 1) continue;
                if (!cands.TryGetValue(co, out var l)) cands[co] = l = new();
                l.Add((cobj, items[0], others));
            }

            var byParts = (byOpt ?? (sig == "Weapon" ? "prefix:2,kind" : "prefix:3")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var catCanon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Category(Rec c, Rec recipe)
            {
                var parts = new List<string>();
                foreach (var p in byParts)
                {
                    if (p.StartsWith("prefix", StringComparison.OrdinalIgnoreCase))
                    {
                        int n = p.Contains(':') && int.TryParse(p.Split(':')[1], out int pn) ? pn : 3;
                        parts.Add(string.Join("_", c.EditorID.Split('_').Take(n)));
                    }
                    else if (p.Equals("kind", StringComparison.OrdinalIgnoreCase))
                        parts.Add(sig == "Weapon" ? (Str(c.Data["Archetype"]) == "Melee" ? "Melee" : "Ranged") : Str(c.Data["Archetype"]) ?? "?");
                    else if (p.Equals("archetype", StringComparison.OrdinalIgnoreCase)) parts.Add(Str(c.Data["Archetype"]) ?? "?");
                    else if (p.Equals("bench", StringComparison.OrdinalIgnoreCase)) parts.Add(store.Edid(Str(recipe.Data["WorkbenchKeyword"])));
                    else parts.Add("all");
                }
                string key = string.Join(" / ", parts);
                if (!catCanon.TryGetValue(key, out var canon)) catCanon[key] = canon = key;
                return canon;
            }

            // ---- pairing quality
            var baseUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in cands) foreach (var bfk in kv.Value.Select(x => x.BaseFk).Distinct(StringComparer.OrdinalIgnoreCase)) Inc(baseUse, bfk);
            var pairs = new List<Pair>();
            var missingBase = new List<string>();
            var multi = new List<string>();
            var baseOrigin = new Dictionary<string, int>();
            var otherComps = new Dictionary<string, int>();
            int oneToOne = 0, onlyComponent = 0;
            foreach (var c in created)
            {
                if (!cands.TryGetValue(c.FormKey, out var l)) continue;
                if (l.Count > 1) multi.Add($"{c.EditorID} ({l.Count} recipes, {l.Select(x => x.BaseFk).Distinct(StringComparer.OrdinalIgnoreCase).Count()} bases)");
                var (recipe, bfk, others) = l[0];
                if (l.Count == 1 && baseUse[bfk] == 1) oneToOne++;
                if (others.Count == 0) onlyComponent++;
                else Inc(otherComps, string.Join(" + ", others.OrderBy(x => x, StringComparer.Ordinal)));
                var b = store.Get(bfk);
                if (b == null) { missingBase.Add($"{c.EditorID} ← {bfk}"); continue; }
                Inc(baseOrigin, Sfs.PluginOf(bfk).Equals(store.TargetFile, StringComparison.OrdinalIgnoreCase) ? $"{Sfs.PluginOf(bfk)} (own item: upgrade of an upgrade)" : Sfs.PluginOf(bfk));
                pairs.Add(new Pair
                {
                    Created = c, Base = b, Recipe = recipe, Category = Category(c, recipe),
                    C = (Sfs.NormalizeLocalized(c.Data) as JsonObject) ?? new JsonObject(),
                    B = (Sfs.NormalizeLocalized(b.Data) as JsonObject) ?? new JsonObject()
                });
            }
            var unpaired = created.Where(c => !cands.ContainsKey(c.FormKey)).ToList();
            var sharedBases = baseUse.Where(kv => kv.Value > 1).ToList();

            Sfs.Say($"## {sig}: pairing\n");
            Sfs.Summary($"{sig}: {created.Count} new records, {pairs.Count} paired with a base, {oneToOne} clean 1:1 (one recipe each, base not shared), {unpaired.Count} unpaired.");
            var pt = new Sfs.Table("Check", "Count", "Details");
            pt.Add("New records in plugin", created.Count, "");
            pt.Add("Paired through an upgrade recipe", pairs.Count, string.Join("; ", baseOrigin.Select(kv => $"base from {kv.Key} ×{kv.Value}")));
            pt.Add("Clean 1:1 (one recipe, base not shared)", oneToOne, "");
            pt.Add("Recipe's only component is the base", onlyComponent, otherComps.Count > 0 ? "others also pay: " + Dist(otherComps, 3) : "");
            pt.Add("Created by more than one upgrade recipe", multi.Count, string.Join("; ", multi.Take(5)));
            pt.Add("Bases shared by several new items", sharedBases.Count, string.Join("; ", sharedBases.Take(5).Select(kv => $"{store.Edid(kv.Key)} ×{kv.Value}")));
            pt.Add("Unpaired (no upgrade recipe)", unpaired.Count, string.Join(", ", unpaired.Take(all ? 1000 : 8).Select(u => u.EditorID)) + (unpaired.Count > 8 && !all ? ", …" : ""));
            if (missingBase.Count > 0) pt.Add("Base not found in any cache", missingBase.Count, string.Join("; ", missingBase.Take(5)));
            Sfs.Print(pt, annotate: false);

            var cats = pairs.GroupBy(p => p.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            Sfs.Say($"\nCategories (`--by {string.Join(",", byParts)}`): {string.Join(", ", cats.Select(g => $"{g.Key} {g.Count()}"))}\n");

            if (showPairs)
            {
                Sfs.Say("### Pairs\n");
                var t = new Sfs.Table("Category", "Created FormKey", "Created EditorID", "Base FormKey", "Base EditorID", "Recipe EditorID");
                foreach (var p in pairs.OrderBy(p => p.Category).ThenBy(p => p.Created.EditorID))
                    t.Add(p.Category, ShortFk(p.Created.FormKey), p.Created.EditorID, ShortFk(p.Base.FormKey), p.Base.EditorID, p.Recipe.EditorID);
                Sfs.Print(t, annotate: false);
            }

            if (listField != null)
            {
                Sfs.Say($"\n### `{listField}` per pair (base → created)\n");
                var t = new Sfs.Table("Category", "Created", "Base", "Base value", "Created value");
                foreach (var p in pairs.OrderBy(p => p.Category).ThenBy(p => p.Created.EditorID))
                    t.Add(p.Category, p.Created.EditorID, p.Base.EditorID, Clip(Sfs.Compact(p.B[listField]), 160), Clip(Sfs.Compact(p.C[listField]), 160));
                Sfs.Print(t);
            }

            // ---- values every created item carries (over all categories)
            var carriedAll = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // key "List\u0001Value"
            foreach (var p in pairs)
                foreach (var (list, values) in ListValues(p.C, sig))
                    foreach (var v in values.Distinct(StringComparer.OrdinalIgnoreCase)) Inc(carriedAll, list + "\u0001" + v);
            var everywhere = carriedAll.Where(kv => kv.Value == pairs.Count && pairs.Count > 1)
                                       .Select(kv => kv.Key.Split('\u0001')).Select(a => (List: a[0], Value: a[1])).ToList();
            if (everywhere.Count > 0)
            {
                Sfs.Say($"### Carried by every one of the {pairs.Count} paired {sig} records\n");
                foreach (var g in everywhere.GroupBy(e => e.List))
                {
                    var added = g.Select(e => e.Value).Select(v => (v, adds: pairs.Count(p => !ListValues(p.B, sig).Where(x => x.List == g.Key).SelectMany(x => x.Values).Contains(v, StringComparer.OrdinalIgnoreCase)))).ToList();
                    Sfs.Summary($"- {g.Key}: " + string.Join(", ", added.Select(a => $"{store.Show(a.v)} (added on {a.adds}, already on the base for {pairs.Count - a.adds})")));
                }
                Sfs.Say("");
            }

            foreach (var g in cats) MkDiffCategory(store, sig, g.Key, g.ToList(), numeric, all);
        }

        /// <summary>The list-type fields compared as sets (FormKey arrays + template include/keyword lists + Components kinds).</summary>
        static List<(string List, List<string> Values)> ListValues(JsonObject d, string sig)
        {
            var res = new List<(string, List<string>)>();
            foreach (var kv in d)
            {
                if (NoiseFields.Contains(kv.Key) || kv.Key == "ObjectTemplates") continue;
                if (IsFkArray(kv.Value)) res.Add((kv.Key, FkList(kv.Value)));
            }
            if (d["ObjectTemplates"] is JsonArray ots)
            {
                var t0i = new List<string>(); var t0k = new List<string>(); var tri = new List<string>(); var trk = new List<string>();
                for (int i = 0; i < ots.Count; i++)
                {
                    var inc = ots[i]?["Includes"] is JsonArray ia ? ia.Select(x => Str(x?["Mod"])).Where(LooksLikeFk).Select(x => Sfs.NormalizeFormKey(x!)) : Enumerable.Empty<string>();
                    var kw = FkList(ots[i]?["Keywords"]);
                    if (i == 0) { t0i.AddRange(inc); t0k.AddRange(kw); } else { tri.AddRange(inc); trk.AddRange(kw); }
                }
                res.Add(("Template[0].Includes", t0i));
                res.Add(("Template[0].Keywords", t0k));
                res.Add(("Template[1+].Includes", tri));
                res.Add(("Template[1+].Keywords", trk));
            }
            if (d["Components"] is JsonArray comps && comps.All(c => c is JsonObject))
                res.Add(("Components (kinds)", comps.Select(c => string.Join("+", ((JsonObject)c!).Select(x => x.Key))).ToList()));
            return res;
        }

        static void MkDiffCategory(RecordStore store, string sig, string cat, List<Pair> pairs, HashSet<string> numeric, bool all)
        {
            int n = pairs.Count;
            Sfs.Say($"\n## {cat} — {n} pairs\n");

            // ---- Name / EditorID patterns
            var nameSfx = new Dictionary<string, int>();
            var nameOther = new List<string>();
            var edidPfx = new Dictionary<string, int>();
            foreach (var p in pairs)
            {
                string bn = Str(p.B["Name"]) ?? p.Base.Name, cn = Str(p.C["Name"]) ?? p.Created.Name;
                if (bn.Length > 0 && cn.StartsWith(bn, StringComparison.Ordinal)) Inc(nameSfx, $"\"<base>{cn.Substring(bn.Length)}\"");
                else { Inc(nameSfx, "other"); nameOther.Add($"\"{Clip(bn, 60)}\" → \"{Clip(cn, 60)}\""); }
                Inc(edidPfx, p.Created.EditorID.EndsWith(p.Base.EditorID, StringComparison.OrdinalIgnoreCase)
                    ? $"\"{p.Created.EditorID.Substring(0, p.Created.EditorID.Length - p.Base.EditorID.Length)}<base>\"" : "other");
            }
            Sfs.Say($"- **Name:** {Dist(nameSfx, 4)}" + (nameOther.Count > 0 ? $". Other e.g. {string.Join("; ", nameOther.Take(all ? 1000 : 3))}" : ""));
            Sfs.Say($"- **EditorID:** {Dist(edidPfx, 3)}");

            // ---- which fields differ
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pairs) foreach (var kv in p.C.Concat(p.B)) if (!NoiseFields.Contains(kv.Key) && seen.Add(kv.Key)) order.Add(kv.Key);
            var changed = new Dictionary<string, int>();
            var newVals = new Dictionary<string, Dictionary<string, int>>();
            foreach (var p in pairs)
                foreach (var f in order)
                {
                    var bv = p.B[f]; var cv = p.C[f];
                    if (JsonNode.DeepEquals(bv, cv)) continue;
                    Inc(changed, f);
                    if (!newVals.TryGetValue(f, out var nv)) newVals[f] = nv = new();
                    Inc(nv, cv == null ? "(removed)" : Clip(cv is JsonValue ? Sfs.Compact(cv) : Sfs.Compact(cv), 50));
                }
            var lists = new HashSet<string>(order.Where(f => pairs.Any(p => IsFkArray(p.C[f]) || IsFkArray(p.B[f]))), StringComparer.OrdinalIgnoreCase) { "ObjectTemplates", "Components" };
            var ft = new Sfs.Table("Field", "Changed", "Created value (when changed)");
            foreach (var f in order.Where(changed.ContainsKey))
            {
                string note = lists.Contains(f) ? "see list table" : numeric.Contains(f) ? "see numbers" :
                              newVals[f].Count <= 3 || newVals[f].Values.Max() * 2 >= changed[f]
                                  ? Dist(newVals[f].ToDictionary(kv => LooksLikeFk(kv.Key) ? store.Show(kv.Key) : kv.Key, kv => kv.Value), 2)
                                  : $"{newVals[f].Count} different values";
                ft.Add(f, $"{changed[f]}/{n}", note);
            }
            Sfs.Say($"\n### Fields changed (created vs base)\n");
            Sfs.Print(ft, annotate: false);
            var same = order.Where(f => !changed.ContainsKey(f)).ToList();
            if (same.Count > 0) Sfs.Say($"\nSame on all {n}: {string.Join(", ", same)}");

            // ---- list fields as sets
            var sets = new Dictionary<string, Dictionary<string, SetCount>>(StringComparer.OrdinalIgnoreCase);
            var tplCounts = new Dictionary<string, int>();
            var tplNames = new Dictionary<string, int>();
            foreach (var p in pairs)
            {
                var bl = ListValues(p.B, sig).ToDictionary(x => x.List, x => x.Values);
                var cl = ListValues(p.C, sig).ToDictionary(x => x.List, x => x.Values);
                foreach (var list in bl.Keys.Union(cl.Keys))
                {
                    var bs = (bl.TryGetValue(list, out var b) ? b : new()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var cs = (cl.TryGetValue(list, out var c) ? c : new()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!sets.TryGetValue(list, out var sc)) sets[list] = sc = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var v in bs.Union(cs, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!sc.TryGetValue(v, out var cnt)) sc[v] = cnt = new SetCount();
                        bool inB = bs.Contains(v), inC = cs.Contains(v);
                        if (inB && inC) cnt.Keep++; else if (inC) cnt.Add++; else cnt.Del++;
                    }
                }
                int nb = (p.B["ObjectTemplates"] as JsonArray)?.Count ?? 0, nc = (p.C["ObjectTemplates"] as JsonArray)?.Count ?? 0;
                Inc(tplCounts, $"{nb}→{nc}");
                if (p.C["ObjectTemplates"] is JsonArray cot)
                    Inc(tplNames, "[" + string.Join(", ", cot.Select(t => Str(t?["Name"]) ?? "-")) + "]");
            }
            int thr = all ? 1 : Math.Max(2, (int)Math.Ceiling(n * 0.05));
            var lt = new Sfs.Table("List", "FormKey", "EditorID", "ADD", "DEL", "KEEP", "Has");
            var notes = new List<string>();
            foreach (var (list, sc) in sets.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var shown = sc.Where(kv => kv.Value.Add >= thr || kv.Value.Del >= thr).OrderByDescending(kv => kv.Value.Add).ThenByDescending(kv => kv.Value.Del).ThenByDescending(kv => kv.Value.Keep).ToList();
                var overflow = all ? new List<KeyValuePair<string, SetCount>>() : shown.Skip(15).ToList();
                if (!all) shown = shown.Take(15).ToList();
                foreach (var (v, cnt) in shown)
                    lt.Add(list, LooksLikeFk(v) ? ShortFk(v) : v, LooksLikeFk(v) ? store.Edid(v) : "", cnt.Add, cnt.Del, cnt.Keep, $"{cnt.Add + cnt.Keep}/{n}");
                var rare = sc.Where(kv => (kv.Value.Add > 0 || kv.Value.Del > 0) && kv.Value.Add < thr && kv.Value.Del < thr).Concat(overflow).ToList();
                if (rare.Count > 0)
                {
                    int ra = rare.Sum(kv => kv.Value.Add), rd = rare.Sum(kv => kv.Value.Del);
                    notes.Add($"- {list}: {rare.Count} more values changed on fewer than {thr} pairs each ({ra} adds, {rd} removals; per-item values), e.g. " +
                              string.Join(", ", rare.Take(3).Select(kv => $"{(kv.Value.Add > 0 ? "+" : "−")}{(LooksLikeFk(kv.Key) ? store.Edid(kv.Key) : kv.Key)}")));
                }
                var kept = sc.Where(kv => kv.Value.Add == 0 && kv.Value.Del == 0 && kv.Value.Keep >= thr).ToList();
                if (kept.Count > 0)
                    notes.Add($"- {list}: kept unchanged: " + string.Join(", ", kept.OrderByDescending(kv => kv.Value.Keep).Take(all ? 1000 : 12)
                              .Select(kv => $"{(LooksLikeFk(kv.Key) ? store.Edid(kv.Key) : kv.Key)} {kv.Value.Keep}")) + (kept.Count > 12 && !all ? $" (+{kept.Count - 12} more)" : ""));
            }
            if (lt.Rows.Count > 0 || notes.Count > 0)
            {
                Sfs.Say($"\n### Lists: ADD / DEL / KEEP (values changed on ≥ {thr} pairs; `--all` lists every value)\n");
                if (lt.Rows.Count > 0) Sfs.Print(lt, annotate: false);
                foreach (var l in notes) Sfs.Say(l);
            }
            if (tplCounts.Count > 0) Sfs.Say($"- ObjectTemplates count base→created: {Dist(tplCounts, 5)}. Created template names: {Dist(tplNames, 3)}");

            // ---- numbers
            var deltas = new Dictionary<string, List<(Pair P, double B, double C)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pairs)
                foreach (var f in numeric)
                {
                    var bv = p.B[f]; var cv = p.C[f];
                    if (bv == null && cv == null) continue;
                    if (bv is JsonArray || cv is JsonArray)
                    {
                        var bm = DamageMap(bv); var cm = DamageMap(cv);
                        foreach (var dt in bm.Keys.Union(cm.Keys, StringComparer.OrdinalIgnoreCase))
                        {
                            string key = $"{f}[{store.Edid(dt)}]";
                            if (!deltas.TryGetValue(key, out var dl)) deltas[key] = dl = new();
                            dl.Add((p, bm.TryGetValue(dt, out var x) ? x : 0, cm.TryGetValue(dt, out var y) ? y : 0));
                        }
                    }
                    else
                    {
                        double? b = Num(bv), c = Num(cv);
                        if (b == null && c == null) continue;
                        if (!deltas.TryGetValue(f, out var dl)) deltas[f] = dl = new();
                        dl.Add((p, b ?? 0, c ?? 0));
                    }
                }
            if (deltas.Count > 0)
            {
                Sfs.Say("\n### Numbers (created − base)\n");
                var nt = new Sfs.Table("Field", "Changed", "Δ (count)", "Δ range", "Ratio range");
                var outl = new List<string>();
                var unchangedNums = new List<string>();
                foreach (var (f, dl) in deltas.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var ch = dl.Where(x => Math.Abs(x.C - x.B) > 1e-6).ToList();
                    if (ch.Count == 0) { unchangedNums.Add($"{f} ({dl.Count})"); continue; }
                    var dd = new Dictionary<string, int>();
                    foreach (var x in ch) Inc(dd, Delta(x.C - x.B));
                    var ratios = ch.Where(x => Math.Abs(x.B) > 1e-9).Select(x => x.C / x.B).ToList();
                    nt.Add(f, $"{ch.Count}/{dl.Count}", ch.Count == 0 ? "" : Dist(dd, 4),
                           ch.Count == 0 ? "" : $"{Delta(ch.Min(x => x.C - x.B))} … {Delta(ch.Max(x => x.C - x.B))}",
                           ratios.Count == 0 ? "" : $"×{Fmt(ratios.Min())} … ×{Fmt(ratios.Max())}");
                    if (ch.Count == 0) continue;
                    var top = dd.OrderByDescending(kv => kv.Value).First();
                    var odd = ch.Where(x => Delta(x.C - x.B) != top.Key).ToList();
                    if (top.Value * 2 > ch.Count && odd.Count > 0)
                        outl.Add($"- **{f}**: usually {top.Key} ({top.Value}/{ch.Count} changed). Different: " +
                                 string.Join("; ", odd.Take(all ? 1000 : 8).Select(x => $"{x.P.Created.EditorID} {Fmt(x.B)}→{Fmt(x.C)} ({Delta(x.C - x.B)})")) + (odd.Count > 8 && !all ? $" (+{odd.Count - 8} more)" : ""));
                    else if (top.Value * 2 <= ch.Count && ch.Count > 3)
                        outl.Add($"- **{f}**: no common change ({dd.Count} different deltas): set per item. Use `--list {f.Split('[')[0]}` for each value.");
                    if (ch.Count < dl.Count && ch.Count > 0 && ch.Count * 5 <= dl.Count)
                        outl.Add($"- **{f}**: changed on only {ch.Count} of {dl.Count}: " + string.Join("; ", ch.Take(all ? 1000 : 6).Select(x => $"{x.P.Created.EditorID} {Fmt(x.B)}→{Fmt(x.C)}")));
                }
                if (nt.Rows.Count > 0) Sfs.Print(nt, annotate: false);
                if (unchangedNums.Count > 0) Sfs.Say($"{(nt.Rows.Count > 0 ? "\n" : "")}Unchanged on every pair: {string.Join(", ", unchangedNums)}");
                foreach (var l in outl.Distinct()) Sfs.Say(l);
            }
        }

        static Dictionary<string, double> DamageMap(JsonNode? n)
        {
            var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (n is not JsonArray a) return d;
            foreach (var e in a)
            {
                string? dt = Str(e?["DamageType"]);
                if (dt == null) continue;
                d[Sfs.NormalizeFormKey(dt)] = Num(e?["Value"]) ?? 0;
            }
            return d;
        }

        // ================================================================ OmodCensus
        static readonly string[] OmodSigs = { "ArmorModification", "WeaponModification", "ObjectModification" };

        static string TierSortKey(string t) =>
            t.StartsWith("Q") && int.TryParse(t.Substring(1), out int q) ? q.ToString("D3") : t == "TiersAny" ? "900" : "999" + t;

        static string PropText(RecordStore store, JsonNode? p, bool withValue = true)
        {
            string prop = Str(p?["Property"]) ?? "?";
            string? rec = Str(p?["Record"]);
            string s = prop + (rec != null ? $"[{store.Edid(rec)}]" : "") + " " + (Str(p?["FunctionType"]) ?? "?");
            if (withValue && rec == null) s += " " + (Str(p?["Value"]) ?? "?");
            else if (withValue && Str(p?["Value"]) is string v && v != "0") s += " " + v;
            if (withValue && Str(p?["Value2"]) is string v2 && v2 != "0") s += "/" + v2;
            if (withValue && Str(p?["Step"]) is string st && st != "0") s += " step " + st;
            return s;
        }

        public static void OmodCensus(List<string> args)
        {
            string? outFile = Sfs.TakeOption(args, "--out");
            var extra = ReadExtra(args);
            bool all = Sfs.TakeFlag(args, "--all");
            int top = int.TryParse(Sfs.TakeOption(args, "--top"), out int tp) ? tp : 6;
            if (args.Count < 2)
            {
                Console.WriteLine("Usage: SFSearch OmodCensus <plugin> <EditorID glob, e.g. mod_DSM_Mk2_Helmet_*> [--with p1,p2] [--top N] [--all] [--out report.md]");
                return;
            }
            using var store = RecordStore.Open(args[0], extra);
            using var report = Sfs.ReportOut.Begin(outFile);
            var rx = Sfs.GlobRegex(args[1]);
            var omods = store.TargetRecords(OmodSigs).Where(r => rx.IsMatch(r.EditorID)).ToList();
            Sfs.Say($"# OmodCensus: `{store.Target}` `{args[1]}`\n");
            if (omods.Count == 0) { Sfs.Summary("No OMODs match that EditorID glob."); return; }

            string TierKey(Rec r) => Sfs.TierOf(r.EditorID) ?? "(no tier)";
            var tiers = omods.GroupBy(TierKey).OrderBy(g => TierSortKey(g.Key)).ToList();
            // A "set" = the tier OMODs that share their TargetOmodKeywords (EditorIDs aren't reliable: DSM names its TiersAny
            // "..._UCPilot_Quality_TiersAny" but the tiers "..._UCPilot_01_Quality_N"). OMODs without target keywords fall back to the EditorID stem.
            string SetKey(Rec r) => FkList(r.Data["TargetOmodKeywords"]) is { Count: > 0 } k
                ? string.Join("+", k.Select(store.Edid).OrderBy(x => x, StringComparer.Ordinal)) : "~" + Sfs.SetStemOf(r.EditorID);
            var setOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in omods.Where(o => o.Data["Includes"] is not JsonArray { Count: > 0 })) setOf[o.FormKey] = SetKey(o);
            var stems = omods.Where(o => setOf.ContainsKey(o.FormKey)).GroupBy(o => setOf[o.FormKey], StringComparer.OrdinalIgnoreCase).ToList();
            Sfs.Summary($"{omods.Count} OMODs ({string.Join(", ", omods.GroupBy(r => r.Signature).Select(g => $"{g.Count()} {g.Key}"))}; {omods.Count(o => o.IsOverride)} overrides). " +
                        $"By tier: {string.Join(", ", tiers.Select(g => $"{g.Key} {g.Count()}"))}");

            // ---- set completeness
            int maxQ = omods.Select(o => Sfs.TierOf(o.EditorID)).Where(t => t != null && t.StartsWith("Q")).Select(t => int.Parse(t!.Substring(1))).DefaultIfEmpty(0).Max();
            var shapeOfSet = new Dictionary<string, int>();
            var incomplete = new List<string>();
            Sfs.Say($"\n- **Sets** (tier OMODs grouped by their target keyword): {stems.Count}");
            foreach (var s in stems)
            {
                var ts = s.Select(TierKey).ToList();
                string shape = string.Join(" ", ts.Distinct().OrderBy(TierSortKey));
                Inc(shapeOfSet, shape);
                var missing = Enumerable.Range(1, maxQ).Select(q => "Q" + q).Where(q => !ts.Contains(q)).ToList();
                var dup = ts.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (missing.Count > 0 || dup.Count > 0)
                    incomplete.Add($"{s.Key}: {(missing.Count > 0 ? "missing " + string.Join(",", missing) : "")}{(dup.Count > 0 ? " repeated " + string.Join(",", dup) : "")}");
            }
            Sfs.Say($"\n- **Tiers per set:** {Dist(shapeOfSet, 4)}");
            if (incomplete.Count > 0) Sfs.Summary($"- Sets with missing/repeated tiers ({incomplete.Count}): " + string.Join("; ", incomplete.Take(all ? 1000 : 10)) + (incomplete.Count > 10 && !all ? "; …" : ""));

            // ---- header fields per tier
            Sfs.Say("\n## Per tier: header fields\n");
            var ht = new Sfs.Table("Tier", "OMODs", "AttachPoint", "TargetOmodKeywords", "MajorFlags", "MaxRank / LTSO", "Name", "Property shapes", "Exact signatures");
            var exactByTier = new Dictionary<string, Dictionary<string, List<Rec>>>();
            var shapeByTier = new Dictionary<string, Dictionary<string, List<Rec>>>();
            foreach (var g in tiers)
            {
                var ap = new Dictionary<string, int>(); var tok = new Dictionary<string, int>(); var mf = new Dictionary<string, int>();
                var mr = new Dictionary<string, int>(); var nm = new Dictionary<string, int>();
                var exact = new Dictionary<string, List<Rec>>(); var shape = new Dictionary<string, List<Rec>>();
                foreach (var o in g)
                {
                    Inc(ap, store.Show(Str(o.Data["AttachPoint"])));
                    int nk = (o.Data["TargetOmodKeywords"] as JsonArray)?.Count ?? 0;
                    Inc(tok, nk == 1 ? "1 keyword" : $"{nk} keywords");
                    Inc(mf, Str(o.Data["MajorFlags"]) ?? "0");
                    Inc(mr, $"{Str(o.Data["MaxRank"]) ?? "-"} / {Str(o.Data["LevelTierScaledOffset"]) ?? "-"}");
                    Inc(nm, Sfs.English(o.Data["Name"]) ?? o.Name);
                    var props = (o.Data["Properties"] as JsonArray)?.ToList() ?? new List<JsonNode?>();
                    string ex = props.Count == 0 ? "(no properties)" : string.Join(" · ", props.Select(p => PropText(store, p)).OrderBy(x => x, StringComparer.Ordinal));
                    string sh = props.Count == 0 ? "(no properties)" : string.Join(" · ", props.Select(p => PropText(store, p, false)).OrderBy(x => x, StringComparer.Ordinal));
                    if (!exact.TryGetValue(ex, out var el)) exact[ex] = el = new(); el.Add(o);
                    if (!shape.TryGetValue(sh, out var sl)) shape[sh] = sl = new(); sl.Add(o);
                }
                exactByTier[g.Key] = exact; shapeByTier[g.Key] = shape;
                ht.Add(g.Key, g.Count(), Dist(ap, 2), Dist(tok, 2), Dist(mf, 2), Dist(mr, 2), Dist(nm, 2), shape.Count, exact.Count);
            }
            Sfs.Print(ht, annotate: false);

            // ---- properties per tier
            Sfs.Say("\n## Per tier: properties\n");
            foreach (var g in tiers)
            {
                var exact = exactByTier[g.Key];
                if (exact.Count <= top)
                {
                    Sfs.Say($"#### {g.Key} — {g.Count()} OMODs, {exact.Count} exact property signature(s)\n");
                    var t = new Sfs.Table("OMODs", "Properties (sorted)", "Example");
                    foreach (var (sig, recs) in exact.OrderByDescending(kv => kv.Value.Count))
                        t.Add(recs.Count, sig, recs[0].EditorID);
                    Sfs.Print(t, annotate: false);
                }
                else
                {
                    var shape = shapeByTier[g.Key];
                    Sfs.Say($"#### {g.Key} — {g.Count()} OMODs, {exact.Count} exact signatures (values differ per item), {shape.Count} shape(s)\n");
                    var t = new Sfs.Table("OMODs", "Shape (property + function)", "Values per property (min … max, distinct)", "Example");
                    foreach (var (sh, recs) in shape.OrderByDescending(kv => kv.Value.Count).Take(all ? 1000 : top))
                    {
                        var ranges = new Dictionary<string, List<double>>();
                        foreach (var o in recs)
                            foreach (var p in (o.Data["Properties"] as JsonArray) ?? new JsonArray())
                            {
                                if (Str(p?["Record"]) != null && Str(p?["Property"]) == "Keyword") continue;
                                string k = PropText(store, p, false);
                                if (Num(p?["Value"]) is double v) { if (!ranges.TryGetValue(k, out var l)) ranges[k] = l = new(); l.Add(v); }
                            }
                        t.Add(recs.Count, sh, string.Join(" · ", ranges.Select(kv =>
                            $"{kv.Key.Split(' ')[0]} {Fmt(kv.Value.Min())} … {Fmt(kv.Value.Max())} ({kv.Value.Distinct().Count()})")), recs[0].EditorID);
                    }
                    Sfs.Print(t, annotate: false);
                    if (shape.Count > top && !all) Sfs.Say($"(+{shape.Count - top} rarer shapes; `--all` lists them)");
                }
                Sfs.Say("");
            }

            // ---- target keywords overall
            var tokAll = new Dictionary<string, int>();
            foreach (var o in omods) foreach (var k in FkList(o.Data["TargetOmodKeywords"])) Inc(tokAll, k);
            if (tokAll.Count > 0)
                Sfs.Say($"- **TargetOmodKeywords:** {tokAll.Count} distinct keywords. " + (tokAll.Count <= 10
                    ? string.Join("; ", tokAll.OrderByDescending(kv => kv.Value).Select(kv => $"{store.Show(kv.Key)} ×{kv.Value}"))
                    : $"Most used: {string.Join("; ", tokAll.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{store.Show(kv.Key)} ×{kv.Value}"))}"));

            // ---- TiersAny ladders
            var ladders = omods.Where(o => o.Data["Includes"] is JsonArray a && a.Count > 0).ToList();
            if (ladders.Count == 0) return;
            Sfs.Say($"\n## Include ladders (TiersAny): {ladders.Count} OMODs\n");
            var lad = new Dictionary<string, List<Rec>>(); var steps = new Dictionary<string, List<Rec>>(); var seq = new Dictionary<string, int>();
            var flags = new Dictionary<string, int>(); var flagged = new List<string>();
            var laddersPerSet = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // Highest tier any ladder includes (TGO ladders stop at Q8 although Mythic Q9 OMODs exist: Q9 isn't "missing").
            int ladderMaxQ = ladders.SelectMany(o => ((JsonArray)o.Data["Includes"]!).Select(i => Sfs.TierOf(store.Edid(Str(i?["Mod"])))))
                                    .Where(t => t != null && t.StartsWith("Q")).Select(t => int.Parse(t!.Substring(1))).DefaultIfEmpty(maxQ).Max();
            foreach (var o in ladders)
            {
                var inc = ((JsonArray)o.Data["Includes"]!).ToList();
                var lv = inc.Select(i => Num(i?["MinimumLevel"]) ?? 0).ToList();
                string ls = string.Join(", ", lv.Select(Fmt));
                string ss = $"Q1 {Fmt(lv[0])}, steps " + string.Join(" ", lv.Zip(lv.Skip(1), (a, b) => Delta(b - a)));
                if (!lad.TryGetValue(ls, out var l1)) lad[ls] = l1 = new(); l1.Add(o);
                if (!steps.TryGetValue(ss, out var l2)) steps[ss] = l2 = new(); l2.Add(o);
                foreach (var i in inc) Inc(flags, $"Optional {Str(i?["Optional"])}, DoNotUseAll {Str(i?["DoNotUseAll"])}");
                var tiersIn = inc.Select(i => Str(i?["Mod"])).Select(fk => (fk, e: store.Edid(fk))).ToList();
                var ts = tiersIn.Select(x => x.e.Length == 0 ? "?" : Sfs.TierOf(x.e) ?? "-").ToList();
                Inc(seq, string.Join(" ", ts));
                var problems = new List<string>();
                var rep = ts.Where(t => t.StartsWith("Q")).GroupBy(t => t).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
                if (rep.Count > 0) problems.Add("repeated " + string.Join(",", rep));
                var miss = Enumerable.Range(1, ladderMaxQ).Select(q => "Q" + q).Where(q => !ts.Contains(q)).ToList();
                if (miss.Count > 0) problems.Add("missing " + string.Join(",", miss));
                var qn = ts.Where(t => t.StartsWith("Q")).Select(t => int.Parse(t.Substring(1))).ToList();
                if (qn.Zip(qn.Skip(1), (a, b) => b < a).Any(x => x)) problems.Add("out of order");
                if (lv.Zip(lv.Skip(1), (a, b) => b < a).Any(x => x)) problems.Add("levels go down");
                var incSets = tiersIn.Select(x => store.Get(x.fk)).Where(r => r != null).GroupBy(r => SetKey(r!)).ToDictionary(g => g.Key, g => g.Count());
                if (incSets.Count > 1) problems.Add("mixes sets: " + Dist(incSets, 3));
                foreach (var k in incSets.Keys) Inc(laddersPerSet, k);
                if (ts.Contains("?")) problems.Add("include not found in caches");
                if (problems.Count > 0) flagged.Add($"{o.EditorID} ({ShortFk(o.FormKey)}): {string.Join("; ", problems)} — tiers {string.Join(" ", ts)}, levels {ls}");
            }
            var lt = new Sfs.Table("OMODs", "MinimumLevel ladder", "Example");
            foreach (var (k, v) in lad.OrderByDescending(kv => kv.Value.Count).Take(all ? 1000 : 10)) lt.Add(v.Count, k, v[0].EditorID);
            Sfs.Print(lt, annotate: false);
            if (lad.Count > 10 && !all) Sfs.Say($"(+{lad.Count - 10} rarer ladders)");
            Sfs.Say("");
            var st = new Sfs.Table("OMODs", "Ladder shape (first level + steps)", "Example");
            foreach (var (k, v) in steps.OrderByDescending(kv => kv.Value.Count).Take(all ? 1000 : 10)) st.Add(v.Count, k, v[0].EditorID);
            Sfs.Print(st, annotate: false);
            Sfs.Say($"\n- **Tier order of the includes:** {Dist(seq, 3)}");
            Sfs.Say($"- **Include flags:** {Dist(flags, 3)}");
            Sfs.Say($"- **Ladder header:** MajorFlags {Dist(ladders.GroupBy(o => Str(o.Data["MajorFlags"]) ?? "0").ToDictionary(g => g.Key, g => g.Count()), 3)}; " +
                    $"properties {Dist(ladders.GroupBy(o => string.Join(" · ", ((o.Data["Properties"] as JsonArray) ?? new JsonArray()).Select(p => PropText(store, p)))).ToDictionary(g => g.Key.Length == 0 ? "(none)" : g.Key, g => g.Count()), 2)}");
            var noLadder = stems.Select(s => s.Key).Where(k => !laddersPerSet.ContainsKey(k)).ToList();
            var manyLadders = laddersPerSet.Where(kv => kv.Value > 1).ToList();
            Sfs.Say($"- **Ladders per set:** {stems.Count - noLadder.Count} of {stems.Count} sets have a ladder" +
                    (noLadder.Count > 0 ? $"; no ladder: {string.Join(", ", noLadder.Take(all ? 1000 : 8))}{(noLadder.Count > 8 && !all ? ", …" : "")}" : "") +
                    (manyLadders.Count > 0 ? $"; used by several ladders: {string.Join(", ", manyLadders.Take(8).Select(kv => $"{kv.Key} ×{kv.Value}"))}" : ""));
            Sfs.Summary($"Ladders: {ladders.Count} include ladders, {lad.Count} distinct level lists, {steps.Count} shapes; {flagged.Count} with problems; {noLadder.Count} sets without a ladder, {manyLadders.Count} sets shared by several ladders.");
            if (flagged.Count > 0)
            {
                Sfs.Say($"\n### Ladders with problems ({flagged.Count})\n");
                foreach (var f in flagged.Take(all ? 1000 : 20)) Sfs.Say("- " + f);
                if (flagged.Count > 20 && !all) Sfs.Say($"- … (+{flagged.Count - 20} more; `--all`)");
            }
        }

        // ================================================================ RecipeCensus
        static readonly Dictionary<string, string> OpSymbols = new(StringComparer.OrdinalIgnoreCase)
        {
            { "EqualTo", "==" }, { "NotEqualTo", "!=" }, { "GreaterThan", ">" }, { "GreaterThanOrEqualTo", ">=" }, { "LessThan", "<" }, { "LessThanOrEqualTo", "<=" }
        };

        // Placed references aren't in the Records table, so a few well-known ones are named here.
        static readonly Dictionary<string, string> KnownRefs = new(StringComparer.OrdinalIgnoreCase)
        {
            { "000014:Starfield.esm", "PlayerRef" }, { "000007:Starfield.esm", "Player" }
        };

        static string NameOrFk(RecordStore store, string fk)
        {
            fk = Sfs.NormalizeFormKey(fk);
            if (KnownRefs.TryGetValue(fk, out var known)) return known;
            string e = store.Edid(fk);
            return e.Length > 0 ? e : ShortFk(fk);
        }

        internal static string CondText(RecordStore store, JsonNode? c, out string? level)
        {
            level = null;
            var d = c?["Data"];
            string fn = Str(d?["Function"]) ?? "?";
            var parms = new List<string>();
            foreach (var pn in new[] { "FirstParameter", "SecondParameter" })
            {
                string? v = Str(d?[pn]);
                if (v == null || v == "0" || v == "-1") continue;
                parms.Add(LooksLikeFk(v) ? NameOrFk(store, v) : v);
            }
            string run = Str(d?["RunOnType"]) ?? "";
            string? refFk = Str(d?["Reference"]);
            if (refFk != null) run += " " + NameOrFk(store, refFk);
            string op = OpSymbols.TryGetValue(Str(c?["CompareOperator"]) ?? "", out var sym) ? sym : Str(c?["CompareOperator"]) ?? "?";
            string val = Str(c?["ComparisonValue"]) ?? Str(c?["GlobalValue"]) ?? "?";
            if (fn == "GetLevel") { level = val; val = "L"; }
            string flags = Str(c?["Flags"]) is string f && f != "0" ? $" [{f}]" : "";
            return $"{fn}({string.Join(", ", parms)}) {op} {val} on {run}{flags}";
        }

        class RecipeInfo
        {
            public Rec R = null!;
            public string Class = "";
            public string Tier = "all";
            public string? CreatedTier;
            public string? GetLevel;
            public List<string> Conds = new();
            public Dictionary<string, string> A = new();
        }

        public static void RecipeCensus(List<string> args)
        {
            string? outFile = Sfs.TakeOption(args, "--out");
            var extra = ReadExtra(args);
            string? benchOpt = Sfs.TakeOption(args, "--bench");
            string? createdOpt = Sfs.TakeOption(args, "--created");
            string? filter = Sfs.TakeOption(args, "--filter");
            string? exclude = Sfs.TakeOption(args, "--exclude");
            bool all = Sfs.TakeFlag(args, "--all");
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch RecipeCensus <plugin> [--bench <EditorID|FormKey>] [--created <Signature>] [--filter <recipe EditorID glob>] [--exclude <glob>] [--with p1,p2] [--all] [--out report.md]");
                return;
            }
            using var store = RecordStore.Open(args[0], extra);
            using var report = Sfs.ReportOut.Begin(outFile);
            var rx = filter == null ? null : Sfs.GlobRegex(filter);
            var exRx = exclude == null ? null : Sfs.GlobRegex(exclude);
            string? createdSig = createdOpt == null ? null : Program.NormalizeRecordTypePublic(createdOpt);

            var infos = new List<RecipeInfo>();
            foreach (var r in store.TargetRecords("ConstructibleObject"))
            {
                if (rx != null && !rx.IsMatch(r.EditorID)) continue;
                if (exRx != null && exRx.IsMatch(r.EditorID)) continue;
                string? bench = Str(r.Data["WorkbenchKeyword"]);
                if (benchOpt != null && !(bench != null && (store.Edid(bench).Equals(benchOpt, StringComparison.OrdinalIgnoreCase) ||
                                                           ShortFk(bench).StartsWith(benchOpt.Split(':')[0], StringComparison.OrdinalIgnoreCase)))) continue;
                string? co = Str(r.Data["CreatedObject"]);
                var cr = store.Get(co);
                if (createdSig != null && !string.Equals(cr?.Signature, createdSig, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new RecipeInfo { R = r };
                string own = co == null ? "" : Sfs.PluginOf(Sfs.NormalizeFormKey(co)).Equals(store.TargetFile, StringComparison.OrdinalIgnoreCase) ? "own" : "from master";
                info.Class = $"{store.Show(bench)} → {cr?.Signature ?? (co == null ? "(nothing)" : "(not in caches)")} ({own})";
                info.CreatedTier = cr == null ? null : Sfs.TierOf(cr.EditorID);
                info.Tier = Sfs.TierOf(r.EditorID) ?? info.CreatedTier ?? "all";

                var conds = new List<string>();
                if (r.Data["Conditions"] is JsonArray ca)
                    foreach (var c in ca)
                    {
                        conds.Add(CondText(store, c, out var lvl));
                        if (lvl != null) info.GetLevel = info.GetLevel == null ? lvl : info.GetLevel + "," + lvl;
                    }
                info.Conds = conds;
                var comps = new List<string>();
                if ((r.Data["ConstructibleComponents"] ?? r.Data["ConstructableComponents"]) is JsonArray cc)
                    foreach (var c in cc)
                    {
                        string? fk = Str(c?["Component"]);
                        var crec = store.Get(fk);
                        string label = crec != null && (crec.Signature is "Armor" or "Weapon") ? $"<{crec.Signature}>" : store.Edid(fk) is { Length: > 0 } e ? e : fk ?? "?";
                        comps.Add($"{label}×{Str(c?["RequiredCount"]) ?? "1"}");
                    }
                info.A["Components"] = comps.Count == 0 ? "none" : string.Join(" + ", comps.OrderBy(x => x, StringComparer.Ordinal));
                info.A["RequiredPerks"] = r.Data["RequiredPerks"] is JsonArray rp && rp.Count > 0
                    ? string.Join(" + ", rp.Select(p => $"{NameOrFk(store, Str(p?["Perk"]) ?? "")} rank {Str(p?["Rank"])}")) : "none";
                var filters = FkList(r.Data["RecipeFilters"]);
                info.A["RecipeFilters"] = filters.Count == 0 ? "none" : string.Join(" + ", filters.Select(f => NameOrFk(store, f)));
                info.A["IFK"] = Str(r.Data["InstantiationFilterKeyword"]) is string ifk ? NameOrFk(store, ifk) : "none";
                info.A["MenuSortOrder"] = Str(r.Data["MenuSortOrder"]) ?? "-";
                info.A["AmountProduced"] = Str(r.Data["AmountProduced"]) ?? "-";
                info.A["LearnMethod"] = Str(r.Data["LearnMethod"]) ?? "-";
                info.A["Tier field"] = Str(r.Data["Tier"]) ?? "-";
                info.A["Value"] = Str(r.Data["Value"]) ?? "-";
                string desc = Sfs.English(r.Data["Description"]) ?? "";
                string cname = cr == null ? "" : (Sfs.English(cr.Data["Name"]) ?? cr.Name);
                info.A["Description"] = desc.Length == 0 ? "empty"
                    : cname.Length > 0 && desc == cname ? "= created item's name"
                    : cname.Length > 0 && desc.StartsWith(cname, StringComparison.Ordinal) ? $"= created name + \"{Clip(desc.Substring(cname.Length), 50)}\""
                    : "own text";
                info.A["Override"] = r.IsOverride ? "override" : "new";
                infos.Add(info);
            }

            Sfs.Say($"# RecipeCensus: `{store.Target}`\n");
            if (infos.Count == 0) { Sfs.Summary("No recipes match."); return; }
            var classes = infos.GroupBy(i => i.Class).OrderByDescending(g => g.Count()).ToList();
            Sfs.Summary($"{infos.Count} recipes in {classes.Count} classes (bench → created type):");
            var ct = new Sfs.Table("#", "Bench → creates", "Recipes", "Tiers");
            int ci = 0;
            foreach (var g in classes)
            {
                ci++;
                var tiers = g.Select(i => i.Tier).Distinct().OrderBy(TierSortKey).ToList();
                ct.Add(ci, g.Key, g.Count(), tiers.Count == 1 && tiers[0] == "all" ? "-" : string.Join(" ", tiers));
                Sfs.ActiveSummary($"  {ci}. {g.Key}: {g.Count()}");
            }
            Sfs.Print(ct, annotate: false);

            string[] attrs = { "Conditions", "Components", "RequiredPerks", "RecipeFilters", "IFK", "MenuSortOrder", "Description", "AmountProduced", "LearnMethod", "Tier field", "Value", "Override" };
            ci = 0;
            foreach (var g in classes)
            {
                ci++;
                var list = g.ToList();
                int tiered = list.Count(i => i.Tier != "all");
                Sfs.Say($"\n## {ci}. {g.Key} — {list.Count} recipes" + (tiered > 0 && tiered < list.Count ? $" ({tiered} tiered, {list.Count - tiered} without a tier)" : "") + "\n");
                var byTier = list.GroupBy(i => i.Tier).OrderBy(x => TierSortKey(x.Key)).ToList();

                // Conditions every recipe of the class has are listed once; the table shows only the extra ones.
                var common = list[0].Conds.Where(c => list.All(i => i.Conds.Contains(c))).Distinct().ToList();
                foreach (var i in list)
                {
                    var extraConds = i.Conds.Where(c => !common.Contains(c)).ToList();
                    i.A["Conditions"] = extraConds.Count == 0 ? (common.Count == 0 ? "none" : "—") : "+ " + string.Join(" · ", extraConds);
                }

                var uniform = new List<string>();
                if (common.Count > 0) uniform.Add("Conditions: " + string.Join(" · ", common));
                var varying = new List<string>();
                foreach (var a in attrs)
                {
                    var vals = list.Select(i => i.A[a]).Distinct().ToList();
                    if (vals.Count > 1) varying.Add(a);
                    else if (!(a == "Conditions" && common.Count > 0)) uniform.Add($"{a}: {vals[0]}");
                }
                var lv = list.Where(i => i.GetLevel != null).GroupBy(i => i.GetLevel!).ToDictionary(x => x.Key, x => x.Count());
                Sfs.Say("**Same on every recipe in this class:**\n");
                foreach (var u in uniform) Sfs.Say($"- {u}");
                Sfs.Say($"\n**GetLevel:** {(lv.Count == 0 ? "no GetLevel condition" : $"{lv.Count} distinct values: {Dist(lv, 8)}")}" +
                        (list.Count(i => i.GetLevel == null) is int nl && nl > 0 && lv.Count > 0 ? $"; {nl} recipes without GetLevel" : ""));

                if (varying.Count > 0 || byTier.Count > 1)
                {
                    Sfs.Say("");
                    var cols = new List<string> { "Tier", "Recipes" };
                    if (byTier.Count > 1) cols.Add("GetLevel");
                    cols.AddRange(varying);
                    var t = new Sfs.Table(cols.ToArray());
                    foreach (var tg in byTier)
                    {
                        var row = new List<object?> { tg.Key, tg.Count() };
                        if (byTier.Count > 1) row.Add(Dist(tg.Where(i => i.GetLevel != null).GroupBy(i => i.GetLevel!).ToDictionary(x => x.Key, x => x.Count()), 3));
                        foreach (var a in varying)
                        {
                            var d = tg.GroupBy(i => i.A[a]).ToDictionary(x => x.Key, x => x.Count());
                            row.Add(d.Count == 1 ? d.Keys.First() : Dist(d, 3));
                        }
                        t.Add(row.ToArray());
                    }
                    Sfs.Print(t, annotate: false);
                }

                // ---- checks: created tier vs recipe tier, duplicates, outliers
                var mism = list.Where(i => i.CreatedTier != null && i.Tier != "all" && Sfs.TierOf(i.R.EditorID) != null && i.CreatedTier != i.Tier).ToList();
                if (mism.Count > 0)
                {
                    var md = mism.GroupBy(i => $"{i.Tier} recipe makes {i.CreatedTier}")
                                 .ToDictionary(x => $"{x.Key} on {x.Count()} of {list.Count(i => i.Tier == x.First().Tier)} {x.First().Tier} recipes", x => x.Count());
                    Sfs.Summary($"- Class {ci}: CreatedObject tier ≠ recipe tier: {string.Join("; ", md.OrderByDescending(kv => kv.Value).Select(kv => kv.Key))}");
                    Sfs.Say("  e.g. " + string.Join("; ", mism.Take(all ? 1000 : 5).Select(i => $"{i.R.EditorID} → {store.Edid(Str(i.R.Data["CreatedObject"]))}")));
                }
                var dups = list.GroupBy(i => Str(i.R.Data["CreatedObject"]) ?? "").Where(x => x.Key.Length > 0 && x.Count() > 1).ToList();
                if (dups.Count > 0)
                    Sfs.Say($"- Created objects made by more than one recipe in this class: {dups.Count}, e.g. " +
                            string.Join("; ", dups.Take(all ? 1000 : 3).Select(x => $"{store.Edid(x.Key)} ← {string.Join(", ", x.Select(i => i.R.EditorID))}")));

                // Outliers: recipes that differ from a ≥80% majority of their tier. The same odd value in several tiers
                // (e.g. melee recipes using the melee filter) is grouped into one line.
                var outl = new Dictionary<(string Attr, string Value, string Usual), List<RecipeInfo>>();
                int outRecipes = 0;
                foreach (var tg in byTier)
                    foreach (var a in attrs)
                    {
                        var d = tg.GroupBy(i => i.A[a]).OrderByDescending(x => x.Count()).ToList();
                        if (d.Count < 2) continue;
                        int n = tg.Count(), dom = d[0].Count();
                        if (dom * 5 < n * 4 && !all) continue; // no clear rule: shown in the table, not as outliers
                        foreach (var x in d.Skip(1))
                        {
                            var key = (a, x.Key, d[0].Key);
                            if (!outl.TryGetValue(key, out var l)) outl[key] = l = new();
                            l.AddRange(x);
                            outRecipes += x.Count();
                        }
                    }
                if (outl.Count > 0)
                {
                    Sfs.Say($"\n**Outliers** (differ from a ≥80% majority in their tier): {outRecipes} cases in {outl.Count} groups\n");
                    foreach (var ((a, v, usual), l) in outl.OrderByDescending(kv => kv.Value.Count).Take(all ? 100000 : 20))
                    {
                        var tiersHit = l.Select(i => i.Tier).Distinct().OrderBy(TierSortKey).ToList();
                        Sfs.Say($"- {a} = {Clip(v, 110)} instead of {Clip(usual, 60)}: {l.Count} recipe(s)" +
                                (tiersHit.Count == 1 && tiersHit[0] == "all" ? "" : $" in {string.Join(" ", tiersHit)}") + ": " +
                                string.Join(", ", l.Select(i => i.R.EditorID).Distinct().Take(all ? 100000 : 6)) + (l.Count > 6 && !all ? ", …" : ""));
                    }
                    if (outl.Count > 20 && !all) Sfs.Say($"- … (+{outl.Count - 20} more groups; `--all`)");
                }
            }
        }
    }
}
