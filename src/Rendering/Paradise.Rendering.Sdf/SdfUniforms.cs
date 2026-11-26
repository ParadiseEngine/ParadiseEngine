using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Sdf;

// CPU layouts match the shaders' (Common/sdfField.slang for the scene's), checked against Slang reflection at renderer initialization.
// Matrix4x4 bytes are uploaded unchanged: row-major numerics data read column-major supplies the
// transpose that the shader's mul(M, v) needs.

/// <summary>Mirror of sdfField.slang <c>SdfShape</c> (160 B, storage-buffer stride 160).</summary>
[StructLayout(LayoutKind.Sequential, Size = 160)]
public struct SdfShapeGpu
{
    public Matrix4x4 WorldToLocal;
    public Vector4 Size;             // kind-specific local dimensions
    public Vector4 Params;           // x kind, y operation, z smoothness, w uniform scale
    public Vector4 Bounds;           // xyz world center, w conservative world radius
    public Vector4 AlbedoRoughness;  // rgb linear albedo, a roughness
    public Vector4 EmissiveMetallic; // rgb linear emission, a metallic
    public Vector4 Cull;             // x ray-selection margin past the bounds, y 1 = always selected
}

/// <summary>Mirror of sdfField.slang <c>SdfLight</c> (64 B, array stride 64).</summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct SdfLightGpu
{
    public Vector4 PositionType;   // xyz position, w: 0 directional / 1 point / 2 spot
    public Vector4 DirectionRange; // xyz surface-to-light, a spot's reversed axis, w range (0 = unbounded)
    public Vector4 ColorShadow;    // rgb linear color × intensity, w: 1 casts shadows
    public Vector4 Spot;           // x cosine of the outer cone angle, y of the inner
}

[InlineArray(SdfRenderer.MaxLights)]
public struct SdfLightArray
{
    private SdfLightGpu _element0;
}

/// <summary>Mirror of sdfField.slang <c>SdfFrameUniforms</c> (784 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfFrameUniformsGpu
{
    public Matrix4x4 InvViewProj; // NDC → world, for the eye ray
    public Matrix4x4 ViewProj;    // world → clip, for the hit's device depth
    public Vector4 Counts;        // x shapes, y lights, z max steps, w max distance
    public Vector4 Trace;         // x surface epsilon, y shadow sharpness (0 = off), z AO strength, w ambient intensity
    public Vector4 SkyZenith;
    public Vector4 SkyHorizon;
    public Vector4 SkyGround;
    public Vector4 FogColor;      // rgb linear color, a density (0 = no fog)
    public Vector4 FogShape;      // x height falloff (0 = uniform), y base height, z start distance, w max opacity
    public Vector4 Reflections;   // x roughest surface that traces its reflection (negative = none), y steps, z longest ray, w 1 = shadowed
    public Vector4 CameraPosition; // xyz the eye, or a point on an orthographic camera's plane
    public SdfLightArray Lights;
}

/// <summary>Mirror of sdfGi.slang <c>SdfGiUniforms</c> (128 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfGiUniformsGpu
{
    public Vector4 Origin;        // xyz world position of probe (0, 0, 0), w spacing
    public Vector4 Counts;        // xyz probes along each axis, w probes per atlas row
    public Vector4 Params;        // x intensity (0 = no probes), y 1 = weigh probes by field visibility, z normal bias, w rays per probe
    public Vector4 Update;        // x blend weight of this frame's rays, y longest ray, z clearance at which a probe is fully valid, w bounce gain
    public Matrix4x4 RayRotation; // this frame's rotation of the ray set
}

/// <summary>Mirror of sdfBloom.slang <c>SdfBloomUniforms</c> (16 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfBloomUniformsGpu
{
    public Vector4 Threshold; // x threshold, y knee
}

/// <summary>Mirror of sdfFxaa.slang <c>SdfFxaaUniforms</c> (32 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfFxaaUniformsGpu
{
    public Vector4 Screen;     // xy texel size, zw resolution
    public Vector4 Parameters; // x relative threshold, y minimum threshold, z subpixel quality
}

/// <summary>Mirror of sdfComposite.slang <c>SdfCompositeUniforms</c> (16 B).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SdfCompositeUniformsGpu
{
    public Vector4 Tone; // x tonemap mode, y exposure, z bloom intensity
}

/// <summary>Validates reflected SDF buffers against the CPU mirrors above.</summary>
/// <remarks>Offsets and sizes are measured on the mirror structs (<see cref="ShaderMirror"/>), so a
/// field that moves in one without moving in the shader fails here.</remarks>
public static class SdfUniformLayout
{
    /// <summary>Checks a program that reads the field: a scene pass or the probe trace.</summary>
    /// <param name="program">The reflected program.</param>
    /// <param name="probes">Whether the program lights from the probes, and so declares their uniforms.</param>
    /// <exception cref="InvalidOperationException">Names the first divergence.</exception>
    public static void ValidateScene(ShaderProgramDesc program, bool probes)
    {
        var frame = default(SdfFrameUniformsGpu);
        ShaderMirror.Uniform(program, "sdfFrame", ref frame)
            .Field("invViewProj", ref frame.InvViewProj)
            .Field("viewProj", ref frame.ViewProj)
            .Field("counts", ref frame.Counts)
            .Field("trace", ref frame.Trace)
            .Field("skyZenith", ref frame.SkyZenith)
            .Field("skyHorizon", ref frame.SkyHorizon)
            .Field("skyGround", ref frame.SkyGround)
            .Field("fogColor", ref frame.FogColor)
            .Field("fogShape", ref frame.FogShape)
            .Field("reflections", ref frame.Reflections)
            .Field("cameraPosition", ref frame.CameraPosition)
            .Field("lights", ref frame.Lights)
            .End();

        var shape = default(SdfShapeGpu);
        ShaderMirror.Storage(program, "sdfShapes", ref shape)
            .Field("worldToLocal", ref shape.WorldToLocal)
            .Field("size", ref shape.Size)
            .Field("params", ref shape.Params)
            .Field("bounds", ref shape.Bounds)
            .Field("albedoRoughness", ref shape.AlbedoRoughness)
            .Field("emissiveMetallic", ref shape.EmissiveMetallic)
            .Field("cull", ref shape.Cull)
            .End();

        if (probes) ValidateGi(program);
    }

    /// <summary>Checks the probe uniforms of a program that reads only them.</summary>
    /// <inheritdoc cref="ValidateScene"/>
    public static void ValidateGi(ShaderProgramDesc program)
    {
        var gi = default(SdfGiUniformsGpu);
        ShaderMirror.Uniform(program, "sdfGi", ref gi)
            .Field("origin", ref gi.Origin)
            .Field("counts", ref gi.Counts)
            .Field("params", ref gi.Params)
            .Field("update", ref gi.Update)
            .Field("rayRotation", ref gi.RayRotation)
            .End();
    }

    /// <inheritdoc cref="ValidateScene"/>
    public static void ValidateBloom(ShaderProgramDesc program)
    {
        var bloom = default(SdfBloomUniformsGpu);
        ShaderMirror.Uniform(program, "sdfBloom", ref bloom).Field("threshold", ref bloom.Threshold).End();
    }

    /// <inheritdoc cref="ValidateScene"/>
    public static void ValidateFxaa(ShaderProgramDesc program)
    {
        var fxaa = default(SdfFxaaUniformsGpu);
        ShaderMirror.Uniform(program, "sdfFxaa", ref fxaa)
            .Field("screen", ref fxaa.Screen)
            .Field("parameters", ref fxaa.Parameters)
            .End();
    }

    /// <inheritdoc cref="ValidateScene"/>
    public static void ValidateComposite(ShaderProgramDesc program)
    {
        var composite = default(SdfCompositeUniformsGpu);
        ShaderMirror.Uniform(program, "sdfComposite", ref composite).Field("tone", ref composite.Tone).End();
    }
}
