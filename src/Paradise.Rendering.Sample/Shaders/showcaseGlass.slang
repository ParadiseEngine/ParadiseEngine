// Scene-color capture is rebound by MaterialTarget after resize and switch transitions.
#include "Common/pbrCore.slang"
[[vk::binding(7, 2)]] Texture2D<float4> sceneColorTexture;
[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    return pbrTransformVertex(input, float3(0.0));
}
[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    uint width, height;
    sceneColorTexture.GetDimensions(width, height);
    float4 surface = shadePbr(input);
    // The disabled capture binds a one-pixel fallback: ordinary tinted transparency.
    if (width <= 1) return surface;
    float2 uv = input.position.xy / float2(width, height);
    float3 behind = sceneColorTexture.SampleLevel(baseColorSampler, uv + float2(0.012, 0.0), 0).rgb;
    return float4(lerp(behind, surface.rgb, 0.2), 0.85);
}
