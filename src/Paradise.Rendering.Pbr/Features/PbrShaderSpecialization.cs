using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr;

/// <summary>Specializes optional shading paths from the same frame-frozen switches as their producers.</summary>
internal static class PbrShaderSpecialization
{
    internal const uint All = 32767;
    internal const uint SoftShadows = 16384;
    internal const uint TextureBits = 15872;
    internal const string OverrideKey = "19000";

    internal static uint Resolve(RenderPipeline pipeline, PbrScene scene)
    {
        uint mask = 256 | TextureBits; // Material recipes remain live unless a material-specific variant proves them absent.
        if (pipeline.IsEnabled(PbrFeatures.Shadows.Id))
        {
            mask |= 1;
            // A hard-only scene must compile out PCSS, not merely skip it through a uniform branch.
            for (var i = 0; i < Math.Min(scene.Lights.Count, FrameUniformsGpu.MaxSceneLights); i++)
            {
                if (!scene.Lights[i].CastsShadows || !scene.Lights[i].SoftShadows) continue;
                mask |= SoftShadows;
                break;
            }
        }
        if (pipeline.IsEnabled(PbrFeatures.ContactShadows.Id)) mask |= 2;
        if (pipeline.IsEnabled(PbrFeatures.Prepass.Id) && scene.Ssao.Enabled) mask |= 4;
        if (pipeline.IsEnabled(PbrFeatures.RayTracedAo.Id) && scene.RayTracedAo.Enabled) mask |= 8;
        if (pipeline.IsEnabled(PbrFeatures.ScreenSpaceReflection.Id)) mask |= 16;
        if (pipeline.IsEnabled(PbrFeatures.GlobalIllumination.Id)) mask |= 32;
        if (pipeline.IsEnabled(PbrFeatures.Decals.Id)) mask |= 64;
        if (pipeline.IsEnabled(PbrFeatures.LightCulling.Id))
        {
            for (var i = 0; i < Math.Min(scene.Lights.Count, FrameUniformsGpu.MaxSceneLights); i++)
            {
                if (scene.Lights[i].Type == PbrLightType.Directional) continue;
                mask |= 128;
                break;
            }
        }
        return mask;
    }

    internal static ShaderProgramDesc Apply(ShaderProgramDesc program, uint features)
    {
        // Older external shaders remain usable with their original dynamic path. Never supply an
        // unknown override: WebGPU validates keys against the chosen module, not the engine layout.
        ShaderModuleDesc[]? modules = null;
        for (var i = 0; i < program.Modules.Length; i++)
        {
            var module = program.Modules[i];
            if ((module.Stage & ShaderStage.Fragment) == 0
                || !module.Wgsl.Contains("@id(19000) override", StringComparison.Ordinal)) continue;
            modules ??= (ShaderModuleDesc[])program.Modules.Clone();
            var constants = module.Constants.ToArray().Where(static value => value.Key != OverrideKey).ToList();
            constants.Add(new ShaderConstant(OverrideKey, features));
            modules[i] = module with { Constants = constants.ToArray() };
        }
        return modules is null ? program : program with { Modules = modules };
    }
}
