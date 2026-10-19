using System.Buffers.Binary;
using System.Numerics;

namespace PhiveSharp;

/// <summary>
/// A field tile's water: the height field the game builds the tile's <c>_TeraWater</c> body from,
/// out of sections 7 to 10 of its static compound.
/// </summary>
/// <remarks>
/// Section 7 is the header: the tile's size and centre in world space, the grid, and the sizes of
/// the three sections after it. Section 8 is four shorts a sample on that grid, rows along x;
/// samples are <c>size / (count - 1)</c> apart, edge to edge. Section 9 is one short a sample.
/// Section 10 is the fluid palette a sample's type indexes, the same sixteen byte entries as a
/// material palette without the filter table - the same twelve in every tile.
///
/// The game makes the water a closed body: the surface from section 8 on top, the bottom from
/// section 9 underneath, both on the tile's height scale, over every grid cell whose four corners
/// are wet. A sample is dry when the low byte of its fourth short is 0x80 or more; the high byte
/// is the fluid, which is what each triangle of the body is tagged with. The collision, the
/// renderer's depth query and the water shaders all read the fourth short that way and no other,
/// so its low seven bits are not read by the game at all. Where no bottom was authored section 9
/// holds zero, the bottom of the tile's height range. The flow is what the renderer's water texture
/// keeps in the same two channels.
/// </remarks>
public sealed class PhiveWater
{
    private const int HeaderSize = 52;
    private const int DryBit = 0x80;

    public Vector3 Size { get; }
    public Vector3 Center { get; }
    public int Width { get; }
    public int Depth { get; }

    /// <summary>Section 8 as read: surface, flow x, flow z, and fluid, a short each, per sample.</summary>
    public ushort[] Samples { get; }

    /// <summary>Section 9 as read: the bottom of the water, one short per sample.</summary>
    public ushort[] Bottoms { get; }

    /// <summary>The fluids a sample's <see cref="TypeAt"/> indexes.</summary>
    public List<PhiveMaterial> Types { get; }

    private PhiveWater(Vector3 size, Vector3 center, int width, int depth, ushort[] samples, ushort[] bottoms, List<PhiveMaterial> types)
    {
        Size = size;
        Center = center;
        Width = width;
        Depth = depth;
        Samples = samples;
        Bottoms = bottoms;
        Types = types;
    }

    /// <summary>Reads sections 7 to 10, or gives null when the container does not carry them.</summary>
    /// <exception cref="InvalidDataException">The sections are there but disagree with their header.</exception>
    public static PhiveWater? FromContainer(PhiveFile container)
    {
        ReadOnlySpan<byte> header = container[7];
        if (header.Length < HeaderSize || container[8].IsEmpty) return null;

        Vector3 size = new(F(header, 0), F(header, 4), F(header, 8));
        Vector3 center = new(F(header, 12), F(header, 16), F(header, 20));
        int width = I(header, 24), depth = I(header, 28);
        ReadOnlySpan<byte> samples = container[8], bottoms = container[9], types = container[10];

        if (width < 2 || depth < 2 || I(header, 32) != samples.Length || samples.Length != width * depth * 8 ||
            I(header, 40) != bottoms.Length || bottoms.Length != width * depth * 2 || I(header, 48) != types.Length)
            throw new InvalidDataException("Sections 8 to 10 do not match the water header in section 7.");

        return new PhiveWater(size, center, width, depth, Shorts(samples), Shorts(bottoms),
            PhiveMaterial.Read(types, default, 0, types.Length / PhiveMaterial.Size));
    }

    private int Index(int x, int z)
    {
        if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)z >= (uint)Depth) throw new ArgumentOutOfRangeException(nameof(z));
        return z * Width + x;
    }

    /// <summary>Where a sample is, in world x and z.</summary>
    public Vector2 PositionOf(int x, int z) => new(
        Center.X - Size.X / 2 + x * Size.X / (Width - 1),
        Center.Z - Size.Z / 2 + z * Size.Z / (Depth - 1));

    /// <summary>A short of this tile's height scale, in world metres.</summary>
    public float Height(ushort raw) => raw / 65535f * Size.Y + Center.Y - Size.Y / 2;

    /// <summary>The water surface at a sample, in world metres.</summary>
    public float SurfaceAt(int x, int z) => Height(Samples[Index(x, z) * 4]);

    /// <summary>The bottom of the water at a sample, in world metres.</summary>
    public float BottomAt(int x, int z) => Height(Bottoms[Index(x, z)]);

    /// <summary>The flow at a sample, each axis from -1 to 1.</summary>
    public Vector2 FlowAt(int x, int z)
    {
        int i = Index(x, z) * 4;
        return new(Samples[i + 1] / 65535f * 2 - 1, Samples[i + 2] / 65535f * 2 - 1);
    }

    /// <summary>The sample's fluid, an index into <see cref="Types"/>.</summary>
    public int TypeAt(int x, int z) => Samples[Index(x, z) * 4 + 3] >> 8;

    public PhiveMaterial? FluidAt(int x, int z) => TypeAt(x, z) is var t && t < Types.Count ? Types[t] : null;

    /// <summary>Whether the sample has water: the low byte of its fourth short is under 0x80.</summary>
    public bool IsWet(int x, int z) => (Samples[Index(x, z) * 4 + 3] & DryBit) == 0;

    /// <summary>
    /// Whether the grid cell from sample (x, z) to (x + 1, z + 1) holds water, which the game takes to
    /// mean all four corners are wet.
    /// </summary>
    public bool CellHasWater(int x, int z) =>
        x + 1 < Width && z + 1 < Depth && IsWet(x, z) && IsWet(x + 1, z) && IsWet(x, z + 1) && IsWet(x + 1, z + 1);

    /// <summary>The low seven bits of the sample's fourth short, which the game does not read.</summary>
    public int UnreadBitsAt(int x, int z) => Samples[Index(x, z) * 4 + 3] & 0x7F;

    private static float F(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s[at..]);

    private static int I(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);

    private static ushort[] Shorts(ReadOnlySpan<byte> s)
    {
        var r = new ushort[s.Length / 2];
        for (int i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadUInt16LittleEndian(s[(i * 2)..]);
        return r;
    }
}
