// Godot procedural sky evaluated from world-space view rays. Blend linear sky/ground endpoints
// before downstream tone mapping; pre-tonemapped endpoints would shift gradient hues. Fragment
// variants provide linear or sRGB-encoded output.

#include "Common/tonemapping.slang"

struct SkyUniforms
{
    float4 skyTop;        // above horizon, at zenith (linear, untonemapped); w: sun angleMax cosine
    float4 skyHorizon;    // above horizon, at horizon; w: sun inv curve
    float4 groundBottom;  // below horizon, at nadir
    float4 groundHorizon; // below horizon, at horizon
    float4 params;        // x: inv_sky_curve, y: inv_ground_curve, z: tonemap mode, w: tonemap exposure
    float4 cameraPos;     // xyz: world camera position, w: tonemap white
    float4 sunDirection;  // xyz: to-sun (unit), w: enabled flag
    float4 sunColor;      // rgb: linear colour × energy, w: sun size cosine (cos of angular radius)
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

    // Sun disk + halo (Godot ProceduralSkyMaterial LIGHT0 branch): full light colour inside the
    // angular-size disk, a pow-curved halo out to the angleMax cosine. Only the sky half carries
    // the sun in Godot (the ground half overwrites it), so guard on eyeDir.y like the mix there.
    if (sky.sunDirection.w > 0.5 && eyeDir.y >= 0.0)
    {
        float sunAngle = dot(sky.sunDirection.xyz, eyeDir);
        float sunSize = sky.sunColor.w;
        float angleMax = sky.skyTop.w;
        if (sunAngle > sunSize)
        {
            gradient = sky.sunColor.rgb;
        }
        else if (sunAngle > angleMax)
        {
            float c2 = (sunSize - sunAngle) / (sunSize - angleMax);
            gradient = lerp(gradient, sky.sunColor.rgb, saturate(pow(1.0 - c2, sky.skyHorizon.w)));
        }
    }
    // Return LINEAR HDR — the composite pass tonemaps the whole scene once (see composite.slang).
    // (tonemap(lerp) order still holds: the gradient is blended linear here, tonemapped downstream.)
    return gradient;
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
