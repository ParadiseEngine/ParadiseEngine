using System;
using System.IO;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Paradise.Assets.Gltf;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Sample;

/// <summary>Demonstrates probe GI in a Cornell room with emissive and moving lights.</summary>
/// <remarks>Colored walls expose indirect light bleed. Optional GLB geometry uses material factors
/// because its PNG textures do not satisfy the cooked KTX2 contract.</remarks>
internal sealed class GiDemoScene : IDisposable
{
    private readonly PbrRenderer _pbr;
    private readonly PbrScene _scene = new();
    private readonly PbrInstance _prop;
    private readonly PbrLight _sunTemplate;
    private readonly PbrLight _lampTemplate;
    private int _frame;
    private float _yaw;
    private float _pitch = 0.12f;
    private float _distance = 8.5f;
    private bool _autoOrbit = true;
    private uint _width;
    private uint _height;

    /// <summary>Process-wide switches read when the scene is built.</summary>
    public static bool ProbeGi { get; set; } = true;
    public static bool RayTracedAo { get; set; }
    public static bool Reflections { get; set; }
    public static bool Decals { get; set; }
    public static bool Fog { get; set; }
    public static bool AnimateLights { get; set; } = true;

    /// <summary>Only the emissive ceiling panel lights the room: it is not a light, so with the
    /// probes off the room is dark and with them on it is lit — the classic Cornell box.</summary>
    public static bool PanelOnly { get; set; }

    /// <summary>Probe budget knobs for the benchmark.</summary>
    public static int RaysPerProbe { get; set; } = 128;
    public static int MaxProbes { get; set; } = 4096;
    public static int ProbesPerFrame { get; set; }

    /// <summary>Extra point lights scattered through the room (--lights N). The room's own two
    /// lights are not enough to show what Forward+ binning costs or saves: the froxel grid is
    /// rebuilt at the same size whatever the light count, so only a scene with many lights
    /// separates the per-frame grid cost from the per-light one.</summary>
    public static int ExtraLights { get; set; }

    private void AddDecals()
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var u = (x + 0.5f) / size - 0.5f;
            var v = (y + 0.5f) / size - 0.5f;
            var radius = MathF.Sqrt(u * u + v * v);
            var arrow = (MathF.Abs(u) < 0.07f && v > -0.12f && v < 0.3f)
                || (v < -0.05f && v > -0.3f && MathF.Abs(u) < (v + 0.3f));
            var index = (y * size + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = 255;
            pixels[index + 3] = (byte)(arrow || (radius > 0.39f && radius < 0.46f) ? 255 : 0);
        }
        var stencil = new PbrDecalTexture(size, size, pixels);
        _scene.Decals.Volumes.Add(new PbrDecal
        {
            Material = new() { ColorTexture = stencil, Color = new Vector4(1, 0.55f, 0.04f, 1),
                Roughness = 0.45f, MaterialWeight = 1 },
            Model = Matrix4x4.CreateScale(2.2f, 2.2f, 0.3f) * Matrix4x4.CreateRotationX(-MathF.PI / 2)
                * Matrix4x4.CreateTranslation(-0.5f, 0.02f, 1.2f),
        });
        _scene.Decals.Volumes.Add(new PbrDecal
        {
            Material = new() { ColorTexture = stencil, Color = new Vector4(0.1f, 0.8f, 1, 1),
                Emission = new Vector3(0.05f, 0.4f, 0.8f), EmissionWeight = 1 },
            Model = Matrix4x4.CreateScale(1.4f, 1.4f, 0.3f) * Matrix4x4.CreateTranslation(1.4f, 2.5f, -2.98f),
        });
    }

    public GiDemoScene(WebGpuRenderer renderer, uint width, uint height, string? modelPath, ILogger? logger = null)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr = new PbrRenderer(renderer, Program.Features, _width, _height, logger: logger);
        _scene.Taa = new PbrTaa { Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--taa") >= 0 };
        _scene.Fxaa = new PbrFxaa { Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--fxaa") >= 0 };

        var (cube, cubeIndices) = Procedural.UnitCube();
        var white = _pbr.Materials.AddDefaultMaterial(new Vector4(0.73f, 0.73f, 0.73f, 1f), metallic: 0f, roughness: 0.9f);
        var red = _pbr.Materials.AddDefaultMaterial(new Vector4(0.65f, 0.05f, 0.05f, 1f), metallic: 0f, roughness: 0.9f);
        var green = _pbr.Materials.AddDefaultMaterial(new Vector4(0.12f, 0.45f, 0.15f, 1f), metallic: 0f, roughness: 0.9f);
        var blue = _pbr.Materials.AddDefaultMaterial(new Vector4(0.2f, 0.35f, 0.9f, 1f), metallic: 0f, roughness: 0.6f);
        // Panel dominant, sun as a secondary key: the Cornell reference look is a room lit by its
        // own emitter, not a floor flooded by daylight.
        var panel = _pbr.Materials.AddMaterial(Emissive(PanelOnly ? new Vector3(12f, 11.2f, 10f) : new Vector3(8f, 7.5f, 6.7f)), []);

        PbrMesh Box(int material) => new([_pbr.UploadPrimitive(cube, cubeIndices, material)]);
        var whiteBox = Box(white);

        // The room: 6 m wide, 4 m tall, 6 m deep, floor at y = 0, open toward +z (the camera).
        const float w = 6f, h = 4f, d = 6f, t = 0.2f;
        Add(whiteBox, Matrix4x4.CreateScale(w + 2 * t, t, d + 2 * t) * Matrix4x4.CreateTranslation(0f, -t / 2, 0f)); // floor
        Add(whiteBox, Matrix4x4.CreateScale(w + 2 * t, t, d + 2 * t) * Matrix4x4.CreateTranslation(0f, h + t / 2, 0f)); // ceiling
        Add(whiteBox, Matrix4x4.CreateScale(w + 2 * t, h + 2 * t, t) * Matrix4x4.CreateTranslation(0f, h / 2, -d / 2 - t / 2)); // back
        Add(Box(red), Matrix4x4.CreateScale(t, h + 2 * t, d + 2 * t) * Matrix4x4.CreateTranslation(-w / 2 - t / 2, h / 2, 0f)); // left
        Add(Box(green), Matrix4x4.CreateScale(t, h + 2 * t, d + 2 * t) * Matrix4x4.CreateTranslation(w / 2 + t / 2, h / 2, 0f)); // right
        Add(Box(panel), Matrix4x4.CreateScale(1.6f, 0.05f, 1.6f) * Matrix4x4.CreateTranslation(0f, h - 0.03f, -0.5f)); // ceiling light

        // The classic pair: a tall box at the back left, a short box at the front right.
        Add(whiteBox, Matrix4x4.CreateScale(1.2f, 2.4f, 1.2f) * Matrix4x4.CreateRotationY(0.3f) * Matrix4x4.CreateTranslation(-1.1f, 1.2f, -1.2f));
        Add(whiteBox, Matrix4x4.CreateScale(1.2f, 1.2f, 1.2f) * Matrix4x4.CreateRotationY(-0.3f) * Matrix4x4.CreateTranslation(1.2f, 0.6f, 0.6f));

        if (Decals) AddDecals();

        if (modelPath is not null) PlaceModel(modelPath, new Vector3(1.2f, 1.2f, 0.6f), 1.2f);

        // A dynamic prop: lit by the probes, not traced, because it moves every frame.
        _prop = new PbrInstance { Mesh = Box(blue), GiMode = PbrGiMode.Dynamic };
        _scene.Instances.Add(_prop);

        // A dim sky: the room is lit by what comes through the open front and bounces, which is
        // the point. With the probes off only the directly lit patches survive.
        _scene.Ambient = new PbrAmbient
        {
            Sky = new Vector3(0.08f, 0.10f, 0.14f),
            Equator = new Vector3(0.05f, 0.05f, 0.06f),
            Ground = new Vector3(0.02f, 0.02f, 0.02f),
        };
        _scene.Tonemap = new PbrTonemap { Mode = PbrTonemapMode.Filmic, Exposure = 0.6f, White = 4f };
        _scene.Bloom = new PbrBloom { Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--no-bloom") < 0, Threshold = 1.2f, Intensity = 0.25f };
        _scene.Gi = new PbrGi { Enabled = ProbeGi, RaysPerProbe = RaysPerProbe, Hysteresis = 0.97f, MaxProbes = MaxProbes, ProbesPerFrame = ProbesPerFrame };
        _scene.RayTracedAo = new PbrRayTracedAo { Enabled = RayTracedAo, RaysPerPixel = 8, MaxDistance = 1.5f };
        _scene.Ssr = new PbrScreenSpaceReflection { Enabled = Reflections, MaxDistance = 12f };
        _scene.Fog = new PbrFog
        {
            Enabled = Fog, Density = 0.035f, HeightFalloff = 0.5f, BaseHeight = 1,
            Color = new Vector3(0.08f, 0.1f, 0.14f), Albedo = new Vector3(0.9f),
            Anisotropy = 0.3f, MaxDistance = 20, Steps = 48,
        };
        if (Fog)
            _scene.FogVolumes.Add(new PbrFogVolume
            {
                Transform = Matrix4x4.CreateScale(5.8f, 1.4f, 5.8f) * Matrix4x4.CreateTranslation(0, 0.7f, 0),
                Density = 0.16f, Albedo = new Vector3(0.75f, 0.85f, 1),
            });

        _sunTemplate = new PbrLight
        {
            Type = PbrLightType.Directional,
            Color = new Vector3(1f, 0.95f, 0.85f),
            Intensity = PanelOnly ? 0f : 0.7f,
            CastsShadows = true,
            SoftShadows = true,
            Size = 1.5f,
        };
        _lampTemplate = new PbrLight
        {
            Type = PbrLightType.Point,
            Color = new Vector3(1f, 0.55f, 0.25f),
            Intensity = PanelOnly ? 0f : 2.5f,
            Range = 9f,
            CastsShadows = true,
        };
        _scene.Lights.Add(_sunTemplate);
        _scene.Lights.Add(_lampTemplate);
        AddExtraLights();
        Animate();

        Console.WriteLine(
            $"[GiDemo] probes {(ProbeGi ? "on" : "off")} (rays {RaysPerProbe}, max {MaxProbes}, per frame {ProbesPerFrame}), ray-traced AO {(RayTracedAo ? "on" : "off")}, " +
            $"{_scene.Lights.Count} lights, {_scene.Instances.Count} instances{(modelPath is null ? "" : $", model {Path.GetFileName(modelPath)}")}.");
    }

    // A deterministic spiral of short-range point lights through the room, so a bench run is
    // comparable between builds.
    private void AddExtraLights()
    {
        for (var i = 0; i < ExtraLights; i++)
        {
            var t = i / (float)Math.Max(ExtraLights, 1);
            var angle = t * MathF.Tau * 3f;
            _scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Point,
                Position = new Vector3(MathF.Cos(angle) * 3.5f, 0.4f + t * 4.5f, MathF.Sin(angle) * 3.5f),
                Color = new Vector3(0.6f + 0.4f * t, 0.7f, 1f - 0.4f * t),
                Intensity = 1.5f,
                Range = 3f,
            });
        }
    }

    private void Add(PbrMesh mesh, Matrix4x4 model) => _scene.Instances.Add(new PbrInstance { Mesh = mesh, Model = model });

    private static GltfMaterialData Emissive(Vector3 color) => new(
        Name: "panel", BaseColorFactor: new Vector4(1f, 1f, 1f, 1f), MetallicFactor: 0f, RoughnessFactor: 1f,
        EmissiveFactor: color, NormalScale: 1f, OcclusionStrength: 1f, TransmissionFactor: 0f,
        AlphaMode: GltfAlphaMode.Opaque, AlphaCutoff: 0.5f, DoubleSided: false,
        BaseColorImage: -1, MetallicRoughnessImage: -1, NormalImage: -1, OcclusionImage: -1, EmissiveImage: -1,
        BaseColorUvTransform: GltfUvTransform.Identity);

    /// <summary>Load a GLB's geometry, drop its texture references (factors only), and stand it on
    /// <paramref name="top"/> scaled to <paramref name="size"/> metres.</summary>
    private void PlaceModel(string path, Vector3 top, float size)
    {
        var asset = GltfSceneReader.ReadGeometry(File.ReadAllBytes(path));
        var materials = new GltfMaterialData[asset.Materials.Length];
        for (var i = 0; i < materials.Length; i++)
        {
            materials[i] = asset.Materials[i] with
            {
                BaseColorImage = -1, MetallicRoughnessImage = -1, NormalImage = -1, OcclusionImage = -1, EmissiveImage = -1,
                // Polished gold: a metal has no diffuse, so everything it shows is the room
                // reflected through the probes' specular fallback — the red and green walls and
                // the panel, blurred to the probes' resolution.
                BaseColorFactor = new Vector4(1.0f, 0.78f, 0.36f, 1f),
                MetallicFactor = 1f,
                RoughnessFactor = 0.25f,
            };
        }
        var meshes = _pbr.UploadMesh(asset with { Materials = materials, Images = [] });

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var instance in asset.Instances)
        {
            foreach (var primitive in asset.Meshes[instance.MeshIndex].Primitives)
            {
                var vertices = primitive.Vertices;
                for (var v = 0; v + 2 < vertices.Length; v += 12)
                {
                    var p = Vector3.Transform(new Vector3(vertices[v], vertices[v + 1], vertices[v + 2]), instance.WorldTransform);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
        }
        var extent = max - min;
        var scale = size / MathF.Max(MathF.Max(extent.X, extent.Y), MathF.Max(extent.Z, 1e-3f));
        var centre = (min + max) * 0.5f;
        var fit = Matrix4x4.CreateTranslation(-centre.X, -min.Y, -centre.Z) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(top);
        foreach (var instance in asset.Instances)
            Add(meshes[instance.MeshIndex], instance.WorldTransform * fit);
    }

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
        _pitch = Math.Clamp(_pitch + deltaY * 0.01f, -0.6f, 1.2f);
    }

    public void Zoom(float wheel) => _distance = Math.Clamp(_distance * (1f - wheel * 0.1f), 2f, 30f);

    /// <summary>The sun swings across the open side; the lamp circles the room at head height;
    /// the prop orbits the tall box. Frame-driven so a headless run is deterministic.</summary>
    private void Animate()
    {
        var time = _frame / 60f;
        var sunAngle = AnimateLights ? 0.9f + 0.5f * MathF.Sin(time * 0.35f) : 0.9f;
        _scene.Lights[0] = _sunTemplate with
        {
            SoftShadows = SoftShadowsOverride ?? _sunTemplate.SoftShadows,
            // From the surface toward the light: high and from the open front, sweeping left-right.
            Direction = Vector3.Normalize(new Vector3(MathF.Sin(sunAngle) * 0.8f, 0.9f, 0.8f)),
        };
        var lampAngle = AnimateLights ? time * 0.8f : 0.4f;
        _scene.Lights[1] = _lampTemplate with
        {
            SoftShadows = SoftShadowsOverride ?? _lampTemplate.SoftShadows,
            Position = new Vector3(1.8f * MathF.Cos(lampAngle), 1.6f, 1.8f * MathF.Sin(lampAngle) + 0.5f),
        };
        var propAngle = AnimateLights ? time * 0.6f : 0f;
        _prop.Model = Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateRotationY(propAngle * 2f)
            * Matrix4x4.CreateTranslation(-1.1f + 1.6f * MathF.Cos(propAngle), 1.6f + 0.4f * MathF.Sin(time), -1.2f + 1.6f * MathF.Sin(propAngle));
        _scene.ElapsedSeconds = time;
    }

    internal PbrScene Scene => _scene;

    internal bool? SoftShadowsOverride { get; set; }

    public void RenderFrame(bool advance = true)
    {
        if (_autoOrbit) _yaw = 0.25f * MathF.Sin(_frame / 60f * 0.2f);
        var target = new Vector3(0f, 1.8f, -0.5f);
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
        Animate();
        if (SoftShadowsOverride is { } soft)
            for (var i = 0; i < _scene.Lights.Count; i++)
                if (_scene.Lights[i].SoftShadows != soft)
                    _scene.Lights[i] = _scene.Lights[i] with { SoftShadows = soft };
        _pbr.RenderFrame(_scene);
        if (advance) _frame++;
    }

    /// <summary>The renderer, for the benchmark's pass names and CPU timings.</summary>
    public PbrRenderer Renderer => _pbr;

    public void Dispose() => _pbr.Dispose();
}
