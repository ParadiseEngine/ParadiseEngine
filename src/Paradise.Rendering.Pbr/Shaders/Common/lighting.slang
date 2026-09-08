// lighting.slang — the frame's lights and shadows as every shader that shades a point sees them:
// the light records, the frame uniforms, the shadow-map array with its comparison sampler, the
// attenuation models, and the shadow lookup. Shared by pbrCore.slang (the raster path) and the
// probe tracer (which shades ray hits), so the two agree on what a light does to a surface.
//
// slangc keeps EVERY global an included file declares, referenced or not, so what is here is
// exactly what a compute pass shading a hit must also bind: the frame UBO, the shadow array and
// its sampler, in group 1 at bindings 0..2. The raster-only bindings live in uniforms.slang.
#ifndef PARADISE_LIGHTING_SLANG
#define PARADISE_LIGHTING_SLANG

static const float PI = 3.14159265359;
static const int MaxSceneLights = 64;

// Shadow metadata budget is independent of the light count and physical atlas extent.
// Mirrored by FrameUniformsGpu.MaxShadowViews and checked by UniformLayoutValidator.
static const int MaxShadowViews = 384;

struct SceneLight
{
    float4 positionAndType;       // xyz: position, w: 0 directional / 1 point / 2 spot
    float4 directionAndRange;     // xyz: direction from surface to light, w: range
    float4 colorAndIntensity;     // rgb: color, w: intensity
    float4 spotAngles;            // x: outer angle degrees, y: inner angle degrees, z: base shadow-view index (<0 = no shadow), w: shadow strength (0 = none)
    float4 shadowAtlas;           // x: distance-attenuation decay, y: view count (1..4 dir, 1 spot, 6 point), z: LIGHT_PARAM_SPECULAR, w: soft-shadow flag
    float4 sizeParams;            // x: LIGHT_PARAM_SIZE (directional: precomputed 1−cos(angular); point/spot: world radius), y: shadow texel world size (directional: metres; point/spot: metres per metre of receiver distance), z: indirect energy (how much of the light the probes bounce), w: PCSS world radius or tan(angular radius)
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
    float4   shadowSettings; // x: 1/atlasSize, y: tonemap mode, z: exposure, w: white
    float4x4 sceneLightShadowMatrices[MaxShadowViews]; // one per shadow view; index = light.spotAngles.z + face
    float4   time;           // x: elapsed seconds (procedural material animation), yzw unused
    float4   shadowFilter;   // x: maximum PCSS radius in texels, y: cascade blend fraction
    // Inverse of the camera view-projection: how a screen-space effect turns a pre-pass depth
    // back into a world position, instead of storing positions at 32-bit float per pixel.
    float4x4 invViewProj;
    float4 shadowViewRects[MaxShadowViews]; // xy offset, zw extent of the inset viewport
    float4 shadowViewData[MaxShadowViews];  // x world texel, yz camera split near/far, w physical array layer
    float4 shadowViewDepth[MaxShadowViews]; // xy light near/far, z perspective flag, w viewport resolution
    float4x4 viewProj;
    float4 contactShadowSettings; // x length, y thickness, z steps, w strength (zero disables)
};

[[vk::binding(0, 1)]]
ConstantBuffer<FrameUniforms> frame;

// Shadow atlas (a single Depth32Float 2D array with one physical layer) + comparison
// sampler, on the group-1 slots reserved when shadow mapping was removed. NOTE: the generated WGSL
// declares shadowTexture as texture_2d_array<f32>; a build-time patch (Slang.targets) rewrites it
// to texture_depth_2d_array, which WGSL requires for textureSampleCompareLevel (Slang issue #6942).
// The loader forces the matching bind-group layout (DepthTextureArray + ComparisonSampler) by these
// exact names.
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
    // Godot's spot cone (light_process_spot): a smooth radial falloff from the cone axis (full) to the
    // edge (zero) — NOT a hard-edged disc. spotRim is 0 on-axis, 1 at the cone boundary; Godot raises it
    // to spot_angle_attenuation (default 1.0, assumed here → linear rim). The previous inner/outer blend
    // drew a hard "circle" on the ground whenever no inner angle was authored (Godot has no inner angle),
    // because it fell back to inner == outer. spotAngles.y (inner angle) is unused in this model.
    float3 lightToSurface = -surfaceToLight;
    float3 coneDirection = -normalize(light.directionAndRange.xyz);
    float cosTheta = dot(lightToSurface, coneDirection);
    float coneCos = cos(radians(light.spotAngles.x * 0.5)); // cosine of the cone half-angle
    float scos = max(cosTheta, coneCos);
    float denom = max(1.0 - coneCos, 1e-4); // guards a degenerate 0°-outer-angle spot (coneCos == 1)
    float spotRim = max(1e-4, (1.0 - scos) / denom);
    return 1.0 - spotRim; // 1 - pow(spotRim, spot_angle_attenuation); default exponent 1
}

// Shadow bias — two mechanisms, each sized by the map's ACTUAL texel world size (per light in
// sizeParams.y: directional = metres, point/spot = metres per metre of receiver distance, since
// perspective texels grow with range). A fixed world-space bias is only ever correct for one texel
// size — the old 0.05 m constant was tuned when the camera-fitted 4096 sun map made ~2.5 cm texels,
// and any coarser map (a scene authoring a smaller ShadowMapSize, a wider legacy fit) outgrew it and
// flat ground self-shadowed in diagonal bands.
//
// 1. NORMAL-OFFSET bias: lift the receiver sample along its surface normal, scaled up toward
//    grazing incidence (capped — the tan() blow-up at N·L → 0 must not push the sample outside
//    the light's footprint). With the plane bias below covering the PCF disk, this only has to
//    cover the CENTRE texel's own depth quantization — half a texel times the slope — and it must
//    stay that small: every texel of lift is a strip of CONTACT LIGHT-LEAK beside each caster
//    (near a wall's base the occluder's stored depth is nearly the receiver's own, so any lift
//    reads the base as non-occluding and the shadow fades toward the contact line — visibly, on
//    a coarse map, where a texel is tens of centimetres). Perpendicular to the surface, so unlike
//    a large depth bias it does not shift the sample along the light and does not detach object
//    shadows from their contact point.
// 2. RECEIVER-PLANE depth bias (Isidoro): each PCF tap compares against the receiver PLANE's depth
//    at the tap's own location — the plane's depth gradient in light NDC is solved from two
//    projected surface tangents — instead of the centre depth. Without it the disk's outer taps
//    need the normal offset to cover the WHOLE disk radius (blur × texel × slope), which at coarse
//    resolutions is metres of lift and visibly eats every short shadow; with it the offset stays
//    at ~1 texel no matter the blur.
//
// ShadowDepthBias is a small constant NDC residual on top. Together these keep flat ground
// acne-free at every map resolution while contact shadows stay attached (matching Godot).
static const float ShadowDepthBias = 0.0004;
// Lift in texels = ShadowNormalBiasTexels · (1 + tan(angle to light)), capped: the half-texel
// centre quantization needs ~0.5·tan texels of lift (the geometry works out to 0.5·sinθ/cos²θ),
// and every texel beyond that widens the contact light-leak strip. The cap bounds the grazing
// blow-up; past it the surface is nearly edge-on to the light and receives little direct light
// anyway, so residual acne there stays invisible.
static const float ShadowNormalBiasTexels = 0.5;      // soft path: plane bias covers the disk
static const float ShadowNormalBiasTexelsHard = 1.0;  // hard path: no plane bias, hw 2x2 footprint
static const float ShadowMaxSlopeTexels = 2.0;    // cap on the total lift, in texels
// NDC cap on the per-tap plane adjustment. It bounds SILHOUETTE light-leak: at a shadow edge the
// receiver plane extends past the caster, and an unbounded gradient there would tilt taps into
// accepting the caster's own depth. Sized against the camera-fit sun's depth range (~0.5-1.5 m of
// depth per 0.01 NDC); the legacy whole-scene fit's larger range makes the cap proportionally
// looser, which errs toward more bias on the path whose texels are also proportionally coarser.
static const float ShadowPlaneBiasClamp = 0.01;

// Cube face index (point lights) from the dominant axis of the light→surface direction.
int getPointShadowFace(float3 dir)
{
    float3 a = abs(dir);
    if (a.x >= a.y && a.x >= a.z) return dir.x > 0.0 ? 0 : 1;
    if (a.y >= a.z)               return dir.y > 0.0 ? 2 : 3;
    return dir.z > 0.0 ? 4 : 5;
}

// Interleaved Gradient Noise (Godot's quick_hash) — decorrelates the per-pixel PCF disk rotation.
float shadowDiskHash(float2 pos)
{
    const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(pos, magic.xy)));
}

// geoN is the GEOMETRIC normal (front-face-corrected interpolated vertex normal), not the
// normal-mapped shading normal: every quantity here — the lift direction, the slope factor, the
// receiver-plane basis — describes the real surface the shadow map rasterized. A normal-mapped
// floor's shading normal tilts the solved plane away from the actual geometry, turning the
// per-tap depth adjustment into a per-texel error in either direction; the shading normal stays
// the lighting's business only. (It also keeps the degenerate-tangent NaN path of sampleNormal
// out of the shadow lookup entirely.)
// lightIndex is unread since the shadow matrices became view-keyed: the light record carries its
// own base view in spotAngles.z. It stays in the signature because a GAME's material shader calls
// this (Pingu's poolInterior.slang does), and dropping a parameter would break that source for no
// gain while the light cap is unchanged. It goes with the cap lift, which breaks those shaders
// anyway.
// A rotated Poisson disk is shared by blocker search and the variable-radius PCF filter.
static const float2 ShadowPoisson[16] = {
    float2(-0.94201624,-0.39906216), float2(0.94558609,-0.76890725),
    float2(-0.09418410,-0.92938870), float2(0.34495938,0.29387760),
    float2(-0.91588581,0.45771432), float2(-0.81544232,-0.87912464),
    float2(-0.38277543,0.27676845), float2(0.97484398,0.75648379),
    float2(0.44323325,-0.97511554), float2(0.53742981,-0.47373420),
    float2(-0.26496911,-0.41893023), float2(0.79197514,0.19090188),
    float2(-0.24188840,0.99706507), float2(-0.81409955,0.91437590),
    float2(0.19984126,0.78641367), float2(0.14383161,-0.14100790)
};

float shadowLinearDepth(float depth, float4 projection)
{
    return projection.z > 0.5
        ? projection.x * projection.y / max(projection.y - depth * (projection.y - projection.x), 1e-6)
        : lerp(projection.x, projection.y, depth);
}

float sampleShadowView(SceneLight light, int index, float3 worldPos, float3 geoN,
                       float3 surfaceToLight, float2 fragCoord)
{
    float4 rect = frame.shadowViewRects[index];
    float4 data = frame.shadowViewData[index];
    float4 projection = frame.shadowViewDepth[index];
    float texelWorld = data.x;
    if (light.positionAndType.w > 0.5) texelWorld *= length(light.positionAndType.xyz - worldPos);
    float ndotl = saturate(dot(geoN, surfaceToLight));
    float slope = sqrt(saturate(1.0 - ndotl * ndotl)) / max(ndotl, 0.05);
    float biasTexels = light.shadowAtlas.w > 0.5 ? ShadowNormalBiasTexels : ShadowNormalBiasTexelsHard;
    float3 samplePos = worldPos + geoN * (texelWorld * min(biasTexels * (1.0 + slope), ShadowMaxSlopeTexels));
    float4 shadowPos = mul(frame.sceneLightShadowMatrices[index], float4(samplePos, 1));
    if (shadowPos.w <= 0) return 1;
    float3 ndc = shadowPos.xyz / shadowPos.w;
    float2 localUv = ndc.xy * float2(0.5, -0.5) + 0.5;
    if (any(localUv < 0) || any(localUv > 1) || ndc.z < 0 || ndc.z > 1) return 1;
    float2 uv = rect.xy + localUv * rect.zw;
    float texel = frame.shadowSettings.x;
    // Clamp to TEXEL CENTERS in this viewport. Hardware 2x2 PCF cannot read another tile.
    float2 uvMin = rect.xy + texel * 0.5;
    float2 uvMax = rect.xy + rect.zw - texel * 0.5;
    float compare = ndc.z - ShadowDepthBias;
    if (light.shadowAtlas.w <= 0.5)
        return shadowTexture.SampleCmpLevelZero(shadowSampler, float3(clamp(uv, uvMin, uvMax), data.w), compare);

    float3 t1 = normalize(cross(geoN, abs(geoN.y) < 0.9 ? float3(0,1,0) : float3(1,0,0)));
    float3 t2 = cross(geoN, t1);
    float step = max(texelWorld, 1e-4);
    float4 q1 = mul(frame.sceneLightShadowMatrices[index], float4(samplePos + t1 * step, 1));
    float4 q2 = mul(frame.sceneLightShadowMatrices[index], float4(samplePos + t2 * step, 1));
    float3 e1 = q1.xyz / max(q1.w, 1e-6) - ndc;
    float3 e2 = q2.xyz / max(q2.w, 1e-6) - ndc;
    float det = e1.x * e2.y - e1.y * e2.x;
    float2 gradient = abs(det) > 1e-9
        ? float2(e1.z * e2.y - e1.y * e2.z, e1.x * e2.z - e1.z * e2.x) / det
        : float2(0,0);
    // Convert from light NDC to ATLAS UV, preserving receiver-plane bias after packing.
    gradient *= float2(2,-2) / rect.zw;
    float angle = shadowDiskHash(fragCoord) * 2 * PI;
    float cr = cos(angle), sr = sin(angle);
    float2 offsets[16];
    float blockers = 0, blockerSum = 0;
    float maxRadius = frame.shadowFilter.x;
    [unroll] for (int k = 0; k < 16; k++)
    {
        offsets[k] = float2(cr * ShadowPoisson[k].x + sr * ShadowPoisson[k].y,
                           -sr * ShadowPoisson[k].x + cr * ShadowPoisson[k].y);
        float2 tap = clamp(uv + offsets[k] * (texel * maxRadius), uvMin, uvMax);
        float dz = clamp(dot(gradient, tap - uv), -ShadowPlaneBiasClamp, ShadowPlaneBiasClamp);
        float depth = shadowTexture.Load(int4(int2(tap / texel), int(data.w), 0));
        if (depth < compare + dz)
        {
            blockerSum += shadowLinearDepth(depth - dz, projection);
            blockers += 1;
        }
    }
    if (blockers == 0) return 1;
    float receiver = shadowLinearDepth(compare, projection);
    float blocker = blockerSum / blockers;
    float separation = max(0, receiver - blocker);
    float penumbraWorld = separation * light.sizeParams.w;
    if (projection.z > 0.5) penumbraWorld /= max(blocker, 1e-4);
    float radius = clamp(penumbraWorld / max(texelWorld, 1e-6), 0.5, maxRadius);
    float lit = 0;
    [unroll] for (int k = 0; k < 16; k++)
    {
        float2 tap = clamp(uv + offsets[k] * (texel * radius), uvMin, uvMax);
        float dz = clamp(dot(gradient, tap - uv), -ShadowPlaneBiasClamp, ShadowPlaneBiasClamp);
        lit += shadowTexture.SampleCmpLevelZero(shadowSampler, float3(tap, data.w), compare + dz);
    }
    return lit / 16;
}

float sampleSceneLightShadow(SceneLight light, int lightIndex, float3 worldPos, float3 geoN, float3 surfaceToLight, float2 fragCoord)
{
    if (light.spotAngles.w <= 0 || light.shadowAtlas.y <= 0 || light.spotAngles.z < 0) return 1;
    int first = int(light.spotAngles.z), count = int(light.shadowAtlas.y), face = 0;
    float cameraDepth = dot(worldPos - frame.cameraPos.xyz, frame.cameraForward.xyz);
    if (light.positionAndType.w < 0.5)
    {
        if (cameraDepth > frame.shadowViewData[first + count - 1].z) return 1;
        [unroll] for (int c = 0; c < 3; c++)
            if (c < count - 1 && cameraDepth > frame.shadowViewData[first + c].z) face = c + 1;
    }
    else if (light.positionAndType.w > 0.5 && light.positionAndType.w < 1.5)
    {
        // Choose the cube face from the biased receiver, keeping cube seams consistent with
        // the projection performed by sampleShadowView.
        float texelWorld = frame.shadowViewData[first].x * length(light.positionAndType.xyz - worldPos);
        float ndotl = saturate(dot(geoN, surfaceToLight));
        float slope = sqrt(saturate(1 - ndotl * ndotl)) / max(ndotl, 0.05);
        float bias = light.shadowAtlas.w > 0.5 ? ShadowNormalBiasTexels : ShadowNormalBiasTexelsHard;
        face = getPointShadowFace(worldPos + geoN * (texelWorld * min(bias * (1 + slope), ShadowMaxSlopeTexels)) - light.positionAndType.xyz);
    }
    float lit = sampleShadowView(light, first + face, worldPos, geoN, surfaceToLight, fragCoord);
    if (light.positionAndType.w < 0.5)
    {
        float4 split = frame.shadowViewData[first + face];
        float blend = saturate((cameraDepth - (split.z - (split.z - split.y) * frame.shadowFilter.y)) /
                              max((split.z - split.y) * frame.shadowFilter.y, 1e-5));
        if (blend > 0)
        {
            float next = face + 1 < count ? sampleShadowView(light, first + face + 1, worldPos, geoN, surfaceToLight, fragCoord) : 1;
            lit = lerp(lit, next, blend);
        }
    }
    return lerp(1 - light.spotAngles.w, 1, lit);
}

#endif // PARADISE_LIGHTING_SLANG
