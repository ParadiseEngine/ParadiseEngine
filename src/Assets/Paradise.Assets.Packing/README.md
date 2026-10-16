# Paradise.Assets.Packing

Read-only Zio filesystems over independently compressed Zstandard chunks. Targets .NET 10,
including desktop NativeAOT and browser WASM, with ZstdSharp.Port 0.8.8.

```csharp
using Paradise.Assets.Packing;
using Zio;
using Zio.FileSystems;

using var storage = new PhysicalFileSystem();
using var assets = new AssetPackFileSystem(storage,
    storage.ConvertPathFromInternal(Path.GetFullPath("build/assets.pak")));
using var input = assets.OpenFile("/models/crate.mesh", FileMode.Open, FileAccess.Read, FileShare.Read);
input.Seek(1024, SeekOrigin.Begin);
byte[] range = new byte[128];
await input.ReadExactlyAsync(range, cancellationToken);
```

The filesystem reports decoded sizes. Each open stream has its own cursor, decoder and one
decoded-chunk cache. `Read` may return a partial result at a chunk boundary; use `ReadExactly`
for fixed records. Independent streams may be used concurrently; a single stream requires
normal caller serialization. There is no shared decompression cache or coordination layer.

The backing storage is borrowed and must outlive readers. Do not modify pack files in place.
Storage readers allow rename/delete sharing: existing streams survive publication of a new
pack, but the mount rejects new opens if its backing file's length or footer changed. Remount
after publication. The memory constructor retains its `ReadOnlyMemory<byte>` without copying;
callers must not change the underlying bytes. Dispose streams to release buffers and decoders.
An open stream remains usable after its filesystem is disposed.

See [the format, build configuration, SafariPark examples and validation commands](../../../docs/asset-packs.md).
