// Tone-maps the SDF path's linear HDR into the final target. Use the linear entry for sRGB
// formats, which encode in hardware, and the encoding entry for everything else.

#include "Common/sdfColor.slang"

struct SdfCompositeUniforms
{
    float4 tone; // x: tonemap mode, y: exposure
};

[[vk::binding(0, 0)]] Texture2D<float4> sceneHdr;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<SdfCompositeUniforms> sdfComposite;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
Varyings compositeVertex(uint vertexId : SV_VertexID)
{
    // Texture uv: x = p.x, y = 1 - p.y (framebuffer row 0 = top = NDC y = +1).
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = float2(p.x, 1.0 - p.y);
    return output;
}

float3 composite(float2 uv)
{
    float3 hdr = sceneHdr.SampleLevel(sceneSampler, uv, 0.0).rgb;
    return sdfApplyTonemap(hdr, sdfComposite.tone.y, sdfComposite.tone.x);
}

[shader("fragment")]
float4 compositeFragment(Varyings input) : SV_Target
{
    return float4(composite(input.uv), 1.0);
}

[shader("fragment")]
float4 compositeFragmentSrgb(Varyings input) : SV_Target
{
    return float4(sdfLinearToSrgb(composite(input.uv)), 1.0);
}
