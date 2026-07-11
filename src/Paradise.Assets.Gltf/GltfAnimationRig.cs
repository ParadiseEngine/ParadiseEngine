using System;
using System.Numerics;

namespace Paradise.Assets.Gltf;

/// <summary>Samples animation clips over a <see cref="GltfAsset"/>'s node hierarchy and
/// produces joint palettes + CPU-skinned vertices. Pure math — no GPU types — mirroring
/// bank-heist's AnimatedGltfModel: palette[i] = inverseBind[i] × jointWorld[i] ×
/// inverse(meshWorld) in the row-vector convention, 4-weight blend with a bind-pose fallback
/// for zero-weight vertices. Allocate once per asset and reuse; all per-frame state lives in
/// caller-provided spans.</summary>
public sealed class GltfAnimationRig
{
    private readonly GltfAsset _asset;
    private readonly Matrix4x4[] _localPose;
    private readonly Matrix4x4[] _worldPose;
    private readonly bool[] _worldValid;

    public GltfAnimationRig(GltfAsset asset)
    {
        _asset = asset;
        _localPose = new Matrix4x4[asset.Nodes.Length];
        _worldPose = new Matrix4x4[asset.Nodes.Length];
        _worldValid = new bool[asset.Nodes.Length];
    }

    public GltfAnimationData? FindAnimation(string name)
    {
        foreach (var animation in _asset.Animations)
        {
            if (string.Equals(animation.Name, name, StringComparison.OrdinalIgnoreCase))
                return animation;
        }
        return null;
    }

    /// <summary>Evaluate the node pose at <paramref name="time"/> seconds into
    /// <paramref name="clip"/> (null = rest pose) and cache world matrices for palette
    /// queries. Unanimated channels keep their rest TRS (glTF semantics). Time is NOT
    /// wrapped here — loop/clamp policy belongs to the caller.</summary>
    public void EvaluatePose(GltfAnimationData? clip, float time)
    {
        var nodes = _asset.Nodes;
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            _localPose[i] = Compose(node.RestScale, node.RestRotation, node.RestTranslation);
            _worldValid[i] = false;
        }

        if (clip is not null)
        {
            // Channels override per-path components; nodes with several channels compose all.
            // Re-decompose is avoided by sampling into TRS slots first.
            var sampled = new (Vector3? T, Quaternion? R, Vector3? S)[nodes.Length];
            foreach (var channel in clip.Channels)
            {
                var index = channel.NodeIndex;
                switch (channel.Path)
                {
                    case GltfAnimationPath.Translation:
                        sampled[index].T = SampleVector3(channel, time);
                        break;
                    case GltfAnimationPath.Rotation:
                        sampled[index].R = SampleQuaternion(channel, time);
                        break;
                    case GltfAnimationPath.Scale:
                        sampled[index].S = SampleVector3(channel, time);
                        break;
                }
            }
            for (var i = 0; i < nodes.Length; i++)
            {
                var (t, r, sc) = sampled[i];
                if (t is null && r is null && sc is null) continue;
                _localPose[i] = Compose(
                    sc ?? nodes[i].RestScale,
                    r ?? nodes[i].RestRotation,
                    t ?? nodes[i].RestTranslation);
            }
        }
    }

    /// <summary>Joint palette for one skinned mesh instance from the pose evaluated by
    /// <see cref="EvaluatePose"/>. <paramref name="palette"/> must hold at least the skin's
    /// joint count.</summary>
    public void ComputeJointPalette(int skinIndex, int meshNodeIndex, Span<Matrix4x4> palette)
    {
        var skin = _asset.Skins[skinIndex];
        var meshWorld = meshNodeIndex >= 0 ? WorldOf(meshNodeIndex) : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(meshWorld, out var inverseMeshWorld))
            inverseMeshWorld = Matrix4x4.Identity;
        for (var i = 0; i < skin.JointNodes.Length; i++)
        {
            // Row-vector convention: v × invBind × jointWorld × invMeshWorld.
            palette[i] = skin.InverseBindMatrices[i] * WorldOf(skin.JointNodes[i]) * inverseMeshWorld;
        }
    }

    /// <summary>CPU-skin a primitive: blends positions (point transform) and normals/tangents
    /// (3×3) by the 4 joint weights into <paramref name="output"/>, which uses the SAME
    /// interleaved layout as <see cref="GltfPrimitive.Vertices"/> (uv passes through).
    /// Zero-weight vertices keep their bind-pose data (bank-heist's fallback).</summary>
    public static void SkinVertices(GltfPrimitive primitive, ReadOnlySpan<Matrix4x4> palette, float[] output)
    {
        var bind = primitive.Vertices;
        var skin = primitive.JointsWeights
            ?? throw new InvalidOperationException("Primitive has no joints/weights stream.");
        if (output.Length < bind.Length)
            throw new ArgumentException($"Output holds {output.Length} floats; need {bind.Length}.");

        var count = primitive.VertexCount;
        for (var i = 0; i < count; i++)
        {
            var v = i * GltfPrimitive.FloatsPerVertex;
            var w = i * GltfPrimitive.SkinFloatsPerVertex;
            var w0 = skin[w + 4];
            var w1 = skin[w + 5];
            var w2 = skin[w + 6];
            var w3 = skin[w + 7];
            var weightSum = w0 + w1 + w2 + w3;
            if (weightSum <= 0.0001f)
            {
                Array.Copy(bind, v, output, v, GltfPrimitive.FloatsPerVertex);
                continue;
            }

            var m0 = palette[(int)skin[w + 0]];
            var m1 = palette[(int)skin[w + 1]];
            var m2 = palette[(int)skin[w + 2]];
            var m3 = palette[(int)skin[w + 3]];

            var p = new Vector3(bind[v + 0], bind[v + 1], bind[v + 2]);
            var n = new Vector3(bind[v + 3], bind[v + 4], bind[v + 5]);
            var t = new Vector3(bind[v + 8], bind[v + 9], bind[v + 10]);

            var position = Vector3.Transform(p, m0) * w0 + Vector3.Transform(p, m1) * w1
                + Vector3.Transform(p, m2) * w2 + Vector3.Transform(p, m3) * w3;
            var normal = Vector3.TransformNormal(n, m0) * w0 + Vector3.TransformNormal(n, m1) * w1
                + Vector3.TransformNormal(n, m2) * w2 + Vector3.TransformNormal(n, m3) * w3;
            var tangent = Vector3.TransformNormal(t, m0) * w0 + Vector3.TransformNormal(t, m1) * w1
                + Vector3.TransformNormal(t, m2) * w2 + Vector3.TransformNormal(t, m3) * w3;

            position /= weightSum;
            normal = Vector3.Normalize(normal);
            tangent = Vector3.Normalize(tangent);

            output[v + 0] = position.X;
            output[v + 1] = position.Y;
            output[v + 2] = position.Z;
            output[v + 3] = normal.X;
            output[v + 4] = normal.Y;
            output[v + 5] = normal.Z;
            output[v + 6] = bind[v + 6];
            output[v + 7] = bind[v + 7];
            output[v + 8] = tangent.X;
            output[v + 9] = tangent.Y;
            output[v + 10] = tangent.Z;
            output[v + 11] = bind[v + 11]; // tangent handedness is rotation-invariant
        }
    }

    private Matrix4x4 WorldOf(int nodeIndex)
    {
        if (_worldValid[nodeIndex]) return _worldPose[nodeIndex];
        var parent = _asset.Nodes[nodeIndex].ParentIndex;
        var world = parent >= 0 ? _localPose[nodeIndex] * WorldOf(parent) : _localPose[nodeIndex];
        _worldPose[nodeIndex] = world;
        _worldValid[nodeIndex] = true;
        return world;
    }

    private static Matrix4x4 Compose(Vector3 scale, Quaternion rotation, Vector3 translation) =>
        Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateFromQuaternion(rotation)
        * Matrix4x4.CreateTranslation(translation);

    private static Vector3 SampleVector3(GltfAnimationChannelData channel, float time)
    {
        var (index, t) = LocateKey(channel.Times, time, channel.Step);
        var a = new Vector3(channel.Values[index * 3], channel.Values[index * 3 + 1], channel.Values[index * 3 + 2]);
        if (t <= 0f) return a;
        var b = new Vector3(channel.Values[(index + 1) * 3], channel.Values[(index + 1) * 3 + 1], channel.Values[(index + 1) * 3 + 2]);
        return Vector3.Lerp(a, b, t);
    }

    private static Quaternion SampleQuaternion(GltfAnimationChannelData channel, float time)
    {
        var (index, t) = LocateKey(channel.Times, time, channel.Step);
        var a = new Quaternion(
            channel.Values[index * 4], channel.Values[index * 4 + 1],
            channel.Values[index * 4 + 2], channel.Values[index * 4 + 3]);
        if (t <= 0f) return Quaternion.Normalize(a);
        var b = new Quaternion(
            channel.Values[(index + 1) * 4], channel.Values[(index + 1) * 4 + 1],
            channel.Values[(index + 1) * 4 + 2], channel.Values[(index + 1) * 4 + 3]);
        return Quaternion.Normalize(Quaternion.Slerp(a, b, t));
    }

    /// <summary>Binary-search the key interval; returns (index, blend t∈[0,1)) — t is forced
    /// to 0 for STEP channels and outside the clip range (clamp semantics).</summary>
    private static (int Index, float T) LocateKey(float[] times, float time, bool step)
    {
        if (times.Length == 0) return (0, 0f);
        if (time <= times[0]) return (0, 0f);
        if (time >= times[^1]) return (times.Length - 1, 0f);
        var hi = Array.BinarySearch(times, time);
        if (hi >= 0) return (hi, 0f);
        hi = ~hi;
        var lo = hi - 1;
        if (step) return (lo, 0f);
        var span = times[hi] - times[lo];
        return (lo, span > 1e-6f ? (time - times[lo]) / span : 0f);
    }
}
