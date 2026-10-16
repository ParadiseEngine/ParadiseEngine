using System.Text;

namespace Paradise.Assets.Project.Test;

/// <summary>Pins cache keys to the Blender addon's digest format.</summary>
/// <remarks>
/// <c>paradise_blender/pipeline/cache.py:digest</c> shares the engine's <c>.editor/cache/</c> keys.
/// Vectors were independently computed with GNU <c>sha256sum</c>: concatenate UTF-8 or byte parts,
/// each prefixed by its byte length as an eight-byte little-endian integer, then hash the stream.
/// A one-sided format change splits the shared cache into incompatible entry sets.
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
        // An empty part still contributes its length prefix.
        await Assert.That(ArtifactDigest.Compute()).IsNotEqualTo(ArtifactDigest.Compute(""));
    }

    [Test]
    public async Task part_boundaries_change_the_digest()
    {
        // Image bytes and encoder arguments must retain their separate boundaries.
        await Assert.That(ArtifactDigest.Compute("ab", "c")).IsNotEqualTo(ArtifactDigest.Compute("a", "bc"));
        await Assert.That(ArtifactDigest.Compute("ab", "c")).IsNotEqualTo(ArtifactDigest.Compute("abc"));
    }

    [Test]
    public async Task strings_hash_as_their_utf8_bytes()
    {
        // Multibyte characters make the UTF-8 byte length differ from the character count.
        const string text = "ktx cr\u00E9ation \u2014 unicode";
        await Assert.That(ArtifactDigest.Compute(text))
            .IsEqualTo(ArtifactDigest.Compute(Encoding.UTF8.GetBytes(text)));
    }

    [Test]
    public async Task digest_is_sixty_four_lowercase_hex_digits()
    {
        var digest = ArtifactDigest.Compute("anything");
        await Assert.That(digest.Length).IsEqualTo(64);
        await Assert.That(digest.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')).IsTrue();
    }

    [Test]
    public async Task byte_parts_of_the_same_content_agree_regardless_of_how_they_are_wrapped()
    {
        byte[] bytes = [1, 2, 3, 250];
        await Assert.That(ArtifactDigest.Compute(bytes))
            .IsEqualTo(ArtifactDigest.Compute(DigestPart.FromBytes(bytes.AsMemory())));
    }
}
