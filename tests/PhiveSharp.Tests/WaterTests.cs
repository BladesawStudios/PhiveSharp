using System.Collections.Concurrent;

namespace PhiveSharp.Tests;

public class WaterTests
{
    [TotkCorpusFact]
    public void EveryFieldTileCarriesWaterTheGameCanRead()
    {
        Problems problems = new();
        ConcurrentBag<string> palettes = [];
        int tiles = 0;
        long wetCells = 0, bottomAbove = 0;
        Corpus.ForEachFile((name, _, data) =>
        {
            StaticCompoundBody c = StaticCompoundBody.FromBinary(data);
            if (c.Water is not { } w) return;
            Interlocked.Increment(ref tiles);
            palettes.Add(string.Join(",", w.Types));
            long wet = 0, above = 0;
            for (int z = 0; z < w.Depth; z++)
                for (int x = 0; x < w.Width; x++)
                {
                    if (w.TypeAt(x, z) >= w.Types.Count) problems.Add($"{name}: ({x},{z}) fluid {w.TypeAt(x, z)} past the palette");
                    if (!w.CellHasWater(x, z)) continue;
                    wet++;
                    if (w.BottomAt(x, z) > w.SurfaceAt(x, z)) above++;
                }
            Interlocked.Add(ref wetCells, wet);
            Interlocked.Add(ref bottomAbove, above);
        }, ".bphsc");

        problems.AssertNone();
        Assert.True(tiles > 1200, $"only {tiles} tiles with water");
        Assert.Single(palettes.Distinct());
        // A handful of authored cells put the bottom a touch over the surface; it is not a misread.
        Assert.True(bottomAbove < wetCells / 10000, $"{bottomAbove} of {wetCells} wet cells have the bottom above the surface");
    }

    [TotkCorpusFact]
    public void FluidsLandWhereTheyBelong()
    {
        // Lava and hot springs are Eldin's, north-east; quicksand is Gerudo's, south-west.
        ConcurrentDictionary<string, (double X, double Z, long N)> sums = [];
        Corpus.ForEachFile((_, _, data) =>
        {
            if (StaticCompoundBody.FromBinary(data).Water is not { } w || w.Width != 256) return;
            for (int z = 0; z < w.Depth; z++)
                for (int x = 0; x < w.Width; x++)
                {
                    if (!w.CellHasWater(x, z) || w.FluidAt(x, z) is not { } fluid) continue;
                    var p = w.PositionOf(x, z);
                    sums.AddOrUpdate(fluid.MaterialName!, (p.X, p.Y, 1), (_, s) => (s.X + p.X, s.Z + p.Y, s.N + 1));
                }
        }, ".bphsc");

        (double X, double Z) Centre(string fluid) => sums[fluid] is var s ? (s.X / s.N, s.Z / s.N) : default;
        Assert.True(Centre("Lava") is { X: > 0, Z: < 0 }, $"lava centres on {Centre("Lava")}");
        Assert.True(Centre("HotWater") is { X: > 0, Z: < 0 }, $"hot water centres on {Centre("HotWater")}");
        Assert.True(Centre("Sand") is { X: < 0, Z: > 0 }, $"sand centres on {Centre("Sand")}");
    }
}
