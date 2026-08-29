using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Paradise.Assets.Project;

/// <summary>
/// The cache key function: SHA-256 over length-prefixed parts.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a cross-language contract, not an implementation detail.</b> It is a port of the
/// Blender addon's <c>paradise_blender/pipeline/cache.py:digest</c>, and the two must agree
/// byte-for-byte, because both tools read and write the same <c>.editor/cache/</c> entries. A
/// change here is a change there, and the fixed vectors in the test suite exist to make a
/// one-sided change fail.
/// </para>
/// <para>
/// Each part is prefixed with its length as 8 bytes little-endian, so <c>("ab", "c")</c> and
/// <c>("a", "bc")</c> hash differently. That is not pedantry: the parts of a real key are an
/// image's bytes and the command line that encodes it, and a boundary confusion between them is
/// exactly how two different inputs would collide onto one cache entry. Strings are encoded
/// UTF-8, with no byte-order mark.
/// </para>
/// <para>
/// The rule that governs every caller: <b>a key must be the COMPLETE input of the step it
/// skips.</b> A key that misses an input does not fail — it serves last week's artifact and
/// reports success. Where an input cannot be observed cheaply and exactly, there is no cache.
/// </para>
/// </remarks>
public static class ArtifactDigest
{
    /// <summary>Bytes of little-endian length written before each part.</summary>
    private const int LengthPrefixBytes = 8;

    /// <summary>
    /// Hashes <paramref name="parts"/> into a lowercase hex SHA-256 string, the form used as a
    /// cache entry filename.
    /// </summary>
    /// <param name="parts">
    /// The complete input of the step being keyed. Strings and byte arrays convert implicitly.
    /// </param>
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

/// <summary>
/// One part of a cache key: either raw bytes or a string, mirroring the Python signature's
/// <c>bytes | str</c>.
/// </summary>
public readonly struct DigestPart
{
    private DigestPart(ReadOnlyMemory<byte> bytes) => Bytes = bytes;

    /// <summary>The part's bytes, as they are fed to the hash.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Wraps raw bytes — file contents, a serialized payload.</summary>
    public static DigestPart FromBytes(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>
    /// Encodes text as UTF-8 without a byte-order mark, which is what Python's
    /// <c>str.encode("utf-8")</c> produces.
    /// </summary>
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
