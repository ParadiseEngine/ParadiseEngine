#include "Common/pbrCore.slang"

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    return pbrTransformVertex(input, float3(0.0, 2.0, 0.0));
}

[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    if (abs(input.worldPos.x) < 0.4 && abs(input.worldPos.y) < 0.4) discard;
    return shadePbr(input);
}
