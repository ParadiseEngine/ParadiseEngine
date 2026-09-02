using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Paradise.Assets.Project;

/// <summary>
/// The cache key: SHA-256 over 8-byte little-endian length-prefixed parts, a byte-for-byte port
/// of <c>paradise_blender/pipeline/cache.py:digest</c> pinned by fixed test vectors. Length
/// prefixes keep <c>("ab", "c")</c> and <c>("a", "bc")</c> distinct, which for an image plus its
/// argv is the difference between two inputs and one cache entry. A key must be the COMPLETE
/// input of the step it skips: a key missing an input serves last week's artifact as a hit.
/// </summary>
public static class ArtifactDigest
{
    private const int LengthPrefixBytes = 8;

    public static string Compute(params ReadOnlySpan<DigestPart> parts)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> lengthPrefix = stackalloc byte[LengthPrefixBytes];
        foreach (var part in parts)
        {
            var bytes = part.Bytes.Span;
            BinaryPrimitives.WriteUInt64LittleEndian(lengthPrefix, (ulong)bytes.Length);
            hasher.AppendData(lengthPrefix);
            hasher.AppendData(bytes);
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        hasher.GetCurrentHash(hash);
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>One part of a cache key, mirroring the Python signature's <c>bytes | str</c>.</summary>
public readonly struct DigestPart
{
    private DigestPart(ReadOnlyMemory<byte> bytes) => Bytes = bytes;

    public ReadOnlyMemory<byte> Bytes { get; }

    public static DigestPart FromBytes(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>UTF-8 without BOM, matching Python's <c>str.encode("utf-8")</c>.</summary>
    public static DigestPart FromString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new DigestPart(Encoding.UTF8.GetBytes(text));
    }

    /// <inheritdoc cref="FromString"/>
    public static implicit operator DigestPart(string text) => FromString(text);

    /// <inheritdoc cref="FromBytes"/>
    public static implicit operator DigestPart(byte[] bytes) => FromBytes(bytes);

    /// <inheritdoc cref="FromBytes"/>
    public static implicit operator DigestPart(ReadOnlyMemory<byte> bytes) => FromBytes(bytes);
}
