using System.Text;

namespace Paradise.Assets.Project.Test;

/// <summary>
/// Parity guard for the cache key function.
/// </summary>
/// <remarks>
/// <para>
/// The digests below are FIXED VECTORS pinning byte-for-byte parity with the Blender addon's
/// <c>paradise_blender/pipeline/cache.py:digest</c>. Both tools address the same
/// <c>.editor/cache/</c>, so a one-sided change to the scheme does not produce a wrong answer —
/// it produces a permanent cache miss on one side and a silent divergence of two entry sets.
/// </para>
/// <para>
/// They were computed from the algorithm cache.py documents (SHA-256 over parts, each preceded
/// by its length as 8 bytes little-endian, strings encoded UTF-8), by an implementation that is
/// neither of the two under test — GNU coreutils <c>sha256sum</c> fed the concatenated stream.
/// The equivalent Python check, for anyone revisiting this:
/// </para>
/// <code>
/// python -c "import hashlib
/// def digest(*parts):
///     h = hashlib.sha256()
///     for p in parts:
///         raw = p.encode('utf-8') if isinstance(p, str) else p
///         h.update(len(raw).to_bytes(8, 'little')); h.update(raw)
///     return h.hexdigest()
/// print(digest('ab', 'c'))"
/// </code>
/// </remarks>
public class ArtifactDigestTests
{
    private const string EmptyInput = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string OneEmptyPart = "af5570f5a1810b7af78caf4bc70a660f0df51e42baf91d4de5b2328de0e83dfc";
    private const string AbThenC = "43ee655579de01ca739b3f95c1c2d3f46d353b2c0df818064ea594506cdb2617";
    private const string AThenBc = "9a8acca1b6c6c0befd3fbc756aed625da998c998f7252e738c4ef061906b9b21";
    private const string Abc = "ce91dc5eec0139adf091900d225971d6ad246a845bad791b5693a9d0d55dd391";
    private const string BytesAndArgv = "256d5830d3b737dad5fdb3d186b7610122f68eef12fa48aef2c9df2126615ee6";

    [Test]
    public async Task fixed_vectors_pin_parity_with_cache_py()
    {
        await Assert.That(ArtifactDigest.Compute()).IsEqualTo(EmptyInput);
        await Assert.That(ArtifactDigest.Compute("")).IsEqualTo(OneEmptyPart);
        await Assert.That(ArtifactDigest.Compute("abc")).IsEqualTo(Abc);
        await Assert.That(ArtifactDigest.Compute("ab", "c")).IsEqualTo(AbThenC);
        await Assert.That(ArtifactDigest.Compute("a", "bc")).IsEqualTo(AThenBc);
        await Assert.That(ArtifactDigest.Compute("paradise", "ktx create --encode uastc --zcmp 18"))
            .IsEqualTo(BytesAndArgv);
    }

    [Test]
    public async Task no_parts_differs_from_one_empty_part()
    {
        // The length prefix is written even for a zero-length part, so "nothing" and "one empty
        // thing" are distinguishable inputs. Dropping the prefix would collapse them.
        await Assert.That(ArtifactDigest.Compute()).IsNotEqualTo(ArtifactDigest.Compute(""));
    }

    [Test]
    public async Task part_boundaries_change_the_digest()
    {
        // The failure this prevents: an image's bytes running into the argv that encodes them,
        // so two different (bytes, argv) pairs collide onto one cache entry.
        await Assert.That(ArtifactDigest.Compute("ab", "c")).IsNotEqualTo(ArtifactDigest.Compute("a", "bc"));
        await Assert.That(ArtifactDigest.Compute("ab", "c")).IsNotEqualTo(ArtifactDigest.Compute("abc"));
    }

    [Test]
    public async Task strings_hash_as_their_utf8_bytes()
    {
        // Built from code points rather than written literally, so the case does not depend on
        // how this source file happens to be decoded: an accented letter (2 UTF-8 bytes) and an
        // em dash (3), both of which make the length prefix differ from the character count.
        var text = "ktx cr" + (char)0x00E9 + "ation " + (char)0x2014 + " unicode";
        await Assert.That(ArtifactDigest.Compute(text))
            .IsEqualTo(ArtifactDigest.Compute(Encoding.UTF8.GetBytes(text)));
    }

    [Test]
    public async Task digest_is_lowercase_hex_of_thirty_two_bytes()
    {
        var digest = ArtifactDigest.Compute("anything");
        await Assert.That(digest.Length).IsEqualTo(64);
        await Assert.That(digest).IsEqualTo(digest.ToLowerInvariant());
    }

    [Test]
    public async Task byte_parts_of_the_same_content_agree_regardless_of_how_they_are_wrapped()
    {
        byte[] bytes = [1, 2, 3, 250];
        await Assert.That(ArtifactDigest.Compute(bytes))
            .IsEqualTo(ArtifactDigest.Compute(DigestPart.FromBytes(bytes.AsMemory())));
    }
}
