using System.Buffers.Binary;

namespace PhiveSharp.Havok;

public sealed partial class TagFile
{
    /// <summary>
    /// Opens a gap of <paramref name="length"/> zero bytes in <see cref="Data"/> at
    /// <paramref name="position"/>, moving everything from there on and keeping every relative
    /// array and pointer aimed where it was.
    /// </summary>
    /// <remarks>
    /// This is what growing an array takes. The data is position independent - an
    /// <c>hkRelArray</c> or <c>hkRelPtr</c> stores the distance from its own address to what it
    /// names - so moving bytes breaks exactly the references whose two ends land on opposite
    /// sides of the gap, and no others. Those are found by walking every item, element by
    /// element, into every record, and any type with an integer <c>offset</c> member is taken
    /// as a relative reference, which is how the readers here resolve them too. Items starting
    /// at or after the gap move with it.
    ///
    /// Opening the gap directly after an array's last element and raising its count grows it in
    /// place. Keep <paramref name="length"/> a multiple of the alignment of what follows.
    /// </remarks>
    public void InsertBytes(int position, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Data.Length);
        if (length == 0) return;

        // Where every relative reference is, measured from, and aimed, before anything moves.
        List<(int Field, int Base, int Size, long Target)> refs = [];
        HashSet<int> seen = [];
        for (int i = 1; i < Items.Count; i++)
        {
            TagItem item = Items[i];
            if (item.IsNull || item.TypeIndex <= 0) continue;

            int stride = ResolveByteSize(item.TypeIndex);
            int count = Math.Max(1, item.Count);
            for (int e = 0; e < count; e++)
                CollectRelatives(item.TypeIndex, item.Offset + e * stride, refs, seen, 0);
        }

        byte[] data = new byte[Data.Length + length];
        Buffer.BlockCopy(Data, 0, data, 0, position);
        Buffer.BlockCopy(Data, position, data, position + length, Data.Length - position);

        int Moved(long at) => (int)(at >= position ? at + length : at);

        foreach (var (field, bas, size, target) in refs)
        {
            long delta = Moved(target) - Moved(bas);
            int at = Moved(field);
            if (size == 8) BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at), delta);
            else BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at), (int)delta);
        }

        for (int i = 1; i < Items.Count; i++)
        {
            TagItem item = Items[i];
            if (!item.IsNull && item.Offset >= position) Items[i] = item with { Offset = item.Offset + length };
        }

        Data = data;
        _itemsByOffset = null;
    }

    private void CollectRelatives(
        int type, int address, List<(int, int, int, long)> refs, HashSet<int> seen, int depth)
    {
        if (depth > 32 || type <= 0 || type >= Types.Count) return;

        // A relative reference: an integer member called offset, measured from the reference's
        // own address, which is how both the array and the pointer readers resolve it.
        foreach (TagField field in ResolveFields(type))
        {
            if (field.Name != "offset" || (TagTypeKind)(ResolveFormat(field.TypeIndex) & 0xF) != TagTypeKind.Int)
                continue;

            int at = address + field.Offset;
            int size = ResolveByteSize(field.TypeIndex);
            if (!seen.Add(at) || at + size > Data.Length) return;

            long delta = size == 8
                ? BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(at))
                : BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(at));

            // Zero is a null reference, and stays one wherever it moves.
            if (delta != 0) refs.Add((at, address, size, address + delta));
            return;
        }

        var kind = (TagTypeKind)(ResolveFormat(type) & 0xF);
        if (kind == TagTypeKind.Record)
        {
            foreach (TagField field in ResolveFields(type))
            {
                if (field.TupleSize > 1)
                {
                    int stride = ResolveByteSize(field.TypeIndex);
                    for (int t = 0; t < field.TupleSize; t++)
                        CollectRelatives(field.TypeIndex, address + field.Offset + t * stride, refs, seen, depth + 1);
                }
                else
                {
                    CollectRelatives(field.TypeIndex, address + field.Offset, refs, seen, depth + 1);
                }
            }
        }
        else if (kind == TagTypeKind.Array)
        {
            // Stored inline, a T[N]: every element is a place a reference could be.
            int element = ResolveSubType(type);
            int stride = element > 0 ? ResolveByteSize(element) : 0;
            int total = ResolveByteSize(type);
            if (stride <= 0 || total <= 0) return;

            var elementKind = (TagTypeKind)(ResolveFormat(element) & 0xF);
            if (elementKind is not (TagTypeKind.Record or TagTypeKind.Array)) return;

            for (int e = 0; e < total / stride; e++)
                CollectRelatives(element, address + e * stride, refs, seen, depth + 1);
        }
    }
}
