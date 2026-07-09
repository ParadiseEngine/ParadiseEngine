// positionPrepass.slang — SSAO depth pre-pass that writes world-space position to an Rgba32Float
// target (xyz = world position, w = 1 coverage; the target is cleared to 0 so background has w = 0).
// The PBR shader samples this (via textureLoad — no depth-format hazard, no matrix reconstruction)
// to estimate ambient occlusion. Reuses pbr.slang's DrawUniforms layout so it can share the main
// draw-UBO ring: only mvp (clip) and model (world position) are used.

struct DrawUniforms
{
    float4x4 mvp;
    float4x4 model;
    float4x4 normalMatrix;
    float4 highlight;
};

[[vk::binding(0, 0)]]
ConstantBuffer<DrawUniforms> draw;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

struct Varyings
{
    float4 position : SV_Position;
    float3 worldPos : WORLDPOS;
};

[shader("vertex")]
Varyings vertexMain(VertexInput input)
{
    Varyings output;
    output.position = mul(draw.mvp, float4(input.position, 1.0));
    output.worldPos = mul(draw.model, float4(input.position, 1.0)).xyz;
    return output;
}

[shader("fragment")]
float4 fragmentMain(Varyings input) : SV_Target
{
    return float4(input.worldPos, 1.0);
}
