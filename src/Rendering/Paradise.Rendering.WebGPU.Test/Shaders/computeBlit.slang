// computeBlit.slang — the raster half of the compute round-trip test: a fullscreen triangle
// sampling the texture the compute fixture wrote, into the headless backbuffer for ReadbackColor.

[[vk::binding(0, 0)]]
Texture2D<float4> sourceTexture;

[[vk::binding(1, 0)]]
SamplerState sourceSampler;

struct BlitVaryings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
BlitVaryings vertexMain(uint vertexIndex: SV_VertexID)
{
    // The 3-vertex fullscreen-triangle trick (same as the engine's composite pass).
    BlitVaryings output;
    float2 uv = float2((vertexIndex << 1) & 2, vertexIndex & 2);
    output.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.uv = uv;
    return output;
}

[shader("fragment")]
float4 fragmentMain(BlitVaryings input) : SV_Target
{
    return sourceTexture.Sample(sourceSampler, input.uv);
}
