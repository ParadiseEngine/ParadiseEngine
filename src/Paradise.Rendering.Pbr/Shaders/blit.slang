// blit.slang — the scene-color capture copy: after the opaque bucket (and sky) land in the HDR
// target, this blits them into the sceneColor texture that blend materials sample for
// screen-space refraction (the engine's screen_texture / _CameraOpaqueTexture / SceneColor
// analog) — and packs the OPAQUE SCENE DEPTH into the otherwise-unused alpha channel, the
// rejection signal depth-aware refraction needs: a refracted sample whose captured depth is
// CLOSER than the water fragment belongs to geometry in front of / above the surface and must
// not be smeared into the water. Deliberately not the composite shader: composite tonemaps;
// capture must stay LINEAR HDR.

[[vk::binding(0, 0)]]
Texture2D<float4> sourceTexture;

[[vk::binding(1, 0)]]
SamplerState sourceSampler;

// Depth32Float viewed as unfilterable float (textureLoad only) — the loader's name-override
// table maps this exact name to the UnfilterableFloatTexture binding type.
[[vk::binding(2, 0)]]
Texture2D<float4> captureDepthTexture;

struct BlitVaryings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
BlitVaryings vertexMain(uint vertexIndex: SV_VertexID)
{
    // The 3-vertex fullscreen-triangle trick, same as composite.
    BlitVaryings output;
    float2 uv = float2((vertexIndex << 1) & 2, vertexIndex & 2);
    output.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.uv = uv;
    return output;
}

[shader("fragment")]
float4 fragmentMain(BlitVaryings input) : SV_Target
{
    float3 color = sourceTexture.Sample(sourceSampler, input.uv).rgb;
    float depth = captureDepthTexture.Load(int3(int2(input.position.xy), 0)).r;
    return float4(color, depth);
}
