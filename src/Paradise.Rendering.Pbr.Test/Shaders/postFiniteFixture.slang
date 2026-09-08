[[vk::binding(0, 0)]] Texture2D<float4> sourceColor;
[shader("vertex")]
float4 vertexMain(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * 2 - 1, 0, 1);
}
[shader("fragment")]
float4 fragmentMain(float4 position : SV_Position) : SV_Target
{
    float3 color = sourceColor.Load(int3(int2(position.xy), 0)).rgb;
    return float4(all(abs(color) <= 65504.0) ? float3(1) : float3(0), 1);
}
