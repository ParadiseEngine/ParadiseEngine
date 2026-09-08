// uniforms.slang — every uniform struct and [[vk::binding]] declaration of the PBR frame, shared
// by pbr.slang's entry points, pbrCore.slang, and game extension shaders. Guarded because both
// pbrCore.slang and a game shader may include it on the same path.
#ifndef PARADISE_PBR_UNIFORMS_SLANG
#define PARADISE_PBR_UNIFORMS_SLANG

#include "lighting.slang"

struct DrawUniforms
{
    float4x4 mvp;          // Model-View-Projection matrix
    float4x4 model;        // Model matrix (for world-space normals/positions)
    float4x4 normalMatrix; // Inverse-transpose model matrix (for world-space normals)
    float4 highlight;      // x: highlight weight, y: joint palette base (skinned), z: probe GI disabled for this draw, w unused
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

// Per-froxel light bitmasks (Forward+). Rebuilt every frame by lightCull.slang; conservatively
// binned (sphere against the froxel's view-space bounding box), so a set bit may be a false
// positive (the light's attenuation then contributes ~0) but never a false negative — clustering is
// pure culling and must not change pixels.
[[vk::binding(3, 1)]]
StructuredBuffer<uint> clusterMasks;

// One joint-matrix buffer contains consecutive palettes; draw.highlight.y is the instance's first
// index. Upload once per frame. CPU row-vector products are read column-major and applied with
// mul(matrix,vector), like rigid transforms.
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

// Prepass normals are Rgba16Float (xyz unit world normal, w coverage); depth is Depth32Float. Use
// textureLoad and frame.invViewProj to reconstruct position. SSAO affects ambient only; zero
// intensity skips stale targets when the prepass is culled.
struct SsaoUniforms
{
    float4 params; // x: intensity (0 = off), y: radius (world units), z: bias, w: power
    float4 screen; // xy: 1/screenSize (texel), zw: screenSize (pixels)
    float4 rtao;   // x: ray-traced AO texture bound this frame (else it is black and skipped), y: screen-space reflection bound, z: roughness at which the reflection has faded out
};

[[vk::binding(0, 3)]]
ConstantBuffer<SsaoUniforms> ssao;

[[vk::binding(1, 3)]]
Texture2D<float4> prepassNormalTexture;

// GGX-prefiltered procedural sky LUT: gradient rows use reflection.y and sun rows use the
// reflection/sun cosine; roughness selects rows within each half. Enabled by frame.aaSettings.x.
[[vk::binding(2, 3)]]
Texture2D<float4> skySpecularLut;

[[vk::binding(3, 3)]]
SamplerState skySpecularSampler;

// CPU-integrated GGX DFG table: 128x128 Rgba16Float, 1024 Hammersley samples, IBL k=alpha^2/2. UV
// is (NdotV,roughness); rg store scale/bias, giving F0*scale+f90*bias. Their sum supplies the F0=1
// multiscatter response.
[[vk::binding(4, 3)]]
Texture2D<float4> dfgLut;

// NOTE: the generated WGSL declares this as texture_2d<f32>; the loader forces the bind-group
// layout to UnfilterableFloat by this exact name, which is what lets a depth texture bind here.
[[vk::binding(5, 3)]]
Texture2D<float> prepassDepthTexture;

// Ray-traced ambient occlusion (r = visibility) from rtao.slang, per pixel; read with textureLoad.
[[vk::binding(6, 3)]]
Texture2D<float4> rtAoTexture;

// Screen-space reflection (rgb = reflected radiance, a = confidence) from ssr.slang; sampled, as it
// may be traced below the frame's resolution.
[[vk::binding(12, 3)]]
Texture2D<float4> ssrTexture;

/// World position of the pre-pass surface under pixel `pixel`, or w = 0 where the pre-pass wrote
/// nothing (depth still at the clear value). WebGPU clip-Z is [0,1] and NDC y points up.
float4 prepassWorldPosition(int2 pixel, float2 screenSize)
{
    float depth = prepassDepthTexture.Load(int3(pixel, 0)).r;
    if (depth >= 1.0) return float4(0.0, 0.0, 0.0, 0.0);
    float2 ndc = (float2(pixel) + 0.5) / screenSize * float2(2.0, -2.0) + float2(-1.0, 1.0);
    float4 world = mul(frame.invViewProj, float4(ndc, depth, 1.0));
    return float4(world.xyz / world.w, 1.0);
}

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal   : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent  : TANGENT;
};

// Skinned vertices extend the reflected interleaved stream with joints/weights (20 floats rather
// than 12), uploaded once. Joint indices are exact float representations of glTF palette indices.
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
