using System.Buffers.Binary;
using System.Collections.Concurrent;
using HkxSharp;

namespace PhiveSharp.Tests;

public class ContainerTests
{
    [Fact]
    public void AContainerWritesBackTheSectionsItWasGiven()
    {
        PhiveFile file = new() { Version = 1, Kind = 0, SlotCount = 4 };
        file.SetSection(0, [1, 2, 3, 4, 5]);
        file.SetSection(2, [9, 9, 9]);

        PhiveFile back = PhiveFile.FromBinary(file.ToBinary());

        Assert.Equal(3, back.Sections.Count);
        Assert.Equal([1, 2, 3, 4, 5], back.Sections[0]);
        Assert.Empty(back.Sections[1]);
        Assert.Equal([9, 9, 9], back.Sections[2]);
        Assert.Equal(file.ToBinary(), back.ToBinary());
    }

    [Fact]
    public void SomethingThatIsNotATagfileIsRefused()
    {
        byte[] junk = new byte[64];
        Assert.False(HkTagfile.IsTagfile(junk));
        Assert.Throws<InvalidDataException>(() => HkTagfile.FromBinary(junk));
    }

    [TotkCorpusFact]
    public void EveryContainerWritesBackUnchanged()
    {
        Problems problems = new();
        ConcurrentDictionary<string, int> kinds = [];
        Corpus.ForEachFile((name, ext, data) =>
        {
            kinds.AddOrUpdate(ext, 1, (_, n) => n + 1);
            try
            {
                if (!PhiveFile.FromBinary(data).ToBinary().AsSpan().SequenceEqual(data)) problems.Add($"{name}: differs");
            }
            catch (Exception e) { problems.Add($"{name}: {e.GetType().Name} {e.Message}"); }
        });

        problems.AssertNone();
        Assert.Equal([".bphcl", ".bphnm", ".bphsc", ".bphsh"], kinds.Keys.Order());
    }

    [TotkCorpusFact]
    public void EveryTagfileWritesBackUnchanged()
    {
        Problems problems = new();
        int tagfiles = 0;
        void Check(string name, PhiveFile file)
        {
            foreach (byte[] section in file.Sections)
            {
                if (HkTagfile.IsTagfile(section))
                {
                    Interlocked.Increment(ref tagfiles);
                    int length = (int)(BinaryPrimitives.ReadUInt32BigEndian(section) & 0x3FFFFFFF);
                    try
                    {
                        byte[] back = HkTagfileWriter.Write(HkTagfile.FromBinary(section));
                        if (!back.AsSpan().SequenceEqual(section.AsSpan(0, length)) || section.AsSpan(length).ContainsAnyExcept((byte)0))
                            problems.Add($"{name}: tagfile differs");
                    }
                    catch (Exception e) { problems.Add($"{name}: {e.GetType().Name} {e.Message}"); }
                }
                else if (PhiveFile.IsPhive(section)) Check(name, PhiveFile.FromBinary(section));
            }
        }
        Corpus.ForEachFile((name, _, data) => Check(name, PhiveFile.FromBinary(data)));

        problems.AssertNone();
        Assert.True(tagfiles > 26000, $"only {tagfiles} tagfiles found");
    }

}
