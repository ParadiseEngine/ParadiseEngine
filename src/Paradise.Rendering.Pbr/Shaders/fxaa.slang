// Edge-direction search and subpixel coverage filtering. Thresholds use perceptual luminance;
// color interpolation stays display-linear, with the sRGB OETF applied only at presentation.
struct FxaaUniforms
{
    float4 screen;
    float4 parameters; // relative threshold, minimum threshold, subpixel quality
};
[[vk::binding(0, 0)]] Texture2D<float4> sourceTexture;
[[vk::binding(1, 0)]] SamplerState sourceSampler;
[[vk::binding(2, 0)]] ConstantBuffer<FxaaUniforms> fxaa;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
Varyings vertexMain(uint index : SV_VertexID)
{
    Varyings output;
    float2 uv = float2((index << 1) & 2, index & 2);
    output.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.uv = uv;
    return output;
}

float luma(float2 uv)
{
    return sqrt(max(dot(sourceTexture.SampleLevel(sourceSampler, uv, 0.0).rgb, float3(0.2126, 0.7152, 0.0722)), 0.0));
}

[shader("fragment")]
float4 fragmentMain(Varyings input) : SV_Target
{
    float2 uv = input.uv, texel = fxaa.screen.xy;
    float center = luma(uv);
    float north = luma(uv + float2(0.0, -texel.y));
    float south = luma(uv + float2(0.0, texel.y));
    float west = luma(uv + float2(-texel.x, 0.0));
    float east = luma(uv + float2(texel.x, 0.0));
    float minimum = min(center, min(min(north, south), min(west, east)));
    float maximum = max(center, max(max(north, south), max(west, east)));
    float range = maximum - minimum;
    if (range < max(fxaa.parameters.y, maximum * fxaa.parameters.x) || range <= 1e-6)
        return sourceTexture.SampleLevel(sourceSampler, uv, 0.0);

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
    subpixel = subpixel * subpixel * fxaa.parameters.z;
    return float4(sourceTexture.SampleLevel(sourceSampler, uv + normal * max(edgeOffset, subpixel), 0.0).rgb, 1.0);
}
