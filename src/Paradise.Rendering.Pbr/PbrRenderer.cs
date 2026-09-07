using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System.Buffers;
using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>The PBR scene renderer: a <see cref="RenderPipeline"/> of features over one shared
/// context, driven once per frame through a <see cref="FrameGraph"/>. Owns geometry upload, the
/// material cache and the draw ring; the passes themselves belong to the features in
/// <see cref="Pipeline"/>, which a host may extend.</summary>
public sealed partial class PbrRenderer : IDisposable
{
    private const int FloatsPerVertex = 12;         // pos3/normal3/uv2/tan4 interleave
    private const int SkinFloatsPerVertex = 8;      // joint4 (indices as floats) + weight4
    private const int SkinnedFloatsPerVertex = FloatsPerVertex + SkinFloatsPerVertex; // vertexMainSkinned's stride

    /// <summary>Palette slots allocated up front. 64 characters at 65 joints, or any mix — one
    /// 16 KB storage buffer. Overflowing is reported once rather than silently dropping a palette,
    /// which would draw the mesh collapsed at the origin and read as a rigging bug.</summary>
    public const int MaxSkinnedJoints = 4096;

    private readonly IRenderer _renderer;
    private readonly ILogger _log;
    private readonly PbrContext _ctx;
    private readonly MaterialPrograms _programs;
    private readonly FrameGraph _graph;
    private readonly ArrayBufferWriter<RenderCommand> _commandWriter = new(256);
    private readonly List<BufferHandle> _ownedBuffers = [];
    private readonly ShadowFeature _shadows;
    private readonly SceneFeature _scene;
    private readonly SceneColorCaptureFeature _capture;
    private readonly CompositeFeature _composite;
    private bool _jointOverflowReported;  // report a full palette buffer once, not per instance
    private bool _disposed;
#if PARADISE_PROFILING
    private readonly System.Diagnostics.Stopwatch _clock = new();
#endif

    /// <param name="renderer">The backend the frame is submitted to.</param>
    /// <param name="switches">The engine's feature configuration — the same object the rest of
    /// the process is switched by. The built-in features declare themselves into it and read it
    /// every frame, so a config file that says <c>"rendering.bloom": false</c> reaches this
    /// renderer without the host writing any renderer-specific code.
    ///
    /// <para>REQUIRED, and second in the list, because the alternative was a defaulted last
    /// parameter: a host that forgot it got a private switchboard, every feature at its declared
    /// default, and a config file that reached nothing — with no error and a frame that still
    /// renders. A caller that configures nothing writes <c>new FeatureSwitches()</c> and has said
    /// so.</para></param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="maxAnisotropy">Anisotropic filtering cap for material textures.</param>
    /// <param name="specularAaVariance">Geometric specular-AA strength.</param>
    /// <param name="specularAaClamp">Geometric specular-AA clamp.</param>
    /// <param name="logger">Where engine diagnostics go.</param>
    public PbrRenderer(
        IRenderer renderer, FeatureSwitches switches, uint width, uint height,
        ushort maxAnisotropy = 16, float specularAaVariance = 0.25f, float specularAaClamp = 0.18f,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(switches);
        _renderer = renderer;
        _log = logger ?? NullLogger.Instance;
        _programs = new MaterialPrograms(renderer);
        _ctx = new PbrContext(renderer, _log, _programs, width, height);
        _graph = new FrameGraph(_ctx.Targets, _ctx.BindGroups, _log);
        Materials = new MaterialResourceCache(renderer, _programs.BuiltIn, maxAnisotropy, _ctx.Targets);
        _ctx.Materials = Materials;

        Pipeline = new RenderPipeline(_ctx.Width, _ctx.Height, switches);
        PbrBuiltInFeatures.AddTo(Pipeline, _ctx, specularAaVariance, specularAaClamp);
        // Found rather than held from construction: the list belongs to PbrBuiltInFeatures, and
        // the four the renderer's own API forwards to are reached the same way a host reaches one.
        _shadows = Pipeline.Find<ShadowFeature>()!;
        _scene = Pipeline.Find<SceneFeature>()!;
        _capture = Pipeline.Find<SceneColorCaptureFeature>()!;
        _composite = Pipeline.Find<CompositeFeature>()!;
    }

    public MaterialResourceCache Materials { get; }

    /// <summary>CPU time the last <see cref="RenderFrame"/> spent in each of its phases. Filled
    /// only by a build with <c>-p:ParadiseProfiling=true</c>; zero otherwise.</summary>
    public PbrCpuTimings LastCpuTimings { get; private set; }

    /// <summary>The passes the last frame submitted, in the order a backend times them.</summary>
    public IReadOnlyList<string> LastPassNames => _graph.LivePassNames;

    /// <summary>The features that make up a frame, in the order they set up. A host adds its own
    /// through <see cref="RenderPipeline.Add"/> — after these by default, or at a
    /// <see cref="PbrFeatureOrder"/> slot to land between two of them; they see the engine's
    /// targets by the names in <see cref="PbrTargets"/> and its results by those in
    /// <see cref="PbrResults"/>.</summary>
    public RenderPipeline Pipeline { get; }

    /// <summary>The engine's feature configuration, as this renderer reads it every frame.
    /// Flipping a switch here changes the next frame — see <see cref="PbrFeatures"/> for the
    /// names, and note that a scene's own <c>Enabled</c> flags still have to agree.</summary>
    public FeatureSwitches Switches => Pipeline.Switches;

    /// <summary>Per-layer shadow map resolution. Settable at runtime (the array is recreated on
    /// the next frame); clamped to [256, 8192]. Scenes author this through the export contract's
    /// <c>Lighting.ShadowMapSize</c>; hosts apply it here.</summary>
    public uint ShadowMapSize
    {
        get => _shadows.MapSize;
        set => _shadows.MapSize = value;
    }

    /// <summary>Soft-shadow PCF disk radius, in shadow texels (the penumbra width of every
    /// shadow edge). Scenes author this through the export contract's <c>Lighting.ShadowBlur</c>;
    /// hosts apply it here. Clamped to [0.5, 8].</summary>
    public float ShadowBlurTexels
    {
        get => _shadows.BlurTexels;
        set => _shadows.BlurTexels = value;
    }

    /// <summary>Radius, in world metres, of the area around the CAMERA the directional (sun)
    /// shadow map covers. A camera-centred fit keeps texel density constant no matter how big the
    /// scene grows; the box is snapped to whole texels so it does not shimmer as the camera moves,
    /// and the depth range still spans the scene AABB so tall casters outside the circle keep
    /// casting in. When the scene is smaller than the radius (or the radius is 0) the whole-scene
    /// fit applies — small scenes keep their tighter box.</summary>
    public float DirectionalShadowRadius
    {
        get => _shadows.DirectionalRadius;
        set => _shadows.DirectionalRadius = value;
    }

    /// <summary>Specular anti-aliasing tuning (RenderSettingsData.SpecularAaVariance/Clamp).</summary>
    public void SetSpecularAa(float variance, float clamp) => _scene.SetSpecularAa(variance, clamp);

    /// <summary>Opt-in scene-color capture: when enabled, the main pass splits at the
    /// opaque/blend boundary and the opaque+sky result is blitted (linear HDR) into
    /// <see cref="SceneColorView"/> before the blend bucket renders — so a blend material
    /// (water) can sample what is BEHIND it for screen-space refraction. Enable before creating
    /// the materials that bind the view. Costs one fullscreen blit plus a color+depth reload
    /// per frame while enabled.</summary>
    public bool SceneColorCapture
    {
        get => Switches.IsEnabled(PbrFeatures.SceneColorCapture.Id);
        set => Switches.Set(PbrFeatures.SceneColorCapture.Id, value);
    }

    /// <summary>The captured opaque scene, linear HDR, target-sized — rgb is the opaque+sky
    /// color, ALPHA is the opaque scene's device depth at that pixel (the depth-aware-refraction
    /// rejection signal: a refracted sample with alpha &lt; the sampling fragment's own depth is
    /// geometry in front of the surface — fall back to the unoffset sample). Two consumer
    /// caveats: the fp16 alpha quantizes 32-bit device depth (~5e-4 steps near the far plane),
    /// so treat it as a coarse near/mid-field signal, not a precise depth buffer; and READ THE
    /// DEPTH VIA textureLoad, never a filtering sampler — bilinear across a depth discontinuity
    /// interpolates a depth belonging to no real surface and mis-rejects at silhouettes (the
    /// color half may stay filtered). Invalid while
    /// <see cref="SceneColorCapture"/> is off. RECREATED on <see cref="Resize"/> — rebind
    /// material extra entries from <see cref="SceneColorViewChanged"/> via
    /// <see cref="MaterialResourceCache.UpdateExtraEntry"/>.</summary>
    public TextureViewHandle SceneColorView => _capture.View;

    /// <summary>Raised whenever <see cref="SceneColorView"/> CHANGES: recreated (enabling
    /// capture, or Resize while enabled — rebind material extra entries to the new view) or
    /// destroyed (disabling capture — the view is INVALID in the handler; unbind or repoint
    /// affected materials, never re-bind the stale view). Always fires after every engine-side
    /// rebind, so subscribers see a consistent renderer.</summary>
    public event Action? SceneColorViewChanged
    {
        add => _capture.ViewChanged += value;
        remove => _capture.ViewChanged -= value;
    }

    public float AspectRatio => _ctx.Width / (float)_ctx.Height;

    public void Resize(uint width, uint height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _ctx.Width && height == _ctx.Height) return;
        _ctx.Resize(width, height);
        // Features re-declare their targets in list order, so the capture — whose ViewChanged
        // subscribers rebind materials — sees a consistent scene before it fires.
        Pipeline.Resize(width, height);
    }

    /// <summary>Register a game-supplied shader program for use by materials. The program is
    /// typically an extension shader that <c>#include</c>s <c>Common/pbrCore.slang</c>, compiled
    /// by the game's build (the NuGet ships the sources and the Slang targets) and loaded via
    /// <see cref="ShaderProgramLoader"/> from the game assembly. It must consume the standard
    /// rigid vertex stream and may declare extra group-2 bindings from slot
    /// <see cref="MaterialResourceCache.StandardMaterialEntryCount"/> up (bind them per material
    /// via the extraEntries overload of <see cref="MaterialResourceCache.AddMaterial(in GltfMaterialData, GltfImageData[], int, ReadOnlySpan{BindGroupEntryDesc})"/>).
    /// Returns a programId (&gt; 0; 0 is the built-in PBR program).
    ///
    /// The pipeline is created with the BUILT-IN layout for groups 0/1/3 so the engine's draw-ring,
    /// frame and SSAO bind groups stay compatible — WebGPU permits a pipeline layout to declare
    /// bindings the shader never uses, and slangc dead-code-eliminates unreferenced globals from
    /// the extension's reflection (e.g. jointMatrices when it has no skinned entry point).
    /// Validation is therefore a subset check, and it throws here — at registration, not at first
    /// draw, where a mismatch would only surface as an async pipeline error that silently drops
    /// draws. Custom programs are rigid-only; shadow and SSAO-prepass passes always run the
    /// built-in vertex shaders — for a BLEND material that is moot (excluded from both), but an
    /// OPAQUE custom material casts shadows and writes prepass positions from its UNDISPLACED
    /// geometry, so a vertex-displaced opaque surface will self-shadow as if flat.</summary>
    public int RegisterMaterialProgram(
        ShaderProgramDesc program,
        string vertexEntryPoint = "vertexMain",
        string fragmentEntryPoint = "fragmentMain") =>
        _programs.Register(Materials, program, vertexEntryPoint, fragmentEntryPoint);

    /// <summary>Stage one instance's joint matrices at <paramref name="offset"/> in the palette
    /// buffer. Call for every skinned instance each frame before <see cref="RenderFrame"/>, which
    /// uploads the whole staged range in one write.
    ///
    /// Matrices use the same convention as <c>DrawUniformsGpu.Mvp</c> — a System.Numerics
    /// row-vector product, applied in the shader as <c>mul(matrix, vector)</c>.</summary>
    public void SetJointPalette(int offset, ReadOnlySpan<Matrix4x4> matrices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || offset + matrices.Length > _ctx.JointCapacity)
        {
            // Loud, and once per frame rather than per instance: silently skipping would draw the
            // character folded into the origin, which looks like a broken rig rather than a full
            // palette buffer.
            if (!_jointOverflowReported)
            {
                _jointOverflowReported = true;
                LogJointPaletteOverflow(_log, offset, matrices.Length, MaxSkinnedJoints);
            }
            return;
        }
        matrices.CopyTo(_ctx.JointPalettes.AsSpan(offset));
        _ctx.JointHighWater = Math.Max(_ctx.JointHighWater, offset + matrices.Length);
    }

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

    /// <summary>Upload a skinned primitive: the 12-float mesh stream interleaved with the 8-float
    /// joints/weights stream into the 20 floats <c>vertexMainSkinned</c> reads.
    ///
    /// The two arrive separately from glTF (<c>GltfPrimitive.Vertices</c> and
    /// <c>.JointsWeights</c>) and are woven together here, ONCE, at upload. That is the whole
    /// difference from CPU skinning: the pose then costs a joint palette per frame — 65 matrices —
    /// instead of rewriting every vertex.</summary>
    public PbrPrimitive UploadSkinnedPrimitive(float[] vertices, float[] jointsWeights, uint[] indices, int materialId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vertexCount = vertices.Length / FloatsPerVertex;
        if (vertexCount * SkinFloatsPerVertex != jointsWeights.Length)
        {
            throw new ArgumentException(
                $"Skin stream holds {jointsWeights.Length} floats; {vertexCount} vertices need " +
                $"{vertexCount * SkinFloatsPerVertex}.", nameof(jointsWeights));
        }

        var interleaved = new float[vertexCount * SkinnedFloatsPerVertex];
        for (var i = 0; i < vertexCount; i++)
        {
            Array.Copy(vertices, i * FloatsPerVertex, interleaved, i * SkinnedFloatsPerVertex, FloatsPerVertex);
            Array.Copy(jointsWeights, i * SkinFloatsPerVertex,
                interleaved, i * SkinnedFloatsPerVertex + FloatsPerVertex, SkinFloatsPerVertex);
        }

        var primitive = UploadPrimitive(interleaved, indices, materialId, stride: SkinnedFloatsPerVertex);
        return primitive with { Skinned = true };
    }

    /// <summary>Upload one interleaved primitive (12 floats per vertex: pos3/normal3/uv2/tan4 —
    /// the GltfPrimitive layout). Also the entry point for procedural geometry.
    /// <paramref name="dynamic"/> makes the vertex buffer updatable via
    /// <see cref="UpdatePrimitiveVertices"/> — the CPU-skinning path re-writes it per frame.</summary>
    public PbrPrimitive UploadPrimitive(float[] vertices, uint[] indices, int materialId, bool dynamic = false, int stride = FloatsPerVertex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vbDesc = new BufferDesc("PbrVertices", 0, dynamic ? BufferUsage.Vertex | BufferUsage.CopyDst : BufferUsage.Vertex);
        var vb = _renderer.CreateBufferWithData(in vbDesc, (ReadOnlySpan<float>)vertices);
        var ibDesc = new BufferDesc("PbrIndices", 0, BufferUsage.Index);
        var ib = _renderer.CreateBufferWithData(in ibDesc, (ReadOnlySpan<uint>)indices);
        _ownedBuffers.Add(vb);
        _ownedBuffers.Add(ib);

        // Object-space AABB from position (floats 0..2 of each vertex) — feeds the directional
        // shadow frustum fit.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var v = 0; v + 2 < vertices.Length; v += stride)
        {
            var p = new Vector3(vertices[v], vertices[v + 1], vertices[v + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        if (vertices.Length < stride) { min = max = Vector3.Zero; }

        // Every primitive gets a hierarchy at upload; the tracer only ever reads the ones the
        // frame's instances reference, and a hierarchy built from the upload-time stream is what a
        // dynamic or skinned primitive traces as (its rest pose, under the instance transform).
        var traceMesh = _ctx.Trace.AddMesh(vertices, stride, indices);

        return new PbrPrimitive(
            vb, ib, (uint)indices.Length,
            (ulong)vertices.Length * sizeof(float), (ulong)indices.Length * sizeof(uint), materialId,
            min, max, TraceMesh: traceMesh);
    }

    /// <summary>Re-write a dynamic primitive's vertex stream (CPU skinning). The primitive must
    /// have been uploaded with <c>dynamic: true</c>; the float count must match the upload.
    /// NOTE: the shadow frustum fit uses the UPLOAD-time AABB — poses that swing far outside
    /// the bind-pose bounds can clip at the directional shadow edge.</summary>
    public void UpdatePrimitiveVertices(in PbrPrimitive primitive, ReadOnlySpan<float> vertices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((ulong)vertices.Length * sizeof(float) != primitive.VertexByteLength)
            throw new ArgumentException(
                $"Vertex float count {vertices.Length} does not match the uploaded primitive " +
                $"({primitive.VertexByteLength / sizeof(float)}).");
        _renderer.UpdateBuffer(primitive.VertexBuffer, 0, vertices);
    }

    /// <summary>Render one frame: partition the scene, let every feature declare its passes,
    /// compile, upload what recording staged, submit.</summary>
    public void RenderFrame(PbrScene scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var timings = new PbrCpuTimings();
        Lap();
        var view = scene.Camera.View;
        var viewProjection = PbrMath.ViewProjection(scene.Camera.View, scene.Camera.Projection);

        // Partition + sort. View-space depth of the instance origin orders blended draws
        // back-to-front (larger distance first). Opaque stays in submission order (depth
        // buffer resolves it) and doubles as the shadow-caster set.
        var opaque = _ctx.Opaque;
        var blend = _ctx.Blend;
        opaque.Clear();
        blend.Clear();
        foreach (var instance in scene.Instances)
        {
            var world = instance.Model.Translation;
            var viewPos = Vector3.Transform(world, view);
            foreach (var primitive in instance.Mesh.Primitives)
            {
                if (Materials.IsBlend(primitive.MaterialId)) blend.Add((instance, primitive, viewPos.Z));
                else opaque.Add((instance, primitive, viewPos.Z));
            }
        }
        // RH view space looks down −Z: more negative Z = farther. Ascending Z sort = far first.
        blend.Sort(static (a, b) => a.ViewDepth.CompareTo(b.ViewDepth));

        var totalDraws = opaque.Count + blend.Count;
        if (totalDraws > PbrContext.MaxDrawsPerFrame)
            throw new InvalidOperationException(
                $"{totalDraws} draws exceed the {PbrContext.MaxDrawsPerFrame}-slot draw ring; split the scene or grow MaxDrawsPerFrame.");

        _ctx.BeginFrame(scene, in view, in viewProjection);
        Materials.ResolveTargets();
        timings.Partition = Lap();
        // The instance hierarchy is a per-frame CPU build; only frames that trace pay for it —
        // which means asking the switches too, or a build with the tracers configured off still
        // pays for a hierarchy nothing will walk.
        var tracesThisFrame =
            (scene.RayTracedAo.Enabled && Switches.IsEnabled(PbrFeatures.RayTracedAo.Id)) ||
            (scene.Gi.Enabled && Switches.IsEnabled(PbrFeatures.GlobalIllumination.Id));
        if (tracesThisFrame) _ctx.Trace.BuildFrame(opaque, Materials);
        timings.TraceBuild = Lap();
        _graph.Reset();
        Pipeline.Setup(_graph);
        timings.Setup = Lap();

        _commandWriter.ResetWrittenCount();
        var stream = _graph.Compile(_commandWriter);
        _ctx.BindGroups.EndFrame();
        timings.Compile = Lap();

        // Recording staged the draw uniforms; upload them now, before the stream that reads them.
        _shadows.UploadStagedDraws();
        if (_ctx.DrawIndex > 0)
            _renderer.UpdateBuffer<byte>(_ctx.DrawUniformRing, 0, _ctx.DrawStaging.AsSpan(0, _ctx.DrawIndex * (int)_ctx.DrawStride));
        // One write for every skinned instance staged this frame — the payload GPU skinning trades
        // for the whole vertex stream. Reset so a frame that skins nothing uploads nothing.
        if (_ctx.JointHighWater > 0)
        {
            _renderer.UpdateBuffer<Matrix4x4>(_ctx.JointBuffer, 0, _ctx.JointPalettes.AsSpan(0, _ctx.JointHighWater));
            _ctx.JointHighWater = 0;
        }

        timings.Upload = Lap();
        _renderer.Submit(in stream);
        timings.Submit = Lap();
        LastCpuTimings = timings;
    }

    /// <summary>Milliseconds since the previous lap; 0 in a build without profiling.</summary>
#if PARADISE_PROFILING
    private double Lap()
    {
        var ms = _clock.Elapsed.TotalMilliseconds;
        _clock.Restart();
        return ms;
    }
#else
    private static double Lap() => 0;
#endif

    internal int PipelineVariantCountForTest => _programs.PipelineCount;
    // Culling is invisible in the submitted stream — a pass that was declared and dropped and
    // a pass that was never declared look identical. The baseline asserts on this so that
    // "the frame is unchanged" cannot be satisfied by culling quietly doing nothing.
    internal int CulledPassCountForTest => _graph.CulledPassCount;
    internal int SkinnedPipelineVariantCountForTest => _programs.SkinnedPipelineCount;
    internal int CustomProgramCountForTest => _programs.CustomProgramCount;
    internal bool UsesSrgbEntryPointForTest => _composite.UsesSrgbEntryPoint;
    internal bool CaptureFrameLightsForTest
    {
        get => _scene.CaptureFrameLightsForTest;
        set => _scene.CaptureFrameLightsForTest = value;
    }
    internal Vector4 GetLightShadowAtlasForTest(int lightIndex) => _scene.GetLightShadowAtlasForTest(lightIndex);
    internal Vector4 GetLightSizeParamsForTest(int lightIndex) => _scene.GetLightSizeParamsForTest(lightIndex);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Materials.Dispose();
        Pipeline.Dispose();
        _programs.Dispose();
        foreach (var buffer in _ownedBuffers) _renderer.DestroyBuffer(buffer);
        _ctx.Dispose();
    }

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Error,
        Message = "Joint palette overflow: {Offset}+{Count} exceeds MaxSkinnedJoints ({Max}). Skinned meshes past this point render in bind pose.")]
    private static partial void LogJointPaletteOverflow(ILogger logger, int offset, int count, int max);
}
