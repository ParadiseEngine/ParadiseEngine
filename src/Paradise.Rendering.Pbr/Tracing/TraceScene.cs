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

/// <summary>Merges primitive BVHs and geometry into buffers used by the compute tracer.</summary>
/// <remarks>WebGPU lacks bindless buffers, so upload rebases nodes, triangles and vertices to
/// absolute merged indices. GI and AO share these meshes; differing participating sets append
/// separate instance hierarchies whose leaf order indexes their own instance table.</remarks>
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
    private readonly Hierarchy _visible;
    private readonly Hierarchy _gi;
    private readonly List<Aabb> _instanceBounds = [];
    private TraceMaterialGpu[] _materials = [];
    private bool _geometryDirty = true;
    private bool _separateGi;

    private sealed class Hierarchy(IRenderer renderer, string name)
    {
        public readonly List<InstanceSource> Sources = [];
        public readonly List<InstanceSource> PreviousSources = [];
        public readonly List<TraceInstanceGpu> Instances = [];
        public BvhNode[] Nodes = [];
        public Aabb Bounds = Aabb.Empty;
        public uint Root;
        public BufferHandle InstanceBuffer;
        public int InstanceCapacity;
        public readonly BufferHandle UniformBuffer = renderer.CreateBuffer(new BufferDesc(
            name, (ulong)Unsafe.SizeOf<TraceUniformsGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
    }

    // Copy values: scene instances and primitive arrays may be edited in place by the host.
    private readonly record struct InstanceSource(Matrix4x4 Model, int TraceMesh, int MaterialId);

    private BufferHandle _nodeBuffer;
    private BufferHandle _triangleBuffer;
    private BufferHandle _vertexBuffer;
    private BufferHandle _materialBuffer;
    private int _nodeCapacity;
    private int _triangleCapacity;
    private int _vertexCapacity;
    private int _materialCapacity;

    public TraceScene(IRenderer renderer, ILogger log)
    {
        _renderer = renderer;
        _log = log;
        _visible = new Hierarchy(renderer, "PbrTraceUniforms");
        _gi = new Hierarchy(renderer, "PbrGiTraceUniforms");
    }

    /// <summary>Meshes uploaded so far; a mesh id is an index into this count.</summary>
    public int MeshCount => _meshes.Count;

    /// <summary>Instances in the current frame's hierarchy. Zero means every ray misses.</summary>
    public int InstanceCount => _visible.Instances.Count;

    public int GiInstanceCount => GiHierarchy.Instances.Count;
    public uint TlasRoot => _visible.Root;

    private Hierarchy GiHierarchy => _separateGi ? _gi : _visible;

    /// <summary>World bounds of GI geometry; empty when there are no participating instances.</summary>
    public Aabb SceneBounds => GiHierarchy.Bounds;

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
    public void BuildFrame(List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> opaque,
        MaterialResourceCache materials, PbrScene? scene = null, bool rayTracedAo = true, bool globalIllumination = true)
    {
        _visible.Sources.Clear();
        if (rayTracedAo)
        {
            foreach (var (instance, primitive, _) in opaque)
                AddInstance(_visible, instance, primitive);
        }

        _gi.Sources.Clear();
        if (scene is null || !globalIllumination)
            _gi.Sources.AddRange(_visible.Sources);
        else
        {
            if (scene.GiGeometry.IncludeSceneInstances)
                GatherGi(scene.Instances, materials);
            GatherGi(scene.GiGeometry.Instances, materials);
        }

        var wasSeparate = _separateGi;
        _separateGi = !CollectionsMarshal.AsSpan(_gi.Sources)
            .SequenceEqual(CollectionsMarshal.AsSpan(_visible.Sources));
        var visibleChanged = _geometryDirty || SourcesChanged(_visible);
        if (visibleChanged) BuildHierarchy(_visible, (uint)_meshNodes.Count);
        // A changed visible hierarchy can move the GI hierarchy's node range in the shared buffer.
        var giChanged = _separateGi && (_geometryDirty || visibleChanged || !wasSeparate || SourcesChanged(_gi));
        if (giChanged) BuildHierarchy(_gi, (uint)(_meshNodes.Count + _visible.Nodes.Length));

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

        Upload(materials.MaterialCount, visibleChanged, giChanged);
    }

    private static bool SourcesChanged(Hierarchy hierarchy) => !CollectionsMarshal.AsSpan(hierarchy.Sources)
        .SequenceEqual(CollectionsMarshal.AsSpan(hierarchy.PreviousSources));

    private void GatherGi(List<PbrInstance> instances, MaterialResourceCache materials)
    {
        foreach (var instance in instances)
        {
            foreach (var primitive in (instance.GiMesh ?? instance.Mesh).Primitives)
            {
                if (!materials.IsBlend(primitive.MaterialId))
                    AddInstance(_gi, instance, primitive);
            }
        }
    }

    private void AddInstance(Hierarchy hierarchy, PbrInstance instance, PbrPrimitive primitive)
    {
        if (primitive.TraceMesh < 0 || instance.GiMode != PbrGiMode.Static) return;
        // Empty bounds transform into NaN, while a singular model cannot take a ray into object space.
        if (_meshes[primitive.TraceMesh].Bounds.IsEmpty || !Matrix4x4.Invert(instance.Model, out _)) return;
        hierarchy.Sources.Add(new InstanceSource(instance.Model, primitive.TraceMesh, primitive.MaterialId));
    }

    private void BuildHierarchy(Hierarchy hierarchy, uint nodeBase)
    {
        _instanceBounds.Clear();
        foreach (var source in hierarchy.Sources)
            _instanceBounds.Add(Aabb.Transform(_meshes[source.TraceMesh].Bounds, source.Model));
        var tlas = BvhBuilder.Build(CollectionsMarshal.AsSpan(_instanceBounds), maxLeafItems: 1);
        if (tlas.RequiredStackDepth > BvhTraversal.StackDepth)
            throw new InvalidOperationException(
                $"Instance hierarchy over {hierarchy.Sources.Count} instances needs a traversal stack of {tlas.RequiredStackDepth}, above the shader's {BvhTraversal.StackDepth}.");
        hierarchy.Bounds = tlas.Bounds;
        hierarchy.Nodes = tlas.Nodes;
        for (var i = 0; i < hierarchy.Nodes.Length; i++) hierarchy.Nodes[i].ChildBase += nodeBase;
        hierarchy.Root = nodeBase;

        hierarchy.Instances.Clear();
        foreach (var slot in tlas.ItemOrder)
        {
            var (model, traceMesh, materialId) = hierarchy.Sources[slot];
            Matrix4x4.Invert(model, out var worldToObject); // singular models were skipped above
            hierarchy.Instances.Add(new TraceInstanceGpu
            {
                WorldToObject = worldToObject,
                NormalMatrix = PbrMath.NormalMatrix(model),
                RootNode = _meshes[traceMesh].RootNode,
                Material = (uint)materialId,
            });
        }
        hierarchy.PreviousSources.Clear();
        hierarchy.PreviousSources.AddRange(hierarchy.Sources);
    }

    private void Upload(int materialCount, bool visibleChanged, bool giChanged)
    {
        var totalNodes = _meshNodes.Count + _visible.Nodes.Length + (_separateGi ? _gi.Nodes.Length : 0);
        var nodesGrew = EnsureBuffer(ref _nodeBuffer, ref _nodeCapacity, totalNodes, NodeSize, "PbrTraceNodes");
        if (nodesGrew || _geometryDirty)
        {
            if (_meshNodes.Count > 0)
                _renderer.UpdateBuffer<BvhNode>(_nodeBuffer, 0, CollectionsMarshal.AsSpan(_meshNodes));
        }
        if (nodesGrew || visibleChanged)
            _renderer.UpdateBuffer<BvhNode>(_nodeBuffer, (ulong)_visible.Root * NodeSize, _visible.Nodes);
        if (_separateGi && (nodesGrew || giChanged))
            _renderer.UpdateBuffer<BvhNode>(_nodeBuffer, (ulong)_gi.Root * NodeSize, _gi.Nodes);

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

        UploadHierarchy(_visible, visibleChanged, "PbrTraceInstances");
        if (_separateGi) UploadHierarchy(_gi, giChanged, "PbrGiTraceInstances");

        EnsureBuffer(ref _materialBuffer, ref _materialCapacity, materialCount, MaterialSize, "PbrTraceMaterials");
        if (materialCount > 0)
            _renderer.UpdateBuffer<TraceMaterialGpu>(_materialBuffer, 0, _materials.AsSpan(0, materialCount));
    }

    private void UploadHierarchy(Hierarchy hierarchy, bool changed, string name)
    {
        EnsureBuffer(ref hierarchy.InstanceBuffer, ref hierarchy.InstanceCapacity, hierarchy.Instances.Count, InstanceSize, name);
        if (changed && hierarchy.Instances.Count > 0)
            _renderer.UpdateBuffer<TraceInstanceGpu>(hierarchy.InstanceBuffer, 0, CollectionsMarshal.AsSpan(hierarchy.Instances));
        if (changed)
        {
            var uniforms = new TraceUniformsGpu { TlasRoot = hierarchy.Root, InstanceCount = (uint)hierarchy.Instances.Count };
            _renderer.UpdateBuffer<TraceUniformsGpu>(hierarchy.UniformBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1));
        }
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
    public GraphBinding[] Bindings(bool globalIllumination = false)
    {
        var hierarchy = globalIllumination ? GiHierarchy : _visible;
        return
        [
            GraphBinding.Buffer(0, _nodeBuffer, 0, (ulong)_nodeCapacity * NodeSize),
            GraphBinding.Buffer(1, _triangleBuffer, 0, (ulong)_triangleCapacity * 16),
            GraphBinding.Buffer(2, _vertexBuffer, 0, (ulong)_vertexCapacity * 16),
            GraphBinding.Buffer(3, hierarchy.InstanceBuffer, 0, (ulong)hierarchy.InstanceCapacity * (ulong)InstanceSize),
            GraphBinding.Buffer(4, _materialBuffer, 0, (ulong)_materialCapacity * (ulong)MaterialSize),
            GraphBinding.Buffer(5, hierarchy.UniformBuffer, 0, (ulong)Unsafe.SizeOf<TraceUniformsGpu>()),
        ];
    }

    [LoggerMessage(EventId = 95, Level = LogLevel.Warning,
        Message = "A {Triangles}-triangle mesh builds a hierarchy of height {Height} that needs a traversal stack of {Needed} (the tracer holds {Available}); it renders but neither occludes nor bounces light.")]
    private static partial void LogUntraceableMesh(ILogger logger, int triangles, int height, int needed, int available);

    public void Dispose()
    {
        if (_nodeBuffer.IsValid) _renderer.DestroyBuffer(_nodeBuffer);
        if (_triangleBuffer.IsValid) _renderer.DestroyBuffer(_triangleBuffer);
        if (_vertexBuffer.IsValid) _renderer.DestroyBuffer(_vertexBuffer);
        if (_visible.InstanceBuffer.IsValid) _renderer.DestroyBuffer(_visible.InstanceBuffer);
        if (_gi.InstanceBuffer.IsValid) _renderer.DestroyBuffer(_gi.InstanceBuffer);
        if (_materialBuffer.IsValid) _renderer.DestroyBuffer(_materialBuffer);
        _renderer.DestroyBuffer(_visible.UniformBuffer);
        _renderer.DestroyBuffer(_gi.UniformBuffer);
    }
}
