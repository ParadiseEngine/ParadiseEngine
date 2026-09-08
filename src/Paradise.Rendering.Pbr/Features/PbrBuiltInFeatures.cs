using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Registers the engine's built-in render features in one place.</summary>
/// <remarks>Games add features through RenderPipeline.Add at PbrFeatureOrder slots. Cross-feature
/// textures travel through the blackboard; shared plans and buffers use explicit constructor
/// dependencies.</remarks>
internal static class PbrBuiltInFeatures
{
    public static void AddTo(RenderPipeline pipeline, PbrContext ctx, float specularAaVariance, float specularAaClamp)
    {
        var frustum = new FrustumCullingFeature(ctx);
        var occlusion = new OcclusionCullingFeature(ctx, frustum);
        var shadows = new ShadowFeature(ctx);
        var ssr = new ScreenSpaceReflectionFeature(ctx);
        var prepass = new PrepassFeature(ctx, frustum);
        var gi = new ProbeGiFeature(ctx, shadows);
        var lightCulling = new LightCullingFeature(ctx);
        var decals = new DecalFeature(ctx);
        var instancing = new InstancingFeature(ctx);
        pipeline
            .Add(frustum, PbrFeatureOrder.FrustumCulling)
            .Add(shadows, PbrFeatureOrder.Shadows)
            .Add(prepass, PbrFeatureOrder.Prepass)
            .Add(occlusion, PbrFeatureOrder.OcclusionCulling)
            .Add(new MotionVectorsFeature(ctx), PbrFeatureOrder.MotionVectors)
            .Add(new ContactShadowFeature(ctx), PbrFeatureOrder.ContactShadows)
            .Add(new RayTracedAoFeature(ctx), PbrFeatureOrder.RayTracedAo)
            .Add(ssr, PbrFeatureOrder.ScreenSpaceReflection)
            .Add(gi, PbrFeatureOrder.GlobalIllumination)
            .Add(lightCulling, PbrFeatureOrder.LightCulling)
            .Add(decals, PbrFeatureOrder.Decals)
            .Add(instancing, PbrFeatureOrder.Instancing)
            .Add(new SceneFeature(ctx, shadows, prepass, gi, lightCulling, frustum, occlusion, instancing, decals, specularAaVariance, specularAaClamp), PbrFeatureOrder.Scene)
            .Add(new SceneColorCaptureFeature(ctx), PbrFeatureOrder.SceneColorCapture)
            .Add(new FogFeature(ctx, shadows), PbrFeatureOrder.Fog)
            .Add(new TemporalAntiAliasingFeature(ctx, pipeline), PbrFeatureOrder.TemporalAntiAliasing)
            .Add(new ExposureFeature(ctx), PbrFeatureOrder.Exposure)
            .Add(new DepthOfFieldFeature(ctx), PbrFeatureOrder.DepthOfField)
            .Add(new MotionBlurFeature(ctx), PbrFeatureOrder.MotionBlur)
            .Add(new BloomFeature(ctx), PbrFeatureOrder.Bloom)
            .Add(new CompositeFeature(ctx), PbrFeatureOrder.Composite)
            .Add(new ColorGradingFeature(ctx), PbrFeatureOrder.ColorGrading)
            .Add(new LensDistortionFeature(ctx), PbrFeatureOrder.LensDistortion)
            .Add(new ChromaticAberrationFeature(ctx), PbrFeatureOrder.ChromaticAberration)
            .Add(new VignetteFeature(ctx), PbrFeatureOrder.Vignette)
            .Add(new FilmGrainFeature(ctx), PbrFeatureOrder.FilmGrain)
            .Add(new SharpeningFeature(ctx), PbrFeatureOrder.Sharpening)
            .Add(new FxaaFeature(ctx), PbrFeatureOrder.AntiAliasing)
            .Add(new PresentationFeature(ctx), PbrFeatureOrder.Presentation);
    }
}
