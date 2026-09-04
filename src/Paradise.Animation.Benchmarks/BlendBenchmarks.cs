using System.Numerics;

using BenchmarkDotNet.Attributes;

using Paradise.BLOB;

namespace Paradise.Animation.Benchmarks;

/// <summary>Cross-fading two poses of one character: the four-joints-per-lane <see cref="JointPoses.Blend"/> against the per-joint scalar loop it replaced.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class BlendBenchmarks
{
    [Params(64)]
    public int Joints { get; set; }

    private JointPose[] _from = null!;
    private JointPose[] _to = null!;
    private JointPose[] _output = null!;
    private NativeBlobAssetReference<JointPoses> _fromSoa = null!;
    private NativeBlobAssetReference<JointPoses> _toSoa = null!;
    private NativeBlobAssetReference<JointPoses> _outputSoa = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(3);
        _from = new JointPose[Joints];
        _to = new JointPose[Joints];
        _output = new JointPose[Joints];
        for (var i = 0; i < Joints; i++)
        {
            _from[i] = Pose(random);
            _to[i] = Pose(random);
        }

        _fromSoa = JointPoses.Create(Joints);
        _toSoa = JointPoses.Create(Joints);
        _outputSoa = JointPoses.Create(Joints);
        _fromSoa.Value.CopyFrom(_from);
        _toSoa.Value.CopyFrom(_to);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fromSoa.Dispose();
        _toSoa.Dispose();
        _outputSoa.Dispose();
    }

    private static JointPose Pose(Random random) => new(
        new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()),
        Quaternion.Normalize(new Quaternion(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f)),
        new Vector3(0.9f + random.NextSingle() * 0.2f));

    [Benchmark(Baseline = true)]
    public void Simd() => JointPoses.Blend(ref _fromSoa.Value, ref _toSoa.Value, 0.37f, ref _outputSoa.Value);

    [Benchmark]
    public void Scalar()
    {
        for (var i = 0; i < _from.Length; i++)
        {
            var a = _from[i];
            var b = _to[i];
            var rotation = Quaternion.Dot(a.Rotation, b.Rotation) < 0f ? -b.Rotation : b.Rotation;
            _output[i] = new JointPose(
                Vector3.Lerp(a.Translation, b.Translation, 0.37f),
                Quaternion.Normalize(Quaternion.Lerp(a.Rotation, rotation, 0.37f)),
                Vector3.Lerp(a.Scale, b.Scale, 0.37f));
        }
    }
}
