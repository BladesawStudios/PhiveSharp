using System.Buffers.Binary;
using PhiveSharp.Havok;

namespace PhiveSharp;

/// <summary>
/// A <c>.bphnm</c>: an <c>hkaiNavMesh</c> in section 0, where it sits in section 1, and in section 2
/// the placed actors that can switch parts of it on and off.
/// </summary>
/// <remarks>
/// Section 1 is the navmesh's section UID and a 3 by 4 transform, row-major with the translation in
/// the last column. A field's stitched navmesh is numbered <c>x * 32 + z</c> across its grid of tiles,
/// plus 1280 for the Depths, and every streaming set in the file joins that section to a neighbour;
/// a tile with no navmesh is all ones and a dungeon is zero. Section 2 is the Banc hashes of placed
/// actors - miasma, cut areas, sage walls - which the game indexes so it can enable and disable the
/// faces that name them through <see cref="PhiveNavFace.LinkedActor"/>.
/// </remarks>
public sealed class PhiveNavMesh
{
    private const int NavMeshSection = 0;
    private const int InfoSection = 1;
    private const int ActorSection = 2;
    private const int InfoSize = 52;

    /// <summary>The section UID of a tile that carries no navmesh.</summary>
    public const uint NoSection = uint.MaxValue;

    /// <summary>The container this was read from. Sections not modelled here are still in it.</summary>
    public PhiveFile Container { get; }

    public uint SectionUid { get; set; }

    /// <summary>Twelve floats, three rows of rotation and translation.</summary>
    public float[] Transform { get; } = new float[12];

    /// <summary>The placed actors a face's <see cref="PhiveNavFace.LinkedActor"/> indexes, by Banc hash.</summary>
    public List<ulong> LinkedActors { get; } = [];

    private PhiveNavMesh(PhiveFile container) => Container = container;

    /// <exception cref="InvalidDataException">The data is not a Phive navmesh.</exception>
    public static PhiveNavMesh FromBinary(ReadOnlySpan<byte> data) => FromContainer(PhiveFile.FromBinary(data));

    public static PhiveNavMesh FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    /// <exception cref="InvalidDataException">The container does not hold a navmesh.</exception>
    public static PhiveNavMesh FromContainer(PhiveFile container)
    {
        if (!TagFile.IsTagFile(container[NavMeshSection]))
            throw new InvalidDataException("Section 0 does not hold a Havok tagfile.");
        ReadOnlySpan<byte> info = container[InfoSection];
        if (info.Length != InfoSize) throw new InvalidDataException($"Section 1 is {info.Length} bytes; a navmesh's is {InfoSize}.");
        ReadOnlySpan<byte> actors = container[ActorSection];
        if (actors.Length % 8 != 0) throw new InvalidDataException("Section 2 is not a whole number of actor hashes.");

        PhiveNavMesh mesh = new(container) { SectionUid = BinaryPrimitives.ReadUInt32LittleEndian(info) };
        for (int i = 0; i < 12; i++) mesh.Transform[i] = BinaryPrimitives.ReadSingleLittleEndian(info[(4 + i * 4)..]);
        for (int at = 0; at < actors.Length; at += 8) mesh.LinkedActors.Add(BinaryPrimitives.ReadUInt64LittleEndian(actors[at..]));
        return mesh;
    }

    /// <summary>The Havok navmesh, parsed fresh; put it back with <see cref="SetNavMesh"/>.</summary>
    public TagFile GetNavMesh() => TagFile.FromBinary(Container[NavMeshSection]);

    public void SetNavMesh(TagFile navMesh) => Container.SetSection(NavMeshSection, navMesh.ToBinary());

    public byte[] ToBinary()
    {
        byte[] info = new byte[InfoSize];
        BinaryPrimitives.WriteUInt32LittleEndian(info, SectionUid);
        for (int i = 0; i < 12; i++) BinaryPrimitives.WriteSingleLittleEndian(info.AsSpan(4 + i * 4), Transform[i]);
        byte[] actors = new byte[LinkedActors.Count * 8];
        for (int i = 0; i < LinkedActors.Count; i++) BinaryPrimitives.WriteUInt64LittleEndian(actors.AsSpan(i * 8), LinkedActors[i]);

        Container.SetSection(InfoSection, info);
        Container.SetSection(ActorSection, actors);
        return Container.ToBinary();
    }

    public void WriteTo(string path) => File.WriteAllBytes(path, ToBinary());
}

/// <summary>
/// One entry of an <c>hkaiNavMesh</c>'s <c>faceData</c>, which Phive packs per face: a linked actor,
/// user tags, and a navmesh material. (Its <c>edgeData</c> is zero throughout.)
/// </summary>
public readonly record struct PhiveNavFace(int Value)
{
    /// <summary>
    /// An index into <see cref="PhiveNavMesh.LinkedActors"/>, or -1 for none. The game takes the low
    /// sixteen bits as the index when bit 23 is set and bit 25 is not.
    /// </summary>
    public int LinkedActor => (Value & 0x2800000) == 0x800000 ? Value & 0xFFFF : -1;

    /// <summary>
    /// The face is usable while its linked actor is switched off rather than on. Bit 22, set mostly on
    /// plain faces linked to the same actors as miasma and tar faces.
    /// </summary>
    public bool UsableWhileActorOff => (Value & 0x400000) != 0;

    /// <summary>PhiveConfig's <c>NavMeshUserTagCollection</c>, one bit each in its order.</summary>
    public int UserTags => (Value >> 16) & 0xF;

    public IEnumerable<string> UserTagNames
    {
        get
        {
            for (int i = 0; i < UserTagNamesTable.Count; i++)
                if ((UserTags & (1 << i)) != 0) yield return UserTagNamesTable[i];
        }
    }

    /// <summary>
    /// Bits 20, 21 and 24, which the game's A* edge filter and cost modifier do not read; only 24 is ever set, almost
    /// always on faces without a linked actor.
    /// </summary>
    public int Unknown => (Value >> 20) & 0x13;

    /// <summary>The face's navmesh material, by PhiveConfig's <c>NavMeshMaterialCollection</c>.</summary>
    public int Material => (int)((uint)Value >> 26) & 0x1F;

    public string? MaterialName => Material < MaterialNames.Count ? MaterialNames[Material] : null;

    public static IReadOnlyList<string> MaterialNames { get; } =
    [
        "Undefined", "LiftableByEnemy", "Smoke", "Beam", "Ice", "Fire", "Breakable", "DomesticAnimalNoEntry",
        "WaterGiantNoEntry", "GiantNoEntry", "LynelNoEntry", "NPCOnly", "NPCSpecial", "GerudoEntrance", "NoEntrySage",
        "Miasma", "EnemyOnly", "GiantOnly", "Tar", "Destructible", "Quicksand", "Water", "HotWater", "Damage",
        "GiantNoEntryForce"
    ];

    public static IReadOnlyList<string> UserTagNamesTable { get; } = ["LowCeiling", "TraversableWhenDisableExtraData", "Road", "Narrow"];
}
