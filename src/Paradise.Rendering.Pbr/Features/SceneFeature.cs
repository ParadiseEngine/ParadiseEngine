using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paradise.Features;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Renders sky, opaque and blended geometry into linear HDR targets.</summary>
/// <remarks>Owns frame resources and reads shadow, SSAO and Forward+ results. SceneColorCapture
/// moves blended geometry to the Transparent stage after capture.</remarks>
public sealed partial class SceneFeature : IRenderFeature
{
    private readonly PbrContext _ctx;
    private readonly ShadowFeature _shadows;
    private readonly PrepassFeature _prepass;
    private readonly ProbeGiFeature _gi;
    private readonly LightCullingFeature _lightCulling;
    private readonly BindGroupLayoutDesc _frameGroupLayout;
    private readonly BindGroupLayoutDesc _lightingGroupLayout;
    private readonly HashSet<int> _materialsSeen = [];
    private float _specularAaVariance;
    private float _specularAaClamp;

    internal SceneFeature(PbrContext ctx, ShadowFeature shadows, PrepassFeature prepass, ProbeGiFeature gi,
        LightCullingFeature lightCulling, float specularAaVariance, float specularAaClamp)
    {
        _ctx = ctx;
        _shadows = shadows;
        _prepass = prepass;
        _gi = gi;
        _lightCulling = lightCulling;
        _specularAaVariance = specularAaVariance;
        _specularAaClamp = specularAaClamp;

        _frameGroupLayout = ctx.Programs.Group(1);
        _lightingGroupLayout = ctx.Programs.Group(3); // pre-pass + SSAO uniforms + sky-specular LUT + DFG

        EnsureTargets();
        CreateSky();
        CreateLuts();
    }

    public FeatureDefinition Definition => PbrFeatures.Scene;
    public FrameRequirements Requires => FrameRequirements.None;

    /// <summary>Specular anti-aliasing tuning (RenderSettingsData.SpecularAaVariance/Clamp).</summary>
    public void SetSpecularAa(float variance, float clamp)
    {
        _specularAaVariance = variance;
        _specularAaClamp = clamp;
    }

    public void Resize(uint width, uint height) => EnsureTargets();

    private void EnsureTargets()
    {
        // Depth is TextureBinding too, so a capture can pack the opaque depth into the scene
        // color's alpha (read as unfilterable float; never bound while also being written).
        _ctx.Targets.Ensure(PbrTargets.Depth, _ctx.FrameTarget(TextureFormat.Depth32Float));
        _ctx.Targets.Ensure(PbrTargets.Hdr, _ctx.FrameTarget(PbrTargets.HdrFormat));
    }

    public void Setup(in FrameContext frame)
    {
        var scene = _ctx.Scene;
        UploadFrameUniforms(scene);
        if (scene.HasSkyBackground) UploadSky(scene);

        var graph = frame.Graph;
        var hdr = graph.Texture(PbrTargets.Hdr);
        frame.Blackboard.Publish(PbrResults.SceneColor, hdr);
        var depth = graph.Texture(PbrTargets.Depth);
        var shadows = graph.Texture(PbrTargets.ShadowArray);
        // The one place the pre-pass is switched off from this side: bind black instead of its
        // targets and, unless another feature reads them, the pass that produces them is unreachable.
        var prepassNormal = frame.Blackboard.GetOrDefault(PbrResults.PrepassNormal, frame.Black);
        var prepassDepth = frame.Blackboard.GetOrDefault(PbrResults.PrepassDepth, frame.Black);
        var rtao = frame.Blackboard.GetOrDefault(PbrResults.RayTracedAo, frame.Black);
        var ssr = frame.Blackboard.GetOrDefault(PbrResults.SsrReflection, frame.Black);
        var giIrradiance = frame.Blackboard.GetOrDefault(PbrResults.GiIrradiance, frame.Black);
        var giVisibility = frame.Blackboard.GetOrDefault(PbrResults.GiVisibility, frame.Black);
        var split = (frame.Requirements & FrameRequirements.SceneColorCapture) != 0;

        // The main HDR pass holds sky + opaque, and the blend bucket too UNLESS capture split it
        // out: a blend material that samples what is behind it needs the opaque half resolved into
        // a sampleable texture first, which cannot happen mid-pass.
        var main = graph.AddRasterPass(split ? "Main.Opaque" : "Main", RenderPassEvent.Opaque)
            .Color(0, hdr, LoadOp.Clear, clear: scene.ClearColor)
            .Depth(depth, LoadOp.Clear, clear: 1f);
        DeclareGroups(main, shadows, prepassNormal, prepassDepth, rtao, ssr, giIrradiance, giVisibility);
        DeclareMaterialReads(graph, main, _ctx.Opaque);
        if (!split) DeclareMaterialReads(graph, main, _ctx.Blend);
        main.Record(this, split ? RecordOpaque : RecordAll);

        if (split)
        {
            // Load/Load back onto the same HDR + depth: blend pipelines already read-not-write
            // depth, so this is exactly the state they expect mid-pass.
            var blend = graph.AddRasterPass("Main.Blend", RenderPassEvent.Transparent)
                .Color(0, hdr, LoadOp.Load, clear: scene.ClearColor)
                .Depth(depth, LoadOp.Load, clear: 1f);
            DeclareGroups(blend, shadows, prepassNormal, prepassDepth, rtao, ssr, giIrradiance, giVisibility);
            DeclareMaterialReads(graph, blend, _ctx.Blend);
            blend.Record(this, RecordBlend);
        }
    }

    /// <summary>The frame targets the bucket's materials follow, declared as reads of the pass
    /// that draws them. Group 2 is the material's own, which the graph does not build, so these
    /// are the reads that cannot be derived from a binding — but they are derived from the
    /// material cache's records rather than written by hand, so a material that follows a target
    /// nobody thought about is still an edge. A target that does not exist this frame is bound as
    /// black and reads nothing.</summary>
    private void DeclareMaterialReads(FrameGraph graph, FrameGraph.PassBuilder pass, List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> bucket)
    {
        _materialsSeen.Clear();
        var materials = _ctx.Materials;
        var targets = _ctx.Targets;
        foreach (var (_, primitive, _) in bucket)
        {
            if (!_materialsSeen.Add(primitive.MaterialId)) continue;
            foreach (var target in materials.TargetsOf(primitive.MaterialId))
                if (targets.Contains(target)) pass.Reads(graph.Texture(target));
        }
    }

    /// <summary>Groups 1 and 3 of the main program, shared by every pass that draws scene geometry.
    /// The frame group's buffers grow, so their sizes are read here each frame and a grown buffer
    /// is a different group by content.</summary>
    private void DeclareGroups(FrameGraph.PassBuilder pass, GraphTexture shadows, GraphTexture prepassNormal, GraphTexture prepassDepth,
        GraphTexture rtao, GraphTexture ssr, GraphTexture giIrradiance, GraphTexture giVisibility)
    {
        pass.BindGroup(1, "PbrFrameGroup", _frameGroupLayout,
        [
            GraphBinding.Buffer(0, _ctx.FrameUniformBuffer, 0, PbrContext.FrameUniformBytes),
            GraphBinding.TextureArray(1, shadows),
            GraphBinding.Sampler(2, _shadows.Sampler),
            GraphBinding.Buffer(3, _lightCulling.ClusterBuffer, 0, _lightCulling.ClusterBufferBytes),
            GraphBinding.Buffer(4, _ctx.JointBuffer, 0, _ctx.JointBufferBytes),
        ]);
        pass.BindGroup(3, "PbrLightingGroup", _lightingGroupLayout,
        [
            GraphBinding.Buffer(0, _prepass.SsaoUniformBuffer, 0, (ulong)Unsafe.SizeOf<SsaoUniformsGpu>()),
            GraphBinding.Texture(1, prepassNormal),
            GraphBinding.View(2, _skySpecLutView),
            GraphBinding.Sampler(3, _skySpecSampler),
            GraphBinding.View(4, _dfgLutView),
            GraphBinding.Texture(5, prepassDepth),
            GraphBinding.Texture(6, rtao),
            GraphBinding.Texture(7, giIrradiance),
            GraphBinding.Texture(8, giVisibility),
            GraphBinding.Buffer(9, _gi.VolumeBuffer, 0, ProbeGiFeature.VolumeBufferBytes),
            GraphBinding.Buffer(10, _gi.ShadingStateBuffer, 0, _gi.StateBufferBytes),
            GraphBinding.Sampler(11, _gi.Sampler),
            GraphBinding.Texture(12, ssr),
        ]);
    }

    private static void RecordAll(SceneFeature self, ref PassRecording pass, int _)
    {
        self.RecordSky(ref pass);
        self.EncodeBucket(ref pass, self._ctx.Opaque, BlendMode.Opaque);
        self.EncodeBucket(ref pass, self._ctx.Blend, BlendMode.AlphaBlend);
    }

    private static void RecordOpaque(SceneFeature self, ref PassRecording pass, int _)
    {
        self.RecordSky(ref pass);
        self.EncodeBucket(ref pass, self._ctx.Opaque, BlendMode.Opaque);
    }

    // The blend bucket continues the SAME draw ring the opaque bucket filled.
    private static void RecordBlend(SceneFeature self, ref PassRecording pass, int _) =>
        self.EncodeBucket(ref pass, self._ctx.Blend, BlendMode.AlphaBlend);

    private void EncodeBucket(
        ref PassRecording pass,
        List<(PbrInstance Instance, PbrPrimitive Primitive, float ViewDepth)> bucket,
        BlendMode blend)
    {
        if (bucket.Count == 0) return;

        // Pipeline is chosen per draw (rigid vs skinned need different vertex layouts, and a
        // material may select a custom program) but only re-set on a change, so an all-rigid
        // stock-material bucket still issues exactly one SetPipeline. Bind groups persist across
        // SetPipeline within a pass — every pipeline shares the built-in groups 0/1/3.
        var skinnedActive = (bool?)null;
        var programActive = -1;
        ref var encoder = ref pass.Encoder;
        pass.SetBindGroup(1);
        pass.SetBindGroup(3);

        var ctx = _ctx;
        var materials = ctx.Materials;
        foreach (var (instance, primitive, _) in bucket)
        {
            var skinned = primitive.Skinned && instance.JointOffset >= 0;
            var programId = materials.GetProgramId(primitive.MaterialId);
            if (skinned && programId != 0)
                throw new InvalidOperationException(
                    $"Material program {programId} is rigid-only, but it is assigned to a skinned primitive. " +
                    "Custom material programs do not support the skinned vertex path (v1).");
            if (skinnedActive != skinned || programActive != programId)
            {
                encoder.SetPipeline(skinned ? ctx.Programs.GetSkinned(blend) : ctx.Programs.Get(programId, blend));
                skinnedActive = skinned;
                programActive = programId;
            }

            var uniforms = new DrawUniformsGpu
            {
                Mvp = instance.Model * ctx.ViewProjection,
                Model = instance.Model,
                NormalMatrix = PbrMath.NormalMatrix(instance.Model),
                // y carries the joint palette base for skinned draws; the lanes beside the
                // highlight weight were already spare, so this needs no uniform layout change.
                Highlight = new Vector4(instance.Highlight, skinned ? instance.JointOffset : 0f,
                    instance.GiMode == PbrGiMode.Disabled ? 1f : 0f, 0f),
            };
            var slot = ctx.DrawIndex;
            MemoryMarshal.Write(ctx.DrawStaging.AsSpan(slot * (int)ctx.DrawStride), in uniforms);

            encoder.SetBindGroup(0, ctx.DrawGroup, dynamicOffset: (uint)(slot * ctx.DrawStride));
            encoder.SetBindGroup(2, materials.GetBindGroup(primitive.MaterialId));
            encoder.SetVertexBuffer(0, primitive.VertexBuffer, 0, primitive.VertexByteLength);
            encoder.SetIndexBuffer(primitive.IndexBuffer, IndexFormat.Uint32, 0, primitive.IndexByteLength);
            encoder.DrawIndexed(new DrawIndexedCommand(primitive.IndexCount, 1, 0, 0, 0));
            ctx.DrawIndex++;
        }
    }

    public void Dispose()
    {
        DisposeSky();
        DisposeLuts();
    }
}
