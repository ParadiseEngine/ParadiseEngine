// pbr.slang — the built-in PBR program's ENTRY POINTS, and nothing else. Every helper, the
// vertex transform, and the whole lighting path live in Common/pbrCore.slang so that game
// shaders can `#include` and extend them (custom displacement, custom surface resolution)
// without touching engine files — see PbrRenderer.RegisterMaterialProgram.
//
// Two properly-authored fragment entry points instead of runtime WGSL surgery:
//   fragmentMain     — linear output, for *UnormSrgb swapchain formats (hardware encodes)
//   fragmentMainSrgb — applies the sRGB OETF in-shader, for non-sRGB-only surfaces
// Exactly one of the two is picked per pipeline based on the surface format — never both, so
// the image is never double-encoded.

#include "Common/pbrCore.slang"

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    return pbrTransformVertex(input, float3(0.0, 0.0, 0.0));
}

/// Blend one vertex by its four joint weights. Returns the bind-pose matrix (identity) when the
/// weights sum to nothing, matching GltfAnimationRig.SkinVertices' fallback — an unweighted vertex
/// otherwise collapses to the origin, which reads as an exploded mesh rather than a rigging bug.
float4x4 skinMatrix(float4 joints, float4 weights)
{
    float total = weights.x + weights.y + weights.z + weights.w;
    if (total <= 0.0001)
    {
        return float4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    }

    uint base = (uint)draw.highlight.y;
    return jointMatrices[base + (uint)joints.x] * weights.x
         + jointMatrices[base + (uint)joints.y] * weights.y
         + jointMatrices[base + (uint)joints.z] * weights.z
         + jointMatrices[base + (uint)joints.w] * weights.w;
}

/// Skinned twin of vertexMain. Skinning happens in OBJECT space, before mvp/model, so everything
/// downstream — including objectPos, which procedural materials ride — behaves exactly as it does
/// for a rigid mesh.
[shader("vertex")]
VertexOutput vertexMainSkinned(VertexInputSkinned input)
{
    float4x4 skin = skinMatrix(input.joints, input.weights);
    float3 position = mul(skin, float4(input.position, 1.0)).xyz;
    // 3x3 of the blended matrix for directions. A weighted sum of rigid transforms is not exactly
    // rigid, so this is only correct up to the non-uniform scale a blend can introduce; the shader
    // renormalizes in the fragment stage as it already does for interpolated normals.
    float3 normal = mul((float3x3)skin, input.normal);
    float3 tangent = mul((float3x3)skin, input.tangent.xyz);

    return pbrTransformVertex(position, normal, input.texcoord, float4(tangent, input.tangent.w));
}

// For *UnormSrgb swapchain formats: return linear, the hardware encodes on store.
[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    return shadePbr(input);
}

// For non-sRGB-only surfaces (e.g. D3D12 swapchains): encode in-shader.
[shader("fragment")]
float4 fragmentMainSrgb(FragmentInput input) : SV_Target
{
    float4 color = shadePbr(input);
    return float4(linearToSrgb(color.rgb), color.a);
}
