# PhiveSharp

A C# reader and writer for **Phive**, the container Nintendo wraps Havok physics data in for
*Tears of the Kingdom* — the static compound bodies under `Phive/StaticCompoundBody`, the
shapes, the navmeshes, the cloth — and for the Havok tagfiles inside them.

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

Read and written back untouched, all 13,534 Phive files in the dump this was written against
come out byte for byte identical — static compounds included, hash map and body table and all —
and so do the 26,715 Havok tagfiles inside them. Every one of their 490,506 shape instances
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
four byte tables at the front, the counts, sixty four placement slots, the name, and four counts
of what follows the shapes. A field tile leaves every placement empty. What fills one is a compound
baked somewhere other than where it is used — a well, a sky island, a merged set — where the
transform says where the whole compound sits, the hash names the actor that placed it, and three
counts say how much of the compound's water came with it.

**The game makes one rigid body per layer, not per actor.** The two tables at the front of section 0
are the compound's groups: a layer and a placement slot each, entities in the first and sensors in
the second, ending at a layer of `0xFF`. Each becomes a rigid body named
`<compound>_Entity_<layer>_<slot>`, whose shape is the shape section in the same position, entity
groups first, and each body is the instance of that shape its entry names. A body's `Info` word says
which group it is in - entity or sensor, layer, sub layer - and an actor placed with a sub-compound
carries that placement's id and slot in what was its reserved space. `EntityGroups`, `SensorGroups`
and the properties on `CompoundBody` and `CompoundActor` unpack them.

**An actor's collision comes and goes with it, by degrees.** The game switches a body's instance on
and off with a level - live, held by a static compound controller, disabled or taken over, removed -
and the last byte of `Info` (`Persistence`) says how far down that list the body stays. An actor can
also be marked to start with its bodies off (`StartsDisabled`) until it turns them on itself.

**After the shapes come more water and the ground.** Water outside the tile's height field -
quicksand pits and whirlpools among it - is records rather than shapes: a section of boxes, one of cylinders and one of quadrants, each record
a transform, a material and the hash of the actor it belongs to (`WaterBoxes`, `WaterCylinders`,
`WaterQuadrants`). Then a table of external meshes and a nested `.bphsh` for each - every field tile
has exactly one, its ground (`ExternalMeshes`).

**Field tiles carry half a megabyte of water** in sections 7 to 10: the height field the game
builds each tile's `_TeraWater` body from. Section 7 says how big the tile is and where, and how big
the grid is; section 8 is a surface, a flow and a fluid per sample on a 256 by 256 grid; section 9 is
the bottom of the water; section 10 is the twelve fluids a sample can be. The body is closed, surface
on top and bottom underneath, over every cell whose four corners are wet - and a sample is dry when
the low byte of its fluid is 0x80 or more, the only part of that byte anything in the game reads.
`Water` reads them. The fluids land where they should - lava around Eldin, quicksand in Gerudo,
sludge around Zora's Domain.

**A navmesh's faces can be switched off by the actors standing on them.** Section 1 of a `.bphnm`
is its section UID - `x * 32 + z` across a field's grid of tiles, plus 1280 in the Depths, the same
number its streaming sets join to their neighbours - and a 3 by 4 transform. Section 2 is the Banc
hashes of placed actors, and each face's `faceData` packs a one-based index into them in its low
sixteen bits, PhiveConfig's four user tags in the next four, and a navmesh material in its top five:
miasma faces name miasma actors, sage walls name their sage wall, and the game enables and disables
those faces as their actors come and go. The `Road` tag traces Hyrule's roads.
`PhiveNavMesh` and `PhiveNavFace` read them.

## What this does not model

A body's run of entries in the property tables in sections 5 and 6 reads as its material palette
(`MaterialsOf`), the same sixteen byte material and eight byte filter entries a shape keeps in its
sections 1 and 2 (`PhiveShape`). Materials, sub materials, flags and the layers a filter hits are
named from `Phive/Config/PhiveConfig.byml`'s own tables, but some bits are still only carried:

- the six bits of a navmesh face between its user tags and its material - of which only three are
  ever set, one of them on every face that links an actor;
- what a water cylinder's or quadrant's four parameters mean beyond telling quicksand from a
  whirlpool, and a second hash a few water records carry.

Inside a tagfile, values can be read and written in place — a transform moved, a flag set, a
shape tag changed — but nothing here grows an array or adds an object. That would move every item
offset and every relative array in the file, which is a rebuild, not an edit.

## Build

```bash
dotnet build PhiveSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
