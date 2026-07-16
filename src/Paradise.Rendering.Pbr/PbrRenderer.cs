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
    private const uint ShadowMapSize = 1024;        // per-layer shadow map resolution (Depth32Float)
    // One array layer per shadow view: dir/spot = 1 layer, point = 6 cube-face layers. Cap = every
    // scene light casting a 6-face point shadow. The array is sized dynamically each frame (grow-only)
    // to the layers actually in use, so a scene with one directional light allocates a single layer.
    private const int MaxShadowLayers = FrameUniformsGpu.MaxSceneLights * 6; // 48

    private readonly WebGpuRenderer _renderer;
    private readonly ShaderProgramDesc _program;
    private readonly bool _useSrgbEntryPoint;
    private readonly uint _drawStride;
    private readonly BufferHandle _frameUniformBuffer;
    private readonly BufferHandle _drawUniformRing;
    private BindGroupHandle _frameGroup; // rebuilt whenever the shadow array is (re)allocated
    private readonly BindGroupHandle _drawGroup;
    private readonly Dictionary<BlendMode, PipelineHandle> _pipelines = new();
    private readonly byte[] _drawStaging;
    private readonly ArrayBufferWriter<RenderCommand> _commandWriter = new(256);
    // [0 .. shadowViews.Count-1] = one depth-only shadow pass per layer, [shadowViews.Count] = main.
    // Grow-only; each frame a length-(shadowViews.Count+1) prefix is submitted.
    private RenderPassDesc[] _passes = new RenderPassDesc[2];
    private readonly List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> _opaque = [];
    private readonly List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> _blend = [];
    private readonly List<BufferHandle> _ownedBuffers = [];
    // Gradient-sky background: a fullscreen triangle (no vertex buffer) drawn first in the main pass
    // with depth-write off / compare Always, so geometry overdraws it. Colours come from a tiny UBO.
    private readonly PipelineHandle _skyPipeline;
    private readonly BufferHandle _skyUniformBuffer;
    private readonly BindGroupHandle _skyGroup;
    // HDR post-process seam: the main pass (PBR + sky) now renders LINEAR HDR into _hdrTexture
    // (Rgba16Float) instead of tonemapping to the swapchain in-shader; a fullscreen composite pass
    // tonemaps it (+ optional bloom) to the surface. This is where future post effects hook in.
    private readonly PipelineHandle _compositePipeline;
    private readonly BufferHandle _compositeUniformBuffer;
    private readonly SamplerHandle _compositeSampler;
    private BindGroupLayoutDesc _compositeGroupLayout;
    private TextureHandle _hdrTexture;
    private TextureViewHandle _hdrView;
    private BindGroupHandle _compositeGroup;
    private const TextureFormat HdrFormat = TextureFormat.Rgba16Float;
    // Bloom mip chain (progressive dual-filter, COD-style): a half-res base halving to ~BloomMinDim.
    // bright-pass (threshold) → downsample chain → additive upsample chain; _bloomViews[0] is the
    // result composite adds. Pipelines built once; textures/views/groups rebuilt on resize.
    private const int BloomMaxLevels = 6;
    private const uint BloomMinDim = 8;
    private readonly PipelineHandle _bloomBrightPipeline;
    private readonly PipelineHandle _bloomDownPipeline;
    private readonly PipelineHandle _bloomUpPipeline;
    private readonly BufferHandle _bloomUniformBuffer;
    private BindGroupLayoutDesc _bloomGroupLayout;
    private int _bloomLevels;
    private TextureHandle[] _bloomTextures = [];
    private TextureViewHandle[] _bloomViews = [];
    private BindGroupHandle[] _bloomGroups = []; // _bloomGroups[i] samples _bloomTextures[i]
    private BindGroupHandle _bloomHdrGroup;        // samples _hdrView (bright pass source)
    // SSAO: a world-position pre-pass (reuses the main draw ring/group + a dedicated pipeline) writes
    // _positionTexture (Rgba32Float, offscreen color) with its own depth (_prepassDepthAux). The PBR
    // shader (group 3) samples it via textureLoad and darkens ambient. The group is rebuilt on resize.
    private readonly ShaderProgramDesc _positionPrepassProgram;
    private readonly PipelineHandle _positionPrepassPipeline;
    private readonly BufferHandle _ssaoUniformBuffer;
    private BindGroupHandle _ssaoGroup;
    private TextureHandle _positionTexture;
    private TextureViewHandle _positionView;
    private TextureHandle _prepassDepthAux;
    private BindGroupLayoutDesc _ssaoGroupLayout;
    // Sky-reflection specular (Godot reflected_light_source = Sky): the gradient sky GGX-prefiltered
    // on the CPU into a small LUT (u: reflection.y, v: roughness — the gradient is azimuth-symmetric).
    // Rgba8UnormSrgb: radiance ∈ [0,1] on the standard hardware-decoded color path. Rebaked only
    // when the sky colours/curves change; group 3 binds it alongside the SSAO resources.
    private readonly TextureHandle _skySpecLutTexture;
    private readonly TextureViewHandle _skySpecLutView;
    private readonly SamplerHandle _skySpecSampler;
    // The environment-BRDF (DFG) table: the real GGX pre-integral, baked once at startup —
    // Godot's integrate_dfg.glsl integrand (Schlick-GGX, IBL k = α²/2, 1024 Hammersley samples).
    private readonly TextureHandle _dfgLutTexture;
    private readonly TextureViewHandle _dfgLutView;
    private (Vector3, Vector3, Vector3, Vector3, float, float, Vector3, float, float, float)? _skySpecKey;
    // Forward+ froxel clustering: one uint bitmask per froxel (bit i = sceneLights[i] overlaps),
    // CPU-binned each frame from conservative view-space sphere bounds. 32x32 px tiles x 32
    // logarithmic Z slices, matching Godot's cluster shape (Godot bins on the GPU with a compute
    // rasterizer; the CPU route produces the same conservative result at our light counts).
    private BufferHandle _clusterBuffer;
    private uint[] _clusterMasks = [];
    private int _clusterTilesX;
    private int _clusterTilesY;
    private const int ClusterTileSize = 32;
    private const int ClusterZSlices = 32;
    // Shadow mapping: a Depth32Float 2D-array (one layer per shadow view) filled by per-layer
    // depth-only caster passes, sampled as texture_depth_2d_array by the main pass.
    private readonly ShaderProgramDesc _shadowProgram;
    private readonly PipelineHandle _shadowPipeline;
    private readonly SamplerHandle _shadowSampler;
    private readonly BufferHandle _shadowDrawRing;
    private readonly BindGroupHandle _shadowDrawGroup;
    private readonly byte[] _shadowStaging;
    // The shadow-map array + its views, (re)allocated by EnsureShadowArray (grow-only). The D2Array
    // view is what the shader samples; each per-layer D2 view is a render target for one shadow pass.
    private TextureHandle _shadowArray;
    private TextureViewHandle _shadowArrayView;
    private TextureViewHandle[] _shadowLayerViews = [];
    private uint _shadowLayerCapacity;
    // Per-frame shadow plan: one render "view" per shadow-casting light face, plus per-light array
    // assignment (base layer / face count; base layer -1 = not shadowed this frame).
    private readonly List<(int LightIndex, int Face, uint Layer, Matrix4x4 Vp)> _shadowViews = [];
    private readonly int[] _shadowBaseLayer = new int[FrameUniformsGpu.MaxSceneLights];
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

        // Shadow-map array comparison sampler (clamp so a PCF tap near a layer edge reads that
        // layer's border, never wraps). The array texture + views + frame bind group are built by
        // EnsureShadowArray below and re-sized each frame to the layers actually in use.
        _shadowSampler = renderer.CreateSampler(new SamplerDesc(
            "PbrShadowSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest,
            MaxAnisotropy: 1, Compare: CompareFunction.LessEqual));

        // Froxel mask buffer must exist before the frame bind group references it (binding 3).
        // _width/_height are set later in the ctor, so size from the ctor parameters directly.
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        EnsureClusterBuffer();

        // Allocate the initial single-layer array + frame bind group (binding 1 = D2Array shadow
        // view, binding 2 = comparison sampler; matches pbr.slang's reserved group-1 slots). A valid
        // array must always be bound even when nothing casts, hence the minimum of one layer.
        EnsureShadowArray(1);

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

        // Gradient-sky background program + pipeline. Fullscreen triangle (no vertex buffer — the
        // vertex shader uses SV_VertexID), depth-write off + compare Always so it never occludes or
        // is occluded by scene geometry. Fragment entry follows the same sRGB decision as the scene.
        var skyProgram = WebGpuRenderer.LoadShaderProgram(typeof(PbrRenderer).Assembly, "Shaders.sky");
        _skyPipeline = renderer.CreatePipeline(
            skyProgram, HdrFormat, // linear HDR into _hdrTexture, like the PBR pass; composite tonemaps
            depthStencilFormat: TextureFormat.Depth32Float,
            depthWriteEnabled: false,
            depthCompare: CompareFunction.Always,
            fragmentEntryPoint: "skyFragment"); // always linear
        var skyUniformDesc = new BufferDesc("PbrSkyUniforms", (ulong)Unsafe.SizeOf<SkyUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst);
        _skyUniformBuffer = renderer.CreateBuffer(in skyUniformDesc);
        _skyGroup = renderer.CreateBindGroup(new BindGroupDesc("PbrSkyGroup", FindGroup(skyProgram, 0), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _skyUniformBuffer, 0, (ulong)Unsafe.SizeOf<SkyUniformsGpu>()),
        }));

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _depthTexture = CreateDepthTexture(_width, _height);

        // SSAO world-position pre-pass program + pipeline. Reuses the main draw ring/group (its group
        // 0 is the same DrawUniforms, made dynamic-offset), renders opaque geometry to an Rgba32Float
        // position target with its own depth. Vertex layout is position-only over the mesh stride.
        var positionProgram = WebGpuRenderer.LoadShaderProgram(typeof(PbrRenderer).Assembly, "Shaders.positionPrepass");
        var positionGroups = (BindGroupLayoutDesc[])positionProgram.Layout.Groups.Clone();
        for (var i = 0; i < positionGroups.Length; i++)
        {
            if (positionGroups[i].GroupIndex != 0) continue;
            positionGroups[i] = new BindGroupLayoutDesc(0, [positionGroups[i].Entries[0] with { HasDynamicOffset = true }]);
        }
        _positionPrepassProgram = new ShaderProgramDesc(
            positionProgram.Modules,
            new PipelineLayoutDesc(positionGroups, positionProgram.Layout.PushConstants),
            positionProgram.VertexBuffers)
        {
            UniformBlocks = positionProgram.UniformBlocks,
        };
        var positionVertexLayout = new[]
        {
            new VertexBufferLayoutDesc(meshStride, VertexStepMode.Vertex,
                new[] { new VertexAttributeDesc(0, VertexFormat.Float32x3, 0) }),
        };
        _positionPrepassPipeline = renderer.CreatePipeline(
            _positionPrepassProgram, TextureFormat.Rgba32Float,
            depthStencilFormat: TextureFormat.Depth32Float,
            depthWriteEnabled: true,
            depthCompare: CompareFunction.Less);

        _ssaoGroupLayout = FindGroup(3); // group 3 of the main PBR program: SSAO + sky-specular LUT
        _ssaoUniformBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrSsaoUniforms", (ulong)Unsafe.SizeOf<SsaoUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
        _positionTexture = CreatePositionTexture(_width, _height);
        _prepassDepthAux = CreateDepthTexture(_width, _height);
        _skySpecLutTexture = renderer.CreateTexture(new TextureDesc(
            "PbrSkySpecularLut", SkySpecLutWidth, SkySpecLutHeight, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Rgba8UnormSrgb, TextureUsage.TextureBinding | TextureUsage.CopyDst));
        _skySpecLutView = renderer.CreateTextureView(new TextureViewDesc(
            "PbrSkySpecularLutView", _skySpecLutTexture, TextureViewDimension.D2, 0, 1));
        _skySpecSampler = renderer.CreateSampler(new SamplerDesc(
            "PbrSkySpecularSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest));
        // The LUT starts black (no reflections until a sky is provided); baked on first use.
        _renderer.WriteTexture(_skySpecLutTexture, 0, new byte[SkySpecLutWidth * SkySpecLutHeight * 4],
            SkySpecLutWidth * 4, SkySpecLutHeight, SkySpecLutWidth, SkySpecLutHeight);
        _dfgLutTexture = renderer.CreateTexture(new TextureDesc(
            "PbrDfgLut", DfgLutSize, DfgLutSize, 1, 1, 1, TextureDimension.D2,
            TextureFormat.Rgba16Float, TextureUsage.TextureBinding | TextureUsage.CopyDst));
        _dfgLutView = renderer.CreateTextureView(new TextureViewDesc(
            "PbrDfgLutView", _dfgLutTexture, TextureViewDimension.D2, 0, 1));
        BakeDfgLut();
        RebuildSsaoGroup();

        // HDR scene target + composite pass. The main pass renders LINEAR HDR here; the composite
        // fullscreen pass tonemaps it (using the tone operators moved out of pbr/sky) to the
        // swapchain, so the whole frame composites in linear HDR (enabling bloom). The composite
        // pipeline targets the surface format (sRGB decision applies HERE now, not the main pass).
        _compositeSampler = renderer.CreateSampler(new SamplerDesc(
            "PbrCompositeSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest));

        // Bloom pipelines (built once; share one bind-group layout: source texture + sampler + params).
        var bloomProgram = WebGpuRenderer.LoadShaderProgram(typeof(PbrRenderer).Assembly, "Shaders.bloom");
        _bloomGroupLayout = FindGroup(bloomProgram, 0);
        _bloomUniformBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrBloomUniforms", (ulong)Unsafe.SizeOf<CompositeUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
        _bloomBrightPipeline = renderer.CreatePipeline(bloomProgram, HdrFormat, fragmentEntryPoint: "brightFragment");
        _bloomDownPipeline = renderer.CreatePipeline(bloomProgram, HdrFormat, fragmentEntryPoint: "downsampleFragment");
        _bloomUpPipeline = renderer.CreatePipeline(bloomProgram, HdrFormat, blend: BlendMode.Additive, fragmentEntryPoint: "upsampleFragment");

        _hdrTexture = CreateHdrTexture(_width, _height);
        EnsureHdrView();
        EnsureBloomChain(_width, _height);

        var compositeProgram = WebGpuRenderer.LoadShaderProgram(typeof(PbrRenderer).Assembly, "Shaders.composite");
        _compositeGroupLayout = FindGroup(compositeProgram, 0);
        _compositePipeline = renderer.CreatePipeline(
            compositeProgram, renderer.ColorFormat,
            fragmentEntryPoint: _useSrgbEntryPoint ? "compositeFragmentSrgb" : "compositeFragment");
        _compositeUniformBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrCompositeUniforms", (ulong)Unsafe.SizeOf<CompositeUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
        RebuildCompositeGroup();

        // The pass list is (re)built each frame: N per-layer shadow passes, an optional SSAO
        // position pre-pass, the main pass (LINEAR HDR → _hdrTexture), then the composite pass.
    }

    // (Re)allocate the shadow-map array (grow-only) to hold at least <paramref name="layerCount"/>
    // full-resolution layers, plus the D2Array sampling view, the per-layer D2 render views, and the
    // frame bind group that references the sampling view. A single shared texture across all shadow
    // views keeps the frame group stable between frames of equal (or smaller) shadow-layer count.
    private void EnsureShadowArray(uint layerCount)
    {
        layerCount = Math.Max(1, layerCount);
        if (_shadowArray.IsValid && layerCount <= _shadowLayerCapacity) return;

        DestroyShadowArray();

        var desc = new TextureDesc(
            "PbrShadowArray", ShadowMapSize, ShadowMapSize, layerCount, 1, 1,
            TextureDimension.D2, TextureFormat.Depth32Float,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding);
        _shadowArray = _renderer.CreateTexture(in desc);
        _shadowArrayView = _renderer.CreateTextureView(new TextureViewDesc(
            "PbrShadowArrayView", _shadowArray, TextureViewDimension.D2Array, 0, layerCount));
        _shadowLayerViews = new TextureViewHandle[layerCount];
        for (var i = 0u; i < layerCount; i++)
            _shadowLayerViews[i] = _renderer.CreateTextureView(new TextureViewDesc(
                $"PbrShadowLayer{i}", _shadowArray, TextureViewDimension.D2, i, 1));
        _shadowLayerCapacity = layerCount;

        RebuildFrameGroup();
    }

    private void RebuildFrameGroup()
    {
        if (_frameGroup.IsValid) _renderer.DestroyBindGroup(_frameGroup);
        var frameGroupDesc = new BindGroupDesc("PbrFrameGroup", FindGroup(1), new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _frameUniformBuffer, 0, (ulong)Unsafe.SizeOf<FrameUniformsGpu>()),
            BindGroupEntryDesc.ForTextureView(1, _shadowArrayView),
            BindGroupEntryDesc.ForSampler(2, _shadowSampler),
            BindGroupEntryDesc.ForBuffer(3, _clusterBuffer, 0, (ulong)(_clusterMasks.Length * sizeof(uint))),
        });
        _frameGroup = _renderer.CreateBindGroup(in frameGroupDesc);
    }

    private void DestroyShadowArray()
    {
        if (_shadowArrayView.IsValid) _renderer.DestroyTextureView(_shadowArrayView);
        foreach (var v in _shadowLayerViews)
            if (v.IsValid) _renderer.DestroyTextureView(v);
        if (_shadowArray.IsValid) _renderer.DestroyTexture(_shadowArray);
        _shadowArray = default;
        _shadowArrayView = default;
        _shadowLayerViews = [];
        _shadowLayerCapacity = 0;
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
        _renderer.DestroyTexture(_positionTexture);
        _renderer.DestroyTexture(_prepassDepthAux);
        _renderer.DestroyTexture(_hdrTexture);
        _width = width;
        _height = height;
        _depthTexture = CreateDepthTexture(width, height);
        _positionTexture = CreatePositionTexture(width, height);
        _prepassDepthAux = CreateDepthTexture(width, height);
        _hdrTexture = CreateHdrTexture(width, height);
        EnsureHdrView(); // HDR target changed → new view (bloom + composite groups reference it)
        EnsureBloomChain(width, height); // mip sizes + the HDR-sampling group changed
        RebuildSsaoGroup(); // position texture changed → rebind
        RebuildCompositeGroup(); // HDR + bloom result views changed → rebind
        EnsureClusterBuffer(); // tile counts changed → new mask buffer + frame group rebind
        // The main pass's depth attachment is rebuilt from _depthTexture each frame in RenderFrame.
    }

    // (Re)allocate the froxel mask buffer for the current resolution and rebind the frame group.
    private void EnsureClusterBuffer()
    {
        var tilesX = (int)((_width + ClusterTileSize - 1) / ClusterTileSize);
        var tilesY = (int)((_height + ClusterTileSize - 1) / ClusterTileSize);
        if (tilesX == _clusterTilesX && tilesY == _clusterTilesY && _clusterBuffer.IsValid) return;
        if (_clusterBuffer.IsValid) _renderer.DestroyBuffer(_clusterBuffer);
        _clusterTilesX = tilesX;
        _clusterTilesY = tilesY;
        _clusterMasks = new uint[tilesX * tilesY * ClusterZSlices * 2]; // two mask words per froxel (64 lights)
        _clusterBuffer = _renderer.CreateBuffer(new BufferDesc(
            "PbrClusterMasks", (ulong)(_clusterMasks.Length * sizeof(uint)),
            BufferUsage.Storage | BufferUsage.CopyDst));
        if (_frameGroup.IsValid) RebuildFrameGroup();
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
    /// the GltfPrimitive layout). Also the entry point for procedural geometry.
    /// <paramref name="dynamic"/> makes the vertex buffer updatable via
    /// <see cref="UpdatePrimitiveVertices"/> — the CPU-skinning path re-writes it per frame.</summary>
    public PbrPrimitive UploadPrimitive(float[] vertices, uint[] indices, int materialId, bool dynamic = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vbDesc = new BufferDesc("PbrVertices", 0, dynamic ? BufferUsage.Vertex | BufferUsage.CopyDst : BufferUsage.Vertex);
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

    /// <summary>Re-write a dynamic primitive's vertex stream (CPU skinning). The primitive must
    /// have been uploaded with <c>dynamic: true</c>; the float count must match the upload.
    /// NOTE: the shadow frustum fit uses the UPLOAD-time AABB — poses that swing far outside
    /// the bind-pose bounds can clip at the directional shadow edge (bank-heist has the same
    /// property).</summary>
    public void UpdatePrimitiveVertices(in PbrPrimitive primitive, ReadOnlySpan<float> vertices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((ulong)vertices.Length * sizeof(float) != primitive.VertexByteLength)
            throw new ArgumentException(
                $"Vertex float count {vertices.Length} does not match the uploaded primitive " +
                $"({primitive.VertexByteLength / sizeof(float)}).");
        _renderer.UpdateBuffer(primitive.VertexBuffer, 0, vertices);
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

        // Shadows: assign one array layer per shadow view to every shadow-casting light —
        // directional/spot take one layer, point takes six cube-face layers — and compute each
        // face's light-space matrix, fit to the opaque casters' world AABB. When none, the shadow
        // passes are skipped entirely (nothing samples an unwritten layer; base layer stays -1).
        _shadowViews.Clear();
        Array.Fill(_shadowBaseLayer, -1);
        Array.Clear(_shadowFaceCount);
        var shadowLayerCount = 0;
        if (_opaque.Count > 0)
        {
            ComputeWorldBounds(out var center, out var extent);
            for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
            {
                var light = scene.Lights[i];
                if (!light.CastsShadows) continue;
                var faceCount = light.Type == PbrLightType.Point ? 6 : 1;
                if (shadowLayerCount + faceCount > MaxShadowLayers) continue; // won't fit; a smaller later light still can
                _shadowBaseLayer[i] = shadowLayerCount;
                _shadowFaceCount[i] = faceCount;
                for (var f = 0; f < faceCount; f++)
                {
                    _shadowViews.Add((i, f, (uint)(shadowLayerCount + f), ComputeLightMatrix(light, f, center, extent)));
                }
                shadowLayerCount += faceCount;
            }
        }

        // Size the shadow-map array to the layers actually in use (grow-only; min one so the frame
        // bind group always has a valid depth array bound). This is the runtime "create shadowmap
        // based on light+shadow count" — a single directional light allocates exactly one layer.
        EnsureShadowArray((uint)shadowLayerCount);

        // Shadow ring budget (separate from the main ring): views × casters. Hard-fail up front —
        // like the main-pass check — so a partial fill (silently missing shadows) can't ship.
        var shadowDrawTotal = _shadowViews.Count * _opaque.Count;
        if (shadowDrawTotal > MaxDrawsPerFrame)
            throw new InvalidOperationException(
                $"{shadowDrawTotal} shadow-caster draws ({_shadowViews.Count} views × {_opaque.Count} casters) exceed the {MaxDrawsPerFrame}-slot shadow ring.");

        BuildClusters(scene);
        UploadFrameUniforms(scene);
        UploadSsaoUniforms(scene);

        if (scene.HasSkyBackground)
        {
            if (scene.SkyReflections) EnsureSkySpecularLut(scene);
            // Inverse of the same view-projection used for MVP, so the sky shader can unproject each
            // background pixel's NDC to a world-space eye ray (uploaded raw-bytes, like MVP). Falls
            // back to identity on a singular VP (degenerate camera) rather than uploading NaN — same
            // precedent as PbrMath.NormalMatrix/TryScreenPointToRay.
            if (!Matrix4x4.Invert(viewProjection, out var invViewProj))
                invViewProj = Matrix4x4.Identity;
            var skyUniforms = new SkyUniformsGpu
            {
                // skyTop.w / skyHorizon.w carry the sun halo thresholds (see sky.slang).
                SkyTop = new Vector4(scene.SkyTopColor, scene.SkySunAngleMaxCos),
                SkyHorizon = new Vector4(scene.SkyHorizonColor, scene.SkySunInvCurve),
                GroundBottom = new Vector4(scene.SkyGroundBottom, 1f),
                GroundHorizon = new Vector4(scene.SkyGroundHorizon, 1f),
                // zw + CameraPos.w carry the tone operator so the sky shader can blend the LINEAR
                // gradient first and tonemap per-pixel (Godot's order; see sky.slang header).
                Params = new Vector4(scene.SkySkyCurveInv, scene.SkyGroundCurveInv, (float)scene.Tonemap.Mode, scene.Tonemap.Exposure),
                CameraPos = new Vector4(scene.Camera.Position, scene.Tonemap.White),
                SunDirection = new Vector4(scene.SkySunDirection, scene.SkySunEnabled ? 1f : 0f),
                SunColor = new Vector4(scene.SkySunColorEnergy, scene.SkySunSizeCos),
                InvViewProj = invViewProj,
            };
            _renderer.UpdateBuffer<SkyUniformsGpu>(_skyUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref skyUniforms, 1));
        }

        // Build the pass list: one depth-only pass per shadow layer, then an optional SSAO
        // position pre-pass, then the main color pass.
        var hasPrepass = scene.Ssao.Enabled && _opaque.Count > 0;
        var prepassIndex = hasPrepass ? _shadowViews.Count : -1;
        var mainPassIndex = _shadowViews.Count + (hasPrepass ? 1 : 0);
        // Bloom inserts 2·levels−1 passes (1 bright + (L−1) down + (L−1) additive up) between the
        // main HDR pass and the composite pass; disabled = zero passes, composite reads unused bloom.
        var bloomEnabled = scene.Bloom.Enabled && _bloomLevels > 1;
        var bloomStart = mainPassIndex + 1;
        var bloomPassCount = bloomEnabled ? 2 * _bloomLevels - 1 : 0;
        var compositeIndex = bloomStart + bloomPassCount;
        if (_passes.Length < compositeIndex + 1) _passes = new RenderPassDesc[compositeIndex + 1];
        for (var k = 0; k < _shadowViews.Count; k++)
        {
            _passes[k] = new RenderPassDesc(colorAttachmentCount: 0)
            {
                Depth = new DepthAttachmentDesc(
                    _shadowArray, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f,
                    DepthView: _shadowLayerViews[_shadowViews[k].Layer]),
            };
        }
        if (hasPrepass)
        {
            _passes[prepassIndex] = new RenderPassDesc(colorAttachmentCount: 1)
            {
                Depth = new DepthAttachmentDesc(_prepassDepthAux, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f),
            };
            // Offscreen color: the Rgba32Float position target, cleared to 0 (background w = 0).
            _passes[prepassIndex].Colors.Slot0 = new ColorAttachmentDesc(
                RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, new ColorRgba(0f, 0f, 0f, 0f), ColorView: _positionView);
        }
        _passes[mainPassIndex] = new RenderPassDesc(colorAttachmentCount: 1)
        {
            Depth = new DepthAttachmentDesc(_depthTexture, LoadOp.Clear, StoreOp.Store, ClearDepth: 1f),
        };
        // Main color pass → the offscreen HDR scene target (linear), not the swapchain.
        _passes[mainPassIndex].Colors.Slot0 = new ColorAttachmentDesc(
            RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, scene.ClearColor, ColorView: _hdrView);

        if (bloomEnabled)
        {
            // bright → _bloomViews[0]; downsample i → _bloomViews[i+1] (Clear); additive upsample
            // k+1 → _bloomViews[k] (Load, so the tent blur accumulates onto the down-mip content).
            var black = new ColorRgba(0f, 0f, 0f, 1f);
            _passes[bloomStart] = new RenderPassDesc(colorAttachmentCount: 1);
            _passes[bloomStart].Colors.Slot0 = new ColorAttachmentDesc(
                RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, black, ColorView: _bloomViews[0]);
            for (var i = 0; i < _bloomLevels - 1; i++)
            {
                var p = bloomStart + 1 + i;
                _passes[p] = new RenderPassDesc(colorAttachmentCount: 1);
                _passes[p].Colors.Slot0 = new ColorAttachmentDesc(
                    RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, black, ColorView: _bloomViews[i + 1]);
            }
            for (var j = 0; j < _bloomLevels - 1; j++)
            {
                var target = _bloomLevels - 2 - j;
                var p = bloomStart + _bloomLevels + j;
                _passes[p] = new RenderPassDesc(colorAttachmentCount: 1);
                _passes[p].Colors.Slot0 = new ColorAttachmentDesc(
                    RenderViewHandle.Invalid, LoadOp.Load, StoreOp.Store, black, ColorView: _bloomViews[target]);
            }
            var bloomUniforms = new CompositeUniformsGpu { Tone = new Vector4(scene.Bloom.Threshold, scene.Bloom.Knee, 0f, 0f) };
            _renderer.UpdateBuffer<CompositeUniformsGpu>(_bloomUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref bloomUniforms, 1));
        }

        // Composite pass → the swapchain: samples the HDR target, tonemaps (+ bloom), no depth.
        _passes[compositeIndex] = new RenderPassDesc(colorAttachmentCount: 1);
        _passes[compositeIndex].Colors.Slot0 = new ColorAttachmentDesc(
            RenderViewHandle.Invalid, LoadOp.Clear, StoreOp.Store, new ColorRgba(0f, 0f, 0f, 1f));

        var compositeUniforms = new CompositeUniformsGpu
        {
            Tone = new Vector4((float)scene.Tonemap.Mode, scene.Tonemap.Exposure, scene.Tonemap.White,
                bloomEnabled ? scene.Bloom.Intensity : 0f),
        };
        _renderer.UpdateBuffer<CompositeUniformsGpu>(_compositeUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref compositeUniforms, 1));

        _commandWriter.ResetWrittenCount();
        var encoder = new RenderCommandEncoder(_commandWriter);

        // One depth-only pass per shadow layer: fill that layer with every opaque caster's depth.
        var shadowDraws = 0;
        EncodeShadowLayers(ref encoder, ref shadowDraws);

        // SSAO position pre-pass: render opaque world positions using the SAME main-draw-ring offsets
        // that EncodeBucket fills for opaque (so no extra ring space is needed).
        if (hasPrepass)
            EncodeDepthPrepass(ref encoder, prepassIndex);

        // Main color pass, sampling the shadow array.
        encoder.BeginPass(mainPassIndex);
        // Gradient-sky background first (fullscreen, no depth write) so geometry draws over it.
        if (scene.HasSkyBackground)
        {
            encoder.SetPipeline(_skyPipeline);
            encoder.SetBindGroup(0, _skyGroup);
            encoder.Draw(new DrawCommand(3, 1, 0, 0));
        }
        var drawIndex = 0;
        EncodeBucket(ref encoder, _opaque, BlendMode.Opaque, viewProjection, ref drawIndex);
        EncodeBucket(ref encoder, _blend, BlendMode.AlphaBlend, viewProjection, ref drawIndex);
        encoder.EndPass();

        if (bloomEnabled)
        {
            // bright: HDR scene → _bloomViews[0]
            encoder.BeginPass(bloomStart);
            encoder.SetPipeline(_bloomBrightPipeline);
            encoder.SetBindGroup(0, _bloomHdrGroup);
            encoder.Draw(new DrawCommand(3, 1, 0, 0));
            encoder.EndPass();
            // downsample: _bloomViews[i] → _bloomViews[i+1]
            for (var i = 0; i < _bloomLevels - 1; i++)
            {
                encoder.BeginPass(bloomStart + 1 + i);
                encoder.SetPipeline(_bloomDownPipeline);
                encoder.SetBindGroup(0, _bloomGroups[i]);
                encoder.Draw(new DrawCommand(3, 1, 0, 0));
                encoder.EndPass();
            }
            // additive upsample: _bloomViews[k+1] → _bloomViews[k], smallest first
            for (var j = 0; j < _bloomLevels - 1; j++)
            {
                var source = _bloomLevels - 1 - j;
                encoder.BeginPass(bloomStart + _bloomLevels + j);
                encoder.SetPipeline(_bloomUpPipeline);
                encoder.SetBindGroup(0, _bloomGroups[source]);
                encoder.Draw(new DrawCommand(3, 1, 0, 0));
                encoder.EndPass();
            }
        }

        // Composite: fullscreen triangle sampling the HDR scene target → tonemap (+ bloom) → swapchain.
        encoder.BeginPass(compositeIndex);
        encoder.SetPipeline(_compositePipeline);
        encoder.SetBindGroup(0, _compositeGroup);
        encoder.Draw(new DrawCommand(3, 1, 0, 0));
        encoder.EndPass();

        if (shadowDraws > 0)
            _renderer.UpdateBuffer<byte>(_shadowDrawRing, 0, _shadowStaging.AsSpan(0, shadowDraws * (int)_drawStride));
        if (drawIndex > 0)
            _renderer.UpdateBuffer<byte>(_drawUniformRing, 0, _drawStaging.AsSpan(0, drawIndex * (int)_drawStride));

        var stream = new RenderCommandStream(_commandWriter.WrittenMemory, _passes.AsMemory(0, compositeIndex + 1));
        _renderer.Submit(in stream);
    }

    // Per-layer depth-only fill: each shadow view is its own render pass writing one full array
    // layer. Every opaque caster is drawn with lightMvp = model × faceViewProjection (mirrors the
    // main Mvp = model × viewProjection so the shadow shader matches pbr.slang). No viewport math —
    // each layer owns the whole [0,1] and the default viewport covers it.
    private void EncodeShadowLayers(ref RenderCommandEncoder encoder, ref int drawIndex)
    {
        for (var k = 0; k < _shadowViews.Count; k++)
        {
            var vp = _shadowViews[k].Vp;
            encoder.BeginPass(k);
            encoder.SetPipeline(_shadowPipeline);
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
            encoder.EndPass();
        }
    }

    // SSAO position pre-pass: render opaque world positions into _positionTexture (offscreen color) +
    // _prepassDepthAux. Reuses the MAIN draw ring/group — opaque[i] uses the same dynamic offset that
    // EncodeBucket fills for it, so no extra ring space or upload is needed.
    private void EncodeDepthPrepass(ref RenderCommandEncoder encoder, int passIndex)
    {
        encoder.BeginPass(passIndex);
        encoder.SetPipeline(_positionPrepassPipeline);
        for (var i = 0; i < _opaque.Count; i++)
        {
            var primitive = _opaque[i].Primitive;
            encoder.SetBindGroup(0, _drawGroup, dynamicOffset: (uint)(i * _drawStride));
            encoder.SetVertexBuffer(0, primitive.VertexBuffer, 0, primitive.VertexByteLength);
            encoder.SetIndexBuffer(primitive.IndexBuffer, IndexFormat.Uint32, 0, primitive.IndexByteLength);
            encoder.DrawIndexed(new DrawIndexedCommand(primitive.IndexCount, 1, 0, 0, 0));
        }
        encoder.EndPass();
    }

    // Upload group-3 SSAO uniforms. Intensity 0 (SSAO off, or no prepass this frame) makes the
    // shader skip position sampling — gated on the same condition RenderFrame uses to decide
    // whether the prepass actually runs (Enabled && _opaque.Count > 0), so a zero-opaque-instance
    // frame never samples an unwritten/stale _positionTexture.
    private void UploadSsaoUniforms(PbrScene scene)
    {
        var s = scene.Ssao;
        var hasPrepass = s.Enabled && _opaque.Count > 0;
        var u = new SsaoUniformsGpu
        {
            Params = new Vector4(hasPrepass ? s.Intensity : 0f, MathF.Max(s.Radius, 1e-3f), s.Bias, MathF.Max(s.Power, 1e-3f)),
            Screen = new Vector4(1f / _width, 1f / _height, _width, _height),
        };
        _renderer.UpdateBuffer<SsaoUniformsGpu>(_ssaoUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref u, 1));
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
        encoder.SetBindGroup(3, _ssaoGroup); // SSAO uniforms + position pre-pass (group 3)

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

    // Cluster depth range for the CURRENT frame (extracted from the projection; the shader must
    // slice with the same values, so they ride the frame UBO).
    private float _clusterNear = 0.05f;
    private float _clusterFar = 100f;

    // World-space camera forward from the row-vector view matrix (third column is -forward).
    private static Vector3 CameraForward(in Matrix4x4 view) =>
        Vector3.Normalize(new Vector3(-view.M13, -view.M23, -view.M33));

    private static int ClusterSlice(float viewZ, float near, float far) =>
        Math.Clamp((int)(MathF.Log(Math.Max(viewZ, near) / near) / MathF.Log(far / near) * ClusterZSlices),
            0, ClusterZSlices - 1);

    /// <summary>Forward+ CPU binning: for every point/spot light, mark the froxels its bounding
    /// sphere (position, range) can touch. Conservative on purpose — tile/slice ranges from a
    /// projected view-space AABB, padded ±1 froxel against CPU/GPU float divergence at cell
    /// boundaries. A false-positive bit costs a near-zero shading add; a false negative would
    /// change pixels, so inclusion always wins. Directional lights are never clustered.</summary>
    private void BuildClusters(PbrScene scene)
    {
        Array.Clear(_clusterMasks);
        var view = scene.Camera.View;
        var proj = scene.Camera.Projection;
        // Near/far from the row-vector perspective projection (M33 = f/(n-f), M43 = n·f/(n-f)).
        // Degenerate extraction (orthographic/custom) keeps the previous values.
        if (MathF.Abs(proj.M33) > 1e-6f && MathF.Abs(proj.M33 + 1f) > 1e-6f)
        {
            var n = proj.M43 / proj.M33;
            var f = proj.M43 / (proj.M33 + 1f);
            if (n > 0f && f > n) { _clusterNear = n; _clusterFar = f; }
        }

        var count = Math.Min(scene.Lights.Count, FrameUniformsGpu.MaxSceneLights);
        for (var i = 0; i < count; i++)
        {
            var light = scene.Lights[i];
            if (light.Type == PbrLightType.Directional) continue;
            var radius = Math.Max(light.Range, 0.01f);
            var centerView = Vector3.Transform(light.Position, view);
            var viewZ = -centerView.Z; // row-vector look-at: -Z is forward
            if (viewZ + radius <= _clusterNear || viewZ - radius >= _clusterFar) continue;

            var slice0 = Math.Max(ClusterSlice(viewZ - radius, _clusterNear, _clusterFar) - 1, 0);
            var slice1 = Math.Min(ClusterSlice(viewZ + radius, _clusterNear, _clusterFar) + 1, ClusterZSlices - 1);

            // Screen rect from the 8 corners of the view-space AABB around the sphere. Any corner
            // at or in front of the near plane → the sphere may wrap the camera → full screen.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            var fullScreen = false;
            for (var c = 0; c < 8 && !fullScreen; c++)
            {
                var corner = centerView + new Vector3(
                    (c & 1) == 0 ? -radius : radius,
                    (c & 2) == 0 ? -radius : radius,
                    (c & 4) == 0 ? -radius : radius);
                if (corner.Z >= -_clusterNear) { fullScreen = true; break; }
                var clip = Vector4.Transform(new Vector4(corner, 1f), proj);
                var ndcX = clip.X / clip.W;
                var ndcY = clip.Y / clip.W;
                var sx = (ndcX * 0.5f + 0.5f) * _width;
                var sy = (1f - (ndcY * 0.5f + 0.5f)) * _height;
                minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
                minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
            }

            int tx0 = 0, tx1 = _clusterTilesX - 1, ty0 = 0, ty1 = _clusterTilesY - 1;
            if (!fullScreen)
            {
                tx0 = Math.Clamp((int)(minX / ClusterTileSize) - 1, 0, _clusterTilesX - 1);
                tx1 = Math.Clamp((int)(maxX / ClusterTileSize) + 1, 0, _clusterTilesX - 1);
                ty0 = Math.Clamp((int)(minY / ClusterTileSize) - 1, 0, _clusterTilesY - 1);
                ty1 = Math.Clamp((int)(maxY / ClusterTileSize) + 1, 0, _clusterTilesY - 1);
                if (tx1 < tx0 || ty1 < ty0) continue; // fully off-screen
            }

            var word = i >> 5;
            var bit = 1u << (i & 31);
            for (var sz = slice0; sz <= slice1; sz++)
                for (var ty = ty0; ty <= ty1; ty++)
                {
                    var row = (sz * _clusterTilesY + ty) * _clusterTilesX;
                    for (var tx = tx0; tx <= tx1; tx++)
                        _clusterMasks[(row + tx) * 2 + word] |= bit;
                }
        }
        _renderer.UpdateBuffer<uint>(_clusterBuffer, 0, _clusterMasks);
        if (Environment.GetEnvironmentVariable("PARADISE_CLUSTER_DEBUG") == "1")
        {
            var empty = 0; var bits = 0L;
            foreach (var m in _clusterMasks)
            {
                if (m == 0) empty++;
                bits += System.Numerics.BitOperations.PopCount(m);
            }
            Console.Error.WriteLine($"[CLUSTER] froxels={_clusterMasks.Length / 2} emptyWords={empty} avgBits={(double)bits/(_clusterMasks.Length / 2):F2}");
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
            // x: sky-reflection specular enabled (Godot reflected_light_source = Sky).
            AaSettings = new Vector4(
                scene.HasSkyBackground && scene.SkyReflections ? 1f : 0f,
                _specularAaVariance, _specularAaClamp, 0f),
            CameraForward = new Vector4(CameraForward(scene.Camera.View), _clusterNear),
            ClusterParams = new Vector4(_clusterTilesX, _clusterTilesY, ClusterZSlices, _clusterFar),
            // x: 1/shadowMapSize (per-layer texel). yzw: tone mapping — mode, exposure, white point.
            ShadowSettings = new Vector4(
                1f / ShadowMapSize,
                (float)scene.Tonemap.Mode,
                scene.Tonemap.Exposure,
                scene.Tonemap.White),
        };
        // L2 sky-SH ambient: coefficients pass through verbatim; [0].w flags the SH path on.
        if (scene.Ambient.Sh is { Length: 9 } sh)
        {
            frame.AmbientSh[0] = new Vector4(sh[0], 1f);
            for (var i = 1; i < 9; i++)
            {
                frame.AmbientSh[i] = new Vector4(sh[i], 0f);
            }
        }
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            frame.Lights[i] = scene.Lights[i].ToGpu();
        }

        // Per-face light-space matrices from the shadow plan.
        foreach (var (lightIndex, face, _, vp) in _shadowViews)
        {
            frame.SceneLightShadowMatrices[lightIndex * 6 + face] = vp;
        }
        // Per-light shadow params: base array layer (spotAngles.z), strength (spotAngles.w),
        // face count (shadowAtlas.y) and soft-shadow flag (shadowAtlas.w). shadowAtlas.x carries
        // the distance-attenuation decay and .z the LIGHT_PARAM_SPECULAR amount (both set by
        // ToGpu) and must be preserved here.
        for (var i = 0; i < scene.Lights.Count && i < FrameUniformsGpu.MaxSceneLights; i++)
        {
            if (_shadowBaseLayer[i] < 0) continue;
            var light = frame.Lights[i];
            light.SpotAngles.Z = _shadowBaseLayer[i];
            light.SpotAngles.W = Math.Clamp(scene.Lights[i].ShadowStrength, 0f, 1f);
            light.ShadowAtlas = new Vector4(light.ShadowAtlas.X, _shadowFaceCount[i], light.ShadowAtlas.Z, scene.Lights[i].SoftShadows ? 1f : 0f);
            frame.Lights[i] = light;
        }
        _renderer.UpdateBuffer<FrameUniformsGpu>(_frameUniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref frame, 1));
        if (CaptureFrameLightsForTest) _lastFrameLightsForTest = frame.Lights;
    }

    // Test-only readback of the per-frame packed light array (e.g. to assert ShadowAtlas.X survives the
    // shadow-caster rebuild). Off by default so production frames never pay the array copy on the hot path.
    internal bool CaptureFrameLightsForTest;
    private SceneLightArray _lastFrameLightsForTest;
    internal Vector4 GetLightShadowAtlasForTest(int lightIndex) => _lastFrameLightsForTest[lightIndex].ShadowAtlas;

    private PipelineHandle GetPipeline(BlendMode blend)
    {
        if (_pipelines.TryGetValue(blend, out var pipeline)) return pipeline;
        pipeline = _renderer.CreatePipeline(
            _program,
            HdrFormat, // main pass now emits LINEAR HDR into _hdrTexture; the composite pass tonemaps
            depthStencilFormat: TextureFormat.Depth32Float,
            blend: blend,
            depthWriteEnabled: blend == BlendMode.Opaque, // blended surfaces read but don't write depth
            fragmentEntryPoint: "fragmentMain"); // always linear (the sRGB decision moved to composite)
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

    // SSAO world-position pre-pass target: Rgba32Float, both a render target and sampled (textureLoad).
    private TextureHandle CreatePositionTexture(uint width, uint height)
    {
        var desc = new TextureDesc(
            "PbrSsaoPosition", width, height, 1, 1, 1,
            TextureDimension.D2, TextureFormat.Rgba32Float,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding);
        return _renderer.CreateTexture(in desc);
    }

    private TextureHandle CreateHdrTexture(uint width, uint height)
    {
        var desc = new TextureDesc(
            "PbrHdrScene", width, height, 1, 1, 1,
            TextureDimension.D2, HdrFormat,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding);
        return _renderer.CreateTexture(in desc);
    }

    private void EnsureHdrView()
    {
        if (_hdrView.IsValid) _renderer.DestroyTextureView(_hdrView);
        _hdrView = _renderer.CreateTextureView(new TextureViewDesc(
            "PbrHdrSceneView", _hdrTexture, TextureViewDimension.D2, 0, 1));
    }

    // (Re)build the composite bind group; binds the HDR scene + the bloom result (_bloomViews[0]).
    private void RebuildCompositeGroup()
    {
        if (_compositeGroup.IsValid) _renderer.DestroyBindGroup(_compositeGroup);
        _compositeGroup = _renderer.CreateBindGroup(new BindGroupDesc("PbrCompositeGroup", _compositeGroupLayout, new[]
        {
            BindGroupEntryDesc.ForTextureView(0, _hdrView),
            BindGroupEntryDesc.ForSampler(1, _compositeSampler),
            BindGroupEntryDesc.ForTextureView(2, _bloomViews[0]),
            BindGroupEntryDesc.ForBuffer(3, _compositeUniformBuffer, 0, (ulong)Unsafe.SizeOf<CompositeUniformsGpu>()),
        }));
    }

    // (Re)allocate the bloom mip chain sized to the current target: a half-res base halving down to
    // ~BloomMinDim (≤ BloomMaxLevels levels). Each level is an Rgba16Float render target sampled by
    // the next pass; per-level bind groups (source = that level) + one group sampling the HDR scene.
    private void EnsureBloomChain(uint width, uint height)
    {
        DestroyBloomChain();
        var sizes = new List<(uint W, uint H)>();
        uint w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        for (var i = 0; i < BloomMaxLevels; i++)
        {
            sizes.Add((w, h));
            if (w <= BloomMinDim || h <= BloomMinDim) break;
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        _bloomLevels = sizes.Count;
        _bloomTextures = new TextureHandle[_bloomLevels];
        _bloomViews = new TextureViewHandle[_bloomLevels];
        _bloomGroups = new BindGroupHandle[_bloomLevels];
        for (var i = 0; i < _bloomLevels; i++)
        {
            _bloomTextures[i] = _renderer.CreateTexture(new TextureDesc(
                $"PbrBloom{i}", sizes[i].W, sizes[i].H, 1, 1, 1, TextureDimension.D2, HdrFormat,
                TextureUsage.RenderAttachment | TextureUsage.TextureBinding));
            _bloomViews[i] = _renderer.CreateTextureView(new TextureViewDesc(
                $"PbrBloomView{i}", _bloomTextures[i], TextureViewDimension.D2, 0, 1));
        }
        for (var i = 0; i < _bloomLevels; i++)
            _bloomGroups[i] = CreateBloomGroup(_bloomViews[i]);
        _bloomHdrGroup = CreateBloomGroup(_hdrView);
    }

    private BindGroupHandle CreateBloomGroup(TextureViewHandle source) =>
        _renderer.CreateBindGroup(new BindGroupDesc("PbrBloomGroup", _bloomGroupLayout, new[]
        {
            BindGroupEntryDesc.ForTextureView(0, source),
            BindGroupEntryDesc.ForSampler(1, _compositeSampler),
            BindGroupEntryDesc.ForBuffer(2, _bloomUniformBuffer, 0, (ulong)Unsafe.SizeOf<CompositeUniformsGpu>()),
        }));

    private void DestroyBloomChain()
    {
        if (_bloomHdrGroup.IsValid) _renderer.DestroyBindGroup(_bloomHdrGroup);
        foreach (var g in _bloomGroups) if (g.IsValid) _renderer.DestroyBindGroup(g);
        foreach (var v in _bloomViews) if (v.IsValid) _renderer.DestroyTextureView(v);
        foreach (var t in _bloomTextures) if (t.IsValid) _renderer.DestroyTexture(t);
        _bloomGroups = [];
        _bloomViews = [];
        _bloomTextures = [];
    }

    // (Re)build the group-3 bind group: SSAO uniform buffer + the (resized) position texture. The
    // sampled binding uses an explicit view, distinct from the default view the pre-pass renders into.
    private const int DfgLutSize = 128;

    /// <summary>Bake the environment-BRDF (DFG) table: an exact port of Godot's
    /// integrate_dfg.glsl (GGX importance sampling, Schlick-GGX with the IBL k = α²/2,
    /// 1024 Hammersley samples). Stored as (scale = ∫(1−Fc)·G_Vis, bias = ∫Fc·G_Vis) so the
    /// shader computes specular = F0·scale + f90·bias and the multiscatter energy compensation
    /// uses scale + bias; u = NdotV, v = roughness. ~130 ms once at startup.</summary>
    private void BakeDfgLut()
    {
        const int samples = 1024;
        var data = new byte[DfgLutSize * DfgLutSize * 8]; // 4 × 16-bit half channels
        for (var row = 0; row < DfgLutSize; row++)
        {
            var roughness = (row + 0.5f) / DfgLutSize;
            var alpha2 = roughness * roughness * roughness * roughness;
            var k = roughness * roughness / 2f; // Schlick-GGX IBL k
            for (var col = 0; col < DfgLutSize; col++)
            {
                var ndv = (col + 0.5f) / DfgLutSize;
                var v = new Vector3(MathF.Sqrt(1f - ndv * ndv), 0f, ndv); // N = +Z tangent frame
                float a = 0f, b = 0f;
                for (var i = 0; i < samples; i++)
                {
                    var u1 = (float)i / samples;
                    uint bits = (uint)i;
                    bits = (bits << 16) | (bits >> 16);
                    bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
                    bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
                    bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
                    bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
                    var u2 = bits * 2.3283064365386963e-10f;
                    var phi = 2f * MathF.PI * u1;
                    var cosTheta = MathF.Sqrt((1f - u2) / (1f + (alpha2 - 1f) * u2));
                    var sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
                    var h = new Vector3(MathF.Cos(phi) * sinTheta, MathF.Sin(phi) * sinTheta, cosTheta);
                    var l = 2f * Vector3.Dot(v, h) * h - v;
                    var ndl = Math.Clamp(l.Z, 0f, 1f);
                    if (ndl <= 0f) continue;
                    var ndh = Math.Clamp(h.Z, 0f, 1f);
                    var vdh = Math.Clamp(Vector3.Dot(v, h), 0f, 1f);
                    var g = (ndv / (ndv * (1f - k) + k)) * (ndl / (ndl * (1f - k) + k));
                    var gVis = g * vdh / MathF.Max(ndh * ndv, 1e-6f);
                    var fc = MathF.Pow(1f - vdh, 5f);
                    a += fc * gVis;
                    b += gVis;
                }
                a /= samples;
                b /= samples;
                var idx = (row * DfgLutSize + col) * 8;
                WriteHalf(data, idx + 0, b - a); // r = scale (∫(1−Fc)·G_Vis)
                WriteHalf(data, idx + 2, a);     // g = bias  (∫Fc·G_Vis)
                WriteHalf(data, idx + 4, 0f);
                WriteHalf(data, idx + 6, 1f);
            }
        }
        _renderer.WriteTexture(_dfgLutTexture, 0, data, DfgLutSize * 8, DfgLutSize, DfgLutSize, DfgLutSize);

        static void WriteHalf(byte[] dest, int offset, float value)
        {
            var bits = BitConverter.HalfToUInt16Bits((Half)value);
            dest[offset] = (byte)bits;
            dest[offset + 1] = (byte)(bits >> 8);
        }
    }

    private const int SkySpecLutWidth = 64;
    private const int SkySpecLutHeight = 16; // rows 0..7 gradient half, 8..15 sun half
    internal const float SkySpecSunScale = 4f; // HDR headroom for the sun half in 8-bit sRGB texels

    // GGX-prefilter the sky into the specular LUT (split-sum first term, N=V=R convention),
    // split into two row halves:
    //   rows 0..7  — the GRADIENT (azimuth-symmetric → depends only on reflection.y = u).
    //   rows 8..15 — the SUN disk/halo, which is radially symmetric around the sun direction →
    //                depends only on dot(reflection, sunDir) = u. Exact, no cubemap needed.
    // The sun half is stored ÷SkySpecSunScale for HDR headroom in the 8-bit sRGB texel (the
    // disk radiance is colour × energy, typically > 1); the shader multiplies it back.
    // CPU cost ~64×16×64 evaluations, re-run only when the sky or sun changes.
    private void EnsureSkySpecularLut(PbrScene scene)
    {
        var key = (scene.SkyTopColor, scene.SkyHorizonColor, scene.SkyGroundBottom, scene.SkyGroundHorizon,
            scene.SkySkyCurveInv, scene.SkyGroundCurveInv,
            scene.SkySunEnabled ? scene.SkySunColorEnergy : Vector3.Zero,
            scene.SkySunSizeCos, scene.SkySunAngleMaxCos, scene.SkySunInvCurve);
        if (_skySpecKey == key) return;
        _skySpecKey = key;

        Vector3 Radiance(float y)
        {
            y = Math.Clamp(y, -1f, 1f);
            return y >= 0f
                ? Vector3.Lerp(scene.SkyTopColor, scene.SkyHorizonColor,
                    Math.Clamp(MathF.Pow(1f - y, scene.SkySkyCurveInv), 0f, 1f))
                : Vector3.Lerp(scene.SkyGroundBottom, scene.SkyGroundHorizon,
                    Math.Clamp(MathF.Pow(1f + y, scene.SkyGroundCurveInv), 0f, 1f));
        }

        // Godot's sun disk/halo weight (sky_material.cpp) as a function of the cosine to the sun.
        Vector3 SunRadiance(float cosToSun)
        {
            if (!scene.SkySunEnabled) return Vector3.Zero;
            float w;
            if (cosToSun > scene.SkySunSizeCos) w = 1f;
            else if (cosToSun > scene.SkySunAngleMaxCos)
            {
                float c2 = (scene.SkySunSizeCos - cosToSun) / (scene.SkySunSizeCos - scene.SkySunAngleMaxCos);
                w = Math.Clamp(MathF.Pow(1f - c2, scene.SkySunInvCurve), 0f, 1f);
            }
            else return Vector3.Zero;
            return scene.SkySunColorEnergy * (w / SkySpecSunScale);
        }

        static float SrgbEncode(float c)
        {
            c = Math.Clamp(c, 0f, 1f);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        }

        const int samples = 64;
        const int halfRows = SkySpecLutHeight / 2;
        var data = new byte[SkySpecLutWidth * SkySpecLutHeight * 4];
        for (var row = 0; row < SkySpecLutHeight; row++)
        {
            bool sunHalf = row >= halfRows;
            float roughness = (row % halfRows + 0.5f) / halfRows;
            float alpha = roughness * roughness;
            float alpha2 = alpha * alpha;
            for (var col = 0; col < SkySpecLutWidth; col++)
            {
                // For the gradient half, u = reflection.y; for the sun half, u = cos(angle to
                // sun) — either way the radiance is symmetric around the Y axis of this frame.
                float ry = col / (SkySpecLutWidth - 1f) * 2f - 1f;
                var r = new Vector3(MathF.Sqrt(MathF.Max(0f, 1f - ry * ry)), ry, 0f);
                // Tangent frame around R for GGX importance sampling.
                var up = MathF.Abs(r.Y) > 0.999f ? Vector3.UnitX : Vector3.UnitY;
                var tangent = Vector3.Normalize(Vector3.Cross(up, r));
                var bitangent = Vector3.Cross(r, tangent);
                Vector3 acc = default;
                float wSum = 0f;
                for (var s = 0; s < samples; s++)
                {
                    // Hammersley: (i+0.5)/N and the radical inverse of i.
                    float u1 = (s + 0.5f) / samples;
                    uint bits = (uint)s;
                    bits = (bits << 16) | (bits >> 16);
                    bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
                    bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
                    bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
                    bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
                    float u2 = bits * 2.3283064365386963e-10f;
                    float phi = 2f * MathF.PI * u1;
                    float cosTheta = MathF.Sqrt((1f - u2) / (1f + (alpha2 - 1f) * u2));
                    float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
                    var h = tangent * (MathF.Cos(phi) * sinTheta)
                        + bitangent * (MathF.Sin(phi) * sinTheta)
                        + r * cosTheta;
                    var l = 2f * Vector3.Dot(r, h) * h - r;
                    float ndl = Vector3.Dot(r, l);
                    if (ndl <= 0f) continue;
                    acc += (sunHalf ? SunRadiance(l.Y) : Radiance(l.Y)) * ndl;
                    wSum += ndl;
                }
                var c = wSum > 0f ? acc / wSum : (sunHalf ? SunRadiance(ry) : Radiance(ry));
                var i = (row * SkySpecLutWidth + col) * 4;
                data[i + 0] = (byte)MathF.Round(SrgbEncode(c.X) * 255f);
                data[i + 1] = (byte)MathF.Round(SrgbEncode(c.Y) * 255f);
                data[i + 2] = (byte)MathF.Round(SrgbEncode(c.Z) * 255f);
                data[i + 3] = 255;
            }
        }
        _renderer.WriteTexture(_skySpecLutTexture, 0, data,
            SkySpecLutWidth * 4, SkySpecLutHeight, SkySpecLutWidth, SkySpecLutHeight);
    }

    private void RebuildSsaoGroup()
    {
        if (_ssaoGroup.IsValid) _renderer.DestroyBindGroup(_ssaoGroup);
        if (_positionView.IsValid) _renderer.DestroyTextureView(_positionView);
        _positionView = _renderer.CreateTextureView(new TextureViewDesc(
            "PbrSsaoPositionView", _positionTexture, TextureViewDimension.D2, 0, 1));
        _ssaoGroup = _renderer.CreateBindGroup(new BindGroupDesc("PbrSsaoGroup", _ssaoGroupLayout, new[]
        {
            BindGroupEntryDesc.ForBuffer(0, _ssaoUniformBuffer, 0, (ulong)Unsafe.SizeOf<SsaoUniformsGpu>()),
            BindGroupEntryDesc.ForTextureView(1, _positionView),
            BindGroupEntryDesc.ForTextureView(2, _skySpecLutView),
            BindGroupEntryDesc.ForSampler(3, _skySpecSampler),
            BindGroupEntryDesc.ForTextureView(4, _dfgLutView),
        }));
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
        DestroyShadowArray();
        _renderer.DestroyPipeline(_skyPipeline);
        _renderer.DestroyBindGroup(_skyGroup);
        _renderer.DestroyBuffer(_skyUniformBuffer);
        _renderer.DestroyPipeline(_positionPrepassPipeline);
        _renderer.DestroyBindGroup(_ssaoGroup);
        if (_positionView.IsValid) _renderer.DestroyTextureView(_positionView);
        _renderer.DestroyBuffer(_ssaoUniformBuffer);
        _renderer.DestroyTexture(_positionTexture);
        _renderer.DestroyTexture(_prepassDepthAux);
        _renderer.DestroyBindGroup(_drawGroup);
        _renderer.DestroyBindGroup(_frameGroup);
        _renderer.DestroyBuffer(_drawUniformRing);
        _renderer.DestroyBuffer(_frameUniformBuffer);
        _renderer.DestroyTexture(_depthTexture);
        _renderer.DestroyPipeline(_compositePipeline);
        _renderer.DestroyBindGroup(_compositeGroup);
        if (_hdrView.IsValid) _renderer.DestroyTextureView(_hdrView);
        _renderer.DestroyTexture(_hdrTexture);
        _renderer.DestroyBuffer(_compositeUniformBuffer);
        _renderer.DestroySampler(_compositeSampler);
        DestroyBloomChain();
        _renderer.DestroyPipeline(_bloomBrightPipeline);
        _renderer.DestroyPipeline(_bloomDownPipeline);
        _renderer.DestroyPipeline(_bloomUpPipeline);
        _renderer.DestroyBuffer(_bloomUniformBuffer);
    }
}
