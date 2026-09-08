# Paradise.BLOB

`Paradise.BLOB` builds immutable unmanaged data with Unity-style blob primitives in plain .NET. Declare an unmanaged `struct`; a builder writes contiguous bytes with relative offsets, and a reader exposes the struct over those bytes without parsing or copying.

Used by collision worlds, behavior trees, mesh blobs, skeletons and animation clips.

## Install

```bash
dotnet add package Paradise.BLOB
```

## Features

- Build immutable unmanaged roots with `ValueBuilder<T>` and `StructBuilder<T>`.
- Store arrays, strings, pointers, trees, sorted arrays, and dynamically typed payloads — including arrays whose elements themselves hold arrays and strings.
- Read blobs from one aligned native copy (`NativeBlobAssetReference<T>`) or over a pinned managed array (`ManagedBlobAssetReference<T>`).
- Keep offsets and alignment correct without hand-rolling binary layouts.
- Supports .NET and NativeAOT without reflection.

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

- `NativeBlobAssetReference<T>(ReadOnlySpan<byte>, int alignment = 16)` makes one aligned native copy, with no GC pinning. Prefer it at runtime; the input can be a file read or a larger span's slice. Dispose it after upload or use; a finalizer frees undisposed memory.
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
- **Validate after opening.** Before indexing trusted blob memory, check required counts and indices, such as draw ranges and joint indices. Dispose the reference before throwing on invalid data.
- **Deterministic bytes.** The same input builds the same bytes, so a blob can live in a source tree beside what it was made from and be fingerprinted by hash.

## Read blob headers by reference

`BlobArray<T>`, `BlobString<TEncoding>` and `BlobPtr<T>` hold offsets **relative to their own address**. Copying a header makes its offset point outside the blob, silently returning wrong data. Avoid these accidental copies:

```csharp
// WRONG: `in` makes `blob` a readonly reference; calling a non-readonly member on
// blob.Draws forces a defensive COPY of the array header.
static void Check(in MeshBlob blob) { var n = blob.Draws[0].IndexCount; }

// WRONG: a `readonly` member on the struct does the same to every array it touches.
public readonly float FirstVertex => Vertices[0];

// WRONG: passing a BlobString (or BlobArray) by value to a helper.
static string? NameOf(BlobString<UTF8Encoding> name) => name.ToString();
```

Do this instead:

```csharp
static void Check(ref MeshBlob blob) { ref var draw = ref blob.Draws[0]; var n = draw.IndexCount; }
public float FirstVertex => Vertices[0];                      // mutable receiver
var name = node.Name.ToString();                               // read in place
```

Access relative-offset data through a mutable `ref`; avoid readonly receivers and by-value helpers.

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
