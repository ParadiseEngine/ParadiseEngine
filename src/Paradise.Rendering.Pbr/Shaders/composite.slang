// Linear HDR bloom and tonemapping; OETF is used only for direct presentation.

struct CompositeUniforms
{
    float4 tone;   // x: tonemap mode, y: exposure, z: white, w: bloom intensity (0 = no bloom)
};

[[vk::binding(0, 0)]]
Texture2D<float4> sceneHdr;
[[vk::binding(1, 0)]]
SamplerState sceneSampler;
[[vk::binding(2, 0)]]
Texture2D<float4> bloomTex;
[[vk::binding(3, 0)]]
ConstantBuffer<CompositeUniforms> comp;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
Varyings compositeVertex(uint vertexId : SV_VertexID)
{
    // Fullscreen triangle: p ∈ {0,2}; clip = p*2-1 ∈ {-1,3}. On-screen fragments have p ∈ [0,1].
    // Texture uv: x = p.x, y = 1-p.y (framebuffer row 0 = top = NDC y=+1).
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = float2(p.x, 1.0 - p.y);
    return output;
}

#include "Common/tonemapping.slang"

float3 composite(float2 uv)
{
    float3 hdr = sceneHdr.Sample(sceneSampler, uv).rgb;
    if (comp.tone.w > 0.0)
        hdr += bloomTex.Sample(sceneSampler, uv).rgb * comp.tone.w;
    return applyTonemap(hdr, comp.tone.y, comp.tone.z, comp.tone.x);
}

[shader("fragment")]
float4 compositeFragment(Varyings input) : SV_Target
{
    return float4(composite(input.uv), 1.0);
}

[shader("fragment")]
float4 compositeFragmentSrgb(Varyings input) : SV_Target
{
    return float4(linearToSrgb(composite(input.uv)), 1.0);
}
