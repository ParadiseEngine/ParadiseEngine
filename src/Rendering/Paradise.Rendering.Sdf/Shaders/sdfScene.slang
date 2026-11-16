// Sphere-traces the scene's analytic distance field from a fullscreen triangle into linear HDR
// and device depth. Lighting reads the same field: marched soft shadows, short-range ambient
// occlusion and a gradient sky for ambient and reflection. SdfField.cs folds the shapes identically
// on the CPU; SdfUniforms.cs mirrors the layouts below.
//
// Every ray first selects the shapes it can come near, once, and its samples fold only those:
// walking the whole list per sample was most of the frame. Ground planes join a ray's field only
// from where the ray reaches them, so the march flies there instead of creeping down to them.

#include "Common/sdf.slang"

static const int SDF_MAX_LIGHTS = 8;
static const int SDF_SHADOW_STEPS = 64;
static const float SDF_PI = 3.14159265358979;

// A ray near more shapes than this folds the whole list, as if nothing were selected.
static const int SDF_MAX_CANDIDATES = 32;

// Ambient occlusion samples up to this far along the normal and reads occluders as far again.
static const float SDF_AO_REACH = 0.13;

struct SdfShape
{
    float4x4 worldToLocal;
    float4 size;             // kind-specific local dimensions
    float4 params;           // x kind, y operation, z smoothness, w uniform scale
    float4 bounds;           // xyz world center, w conservative world radius
    float4 albedoRoughness;  // rgb linear albedo, a roughness
    float4 emissiveMetallic; // rgb linear emission, a metallic
    float4 cull;             // x: distance past the bounds within which a ray still selects the shape, y: 1 always selected
};

struct SdfLight
{
    float4 positionType;   // xyz position, w: 0 directional / 1 point
    float4 directionRange; // xyz surface-to-light (directional), w range (0 = unbounded)
    float4 colorShadow;    // rgb linear color x intensity, w: 1 casts shadows
};

struct SdfFrameUniforms
{
    float4x4 invViewProj; // NDC -> world, for the eye ray
    float4x4 viewProj;    // world -> clip, for the hit's device depth
    float4 counts;        // x shapes, y lights, z max steps, w max distance
    float4 trace;         // x surface epsilon, y shadow sharpness (0 = off), z AO strength, w ambient intensity
    float4 skyZenith;
    float4 skyHorizon;
    float4 skyGround;
    SdfLight lights[SDF_MAX_LIGHTS];
};

[[vk::binding(0, 0)]] ConstantBuffer<SdfFrameUniforms> sdfFrame;
[[vk::binding(1, 0)]] StructuredBuffer<SdfShape> sdfShapes;

// The current ray's selection, in list order because the fold is ordered. Module scope rather
// than parameters: every field sample reads it, and an array passed by value is copied per call.
static int gCandidateCount = -1; // negative: fold every shape
static int gCandidates[SDF_MAX_CANDIDATES];
// Union planes take part in a sample only at or past gPlanesFrom, where the ray reaches them, or
// between gBandFrom and gBandUntil, where it passes shapes that could blend with them.
static float gPlanesFrom = 0.0;
static float gBandFrom = 0.0;
static float gBandUntil = 0.0;
static float gRayT = 0.0;

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

struct SdfSurface
{
    float3 albedo;
    float roughness;
    float3 emissive;
    float metallic;
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

// A union cannot change the result while the shape's bounding sphere is farther than the
// accumulated distance plus the blend radius; other operations always evaluate.
bool sdfCanSkip(int index, float3 p, float accumulated, float smoothness)
{
    if (sdfShapes[index].params.y > 0.5) return false;
    float4 bounds = sdfShapes[index].bounds;
    return length(p - bounds.xyz) - bounds.w >= accumulated + smoothness;
}

float sdfEvaluate(int index, float3 p)
{
    float3 local = mul(sdfShapes[index].worldToLocal, float4(p, 1.0)).xyz;
    float4 params = sdfShapes[index].params;
    return sdfPrimitive(params.x, local, sdfShapes[index].size) * params.w;
}

// Selects the shapes a ray can read, and where its union planes begin to matter.
//
// A sample at ray parameter t reads surfaces within pad + slope * t of it: the hit threshold of a
// primary ray, the penumbra cone of a shadow ray. A shape is left out when the ray stays farther
// than that, plus the shape's own margin, from its bounding sphere. The margin (cull.x, 2.5 times
// the largest blend radius from the shape onward) is the distance at which a blend with a later
// shape can no longer carry the shape's influence to a surface. Leaving a shape out only ever
// lengthens steps through space the ray never touches the shape in, so the hit is unchanged.
//
// planePad + planeSlope * t is the same reach for the march alone. A union plane is infinite, so
// it is always selected, but a ray above it reads it in only two places: where the ray descends
// to within that reach of it, and beside selected shapes whose margin says a blend can raise the
// ground toward them. The march stops at the start of each rather than stepping past.
void sdfSelectCandidates(float3 origin, float3 direction, float reach, float pad, float slope, float planePad, float planeSlope)
{
    int count = (int)sdfFrame.counts.x;
    // The closest approach of a cone of this slope to a point, relative to that of its axis.
    float narrow = slope < 1.0 ? sqrt(1.0 - slope * slope) : 0.0;
    int selected = 0;
    for (int i = 0; i < count; i++)
    {
        float4 bounds = sdfShapes[i].bounds;
        float4 cull = sdfShapes[i].cull;
        float3 toCenter = bounds.xyz - origin;
        float along = clamp(dot(toCenter, direction), 0.0, reach);
        float away = length(toCenter - direction * along);
        if (cull.y < 0.5 && away * narrow - bounds.w >= cull.x + pad + slope * along) continue;
        if (selected == SDF_MAX_CANDIDATES)
        {
            // Every shape and every plane, with no band left over from an earlier ray.
            gCandidateCount = -1;
            gPlanesFrom = 0.0;
            gBandFrom = SDF_FAR;
            gBandUntil = -SDF_FAR;
            return;
        }
        gCandidates[selected] = i;
        selected++;
    }
    gCandidateCount = selected;

    float planesFrom = SDF_FAR;
    float bandFrom = SDF_FAR;
    float bandUntil = -SDF_FAR;
    int planes = 0;
    float planeBlend = 0.0;
    for (int n = 0; n < selected; n++)
    {
        int i = gCandidates[n];
        float4 params = sdfShapes[i].params;
        float4 cull = sdfShapes[i].cull;
        if (params.x > 4.5)
        {
            planes++;
            planeBlend = max(planeBlend, params.z);
            if (params.y > 0.5) continue;
            float height = mul(sdfShapes[i].worldToLocal, float4(origin, 1.0)).y * params.w;
            float climb = mul(sdfShapes[i].worldToLocal, float4(direction, 0.0)).y * params.w;
            // height + climb * t falls to planePad + planeSlope * t at gap / closing.
            float gap = height - planePad;
            float closing = planeSlope - climb;
            if (gap <= 0.0) planesFrom = 0.0;
            else if (closing > 1e-6) planesFrom = min(planesFrom, gap / closing);
            continue;
        }
        // No margin: neither this shape nor any after it blends, so the ground beside it is flat.
        if (cull.x <= 0.0) continue;
        float4 bounds = sdfShapes[i].bounds;
        float3 toCenter = bounds.xyz - origin;
        float along = clamp(dot(toCenter, direction), 0.0, reach);
        float away = length(toCenter - direction * along);
        // The stretch of the ray inside the sphere it was selected by, widened as the reach grows across it.
        float inner = bounds.w + cull.x + pad;
        float radius = inner + slope * (along + inner);
        float halfChord = sqrt(max(radius * radius - away * away, 0.0));
        bandFrom = min(bandFrom, along - halfChord);
        bandUntil = max(bandUntil, along + halfChord);
    }
    // A blending plane meets the others along lines, not beside bounds: they all take part throughout.
    if (planes > 1 && planeBlend > 0.0) planesFrom = 0.0;
    gPlanesFrom = planesFrom;
    gBandFrom = bandFrom;
    gBandUntil = bandUntil;
}

// Whether the fold leaves this shape out of the current sample: a union plane the ray is neither
// at nor beside a blending shape of, or a union too far to change the accumulated distance.
bool sdfLeftOut(int index, float4 params, float3 p, float accumulated)
{
    if (params.x > 4.5 && params.y < 0.5 && gRayT < gPlanesFrom && (gRayT < gBandFrom || gRayT > gBandUntil)) return true;
    return sdfCanSkip(index, p, accumulated, params.z);
}

float sceneDistance(float3 p)
{
    float accumulated = SDF_FAR;
    bool every = gCandidateCount < 0;
    int count = every ? (int)sdfFrame.counts.x : gCandidateCount;
    for (int n = 0; n < count; n++)
    {
        int i = every ? n : gCandidates[n];
        float4 params = sdfShapes[i].params;
        if (sdfLeftOut(i, params, p, accumulated)) continue;
        float weight;
        accumulated = sdfCombine(params.y, accumulated, sdfEvaluate(i, p), params.z, weight);
    }
    return accumulated;
}

SdfSurface sceneSurface(float3 p)
{
    float accumulated = SDF_FAR;
    float4 albedoRoughness = float4(0.0, 0.0, 0.0, 0.0);
    float4 emissiveMetallic = float4(0.0, 0.0, 0.0, 0.0);
    bool every = gCandidateCount < 0;
    int count = every ? (int)sdfFrame.counts.x : gCandidateCount;
    for (int n = 0; n < count; n++)
    {
        int i = every ? n : gCandidates[n];
        float4 params = sdfShapes[i].params;
        if (sdfLeftOut(i, params, p, accumulated)) continue;
        float weight;
        accumulated = sdfCombine(params.y, accumulated, sdfEvaluate(i, p), params.z, weight);
        albedoRoughness = albedoRoughness * (1.0 - weight) + sdfShapes[i].albedoRoughness * weight;
        emissiveMetallic = emissiveMetallic * (1.0 - weight) + sdfShapes[i].emissiveMetallic * weight;
    }
    SdfSurface surface;
    surface.albedo = albedoRoughness.rgb;
    surface.roughness = albedoRoughness.a;
    surface.emissive = emissiveMetallic.rgb;
    surface.metallic = emissiveMetallic.a;
    return surface;
}

// Four tetrahedral taps: the cheapest central-difference gradient.
float3 sceneNormal(float3 p, float epsilon)
{
    const float3 a = float3(1.0, -1.0, -1.0);
    const float3 b = float3(-1.0, -1.0, 1.0);
    const float3 c = float3(-1.0, 1.0, -1.0);
    const float3 d = float3(1.0, 1.0, 1.0);
    float3 gradient = a * sceneDistance(p + a * epsilon) + b * sceneDistance(p + b * epsilon)
        + c * sceneDistance(p + c * epsilon) + d * sceneDistance(p + d * epsilon);
    float len = length(gradient);
    return len > 0.0 ? gradient / len : float3(0.0, 1.0, 0.0);
}

float3 skyColor(float3 direction)
{
    float up = direction.y;
    if (up >= 0.0) return lerp(sdfFrame.skyHorizon.rgb, sdfFrame.skyZenith.rgb, sqrt(saturate(up)));
    return lerp(sdfFrame.skyHorizon.rgb, sdfFrame.skyGround.rgb, sqrt(saturate(-up)));
}

// A hemisphere blend standing in for the cosine-convolved sky.
float3 skyIrradiance(float3 normal)
{
    float3 upper = 0.5 * (sdfFrame.skyZenith.rgb + sdfFrame.skyHorizon.rgb);
    float3 lower = 0.5 * (sdfFrame.skyGround.rgb + sdfFrame.skyHorizon.rgb);
    return lerp(lower, upper, normal.y * 0.5 + 0.5);
}

// Where the march goes after a sample: forward by the clearance, but no farther than the next
// point where the ray's planes join the field.
float sdfAdvance(float t, float clearance)
{
    float next = t + clearance;
    if (t < gBandFrom && next > gBandFrom) next = gBandFrom;
    if (t < gPlanesFrom && next > gPlanesFrom) next = gPlanesFrom;
    return next;
}

// The closest approach of the field to the shadow ray, relative to distance travelled, bounds
// the penumbra: a near miss far from the receiver darkens less than one close to it.
float softShadow(float3 origin, float3 direction, float maxDistance, float sharpness)
{
    // A sample at t is darkened by anything within t / sharpness of it, which is the cone the
    // selection tests; the fixed part covers the blocker threshold below.
    sdfSelectCandidates(origin, direction, maxDistance, 0.001, 1.0 / sharpness, 0.001, 1.0 / sharpness);
    if (gCandidateCount == 0) return 1.0;

    float visibility = 1.0;
    float t = 0.02;
    for (int i = 0; i < SDF_SHADOW_STEPS; i++)
    {
        if (t >= maxDistance) break;
        gRayT = t;
        float clearance = sceneDistance(origin + direction * t);
        if (clearance < 0.0005) return 0.0;
        visibility = min(visibility, sharpness * clearance / t);
        t = sdfAdvance(t, max(clearance, 0.01));
    }
    return saturate(visibility);
}

// Compares the field along the normal with the free-space distance it would have there.
float ambientOcclusion(float3 p, float3 normal)
{
    float occlusion = 0.0;
    float scale = 1.0;
    for (int i = 0; i < 5; i++)
    {
        float height = 0.01 + 0.03 * (float)i;
        occlusion += (height - sceneDistance(p + normal * height)) * scale;
        scale *= 0.95;
    }
    return saturate(1.0 - 3.0 * occlusion);
}

float distributionGgx(float nDotH, float roughness)
{
    float alpha = roughness * roughness;
    float alpha2 = alpha * alpha;
    float denominator = nDotH * nDotH * (alpha2 - 1.0) + 1.0;
    return alpha2 / (SDF_PI * denominator * denominator);
}

float3 shadeSurface(float3 p, float3 normal, float3 view, SdfSurface surface, float bias)
{
    float3 diffuseColor = surface.albedo * (1.0 - surface.metallic);
    float3 f0 = lerp(float3(0.04, 0.04, 0.04), surface.albedo, surface.metallic);
    float roughness = surface.roughness;
    float alpha = roughness * roughness;
    float nDotV = max(dot(normal, view), 1e-4);
    float sharpness = sdfFrame.trace.y;
    float3 shadowOrigin = p + normal * bias;

    // Before the lights: occlusion reads the primary ray's selection, which each shadow ray replaces.
    float ambientIntensity = sdfFrame.trace.w;
    float occlusion = 1.0;
    if (ambientIntensity > 0.0 && sdfFrame.trace.z > 0.0)
        occlusion = lerp(1.0, ambientOcclusion(p, normal), sdfFrame.trace.z);

    float3 color = surface.emissive;
    int lightCount = (int)sdfFrame.counts.y;
    for (int i = 0; i < lightCount; i++)
    {
        SdfLight light = sdfFrame.lights[i];
        float3 toLight = normalize(light.directionRange.xyz);
        float attenuation = 1.0;
        float lightDistance = sdfFrame.counts.w;
        if (light.positionType.w > 0.5)
        {
            float3 offset = light.positionType.xyz - p;
            lightDistance = length(offset);
            toLight = offset / max(lightDistance, 1e-4);
            // Inverse square, windowed to reach zero at the range.
            attenuation = 1.0 / max(lightDistance * lightDistance, 1e-4);
            float range = light.directionRange.w;
            if (range > 0.0)
            {
                float ratio = lightDistance / range;
                float window = saturate(1.0 - ratio * ratio * ratio * ratio);
                attenuation *= window * window;
            }
        }

        float nDotL = saturate(dot(normal, toLight));
        if (nDotL <= 0.0 || attenuation <= 0.0) continue;
        float shadow = 1.0;
        if (sharpness > 0.0 && light.colorShadow.w > 0.5)
            shadow = softShadow(shadowOrigin, toLight, lightDistance, sharpness);
        if (shadow <= 0.0) continue;

        // Matches the PBR path's light units: Lambert omits 1/pi and the specular lobe keeps pi.
        float3 halfVector = normalize(view + toLight);
        float nDotH = saturate(dot(normal, halfVector));
        float lDotH = saturate(dot(toLight, halfVector));
        float visibility = 0.5 / max(lerp(2.0 * nDotL * nDotV, nDotL + nDotV, alpha), 1e-4);
        float grazing = 1.0 - lDotH;
        float grazing2 = grazing * grazing;
        float3 fresnel = f0 + (1.0 - f0) * (grazing2 * grazing2 * grazing);
        float3 specular = distributionGgx(nDotH, roughness) * visibility * fresnel * SDF_PI;
        color += (diffuseColor + specular) * light.colorShadow.rgb * (attenuation * nDotL * shadow);
    }

    if (ambientIntensity > 0.0)
    {
        float3 irradiance = skyIrradiance(normal);
        // Rough surfaces see the blurred sky, smooth ones the gradient in the mirror direction.
        float3 reflection = lerp(skyColor(reflect(-view, normal)), irradiance, roughness);
        float away = 1.0 - nDotV;
        float away2 = away * away;
        float3 fresnel = f0 + (max(float3(1.0 - roughness, 1.0 - roughness, 1.0 - roughness), f0) - f0) * (away2 * away2 * away);
        color += (diffuseColor * irradiance + fresnel * reflection) * (ambientIntensity * occlusion);
    }
    return color;
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
        output.color = float4(skyColor(direction), 1.0);
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
    output.color = float4(color, 1.0);
    output.depth = saturate(clip.z / clip.w);
    return output;
}
