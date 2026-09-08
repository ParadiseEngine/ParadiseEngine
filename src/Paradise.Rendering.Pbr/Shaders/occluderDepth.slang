struct DrawUniforms
{
    float4x4 mvp;
    float4x4 model;
    float4x4 normalMatrix;
    float4 highlight;
};
[[vk::binding(0, 0)]] ConstantBuffer<DrawUniforms> draw;
struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent : TANGENT;
};
[shader("vertex")]
float4 vertexMain(VertexInput input) : SV_Position
{
    return mul(draw.mvp, float4(input.position, 1.0));
}
