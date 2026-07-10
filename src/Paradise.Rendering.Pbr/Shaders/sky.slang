// sky.slang — fullscreen background reproducing Godot's ProceduralSkyMaterial (the sample scene's
// Sky background). Godot evaluates the sky per world-space view ray (EYEDIR): a sky gradient above
// the horizon (sky_top → sky_horizon) and a ground gradient below (ground_bottom → ground_horizon),
// each with a power curve, selected by the sign of EYEDIR.y. We reconstruct EYEDIR from the inverse
// view-projection so a downward-pitched camera sees the correct horizon-relative gradient instead of
// a flat screen-space one.
//
// COLOUR ORDER (matches Godot exactly): the four gradient endpoints arrive LINEAR and UNTONEMAPPED;
// the gradient is blended in linear space and the environment tone operator is applied per-pixel
// afterwards. This order matters — tonemap(lerp(a,b)) ≠ lerp(tonemap(a),tonemap(b)) for the
// nonlinear operators, and pre-tonemapped endpoints visibly hue-shift the mid-gradient. The tone
// operators are duplicated from pbr.slang (the build compiles every .slang standalone, so shared
// includes aren't available; keep the two blocks in sync).
// Two fragment entry points mirror pbr.slang: linear output for *UnormSrgb swapchains, in-shader sRGB
// OETF otherwise.

struct SkyUniforms
{
    float4 skyTop;        // above horizon, at zenith  (linear, untonemapped)
    float4 skyHorizon;    // above horizon, at horizon
    float4 groundBottom;  // below horizon, at nadir
    float4 groundHorizon; // below horizon, at horizon
    float4 params;        // x: inv_sky_curve, y: inv_ground_curve, z: tonemap mode, w: tonemap exposure
    float4 cameraPos;     // xyz: world camera position, w: tonemap white
    float4x4 invViewProj; // NDC (far plane) → world, for the eye ray
};

[[vk::binding(0, 0)]]
ConstantBuffer<SkyUniforms> sky;

struct SkyVaryings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
SkyVaryings skyVertex(uint vertexId : SV_VertexID)
{
    // Fullscreen triangle: verts (0,0),(2,0),(0,2) → clip (-1,-1),(3,-1),(-1,3). uv = clip mapped to
    // [0,2]; the fragment recovers NDC as uv*2-1.
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    SkyVaryings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = p;
    return output;
}

// --- Tone operators, duplicated from pbr.slang (keep in sync) ---

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

// AgX (mode 4) — see pbr.slang for provenance (allenwp AgX, Godot 4.4+ default params).
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

// Apply the exported tone operator. mode: 0 Linear, 1 Reinhard, 2 Filmic, 3 ACES (approx), 4 AgX.
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

// Godot ProceduralSkyMaterial gradient (scene/resources/3d/sky_material.cpp): a two-part gradient
// selected by the horizon. inv_sky_curve = 0.6/sky_curve, inv_ground_curve = 0.6/ground_curve.
// Blends LINEAR endpoints, then tonemaps — the same per-pixel order as Godot's sky + tonemap passes.
float3 skyColor(float2 uv)
{
    float2 ndcxy = uv * 2.0 - 1.0;
    float4 worldH = mul(sky.invViewProj, float4(ndcxy, 1.0, 1.0)); // far-plane point in world
    float3 eyeDir = normalize(worldH.xyz / worldH.w - sky.cameraPos.xyz);

    float vAngle = clamp(eyeDir.y, -1.0, 1.0);
    float3 above = lerp(sky.skyTop.rgb, sky.skyHorizon.rgb, saturate(pow(1.0 - vAngle, sky.params.x)));
    float3 below = lerp(sky.groundBottom.rgb, sky.groundHorizon.rgb, saturate(pow(1.0 + vAngle, sky.params.y)));
    float3 gradient = eyeDir.y >= 0.0 ? above : below;
    return applyTonemap(gradient, sky.params.w, sky.cameraPos.w, sky.params.z);
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

[shader("fragment")]
float4 skyFragment(SkyVaryings input) : SV_Target
{
    return float4(skyColor(input.uv), 1.0);
}

[shader("fragment")]
float4 skyFragmentSrgb(SkyVaryings input) : SV_Target
{
    return float4(linearToSrgb(skyColor(input.uv)), 1.0);
}
