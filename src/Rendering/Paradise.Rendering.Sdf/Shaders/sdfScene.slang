// Sphere-traces the scene's analytic distance field from a fullscreen triangle into linear HDR
// and device depth. Lighting reads the same field: marched soft shadows, short-range ambient
// occlusion and a gradient sky for ambient and reflection. SdfField.cs folds the shapes identically
// on the CPU; SdfUniforms.cs mirrors the layouts below.

#include "Common/sdf.slang"

static const int SDF_MAX_LIGHTS = 8;
static const int SDF_SHADOW_STEPS = 64;
static const float SDF_PI = 3.14159265358979;

struct SdfShape
{
    float4x4 worldToLocal;
    float4 size;             // kind-specific local dimensions
    float4 params;           // x kind, y operation, z smoothness, w uniform scale
    float4 bounds;           // xyz world center, w conservative world radius
    float4 albedoRoughness;  // rgb linear albedo, a roughness
    float4 emissiveMetallic; // rgb linear emission, a metallic
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

float sceneDistance(float3 p)
{
    float accumulated = SDF_FAR;
    int count = (int)sdfFrame.counts.x;
    for (int i = 0; i < count; i++)
    {
        float4 params = sdfShapes[i].params;
        if (sdfCanSkip(i, p, accumulated, params.z)) continue;
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
    int count = (int)sdfFrame.counts.x;
    for (int i = 0; i < count; i++)
    {
        float4 params = sdfShapes[i].params;
        if (sdfCanSkip(i, p, accumulated, params.z)) continue;
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

// The closest approach of the field to the shadow ray, relative to distance travelled, bounds
// the penumbra: a near miss far from the receiver darkens less than one close to it.
float softShadow(float3 origin, float3 direction, float maxDistance, float sharpness)
{
    float visibility = 1.0;
    float t = 0.02;
    for (int i = 0; i < SDF_SHADOW_STEPS; i++)
    {
        if (t >= maxDistance) break;
        float clearance = sceneDistance(origin + direction * t);
        if (clearance < 0.0005) return 0.0;
        visibility = min(visibility, sharpness * clearance / t);
        t += max(clearance, 0.01);
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

    float ambientIntensity = sdfFrame.trace.w;
    if (ambientIntensity > 0.0)
    {
        float occlusion = lerp(1.0, ambientOcclusion(p, normal), sdfFrame.trace.z);
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

    // The hit threshold grows with distance like a pixel's footprint, so far surfaces converge
    // in as few steps as near ones.
    int maxSteps = (int)sdfFrame.counts.z;
    float t = 0.0;
    bool hit = false;
    for (int i = 0; i < maxSteps; i++)
    {
        float clearance = sceneDistance(origin + direction * t);
        if (clearance < epsilon * (1.0 + t))
        {
            hit = true;
            break;
        }
        t += clearance;
        if (t > maxDistance) break;
    }

    SceneOutput output;
    if (!hit)
    {
        output.color = float4(skyColor(direction), 1.0);
        output.depth = 1.0;
        return output;
    }

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
