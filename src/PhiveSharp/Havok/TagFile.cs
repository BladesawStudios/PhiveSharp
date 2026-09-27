using System.Buffers.Binary;
using System.Text;

namespace PhiveSharp.Havok;

public sealed partial class TagFile
{
    private const int ChunkHeaderSize = 8;
    private const uint SizeMask = 0x3FFFFFFF;
    private const uint LeafFlag = 0x40000000;

    private const int TablePadding = 4;

    private const int DataPadding = 16;

    private const byte StringPadding = 0xFF;

    private const byte StructurePadding = 0x00;

    public sealed record ExtraChunk(string Parent, string Name, byte[] Body, bool IsLeaf);

    public string SdkVersion { get; set; } = "20220100";

    public byte[] Data { get; set; } = [];

    public byte[] TypePointers { get; set; } = [];

    public List<TagType> Types { get; } = [];

    public List<string> TypeStrings { get; } = [];

    public List<string> FieldStrings { get; } = [];

    public List<int> BodyOrder { get; } = [];

    public List<int> HashOrder { get; } = [];

    public List<TagItem> Items { get; } = [];

    public List<TagPatch> Patches { get; } = [];

    public List<ExtraChunk> ExtraChunks { get; } = [];

    private readonly List<(string Parent, string Name)> _layout = [];

    private string _typeBodyChunk = "TBDY";

    public TagItem? Root => Items.Count > 1 ? Items[1] : null;

    public TagObject? RootObject =>
        Root is { } root && root.TypeIndex > 0 ? new TagObject(this, root.TypeIndex, root.Offset) : null;

    public static bool IsTagFile(ReadOnlySpan<byte> data) =>
        data.Length >= ChunkHeaderSize && data.Slice(4, 4).SequenceEqual("TAG0"u8);

    public TagType? FindType(string name) => Types.FirstOrDefault(t => t.Name == name);

    private Dictionary<int, int>? _itemsByOffset;
    private int _itemsByOffsetCount = -1;

    public TagItem? ItemAt(int offset)
    {
        if (_itemsByOffset is null || _itemsByOffsetCount != Items.Count)
        {
            _itemsByOffset = [];
            for (int i = 1; i < Items.Count; i++) _itemsByOffset.TryAdd(Items[i].Offset, i);
            _itemsByOffsetCount = Items.Count;
        }

        return _itemsByOffset.TryGetValue(offset, out int index) ? Items[index] : null;
    }

    public int ResolveFormat(int typeIndex)
    {
        for (int i = typeIndex, guard = 0; i > 0 && i < Types.Count && guard < Types.Count; guard++)
        {
            if (Types[i].Format != 0) return Types[i].Format;
            i = Types[i].ParentIndex;
        }
        return 0;
    }

    public int ResolveByteSize(int typeIndex)
    {
        for (int i = typeIndex, guard = 0; i > 0 && i < Types.Count && guard < Types.Count; guard++)
        {
            if (Types[i].ByteSize != 0) return Types[i].ByteSize;
            i = Types[i].ParentIndex;
        }
        return 0;
    }

    public int ResolveSubType(int typeIndex)
    {
        for (int i = typeIndex, guard = 0; i > 0 && i < Types.Count && guard < Types.Count; guard++)
        {
            if (Types[i].SubTypeIndex != 0) return Types[i].SubTypeIndex;
            i = Types[i].ParentIndex;
        }
        return 0;
    }

    public IEnumerable<TagField> ResolveFields(int typeIndex)
    {
        for (int i = typeIndex, guard = 0; i > 0 && i < Types.Count && guard < Types.Count; guard++)
        {
            foreach (TagField field in Types[i].Fields) yield return field;
            i = Types[i].ParentIndex;
        }
    }

    public static TagFile FromBinary(ReadOnlySpan<byte> data)
    {
        if (!IsTagFile(data)) throw new InvalidDataException("Not a Havok tagfile: expected a TAG0 chunk.");

        (int size, bool leaf) = ReadChunkHeader(data, 0);
        if (leaf) throw new InvalidDataException("The TAG0 chunk is marked as holding bytes.");
        if (size > data.Length) throw new InvalidDataException("The TAG0 chunk runs past the end of the data.");

        TagFile file = new();
        file.Types.Add(new TagType());

        Dictionary<string, (int Offset, int Size)> chunks = [];
        List<(string Parent, string Name, int Offset, int Size, bool Leaf)> all = [];

        foreach (var chunk in Chunks(data, ChunkHeaderSize, size, "TAG0"))
        {
            all.Add(chunk);
            chunks.TryAdd(chunk.Name, (chunk.Offset + ChunkHeaderSize, chunk.Size - ChunkHeaderSize));
            file._layout.Add((chunk.Parent, chunk.Name));
        }

        if (chunks.TryGetValue("SDKV", out var sdkv))
            file.SdkVersion = Encoding.ASCII.GetString(data.Slice(sdkv.Offset, sdkv.Size)).TrimEnd('\0');
        if (chunks.TryGetValue("DATA", out var dataChunk))
            file.Data = data.Slice(dataChunk.Offset, dataChunk.Size).ToArray();
        if (chunks.TryGetValue("TPTR", out var tptr))
            file.TypePointers = data.Slice(tptr.Offset, tptr.Size).ToArray();
        if (chunks.TryGetValue("TST1", out var tst1))
            ReadStrings(data.Slice(tst1.Offset, tst1.Size), file.TypeStrings);
        if (chunks.TryGetValue("FST1", out var fst1))
            ReadStrings(data.Slice(fst1.Offset, fst1.Size), file.FieldStrings);
        if (chunks.TryGetValue("TNA1", out var tna1))
            file.ReadTypeNames(data.Slice(tna1.Offset, tna1.Size));
        if (chunks.TryGetValue("TBDY", out var tbdy)) file.ReadTypeBodies(data.Slice(tbdy.Offset, tbdy.Size));
        else if (chunks.TryGetValue("TBOD", out tbdy))
        {
            file._typeBodyChunk = "TBOD";
            file.ReadTypeBodies(data.Slice(tbdy.Offset, tbdy.Size));
        }
        if (chunks.TryGetValue("THSH", out var thsh))
            file.ReadTypeHashes(data.Slice(thsh.Offset, thsh.Size));
        if (chunks.TryGetValue("ITEM", out var item))
            file.ReadItems(data.Slice(item.Offset, item.Size));
        if (chunks.TryGetValue("PTCH", out var ptch))
            file.ReadPatches(data.Slice(ptch.Offset, ptch.Size));

        foreach ((string parent, string name, int offset, int chunkSize, bool chunkLeaf) in all)
        {
            if (Known.Contains(name)) continue;
            file.ExtraChunks.Add(new ExtraChunk(
                parent, name,
                data.Slice(offset + ChunkHeaderSize, chunkSize - ChunkHeaderSize).ToArray(), chunkLeaf));
        }

        return file;
    }

    private static readonly HashSet<string> Known =
        ["TAG0", "SDKV", "DATA", "TYPE", "TPTR", "TST1", "TNA1", "FST1", "TBDY", "TBOD", "THSH", "INDX", "ITEM", "PTCH"];
    public byte[] ToBinary()
    {
        Dictionary<string, (byte[] Body, bool Leaf, int Padding, byte Fill)> bodies = new()
        {
            ["SDKV"] = (Encoding.ASCII.GetBytes(SdkVersion), true, TablePadding, StructurePadding),
            ["DATA"] = (Data, true, DataPadding, StructurePadding),
            ["TPTR"] = (TypePointers, true, TablePadding, StructurePadding),
            ["TST1"] = (WriteStrings(TypeStrings), true, TablePadding, StringPadding),
            ["TNA1"] = (WriteTypeNames(), true, TablePadding, StructurePadding),
            ["FST1"] = (WriteStrings(FieldStrings), true, TablePadding, StringPadding),
            [_typeBodyChunk] = (WriteTypeBodies(), true, TablePadding, StructurePadding),
            ["THSH"] = (WriteTypeHashes(), true, TablePadding, StructurePadding),
            ["ITEM"] = (WriteItems(), true, TablePadding, StructurePadding),
        };

        if (Patches.Count > 0) bodies["PTCH"] = (WritePatches(), true, TablePadding, StructurePadding);
        foreach (ExtraChunk extra in ExtraChunks)
            bodies[extra.Name] = (extra.Body, extra.IsLeaf, TablePadding, StructurePadding);

        MemoryStream output = new();
        output.Write(new byte[ChunkHeaderSize]);

        foreach (string name in Children("TAG0", ["SDKV", "DATA", "TYPE", "INDX"]))
        {
            if (name is "TYPE" or "INDX")
            {
                string[] fallback = name == "TYPE"
                    ? ["TPTR", "TST1", "TNA1", "FST1", _typeBodyChunk, "THSH"]
                    : ["ITEM", "PTCH"];

                long start = output.Position;
                output.Write(new byte[ChunkHeaderSize]);
                foreach (string child in Children(name, fallback))
                    if (bodies.TryGetValue(child, out var body))
                        WriteChunk(output, child, body.Body, body.Leaf, body.Padding, body.Fill);
                PatchChunkHeader(output, start, name, false);
            }
            else if (bodies.TryGetValue(name, out var body))
            {
                WriteChunk(output, name, body.Body, body.Leaf, body.Padding, body.Fill);
            }
        }

        PatchChunkHeader(output, 0, "TAG0", false);
        return output.ToArray();

        IEnumerable<string> Children(string parent, string[] fallback) =>
            _layout.Any(entry => entry.Parent == parent)
                ? _layout.Where(entry => entry.Parent == parent).Select(entry => entry.Name)
                : fallback;
    }

    // Chunks -----------------------------------------------------------------------------
    private static (int Size, bool Leaf) ReadChunkHeader(ReadOnlySpan<byte> data, int offset)
    {
        uint word = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        return ((int)(word & SizeMask), (word & LeafFlag) != 0);
    }

    private static IEnumerable<(string Parent, string Name, int Offset, int Size, bool Leaf)>
        Chunks(ReadOnlySpan<byte> data, int start, int end, string parent)
    {
        List<(string, string, int, int, bool)> found = [];
        int position = start;

        while (position + ChunkHeaderSize <= end)
        {
            (int size, bool leaf) = ReadChunkHeader(data, position);
            if (size < ChunkHeaderSize || position + size > end) break;

            string name = Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            found.Add((parent, name, position, size, leaf));

            if (!leaf)
                foreach (var child in Chunks(data, position + ChunkHeaderSize, position + size, name))
                    found.Add(child);

            position += size;
        }

        return found;
    }

    private static void WriteChunk(Stream output, string name, byte[] body, bool leaf, int padding, byte fill = 0)
    {
        int padded = (body.Length + padding - 1) & ~(padding - 1);

        Span<byte> header = stackalloc byte[ChunkHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(ChunkHeaderSize + padded) | (leaf ? LeafFlag : 0));
        Encoding.ASCII.GetBytes(name).CopyTo(header[4..]);

        byte[] tail = new byte[padded - body.Length];
        Array.Fill(tail, fill);

        output.Write(header);
        output.Write(body);
        output.Write(tail);
    }

    private static void PatchChunkHeader(MemoryStream output, long start, string name, bool leaf)
    {
        long end = output.Position;
        int size = (int)(end - start);

        Span<byte> header = stackalloc byte[ChunkHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size | (leaf ? LeafFlag : 0));
        Encoding.ASCII.GetBytes(name).CopyTo(header[4..]);

        output.Position = start;
        output.Write(header);
        output.Position = end;
    }

    // String tables ----------------------------------------------------------------------
    private static void ReadStrings(ReadOnlySpan<byte> body, List<string> into)
    {
        int position = 0;
        while (position < body.Length)
        {
            int nul = body[position..].IndexOf((byte)0);
            if (nul < 0) break;
            // An empty entry is the padding at the end of the chunk, not a name.
            if (nul == 0) break;

            into.Add(Encoding.UTF8.GetString(body.Slice(position, nul)));
            position += nul + 1;
        }
    }

    private static byte[] WriteStrings(List<string> strings)
    {
        MemoryStream output = new();
        foreach (string value in strings)
        {
            output.Write(Encoding.UTF8.GetBytes(value));
            output.WriteByte(0);
        }
        return output.ToArray();
    }

    private static int Intern(List<string> strings, string value)
    {
        int index = strings.IndexOf(value);
        if (index >= 0) return index;

        strings.Add(value);
        return strings.Count - 1;
    }

    // Type names -------------------------------------------------------------------------
    private void ReadTypeNames(ReadOnlySpan<byte> body)
    {
        PackedReader reader = new(body);
        int count = reader.Read();

        for (int i = 1; i < count; i++)
        {
            TagType type = new() { Name = StringAt(TypeStrings, reader.Read()) };
            int templates = reader.Read();
            for (int t = 0; t < templates; t++)
                type.Templates.Add(new TagTemplate(StringAt(TypeStrings, reader.Read()), reader.Read()));

            Types.Add(type);
        }
    }

    private byte[] WriteTypeNames()
    {
        MemoryStream output = new();
        PackedWriter writer = new(output);
        writer.Write(Types.Count);

        for (int i = 1; i < Types.Count; i++)
        {
            TagType type = Types[i];
            writer.Write(Intern(TypeStrings, type.Name));
            writer.Write(type.Templates.Count);
            foreach (TagTemplate template in type.Templates)
            {
                writer.Write(Intern(TypeStrings, template.Name));
                writer.Write(template.Value);
            }
        }

        return output.ToArray();
    }

    // Type bodies ------------------------------------------------------------------------
    private void ReadTypeBodies(ReadOnlySpan<byte> body)
    {
        PackedReader reader = new(body);
        while (reader.Remaining > 1)
        {
            int index = reader.Read();
            if (index == 0) break;
            if (index >= Types.Count)
                throw new InvalidDataException($"Type body for type {index}, which the file does not name.");

            TagType type = Types[index];
            type.HasBody = true;
            BodyOrder.Add(index);

            type.ParentIndex = reader.Read();
            TagType.Presence flags = (TagType.Presence)reader.Read();
            type.Flags = flags;

            if (flags.HasFlag(TagType.Presence.Format)) type.Format = reader.Read();
            if (flags.HasFlag(TagType.Presence.SubType)) type.SubTypeIndex = reader.Read();
            if (flags.HasFlag(TagType.Presence.Version)) type.Version = reader.Read();
            if (flags.HasFlag(TagType.Presence.ByteSize))
            {
                type.ByteSize = reader.Read();
                type.AlignmentAndFlags = reader.Read();
            }
            if (flags.HasFlag(TagType.Presence.Unknown)) type.Unknown = reader.Read();

            if (flags.HasFlag(TagType.Presence.Fields))
            {
                int fields = reader.Read();
                for (int f = 0; f < fields; f++)
                {
                    string name = StringAt(FieldStrings, reader.Read());
                    int fieldFlags = reader.Read();
                    int tuple = (fieldFlags & TagField.TupleFlag) != 0 ? reader.Read() : 0;
                    int offset = reader.Read();
                    int fieldType = reader.Read();
                    type.Fields.Add(new TagField(name, fieldFlags, tuple, offset, fieldType));
                }
            }

            if (flags.HasFlag(TagType.Presence.Interfaces))
            {
                int interfaces = reader.Read();
                for (int n = 0; n < interfaces; n++) type.Interfaces.Add((reader.Read(), reader.Read()));
            }

            if (flags.HasFlag(TagType.Presence.Attribute)) type.Attribute = reader.Read();
        }
    }

    private byte[] WriteTypeBodies()
    {
        MemoryStream output = new();
        PackedWriter writer = new(output);

        foreach (int index in BodyOrder)
        {
            TagType type = Types[index];
            TagType.Presence flags = type.Flags;

            writer.Write(index);
            writer.Write(type.ParentIndex);
            writer.Write((int)flags);

            if (flags.HasFlag(TagType.Presence.Format)) writer.Write(type.Format);
            if (flags.HasFlag(TagType.Presence.SubType)) writer.Write(type.SubTypeIndex);
            if (flags.HasFlag(TagType.Presence.Version)) writer.Write(type.Version);
            if (flags.HasFlag(TagType.Presence.ByteSize))
            {
                writer.Write(type.ByteSize);
                writer.Write(type.AlignmentAndFlags);
            }
            if (flags.HasFlag(TagType.Presence.Unknown)) writer.Write(type.Unknown);

            if (flags.HasFlag(TagType.Presence.Fields))
            {
                writer.Write(type.Fields.Count);
                foreach (TagField field in type.Fields)
                {
                    writer.Write(Intern(FieldStrings, field.Name));
                    writer.Write(field.Flags);
                    if (field.IsTuple) writer.Write(field.TupleSize);
                    writer.Write(field.Offset);
                    writer.Write(field.TypeIndex);
                }
            }

            if (flags.HasFlag(TagType.Presence.Interfaces))
            {
                writer.Write(type.Interfaces.Count);
                foreach ((int typeIndex, int value) in type.Interfaces)
                {
                    writer.Write(typeIndex);
                    writer.Write(value);
                }
            }

            if (flags.HasFlag(TagType.Presence.Attribute)) writer.Write(type.Attribute);
        }

        return output.ToArray();
    }

    private void ReadTypeHashes(ReadOnlySpan<byte> body)
    {
        PackedReader reader = new(body);
        int count = reader.Read();

        for (int i = 0; i < count; i++)
        {
            int index = reader.Read();
            uint hash = reader.ReadUInt32();
            if (index > 0 && index < Types.Count)
            {
                Types[index].Hash = hash;
                HashOrder.Add(index);
            }
        }
    }

    private byte[] WriteTypeHashes()
    {
        MemoryStream output = new();
        PackedWriter writer = new(output);
        writer.Write(HashOrder.Count);

        byte[] hash = new byte[4];
        foreach (int index in HashOrder)
        {
            writer.Write(index);
            BinaryPrimitives.WriteUInt32LittleEndian(hash, Types[index].Hash ?? 0);
            output.Write(hash);
        }

        return output.ToArray();
    }

    // Items and patches ------------------------------------------------------------------

    private void ReadItems(ReadOnlySpan<byte> body)
    {
        for (int offset = 0; offset + 12 <= body.Length; offset += 12)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]);
            Items.Add(new TagItem(
                (int)(word & 0xFFFFFF),
                (int)(word >> 24),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 4)..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 8)..])));
        }
    }

    private byte[] WriteItems()
    {
        byte[] body = new byte[Items.Count * 12];
        Span<byte> span = body;

        for (int i = 0; i < Items.Count; i++)
        {
            TagItem item = Items[i];
            BinaryPrimitives.WriteUInt32LittleEndian(span[(i * 12)..],
                (uint)(item.TypeIndex & 0xFFFFFF) | ((uint)item.Flags << 24));
            BinaryPrimitives.WriteUInt32LittleEndian(span[(i * 12 + 4)..], (uint)item.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(i * 12 + 8)..], (uint)item.Count);
        }

        return body;
    }

    private void ReadPatches(ReadOnlySpan<byte> body)
    {
        int offset = 0;
        while (offset + 8 <= body.Length)
        {
            int typeIndex = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]);
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 4)..]);
            offset += 8;

            if (count < 0 || offset + count * 4 > body.Length) break;

            List<int> offsets = new(count);
            for (int i = 0; i < count; i++)
                offsets.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + i * 4)..]));

            Patches.Add(new TagPatch(typeIndex, offsets));
            offset += count * 4;
        }
    }

    private byte[] WritePatches()
    {
        MemoryStream output = new();
        Span<byte> word = stackalloc byte[4];

        foreach (TagPatch patch in Patches)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)patch.TypeIndex);
            output.Write(word);
            BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)patch.Offsets.Count);
            output.Write(word);

            foreach (int offset in patch.Offsets)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)offset);
                output.Write(word);
            }
        }

        return output.ToArray();
    }

    private static string StringAt(List<string> strings, int index) =>
        index >= 0 && index < strings.Count
            ? strings[index]
            : throw new InvalidDataException($"String index {index} is outside the table.");

    /// <summary>
    /// Reads the variable length integers the type tables are built from: the top bits of the
    /// first byte say how many bytes follow, and the value is big endian in what is left.
    /// </summary>
    private ref struct PackedReader(ReadOnlySpan<byte> body)
    {
        private readonly ReadOnlySpan<byte> _body = body;
        private int _position;

        public readonly int Remaining => _body.Length - _position;

        public int Read()
        {
            byte first = _body[_position++];

            if ((first & 0x80) == 0) return first;
            if ((first & 0xC0) == 0x80) return ((first & 0x3F) << 8) | _body[_position++];
            if ((first & 0xE0) == 0xC0) return ((first & 0x1F) << 16) | (_body[_position++] << 8) | _body[_position++];
            if ((first & 0xF0) == 0xE0)
                return ((first & 0x0F) << 24) | (_body[_position++] << 16) | (_body[_position++] << 8) | _body[_position++];

            throw new InvalidDataException($"Unsupported packed integer prefix 0x{first:X2}.");
        }

        public uint ReadUInt32()
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_body[_position..]);
            _position += 4;
            return value;
        }
    }

    private readonly struct PackedWriter(Stream output)
    {
        public void Write(int value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);

            if (value < 0x80)
            {
                output.WriteByte((byte)value);
            }
            else if (value < 0x4000)
            {
                output.WriteByte((byte)(0x80 | (value >> 8)));
                output.WriteByte((byte)value);
            }
            else if (value < 0x200000)
            {
                output.WriteByte((byte)(0xC0 | (value >> 16)));
                output.WriteByte((byte)(value >> 8));
                output.WriteByte((byte)value);
            }
            else if (value < 0x10000000)
            {
                output.WriteByte((byte)(0xE0 | (value >> 24)));
                output.WriteByte((byte)(value >> 16));
                output.WriteByte((byte)(value >> 8));
                output.WriteByte((byte)value);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Too large for a packed integer.");
            }
        }
    }
}
