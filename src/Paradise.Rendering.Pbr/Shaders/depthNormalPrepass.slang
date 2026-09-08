// Opaque depth/normal prepass: Depth32Float and Rgba16Float (world normal, coverage=1; clear=0).
// Effects reconstruct world position through frame.invViewProj. Reuses the main draw uniform
// layout.

struct DrawUniforms
{
    float4x4 mvp;
    float4x4 model;
    float4x4 normalMatrix;
    float4 highlight;
};

[[vk::binding(0, 0)]]
ConstantBuffer<DrawUniforms> draw;

// The same palette buffer the main pass binds, in this program's own group 1. Without it a skinned
// mesh would write bind-pose depth here and the SSAO it feeds would occlude where the character
// is not.
[[vk::binding(0, 1)]]
StructuredBuffer<float4x4> jointMatrices;

// Every attribute of the rigid stream, not just the two read: the vertex layout is reflected from
// these fields, so a shorter struct computes a stride that does not match the buffer and the pass
// silently reads interleaved normals and uvs as positions. (The position-only pre-pass this
// replaced did exactly that, and nothing but the SSAO's texture told.)
struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal   : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent  : TANGENT;
};

// Likewise every attribute of the skinned stream.
struct VertexInputSkinned
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal   : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent  : TANGENT;
    [[vk::location(4)]] float4 joints   : JOINTS;
    [[vk::location(5)]] float4 weights  : WEIGHTS;
};

struct Varyings
{
    float4 position : SV_Position;
    float3 worldNormal : NORMAL;
};

[shader("vertex")]
Varyings vertexMain(VertexInput input)
{
    Varyings output;
    output.position = mul(draw.mvp, float4(input.position, 1.0));
    output.worldNormal = mul((float3x3)draw.normalMatrix, input.normal);
    return output;
}

[shader("vertex")]
Varyings vertexMainSkinned(VertexInputSkinned input)
{
    float total = input.weights.x + input.weights.y + input.weights.z + input.weights.w;
    float3 position = input.position;
    float3 normal = input.normal;
    if (total > 0.0001)
    {
        uint base = (uint)draw.highlight.y;
        float4x4 skin = jointMatrices[base + (uint)input.joints.x] * input.weights.x
                      + jointMatrices[base + (uint)input.joints.y] * input.weights.y
                      + jointMatrices[base + (uint)input.joints.z] * input.weights.z
                      + jointMatrices[base + (uint)input.joints.w] * input.weights.w;
        position = mul(skin, float4(input.position, 1.0)).xyz;
        normal = mul((float3x3)skin, input.normal);
    }

    Varyings output;
    output.position = mul(draw.mvp, float4(position, 1.0));
    output.worldNormal = mul((float3x3)draw.normalMatrix, normal);
    return output;
}

// The geometric normal, front-face corrected the way pbrCore shades it: the pipeline does not
// cull, so a back-facing fragment reports the normal of the side the camera sees.
[shader("fragment")]
float4 fragmentMain(Varyings input, bool isFrontFace: SV_IsFrontFace) : SV_Target
{
    float3 n = normalize(input.worldNormal);
    return float4(isFrontFace ? n : -n, 1.0);
}
