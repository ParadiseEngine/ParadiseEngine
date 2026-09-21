#include "Common/probes.slang"

struct DebugUniforms
{
    float4x4 viewProjection;
    float4 right;
    float4 up;
};
[[vk::binding(0, 0)]] ConstantBuffer<DebugUniforms> debug;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    nointerpolation uint probe : TEXCOORD1;
};

[shader("vertex")]
Varyings vertexMain(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    static const float2 corners[6] = {
        float2(-1, -1), float2(1, -1), float2(-1, 1),
        float2(-1, 1), float2(1, -1), float2(1, 1)
    };
    Varyings output;
    output.uv = corners[vertex];
    float3 world = probePosition(instance) + debug.right.w *
        (debug.right.xyz * output.uv.x + debug.up.xyz * output.uv.y);
    output.position = mul(debug.viewProjection, float4(world, 1));
    output.probe = instance;
    return output;
}

[shader("fragment")]
float4 fragmentMain(Varyings input) : SV_Target
{
    float r2 = dot(input.uv, input.uv);
    if (r2 > 1) discard;
    float3 color = probeStates[input.probe].w > 0.5 ? float3(0.1, 1, 0.3) : float3(1, 0.05, 0.02);
    return float4(color * (0.35 + 0.65 * sqrt(1 - r2)), 1);
}
