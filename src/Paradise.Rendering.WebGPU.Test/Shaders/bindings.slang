// Golden fixture for the M2 reflection contract: exercises every global-parameter shape the
// PBR migration needs — constant buffers with scalar/vector/matrix/array-of-struct members
// across two bind groups, plus a texture + sampler pair. The companion test pins the slangc
// -reflection-json output shape (kinds, spaces, indices, uniform field offsets) so schema
// drift breaks a test, not a frame.

struct PointLight
{
    float4 positionAndRange;
    float4 color;
};

struct FrameParams
{
    float4x4 viewProjection;
    float4 cameraPosition;
    float4 ambientColor;
    PointLight lights[4];
    float exposure;
    float pad0;
    float pad1;
    float pad2;
};

struct DrawParams
{
    float4x4 model;
    float4 tint;
};

[[vk::binding(0, 0)]]
ConstantBuffer<DrawParams> drawParams;

[[vk::binding(0, 1)]]
ConstantBuffer<FrameParams> frameParams;

[[vk::binding(1, 1)]]
Texture2D baseColorTexture;

[[vk::binding(2, 1)]]
SamplerState baseColorSampler;

struct VsIn
{
    float3 pos : POSITION;
    float2 uv : TEXCOORD0;
};

struct VsOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut vs_main(VsIn input)
{
    VsOut o;
    o.pos = mul(frameParams.viewProjection, mul(drawParams.model, float4(input.pos, 1.0)));
    o.uv = input.uv;
    return o;
}

[shader("fragment")]
float4 fs_main(VsOut input) : SV_Target
{
    float4 texel = baseColorTexture.Sample(baseColorSampler, input.uv);
    float3 lit = texel.rgb * frameParams.ambientColor.rgb * frameParams.exposure;
    for (int i = 0; i < 4; i++)
    {
        lit += frameParams.lights[i].color.rgb * frameParams.lights[i].positionAndRange.w;
    }
    return float4(lit, texel.a) * drawParams.tint;
}
