// Shared raster and probe-tracer lighting: frame uniforms, attenuation and shadow lookup. Only
// group-1 bindings 0..2 belong here; Slang retains included globals, so raster-only bindings stay
// in uniforms.slang.
#ifndef PARADISE_LIGHTING_SLANG
#define PARADISE_LIGHTING_SLANG

static const float PI = 3.14159265359;
static const int MaxSceneLights = 64;

// Each shadow view uses one layer and matrix, with a budget independent of MaxSceneLights.
// Match FrameUniformsGpu.MaxShadowViews; UniformLayoutValidator checks this at renderer init.
static const int MaxShadowViews = 384;

struct SceneLight
{
    float4 positionAndType;       // xyz: position, w: 0 directional / 1 point / 2 spot
    float4 directionAndRange;     // xyz: direction from surface to light, w: range
    float4 colorAndIntensity;     // rgb: color, w: intensity
    float4 spotAngles;            // x: outer angle degrees, y: inner angle degrees, z: base shadow-array layer (<0 = no shadow), w: shadow strength (0 = none)
    float4 shadowAtlas;           // x: distance-attenuation decay, y: face count (1 = dir/spot, 6 = point), z: LIGHT_PARAM_SPECULAR, w: soft-shadow flag
    float4 sizeParams;            // x: LIGHT_PARAM_SIZE (directional: precomputed 1−cos(angular); point/spot: world radius), y: shadow texel world size (directional: metres; point/spot: metres per metre of receiver distance), z: indirect energy (how much of the light the probes bounce), w unused
};

struct FrameUniforms
{
    float4   cameraPos;      // World-space camera position (xyz), w unused
    float4   ambient;        // Ambient sky color (rgb) + exposure multiplier (a)
    float4   ambientEquator; // Ambient equator color (rgb), w: scene light count
    float4   ambientGround;  // Ambient ground color (rgb), w: flat ambient flag
    float4   aaSettings;     // x: unused, y: specularAA variance, z: specularAA clamp, w: unused
    // L2 spherical-harmonic sky irradiance (E/π), 9 RGB coefficients in Ramamoorthi order
    // (Y00, Y1-1, Y10, Y11, Y2-2, Y2-1, Y20, Y21, Y22) with the band factors Â=(1, 2/3, 1/4)
    // premultiplied at export — the per-normal ambient Godot's sky-SH produces, replacing the
    // 3-zone hemisphere blend. ambientSh[0].w > 0.5 enables the SH path (zones are the fallback).
    float4   ambientSh[9];
    // Forward+ clustering (Godot's froxel model, binned in compute by lightCull.slang): the screen
    // is tiled 32x32 px and view depth is split into logarithmic slices; clusterMasks holds TWO
    // uint bitmask words per froxel (word i>>5, bit i&31 = sceneLights[i] overlaps it).
    // Directional lights are never clustered.
    float4   cameraForward;  // xyz: world-space camera forward (unit), w: cluster near
    float4   clusterParams;  // x: tilesX, y: tilesY, z: zSlices, w: cluster far (x<1 disables)
    SceneLight sceneLights[MaxSceneLights];
    float4   shadowSettings; // x: 1/shadowMapSize (per-layer texel size), y: tonemap mode, z: exposure, w: white
    float4x4 sceneLightShadowMatrices[MaxShadowViews]; // one per shadow-array layer; index = light.spotAngles.z + face
    float4   time;           // x: elapsed seconds (procedural material animation), yzw unused
    float4   shadowFilter;   // x: soft-shadow PCF disk radius in shadow texels, yzw unused
    // Inverse of the camera view-projection: how a screen-space effect turns a pre-pass depth
    // back into a world position, instead of storing positions at 32-bit float per pixel.
    float4x4 invViewProj;
};

[[vk::binding(0, 1)]]
ConstantBuffer<FrameUniforms> frame;

// Shadow depth array and comparison sampler. Slang.targets patches shadowTexture to
// texture_depth_2d_array for SampleCmp (Slang #6942); the reflection loader recognizes these exact
// names.
[[vk::binding(1, 1)]]
Texture2DArray<float> shadowTexture;

[[vk::binding(2, 1)]]
SamplerComparisonState shadowSampler;


float getDistanceAttenuation(float distanceToLight, float range, float decay)
{
    if (range <= 0.0)
    {
        return 1.0;
    }

    // Godot's get_omni_attenuation: window² · pow(distance, -decay) with a (d/range)^4 cutoff. The
    // 4th-power window keeps the light near full strength before cutting sharply at the range boundary.
    // decay is the exported LIGHT_PARAM_ATTENUATION (Godot's default 1.0 is inverse-LINEAR, not
    // inverse-square) — matching it is what makes the light pool the right size/brightness.
    float nd = distanceToLight / range;
    nd *= nd;              // (d/range)^2
    nd *= nd;              // (d/range)^4
    float window = saturate(1.0 - nd);
    float smoothRangeFade = window * window;
    float dist = max(distanceToLight, 0.0001);
    return smoothRangeFade * pow(dist, -decay);
}

float getSpotAttenuation(SceneLight light, float3 surfaceToLight)
{
    // Match Godot's linear spot-angle attenuation: full on-axis, zero at the outer cone. The
    // inner-angle field is unused; using equal inner/outer angles creates a hard disc.
    float3 lightToSurface = -surfaceToLight;
    float3 coneDirection = -normalize(light.directionAndRange.xyz);
    float cosTheta = dot(lightToSurface, coneDirection);
    float coneCos = cos(radians(light.spotAngles.x * 0.5)); // cosine of the cone half-angle
    float scos = max(cosTheta, coneCos);
    float denom = max(1.0 - coneCos, 1e-4); // guards a degenerate 0°-outer-angle spot (coneCos == 1)
    float spotRim = max(1e-4, (1.0 - scos) / denom);
    return 1.0 - spotRim; // 1 - pow(spotRim, spot_angle_attenuation); default exponent 1
}

// Shadow bias scales with actual texel size: directional uses meters; point/spot use meters per
// receiver-distance meter.
// Normal offset covers center-texel quantization, with a grazing-angle cap to limit contact light
// leaks. Receiver-plane bias adjusts each PCF tap to its own projected depth, keeping normal offset
// small even for wide filters. ShadowDepthBias adds a small residual NDC offset.
static const float ShadowDepthBias = 0.0004;
// Lift is ShadowNormalBiasTexels*(1+tan(angle)), capped at grazing angles. Larger offsets widen
// contact light leaks; capped residual acne occurs where direct light is weak.
static const float ShadowNormalBiasTexels = 0.5;      // soft path: plane bias covers the disk
static const float ShadowNormalBiasTexelsHard = 1.0;  // hard path: no plane bias, hw 2x2 footprint
static const float ShadowMaxSlopeTexels = 2.0;    // cap on the total lift, in texels
// Cap receiver-plane NDC adjustment to limit leaks at caster silhouettes. A larger legacy-fit depth
// range makes the same cap looser where texels are also coarser.
static const float ShadowPlaneBiasClamp = 0.01;

// Cube face index (point lights) from the dominant axis of the light→surface direction.
int getPointShadowFace(float3 dir)
{
    float3 a = abs(dir);
    if (a.x >= a.y && a.x >= a.z) return dir.x > 0.0 ? 0 : 1;
    if (a.y >= a.z)               return dir.y > 0.0 ? 2 : 3;
    return dir.z > 0.0 ? 4 : 5;
}

// Light visibility in [0,1]: 1 = fully lit, (1 - strength) = fully shadowed. Samples the shadow-map
// array — one full-resolution layer per shadow view (dir/spot = 1, point = 6 cube faces) — via the
// light's per-face view-projection. No tile UV math and no seam clamp: each layer owns all of [0,1].
// Interleaved Gradient Noise (Godot's quick_hash) — decorrelates the per-pixel PCF disk rotation.
float shadowDiskHash(float2 pos)
{
    const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(pos, magic.xy)));
}

// Use the geometric normal for receiver lift and plane bias. A normal-map perturbation does not
// describe the rasterized surface and would corrupt shadow depth comparisons.
// Keep the unused lightIndex parameter for existing game material shaders; the light record
// supplies the shadow base layer through spotAngles.z.
float sampleSceneLightShadow(SceneLight light, int lightIndex, float3 worldPos, float3 geoN, float3 surfaceToLight, float2 fragCoord)
{
    if (light.spotAngles.w <= 0.0 || light.shadowAtlas.y <= 0.0 || light.spotAngles.z < 0.0)
        return 1.0; // no shadow for this light

    // The face is chosen below, from the BIASED sample position: picking it from the surface's own
    // direction and then projecting a position lifted off the surface can land the sample just
    // outside that face's 90° frustum, where "outside the frustum" reads as lit — a thin bright
    // seam along every cube-face boundary, visible wherever the light is the only illumination.
    int face = 0;

    // Cover center-texel quantization with capped normal offset. Hard shadows need a larger offset
    // because their hardware 2x2 comparisons lack per-tap plane bias.
    float texelWorld = light.sizeParams.y;
    if (light.positionAndType.w > 0.5)
    {
        // Perspective (point/spot): texels grow with distance. RADIAL distance, not axial — an
        // over-estimate of up to 1/cos(off-axis angle) at a cone's rim, deliberately erring
        // toward more bias rather than under-covering the texel.
        texelWorld *= length(light.positionAndType.xyz - worldPos);
    }
    float biasTexels = light.shadowAtlas.w > 0.5 ? ShadowNormalBiasTexels : ShadowNormalBiasTexelsHard;
    float ndotl = saturate(dot(geoN, surfaceToLight));
    float slope = sqrt(saturate(1.0 - ndotl * ndotl)) / max(ndotl, 0.05); // tan(angle to light)
    float liftTexels = min(biasTexels * (1.0 + slope), ShadowMaxSlopeTexels);
    float3 samplePos = worldPos + geoN * (texelWorld * liftTexels);

    if (light.positionAndType.w > 0.5 && light.positionAndType.w < 1.5) // point light
        face = getPointShadowFace(samplePos - light.positionAndType.xyz);
    if ((float)face >= light.shadowAtlas.y)
        return 1.0;

    // Index the matrix by the same array layer sampled below.
    int matrixIndex = (int)light.spotAngles.z + face;
    float4 shadowPos = mul(frame.sceneLightShadowMatrices[matrixIndex], float4(samplePos, 1.0));
    if (shadowPos.w <= 0.0)
        return 1.0;
    float3 ndc = shadowPos.xyz / shadowPos.w;
    float2 uv = ndc.xy * float2(0.5, -0.5) + 0.5; // NDC → UV, Y flipped (WebGPU clip-Z [0,1])
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || ndc.z < 0.0 || ndc.z > 1.0)
        return 1.0; // outside the light frustum → treat as lit

    float layer = light.spotAngles.z + (float)face; // array slice for this view
    float texel = frame.shadowSettings.x;
    float compare = ndc.z - ShadowDepthBias;

    float lit;
    if (light.shadowAtlas.w > 0.5)
    {
        // Soft shadows use a 16-tap Vogel disk at the authored blur radius, rotated by interleaved
        // gradient noise. Fewer taps produce visible stippling on coarse maps; linear comparison
        // sampling covers narrow penumbrae.
        static const float2 kVogel16[16] = {
            float2(+0.176777, +0.000000),
            float2(-0.225780, +0.206818),
            float2(+0.034587, -0.393769),
            float2(+0.284530, +0.371204),
            float2(-0.522210, -0.092451),
            float2(+0.494753, -0.314594),
            float2(-0.165602, +0.615488),
            float2(-0.315405, -0.607676),
            float2(+0.684569, +0.250232),
            float2(-0.712353, +0.293773),
            float2(+0.343624, -0.733602),
            float2(+0.253403, +0.809035),
            float2(-0.764550, -0.443523),
            float2(+0.897228, -0.196804),
            float2(-0.547908, +0.778490),
            float2(-0.125948, -0.976159),
        };
        float s = texel * frame.shadowFilter.x;
        float r = shadowDiskHash(fragCoord) * 2.0 * PI;
        float cr = cos(r);
        float sr = sin(r);

        // Solve receiver-plane depth gradients from projected tangents and adjust each tap at its
        // offset. Exact for orthographic light, first-order for perspective; avoids bias sized to
        // the whole PCF disk.
        float3 tan1 = normalize(cross(geoN, abs(geoN.y) < 0.9 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0)));
        float3 tan2 = cross(geoN, tan1);
        float planeStep = max(texelWorld, 1e-4);
        float4 q1 = mul(frame.sceneLightShadowMatrices[matrixIndex], float4(samplePos + tan1 * planeStep, 1.0));
        float4 q2 = mul(frame.sceneLightShadowMatrices[matrixIndex], float4(samplePos + tan2 * planeStep, 1.0));
        float3 e1 = q1.xyz / max(q1.w, 1e-6) - ndc;
        float3 e2 = q2.xyz / max(q2.w, 1e-6) - ndc;
        float det = e1.x * e2.y - e1.y * e2.x;
        // dz per NDC-xy offset d: dz = dot(zGrad, d) with the 2x2 solve below; degenerate when the
        // plane is edge-on to the light (both tangents project to one line) — no adjustment then.
        float2 zGrad = abs(det) > 1e-9
            ? float2(e1.z * e2.y - e1.y * e2.z, e1.x * e2.z - e1.z * e2.x) / det
            : float2(0.0, 0.0);

        lit = 0.0;
        [unroll] for (int k = 0; k < 16; k++)
        {
            // Godot's mat2(vec2(cr,-sr), vec2(sr,cr)) * offset (column-major GLSL).
            float2 o = float2(cr * kVogel16[k].x + sr * kVogel16[k].y,
                              -sr * kVogel16[k].x + cr * kVogel16[k].y);
            float2 uvOff = o * s;
            // UV → NDC offset is (2, -2) (the Y flip from the uv mapping above).
            float dz = clamp(zGrad.x * (2.0 * uvOff.x) + zGrad.y * (-2.0 * uvOff.y),
                             -ShadowPlaneBiasClamp, ShadowPlaneBiasClamp);
            lit += shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv + uvOff, layer), compare + dz);
        }
        lit *= 0.0625;
    }
    else
    {
        lit = shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv, layer), compare);
    }

    return lerp(1.0 - light.spotAngles.w, 1.0, lit);
}


#endif // PARADISE_LIGHTING_SLANG
