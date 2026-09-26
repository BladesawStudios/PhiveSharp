namespace PhiveSharp.Havok;

public enum TagTypeKind
{
    Void = 0,
    Opaque = 1,
    Bool = 2,
    String = 3,
    Int = 4,
    Float = 5,

    Pointer = 6,

    Record = 7,

    Array = 8,
}

public readonly record struct TagTemplate(string Name, int Value)
{
    public bool IsType => Name.Length > 0 && Name[0] == 't';
}

public readonly record struct TagField(string Name, int Flags, int TupleSize, int Offset, int TypeIndex)
{
    public const int TupleFlag = 0x80;

    public bool IsTuple => (Flags & TupleFlag) != 0;
}

public sealed class TagType
{
    [Flags]
    public enum Presence
    {
        None = 0,
        Format = 0x1,
        SubType = 0x2,
        Version = 0x4,
        ByteSize = 0x8,
        Unknown = 0x10,
        Fields = 0x20,
        Interfaces = 0x40,
        Attribute = 0x80,
    }

    public string Name { get; set; } = "";

    public int ParentIndex { get; set; }

    public Presence Flags { get; set; }

    public int Format { get; set; }

    public int SubTypeIndex { get; set; }

    public int Version { get; set; }

    public int ByteSize { get; set; }

    public int AlignmentAndFlags { get; set; }

    public int Alignment => AlignmentAndFlags & 0xFF;

    public int Unknown { get; set; }

    public List<TagField> Fields { get; } = [];

    public List<TagTemplate> Templates { get; } = [];

    public List<(int TypeIndex, int Value)> Interfaces { get; } = [];

    public int Attribute { get; set; }

    public uint? Hash { get; set; }

    public bool HasBody { get; set; }

    public TagTypeKind Kind => (TagTypeKind)(Format & 0xF);

    public bool IsSigned => (Format & 0x200) != 0;

    public void UpdateFlags()
    {
        Presence flags = Presence.None;
        if (Format != 0) flags |= Presence.Format;
        if (SubTypeIndex != 0) flags |= Presence.SubType;
        if (Version != 0) flags |= Presence.Version;
        if (ByteSize != 0 || AlignmentAndFlags != 0) flags |= Presence.ByteSize;
        if (Unknown != 0) flags |= Presence.Unknown;
        if (Fields.Count > 0) flags |= Presence.Fields;
        if (Interfaces.Count > 0) flags |= Presence.Interfaces;
        if (Attribute != 0) flags |= Presence.Attribute;
        Flags = flags;
        HasBody = flags != Presence.None || ParentIndex != 0;
    }

    public override string ToString() => Name;
}
