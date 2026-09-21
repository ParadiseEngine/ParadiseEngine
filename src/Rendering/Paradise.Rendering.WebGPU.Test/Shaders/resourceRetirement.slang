[[vk::binding(0, 0)]]
StructuredBuffer<uint> multiplier;

[[vk::binding(1, 0)]]
Texture2D<float4> sourceTexture;

[[vk::binding(2, 0)]]
SamplerState sourceSampler;

[[vk::binding(3, 0)]]
RWStructuredBuffer<uint> result;

[shader("compute")]
[numthreads(1, 1, 1)]
void computeMain(uint3 id: SV_DispatchThreadID)
{
    float red = sourceTexture.SampleLevel(sourceSampler, float2(0.5, 0.5), 0.0).r;
    result[0] = uint(round(red * 255.0)) * multiplier[0];
}
