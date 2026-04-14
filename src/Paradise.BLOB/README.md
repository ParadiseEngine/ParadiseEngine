# Paradise.BLOB

`Paradise.BLOB` is a .NET blob builder for immutable unmanaged data layouts. It provides Unity-style blob primitives and builders that work in plain .NET code.

## Install

```bash
dotnet add package Paradise.BLOB
```

## Features

- Build immutable unmanaged roots with `ValueBuilder<T>` and `StructBuilder<T>`.
- Store arrays, strings, pointers, trees, sorted arrays, and dynamically typed payloads.
- Read blobs through raw bytes or pinned `ManagedBlobAssetReference<T>` handles.
- Keep offsets and alignment correct without hand-rolling binary layouts.
- Friendly to regular .NET apps and NativeAOT-oriented workflows.

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

using var blob = builder.CreateManagedBlobAssetReference();

Console.WriteLine(blob.Value.Name.ToString());
Console.WriteLine(string.Join(", ", blob.Value.Values.ToArray()));
Console.WriteLine(blob.Value.MaxValue.Value);
```

## Common builders

- `ArrayBuilder<T>` and `SetArray(...)` for contiguous unmanaged arrays.
- `StringBuilder<TEncoding>` and `SetString(...)` for encoded blob strings.
- `PtrBuilderWithNewValue<T>` and `SetPointer(...)` for blob pointers.
- `TreeBuilder<T>` and `AnyTreeBuilder` for preordered trees with subtree end indices.
- `SortedArrayBuilder<TKey, TValue>` for hash-ordered key/value lookup tables.

## Notes

- Blob roots and referenced values must be unmanaged.
- Dispose `ManagedBlobAssetReference<T>` when you are done with it because it pins the backing byte array.
- Use `CreateBlob()` if you want the raw serialized bytes for storage or transport.
