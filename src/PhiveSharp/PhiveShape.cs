using HkxSharp;

namespace PhiveSharp;

/// <summary>
/// A <c>.bphsh</c>: a Havok shape in section 0 and the material palette its shape tags index in
/// sections 1 and 2.
/// </summary>
public sealed class PhiveShape
{
    private const int ShapeSection = 0;
    private const int MaterialSection = 1;
    private const int FilterSection = 2;

    /// <summary>The container this was read from. Sections not modelled here are still in it.</summary>
    public PhiveFile Container { get; }

    /// <summary>The palette. A mesh primitive's shape tag is an index into it.</summary>
    public List<PhiveMaterial> Materials { get; } = [];

    private PhiveShape(PhiveFile container) => Container = container;

    /// <exception cref="InvalidDataException">The data is not a Phive shape.</exception>
    public static PhiveShape FromBinary(ReadOnlySpan<byte> data) => FromContainer(PhiveFile.FromBinary(data));

    public static PhiveShape FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    /// <exception cref="InvalidDataException">The container does not hold a shape.</exception>
    public static PhiveShape FromContainer(PhiveFile container)
    {
        if (!HkTagfile.IsTagfile(container[ShapeSection]))
            throw new InvalidDataException("Section 0 does not hold a Havok tagfile.");

        PhiveShape shape = new(container);
        ReadOnlySpan<byte> entries = container[MaterialSection];
        shape.Materials.AddRange(PhiveMaterial.Read(entries, container[FilterSection], 0, entries.Length / PhiveMaterial.Size));
        return shape;
    }

    /// <summary>The Havok shape, parsed fresh; put it back with <see cref="SetShape"/>.</summary>
    public HkTagfile GetShape() => HkTagfile.FromBinary(Container[ShapeSection]);

    public void SetShape(HkTagfile shape) => Container.SetSection(ShapeSection, HkTagfileWriter.Write(shape));

    public byte[] ToBinary()
    {
        (byte[] entries, byte[] filters) = PhiveMaterial.Write(Materials);
        Container.SetSection(MaterialSection, entries);
        Container.SetSection(FilterSection, filters);
        return Container.ToBinary();
    }

    public void WriteTo(string path) => File.WriteAllBytes(path, ToBinary());
}
