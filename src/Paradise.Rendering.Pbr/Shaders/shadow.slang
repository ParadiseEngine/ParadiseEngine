// shadow.slang — depth-only shadow caster. Renders scene geometry from a light's point of view
// into the shadow atlas; the pipeline writes only depth (no fragment entry / no color target).
// The caller pre-multiplies the light view-projection with each object's model matrix (lightMvp),
// so this shader needs no separate model/frame data. The vertex layout matches pbr.slang's mesh
// (position at location 0); the other interleaved attributes are ignored.

struct ShadowDrawUniforms
{
    float4x4 lightMvp; // light view-projection × model
    float4 params;     // x: joint palette base index (skinned casters only), yzw unused
};

[[vk::binding(0, 0)]]
ConstantBuffer<ShadowDrawUniforms> draw;

// The same palette buffer the main pass binds, in this program's own group 1. A skinned caster
// must pose its shadow with the pose that drew its mesh, or the character animates while its
// shadow stays in bind pose — which reads as a shadow bug and sends you looking in the wrong place.
[[vk::binding(0, 1)]]
StructuredBuffer<float4x4> jointMatrices;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

// Declares every attribute of the skinned mesh stream, not just the ones it reads: the vertex
// layout is reflected from these fields, so omitting the unused middle ones would compute a
// stride that does not match the buffer and silently sample the wrong vertices.
struct VertexInputSkinned
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal   : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent  : TANGENT;
    [[vk::location(4)]] float4 joints   : JOINTS;
    [[vk::location(5)]] float4 weights  : WEIGHTS;
};

[shader("vertex")]
float4 vertexMain(VertexInput input) : SV_Position
{
    return mul(draw.lightMvp, float4(input.position, 1.0));
}

[shader("vertex")]
float4 vertexMainSkinned(VertexInputSkinned input) : SV_Position
{
    float total = input.weights.x + input.weights.y + input.weights.z + input.weights.w;
    float3 position = input.position;
    if (total > 0.0001)
    {
        uint base = (uint)draw.params.x;
        float4x4 skin = jointMatrices[base + (uint)input.joints.x] * input.weights.x
                      + jointMatrices[base + (uint)input.joints.y] * input.weights.y
                      + jointMatrices[base + (uint)input.joints.z] * input.weights.z
                      + jointMatrices[base + (uint)input.joints.w] * input.weights.w;
        position = mul(skin, float4(input.position, 1.0)).xyz;
    }
    return mul(draw.lightMvp, float4(position, 1.0));
}
