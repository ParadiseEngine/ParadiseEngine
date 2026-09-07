// rtao.slang — ray-traced ambient occlusion, the tracer's proving ground: for every pixel the
// depth + normal pre-pass covered, a few cosine-distributed rays into the scene's BVH, and the
// fraction that escape within a radius written as the ambient occlusion the scene pass multiplies
// its ambient by. It exists so the traversal can be validated against a picture before the probe
// passes depend on it; a game may also simply prefer it to SSAO.
#include "Common/bvh.slang"

struct RtaoUniforms
{
    float4 params;       // x: rays per pixel, y: max distance (world), z: frame index, w: normal bias
    float4 screen;       // xy: screen size (pixels), zw unused
    float4x4 invViewProj;
};

[[vk::binding(0, 0)]]
ConstantBuffer<RtaoUniforms> rtao;

[[vk::binding(1, 0)]]
Texture2D<float4> prepassNormalTexture;

// Same name as the scene's declaration: the loader forces UnfilterableFloat by it, which is what
// lets the pre-pass depth bind here.
[[vk::binding(2, 0)]]
Texture2D<float> prepassDepthTexture;

[[vk::binding(3, 0)]]
[format("rgba16f")]
WTexture2D<float4> rtaoTexture;

// PCG-style integer hash → [0,1), decorrelated per pixel and per frame.
uint pcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float hashToUnit(uint v)
{
    return float(pcg(v) & 0x00FFFFFFu) / 16777216.0;
}

// Cosine-weighted direction about n from two uniform numbers.
float3 cosineDirection(float3 n, float u1, float u2)
{
    float r = sqrt(u1);
    float phi = 6.2831853 * u2;
    float3 t = normalize(abs(n.y) < 0.99 ? cross(float3(0.0, 1.0, 0.0), n) : cross(float3(1.0, 0.0, 0.0), n));
    float3 b = cross(n, t);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - u1)));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void rtaoMain(uint3 id: SV_DispatchThreadID)
{
    int2 pixel = int2(id.xy);
    float2 screen = rtao.screen.xy;
    if (pixel.x >= (int)screen.x || pixel.y >= (int)screen.y) return;

    float4 normalSample = prepassNormalTexture.Load(int3(pixel, 0));
    float depth = prepassDepthTexture.Load(int3(pixel, 0)).r;
    if (normalSample.w < 0.5 || depth >= 1.0)
    {
        rtaoTexture.Store(pixel, float4(1.0, 1.0, 1.0, 1.0));
        return;
    }

    float2 ndc = (float2(pixel) + 0.5) / screen * float2(2.0, -2.0) + float2(-1.0, 1.0);
    float4 world = mul(rtao.invViewProj, float4(ndc, depth, 1.0));
    float3 position = world.xyz / world.w;
    float3 normal = normalize(normalSample.xyz);
    float3 origin = position + normal * rtao.params.w;

    int rays = max((int)rtao.params.x, 1);
    uint seed = (uint)pixel.x * 1973u + (uint)pixel.y * 9277u + (uint)rtao.params.z * 26699u;
    float open = 0.0;
    for (int i = 0; i < rays; i++)
    {
        float u1 = hashToUnit(seed + (uint)i * 2u);
        float u2 = hashToUnit(seed + (uint)i * 2u + 1u);
        float3 dir = cosineDirection(normal, u1, u2);
        if (!traceAny(origin, dir, rtao.params.y)) open += 1.0;
    }
    float ao = open / (float)rays;
    rtaoTexture.Store(pixel, float4(ao, ao, ao, 1.0));
}
