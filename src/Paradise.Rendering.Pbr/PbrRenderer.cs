using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Assets.Gltf;
using Paradise.Rendering.WebGPU;

namespace Paradise.Rendering.Pbr;

/// <summary>The PBR scene renderer: owns the Slang-compiled program (reflection-validated at
/// construction), ≤4 lazily-built pipeline variants (opaque/blend × linear/sRGB fragment entry
/// selected ONCE by the surface format — never double-encoded), a Depth32Float depth buffer,
/// a dynamic-offset draw-UBO ring, and the per-frame command-stream emission: opaque first,
/// then blend back-to-front. All geometry/material upload goes through
/// <see cref="UploadMesh"/>/<see cref="MaterialResourceCache"/>.</summary>
public sealed class PbrRenderer : IDisposable
{
    private const int MaxDrawsPerFrame = 4096;

    private readonly WebGpuRenderer _renderer;
    private readonly ShaderProgramDesc _program;
    private readonly bool _useSrgbEntryPoint;
    private readonly uint _drawStride;
    private readonly BufferHandle _frameUniformBuffer;
    private readonly BufferHandle _drawUniformRing;
    private readonly BindGroupHandle _frameGroup;
    private readonly BindGroupHandle _drawGroup;
    private readonly Dictionary<BlendMode, PipelineHandle> _pipelines = new();
    private readonly byte[] _drawStaging;
    private readonly ArrayBufferWriter<RenderCommand> _commandWriter = new(256);
    private readonly RenderPassDesc[] _passes = new RenderPassDesc[1];
    private readonly List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> _opaque = [];
    private readonly List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> _blend = [];
    private readonly List<BufferHandle> _ownedBuffers = [];
    private TextureHandle _depthTexture;
    private uint _width;
    private uint _height;
    private float _specularAaVariance;
    private float _specularAaClamp;
    private bool _disposed;

    public MaterialResourceCache Materials { get; }

    public PbrRenderer(
        WebGpuRenderer renderer, uint width, uint height,
        ushort maxAnisotropy = 16, float specularAaVariance = 0.25f, float specularAaClamp = 0.18f)
    {
        _renderer = renderer;
        _specularAaVariance = specularAaVariance;
        _specularAaClamp = specularAaClamp;

        var program = WebGpuRenderer.LoadShaderProgram(typeof(PbrRenderer).Assembly, "Shaders.pbr");
        UniformLayoutValidator.Validate(program);

        // Group 0 (draw UBO) becomes a dynamic-offset ring: a LAYOUT property, so the program's
        // layout is rebuilt with the flag and both the pipelines and the bind group are created
        // from the modified layout (content-keyed layout cache keeps them Dawn-compatible).
        var groups = (BindGroupLayoutDesc[])program.Layout.Groups.Clone();
        for (var i = 0; i < groups.Length; i++)
        {
            if (groups[i].GroupIndex != 0) continue;
            groups[i] = new BindGroupLayoutDesc(0, [groups[i].Entries[0] with { HasDynamicOffset = true }]);
        }
        var dynamicLayout = new PipelineLayoutDesc(groups, program.Layout.PushConstants);
        _program = new ShaderProgramDesc(program.Modules, dynamicLayout, program.VertexBuffers)
        {
            UniformBlocks = program.UniformBlocks,
        };

        // One sRGB decision for the renderer's lifetime, driven by the surface format: sRGB
        // formats let the hardware encode (linear entry); everything else encodes in-shader.
        _useSrgbEntryPoint = !IsSrgbFormat(renderer.ColorFormat);

        _drawStride = renderer.UniformBufferOffsetAlignment;
        _drawStaging = new byte[_drawStride * MaxDrawsPerFrame];

        var frameDesc = new BufferDesc("PbrFrameUniforms", (ulong)Unsafe.SizeOf<FrameUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst);
        _frameUniformBuffer = renderer.CreateBuffer(in frameDesc);
        var ringDesc = new BufferDesc("PbrDrawRing", (ulong)_drawStride * MaxDrawsPerFrame, BufferUsage.Uniform | BufferUsage.CopyDst);
        _drawUniformRing = renderer.CreateBuffer(in ringDesc);

        var frameGroupDesc = new BindGroupDesc("PbrFrameGroup", FindGroup(1), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _frameUniformBuffer, 0, (ulong)Unsafe.SizeOf<FrameUniformsGpu>()),
        });
        _frameGroup = renderer.CreateBindGroup(in frameGroupDesc);
        var drawGroupDesc = new BindGroupDesc("PbrDrawGroup", FindGroup(0), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _drawUniformRing, 0, (ulong)Unsafe.SizeOf<DrawUniformsGpu>()),
        });
        _drawGroup = renderer.CreateBindGroup(in drawGroupDesc);

        Materials = new MaterialResourceCache(renderer, _program, maxAnisotropy);

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _depthTexture = CreateDepthTexture(_width, _height);
        _passes[0] = new RenderPassDesc(colorAttachmentCount: 1)
        {
            Depth = new DepthAttachmentDesc(_depthTexture, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f),
        };
    }

    /// <summary>Specular anti-aliasing tuning (RenderSettingsData.SpecularAaVariance/Clamp).</summary>
    public void SetSpecularAa(float variance, float clamp)
    {
        _specularAaVariance = variance;
        _specularAaClamp = clamp;
    }

    public void Resize(uint width, uint height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height) return;
        _renderer.DestroyTexture(_depthTexture);
        _width = width;
        _height = height;
        _depthTexture = CreateDepthTexture(width, height);
        _passes[0].Depth = new DepthAttachmentDesc(_depthTexture, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f);
    }

    public float AspectRatio => _width / (float)_height;

    /// <summary>Upload a decoded GLB: registers every material (slot order preserved) and every
    /// primitive's interleaved vertex/index buffers. The returned meshes parallel
    /// <paramref name="asset"/>.Meshes; instances are the caller's to place.</summary>
    public PbrMesh[] UploadMesh(GltfAsset asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var materialIds = new int[asset.Materials.Length];
        for (var i = 0; i < asset.Materials.Length; i++)
        {
            materialIds[i] = Materials.AddMaterial(in asset.Materials[i], asset.Images);
        }
        var fallbackMaterial = -1;

        var meshes = new PbrMesh[asset.Meshes.Length];
        for (var m = 0; m < asset.Meshes.Length; m++)
        {
            var primitives = new PbrPrimitive[asset.Meshes[m].Primitives.Length];
            for (var p = 0; p < primitives.Length; p++)
            {
                var source = asset.Meshes[m].Primitives[p];
                var materialId = source.MaterialIndex >= 0
                    ? materialIds[source.MaterialIndex]
                    : (fallbackMaterial >= 0 ? fallbackMaterial : fallbackMaterial = Materials.AddDefaultMaterial(new Vector4(0.8f, 0.8f, 0.8f, 1f)));
                primitives[p] = UploadPrimitive(source.Vertices, source.Indices, materialId);
            }
            meshes[m] = new PbrMesh(primitives);
        }
        return meshes;
    }

    /// <summary>Upload one interleaved primitive (12 floats per vertex: pos3/normal3/uv2/tan4 —
    /// the GltfPrimitive layout). Also the entry point for procedural geometry.</summary>
    public PbrPrimitive UploadPrimitive(float[] vertices, uint[] indices, int materialId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vbDesc = new BufferDesc("PbrVertices", 0, BufferUsage.Vertex);
        var vb = _renderer.CreateBufferWithData(in vbDesc, (ReadOnlySpan<float>)vertices);
        var ibDesc = new BufferDesc("PbrIndices", 0, BufferUsage.Index);
        var ib = _renderer.CreateBufferWithData(in ibDesc, (ReadOnlySpan<uint>)indices);
        _ownedBuffers.Add(vb);
        _ownedBuffers.Add(ib);
        return new PbrPrimitive(
            vb, ib, (uint)indices.Length,
            (ulong)vertices.Length * sizeof(float), (ulong)indices.Length * sizeof(uint), materialId);
    }

    /// <summary>Render one frame: frame UBO upload, draw-ring fill, opaque-then-blend command
    /// stream (blend back-to-front by view depth), one Submit.</summary>
    public void RenderFrame(PbrScene scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var view = scene.Camera.View;
        var viewProjection = PbrMath.ViewProjection(scene.Camera.View, scene.Camera.Projection);
        UploadFrameUniforms(scene);

        // Partition + sort. View-space depth of the instance origin orders blended draws
        // back-to-front (larger distance first). Opaque stays in submission order (depth
        // buffer resolves it; pipeline switches are already minimal with ≤2 variants).
        _opaque.Clear();
        _blend.Clear();
        foreach (var instance in scene.Instances)
        {
            var world = instance.Model.Translation;
            var viewPos = Vector3.Transform(world, view);
            foreach (var primitive in instance.Mesh.Primitives)
            {
                if (Materials.IsBlend(primitive.MaterialId)) _blend.Add((instance, primitive, viewPos.Z));
                else _opaque.Add((instance, primitive, viewPos.Z));
            }
        }
        // RH view space looks down −Z: more negative Z = farther. Ascending Z sort = far first.
        _blend.Sort(static (a, b) => a.ViewDepth.CompareTo(b.ViewDepth));

        var totalDraws = _opaque.Count + _blend.Count;
        if (totalDraws > MaxDrawsPerFrame)
            throw new InvalidOperationException(
                $"{totalDraws} draws exceed the {MaxDrawsPerFrame}-slot draw ring; split the scene or grow MaxDrawsPerFrame.");

        _commandWriter.ResetWrittenCount();
        var encoder = new RenderCommandEncoder(_commandWriter);
        _passes[0].Colors.Slot0 = new ColorAttachmentDesc(
            RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, scene.ClearColor);

        encoder.BeginPass(0);
        var drawIndex = 0;
        EncodeBucket(ref encoder, _opaque, BlendMode.Opaque, viewProjection, ref drawIndex);
        EncodeBucket(ref encoder, _blend, BlendMode.AlphaBlend, viewProjection, ref drawIndex);
        encoder.EndPass();

        if (drawIndex > 0)
        {
            _renderer.UpdateBuffer<byte>(_drawUniformRing, 0, _drawStaging.AsSpan(0, drawIndex * (int)_drawStride));
        }

        var stream = new RenderCommandStream(_commandWriter.WrittenMemory, _passes);
        _renderer.Submit(in stream);
    }

    private void EncodeBucket(
        ref RenderCommandEncoder encoder,
        List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> bucket,
        BlendMode blend,
        in Matrix4x4 viewProjection,
        ref int drawIndex)
    {
        if (bucket.Count == 0) return;

        encoder.SetPipeline(GetPipeline(blend));
        encoder.SetBindGroup(1, _frameGroup);

        foreach (var (instance, primitive, _) in bucket)
        {
            var uniforms = new DrawUniformsGpu
            {
                Mvp = instance.Model * viewProjection,
                Model = instance.Model,
                NormalMatrix = PbrMath.NormalMatrix(instance.Model),
                Highlight = new Vector4(instance.Highlight, 0f, 0f, 0f),
            };
            MemoryMarshal.Write(_drawStaging.AsSpan(drawIndex * (int)_drawStride), in uniforms);

            encoder.SetBindGroup(0, _drawGroup, dynamicOffset: (uint)(drawIndex * _drawStride));
            encoder.SetBindGroup(2, Materials.GetBindGroup(primitive.MaterialId));
            encoder.SetVertexBuffer(0, primitive.VertexBuffer, 0, primitive.VertexByteLength);
            encoder.SetIndexBuffer(primitive.IndexBuffer, IndexFormat.Uint32, 0, primitive.IndexByteLength);
            encoder.DrawIndexed(new DrawIndexedCommand(primitive.IndexCount, 1, 0, 0, 0));
            drawIndex++;
        }
    }

    private void UploadFrameUniforms(PbrScene scene)
    {
        var frame = new FrameUniformsGpu
        {
            CameraPos = new Vector4(scene.Camera.Position, 0f),
            Ambient = new Vector4(scene.Ambient.Sky, scene.Ambient.Exposure),
            AmbientEquator = new Vector4(scene.Ambient.Equator, Math.Min(scene.Lights.Count, FrameUniformsGpu.MaxSceneLights)),
            AmbientGround = new Vector4(scene.Ambient.Ground, scene.Ambient.Flat ? 1f : 0f),
            AaSettings = new Vector4(0f, _specularAaVariance, _specularAaClamp, 0f),
        };
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            frame.Lights[i] = scene.Lights[i].ToGpu();
        }
        _renderer.UpdateBuffer<FrameUniformsGpu>(_frameUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref frame, 1));
    }

    private PipelineHandle GetPipeline(BlendMode blend)
    {
        if (_pipelines.TryGetValue(blend, out var pipeline)) return pipeline;
        pipeline = _renderer.CreatePipeline(
            _program,
            _renderer.ColorFormat,
            depthStencilFormat: TextureFormat.Depth32Float,
            blend: blend,
            depthWriteEnabled: blend == BlendMode.Opaque, // blended surfaces read but don't write depth
            fragmentEntryPoint: _useSrgbEntryPoint ? "fragmentMainSrgb" : "fragmentMain");
        _pipelines[blend] = pipeline;
        return pipeline;
    }

    internal int PipelineVariantCountForTest => _pipelines.Count;
    internal bool UsesSrgbEntryPointForTest => _useSrgbEntryPoint;

    private static bool IsSrgbFormat(TextureFormat format) =>
        format is TextureFormat.Rgba8UnormSrgb or TextureFormat.Bgra8UnormSrgb;

    private TextureHandle CreateDepthTexture(uint width, uint height)
    {
        var desc = new TextureDesc(
            "PbrDepth", width, height, 1, 1, 1,
            TextureDimension.D2, TextureFormat.Depth32Float, TextureUsage.RenderAttachment);
        return _renderer.CreateTexture(in desc);
    }

    private BindGroupLayoutDesc FindGroup(uint groupIndex)
    {
        foreach (var group in _program.Layout.Groups)
        {
            if (group.GroupIndex == groupIndex) return group;
        }
        throw new InvalidOperationException($"PBR program reflects no bind group {groupIndex}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Materials.Dispose();
        foreach (var pipeline in _pipelines.Values) _renderer.DestroyPipeline(pipeline);
        foreach (var buffer in _ownedBuffers) _renderer.DestroyBuffer(buffer);
        _renderer.DestroyBindGroup(_drawGroup);
        _renderer.DestroyBindGroup(_frameGroup);
        _renderer.DestroyBuffer(_drawUniformRing);
        _renderer.DestroyBuffer(_frameUniformBuffer);
        _renderer.DestroyTexture(_depthTexture);
    }
}
