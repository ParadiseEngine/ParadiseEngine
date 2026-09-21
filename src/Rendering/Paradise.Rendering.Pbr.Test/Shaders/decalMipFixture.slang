#include "Common/pbrCore.slang"
[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    return pbrTransformVertex(input, float3(0));
}
[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    // Level one of the red/blue 2x2 fixture is purple. A base-only view clamps this
    // lookup to a red corner of level zero, even though the texture owns both levels.
    return decalAtlas.SampleLevel(decalSampler, float3(0.01, 0.01, 0), 1);
}
