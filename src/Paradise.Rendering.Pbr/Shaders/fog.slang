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
float3 incidentLight(float3 position, float3 rayDirection, float2 screen)
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
        float visibility = sampleSceneLightShadow(light, i, position, L, L, screen);
        result += light.colorAndIntensity.rgb * light.colorAndIntensity.w * attenuation * visibility
            * phase(clamp(dot(L, rayDirection), -1, 1));
    }
    return result;
}
[shader("fragment")]
float4 fragmentMain(Varyings input) : SV_Target
{
    float4 color = sourceColor.SampleLevel(sourceSampler, input.uv, 0);
    float depth = captureDepthTexture.Load(int3(int2(input.position.xy), 0));
    float2 ndc = input.uv * float2(2, -2) + float2(-1, 1);
    float4 nearPoint = mul(frame.invViewProj, float4(ndc, 0, 1));
    float4 farPoint = mul(frame.invViewProj, float4(ndc, min(depth, 0.999999), 1));
    nearPoint.xyz /= nearPoint.w;
    farPoint.xyz /= farPoint.w;
    // Near-plane origins work for both perspective and orthographic cameras.
    float3 origin = fog.albedo.w > 0.5 ? frame.cameraPos.xyz : nearPoint.xyz;
    float3 ray = farPoint.xyz - origin;
    float rayLength = length(ray);
    if (rayLength <= 0.00001) return color;
    float3 direction = ray / rayLength;
    float distance = depth >= 0.999999 ? fog.heightDistance.w : min(rayLength, fog.heightDistance.w);
    float start = min(fog.heightDistance.z, distance);
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
            float3 p = mul(local.inverseTransform, float4(position, 1)).xyz;
            if (all(abs(p) <= float3(0.5)))
            {
                density += local.albedoDensity.w;
                albedoDensity += local.albedoDensity.rgb * local.albedoDensity.w;
            }
        }
        float segmentTransmission = exp(-density * stepLength);
        float3 source = fog.colorDensity.rgb;
        if (fog.scattering.z > 0.5 && density > 0.000001)
            source += albedoDensity / density * incidentLight(position, direction, input.position.xy);
        scattered += transmission * (1 - segmentTransmission) * source;
        transmission *= segmentTransmission;
        if (transmission < 0.001) break;
    }
    return float4(color.rgb * transmission + scattered, color.a);
}
