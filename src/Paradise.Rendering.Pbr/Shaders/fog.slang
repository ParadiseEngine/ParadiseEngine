#include "Common/lighting.slang"

struct FogUniforms
{
    float4 colorDensity;
    float4 heightDistance; // base height, falloff, start distance, max distance
    float4 scattering; // steps, anisotropy, direct-light enabled, local-volume count
    float4 albedo;
};
struct FogVolume
{
    float4x4 inverseTransform;
    float4 albedoDensity;
};
[[vk::binding(0, 0)]] Texture2D<float4> sourceColor;
[[vk::binding(1, 0)]] SamplerState sourceSampler;
[[vk::binding(2, 0)]] Texture2D<float> captureDepthTexture;
[[vk::binding(3, 0)]] ConstantBuffer<FogUniforms> fog;
[[vk::binding(4, 0)]] StructuredBuffer<FogVolume> fogVolumes;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};
[shader("vertex")]
Varyings vertexMain(uint vertexId : SV_VertexID)
{
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    Varyings output;
    output.position = float4(p * 2 - 1, 0, 1);
    output.uv = float2(p.x, 1 - p.y);
    return output;
}
float phase(float cosine)
{
    float g = fog.scattering.y;
    float denominator = max(1 + g * g - 2 * g * cosine, 0.0001);
    return (1 - g * g) / (4 * PI * denominator * sqrt(denominator));
}
// Participating media have no receiver surface: applying raster normal/plane bias moves
// samples across thin blockers. Hardware PCF compares the actual volume point without
// the raster depth offset either: there is no coplanar receiver to cause surface acne.
float volumeShadowView(int index, float3 position)
{
    float4 projected = mul(frame.sceneLightShadowMatrices[index], float4(position, 1));
    if (projected.w <= 0) return 1;
    float3 ndc = projected.xyz / projected.w;
    float2 localUv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (any(localUv < 0) || any(localUv > 1) || ndc.z < 0 || ndc.z > 1) return 1;
    float4 rect = frame.shadowViewRects[index];
    float halfTexel = frame.shadowSettings.x * 0.5;
    float2 uv = clamp(rect.xy + localUv * rect.zw, rect.xy + halfTexel, rect.xy + rect.zw - halfTexel);
    return shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv, frame.shadowViewData[index].w), ndc.z);
}
float volumeShadow(SceneLight light, float3 position)
{
    if (light.spotAngles.w <= 0 || light.shadowAtlas.y <= 0 || light.spotAngles.z < 0) return 1;
    int first = (int)light.spotAngles.z, count = (int)light.shadowAtlas.y, face = 0;
    float cameraDepth = dot(position - frame.cameraPos.xyz, frame.cameraForward.xyz);
    if (light.positionAndType.w < 0.5)
    {
        if (cameraDepth > frame.shadowViewData[first + count - 1].z) return 1;
        for (int c = 0; c < count - 1; c++)
            if (cameraDepth > frame.shadowViewData[first + c].z) face = c + 1;
    }
    else if (light.positionAndType.w < 1.5)
        face = getPointShadowFace(position - light.positionAndType.xyz);
    if (face >= count) return 1;
    float visibility = volumeShadowView(first + face, position);
    if (light.positionAndType.w < 0.5)
    {
        float4 split = frame.shadowViewData[first + face];
        float width = (split.z - split.y) * frame.shadowFilter.y;
        float blend = saturate((cameraDepth - (split.z - width)) / max(width, 1e-5));
        if (blend > 0)
            visibility = lerp(visibility, face + 1 < count ? volumeShadowView(first + face + 1, position) : 1, blend);
    }
    return lerp(1, visibility, light.spotAngles.w);
}
float3 incidentLight(float3 position, float3 rayDirection)
{
    float3 result = float3(0);
    for (int i = 0; i < min((int)frame.ambientEquator.w, MaxSceneLights); i++)
    {
        SceneLight light = frame.sceneLights[i];
        float3 L = normalize(light.directionAndRange.xyz);
        float attenuation = 1;
        if (light.positionAndType.w > 0.5)
        {
            float3 offset = light.positionAndType.xyz - position;
            float distance = max(length(offset), 0.001);
            L = offset / distance;
            attenuation = getDistanceAttenuation(distance, light.directionAndRange.w, light.shadowAtlas.x);
            if (light.positionAndType.w > 1.5) attenuation *= getSpotAttenuation(light, L);
        }
        if (attenuation <= 0) continue;
        float visibility = volumeShadow(light, position);
        result += light.colorAndIntensity.rgb * light.colorAndIntensity.w * attenuation * visibility
            * phase(clamp(dot(L, rayDirection), -1, 1));
    }
    return result;
}
// The unnormalized local direction keeps intersection distances in world metres.
float2 volumeInterval(FogVolume volume, float3 origin, float3 direction, float start, float end)
{
    float3 o = mul(volume.inverseTransform, float4(origin, 1)).xyz;
    float3 d = mul(volume.inverseTransform, float4(direction, 0)).xyz;
    float entry = start;
    float exit = end;
    for (int axis = 0; axis < 3; axis++)
    {
        if (d[axis] == 0)
        {
            if (abs(o[axis]) > 0.5) return float2(0);
        }
        else
        {
            float a = (-0.5 - o[axis]) / d[axis];
            float b = (0.5 - o[axis]) / d[axis];
            entry = max(entry, min(a, b));
            exit = min(exit, max(a, b));
        }
    }
    return float2(entry, max(entry, exit));
}
[shader("fragment")]
float4 fragmentMain(Varyings input) : SV_Target
{
    float4 color = sourceColor.SampleLevel(sourceSampler, input.uv, 0);
    float depth = captureDepthTexture.Load(int3(int2(input.position.xy), 0));
    float2 ndc = input.uv * float2(2, -2) + float2(-1, 1);
    float4 nearPoint = mul(frame.invViewProj, float4(ndc, 0, 1));
    float4 farPoint = mul(frame.invViewProj, float4(ndc, 0.5, 1));
    nearPoint.xyz /= nearPoint.w;
    farPoint.xyz /= farPoint.w;
    // A finite interior point also supports projections with an infinite far plane.
    float3 origin = fog.albedo.w > 0.5 ? frame.cameraPos.xyz : nearPoint.xyz;
    float3 ray = farPoint.xyz - origin;
    float rayLength = length(ray);
    if (rayLength <= 0.00001) return color;
    float3 direction = ray / rayLength;
    float distance = fog.heightDistance.w;
    if (depth < 1)
    {
        float4 surface = mul(frame.invViewProj, float4(ndc, depth, 1));
        distance = min(length(surface.xyz / surface.w - origin), distance);
    }
    float start = min(fog.heightDistance.z, distance);
    if (distance <= start) return color;
    float2 intervals[16];
    for (int volume = 0; volume < (int)fog.scattering.w; volume++)
        intervals[volume] = volumeInterval(fogVolumes[volume], origin, direction, start, distance);
    int steps = (int)fog.scattering.x;
    float stepLength = (distance - start) / steps;
    float transmission = 1;
    float3 scattered = float3(0);
    for (int step = 0; step < steps; step++)
    {
        float3 position = origin + direction * (start + (step + 0.5) * stepLength);
        float density = fog.colorDensity.w * exp(clamp(-(position.y - fog.heightDistance.x) * fog.heightDistance.y, -20, 20));
        float3 albedoDensity = fog.albedo.rgb * density;
        for (int volume = 0; volume < (int)fog.scattering.w; volume++)
        {
            FogVolume local = fogVolumes[volume];
            float segmentStart = start + step * stepLength;
            float overlap = max(0, min(segmentStart + stepLength, intervals[volume].y)
                - max(segmentStart, intervals[volume].x));
            float localDensity = local.albedoDensity.w * (overlap / stepLength);
            density += localDensity;
            albedoDensity += local.albedoDensity.rgb * localDensity;
        }
        float segmentTransmission = exp(-density * stepLength);
        float3 source = fog.colorDensity.rgb;
        if (fog.scattering.z > 0.5 && density > 0.000001)
            source += albedoDensity / density * incidentLight(position, direction);
        scattered += transmission * (1 - segmentTransmission) * source;
        transmission *= segmentTransmission;
        if (transmission < 0.001) break;
    }
    return float4(color.rgb * transmission + scattered, color.a);
}
