// compute.slang — the compute-support fixture. Pins two things on every PR:
//   1. slangc's reflection shape for RW resources (access + storage format), which
//      ShaderProgramLoader's StorageTexture/StorageBuffer mapping is built against.
//   2. The end-to-end path in ComputeGpuTests: this kernel writes a recognisable gradient
//      into a storage texture that a render pass then samples onto the headless target.

struct FillParams
{
    float4 tint; // rgb: multiplier, a: unused
};

[[vk::binding(0, 0)]]
ConstantBuffer<FillParams> fillParams;

[[vk::binding(1, 0)]]
StructuredBuffer<float> readValues;

[[vk::binding(2, 0)]]
RWStructuredBuffer<float> writeValues;

// The kernel only writes this rgba16f output, so use WTexture2D and a matching write-only layout.
// [format] is required: without it slangc defaults the WGSL to rgba32float
// while reflecting no format at all, which the loader turns into a load-time error.
[[vk::binding(3, 0)]]
[format("rgba16f")] // GLSL image-format names — slangc rejects WebGPU-style "rgba16float"
WTexture2D<float4> outputTexture;

[shader("compute")]
[numthreads(8, 8, 1)]
void computeMain(uint3 id: SV_DispatchThreadID)
{
    uint width;
    uint height;
    outputTexture.GetDimensions(width, height);
    if (id.x >= width || id.y >= height)
    {
        return;
    }
    float u = (width > 1) ? id.x / float(width - 1) : 0.0;
    float v = (height > 1) ? id.y / float(height - 1) : 0.0;
    float boost = readValues[0];
    outputTexture.Store(id.xy, float4(fillParams.tint.rgb * float3(u, v, 1.0 - u) * boost, 1.0));
    if (id.x == 0 && id.y == 0)
    {
        writeValues[0] = boost * 2.0;
    }
}
