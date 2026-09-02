using System.Text.Json.Nodes;

namespace Paradise.Assets.Pipeline.Test;

public class GlbTextureRewriterTests
{
    [Test]
    public async Task glb_round_trips_json_and_bin()
    {
        var gltf = new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["meshes"] = new JsonArray() };
        byte[] bin = [1, 2, 3, 4, 5];
        var path = Path.Combine(Path.GetTempPath(), $"paradise_glb_{Guid.NewGuid():N}.glb");
        try
        {
            GlbBinary.Write(path, gltf, bin);
            var read = GlbBinary.TryRead(path, out var readGltf, out var readBin);

            await Assert.That(read).IsTrue();
            await Assert.That((string?)readGltf["asset"]!["version"]).IsEqualTo("2.0");
            // BIN chunk is padded to a 4-byte boundary; the original bytes are preserved as a prefix.
            await Assert.That(readBin.Length).IsGreaterThanOrEqualTo(bin.Length);
            await Assert.That(readBin[0]).IsEqualTo((byte)1);
            await Assert.That(readBin[4]).IsEqualTo((byte)5);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task an_unknown_chunk_between_json_and_bin_is_skipped_like_the_runtime_does()
    {
        // The spec says skip; the runtime's GlbContainer skips; a build that refused the same
        // file the game would load was the disagreement issue #207 named.
        var gltf = new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" } };
        byte[] bin = [1, 2, 3, 4];
        var plain = GlbBinary.Write(gltf, bin);
        using var stream = new MemoryStream();
        stream.Write(plain, 0, 12 + 8 + (int)BitConverter.ToUInt32(plain, 12));
        // Five bytes plus three of padding: the declared length excludes padding, and a reader
        // that forgets the alignment lands inside it and reads garbage as the next header.
        byte[] vendor = [9, 9, 9, 9, 9];
        stream.Write(BitConverter.GetBytes((uint)vendor.Length));
        stream.Write(BitConverter.GetBytes(0x5A5A5A5Au));
        stream.Write(vendor);
        stream.Write(new byte[3]);
        stream.Write(plain, 12 + 8 + (int)BitConverter.ToUInt32(plain, 12), plain.Length - (12 + 8 + (int)BitConverter.ToUInt32(plain, 12)));
        var withVendorChunk = stream.ToArray();
        BitConverter.GetBytes((uint)withVendorChunk.Length).CopyTo(withVendorChunk, 8);

        await Assert.That(GlbBinary.TryRead(withVendorChunk, out var read, out var readBin)).IsTrue();
        await Assert.That(read["asset"]!["version"]!.GetValue<string>()).IsEqualTo("2.0");
        await Assert.That(readBin).IsEquivalentTo(bin);
        await Assert.That(() => Paradise.Assets.Gltf.GlbContainer.Parse(withVendorChunk).Bin.Length).IsEqualTo(4);
    }

    [Test]
    public async Task a_header_over_declaring_the_length_is_refused_as_the_runtime_refuses_it()
    {
        var plain = GlbBinary.Write(new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" } }, [1, 2, 3, 4]);
        BitConverter.GetBytes((uint)plain.Length + 8).CopyTo(plain, 8);

        await Assert.That(GlbBinary.TryRead(plain, out _, out _)).IsFalse();
        await Assert.That(() => Paradise.Assets.Gltf.GlbContainer.Parse(plain)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task a_json_chunk_longer_than_the_file_is_refused_without_allocating_it()
    {
        var plain = GlbBinary.Write(new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" } }, []);
        BitConverter.GetBytes(0x7FFFFFF0u).CopyTo(plain, 12);

        await Assert.That(GlbBinary.TryRead(plain, out _, out _)).IsFalse();
    }

    [Test]
    public async Task the_extension_is_required_only_when_a_texture_depends_on_it()
    {
        // An image no texture references gets the extension listed as used, never required:
        // requiring it would make conformant readers reject content the file does not hold.
        var orphan = new JsonObject
        {
            ["images"] = new JsonArray(new JsonObject { ["uri"] = "a.ktx2", ["mimeType"] = "image/ktx2" }),
            ["textures"] = new JsonArray(new JsonObject { ["source"] = 5 }),
        };
        GlbTextureRewriter.DeclareBasisu(orphan, new HashSet<int> { 0 });
        await Assert.That(orphan["extensionsUsed"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(orphan["extensionsRequired"]).IsNull();
        await Assert.That(orphan["textures"]![0]!["source"]!.GetValue<int>()).IsEqualTo(5);

        var noTextures = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["uri"] = "a.ktx2" }) };
        GlbTextureRewriter.DeclareBasisu(noTextures, new HashSet<int> { 0 });
        await Assert.That(noTextures["extensionsUsed"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(noTextures["extensionsRequired"]).IsNull();
    }

    [Test]
    public async Task corrupt_glb_returns_false_instead_of_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"paradise_bad_{Guid.NewGuid():N}.glb");
        File.WriteAllText(path, "not a glb");
        try
        {
            await Assert.That(GlbBinary.TryRead(path, out _, out _)).IsFalse();
            await Assert.That(GlbTextureRewriter.TryListEmbedded(File.ReadAllBytes(path), "bad", out _, out var error)).IsFalse();
            await Assert.That(error).Contains("readable GLB");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task listing_reports_each_embedded_image_with_its_bytes_preset_and_sidecar_name()
    {
        byte[] png = [1, 2, 3];
        byte[] ktx2 = [.. Ktx2Header.Identifier.ToArray(), 9, 9];
        var glb = TwoImageGlb(png, ktx2);

        await Assert.That(GlbTextureRewriter.TryListEmbedded(glb, "crate", out var images, out _)).IsTrue();

        await Assert.That(images.Count).IsEqualTo(2);
        await Assert.That(images[0].Bytes).IsEquivalentTo(png);
        await Assert.That(images[0].SourceExtension).IsEqualTo(".png");
        await Assert.That(images[0].Preset).IsEqualTo(TextureEncodingPreset.UastcNormalLinear);
        await Assert.That(images[0].PresetNote).IsNull();
        await Assert.That(images[0].SidecarName).IsEqualTo("crate_0.ktx2");
        await Assert.That(images[1].IsKtx2).IsTrue();
        await Assert.That(images[1].Bytes).IsEquivalentTo(ktx2);
        // Bound to no material slot: the name decides, and the note says so.
        await Assert.That(images[1].PresetNote).Contains("inferred");
        await Assert.That(images[1].SidecarName).IsEqualTo("crate_1.ktx2");
    }

    [Test]
    public async Task an_external_image_is_not_listed_and_a_glb_without_images_lists_nothing()
    {
        var external = GlbBinary.Write(new JsonObject { ["images"] = new JsonArray(new JsonObject { ["uri"] = "a.png" }), ["bufferViews"] = new JsonArray() }, []);
        await Assert.That(GlbTextureRewriter.TryListEmbedded(external, "x", out var images, out _)).IsTrue();
        await Assert.That(images).IsEmpty();

        var bare = GlbBinary.Write(new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" } }, []);
        await Assert.That(GlbTextureRewriter.TryListEmbedded(bare, "x", out images, out _)).IsTrue();
        await Assert.That(images).IsEmpty();
    }

    [Test]
    public async Task externalizing_drops_the_image_views_and_remaps_the_geometry_accessor()
    {
        byte[] png = [1, 2, 3];
        byte[] ktx2 = [.. Ktx2Header.Identifier.ToArray(), 9, 9];
        var glb = TwoImageGlb(png, ktx2);
        GlbTextureRewriter.TryListEmbedded(glb, "crate", out var images, out _);

        await Assert.That(GlbTextureRewriter.TryExternalize(glb, images, out var rewritten, out _)).IsTrue();

        GlbBinary.TryRead(rewritten, out var gltf, out var bin);
        var imageNodes = (JsonArray)gltf["images"]!;
        await Assert.That((string?)imageNodes[0]!["uri"]).IsEqualTo("crate_0.ktx2");
        await Assert.That((string?)imageNodes[1]!["uri"]).IsEqualTo("crate_1.ktx2");
        await Assert.That(imageNodes[0]!["bufferView"]).IsNull();
        // Only the geometry view survives, and the accessor follows it to its new index.
        var views = (JsonArray)gltf["bufferViews"]!;
        await Assert.That(views.Count).IsEqualTo(1);
        await Assert.That(gltf["accessors"]![0]!["bufferView"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(bin.AsSpan(0, 4).ToArray()).IsEquivalentTo(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        await Assert.That(gltf["buffers"]![0]!["byteLength"]!.GetValue<int>()).IsEqualTo(bin.Length);
        // Every externalised image is declared through KHR_texture_basisu, the pass-through
        // KTX2 included: image/ktx2 is only valid under the extension (#207).
        await Assert.That(gltf["textures"]![0]!["extensions"]!["KHR_texture_basisu"]!["source"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(gltf["textures"]![1]!["extensions"]!["KHR_texture_basisu"]!["source"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(gltf["textures"]![1]!["source"]).IsNull();
        await Assert.That(gltf["extensionsRequired"]!.AsArray().Count).IsEqualTo(1);

        // Idempotent: nothing embedded remains.
        await Assert.That(GlbTextureRewriter.TryListEmbedded(rewritten, "crate", out var left, out _)).IsTrue();
        await Assert.That(left).IsEmpty();
        await Assert.That(GlbTextureRewriter.TryExternalize(rewritten, left, out var again, out _)).IsTrue();
        await Assert.That(ReferenceEquals(again, rewritten)).IsTrue();
    }

    [Test]
    public async Task embedding_replaces_the_image_bytes_in_place_and_declares_basisu()
    {
        byte[] png = [1, 2, 3];
        byte[] ktx2 = [.. Ktx2Header.Identifier.ToArray(), 7, 7, 7, 7, 7];
        var glb = TwoImageGlb(png, [4, 5, 6]);

        await Assert.That(GlbTextureRewriter.TryEmbedKtx2(glb, new Dictionary<int, byte[]> { [0] = ktx2 }, out var rewritten, out _)).IsTrue();

        GlbBinary.TryRead(rewritten, out var gltf, out var bin);
        var view = gltf["bufferViews"]![gltf["images"]![0]!["bufferView"]!.GetValue<int>()]!;
        var offset = view["byteOffset"]!.GetValue<int>();
        var length = view["byteLength"]!.GetValue<int>();
        await Assert.That(bin.AsSpan(offset, length).ToArray()).IsEquivalentTo(ktx2);
        await Assert.That((string?)gltf["images"]![0]!["mimeType"]).IsEqualTo("image/ktx2");
        await Assert.That(gltf["textures"]![0]!["extensions"]!["KHR_texture_basisu"]).IsNotNull();
        // The untouched image and the geometry still resolve after the repack.
        var other = gltf["bufferViews"]![gltf["images"]![1]!["bufferView"]!.GetValue<int>()]!;
        await Assert.That(bin.AsSpan(other["byteOffset"]!.GetValue<int>(), other["byteLength"]!.GetValue<int>()).ToArray()).IsEquivalentTo(new byte[] { 4, 5, 6 });

        await Assert.That(GlbTextureRewriter.TryEmbedKtx2(glb, new Dictionary<int, byte[]> { [5] = ktx2 }, out _, out var error)).IsFalse();
        await Assert.That(error).Contains("#5");
    }

    [Test]
    public async Task a_kept_view_pointing_outside_the_bin_fails_the_externalize_instead_of_shipping()
    {
        var glb = TwoImageGlb([1, 2, 3], [.. Ktx2Header.Identifier.ToArray(), 9]);
        GlbBinary.TryRead(glb, out var gltf, out var bin);
        // The geometry view, which listing does not validate, claims more than the BIN holds.
        gltf["bufferViews"]![0]!["byteLength"] = 4096;
        var corrupt = GlbBinary.Write(gltf, bin);
        await Assert.That(GlbTextureRewriter.TryListEmbedded(corrupt, "crate", out var images, out _)).IsTrue();

        await Assert.That(GlbTextureRewriter.TryExternalize(corrupt, images, out _, out var error)).IsFalse();
        await Assert.That(error).Contains("buffer view #0");
    }

    [Test]
    public async Task a_view_pointing_outside_the_bin_fails_the_embed_too()
    {
        var glb = TwoImageGlb([1, 2, 3], [4, 5, 6]);
        GlbBinary.TryRead(glb, out var gltf, out var bin);
        gltf["bufferViews"]![0]!["byteLength"] = 4096;

        await Assert.That(GlbTextureRewriter.TryEmbedKtx2(GlbBinary.Write(gltf, bin), new Dictionary<int, byte[]> { [0] = [7] }, out _, out var error)).IsFalse();
        await Assert.That(error).Contains("buffer view #0");
    }

    /// <summary>Geometry view 0, image 0 (PNG, bound as a normal map) in view 1, image 1 (bound to nothing) in view 2.</summary>
    private static byte[] TwoImageGlb(byte[] first, byte[] second)
    {
        byte[] geometry = [0xAA, 0xBB, 0xCC, 0xDD];
        var bin = new byte[12 + first.Length + GlbBinary.AlignToFour(first.Length) - first.Length + second.Length];
        using var stream = new MemoryStream();
        stream.Write(geometry);
        var firstOffset = (int)stream.Position;
        stream.Write(first);
        GlbBinary.WritePadding(stream, 0);
        var secondOffset = (int)stream.Position;
        stream.Write(second);
        bin = stream.ToArray();

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["accessors"] = new JsonArray(new JsonObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = 1, ["type"] = "SCALAR" }),
            ["images"] = new JsonArray(
                new JsonObject { ["name"] = "Wall_Albedo", ["mimeType"] = "image/png", ["bufferView"] = 1 },
                new JsonObject { ["name"] = "Wall_Rough", ["mimeType"] = "image/ktx2", ["bufferView"] = 2 }),
            ["textures"] = new JsonArray(new JsonObject { ["source"] = 0 }, new JsonObject { ["source"] = 1 }),
            ["materials"] = new JsonArray(new JsonObject { ["normalTexture"] = new JsonObject { ["index"] = 0 } }),
            ["bufferViews"] = new JsonArray(
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = geometry.Length },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = firstOffset, ["byteLength"] = first.Length },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = secondOffset, ["byteLength"] = second.Length }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
        };
        return GlbBinary.Write(gltf, bin);
    }
}
