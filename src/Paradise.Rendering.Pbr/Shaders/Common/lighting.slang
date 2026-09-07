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
    // Forward+ clustering (Godot's froxel model, CPU-binned): the screen is tiled 32x32 px and
    // view depth is split into logarithmic slices; clusterMasks holds TWO uint bitmask words per
    // froxel (word i>>5, bit i&31 = sceneLights[i] overlaps it). Directional lights are never
    // clustered.
    float4   cameraForward;  // xyz: world-space camera forward (unit), w: cluster near
    float4   clusterParams;  // x: tilesX, y: tilesY, z: zSlices, w: cluster far (x<1 disables)
    SceneLight sceneLights[MaxSceneLights];
    float4   shadowSettings; // x: 1/shadowMapSize (per-layer texel size), y: tonemap mode, z: exposure, w: white
    float4x4 sceneLightShadowMatrices[MaxSceneLights * 6]; // per light, per cube face (face 0 for dir/spot)
    float4   time;           // x: elapsed seconds (procedural material animation), yzw unused
    float4   shadowFilter;   // x: soft-shadow PCF disk radius in shadow texels, yzw unused
    // Inverse of the camera view-projection: how a screen-space effect turns a pre-pass depth
    // back into a world position, instead of storing positions at 32-bit float per pixel.
    float4x4 invViewProj;
};

[[vk::binding(0, 1)]]
ConstantBuffer<FrameUniforms> frame;

// Shadow map array (a single Depth32Float 2D array, one layer per shadow view) + comparison
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

// Light visibility in [0,1]: 1 = fully lit, (1 - strength) = fully shadowed. Samples the shadow-map
// array — one full-resolution layer per shadow view (dir/spot = 1, point = 6 cube faces) — via the
// light's per-face view-projection. No tile UV math and no seam clamp: each layer owns all of [0,1].
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
float sampleSceneLightShadow(SceneLight light, int lightIndex, float3 worldPos, float3 geoN, float3 surfaceToLight, float2 fragCoord)
{
    if (light.spotAngles.w <= 0.0 || light.shadowAtlas.y <= 0.0 || light.spotAngles.z < 0.0)
        return 1.0; // no shadow for this light

    // The face is chosen below, from the BIASED sample position: picking it from the surface's own
    // direction and then projecting a position lifted off the surface can land the sample just
    // outside that face's 90° frustum, where "outside the frustum" reads as lit — a thin bright
    // seam along every cube-face boundary, visible wherever the light is the only illumination.
    int face = 0;

    // Normal-offset bias (mechanism 1 above): the centre texel's quantization, more toward
    // grazing, capped — and no more, or the contact strip beside every caster lightens. The
    // hard-shadow path carries a larger base: it has no receiver-plane bias (a single tap has no
    // offsets to adjust), and its hardware 2x2 comparison footprint needs up to ~sqrt(2)·tan
    // texels of depth coverage that the soft path's per-tap plane adjustment provides instead.
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

    int matrixIndex = lightIndex * 6 + face;
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
        // Soft shadows: Godot's SOFT_HIGH-class PCF — a 16-sample Vogel disk
        // (r = sqrt((i+0.5)/16), theta = 2.4i), disk radius frame.shadowFilter.x shadow texels
        // (scene-authored; see Lighting.ShadowBlur in the export contract), randomly rotated
        // per pixel with interleaved gradient noise like sample_directional_pcf_shadow. The
        // rotation trades fixed-kernel banding for fine noise — but the noise AMPLITUDE goes as
        // 1/sqrt(taps), and it stops averaging out once the penumbra is WIDE on screen: on a
        // coarse map (a texel is tens of centimetres) 8 taps rendered every shadow edge as
        // visible diagonal stipple, IGN's own dither pattern. 16 taps is the tier that reads
        // as smooth there while the per-tap hardware 2x2 comparison (the sampler is Linear)
        // keeps the narrow-penumbra case dense. 4 taps at radius 2 (the old SOFT_LOW tier)
        // visibly sawtoothed even at fine texel sizes.
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

        // Receiver-plane depth bias (mechanism 2 in the bias comment): the plane's NDC depth
        // gradient, solved from two surface tangents projected through this face's matrix. Each
        // tap then compares against the PLANE's depth at its own offset rather than the centre's,
        // which is what lets the normal offset above stay at ~1 texel instead of covering the
        // whole disk. Exact for the ortho sun; first-order (and ample) for spot/point perspective.
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
