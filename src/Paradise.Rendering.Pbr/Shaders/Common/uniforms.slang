// uniforms.slang — every uniform struct and [[vk::binding]] declaration of the PBR frame, shared
// by pbr.slang's entry points, pbrCore.slang, and game extension shaders. Guarded because both
// pbrCore.slang and a game shader may include it on the same path.
#ifndef PARADISE_PBR_UNIFORMS_SLANG
#define PARADISE_PBR_UNIFORMS_SLANG

static const float PI = 3.14159265359;
static const int MaxSceneLights = 64;

struct SceneLight
{
    float4 positionAndType;       // xyz: position, w: 0 directional / 1 point / 2 spot
    float4 directionAndRange;     // xyz: direction from surface to light, w: range
    float4 colorAndIntensity;     // rgb: color, w: intensity
    float4 spotAngles;            // x: outer angle degrees, y: inner angle degrees, z: base shadow-array layer (<0 = no shadow), w: shadow strength (0 = none)
    float4 shadowAtlas;           // x: distance-attenuation decay, y: face count (1 = dir/spot, 6 = point), z: LIGHT_PARAM_SPECULAR, w: soft-shadow flag
    float4 sizeParams;            // x: LIGHT_PARAM_SIZE (directional: precomputed 1−cos(angular); point/spot: world radius), yzw unused
};

struct DrawUniforms
{
    float4x4 mvp;          // Model-View-Projection matrix
    float4x4 model;        // Model matrix (for world-space normals/positions)
    float4x4 normalMatrix; // Inverse-transpose model matrix (for world-space normals)
    float4 highlight;      // x: highlight weight, yzw unused
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
};

struct MaterialUniforms
{
    float4 baseColorFactor;   // glTF pbrMetallicRoughness.baseColorFactor (rgba)
    float  metallicFactor;    // glTF pbrMetallicRoughness.metallicFactor
    float  roughnessFactor;   // glTF pbrMetallicRoughness.roughnessFactor
    float  normalScale;       // glTF normalTexture.scale
    float  occlusionStrength; // glTF occlusionTexture.strength
    float4 emissiveFactor;    // glTF emissiveFactor (rgb), w: KHR_materials_transmission factor
    float4 uvOffsetScale;     // KHR_texture_transform (baseColor): xy offset, zw scale
    float4 uvRotation;        // x: rotation radians, yzw unused
    // Procedural material (see the recipe library below): a noise-driven, time-animated surface
    // overriding albedo/roughness/metallic/emissive when kind >= 1. Object-space so the pattern
    // rides a rolling ball. procColorA/B tint the tintable recipes.
    float4 procColorA;        // recipe color A (rgb)
    float4 procColorB;        // recipe color B (rgb)
    float4 procParams;        // x: kind (0 = off), y: noise scale, z: flow speed, w: emissive strength
};

[[vk::binding(0, 0)]]
ConstantBuffer<DrawUniforms> draw;

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

// Per-froxel light bitmasks (Forward+). Rebuilt by the renderer every frame; conservatively
// binned on the CPU (sphere bounds, ±1 froxel padding), so a set bit may be a false positive
// (the light's attenuation then contributes ~0) but never a false negative — clustering is
// pure culling and must not change pixels.
[[vk::binding(3, 1)]]
StructuredBuffer<uint> clusterMasks;

// Joint palettes for skinned draws, packed end to end: one contiguous run of matrices per skinned
// instance, and DrawUniforms.highlight.y carries that instance's first index. One buffer for every
// skinned draw in the frame keeps this to a single binding and a single upload — 24 KB for a scene
// of six 65-joint characters, versus re-uploading their vertex streams.
//
// Same convention as DrawUniforms.mvp: built as a row-vector product on the CPU and applied here
// as mul(matrix, vector), which is what the rigid path already does with mvp/model.
[[vk::binding(4, 1)]]
StructuredBuffer<float4x4> jointMatrices;

[[vk::binding(0, 2)]]
ConstantBuffer<MaterialUniforms> material;

[[vk::binding(1, 2)]]
Texture2D<float4> baseColorTexture;

[[vk::binding(2, 2)]]
SamplerState baseColorSampler;

[[vk::binding(3, 2)]]
Texture2D<float4> metallicRoughnessTexture;

[[vk::binding(4, 2)]]
Texture2D<float4> normalTexture;

[[vk::binding(5, 2)]]
Texture2D<float4> occlusionTexture;

[[vk::binding(6, 2)]]
Texture2D<float4> emissiveTexture;

// Group 3: screen-space ambient occlusion. positionTexture is the world-position pre-pass (xyz =
// world position, w = coverage; Rgba32Float, sampled with textureLoad — unfilterable, no sampler).
// AO is computed inline in the fragment and multiplies only the ambient term; intensity 0 disables
// it (the pre-pass is skipped and the shader early-outs, so the stale texture is never read).
struct SsaoUniforms
{
    float4 params; // x: intensity (0 = off), y: radius (world units), z: bias, w: power
    float4 screen; // xy: 1/screenSize (texel), zw: screenSize (pixels)
};

[[vk::binding(0, 3)]]
ConstantBuffer<SsaoUniforms> ssao;

[[vk::binding(1, 3)]]
Texture2D<float4> positionTexture;

// Sky-reflection specular (Godot Environment.reflected_light_source = Sky/Background): the
// ProceduralSky radiance GGX-prefiltered on the CPU into a small LUT. The gradient sky is
// azimuth-symmetric, so the prefiltered radiance depends only on reflection.y (u) and
// roughness (v). Enabled via frame.aaSettings.x (sun disk/halo excluded — a 1D-symmetric LUT
// cannot carry a directional glint; its diffuse-scale contribution is negligible).
[[vk::binding(2, 3)]]
Texture2D<float4> skySpecularLut;

[[vk::binding(3, 3)]]
SamplerState skySpecularSampler;

// The environment BRDF (DFG) table — the real GGX pre-integral (Godot's integrate_dfg.glsl
// integrand: Schlick-GGX with the IBL k = α²/2, 1024 Hammersley samples), CPU-baked at startup
// into a 128×128 Rgba16Float. r = scale (∫(1−Fc)·G_Vis), g = bias (∫Fc·G_Vis); specular =
// F0·scale + f90·bias, and dfg_y (the F0=1 response used by the multiscatter energy
// compensation) = scale + bias. u = NdotV, v = roughness. Replaces the Karis analytic fit,
// whose few-percent corner error showed up on metals (× energy compensation × many lights).
[[vk::binding(4, 3)]]
Texture2D<float4> dfgLut;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal   : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent  : TANGENT;
};

// Skinned meshes extend the same stream rather than binding a second vertex buffer: the loader
// reflects ONE interleaved layout per vertex entry point, so joints/weights ride along at the end
// (20 floats/vertex instead of 12). They are uploaded once with the mesh, not per frame.
//
// Joint indices arrive as floats because that is how GltfPrimitive.JointsWeights stores them —
// exact for any index a glTF skin can hold, and it keeps the stream a single float array.
struct VertexInputSkinned
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal   : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent  : TANGENT;
    [[vk::location(4)]] float4 joints   : JOINTS;
    [[vk::location(5)]] float4 weights  : WEIGHTS;
};

struct VertexOutput
{
    float4 position    : SV_Position;
    float3 worldPos    : WORLDPOS;
    float3 worldNormal : NORMAL;
    float2 texcoord    : TEXCOORD0;
    float4 worldTangent : TANGENT;
    float3 objectPos   : OBJECTPOS; // object-space position (procedural materials ride the mesh)
};

struct FragmentInput
{
    float4 position    : SV_Position;
    float3 worldPos    : WORLDPOS;
    float3 worldNormal : NORMAL;
    float2 texcoord    : TEXCOORD0;
    float4 worldTangent : TANGENT;
    float3 objectPos   : OBJECTPOS;
    // Godot flips the geometric normal on back-facing fragments (double-sided materials render
    // their inside with the outward normal negated); without this the inside of a mesh reads the
    // wrong ambient hemisphere / NdotL. The pipeline doesn't cull, so backfaces are shaded here.
    bool isFrontFace   : SV_IsFrontFace;
};

#endif // PARADISE_PBR_UNIFORMS_SLANG
