using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Paradise.Assets.Gltf.Test;

public class GltfMaterialAndImageTests
{
    private static readonly byte[] PngMagicBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Ktx2MagicBytes = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9];
    private static readonly byte[] JpegMagicBytes = [0xFF, 0xD8, 0xFF, 0xE0, 4, 4];

    private static GlbTestBuilder AssetWithMaterial(JsonObject material, out int materialIndex)
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3");
        materialIndex = b.AddMaterial(material);
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, material: materialIndex));
        b.SetSceneRoots(b.AddNode(mesh: mesh));
        return b;
    }

    [Test]
    public async Task full_metallic_roughness_material_round_trips()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3");
        var baseImage = b.AddImage(PngMagicBytes);
        var ktxImage = b.AddImage(Ktx2MagicBytes);
        var baseTexture = b.AddTexture(source: baseImage);
        // basisu source wins over the (deliberately wrong) base source.
        var normalTexture = b.AddTexture(source: baseImage, basisuSource: ktxImage);

        var material = new JsonObject
        {
            ["name"] = "crate",
            ["pbrMetallicRoughness"] = new JsonObject
            {
                ["baseColorFactor"] = new JsonArray { 0.1, 0.2, 0.3, 0.9 },
                ["baseColorTexture"] = new JsonObject
                {
                    ["index"] = baseTexture,
                    ["extensions"] = new JsonObject
                    {
                        ["KHR_texture_transform"] = new JsonObject
                        {
                            ["offset"] = new JsonArray { 0.25, 0.5 },
                            ["scale"] = new JsonArray { 2.0, 3.0 },
                            ["rotation"] = 0.5,
                        },
                    },
                },
                ["metallicFactor"] = 0.75,
                ["roughnessFactor"] = 0.4,
                ["metallicRoughnessTexture"] = new JsonObject { ["index"] = baseTexture },
            },
            ["normalTexture"] = new JsonObject { ["index"] = normalTexture, ["scale"] = 0.8 },
            ["occlusionTexture"] = new JsonObject { ["index"] = baseTexture, ["strength"] = 0.6 },
            ["emissiveTexture"] = new JsonObject { ["index"] = baseTexture },
            ["emissiveFactor"] = new JsonArray { 1.0, 0.5, 0.25 },
            ["alphaMode"] = "MASK",
            ["alphaCutoff"] = 0.33,
            ["doubleSided"] = true,
            ["extensions"] = new JsonObject
            {
                ["KHR_materials_transmission"] = new JsonObject { ["transmissionFactor"] = 0.9 },
            },
        };
        var materialIndex = b.AddMaterial(material);
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position, material: materialIndex));
        b.SetSceneRoots(b.AddNode(mesh: mesh));

        var asset = GltfSceneReader.Read(b.Build());
        var m = asset.Materials[materialIndex];

        await Assert.That(m.Name).IsEqualTo("crate");
        await Assert.That(m.BaseColorFactor).IsEqualTo(new Vector4(0.1f, 0.2f, 0.3f, 0.9f));
        await Assert.That(m.MetallicFactor).IsEqualTo(0.75f);
        await Assert.That(m.RoughnessFactor).IsEqualTo(0.4f);
        await Assert.That(m.EmissiveFactor).IsEqualTo(new Vector3(1f, 0.5f, 0.25f));
        await Assert.That(m.NormalScale).IsEqualTo(0.8f);
        await Assert.That(m.OcclusionStrength).IsEqualTo(0.6f);
        await Assert.That(m.TransmissionFactor).IsEqualTo(0.9f);
        await Assert.That(m.AlphaMode).IsEqualTo(GltfAlphaMode.Mask);
        await Assert.That(m.AlphaCutoff).IsEqualTo(0.33f);
        await Assert.That(m.DoubleSided).IsTrue();

        await Assert.That(m.BaseColorImage).IsEqualTo(baseImage);
        await Assert.That(m.MetallicRoughnessImage).IsEqualTo(baseImage);
        await Assert.That(m.NormalImage).IsEqualTo(ktxImage); // basisu indirection resolved
        await Assert.That(m.OcclusionImage).IsEqualTo(baseImage);
        await Assert.That(m.EmissiveImage).IsEqualTo(baseImage);

        await Assert.That(m.BaseColorUvTransform.Offset).IsEqualTo(new Vector2(0.25f, 0.5f));
        await Assert.That(m.BaseColorUvTransform.Scale).IsEqualTo(new Vector2(2f, 3f));
        await Assert.That(m.BaseColorUvTransform.Rotation).IsEqualTo(0.5f);

        await Assert.That(asset.Meshes[0].Primitives[0].MaterialIndex).IsEqualTo(materialIndex);
    }

    [Test]
    public async Task defaulted_material_uses_gltf_spec_defaults()
    {
        var b = AssetWithMaterial(new JsonObject(), out var materialIndex);
        var m = GltfSceneReader.Read(b.Build()).Materials[materialIndex];

        await Assert.That(m.BaseColorFactor).IsEqualTo(Vector4.One);
        await Assert.That(m.MetallicFactor).IsEqualTo(1f);
        await Assert.That(m.RoughnessFactor).IsEqualTo(1f);
        await Assert.That(m.EmissiveFactor).IsEqualTo(Vector3.Zero);
        await Assert.That(m.AlphaMode).IsEqualTo(GltfAlphaMode.Opaque);
        await Assert.That(m.AlphaCutoff).IsEqualTo(0.5f);
        await Assert.That(m.TransmissionFactor).IsEqualTo(0f);
        await Assert.That(m.BaseColorImage).IsEqualTo(-1);
        await Assert.That(m.NormalImage).IsEqualTo(-1);
        await Assert.That(m.BaseColorUvTransform).IsEqualTo(GltfUvTransform.Identity);
    }

    [Test]
    public async Task unknown_alpha_mode_throws()
    {
        var b = AssetWithMaterial(new JsonObject { ["alphaMode"] = "SHINY" }, out _);
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<InvalidDataException>();
    }

    [Test]
    public async Task image_kinds_are_sniffed_by_magic()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3");
        var png = b.AddImage(PngMagicBytes, mimeType: "image/png");
        var ktx2 = b.AddImage(Ktx2MagicBytes, mimeType: "image/ktx2");
        // mimeType deliberately lies — magic must win (ToktxKtx2 rewrites in place).
        var jpeg = b.AddImage(JpegMagicBytes, mimeType: "image/png");
        var unknown = b.AddImage([1, 2, 3, 4]);
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        b.SetSceneRoots(b.AddNode(mesh: mesh));

        var asset = GltfSceneReader.Read(b.Build());
        await Assert.That(asset.Images[png].Kind).IsEqualTo(GltfImageKind.Png);
        await Assert.That(asset.Images[ktx2].Kind).IsEqualTo(GltfImageKind.Ktx2);
        await Assert.That(asset.Images[jpeg].Kind).IsEqualTo(GltfImageKind.Jpeg);
        await Assert.That(asset.Images[unknown].Kind).IsEqualTo(GltfImageKind.Unknown);
        await Assert.That(asset.Images[png].Bytes).IsEquivalentTo(PngMagicBytes);
    }

    [Test]
    public async Task external_image_uri_throws()
    {
        var b = new GlbTestBuilder();
        var position = b.AddFloatAccessor([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3");
        b.AddExternalImage("textures/crate.png");
        var mesh = b.AddMesh(GlbTestBuilder.Primitive(position));
        b.SetSceneRoots(b.AddNode(mesh: mesh));
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<NotSupportedException>();
    }

    [Test]
    public async Task texture_index_out_of_range_throws()
    {
        var b = AssetWithMaterial(new JsonObject
        {
            ["pbrMetallicRoughness"] = new JsonObject
            {
                ["baseColorTexture"] = new JsonObject { ["index"] = 3 },
            },
        }, out _);
        await Assert.That(() => GltfSceneReader.Read(b.Build())).Throws<InvalidDataException>();
    }
}
