using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.Rendering.Pbr;

namespace Paradise.Rendering.Sample;

/// <summary>A procedural skinned character: a blocky mannequin of cube parts bound to an
/// eleven-joint hierarchy (hips, chest, head, two arms, two legs), with the vertices at each
/// elbow and knee weighted half to either joint so the joints bend rather than break. It walks
/// in place from a sine-driven cycle, so the sample has a moving rig without needing a rigged
/// asset — which the repository does not ship — or the animation pipeline.</summary>
internal sealed class SkinnedMannequin
{
    private const int FloatsPerVertex = 12;
    private const int SkinFloatsPerVertex = 8;

    private enum Joint
    {
        Hips, Chest, Head,
        LeftUpperArm, LeftForearm, RightUpperArm, RightForearm,
        LeftThigh, LeftShin, RightThigh, RightShin,
    }

    public const int JointCount = 11;

    // Bind-pose joint positions (metres, Y up, the character faces +Z) and parents.
    private static readonly Vector3[] s_bind =
    [
        new(0f, 1.0f, 0f), new(0f, 1.35f, 0f), new(0f, 1.75f, 0f),
        new(0.3f, 1.55f, 0f), new(0.3f, 1.25f, 0f), new(-0.3f, 1.55f, 0f), new(-0.3f, 1.25f, 0f),
        new(0.12f, 1.0f, 0f), new(0.12f, 0.5f, 0f), new(-0.12f, 1.0f, 0f), new(-0.12f, 0.5f, 0f),
    ];

    private static readonly int[] s_parent = [-1, 0, 1, 1, 3, 1, 5, 0, 7, 0, 9];

    private readonly Matrix4x4[] _world = new Matrix4x4[JointCount];

    public float[] Vertices { get; }
    public float[] JointsWeights { get; }
    public uint[] Indices { get; }

    public SkinnedMannequin()
    {
        var (cube, cubeIndices) = Procedural.UnitCube();
        var vertices = new List<float>();
        var skin = new List<float>();
        var indices = new List<uint>();

        // A part is a cube spanning from `top` down to `bottom`, bound to `joint`; vertices near
        // `bottom` split their weight with `blendJoint` (the joint below) when one is given.
        void Part(Vector3 top, Vector3 bottom, float width, float depth, Joint joint, Joint? blendJoint = null, float blendSpan = 0.12f)
        {
            var baseIndex = (uint)(vertices.Count / FloatsPerVertex);
            var centre = (top + bottom) * 0.5f;
            var height = top.Y - bottom.Y;
            for (var v = 0; v < cube.Length; v += FloatsPerVertex)
            {
                var p = new Vector3(cube[v] * width + centre.X, cube[v + 1] * height + centre.Y, cube[v + 2] * depth + centre.Z);
                vertices.Add(p.X); vertices.Add(p.Y); vertices.Add(p.Z);
                for (var k = 3; k < FloatsPerVertex; k++) vertices.Add(cube[v + k]);

                var childWeight = 0f;
                if (blendJoint is { } child)
                    childWeight = 0.5f * MathF.Max(0f, 1f - (p.Y - bottom.Y) / blendSpan);
                skin.Add((int)joint); skin.Add(blendJoint is { } c ? (int)c : 0); skin.Add(0); skin.Add(0);
                skin.Add(1f - childWeight); skin.Add(childWeight); skin.Add(0); skin.Add(0);
            }
            foreach (var i in cubeIndices) indices.Add(baseIndex + i);
        }

        Vector3 B(Joint j) => s_bind[(int)j];
        Part(new(0f, 1.6f, 0f), B(Joint.Hips) - new Vector3(0f, 0.1f, 0f), 0.44f, 0.24f, Joint.Chest, Joint.Hips, 0.4f);
        Part(new(0f, 2.05f, 0f), B(Joint.Head) - new Vector3(0f, 0.04f, 0f), 0.24f, 0.24f, Joint.Head);
        Part(B(Joint.LeftUpperArm) + new Vector3(0.02f, 0.05f, 0f), B(Joint.LeftForearm), 0.12f, 0.12f, Joint.LeftUpperArm, Joint.LeftForearm);
        Part(B(Joint.LeftForearm), B(Joint.LeftForearm) - new Vector3(0f, 0.32f, 0f), 0.11f, 0.11f, Joint.LeftForearm);
        Part(B(Joint.RightUpperArm) + new Vector3(-0.02f, 0.05f, 0f), B(Joint.RightForearm), 0.12f, 0.12f, Joint.RightUpperArm, Joint.RightForearm);
        Part(B(Joint.RightForearm), B(Joint.RightForearm) - new Vector3(0f, 0.32f, 0f), 0.11f, 0.11f, Joint.RightForearm);
        Part(B(Joint.LeftThigh), B(Joint.LeftShin), 0.16f, 0.16f, Joint.LeftThigh, Joint.LeftShin);
        Part(B(Joint.LeftShin), new(0.12f, 0f, 0f), 0.14f, 0.14f, Joint.LeftShin);
        Part(B(Joint.RightThigh), B(Joint.RightShin), 0.16f, 0.16f, Joint.RightThigh, Joint.RightShin);
        Part(B(Joint.RightShin), new(-0.12f, 0f, 0f), 0.14f, 0.14f, Joint.RightShin);

        Vertices = [.. vertices];
        JointsWeights = [.. skin];
        Indices = [.. indices];
        if (JointsWeights.Length != Vertices.Length / FloatsPerVertex * SkinFloatsPerVertex)
            throw new InvalidOperationException("Skin stream does not match the vertex stream.");
    }

    /// <summary>Writes the joint palette for the walk cycle at <paramref name="time"/> seconds,
    /// posed under <paramref name="root"/>: each entry is inverse-bind × the joint's world
    /// transform, in the renderer's row-vector convention.</summary>
    public void Pose(float time, in Matrix4x4 root, Span<Matrix4x4> palette)
    {
        if (palette.Length < JointCount) throw new ArgumentException($"Need {JointCount} palette slots.", nameof(palette));
        var phase = time * 5.5f;
        var swing = MathF.Sin(phase);
        var legSwing = 0.55f * swing;
        var armSwing = 0.45f * swing;
        // A shin only bends backward, on the leg that is swinging forward; likewise the elbow.
        var leftShin = MathF.Max(0f, -0.9f * MathF.Sin(phase - 0.6f));
        var rightShin = MathF.Max(0f, 0.9f * MathF.Sin(phase - 0.6f));

        Span<Quaternion> local = stackalloc Quaternion[JointCount];
        local[(int)Joint.Hips] = Quaternion.CreateFromYawPitchRoll(0.08f * swing, 0f, 0.04f * swing);
        local[(int)Joint.Chest] = Quaternion.CreateFromYawPitchRoll(-0.12f * swing, 0.03f, 0f);
        local[(int)Joint.Head] = Quaternion.CreateFromYawPitchRoll(0.06f * swing, 0.05f * MathF.Sin(phase * 2f), 0f);
        local[(int)Joint.LeftUpperArm] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -armSwing);
        local[(int)Joint.LeftForearm] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f - MathF.Max(0f, -0.5f * swing));
        local[(int)Joint.RightUpperArm] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, armSwing);
        local[(int)Joint.RightForearm] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f - MathF.Max(0f, 0.5f * swing));
        local[(int)Joint.LeftThigh] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, legSwing);
        local[(int)Joint.LeftShin] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, leftShin);
        local[(int)Joint.RightThigh] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -legSwing);
        local[(int)Joint.RightShin] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rightShin);

        var bob = Matrix4x4.CreateTranslation(0f, 0.04f * MathF.Abs(MathF.Sin(phase)), 0f) * root;
        for (var j = 0; j < JointCount; j++)
        {
            var rotation = Matrix4x4.CreateFromQuaternion(local[j]);
            var parent = s_parent[j];
            // Row-vector order: rotate about the joint, then carry it to its parent's frame.
            _world[j] = parent < 0
                ? rotation * Matrix4x4.CreateTranslation(s_bind[j]) * bob
                : rotation * Matrix4x4.CreateTranslation(s_bind[j] - s_bind[parent]) * _world[parent];
            palette[j] = Matrix4x4.CreateTranslation(-s_bind[j]) * _world[j];
        }
    }
}
