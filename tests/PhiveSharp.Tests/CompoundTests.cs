using PhiveSharp.Havok;

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
                if (c.GetShape(shapes[k])?.RootObject is not { TypeName: "hknpCompoundShape" } root) continue;
                Interlocked.Increment(ref sections);
                foreach (TagObject instance in root.GetObject("instances").GetArray("elements").Elements)
                {
                    long index = instance.GetInt64("shapeTag");
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
                if (!p.IsUsed || (p.Id & 0xFFFF) != a.PlacementId) problems.Add($"{name}: actor {a.Hash:X16} names placement {a.PlacementId:X}/{a.PlacementSlot}");
            }
            foreach (CompoundGroup g in c.EntityGroups.Concat(c.SensorGroups).Where(g => g.Slot != 0))
                if ((c.Placements[g.Slot].Id & 0xFFFF) != g.PlacementId) problems.Add($"{name}: group names placement {g.PlacementId:X}/{g.Slot}");
        }, ".bphsc");

        problems.AssertNone();
        Assert.True(placed > 2000, $"only {placed} actors name a placement");
    }
}
