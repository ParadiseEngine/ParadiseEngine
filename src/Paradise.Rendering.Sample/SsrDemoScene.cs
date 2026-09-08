using System;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Sample;

/// <summary>The screen-space reflection test scene: a polished dark floor under a row of colored
/// pillars, a glowing slab, a spinning gold block and a walking skinned mannequin, seen from low
/// enough that the floor is mostly reflection. Half the floor is a mirror and half is brushed, so the roughness fade is visible in
/// one frame; the camera orbits slowly, which is what shows a reflection that lags or smears.</summary>
internal sealed class SsrDemoScene : IDisposable
{
    private readonly PbrRenderer _pbr;
    private readonly PbrScene _scene = new();
    private readonly PbrInstance _spinner;
    private readonly PbrInstance _walker;
    private readonly SkinnedMannequin _mannequin = new();
    private readonly Matrix4x4[] _palette = new Matrix4x4[SkinnedMannequin.JointCount];
    private int _frame;
    private float _yaw;
    private float _pitch = 0.18f;
    private float _distance = 9f;
    private bool _autoOrbit = true;
    private uint _width;
    private uint _height;

    /// <summary>Process-wide switches read when the scene is built.</summary>
    public static bool Reflections { get; set; } = true;
    public static bool Animate { get; set; } = true;

    public SsrDemoScene(WebGpuRenderer renderer, uint width, uint height, ILogger? logger = null)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr = new PbrRenderer(renderer, Program.Features, _width, _height, logger: logger);
        _scene.Taa = new PbrTaa { Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--taa") >= 0 };
        _scene.Fxaa = new PbrFxaa { Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--fxaa") >= 0 };

        var (cube, cubeIndices) = Procedural.UnitCube();
        var mirror = _pbr.Materials.AddDefaultMaterial(new Vector4(0.55f, 0.55f, 0.6f, 1f), metallic: 1f, roughness: 0.05f);
        var brushed = _pbr.Materials.AddDefaultMaterial(new Vector4(0.55f, 0.55f, 0.6f, 1f), metallic: 1f, roughness: 0.35f);
        var red = _pbr.Materials.AddDefaultMaterial(new Vector4(0.8f, 0.1f, 0.1f, 1f), metallic: 0f, roughness: 0.6f);
        var green = _pbr.Materials.AddDefaultMaterial(new Vector4(0.1f, 0.7f, 0.2f, 1f), metallic: 0f, roughness: 0.6f);
        var blue = _pbr.Materials.AddDefaultMaterial(new Vector4(0.15f, 0.3f, 0.9f, 1f), metallic: 0f, roughness: 0.6f);
        var white = _pbr.Materials.AddDefaultMaterial(new Vector4(0.85f, 0.85f, 0.85f, 1f), metallic: 0f, roughness: 0.8f);
        var gold = _pbr.Materials.AddDefaultMaterial(new Vector4(1f, 0.78f, 0.36f, 1f), metallic: 1f, roughness: 0.2f);
        var glow = _pbr.Materials.AddMaterial(Emissive(new Vector3(6f, 4.5f, 2.5f)), []);

        PbrMesh Box(int material) => new([_pbr.UploadPrimitive(cube, cubeIndices, material)]);

        // Two floor halves, meeting at x = 0: mirror on the left, brushed on the right.
        Add(Box(mirror), Matrix4x4.CreateScale(12f, 0.2f, 24f) * Matrix4x4.CreateTranslation(-6f, -0.1f, 0f));
        Add(Box(brushed), Matrix4x4.CreateScale(12f, 0.2f, 24f) * Matrix4x4.CreateTranslation(6f, -0.1f, 0f));
        // A back wall closes the scene so reflections of the pillars have something behind them.
        Add(Box(white), Matrix4x4.CreateScale(24f, 6f, 0.3f) * Matrix4x4.CreateTranslation(0f, 3f, -8f));

        var pillars = new[] { red, green, blue, red, green, blue };
        for (var i = 0; i < pillars.Length; i++)
        {
            var x = -7.5f + i * 3f;
            var pillarHeight = 1.5f + (i % 3) * 0.8f;
            Add(Box(pillars[i]), Matrix4x4.CreateScale(0.8f, pillarHeight, 0.8f) * Matrix4x4.CreateTranslation(x, pillarHeight / 2, -4f));
        }
        Add(Box(glow), Matrix4x4.CreateScale(5f, 0.3f, 0.3f) * Matrix4x4.CreateTranslation(0f, 3.5f, -7.5f));

        _spinner = new PbrInstance { Mesh = Box(gold), GiMode = PbrGiMode.Dynamic };
        _scene.Instances.Add(_spinner);

        // A skinned walker on the mirror half: its reflection follows the pose because the
        // pre-pass skins too, and it is dynamic because it moves every frame.
        var orange = _pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.45f, 0.12f, 1f), metallic: 0f, roughness: 0.5f);
        var walkerMesh = new PbrMesh([_pbr.UploadSkinnedPrimitive(_mannequin.Vertices, _mannequin.JointsWeights, _mannequin.Indices, orange)]);
        _walker = new PbrInstance { Mesh = walkerMesh, JointOffset = 0, GiMode = PbrGiMode.Dynamic };
        _scene.Instances.Add(_walker);

        _scene.HasSkyBackground = true;
        _scene.SkyTopColor = new Vector3(0.25f, 0.45f, 0.85f);
        _scene.SkyHorizonColor = new Vector3(0.7f, 0.75f, 0.85f);
        _scene.SkyGroundHorizon = new Vector3(0.35f, 0.33f, 0.3f);
        _scene.SkyGroundBottom = new Vector3(0.12f, 0.11f, 0.1f);
        _scene.SkyReflections = true;
        _scene.Ambient = new PbrAmbient
        {
            Sky = new Vector3(0.3f, 0.36f, 0.45f),
            Equator = new Vector3(0.2f, 0.2f, 0.22f),
            Ground = new Vector3(0.08f, 0.07f, 0.06f),
        };
        _scene.Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Filmic, Exposure = 0.8f, White = 4f };
        _scene.Bloom = new PbrBloom { Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--no-bloom") < 0, Threshold = 1.2f, Intensity = 0.2f };
        _scene.Ssr = new PbrScreenSpaceReflection { Enabled = Reflections, MaxSteps = 64, MaxDistance = 24f, Thickness = 0.4f, MaxRoughness = 0.5f };

        _scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(0.4f, 0.9f, 0.5f)),
            Color = new Vector3(1f, 0.96f, 0.9f),
            Intensity = 1.2f,
            CastsShadows = true,
            SoftShadows = true,
            Size = 1f,
        });
        AnimateSpinner();

        Console.WriteLine($"[SsrDemo] reflections {(Reflections ? "on" : "off")}, {_scene.Instances.Count} instances.");
    }

    private void Add(PbrMesh mesh, Matrix4x4 model) => _scene.Instances.Add(new PbrInstance { Mesh = mesh, Model = model });

    private static GltfMaterialData Emissive(Vector3 color) => new(
        Name: "glow", BaseColorFactor: new Vector4(1f, 1f, 1f, 1f), MetallicFactor: 0f, RoughnessFactor: 1f,
        EmissiveFactor: color, NormalScale: 1f, OcclusionStrength: 1f, TransmissionFactor: 0f,
        AlphaMode: GltfAlphaMode.Opaque, AlphaCutoff: 0.5f, DoubleSided: false,
        BaseColorImage: -1, MetallicRoughnessImage: -1, NormalImage: -1, OcclusionImage: -1, EmissiveImage: -1,
        BaseColorUvTransform: GltfUvTransform.Identity);

    public void Resize(uint width, uint height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr.Resize(_width, _height);
    }

    public void Drag(float deltaX, float deltaY)
    {
        _autoOrbit = false;
        _yaw += deltaX * 0.01f;
        _pitch = Math.Clamp(_pitch + deltaY * 0.01f, 0.02f, 1.2f);
    }

    public void Zoom(float wheel) => _distance = Math.Clamp(_distance * (1f - wheel * 0.1f), 2f, 30f);

    /// <summary>The gold block spins over the mirror half, clear of the seam so its reflection is
    /// read against one material. Frame-driven so a headless run is deterministic.</summary>
    private void AnimateSpinner()
    {
        var time = _frame / 60f;
        var angle = Animate ? time * 0.7f : 0.4f;
        _spinner.Model = Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateRotationX(angle * 0.5f)
            * Matrix4x4.CreateTranslation(-2.2f, 1.6f + 0.2f * MathF.Sin(time), -1f);
        // The walker turns slowly on the spot so the reflection shows every side of the rig.
        var walkTime = Animate ? time : 0.3f;
        _walker.Model = Matrix4x4.CreateRotationY(Animate ? time * 0.4f : 0.6f) * Matrix4x4.CreateTranslation(-4.6f, 0f, -0.5f);
        _mannequin.Pose(walkTime, Matrix4x4.Identity, _palette);
        _pbr.SetJointPalette(_walker.JointOffset, _palette);
        _scene.ElapsedSeconds = time;
    }

    public void RenderFrame()
    {
        if (_autoOrbit && Animate) _yaw = 0.35f * MathF.Sin(_frame / 60f * 0.25f);
        var target = new Vector3(0f, 1f, -3f);
        var eye = target + new Vector3(
            _distance * MathF.Cos(_pitch) * MathF.Sin(_yaw),
            _distance * MathF.Sin(_pitch),
            _distance * MathF.Cos(_pitch) * MathF.Cos(_yaw));
        _scene.Camera = new PbrCamera
        {
            View = PbrMath.LookAt(eye, target, Vector3.UnitY),
            Projection = PbrMath.Perspective(MathF.PI / 3.2f, _width / (float)_height, 0.05f, 100f),
            Position = eye,
        };
        AnimateSpinner();
        _pbr.RenderFrame(_scene);
        _frame++;
    }

    public PbrRenderer Renderer => _pbr;

    public void Dispose() => _pbr.Dispose();
}
