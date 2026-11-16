// The scene pass: sphere-traces the scene's analytic distance field from a fullscreen triangle
// into linear HDR and device depth. Lighting reads the same field: marched soft shadows,
// short-range ambient occlusion, and ambient light from the sky.
//
// Included by one entry file per combination of two optional parts, each of which the including
// file asks for with a define: SDF_PROBES lights the ambient term from the irradiance probes,
// SDF_REFLECTIONS traces mirror reflections. The renderer picks the program a frame needs, so a
// frame pays only for what it uses. sdfField.slang holds the field and what every pass shares.

#include "sdfField.slang"

struct Varyings
{
    float4 position : SV_Position;
    float2 ndc : TEXCOORD0;
};

struct SceneOutput
{
    float4 color : SV_Target;
    float depth : SV_Depth;
};

[shader("vertex")]
Varyings vertexMain(uint vertexId : SV_VertexID)
{
    // Fullscreen triangle: p in {0,2}, clip = p*2-1 in {-1,3}; clip xy is also the NDC the
    // fragment unprojects.
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.ndc = p * 2.0 - 1.0;
    return output;
}

float3 shadeSurface(float3 p, float3 normal, float3 view, SdfSurface surface, float bias)
{
    float3 diffuseColor = surface.albedo * (1.0 - surface.metallic);
    float3 f0 = lerp(float3(0.04, 0.04, 0.04), surface.albedo, surface.metallic);
    float roughness = surface.roughness;
    float nDotV = max(dot(normal, view), 1e-4);
    float ambientIntensity = sdfFrame.trace.w;
#ifdef SDF_PROBES
    float probeGain = sdfGi.params.x;
#else
    float probeGain = 0.0;
#endif
#ifdef SDF_REFLECTIONS
    bool traced = roughness <= sdfFrame.reflections.x;
#else
    bool traced = false;
#endif
    bool ambient = ambientIntensity > 0.0 || probeGain > 0.0;

    // Before anything else: occlusion reads the primary ray's selection, which every ray and
    // probe lookup below replaces.
    float occlusion = 1.0;
    if (ambient && sdfFrame.trace.z > 0.0)
        occlusion = lerp(1.0, ambientOcclusion(p, normal), sdfFrame.trace.z);

    float3 color = surface.emissive + sdfLighting(p, normal, view, surface, bias, true, false);
    if (!ambient && !traced) return color;

    // Smooth surfaces show what lies in the mirror direction: the scene where it is traced,
    // otherwise the sky, or under probes their light from that side. Rough ones blur toward
    // the irradiance.
    float3 mirror = reflect(-view, normal);
    float3 irradiance = skyIrradiance(normal) * ambientIntensity;
    float3 sharp = skyColor(mirror) * ambientIntensity;
#ifdef SDF_PROBES
    if (probeGain > 0.0)
    {
        SdfProbeLight probes = sdfSampleProbes(p + normal * bias, normal, sdfGi.params.y > 0.5);
        irradiance = lerp(irradiance, sdfProbeIrradiance(probes, normal) * probeGain, probes.weight);
        sharp = lerp(sharp, sdfProbeRadiance(probes, mirror) * probeGain, probes.weight);
    }
#endif
#ifdef SDF_REFLECTIONS
    if (traced) sharp = sdfTraceReflection(p + normal * bias, mirror);
#endif
    float3 reflection = lerp(sharp, irradiance, roughness);
    float away = 1.0 - nDotV;
    float away2 = away * away;
    float3 fresnel = f0 + (max(float3(1.0 - roughness, 1.0 - roughness, 1.0 - roughness), f0) - f0) * (away2 * away2 * away);
    return color + (diffuseColor * irradiance + fresnel * reflection) * occlusion;
}

[shader("fragment")]
SceneOutput fragmentMain(Varyings input)
{
    float4 nearPoint = mul(sdfFrame.invViewProj, float4(input.ndc, 0.0, 1.0));
    float4 farPoint = mul(sdfFrame.invViewProj, float4(input.ndc, 1.0, 1.0));
    float3 origin = nearPoint.xyz / nearPoint.w;
    float3 span = farPoint.xyz / farPoint.w - origin;
    float spanLength = length(span);
    float3 direction = span / max(spanLength, 1e-6);
    float maxDistance = min(spanLength, sdfFrame.counts.w);
    float epsilon = sdfFrame.trace.x;

    // The selection also serves the samples around the hit point, so it reaches as far as they
    // read. A sample this far from the surface reads occluders as far again: the normal taps sit
    // sqrt(3) thresholds out, the occlusion samples SDF_AO_REACH. The march itself reads planes
    // from where they come within the hit threshold, the rule SdfField.TryRaycast hits by: any
    // later and a ray that starts inside that band, or grazes into it, would miss the plane.
    sdfSelectCandidates(origin, direction, maxDistance,
        2.0 * SDF_AO_REACH + 4.5 * max(epsilon, 1e-4), 4.5 * epsilon, epsilon, epsilon);

    // The hit threshold grows with distance like a pixel's footprint, so far surfaces converge
    // in as few steps as near ones.
    int maxSteps = (int)sdfFrame.counts.z;
    float t = 0.0;
    bool hit = false;
    for (int i = 0; i < maxSteps; i++)
    {
        gRayT = t;
        float clearance = sceneDistance(origin + direction * t);
        if (clearance < epsilon * (1.0 + t))
        {
            hit = true;
            break;
        }
        t = sdfAdvance(t, clearance);
        if (t > maxDistance) break;
    }

    SceneOutput output;
    if (!hit)
    {
        // The sky sits just past the farthest point the ray traced, so it is fogged like a
        // surface clipped there and no seam opens between them when the far plane is close.
        output.color = float4(sdfApplyFog(skyColor(direction), origin, direction, maxDistance), 1.0);
        output.depth = 1.0;
        return output;
    }

    // Samples around the hit point belong to no march: every selected plane takes part.
    gPlanesFrom = 0.0;
    float3 p = origin + direction * t;
    float threshold = epsilon * (1.0 + t);
    float3 normal = sceneNormal(p, max(threshold, 1e-4));
    SdfSurface surface = sceneSurface(p);
    // Lifted past the hit threshold so shadow and occlusion rays do not start inside it.
    float3 color = shadeSurface(p, normal, -direction, surface, 2.0 * threshold + 1e-3);

    float4 clip = mul(sdfFrame.viewProj, float4(p, 1.0));
    output.color = float4(sdfApplyFog(color, origin, direction, t), 1.0);
    output.depth = saturate(clip.z / clip.w);
    return output;
}
