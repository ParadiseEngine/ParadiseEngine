// FXAA on the tonemapped frame: an edge-direction search with subpixel coverage filtering.
// Thresholds use perceptual luminance; color interpolation stays display-linear. Use the linear
// entry for sRGB formats, which encode in hardware, and the encoding entry for everything else.

#include "Common/sdfColor.slang"

struct SdfFxaaUniforms
{
    float4 screen;     // xy texel size, zw resolution
    float4 parameters; // x relative threshold, y minimum threshold, z subpixel quality
};

[[vk::binding(0, 0)]] Texture2D<float4> sourceTexture;
[[vk::binding(1, 0)]] SamplerState sourceSampler;
[[vk::binding(2, 0)]] ConstantBuffer<SdfFxaaUniforms> sdfFxaa;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
Varyings fxaaVertex(uint vertexId : SV_VertexID)
{
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = float2(p.x, 1.0 - p.y);
    return output;
}

float luma(float2 uv)
{
    return sqrt(max(dot(sourceTexture.SampleLevel(sourceSampler, uv, 0.0).rgb, float3(0.2126, 0.7152, 0.0722)), 0.0));
}

float3 resolve(float2 uv)
{
    float2 texel = sdfFxaa.screen.xy;
    float center = luma(uv);
    float north = luma(uv + float2(0.0, -texel.y));
    float south = luma(uv + float2(0.0, texel.y));
    float west = luma(uv + float2(-texel.x, 0.0));
    float east = luma(uv + float2(texel.x, 0.0));
    float minimum = min(center, min(min(north, south), min(west, east)));
    float maximum = max(center, max(max(north, south), max(west, east)));
    float range = maximum - minimum;
    if (range < max(sdfFxaa.parameters.y, maximum * sdfFxaa.parameters.x) || range <= 1e-6)
        return sourceTexture.SampleLevel(sourceSampler, uv, 0.0).rgb;

    float nw = luma(uv + float2(-texel.x, -texel.y));
    float ne = luma(uv + float2(texel.x, -texel.y));
    float sw = luma(uv + float2(-texel.x, texel.y));
    float se = luma(uv + float2(texel.x, texel.y));
    float horizontal = abs(nw + sw - 2.0 * west) + 2.0 * abs(north + south - 2.0 * center) + abs(ne + se - 2.0 * east);
    float vertical = abs(nw + ne - 2.0 * north) + 2.0 * abs(west + east - 2.0 * center) + abs(sw + se - 2.0 * south);
    bool edgeHorizontal = horizontal >= vertical;
    float first = edgeHorizontal ? north : west;
    float second = edgeHorizontal ? south : east;
    float firstGradient = abs(first - center), secondGradient = abs(second - center);
    bool firstSteeper = firstGradient >= secondGradient;
    float gradient = max(firstGradient, secondGradient);
    float edgeLuma = (center + (firstSteeper ? first : second)) * 0.5;
    float2 normal = edgeHorizontal ? float2(0.0, texel.y) : float2(texel.x, 0.0);
    if (firstSteeper) normal = -normal;
    float2 tangent = edgeHorizontal ? float2(texel.x, 0.0) : float2(0.0, texel.y);
    float2 edgeUv = uv + normal * 0.5;
    float2 negative = edgeUv - tangent, positive = edgeUv + tangent;
    float negativeDelta = luma(negative) - edgeLuma;
    float positiveDelta = luma(positive) - edgeLuma;
    float threshold = gradient * 0.25;
    bool negativeDone = abs(negativeDelta) >= threshold, positiveDone = abs(positiveDelta) >= threshold;
    for (int i = 0; i < 10; i++)
    {
        if (negativeDone && positiveDone) break;
        float stride = i < 4 ? 1.0 : (i < 8 ? 2.0 : 4.0);
        if (!negativeDone)
        {
            negative -= tangent * stride;
            negativeDelta = luma(negative) - edgeLuma;
            negativeDone = abs(negativeDelta) >= threshold;
        }
        if (!positiveDone)
        {
            positive += tangent * stride;
            positiveDelta = luma(positive) - edgeLuma;
            positiveDone = abs(positiveDelta) >= threshold;
        }
    }
    float distanceNegative = edgeHorizontal ? uv.x - negative.x : uv.y - negative.y;
    float distancePositive = edgeHorizontal ? positive.x - uv.x : positive.y - uv.y;
    bool negativeCloser = distanceNegative < distancePositive;
    float closestDelta = negativeCloser ? negativeDelta : positiveDelta;
    float edgeOffset = 0.5 - min(distanceNegative, distancePositive) / max(distanceNegative + distancePositive, 1e-6);
    if ((closestDelta < 0.0) == (center < edgeLuma)) edgeOffset = 0.0;

    float neighborhood = (2.0 * (north + south + west + east) + nw + ne + sw + se) / 12.0;
    float subpixel = saturate(abs(neighborhood - center) / range);
    subpixel = subpixel * subpixel * (3.0 - 2.0 * subpixel);
    subpixel = subpixel * subpixel * sdfFxaa.parameters.z;
    return sourceTexture.SampleLevel(sourceSampler, uv + normal * max(edgeOffset, subpixel), 0.0).rgb;
}

[shader("fragment")]
float4 fxaaFragment(Varyings input) : SV_Target
{
    return float4(resolve(input.uv), 1.0);
}

[shader("fragment")]
float4 fxaaFragmentSrgb(Varyings input) : SV_Target
{
    return float4(sdfLinearToSrgb(resolve(input.uv)), 1.0);
}
