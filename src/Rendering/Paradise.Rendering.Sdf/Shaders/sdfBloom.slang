// Progressive dual-filter bloom on linear HDR (Call of Duty: Advanced Warfare). Shared bindings
// drive a soft-knee bright pass, a 13-tap downsample and an additive 9-tap upsample. The source's
// dimensions supply the texel size; the composite scales the result by the bloom intensity.

struct SdfBloomUniforms
{
    float4 threshold; // x threshold, y knee
};

[[vk::binding(0, 0)]] Texture2D<float4> bloomSource;
[[vk::binding(1, 0)]] SamplerState bloomSampler;
[[vk::binding(2, 0)]] ConstantBuffer<SdfBloomUniforms> sdfBloom;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
Varyings bloomVertex(uint vertexId : SV_VertexID)
{
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = float2(p.x, 1.0 - p.y);
    return output;
}

float2 sourceTexel()
{
    uint width, height;
    bloomSource.GetDimensions(width, height);
    return float2(1.0 / (float)width, 1.0 / (float)height);
}

float3 tap(float2 uv)
{
    return bloomSource.SampleLevel(bloomSampler, uv, 0.0).rgb;
}

// Soft-knee threshold: a quadratic ramp across the knee, so the onset of bloom is not a hard clip.
[shader("fragment")]
float4 brightFragment(Varyings input) : SV_Target
{
    float3 color = tap(input.uv);
    float threshold = sdfBloom.threshold.x;
    float knee = max(sdfBloom.threshold.y, 1e-4);
    float brightness = max(color.r, max(color.g, color.b));
    float soft = clamp(brightness - (threshold - knee), 0.0, 2.0 * knee);
    soft = soft * soft * (0.25 / knee);
    float contribution = max(soft, brightness - threshold) / max(brightness, 1e-4);
    return float4(color * contribution, 1.0);
}

// 13 taps: a center box and four corner boxes with normalized weights.
[shader("fragment")]
float4 downsampleFragment(Varyings input) : SV_Target
{
    float2 uv = input.uv;
    float2 t = sourceTexel();
    float3 corners = tap(uv + t * float2(-2.0, 2.0)) + tap(uv + t * float2(2.0, 2.0))
        + tap(uv + t * float2(-2.0, -2.0)) + tap(uv + t * float2(2.0, -2.0));
    float3 edges = tap(uv + t * float2(0.0, 2.0)) + tap(uv + t * float2(-2.0, 0.0))
        + tap(uv + t * float2(2.0, 0.0)) + tap(uv + t * float2(0.0, -2.0));
    float3 inner = tap(uv + t * float2(-1.0, 1.0)) + tap(uv + t * float2(1.0, 1.0))
        + tap(uv + t * float2(-1.0, -1.0)) + tap(uv + t * float2(1.0, -1.0));
    return float4(tap(uv) * 0.125 + corners * 0.03125 + edges * 0.0625 + inner * 0.125, 1.0);
}

// A 9-tap tent, blended additively onto the larger level.
[shader("fragment")]
float4 upsampleFragment(Varyings input) : SV_Target
{
    float2 uv = input.uv;
    float2 t = sourceTexel();
    float3 corners = tap(uv + t * float2(-1.0, 1.0)) + tap(uv + t * float2(1.0, 1.0))
        + tap(uv + t * float2(-1.0, -1.0)) + tap(uv + t * float2(1.0, -1.0));
    float3 edges = tap(uv + t * float2(0.0, 1.0)) + tap(uv + t * float2(-1.0, 0.0))
        + tap(uv + t * float2(1.0, 0.0)) + tap(uv + t * float2(0.0, -1.0));
    return float4((corners + edges * 2.0 + tap(uv) * 4.0) * (1.0 / 16.0), 1.0);
}
