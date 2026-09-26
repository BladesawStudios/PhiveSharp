using System.Buffers.Binary;
using System.Numerics;

namespace PhiveSharp.Havok;

public readonly struct TagObject(TagFile file, int typeIndex, int offset)
{
    public TagFile File { get; } = file;

    public int TypeIndex { get; } = typeIndex;

    public int Offset { get; } = offset;

    public TagType Type => File.Types[TypeIndex];

    public string TypeName => Type.Name;

    public TagField? Find(string name)
    {
        foreach (TagField field in File.ResolveFields(TypeIndex))
            if (field.Name == name) return field;
        return null;
    }

    public bool Has(string name) => Find(name) is not null;

    public IEnumerable<TagField> Fields => File.ResolveFields(TypeIndex);

    private TagField Require(string name) =>
        Find(name) ?? throw new KeyNotFoundException($"{TypeName} has no member named \"{name}\".");

    public TagObject GetObject(string name)
    {
        TagField field = Require(name);
        return new TagObject(File, field.TypeIndex, Offset + field.Offset);
    }

    public long GetInt64(string name)
    {
        TagField field = Require(name);
        return ReadInteger(field.TypeIndex, Offset + field.Offset);
    }

    public ulong GetUInt64(string name) => (ulong)GetInt64(name);

    public int GetInt32(string name) => (int)GetInt64(name);

    public bool GetBoolean(string name) => GetInt64(name) != 0;

    public float GetSingle(string name) => (float)GetDouble(name);

    public double GetDouble(string name)
    {
        TagField field = Require(name);
        return ReadFloating(field.TypeIndex, Offset + field.Offset);
    }

    public Vector4 GetVector(string name)
    {
        TagField field = Require(name);
        int address = Offset + field.Offset;
        int element = File.ResolveSubType(field.TypeIndex);

        // hkVector4f and hkQuaternionf are tuples of floats; hkFloat3 is a record of three.
        if ((TagTypeKind)(File.ResolveFormat(field.TypeIndex) & 0xF) == TagTypeKind.Array && element != 0)
        {
            int size = File.ResolveByteSize(element);
            int count = size > 0 ? File.ResolveByteSize(field.TypeIndex) / size : 0;
            return new Vector4(
                count > 0 ? (float)ReadFloating(element, address) : 0f,
                count > 1 ? (float)ReadFloating(element, address + size) : 0f,
                count > 2 ? (float)ReadFloating(element, address + size * 2) : 0f,
                count > 3 ? (float)ReadFloating(element, address + size * 3) : 0f);
        }

        TagObject record = new(File, field.TypeIndex, address);
        return new Vector4(
            record.Has("x") ? record.GetSingle("x") : 0f,
            record.Has("y") ? record.GetSingle("y") : 0f,
            record.Has("z") ? record.GetSingle("z") : 0f,
            record.Has("w") ? record.GetSingle("w") : 0f);
    }

    public TagArray GetArray(string name)
    {
        TagField field = Require(name);
        int address = Offset + field.Offset;
        int type = field.TypeIndex;

        if ((TagTypeKind)(File.ResolveFormat(type) & 0xF) != TagTypeKind.Array)
        {
            foreach (TagField inner in File.ResolveFields(type))
            {
                if ((TagTypeKind)(File.ResolveFormat(inner.TypeIndex) & 0xF) != TagTypeKind.Array) continue;
                address += inner.Offset;
                type = inner.TypeIndex;
                break;
            }
        }

        return Resolve(File, type, address, field.TupleSize);
    }

    public TagObject? GetPointer(string name)
    {
        TagField field = Require(name);
        int address = Offset + field.Offset;
        int type = field.TypeIndex;

        if ((TagTypeKind)(File.ResolveFormat(type) & 0xF) != TagTypeKind.Pointer) return null;

        int target;
        TagField? relative = null;
        foreach (TagField inner in File.ResolveFields(type))
            if (inner.Name == "offset" && (TagTypeKind)(File.ResolveFormat(inner.TypeIndex) & 0xF) == TagTypeKind.Int)
                relative = inner;

        if (relative is { } stored)
        {
            long delta = ReadInteger(stored.TypeIndex, address + stored.Offset);
            if (delta == 0) return null;
            target = (int)(address + stored.Offset + delta);
        }
        else
        {
            long item = ReadInteger(type, address);
            if (item <= 0 || item >= File.Items.Count) return null;
            target = File.Items[(int)item].Offset;
        }

        if (target < 0 || target >= File.Data.Length) return null;

        int pointee = File.ItemAt(target)?.TypeIndex ?? File.ResolveSubType(type);
        return pointee > 0 ? new TagObject(File, pointee, target) : null;
    }

    public void SetInt64(string name, long value)
    {
        TagField field = Require(name);
        WriteInteger(field.TypeIndex, Offset + field.Offset, value);
    }

    public void SetInt32(string name, int value) => SetInt64(name, value);

    public void SetBoolean(string name, bool value) => SetInt64(name, value ? 1 : 0);

    public void SetSingle(string name, float value)
    {
        TagField field = Require(name);
        WriteFloating(field.TypeIndex, Offset + field.Offset, value);
    }

    public void SetVector(string name, Vector4 value)
    {
        TagField field = Require(name);
        int address = Offset + field.Offset;
        int element = File.ResolveSubType(field.TypeIndex);

        if ((TagTypeKind)(File.ResolveFormat(field.TypeIndex) & 0xF) == TagTypeKind.Array && element != 0)
        {
            int size = File.ResolveByteSize(element);
            int count = size > 0 ? File.ResolveByteSize(field.TypeIndex) / size : 0;
            for (int i = 0; i < count && i < 4; i++) WriteFloating(element, address + size * i, value[i]);
            return;
        }

        TagObject record = new(File, field.TypeIndex, address);
        if (record.Has("x")) record.SetSingle("x", value.X);
        if (record.Has("y")) record.SetSingle("y", value.Y);
        if (record.Has("z")) record.SetSingle("z", value.Z);
        if (record.Has("w")) record.SetSingle("w", value.W);
    }

    internal long ReadInteger(int typeIndex, int address)
    {
        int format = File.ResolveFormat(typeIndex);
        bool signed = (format & 0x200) != 0;
        ReadOnlySpan<byte> data = File.Data;

        return File.ResolveByteSize(typeIndex) switch
        {
            1 => signed ? (sbyte)data[address] : data[address],
            2 => signed
                ? BinaryPrimitives.ReadInt16LittleEndian(data[address..])
                : BinaryPrimitives.ReadUInt16LittleEndian(data[address..]),
            4 => signed
                ? BinaryPrimitives.ReadInt32LittleEndian(data[address..])
                : BinaryPrimitives.ReadUInt32LittleEndian(data[address..]),
            8 => BinaryPrimitives.ReadInt64LittleEndian(data[address..]),
            var size => throw new InvalidDataException($"Cannot read a {size} byte integer."),
        };
    }

    internal void WriteInteger(int typeIndex, int address, long value)
    {
        Span<byte> data = File.Data;

        switch (File.ResolveByteSize(typeIndex))
        {
            case 1: data[address] = (byte)value; break;
            case 2: BinaryPrimitives.WriteInt16LittleEndian(data[address..], (short)value); break;
            case 4: BinaryPrimitives.WriteInt32LittleEndian(data[address..], (int)value); break;
            case 8: BinaryPrimitives.WriteInt64LittleEndian(data[address..], value); break;
            default: throw new InvalidDataException("Cannot write an integer of this size.");
        }
    }

    internal double ReadFloating(int typeIndex, int address)
    {
        ReadOnlySpan<byte> data = File.Data;

        return File.ResolveByteSize(typeIndex) switch
        {
            2 => (double)BinaryPrimitives.ReadHalfLittleEndian(data[address..]),
            4 => BinaryPrimitives.ReadSingleLittleEndian(data[address..]),
            8 => BinaryPrimitives.ReadDoubleLittleEndian(data[address..]),
            var size => throw new InvalidDataException($"Cannot read a {size} byte float."),
        };
    }

    internal void WriteFloating(int typeIndex, int address, double value)
    {
        Span<byte> data = File.Data;

        switch (File.ResolveByteSize(typeIndex))
        {
            case 2: BinaryPrimitives.WriteHalfLittleEndian(data[address..], (Half)value); break;
            case 4: BinaryPrimitives.WriteSingleLittleEndian(data[address..], (float)value); break;
            case 8: BinaryPrimitives.WriteDoubleLittleEndian(data[address..], value); break;
            default: throw new InvalidDataException("Cannot write a float of this size.");
        }
    }

    internal static TagArray Resolve(TagFile file, int typeIndex, int address, int tupleSize)
    {
        int element = file.ResolveSubType(typeIndex);
        int stride = element != 0 ? file.ResolveByteSize(element) : 0;

        TagField? offsetField = null, sizeField = null, pointerField = null;
        foreach (TagField field in file.ResolveFields(typeIndex))
        {
            TagTypeKind kind = (TagTypeKind)(file.ResolveFormat(field.TypeIndex) & 0xF);
            if (field.Name is "offset" && kind == TagTypeKind.Int) offsetField ??= field;
            else if (field.Name is "size" or "numElements" && kind == TagTypeKind.Int) sizeField ??= field;
            else if (kind == TagTypeKind.Pointer) pointerField ??= field;
        }

        if (offsetField is { } relative && sizeField is { } length)
        {
            TagObject cursor = new(file, typeIndex, address);
            long delta = cursor.ReadInteger(relative.TypeIndex, address + relative.Offset);
            long count = cursor.ReadInteger(length.TypeIndex, address + length.Offset);
            return new TagArray(file, element, (int)(address + delta), (int)count, stride);
        }

        if (pointerField is { } pointer && sizeField is { } pointerLength)
        {
            TagObject cursor = new(file, typeIndex, address);
            long item = cursor.ReadInteger(pointer.TypeIndex, address + pointer.Offset);
            long count = cursor.ReadInteger(pointerLength.TypeIndex, address + pointerLength.Offset);

            int start = item > 0 && item < file.Items.Count ? file.Items[(int)item].Offset : -1;
            return new TagArray(file, element, start, (int)count, stride);
        }

        int inline = tupleSize > 0 ? tupleSize : stride > 0 ? file.ResolveByteSize(typeIndex) / stride : 0;
        return new TagArray(file, element, address, inline, stride);
    }
}

public readonly struct TagArray(TagFile file, int elementTypeIndex, int offset, int count, int stride)
{
    public TagFile File { get; } = file;

    public int ElementTypeIndex { get; } = elementTypeIndex;

    public int Offset { get; } = offset;

    public int Count { get; } = count;

    public int Stride { get; } = stride;

    public bool IsEmpty => Count <= 0 || Offset < 0;

    public TagObject this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return new TagObject(File, ElementTypeIndex, Offset + index * Stride);
        }
    }

    public IEnumerable<TagObject> Elements
    {
        get
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
    }

    public long GetInt64(int index) =>
        new TagObject(File, ElementTypeIndex, 0).ReadInteger(ElementTypeIndex, Offset + index * Stride);

    public double GetDouble(int index) =>
        new TagObject(File, ElementTypeIndex, 0).ReadFloating(ElementTypeIndex, Offset + index * Stride);
}
