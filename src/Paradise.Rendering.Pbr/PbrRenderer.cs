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
    private readonly HashSet<PbrGeometryOwnership> _geometry = [];
    private readonly object _geometryOwner = new();
    private bool _jointOverflowReported;  // report a full palette buffer once, not per instance
    private bool _disposed;
    private bool _rendering;
#if PARADISE_PROFILING
    private readonly System.Diagnostics.Stopwatch _clock = new();
#endif

    /// <param name="renderer">The backend the frame is submitted to.</param>
    /// <param name="switches">The process-wide feature configuration, read each frame.
    /// Required so this renderer cannot silently ignore host configuration by creating a private
    /// switchboard; unconfigured hosts explicitly pass <c>new FeatureSwitches()</c>.</param>
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
        Materials = new MaterialResourceCache(renderer, _programs.BuiltIn, maxAnisotropy, _ctx.Targets)
        {
            IsFrameInProgress = () => _rendering,
        };
        _ctx.Materials = Materials;

        Pipeline = new RenderPipeline(_ctx.Width, _ctx.Height, switches);
        PbrBuiltInFeatures.AddTo(Pipeline, _ctx, specularAaVariance, specularAaClamp);
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

    /// <summary>Registers a custom rigid material shader and returns its positive program
    /// ID.</summary>
    /// <remarks>Compile game shaders against Common/pbrCore.slang and load them through
    /// ShaderProgramLoader. Extra group-2 bindings start at StandardMaterialEntryCount; groups
    /// 0/1/3 must be compatible subsets of the built-in layout. Optional instanced entry points
    /// use Common/pbrInstancing.slang and its group-0 storage binding. Validate at registration to avoid
    /// asynchronous GPU errors. Shadows and prepass use built-in vertices, so opaque vertex
    /// displacement is not reflected in those passes.</remarks>
    public int RegisterMaterialProgram(
        ShaderProgramDesc program,
        string vertexEntryPoint = "vertexMain",
        string fragmentEntryPoint = "fragmentMain") =>
        _programs.Register(Materials, program, vertexEntryPoint, fragmentEntryPoint);

    /// <summary>Registers a custom rigid material with explicit optimization guarantees and optional instanced entries.</summary>
    public int RegisterMaterialProgram(
        ShaderProgramDesc program,
        MaterialProgramOptions options,
        string vertexEntryPoint = "vertexMain",
        string fragmentEntryPoint = "fragmentMain") =>
        _programs.Register(Materials, program, vertexEntryPoint, fragmentEntryPoint, options);

    /// <summary>Releases an unused custom material program and its cached pipelines.</summary>
    public bool ReleaseMaterialProgram(int programId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rendering) throw new InvalidOperationException("Release render resources between frames.");
        return _programs.Release(Materials, programId);
    }

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
            // Report overflow once per frame; missing palettes otherwise resemble broken rigs.
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
    /// <remarks>Call between render frames; a failed upload releases everything it created.</remarks>
    public PbrMesh[] UploadMesh(GltfAsset asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rendering) throw new InvalidOperationException("Upload render resources between frames.");
        var uploadedPrimitives = new List<PbrPrimitive>();
        var uploadedMaterials = new List<int>();
        try
        {
            var materialIds = new int[asset.Materials.Length];
            for (var i = 0; i < asset.Materials.Length; i++)
            {
                materialIds[i] = Materials.AddMaterial(in asset.Materials[i], asset.Images);
                uploadedMaterials.Add(materialIds[i]);
            }
            var fallbackMaterial = -1;

            var meshes = new PbrMesh[asset.Meshes.Length];
            for (var m = 0; m < asset.Meshes.Length; m++)
            {
                var primitives = new PbrPrimitive[asset.Meshes[m].Primitives.Length];
                for (var p = 0; p < primitives.Length; p++)
                {
                    var source = asset.Meshes[m].Primitives[p];
                    if (source.MaterialIndex < 0 && fallbackMaterial < 0)
                    {
                        fallbackMaterial = Materials.AddDefaultMaterial(new Vector4(0.8f, 0.8f, 0.8f, 1f));
                        uploadedMaterials.Add(fallbackMaterial);
                    }
                    var materialId = source.MaterialIndex >= 0 ? materialIds[source.MaterialIndex] : fallbackMaterial;
                    primitives[p] = UploadPrimitive(source.Vertices, source.Indices, materialId);
                    uploadedPrimitives.Add(primitives[p]);
                }
                meshes[m] = new PbrMesh(primitives);
            }
            return meshes;
        }
        catch
        {
            foreach (var primitive in uploadedPrimitives) ReleaseGeometry(primitive.Ownership!);
            foreach (var materialId in uploadedMaterials) Materials.ReleaseMaterial(materialId);
            throw;
        }
    }

    /// <summary>Uploads a skinned primitive by interleaving 12 vertex floats with 8 joint/weight floats.</summary>
    /// <remarks><c>GltfPrimitive.Vertices</c> and <c>.JointsWeights</c> are combined once into the
    /// 20-float <c>vertexMainSkinned</c> stream. Subsequent poses upload joint palettes rather than
    /// rewriting every vertex.</remarks>
    public PbrPrimitive UploadSkinnedPrimitive(float[] vertices, float[] jointsWeights, uint[] indices, int materialId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rendering) throw new InvalidOperationException("Upload render resources between frames.");
        if (vertices.Length % FloatsPerVertex != 0)
            throw new ArgumentException("The vertex stream must contain complete vertices.", nameof(vertices));
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
    /// <remarks>Call between render frames; material variants borrow the returned geometry's lifetime.</remarks>
    public PbrPrimitive UploadPrimitive(float[] vertices, uint[] indices, int materialId, bool dynamic = false, int stride = FloatsPerVertex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rendering) throw new InvalidOperationException("Upload render resources between frames.");
        var traceMesh = _ctx.Trace.AddMesh(vertices, stride, indices);
        var vb = default(BufferHandle);
        var ib = default(BufferHandle);
        try
        {
            var vbDesc = new BufferDesc("PbrVertices", 0, dynamic ? BufferUsage.Vertex | BufferUsage.CopyDst : BufferUsage.Vertex);
            vb = _renderer.CreateBufferWithData(in vbDesc, (ReadOnlySpan<float>)vertices);
            var ibDesc = new BufferDesc("PbrIndices", 0, BufferUsage.Index);
            ib = _renderer.CreateBufferWithData(in ibDesc, (ReadOnlySpan<uint>)indices);

            // Position occupies the first three floats in either rigid or skinned streams.
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (var v = 0; v + 2 < vertices.Length; v += stride)
            {
                var p = new Vector3(vertices[v], vertices[v + 1], vertices[v + 2]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            if (vertices.Length < stride) { min = max = Vector3.Zero; }

            var ownership = new PbrGeometryOwnership(_geometryOwner, vb, ib, traceMesh);
            var primitive = new PbrPrimitive(
                vb, ib, (uint)indices.Length,
                (ulong)vertices.Length * sizeof(float), (ulong)indices.Length * sizeof(uint), materialId,
                min, max, TraceMesh: traceMesh, Dynamic: dynamic) { Ownership = ownership };
            _geometry.Add(ownership);
            return primitive;
        }
        catch
        {
            if (ib.IsValid) _renderer.DestroyBuffer(ib);
            if (vb.IsValid) _renderer.DestroyBuffer(vb);
            if (traceMesh >= 0) _ctx.Trace.RemoveMesh(traceMesh);
            throw;
        }
    }

    /// <summary>Releases an uploaded primitive's geometry and trace hierarchy once.</summary>
    /// <remarks>Material variants made with <c>with</c> borrow the same geometry ownership;
    /// release only after every mesh using it has retired, between frames. Materials have their
    /// own lifetime. A repeated release returns false; foreign geometry is rejected.</remarks>
    public bool ReleasePrimitive(PbrPrimitive primitive)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        var ownership = primitive.Ownership;
        if (ownership is null || !ReferenceEquals(ownership.Owner, _geometryOwner)
            || ownership.Vertices != primitive.VertexBuffer || ownership.Indices != primitive.IndexBuffer
            || ownership.TraceMesh != primitive.TraceMesh)
            throw new ArgumentException("The primitive does not identify geometry uploaded by this renderer.", nameof(primitive));
        if (ownership.Released) return false;
        if (_rendering) throw new InvalidOperationException("Release render resources between frames.");
        ReleaseGeometry(ownership);
        return true;
    }

    private void ReleaseGeometry(PbrGeometryOwnership ownership)
    {
        ownership.Released = true;
        _geometry.Remove(ownership);
        if (ownership.TraceMesh >= 0) _ctx.Trace.RemoveMesh(ownership.TraceMesh);
        try { _renderer.DestroyBuffer(ownership.Vertices); }
        finally { _renderer.DestroyBuffer(ownership.Indices); }
    }

    private void ValidateGeometry(PbrPrimitive primitive)
    {
        if (primitive.Ownership is not { } ownership) return;
        if (!ReferenceEquals(ownership.Owner, _geometryOwner)
            || ownership.Vertices != primitive.VertexBuffer || ownership.Indices != primitive.IndexBuffer)
            throw new ArgumentException("The primitive does not identify geometry uploaded by this renderer.", nameof(primitive));
        ObjectDisposedException.ThrowIf(ownership.Released, primitive);
    }

    private void ValidateGeometry(PbrScene scene)
    {
        foreach (var instance in scene.Instances)
        {
            foreach (var primitive in instance.Mesh.Primitives) ValidateGeometry(primitive);
            if (instance.GiMesh is { } proxy)
                foreach (var primitive in proxy.Primitives) ValidateGeometry(primitive);
        }
        foreach (var instance in scene.GiGeometry.Instances)
            foreach (var primitive in (instance.GiMesh ?? instance.Mesh).Primitives) ValidateGeometry(primitive);
    }

    /// <summary>Re-write a dynamic primitive's vertex stream (CPU skinning). The primitive must
    /// have been uploaded with <c>dynamic: true</c>; the float count must match the upload.
    /// NOTE: the shadow frustum fit uses the UPLOAD-time AABB — poses that swing far outside
    /// the bind-pose bounds can clip at the directional shadow edge.</summary>
    public void UpdatePrimitiveVertices(in PbrPrimitive primitive, ReadOnlySpan<float> vertices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateGeometry(primitive);
        if ((ulong)vertices.Length * sizeof(float) != primitive.VertexByteLength)
            throw new ArgumentException(
                $"Vertex float count {vertices.Length} does not match the uploaded primitive " +
                $"({primitive.VertexByteLength / sizeof(float)}).");
        _renderer.UpdateBuffer(primitive.VertexBuffer, 0, vertices);
    }

    /// <summary>Extract a frame, declare and compile its passes, then upload and submit.</summary>
    /// <remarks>Finish instance and joint-palette updates before calling this method or during
    /// feature PrepareFrame callbacks. Instance data is frozen before feature Setup runs.</remarks>
    public void RenderFrame(PbrScene scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rendering) throw new InvalidOperationException("A render frame is already active.");
        _rendering = true;
        try { RenderFrameCore(scene); }
        finally { _rendering = false; }
    }

    private void RenderFrameCore(PbrScene scene)
    {

        var timings = new PbrCpuTimings();
        Lap();
        // Before anything reads a switch: this fixes which features run in THIS frame and is the
        // only place a transition is announced, so the trace-hierarchy decision below and the
        // features' own setup cannot disagree about what is on.
        _ctx.BeginFrame(scene);
        Pipeline.BeginFrame();
        Pipeline.PrepareFrame();
        ValidateGeometry(scene);
        _ctx.Frame.Extract(scene, Materials, _ctx.View, _ctx.ViewProjection, Pipeline.IsEnabled(PbrFeatures.Instancing.Id));
        var opaque = _ctx.Opaque;
        _ctx.EnsureDrawCapacity(_ctx.Frame.DrawCount);

        Materials.ResolveTargets();
        timings.Partition = Lap();
        // The instance hierarchy is a per-frame CPU build; only frames that trace pay for it —
        // which means asking the switches too, or a build with the tracers configured off still
        // pays for a hierarchy nothing will walk.
        var tracesAo = scene.RayTracedAo.Enabled && Pipeline.IsEnabled(PbrFeatures.RayTracedAo.Id);
        var tracesGi = _ctx.TraceGlobalIllumination;
        if (tracesAo || tracesGi)
            _ctx.Trace.BuildFrame(opaque, Materials, scene, rayTracedAo: tracesAo, globalIllumination: tracesGi);
        timings.TraceBuild = Lap();
        // Staging remains mandatory when the preparation feature is switched off.
        if (!Pipeline.IsEnabled(PbrFeatures.DrawPreparation.Id))
            _ctx.Frame.PrepareDirect(_ctx.DrawStaging, (int)_ctx.DrawStride);
        _graph.Reset();
        Pipeline.Setup(_graph);
        timings.Setup = Lap();

        _commandWriter.ResetWrittenCount();
        var stream = _graph.Compile(_commandWriter);
        _ctx.BindGroups.EndFrame();
        timings.Compile = Lap();

        // Frame extraction and pass recording are complete; upload everything the stream reads.
        Pipeline.BeforeSubmit();
        if (_ctx.Frame.DrawCount > 0)
            _renderer.UpdateBuffer<byte>(_ctx.DrawUniformRing, 0, _ctx.DrawStaging.AsSpan(0, _ctx.Frame.DrawCount * (int)_ctx.DrawStride));
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
    internal int PackedDrawCapacityForTest => _ctx.Frame.Draws.Length;
    internal bool FramePackingEnabledForTest => _ctx.Frame.Packed;

    public void Dispose()
    {
        if (_disposed) return;
        if (_rendering) throw new InvalidOperationException("Dispose the renderer between frames.");
        _disposed = true;
        Materials.Dispose();
        Pipeline.Dispose();
        _programs.Dispose();
        foreach (var ownership in _geometry)
        {
            ownership.Released = true;
            _renderer.DestroyBuffer(ownership.Vertices);
            _renderer.DestroyBuffer(ownership.Indices);
        }
        _geometry.Clear();
        _ctx.Dispose();
    }

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Error,
        Message = "Joint palette overflow: {Offset}+{Count} exceeds MaxSkinnedJoints ({Max}). Skinned meshes past this point render in bind pose.")]
    private static partial void LogJointPaletteOverflow(ILogger logger, int offset, int count, int max);
}
