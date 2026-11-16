// The scene's field as every SDF pass reads it: the frame and shape bindings, per-ray shape
// selection, the fold, marched shadows, direct lighting, probe lookups and traced reflections.
// SdfField.cs folds the shapes identically on the CPU; SdfUniforms.cs mirrors the layouts below.
//
// Every ray first selects the shapes it can come near, once, and its samples fold only those:
// walking the whole list per sample was most of the frame. Ground planes join a ray's field only
// from where the ray reaches them, so the march flies there instead of creeping down to them.
//
// A program defines SDF_PROBES before including this to light from the irradiance probes. Code
// that marches the field costs frame time even where a uniform switches it off, so what a
// program does not use is compiled out rather than branched around.
#ifndef PARADISE_SDF_FIELD_SLANG
#define PARADISE_SDF_FIELD_SLANG

#include "sdf.slang"
#include "sdfGi.slang"

static const int SDF_MAX_LIGHTS = 8;
static const int SDF_SHADOW_STEPS = 64;
static const int SDF_PROBE_STEPS = 64;
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
    float4 positionType;   // xyz position, w: 0 directional / 1 point / 2 spot
    float4 directionRange; // xyz surface-to-light (directional) or toward the light along a spot's axis, w range (0 = unbounded)
    float4 colorShadow;    // rgb linear color x intensity, w: 1 casts shadows
    float4 spot;           // x cosine of the outer cone angle, y of the inner
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
    float4 fogColor;      // rgb linear color, a density (0 = no fog)
    float4 fogShape;      // x height falloff (0 = uniform), y base height, z start distance, w max opacity
    float4 reflections;   // x roughest surface that traces its reflection (negative = none), y steps, z longest ray, w 1 = shadowed
    float4 cameraPosition; // xyz the eye, or a point on an orthographic camera's plane
    SdfLight lights[SDF_MAX_LIGHTS];
};

[[vk::binding(0, 0)]] ConstantBuffer<SdfFrameUniforms> sdfFrame;
[[vk::binding(1, 0)]] StructuredBuffer<SdfShape> sdfShapes;
#ifdef SDF_PROBES
// Irradiance probes, read with Load only. Declared, like everything that reads them, only for
// the programs that light from probes.
[[vk::binding(2, 0)]] Texture2D<float4> sdfProbes;
[[vk::binding(3, 0)]] ConstantBuffer<SdfGiUniforms> sdfGi;
#endif

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

struct SdfSurface
{
    float3 albedo;
    float roughness;
    float3 emissive;
    float metallic;
};

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

// Direct light from every light at a surface point. asDiffuse treats the whole albedo as a matte
// reflector and drops the specular lobe: what a bounce or a reflection of the surface carries.
// Each shadowed light replaces the ray selection.
float3 sdfLighting(float3 p, float3 normal, float3 view, SdfSurface surface, float bias, bool shadows, bool asDiffuse)
{
    float3 diffuseColor = asDiffuse ? surface.albedo : surface.albedo * (1.0 - surface.metallic);
    float3 f0 = lerp(float3(0.04, 0.04, 0.04), surface.albedo, surface.metallic);
    float roughness = surface.roughness;
    float alpha = roughness * roughness;
    float nDotV = max(dot(normal, view), 1e-4);
    float sharpness = sdfFrame.trace.y;
    float3 shadowOrigin = p + normal * bias;

    float3 color = float3(0.0, 0.0, 0.0);
    int lightCount = (int)sdfFrame.counts.y;
    for (int i = 0; i < lightCount; i++)
    {
        SdfLight light = sdfFrame.lights[i];
        float3 axis = normalize(light.directionRange.xyz);
        float3 toLight = axis;
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
            if (light.positionType.w > 1.5)
                attenuation *= smoothstep(light.spot.x, light.spot.y, dot(toLight, axis));
        }

        float nDotL = saturate(dot(normal, toLight));
        if (nDotL <= 0.0 || attenuation <= 0.0) continue;
        float shadow = 1.0;
        if (shadows && sharpness > 0.0 && light.colorShadow.w > 0.5)
            shadow = softShadow(shadowOrigin, toLight, lightDistance, sharpness);
        if (shadow <= 0.0) continue;

        float3 lobe = diffuseColor;
        if (!asDiffuse)
        {
            // Matches the PBR path's light units: Lambert omits 1/pi and the specular lobe keeps pi.
            float3 halfVector = normalize(view + toLight);
            float nDotH = saturate(dot(normal, halfVector));
            float lDotH = saturate(dot(toLight, halfVector));
            float visibility = 0.5 / max(lerp(2.0 * nDotL * nDotV, nDotL + nDotV, alpha), 1e-4);
            float grazing = 1.0 - lDotH;
            float grazing2 = grazing * grazing;
            float3 fresnel = f0 + (1.0 - f0) * (grazing2 * grazing2 * grazing);
            lobe += distributionGgx(nDotH, roughness) * visibility * fresnel * SDF_PI;
        }
        color += lobe * light.colorShadow.rgb * (attenuation * nDotL * shadow);
    }
    return color;
}

#ifdef SDF_PROBES
// The light the probes around a surface point hold, as a linear function of direction.
struct SdfProbeLight
{
    float3 mean;    // mean radiance around the point
    float3 momentX; // three times the mean of radiance x direction, per axis
    float3 momentY;
    float3 momentZ;
    float weight;   // 0 where no probe reaches the point, 1 inside the volume
};

// A probe stores the mean radiance m around it and three times the mean of radiance x direction,
// v; radiance toward w is then about m + v . w.
float3 sdfProbeRadiance(SdfProbeLight light, float3 direction)
{
    return max(light.mean + light.momentX * direction.x + light.momentY * direction.y + light.momentZ * direction.z,
        float3(0.0, 0.0, 0.0));
}

// Irradiance over pi: the cosine-weighted mean of m + v . w about the normal is m + 2/3 v . n.
float3 sdfProbeIrradiance(SdfProbeLight light, float3 normal)
{
    return sdfProbeRadiance(light, normal * (2.0 / 3.0));
}

// Blends the eight probes around a surface point. They are weighed trilinearly, by how far they
// sit in front of the surface, by their validity, and with visibility by the field halfway to
// them: a segment of length d is clear when the field at its middle exceeds d / 2, so a probe
// behind a wall weighs nothing and its light does not leak through. Visibility replaces the ray
// selection.
SdfProbeLight sdfSampleProbes(float3 p, float3 normal, bool visibility)
{
    SdfProbeLight light;
    light.mean = float3(0.0, 0.0, 0.0);
    light.momentX = float3(0.0, 0.0, 0.0);
    light.momentY = float3(0.0, 0.0, 0.0);
    light.momentZ = float3(0.0, 0.0, 0.0);
    light.weight = 0.0;

    float spacing = sdfGi.origin.w;
    float3 counts = sdfGi.counts.xyz;
    int perRow = (int)sdfGi.counts.w;
    float3 biased = p + normal * sdfGi.params.z;
    float3 grid = (biased - sdfGi.origin.xyz) / spacing;
    float3 base = clamp(floor(grid), float3(0.0, 0.0, 0.0), counts - 2.0);
    float3 along = saturate(grid - base);
    // Outside the volume the probes fade out over one cell.
    float3 outside = max(max(-grid, grid - (counts - 1.0)), float3(0.0, 0.0, 0.0));
    float inside = saturate(1.0 - max(outside.x, max(outside.y, outside.z)));
    if (inside <= 0.0) return light;
    if (visibility)
    {
        // A midpoint lies half the way to its probe and reads the field up to 0.3 of the way
        // around it, where the weight saturates.
        float3 farthest = max(abs(grid - base), abs(grid - base - 1.0));
        sdfSelectCandidates(biased, normal, 0.0, 0.8 * length(farthest) * spacing + 1e-3, 0.0, 0.0, 0.0);
        gPlanesFrom = 0.0;
    }

    float total = 0.0;
    for (int corner = 0; corner < 8; corner++)
    {
        float3 offset = float3((float)(corner & 1), (float)((corner >> 1) & 1), (float)((corner >> 2) & 1));
        float3 cell = base + offset;
        float3 trilinear = lerp(1.0 - along, along, offset);
        int probe = (int)(cell.x + counts.x * (cell.z + counts.z * cell.y));
        float3 toProbe = sdfGi.origin.xyz + cell * spacing - biased;
        float distance_ = length(toProbe);
        float facing = dot(toProbe / max(distance_, 1e-4), normal) * 0.5 + 0.5;
        float weight = trilinear.x * trilinear.y * trilinear.z * (facing * facing + 0.2)
            * sdfProbes.Load(int3(sdfProbeTexel(probe, 4, perRow), 0)).r;
        if (weight <= 0.0) continue;
        if (visibility)
            weight *= saturate(sceneDistance(biased + toProbe * 0.5) / max(0.3 * distance_, 1e-4));
        light.mean += weight * sdfProbes.Load(int3(sdfProbeTexel(probe, 0, perRow), 0)).rgb;
        light.momentX += weight * sdfProbes.Load(int3(sdfProbeTexel(probe, 1, perRow), 0)).rgb;
        light.momentY += weight * sdfProbes.Load(int3(sdfProbeTexel(probe, 2, perRow), 0)).rgb;
        light.momentZ += weight * sdfProbes.Load(int3(sdfProbeTexel(probe, 3, perRow), 0)).rgb;
        total += weight;
    }
    // No probe can see the point: the caller's sky is the best remaining guess.
    if (total < 1e-4) return light;

    light.mean /= total;
    light.momentX /= total;
    light.momentY /= total;
    light.momentZ /= total;
    light.weight = inside;
    return light;
}
#endif // SDF_PROBES

// Ambient irradiance over pi at a surface point: the sky's, or inside the probe volume the
// probes' scaled by gain, which carry the sky as geometry occludes it and the light surfaces
// bounce. A gain of zero reads no probe. Reading probes replaces the ray selection.
float3 sdfAmbientIrradiance(float3 p, float3 normal, float gain)
{
    float3 irradiance = skyIrradiance(normal) * sdfFrame.trace.w;
#ifdef SDF_PROBES
    if (gain > 0.0)
    {
        SdfProbeLight light = sdfSampleProbes(p, normal, sdfGi.params.y > 0.5);
        irradiance = lerp(irradiance, sdfProbeIrradiance(light, normal) * gain, light.weight);
    }
#endif
    return irradiance;
}

// Marches a ray that starts on a surface or in free space and returns the distance to what it
// hits, or a negative number. The threshold is twice the eye ray's: what a bounce or a reflection
// shows is seen through a blur of its own. Replaces the ray selection.
float sdfTraceRay(float3 origin, float3 direction, float reach, int steps)
{
    float epsilon = sdfFrame.trace.x;
    // Planes join where they come within this ray's hit threshold, twice the eye ray's.
    sdfSelectCandidates(origin, direction, reach, 9.0 * max(epsilon, 1e-4), 9.0 * epsilon, 2.0 * epsilon, 2.0 * epsilon);
    float t = 0.0;
    for (int i = 0; i < steps; i++)
    {
        gRayT = t;
        float clearance = sceneDistance(origin + direction * t);
        if (clearance < 2.0 * epsilon * (1.0 + t)) return t;
        t = sdfAdvance(t, clearance);
        if (t > reach) break;
    }
    return -1.0;
}

// The light arriving along a secondary ray: the sky, or the surface it reaches, lit as a matte
// reflector. With ambient the surface also returns the ambient light it receives, the probes'
// scaled by probeGain. Replaces the ray selection.
float3 sdfSecondaryRadiance(float3 origin, float3 direction, float reach, int steps, bool shadows, bool ambient, float probeGain)
{
    float t = sdfTraceRay(origin, direction, reach, steps);
    if (t < 0.0) return skyColor(direction) * sdfFrame.trace.w;

    // Samples around the hit point belong to no march: every selected plane takes part.
    gPlanesFrom = 0.0;
    float3 p = origin + direction * t;
    float threshold = 2.0 * sdfFrame.trace.x * (1.0 + t);
    float3 normal = sceneNormal(p, max(threshold, 1e-4));
    SdfSurface surface = sceneSurface(p);
    float bias = 2.0 * threshold + 1e-3;
    float3 color = surface.emissive + sdfLighting(p, normal, -direction, surface, bias, shadows, true);
    if (ambient)
        color += surface.albedo * sdfAmbientIrradiance(p + normal * bias, normal, probeGain);
    return color;
}

// What a mirror ray sees.
float3 sdfTraceReflection(float3 origin, float3 direction)
{
#ifdef SDF_PROBES
    float probeGain = sdfGi.params.x;
#else
    float probeGain = 0.0;
#endif
    return sdfSecondaryRadiance(origin, direction, sdfFrame.reflections.z, (int)sdfFrame.reflections.y,
        sdfFrame.reflections.w > 0.5, true, probeGain);
}

// Exponential fog between the eye and a point t along a ray that starts at origin. With a height
// falloff the density is density * exp(-falloff * (y - base)), integrated along the ray in closed
// form. SdfMath.FogOpacity is the CPU twin.
float3 sdfApplyFog(float3 color, float3 origin, float3 direction, float t)
{
    float density = sdfFrame.fogColor.a;
    if (density <= 0.0) return color;
    // From the eye, or an orthographic camera's plane, to where the ray starts on the near plane.
    float lead = dot(origin - sdfFrame.cameraPosition.xyz, direction);
    float start = sdfFrame.fogShape.z;
    float travelled = max(t + lead - start, 0.0);
    float depth = density * travelled;
    float falloff = sdfFrame.fogShape.x;
    if (falloff > 0.0)
    {
        float height = origin.y + direction.y * (start - lead) - sdfFrame.fogShape.y;
        // Along the ray the density is its value at the start times exp(-rate * s), whose
        // integral is the distance itself for a level ray.
        float rate = falloff * direction.y;
        float rise = rate * travelled;
        float thickness = abs(rise) > 1e-4 ? (1.0 - exp(-clamp(rise, -60.0, 60.0))) / rate : travelled;
        depth = density * exp(clamp(-falloff * height, -60.0, 60.0)) * thickness;
    }
    float opacity = min(1.0 - exp(-depth), sdfFrame.fogShape.w);
    return lerp(color, sdfFrame.fogColor.rgb, opacity);
}

#endif // PARADISE_SDF_FIELD_SLANG
