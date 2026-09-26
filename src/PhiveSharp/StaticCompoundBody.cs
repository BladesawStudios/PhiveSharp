using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using PhiveSharp.Havok;

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
    /// The twelve bytes after the hashes. They are zero in all but a handful of the shipped
    /// files, and what the exceptions mean is not known, so they are carried through.
    /// </summary>
    public byte[] Reserved { get; set; } = new byte[12];

    public bool HasBodies => FirstBody >= 0;

    public override string ToString() => $"{Hash:X16} bodies {FirstBody}..{LastBody}";
}

/// <summary>
/// One entry of the body table: a piece of collision, the actor it came from, and the run of
/// property entries it uses.
/// </summary>
/// <param name="Index">The entry's own position in the table, as stored.</param>
/// <param name="Info">
/// A word that is nearly constant within a file - 0x1000800 and 0x2000b00 are the common values
/// - and looks like a description of the body kind. Not decoded.
/// </param>
/// <param name="ActorIndex">
/// The owning actor, indexing <see cref="StaticCompoundBody.Actors"/>. It always agrees with
/// that actor's <see cref="CompoundActor.FirstBody"/> range, so the link can be followed either way.
/// </param>
/// <param name="Slot">
/// The body's place within its actor's run, restarting when <paramref name="FirstProperty"/>
/// moves to a new group.
/// </param>
/// <param name="FirstProperty">
/// Where this body's entries start in the property tables - sections 5 and 6, one sixteen byte
/// and one eight byte entry each. Runs may overlap: two bodies can share properties.
/// </param>
/// <param name="PropertyCount">How many property entries the body uses.</param>
public readonly record struct CompoundBody(
    uint Index, uint Info, int ActorIndex, int Slot, int FirstProperty, int PropertyCount);

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

    /// <summary>An identifier for the placement; 0xFFFF in an unused slot.</summary>
    public uint Id { get; set; } = Empty;

    /// <summary>The four bytes after the id, always zero in the shipped files.</summary>
    public uint Reserved { get; set; }

    /// <summary>The Banc hash of the actor this compound was placed as, or zero.</summary>
    public ulong Hash { get; set; }

    /// <summary>The id an unused slot carries.</summary>
    public const uint Empty = 0xFFFF;

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
/// <item>section 0, a fixed 19,120 byte block: the counts, the placements, and the name;</item>
/// <item>section 1, the actor table;</item>
/// <item>sections 2 and 3, the hash map over it, which are rebuilt on write;</item>
/// <item>section 4, the body table;</item>
/// <item>sections 5 and 6, per body properties, carried through as bytes;</item>
/// <item>section 7, eighty bytes that are zero except in field tiles;</item>
/// <item>sections 8 to 10, present only in field tiles, half a megabyte of terrain collision;</item>
/// <item>sections 11 and up, the shapes: Havok tagfiles and nested containers.</item>
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
    private const int NameSize = 0x60;
    private const int TrailerOffset = NameOffset + NameSize;    // 0x4A80
    private const int TrailerSize = InfoSectionSize - TrailerOffset;
    private const int ActorSize = 32;
    private const int BodySize = 20;

    /// <summary>A compound rounds its own sections up to sixteen, twice the container's own step.</summary>
    private const int TablePadding = 16;

    private const int InfoSection = 0;
    private const int ActorSection = 1;
    private const int HashSection = 2;
    private const int HashIndexSection = 3;
    private const int BodySection = 4;

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
    /// The first of the two four byte tables at the front of section 0, read big endian, which
    /// is how the small values it holds come out as small numbers.
    /// </summary>
    /// <remarks>
    /// Every file has a handful of entries - 2, 8, 9, 10, 11, 23, 26, 28 - and what they select
    /// is not known. They are kept so the section can be written back.
    /// </remarks>
    public List<uint> TagsA { get; } = [];

    /// <summary>The second table, which starts halfway through the block at entry 2048.</summary>
    public List<uint> TagsB { get; } = [];

    /// <summary>
    /// How many bodies are of the first kind. The body table holds these and then
    /// <see cref="KindBCount"/> of a second kind; what separates them is not known, so adding
    /// bodies means saying which kind they are by hand.
    /// </summary>
    public int KindACount { get; set; }

    /// <summary>How many bodies are of the second kind.</summary>
    public int KindBCount { get; set; }

    /// <summary>How many entries the property tables in sections 5 and 6 hold.</summary>
    public int PropertyCount { get; set; }

    /// <summary>The second copy of <see cref="PropertyCount"/>, which the header also carries.</summary>
    public int PropertyCount2 { get; set; }

    /// <summary>The forty eight bytes at the end of section 0. Flags of some sort; not decoded.</summary>
    public byte[] Trailer { get; set; } = new byte[TrailerSize];

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
                Id = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]),
                Reserved = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]),
                Hash = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]),
            });
        }

        int nul = info.Slice(NameOffset, NameSize).IndexOf((byte)0);
        compound.Name = Encoding.UTF8.GetString(info.Slice(NameOffset, nul < 0 ? NameSize : nul));
        compound.Trailer = info.Slice(TrailerOffset, TrailerSize).ToArray();

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

    /// <summary>The sections holding shapes, whether as tagfiles or as nested containers.</summary>
    public IEnumerable<int> ShapeSections
    {
        get
        {
            for (int i = FirstShapeSection; i < Container.Sections.Count; i++)
            {
                ReadOnlySpan<byte> section = Container[i];
                if (section.Length > 0 && (TagFile.IsTagFile(section) || PhiveFile.IsPhive(section))) yield return i;
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
    public TagFile? GetShape(int section)
    {
        ReadOnlySpan<byte> data = Container[section];
        if (TagFile.IsTagFile(data)) return TagFile.FromBinary(data);

        if (PhiveFile.IsPhive(data))
        {
            PhiveFile nested = PhiveFile.FromBinary(data);
            foreach (byte[] inner in nested.Sections)
                if (TagFile.IsTagFile(inner)) return TagFile.FromBinary(inner);
        }

        return null;
    }

    /// <summary>Puts an edited shape back in the section it came from.</summary>
    /// <exception cref="InvalidOperationException">The section holds no shape.</exception>
    public void SetShape(int section, TagFile shape)
    {
        ReadOnlySpan<byte> data = Container[section];

        if (TagFile.IsTagFile(data))
        {
            Container.SetSection(section, Padded(shape.ToBinary()));
            return;
        }

        if (PhiveFile.IsPhive(data))
        {
            PhiveFile nested = PhiveFile.FromBinary(data);
            for (int i = 0; i < nested.Sections.Count; i++)
            {
                if (!TagFile.IsTagFile(nested.Sections[i])) continue;
                nested.SetSection(i, Padded(shape.ToBinary()));
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
            BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], placement.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], placement.Reserved);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], placement.Hash);
        }

        byte[] name = Encoding.UTF8.GetBytes(Name);
        if (name.Length >= NameSize)
            throw new InvalidOperationException($"The name is {name.Length} bytes; the field holds {NameSize - 1}.");
        name.CopyTo(span[NameOffset..]);

        Trailer.AsSpan(0, Math.Min(Trailer.Length, TrailerSize)).CopyTo(span[TrailerOffset..]);
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
                (uint)(body.ActorIndex & 0xFFFF) | ((uint)body.Slot << 16));
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
