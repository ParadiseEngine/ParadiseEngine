using System.Numerics;

using BenchmarkDotNet.Attributes;

using Paradise.Assets.Pipeline.Test;
using Paradise.BLOB;

namespace Paradise.Animation.Benchmarks;

/// <summary>
/// One frame of one character: sample a clip and walk the hierarchy to model space, for every
/// runtime that has played this engine's clips — the glTF reference sampler ShiningPie runs
/// today, the managed-class port that replaced it (frozen copy), the blob runtime that ships,
/// and ozz's own C++ when <c>PARADISE_OZZ_NATIVE</c> names the spike's shim. Two access
/// patterns: <c>Advance</c> steps 1/100 of the clip per frame (playback, where the cursor cache
/// pays), <c>Seek</c> jumps to a random ratio every frame (scrubbing, where i-frames pay).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 15)]
public unsafe class AnimationBenchmarks
{
    [Params(64)]
    public int Joints { get; set; }

    private Rig _rig = null!;
    private float[] _seeks = null!;
    private int _frame;

    private NativeBlobAssetReference<SkeletonBlob> _blobSkeleton = null!;
    private NativeBlobAssetReference<AnimationBlob> _blobClip = null!;
    private NativeBlobAssetReference<SamplingContext> _blobContext = null!;
    private NativeBlobAssetReference<JointPoses> _poses = null!;
    private JointPose[] _managedPoses = null!;
    private Matrix4x4[] _models = null!;

    private Managed.Skeleton _managedSkeleton = null!;
    private Managed.AnimationClip _managedClip = null!;
    private Managed.SamplingContext _managedContext = null!;

    private ManagedSimd.SamplingContext _simdContext = null!;
    private ManagedSimd.SoaTransforms _simdPoses = null!;

    private GltfAnimationRig _gltfRig = null!;
    private Matrix4x4[] _palette = null!;

    private nint _nativeSkeleton;
    private nint _nativeClip;
    private nint _nativeContext;
    private float[] _nativeModels = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rig = new Rig(Joints, seconds: 1.333f);
        var random = new Random(1);
        _seeks = Enumerable.Range(0, 1024).Select(_ => random.NextSingle()).ToArray();

        _blobSkeleton = OzzArchive.ReadSkeleton(_rig.SkeletonArchive);
        _blobClip = OzzArchive.ReadAnimation(_rig.ClipArchive);
        _blobContext = SamplingContext.Create(_blobClip.Value.TrackCount);
        _poses = JointPoses.Create(_blobClip.Value.TrackCount);
        _managedPoses = new JointPose[_blobClip.Value.TrackCount];
        _models = new Matrix4x4[_blobSkeleton.Value.JointCount];

        _managedSkeleton = Managed.Skeleton.Load(_rig.SkeletonArchive);
        _managedClip = Managed.AnimationClip.Load(_rig.ClipArchive);
        _managedContext = new Managed.SamplingContext(_managedClip.TrackCount);

        _simdContext = new ManagedSimd.SamplingContext(_managedClip.TrackCount);
        _simdPoses = new ManagedSimd.SoaTransforms(_managedClip.TrackCount);

        _gltfRig = new GltfAnimationRig(_rig.Asset);
        _palette = new Matrix4x4[_rig.Asset.Skins[_rig.SkinIndex].JointNodes.Length];

        if (NativeOzz.IsAvailable)
        {
            fixed (byte* s = _rig.SkeletonArchive) _nativeSkeleton = NativeOzz.LoadSkeleton(s, (nuint)_rig.SkeletonArchive.Length);
            fixed (byte* a = _rig.ClipArchive) _nativeClip = NativeOzz.LoadAnimation(a, (nuint)_rig.ClipArchive.Length);
            _nativeContext = NativeOzz.CreateContext(NativeOzz.TrackCount(_nativeClip));
            _nativeModels = new float[_blobSkeleton.Value.JointCount * 16];
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _poses.Dispose();
        _blobContext.Dispose();
        _blobClip.Dispose();
        _blobSkeleton.Dispose();
        if (_nativeContext != 0) NativeOzz.FreeContext(_nativeContext);
        if (_nativeClip != 0) NativeOzz.FreeAnimation(_nativeClip);
        if (_nativeSkeleton != 0) NativeOzz.FreeSkeleton(_nativeSkeleton);
    }

    private float Advance() => (_frame++ % 100) / 100f;

    private float Seek() => _seeks[_frame++ & 1023];

    [Benchmark(Baseline = true), BenchmarkCategory("Advance")]
    public void Blob_Advance()
    {
        _blobContext.Value.Sample(ref _blobClip.Value, Advance(), ref _poses.Value);
        LocalToModel.Compute(ref _blobSkeleton.Value, ref _poses.Value, _models);
    }

    [Benchmark, BenchmarkCategory("Advance")]
    public void Managed_Advance()
    {
        _managedContext.Sample(_managedClip, Advance(), _managedPoses);
        Managed.LocalToModel.Compute(_managedSkeleton, _managedPoses, _models);
    }

    [Benchmark, BenchmarkCategory("Advance")]
    public void ManagedSimd_Advance()
    {
        _simdContext.Sample(_managedClip, Advance(), _simdPoses);
        ManagedSimd.LocalToModel.Compute(_managedSkeleton, _simdPoses, _models);
    }

    [Benchmark, BenchmarkCategory("Advance")]
    public void GltfRig_Advance()
    {
        _gltfRig.EvaluatePose(_rig.Clip, Advance() * _rig.Clip.Duration);
        _gltfRig.ComputeJointPalette(_rig.SkinIndex, _rig.MeshNode, _palette);
    }

    [Benchmark, BenchmarkCategory("Advance")]
    public void NativeOzz_Advance()
    {
        if (!NativeOzz.IsAvailable) throw new NotSupportedException($"Set {NativeOzz.EnvironmentVariable} to the shim library to measure native ozz.");
        fixed (float* m = _nativeModels) NativeOzz.SampleModelSpace(_nativeSkeleton, _nativeClip, _nativeContext, Advance(), m);
    }

    [Benchmark, BenchmarkCategory("Seek")]
    public void Blob_Seek()
    {
        _blobContext.Value.Sample(ref _blobClip.Value, Seek(), ref _poses.Value);
        LocalToModel.Compute(ref _blobSkeleton.Value, ref _poses.Value, _models);
    }

    [Benchmark, BenchmarkCategory("Seek")]
    public void Managed_Seek()
    {
        _managedContext.Sample(_managedClip, Seek(), _managedPoses);
        Managed.LocalToModel.Compute(_managedSkeleton, _managedPoses, _models);
    }

    [Benchmark, BenchmarkCategory("Seek")]
    public void ManagedSimd_Seek()
    {
        _simdContext.Sample(_managedClip, Seek(), _simdPoses);
        ManagedSimd.LocalToModel.Compute(_managedSkeleton, _simdPoses, _models);
    }

    [Benchmark, BenchmarkCategory("Seek")]
    public void GltfRig_Seek()
    {
        _gltfRig.EvaluatePose(_rig.Clip, Seek() * _rig.Clip.Duration);
        _gltfRig.ComputeJointPalette(_rig.SkinIndex, _rig.MeshNode, _palette);
    }

    [Benchmark, BenchmarkCategory("Seek")]
    public void NativeOzz_Seek()
    {
        if (!NativeOzz.IsAvailable) throw new NotSupportedException($"Set {NativeOzz.EnvironmentVariable} to the shim library to measure native ozz.");
        fixed (float* m = _nativeModels) NativeOzz.SampleModelSpace(_nativeSkeleton, _nativeClip, _nativeContext, Seek(), m);
    }
}
