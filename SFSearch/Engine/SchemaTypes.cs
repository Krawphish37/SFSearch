using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace SFSearch.Engine
{
    /// <summary>
    /// Record types as schema.db knows them (milestone M1): a 4-letter signature and the Mutagen name or names stored
    /// under it. The header index knows signatures only, so the new tools turn "Weapon" into WEAP here and show
    /// "WEAP Weapon" in their answers. schema.db sits beside SFSearch.exe. Without it, types are known by signature alone.
    /// </summary>
    public sealed class SchemaTypes
    {
        readonly Dictionary<string, List<string>> _bySig = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _sigOf = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>False when schema.db isn't there or can't be read.</summary>
        public bool Available { get; private set; }
        public string Path { get; private set; } = "";
        /// <summary>The schema's own version (it changes with the Mutagen build the schema was made from).</summary>
        public string Version { get; private set; } = "";

        public static string DefaultPath => System.IO.Path.Combine(AppContext.BaseDirectory, "schema.db");

        static SchemaTypes? _default;
        /// <summary>The schema beside the program, read once.</summary>
        public static SchemaTypes Default => _default ??= Load(DefaultPath);

        public static SchemaTypes Load(string path)
        {
            var s = new SchemaTypes { Path = path };
            if (!File.Exists(path)) return s;
            try
            {
                using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
                c.Open();
                using (var x = c.CreateCommand())
                {
                    x.CommandText = "SELECT Sig, Type FROM RecordTypes ORDER BY Sig, Type";
                    using var r = x.ExecuteReader();
                    while (r.Read())
                    {
                        string sig = r.GetString(0), type = r.GetString(1);
                        if (!s._bySig.TryGetValue(sig, out var list)) s._bySig[sig] = list = new List<string>();
                        list.Add(type);
                        s._sigOf[type] = sig;
                    }
                }
                using (var x = c.CreateCommand())
                {
                    x.CommandText = "SELECT Value FROM Meta WHERE Key = 'SchemaVersion'";
                    s.Version = x.ExecuteScalar() as string ?? "";
                }
                s.Available = s._bySig.Count > 0;
            }
            catch (SqliteException) { s._bySig.Clear(); s._sigOf.Clear(); }
            return s;
        }

        public IEnumerable<string> Signatures => _bySig.Keys;
        public IEnumerable<string> TypeNames => _sigOf.Keys;

        /// <summary>The Mutagen names stored under a signature (one for all but OMOD and GMST). Empty when the schema doesn't know it.</summary>
        public IReadOnlyList<string> TypesOf(string sig) => _bySig.TryGetValue(sig, out var l) ? l : Array.Empty<string>();

        /// <summary>The signature of a Mutagen name, letter case aside. Null when there is no such name.</summary>
        public string? SigOf(string type) => _sigOf.TryGetValue(type, out var s) ? s : null;

        /// <summary>
        /// One readable name for a signature. A signature with several Mutagen types gets the name they share:
        /// OMOD is "ObjectModification", GMST is "GameSetting". Null when the schema doesn't know the signature.
        /// </summary>
        public string? NameOf(string sig)
        {
            var types = TypesOf(sig);
            if (types.Count == 0) return null;
            if (types.Count == 1) return types[0];
            // The member every other name ends with a part of ("ObjectModification"), else what all names start with
            var general = types.Where(t => !t.StartsWith("Unknown", StringComparison.Ordinal) && t.StartsWith("Object", StringComparison.Ordinal)).OrderBy(t => t.Length).FirstOrDefault();
            if (general != null) return general;
            string prefix = types[0];
            foreach (var t in types)
            {
                int n = 0;
                while (n < prefix.Length && n < t.Length && prefix[n] == t[n]) n++;
                prefix = prefix[..n];
            }
            return prefix.Length >= 4 ? prefix : types[0];
        }

        /// <summary>"WEAP Weapon" as answers show a type; a star marks a signature that holds several Mutagen types.</summary>
        public string Label(string sig)
        {
            string? name = NameOf(sig);
            if (name == null) return sig;
            return TypesOf(sig).Count > 1 ? $"{sig} {name}*" : $"{sig} {name}";
        }

        /// <summary>The real names nearest to a wrong one, for "did you mean".</summary>
        public List<string> Nearest(string wrong, int count = 4)
        {
            var all = TypeNames.Concat(Signatures).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return all.Select(n => (Name: n, D: Distance(n.ToLowerInvariant(), wrong.ToLowerInvariant()),
                    Part: n.Contains(wrong, StringComparison.OrdinalIgnoreCase) || wrong.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x.Part || x.D <= Math.Max(2, wrong.Length / 3))
                .OrderBy(x => x.Part ? 0 : 1).ThenBy(x => x.D).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(count).Select(x => x.Name).ToList();
        }

        static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1], prev[j]) + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }
    }
}
