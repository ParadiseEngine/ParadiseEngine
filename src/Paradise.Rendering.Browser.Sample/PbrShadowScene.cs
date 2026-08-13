using System;
using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Pbr;

namespace Paradise.Rendering.Browser.Sample;

/// <summary>The acceptance scene: <see cref="PbrRenderer"/> driving a procedural ground plane and
/// three boxes lit by one shadow-casting directional light, with the sky background, bloom and
/// SSAO all on. Between them those features exercise every browser-backend feature the desktop
/// backend has that the spike did not cover — depth-only pipelines, a Depth32Float 2D-array with a
/// per-layer render view and a D2Array sampling view, an Rgba16Float HDR target with additive
/// blending across a bloom mip chain, an Rgba32Float unfilterable-float pre-pass target, dynamic
/// uniform offsets, comparison samplers and read-only storage buffers.</summary>
internal sealed class PbrShadowScene : IDisposable
{
    private readonly PbrRenderer _pbr;
    private readonly PbrScene _scene = new();
    private uint _width;
    private uint _height;
    private float _yaw = 0.7f;

    /// <param name="extraBoxes">Additional boxes in a ring around the centre. The default scene is
    /// three; raising it is how the harness measures what a real draw count costs across the JS
    /// boundary, since every instance adds a main-pass draw AND a shadow-pass draw.</param>
    public PbrShadowScene(IRenderer renderer, uint width, uint height, int extraBoxes = 0)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr = new PbrRenderer(renderer, _width, _height);

        var (vertices, indices) = Procedural.UnitCube();
        var groundMaterial = _pbr.Materials.AddDefaultMaterial(new Vector4(0.42f, 0.45f, 0.5f, 1f), metallic: 0f, roughness: 0.9f);
        var matte = _pbr.Materials.AddDefaultMaterial(new Vector4(0.75f, 0.3f, 0.2f, 1f), metallic: 0f, roughness: 0.7f);
        var metal = _pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.9f, 0.95f, 1f), metallic: 1f, roughness: 0.25f);
        var emissive = _pbr.Materials.AddDefaultMaterial(new Vector4(0.95f, 0.75f, 0.25f, 1f), metallic: 0f, roughness: 0.4f);

        // Procedural.UnitCube is the only primitive helper the Pbr package ships, so the ground
        // plane is a flattened box and the "spheres" are boxes too. Nothing here depends on the
        // shape — the point is a caster, a receiver, and geometry that fills the shadow frustum.
        var ground = new PbrMesh([_pbr.UploadPrimitive(vertices, indices, groundMaterial)]);
        _scene.Instances.Add(new PbrInstance
        {
            Mesh = ground,
            Model = Matrix4x4.CreateScale(12f, 0.2f, 12f) * Matrix4x4.CreateTranslation(0f, -0.6f, 0f),
        });

        AddBox(vertices, indices, matte, new Vector3(-1.3f, 0f, 0.2f), 1f, 0.3f);
        AddBox(vertices, indices, metal, new Vector3(0.9f, 0.1f, -0.4f), 0.8f, -0.5f);
        AddBox(vertices, indices, emissive, new Vector3(0.1f, 0.75f, 1.1f), 0.5f, 0.9f);
        // One translucent box so the frame also builds an AlphaBlend pipeline and takes the
        // back-to-front blended bucket. AddDefaultMaterial always produces an opaque material, so
        // the blend intent has to come from a hand-built glTF material record.
        AddBox(vertices, indices, _pbr.Materials.AddMaterial(TranslucentMaterial(), []),
            new Vector3(-0.4f, 0.35f, 1.9f), 0.9f, -0.2f);

        var palette = new[] { matte, metal, emissive, groundMaterial };
        for (var i = 0; i < extraBoxes; i++)
        {
            var angle = i * 2.399963f; // golden angle, so a ring of any size stays evenly spread
            var radius = 1.8f + 0.06f * i;
            AddBox(vertices, indices, palette[i % palette.Length],
                new Vector3(radius * MathF.Cos(angle), 0.15f + 0.02f * (i % 5), radius * MathF.Sin(angle)),
                0.35f, angle);
        }

        _scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(0.45f, 1f, 0.35f)),
            Color = new Vector3(1f, 0.97f, 0.92f),
            Intensity = 1.6f,
            CastsShadows = true,
            ShadowStrength = 1f,
        });
        _scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Point,
            Position = new Vector3(-2.5f, 1.5f, 2.5f),
            Color = new Vector3(0.4f, 0.55f, 1f),
            Intensity = 2.5f,
            Range = 12f,
            // Inverse-square falloff; the default 1.0 is inverse-LINEAR (Godot's own default) and
            // leaves this light far too hot at the distances in this scene.
            AttenuationExponent = 2f,
        });

        _scene.HasSkyBackground = true;
        _scene.SkyTopColor = new Vector3(0.10f, 0.18f, 0.42f);
        _scene.SkyHorizonColor = new Vector3(0.55f, 0.62f, 0.75f);
        _scene.SkyGroundHorizon = new Vector3(0.24f, 0.22f, 0.20f);
        _scene.SkyGroundBottom = new Vector3(0.06f, 0.05f, 0.05f);
        _scene.SkyReflections = true;
        _scene.Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Filmic, Exposure = 0.7f, White = 1f };
        _scene.Bloom = new PbrBloom { Enabled = true, Threshold = 1.1f, Intensity = 0.5f };
        _scene.Ssao = new PbrSsao { Enabled = true, Radius = 0.6f, Intensity = 1.5f };
        _scene.Ambient = new PbrAmbient { Sky = new Vector3(0.10f, 0.12f, 0.16f), Equator = new Vector3(0.07f, 0.07f, 0.08f), Ground = new Vector3(0.03f, 0.03f, 0.03f) };
    }

    private static GltfMaterialData TranslucentMaterial() => new(
        Name: "translucent",
        BaseColorFactor: new Vector4(0.35f, 0.85f, 0.7f, 0.35f),
        MetallicFactor: 0f,
        RoughnessFactor: 0.25f,
        EmissiveFactor: Vector3.Zero,
        NormalScale: 1f,
        OcclusionStrength: 1f,
        TransmissionFactor: 0f,
        AlphaMode: GltfAlphaMode.Blend,
        AlphaCutoff: 0.5f,
        DoubleSided: false,
        BaseColorImage: -1,
        MetallicRoughnessImage: -1,
        NormalImage: -1,
        OcclusionImage: -1,
        EmissiveImage: -1,
        BaseColorUvTransform: GltfUvTransform.Identity);

    private void AddBox(float[] vertices, uint[] indices, int materialId, Vector3 position, float scale, float rotation)
    {
        var mesh = new PbrMesh([_pbr.UploadPrimitive(vertices, indices, materialId)]);
        _scene.Instances.Add(new PbrInstance
        {
            Mesh = mesh,
            Model = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(rotation) * Matrix4x4.CreateTranslation(position),
        });
    }

    public void Resize(uint width, uint height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr.Resize(_width, _height);
    }

    public void RenderFrame()
    {
        _yaw += 0.01f;
        var eye = new Vector3(5.5f * MathF.Sin(_yaw), 2.6f, 5.5f * MathF.Cos(_yaw));
        _scene.Camera = new PbrCamera
        {
            View = PbrMath.LookAt(eye, new Vector3(0f, 0.2f, 0f), Vector3.UnitY),
            Projection = PbrMath.Perspective(MathF.PI / 3f, _width / (float)_height, 0.05f, 200f),
            Position = eye,
        };
        _pbr.RenderFrame(_scene);
    }

    public void Dispose() => _pbr.Dispose();
}
