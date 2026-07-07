using System;
using System.IO;
using System.Numerics;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;
using Paradise.Assets.Gltf;

namespace Paradise.Rendering.Sample;

/// <summary>The PR-5 demo: PBR-render a GLB (any file — Godot-exported, DCC-authored, …) or,
/// with no path, a procedural two-cube arrangement. Orbit camera: drag rotates, wheel zooms;
/// idle auto-orbit. Headless mode steps the orbit per frame.</summary>
internal sealed class PbrViewerScene : IDisposable
{
    private readonly PbrRenderer _pbr;
    private readonly PbrScene _scene = new();
    private float _yaw = 0.6f;
    private float _pitch = 0.45f;
    private float _distance = 4f;
    private bool _autoOrbit = true;
    private uint _width;
    private uint _height;

    public PbrViewerScene(WebGpuRenderer renderer, uint width, uint height, string? glbPath)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _pbr = new PbrRenderer(renderer, _width, _height);

        if (glbPath is not null)
        {
            var glbDir = Path.GetDirectoryName(Path.GetFullPath(glbPath))!;
            var asset = GltfSceneReader.Read(
                File.ReadAllBytes(glbPath),
                uri => File.ReadAllBytes(Path.Combine(glbDir, uri.Replace('/', Path.DirectorySeparatorChar))));
            var meshes = _pbr.UploadMesh(asset);
            if (asset.Instances.Length == 0)
                throw new InvalidOperationException($"'{glbPath}' has no mesh instances in its default scene.");
            foreach (var instance in asset.Instances)
            {
                _scene.Instances.Add(new PbrInstance
                {
                    Mesh = meshes[instance.MeshIndex],
                    Model = instance.WorldTransform,
                });
            }
            Console.WriteLine(
                $"[PbrViewer] {glbPath}: {asset.Meshes.Length} mesh(es), {asset.Instances.Length} instance(s), " +
                $"{asset.Materials.Length} material(s), {asset.Images.Length} image(s).");
        }
        else
        {
            var (vertices, indices) = Procedural.UnitCube();
            var matte = _pbr.Materials.AddDefaultMaterial(new Vector4(0.75f, 0.3f, 0.2f, 1f), metallic: 0f, roughness: 0.7f);
            var metal = _pbr.Materials.AddDefaultMaterial(new Vector4(0.9f, 0.9f, 0.95f, 1f), metallic: 1f, roughness: 0.25f);
            var matteMesh = new PbrMesh([_pbr.UploadPrimitive(vertices, indices, matte)]);
            var metalMesh = new PbrMesh([_pbr.UploadPrimitive(vertices, indices, metal)]);
            _scene.Instances.Add(new PbrInstance { Mesh = matteMesh, Model = Matrix4x4.CreateTranslation(-0.75f, 0f, 0f) });
            _scene.Instances.Add(new PbrInstance
            {
                Mesh = metalMesh,
                Model = Matrix4x4.CreateScale(0.7f) * Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(0.9f, 0.15f, 0f),
            });
        }

        _scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(0.45f, 1f, 0.35f)),
            Color = new Vector3(1f, 0.97f, 0.92f),
            Intensity = 1.6f,
        });
        _scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Point,
            Position = new Vector3(-2.5f, 1.5f, 2.5f),
            Color = new Vector3(0.4f, 0.55f, 1f),
            Intensity = 5f,
            Range = 12f,
        });
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
        _pitch = Math.Clamp(_pitch + deltaY * 0.01f, -1.45f, 1.45f);
    }

    public void Zoom(float wheel)
    {
        _distance = Math.Clamp(_distance * (1f - wheel * 0.1f), 0.5f, 50f);
    }

    public void RenderFrame()
    {
        if (_autoOrbit) _yaw += 0.008f;

        var eye = new Vector3(
            _distance * MathF.Cos(_pitch) * MathF.Sin(_yaw),
            _distance * MathF.Sin(_pitch),
            _distance * MathF.Cos(_pitch) * MathF.Cos(_yaw));
        _scene.Camera = new PbrCamera
        {
            View = PbrMath.LookAt(eye, Vector3.Zero, Vector3.UnitY),
            Projection = PbrMath.Perspective(MathF.PI / 3f, _width / (float)_height, 0.05f, 200f),
            Position = eye,
        };

        _pbr.RenderFrame(_scene);
    }

    public void Dispose() => _pbr.Dispose();
}
