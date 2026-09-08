[[vk::binding(0, 0)]] Texture2D<float4> velocityTexture;

struct Varyings { float4 position : SV_Position; };

[shader("vertex")]
Varyings vertexMain(uint vertexIndex : SV_VertexID)
{
    Varyings output;
    float2 uv = float2((vertexIndex << 1) & 2, vertexIndex & 2);
    output.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return output;
}

[shader("fragment")]
float4 fragmentMain(Varyings input) : SV_Target
{
    float4 velocity = velocityTexture.Load(int3(int2(input.position.xy), 0));
    return float4(velocity.xy * 2.0 + 0.5, velocity.w, velocity.z);
}
