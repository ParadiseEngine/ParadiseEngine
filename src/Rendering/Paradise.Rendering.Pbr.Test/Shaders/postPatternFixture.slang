struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};
[shader("vertex")]
Varyings patternVertex(uint id : SV_VertexID)
{
    float2 p = float2((id << 1) & 2, id & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = float2(p.x, 1.0 - p.y);
    return output;
}
[shader("fragment")]
float4 patternFragment(Varyings input) : SV_Target
{
    float stripe = 0.5 + 0.35 * sin(input.uv.x * 120.0) * sin(input.uv.y * 100.0);
    return float4(stripe * 0.5, 0.1 + input.uv.x * 0.4, 0.1 + input.uv.y * 0.2, 1.0);
}
