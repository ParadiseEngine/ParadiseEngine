using System.Numerics;

using Paradise.Animation.Offline;
using Paradise.BLOB;

namespace Paradise.Animation.Test;

/// <summary>Rigs and clips the tests share: a two-joint chain, and the LCG-driven rig the parity fixtures were generated from.</summary>
internal static class TestRigs
{
    public static readonly Quaternion QuarterTurnZ = new(0f, 0f, 0.7071068f, 0.7071068f);

    /// <summary>hip at (0,1,0) with child knee turned a quarter turn about Z, plus an unparented "prop" node.</summary>
    public static NativeBlobAssetReference<SkeletonBlob> Chain()
    {
        var raw = new RawSkeleton();
        var hip = new RawJoint("hip") { Transform = new JointPose(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One) };
        hip.Children.Add(new RawJoint("knee") { Transform = new JointPose(Vector3.Zero, QuarterTurnZ, Vector3.One) });
        raw.Roots.Add(hip);
        raw.Roots.Add(new RawJoint("prop"));
        return SkeletonBuilder.Build(raw);
    }

    /// <summary>The generator behind <c>Fixtures/ozz-*.ozz</c>: the same LCG, the same call order as the C++ program that wrote them, so the raw input is bit-identical.</summary>
    public static (RawSkeleton Skeleton, Func<int, RawAnimation> Clip) Parity(int joints = 37, int keys = 12)
    {
        var lcg = new Lcg(12345u);
        var children = new List<int>[joints];
        for (var i = 0; i < joints; i++) children[i] = [];
        for (var i = 1; i < joints; i++) children[(i - 1) / 3].Add(i);

        var raw = new RawSkeleton();
        var root = new RawJoint("j0") { Transform = lcg.Pose() };
        raw.Roots.Add(root);
        Fill(root, 0, children, lcg);

        return (raw, jointCount =>
        {
            var clip = new RawAnimation { Name = "parity", Duration = 2.5f };
            for (var i = 0; i < jointCount; i++)
            {
                var track = new RawTrack();
                var count = i % 5 == 0 ? 0 : i % 7 == 0 ? 1 : keys;
                for (var k = 0; k < count; k++)
                {
                    var t = count == 1 ? 1.0f : clip.Duration * k / (keys - 1);
                    var smooth = i % 3 == 1;
                    track.Translations.Add(new TranslationKey(t, smooth ? new Vector3(t * .1f, 0f, 0f) : new Vector3(lcg.Unit(), lcg.Unit(), lcg.Unit())));
                    track.Rotations.Add(new RotationKey(t, smooth ? Quaternion.Identity : new Quaternion(lcg.Unit(), lcg.Unit(), lcg.Unit(), lcg.Unit())));
                    track.Scales.Add(new ScaleKey(t, new Vector3(1f + lcg.Unit() * .1f, 1f + lcg.Unit() * .1f, 1f + lcg.Unit() * .1f)));
                }

                clip.Tracks.Add(track);
            }

            return clip;
        });
    }

    private static void Fill(RawJoint joint, int index, List<int>[] children, Lcg lcg)
    {
        foreach (var c in children[index])
        {
            var child = new RawJoint($"j{c}") { Transform = lcg.Pose() };
            joint.Children.Add(child);
            Fill(child, c, children, lcg);
        }
    }

    private sealed class Lcg(uint seed)
    {
        private uint _state = seed;

        public float Unit()
        {
            _state = _state * 1664525u + 1013904223u;
            return (int)(_state % 20001u) / 10000.0f - 1.0f;
        }

        public JointPose Pose() => new(new Vector3(Unit(), Unit(), Unit()), new Quaternion(Unit(), Unit(), Unit(), Unit()), new Vector3(1f + Unit() * .1f, 1f + Unit() * .1f, 1f + Unit() * .1f));
    }

    public static byte[] Fixture(string name)
    {
        using var stream = typeof(TestRigs).Assembly.GetManifestResourceStream($"Paradise.Animation.Test.Fixtures.{name}")
            ?? throw new FileNotFoundException($"Embedded fixture '{name}' is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static float MaxAbs(Matrix4x4 m)
    {
        var max = 0f;
        foreach (var v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) max = MathF.Max(max, MathF.Abs(v));
        return max;
    }
}
