// computeDisplay.slang — the raster half of the compute demo: a fullscreen triangle sampling
// the plasma texture the compute pass wrote, into the backbuffer.

[[vk::binding(0, 0)]]
Texture2D<float4> plasmaTexture;

[[vk::binding(1, 0)]]
SamplerState plasmaSampler;

struct DisplayVaryings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
DisplayVaryings vertexMain(uint vertexIndex: SV_VertexID)
{
    DisplayVaryings output;
    float2 uv = float2((vertexIndex << 1) & 2, vertexIndex & 2);
    output.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.uv = uv;
    return output;
}

[shader("fragment")]
float4 fragmentMain(DisplayVaryings input) : SV_Target
{
    return plasmaTexture.Sample(plasmaSampler, input.uv);
}
