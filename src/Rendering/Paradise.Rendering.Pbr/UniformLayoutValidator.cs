namespace Paradise.Rendering.Pbr;

/// <summary>Validates the PBR program's reflected draw, frame and material blocks against their CPU mirrors.</summary>
/// <remarks>Offsets and sizes are measured on the mirror structs (<see cref="ShaderMirror"/>), so a
/// field that moves in <c>PbrUniforms</c> without moving in the shader fails here.</remarks>
public static class UniformLayoutValidator
{
    /// <summary>Validate the three uniform blocks every PBR material program declares. Call once
    /// per program; throws <see cref="InvalidOperationException"/> naming the first divergence.</summary>
    public static void Validate(ShaderProgramDesc program)
    {
        ValidateDraw(program);

        var frame = default(FrameUniformsGpu);
        ShaderMirror.Uniform(program, "frame", ref frame)
            .Field("cameraPos", ref frame.CameraPos)
            .Field("ambient", ref frame.Ambient)
            .Field("ambientEquator", ref frame.AmbientEquator)
            .Field("ambientGround", ref frame.AmbientGround)
            .Field("aaSettings", ref frame.AaSettings)
            .Field("ambientSh", ref frame.AmbientSh)
            .Field("cameraForward", ref frame.CameraForward)
            .Field("clusterParams", ref frame.ClusterParams)
            .Field("sceneLights", ref frame.Lights)
            .Field("shadowSettings", ref frame.ShadowSettings)
            .Field("sceneLightShadowMatrices", ref frame.SceneLightShadowMatrices)
            .Field("time", ref frame.Time)
            .Field("shadowFilter", ref frame.ShadowFilter)
            .Field("invViewProj", ref frame.InvViewProj)
            .Field("shadowViewRects", ref frame.ShadowViewRects)
            .Field("shadowViewData", ref frame.ShadowViewData)
            .Field("shadowViewDepth", ref frame.ShadowViewDepth)
            .Field("viewProj", ref frame.ViewProj)
            .Field("contactShadowSettings", ref frame.ContactShadowSettings)
            .End();

        var material = default(MaterialUniformsGpu);
        ShaderMirror.Uniform(program, "material", ref material)
            .Field("baseColorFactor", ref material.BaseColorFactor)
            .Field("metallicFactor", ref material.MetallicFactor)
            .Field("roughnessFactor", ref material.RoughnessFactor)
            .Field("normalScale", ref material.NormalScale)
            .Field("occlusionStrength", ref material.OcclusionStrength)
            .Field("emissiveFactor", ref material.EmissiveFactor)
            .Field("uvOffsetScale", ref material.UvOffsetScale)
            .Field("uvRotation", ref material.UvRotation)
            .Field("procColorA", ref material.ProcColorA)
            .Field("procColorB", ref material.ProcColorB)
            .Field("procParams", ref material.ProcParams)
            .End();
    }

    /// <summary>Validates the <c>draw</c> block of a program that draws scene geometry.</summary>
    internal static void ValidateDraw(ShaderProgramDesc program)
    {
        var draw = default(DrawUniformsGpu);
        DrawFields(ShaderMirror.Uniform(program, "draw", ref draw), ref draw);
    }

    /// <summary>Validates a storage buffer of per-instance draw records.</summary>
    internal static void ValidateInstanceDraws(ShaderProgramDesc program, string bufferName)
    {
        var draw = default(DrawUniformsGpu);
        DrawFields(ShaderMirror.Storage(program, bufferName, ref draw), ref draw);
    }

    private static void DrawFields(ShaderMirror<DrawUniformsGpu> mirror, ref DrawUniformsGpu draw) => mirror
        .Field("mvp", ref draw.Mvp)
        .Field("model", ref draw.Model)
        .Field("normalMatrix", ref draw.NormalMatrix)
        .Field("highlight", ref draw.Highlight)
        .End();
}
