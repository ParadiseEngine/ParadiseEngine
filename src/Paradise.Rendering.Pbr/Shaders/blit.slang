// blit.slang — a fullscreen passthrough copy. Used by the scene-color capture: after the opaque
// bucket (and sky) land in the HDR target, this blits them into the sceneColor texture that blend
// materials sample for screen-space refraction (the engine's screen_texture / _CameraOpaqueTexture
// / SceneColor analog). Deliberately not the composite shader: composite tonemaps and binds four
// resources; capture must stay LINEAR HDR and this stays two bindings.

[[vk::binding(0, 0)]]
Texture2D<float4> sourceTexture;

[[vk::binding(1, 0)]]
SamplerState sourceSampler;

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
    return sourceTexture.Sample(sourceSampler, input.uv);
}
