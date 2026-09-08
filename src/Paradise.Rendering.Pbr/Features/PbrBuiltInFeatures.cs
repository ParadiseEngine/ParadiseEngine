using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>The engine's own features, and the one place that knows the list.
///
/// <para>It is a file of its own so that adding a built-in feature is adding it HERE — not in
/// <see cref="PbrRenderer"/>, whose job is uploading geometry and driving a frame, and which
/// used to have to grow a field, a constructor line and a chained <c>Add</c> for every effect
/// the engine gained. A game adds its features the same way, through
/// <see cref="RenderPipeline.Add"/> at a <see cref="PbrFeatureOrder"/> slot, and needs no change
/// here at all.</para>
///
/// <para>The constructor arguments that are not the context — the scene reads the shadow plan and
/// the froxel grid, the pre-pass reads whether reflections have a history — are the engine
/// features that are genuinely one thing split in two, or that hand over a BUFFER, which the
/// blackboard does not carry. Everything else a feature needs from another feature travels by name
/// on the frame's blackboard, which is what lets any of them be switched off
/// independently.</para></summary>
internal static class PbrBuiltInFeatures
{
    public static void AddTo(RenderPipeline pipeline, PbrContext ctx, float specularAaVariance, float specularAaClamp)
    {
        var shadows = new ShadowFeature(ctx);
        var ssr = new ScreenSpaceReflectionFeature(ctx);
        var prepass = new PrepassFeature(ctx, ssr);
        var gi = new ProbeGiFeature(ctx, shadows);
        var lightCulling = new LightCullingFeature(ctx);
        pipeline
            .Add(shadows, PbrFeatureOrder.Shadows)
            .Add(prepass, PbrFeatureOrder.Prepass)
            .Add(new MotionVectorsFeature(ctx), PbrFeatureOrder.MotionVectors)
            .Add(new RayTracedAoFeature(ctx), PbrFeatureOrder.RayTracedAo)
            .Add(ssr, PbrFeatureOrder.ScreenSpaceReflection)
            .Add(gi, PbrFeatureOrder.GlobalIllumination)
            .Add(lightCulling, PbrFeatureOrder.LightCulling)
            .Add(new SceneFeature(ctx, shadows, prepass, gi, lightCulling, specularAaVariance, specularAaClamp), PbrFeatureOrder.Scene)
            .Add(new SceneColorCaptureFeature(ctx), PbrFeatureOrder.SceneColorCapture)
            .Add(new TemporalAntiAliasingFeature(ctx, pipeline), PbrFeatureOrder.TemporalAntiAliasing)
            .Add(new BloomFeature(ctx), PbrFeatureOrder.Bloom)
            .Add(new CompositeFeature(ctx), PbrFeatureOrder.Composite)
            .Add(new FxaaFeature(ctx), PbrFeatureOrder.AntiAliasing)
            .Add(new PresentationFeature(ctx), PbrFeatureOrder.Presentation);
    }
}
