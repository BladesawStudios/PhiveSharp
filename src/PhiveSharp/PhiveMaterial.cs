using System.Buffers.Binary;

namespace PhiveSharp;

/// <summary>
/// One entry of a material palette: what a mesh primitive's shape tag stands for. A shape keeps its
/// palette in sections 1 and 2; a static compound keeps every body's in sections 5 and 6, and a body
/// names its run of them.
/// </summary>
/// <param name="Material">The material, sixteen bytes into the palette entry with the next two.</param>
/// <param name="SubMaterial">The sub material.</param>
/// <param name="Flags">Collision flags, 0x8000 on most entries.</param>
/// <param name="Filter">The collision filter, from the separate eight byte table.</param>
public readonly record struct PhiveMaterial(int Material, int SubMaterial, ulong Flags, ulong Filter)
{
    internal const int Size = 16;
    internal const int FilterSize = 8;

    internal static List<PhiveMaterial> Read(ReadOnlySpan<byte> entries, ReadOnlySpan<byte> filters, int first, int count)
    {
        if (first < 0 || count < 0 || (first + count) * Size > entries.Length || (first + count) * FilterSize > filters.Length)
            throw new InvalidDataException($"Materials {first}..{first + count} run past the palette.");

        List<PhiveMaterial> result = new(count);
        for (int i = first; i < first + count; i++)
            result.Add(new PhiveMaterial(
                BinaryPrimitives.ReadInt32LittleEndian(entries[(i * Size)..]),
                BinaryPrimitives.ReadInt32LittleEndian(entries[(i * Size + 4)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entries[(i * Size + 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(filters[(i * FilterSize)..])));
        return result;
    }

    /// <summary>Both tables, each rounded up to sixteen bytes the way the shipped files are.</summary>
    internal static (byte[] Entries, byte[] Filters) Write(IReadOnlyList<PhiveMaterial> materials)
    {
        byte[] entries = new byte[materials.Count * Size];
        byte[] filters = new byte[(materials.Count * FilterSize + 15) & ~15];
        for (int i = 0; i < materials.Count; i++)
        {
            PhiveMaterial m = materials[i];
            BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(i * Size), m.Material);
            BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(i * Size + 4), m.SubMaterial);
            BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(i * Size + 8), m.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(filters.AsSpan(i * FilterSize), m.Filter);
        }
        return (entries, filters);
    }
}
