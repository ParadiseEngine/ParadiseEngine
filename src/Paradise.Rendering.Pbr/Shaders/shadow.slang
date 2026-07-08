// shadow.slang — depth-only shadow caster. Renders scene geometry from a light's point of view
// into the shadow atlas; the pipeline writes only depth (no fragment entry / no color target).
// The caller pre-multiplies the light view-projection with each object's model matrix (lightMvp),
// so this shader needs no separate model/frame data. The vertex layout matches pbr.slang's mesh
// (position at location 0); the other interleaved attributes are ignored.

struct ShadowDrawUniforms
{
    float4x4 lightMvp; // light view-projection × model
};

[[vk::binding(0, 0)]]
ConstantBuffer<ShadowDrawUniforms> draw;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

[shader("vertex")]
float4 vertexMain(VertexInput input) : SV_Position
{
    return mul(draw.lightMvp, float4(input.position, 1.0));
}
