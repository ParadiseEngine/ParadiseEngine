// sky.slang — fullscreen background reproducing Godot's ProceduralSkyMaterial (the sample scene's
// Sky background). Godot evaluates the sky per world-space view ray (EYEDIR): a sky gradient above
// the horizon (sky_top → sky_horizon) and a ground gradient below (ground_bottom → ground_horizon),
// each with a power curve, selected by the sign of EYEDIR.y. We reconstruct EYEDIR from the inverse
// view-projection so a downward-pitched camera sees the correct horizon-relative gradient instead of
// a flat screen-space one. The four colours are already tone-mapped at export (exposure + operator
// applied), so this shader only evaluates the gradient and encodes — matching the tonemapped scene.
// Two fragment entry points mirror pbr.slang: linear output for *UnormSrgb swapchains, in-shader sRGB
// OETF otherwise.

struct SkyUniforms
{
    float4 skyTop;        // above horizon, at zenith  (linear, post-tonemap)
    float4 skyHorizon;    // above horizon, at horizon
    float4 groundBottom;  // below horizon, at nadir
    float4 groundHorizon; // below horizon, at horizon
    float4 params;        // x: inv_sky_curve, y: inv_ground_curve
    float4 cameraPos;     // xyz: world camera position
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

// Godot ProceduralSkyMaterial gradient (servers/.../sky_material.cpp): a two-part gradient selected by
// the horizon. inv_sky_curve = 0.6/sky_curve, inv_ground_curve = 0.6/ground_curve.
float3 skyColor(float2 uv)
{
    float2 ndcxy = uv * 2.0 - 1.0;
    float4 worldH = mul(sky.invViewProj, float4(ndcxy, 1.0, 1.0)); // far-plane point in world
    float3 eyeDir = normalize(worldH.xyz / worldH.w - sky.cameraPos.xyz);

    float vAngle = clamp(eyeDir.y, -1.0, 1.0);
    float3 above = lerp(sky.skyTop.rgb, sky.skyHorizon.rgb, saturate(pow(1.0 - vAngle, sky.params.x)));
    float3 below = lerp(sky.groundBottom.rgb, sky.groundHorizon.rgb, saturate(pow(1.0 + vAngle, sky.params.y)));
    return eyeDir.y >= 0.0 ? above : below;
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
