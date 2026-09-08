// HDR history stays before exposure and bloom. Motion includes camera jitter and uses top-left UVs.
struct TemporalUniforms
{
    float4 screen;
    float4 parameters; // history weight, variance gamma, relative view-depth tolerance, history valid
    float4x4 previousInverseProjection;
};
[[vk::binding(0, 0)]] ConstantBuffer<TemporalUniforms> temporal;
[[vk::binding(1, 0)]] Texture2D<float4> currentColor;
[[vk::binding(2, 0)]] Texture2D<float4> motionTexture;
[[vk::binding(3, 0)]] Texture2D<float4> captureDepthTexture;
[[vk::binding(4, 0)]] Texture2D<float4> historyColor;
[[vk::binding(5, 0)]] Texture2D<float4> historyDepthTexture;
[[vk::binding(6, 0)]] [format("rgba16f")] WTexture2D<float4> resolvedColor;
[[vk::binding(7, 0)]] [format("r32f")] WTexture2D<float> resolvedDepth;

float3 saneColor(float3 color)
{
    return all(abs(color) <= 65504.0) ? clamp(color, 0.0, 65504.0) : float3(0.0);
}

// Bounding in luminance-compressed YCoCg preserves HDR highlights without letting one firefly
// expand the clipping box across the entire radiance range.
float3 toClipSpace(float3 color)
{
    color /= 1.0 + dot(color, float3(0.2126, 0.7152, 0.0722));
    return float3(dot(color, float3(0.25, 0.5, 0.25)), (color.r - color.b) * 0.5,
        dot(color, float3(-0.25, 0.5, -0.25)));
}

float3 fromClipSpace(float3 color)
{
    float3 rgb = max(float3(color.x + color.y - color.z, color.x + color.z, color.x - color.y - color.z), 0.0);
    return min(rgb / max(1.0 - dot(rgb, float3(0.2126, 0.7152, 0.0722)), 1e-5), 65504.0);
}

float previousViewDepth(float2 uv, float depth)
{
    float4 view = mul(temporal.previousInverseProjection, float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), depth, 1.0));
    return view.z / view.w;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void resolve(uint3 dispatchId : SV_DispatchThreadID)
{
    int2 pixel = int2(dispatchId.xy);
    int2 size = int2(temporal.screen.zw);
    if (any(pixel >= size)) return;
    float3 current = saneColor(currentColor.Load(int3(pixel, 0)).rgb);
    float currentDepth = captureDepthTexture.Load(int3(pixel, 0)).r;
    resolvedDepth.Store(pixel, currentDepth);
    float4 motion = motionTexture.Load(int3(pixel, 0));
    float2 uv = (float2(pixel) + 0.5) * temporal.screen.xy;
    float2 previousUv = uv - motion.xy;
    if (temporal.parameters.w < 0.5 || motion.w < 0.5 || !all(abs(motion) <= 65504.0)
        || any(previousUv < 0.0) || any(previousUv >= 1.0) || motion.z < 0.0 || motion.z > 1.0)
    {
        resolvedColor.Store(pixel, float4(current, 1.0));
        return;
    }

    // Require matching surface depth before accepting history. Background taps may contribute
    // silhouette coverage, but another surface at a different depth must never contribute.
    // Rejecting all background taps renormalizes a white edge back to white and prevents AA.
    float expectedDepth = previousViewDepth(previousUv, motion.z);
    float2 previousPixel = previousUv * temporal.screen.zw - 0.5;
    int2 base = int2(floor(previousPixel));
    float2 fraction = frac(previousPixel);
    float3 history = float3(0.0);
    float weightSum = 0.0;
    float surfaceWeight = 0.0;
    for (int y = 0; y < 2; y++)
    {
        for (int x = 0; x < 2; x++)
        {
            int2 tap = base + int2(x, y);
            if (any(tap < 0) || any(tap >= size)) continue;
            float depth = historyDepthTexture.Load(int3(tap, 0)).r;
            float2 tapUv = (float2(tap) + 0.5) * temporal.screen.xy;
            float tapDepth = previousViewDepth(tapUv, depth);
            bool matches = abs(tapDepth) < 1e20 && abs(tapDepth - expectedDepth)
                <= max(1e-4, abs(expectedDepth) * temporal.parameters.z);
            if (!matches && depth < 1.0) continue;
            float weight = (x == 0 ? 1.0 - fraction.x : fraction.x) * (y == 0 ? 1.0 - fraction.y : fraction.y);
            history += saneColor(historyColor.Load(int3(tap, 0)).rgb) * weight;
            weightSum += weight;
            if (matches) surfaceWeight += weight;
        }
    }
    if (surfaceWeight < 0.25)
    {
        resolvedColor.Store(pixel, float4(current, 1.0));
        return;
    }
    history /= weightSum;

    float3 minimum = float3(1e10), maximum = float3(-1e10);
    float3 mean = float3(0.0), secondMoment = float3(0.0);
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 tap = clamp(pixel + int2(x, y), int2(0), size - 1);
            float3 value = toClipSpace(saneColor(currentColor.Load(int3(tap, 0)).rgb));
            minimum = min(minimum, value);
            maximum = max(maximum, value);
            mean += value / 9.0;
            secondMoment += value * value / 9.0;
        }
    }
    float3 sigma = sqrt(max(secondMoment - mean * mean, 0.0));
    minimum = max(minimum, mean - sigma * temporal.parameters.y);
    maximum = min(maximum, mean + sigma * temporal.parameters.y);
    history = fromClipSpace(clamp(toClipSpace(history), minimum, maximum));
    float speed = length(motion.xy * temporal.screen.zw);
    float weight = temporal.parameters.x * lerp(1.0, 0.5, saturate(speed / 16.0));
    resolvedColor.Store(pixel, float4(saneColor(lerp(current, history, weight)), 1.0));
}
