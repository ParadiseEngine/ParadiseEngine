using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Paradise.Geometry;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Mirror of bvh.slang <c>TraceInstance</c> (144 B).</summary>
[StructLayout(LayoutKind.Sequential, Size = 144)]
public struct TraceInstanceGpu
{
    public Matrix4x4 WorldToObject;
    public Matrix4x4 NormalMatrix;
    public uint RootNode;
    public uint Material;
    public uint Flags;
    private uint _pad;
}

/// <summary>Mirror of bvh.slang <c>TraceMaterial</c> (32 B).</summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct TraceMaterialGpu
{
    public Vector4 BaseColor;
    public Vector4 Emissive;
}

/// <summary>Mirror of bvh.slang <c>TraceUniforms</c> (16 B).</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct TraceUniformsGpu
{
    public uint TlasRoot;
    public uint InstanceCount;
    private uint _pad0;
    private uint _pad1;
}

/// <summary>One triangle of the merged trace geometry: three absolute vertex indices.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct TraceTriangleGpu
{
    public uint V0;
    public uint V1;
    public uint V2;
    private uint _pad;
}

/// <summary>The scene as the compute tracer sees it: every uploaded primitive's bounding volume
/// hierarchy merged into one node buffer, its triangles and vertices into one each, and per frame
/// an instance hierarchy over the opaque instances that participate.
///
/// <para>One buffer per kind, with per-mesh offsets folded in at upload, is the layout decision
/// the plan fixed on day one: WebGPU has no bindless, so a mesh cannot be a buffer of its own. A
/// mesh's nodes reference triangle slots and its triangles reference vertices by ABSOLUTE index
/// into the merged buffers, rebased once here. The instance hierarchy is appended after the mesh
/// nodes, so a frame that only moves instances rewrites its region alone, and the instance table
/// is laid out in that hierarchy's leaf order so a leaf slot indexes it directly.</para></summary>
internal sealed partial class TraceScene : IDisposable
{
    private const int NodeSize = 96;
    private static readonly int InstanceSize = Unsafe.SizeOf<TraceInstanceGpu>();
    private static readonly int MaterialSize = Unsafe.SizeOf<TraceMaterialGpu>();

    private readonly IRenderer _renderer;
    private readonly ILogger _log;
    private readonly List<BvhNode> _meshNodes = [];
    private readonly List<TraceTriangleGpu> _triangles = [];
    private readonly List<Vector4> _vertices = [];
    private readonly List<(uint RootNode, Aabb Bounds)> _meshes = [];
    private readonly List<TraceInstanceGpu> _instances = [];
    private readonly List<Aabb> _instanceBounds = [];
    private readonly List<(PbrInstance Instance, PbrPrimitive Primitive)> _instanceSources = [];
    private TraceMaterialGpu[] _materials = [];
    private BvhNode[] _tlasNodes = [];
    private bool _geometryDirty = true;

    private BufferHandle _nodeBuffer;
    private BufferHandle _triangleBuffer;
    private BufferHandle _vertexBuffer;
    private BufferHandle _instanceBuffer;
    private BufferHandle _materialBuffer;
    private readonly BufferHandle _uniformBuffer;
    private int _nodeCapacity;
    private int _triangleCapacity;
    private int _vertexCapacity;
    private int _instanceCapacity;
    private int _materialCapacity;

    public TraceScene(IRenderer renderer, ILogger log)
    {
        _renderer = renderer;
        _log = log;
        _uniformBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrTraceUniforms", (ulong)Unsafe.SizeOf<TraceUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    /// <summary>Meshes uploaded so far; a mesh id is an index into this count.</summary>
    public int MeshCount => _meshes.Count;

    /// <summary>Instances in the current frame's hierarchy. Zero means every ray misses.</summary>
    public int InstanceCount => _instances.Count;

    public uint TlasRoot { get; private set; }

    /// <summary>World bounds of the frame's participating instances; empty when there are none.</summary>
    public Aabb SceneBounds { get; private set; } = Aabb.Empty;

    /// <summary>Build a hierarchy over an interleaved vertex stream (position in floats 0..2,
    /// normal in 3..5 of each <paramref name="stride"/>-float vertex) and merge it in. Returns the
    /// mesh id a <see cref="PbrPrimitive"/> carries, or -1 for a mesh the shader walk could not
    /// hold — logged, and rendered without taking part in tracing, because a host that never
    /// traces must still be able to upload it.</summary>
    public int AddMesh(ReadOnlySpan<float> vertices, int stride, ReadOnlySpan<uint> indices)
    {
        if (stride < 6)
            throw new ArgumentException($"A traced vertex needs a position and a normal: stride {stride} holds fewer than six floats.", nameof(stride));
        if (vertices.Length % stride != 0)
            throw new ArgumentException($"{vertices.Length} floats do not divide into {stride}-float vertices.", nameof(vertices));
        var vertexCount = vertices.Length / stride;
        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        for (var v = 0; v < vertexCount; v++)
        {
            var at = v * stride;
            positions[v] = new Vector3(vertices[at], vertices[at + 1], vertices[at + 2]);
            var n = new Vector3(vertices[at + 3], vertices[at + 4], vertices[at + 5]);
            normals[v] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
        }

        var bvh = TriangleBvh.Build(positions, indices);
        // The shader walk has a fixed stack; a hierarchy it cannot hold would drop children and
        // miss occluders by direction, so the mesh is left out of tracing rather than traced wrong.
        if (bvh.RequiredStackDepth > BvhTraversal.StackDepth)
        {
            LogUntraceableMesh(_log, indices.Length / 3, bvh.Height, bvh.RequiredStackDepth, BvhTraversal.StackDepth);
            return -1;
        }
        var nodeBase = (uint)_meshNodes.Count;
        var triangleBase = (uint)_triangles.Count;
        var vertexBase = (uint)(_vertices.Count / 2);

        foreach (var source in bvh.Nodes)
        {
            var node = source;
            node.ChildBase += nodeBase;
            node.LeafBase += triangleBase;
            _meshNodes.Add(node);
        }
        // Triangles in leaf order, so a leaf's run is contiguous.
        foreach (var triangle in bvh.ItemOrder)
        {
            _triangles.Add(new TraceTriangleGpu
            {
                V0 = indices[triangle * 3] + vertexBase,
                V1 = indices[triangle * 3 + 1] + vertexBase,
                V2 = indices[triangle * 3 + 2] + vertexBase,
            });
        }
        for (var v = 0; v < vertexCount; v++)
        {
            _vertices.Add(new Vector4(positions[v], 0f));
            _vertices.Add(new Vector4(normals[v], 0f));
        }

        _meshes.Add((nodeBase, bvh.Bounds));
        _geometryDirty = true;
        return _meshes.Count - 1;
    }

    /// <summary>Gather the frame's participating instances, build the hierarchy over them, and
    /// upload whatever changed. Call before features set up; they bind through
    /// <see cref="Bindings"/>.</summary>
    public void BuildFrame(List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> opaque, MaterialResourceCache materials)
    {
        _instanceSources.Clear();
        _instanceBounds.Clear();
        foreach (var (instance, primitive, _) in opaque)
        {
            if (primitive.TraceMesh < 0 || instance.GiMode != PbrGiMode.Static) continue;
            // An empty primitive has inverted infinite bounds; transformed, those turn into NaN and
            // poison the fit. It has nothing to hit either way.
            var bounds = _meshes[primitive.TraceMesh].Bounds;
            if (bounds.IsEmpty) continue;
            // A singular model (a zero scale) cannot take a ray into object space; the instance is
            // flat or degenerate on screen too, so it is left out rather than traced at rest pose.
            if (!Matrix4x4.Invert(instance.Model, out _)) continue;
            _instanceSources.Add((instance, primitive));
            _instanceBounds.Add(Aabb.Transform(bounds, instance.Model));
        }

        var tlas = BvhBuilder.Build(CollectionsMarshal.AsSpan(_instanceBounds), maxLeafItems: 1);
        if (tlas.RequiredStackDepth > BvhTraversal.StackDepth)
            throw new InvalidOperationException(
                $"Instance hierarchy over {_instanceSources.Count} instances needs a traversal stack of {tlas.RequiredStackDepth}, above the shader's {BvhTraversal.StackDepth}.");
        SceneBounds = tlas.Bounds;
        var meshNodeCount = (uint)_meshNodes.Count;
        _tlasNodes = tlas.Nodes;
        for (var i = 0; i < _tlasNodes.Length; i++) _tlasNodes[i].ChildBase += meshNodeCount;
        TlasRoot = meshNodeCount;

        _instances.Clear();
        foreach (var slot in tlas.ItemOrder)
        {
            var (instance, primitive) = _instanceSources[slot];
            var model = instance.Model;
            Matrix4x4.Invert(model, out var worldToObject); // singular models were skipped above
            _instances.Add(new TraceInstanceGpu
            {
                WorldToObject = worldToObject,
                NormalMatrix = PbrMath.NormalMatrix(model),
                RootNode = _meshes[primitive.TraceMesh].RootNode,
                Material = (uint)primitive.MaterialId,
            });
        }

        if (_materials.Length < materials.MaterialCount) _materials = new TraceMaterialGpu[materials.MaterialCount];
        for (var m = 0; m < materials.MaterialCount; m++)
        {
            var surface = materials.GetTraceSurface(m);
            _materials[m] = new TraceMaterialGpu
            {
                BaseColor = new Vector4(surface.BaseColor.X, surface.BaseColor.Y, surface.BaseColor.Z, surface.Metallic),
                Emissive = new Vector4(surface.Emissive, 0f),
            };
        }

        Upload(materials.MaterialCount);
    }

    private void Upload(int materialCount)
    {
        var totalNodes = _meshNodes.Count + _tlasNodes.Length;
        var nodesGrew = EnsureBuffer(ref _nodeBuffer, ref _nodeCapacity, totalNodes, NodeSize, "PbrTraceNodes");
        if (nodesGrew || _geometryDirty)
        {
            if (_meshNodes.Count > 0)
                _renderer.UpdateBuffer<BvhNode>(_nodeBuffer, 0, CollectionsMarshal.AsSpan(_meshNodes));
        }
        _renderer.UpdateBuffer<BvhNode>(_nodeBuffer, (ulong)_meshNodes.Count * NodeSize, _tlasNodes);

        if (EnsureBuffer(ref _triangleBuffer, ref _triangleCapacity, _triangles.Count, 16, "PbrTraceTriangles") || _geometryDirty)
        {
            if (_triangles.Count > 0)
                _renderer.UpdateBuffer<TraceTriangleGpu>(_triangleBuffer, 0, CollectionsMarshal.AsSpan(_triangles));
        }
        if (EnsureBuffer(ref _vertexBuffer, ref _vertexCapacity, _vertices.Count, 16, "PbrTraceVertices") || _geometryDirty)
        {
            if (_vertices.Count > 0)
                _renderer.UpdateBuffer<Vector4>(_vertexBuffer, 0, CollectionsMarshal.AsSpan(_vertices));
        }
        _geometryDirty = false;

        EnsureBuffer(ref _instanceBuffer, ref _instanceCapacity, _instances.Count, InstanceSize, "PbrTraceInstances");
        if (_instances.Count > 0)
            _renderer.UpdateBuffer<TraceInstanceGpu>(_instanceBuffer, 0, CollectionsMarshal.AsSpan(_instances));

        EnsureBuffer(ref _materialBuffer, ref _materialCapacity, materialCount, MaterialSize, "PbrTraceMaterials");
        if (materialCount > 0)
            _renderer.UpdateBuffer<TraceMaterialGpu>(_materialBuffer, 0, _materials.AsSpan(0, materialCount));

        var uniforms = new TraceUniformsGpu { TlasRoot = TlasRoot, InstanceCount = (uint)_instances.Count };
        _renderer.UpdateBuffer<TraceUniformsGpu>(_uniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
    }

    /// <summary>(Re)create <paramref name="buffer"/> when <paramref name="count"/> elements exceed its
    /// capacity. Never below one element: WebGPU rejects a zero-sized storage binding, and a
    /// scene with nothing to trace still binds every buffer.</summary>
    private bool EnsureBuffer(ref BufferHandle buffer, ref int capacity, int count, int elementSize, string name)
    {
        var needed = Math.Max(count, 1);
        if (buffer.IsValid && needed <= capacity) return false;
        if (buffer.IsValid) _renderer.DestroyBuffer(buffer);
        capacity = Math.Max(needed, capacity + capacity / 2);
        buffer = _renderer.CreateBuffer(new BufferDesc(name, (ulong)capacity * (ulong)elementSize, BufferUsage.Storage | BufferUsage.CopyDst));
        return true;
    }

    /// <summary>The six bindings bvh.slang declares, in its order, for a tracing pass's trace group.
    /// Valid after <see cref="BuildFrame"/>; the sizes are the buffers' current capacities, so a
    /// grown buffer is a different group by content and the cache rebuilds it.</summary>
    public GraphBinding[] Bindings() =>
    [
        GraphBinding.Buffer(0, _nodeBuffer, 0, (ulong)_nodeCapacity * NodeSize),
        GraphBinding.Buffer(1, _triangleBuffer, 0, (ulong)_triangleCapacity * 16),
        GraphBinding.Buffer(2, _vertexBuffer, 0, (ulong)_vertexCapacity * 16),
        GraphBinding.Buffer(3, _instanceBuffer, 0, (ulong)_instanceCapacity * (ulong)InstanceSize),
        GraphBinding.Buffer(4, _materialBuffer, 0, (ulong)_materialCapacity * (ulong)MaterialSize),
        GraphBinding.Buffer(5, _uniformBuffer, 0, (ulong)Unsafe.SizeOf<TraceUniformsGpu>()),
    ];

    [LoggerMessage(EventId = 95, Level = LogLevel.Warning,
        Message = "A {Triangles}-triangle mesh builds a hierarchy of height {Height} that needs a traversal stack of {Needed} (the tracer holds {Available}); it renders but neither occludes nor bounces light.")]
    private static partial void LogUntraceableMesh(ILogger logger, int triangles, int height, int needed, int available);

    public void Dispose()
    {
        if (_nodeBuffer.IsValid) _renderer.DestroyBuffer(_nodeBuffer);
        if (_triangleBuffer.IsValid) _renderer.DestroyBuffer(_triangleBuffer);
        if (_vertexBuffer.IsValid) _renderer.DestroyBuffer(_vertexBuffer);
        if (_instanceBuffer.IsValid) _renderer.DestroyBuffer(_instanceBuffer);
        if (_materialBuffer.IsValid) _renderer.DestroyBuffer(_materialBuffer);
        _renderer.DestroyBuffer(_uniformBuffer);
    }
}
