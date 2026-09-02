using System.Text;
using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The mesh half of the texture story: a source GLB names the PNG an author has, and the built
/// GLB has to name the KTX2 the texture step wrote in its place.
/// </summary>
public class MeshTextureReferencesTests
{
    [Test]
    public async Task an_external_png_reference_becomes_the_ktx2_beside_it()
    {
        var glb = Glb("""{"images":[{"uri":"../textures/rust.png","mimeType":"image/png","name":"rust"}],"textures":[{"source":0}]}""");

        var rewrite = MeshTextureReferences.Rewrite(glb);

        // The relative path is preserved and only the extension moves: the texture step writes
        // its output at the source's own place in the tree, so the reference still resolves.
        var gltf = Read(rewrite.Glb);
        await Assert.That(Images(rewrite.Glb)[0]!["uri"]!.GetValue<string>()).IsEqualTo("../textures/rust.ktx2");
        await Assert.That(Images(rewrite.Glb)[0]!["mimeType"]!.GetValue<string>()).IsEqualTo("image/ktx2");
        await Assert.That(rewrite.Sources).IsEquivalentTo(new[] { "../textures/rust.png" });
        // image/ktx2 is only valid under KHR_texture_basisu — the one contract every KTX2 the
        // pipeline writes follows (#207), so readers other than Paradise's accept the mesh.
        await Assert.That(gltf["textures"]![0]!["extensions"]!["KHR_texture_basisu"]!["source"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(gltf["textures"]![0]!["source"]).IsNull();
        await Assert.That(gltf["extensionsUsed"]!.AsArray().Select(n => n!.GetValue<string>())).Contains("KHR_texture_basisu");
        await Assert.That(gltf["extensionsRequired"]!.AsArray().Select(n => n!.GetValue<string>())).Contains("KHR_texture_basisu");
    }

    [Test]
    public async Task an_authored_ktx2_reference_is_declared_like_a_repointed_one()
    {
        // One answer for every external KTX2 the built mesh names: an author-supplied .ktx2 is
        // under the same extension as one the texture step wrote, or the file would carry the
        // undeclared image/ktx2 shape #207 removed everywhere else.
        var glb = Glb("""{"images":[{"uri":"t.png"},{"uri":"other.ktx2","mimeType":"image/ktx2"}],"textures":[{"source":0},{"source":1}]}""");

        var rewrite = MeshTextureReferences.Rewrite(glb);

        var gltf = Read(rewrite.Glb);
        await Assert.That(gltf["textures"]![0]!["extensions"]!["KHR_texture_basisu"]).IsNotNull();
        await Assert.That(gltf["textures"]![1]!["extensions"]!["KHR_texture_basisu"]!["source"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(gltf["textures"]![1]!["source"]).IsNull();
        // Only the PNG is a source to compile; the authored KTX2 is already what ships.
        await Assert.That(rewrite.Sources).IsEquivalentTo(new[] { "t.png" });
    }

    [Test]
    public async Task an_image_no_texture_uses_leaves_the_mesh_unchanged()
    {
        var glb = Glb("""{"images":[{"uri":"other.ktx2","mimeType":"image/ktx2"}]}""");

        var rewrite = MeshTextureReferences.Rewrite(glb);

        await Assert.That(rewrite.Sources.Count).IsEqualTo(0);
        await Assert.That(Read(rewrite.Glb)["extensionsUsed"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(Read(rewrite.Glb)["extensionsRequired"]).IsNull();
    }

    [Test]
    public async Task jpeg_references_are_rewritten_too()
    {
        var glb = Glb("""{"images":[{"uri":"a.jpg","mimeType":"image/jpeg"},{"uri":"b.jpeg","mimeType":"image/jpeg"}]}""");

        var rewrite = MeshTextureReferences.Rewrite(glb);

        await Assert.That(Images(rewrite.Glb)[0]!["uri"]!.GetValue<string>()).IsEqualTo("a.ktx2");
        await Assert.That(Images(rewrite.Glb)[1]!["uri"]!.GetValue<string>()).IsEqualTo("b.ktx2");
        await Assert.That(rewrite.Sources.Count).IsEqualTo(2);
    }

    [Test]
    public async Task other_members_of_the_document_survive()
    {
        // The rewrite goes through the container rather than a string replace, so everything it
        // does not touch has to come back out — including the image's own name, which is what a
        // material refers to.
        var glb = Glb("""{"asset":{"version":"2.0"},"images":[{"uri":"t.png","name":"t"}],"meshes":[{"name":"crate"}]}""");

        var gltf = Read(MeshTextureReferences.Rewrite(glb).Glb);

        await Assert.That(gltf["asset"]!["version"]!.GetValue<string>()).IsEqualTo("2.0");
        await Assert.That(gltf["meshes"]![0]!["name"]!.GetValue<string>()).IsEqualTo("crate");
        await Assert.That(gltf["images"]![0]!["name"]!.GetValue<string>()).IsEqualTo("t");
    }

    [Test]
    public async Task the_binary_chunk_is_carried_through_untouched()
    {
        // Geometry is the whole point of the file. A rewrite that repacked or dropped the BIN
        // chunk would produce a mesh that resolves its textures and renders nothing.
        var bin = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var glb = Glb("""{"images":[{"uri":"t.png"}]}""", bin);

        GlbBinary.TryRead(MeshTextureReferences.Rewrite(glb).Glb, out _, out var rewritten);

        await Assert.That(rewritten).IsEquivalentTo(bin);
    }

    [Test]
    public async Task a_reference_that_is_already_ktx2_is_no_source_and_keeps_its_uri()
    {
        var glb = Glb("""{"images":[{"uri":"../textures/rust.ktx2","mimeType":"image/ktx2"}],"textures":[{"source":0}]}""");

        var rewrite = MeshTextureReferences.Rewrite(glb);

        await Assert.That(rewrite.Sources.Count).IsEqualTo(0);
        await Assert.That(Images(rewrite.Glb)[0]!["uri"]!.GetValue<string>()).IsEqualTo("../textures/rust.ktx2");
        await Assert.That(Read(rewrite.Glb)["textures"]![0]!["extensions"]!["KHR_texture_basisu"]).IsNotNull();
    }

    [Test]
    public async Task rewriting_twice_changes_nothing_the_second_time()
    {
        var once = MeshTextureReferences.Rewrite(Glb("""{"images":[{"uri":"t.png","mimeType":"image/png"}]}"""));

        var twice = MeshTextureReferences.Rewrite(once.Glb);

        await Assert.That(twice.Sources.Count).IsEqualTo(0);
        await Assert.That(twice.Glb).IsEquivalentTo(once.Glb);
    }

    [Test]
    public async Task an_embedded_image_is_not_a_reference()
    {
        // bufferView-backed images have no path to repoint — they are the externalization step's
        // problem, and BuildRunner refuses them before this ever runs.
        var glb = Glb("""{"images":[{"bufferView":0,"mimeType":"image/png"}]}""");

        var rewrite = MeshTextureReferences.Rewrite(glb);

        await Assert.That(rewrite.Sources.Count).IsEqualTo(0);
        await Assert.That(rewrite.Glb).IsEquivalentTo(glb);
    }

    [Test]
    public async Task a_data_uri_is_not_a_reference()
    {
        // Its bytes are in the document, so there is no source file to compile — and no path that
        // would mean anything after an extension swap.
        var glb = Glb("""{"images":[{"uri":"data:image/png;base64,iVBORw0KGgo=","mimeType":"image/png"}]}""");

        await Assert.That(MeshTextureReferences.Rewrite(glb).Sources.Count).IsEqualTo(0);
    }

    [Test]
    public async Task a_mesh_with_no_images_comes_back_as_itself()
    {
        var glb = Glb("""{"meshes":[{"name":"crate"}]}""");

        var rewrite = MeshTextureReferences.Rewrite(glb);

        await Assert.That(rewrite.Sources.Count).IsEqualTo(0);
        await Assert.That(rewrite.Glb).IsEquivalentTo(glb);
    }

    [Test]
    public async Task bytes_that_are_not_a_glb_are_returned_unchanged()
    {
        // Naming the file is the caller's job, so this reports nothing rather than throwing.
        var notAGlb = Encoding.UTF8.GetBytes("this is not a mesh");

        var rewrite = MeshTextureReferences.Rewrite(notAGlb);

        await Assert.That(rewrite.Sources.Count).IsEqualTo(0);
        await Assert.That(rewrite.Glb).IsEquivalentTo(notAGlb);
    }

    private static JsonArray Images(byte[] glb) => (JsonArray)Read(glb)["images"]!;

    private static JsonObject Read(byte[] glb)
    {
        GlbBinary.TryRead(glb, out var gltf, out _);
        return gltf;
    }

    private static byte[] Glb(string json, byte[]? bin = null)
        => GlbBinary.Write(JsonNode.Parse(json)!.AsObject(), bin ?? []);
}
