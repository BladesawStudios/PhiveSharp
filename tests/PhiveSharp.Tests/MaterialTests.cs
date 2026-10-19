using System.Collections.Concurrent;

namespace PhiveSharp.Tests;

public class MaterialTests
{
    [Fact]
    public void AMaterialIsNamedFromPhiveConfig()
    {
        PhiveMaterial stairs = new(7, 0, 0x8041, ulong.MaxValue);

        Assert.Equal("Stone", stairs.MaterialName);
        Assert.Equal("Undefined", stairs.SubMaterialName);
        Assert.Equal(["NoClimb", "NoDashUp", "NoStick"], stairs.FlagNameList);
        Assert.Equal("Stone/Undefined [NoClimb, NoDashUp, NoStick]", stairs.ToString());
        Assert.Equal("Sand/karesansui", new PhiveMaterial(3, 40, 0, 0).ToString());
        Assert.Null(new PhiveMaterial(999, 0, 0, 0).MaterialName);
    }

    [Fact]
    public void AFilterIsTheLayersItHits()
    {
        // PhiveConfig's "Default" entity mask hits everything but the Ultrahand blocker.
        PhiveMaterial m = new(7, 0, 0, 0xFFFFFFFF_03F7FFFF);

        Assert.Equal(0x03F7FFFFu, m.LayerHitMask);
        Assert.DoesNotContain("UltraHandBlocker", m.HitLayers);
        Assert.Contains("Player", m.HitLayers);
        Assert.Equal(PhiveMaterial.SubLayerNames, m.HitSubLayers);
    }

    [TotkCorpusFact]
    public void EveryShapeWritesBackUnchangedWithANamedPalette()
    {
        Problems problems = new();
        int count = Corpus.ForEachFile((name, _, data) =>
        {
            try
            {
                PhiveShape shape = PhiveShape.FromBinary(data);
                if (shape.Materials.Count == 0) problems.Add($"{name}: empty palette");
                foreach (PhiveMaterial m in shape.Materials)
                    if (m.MaterialName is null || m.SubMaterialName is null) problems.Add($"{name}: unnamed {m.Material}/{m.SubMaterial}");
                if (!shape.ToBinary().AsSpan().SequenceEqual(data)) problems.Add($"{name}: differs");
            }
            catch (Exception e) { problems.Add($"{name}: {e.GetType().Name} {e.Message}"); }
        }, ".bphsh");

        problems.AssertNone();
        Assert.True(count > 7000, $"only {count} shapes found");
    }

    [TotkCorpusFact]
    public void EveryCompoundBodysPaletteIsNamed()
    {
        Problems problems = new();
        int bodies = 0;
        Corpus.ForEachFile((name, _, data) =>
        {
            StaticCompoundBody c = StaticCompoundBody.FromBinary(data);
            foreach (CompoundBody b in c.Bodies)
            {
                Interlocked.Increment(ref bodies);
                try
                {
                    foreach (PhiveMaterial m in c.MaterialsOf(b))
                        if (m.MaterialName is null || m.SubMaterialName is null) problems.Add($"{name}: unnamed {m.Material}/{m.SubMaterial}");
                }
                catch (Exception e) { problems.Add($"{name}: body {b.Index}: {e.Message}"); }
            }
        }, ".bphsc");

        problems.AssertNone();
        Assert.True(bodies > 400000, $"only {bodies} bodies found");
    }
}
