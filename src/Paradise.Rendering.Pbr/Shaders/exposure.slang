struct ExposureUniforms
{
    float4 limits; // min/max EV, compensation, middle gray
    float4 adaptation; // brighten/darken rates, seconds, history valid
    float4 mode; // automatic
};
[[vk::binding(0, 0)]] Texture2D<float4> sourceColor;
[[vk::binding(1, 0)]] SamplerState linearSampler;
[[vk::binding(2, 0)]] ConstantBuffer<ExposureUniforms> exposure;
[[vk::binding(3, 0)]] Texture2D<float4> previousExposure;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
Varyings exposureVertex(uint id : SV_VertexID)
{
    float2 p = float2((id << 1) & 2, id & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = float2(p.x, 1.0 - p.y);
    return output;
}

float2 reduceLog(int2 outputPixel, bool first)
{
    uint width, height;
    sourceColor.GetDimensions(width, height);
    float2 sum = float2(0.0);
    for (int y = 0; y < 4; y++)
    for (int x = 0; x < 4; x++)
    {
        int2 p = outputPixel * 4 + int2(x, y);
        if (p.x >= int(width) || p.y >= int(height)) continue;
        float4 color = sourceColor.Load(int3(p, 0));
        float luma = dot(max(color.rgb, 0.0), float3(0.2126, 0.7152, 0.0722));
        sum += first ? float2(log2(clamp(luma, 1.0 / 65536.0, 65536.0)), 1.0) : color.rg;
    }
    // Both sum and coverage use the same divisor: odd edge blocks keep their correct weight,
    // while the half-float intermediates never overflow their pixel count.
    return sum / 16.0;
}

[shader("fragment")]
float4 logLuminanceFragment(Varyings input) : SV_Target
{
    return float4(reduceLog(int2(input.position.xy), true), 0.0, 1.0);
}

[shader("fragment")]
float4 reduceLuminanceFragment(Varyings input) : SV_Target
{
    return float4(reduceLog(int2(input.position.xy), false), 0.0, 1.0);
}

[shader("fragment")]
float4 adaptExposureFragment(Varyings input) : SV_Target
{
    float target = exposure.limits.z;
    if (exposure.mode.x > 0.5)
    {
        float2 logLuma = sourceColor.Load(int3(0, 0, 0)).rg;
        target += log2(exposure.limits.w) - logLuma.x / max(logLuma.y, 1e-7);
    }
    target = clamp(target, exposure.limits.x, exposure.limits.y);
    float previous = clamp(previousExposure.Load(int3(0, 0, 0)).r, exposure.limits.x, exposure.limits.y);
    float speed = target > previous ? exposure.adaptation.x : exposure.adaptation.y;
    float value = exposure.adaptation.w > 0.5
        ? lerp(previous, target, 1.0 - exp(-speed * exposure.adaptation.z)) : target;
    return float4(clamp(value, exposure.limits.x, exposure.limits.y), 0.0, 0.0, 1.0);
}

[shader("fragment")]
float4 applyExposureFragment(Varyings input) : SV_Target
{
    float ev = previousExposure.Load(int3(0, 0, 0)).r;
    return float4(min(max(sourceColor.SampleLevel(linearSampler, input.uv, 0.0).rgb, 0.0) * exp2(ev), 65504.0), 1.0);
}
