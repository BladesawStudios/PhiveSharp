using System.Buffers.Binary;

namespace PhiveSharp;
public sealed class PhiveFile
{
    private static ReadOnlySpan<byte> Magic => "Phive\0"u8;

    /// <summary>Byte order mark, read little endian. A big endian file would read 0xFFFE.</summary>
    public const ushort ByteOrderMark = 0xFEFF;

    /// <summary>Sections start on an eight byte boundary.</summary>
    public const int Alignment = 8;

    /// <summary>The header is padded out to sixteen, whatever the slot count works out to.</summary>
    public const int HeaderAlignment = 16;

    private const int MagicLength = 6;
    private const int TableOffset = 0x0C;

    /// <summary>
    /// The container version. Static compounds are version 2; shapes, navmeshes and the
    /// containers nested inside a static compound are version 1.
    /// </summary>
    public int Version { get; set; } = 2;

    /// <summary>
    /// The byte in front of the slot count. It goes with the resource type - 2 for static
    /// compounds, 1 for navmeshes, 0 for shapes and for nested containers - and what it
    /// selects is not known, so it is carried through unchanged.
    /// </summary>
    public byte Kind { get; set; } = 2;

    /// <summary>
    /// How many section slots the header has room for: 96 in a static compound, 4 in a shape,
    /// 3 in a navmesh. <see cref="Sections"/> may be shorter than this but not longer.
    /// </summary>
    /// <remarks>
    /// The table is written at full capacity however little of it is used, so this fixes the
    /// header size at <c>align(0x0C + SlotCount * 8, 16)</c> - 0x310 bytes for a static compound.
    /// </remarks>
    public int SlotCount { get; set; } = 96;

    /// <summary>
    /// The declared sections, in slot order. An entry may be empty, which is not the same as
    /// the slot not being there: a file declares a run of slots, and anything past the end of
    /// that run is absent.
    /// </summary>
    /// <remarks>
    /// Files declare a slot or two past the last one they fill, so a round trip keeps the
    /// length the list was read with rather than trimming to the last non-empty section.
    /// </remarks>
    public List<byte[]> Sections { get; } = [];

    /// <summary>The bytes the header occupies, its padding included, at the current slot count.</summary>
    public int HeaderSize
    {
        get
        {
            int size = TableOffset + SlotCount * 8;
            return (size + HeaderAlignment - 1) & ~(HeaderAlignment - 1);
        }
    }

    /// <summary>True when <paramref name="data"/> starts with a Phive header.</summary>
    public static bool IsPhive(ReadOnlySpan<byte> data) =>
        data.Length >= TableOffset && data[..MagicLength].SequenceEqual(Magic);

    /// <exception cref="InvalidDataException">The data is not a Phive container this can read.</exception>
    public static PhiveFile FromBinary(ReadOnlySpan<byte> data)
    {
        if (!IsPhive(data))
            throw new InvalidDataException("Not a Phive container: expected a Phive magic.");

        ushort bom = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (bom != ByteOrderMark)
            throw new InvalidDataException(
                $"Unexpected byte order mark 0x{bom:X4}; only little endian containers are supported.");

        PhiveFile file = new()
        {
            Version = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
            Kind = data[10],
            SlotCount = data[11],
        };

        int header = file.HeaderSize;
        if (data.Length < header) throw new InvalidDataException("Truncated Phive header.");

        int sizeTable = TableOffset + file.SlotCount * 4;
        int end = header;

        for (int i = 0; i < file.SlotCount; i++)
        {
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(TableOffset + i * 4)..]);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(sizeTable + i * 4)..]);

            // A zero offset ends the declared run. An empty section in the middle still
            // carries the offset of whatever comes after it, so zero is unambiguous.
            if (offset == 0) break;

            if (offset != end)
                throw new InvalidDataException(
                    $"Section {i} starts at 0x{offset:X} where 0x{end:X} was expected; " +
                    "sections are laid end to end.");
            if (size < 0 || (size > 0 && offset + size > data.Length))
                throw new InvalidDataException($"Section {i} runs past the end of the data.");

            // The last declared slot is often empty and sits at the aligned position after the
            // end of the file, which is a slot that is not there rather than a broken offset.
            file.Sections.Add(size > 0 ? data.Slice(offset, size).ToArray() : []);
            end = Align(offset + size);
        }

        return file;
    }

    public static PhiveFile FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    /// <summary>
    /// Serialises the container. A section is written at the length it has here, and the next one
    /// starts on the following <see cref="Alignment"/> boundary, which is how the shipped files
    /// are laid out - so a container that has not been touched writes back byte for byte.
    ///
    /// Static compounds round their sections up to sixteen before storing them, and that padding
    /// is part of the section as read. Keep it if you replace one.
    /// </summary>
    /// <exception cref="InvalidOperationException">There are more sections than slots.</exception>
    public byte[] ToBinary()
    {
        if (Sections.Count > SlotCount)
            throw new InvalidOperationException(
                $"{Sections.Count} sections will not fit in {SlotCount} slots; raise {nameof(SlotCount)}.");

        // Each section starts on a boundary, but the file ends where its last non-empty section
        // does: a trailing empty slot carries the aligned offset after it, which can be past the
        // end of the file.
        int header = HeaderSize;
        int position = header;
        int total = header;

        foreach (byte[] section in Sections)
        {
            if (section.Length > 0) total = position + section.Length;
            position = Align(position + section.Length);
        }

        byte[] output = new byte[total];
        Span<byte> span = output;

        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], ByteOrderMark);
        span[10] = Kind;
        span[11] = (byte)SlotCount;

        int sizeTable = TableOffset + SlotCount * 4;
        int offset = header;

        for (int i = 0; i < Sections.Count; i++)
        {
            byte[] section = Sections[i];
            BinaryPrimitives.WriteUInt32LittleEndian(span[(TableOffset + i * 4)..], (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(sizeTable + i * 4)..], (uint)section.Length);
            if (section.Length > 0) section.CopyTo(span[offset..]);
            offset = Align(offset + section.Length);
        }

        return output;
    }

    public void WriteTo(string path) => File.WriteAllBytes(path, ToBinary());

    /// <summary>The bytes of a section, or an empty span when the slot is absent or empty.</summary>
    public ReadOnlySpan<byte> this[int index] =>
        index >= 0 && index < Sections.Count ? Sections[index] : default;

    /// <summary>Replaces a section, declaring any slots in front of it the file did not have.</summary>
    public void SetSection(int index, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);

        while (Sections.Count <= index) Sections.Add([]);
        Sections[index] = data.ToArray();
    }

    /// <summary>True when the section holds a container of its own.</summary>
    public bool IsNested(int index) => IsPhive(this[index]);

    /// <summary>
    /// The container nested in a section, or null when that section holds something else.
    /// </summary>
    /// <remarks>
    /// This is how a static compound carries its mesh shapes: each one is a whole version 1
    /// container sitting in a section of the version 2 one. What comes back is a copy - put it
    /// back with <see cref="SetSection"/> for changes to it to reach the file.
    /// </remarks>
    public PhiveFile? GetNested(int index) => IsNested(index) ? FromBinary(this[index]) : null;

    private static int Align(int value) => (value + Alignment - 1) & ~(Alignment - 1);
}
