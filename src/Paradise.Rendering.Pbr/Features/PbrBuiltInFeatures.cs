using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Registers the engine's built-in render features in one place.</summary>
/// <remarks>Games add features through RenderPipeline.Add at PbrFeatureOrder slots. Features exchange frame data and resources
/// through typed blackboard results, without holding references to one another.</remarks>
internal static class PbrBuiltInFeatures
{
    public static void AddTo(RenderPipeline pipeline, PbrContext ctx, float specularAaVariance, float specularAaClamp)
    {
        var frustum = new FrustumCullingFeature(ctx);
        var occlusion = new OcclusionCullingFeature(ctx);
        var instancing = new InstancingFeature(ctx);
        var shadows = new ShadowFeature(ctx);
        var ssr = new ScreenSpaceReflectionFeature(ctx);
        var prepass = new PrepassFeature(ctx);
        var gi = new ProbeGiFeature(ctx);
        var lightCulling = new LightCullingFeature(ctx);
        var decals = new DecalFeature(ctx);
        pipeline
            .Add(new DrawPreparationFeature(ctx), PbrFeatureOrder.DrawPreparation)
            .Add(frustum, PbrFeatureOrder.FrustumCulling)
            .Add(shadows, PbrFeatureOrder.Shadows)
            .Add(prepass, PbrFeatureOrder.Prepass)
            .Add(occlusion, PbrFeatureOrder.OcclusionCulling)
            .Add(new MotionVectorsFeature(ctx), PbrFeatureOrder.MotionVectors)
            .Add(new ContactShadowFeature(ctx), PbrFeatureOrder.ContactShadows)
            .Add(new RayTracedAoFeature(ctx), PbrFeatureOrder.RayTracedAo)
            .Add(ssr, PbrFeatureOrder.ScreenSpaceReflection)
            .Add(lightCulling, PbrFeatureOrder.LightCulling)
            .Add(new FrameLightingFeature(ctx), PbrFeatureOrder.FrameLighting)
            .Add(gi, PbrFeatureOrder.GlobalIllumination)
            .Add(decals, PbrFeatureOrder.Decals)
            .Add(instancing, PbrFeatureOrder.Instancing)
            .Add(new SceneFeature(ctx, specularAaVariance, specularAaClamp), PbrFeatureOrder.Scene)
            .Add(new SceneColorCaptureFeature(ctx), PbrFeatureOrder.SceneColorCapture)
            .Add(new ProbeGiDebugFeature(ctx), PbrFeatureOrder.GiProbes)
            .Add(new FogFeature(ctx), PbrFeatureOrder.Fog)
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
