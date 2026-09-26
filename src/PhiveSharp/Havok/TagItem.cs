namespace PhiveSharp.Havok;

public readonly record struct TagItem(int TypeIndex, int Flags, int Offset, int Count)
{
    public const int ObjectFlag = 0x10;

    public const int ArrayFlag = 0x20;

    public static TagItem Null => new(0, 0, 0, 0);

    public bool IsNull => TypeIndex == 0 && Count == 0 && Offset == 0;
}

public sealed record TagPatch(int TypeIndex, List<int> Offsets);
