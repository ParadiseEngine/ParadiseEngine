#include "Common/pbrInstancing.slang"

struct CustomMaterial { float4 tint; };
[[vk::binding(7, 2)]] ConstantBuffer<CustomMaterial> customMaterial;

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    return pbrTransformVertex(input, float3(0));
}

[shader("vertex")]
VertexOutput vertexMainInstanced(VertexInput input, uint instanceId : SV_InstanceID)
{
    return pbrTransformVertexInstanced(input, instanceId);
}

float4 shadeCustom(FragmentInput input, DrawUniforms objectDraw)
{
    // Model-dependent color and holes expose accidentally reading the first draw's uniform.
    float3 origin = mul(objectDraw.model, float4(0, 0, 0, 1)).xyz;
    if (input.objectPos.y > 0.1 && input.objectPos.x > origin.x * 0.08) discard;
    PbrSurface surface = resolvePbrSurface(input);
    surface.emissive += customMaterial.tint.rgb * (0.2 + abs(origin.x) * 0.2);
    return shadeSurface(input, surface);
}

[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    return shadeCustom(input, draw);
}

[shader("fragment")]
float4 fragmentMainInstanced(InstancedFragmentInput input) : SV_Target
{
    return shadeCustom(input.surface, pbrInstanceDraw(input.instanceDrawIndex));
}
