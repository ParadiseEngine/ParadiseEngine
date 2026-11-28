using System.Numerics;
using Paradise.Rendering.Pbr;

namespace Paradise.Runtime.Benchmarks;

/// <summary>A scene that turns on what the samples' renderer showcase does: probe GI, ray-traced AO, SSAO,
/// contact shadows, occlusion culling, screen-space reflection, fog with a volume, decals, shadowed
/// directional/point/spot lights plus 20 unshadowed point lights, and the full post chain.</summary>
/// <remarks>Modelled on ParadiseSamples' <c>RendererShowcaseScene</c> and <c>GiDemoScene</c>, minus their
/// custom glass shader and skinned rig. Lights animate by mutating their properties, which allocates
/// nothing; the samples' <c>light with { … }</c> allocates a new <see cref="PbrLight"/> per frame.</remarks>
internal sealed class ShowcaseScene
{
    private readonly PbrScene _scene;
    private readonly PbrLight _sun;
    private readonly PbrLight _lamp;

    public ShowcaseScene(PbrRenderer renderer, PbrScene scene)
    {
        _scene = scene;
        renderer.Pipeline.Find<ProbeGiFeature>()!.Settings = new PbrGi { Enabled = true, RaysPerProbe = 32, MaxProbes = 512 };
        scene.Ssao = new PbrSsao { Enabled = true };
        scene.RayTracedAo = new PbrRayTracedAo { Enabled = true, RaysPerPixel = 4 };
        scene.ContactShadows = new PbrContactShadows { Enabled = true };
        scene.Ssr = new PbrScreenSpaceReflection { Enabled = true, MaxDistance = 12f };
        scene.Visibility.OcclusionEnabled = true;
        scene.MotionVectors = new PbrMotionVectors { Enabled = true };
        scene.Taa = new PbrTaa { Enabled = true };
        scene.Fxaa = new PbrFxaa { Enabled = true };
        scene.Bloom = new PbrBloom { Enabled = true, Threshold = 1.2f, Intensity = 0.25f };
        scene.Exposure = new PbrExposure { Enabled = true, CompensationEv = 0.2f };
        scene.DepthOfField = new PbrDepthOfField { Enabled = true, FocusDistance = 8, MaxRadiusPixels = 5 };
        scene.MotionBlur = new PbrMotionBlur { Enabled = true, MaxRadiusPixels = 8 };
        scene.ColorGrading = new PbrColorGrading { Enabled = true, Saturation = 1.08f, Temperature = 0.1f };
        scene.LensDistortion = new PbrLensDistortion { Enabled = true, Strength = 0.025f };
        scene.ChromaticAberration = new PbrChromaticAberration { Enabled = true, IntensityPixels = 0.6f };
        scene.Vignette = new PbrVignette { Enabled = true, Intensity = 0.15f };
        scene.FilmGrain = new PbrFilmGrain { Enabled = true, Intensity = 0.008f };
        scene.Sharpening = new PbrSharpening { Enabled = true, Strength = 0.15f };
        scene.Fog = new PbrFog
        {
            Enabled = true, Density = 0.035f, HeightFalloff = 0.5f, BaseHeight = 1,
            Color = new Vector3(0.08f, 0.1f, 0.14f), Albedo = new Vector3(0.9f), Anisotropy = 0.3f, MaxDistance = 20, Steps = 48,
        };
        scene.FogVolumes.Add(new PbrFogVolume
        {
            Transform = Matrix4x4.CreateScale(5.8f, 1.4f, 5.8f) * Matrix4x4.CreateTranslation(0, 0.7f, 0),
            Density = 0.16f, Albedo = new Vector3(0.75f, 0.85f, 1),
        });
        AddDecals(scene);

        var (vertices, indices) = Procedural.UnitCube();
        PbrMesh Box(Vector4 color, float metallic = 0f, float roughness = 0.6f) =>
            new([renderer.UploadPrimitive(vertices, indices, renderer.Materials.AddDefaultMaterial(color, metallic, roughness))]);
        var white = Box(new Vector4(0.73f, 0.73f, 0.73f, 1f), roughness: 0.9f);
        var floor = Box(new Vector4(0.6f, 0.65f, 0.7f, 1f), 0.85f, 0.12f);
        var panel = new PbrMesh([renderer.UploadPrimitive(vertices, indices,
            renderer.Materials.AddMaterial(new PbrMaterialDesc { Name = "panel", RoughnessFactor = 1f, EmissiveFactor = new Vector3(8f, 7.5f, 6.7f) }))]);
        Prop(floor, Matrix4x4.CreateScale(12f, 0.2f, 12f) * Matrix4x4.CreateTranslation(0f, -0.1f, -2f));
        Prop(white, Matrix4x4.CreateScale(12f, 6f, 0.2f) * Matrix4x4.CreateTranslation(0f, 3f, -8f));
        Prop(Box(new Vector4(0.65f, 0.05f, 0.05f, 1f), roughness: 0.9f), Matrix4x4.CreateScale(0.2f, 6f, 12f) * Matrix4x4.CreateTranslation(-6f, 3f, -2f));
        Prop(Box(new Vector4(0.12f, 0.45f, 0.15f, 1f), roughness: 0.9f), Matrix4x4.CreateScale(0.2f, 6f, 12f) * Matrix4x4.CreateTranslation(6f, 3f, -2f));
        Prop(panel, Matrix4x4.CreateScale(1.6f, 0.05f, 1.6f) * Matrix4x4.CreateTranslation(0f, 5.9f, -2f));
        var columns = Box(new Vector4(0.8f, 0.55f, 0.12f, 1f), 0.4f);
        for (var i = 0; i < 30; i++)
            Prop(columns, Matrix4x4.CreateScale(0.13f, 0.4f + i % 3 * 0.12f, 0.13f) * Matrix4x4.CreateTranslation(-2.6f + i % 10 * 0.55f, 0.3f, -6.5f + i / 10 * 0.4f));
        // Hidden and off-camera draws give both culling controls observable work.
        for (var i = 0; i < 12; i++)
            Prop(columns, Matrix4x4.CreateScale(0.15f) * Matrix4x4.CreateTranslation(i < 6 ? -1.1f : 30f, 0.4f + i * 0.05f, -7.9f), PbrGiMode.Disabled);

        _sun = new PbrLight
        {
            Type = PbrLightType.Directional, Color = new Vector3(1f, 0.95f, 0.85f), Intensity = 0.7f,
            CastsShadows = true, SoftShadows = true, Size = 1.5f,
        };
        _lamp = new PbrLight
        {
            Type = PbrLightType.Point, Color = new Vector3(1f, 0.55f, 0.25f), Intensity = 2.5f, Range = 9f, CastsShadows = true,
        };
        scene.Lights.Add(_sun);
        scene.Lights.Add(_lamp);
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Spot, Position = new Vector3(-2, 3, 2), Direction = Vector3.Normalize(new Vector3(1, -2, -2)),
            Color = new Vector3(1, 0.5f, 0.2f), Range = 8, Intensity = 7, CastsShadows = true, SoftShadows = true,
            SpotOuterDegrees = 38, SpotInnerDegrees = 22,
        });
        for (var i = 0; i < 20; i++)
        {
            var t = i / 20f;
            var angle = t * MathF.Tau * 3f;
            scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Point, Position = new Vector3(MathF.Cos(angle) * 3.5f, 0.4f + t * 4.5f, MathF.Sin(angle) * 3.5f - 2f),
                Color = new Vector3(0.6f + 0.4f * t, 0.7f, 1f - 0.4f * t), Intensity = 1.5f, Range = 3f,
            });
        }
    }

    /// <summary>Static and culled geometry drawn every frame beside the game's entities.</summary>
    public List<(PbrMesh Mesh, Matrix4x4 Model, PbrGiMode GiMode)> Props { get; } = [];

    /// <summary>Frame-driven, so runs are comparable between builds.</summary>
    /// <param name="frame">The presentation frame.</param>
    /// <param name="records">Animate as the samples do, replacing each changed light and setting record with
    /// <c>with</c>, instead of assigning their properties in place.</param>
    public void Animate(int frame, bool records)
    {
        var time = frame / 60f;
        var sunAngle = 0.9f + 0.5f * MathF.Sin(time * 0.35f);
        var sunDirection = Vector3.Normalize(new Vector3(MathF.Sin(sunAngle) * 0.8f, 0.9f, 0.8f));
        var lampAngle = time * 0.8f;
        var lampPosition = new Vector3(1.8f * MathF.Cos(lampAngle), 1.6f, 1.8f * MathF.Sin(lampAngle) - 1.5f);
        _scene.DeltaSeconds = 1f / 60f;
        var focus = 8f + MathF.Sin(time);
        var compensation = 0.2f + 0.1f * MathF.Sin(time * 0.5f);
        if (!records)
        {
            _sun.Direction = sunDirection;
            _lamp.Position = lampPosition;
            _scene.DepthOfField.FocusDistance = focus;
            _scene.Exposure.CompensationEv = compensation;
            return;
        }

        _scene.Lights[0] = _sun with { Direction = sunDirection };
        _scene.Lights[1] = _lamp with { Position = lampPosition };
        _scene.DepthOfField = _scene.DepthOfField with { FocusDistance = focus };
        _scene.Exposure = _scene.Exposure with { CompensationEv = compensation };
    }

    private void Prop(PbrMesh mesh, Matrix4x4 model, PbrGiMode gi = PbrGiMode.Static) => Props.Add((mesh, model, gi));

    private static void AddDecals(PbrScene scene)
    {
        const int Size = 64;
        var pixels = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var u = (x + 0.5f) / Size - 0.5f;
                var v = (y + 0.5f) / Size - 0.5f;
                var radius = MathF.Sqrt(u * u + v * v);
                var index = (y * Size + x) * 4;
                pixels[index] = pixels[index + 1] = pixels[index + 2] = 255;
                pixels[index + 3] = (byte)(radius > 0.39f && radius < 0.46f ? 255 : 0);
            }
        }

        var stencil = new PbrDecalTexture(Size, Size, pixels);
        scene.Decals.Volumes.Add(new PbrDecal
        {
            Material = new() { ColorTexture = stencil, Color = new Vector4(1, 0.55f, 0.04f, 1), Roughness = 0.45f, MaterialWeight = 1 },
            Model = Matrix4x4.CreateScale(2.2f, 2.2f, 0.3f) * Matrix4x4.CreateRotationX(-MathF.PI / 2) * Matrix4x4.CreateTranslation(-0.5f, 0.02f, 1.2f),
        });
        scene.Decals.Volumes.Add(new PbrDecal
        {
            Material = new() { ColorTexture = stencil, Color = new Vector4(0.1f, 0.8f, 1, 1), Emission = new Vector3(0.05f, 0.4f, 0.8f), EmissionWeight = 1 },
            Model = Matrix4x4.CreateScale(1.4f, 1.4f, 0.3f) * Matrix4x4.CreateTranslation(1.4f, 2.5f, -7.9f),
        });
    }
}
