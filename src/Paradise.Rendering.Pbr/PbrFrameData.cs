using System.Numerics;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr;

internal readonly record struct FrameDraw(
    PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth, int ObjectIndex = -1)
{
    public void Deconstruct(out PbrInstance instance, out PbrPrimitive primitive, out float viewDepth)
    {
        instance = Instance;
        primitive = Primitive;
        viewDepth = ViewDepth;
    }

    public static implicit operator FrameDraw((PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth) draw) =>
        new(draw.Instance, draw.Primitive, draw.ViewDepth);
}

/// <summary>Captures frame transforms and stages direct or optionally packed draw uniforms.</summary>
internal sealed class PbrFrameData
{
    private readonly Dictionary<(PbrPrimitive Primitive, bool Skinned), int> _batches = new(new BatchComparer());
    private FrameDraw[] _sortScratch = [];
    private int[] _batchForDraw = [];
    private int[] _batchOffsets = [];

    public List<DrawUniformsGpu> Objects { get; } = [];
    public List<FrameDraw> Opaque { get; } = [];
    public List<FrameDraw> Blend { get; } = [];
    public DrawUniformsGpu[] Draws { get; private set; } = [];
    public int DrawCount => checked(Opaque.Count + Blend.Count);
    public bool Packed { get; private set; }

    public bool IsSkinned(in FrameDraw draw) =>
        draw.Primitive.Skinned && CollectionsMarshal.AsSpan(Objects)[draw.ObjectIndex].Highlight.Y >= 0;

    public void Extract(PbrScene scene, MaterialResourceCache materials, in Matrix4x4 view, in Matrix4x4 viewProjection)
    {
        Objects.Clear();
        Opaque.Clear();
        Blend.Clear();
        foreach (var instance in scene.Instances)
        {
            if (instance.Mesh.Primitives.Length == 0) continue;
            var objectIndex = Objects.Count;
            Objects.Add(new DrawUniformsGpu
            {
                Mvp = instance.Model * viewProjection,
                Model = instance.Model,
                NormalMatrix = PbrMath.NormalMatrix(instance.Model),
                Highlight = new Vector4(instance.Highlight, instance.JointOffset,
                    instance.GiMode == PbrGiMode.Disabled ? 1f : 0f, instance.ReceivesDecals ? 0f : 1f),
            });
            var depth = Vector3.Transform(instance.Model.Translation, view).Z;
            foreach (var primitive in instance.Mesh.Primitives)
            {
                var draw = new FrameDraw(instance, primitive, depth, objectIndex);
                if (materials.IsBlend(primitive.MaterialId)) Blend.Add(draw);
                else Opaque.Add(draw);
            }
        }
        // RH view space looks down -Z, so ascending depth is back-to-front.
        Blend.Sort(static (a, b) => a.ViewDepth.CompareTo(b.ViewDepth));
    }

    public void ReorderOpaque(MaterialResourceCache materials)
    {
        if (Opaque.Count < 2) return;
        if (_sortScratch.Length < Opaque.Count)
        {
            var capacity = DrawBufferCapacity.Grow(_sortScratch.Length, Opaque.Count);
            _sortScratch = new FrameDraw[capacity];
            _batchForDraw = new int[capacity];
            _batchOffsets = new int[capacity];
        }
        var start = 0;
        while (start < Opaque.Count)
        {
            if (!materials.AllowsOpaqueReordering(Opaque[start].Primitive.MaterialId))
            {
                start++;
                continue;
            }
            var end = start + 1;
            while (end < Opaque.Count && materials.AllowsOpaqueReordering(Opaque[end].Primitive.MaterialId)) end++;
            GroupSegment(start, end);
            start = end;
        }
        _batches.Clear();
    }

    private void GroupSegment(int start, int end)
    {
        _batches.Clear();
        var previous = (Primitive: (PbrPrimitive?)null, Skinned: false);
        var batch = 0;
        var reorder = false;
        for (var i = start; i < end; i++)
        {
            var draw = Opaque[i];
            var key = (draw.Primitive, Skinned: IsSkinned(draw));
            if (!ReferenceEquals(key.Primitive, previous.Primitive) || key.Skinned != previous.Skinned)
            {
                if (!_batches.TryGetValue(key, out batch))
                {
                    batch = _batches.Count;
                    _batches.Add(key, batch);
                    _batchOffsets[batch] = 0;
                }
                previous = key;
            }
            if (i > start && batch < _batchForDraw[i - 1]) reorder = true;
            _batchForDraw[i] = batch;
            _batchOffsets[batch]++;
        }
        if (!reorder) return;

        // Counting sort preserves first-appearance batch order and submission order within a batch.
        var offset = start;
        for (var b = 0; b < _batches.Count; b++)
        {
            var count = _batchOffsets[b];
            _batchOffsets[b] = offset;
            offset += count;
        }
        for (var i = start; i < end; i++) _sortScratch[_batchOffsets[_batchForDraw[i]]++] = Opaque[i];
        _sortScratch.AsSpan(start, end - start).CopyTo(CollectionsMarshal.AsSpan(Opaque)[start..end]);
        Array.Clear(_sortScratch, start, end - start);
    }

    private sealed class BatchComparer : IEqualityComparer<(PbrPrimitive Primitive, bool Skinned)>
    {
        public bool Equals((PbrPrimitive Primitive, bool Skinned) x, (PbrPrimitive Primitive, bool Skinned) y) =>
            x.Skinned == y.Skinned && x.Primitive == y.Primitive;

        // Shared buffer/material handles distinguish common batches without hashing bounds on every draw.
        // Equality still checks the entire primitive descriptor, including any colliding submeshes.
        public int GetHashCode((PbrPrimitive Primitive, bool Skinned) key) =>
            HashCode.Combine(key.Primitive.VertexBuffer, key.Primitive.IndexBuffer, key.Primitive.MaterialId, key.Skinned);
    }

    public DrawUniformsGpu GetUniforms(in FrameDraw draw)
    {
        var uniforms = CollectionsMarshal.AsSpan(Objects)[draw.ObjectIndex];
        if (!draw.Primitive.Skinned) uniforms.Highlight.Y = 0;
        return uniforms;
    }

    public void Stage(int capacity, byte[] uniformStaging, int uniformStride, bool packed)
    {
        Packed = packed;
        if (packed && Draws.Length < capacity) Draws = new DrawUniformsGpu[capacity];
        StageBucket(Opaque, 0, uniformStaging, uniformStride);
        StageBucket(Blend, Opaque.Count, uniformStaging, uniformStride);
    }

    private void StageBucket(List<FrameDraw> bucket, int firstSlot, byte[] uniformStaging, int uniformStride)
    {
        for (var i = 0; i < bucket.Count; i++)
        {
            var uniforms = GetUniforms(bucket[i]);
            var slot = firstSlot + i;
            if (Packed) Draws[slot] = uniforms;
            // Uniform binding alignment is separate from the natural storage-buffer stride.
            MemoryMarshal.Write(uniformStaging.AsSpan(slot * uniformStride), in uniforms);
        }
    }

    public ReadOnlySpan<DrawUniformsGpu> InstanceData(int capacity, byte[] uniformStaging, int uniformStride)
    {
        if (!Packed)
        {
            // The direct path pays for instance storage only when a main-pass batch uses it.
            if (Draws.Length < capacity) Draws = new DrawUniformsGpu[capacity];
            for (var i = 0; i < DrawCount; i++)
                Draws[i] = MemoryMarshal.Read<DrawUniformsGpu>(uniformStaging.AsSpan(i * uniformStride));
        }
        return Draws.AsSpan(0, DrawCount);
    }
}
