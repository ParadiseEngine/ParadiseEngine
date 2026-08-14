// proceduralCompute.slang — the compute half of the compute demo scene: an animated plasma
// written into a storage texture each frame from a compute pass inside a SubmitOffscreen
// stream. The raster half (computeDisplay.slang) samples it to the backbuffer.

struct ComputeParams
{
    float4 timeAndSize; // x: elapsed seconds, y: texture size (square), zw: unused
};

[[vk::binding(0, 0)]]
ConstantBuffer<ComputeParams> computeParams;

// Write-only + explicit [format]: core WebGPU allows read_write storage only on r32*, and the
// format attribute is REQUIRED (without it slangc silently defaults the WGSL to rgba32float
// while reflecting no format — the loader refuses to guess).
[[vk::binding(1, 0)]]
[format("rgba16f")]
WTexture2D<float4> plasmaTexture;

[shader("compute")]
[numthreads(8, 8, 1)]
void computeMain(uint3 id: SV_DispatchThreadID)
{
    float size = computeParams.timeAndSize.y;
    if (id.x >= uint(size) || id.y >= uint(size))
    {
        return;
    }
    float t = computeParams.timeAndSize.x;
    float2 p = float2(id.xy) / size * 6.2831853;
    float v = sin(p.x + t)
            + sin((p.y + t) * 0.5)
            + sin((p.x + p.y + t) * 0.5)
            + sin(sqrt(p.x * p.x + p.y * p.y + 1.0) + t * 1.7);
    float3 color = 0.5 + 0.5 * float3(sin(v * 3.14159), sin(v * 3.14159 + 2.094), sin(v * 3.14159 + 4.188));
    plasmaTexture.Store(id.xy, float4(color, 1.0));
}
