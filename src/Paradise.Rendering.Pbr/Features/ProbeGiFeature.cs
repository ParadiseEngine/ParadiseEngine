using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Geometry;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Updates probe irradiance and visibility by tracing the static scene's BVH.</summary>
/// <remarks>Each frame traces, blends irradiance and distance moments, then relocates/classifies
/// probes. The volume fits static geometry unless authored. Ping-pong atlases and state preserve
/// the previous bounce and avoid unsupported read/write storage.</remarks>
public sealed class ProbeGiFeature : IRenderFeature
{
    // Must match probeBlend.slang's IrradianceTexels / VisibilityTexels.
    private const int IrradianceTexels = 8;
    private const int VisibilityTexels = 14;
    private const int MaxAtlasSize = 8192;
    // Must match probeBlend.slang's MaxRays: the blend stages a probe's rays in workgroup memory.
    private const int MaxRaysPerProbe = 256;
    private const int TraceWorkgroup = 64;

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct Int4
    {
        public int X, Y, Z, W;
    }

    /// <summary>Mirror of probes.slang <c>ProbeVolume</c> (144 B).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 144)]
    private struct ProbeVolumeGpu
    {
        public Vector4 Origin;
        public Vector4 Spacing;
        public Int4 Counts;
        public Vector4 Atlas;
        public Vector4 Atlas2;
        public Vector4 Params;
        public Vector4 Rotation;
        public Int4 Window;
        public Int4 Scroll;
    }

    private readonly PbrContext _ctx;
    private readonly ShadowFeature _shadows;
    private readonly ComputePipelineHandle _tracePipeline;
    private readonly ComputePipelineHandle _blendIrradiancePipeline;
    private readonly ComputePipelineHandle _blendVisibilityPipeline;
    private readonly ComputePipelineHandle _updatePipeline;
    private readonly ComputePipelineHandle _carryIrradiancePipeline;
    private readonly ComputePipelineHandle _carryVisibilityPipeline;
    private readonly ShaderProgramDesc _traceProgram;
    private readonly ShaderProgramDesc _blendProgram;
    private readonly ShaderProgramDesc _updateProgram;
    private readonly ShaderProgramDesc _carryProgram;
    private BufferHandle _carryBuffer;
    private int[] _carryProbeIndices = [];
    private int _carryCount;
    private readonly BufferHandle[] _stateBuffers = new BufferHandle[2];
    private readonly GiLightHierarchy _lightHierarchy = new();
    private readonly BufferHandle _lightTreeBuffer;
    private BufferHandle _rayBuffer;
    private int _rayCapacity;
    private int _stateCapacity;
    private int _current;
    private PbrProbeVolume? _fitted;
    private int _probeCount;
    private readonly ProbeGrid _grid = new();
    private readonly ProbeUpdateScheduler _scheduler = new();
    private readonly List<Aabb> _invalidations = [];
    private BufferHandle _updateBuffer;
    private int _updateCapacity;
    private int _framesSinceReset;
    private ProbeVolumeGpu _volume;
    private readonly Random _random = new(1234);

    internal ProbeGiFeature(PbrContext ctx, ShadowFeature shadows)
    {
        _ctx = ctx;
        _shadows = shadows;
        var renderer = ctx.Renderer;

        _traceProgram = ShaderPrograms.Load("Shaders.probeTrace");
        _blendProgram = ShaderPrograms.Load("Shaders.probeBlend");
        _updateProgram = ShaderPrograms.Load("Shaders.probeUpdate");
        _carryProgram = ShaderPrograms.Load("Shaders.probeCarry");
        _tracePipeline = renderer.CreateComputePipeline(_traceProgram);
        _blendIrradiancePipeline = renderer.CreateComputePipeline(_blendProgram, "blendIrradiance");
        _blendVisibilityPipeline = renderer.CreateComputePipeline(_blendProgram, "blendVisibility");
        _updatePipeline = renderer.CreateComputePipeline(_updateProgram);
        _carryIrradiancePipeline = renderer.CreateComputePipeline(_carryProgram, "carryIrradiance");
        _carryVisibilityPipeline = renderer.CreateComputePipeline(_carryProgram, "carryVisibility");

        VolumeBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrProbeVolume", (ulong)Unsafe.SizeOf<ProbeVolumeGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
        _lightTreeBuffer = renderer.CreateBuffer(new BufferDesc(
            "PbrGiLightTree", (ulong)Unsafe.SizeOf<GiLightTreeGpu>(), BufferUsage.Uniform | BufferUsage.CopyDst));
        Sampler = renderer.CreateSampler(new SamplerDesc(
            "PbrProbeSampler",
            SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge, SamplerAddressMode.ClampToEdge,
            SamplerFilterMode.Linear, SamplerFilterMode.Linear, SamplerFilterMode.Nearest));

        // Every resource the scene binds exists from the start, at its smallest, so a frame with
        // the probes off binds real objects and a disabled volume rather than nothing.
        EnsureStateBuffers(1);
        ResetStates();
        EnsureUpdateBuffer(1);
        EnsureRayBuffer(1);
        EnsureAtlases(1, 1, 1);
        UploadVolume();
        // The buffer the SCENE binds, not one of ours: Setup normally picks which of the two
        // state buffers this frame shades against, and a build with the probes switched off never
        // runs one — leaving the scene to bind a default handle and the graph to fail resolving
        // its group. Picked here so the answer exists before the first frame.
        ShadingStateBuffer = _stateBuffers[_current];
    }

    /// <summary>Runtime settings, replaced on the rendering thread before the next frame.</summary>
    public PbrGi Settings { get; set; } = new();

    public FeatureDefinition Definition => PbrFeatures.GlobalIllumination;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>The probe volume uniforms the scene binds at group 3; origin.w = 0 while the
    /// probes are off, which is how the shader knows to shade with the sky.</summary>
    internal BufferHandle VolumeBuffer { get; }
    internal static ulong VolumeBufferBytes => (ulong)Unsafe.SizeOf<ProbeVolumeGpu>();

    /// <summary>The probe-state buffer the scene shades this frame with: the one this frame's
    /// atlases were blended against, not the one the update pass wrote for the next frame.</summary>
    internal BufferHandle ShadingStateBuffer { get; private set; }
    internal ulong StateBufferBytes => (ulong)_stateCapacity * 16;

    internal SamplerHandle Sampler { get; }

    /// <summary>Probes in the current volume; 0 while the probes are off.</summary>
    public int ProbeCount => _volume.Origin.W > 0.5f ? _probeCount : 0;

    /// <summary>The volume in use this frame — the authored one or the fit — or null while off.</summary>
    public PbrProbeVolume? ActiveVolume => _volume.Origin.W > 0.5f ? _fitted : null;

    public void Resize(uint width, uint height)
    {
        // Probe atlases are sized by the volume, not the frame.
    }

    /// <summary>The volume uniform the scene binds is what tells the shader a probe grid covers
    /// this pixel. Switched off, the atlases stop being blended but the volume would still claim
    /// them, and every surface inside it would shade against a picture that is no longer being
    /// updated — so the volume is retracted here.</summary>
    public void OnEnabledChanged(bool enabled)
    {
        if (enabled || _volume.Origin.W == 0f) return;
        _volume.Origin.W = 0f;
        UploadVolume();
    }

    public void Setup(in FrameContext frame)
    {
        var gi = Settings;
        if (gi.Scrolling && gi.Volume is null)
            throw new InvalidOperationException("Scrolling requires an authored PbrGi.Volume.");
        if (gi.UpdateFocus is { } focus && !Finite(focus))
            throw new InvalidOperationException("PbrGi.UpdateFocus must be finite.");
        ShadingStateBuffer = _stateBuffers[_current];
        var fit = gi.Enabled ? gi.Volume ?? Fit(_ctx.Trace.SceneBounds, gi) : null;
        if (gi.Volume is { } authored) ValidateAuthored(authored, gi);
        if (fit is null)
        {
            if (_volume.Origin.W != 0f)
            {
                _volume.Origin.W = 0f;
                UploadVolume();
            }
            return;
        }

        EnsureVolume(fit, gi);
        fit = _fitted!;
        ApplyInvalidations();
        _lightHierarchy.Build(_ctx.Scene.Lights, gi.LightCullingEnabled);
        ref readonly var lightTree = ref _lightHierarchy.Data;
        _ctx.Renderer.UpdateBuffer<GiLightTreeGpu>(_lightTreeBuffer, 0, MemoryMarshal.CreateReadOnlySpan(in lightTree, 1));
        // After EnsureVolume: a refit recreates the state buffers, and the scene must bind the
        // one this frame's atlases are blended against, not the handle that was just destroyed.
        ShadingStateBuffer = _stateBuffers[_current];
        var graph = frame.Graph;
        var rays = Math.Clamp(gi.RaysPerProbe, 8, MaxRaysPerProbe);
        var windowCount = _scheduler.Select(_grid, gi.ProbesPerFrame, gi.UpdateFocus);
        _ctx.Renderer.UpdateBuffer<ProbeUpdateScheduler.Update>(_updateBuffer, 0, _scheduler.Updates);
        EnsureRayBuffer(windowCount * rays);

        var next = 1 - _current;
        FillVolume(fit, gi, rays, windowCount);
        UploadVolume();

        var rayBuffer = graph.ImportBuffer(_rayBuffer, GraphResourceScope.GraphOnly);
        var readIrradiance = graph.Texture(PbrTargets.GiIrradiance[_current]);
        var readVisibility = graph.Texture(PbrTargets.GiVisibility[_current]);
        var writeIrradiance = graph.Texture(PbrTargets.GiIrradiance[next]);
        var writeVisibility = graph.Texture(PbrTargets.GiVisibility[next]);
        var rayBytes = (ulong)_rayCapacity * 16;
        var nextStates = graph.ImportBuffer(_stateBuffers[next], GraphResourceScope.GraphOnly);

        // Current updates overwrite complete tiles and states, so only the difference between
        // the previous and current selections needs carrying to the other atlas.
        var carryCount = 0;
        for (var slot = 0; slot < _carryCount; slot++)
        {
            var probe = _carryProbeIndices[slot];
            if (_scheduler.Updates[probe].Slot < 0) _carryProbeIndices[carryCount++] = probe;
        }
        if (carryCount > 0)
        {
            _ctx.Renderer.UpdateBuffer<int>(_carryBuffer, 0, _carryProbeIndices.AsSpan(0, carryCount));
            graph.AddComputePass("Gi.Carry", RenderPassEvent.GlobalIllumination, offset: -1)
                .BindGroup(0, "PbrGiCarryGroup", ShaderPrograms.FindGroup(_carryProgram, 0),
                [
                    GraphBinding.Buffer(0, _carryBuffer, 0, (ulong)_carryProbeIndices.Length * sizeof(int)),
                    GraphBinding.StorageTexture(1, writeIrradiance),
                    GraphBinding.StorageTexture(2, writeVisibility),
                    GraphBinding.TrackedBuffer(3, nextStates, 0, StateBufferBytes, write: true),
                ])
                .BindGroup(3, "PbrGiCarryProbeGroup", ShaderPrograms.FindGroup(_carryProgram, 3),
                    ProbeGroupBindings(_carryProgram, readIrradiance, readVisibility, _stateBuffers[_current]))
                .Record(this, RecordCarry, carryCount);
        }

        graph.AddComputePass("Gi.Trace", RenderPassEvent.GlobalIllumination)
            .BindGroup(0, "PbrGiTraceGroup", ShaderPrograms.FindGroup(_traceProgram, 0),
            [
                GraphBinding.TrackedBuffer(0, rayBuffer, 0, rayBytes, write: true),
                GraphBinding.Buffer(1, _lightTreeBuffer, 0, (ulong)Unsafe.SizeOf<GiLightTreeGpu>()),
            ])
            .BindGroup(1, "PbrGiFrameGroup", ShaderPrograms.FindGroup(_traceProgram, 1), FrameGroupBindings(_traceProgram, graph))
            .BindGroup(2, "PbrGiSceneGroup", ShaderPrograms.FindGroup(_traceProgram, 2), _ctx.Trace.Bindings(globalIllumination: true))
            .BindGroup(3, "PbrGiProbeGroup", ShaderPrograms.FindGroup(_traceProgram, 3),
                ProbeGroupBindings(_traceProgram, readIrradiance, readVisibility, _stateBuffers[_current]))
            .Record(this, RecordTrace, (windowCount * rays + TraceWorkgroup - 1) / TraceWorkgroup);

        // Both blends in one pass: same groups, two pipelines. The read atlases were written last
        // frame, so nothing this frame produces them and the graph does not order them.
        graph.AddComputePass("Gi.Blend", RenderPassEvent.GlobalIllumination, offset: 1)
            .BindGroup(0, "PbrGiBlendGroup", ShaderPrograms.FindGroup(_blendProgram, 0),
            [
                GraphBinding.TrackedBuffer(0, rayBuffer, 0, rayBytes),
                GraphBinding.StorageTexture(1, writeIrradiance),
                GraphBinding.StorageTexture(2, writeVisibility),
            ])
            .BindGroup(3, "PbrGiBlendProbeGroup", ShaderPrograms.FindGroup(_blendProgram, 3),
                ProbeGroupBindings(_blendProgram, readIrradiance, readVisibility, _stateBuffers[_current]))
            .Record(this, RecordBlend, windowCount);

        // Nothing in this frame reads the next frame's states, so the graph would cull the update;
        // its consumer is the next frame's trace.
        graph.AddComputePass("Gi.Update", RenderPassEvent.GlobalIllumination, offset: 2)
            .BindGroup(0, "PbrGiUpdateGroup", ShaderPrograms.FindGroup(_updateProgram, 0),
            [
                GraphBinding.TrackedBuffer(0, rayBuffer, 0, rayBytes),
                GraphBinding.TrackedBuffer(1, nextStates, 0, StateBufferBytes, write: true),
            ])
            .BindGroup(3, "PbrGiUpdateProbeGroup", ShaderPrograms.FindGroup(_updateProgram, 3),
                ProbeGroupBindings(_updateProgram, readIrradiance, readVisibility, _stateBuffers[_current]))
            .NeverCull()
            .Record(this, RecordUpdate, (windowCount + TraceWorkgroup - 1) / TraceWorkgroup);

        frame.Blackboard.Publish(PbrResults.GiIrradiance, writeIrradiance);
        frame.Blackboard.Publish(PbrResults.GiVisibility, writeVisibility);
        EnsureCarryBuffer(_probeCount);
        for (var slot = 0; slot < windowCount; slot++)
            _carryProbeIndices[slot] = _scheduler.Updates[slot].Probe;
        _carryCount = windowCount;
        _current = next;
        _framesSinceReset++;
    }

    private static void RecordTrace(ProbeGiFeature self, ref PassRecording pass, int workgroups)
    {
        pass.Encoder.SetComputePipeline(self._tracePipeline);
        pass.SetBindGroup(0);
        pass.SetBindGroup(1);
        pass.SetBindGroup(2);
        pass.SetBindGroup(3);
        pass.Encoder.Dispatch(new DispatchCommand((uint)workgroups, 1, 1));
    }

    private static void RecordBlend(ProbeGiFeature self, ref PassRecording pass, int probes)
    {
        pass.SetBindGroup(0);
        pass.SetBindGroup(3);
        pass.Encoder.SetComputePipeline(self._blendIrradiancePipeline);
        pass.Encoder.Dispatch(new DispatchCommand((uint)probes, 1, 1));
        pass.Encoder.SetComputePipeline(self._blendVisibilityPipeline);
        pass.Encoder.Dispatch(new DispatchCommand((uint)probes, 1, 1));
    }

    private static void RecordCarry(ProbeGiFeature self, ref PassRecording pass, int probes)
    {
        pass.SetBindGroup(0);
        pass.SetBindGroup(3);
        pass.Encoder.SetComputePipeline(self._carryIrradiancePipeline);
        pass.Encoder.Dispatch(new DispatchCommand((uint)probes, 1, 1));
        pass.Encoder.SetComputePipeline(self._carryVisibilityPipeline);
        pass.Encoder.Dispatch(new DispatchCommand((uint)probes, 1, 1));
    }

    private static void RecordUpdate(ProbeGiFeature self, ref PassRecording pass, int workgroups)
    {
        pass.Encoder.SetComputePipeline(self._updatePipeline);
        pass.SetBindGroup(0);
        pass.SetBindGroup(3);
        pass.Encoder.Dispatch(new DispatchCommand((uint)workgroups, 1, 1));
    }

    /// <summary>The group-1 (frame) bindings a compute program reflects — only what it references
    /// survives slangc, so the entries are chosen by the layout rather than assumed.</summary>
    private GraphBinding[] FrameGroupBindings(ShaderProgramDesc program, FrameGraph graph)
    {
        var layout = ShaderPrograms.FindGroup(program, 1);
        var bindings = new GraphBinding[layout.Entries.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            bindings[i] = layout.Entries[i].Binding switch
            {
                0 => GraphBinding.Buffer(0, _ctx.FrameUniformBuffer, 0, PbrContext.FrameUniformBytes),
                1 => GraphBinding.TextureArray(1, graph.Texture(PbrTargets.ShadowArray)),
                2 => GraphBinding.Sampler(2, _shadows.Sampler),
                // The Forward+ cluster masks are a screen-space concern no ray reads, but slangc
                // keeps the declaration; any storage buffer satisfies the layout, so the joint
                // palettes stand in rather than a buffer that exists only to be bound.
                3 => GraphBinding.Buffer(3, _ctx.JointBuffer, 0, _ctx.JointBufferBytes),
                4 => GraphBinding.Buffer(4, _ctx.JointBuffer, 0, _ctx.JointBufferBytes),
                var other => throw new InvalidOperationException($"Probe program references frame binding {other}, which this feature does not supply."),
            };
        }
        return bindings;
    }

    /// <summary>The group-3 (probe) bindings a program reflects, over the atlases it READS.</summary>
    internal GraphBinding[] ProbeGroupBindings(ShaderProgramDesc program, GraphTexture irradiance, GraphTexture visibility, BufferHandle states)
    {
        var layout = ShaderPrograms.FindGroup(program, 3);
        var bindings = new GraphBinding[layout.Entries.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            bindings[i] = layout.Entries[i].Binding switch
            {
                7 => GraphBinding.Texture(7, irradiance),
                8 => GraphBinding.Texture(8, visibility),
                9 => GraphBinding.Buffer(9, VolumeBuffer, 0, VolumeBufferBytes),
                10 => GraphBinding.Buffer(10, states, 0, StateBufferBytes),
                11 => GraphBinding.Sampler(11, Sampler),
                12 => GraphBinding.Buffer(12, _updateBuffer, 0, (ulong)_updateCapacity * 8),
                var other => throw new InvalidOperationException($"Probe program references probe binding {other}, which this feature does not supply."),
            };
        }
        return bindings;
    }

    /// <summary>Fit a volume to the static scene: probes spaced so the count stays under the
    /// budget and the atlases under the texture size limit, centred on the padded bounds.</summary>
    internal static PbrProbeVolume? Fit(Aabb bounds, PbrGi gi)
    {
        if (bounds.IsEmpty) return null;
        var margin = MathF.Max(gi.FitMargin, 0f);
        var min = bounds.Min - new Vector3(margin);
        var max = bounds.Max + new Vector3(margin);
        var extent = Vector3.Max(max - min, new Vector3(0.01f));
        var maxProbes = Math.Max(gi.MaxProbes, 8);

        var spacing = MathF.Cbrt(extent.X * extent.Y * extent.Z / maxProbes);
        spacing = MathF.Max(spacing, MathF.Max(gi.ProbeSpacing, 0.01f));
        // Widen the spacing until the grid fits the budget and the atlas. Terminates: every axis
        // bottoms out at two probes, and 2×2×2 fits any budget of eight or more. The cube-root
        // start is far off for a long corridor with a tight budget, which is why this is a loop
        // and not a fixed number of attempts.
        int cx, cy, cz;
        while (true)
        {
            cx = CountFor(extent.X, spacing);
            cy = CountFor(extent.Y, spacing);
            cz = CountFor(extent.Z, spacing);
            var fitsBudget = (long)cx * cy * cz <= maxProbes;
            var fitsAtlas = (long)cx * cy * (VisibilityTexels + 2) <= MaxAtlasSize && cz * (VisibilityTexels + 2) <= MaxAtlasSize;
            if (fitsBudget && fitsAtlas) break;
            spacing *= 1.1f;
        }

        // Centre the grid on the bounds so the padding is symmetric.
        var span = new Vector3(cx - 1, cy - 1, cz - 1) * spacing;
        var origin = min + (extent - span) * 0.5f;
        return new PbrProbeVolume(origin, new Vector3(spacing), cx, cy, cz);

        static int CountFor(float extent, float spacing) => Math.Clamp((int)MathF.Ceiling(extent / spacing) + 1, 2, 256);
    }

    /// <summary>An authored volume is held to the limits the fit enforces on itself: at least two
    /// probes per axis (the lookup interpolates between neighbours), the probe budget, and the
    /// texture size the atlases must fit. Thrown here, at setup, with the offending count named.</summary>
    internal static void ValidateAuthored(PbrProbeVolume volume, PbrGi gi)
    {
        if (volume.CountX < 2 || volume.CountY < 2 || volume.CountZ < 2)
            throw new ArgumentException(
                $"Probe volume counts {volume.CountX}x{volume.CountY}x{volume.CountZ}: every axis needs at least two probes.", nameof(gi));
        // Divide the budget before multiplying all three axes: malformed int counts can overflow long.
        if ((long)volume.CountX * volume.CountY > (long)gi.MaxProbes / volume.CountZ)
            throw new ArgumentException($"Probe volume exceeds PbrGi.MaxProbes = {gi.MaxProbes}.", nameof(gi));
        var probes = (long)volume.CountX * volume.CountY * volume.CountZ;
        if (probes > gi.MaxProbes)
            throw new ArgumentException(
                $"Probe volume holds {probes} probes, above PbrGi.MaxProbes = {gi.MaxProbes}; raise the budget or thin the grid.", nameof(gi));
        var tile = VisibilityTexels + 2;
        if ((long)volume.CountX * volume.CountY * tile > MaxAtlasSize || (long)volume.CountZ * tile > MaxAtlasSize)
            throw new ArgumentException(
                $"Probe volume {volume.CountX}x{volume.CountY}x{volume.CountZ} needs a {volume.CountX * volume.CountY * tile}x{volume.CountZ * tile} visibility atlas, above the {MaxAtlasSize} texture limit.", nameof(gi));
        if (!Finite(volume.Origin) || !Finite(volume.Spacing))
            throw new ArgumentException("Probe volume origin and spacing must be finite.", nameof(gi));
        if (volume.Spacing.X <= 0f || volume.Spacing.Y <= 0f || volume.Spacing.Z <= 0f)
            throw new ArgumentException($"Probe volume spacing {volume.Spacing} must be positive on every axis.", nameof(gi));
    }

    private void EnsureVolume(PbrProbeVolume fit, PbrGi gi)
    {
        var reset = _grid.SetVolume(fit, gi.Scrolling);
        _fitted = _grid.Volume;
        _probeCount = _grid.Count;
        if (reset)
        {
            EnsureAtlases(fit.CountX, fit.CountY, fit.CountZ);
            EnsureStateBuffers(_probeCount);
            EnsureUpdateBuffer(_probeCount);
            _scheduler.Reset(_probeCount);
            ResetStates();
            _carryCount = 0;
            _framesSinceReset = 0;
        }
        else
        {
            foreach (var probe in _grid.ResetIndices) ResetProbe(probe);
        }
    }

    /// <summary>Invalidates resident probe history and classification around changed world-space bounds.</summary>
    /// <remarks>Call on the rendering thread when streaming or replacing geometry or changing lighting;
    /// bounds are expanded by one probe cell to include neighboring interpolation samples.</remarks>
    public void Invalidate(Aabb bounds)
    {
        if (bounds.IsEmpty) return;
        if (!Finite(bounds.Min) || !Finite(bounds.Max))
            throw new ArgumentException("Probe invalidation bounds must be finite.", nameof(bounds));
        _invalidations.Add(bounds);
    }

    private void ApplyInvalidations()
    {
        foreach (var bounds in _invalidations)
        {
            var min = bounds.Min - _fitted!.Spacing;
            var max = bounds.Max + _fitted.Spacing;
            for (var probe = 0; probe < _probeCount; probe++)
            {
                var position = _grid.Position(probe);
                if (position.X >= min.X && position.Y >= min.Y && position.Z >= min.Z
                    && position.X <= max.X && position.Y <= max.Y && position.Z <= max.Z) ResetProbe(probe);
            }
        }
        _invalidations.Clear();
    }

    private void ResetProbe(int probe)
    {
        _scheduler.Invalidate(probe);
        var state = new Vector4(0f, 0f, 0f, -1f);
        foreach (var buffer in _stateBuffers)
            _ctx.Renderer.UpdateBuffer<Vector4>(buffer, (ulong)probe * 16, MemoryMarshal.CreateReadOnlySpan(ref state, 1));
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private void FillVolume(PbrProbeVolume fit, PbrGi gi, int rays, int windowCount)
    {
        var irradianceWidth = fit.CountX * fit.CountY * (IrradianceTexels + 2);
        var irradianceHeight = fit.CountZ * (IrradianceTexels + 2);
        var visibilityWidth = fit.CountX * fit.CountY * (VisibilityTexels + 2);
        var visibilityHeight = fit.CountZ * (VisibilityTexels + 2);
        // Rays reach a probe and a half past its neighbours: far enough to see the walls around a
        // cell, short enough that a visibility texel's variance stays meaningful.
        var maxDistance = 1.5f * fit.Spacing.Length();
        _volume = new ProbeVolumeGpu
        {
            Origin = new Vector4(fit.Origin, 1f),
            Spacing = new Vector4(fit.Spacing, maxDistance),
            Counts = new Int4 { X = fit.CountX, Y = fit.CountY, Z = fit.CountZ, W = _probeCount },
            Atlas = new Vector4(IrradianceTexels, VisibilityTexels, 1f / irradianceWidth, 1f / irradianceHeight),
            Atlas2 = new Vector4(1f / visibilityWidth, 1f / visibilityHeight, MathF.Max(gi.NormalBias, 0f), MathF.Max(gi.ViewBias, 0f)),
            Params = new Vector4(MathF.Max(gi.Intensity, 0f), rays, WarmUpHysteresis(gi.Hysteresis), 0f),
            Rotation = RandomRotation(),
            Window = new Int4 { Y = windowCount },
            Scroll = new Int4 { X = _grid.Scroll.X, Y = _grid.Scroll.Y, Z = _grid.Scroll.Z },
        };
    }

    /// <summary>The configured hysteresis, ramped up from zero over the frames after a reset:
    /// indirect light converges one bounce per frame, and at 0.97 a closed room takes seconds to
    /// fill in from black. Averaging the first frames lightly instead reaches the steady state
    /// in a dozen frames, trading some noise nobody sees under a black-to-lit fade.</summary>
    private float WarmUpHysteresis(float configured)
    {
        var target = Math.Clamp(configured, 0f, 0.999f);
        // Half a frame's weight per frame of age: 0.9 after eighteen frames, 0.97 after sixty.
        var ramp = 1f - 1f / (_framesSinceReset * 0.5f + 1f);
        return MathF.Min(target, ramp);
    }

    // Shoemake's uniform random unit quaternion.
    private Vector4 RandomRotation()
    {
        var u1 = (float)_random.NextDouble();
        var u2 = (float)_random.NextDouble() * MathF.Tau;
        var u3 = (float)_random.NextDouble() * MathF.Tau;
        var a = MathF.Sqrt(1f - u1);
        var b = MathF.Sqrt(u1);
        return new Vector4(a * MathF.Sin(u2), a * MathF.Cos(u2), b * MathF.Sin(u3), b * MathF.Cos(u3));
    }

    private void UploadVolume()
    {
        _ctx.Renderer.UpdateBuffer<ProbeVolumeGpu>(VolumeBuffer, 0, MemoryMarshal.CreateReadOnlySpan(ref _volume, 1));
    }

    private void EnsureAtlases(int countX, int countY, int countZ)
    {
        for (var i = 0; i < 2; i++)
        {
            _ctx.Targets.Ensure(PbrTargets.GiIrradiance[i], Atlas(countX * countY * (IrradianceTexels + 2), countZ * (IrradianceTexels + 2)));
            _ctx.Targets.Ensure(PbrTargets.GiVisibility[i], Atlas(countX * countY * (VisibilityTexels + 2), countZ * (VisibilityTexels + 2)));
        }

        static TextureDesc Atlas(int width, int height) => new(
            null, (uint)width, (uint)height, 1, 1, 1, TextureDimension.D2, TextureFormat.Rgba16Float,
            TextureUsage.StorageBinding | TextureUsage.TextureBinding);
    }

    private void EnsureStateBuffers(int probes)
    {
        var needed = Math.Max(probes, 1);
        if (_stateBuffers[0].IsValid && needed <= _stateCapacity) return;
        for (var i = 0; i < 2; i++)
        {
            if (_stateBuffers[i].IsValid) _ctx.Renderer.DestroyBuffer(_stateBuffers[i]);
            // CopySrc so a test can read the states back: the one way to see a probe's relocation.
            _stateBuffers[i] = _ctx.Renderer.CreateBuffer(new BufferDesc(
                $"PbrProbeStates{i}", (ulong)needed * 16, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc));
        }
        _stateCapacity = needed;
    }

    /// <summary>Every probe awaiting its first trace, on its grid point, in both buffers.</summary>
    private void ResetStates()
    {
        var states = new Vector4[_stateCapacity];
        Array.Fill(states, new Vector4(0f, 0f, 0f, -1f));
        for (var i = 0; i < 2; i++) _ctx.Renderer.UpdateBuffer<Vector4>(_stateBuffers[i], 0, states);
    }

    private void EnsureUpdateBuffer(int count)
    {
        if (_updateBuffer.IsValid && count <= _updateCapacity) return;
        if (_updateBuffer.IsValid) _ctx.Renderer.DestroyBuffer(_updateBuffer);
        _updateCapacity = count;
        _updateBuffer = _ctx.Renderer.CreateBuffer(new BufferDesc(
            "PbrProbeUpdates", (ulong)count * 8, BufferUsage.Storage | BufferUsage.CopyDst));
    }

    private void EnsureRayBuffer(int rays)
    {
        var needed = Math.Max(rays, 1);
        if (_rayBuffer.IsValid && needed <= _rayCapacity) return;
        if (_rayBuffer.IsValid) _ctx.Renderer.DestroyBuffer(_rayBuffer);
        _rayCapacity = Math.Max(needed, _rayCapacity + _rayCapacity / 2);
        _rayBuffer = _ctx.Renderer.CreateBuffer(new BufferDesc(
            "PbrProbeRays", (ulong)_rayCapacity * 16, BufferUsage.Storage | BufferUsage.CopyDst));
    }

    private void EnsureCarryBuffer(int probes)
    {
        if (probes <= _carryProbeIndices.Length) return;
        if (_carryBuffer.IsValid) _ctx.Renderer.DestroyBuffer(_carryBuffer);
        _carryProbeIndices = new int[probes];
        _carryBuffer = _ctx.Renderer.CreateBuffer(new BufferDesc(
            "PbrProbeCarryIndices", (ulong)probes * sizeof(int), BufferUsage.Storage | BufferUsage.CopyDst));
    }

    public void Dispose()
    {
        var renderer = _ctx.Renderer;
        renderer.DestroyComputePipeline(_tracePipeline);
        renderer.DestroyComputePipeline(_blendIrradiancePipeline);
        renderer.DestroyComputePipeline(_blendVisibilityPipeline);
        renderer.DestroyComputePipeline(_updatePipeline);
        renderer.DestroyComputePipeline(_carryIrradiancePipeline);
        renderer.DestroyComputePipeline(_carryVisibilityPipeline);
        if (_carryBuffer.IsValid) renderer.DestroyBuffer(_carryBuffer);
        renderer.DestroyBuffer(VolumeBuffer);
        renderer.DestroyBuffer(_lightTreeBuffer);
        renderer.DestroyBuffer(_updateBuffer);
        if (_rayBuffer.IsValid) renderer.DestroyBuffer(_rayBuffer);
        foreach (var buffer in _stateBuffers)
            if (buffer.IsValid) renderer.DestroyBuffer(buffer);
        renderer.DestroySampler(Sampler);
    }
}
