// pbr.slang — PBR metallic-roughness shader (Cook-Torrance BRDF), ported from bank-heist with
// skinning and shadow mapping deleted (bindings (0,3), (1,1), (2,1) freed for future PRs) and
// two properly-authored fragment entry points instead of runtime WGSL surgery:
//   fragmentMain     — linear output, for *UnormSrgb swapchain formats (hardware encodes)
//   fragmentMainSrgb — applies the sRGB OETF in-shader, for non-sRGB-only surfaces
// Implements GGX/Trowbridge-Reitz NDF, Smith-Schlick geometry, Schlick Fresnel, hemisphere
// ambient (no IBL), 1..8 punctual lights (directional/point/spot), geometric specular AA,
// BC5 two-channel normal reconstruction, and the KHR_materials_transmission glass response.
// The baseColor UV transform (KHR_texture_transform) lives in MaterialUniforms.

static const float PI = 3.14159265359;
static const int MaxSceneLights = 8;
static const float UnityDiffuseIntensityScale = 0.9;
static const float UnitySpecularIntensityScale = 12.0;
static const float UnitySpecularRoughnessScale = 1.35;

struct SceneLight
{
    float4 positionAndType;       // xyz: position, w: 0 directional / 1 point / 2 spot
    float4 directionAndRange;     // xyz: direction from surface to light, w: range
    float4 colorAndIntensity;     // rgb: color, w: intensity
    float4 spotAngles;            // x: outer angle degrees, y: inner angle degrees, z: base shadow-array layer (<0 = no shadow), w: shadow strength (0 = none)
    float4 shadowAtlas;           // x: unused, y: face count (1 = dir/spot, 6 = point), z: unused, w: soft-shadow flag
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
    SceneLight sceneLights[MaxSceneLights];
    float4   shadowSettings; // x: 1/shadowMapSize (per-layer texel size), yzw: unused
    float4x4 sceneLightShadowMatrices[MaxSceneLights * 6]; // per light, per cube face (face 0 for dir/spot)
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

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal   : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent  : TANGENT;
};

struct VertexOutput
{
    float4 position    : SV_Position;
    float3 worldPos    : WORLDPOS;
    float3 worldNormal : NORMAL;
    float2 texcoord    : TEXCOORD0;
    float4 worldTangent : TANGENT;
};

struct FragmentInput
{
    float4 position    : SV_Position;
    float3 worldPos    : WORLDPOS;
    float3 worldNormal : NORMAL;
    float2 texcoord    : TEXCOORD0;
    float4 worldTangent : TANGENT;
};

// --- PBR helper functions ---

// GGX/Trowbridge-Reitz Normal Distribution Function
float distributionGGX(float3 N, float3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;

    float denom = NdotH2 * (a2 - 1.0) + 1.0;
    denom = PI * denom * denom;

    return a2 / max(denom, 0.0001);
}

// Smith's method with Schlick-GGX geometry function
float geometrySchlickGGX(float NdotV, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}

float geometrySmith(float3 N, float3 V, float3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float ggx1 = geometrySchlickGGX(NdotV, roughness);
    float ggx2 = geometrySchlickGGX(NdotL, roughness);
    return ggx1 * ggx2;
}

// Schlick Fresnel approximation
float3 fresnelSchlick(float cosTheta, float3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

float glassFresnel(float3 N, float3 V)
{
    float viewFacing = max(dot(N, V), 0.0);
    return pow(saturate(1.0 - viewFacing), 5.0);
}

float3 applyGlassResponse(float3 litColor, float3 baseColor, float fresnel)
{
    float3 surfaceTint = baseColor * (0.025 + fresnel * 0.08);
    float3 reflectedEdge = float3(0.72, 0.88, 0.82) * fresnel * 0.09;
    return litColor * (0.20 + fresnel * 0.30) + surfaceTint + reflectedEdge;
}

// --- Shader entry points ---

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    VertexOutput output;
    output.position = mul(draw.mvp, float4(input.position, 1.0));
    output.worldPos = mul(draw.model, float4(input.position, 1.0)).xyz;
    output.worldNormal = mul((float3x3)draw.normalMatrix, input.normal);
    output.texcoord = input.texcoord;
    output.worldTangent = float4(mul((float3x3)draw.normalMatrix, input.tangent.xyz), input.tangent.w);
    return output;
}

// KHR_texture_transform for the baseColor slot: uv' = Translate(offset) · Rotate(r) · Scale(s).
float2 transformBaseColorUv(float2 uv)
{
    float2 scaled = uv * material.uvOffsetScale.zw;
    float r = material.uvRotation.x;
    float c = cos(r);
    float s = sin(r);
    float2 rotated = float2(c * scaled.x + s * scaled.y, -s * scaled.x + c * scaled.y);
    return rotated + material.uvOffsetScale.xy;
}

float3 sampleNormal(float2 texcoord, float3 worldNormal, float4 worldTangent)
{
    // Normal maps are two-channel (BC5, or the RGBA fallback's swizzled equivalent): only R/G
    // carry data. Reconstruct Z from the scaled XY: z = sqrt(1 - x*x - y*y).
    float2 nxy = normalTexture.Sample(baseColorSampler, texcoord).xy * 2.0 - 1.0;
    nxy *= material.normalScale;
    float nz = sqrt(saturate(1.0 - dot(nxy, nxy)));
    float3 normalSample = float3(nxy, nz);

    float3 N = normalize(worldNormal);
    float3 T = normalize(worldTangent.xyz);
    T = normalize(T - N * dot(N, T));
    float3 B = cross(N, T) * worldTangent.w;

    return normalize(T * normalSample.x + B * normalSample.y + N * normalSample.z);
}

float getDistanceAttenuation(float distanceToLight, float range)
{
    if (range <= 0.0)
    {
        return 1.0;
    }

    float normalizedDistance = saturate(distanceToLight / range);
    float falloff = saturate(1.0 - normalizedDistance * normalizedDistance);
    float smoothRangeFade = falloff * falloff;
    float distanceSquared = max(distanceToLight * distanceToLight, 0.0001);
    return smoothRangeFade / distanceSquared;
}

float getSpotAttenuation(SceneLight light, float3 surfaceToLight)
{
    float3 lightToSurface = -surfaceToLight;
    float3 coneDirection = -normalize(light.directionAndRange.xyz);
    float cosTheta = dot(lightToSurface, coneDirection);
    float outerCos = cos(radians(light.spotAngles.x * 0.5));
    float innerAngle = light.spotAngles.y > 0.0 ? light.spotAngles.y : light.spotAngles.x;
    float innerCos = cos(radians(innerAngle * 0.5));
    float denom = max(innerCos - outerCos, 0.001);
    return saturate((cosTheta - outerCos) / denom);
}

static const float ShadowDepthBias = 0.00035;

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
float sampleSceneLightShadow(SceneLight light, int lightIndex, float3 worldPos, float3 surfaceToLight)
{
    if (light.spotAngles.w <= 0.0 || light.shadowAtlas.y <= 0.0 || light.spotAngles.z < 0.0)
        return 1.0; // no shadow for this light

    int face = 0;
    if (light.positionAndType.w > 0.5 && light.positionAndType.w < 1.5) // point light
        face = getPointShadowFace(-surfaceToLight);
    if ((float)face >= light.shadowAtlas.y)
        return 1.0;

    int matrixIndex = lightIndex * 6 + face;
    float4 shadowPos = mul(frame.sceneLightShadowMatrices[matrixIndex], float4(worldPos, 1.0));
    if (shadowPos.w <= 0.0)
        return 1.0;
    float3 ndc = shadowPos.xyz / shadowPos.w;
    float2 uv = ndc.xy * float2(0.5, -0.5) + 0.5; // NDC → UV, Y flipped (WebGPU clip-Z [0,1])
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || ndc.z < 0.0 || ndc.z > 1.0)
        return 1.0; // outside the light frustum → treat as lit

    float layer = light.spotAngles.z + (float)face; // array slice for this view
    float texel = frame.shadowSettings.x;
    float compare = ndc.z - ShadowDepthBias;

    float lit = shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv, layer), compare);
    if (light.shadowAtlas.w > 0.5) // 5-tap cross PCF (sampler clamps to layer edge — no seam risk)
    {
        lit += shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv + float2(texel, 0.0), layer), compare);
        lit += shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv + float2(-texel, 0.0), layer), compare);
        lit += shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv + float2(0.0, texel), layer), compare);
        lit += shadowTexture.SampleCmpLevelZero(shadowSampler, float3(uv + float2(0.0, -texel), layer), compare);
        lit *= 0.2;
    }

    return lerp(1.0 - light.spotAngles.w, 1.0, lit);
}

float3 evaluateDirectLight(
    SceneLight light,
    int lightIndex,
    float3 worldPos,
    float3 N,
    float3 V,
    float3 F0,
    float3 baseColor,
    float metallic,
    float roughness)
{
    float3 L = float3(0.0, 1.0, 0.0);
    float attenuation = 1.0;
    if (light.positionAndType.w < 0.5)
    {
        L = normalize(light.directionAndRange.xyz);
    }
    else
    {
        float3 lightVector = light.positionAndType.xyz - worldPos;
        float distanceToLight = length(lightVector);
        L = lightVector / max(distanceToLight, 0.0001);
        attenuation = getDistanceAttenuation(distanceToLight, light.directionAndRange.w);
        if (light.positionAndType.w > 1.5)
        {
            attenuation *= getSpotAttenuation(light, L);
        }
    }

    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);
    float3 H = normalize(V + L);
    float specularRoughness = saturate(roughness * UnitySpecularRoughnessScale);
    float D = distributionGGX(N, H, specularRoughness);
    float G = geometrySmith(N, V, L, specularRoughness);
    float3 F = fresnelSchlick(max(dot(H, V), 0.0), F0);
    float3 specular = D * G * F / max(4.0 * NdotV * NdotL, 0.0001);
    float3 kD = (1.0 - F) * (1.0 - metallic);
    float3 diffuse = kD * baseColor;
    float3 radiance = light.colorAndIntensity.rgb * light.colorAndIntensity.w * attenuation;
    float shadow = sampleSceneLightShadow(light, lightIndex, worldPos, L);
    return (diffuse * UnityDiffuseIntensityScale + specular * UnitySpecularIntensityScale) * radiance * NdotL * shadow;
}

// Widen roughness based on how fast the shading normal changes across the screen.
// Where the normal varies quickly (high-frequency detail), the GGX highlight would
// alias into sparkle; raising roughness there keeps it stable.
float applySpecularAntiAliasing(float roughness, float3 N)
{
    float3 dNdx = ddx(N);
    float3 dNdy = ddy(N);
    float variance = frame.aaSettings.y * (dot(dNdx, dNdx) + dot(dNdy, dNdy));
    float kernelRoughness2 = min(2.0 * variance, frame.aaSettings.z);
    float alpha2 = saturate(roughness * roughness + kernelRoughness2);
    return sqrt(alpha2);
}

float4 shadePbr(FragmentInput input)
{
    float2 baseUv = transformBaseColorUv(input.texcoord);
    float4 baseColorSample = baseColorTexture.Sample(baseColorSampler, baseUv);
    float4 baseColor = baseColorSample * material.baseColorFactor;

    float4 metallicRoughnessSample = metallicRoughnessTexture.Sample(baseColorSampler, input.texcoord);
    float metallic = saturate(material.metallicFactor * metallicRoughnessSample.b);
    float roughness = max(material.roughnessFactor * metallicRoughnessSample.g, 0.04); // clamp to avoid division by zero

    float3 N = sampleNormal(input.texcoord, input.worldNormal, input.worldTangent);
    roughness = applySpecularAntiAliasing(roughness, N);
    float3 V = normalize(frame.cameraPos.xyz - input.worldPos);

    // Dielectric F0 = 0.04, metallic F0 = albedo
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), baseColor.rgb, metallic);

    // Direct lighting
    int sceneLightCount = min((int)frame.ambientEquator.w, MaxSceneLights);
    float3 Lo = float3(0.0, 0.0, 0.0);
    for (int i = 0; i < sceneLightCount; i++)
    {
        Lo += evaluateDirectLight(
            frame.sceneLights[i],
            i,
            input.worldPos,
            N,
            V,
            F0,
            baseColor.rgb,
            metallic,
            roughness);
    }

    // Simple ambient hemisphere tuned to better match Unity's lit presentation.
    float3 skyColor = frame.ambient.rgb;
    float3 equatorColor = frame.ambientEquator.rgb;
    float3 groundColor = frame.ambientGround.rgb;
    float hemisphere = dot(N, float3(0.0, 1.0, 0.0)) * 0.5 + 0.5;
    float3 hemisphereColor = hemisphere < 0.5
        ? lerp(groundColor, equatorColor, hemisphere * 2.0)
        : lerp(equatorColor, skyColor, (hemisphere - 0.5) * 2.0);
    if (frame.ambientGround.w > 0.5)
    {
        hemisphereColor = skyColor;
    }
    float occlusionSample = occlusionTexture.Sample(baseColorSampler, input.texcoord).r;
    float occlusion = lerp(1.0, occlusionSample, material.occlusionStrength);
    float exposure = max(frame.ambient.a, 0.0);
    float3 ambient = hemisphereColor * exposure * baseColor.rgb * (1.0 - metallic) * occlusion;

    float3 color = ambient + Lo;
    float transmission = saturate(material.emissiveFactor.w);
    if (transmission > 0.0)
    {
        float fresnel = glassFresnel(N, V);
        color = lerp(color, applyGlassResponse(color, baseColor.rgb, fresnel), transmission);
        baseColor.a = saturate(baseColor.a * lerp(1.0, 0.12, transmission) + fresnel * 0.05);
    }

    // Emissive
    color += emissiveTexture.Sample(baseColorSampler, input.texcoord).rgb * material.emissiveFactor.rgb;

    // Highlight flash (selection/hit feedback): additive push toward white.
    color += draw.highlight.x * 0.35;

    return float4(color, baseColor.a);
}

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

// For *UnormSrgb swapchain formats: return linear, the hardware encodes on store.
[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    return shadePbr(input);
}

// For non-sRGB-only surfaces (e.g. D3D12 swapchains): encode in-shader. Exactly one of the
// two entry points is picked per pipeline based on the surface format — never both, so the
// image is never double-encoded.
[shader("fragment")]
float4 fragmentMainSrgb(FragmentInput input) : SV_Target
{
    float4 color = shadePbr(input);
    return float4(linearToSrgb(color.rgb), color.a);
}
