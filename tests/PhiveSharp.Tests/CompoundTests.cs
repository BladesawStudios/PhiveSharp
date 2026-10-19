using HkxSharp;

namespace PhiveSharp.Tests;

public class CompoundTests
{
    [Fact]
    public void GroupsAndBodiesUnpack()
    {
        // As stored: placement id 0x05A3, slot 1, GroundHighRes.
        Assert.Equal(new CompoundGroup(0x5A3, 1, 8), CompoundGroup.FromTag(0xA3050108));

        CompoundBody entity = new(0, 0x010C0800, 0, 0, 0, 0);
        Assert.False(entity.IsSensor);
        Assert.Equal("GroundHighRes", entity.LayerName);
        Assert.Equal("AirWall", entity.SubLayerName);

        CompoundBody sensor = new(0, 0x00001C01, 0, 0, 0, 0);
        Assert.True(sensor.IsSensor);
        Assert.Equal("ForbidAutoPlacementArea", sensor.LayerName);

        Assert.Equal(2, new CompoundBody(0, 0x02000800, 0, 0, 0, 0).Persistence);
        CompoundActor actor = new();
        actor.StartsDisabled = true;
        Assert.Equal(1, actor.Reserved[7]);
    }

    [TotkCorpusFact]
    public void EachBodyIsItsInstanceInItsGroupsShape()
    {
        Problems problems = new();
        long checkedBodies = 0;
        Corpus.ForEachFile((name, _, data) =>
        {
            StaticCompoundBody c = StaticCompoundBody.FromBinary(data);
            var groups = c.EntityGroups.Select(g => (g.Layer, g.Slot, Sensor: false)).Concat(c.SensorGroups.Select(g => (g.Layer, g.Slot, Sensor: true))).ToList();
            for (int k = 0; k < groups.Count; k++)
            {
                if (c.GetShape(StaticCompoundBody.FirstShapeSection + k)?.Root is not { ClassName: "hknpCompoundShape" } root) continue;
                object?[] instances = Instances(root);
                for (int i = 0; i < c.Bodies.Count; i++)
                {
                    CompoundBody b = c.Bodies[i];
                    if (b.Layer != groups[k].Layer || b.IsSensor != groups[k].Sensor || c.Actors[b.ActorIndex].PlacementSlot != groups[k].Slot) continue;
                    Interlocked.Increment(ref checkedBodies);
                    if (b.Instance >= instances.Length || ShapeTag(instances[b.Instance]) != i)
                        problems.Add($"{name}: body {i} is not instance {b.Instance} of section {StaticCompoundBody.FirstShapeSection + k}");
                }
            }
        }, ".bphsc");

        problems.AssertNone();
        Assert.True(checkedBodies > 400000, $"only {checkedBodies} bodies checked");
    }

    [TotkCorpusFact]
    public void WaterAndExternalMeshesAddUp()
    {
        Problems problems = new();
        int tiles = 0, quicksand = 0, whirlpools = 0;
        Corpus.ForEachFile((name, _, data) =>
        {
            StaticCompoundBody c = StaticCompoundBody.FromBinary(data);
            if (c.Placements.Sum(p => p.WaterBoxCount) != c.WaterBoxCount ||
                c.Placements.Sum(p => p.WaterCylinderCount) != c.WaterCylinderCount ||
                c.Placements.Sum(p => p.WaterQuadrantCount) != c.WaterQuadrantCount)
                problems.Add($"{name}: placements' water counts do not add up to the header's");
            if (c.WaterBoxes.Count != c.WaterBoxCount || c.WaterCylinders.Count != c.WaterCylinderCount || c.WaterQuadrants.Count != c.WaterQuadrantCount)
                problems.Add($"{name}: a water section is short");
            foreach (CompoundWater w in c.WaterBoxes.Concat(c.WaterCylinders).Concat(c.WaterQuadrants))
                if (w.MaterialName is null) problems.Add($"{name}: water material {w.Material}");
            foreach (CompoundWater w in c.WaterCylinders)
            {
                if (w.IsQuicksand) { Interlocked.Increment(ref quicksand); if (w.MaterialName != "Sand") problems.Add($"{name}: quicksand of {w.MaterialName}"); }
                if (w.IsWhirlpool) Interlocked.Increment(ref whirlpools);
            }
            var meshes = c.ExternalMeshes;
            if (meshes.Count != c.ExternalMeshCount) problems.Add($"{name}: {meshes.Count} of {c.ExternalMeshCount} external meshes");
            foreach (CompoundExternalMesh m in meshes)
                if (c.GetShape(m.Section)?.Root?.ClassName != "hknpMeshShape") problems.Add($"{name}: external mesh in section {m.Section} is not a mesh");
            if (Path.GetFileName(name).StartsWith("MainField_") || Path.GetFileName(name).StartsWith("MinusField_"))
            {
                if (meshes.Count != 1) problems.Add($"{name}: field tile with {meshes.Count} external meshes");
                else Interlocked.Increment(ref tiles);
            }
        }, ".bphsc");

        problems.AssertNone();
        Assert.True(tiles > 2500, $"only {tiles} field tiles");
        Assert.True(quicksand > 0 && whirlpools > 0, $"{quicksand} quicksand pits, {whirlpools} whirlpools");
    }

    [TotkCorpusFact]
    public void EveryBodyBelongsToOneOfItsKindsGroups()
    {
        Problems problems = new();
        Corpus.ForEachFile((name, _, data) =>
        {
            StaticCompoundBody c = StaticCompoundBody.FromBinary(data);
            var entity = c.EntityGroups.ToList();
            var sensor = c.SensorGroups.ToList();
            foreach (CompoundGroup g in entity)
                if (g.Layer >= PhiveMaterial.LayerNames.Count) problems.Add($"{name}: entity group layer {g.Layer}");
            foreach (CompoundGroup g in sensor)
                if (g.Layer >= PhiveMaterial.SensorLayerNames.Count) problems.Add($"{name}: sensor group layer {g.Layer}");
            if (c.Bodies.Count(b => !b.IsSensor) != c.KindACount || c.Bodies.Count(b => b.IsSensor) != c.KindBCount)
                problems.Add($"{name}: body kinds do not match the header's counts");
            foreach (CompoundBody b in c.Bodies)
                if (!(b.IsSensor ? sensor : entity).Any(g => g.Layer == b.Layer))
                    problems.Add($"{name}: body {b.Index} on {b.LayerName} has no group");
        }, ".bphsc");

        problems.AssertNone();
    }

    [TotkCorpusFact]
    public void EachGroupsShapeSectionHoldsOnlyItsBodies()
    {
        Problems problems = new();
        int sections = 0;
        Corpus.ForEachFile((name, _, data) =>
        {
            StaticCompoundBody c = StaticCompoundBody.FromBinary(data);
            var groups = c.EntityGroups.Select(g => (g.Layer, Sensor: false)).Concat(c.SensorGroups.Select(g => (g.Layer, Sensor: true))).ToList();
            var shapes = c.ShapeSections.ToList();
            for (int k = 0; k < groups.Count && k < shapes.Count; k++)
            {
                if (c.GetShape(shapes[k])?.Root is not { ClassName: "hknpCompoundShape" } root) continue;
                Interlocked.Increment(ref sections);
                foreach (object? instance in Instances(root))
                {
                    long index = ShapeTag(instance);
                    if (index < 0 || index >= c.Bodies.Count) continue;
                    CompoundBody body = c.Bodies[(int)index];
                    if (body.Layer != groups[k].Layer || body.IsSensor != groups[k].Sensor)
                    {
                        problems.Add($"{name}: section {shapes[k]} holds body {index} on {body.LayerName}, not group {k}'s layer {groups[k].Layer}");
                        break;
                    }
                }
            }
        }, ".bphsc");

        problems.AssertNone();
        Assert.True(sections > 13000, $"only {sections} group sections found");
    }

    [TotkCorpusFact]
    public void ActorsNameThePlacementTheyCameWith()
    {
        Problems problems = new();
        int placed = 0;
        Corpus.ForEachFile((name, _, data) =>
        {
            StaticCompoundBody c = StaticCompoundBody.FromBinary(data);
            foreach (CompoundActor a in c.Actors.Where(a => a.PlacementId != 0 || a.PlacementSlot != 0))
            {
                Interlocked.Increment(ref placed);
                CompoundPlacement p = c.Placements[a.PlacementSlot];
                if (!p.IsUsed || p.Id != a.PlacementId) problems.Add($"{name}: actor {a.Hash:X16} names placement {a.PlacementId:X}/{a.PlacementSlot}");
            }
            foreach (CompoundGroup g in c.EntityGroups.Concat(c.SensorGroups).Where(g => g.Slot != 0))
                if (c.Placements[g.Slot].Id != g.PlacementId) problems.Add($"{name}: group names placement {g.PlacementId:X}/{g.Slot}");
        }, ".bphsc");

        problems.AssertNone();
        Assert.True(placed > 2000, $"only {placed} actors name a placement");
    }

    [TotkCorpusFact]
    public void AShapeCanGrowAnInstance()
    {
        string path = Directory.EnumerateFiles(Path.Combine(Corpus.TotkRoot!, "Phive", "StaticCompoundBody", "MainField"), "MainField_E-4*.bphsc.zs").First();
        using ZsDicSharp.ZsDic zs = ZsDicSharp.ZsDic.FromRomfs(Corpus.TotkRoot!);
        StaticCompoundBody c = StaticCompoundBody.FromBinary(zs.DecompressFile(path));
        int section = c.ShapeSections.First(i => c.GetShape(i)?.Root?.ClassName == "hknpCompoundShape");

        HkTagfile shape = c.GetShape(section)!;
        HkStruct instances = (HkStruct)shape.Root!["instances"]!;
        int at = instances.Class.IndexOf("elements");
        object?[] before = (object?[])instances.Values[at]!;
        instances.Values[at] = before.Append(before[0]).ToArray();
        c.SetShape(section, shape);

        object?[] after = Instances(StaticCompoundBody.FromBinary(c.ToBinary()).GetShape(section)!.Root!);
        Assert.Equal(before.Length + 1, after.Length);
        Assert.Equal(ShapeTag(before[0]), ShapeTag(after[^1]));
        Assert.IsType<HkObject>(((HkStruct)after[^1]!)["shape"]);
    }

    private static object?[] Instances(HkObject compound) => (object?[])((HkStruct)compound["instances"]!)["elements"]!;

    private static long ShapeTag(object? instance) => Convert.ToInt64(((HkStruct)instance!)["shapeTag"]);
}
