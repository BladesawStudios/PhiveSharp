using PhiveSharp.Havok;

namespace PhiveSharp.Tests;

public class NavMeshTests
{
    [Fact]
    public void AFaceUnpacksIntoActorTagsAndMaterial()
    {
        PhiveNavFace face = new(unchecked((int)0x3C820005));

        Assert.Equal(5, face.LinkedActor);
        Assert.False(face.UsableWhileActorOff);
        Assert.Equal(0, new PhiveNavFace(unchecked((int)0x00C00000)).LinkedActor);
        Assert.True(new PhiveNavFace(unchecked((int)0x00C00000)).UsableWhileActorOff);
        Assert.Equal(-1, new PhiveNavFace(unchecked((int)0x02800003)).LinkedActor);
        Assert.Equal("Miasma", face.MaterialName);
        Assert.Equal(["TraversableWhenDisableExtraData"], face.UserTagNames);
        Assert.Equal(-1, new PhiveNavFace(0x54000000).LinkedActor);
        Assert.Equal("Water", new PhiveNavFace(0x54000000).MaterialName);
    }

    [TotkCorpusFact]
    public void EveryNavMeshWritesBackUnchanged()
    {
        Problems problems = new();
        int count = Corpus.ForEachFile((name, _, data) =>
        {
            try
            {
                if (!PhiveNavMesh.FromBinary(data).ToBinary().AsSpan().SequenceEqual(data)) problems.Add($"{name}: differs");
            }
            catch (Exception e) { problems.Add($"{name}: {e.GetType().Name} {e.Message}"); }
        }, ".bphnm");

        problems.AssertNone();
        Assert.True(count > 2500, $"only {count} navmeshes found");
    }

    [TotkCorpusFact]
    public void StitchedTilesAreNumberedAcrossTheirField()
    {
        Problems problems = new();
        Corpus.ForEachFile((name, _, data) =>
        {
            if (!name.Contains("StitchedNavMesh")) return;
            PhiveNavMesh mesh = PhiveNavMesh.FromBinary(data);
            if (mesh.SectionUid == PhiveNavMesh.NoSection) return;
            string[] parts = Path.GetFileName(name).Split('.')[0].Split('_');
            uint expected = (uint)(int.Parse(parts[0][1..]) * 32 + int.Parse(parts[1][1..]) + (name.Contains("MinusField") ? 1280 : 0));
            if (mesh.SectionUid != expected) problems.Add($"{name}: section {mesh.SectionUid}, expected {expected}");
        }, ".bphnm");

        problems.AssertNone();
    }

    [TotkCorpusFact]
    public void EveryFaceNamesARealActorAndMaterial()
    {
        Problems problems = new();
        long linked = 0;
        Corpus.ForEachFile((name, _, data) =>
        {
            PhiveNavMesh mesh = PhiveNavMesh.FromBinary(data);
            if (mesh.GetNavMesh().RootObject is not { } root) return;
            foreach (TagObject variant in root.GetArray("namedVariants").Elements)
            {
                if (variant.GetPointer("variant") is not { TypeName: "hkaiNavMesh" } nav) continue;
                TagArray faceData = nav.GetArray("faceData");
                for (int i = 0; i < faceData.Count; i++)
                {
                    PhiveNavFace face = new((int)faceData.GetInt64(i));
                    if (face.MaterialName is null) problems.Add($"{name}: face {i} material {face.Material}");
                    if (face.LinkedActor >= mesh.LinkedActors.Count) problems.Add($"{name}: face {i} links actor {face.LinkedActor} of {mesh.LinkedActors.Count}");
                    if (face.LinkedActor >= 0) Interlocked.Increment(ref linked);
                    else if ((face.Value & 0xFFFF) != 0) problems.Add($"{name}: face {i} has an actor index but no link");
                }
            }
        }, ".bphnm");

        problems.AssertNone();
        Assert.True(linked > 100000, $"only {linked} faces link an actor");
    }

    [TotkCorpusFact]
    public void RoadsAreOnTheSurfaceAndNotInTheDepths()
    {
        long surface = 0, depths = 0;
        Corpus.ForEachFile((name, _, data) =>
        {
            if (!name.Contains("StitchedNavMesh")) return;
            if (PhiveNavMesh.FromBinary(data).GetNavMesh().RootObject is not { } root) return;
            foreach (TagObject variant in root.GetArray("namedVariants").Elements)
            {
                if (variant.GetPointer("variant") is not { TypeName: "hkaiNavMesh" } nav) continue;
                TagArray faceData = nav.GetArray("faceData");
                long roads = 0;
                for (int i = 0; i < faceData.Count; i++)
                    if (new PhiveNavFace((int)faceData.GetInt64(i)).UserTagNames.Contains("Road")) roads++;
                Interlocked.Add(ref name.Contains("MinusField") ? ref depths : ref surface, roads);
            }
        }, ".bphnm");

        Assert.Equal(0, depths);
        Assert.True(surface > 10000, $"only {surface} road faces on the surface");
    }
}
