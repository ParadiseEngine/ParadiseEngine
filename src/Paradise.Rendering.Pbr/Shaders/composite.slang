// composite.slang — the final post-process pass. Samples the linear-HDR scene target (the main
// pass now renders LINEAR HDR into an Rgba16Float texture instead of tonemapping in-shader),
// optionally adds bloom, applies the exported tone operator ONCE, and writes to the swapchain.
//
// Tonemapping moved OUT of pbr.slang / sky.slang into here so the whole frame is composited in
// linear HDR first (enabling bloom + a single consistent tone curve). The tone operators are
// duplicated verbatim from pbr.slang — the build compiles every .slang standalone, so shared
// includes aren't available; keep the blocks in sync.
//
// Two fragment entry points mirror pbr.slang: linear output for *UnormSrgb swapchains (hardware
// encodes), in-shader sRGB OETF otherwise.

struct CompositeUniforms
{
    float4 tone;   // x: tonemap mode, y: exposure, z: white, w: bloom intensity (0 = no bloom)
};

[[vk::binding(0, 0)]]
Texture2D<float4> sceneHdr;
[[vk::binding(1, 0)]]
SamplerState sceneSampler;
[[vk::binding(2, 0)]]
Texture2D<float4> bloomTex;
[[vk::binding(3, 0)]]
ConstantBuffer<CompositeUniforms> comp;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
Varyings compositeVertex(uint vertexId : SV_VertexID)
{
    // Fullscreen triangle: p ∈ {0,2}; clip = p*2-1 ∈ {-1,3}. On-screen fragments have p ∈ [0,1].
    // Texture uv: x = p.x, y = 1-p.y (framebuffer row 0 = top = NDC y=+1).
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = float2(p.x, 1.0 - p.y);
    return output;
}

// ---- tone operators (duplicated from pbr.slang — keep in sync) ----

float3 tonemapReinhard(float3 color, float white)
{
    return (color * (1.0 + color / (white * white))) / (1.0 + color);
}

float3 tonemapFilmic(float3 color, float white)
{
    const float expBias = 2.0;
    const float A = 0.22 * expBias * expBias;
    const float B = 0.30 * expBias;
    const float C = 0.10;
    const float D = 0.20;
    const float E = 0.01;
    const float F = 0.30;
    float3 c = ((color * (A * color + C * B) + D * E) / (color * (A * color + B) + D * F)) - E / F;
    float w = ((white * (A * white + C * B) + D * E) / (white * (A * white + B) + D * F)) - E / F;
    return c / w;
}

float3 tonemapAces(float3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

float3 agxAllenwpCurve(float3 x)
{
    const float awpCrossover = 0.18;
    const float awpShoulderMax = 0.82;
    const float awpContrast = 1.25;
    const float awpToeA = 0.534111561;
    const float awpSlope = 1.025000000;
    const float awpW = 4.140500000;
    float3 s = x - awpCrossover;
    float3 slopeS = awpSlope * s;
    s = slopeS * (1.0 + s / awpW) / (1.0 + slopeS / awpShoulderMax) + awpCrossover;
    float3 t = pow(max(x, 0.0), awpContrast);
    t = t / (t + awpToeA);
    return float3(
        x.x < awpCrossover ? t.x : s.x,
        x.y < awpCrossover ? t.y : s.y,
        x.z < awpCrossover ? t.z : s.z);
}

float3 tonemapAgx(float3 color)
{
    const float3x3 agxInset = float3x3(
        0.544814746488245, 0.373787398372697,  0.0813978551390581,
        0.140416948464053, 0.754137554567394,  0.105445496968552,
        0.0888104196149096, 0.178871756420858, 0.732317823964232);
    const float3x3 agxOutset = float3x3(
        1.96488741169489,   -0.855988495690215, -0.108898916004672,
        -0.299313364904742,  1.32639796461980,  -0.0270845997150571,
        -0.164352742528393, -0.238183969428088,  1.40253671195648);
    color = mul(agxInset, max(color, 0.0));
    color = agxAllenwpCurve(color);
    color = min(1.0, color);
    color = mul(agxOutset, color);
    return color;
}

float3 applyTonemap(float3 color, float exposure, float white, float mode)
{
    color = max(color * exposure, 0.0);
    white = max(white, 1e-4);
    if (mode < 0.5) return color;
    if (mode < 1.5) return tonemapReinhard(color, white);
    if (mode < 2.5) return tonemapFilmic(color, white);
    if (mode < 3.5) return tonemapAces(color);
    return tonemapAgx(color);
}

float3 linearToSrgb(float3 linearColor)
{
    float3 lo = linearColor * 12.92;
    float3 hi = 1.055 * pow(max(linearColor, 0.0), 1.0 / 2.4) - 0.055;
    return float3(
        linearColor.x <= 0.0031308 ? lo.x : hi.x,
        linearColor.y <= 0.0031308 ? lo.y : hi.y,
        linearColor.z <= 0.0031308 ? lo.z : hi.z);
}

float3 composite(float2 uv)
{
    float3 hdr = sceneHdr.Sample(sceneSampler, uv).rgb;
    if (comp.tone.w > 0.0)
        hdr += bloomTex.Sample(sceneSampler, uv).rgb * comp.tone.w;
    return applyTonemap(hdr, comp.tone.y, comp.tone.z, comp.tone.x);
}

[shader("fragment")]
float4 compositeFragment(Varyings input) : SV_Target
{
    return float4(composite(input.uv), 1.0);
}

[shader("fragment")]
float4 compositeFragmentSrgb(Varyings input) : SV_Target
{
    return float4(linearToSrgb(composite(input.uv)), 1.0);
}
