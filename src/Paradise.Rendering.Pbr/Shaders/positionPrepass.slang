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

// The same palette buffer the main pass binds, in this program's own group 1. Without it a skinned
// mesh would write bind-pose world positions here and the SSAO it feeds would occlude where the
// character is not.
[[vk::binding(0, 1)]]
StructuredBuffer<float4x4> jointMatrices;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

// Every attribute of the skinned stream, not just the ones read — the vertex layout is reflected
// from these fields, so a short struct would compute a stride that does not match the buffer.
struct VertexInputSkinned
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal   : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent  : TANGENT;
    [[vk::location(4)]] float4 joints   : JOINTS;
    [[vk::location(5)]] float4 weights  : WEIGHTS;
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

[shader("vertex")]
Varyings vertexMainSkinned(VertexInputSkinned input)
{
    float total = input.weights.x + input.weights.y + input.weights.z + input.weights.w;
    float3 position = input.position;
    if (total > 0.0001)
    {
        uint base = (uint)draw.highlight.y;
        float4x4 skin = jointMatrices[base + (uint)input.joints.x] * input.weights.x
                      + jointMatrices[base + (uint)input.joints.y] * input.weights.y
                      + jointMatrices[base + (uint)input.joints.z] * input.weights.z
                      + jointMatrices[base + (uint)input.joints.w] * input.weights.w;
        position = mul(skin, float4(input.position, 1.0)).xyz;
    }

    Varyings output;
    output.position = mul(draw.mvp, float4(position, 1.0));
    output.worldPos = mul(draw.model, float4(position, 1.0)).xyz;
    return output;
}

[shader("fragment")]
float4 fragmentMain(Varyings input) : SV_Target
{
    return float4(input.worldPos, 1.0);
}
