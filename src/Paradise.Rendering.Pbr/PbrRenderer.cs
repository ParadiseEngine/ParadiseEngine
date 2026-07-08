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
    private const int FloatsPerVertex = 12;         // pos3/normal3/uv2/tan4 interleave
    private const uint ShadowAtlasSize = 4096;      // shared shadow atlas (Depth32Float)
    private const uint ShadowTileSize = 1024;       // one atlas cell
    private const int ShadowAtlasColumns = 4;       // 4096 / 1024
    private const int MaxShadowTiles = ShadowAtlasColumns * ShadowAtlasColumns; // 16
    private const int ShadowMainPassIndex = 1;      // pass 0 = shadow depth, pass 1 = main color

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
    private readonly RenderPassDesc[] _passes = new RenderPassDesc[2]; // [0] shadow depth, [1] main
    private readonly List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> _opaque = [];
    private readonly List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> _blend = [];
    private readonly List<BufferHandle> _ownedBuffers = [];
    // Shadow mapping: a depth atlas filled by a depth-only caster pass, sampled by the main pass.
    private readonly ShaderProgramDesc _shadowProgram;
    private readonly PipelineHandle _shadowPipeline;
    private readonly TextureHandle _shadowAtlas;
    private readonly SamplerHandle _shadowSampler;
    private readonly BufferHandle _shadowDrawRing;
    private readonly BindGroupHandle _shadowDrawGroup;
    private readonly byte[] _shadowStaging;
    // Per-frame shadow plan: one render "view" per shadow-casting light face, plus per-light atlas
    // tile assignment (base tile / face count; base tile -1 = not shadowed this frame).
    private readonly List<(int LightIndex, int Face, uint Tile, Matrix4x4 Vp)> _shadowViews = [];
    private readonly int[] _shadowBaseTile = new int[FrameUniformsGpu.MaxSceneLights];
    private readonly int[] _shadowFaceCount = new int[FrameUniformsGpu.MaxSceneLights];
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

        // Shadow atlas: a single Depth32Float texture that is both the depth target of the caster
        // pass and a sampled depth texture in the main pass. Paired with a comparison sampler.
        var atlasDesc = new TextureDesc(
            "PbrShadowAtlas", ShadowAtlasSize, ShadowAtlasSize, 1, 1, 1,
            TextureDimension.D2, TextureFormat.Depth32Float,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding);
        _shadowAtlas = renderer.CreateTexture(in atlasDesc);
        _shadowSampler = renderer.CreateSampler(new SamplerDesc(
            "PbrShadowSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest,
            MaxAnisotropy: 1, Compare: CompareFunction.LessEqual));

        // Frame group now carries the shadow atlas (binding 1, DepthTexture) + comparison sampler
        // (binding 2), matching pbr.slang's reserved group-1 slots.
        var frameGroupDesc = new BindGroupDesc("PbrFrameGroup", FindGroup(1), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _frameUniformBuffer, 0, (ulong)Unsafe.SizeOf<FrameUniformsGpu>()),
            BindGroupEntryDesc.ForTexture(1, _shadowAtlas),
            BindGroupEntryDesc.ForSampler(2, _shadowSampler),
        });
        _frameGroup = renderer.CreateBindGroup(in frameGroupDesc);
        var drawGroupDesc = new BindGroupDesc("PbrDrawGroup", FindGroup(0), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _drawUniformRing, 0, (ulong)Unsafe.SizeOf<DrawUniformsGpu>()),
        });
        _drawGroup = renderer.CreateBindGroup(in drawGroupDesc);

        Materials = new MaterialResourceCache(renderer, _program, maxAnisotropy);

        // Shadow caster program + depth-only pipeline. Its group-0 draw UBO is a dynamic-offset ring
        // like the main one; the vertex layout reads position from the full interleaved mesh stride
        // (shadow.slang declares only location 0).
        var shadowProgram = WebGpuRenderer.LoadShaderProgram(typeof(PbrRenderer).Assembly, "Shaders.shadow");
        var shadowGroups = (BindGroupLayoutDesc[])shadowProgram.Layout.Groups.Clone();
        for (var i = 0; i < shadowGroups.Length; i++)
        {
            if (shadowGroups[i].GroupIndex != 0) continue;
            shadowGroups[i] = new BindGroupLayoutDesc(0, [shadowGroups[i].Entries[0] with { HasDynamicOffset = true }]);
        }
        _shadowProgram = new ShaderProgramDesc(
            shadowProgram.Modules,
            new PipelineLayoutDesc(shadowGroups, shadowProgram.Layout.PushConstants),
            shadowProgram.VertexBuffers)
        {
            UniformBlocks = shadowProgram.UniformBlocks,
        };
        var meshStride = _program.VertexBuffers[0].Stride;
        var shadowVertexLayout = new[]
        {
            new VertexBufferLayoutDesc(meshStride, VertexStepMode.Vertex,
                new[] { new VertexAttributeDesc(0, VertexFormat.Float32x3, 0) }),
        };
        _shadowPipeline = renderer.CreateDepthOnlyPipeline(_shadowProgram, TextureFormat.Depth32Float, shadowVertexLayout);

        var shadowRingDesc = new BufferDesc("PbrShadowDrawRing", (ulong)_drawStride * MaxDrawsPerFrame, BufferUsage.Uniform | BufferUsage.CopyDst);
        _shadowDrawRing = renderer.CreateBuffer(in shadowRingDesc);
        _shadowStaging = new byte[_drawStride * MaxDrawsPerFrame];
        var shadowDrawGroupDesc = new BindGroupDesc("PbrShadowDrawGroup", FindGroup(_shadowProgram, 0), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _shadowDrawRing, 0, (ulong)Unsafe.SizeOf<ShadowDrawUniformsGpu>()),
        });
        _shadowDrawGroup = renderer.CreateBindGroup(in shadowDrawGroupDesc);

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _depthTexture = CreateDepthTexture(_width, _height);
        // Pass 0 = shadow depth (no color, atlas depth attachment); pass 1 = main color + depth.
        _passes[0] = new RenderPassDesc(colorAttachmentCount: 0)
        {
            Depth = new DepthAttachmentDesc(_shadowAtlas, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f),
        };
        _passes[ShadowMainPassIndex] = new RenderPassDesc(colorAttachmentCount: 1)
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
        _passes[ShadowMainPassIndex].Depth = new DepthAttachmentDesc(_depthTexture, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f);
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

        // Object-space AABB from position (floats 0..2 of each 12-float vertex) — feeds the
        // directional shadow frustum fit.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var v = 0; v + 2 < vertices.Length; v += FloatsPerVertex)
        {
            var p = new Vector3(vertices[v], vertices[v + 1], vertices[v + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        if (vertices.Length < FloatsPerVertex) { min = max = Vector3.Zero; }

        return new PbrPrimitive(
            vb, ib, (uint)indices.Length,
            (ulong)vertices.Length * sizeof(float), (ulong)indices.Length * sizeof(uint), materialId,
            min, max);
    }

    /// <summary>Render one frame: frame UBO upload, draw-ring fill, opaque-then-blend command
    /// stream (blend back-to-front by view depth), one Submit.</summary>
    public void RenderFrame(PbrScene scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var view = scene.Camera.View;
        var viewProjection = PbrMath.ViewProjection(scene.Camera.View, scene.Camera.Projection);

        // Partition + sort. View-space depth of the instance origin orders blended draws
        // back-to-front (larger distance first). Opaque stays in submission order (depth
        // buffer resolves it) and doubles as the shadow-caster set.
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

        // Shadows: allocate atlas tiles to every shadow-casting light — directional/spot take one
        // tile, point takes six cube-face tiles — and compute each face's light-space matrix, fit
        // to the opaque casters' world AABB. When none, the shadow pass just clears the atlas.
        _shadowViews.Clear();
        Array.Fill(_shadowBaseTile, -1);
        Array.Clear(_shadowFaceCount);
        if (_opaque.Count > 0)
        {
            ComputeWorldBounds(out var center, out var extent);
            var nextTile = 0;
            for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
            {
                var light = scene.Lights[i];
                if (!light.CastsShadows) continue;
                var faceCount = light.Type == PbrLightType.Point ? 6 : 1;
                if (nextTile + faceCount > MaxShadowTiles) continue; // won't fit; a later smaller light still can
                _shadowBaseTile[i] = nextTile;
                _shadowFaceCount[i] = faceCount;
                for (var f = 0; f < faceCount; f++)
                {
                    _shadowViews.Add((i, f, (uint)(nextTile + f), ComputeLightMatrix(light, f, center, extent)));
                }
                nextTile += faceCount;
            }
        }

        // Shadow ring budget (separate from the main ring): views × casters. Hard-fail up front —
        // like the main-pass check — so a partial atlas (silently missing shadows) can't ship.
        var shadowDrawTotal = _shadowViews.Count * _opaque.Count;
        if (shadowDrawTotal > MaxDrawsPerFrame)
            throw new InvalidOperationException(
                $"{shadowDrawTotal} shadow-caster draws ({_shadowViews.Count} views × {_opaque.Count} casters) exceed the {MaxDrawsPerFrame}-slot shadow ring.");

        UploadFrameUniforms(scene);

        _commandWriter.ResetWrittenCount();
        var encoder = new RenderCommandEncoder(_commandWriter);
        _passes[ShadowMainPassIndex].Colors.Slot0 = new ColorAttachmentDesc(
            RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, scene.ClearColor);

        // Pass 0: fill the shadow atlas (depth-only). Skipped entirely when no light casts — no
        // light samples an unfilled atlas (each carries base tile -1), so the clear is unnecessary.
        var shadowDraws = 0;
        if (_shadowViews.Count > 0)
        {
            encoder.BeginPass(0);
            EncodeShadowAtlas(ref encoder, ref shadowDraws);
            encoder.EndPass();
        }

        // Pass 1: main color, sampling the atlas.
        encoder.BeginPass(ShadowMainPassIndex);
        var drawIndex = 0;
        EncodeBucket(ref encoder, _opaque, BlendMode.Opaque, viewProjection, ref drawIndex);
        EncodeBucket(ref encoder, _blend, BlendMode.AlphaBlend, viewProjection, ref drawIndex);
        encoder.EndPass();

        if (shadowDraws > 0)
            _renderer.UpdateBuffer<byte>(_shadowDrawRing, 0, _shadowStaging.AsSpan(0, shadowDraws * (int)_drawStride));
        if (drawIndex > 0)
            _renderer.UpdateBuffer<byte>(_drawUniformRing, 0, _drawStaging.AsSpan(0, drawIndex * (int)_drawStride));

        var stream = new RenderCommandStream(_commandWriter.WrittenMemory, _passes);
        _renderer.Submit(in stream);
    }

    // Depth-only atlas fill: for each planned shadow view, restrict the viewport to its tile and
    // draw every opaque caster with lightMvp = model × faceViewProjection (mirrors the main
    // Mvp = model × viewProjection so the shadow shader matches pbr.slang).
    private void EncodeShadowAtlas(ref RenderCommandEncoder encoder, ref int drawIndex)
    {
        encoder.SetPipeline(_shadowPipeline);
        foreach (var (_, _, tile, vp) in _shadowViews)
        {
            var col = (uint)(tile % ShadowAtlasColumns) * ShadowTileSize;
            var row = (uint)(tile / ShadowAtlasColumns) * ShadowTileSize;
            encoder.SetViewport(col, row, ShadowTileSize, ShadowTileSize);
            foreach (var (instance, primitive, _) in _opaque)
            {
                // Budget guaranteed by the up-front shadowDrawTotal check in RenderFrame.
                var uniforms = new ShadowDrawUniformsGpu { LightMvp = instance.Model * vp };
                MemoryMarshal.Write(_shadowStaging.AsSpan(drawIndex * (int)_drawStride), in uniforms);
                encoder.SetBindGroup(0, _shadowDrawGroup, dynamicOffset: (uint)(drawIndex * _drawStride));
                encoder.SetVertexBuffer(0, primitive.VertexBuffer, 0, primitive.VertexByteLength);
                encoder.SetIndexBuffer(primitive.IndexBuffer, IndexFormat.Uint32, 0, primitive.IndexByteLength);
                encoder.DrawIndexed(new DrawIndexedCommand(primitive.IndexCount, 1, 0, 0, 0));
                drawIndex++;
            }
        }
    }

    // Light-space view-projection for one shadow face: directional = ortho fit to the scene AABB;
    // spot = perspective down the cone; point = one of six 90°-FOV cube faces.
    private static Matrix4x4 ComputeLightMatrix(PbrLight light, int face, Vector3 center, Vector3 extent)
    {
        switch (light.Type)
        {
            case PbrLightType.Spot:
            {
                var aim = light.Direction.LengthSquared() > 1e-6f ? Vector3.Normalize(-light.Direction) : -Vector3.UnitY;
                var up = MathF.Abs(aim.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
                var view = PbrMath.LookAt(light.Position, light.Position + aim, up);
                var fov = Math.Clamp(light.SpotOuterDegrees * (MathF.PI / 180f) * 1.05f, 0.1f, 3.0f);
                var proj = PbrMath.Perspective(fov, 1f, 0.05f, MathF.Max(light.Range, 1f));
                return PbrMath.ViewProjection(view, proj);
            }
            case PbrLightType.Point:
            {
                var (dir, up) = CubeFace(face);
                var view = PbrMath.LookAt(light.Position, light.Position + dir, up);
                var proj = PbrMath.Perspective(MathF.PI / 2f, 1f, 0.05f, MathF.Max(light.Range, 1f));
                return PbrMath.ViewProjection(view, proj);
            }
            default: // Directional
                return ComputeDirectionalLightMatrix(light.Direction, center, extent);
        }
    }

    // Standard cube-face direction/up (RH), indexed to match the shader's getPointShadowFace:
    // +X, -X, +Y, -Y, +Z, -Z.
    private static (Vector3 Dir, Vector3 Up) CubeFace(int face) => face switch
    {
        0 => (Vector3.UnitX, -Vector3.UnitY),
        1 => (-Vector3.UnitX, -Vector3.UnitY),
        2 => (Vector3.UnitY, Vector3.UnitZ),
        3 => (-Vector3.UnitY, -Vector3.UnitZ),
        4 => (Vector3.UnitZ, -Vector3.UnitY),
        _ => (-Vector3.UnitZ, -Vector3.UnitY),
    };

    // World-space AABB over the opaque casters (their object-space bounds transformed by Model).
    private void ComputeWorldBounds(out Vector3 center, out Vector3 extent)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (instance, primitive, _) in _opaque)
        {
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? primitive.LocalMin.X : primitive.LocalMax.X,
                    (c & 2) == 0 ? primitive.LocalMin.Y : primitive.LocalMax.Y,
                    (c & 4) == 0 ? primitive.LocalMin.Z : primitive.LocalMax.Z);
                var wp = Vector3.Transform(corner, instance.Model);
                min = Vector3.Min(min, wp);
                max = Vector3.Max(max, wp);
            }
        }
        if (min.X > max.X) { min = max = Vector3.Zero; }
        center = (min + max) * 0.5f;
        extent = (max - min) * 0.5f;
    }

    // Directional light view-projection: place a camera along the light direction, looking at the
    // scene center, and fit an off-center ortho box to the scene AABB in light space (RH, clip-Z
    // [0,1]). Matches bank-heist (up-vector guard for near-vertical light, XY/Z padding).
    private static Matrix4x4 ComputeDirectionalLightMatrix(Vector3 surfaceToLight, Vector3 center, Vector3 extent)
    {
        var lightDir = surfaceToLight.LengthSquared() > 1e-6f ? Vector3.Normalize(surfaceToLight) : Vector3.UnitY;
        const float depthPad = 32f;
        const float xyPad = 1f;
        var radius = MathF.Max(4f, 0.5f * extent.Length());
        var eye = center + lightDir * (radius + depthPad);
        var up = MathF.Abs(lightDir.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var lightView = PbrMath.LookAt(eye, center, up);

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (var c = 0; c < 8; c++)
        {
            var corner = center + new Vector3(
                (c & 1) == 0 ? -extent.X : extent.X,
                (c & 2) == 0 ? -extent.Y : extent.Y,
                (c & 4) == 0 ? -extent.Z : extent.Z);
            var lp = Vector3.Transform(corner, lightView);
            minX = MathF.Min(minX, lp.X); maxX = MathF.Max(maxX, lp.X);
            minY = MathF.Min(minY, lp.Y); maxY = MathF.Max(maxY, lp.Y);
            minZ = MathF.Min(minZ, lp.Z); maxZ = MathF.Max(maxZ, lp.Z);
        }
        // RH light space: the scene sits at negative Z. near/far are positive distances.
        var near = MathF.Max(0.01f, -maxZ - depthPad);
        var far = MathF.Max(near + 1f, -minZ + depthPad);
        var lightProj = PbrMath.OrthographicOffCenter(minX - xyPad, maxX + xyPad, minY - xyPad, maxY + xyPad, near, far);
        return PbrMath.ViewProjection(lightView, lightProj);
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
            ShadowSettings = new Vector4(1f / ShadowAtlasSize, 0f, 0f, 0f),
        };
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            frame.Lights[i] = scene.Lights[i].ToGpu();
        }

        // Per-face light-space matrices from the shadow plan.
        foreach (var (lightIndex, face, _, vp) in _shadowViews)
        {
            frame.SceneLightShadowMatrices[lightIndex * 6 + face] = vp;
        }
        // Per-light atlas params (columns, face count, tile scale, soft flag) + base tile + strength.
        const float tileScale = ShadowTileSize / (float)ShadowAtlasSize; // 0.25
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            if (_shadowBaseTile[i] < 0) continue;
            var light = frame.Lights[i];
            light.SpotAngles.Z = _shadowBaseTile[i];
            light.SpotAngles.W = Math.Clamp(scene.Lights[i].ShadowStrength, 0f, 1f);
            light.ShadowAtlas = new Vector4(ShadowAtlasColumns, _shadowFaceCount[i], tileScale, scene.Lights[i].SoftShadows ? 1f : 0f);
            frame.Lights[i] = light;
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

    private BindGroupLayoutDesc FindGroup(uint groupIndex) => FindGroup(_program, groupIndex);

    private static BindGroupLayoutDesc FindGroup(ShaderProgramDesc program, uint groupIndex)
    {
        foreach (var group in program.Layout.Groups)
        {
            if (group.GroupIndex == groupIndex) return group;
        }
        throw new InvalidOperationException($"Program reflects no bind group {groupIndex}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Materials.Dispose();
        foreach (var pipeline in _pipelines.Values) _renderer.DestroyPipeline(pipeline);
        foreach (var buffer in _ownedBuffers) _renderer.DestroyBuffer(buffer);
        _renderer.DestroyPipeline(_shadowPipeline);
        _renderer.DestroyBindGroup(_shadowDrawGroup);
        _renderer.DestroyBuffer(_shadowDrawRing);
        _renderer.DestroySampler(_shadowSampler);
        _renderer.DestroyTexture(_shadowAtlas);
        _renderer.DestroyBindGroup(_drawGroup);
        _renderer.DestroyBindGroup(_frameGroup);
        _renderer.DestroyBuffer(_drawUniformRing);
        _renderer.DestroyBuffer(_frameUniformBuffer);
        _renderer.DestroyTexture(_depthTexture);
    }
}
