using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Packing.Smoke;

/// <summary>The identical checks compiled for desktop NativeAOT and browser WASM.</summary>
public static class SmokeCheck
{
    public static async Task<string> VerifyBytesAsync(byte[] bytes)
    {
        using var pack = new AssetPackFileSystem(bytes);
        string result = await VerifyAsync(pack).ConfigureAwait(false);
        return $"{result}; pack SHA-256 {Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    public static async Task<string> VerifyAsync(IFileSystem pack)
    {
        var clock = Stopwatch.StartNew();
        int count = 0;
        long total = 0;
        foreach (string line in pack.ReadAllText("/_smoke/checksums.tsv").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\t');
            var path = new UPath("/" + fields[2]);
            long length = long.Parse(fields[1], CultureInfo.InvariantCulture);
            byte[] bytes = pack.ReadAllBytes(path);
            Require(bytes.LongLength == length && pack.GetFileLength(path) == length, "logical length " + path);
            Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == fields[0], "SHA-256 " + path);
            using var stream = pack.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (long at in new[] { Math.Max(0, length - 4097), Math.Min(length, 256 * 1024 - 3), 0 })
            {
                stream.Seek(at, SeekOrigin.Begin);
                var slice = new byte[(int)Math.Min(4097, length - at)];
                await stream.ReadExactlyAsync(slice).ConfigureAwait(false);
                Require(slice.AsSpan().SequenceEqual(bytes.AsSpan((int)at, slice.Length)), "seek/async " + path);
            }

            stream.Seek(0, SeekOrigin.End);
            Require(stream.ReadByte() == -1, "EOF " + path);
            count++;
            total += length;
        }

        // Exercise the managed compressor as well as the decoder on both target runtimes.
        using var source = new MemoryFileSystem();
        byte[] boundary = Enumerable.Range(0, 256 * 1024 + 17).Select(i => (byte)(i % 251)).ToArray();
        source.WriteAllBytes("/boundary", boundary);
        source.WriteAllBytes("/stored.ktx2", boundary);
        source.WriteAllBytes("/empty", []);
        AssetPackFile[] entries = [new("boundary", "/boundary"), new("stored.ktx2", "/stored.ktx2"), new("empty", "/empty")];
        using var written = new MemoryStream();
        AssetPackWriter.Write(source, written, entries);
        using var second = new MemoryStream();
        AssetPackWriter.Write(source, second, entries.Reverse());
        Require(written.ToArray().AsSpan().SequenceEqual(second.ToArray()), "deterministic writer");
        using var roundTrip = new AssetPackFileSystem(written.ToArray());
        Require(roundTrip.ReadAllBytes("/boundary").AsSpan().SequenceEqual(boundary), "compressed round trip");
        Require(roundTrip.ReadAllBytes("/stored.ktx2").AsSpan().SequenceEqual(boundary), "stored round trip");
        Require(roundTrip.GetFileLength("/empty") == 0, "empty asset");
        return $"PACK-OK: {count} assets, {total} decoded bytes, {clock.Elapsed.TotalMilliseconds:F1} ms";
    }

    private static void Require(bool passed, string check)
    {
        if (!passed) throw new InvalidDataException("PACK-FAIL: " + check);
    }
}
