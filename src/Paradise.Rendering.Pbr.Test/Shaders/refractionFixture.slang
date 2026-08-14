// refractionFixture.slang — the scene-color capture consumed the way a water shader will:
// a custom material program whose fragment samples PbrRenderer.SceneColorView (bound as the
// group-2 extra entry) at this fragment's own screen position — i.e. "what is BEHIND me".
// material.procColorA.xy carries the screen size (the same free-lane trick real consumers use).
#include "Common/pbrCore.slang"

[[vk::binding(7, 2)]]
Texture2D<float4> sceneColorTexture;

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    return pbrTransformVertex(input, float3(0.0, 0.0, 0.0));
}

[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    float2 screenUv = input.position.xy / material.procColorA.xy;
    float3 behind = sceneColorTexture.SampleLevel(baseColorSampler, screenUv, 0.0).rgb;
    // Emit the captured color as EMISSIVE-only so the test reads it back nearly verbatim
    // (no lighting variance), with full coverage.
    return float4(behind, 1.0);
}
