// bloom.slang — physically-plausible bloom on the linear-HDR scene target (the "Next-gen post
// processing in Call of Duty: Advanced Warfare" progressive dual-filter). Three fullscreen passes
// share one bind-group layout (source texture + sampler + params):
//   brightPass  — soft-knee threshold of the HDR scene → bloom mip 0 (half res)
//   downsample  — 13-tap filtered box downsample, mip i → mip i+1
//   upsample    — 9-tap tent upsample, mip i+1 → mip i, ADDITIVE blend (accumulates the chain)
// The filter kernels read the SOURCE texel size from textureDimensions, so no per-level uniform is
// needed. Output is linear HDR; composite.slang adds it back scaled by intensity.

struct BloomParams
{
    float4 params; // x: threshold, y: knee, zw: unused
};

[[vk::binding(0, 0)]]
Texture2D<float4> src;
[[vk::binding(1, 0)]]
SamplerState samp;
[[vk::binding(2, 0)]]
ConstantBuffer<BloomParams> bloom;

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

float2 srcTexel()
{
    uint w, h;
    src.GetDimensions(w, h);
    return float2(1.0 / float(w), 1.0 / float(h));
}

// Soft-knee threshold (Unity/COD): quadratic ramp across a knee width so the bloom onset isn't a
// hard clip. threshold t, knee k → curve = (t-k, 2k, 0.25/k).
[shader("fragment")]
float4 brightFragment(Varyings input) : SV_Target
{
    float3 c = src.Sample(samp, input.uv).rgb;
    float t = bloom.params.x;
    float k = max(bloom.params.y, 1e-4);
    float br = max(c.r, max(c.g, c.b));
    float soft = clamp(br - (t - k), 0.0, 2.0 * k);
    soft = soft * soft * (0.25 / k);
    float contrib = max(soft, br - t) / max(br, 1e-4);
    return float4(c * contrib, 1.0);
}

// 13-tap downsample (COD): a center box + 4 corner boxes, weighted to kill fireflies.
[shader("fragment")]
float4 downsampleFragment(Varyings input) : SV_Target
{
    float2 uv = input.uv;
    float2 t = srcTexel();
    float3 a = src.Sample(samp, uv + t * float2(-2, 2)).rgb;
    float3 b = src.Sample(samp, uv + t * float2( 0, 2)).rgb;
    float3 c = src.Sample(samp, uv + t * float2( 2, 2)).rgb;
    float3 d = src.Sample(samp, uv + t * float2(-2, 0)).rgb;
    float3 e = src.Sample(samp, uv).rgb;
    float3 f = src.Sample(samp, uv + t * float2( 2, 0)).rgb;
    float3 g = src.Sample(samp, uv + t * float2(-2,-2)).rgb;
    float3 h = src.Sample(samp, uv + t * float2( 0,-2)).rgb;
    float3 i = src.Sample(samp, uv + t * float2( 2,-2)).rgb;
    float3 j = src.Sample(samp, uv + t * float2(-1, 1)).rgb;
    float3 k = src.Sample(samp, uv + t * float2( 1, 1)).rgb;
    float3 l = src.Sample(samp, uv + t * float2(-1,-1)).rgb;
    float3 m = src.Sample(samp, uv + t * float2( 1,-1)).rgb;
    float3 result = e * 0.125;
    result += (a + c + g + i) * 0.03125;
    result += (b + d + f + h) * 0.0625;
    result += (j + k + l + m) * 0.125;
    return float4(result, 1.0);
}

// 9-tap tent upsample (COD), additive-blended onto the larger mip.
[shader("fragment")]
float4 upsampleFragment(Varyings input) : SV_Target
{
    float2 uv = input.uv;
    float2 t = srcTexel();
    float3 result = src.Sample(samp, uv + t * float2(-1, 1)).rgb * 1.0;
    result += src.Sample(samp, uv + t * float2( 0, 1)).rgb * 2.0;
    result += src.Sample(samp, uv + t * float2( 1, 1)).rgb * 1.0;
    result += src.Sample(samp, uv + t * float2(-1, 0)).rgb * 2.0;
    result += src.Sample(samp, uv).rgb * 4.0;
    result += src.Sample(samp, uv + t * float2( 1, 0)).rgb * 2.0;
    result += src.Sample(samp, uv + t * float2(-1,-1)).rgb * 1.0;
    result += src.Sample(samp, uv + t * float2( 0,-1)).rgb * 2.0;
    result += src.Sample(samp, uv + t * float2( 1,-1)).rgb * 1.0;
    return float4(result * (1.0 / 16.0), 1.0);
}
