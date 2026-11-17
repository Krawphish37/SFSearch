// A typed view's column read by the record's value walk (GAPS S122): the walk that reads a list view's entries from
// the bytes (RawWalkLists.cs) places every value it reaches, and a typed column is such a value of the one entry the
// record itself is. So a column with no fixed place (RawColumn.MakeColumn) and no layout's plan (RawPlannedColumns.cs)
// is read as a walked list's column is, by the same reader (WalkColumn): a field under an object Mutagen reads by hand
// and lays out (a script adapter's fragments, an NPC's race override, a weather's directional colours), a field after a
// text or a list in its subrecord, a field of one of two objects with the same subrecords (which the walk tells apart
// as Mutagen's generated reader does).
// Per record: one walk for the columns asked together, the parts of the record no column sits in left out; a part the
// walk didn't read at or above a column's field sends the record to Mutagen; a field the bytes don't hold is what
// Mutagen's object holds (WalkColumn). sfsquery-tests typed-all holds every such column against Mutagen's object, for
// every record of every type.
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

sealed partial class RawList
{
    /// <summary>One typed column read by the value walk.</summary>
    internal sealed class TypedWalk
    {
        internal required RecordSchema Schema;
        internal required string Type;
        internal required LinkFields Fields;
        internal required ColumnSpec Spec;
        /// <summary>The column's plain path from the record (what an unread part blocks).</summary>
        internal required string Plain;
        internal required string? Guard;
        internal WalkColumn? Column;
        internal bool Made;

        /// <summary>The reader of a typed column the value walk reaches in some kind of the objects on its way; null
        /// when the walk doesn't reach it, or the column's kind isn't one a walked column reads.</summary>
        public static TypedWalk? For(string type, ColumnSpec c, RecordSchema schema)
        {
            if (c.ShapeVaries) return null;
            string? plain = c.Kind == "ObjectType" ? c.ObjectPath : c.RelPath;
            if (plain is not { Length: > 0 } || plain.Contains('[')) return null;
            if (c.Kind != "ObjectType" && !WalkKinds.Contains(c.Kind)) return null;
            var reach = LinkByteWalker.ValueReach(schema, type);
            bool reached = c.Kind == "ObjectType"
                ? reach.Any(kv => kv.Value == null && LinkByteWalker.Unmarked(kv.Key).StartsWith(plain + ".", StringComparison.Ordinal))
                : reach.Any(kv => kv.Value == null && LinkByteWalker.Unmarked(kv.Key) == plain);
            if (!reached) return null;
            // Where the walk's claim of the record's subrecords isn't sure to be Mutagen's (each found by typed-all against
            // Mutagen's object): the column stays on Mutagen
            var model = schema.Model;
            var rows = model.Expand(type).ToList();
            // - a list whose items sit between markers the walk doesn't read (a sound echo marker's Markers): the items'
            //   subrecords may be claimed by another field of the record (its DefaultStartMarker)
            if (reach.Values.Any(why => why != null && why.StartsWith("its items sit between", StringComparison.Ordinal))) return null;
            // - the field's subrecord also named by another field of the record's own (a pack-in's two OPDS, a planet's
            //   two FNAM and GNAM): which of the two a subrecord is, the walk decides by a rule that isn't the reader's
            string top = plain.Split('.')[0];
            var subs = rows.Where(r => c.Kind == "ObjectType" ? r.PlainPath.StartsWith(plain + ".", StringComparison.Ordinal) : r.PlainPath == plain)
                .Select(r => r.Subrecord).Where(s => s is { Length: 4 }).Distinct().ToList();
            if (subs.Count == 0) return null;
            foreach (var r in rows)
            {
                if (r.PlainPath.Split('.')[0] == top) continue;
                // (the same subrecord inside a list's entries, a weapon's FULL in its object templates, the walk tells from the
                // record's own as Mutagen's generated reader does: GAPS S165)
                if (r.InList || r.Path.Contains('[')) continue;
                if (!(subs.Contains(r.Subrecord) || r.Def.CustomSubrecordList.Any(subs.Contains))) continue;
                var stack = r.Stack.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
                // (a record nested in this one is read from its own bytes; a component's subrecords sit between BFCB and BFCE)
                if (stack.Any(o => model.Objects.TryGetValue(o, out var od) && od.ObjType == "Record") || stack.Any(o => model.EndMarkerOf(o) == "BFCE")) continue;
                return null;
            }
            return new TypedWalk
            {
                Schema = schema, Type = type, Fields = LinkByteWalker.FieldsOf(schema, type), Spec = c, Plain = plain,
                Guard = c.GuardOmittable && c.NullGuard != null ? c.NullGuard : null,
            };
        }
    }

    /// <summary>Several typed columns of one record type read by one value walk.</summary>
    internal sealed class TypedWalks
    {
        readonly TypedWalk[] _walks;
        readonly Func<string, bool> _want;
        readonly HashSet<string>? _guards;
        [ThreadStatic] static LinkByteWalker? t_typedWalker;

        public TypedWalks(TypedWalk[] walks)
        {
            _walks = walks;
            var plains = walks.Select(w => w.Plain).Distinct(StringComparer.Ordinal).ToArray();
            _want = p => { foreach (string cp in plains) if (Under(cp, p) || Under(p, cp)) return true; return false; };
            var guards = walks.Where(w => w.Guard != null).Select(w => w.Guard!).ToHashSet(StringComparer.Ordinal);
            _guards = guards.Count > 0 ? guards : null;
        }

        /// <summary>The columns' values for one record (into <paramref name="values"/> at <paramref name="at"/>); false
        /// when one of them must come from Mutagen.</summary>
        public bool Read(ReadOnlySpan<byte> head, ReadOnlySpan<byte> data, RawPlugin plugin, object?[] values, int[] at)
        {
            var first = _walks[0];
            var raw = RawRecord.FromInflated(head, data);
            var walker = t_typedWalker;
            if (walker == null || walker.Schema != first.Schema) t_typedWalker = walker = new LinkByteWalker(first.Schema);
            var found = new Found { Keep = null, Guards = _guards, Raw = raw };
            walker.WalkValues(first.Fields, raw, plugin.Localized, found, want: _want, component: k => plugin.Component(raw, k, first.Fields.Type));
            for (int ui = 0; ui < found.Unread.Count; ui++)
            {
                string g = Indexes.Replace(found.Unread[ui], "[*]");
                foreach (var w in _walks)
                    if (Under(w.Plain, g)) { LeftWhy?.Invoke(first.Type + " (typed)", $"unread {g}: {found.UnreadWhy[ui]}"); return false; }
            }
            for (int i = 0; i < _walks.Length; i++)
            {
                var w = _walks[i];
                if (!w.Made) { w.Column = WalkColumn.Make(w.Spec, w.Type, w.Schema); w.Made = true; }
                if (w.Column == null || !w.Column.Read("", found, raw, plugin, out values[at[i]]))
                {
                    LeftWhy?.Invoke(first.Type + " (typed)", (w.Column == null ? "no reader for column " : "no value for column ") + w.Spec.Name);
                    return false;
                }
            }
            return true;
        }
    }
}
