// sky.slang — fullscreen background gradient approximating Godot's procedural sky as seen behind
// the scene. Drawn first in the main pass with depth-write off / depth-compare Always, so scene
// geometry overdraws it. The two colours are the sky's top/horizon radiance already tone-mapped at
// export time, so this shader only lerps + encodes — matching the tonemapped scene without
// re-running the operator here. Two fragment entry points mirror pbr.slang: linear output for
// *UnormSrgb swapchains (hardware encodes), in-shader sRGB OETF otherwise.

struct SkyUniforms
{
    float4 topColor;     // linear, post-tonemap — screen top
    float4 horizonColor; // linear, post-tonemap — screen bottom (toward the horizon)
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
    // Fullscreen triangle: verts (0,0),(2,0),(0,2) → clip (-1,-1),(3,-1),(-1,3). uv.y is 0 at the
    // bottom of the screen and 1 at the top (2 is off-screen).
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    SkyVaryings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = p;
    return output;
}

float3 skyGradient(float2 uv)
{
    // The camera looks down, so the ground plane occludes all but the top ~quarter of the screen.
    // A power curve concentrates the top→horizon transition into that visible band (matching Godot's
    // quick darkening from the top edge to the horizon) instead of spreading it over the full height.
    float t = pow(saturate(uv.y), 8.0);
    return lerp(sky.horizonColor.rgb, sky.topColor.rgb, t);
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
    return float4(skyGradient(input.uv), 1.0);
}

[shader("fragment")]
float4 skyFragmentSrgb(SkyVaryings input) : SV_Target
{
    return float4(linearToSrgb(skyGradient(input.uv)), 1.0);
}
