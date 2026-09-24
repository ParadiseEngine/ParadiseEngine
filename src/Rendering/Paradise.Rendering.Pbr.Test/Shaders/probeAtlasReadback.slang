[[vk::binding(0, 0)]]
Texture2D<float4> irradiance;
[[vk::binding(1, 0)]]
Texture2D<float4> visibility;
[[vk::binding(2, 0)]]
RWStructuredBuffer<float4> atlasTexels;

[shader("compute")]
[numthreads(64, 1, 1)]
void readbackMain(uint3 id: SV_DispatchThreadID)
{
    uint width, height;
    irradiance.GetDimensions(width, height);
    uint index = id.x;
    if (index < width * height)
    {
        atlasTexels[index] = irradiance.Load(int3(index % width, index / width, 0));
        return;
    }
    uint firstVisibility = width * height;
    visibility.GetDimensions(width, height);
    index -= firstVisibility;
    if (index < width * height)
        atlasTexels[firstVisibility + index] = visibility.Load(int3(index % width, index / width, 0));
}
