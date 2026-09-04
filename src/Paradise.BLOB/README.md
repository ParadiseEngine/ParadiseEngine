# Paradise.BLOB

`Paradise.BLOB` is a .NET blob builder for immutable unmanaged data layouts. It provides Unity-style blob primitives and builders that work in plain .NET code: you declare the layout as an unmanaged `struct`, a builder writes it as one contiguous byte array with relative offsets, and a reader hands the same struct back over those bytes with no parse and no copy.

The engine ships several asset formats on it — the collision world (`Paradise.Physics`), behaviour trees (`Paradise.BT`), mesh blobs (`Paradise.Assets.Mesh`) and skeletons and animation clips (`Paradise.Animation`) — so the conventions below are the ones those follow.

## Install

```bash
dotnet add package Paradise.BLOB
```

## Features

- Build immutable unmanaged roots with `ValueBuilder<T>` and `StructBuilder<T>`.
- Store arrays, strings, pointers, trees, sorted arrays, and dynamically typed payloads — including arrays whose elements themselves hold arrays and strings.
- Read blobs from one aligned native copy (`NativeBlobAssetReference<T>`) or over a pinned managed array (`ManagedBlobAssetReference<T>`).
- Keep offsets and alignment correct without hand-rolling binary layouts.
- Friendly to regular .NET apps and NativeAOT-oriented workflows: no reflection, no serializer.

## Quick start

```csharp
using Paradise.BLOB;
using System.Text;

public struct DemoBlob
{
    public BlobString<UTF8Encoding> Name;
    public BlobArray<int> Values;
    public BlobPtr<int> MaxValue;
}

var builder = new StructBuilder<DemoBlob>();
builder.SetString(ref builder.Value.Name, "demo");
builder.SetArray(ref builder.Value.Values, new[] { 1, 2, 3 });
builder.SetPointer(ref builder.Value.MaxValue, 3);

byte[] bytes = builder.CreateBlob();               // what you store, ship or hash

using var blob = new NativeBlobAssetReference<DemoBlob>(bytes);   // one aligned copy, no pinning
ref var root = ref blob.Value;

Console.WriteLine(root.Name.ToString());
Console.WriteLine(string.Join(", ", root.Values.ToArray()));
Console.WriteLine(root.MaxValue.Value);
```

## Reading: native or managed

- `NativeBlobAssetReference<T>(ReadOnlySpan<byte>, int alignment = 16)` copies the bytes once into aligned native memory. Prefer it at runtime: the root and every array in it are aligned however you asked, nothing is pinned in the GC heap, and the source span can be a file read or a slice of something larger. Dispose it when the data has been uploaded or is no longer needed; a finalizer frees it otherwise.
- `ManagedBlobAssetReference<T>(byte[])` pins a managed array in place and reads through the pin. Use it when the bytes must stay a `byte[]` you also hand elsewhere. Dispose it, or the array stays pinned.

Both hand back `ref T Value` — a reference into the blob, not a copy.

## Nested layouts: arrays of structs that hold arrays

An element type may itself carry `BlobArray<T>` and `BlobString<TEncoding>` fields. Build such an array from one builder per element:

```csharp
public struct Draw
{
    public uint FirstIndex;
    public uint IndexCount;
    public BlobString<UTF8Encoding> Name;
}

public struct MeshBlob
{
    public uint Magic;
    public uint Version;
    public BlobArray<float> Vertices;
    public BlobArray<Draw> Draws;
}

var mesh = new StructBuilder<MeshBlob>();
mesh.Value.Magic = 0x48534D50;   // "PMSH"
mesh.Value.Version = 1;
mesh.SetArray(ref mesh.Value.Vertices, vertices);
mesh.SetArray(ref mesh.Value.Draws, draws.Select(d =>
{
    var draw = new StructBuilder<Draw>();
    draw.Value.FirstIndex = d.First;
    draw.Value.IndexCount = d.Count;
    draw.SetString(ref draw.Value.Name, d.Name);
    return (IBuilder<Draw>)draw;
}));
```

## Conventions the engine's formats follow

- **Magic and version first.** The first two fields of a root are a `uint` magic and a `uint` version, so a reader can refuse a foreign or newer blob by name before touching an offset. Check them on the bytes (`BitConverter.ToUInt32(bytes)`) before constructing a reference.
- **Validate after opening.** A blob is trusted memory once opened; a reader that will index into arrays checks the counts and indices it depends on (a draw that runs past the index buffer, a joint that names a node outside the tree) right after opening, and disposes the reference before throwing.
- **Deterministic bytes.** The same input builds the same bytes, so a blob can live in a source tree beside what it was made from and be fingerprinted by hash.

## The one rule when reading: never through a copy

`BlobArray<T>`, `BlobString<TEncoding>` and `BlobPtr<T>` are small headers holding an offset **relative to their own address**. Anything that copies the header moves that address and the offset then points at the stack, not the blob — with no error, just wrong data. Three ways to copy one by accident:

```csharp
// WRONG: `in` makes `blob` a readonly reference; calling a non-readonly member on
// blob.Draws forces a defensive COPY of the array header.
static void Check(in MeshBlob blob) { var n = blob.Draws[0].IndexCount; }

// WRONG: a `readonly` member on the struct does the same to every array it touches.
public readonly int VertexCount => Vertices.Length / 12;

// WRONG: passing a BlobString (or BlobArray) by value to a helper.
static string? NameOf(BlobString<UTF8Encoding> name) => name.ToString();
```

Do this instead:

```csharp
static void Check(ref MeshBlob blob) { ref var draw = ref blob.Draws[0]; var n = draw.IndexCount; }
public int VertexCount => Vertices.Length / 12;                 // not readonly
var name = node.Name.ToString();                               // read in place
```

Reach every blob member through a mutable `ref`, do not mark members that touch one `readonly`, and do not hand one to a method by value. A by-value read can pass a test by luck and fail elsewhere.

## Common builders

- `ArrayBuilder<T>` and `SetArray(...)` for contiguous unmanaged arrays.
- `ArrayBuilderWithItemBuilders<T>` and the `SetArray(ref field, IEnumerable<IBuilder<T>>)` overload for arrays of structs that hold arrays or strings.
- `StringBuilder<TEncoding>` and `SetString(...)` for encoded blob strings.
- `PtrBuilderWithNewValue<T>` and `SetPointer(...)` for blob pointers.
- `TreeBuilder<T>` and `AnyTreeBuilder` for preordered trees with subtree end indices.
- `SortedArrayBuilder<TKey, TValue>` for hash-ordered key/value lookup tables.

## Notes

- Blob roots and referenced values must be unmanaged.
- `CreateBlob()` returns the raw serialized bytes for storage or transport; `CreateNativeBlobAssetReference()` and `CreateManagedBlobAssetReference()` open them directly.
- Dispose every reference: a native one frees its memory, a managed one unpins its array.
