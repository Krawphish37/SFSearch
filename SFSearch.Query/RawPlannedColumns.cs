// A typed view's column read from the record's bytes where Mutagen's own layout of a subrecord places the value (J125,
// GAPS S122): the package's Layouts/HandReadLayouts.xml compiled (LayoutPlan) for a layout of Subs at the record's level
// (an AnyRecord's one Sub: NVNM's geometry, XTRV, XNSE, VMAD; a record type's Subs). The subrecord is found once at the
// record's own level, as for any byte column (RawRecordColumns), then the plan places its values: a navmesh's version,
// CRC, parent (its kind by the Case the parent FormID picks), grid size and bounds; a traversal's FormID and the rest of
// XTRV; a navmesh spline's reflection bytes; VMAD's version and object format before its scripts (a plan of the
// subrecord's start, LayoutPlan.Partial). A value of a Case the bytes didn't pick is NULL (the object is of the other
// kind); a value below its Versioning's form version is Mutagen's default (read as zero bytes); a Const is its value (a
// navmesh's Versioning, Break0). A record whose bytes don't follow the layout goes to Mutagen.
using System.Buffers.Binary;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

sealed partial class RawColumn
{
    /// <summary>Read by a layout's plan (see the file's head); null for any other column.</summary>
    PlannedValue? _planned;
    public bool IsPlanned => _planned != null;

    sealed class PlannedValue
    {
        public required LayoutPlan Plan;
        /// <summary>The slots that may hold the value (one per Case naming it); the placed one is read.</summary>
        public required int[] Slots;
        /// <summary>The value is a whole object's bytes from its start (a point stored as an object: a navmesh parent's
        /// Coordinates), or the bytes to the subrecord's end.</summary>
        public bool Rest;
        /// <summary>Per variant guard: the slots giving the kind there (a Case's type), and the kinds the view takes.</summary>
        public required (int[] Kinds, HashSet<string> Variants)[] Guards;
        /// <summary>For an ObjectType column: the slots giving the kind.</summary>
        public int[]? KindSlots;
        /// <summary>A Null at the value or an object on its way: placed, the value is NULL.</summary>
        public required int[] Nulls;
        /// <summary>A Const's value as bytes (null: none), and the bytes of Mutagen's default for a value not placed.</summary>
        public byte[]? Const;
        public required byte[] Zero;
        /// <summary>Every slot of the value is in a Case of a kind (its path holds one): not placed, the object is another kind.</summary>
        public bool InCase;
        public bool AbsentIsNull;
        [ThreadStatic] static Dictionary<LayoutPlan, LayoutPlan.Placed>? t_placed;
        public LayoutPlan.Placed Placed()
        {
            var of = t_placed ??= new Dictionary<LayoutPlan, LayoutPlan.Placed>(ReferenceEqualityComparer.Instance);
            if (!of.TryGetValue(Plan, out var r)) of[Plan] = r = Plan.NewPlaced();
            return r;
        }
    }

    static readonly System.Reflection.Assembly StarfieldAssembly = typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly;

    /// <summary>A path with its kinds of object left out.</summary>
    static string Unmarked(string path) => LinkByteWalker.Unmarked(path);

    /// <summary>The planned reader of a typed column (see the file's head), or null when no layout of Subs at the record's
    /// level places it.</summary>
    static RawColumn? Planned(string type, ColumnSpec c, RecordSchema schema)
    {
        if (c.Kind is "Array2DRow" or "Json" or "String" or "AssetLink" || c.ShapeVaries || !c.FromBase && c.VariantGuards.Count == 0) return null;
        string rel = c.Kind == "ObjectType" ? c.ObjectPath ?? "" : c.RelPath ?? "";
        if (rel.Length == 0 || rel.Contains('[')) return null;
        var m = schema.Model;
        string? recordSig = schema.SignatureOf(type);
        foreach (var h in m.HandLayouts.Where(x => x.Kind == "Record" && x.Sig == recordSig || x.Kind == "AnyRecord"))
        {
            if (LayoutPlan.SubsOf(h) is not { } subs) continue;
            foreach (var (sub, sig, field, repeats, _) in subs)
            {
                if (repeats || subs.Count(x => x.Sig == sig) != 1) continue;
                string inSub;
                if (field == null) inSub = rel;
                else if (rel.StartsWith(field + ".", StringComparison.Ordinal)) inSub = rel[(field.Length + 1)..];
                else continue;
                if (LayoutPlan.Of(m, sub, out _) is not { } plan) continue;
                // The Sub's object: absent with its subrecord; NULL when Mutagen's may be null, else read through Mutagen
                bool absentIsNull = field == null ? false : schema.Model.Expand(type).FirstOrDefault(r => r.PlainPath == field)?.Def.Nullable == true;
                var slots = plan.Slots;
                int[] Where(Func<LayoutPlan.Slot, bool> f) => Enumerable.Range(0, slots.Count).Where(k => slots[k].List < 0 && f(slots[k])).ToArray();
                var guards = new List<(int[], HashSet<string>)>();
                foreach (var (objPath, variants) in c.VariantGuards)
                {
                    if (!(field != null && objPath.StartsWith(field + ".", StringComparison.Ordinal) || field == null)) return null;
                    string inObj = field == null ? objPath : objPath[(field.Length + 1)..];
                    var kinds = Where(s => s.Kind == "Kind" && Unmarked(s.Path) == inObj);
                    if (kinds.Length == 0) return null;
                    guards.Add((kinds, variants));
                }
                // Nulls at the value or on its way
                var nulls = Where(s => s.Kind == "Null" && (Unmarked(s.Path) == inSub || inSub.StartsWith(Unmarked(s.Path) + ".", StringComparison.Ordinal)));
                var pv = new PlannedValue { Plan = plan, Slots = Array.Empty<int>(), Guards = guards.ToArray(), Nulls = nulls, Zero = Array.Empty<byte>(), AbsentIsNull = absentIsNull };
                string sub4 = sig;
                if (c.Kind == "ObjectType")
                {
                    var kinds = Where(s => s.Kind == "Kind" && Unmarked(s.Path) == inSub);
                    if (kinds.Length == 0) return null;
                    pv.KindSlots = kinds;
                    return new RawColumn(c.Name, sub4, 0, 0, Form.Constant, absentIsNull, null, false, null) { _planned = pv };
                }
                var vals = Where(s => Unmarked(s.Path) == inSub && s.Kind is not ("Kind" or "Null"));
                if (vals.Length == 0) return null;
                var first = slots[vals[0]];
                if (vals.Any(k => slots[k].Kind != first.Kind) || first.Encoding != null) return null;
                pv.InCase = vals.All(k => slots[k].Path.Contains('{'));
                if ((c.Row ?? c.FirstRow)?.Def is not { } def) return null;
                Type? et = FlagsRaw.IsEnumKind(c.Kind) ? EnumType(type, rel) : null;
                int size;
                switch (first.Kind)
                {
                    case "Const":
                    {
                        // No bytes of its own: the value it says, as the bytes of the field's width
                        size = et != null ? System.Runtime.InteropServices.Marshal.SizeOf(Enum.GetUnderlyingType(et)) : def.Size ?? 0;
                        if (size is not (1 or 2 or 4 or 8)) return null;
                        long n;
                        if (et != null) { if (!Enum.TryParse(et, first.Value, out var ev)) return null; n = Convert.ToInt64(ev, System.Globalization.CultureInfo.InvariantCulture); }
                        else if (!long.TryParse(first.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n)) return null;
                        pv.Const = new byte[size];
                        for (int i = 0; i < size; i++) pv.Const[i] = (byte)(n >> (8 * i));
                        break;
                    }
                    case "Object":
                        // A point stored as an object of its parts (a parent's Coordinates, the grid's max distance)
                        if (c.Kind is not ("P2Int16" or "P2Float" or "P3Float" or "P3Int16") || def.Size is not int os) return null;
                        size = os;
                        break;
                    case "FormID": if (c.Kind != "FormLink") return null; size = 4; break;
                    case "Bytes":
                        if (c.Kind != "ByteArray") return null;
                        size = first.Size; pv.Rest = first.Size < 0;
                        break;
                    default:
                        if (!(KindFitsLayout(first.Kind, c.Kind) || first.Kind == c.Kind)) return null;
                        size = first.Size;
                        break;
                }
                if (size == 0 && !pv.Rest) return null;
                pv.Slots = vals; pv.Zero = new byte[Math.Max(size, 0)];
                bool zeroIsDefault = !def.Nullable && def.Default == null;
                object? viewDefault = c.DefaultSql != null && !c.Nullable && DirectColumn.TryLiteral(c.DefaultSql, out var dv) ? dv : null;
                if (FormOf(c.Kind, pv.Rest ? 1 : size, et) is not { } form || form is Form.Text or Form.Present or Form.Translated or Form.Percent) return null;
                return new RawColumn(c.Name, sub4, 0, pv.Rest ? -1 : size, form, absentIsNull, et, zeroIsDefault && form is not (Form.Link or Form.Bytes or Form.Color), viewDefault, whole: pv.Rest)
                {
                    _planned = pv, _swap = Swapped(def),
                };
            }
        }
        return null;
    }

    /// <summary>A hand-read layout's value kind fits the column's kind (as RawLists' KindFits).</summary>
    static bool KindFitsLayout(string layout, string column) => (layout, column) switch
    {
        ("I32", "Int32") or ("U32", "UInt32") or ("I16", "Int16") or ("U16", "UInt16") or ("I8", "Int8") or ("U8", "UInt8") or ("U64", "UInt64") => true,
        ("F32", "Float") or ("F16", "Half") or ("P3F32", "P3Float") or ("P3I16", "P3Int16") or ("Color", "Color") or ("Guid", "Guid") => true,
        ("U8" or "U16" or "U32" or "I32" or "I16" or "I8", "Enum" or "Flags" or FlagsRaw.Kind) => true,
        _ => false,
    };

    /// <summary>The column's value from its subrecord's content by the plan (<paramref name="present"/> false: the record
    /// hasn't the subrecord).</summary>
    public Outcome ReadPlanned(ReadOnlySpan<byte> content, bool present, int formVersion, RawPlugin plugin, out object? value)
    {
        value = null;
        var pv = _planned!;
        if (!present) return pv.AbsentIsNull ? Outcome.Value : Outcome.Mutagen;
        var r = pv.Placed();
        if (!pv.Plan.Run(content, formVersion, r)) return Outcome.Mutagen;
        // The kind each guard needs: an object of another kind has no such field (NULL)
        foreach (var (kinds, variants) in pv.Guards)
        {
            string? chosen = null;
            foreach (int k in kinds) if (r.At[k] >= 0) { chosen = pv.Plan.Slots[k].Value; break; }
            if (chosen == null || !variants.Contains(chosen)) return Outcome.Value;
        }
        if (pv.KindSlots != null)
        {
            foreach (int k in pv.KindSlots)
                if (r.At[k] >= 0)
                {
                    if (StarfieldAssembly.GetType("Mutagen.Bethesda.Starfield." + pv.Plan.Slots[k].Value) is not { } t) return Outcome.Mutagen;
                    value = DirectColumn.ObjectTypeName(t);
                    return Outcome.Value;
                }
            return Outcome.Value;
        }
        foreach (int k in pv.Nulls) if (r.At[k] >= 0) return Outcome.Value;
        int at = -1;
        foreach (int k in pv.Slots) if ((at = r.At[k]) >= 0) break;
        ReadOnlySpan<byte> b;
        if (pv.Const != null) b = pv.Const;
        else if (at < 0)
        {
            // In a Case not taken: the object is of another kind (NULL); else below its Versioning: Mutagen's default
            if (pv.InCase) return Outcome.Value;
            b = pv.Zero;
        }
        else if (pv.Rest) b = content[at..];
        else
        {
            if (at + pv.Zero.Length > content.Length) return Outcome.Mutagen;
            b = content.Slice(at, pv.Zero.Length);
        }
        return Read(b, true, plugin, out value);
    }
}
