// depthProbeFixture.slang — visualizes the capture's ALPHA channel (the opaque scene depth the
// blit packs for depth-aware refraction) as grayscale, so a readback can assert that geometry
// pixels carry a nearer depth than background pixels.
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
    // Filtered sampling is fine for THIS flat-region probe, but real consumers must read the
    // packed depth via textureLoad — bilinear across a depth edge interpolates a depth that
    // belongs to no surface (see the SceneColorView doc).
    float capturedDepth = sceneColorTexture.SampleLevel(baseColorSampler, screenUv, 0.0).a;
    // Device-depth deltas are tiny (~0.04 between a mid-distance cube and the far plane) and an
    // 8-bit tonemapped readback flattens them — amplify: geometry (depth < 1) reads WHITE, the
    // cleared background (depth == 1) reads BLACK.
    float nearFactor = saturate((1.0 - capturedDepth) * 40.0);
    return float4(nearFactor, nearFactor, nearFactor, 1.0);
}
