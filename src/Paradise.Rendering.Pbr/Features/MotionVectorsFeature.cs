using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Rasterizes camera, object and skinned motion for the opaque scene.</summary>
/// <remarks>XY is current UV minus previous UV, with a top-left origin and projection jitter
/// included. A temporal consumer samples at current UV minus XY, compares its saved depth with
/// Z (previous device depth), and rejects W = 0. Background and transparent geometry have no
/// history. Blended geometry conservatively rejects history over its full visible triangles.
/// CPU-deformed vertices and custom vertex displacement need their own motion pass.</remarks>
public sealed class MotionVectorsFeature : IRenderFeature
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MotionDrawGpu
    {
        public Matrix4x4 CurrentMvp;
        public Matrix4x4 PreviousMvp;
        public Vector4 Params;
    }

    private readonly PbrContext _ctx;
    private readonly MotionHistory _history = new();
    private ShaderProgramDesc? _program;
    private PipelineHandle _rigidPipeline;
    private PipelineHandle _skinnedPipeline;
    private BufferHandle _drawBuffer;
    private BufferHandle _previousJoints;
    private BindGroupHandle _drawGroup;
    private BindGroupHandle _jointGroup;
    private byte[] _staging = [];
    private Matrix4x4[] _jointSnapshot = [];
    private int _jointCount;
    private int _drawCount;
    private bool _recorded;

    internal MotionVectorsFeature(PbrContext ctx) => _ctx = ctx;

    public FeatureDefinition Definition => PbrFeatures.MotionVectors;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>Whether this frame can reproject into the preceding rendered frame.</summary>
    public bool HistoryReady { get; private set; }

    /// <summary>The current motion target, or an invalid handle while motion is disabled.</summary>
    public TextureViewHandle View { get; private set; }

    /// <summary>Rejects history on the next frame; use for camera cuts without replacing the scene.</summary>
    public void ResetHistory()
    {
        _history.Reset();
        HistoryReady = false;
    }

    public void Resize(uint width, uint height)
    {
        ResetHistory();
        View = default;
    }

    public void OnEnabledChanged(bool enabled)
    {
        ResetHistory();
        if (!enabled) View = default;
    }

    public void Setup(in FrameContext frame)
    {
        _recorded = false;
        _drawCount = 0;
        if (!_ctx.Scene.MotionVectors.Enabled && (frame.Requirements & FrameRequirements.MotionVectors) == 0)
        {
            ResetHistory();
            View = default;
            return;
        }

        EnsureResources();
        _ctx.Targets.Ensure(PbrTargets.MotionVectors, _ctx.FrameTarget(TextureFormat.Rgba16Float));
        _ctx.Targets.Ensure(PbrTargets.MotionDepth, _ctx.FrameTarget(TextureFormat.Depth32Float));
        // View is public, so its producer and store must survive even without an in-graph consumer.
        _ctx.Targets.Export(PbrTargets.MotionVectors);
        View = _ctx.Targets.View(PbrTargets.MotionVectors);
        HistoryReady = _history.Begin(_ctx.Scene);
        var output = frame.Graph.Texture(PbrTargets.MotionVectors);
        frame.Graph.AddRasterPass("MotionVectors.Geometry", RenderPassEvent.AfterPrepass, offset: 1)
            .Color(0, output, LoadOp.Clear, clear: new ColorRgba(0f, 0f, 0f, 0f))
            .Depth(frame.Graph.Texture(PbrTargets.MotionDepth), LoadOp.Clear, clear: 1f)
            .Record(this, Record);
        frame.Blackboard.Publish(PbrResults.MotionVectors, output);
    }

    private void EnsureResources()
    {
        if (_program is not null) return;
        var renderer = _ctx.Renderer;
        _program = ShaderPrograms.WithDynamicDrawRing(ShaderPrograms.Load("Shaders.motionVectors"));
        UniformLayoutValidator.ValidateBlock(_program, "motion", (uint)Unsafe.SizeOf<MotionDrawGpu>(),
            [("currentMvp", 0, 64), ("previousMvp", 64, 64), ("params", 128, 16)]);
        _rigidPipeline = CreatePipeline("vertexMain");
        _staging = new byte[_ctx.DrawStride * PbrContext.MaxDrawsPerFrame];
        _drawBuffer = renderer.CreateBuffer(new BufferDesc("PbrMotionDrawRing", (ulong)_staging.Length,
            BufferUsage.Uniform | BufferUsage.CopyDst));
        _previousJoints = renderer.CreateBuffer(new BufferDesc("PbrMotionPreviousJoints", _ctx.JointBufferBytes,
            BufferUsage.Storage | BufferUsage.CopyDst));
        _jointSnapshot = new Matrix4x4[_ctx.JointCapacity];
        _drawGroup = renderer.CreateBindGroup(new BindGroupDesc("PbrMotionDrawGroup", ShaderPrograms.FindGroup(_program, 0),
            new[] { BindGroupEntryDesc.ForBuffer(0, _drawBuffer, 0, (ulong)Unsafe.SizeOf<MotionDrawGpu>()) }));
        _jointGroup = renderer.CreateBindGroup(new BindGroupDesc("PbrMotionJoints", ShaderPrograms.FindGroup(_program, 1),
        new[] {
            BindGroupEntryDesc.ForBuffer(0, _ctx.JointBuffer, 0, _ctx.JointBufferBytes),
            BindGroupEntryDesc.ForBuffer(1, _previousJoints, 0, _ctx.JointBufferBytes),
        }));
    }

    private PipelineHandle CreatePipeline(string entryPoint) => _ctx.Renderer.CreatePipeline(
        _program!, TextureFormat.Rgba16Float, depthStencilFormat: TextureFormat.Depth32Float,
        depthWriteEnabled: true, depthCompare: CompareFunction.Less, vertexEntryPoint: entryPoint);

    private static void Record(MotionVectorsFeature self, ref PassRecording pass, int _)
    {
        self._recorded = true;
        pass.Encoder.SetBindGroup(1, self._jointGroup);
        RecordBucket(self, ref pass, self._ctx.Opaque, includeHistory: true);
        RecordBucket(self, ref pass, self._ctx.Blend, includeHistory: false);
    }

    private static void RecordBucket(MotionVectorsFeature self, ref PassRecording pass,
        List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> bucket, bool includeHistory)
    {
        var ctx = self._ctx;
        ref var encoder = ref pass.Encoder;
        bool? activeSkinned = null;
        foreach (var (instance, primitive, _) in bucket)
        {
            var skinned = primitive.Skinned && instance.JointOffset >= 0;
            if (activeSkinned != skinned)
            {
                if (skinned && !self._skinnedPipeline.IsValid)
                    self._skinnedPipeline = self.CreatePipeline("vertexMainSkinned");
                encoder.SetPipeline(skinned ? self._skinnedPipeline : self._rigidPipeline);
                activeSkinned = skinned;
            }

            var previous = default(MotionHistory.InstanceState);
            var valid = includeHistory && self.HistoryReady && self._history.TryPrevious(instance, out previous)
                && (instance.JointOffset >= 0) == (previous.JointOffset >= 0);
            if (!valid) previous = new MotionHistory.InstanceState(instance.Model, instance.JointOffset);
            var draw = new MotionDrawGpu
            {
                CurrentMvp = instance.Model * ctx.ViewProjection,
                PreviousMvp = valid ? previous.Model * self._history.ViewProjection : instance.Model * ctx.ViewProjection,
                Params = new Vector4(Math.Max(instance.JointOffset, 0), Math.Max(previous.JointOffset, 0), valid ? 1f : 0f, 0f),
            };
            var offset = self._drawCount++ * (int)ctx.DrawStride;
            MemoryMarshal.Write(self._staging.AsSpan(offset), in draw);
            encoder.SetBindGroup(0, self._drawGroup, dynamicOffset: (uint)offset);
            encoder.SetVertexBuffer(0, primitive.VertexBuffer, 0, primitive.VertexByteLength);
            encoder.SetIndexBuffer(primitive.IndexBuffer, IndexFormat.Uint32, 0, primitive.IndexByteLength);
            encoder.DrawIndexed(new DrawIndexedCommand(primitive.IndexCount, 1, 0, 0, 0));
        }
    }

    public void BeforeSubmit()
    {
        if (!_recorded) return;
        if (_drawCount > 0)
            _ctx.Renderer.UpdateBuffer<byte>(_drawBuffer, 0, _staging.AsSpan(0, _drawCount * (int)_ctx.DrawStride));
        // Upload the old CPU snapshot before overwriting it. Copying JointBuffer at frame start
        // would copy the newly staged pose and erase all deformation velocity.
        if (_jointCount > 0)
            _ctx.Renderer.UpdateBuffer<Matrix4x4>(_previousJoints, 0, _jointSnapshot.AsSpan(0, _jointCount));
        // An inactive feature misses palette writes. Reset from the complete current state,
        // including identity slots that have never been explicitly staged, on every restart.
        // Otherwise only the prefix changed this frame needs uploading on the next frame.
        _jointCount = HistoryReady ? _ctx.JointHighWater : _ctx.JointCapacity;
        _ctx.JointPalettes.AsSpan(0, _jointCount).CopyTo(_jointSnapshot);
        _history.Capture(_ctx.Scene, _ctx.ViewProjection, _ctx.Opaque);
    }

    public void Dispose()
    {
        if (_program is null) return;
        if (_skinnedPipeline.IsValid) _ctx.Renderer.DestroyPipeline(_skinnedPipeline);
        _ctx.Renderer.DestroyPipeline(_rigidPipeline);
        _ctx.Renderer.DestroyBindGroup(_drawGroup);
        _ctx.Renderer.DestroyBindGroup(_jointGroup);
        _ctx.Renderer.DestroyBuffer(_drawBuffer);
        _ctx.Renderer.DestroyBuffer(_previousJoints);
    }
}

internal sealed class MotionHistory
{
    internal readonly record struct InstanceState(Matrix4x4 Model, int JointOffset);

    private readonly Dictionary<PbrInstance, InstanceState> _instances = new(ReferenceEqualityComparer.Instance);
    private PbrScene? _scene;
    private ulong _version;

    public Matrix4x4 ViewProjection { get; private set; }

    public bool Begin(PbrScene scene)
    {
        if (!ReferenceEquals(_scene, scene) || _version != scene.TemporalHistoryVersion)
        {
            Reset();
            return false;
        }
        return true;
    }

    public bool TryPrevious(PbrInstance instance, out InstanceState state) => _instances.TryGetValue(instance, out state);

    public void Capture(PbrScene scene, Matrix4x4 viewProjection,
        List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> opaque)
    {
        _scene = scene;
        _version = scene.TemporalHistoryVersion;
        ViewProjection = viewProjection;
        _instances.Clear();
        foreach (var (instance, _, _) in opaque)
            _instances[instance] = new InstanceState(instance.Model, instance.JointOffset);
    }

    public void Reset()
    {
        _scene = null;
        _instances.Clear();
    }
}
