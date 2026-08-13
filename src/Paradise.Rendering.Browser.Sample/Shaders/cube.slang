// M2 demo shader: one constant buffer per group-0 draw data (MVP + tint), a sampled
// checkerboard texture + sampler in group 1, simple N·L lambert against a fixed light.
// Everything the binding pipeline supports in one small shader.

struct DrawParams
{
    float4x4 mvp;
    float4 tint;
};

[[vk::binding(0, 0)]]
ConstantBuffer<DrawParams> drawParams;

[[vk::binding(0, 1)]]
Texture2D baseColorTexture;

[[vk::binding(1, 1)]]
SamplerState baseColorSampler;

struct VsIn
{
    float3 pos : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

struct VsOut
{
    float4 pos : SV_Position;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut vs_main(VsIn input)
{
    VsOut o;
    o.pos = mul(drawParams.mvp, float4(input.pos, 1.0));
    o.normal = input.normal;
    o.uv = input.uv;
    return o;
}

[shader("fragment")]
float4 fs_main(VsOut input) : SV_Target
{
    float3 lightDir = normalize(float3(0.5, 1.0, 0.6));
    float ndotl = max(dot(normalize(input.normal), lightDir), 0.0);
    float lighting = 0.25 + 0.75 * ndotl; // cheap ambient + lambert
    float4 texel = baseColorTexture.Sample(baseColorSampler, input.uv);
    return float4(texel.rgb * lighting, 1.0) * drawParams.tint;
}
