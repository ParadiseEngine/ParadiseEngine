using System.Text;
using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>The reading half every mesh format needs and the writing half only some have: which external files a container names, and spelling a new uri into one that can be written.</summary>
public class MeshContainerTests
{
    [Test]
    public async Task external_images_are_read_by_slot_and_uri()
    {
        var glb = Glb("""{"images":[{"uri":"../textures/rust.png"},{"bufferView":0},{"uri":"data:image/png;base64,AA=="},{"uri":"t.png"}]}""");

        var named = MeshContainer.Read("/game/assets/models/crate.glb", glb);

        // Embedded and data: images are not files; they occupy their index but name nothing.
        await Assert.That(named).IsEquivalentTo(new[]
        {
            new ContainerReference("images[0]", "../textures/rust.png"),
            new ContainerReference("images[3]", "t.png"),
        });
    }

    [Test]
    public async Task rewriting_a_uri_touches_only_that_slot_and_leaves_the_binary_chunk()
    {
        var glb = Glb("""{"images":[{"uri":"../textures/rust.png","mimeType":"image/png"},{"uri":"t.png"}]}""");

        var rewritten = MeshContainer.RewriteUris("/game/assets/models/crate.glb", glb, new Dictionary<string, string> { ["images[0]"] = "../textures/metal/rust.png" });

        var images = Read(rewritten)["images"]!.AsArray();
        await Assert.That(images[0]!["uri"]!.GetValue<string>()).IsEqualTo("../textures/metal/rust.png");
        await Assert.That(images[0]!["mimeType"]!.GetValue<string>()).IsEqualTo("image/png");
        await Assert.That(images[1]!["uri"]!.GetValue<string>()).IsEqualTo("t.png");
    }

    [Test]
    public async Task a_uri_that_already_agrees_leaves_the_bytes_as_they_were()
    {
        var glb = Glb("""{"images":[{"uri":"../textures/rust.png"}]}""");

        var rewritten = MeshContainer.RewriteUris("/game/assets/models/crate.glb", glb, new Dictionary<string, string> { ["images[0]"] = "../textures/rust.png" });

        await Assert.That(ReferenceEquals(rewritten, glb)).IsTrue();
    }

    [Test]
    public async Task a_format_that_cannot_be_written_reads_as_nothing_and_is_returned_unchanged()
    {
        var bytes = Encoding.UTF8.GetBytes("Kaydara FBX Binary");

        await Assert.That(MeshContainer.CanRewrite("/game/assets/models/crate.fbx")).IsFalse();
        await Assert.That(MeshContainer.Read("/game/assets/models/crate.fbx", bytes)).IsEmpty();
        await Assert.That(ReferenceEquals(MeshContainer.RewriteUris("/game/assets/models/crate.fbx", bytes, new Dictionary<string, string> { ["images[0]"] = "x" }), bytes)).IsTrue();
    }

    [Test]
    [Arguments("models/crate.glb", "../textures/rust.png", "textures/rust.png")]
    [Arguments("models/crate.glb", "rust.png", "models/rust.png")]
    [Arguments("models/props/crate.glb", "../../textures/a%20b.png", "textures/a b.png")]
    [Arguments("crate.glb", "textures/rust.png", "textures/rust.png")]
    public async Task a_uri_resolves_to_the_assets_relative_path(string containerPath, string uri, string expected)
    {
        await Assert.That(MeshContainer.AssetPathFor(containerPath, uri)).IsEqualTo(expected);
    }

    [Test]
    public async Task a_uri_that_climbs_out_of_assets_resolves_to_nothing()
    {
        await Assert.That(MeshContainer.AssetPathFor("models/crate.glb", "../../etc/passwd")).IsNull();
    }

    [Test]
    [Arguments("models/crate.glb", "textures/rust.png", "../textures/rust.png")]
    [Arguments("models/crate.glb", "models/rust.png", "rust.png")]
    [Arguments("models/props/crate.glb", "textures/a b.png", "../../textures/a%20b.png")]
    [Arguments("crate.glb", "textures/rust.png", "textures/rust.png")]
    [Arguments("models/crate.glb", "models/props/rust.png", "props/rust.png")]
    public async Task an_assets_relative_path_becomes_the_uri_a_container_writes(string containerPath, string assetPath, string expected)
    {
        await Assert.That(MeshContainer.UriFor(containerPath, assetPath)).IsEqualTo(expected);
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
