// J125 (GAPS S122): the typed columns read by a layout's plan (RawPlannedColumns.cs: a value Mutagen's own layout of
// a subrecord places, where its generated reader gives no fixed place). Every record type that has such a column, its
// records' planned columns from the bytes against Mutagen's object, record by record: the same value and kind, or the
// record left to Mutagen. Over Starfield.esm and three smaller plugins that hold the world types it has few or none of
// (20 s in all, Starfield.esm's 3.29 million placed objects 9 s of it: only the planned columns are read).
using SFSearch.Index;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class PlannedColumnTests
{
    const string SF = "Starfield.esm";
    static readonly string[] Plugins = { SF, "sfta03.esm", "kgcdoom.esm", "ShatteredSpace.esm" };

    public static string Run(Ctx c)
    {
        var said = new List<string>();
        int types = 0, columns = 0;
        bool heavy = c.Records.AllowHeavy;
        c.Records.AllowHeavy = true;
        try
        {
            foreach (string root in c.Engine.Views.RootNames.OrderBy(r => r, StringComparer.Ordinal))
            {
                var view = c.Engine.Views.Catalog(root).Typed;
                if (view.IsUnion || view.Types.Count != 1 || c.Schema.SignatureOf(view.Types[0]) is not { } sig) continue;
                int planned = view.Columns.Count(col => RawColumn.For(view, col, c.Schema) is { IsPlanned: true });
                if (planned == 0) continue;
                types++; columns += planned;
                int Count(string plugin) => c.Index.Plugin(plugin)?.Segment?.Signatures.Where(s => s.Signature == sig).Select(s => s.Count).FirstOrDefault() ?? 0;
                var plugins = Plugins.Where(p => Count(p) > 0).ToList();
                if (plugins.Count == 0) { said.Add($"{root}: {planned} planned column(s), no record in the plugins tried"); continue; }
                foreach (string plugin in plugins)
                    said.Add(GSqlTests.RawAgainstMutagen(c, plugin, view.Types[0], raw => raw.IsPlanned));
            }
        }
        finally { c.Records.AllowHeavy = heavy; }
        Check(types > 0, "no typed column is read by a layout's plan");
        return $"{columns} planned columns of {types} types; " + string.Join("; ", said);
    }

    /// <summary>
    /// GAPS S166: the condition list views (…_Conditions) the bytes read, by the record XML's layout of a CTDA
    /// (LinkByteWalker's ConditionValues). Every record type that has one, Starfield.esm's records: each view's rows from
    /// the record's bytes against Mutagen's object walked by DirectList, row for row and value for value, or the record
    /// left to Mutagen.
    /// </summary>
    public static string Conditions(Ctx c)
    {
        var said = new List<string>();
        int types = 0, views = 0;
        bool heavy = c.Records.AllowHeavy;
        c.Records.AllowHeavy = true;
        try
        {
            foreach (string root in c.Engine.Views.RootNames.OrderBy(r => r, StringComparer.Ordinal))
            {
                var cat = c.Engine.Views.Catalog(root);
                if (cat.Typed.IsUnion || cat.Typed.Types.Count != 1 || c.Schema.SignatureOf(cat.Typed.Types[0]) is not { } sig) continue;
                bool Held(ViewSpec v) => v.Name.EndsWith("Conditions", StringComparison.Ordinal);
                int n = cat.Lists.Values.Count(v => Held(v) && RawList.For(v, Array.Empty<string>(), c.Schema, cat.Typed) != null);
                if (n == 0) continue;
                if ((c.Index.Plugin(SF)?.Segment?.Signatures.Where(s => s.Signature == sig).Select(s => s.Count).FirstOrDefault() ?? 0) == 0) continue;
                types++; views += n;
                said.Add(WorldReadTests.ListsAgainstMutagen(c, SF, cat.Typed.Types[0], Held));
            }
        }
        finally { c.Records.AllowHeavy = heavy; }
        Check(views > 0, "no condition list view is read from the bytes");
        return $"{views} condition list views of {types} types; " + string.Join("; ", said);
    }
}
