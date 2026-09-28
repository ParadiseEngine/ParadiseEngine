using TUnit.Assertions.Enums;
using System.Text;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>What every mesh format is asked: which external files a container names, and where each uri points.</summary>
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
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task a_gltf_names_its_buffer_files_and_a_glb_does_not()
    {
        const string json = """{"images":[{"uri":"t.png"}],"buffers":[{"byteLength":4,"uri":"crate.bin"},{"byteLength":3,"uri":"data:application/octet-stream;base64,AAAA"}]}""";
        var gltf = Encoding.UTF8.GetBytes(json);

        // A data: buffer is no file; a GLB's buffers are its own BIN chunk.
        await Assert.That(MeshContainer.Read("/game/assets/models/crate.gltf", gltf)).IsEquivalentTo(new[]
        {
            new ContainerReference("images[0]", "t.png"),
            new ContainerReference("buffers[0]", "crate.bin"),
        }, CollectionOrdering.Matching);
        await Assert.That(MeshContainer.Read("/game/assets/models/crate.glb", Glb(json)).Select(named => named.Slot)).IsEquivalentTo(new[] { "images[0]" });
    }

    [Test]
    public async Task a_format_that_names_no_files_reads_as_nothing()
    {
        var bytes = Encoding.UTF8.GetBytes("Kaydara FBX Binary");

        await Assert.That(MeshContainer.Read("/game/assets/models/crate.fbx", bytes)).IsEmpty();
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
    [Arguments("../../etc/passwd")]
    [Arguments("..%2F..%2Fetc/passwd")]
    [Arguments("..\\..\\etc\\passwd")]
    [Arguments("/etc/passwd")]
    [Arguments("C:/textures/rust.png")]
    [Arguments("https://example.com/rust.png")]
    public async Task a_uri_that_leaves_assets_or_is_not_relative_resolves_to_nothing(string uri)
    {
        await Assert.That(MeshContainer.AssetPathFor("models/crate.glb", uri)).IsNull();
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
}
