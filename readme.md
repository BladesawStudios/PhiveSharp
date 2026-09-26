# PhiveSharp

A C# reader and writer for **Phive**, the container Nintendo wraps Havok physics data in for
*Tears of the Kingdom* — the static compound bodies under `Phive/StaticCompoundBody`, the
shapes, the navmeshes — and for the Havok tagfiles inside them.

```csharp
StaticCompoundBody compound = StaticCompoundBody.FromBinary(decompressed);

foreach (CompoundActor actor in compound.Actors.Where(a => a.HasBodies))
{
    // The actor's identity in the Banc placement files.
    Console.WriteLine($"{actor.Hash:X16} has {compound.BodiesOf(actor).Count()} bodies");
}

foreach (int section in compound.ShapeSections)
{
    if (compound.GetShape(section)?.RootObject is not { TypeName: "hknpCompoundShape" } shape) continue;

    foreach (TagObject instance in shape.GetArray("instances").Elements)
        Console.WriteLine($"{instance.GetVector("translation")} " +
                          $"tag {instance.GetInt64("shapeTag")} " +
                          $"{instance.GetPointer("shape")?.TypeName}");   // hknpMeshShape, hknpCylinderShape …
}
```

Edits go back out the way they came in:

```csharp
compound.Actors.RemoveAll(a => a.Hash == doomed);   // the hash map is rebuilt on write
compound.WriteTo("Cave_Akkala_0005.Nin_NX_NVN.bphsc");
```

**Zstd is not handled here.** Every Phive file on disc is compressed against a shipped
dictionary; decompress first — `ZsDicSharp` does that — and compress the bytes this gives back.
There are no dependencies.

Read and written back untouched, all 13,150 Phive files in the dump this was written against
come out byte for byte identical — static compounds included, hash map and body table and all —
and so do the 26,331 Havok tagfiles inside them. Every one of their 490,506 shape instances
reads, and every shape pointer follows.

## What the formats do that is not obvious

**The container is recursive, and a section's number is its meaning.** Sections are laid end to
end from a padded header, each starting on an eight byte boundary, so an empty slot carries the
offset of the section *after* it rather than zero — which is how a file says "section 5 is empty"
as against "there is no section 5". A static compound's mesh shapes are not tagfiles in its
sections; each one is a whole second Phive container, version 1, nested inside a section of the
version 2 one, with the tagfile in *its* first section.

**A tagfile's chunk sizes are big endian in an otherwise little endian file**, and its string
tables pad with `0xFF` rather than with zeroes while every other table pads with zeroes. Both
matter only when writing, and both will silently corrupt a file that ignores them.

**Static compound shapes carry no pointer patch table.** Havok tagfiles normally store pointers
as item indices and patch them on load. Here the arrays are relative — `hkRelArray` keeps a
signed offset from its own address — so the data is position independent and there is nothing to
patch. Navmeshes, which use ordinary pointers, do have the table.

**The type table is sparse, and typedefs carry nothing.** `hkReal` is a name with a parent of
`float` and no size, format or fields of its own, so anything reading a value has to walk up the
parent chain first. `TagFile.ResolveFormat`, `ResolveByteSize` and `ResolveFields` do that.

**A member can carry a tuple length between its flags and its offset.** Field flag `0x80` puts an
extra packed integer in the middle of the member record. Missing it does not fail loudly: the
parse stays in step for a while and then reads a field name index that happens to be in range,
and the type table comes out quietly wrong.

**The actor hash map is reproducible, so it can be rebuilt rather than preserved.** Sections 2
and 3 are an open addressed map with twice as many slots as there are actors, a slot of
`hash % slots`, and linear probing forward. Inserting in the actor table's own order gives back
the bytes that were in the file — for all 3,028 shipped compounds — which is what makes adding
and removing actors safe.

**The actor and body tables point at each other.** An actor holds the first and last index of its
run of bodies, inclusive, and each body holds its actor's index back. Most actors bake to one
body; the ones that do not span two or three. An actor with `-1` for both has no collision in
this compound at all, which is most of them.

**Section 0 is the same 19,120 bytes in every file**, arrays at capacity whether used or not: two
four byte tables at the front, the counts, sixty four placement slots, and the name. A field tile
leaves every placement empty. What fills one is a compound baked somewhere other than where it is
used — a well, a sky island, a merged set — where the transform says where the whole compound
sits and the hash names the actor that placed it.

**Field tiles carry half a megabyte of terrain collision** in sections 8, 9 and 10, at exactly
0x80000, 0x20000 and 0xC0 bytes. Nothing else has them, and this does not decode them: they are
carried through as bytes, as are the per body property tables in sections 5 and 6.

## What this does not model

Sections 5 to 10 of a static compound are kept as bytes. A body names a run of entries in the
property tables and that link is followed, but the entries themselves are not decoded, and
neither is the terrain collision.

Inside a tagfile, values can be read and written in place — a transform moved, a flag set, a
shape tag changed — but nothing here grows an array or adds an object. That would move every item
offset and every relative array in the file, which is a rebuild, not an edit.

## Build

```bash
dotnet build PhiveSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
