using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Paradise.Assets.Gltf;
using Paradise.Assets.Mesh;
using Paradise.Assets.Packing;
using Paradise.Assets.Packing.Benchmarks;
using Paradise.Assets.Pipeline;

using Zio;
using Zio.FileSystems;

// This host cooks the optional SafariPark corpus; the runtime smoke hosts depend only on Packing.
if (args is ["--build", var buildDirectory, var resultDirectory])
{
    BuildCorpusBenchmark.Run(buildDirectory, resultDirectory);
    return;
}

if (args.Length != 3) throw new ArgumentException("Arguments: <engine root> <GLB directory> <output directory>, or --build <build directory> <output directory>");
using var storage = new PhysicalFileSystem();
var engine = storage.ConvertPathFromInternal(Path.GetFullPath(args[0]));
var input = storage.ConvertPathFromInternal(Path.GetFullPath(args[1]));
var output = storage.ConvertPathFromInternal(Path.GetFullPath(args[2]));
storage.CreateDirectory(output);
using var corpus = new MemoryFileSystem();
using var originals = new MemoryFileSystem();
var skipped = new List<string>();
var textureHashes = new HashSet<string>(StringComparer.Ordinal);
foreach (var path in storage.EnumerateFiles(input, "*.glb").OrderBy(path => path.FullName, StringComparer.Ordinal))
{
    byte[] bytes = storage.ReadAllBytes(path);
    originals.WriteAllBytes("/" + path.GetName(), bytes);
    string name = path.GetNameWithoutExtension() ?? throw new InvalidDataException("GLB filename is empty.");
    CollectTextures(bytes, name);
    Console.WriteLine("Cooking " + path.GetName());
    CookedGlb cooked;
    try { cooked = GltfCook.Cook(GltfSceneReader.ReadGeometry(NormalizeSafariGlb(bytes))); }
    catch (InvalidDataException error) when (error.Message.Contains("CUBICSPLINE", StringComparison.Ordinal)
        || error.Message.Contains("skins; a Paradise mesh carries one skeleton", StringComparison.Ordinal))
    {
        skipped.Add($"{path.GetName()}: {error.Message}");
        continue;
    }
    var mesh = cooked.Mesh;
    if (mesh.Skin is { } skin) mesh = mesh with { Skin = skin with { Skeleton = $"animations/{name}.skeleton" } };
    Add($"meshes/{name}.mesh", MeshBlobFormat.Write(mesh));
    if (cooked.Skeleton is { } skeleton)
    {
        Add($"animations/{name}.skeleton", skeleton);
        for (int i = 0; i < cooked.Clips.Count; i++) Add($"animations/{name}-{i}.clip", GltfCook.BuildClip(cooked.Clips[i], skeleton));
    }

    // Representative authored config documents, explicitly synthetic rather than measured game content.
    Add($"documents/{name}.toml", Encoding.UTF8.GetBytes($"name = \"{name}\"\nmesh = \"meshes/{name}.mesh\"\nposition = [0.0, 1.0, 0.0]\nscale = 1.0\nvisible = true\n"));
}

foreach (var texture in storage.EnumerateFiles(engine / "src/Assets/Paradise.Assets.Textures.Test/fixtures", "*.ktx2"))
    Add("textures/" + texture.GetName(), storage.ReadAllBytes(texture));
Add("documents/empty", []);
Add("documents/零.txt", "UTF-8 path round trip\n"u8.ToArray());

var report = new StringBuilder();
report.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
foreach (string reason in skipped) report.AppendLine("Excluded from cooked corpus: " + reason);
report.AppendLine("256 KiB chunks, Zstandard level 3, SHA-256 verification enabled. Warm in-memory storage; excludes disk, network and GPU upload. Median of 7 runs after 2 warmups; managed allocation per run, pooled buffers may be reused.");
report.AppendLine("category,files,raw_bytes,pack_bytes,loose_ms,packed_ms,loose_alloc_bytes,packed_alloc_bytes,random_4KiB_us,random_alloc_bytes");
foreach (string category in new[] { "meshes", "animations", "documents", "textures" })
{
    UPath[] paths = corpus.EnumerateFiles("/" + category, "*", SearchOption.AllDirectories).ToArray();
    byte[] bytes = WritePack(corpus, paths);
    using var packed = new AssetPackFileSystem(bytes);
    foreach (var path in paths)
        if (!SHA256.HashData(corpus.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(packed.ReadAllBytes(path))))
            throw new InvalidDataException("Round-trip mismatch: " + path);
    var loose = Measure(() => { foreach (var path in paths) _ = corpus.ReadAllBytes(path); });
    var compressed = Measure(() => { foreach (var path in paths) _ = packed.ReadAllBytes(path); });
    byte[] block = new byte[4096];
    var nonempty = paths.Where(path => packed.GetFileLength(path) > 0).ToArray();
    var random = Measure(() =>
    {
        foreach (var path in nonempty)
        {
            using var stream = packed.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            for (int i = 0; i < 16; i++)
            {
                long at = (long)i * 104729 % stream.Length;
                stream.Position = at;
                stream.ReadExactly(block.AsSpan(0, (int)Math.Min(block.Length, stream.Length - at)));
            }
        }
    });
    report.AppendLine(FormattableString.Invariant($"{category},{paths.Length},{paths.Sum(corpus.GetFileLength)},{bytes.Length},{loose.Milliseconds:F3},{compressed.Milliseconds:F3},{loose.Allocated},{compressed.Allocated},{random.Milliseconds * 1000 / Math.Max(1, nonempty.Length * 16):F3},{random.Allocated / Math.Max(1, nonempty.Length * 16)}"));
}

var checks = new StringBuilder();
foreach (var path in corpus.EnumerateFiles("/", "*", SearchOption.AllDirectories).OrderBy(path => path.FullName, StringComparer.Ordinal))
    checks.Append(CultureInfo.InvariantCulture, $"{Convert.ToHexStringLower(SHA256.HashData(corpus.ReadAllBytes(path)))}\t{corpus.GetFileLength(path)}\t{path.FullName[1..]}\n");
Add("_smoke/checksums.tsv", Encoding.UTF8.GetBytes(checks.ToString()));
byte[] fixture = WritePack(corpus, corpus.EnumerateFiles("/", "*", SearchOption.AllDirectories));
storage.WriteAllBytes(output / "smoke.pak", fixture);
byte[] safari = WritePack(originals, originals.EnumerateFiles("/"));
using (var roundTrip = new AssetPackFileSystem(safari))
{
    foreach (var path in originals.EnumerateFiles("/"))
        if (!roundTrip.ReadAllBytes(path).AsSpan().SequenceEqual(originals.ReadAllBytes(path)))
            throw new InvalidDataException("SafariPark callback bytes changed: " + path);
}
storage.WriteAllBytes(output / "safari-assets.pak", safari);
report.AppendLine(CultureInfo.InvariantCulture, $"smoke.pak: {fixture.Length} bytes, SHA-256 {Convert.ToHexStringLower(SHA256.HashData(fixture))}");
storage.WriteAllText(output / "benchmark.txt", report.ToString());
Console.WriteLine(report);

void Add(string path, byte[] bytes)
{
    var full = new UPath("/" + path);
    corpus.CreateDirectory(full.GetDirectory());
    corpus.WriteAllBytes(full, bytes);
}

void CollectTextures(byte[] bytes, string name)
{
    if (!GlbBinary.TryRead(bytes, out var document, out var bin)) throw new InvalidDataException("Invalid GLB fixture.");
    if (document["images"] is not System.Text.Json.Nodes.JsonArray images) return;
    for (int i = 0; i < images.Count; i++)
    {
        var image = images[i]!.AsObject();
        if (image["mimeType"]?.GetValue<string>() != "image/ktx2" || image["bufferView"] is not { } viewIndex) continue;
        var view = document["bufferViews"]![viewIndex.GetValue<int>()]!;
        int offset = view["byteOffset"]?.GetValue<int>() ?? 0;
        int length = view["byteLength"]!.GetValue<int>();
        byte[] texture = bin.AsSpan(offset, length).ToArray();
        if (textureHashes.Add(Convert.ToHexStringLower(SHA256.HashData(texture))))
            Add($"textures/{name}-{i}.ktx2", texture);
    }
}

static byte[] WritePack(IFileSystem files, IEnumerable<UPath> paths)
{
    using var stream = new MemoryStream();
    AssetPackWriter.Write(files, stream, paths.Select(path => new AssetPackFile(path.FullName[1..], path)));
    return stream.ToArray();
}

static byte[] NormalizeSafariGlb(byte[] bytes)
{
    // SafariPark's host removes stale data-URI copies of the BIN buffer from these exports.
    // Apply the same normalization for cooking; the original GLBs in safari-assets.pak stay intact.
    if (!GlbBinary.TryRead(bytes, out var document, out var bin)) throw new InvalidDataException("Invalid GLB fixture.");
    bool changed = false;
    if (document["buffers"] is System.Text.Json.Nodes.JsonArray buffers && buffers.Count == 1 && bin.Length > 0)
    {
        var buffer = buffers[0]!.AsObject();
        if (buffer["uri"]?.GetValue<string>() is { } uri && uri.StartsWith("data:", StringComparison.Ordinal))
        {
            buffer.Remove("uri");
            changed = true;
        }
    }

    return changed ? GlbBinary.Write(document, bin) : bytes;
}

static (double Milliseconds, long Allocated) Measure(Action run)
{
    run();
    run();
    var times = new double[7];
    var allocations = new long[7];
    for (int i = 0; i < times.Length; i++)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        run();
        times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocations[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
    }

    Array.Sort(times);
    Array.Sort(allocations);
    return (times[3], allocations[3]);
}
