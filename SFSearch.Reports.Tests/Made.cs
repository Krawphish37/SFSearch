// Plugins made for the tests, written by Mutagen into the work folder (never into Data, never into the repo).
// Each is a new plugin with Starfield.esm as its only master and one known fault, or one known shape.
using System.Buffers.Binary;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using MutagenStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.Reports.Tests;

static class Made
{
    public const string Game = "Starfield.esm";

    public static StarfieldMod New(string name) => new(ModKey.FromFileName(name), StarfieldRelease.Starfield);

    public static FormKey Key(string formKey) => FormKey.Factory(formKey);

    /// <summary>Writes a new plugin into the work folder. Its masters are worked out by Mutagen from its links.</summary>
    public static string Write(Ctx ctx, StarfieldMod mod, string folder, MutagenStyle self = MutagenStyle.Full, bool checkCompaction = false, string? extraMaster = null)
    {
        string path = Path.Combine(ctx.Folder(folder), mod.ModKey.FileName);
        ctx.MustBeInWork(path);
        var order = new List<KeyedMasterStyle> { new(ModKey.FromFileName(Game), MutagenStyle.Full) };
        if (extraMaster != null) order.Add(new KeyedMasterStyle(ModKey.FromFileName(extraMaster), MutagenStyle.Full));
        order.Add(new KeyedMasterStyle(mod.ModKey, self));
        var lookup = new LoadOrder<IModMasterStyledGetter>(order);
        mod.WriteToBinary(path, new BinaryWriteParameters
        {
            MasterFlagsLookup = lookup, ModKey = ModKeyOption.CorrectToPath,
            MastersListOrdering = new MastersListOrderingByLoadOrder(lookup),
            LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed(),
            // The header's size flags as the test set them (Mutagen 0.55.0-fixes.13's default would raise a size the records don't fit, and add the Master flag)
            HeaderSizeFlags = HeaderSizeFlagsOption.NoCheck,
            FormIDCompaction = checkCompaction ? FormIDCompactionOption.Iterate : FormIDCompactionOption.NoCheck,
        });
        return path;
    }

    /// <summary>The flags of a plugin file's header (TES4), at byte 8.</summary>
    public static uint HeaderFlags(string path)
    {
        Span<byte> b = stackalloc byte[12];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b[8..]);
    }

    /// <summary>Sets header flags in a plugin file the tests wrote into the work folder.</summary>
    public static void SetHeaderFlags(Ctx ctx, string path, uint flags)
    {
        ctx.MustBeInWork(path);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, flags);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.Position = 8;
        fs.Write(b);
    }
}
