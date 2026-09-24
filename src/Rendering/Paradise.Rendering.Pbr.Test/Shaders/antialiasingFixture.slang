[[vk::binding(0, 0)]] ConstantBuffer<float4> signal;
struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};
[shader("vertex")]
Varyings vertexMain(uint index : SV_VertexID)
{
    Varyings output;
    float2 uv = float2((index << 1) & 2, index & 2);
    output.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.uv = uv;
    return output;
}
[shader("fragment")]
float4 fragmentMain(Varyings input) : SV_Target
{
    float value = signal.x;
    if (signal.z > 1.5)
        value += ((uint(input.position.x) + uint(input.position.y)) & 1) == 0 ? signal.y : -signal.y;
    else if (signal.z > 0.5)
        value = input.uv.x > input.uv.y * 0.47 + 0.2 ? 1.0 : 0.0;
    return float4(value, value, value, 1.0);
}
