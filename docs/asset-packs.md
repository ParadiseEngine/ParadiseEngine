# Chunked asset packs

`Paradise.Assets.Packing` mounts an engine-owned `.pak` as a concrete, read-only Zio filesystem.
Each asset is split into independent chunks (256 KiB by default). Each compressed chunk is
one Zstandard frame, so a seek only decodes the touched chunk. No chunk spans two files.
ZstdSharp.Port 0.8.8 uses managed buffer APIs on desktop and in the browser.

## Building a release

In `assets/project.toml`:

```toml
[build.profiles.release]
pack = true
document_format = "json"
```

Run `paradise assets build --profile release`. A successful build publishes `build/assets.pak`
and then its loose `manifest.json` completion marker. Loose cooked files and the build index
remain for incremental builds. Ship the pack and any manifest-listed `.bnk`/`.wem` files at
their existing relative paths: Wwise's current native loader needs physical files.

The pack contains exactly the manifest-listed cooked outputs, excluding the banks, plus the
same `manifest.json`. The manifest keeps GUIDs, paths, SHA-256 hashes and **decoded** lengths;
physical chunk details belong only to the pack TOC. Build indexes, sidecars and temporary
files are not enumerated into the pack. Packed profiles reject importer outputs using the
root names `assets.pak`, `manifest.json`, `.build-index.json`, or the suffixes `.meta`, `.partial`
and `.tmp`, so an importer cannot publish metadata as an asset or overwrite a release artifact.

Entries use ordinal path order, fixed codec settings and no variable timestamps. Unchanged
inputs produce identical packs with this codec version. A runner reuses its last successful
pack when the serialized manifest and the complete compressed file's SHA-256 still match.
This avoids recompression and publication on unchanged watch events, while detecting missing
or corrupted packs even when their size and timestamp are unchanged.

Publication writes a complete temporary sibling, renames an existing destination to a backup,
then moves the candidate into place. If promotion fails, the backup is restored; the complete
candidate remains for recovery. If rollback also fails, both recovery paths are reported and
their files retained. Incomplete staging files are removed. Zio's copy-based `ReplaceFile` is
not used for these artifacts. A packaging/publication error fails the build and leaves no
completion manifest. A full packed rebuild removes older pack recovery files only after successful
publication; a loose profile also sweeps the obsolete pack. The destination may be briefly absent
between renames: this is not an atomic exchange, a transaction over the loose tree, or a durability
fsync. Killing the process in that window leaves the complete backup and candidate for recovery.
The backing filesystem must support same-directory renames, and one build owns an output directory
at a time. A failed run clears the incremental session, so its next retry performs full recovery
even when the watcher has no new asset events.

## Writing an explicit set of files

```csharp
using Paradise.Assets.Packing;
using Zio;

// source and output are host-owned IFileSystem instances; output is a temporary file.
using var stream = output.OpenFile("/assets.pak.partial", FileMode.Create, FileAccess.Write);
AssetPackWriter.Write(source, stream,
[
    new AssetPackFile("models/crate.mesh", "/cooked/models/crate.mesh"),
    new AssetPackFile("textures/crate.ktx2", "/cooked/textures/crate.ktx2"),
], new AssetPackOptions { ChunkSize = 256 * 1024, CompressionLevel = 3 }, cancellationToken);
```

The writer leaves the source and destination open. The destination must be writable, seekable
and distinct from every source file; its contents are replaced. The caller publishes only
after success. Zstandard level 3 is the default. A chunk is stored raw if compression does not
make it smaller, and `.ktx2` files always use raw chunks because their texture encoding already
handles compression. Small files use one chunk; empty files use none.

## Mounting in hosts

```csharp
using Paradise.Assets.Packing;
using Zio;
using Zio.FileSystems;

using var storage = new PhysicalFileSystem();
using var assets = new AssetPackFileSystem(storage,
    storage.ConvertPathFromInternal(Path.GetFullPath("build/assets.pak")));
Func<string, byte[]> readAsset = name => assets.ReadAllBytes("/" + name);
// new SafariGame(renderer, features, width, height, logger, readAsset);
```

For the browser, asynchronously fetch the whole pack before starting the synchronous asset
consumer. Pass the byte array through the host's JS export, then retain the mount:

```javascript
const response = await fetch('assets.pak');
if (!response.ok) throw new Error(`assets.pak: HTTP ${response.status}`);
program.ProvideAssetPack(new Uint8Array(await response.arrayBuffer()));
await program.InitAsync(hostModuleUrl, width, height, logLevel);
```

`ProvideAssetPack` constructs `new AssetPackFileSystem(bytes)`; `readAsset` uses `ReadAllBytes`
as above. The constructor borrows immutable memory without expanding the whole pack. Keep it
alive for the application's lifetime. JS/.NET transfer can temporarily duplicate the download.

The sibling SafariPark desktop host accepts `--assets-pack <path>`; SafariPark.Web accepts
`?pack=assets/safari-assets.pak`. Its existing defaults still load loose GLBs. The benchmark tool
below creates `safari-assets.pak` with the original GLB filenames for this example. Copy it into
the browser's deployed `assets/` directory. A cooked release pack cannot replace those source
GLBs: the sample's current loaders still expect GLBs. General runtime hosts mount the cooked
pack and use the corresponding mesh, animation and document readers.

Mounts compose with `MountFileSystem` / `SubFileSystem`; `ResolvePath` stays on the pack
filesystem, so cross-filesystem copies read decoded data. All writes and watches are rejected.
Paths are case-sensitive. Metadata timestamps are fixed to the Unix epoch. Directory entries
are synthesized from file paths; empty directories are not stored. Missing paths return Zio's
default file-time sentinel when their timestamps are queried.

Storage readers allow deletion/rename sharing on Windows so a build can publish while assets
are being read. An already open stream keeps reading the old file. Remount after publication
before opening more assets: each open checks the backing file's length and footer against the
mounted TOC and rejects a changed pack. Never modify a mounted pack in place; in-memory bytes
must remain immutable for the lifetime of their mount and streams.

## Version 1 binary layout

All integers are little endian, all lengths are bytes, and offsets start at the beginning of
the pack. Offsets and logical lengths are signed 64-bit nonnegative values. Counts, chunk
lengths, codec numbers, version and path lengths are signed 32-bit nonnegative values.

```text
24-byte header | contiguous asset chunks | uncompressed TOC | 64-byte footer
```

| Header offset | Bytes | Value |
| --- | ---: | --- |
| 0 | 8 | ASCII `PDPACK\0\0` |
| 8 | 4 | Version = 1 |
| 12 | 4 | Decoded chunk size; power of two, 1 KiB through 1 MiB |
| 16 | 8 | Reserved = 0 |

The TOC starts with `int32 assetCount, int32 chunkCount`, then all asset records, then all
chunk records. Its maximum size is 64 MiB.

| Asset field (in order) | Representation |
| --- | --- |
| Path length and path | int32 + that many strict UTF-8 bytes, maximum 4096 bytes |
| Logical length | int64 |
| First chunk index | int32 |
| Chunk count | int32 |

Paths are canonical relative names with `/` separators. Empty components, `.`, `..`, `\`,
`:`, control characters, duplicates and file/directory collisions are rejected. Asset records
are strictly ordered by ordinal .NET string comparison (UTF-16 code units). Paths are not
Unicode-normalized. Assets reference consecutive, disjoint chunk ranges in that order.

| Chunk field (in order) | Representation |
| --- | --- |
| Absolute physical offset | int64 |
| Stored length | int32 |
| Decoded length | int32 |
| Codec | int32: 0 = raw, 1 = Zstandard frame |
| Decoded content checksum | 32 SHA-256 bytes |

Every chunk except an asset's last has the header's decoded chunk size. A compressed chunk
must be smaller than its decoded bytes and contain a frame with a matching content size.
Raw chunks have equal stored and decoded sizes. Payloads are contiguous, with no alignment
padding or unindexed bytes. Chunk records are 52 bytes each.

| Footer offset | Bytes | Value |
| --- | ---: | --- |
| 0 | 8 | ASCII `PDPAKEND` |
| 8 | 8 | TOC absolute offset |
| 16 | 8 | TOC length |
| 24 | 32 | SHA-256 of the complete TOC |
| 56 | 8 | Reserved = 0 |

Mounting checks the complete TOC, bounds, version, paths and chunk layout before exposing the
filesystem. Reads check a touched chunk's decoded length and SHA-256 before copying that
chunk's data to the caller. Untouched payload corruption is discovered when read. Checksums
detect corruption; they are not signatures or authentication. Callers should abandon an
asset if a later chunk fails validation.

This container is **not** a `.zst` seekable-format file. It uses the same independent-frame
principle but has an asset-level TOC and per-chunk hashes. References:
[Zstandard seekable format](https://github.com/facebook/zstd/tree/dev/contrib/seekable_format),
[ZstdSharp 0.8.8](https://github.com/oleg-st/ZstdSharp/tree/0.8.8).

## Validation and benchmarks

From the engine root, run the Packing, Pipeline, Project and CLI test projects with
`dotnet test --project src/<subsystem>/<project>/<project>.csproj --output normal`.

The optional benchmark uses the sibling SafariPark GLBs, cooks meshes and ozz animation
archives, generates representative TOML configs, extracts distinct embedded KTX2 textures,
and includes the checked-in KTX2 fixtures.
Unsupported cubic-animation or multiple-skin GLBs are listed as exclusions in the report.
It mirrors SafariPark's removal of stale data-URI copies of BIN buffers before cooking.

```powershell
dotnet run -c Release --project src/Assets/Paradise.Assets.Packing.Benchmarks -- . ../SafariPark.Core/Assets ../.scratch/asset-pack-validation
dotnet publish src/Assets/Paradise.Assets.Packing.Smoke -c Release -r win-x64 -p:PublishAot=true -o ../.scratch/asset-pack-native
../.scratch/asset-pack-native/Paradise.Assets.Packing.Smoke.exe ../.scratch/asset-pack-validation/smoke.pak
dotnet publish src/Assets/Paradise.Assets.Packing.BrowserSmoke -c Release -o ../.scratch/asset-pack-browser
Copy-Item ../.scratch/asset-pack-validation/smoke.pak ../.scratch/asset-pack-browser/wwwroot/smoke.pak
node src/Assets/Paradise.Assets.Packing.BrowserSmoke/serve.mjs ../.scratch/asset-pack-browser/wwwroot
```

Open `http://127.0.0.1:8191`. Both hosts must report `PACK-OK` with the same pack SHA-256.
The shared smoke code checks full hashes, logical sizes, backward and cross-chunk reads,
asynchronous reads, EOF, stored/compressed round trips and deterministic writing. The browser
project requires the `wasm-tools` workload and intentionally stays outside the main solution.
For Linux desktop use `linux-x64` and the extensionless smoke executable.

`benchmark.txt` reports complete pack sizes, median warm loading time, managed allocations
and random 4 KiB read cost by asset class. It compares in-memory loose and compressed storage
with SHA-256 validation enabled. It excludes disk, network, upload and application parsing;
small KTX2 fixtures do not establish shipping texture throughput. The smoke's elapsed time
includes extra validation and writer checks and is not a browser loading benchmark.
See [the recorded validation results](asset-pack-validation.md) for the tested corpus and limits.

To measure an existing game's **actual cooked build** without modifying it, use `--build`:

```powershell
dotnet run -c Release --project src/Assets/Paradise.Assets.Packing.Benchmarks -- --build C:/proj/mcp/BackpackCult/build ../.scratch/asset-pack-encore
dotnet run -c Release --project src/Assets/Paradise.Assets.Packing.Benchmarks -- --build C:/proj/mcp/shiningpie/build ../.scratch/asset-pack-shiningpie
```

This reads only manifest-listed outputs plus the manifest, verifies the original hashes,
packs them with 64 KiB / 256 KiB / 1 MiB chunks at level 3, and verifies every decoded file.
It reports complete sizes including metadata, per-file and per-category accounting, and
combined size with Wwise files kept loose. The output directory must be outside the source
build tree. Files shipped separately by a game host are outside this measurement; the
recorded build profile is reported as-is, without recooking or pruning unused assets.

V1 has no dictionaries, shared decompression cache, HTTP range fetching or build-cache
compression. Current mesh and animation readers consume complete assets. The stream supports
partial access, but those readers need separate work to reduce total decoded asset data.
