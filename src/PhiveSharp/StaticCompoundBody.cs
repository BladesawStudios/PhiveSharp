using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using HkxSharp;

namespace PhiveSharp;

/// <summary>
/// One entry of the actor table: an actor from the map, and the run of bodies in this compound
/// that carry its collision.
/// </summary>
/// <remarks>
/// <see cref="Hash"/> and <see cref="SrtHash"/> are the actor's identity in the Banc placement
/// files, which is what makes this table useful: it is the only thing tying a lump of collision
/// geometry back to the actor it was baked from.
/// </remarks>
public sealed class CompoundActor
{
    /// <summary>The actor's Banc hash.</summary>
    public ulong Hash { get; set; }

    /// <summary>The actor's Banc SRT hash, which covers its placement.</summary>
    public uint SrtHash { get; set; }

    /// <summary>
    /// The first body in <see cref="StaticCompoundBody.Bodies"/> belonging to this actor, or -1
    /// when the actor has no collision here.
    /// </summary>
    public int FirstBody { get; set; } = -1;

    /// <summary>The last body belonging to this actor, inclusive, or -1. Often the same as
    /// <see cref="FirstBody"/>, since most actors bake to one body.</summary>
    public int LastBody { get; set; } = -1;

    /// <summary>
    /// The twelve bytes after the hashes. For an actor that came in with a placed sub-compound they
    /// name that placement - <see cref="PlacementId"/> and <see cref="PlacementSlot"/> - and byte 7
    /// is <see cref="StartsDisabled"/>.
    /// </summary>
    public byte[] Reserved { get; set; } = new byte[12];

    public bool HasBodies => FirstBody >= 0;

    /// <summary>The <see cref="CompoundPlacement.Id"/> the actor came in with, or zero.</summary>
    public int PlacementId => BinaryPrimitives.ReadUInt16LittleEndian(Reserved);

    /// <summary>The slot in <see cref="StaticCompoundBody.Placements"/> the actor came in with, or zero.</summary>
    public int PlacementSlot => Reserved[4];

    /// <summary>
    /// The game switches the actor's bodies off as the compound loads - as far as
    /// <see cref="CompoundBody.Persistence"/> goes, all of them - until the actor itself turns them on.
    /// </summary>
    public bool StartsDisabled
    {
        get => Reserved[7] != 0;
        set => Reserved[7] = (byte)(value ? 1 : 0);
    }

    public override string ToString() => $"{Hash:X16} bodies {FirstBody}..{LastBody}";
}

/// <summary>
/// One entry of the body table: a piece of collision, the actor it came from, and the run of
/// property entries it uses.
/// </summary>
/// <param name="Index">The entry's own position in the table, as stored.</param>
/// <param name="Info">
/// What kind of body it is, a byte each: entity or sensor, the layer, the sub layer, and how long it
/// outlasts its actor. See <see cref="IsSensor"/>, <see cref="Layer"/> and <see cref="Persistence"/>.
/// </param>
/// <param name="ActorIndex">
/// The owning actor, indexing <see cref="StaticCompoundBody.Actors"/>. It always agrees with
/// that actor's <see cref="CompoundActor.FirstBody"/> range, so the link can be followed either way.
/// </param>
/// <param name="Instance">
/// The body's instance in its group's compound shape, whose <c>shapeTag</c> is the body's index back.
/// </param>
/// <param name="FirstProperty">
/// Where this body's entries start in the property tables - sections 5 and 6, one sixteen byte
/// and one eight byte entry each. Runs may overlap: two bodies can share properties.
/// </param>
/// <param name="PropertyCount">How many property entries the body uses.</param>
public readonly record struct CompoundBody(
    uint Index, uint Info, int ActorIndex, int Instance, int FirstProperty, int PropertyCount)
{
    /// <summary>
    /// How far the body outlasts its actor. The game switches an actor's instances on and off with a
    /// level - -1 while the actor is live, 0 once a static compound controller has it, 1 once it is
    /// disabled or taken over, 3 to remove it - and keeps a body on while the level is below this:
    /// 0 for only while the actor is live, 1 for under a controller too, 2 for after it is gone.
    /// </summary>
    public int Persistence => (int)(Info >> 24);

    /// <summary>A sensor body rather than an entity; the compound counts each kind in its header.</summary>
    public bool IsSensor => (Info & 0xFF) == 1;

    /// <summary>The body's layer: an entity layer, or a sensor layer for a sensor.</summary>
    public int Layer => (int)(Info >> 8) & 0xFF;

    /// <summary>The body's sub layer, from the entity or sensor sub layers to match.</summary>
    public int SubLayer => (int)(Info >> 16) & 0xFF;

    public string? LayerName => Name(IsSensor ? PhiveMaterial.SensorLayerNames : PhiveMaterial.LayerNames, Layer);

    public string? SubLayerName => Name(IsSensor ? PhiveMaterial.SensorSubLayerNames : PhiveMaterial.SubLayerNames, SubLayer);

    private static string? Name(IReadOnlyList<string> names, int i) => i < names.Count ? names[i] : null;
}

/// <summary>
/// One of the compound's rigid bodies: every body on one layer from one placement goes into the same
/// one, named <c>&lt;compound&gt;_Entity_&lt;layer&gt;_&lt;slot&gt;</c> or the same with <c>Sensor</c>,
/// and its shape is the shape section in the same place in order - entity groups first.
/// </summary>
/// <param name="PlacementId">The placement's <see cref="CompoundPlacement.Id"/>; zero for slot 0.</param>
/// <param name="Slot">The slot in <see cref="StaticCompoundBody.Placements"/>.</param>
/// <param name="Layer">The entity or sensor layer.</param>
public readonly record struct CompoundGroup(int PlacementId, int Slot, int Layer)
{
    /// <summary>Unpacks an entry of <see cref="StaticCompoundBody.TagsA"/> or <see cref="StaticCompoundBody.TagsB"/>, which are read big endian.</summary>
    public static CompoundGroup FromTag(uint tag) => new((int)((tag >> 24) | (tag >> 8 & 0xFF00)), (int)(tag >> 8) & 0xFF, (int)tag & 0xFF);

    /// <summary>The layer value that ends a table.</summary>
    public const int End = 0xFF;
}

/// <summary>A piece of water the compound carries outside its tile height field.</summary>
/// <remarks>
/// Each is a height field the game builds <see cref="Scale"/> X by Z across and hanging
/// <see cref="Scale"/> Y below its transform. The <see cref="Parameters"/> shape the rest:
/// <list type="bullet">
/// <item>a cylinder: how far its surface dips at the centre (the game builds a funnel when this is set),
/// then the speed it pulls things outward at its rim - negative pulls them in - and the speed it swirls
/// them round, both rising toward the centre by 3 and 1.5 times for plain water; and one unused;</item>
/// <item>a quadrant: the height and the radius of its curve and the thickness of the box under it, and
/// one unused (a right angle in the shipped files).</item>
/// </list>
/// </remarks>
/// <param name="PlacementId">The <see cref="CompoundPlacement.Id"/> it came in with, or zero.</param>
/// <param name="FlowMode">
/// How a box's water flows: 0 along its own Z axis, 1 and 2 turning between its X and Z axes across
/// the box, one way or the other.
/// </param>
/// <param name="FlowSpeed">
/// How fast a box or quadrant carries things along; a quadrant's is at least a half, and scaled by 4.5
/// up to 10.
/// </param>
/// <param name="Material">An index into PhiveConfig's material collection: water, hot water, ice water, sand and so on.</param>
/// <param name="Hash">The Banc hash of the actor whose presence switches the water on, or zero.</param>
/// <param name="SecondHash">A second hash, on a few; not decoded.</param>
/// <param name="Parameters">Four more floats for a cylinder or a quadrant, none for a box.</param>
public sealed record CompoundWater(
    int PlacementId, int FlowMode, float FlowSpeed, Vector3 Scale, Vector3 Rotate, Vector3 Translate,
    int Material, ulong Hash, ulong SecondHash, float[] Parameters)
{
    public string? MaterialName => Material >= 0 && Material < PhiveMaterial.MaterialNames.Count ? PhiveMaterial.MaterialNames[Material] : null;

    /// <summary>A cylinder the game makes a whirlpool of: its third parameter is set.</summary>
    public bool IsWhirlpool => Parameters is [_, _, not 0f, _];

    /// <summary>A cylinder the game makes a quicksand pit of: no third parameter, and a second below zero.</summary>
    public bool IsQuicksand => Parameters is [_, < 0f, 0f, _];
}

/// <summary>An external mesh's placement, and the section that holds it.</summary>
public readonly record struct CompoundExternalMesh(Vector3 Scale, Vector3 Rotate, Vector3 Translate, int Section);

/// <summary>
/// A transform the compound was built under, with the hash of the actor it belongs to.
/// </summary>
/// <remarks>
/// Field tiles leave these empty. What fills them is a compound that was baked somewhere other
/// than where it is used - a well, a sky island, a merged set - where the transform says where
/// the whole compound sits and the hash names the actor that placed it.
/// </remarks>
public sealed class CompoundPlacement
{
    public Vector3 Translate { get; set; }

    /// <summary>Rotation in radians about each axis, the same three floats Banc stores.</summary>
    public Vector3 Rotate { get; set; }

    /// <summary>An identifier for the placement, which its groups, actors and water carry; 0xFFFF in an unused slot.</summary>
    public int Id { get; set; } = Empty;

    /// <summary>How many of <see cref="StaticCompoundBody.WaterBoxes"/> came in with this placement.</summary>
    public int WaterBoxCount { get; set; }

    /// <summary>How many of <see cref="StaticCompoundBody.WaterQuadrants"/> came in with this placement.</summary>
    public int WaterQuadrantCount { get; set; }

    /// <summary>How many of <see cref="StaticCompoundBody.WaterCylinders"/> came in with this placement.</summary>
    public int WaterCylinderCount { get; set; }

    /// <summary>The Banc hash of the actor this compound was placed as, or zero.</summary>
    public ulong Hash { get; set; }

    /// <summary>The id an unused slot carries.</summary>
    public const int Empty = 0xFFFF;

    public bool IsUsed => Hash != 0;
}

/// <summary>
/// A static compound body - a <c>.bphsc</c> file: the baked collision for one piece of map, and
/// the table that says which actor each piece of it came from.
/// </summary>
/// <remarks>
/// The container is a <see cref="PhiveFile"/> whose early sections the engine reads straight out
/// of the file and whose later ones hold the Havok shapes. This models the ones that can be
/// followed:
///
/// <list type="bullet">
/// <item>section 0, a fixed 19,120 byte block: the entity and sensor groups, the counts, the placements, the name, and how much water and how many external meshes follow the shapes;</item>
/// <item>section 1, the actor table;</item>
/// <item>sections 2 and 3, the hash map over it, which are rebuilt on write;</item>
/// <item>section 4, the body table;</item>
/// <item>sections 5 and 6, each body's material palette (<see cref="MaterialsOf"/>);</item>
/// <item>sections 7 to 10, a field tile's water (<see cref="Water"/>);</item>
/// <item>sections 11 and up, one shape per group, as Havok tagfiles;</item>
/// <item>after them, a section each of box, cylinder and quadrant water that there is any of, then a table of external meshes and a nested container for each.</item>
/// </list>
///
/// Everything not modelled is kept as bytes and written back untouched, so a file that is read
/// and written without being edited comes out identical.
///
/// Files on disc are zstd compressed; decompress before reading and compress after writing.
/// </remarks>
public sealed class StaticCompoundBody
{
    /// <summary>Section 0 is this size in every shipped file, arrays at capacity and all.</summary>
    public const int InfoSectionSize = 0x4AB0;

    private const int TagTableCapacity = 2048;
    private const int TagTableSize = TagTableCapacity * 4;
    private const int CountsOffset = TagTableSize * 2;          // 0x4000
    private const int PlacementsOffset = CountsOffset + 32;     // 0x4020
    private const int PlacementSize = 0x28;
    private const int PlacementCapacity = 64;
    private const int NameOffset = PlacementsOffset + PlacementCapacity * PlacementSize;  // 0x4A20
    private const int NameSize = 0x80;
    private const int ExtraCountsOffset = NameOffset + NameSize; // 0x4AA0
    private const int WaterBoxSize = 0x48;
    private const int WaterSize = 0x58;
    private const int ExternalMeshSize = 0x30;
    private const int ActorSize = 32;
    private const int BodySize = 20;

    /// <summary>A compound rounds its own sections up to sixteen, twice the container's own step.</summary>
    private const int TablePadding = 16;

    private const int InfoSection = 0;
    private const int ActorSection = 1;
    private const int HashSection = 2;
    private const int HashIndexSection = 3;
    private const int BodySection = 4;
    private const int PropertySection = 5;
    private const int PropertyFilterSection = 6;

    /// <summary>The first section a shape can be in. Everything below it is engine tables.</summary>
    public const int FirstShapeSection = 11;

    /// <summary>The container this was read from. Sections not modelled here are still in it.</summary>
    public PhiveFile Container { get; }

    /// <summary>The compound's name, which is the file's own name without its extensions.</summary>
    public string Name { get; set; } = "";

    /// <summary>The actor table, in the order the bodies index it.</summary>
    public List<CompoundActor> Actors { get; } = [];

    /// <summary>The body table.</summary>
    public List<CompoundBody> Bodies { get; } = [];

    /// <summary>The placement slots, all sixty four of them, mostly unused.</summary>
    public List<CompoundPlacement> Placements { get; } = [];

    /// <summary>
    /// The entity groups, as stored: the first of the two four byte tables at the front of section 0,
    /// read big endian. <see cref="EntityGroups"/> unpacks them.
    /// </summary>
    public List<uint> TagsA { get; } = [];

    /// <summary>The sensor groups, as stored: the second table, halfway through the block at entry 2048.</summary>
    public List<uint> TagsB { get; } = [];

    /// <summary>
    /// The rigid bodies the game makes for the entity bodies, one per layer and placement, in the order
    /// of the shape sections that hold them.
    /// </summary>
    public IEnumerable<CompoundGroup> EntityGroups => TagsA.Select(CompoundGroup.FromTag);

    /// <summary>The same for the sensor bodies, whose shape sections follow the entity groups'.</summary>
    public IEnumerable<CompoundGroup> SensorGroups => TagsB.Select(CompoundGroup.FromTag);

    /// <summary>
    /// How many entity bodies the body table holds - the ones whose <see cref="CompoundBody.IsSensor"/> is
    /// false. Adding a body means counting it here or in <see cref="KindBCount"/>.
    /// </summary>
    public int KindACount { get; set; }

    /// <summary>How many sensor bodies the body table holds.</summary>
    public int KindBCount { get; set; }

    /// <summary>How many entries the property tables in sections 5 and 6 hold.</summary>
    public int PropertyCount { get; set; }

    /// <summary>The second copy of <see cref="PropertyCount"/>, which the header also carries.</summary>
    public int PropertyCount2 { get; set; }

    /// <summary>How many records the box water section holds; see <see cref="WaterBoxes"/>.</summary>
    public int WaterBoxCount { get; set; }

    /// <summary>How many records the cylinder water section holds; see <see cref="WaterCylinders"/>.</summary>
    public int WaterCylinderCount { get; set; }

    /// <summary>How many records the quadrant water section holds; see <see cref="WaterQuadrants"/>.</summary>
    public int WaterQuadrantCount { get; set; }

    /// <summary>How many external meshes follow the water; see <see cref="ExternalMeshes"/>.</summary>
    public int ExternalMeshCount { get; set; }

    private StaticCompoundBody(PhiveFile container) => Container = container;

    /// <exception cref="InvalidDataException">The data is not a static compound body.</exception>
    public static StaticCompoundBody FromBinary(ReadOnlySpan<byte> data) => FromContainer(PhiveFile.FromBinary(data));

    public static StaticCompoundBody FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    /// <summary>Reads a compound out of a container that has already been parsed.</summary>
    /// <exception cref="InvalidDataException">The container does not hold a static compound.</exception>
    public static StaticCompoundBody FromContainer(PhiveFile container)
    {
        StaticCompoundBody compound = new(container);
        ReadOnlySpan<byte> info = container[InfoSection];

        if (info.Length < InfoSectionSize)
            throw new InvalidDataException(
                $"Section 0 is {info.Length} bytes; a static compound's is {InfoSectionSize}.");

        int kindA = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[CountsOffset..]);
        int kindB = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(CountsOffset + 4)..]);
        int tagsA = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(CountsOffset + 8)..]);
        int tagsB = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(CountsOffset + 12)..]);
        int actors = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(CountsOffset + 16)..]);
        int bodies = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(CountsOffset + 20)..]);

        compound.KindACount = kindA;
        compound.KindBCount = kindB;
        compound.PropertyCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(CountsOffset + 24)..]);
        compound.PropertyCount2 = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(CountsOffset + 28)..]);

        for (int i = 0; i < tagsA && i < TagTableCapacity; i++)
            compound.TagsA.Add(BinaryPrimitives.ReadUInt32BigEndian(info[(i * 4)..]));
        for (int i = 0; i < tagsB && i < TagTableCapacity; i++)
            compound.TagsB.Add(BinaryPrimitives.ReadUInt32BigEndian(info[(TagTableSize + i * 4)..]));

        for (int i = 0; i < PlacementCapacity; i++)
        {
            ReadOnlySpan<byte> entry = info.Slice(PlacementsOffset + i * PlacementSize, PlacementSize);
            compound.Placements.Add(new CompoundPlacement
            {
                Translate = ReadVector3(entry),
                Rotate = ReadVector3(entry[12..]),
                Id = BinaryPrimitives.ReadUInt16LittleEndian(entry[24..]),
                WaterBoxCount = BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]),
                WaterQuadrantCount = BinaryPrimitives.ReadUInt16LittleEndian(entry[28..]),
                WaterCylinderCount = BinaryPrimitives.ReadUInt16LittleEndian(entry[30..]),
                Hash = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]),
            });
        }

        int nul = info.Slice(NameOffset, NameSize).IndexOf((byte)0);
        compound.Name = Encoding.UTF8.GetString(info.Slice(NameOffset, nul < 0 ? NameSize : nul));
        compound.WaterBoxCount = BinaryPrimitives.ReadInt32LittleEndian(info[ExtraCountsOffset..]);
        compound.WaterCylinderCount = BinaryPrimitives.ReadInt32LittleEndian(info[(ExtraCountsOffset + 4)..]);
        compound.WaterQuadrantCount = BinaryPrimitives.ReadInt32LittleEndian(info[(ExtraCountsOffset + 8)..]);
        compound.ExternalMeshCount = BinaryPrimitives.ReadInt32LittleEndian(info[(ExtraCountsOffset + 12)..]);

        ReadOnlySpan<byte> actorTable = container[ActorSection];
        if (actorTable.Length < actors * ActorSize)
            throw new InvalidDataException(
                $"Section 1 holds {actorTable.Length} bytes, too few for the {actors} actors section 0 counts.");

        for (int i = 0; i < actors; i++)
        {
            ReadOnlySpan<byte> entry = actorTable.Slice(i * ActorSize, ActorSize);
            compound.Actors.Add(new CompoundActor
            {
                FirstBody = BinaryPrimitives.ReadInt32LittleEndian(entry),
                LastBody = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]),
                Hash = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]),
                SrtHash = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]),
                Reserved = entry[20..32].ToArray(),
            });
        }

        ReadOnlySpan<byte> bodyTable = container[BodySection];
        if (bodyTable.Length < bodies * BodySize)
            throw new InvalidDataException(
                $"Section 4 holds {bodyTable.Length} bytes, too few for the {bodies} bodies section 0 counts.");

        for (int i = 0; i < bodies; i++)
        {
            ReadOnlySpan<byte> entry = bodyTable.Slice(i * BodySize, BodySize);
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            compound.Bodies.Add(new CompoundBody(
                BinaryPrimitives.ReadUInt32LittleEndian(entry),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]),
                (int)(key & 0xFFFF),
                (int)(key >> 16),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[16..])));
        }

        return compound;
    }

    /// <summary>
    /// Writes the modelled sections back into the container and serialises it. The hash map over
    /// the actor table is rebuilt from the actors, so adding or removing one is enough.
    /// </summary>
    public byte[] ToBinary()
    {
        Container.SetSection(InfoSection, Padded(BuildInfo()));
        Container.SetSection(ActorSection, Padded(BuildActors()));
        BuildHashMap(out byte[] hashes, out byte[] indices);
        Container.SetSection(HashSection, Padded(hashes));
        Container.SetSection(HashIndexSection, Padded(indices));
        Container.SetSection(BodySection, Padded(BuildBodies()));

        return Container.ToBinary();
    }

    public void WriteTo(string path) => File.WriteAllBytes(path, ToBinary());

    /// <summary>The actor with a Banc hash, or null when this compound holds none of its collision.</summary>
    /// <remarks>
    /// This is a scan. The file's own hash map exists because the engine looks actors up by hash
    /// every frame; nothing here needs to, and a map that has to be kept in step with an editable
    /// list is a bug waiting to happen.
    /// </remarks>
    public CompoundActor? FindActor(ulong hash) => Actors.FirstOrDefault(a => a.Hash == hash);

    /// <summary>The bodies belonging to an actor.</summary>
    public IEnumerable<CompoundBody> BodiesOf(CompoundActor actor)
    {
        if (!actor.HasBodies) yield break;
        for (int i = actor.FirstBody; i <= actor.LastBody && i < Bodies.Count; i++) yield return Bodies[i];
    }

    /// <summary>The tile's water, from sections 7 to 10, or null for a compound that carries none.</summary>
    public PhiveWater? Water => PhiveWater.FromContainer(Container);

    /// <summary>The first section after the groups' shapes, where the water and external meshes start, in that order.</summary>
    private int ExtraSection => FirstShapeSection + TagsA.Count + TagsB.Count;

    /// <summary>Water the game makes a height field box of.</summary>
    public IReadOnlyList<CompoundWater> WaterBoxes => ReadWater(ExtraSection, WaterBoxCount, WaterBoxSize);

    /// <summary>Cylinders of water or sand, which the game makes whirlpools and quicksand pits of.</summary>
    public IReadOnlyList<CompoundWater> WaterCylinders =>
        ReadWater(ExtraSection + (WaterBoxCount > 0 ? 1 : 0), WaterCylinderCount, WaterSize);

    /// <summary>Water the game makes a height field box, a height field quadrant and a box of - and one box more for lava.</summary>
    public IReadOnlyList<CompoundWater> WaterQuadrants =>
        ReadWater(ExtraSection + (WaterBoxCount > 0 ? 1 : 0) + (WaterCylinderCount > 0 ? 1 : 0), WaterQuadrantCount, WaterSize);

    /// <summary>
    /// Meshes kept outside the compound's own shapes, each a nested <c>.bphsh</c> the game names
    /// <c>&lt;compound&gt;_ExternalMesh_&lt;n&gt;</c>. Every field tile has one: its ground.
    /// </summary>
    public IReadOnlyList<CompoundExternalMesh> ExternalMeshes
    {
        get
        {
            if (ExternalMeshCount <= 0) return [];
            int table = ExtraSection + (WaterBoxCount > 0 ? 1 : 0) + (WaterCylinderCount > 0 ? 1 : 0) + (WaterQuadrantCount > 0 ? 1 : 0);
            ReadOnlySpan<byte> data = Container[table];
            List<CompoundExternalMesh> meshes = [];
            for (int i = 0; i < ExternalMeshCount && (i + 1) * ExternalMeshSize <= data.Length; i++)
            {
                ReadOnlySpan<byte> entry = data.Slice(i * ExternalMeshSize, ExternalMeshSize);
                meshes.Add(new CompoundExternalMesh(ReadVector3(entry), ReadVector3(entry[12..]), ReadVector3(entry[24..]), table + 1 + i));
            }
            return meshes;
        }
    }

    private List<CompoundWater> ReadWater(int section, int count, int size)
    {
        if (count <= 0) return [];
        ReadOnlySpan<byte> data = Container[section];
        List<CompoundWater> water = [];
        for (int i = 0; i < count && (i + 1) * size <= data.Length; i++)
        {
            ReadOnlySpan<byte> entry = data.Slice(i * size, size);
            float[] parameters = new float[(size - WaterBoxSize) / 4];
            for (int k = 0; k < parameters.Length; k++) parameters[k] = BinaryPrimitives.ReadSingleLittleEndian(entry[(WaterBoxSize + k * 4)..]);
            water.Add(new CompoundWater(
                BinaryPrimitives.ReadUInt16LittleEndian(entry),
                BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]),
                BinaryPrimitives.ReadSingleLittleEndian(entry[4..]),
                ReadVector3(entry[12..]),
                ReadVector3(entry[24..]),
                ReadVector3(entry[36..]),
                BinaryPrimitives.ReadInt32LittleEndian(entry[48..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[56..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[64..]),
                parameters));
        }
        return water;
    }

    /// <summary>
    /// A body's material palette, from the property tables in sections 5 and 6. The mesh shape the
    /// body is an instance of tags its primitives with indices into this.
    /// </summary>
    /// <exception cref="InvalidDataException">The body's run is past the end of the tables.</exception>
    public List<PhiveMaterial> MaterialsOf(CompoundBody body) =>
        PhiveMaterial.Read(Container[PropertySection], Container[PropertyFilterSection], body.FirstProperty, body.PropertyCount);

    /// <summary>The sections holding shapes, whether as tagfiles or as nested containers.</summary>
    public IEnumerable<int> ShapeSections
    {
        get
        {
            for (int i = FirstShapeSection; i < Container.Sections.Count; i++)
            {
                ReadOnlySpan<byte> section = Container[i];
                if (section.Length > 0 && (HkTagfile.IsTagfile(section) || PhiveFile.IsPhive(section))) yield return i;
            }
        }
    }

    /// <summary>
    /// The Havok shape in a section, reaching through a nested container when there is one.
    /// </summary>
    /// <remarks>
    /// A compound shape sits in its section as a bare tagfile. A mesh shape is wrapped in a
    /// version 1 container first, and its tagfile is that container's section 0.
    /// </remarks>
    public HkTagfile? GetShape(int section)
    {
        ReadOnlySpan<byte> data = Container[section];
        if (HkTagfile.IsTagfile(data)) return HkTagfile.FromBinary(data);

        if (PhiveFile.IsPhive(data))
        {
            PhiveFile nested = PhiveFile.FromBinary(data);
            foreach (byte[] inner in nested.Sections)
                if (HkTagfile.IsTagfile(inner)) return HkTagfile.FromBinary(inner);
        }

        return null;
    }

    /// <summary>Puts an edited shape back in the section it came from.</summary>
    /// <exception cref="InvalidOperationException">The section holds no shape.</exception>
    public void SetShape(int section, HkTagfile shape)
    {
        ReadOnlySpan<byte> data = Container[section];

        if (HkTagfile.IsTagfile(data))
        {
            Container.SetSection(section, Padded(HkTagfileWriter.Write(shape)));
            return;
        }

        if (PhiveFile.IsPhive(data))
        {
            PhiveFile nested = PhiveFile.FromBinary(data);
            for (int i = 0; i < nested.Sections.Count; i++)
            {
                if (!HkTagfile.IsTagfile(nested.Sections[i])) continue;
                nested.SetSection(i, Padded(HkTagfileWriter.Write(shape)));
                Container.SetSection(section, nested.ToBinary());
                return;
            }
        }

        throw new InvalidOperationException($"Section {section} does not hold a shape.");
    }

    private byte[] BuildInfo()
    {
        byte[] info = new byte[InfoSectionSize];
        Span<byte> span = info;

        // Both tables are filled with the unused marker first; the shipped files do the same.
        for (int i = 0; i < TagTableCapacity * 2; i++)
            BinaryPrimitives.WriteUInt32BigEndian(span[(i * 4)..], 0xFF);

        for (int i = 0; i < TagsA.Count; i++)
            BinaryPrimitives.WriteUInt32BigEndian(span[(i * 4)..], TagsA[i]);
        for (int i = 0; i < TagsB.Count; i++)
            BinaryPrimitives.WriteUInt32BigEndian(span[(TagTableSize + i * 4)..], TagsB[i]);

        BinaryPrimitives.WriteUInt32LittleEndian(span[CountsOffset..], (uint)KindACount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(CountsOffset + 4)..], (uint)KindBCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(CountsOffset + 8)..], (uint)TagsA.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(CountsOffset + 12)..], (uint)TagsB.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(CountsOffset + 16)..], (uint)Actors.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(CountsOffset + 20)..], (uint)Bodies.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(CountsOffset + 24)..], (uint)PropertyCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(CountsOffset + 28)..], (uint)PropertyCount2);

        for (int i = 0; i < PlacementCapacity && i < Placements.Count; i++)
        {
            CompoundPlacement placement = Placements[i];
            Span<byte> entry = span.Slice(PlacementsOffset + i * PlacementSize, PlacementSize);
            WriteVector3(entry, placement.Translate);
            WriteVector3(entry[12..], placement.Rotate);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[24..], (ushort)placement.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], (ushort)placement.WaterBoxCount);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[28..], (ushort)placement.WaterQuadrantCount);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[30..], (ushort)placement.WaterCylinderCount);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], placement.Hash);
        }

        byte[] name = Encoding.UTF8.GetBytes(Name);
        if (name.Length >= NameSize)
            throw new InvalidOperationException($"The name is {name.Length} bytes; the field holds {NameSize - 1}.");
        name.CopyTo(span[NameOffset..]);

        BinaryPrimitives.WriteInt32LittleEndian(span[ExtraCountsOffset..], WaterBoxCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[(ExtraCountsOffset + 4)..], WaterCylinderCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[(ExtraCountsOffset + 8)..], WaterQuadrantCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[(ExtraCountsOffset + 12)..], ExternalMeshCount);
        return info;
    }

    private byte[] BuildActors()
    {
        byte[] table = new byte[Actors.Count * ActorSize];
        Span<byte> span = table;

        for (int i = 0; i < Actors.Count; i++)
        {
            CompoundActor actor = Actors[i];
            Span<byte> entry = span.Slice(i * ActorSize, ActorSize);
            BinaryPrimitives.WriteInt32LittleEndian(entry, actor.FirstBody);
            BinaryPrimitives.WriteInt32LittleEndian(entry[4..], actor.LastBody);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], actor.Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], actor.SrtHash);
            actor.Reserved.AsSpan(0, Math.Min(actor.Reserved.Length, 12)).CopyTo(entry[20..]);
        }

        return table;
    }

    private byte[] BuildBodies()
    {
        byte[] table = new byte[Bodies.Count * BodySize];
        Span<byte> span = table;

        for (int i = 0; i < Bodies.Count; i++)
        {
            CompoundBody body = Bodies[i];
            Span<byte> entry = span.Slice(i * BodySize, BodySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, body.Index);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], body.Info);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..],
                (uint)(body.ActorIndex & 0xFFFF) | ((uint)body.Instance << 16));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)body.FirstProperty);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], (uint)body.PropertyCount);
        }

        return table;
    }

    /// <summary>
    /// Builds the two tables the engine looks actors up through: hashes in one, the index of the
    /// actor each hash belongs to in the other.
    /// </summary>
    /// <remarks>
    /// It is an open addressed map with twice as many slots as there are actors, a slot of
    /// <c>hash % slots</c>, and linear probing forward from there. Insertion follows the actor
    /// table's own order, which is what makes the result reproducible: rebuilding the map for an
    /// untouched compound gives back the bytes that were in the file.
    /// </remarks>
    private void BuildHashMap(out byte[] hashes, out byte[] indices)
    {
        if (Actors.Count == 0)
        {
            hashes = [];
            indices = [];
            return;
        }

        int slots = Actors.Count * 2;
        ulong[] table = new ulong[slots];
        ushort[] owners = new ushort[slots];

        for (int i = 0; i < Actors.Count; i++)
        {
            ulong hash = Actors[i].Hash;
            if (hash == 0)
                throw new InvalidOperationException(
                    $"Actor {i} has a zero hash, which the map cannot tell from an empty slot.");

            int slot = (int)(hash % (ulong)slots);
            int guard = 0;
            while (table[slot] != 0)
            {
                if (table[slot] == hash)
                    throw new InvalidOperationException($"Two actors share the hash {hash:X16}.");
                slot = (slot + 1) % slots;
                if (++guard > slots) throw new InvalidOperationException("The actor hash map is full.");
            }

            table[slot] = hash;
            owners[slot] = (ushort)i;
        }

        hashes = new byte[slots * 8];
        indices = new byte[slots * 2];

        for (int i = 0; i < slots; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(hashes.AsSpan(i * 8), table[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(i * 2), owners[i]);
        }
    }

    /// <summary>
    /// Rounds a section out to the container's alignment. A compound stores its table sections
    /// at their padded length rather than their exact one, and a file written the other way
    /// would differ from the shipped ones by a handful of size fields.
    /// </summary>
    private static byte[] Padded(byte[] data)
    {
        int padded = (data.Length + TablePadding - 1) & ~(TablePadding - 1);
        if (padded == data.Length) return data;

        byte[] output = new byte[padded];
        data.CopyTo(output, 0);
        return output;
    }

    private static Vector3 ReadVector3(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadSingleLittleEndian(data),
        BinaryPrimitives.ReadSingleLittleEndian(data[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(data[8..]));

    private static void WriteVector3(Span<byte> data, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(data, value.X);
        BinaryPrimitives.WriteSingleLittleEndian(data[4..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data[8..], value.Z);
    }
}
