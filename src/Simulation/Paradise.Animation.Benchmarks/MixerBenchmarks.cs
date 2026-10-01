using System.Numerics;

using BenchmarkDotNet.Attributes;

using Paradise.Animation.Offline;
using Paradise.BLOB;

namespace Paradise.Animation.Benchmarks;

/// <summary>One character's frame through <see cref="AnimationPlayer"/> — advance, sample, blend, hierarchy walk — as its inputs grow, against the two-slot cross-fade it replaced.</summary>
/// <remarks>Every input is its own clip keyed at 30 Hz on every joint's translation, rotation and scale, at an unequal
/// nonzero weight, so each one is sampled. <c>Layered</c> adds an override on the joint1 subtree and an additive layer
/// over the same mix. <c>TwoSlot</c> is the old player's cross-fade frame: two samples, one lerp, the hierarchy walk.</remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public class MixerBenchmarks
{
    private const float Step = 1f / 60f;

    [Params(64)]
    public int Joints { get; set; }

    [Params(1, 2, 3, 8, 16)]
    public int Inputs { get; set; }

    private NativeBlobAssetReference<SkeletonBlob> _skeleton = null!;
    private NativeBlobAssetReference<AnimationBlob>[] _clips = null!;
    private NativeBlobAssetReference<AdditiveAnimationBlob> _additive = null!;
    private AnimationPlayer _mix = null!;
    private AnimationPlayer _layered = null!;

    private NativeBlobAssetReference<SamplingContext> _currentContext = null!;
    private NativeBlobAssetReference<SamplingContext> _outgoingContext = null!;
    private NativeBlobAssetReference<JointPoses> _current = null!;
    private NativeBlobAssetReference<JointPoses> _outgoing = null!;
    private Matrix4x4[] _models = null!;
    private float _time;

    [GlobalSetup]
    public void Setup()
    {
        _skeleton = SkeletonBuilder.Build(Skeleton(Joints));
        _clips = new NativeBlobAssetReference<AnimationBlob>[Math.Max(Inputs, 2)];
        for (var i = 0; i < _clips.Length; i++) _clips[i] = AnimationBuilder.Build(Clip(Joints, seconds: 1f + 0.1f * i, phase: i));
        _additive = AdditiveAnimationBuilder.Build(ref _clips[1].Value);

        _mix = new AnimationPlayer(_skeleton, Inputs);
        _layered = new AnimationPlayer(_skeleton, Inputs + 2);
        for (var i = 0; i < Inputs; i++)
        {
            _mix.Add(_clips[i], 1f / (i + 1));
            _layered.Add(_clips[i], 1f / (i + 1));
        }

        var upper = _layered.AddLayer(AnimationLayerMode.Override, 0.7f, JointMask.Branch(ref _skeleton.Value, "joint1"));
        _layered.Add(_clips[0], upper, rate: 1.3f);
        _layered.Add(_additive, _layered.AddLayer(AnimationLayerMode.Additive, 0.5f));

        _currentContext = SamplingContext.Create(Joints);
        _outgoingContext = SamplingContext.Create(Joints);
        _current = JointPoses.Create(Joints);
        _outgoing = JointPoses.Create(Joints);
        _models = new Matrix4x4[Joints];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _mix.Dispose();
        _layered.Dispose();
        _current.Dispose();
        _outgoing.Dispose();
        _currentContext.Dispose();
        _outgoingContext.Dispose();
        _additive.Dispose();
        foreach (var clip in _clips) clip.Dispose();
        _skeleton.Dispose();
    }

    [Benchmark]
    public void Mix()
    {
        _mix.Advance(Step);
        _mix.Evaluate();
    }

    [Benchmark]
    public void Layered()
    {
        _layered.Advance(Step);
        _layered.Evaluate();
    }

    [Benchmark(Baseline = true)]
    public void TwoSlot()
    {
        _time = (_time + Step) % 1f;
        _currentContext.Value.Sample(ref _clips[0].Value, _time, ref _current.Value);
        _outgoingContext.Value.Sample(ref _clips[1].Value, _time, ref _outgoing.Value);
        JointPoses.Blend(ref _outgoing.Value, ref _current.Value, 0.37f, ref _current.Value);
        LocalToModel.Compute(ref _skeleton.Value, ref _current.Value, _models);
    }

    /// <summary>Joint i parents to (i−1)/3: a tree three wide, like a spine with limbs.</summary>
    private static RawSkeleton Skeleton(int joints)
    {
        var raw = new RawSkeleton();
        var nodes = new RawJoint[joints];
        for (var i = 0; i < joints; i++)
        {
            nodes[i] = new RawJoint($"joint{i}") { Transform = new JointPose(new Vector3(0f, 0.25f, 0f), Quaternion.Identity, Vector3.One) };
            if (i == 0) raw.Roots.Add(nodes[i]);
            else nodes[(i - 1) / 3].Children.Add(nodes[i]);
        }

        return raw;
    }

    private static RawAnimation Clip(int joints, float seconds, int phase)
    {
        var raw = new RawAnimation { Name = $"clip{phase}", Duration = seconds };
        var frames = (int)MathF.Round(seconds * 30f);
        for (var joint = 0; joint < joints; joint++)
        {
            var track = new RawTrack();
            var offset = joint * 0.37f + phase;
            for (var frame = 0; frame <= frames; frame++)
            {
                var time = MathF.Min(frame / 30f, seconds);
                var angle = 0.4f * MathF.Sin(time * MathF.Tau / seconds + offset);
                track.Translations.Add(new TranslationKey(time, new Vector3(0f, 0.25f + 0.02f * MathF.Sin(2f * angle), 0f)));
                track.Rotations.Add(new RotationKey(time, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle)));
                track.Scales.Add(new ScaleKey(time, new Vector3(1f + 0.05f * MathF.Cos(angle))));
            }

            raw.Tracks.Add(track);
        }

        return raw;
    }
}
