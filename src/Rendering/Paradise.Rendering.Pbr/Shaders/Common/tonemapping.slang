// Binding-free tone operators matching Godot modes and the sRGB output transfer function.
// Shared by PBR extensions, sky and composition; keep color math in one place.
#ifndef PARADISE_TONEMAPPING_SLANG
#define PARADISE_TONEMAPPING_SLANG

// Reinhard (extended, white-point normalized) — Godot mode 1.
float3 tonemapReinhard(float3 color, float white)
{
    return (color * (1.0 + color / (white * white))) / (1.0 + color);
}

// Filmic (modified Hable/Uncharted-2, fixed exposure_bias = 2) — Godot mode 2.
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

// ACES filmic (Narkowicz fit) — a close approximation of Godot mode 3.
float3 tonemapAces(float3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

// AgX from Godot's tonemap.glsl/allenwp_curve, using default SDR parameters: contrast 1.25, white
// 2.0, output max 1.0. Returns linear color; the entry point handles output encoding.
float3 agxAllenwpCurve(float3 x)
{
    const float awpCrossover = 0.18;      // 18% middle gray
    const float awpShoulderMax = 0.82;    // output_max_value(1.0) - crossover
    const float awpContrast = 1.25;
    const float awpToeA = 0.534111561;    // CPU-precomputed (see environment_storage.cpp)
    const float awpSlope = 1.025000000;
    const float awpW = 4.140500000;

    // Reinhard-like shoulder (x >= crossover).
    float3 s = x - awpCrossover;
    float3 slopeS = awpSlope * s;
    s = slopeS * (1.0 + s / awpW) / (1.0 + slopeS / awpShoulderMax) + awpCrossover;
    // Sigmoid-power toe (x < crossover).
    float3 t = pow(max(x, 0.0), awpContrast);
    t = t / (t + awpToeA);
    return float3(
        x.x < awpCrossover ? t.x : s.x,
        x.y < awpCrossover ? t.y : s.y,
        x.z < awpCrossover ? t.z : s.z);
}

float3 tonemapAgx(float3 color)
{
    // Combined Rec.709→Rec.2020 + AgX inset (row-major for mul(M, v)).
    const float3x3 agxInset = float3x3(
        0.544814746488245, 0.373787398372697,  0.0813978551390581,
        0.140416948464053, 0.754137554567394,  0.105445496968552,
        0.0888104196149096, 0.178871756420858, 0.732317823964232);
    // Combined inverse AgX outset + Rec.2020→Rec.709.
    const float3x3 agxOutset = float3x3(
        1.96488741169489,   -0.855988495690215, -0.108898916004672,
        -0.299313364904742,  1.32639796461980,  -0.0270845997150571,
        -0.164352742528393, -0.238183969428088,  1.40253671195648);

    color = mul(agxInset, max(color, 0.0));
    color = agxAllenwpCurve(color);
    color = min(1.0, color);              // clip to output_max_value (SDR) — kills bright-input cyan
    color = mul(agxOutset, color);
    return color;                         // linear; may carry small negatives (as in Godot)
}

// Apply the exported tone operator. mode: 0 Linear, 1 Reinhard, 2 Filmic (pixel-verified vs Godot),
// 3 ACES (Narkowicz approx), 4 AgX (Godot 4.4+ allenwp AgX, default params).
float3 applyTonemap(float3 color, float exposure, float white, float mode)
{
    color = max(color * exposure, 0.0);
    white = max(white, 1e-4);
    if (mode < 0.5) return color;                        // Linear
    if (mode < 1.5) return tonemapReinhard(color, white);
    if (mode < 2.5) return tonemapFilmic(color, white);
    if (mode < 3.5) return tonemapAces(color);           // ACES (approx)
    return tonemapAgx(color);                            // AgX
}

// IEC 61966-2-1 OETF; call only when the output target does not encode sRGB.
float3 linearToSrgb(float3 linearColor)
{
    // IEC 61966-2-1 OETF, per channel.
    float3 lo = linearColor * 12.92;
    float3 hi = 1.055 * pow(max(linearColor, 0.0), 1.0 / 2.4) - 0.055;
    return float3(
        linearColor.x <= 0.0031308 ? lo.x : hi.x,
        linearColor.y <= 0.0031308 ? lo.y : hi.y,
        linearColor.z <= 0.0031308 ? lo.z : hi.z);
}

#endif // PARADISE_TONEMAPPING_SLANG
