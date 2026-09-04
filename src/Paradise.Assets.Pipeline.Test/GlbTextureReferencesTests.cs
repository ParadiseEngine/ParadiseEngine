using System.Text;
using System.Text.Json.Nodes;

using Paradise.Authoring;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// A GLB names its textures by identity in <c>images[i].extras.paradise</c>, and the uri is a hint
/// the pipeline can catch up — the same rule documents follow, kept inside the file the DCC owns.
/// </summary>
public class GlbTextureReferencesTests
{
    private static readonly Guid s_rust = Guid.Parse("11111111-2222-4333-8444-555555555555");

    [Test]
    public async Task an_unstamped_external_image_reads_with_no_reference()
    {
        var glb = Glb("""{"images":[{"uri":"../textures/rust.png"}]}""");

        var images = GlbTextureReferences.Read(glb);

        await Assert.That(images.Count).IsEqualTo(1);
        await Assert.That(images[0].Uri).IsEqualTo("../textures/rust.png");
        await Assert.That(images[0].Reference).IsNull();
    }

    [Test]
    public async Task stamping_writes_the_reference_beside_the_uri_and_reads_it_back()
    {
        var glb = Glb("""{"images":[{"uri":"../textures/rust.png"}]}""");

        var stamped = GlbTextureReferences.Stamp(glb, _ => new AssetReference(s_rust, "textures/rust.png"));

        var images = GlbTextureReferences.Read(stamped);
        await Assert.That(images[0].Reference).IsEqualTo(new AssetReference(s_rust, "textures/rust.png"));
        var extras = Read(stamped)["images"]![0]!["extras"]!["paradise"]!;
        await Assert.That(extras["guid"]!.GetValue<string>()).IsEqualTo("11111111-2222-4333-8444-555555555555");
        await Assert.That(extras["path"]!.GetValue<string>()).IsEqualTo("textures/rust.png");
    }

    [Test]
    public async Task an_already_stamped_image_is_left_alone_and_bytes_come_back_as_themselves()
    {
        var glb = GlbTextureReferences.Stamp(
            Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""), _ => new AssetReference(s_rust, "textures/rust.png"));

        var again = GlbTextureReferences.Stamp(glb, _ => new AssetReference(Guid.NewGuid(), "textures/other.png"));

        await Assert.That(ReferenceEquals(again, glb)).IsTrue();
    }

    [Test]
    public async Task a_uri_the_resolver_cannot_place_stays_unstamped()
    {
        var glb = Glb("""{"images":[{"uri":"../textures/gone.png"}]}""");

        var stamped = GlbTextureReferences.Stamp(glb, _ => null);

        await Assert.That(ReferenceEquals(stamped, glb)).IsTrue();
        await Assert.That(GlbTextureReferences.Read(stamped)[0].Reference).IsNull();
    }

    [Test]
    public async Task embedded_and_data_images_carry_nothing()
    {
        var glb = Glb("""{"images":[{"bufferView":0},{"uri":"data:image/png;base64,AA=="}]}""");

        await Assert.That(GlbTextureReferences.Read(glb)).IsEmpty();
        await Assert.That(ReferenceEquals(GlbTextureReferences.Stamp(glb, _ => new AssetReference(s_rust, "x.png")), glb)).IsTrue();
    }

    [Test]
    public async Task following_a_moved_texture_rewrites_the_uri_and_the_stamp_path()
    {
        // The texture moved in Finder: its sidecar travelled, so the guid still names it at the
        // new place. The uri is what Blender follows, so it moves too.
        var glb = GlbTextureReferences.Stamp(
            Glb("""{"images":[{"uri":"../textures/rust.png","mimeType":"image/png"}]}"""),
            _ => new AssetReference(s_rust, "textures/rust.png"));

        var followed = GlbTextureReferences.FollowUris(glb, "models/crate.glb", _ => "textures/metal/rust.png");

        var images = GlbTextureReferences.Read(followed);
        await Assert.That(images[0].Uri).IsEqualTo("../textures/metal/rust.png");
        await Assert.That(images[0].Reference).IsEqualTo(new AssetReference(s_rust, "textures/metal/rust.png"));
        // Nothing else moves: the mime type and the binary chunk are the file's own.
        await Assert.That(Read(followed)["images"]![0]!["mimeType"]!.GetValue<string>()).IsEqualTo("image/png");
    }

    [Test]
    public async Task a_texture_that_did_not_move_leaves_the_bytes_as_they_were()
    {
        var glb = GlbTextureReferences.Stamp(
            Glb("""{"images":[{"uri":"../textures/rust.png"}]}"""), _ => new AssetReference(s_rust, "textures/rust.png"));

        var followed = GlbTextureReferences.FollowUris(glb, "models/crate.glb", reference => reference.Path);

        await Assert.That(ReferenceEquals(followed, glb)).IsTrue();
    }

    [Test]
    public async Task a_stamp_that_is_not_reference_shaped_reads_as_absent()
    {
        // Half a stamp is no stamp: re-stamping it is the repair, and reading it as a reference
        // would carry a guid nobody can check.
        var glb = Glb("""{"images":[{"uri":"t.png","extras":{"paradise":{"guid":"not-a-guid","path":"t.png"}}}]}""");

        await Assert.That(GlbTextureReferences.Read(glb)[0].Reference).IsNull();
    }

    [Test]
    [Arguments("models/crate.glb", "../textures/rust.png", "textures/rust.png")]
    [Arguments("models/crate.glb", "rust.png", "models/rust.png")]
    [Arguments("models/props/crate.glb", "../../textures/a%20b.png", "textures/a b.png")]
    [Arguments("crate.glb", "textures/rust.png", "textures/rust.png")]
    public async Task a_uri_resolves_to_the_assets_relative_path(string glbPath, string uri, string expected)
    {
        await Assert.That(GlbTextureReferences.AssetPathFor(glbPath, uri)).IsEqualTo(expected);
    }

    [Test]
    public async Task a_uri_that_climbs_out_of_assets_resolves_to_nothing()
    {
        await Assert.That(GlbTextureReferences.AssetPathFor("models/crate.glb", "../../etc/passwd")).IsNull();
    }

    [Test]
    [Arguments("models/crate.glb", "textures/rust.png", "../textures/rust.png")]
    [Arguments("models/crate.glb", "models/rust.png", "rust.png")]
    [Arguments("models/props/crate.glb", "textures/a b.png", "../../textures/a%20b.png")]
    [Arguments("crate.glb", "textures/rust.png", "textures/rust.png")]
    [Arguments("models/crate.glb", "models/props/rust.png", "props/rust.png")]
    public async Task an_assets_relative_path_becomes_the_uri_a_glb_writes(string glbPath, string assetPath, string expected)
    {
        await Assert.That(GlbTextureReferences.UriFor(glbPath, assetPath)).IsEqualTo(expected);
    }

    [Test]
    public async Task bytes_that_are_not_a_glb_read_as_nothing_and_are_returned_unchanged()
    {
        var bytes = Encoding.UTF8.GetBytes("not a glb");

        await Assert.That(GlbTextureReferences.Read(bytes)).IsEmpty();
        await Assert.That(ReferenceEquals(GlbTextureReferences.Stamp(bytes, _ => null), bytes)).IsTrue();
    }

    internal static byte[] Glb(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var padded = payload.Length % 4 == 0 ? payload : [.. payload, .. Enumerable.Repeat((byte)' ', 4 - payload.Length % 4)];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write(12 + 8 + padded.Length);
        writer.Write(padded.Length);
        writer.Write(0x4E4F534Au);
        writer.Write(padded);
        writer.Flush();
        return stream.ToArray();
    }

    private static JsonObject Read(byte[] glb)
    {
        GlbBinary.TryRead(glb, out var gltf, out _);
        return gltf;
    }
}
