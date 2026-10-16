using System.Buffers.Binary;
using System.Security.Cryptography;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Packing.Test;

public class AssetPackTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(1023)]
    [Arguments(1024)]
    [Arguments(1025)]
    [Arguments(3091)]
    public async Task round_trip_preserves_lengths_and_bytes(int length)
    {
        byte[] original = Data(length);
        using var pack = new AssetPackFileSystem(Pack(("folder/asset.bin", original)));
        using var input = pack.OpenFile("/folder/asset.bin", FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.That(input.Length).IsEqualTo((long)length);
        await Assert.That(pack.GetFileLength("/folder/asset.bin")).IsEqualTo((long)length);
        await Assert.That(pack.ReadAllBytes("/folder/asset.bin").SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task seeks_across_chunks_and_beyond_eof_match_uncompressed_bytes()
    {
        byte[] original = Data(13 * 1024 + 17);
        using var pack = new AssetPackFileSystem(Pack(("a", original)));
        using var input = pack.OpenFile("/a", FileMode.Open, FileAccess.Read, FileShare.Read);
        var random = new Random(47);
        for (int i = 0; i < 100; i++)
        {
            int offset = i == 0 ? 1020 : random.Next(original.Length);
            int length = Math.Min(2050, original.Length - offset);
            input.Seek(offset, SeekOrigin.Begin);
            byte[] actual = new byte[length];
            input.ReadExactly(actual);
            await Assert.That(actual.AsSpan().SequenceEqual(original.AsSpan(offset, length))).IsTrue();
        }

        await Assert.That(input.Seek(-17, SeekOrigin.End)).IsEqualTo((long)original.Length - 17);
        input.Seek(100, SeekOrigin.End);
        await Assert.That(input.ReadByte()).IsEqualTo(-1);
        await Assert.That(() => input.Seek(-1, SeekOrigin.Begin)).Throws<IOException>();
        input.Position = 0;
        await Assert.That(input.ReadByte()).IsEqualTo((int)original[0]);
    }

    [Test]
    public async Task asynchronous_reads_and_cancellation_preserve_position()
    {
        byte[] original = Data(4097);
        using var pack = new AssetPackFileSystem(Pack(("a", original)));
        using var input = pack.OpenFile("/a", FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] actual = new byte[original.Length];
        await Assert.That(async () => await input.ReadAsync(actual, new CancellationToken(true)).ConfigureAwait(false)).Throws<OperationCanceledException>();
        await Assert.That(input.Position).IsEqualTo(0L);
        await input.ReadExactlyAsync(actual).ConfigureAwait(false);
        await Assert.That(actual.SequenceEqual(original)).IsTrue();
        await Assert.That(await input.ReadAsync(actual).ConfigureAwait(false)).IsEqualTo(0);
    }

    [Test]
    public async Task independent_readers_survive_mount_disposal()
    {
        byte[] original = Data(12 * 1024 + 3);
        var pack = new AssetPackFileSystem(Pack(("a", original)));
        using var first = pack.OpenFile("/a", FileMode.Open, FileAccess.Read, FileShare.Read);
        using var second = pack.OpenFile("/a", FileMode.Open, FileAccess.Read, FileShare.Read);
        pack.Dispose();
        await Assert.That(() => pack.OpenFile("/a", FileMode.Open, FileAccess.Read, FileShare.Read)).Throws<ObjectDisposedException>();
        await Task.WhenAll(Read(first), Read(second)).ConfigureAwait(false);
        first.Dispose();
        await Assert.That(() => first.ReadByte()).Throws<ObjectDisposedException>();

        async Task Read(Stream input)
        {
            using var result = new MemoryStream();
            await input.CopyToAsync(result).ConfigureAwait(false);
            await Assert.That(result.ToArray().SequenceEqual(original)).IsTrue();
        }
    }

    [Test]
    public async Task mounts_enumeration_and_cross_filesystem_copies_see_decoded_content()
    {
        byte[] original = Data(3000);
        using var pack = new AssetPackFileSystem(Pack(("ui/a.bin", original), ("ui/fonts/f.bin", Data(9)), ("root.bin", [])));
        using var mount = new MountFileSystem(owned: false);
        mount.Mount("/content", pack);
        using var sub = new SubFileSystem(mount, "/content/ui", owned: false);
        using var destination = new MemoryFileSystem();
        sub.CopyFileCross("/a.bin", destination, "/copied", overwrite: false, copyAttributes: false);
        await Assert.That(destination.ReadAllBytes("/copied").SequenceEqual(original)).IsTrue();
        sub.CopyFileCross("/a.bin", destination, "/with-attributes", overwrite: false, copyAttributes: true);
        await Assert.That(destination.ReadAllBytes("/with-attributes").SequenceEqual(original)).IsTrue();
        await Assert.That(destination.GetAttributes("/with-attributes").HasFlag(FileAttributes.ReadOnly)).IsTrue();
        await Assert.That(sub.EnumerateFiles("/", "*.bin", SearchOption.AllDirectories).Count()).IsEqualTo(2);
        await Assert.That(sub.EnumerateFiles("/", "*.bin", SearchOption.TopDirectoryOnly).Count()).IsEqualTo(1);
        await Assert.That(pack.EnumerateDirectories("/", "*", SearchOption.AllDirectories).Count()).IsEqualTo(2);
        await Assert.That(pack.EnumerateItems("/ui", SearchOption.AllDirectories).Single(item => item.Path == "/ui/a.bin").Length).IsEqualTo(3000L);
        await Assert.That(pack.ResolvePath("/ui/a.bin").FileSystem == pack).IsTrue();
        await Assert.That(pack.CanWatch("/")).IsFalse();
        await Assert.That(() => pack.Watch("/")).Throws<NotSupportedException>();
        await Assert.That(() => pack.WriteAllText("/new", "no")).Throws<IOException>();
        await Assert.That(() => pack.DeleteFile("/ui/a.bin")).Throws<IOException>();
        await Assert.That(() => pack.OpenFile("/missing", FileMode.Open, FileAccess.Read, FileShare.Read)).Throws<FileNotFoundException>();
        await Assert.That(() => pack.EnumerateFiles("/missing").ToArray()).Throws<DirectoryNotFoundException>();
    }

    [Test]
    public async Task writer_is_deterministic_and_stores_incompressible_and_ktx2_chunks()
    {
        byte[] noise = new byte[2048];
        new Random(32).NextBytes(noise);
        byte[] bytes = Pack(("c.ktx2", Data(2048)), ("b", noise), ("a", Data(2048)));
        byte[] reordered = Pack(("a", Data(2048)), ("c.ktx2", Data(2048)), ("b", noise));
        await Assert.That(bytes.SequenceEqual(reordered)).IsTrue();
        using var input = new MemoryStream(bytes);
        using var reader = new BinaryReader(input);
        input.Position = TocOffset(bytes);
        int files = reader.ReadInt32();
        int chunks = reader.ReadInt32();
        for (int i = 0; i < files; i++) { int size = reader.ReadInt32(); input.Position += size + 16; }
        var codecs = new List<int>();
        for (int i = 0; i < chunks; i++) { input.Position += 16; codecs.Add(reader.ReadInt32()); input.Position += 32; }
        await Assert.That(codecs.SequenceEqual(new[] { 1, 1, 0, 0, 0, 0 })).IsTrue();
    }

    [Test]
    [Arguments("../a")]
    [Arguments("/a")]
    [Arguments("a//b")]
    [Arguments("a/./b")]
    [Arguments("a\\b")]
    [Arguments("C:/a")]
    public async Task writer_rejects_noncanonical_paths(string path)
        => await Assert.That(() => Pack((path, Data(1)))).Throws<InvalidDataException>();

    [Test]
    public async Task writer_rejects_duplicate_and_file_directory_collisions()
    {
        await Assert.That(() => Pack(("a", []), ("a", []))).Throws<InvalidDataException>();
        await Assert.That(() => Pack(("a", []), ("a/b", []))).Throws<InvalidDataException>();
        using var pack = new AssetPackFileSystem(Pack());
        await Assert.That(pack.DirectoryExists("/")).IsTrue();
        await Assert.That(pack.EnumerateFiles("/").Any()).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(8)]
    [Arguments(12)]
    [Arguments(16)]
    public async Task invalid_header_is_rejected(int position)
    {
        byte[] bytes = Pack(("a", Data(2048)));
        bytes[position] ^= 0xFF;
        await Assert.That(() => new AssetPackFileSystem(bytes)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task corrupt_metadata_and_truncation_are_rejected()
    {
        byte[] bytes = Pack(("a", Data(2048)));
        bytes[TocOffset(bytes) + 12] ^= 1;
        await Assert.That(() => new AssetPackFileSystem(bytes)).Throws<InvalidDataException>();
        await Assert.That(() => new AssetPackFileSystem(bytes.AsMemory(0, bytes.Length - 1))).Throws<InvalidDataException>();
        await Assert.That(() => new AssetPackFileSystem(new byte[10])).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(0, int.MaxValue)] // entry count
    [Arguments(8, int.MaxValue)] // path size
    [Arguments(13, -1)] // logical length
    [Arguments(21, 1)] // first chunk
    [Arguments(25, 0)] // chunk count
    [Arguments(29, 0)] // physical offset
    [Arguments(37, int.MaxValue)] // stored size
    [Arguments(41, 1023)] // decoded size
    [Arguments(45, 2)] // codec
    public async Task invalid_toc_fields_are_rejected_even_with_a_valid_checksum(int offset, int value)
    {
        byte[] bytes = Pack(("a", Data(2048)));
        RewriteToc(bytes, toc => BinaryPrimitives.WriteInt32LittleEndian(toc.AsSpan(offset), value));
        await Assert.That(() => new AssetPackFileSystem(bytes)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task duplicate_or_escaping_toc_paths_are_rejected()
    {
        byte[] duplicate = Pack(("a", []), ("b", []));
        RewriteToc(duplicate, toc => toc[33] = (byte)'a');
        await Assert.That(() => new AssetPackFileSystem(duplicate)).Throws<InvalidDataException>();
        byte[] escaping = Pack(("a", []));
        RewriteToc(escaping, toc => toc[12] = (byte)'.');
        await Assert.That(() => new AssetPackFileSystem(escaping)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("a")]
    [Arguments("a.ktx2")]
    public async Task corrupt_payload_is_rejected_before_exposing_bytes(string name)
    {
        byte[] bytes = Pack((name, Data(2048)));
        bytes[24] ^= 0x80;
        using var pack = new AssetPackFileSystem(bytes);
        using var input = pack.OpenFile("/" + name, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] actual = Enumerable.Repeat((byte)255, 1024).ToArray();
        await Assert.That(() => input.Read(actual)).Throws<InvalidDataException>();
        await Assert.That(actual.All(value => value == 255)).IsTrue();
        await Assert.That(input.Position).IsEqualTo(0L);
    }

    [Test]
    public async Task parallel_disk_readers_use_independent_positions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "paradise-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] original = Data(128 * 1024 + 31);
            string path = Path.Combine(directory, "assets.pak");
            File.WriteAllBytes(path, Pack(("a", original)));
            using var disk = new PhysicalFileSystem();
            using var pack = new AssetPackFileSystem(disk, disk.ConvertPathFromInternal(path));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async number =>
            {
                using var input = pack.OpenFile("/a", FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] result = new byte[3072];
                for (int i = 0; i < 12; i++)
                {
                    int offset = (number * 1387 + i * 419) % (original.Length - result.Length);
                    input.Position = offset;
                    await input.ReadExactlyAsync(result).ConfigureAwait(false);
                    await Assert.That(result.AsSpan().SequenceEqual(original.AsSpan(offset, result.Length))).IsTrue();
                }
            })).ConfigureAwait(false);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static byte[] Data(int length) => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    [Test]
    public async Task random_reads_touch_one_chunk_and_reuse_only_the_streams_cached_chunk()
    {
        using var files = new MemoryFileSystem();
        files.CreateDirectory("/storage");
        files.WriteAllBytes("/storage/pack", Pack(("a.ktx2", Data(100 * 1024))));
        using var observed = new ObservedStorage(files);
        using var pack = new AssetPackFileSystem(observed, "/pack");
        observed.Reads.Clear();
        using var input = pack.OpenFile("/a.ktx2", FileMode.Open, FileAccess.Read, FileShare.Read);
        input.Position = 72 * 1024 + 13;
        _ = input.ReadByte();
        _ = input.ReadByte();
        await Assert.That(observed.Reads.SequenceEqual(new[] { (24L + 72 * 1024, 1024) })).IsTrue();
        input.Position = 0;
        _ = input.ReadByte();
        await Assert.That(observed.Reads.Count).IsEqualTo(2);
        await Assert.That(observed.Reads[1]).IsEqualTo((24L, 1024));
    }

    [Test]
    public async Task cancelled_source_read_can_be_retried_without_reusing_a_partial_chunk()
    {
        byte[] original = Data(3073);
        using var files = new MemoryFileSystem();
        files.CreateDirectory("/storage");
        files.WriteAllBytes("/storage/pack", Pack(("a", original)));
        using var observed = new ObservedStorage(files);
        using var pack = new AssetPackFileSystem(observed, "/pack");
        using var input = pack.OpenFile("/a", FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = input.ReadByte();
        input.Position = 1024;
        observed.CancelNextAsync = true;
        byte[] actual = new byte[100];
        await Assert.That(async () => await input.ReadAsync(actual).ConfigureAwait(false)).Throws<OperationCanceledException>();
        await Assert.That(input.Position).IsEqualTo(1024L);
        await Assert.That(actual.All(value => value == 0)).IsTrue();
        await input.ReadExactlyAsync(actual).ConfigureAwait(false);
        await Assert.That(actual.AsSpan().SequenceEqual(original.AsSpan(1024, 100))).IsTrue();
        input.Position = 0;
        await Assert.That(input.ReadByte()).IsEqualTo((int)original[0]);
    }

    [Test]
    public async Task frame_size_must_agree_with_the_toc_before_any_bytes_are_returned()
    {
        byte[] bytes = Pack(("a", Data(500)));
        RewriteToc(bytes, toc =>
        {
            BinaryPrimitives.WriteInt64LittleEndian(toc.AsSpan(13), 501);
            BinaryPrimitives.WriteInt32LittleEndian(toc.AsSpan(29 + 12), 501);
        });
        using var pack = new AssetPackFileSystem(bytes);
        await Assert.That(() => pack.ReadAllBytes("/a")).Throws<InvalidDataException>();
    }

    private sealed class ObservedStorage(IFileSystem files) : SubFileSystem(files, "/storage", owned: false)
    {
        public List<(long Offset, int Count)> Reads { get; } = [];
        public bool CancelNextAsync { get; set; }

        protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
            => new ObservedStream(base.OpenFileImpl(path, mode, access, share), this);
    }

    private sealed class ObservedStream(Stream input, ObservedStorage owner) : Stream
    {
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => input.CanSeek;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => input.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            long at = input.Position;
            int count = input.Read(buffer);
            owner.Reads.Add((at, count));
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (owner.CancelNextAsync)
            {
                owner.CancelNextAsync = false;
                _ = input.Read(buffer.Span[..(buffer.Length / 2)]);
                return ValueTask.FromCanceled<int>(new CancellationToken(true));
            }

            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }

    private static byte[] Pack(params (string Path, byte[] Bytes)[] files)
    {
        using var source = new MemoryFileSystem();
        var entries = new List<AssetPackFile>();
        for (int i = 0; i < files.Length; i++)
        {
            var path = new UPath("/source" + i);
            source.WriteAllBytes(path, files[i].Bytes);
            entries.Add(new AssetPackFile(files[i].Path, path));
        }

        using var result = new MemoryStream();
        AssetPackWriter.Write(source, result, entries, new AssetPackOptions { ChunkSize = 1024 });
        return result.ToArray();
    }

    private static int TocOffset(byte[] pack) => checked((int)BinaryPrimitives.ReadInt64LittleEndian(pack.AsSpan(pack.Length - 56)));

    private static void RewriteToc(byte[] pack, Action<byte[]> change)
    {
        int offset = TocOffset(pack);
        byte[] toc = pack[offset..^64];
        change(toc);
        toc.CopyTo(pack, offset);
        SHA256.HashData(toc).CopyTo(pack, pack.Length - 40);
    }
}
