using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Paradise.Assets.Pipeline;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Packing.Benchmarks;

/// <summary>Measures an existing cooked manifest without modifying or recooking the project.</summary>
internal static class BuildCorpusBenchmark
{
    internal static void Run(string buildDirectory, string resultDirectory)
    {
        using var storage = new PhysicalFileSystem();
        var build = storage.ConvertPathFromInternal(Path.GetFullPath(buildDirectory));
        var output = storage.ConvertPathFromInternal(Path.GetFullPath(resultDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(output.FullName.TrimEnd('/'), build.FullName.TrimEnd('/'), comparison)
            || output.FullName.StartsWith(build.FullName.TrimEnd('/') + "/", comparison))
            throw new ArgumentException("The benchmark output must be outside the source build tree.");
        using var source = new SubFileSystem(storage, build, owned: false);
        var manifest = BuildManifest.Load(source, "/manifest.json");
        if (manifest.Version != BuildManifest.CurrentVersion) throw new InvalidDataException("Unsupported build manifest.");
        var expected = new Dictionary<string, (long Length, string Hash)>(StringComparer.Ordinal);
        var included = new List<AssetPackFile>();
        long looseBytes = 0;
        int looseFiles = 0;
        foreach (var asset in manifest.Assets)
        {
            ValidatePath(asset.Path);
            var path = new UPath("/" + asset.Path);
            using var stream = source.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (stream.Length != asset.Size || !string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Built file does not match the manifest: " + asset.Path);
            if (asset.Path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)
                || asset.Path.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
            {
                looseBytes += asset.Size;
                looseFiles++;
            }
            else
            {
                expected.Add(asset.Path, (asset.Size, hash));
                included.Add(new AssetPackFile(asset.Path, path));
            }
        }

        string manifestHash;
        using (var stream = source.OpenFile("/manifest.json", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            manifestHash = Convert.ToHexStringLower(SHA256.HashData(stream));
            expected.Add("manifest.json", (stream.Length, manifestHash));
        }

        included.Add(new AssetPackFile("manifest.json", "/manifest.json"));
        long rawBytes = expected.Values.Sum(file => file.Length);
        storage.CreateDirectory(output);
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Project: {manifest.Project}; recorded profile: '{manifest.Profile}'");
        report.AppendLine("Existing built files; no recooking or conversion to a different build profile.");
        report.AppendLine("Source: " + Path.GetFullPath(buildDirectory));
        report.AppendLine("Manifest SHA-256: " + manifestHash);
        report.AppendLine(CultureInfo.InvariantCulture, $"Packed files including manifest: {included.Count}; decoded bytes: {rawBytes}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Loose Wwise files: {looseFiles}; bytes: {looseBytes}");
        report.AppendLine("ZstdSharp.Port 0.8.8, level 3; KTX2 stored raw; full SHA-256 verification of sources and decoded output.");
        report.AppendLine("Complete pack sizes include the header, paths, TOC, chunk checksums and footer. Combined sizes add loose Wwise files, not executable code or assets outside the build manifest.");
        report.AppendLine("chunk_KiB\traw_bytes\tpack_bytes\tpack_percent\tsaved_percent\tmetadata_bytes\tcompressed_chunks\tstored_chunks\tcombined_raw_bytes\tcombined_packed_bytes\tcombined_saved_percent\twrite_ms");
        foreach (int chunkSize in new[] { 64 * 1024, 256 * 1024, 1024 * 1024 })
        {
            string name = $"assets-{chunkSize / 1024}KiB";
            var path = output / (name + ".pak");
            Console.WriteLine($"Packing {manifest.Project}: {chunkSize / 1024} KiB chunks, {included.Count} files...");
            var clock = Stopwatch.StartNew();
            using (var stream = storage.OpenFile(path, FileMode.Create, FileAccess.Write, FileShare.None))
                AssetPackWriter.Write(source, stream, included, new AssetPackOptions { ChunkSize = chunkSize });
            clock.Stop();
            using (var packed = new AssetPackFileSystem(storage, path))
            {
                foreach (var file in included)
                {
                    using var stream = packed.OpenFile("/" + file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                    var original = expected[file.Path];
                    if (stream.Length != original.Length || hash != original.Hash)
                        throw new InvalidDataException("Pack round-trip mismatch: " + file.Path);
                }
            }

            List<PackRow> rows = ReadValidatedToc(storage, path);
            long size = storage.GetFileLength(path);
            long metadata = 96 + rows.Sum(row => row.MetadataBytes);
            if (rows.Sum(row => row.StoredBytes) + metadata != size || rows.Sum(row => row.RawBytes) != rawBytes)
                throw new InvalidDataException("Pack accounting does not match the validated container.");
            report.AppendLine(FormattableString.Invariant($"{chunkSize / 1024}\t{rawBytes}\t{size}\t{100.0 * size / rawBytes:F3}\t{100.0 * (rawBytes - size) / rawBytes:F3}\t{metadata}\t{rows.Sum(row => row.CompressedChunks)}\t{rows.Sum(row => row.StoredChunks)}\t{rawBytes + looseBytes}\t{size + looseBytes}\t{100.0 * (rawBytes - size) / (rawBytes + looseBytes):F3}\t{clock.Elapsed.TotalMilliseconds:F1}"));
            var detail = new StringBuilder("path\tcategory\traw_bytes\tstored_payload_bytes\tentry_and_chunk_metadata_bytes\tcompressed_chunks\tstored_chunks\n");
            foreach (var row in rows)
                detail.AppendLine(FormattableString.Invariant($"{row.Path}\t{Category(row.Path)}\t{row.RawBytes}\t{row.StoredBytes}\t{row.MetadataBytes}\t{row.CompressedChunks}\t{row.StoredChunks}"));
            storage.WriteAllText(output / (name + ".tsv"), detail.ToString());
            if (chunkSize == 256 * 1024)
            {
                var categories = new StringBuilder("category\tfiles\traw_bytes\tstored_payload_bytes\tentry_and_chunk_metadata_bytes\tsaved_percent_including_entry_metadata\n");
                foreach (var group in rows.GroupBy(row => Category(row.Path)).OrderBy(group => group.Key, StringComparer.Ordinal))
                {
                    long raw = group.Sum(row => row.RawBytes);
                    long stored = group.Sum(row => row.StoredBytes);
                    long toc = group.Sum(row => row.MetadataBytes);
                    categories.AppendLine(FormattableString.Invariant($"{group.Key}\t{group.Count()}\t{raw}\t{stored}\t{toc}\t{(raw == 0 ? 0 : 100.0 * (raw - stored - toc) / raw):F3}"));
                }

                categories.AppendLine("Shared header, footer and TOC counts: 96 bytes, not attributed to categories.");
                storage.WriteAllText(output / "categories-256KiB.tsv", categories.ToString());
                using var stream = storage.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                storage.WriteAllText(output / "assets-256KiB.sha256", Convert.ToHexStringLower(SHA256.HashData(stream)) + "\n");
            }
        }

        report.AppendLine("Every packed file matched its original manifest length and SHA-256 at all three chunk sizes.");
        storage.WriteAllText(output / "compression.txt", report.ToString());
        Console.WriteLine(report);
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
            || path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Noncanonical manifest path: " + path);
        string root = path.Split('/')[0];
        if (root.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || root.Equals("assets.pak", StringComparison.OrdinalIgnoreCase)
            || root.Equals(".build-index.json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reserved build metadata was listed as an asset: " + path);
    }

    private static string Category(string path) => path == "manifest.json" ? "manifest" : Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mesh" or ".skinnedmesh" => "meshes",
        ".anim" or ".skeleton" or ".clip" => "animations",
        ".toml" or ".json" or ".prefab" or ".material" => "documents",
        ".ktx2" => "textures",
        ".glb" => "GLBs",
        ".navmesh" => "navigation",
        _ => "other",
    };

    // Accounting only: AssetPackFileSystem has already validated this V1 TOC and every payload.
    // Keep these offsets beside the format documentation, rather than adding a runtime API just for a benchmark.
    private static List<PackRow> ReadValidatedToc(IFileSystem storage, UPath path)
    {
        using var stream = storage.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 8;
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Benchmark accounting requires V1.");
        stream.Position = stream.Length - 64 + 8;
        long tocOffset = reader.ReadInt64();
        stream.Position = tocOffset;
        int count = reader.ReadInt32();
        _ = reader.ReadInt32();
        var entries = new (string Path, long Raw, int First, int Count, int PathBytes)[count];
        for (int i = 0; i < entries.Length; i++)
        {
            int pathBytes = reader.ReadInt32();
            string name = Encoding.UTF8.GetString(reader.ReadBytes(pathBytes));
            entries[i] = (name, reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32(), pathBytes);
        }

        long chunksStart = stream.Position;
        var rows = new List<PackRow>(count);
        foreach (var entry in entries)
        {
            stream.Position = chunksStart + 52L * entry.First;
            long stored = 0;
            int compressed = 0;
            for (int i = 0; i < entry.Count; i++)
            {
                _ = reader.ReadInt64();
                stored += reader.ReadInt32();
                _ = reader.ReadInt32();
                if (reader.ReadInt32() == 1) compressed++;
                stream.Position += 32;
            }

            rows.Add(new PackRow(entry.Path, entry.Raw, stored, 20L + entry.PathBytes + entry.Count * 52L, compressed, entry.Count - compressed));
        }

        return rows;
    }

    private sealed record PackRow(string Path, long RawBytes, long StoredBytes, long MetadataBytes, int CompressedChunks, int StoredChunks);
}
