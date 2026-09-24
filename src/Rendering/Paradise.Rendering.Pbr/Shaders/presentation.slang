#include "Common/tonemapping.slang"

[[vk::binding(0, 0)]] Texture2D<float4> displayColor;
[[vk::binding(1, 0)]] SamplerState displaySampler;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
Varyings postVertex(uint id : SV_VertexID)
{
    float2 p = float2((id << 1) & 2, id & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = float2(p.x, 1.0 - p.y);
    return output;
}

[shader("fragment")]
float4 presentLinear(Varyings input) : SV_Target
{
    return float4(displayColor.SampleLevel(displaySampler, input.uv, 0.0).rgb, 1.0);
}

[shader("fragment")]
float4 presentSrgb(Varyings input) : SV_Target
{
    return float4(linearToSrgb(displayColor.SampleLevel(displaySampler, input.uv, 0.0).rgb), 1.0);
}
