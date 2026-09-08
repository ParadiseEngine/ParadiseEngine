#include "Common/pbrCore.slang"

[[vk::binding(1, 0)]]
StructuredBuffer<DrawUniforms> instanceDraws;

[shader("vertex")]
VertexOutput vertexMain(VertexInput input, uint instanceId : SV_InstanceID)
{
    return pbrTransformVertex(input.position, input.normal, input.texcoord, input.tangent, instanceDraws[instanceId]);
}

[shader("vertex")]
VertexOutput vertexMainSkinned(VertexInputSkinned input, uint instanceId : SV_InstanceID)
{
    DrawUniforms instance = instanceDraws[instanceId];
    float4x4 skin = float4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    if (dot(input.weights, float4(1, 1, 1, 1)) > 0.0001)
    {
        uint base = (uint)instance.highlight.y;
        skin = jointMatrices[base + (uint)input.joints.x] * input.weights.x
             + jointMatrices[base + (uint)input.joints.y] * input.weights.y
             + jointMatrices[base + (uint)input.joints.z] * input.weights.z
             + jointMatrices[base + (uint)input.joints.w] * input.weights.w;
    }
    return pbrTransformVertex(mul(skin, float4(input.position, 1)).xyz,
        mul((float3x3)skin, input.normal), input.texcoord,
        float4(mul((float3x3)skin, input.tangent.xyz), input.tangent.w), instance);
}

[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    return shadePbr(input);
}
