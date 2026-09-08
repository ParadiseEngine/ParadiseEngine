#ifndef PARADISE_DECALS_SLANG
#define PARADISE_DECALS_SLANG

// DecalGpu in Decals.cs: eight float4s after the inverse transform, 192 bytes per volume.
struct DecalData
{
    float4x4 worldToLocal;
    float4 projectionNormal;
    float4 tangent;
    float4 color;
    float4 emission;
    float4 factors;    // roughness, metallic, normal strength, opacity
    float4 weights;    // color, normal, material, emission
    float4 projection; // facing cosine at full/zero coverage, XY/Z fade widths
    float4 atlas;      // first array layer: premultiplied linear RGBA, normal, metallic/roughness
};

struct DecalUniforms { float4 settings; };
[[vk::binding(5, 1)]] ConstantBuffer<DecalUniforms> decalFrame;
[[vk::binding(6, 1)]] StructuredBuffer<DecalData> decals;
[[vk::binding(7, 1)]] Texture2DArray<float4> decalAtlas;
[[vk::binding(8, 1)]] SamplerState decalSampler;

void applyDecals(FragmentInput input, float3 geometricNormal, inout float3 N,
                 inout float4 baseColor, inout float metallic, inout float roughness, inout float3 emissive)
{
    if (decalFrame.settings.x < 1.0 || draw.highlight.w > 0.5) return;
    // Derivatives must be evaluated before any per-fragment volume/facing branch. Explicit
    // gradients also make mip selection independent of alpha, volume boundaries and layer order.
    float3 positionDx = ddx(input.worldPos);
    float3 positionDy = ddy(input.worldPos);
    uint count = min((uint)decalFrame.settings.x, 32u);
    for (uint i = 0; i < count; i++)
    {
        DecalData decal = decals[i];
        float3 local = mul(decal.worldToLocal, float4(input.worldPos, 1.0)).xyz;
        if (any(abs(local) > 0.5)) continue;
        float facing = dot(geometricNormal, decal.projectionNormal.xyz);
        float alpha = saturate((facing - decal.projection.y) / max(decal.projection.x - decal.projection.y, 1e-5));
        if (decal.projection.z > 0.0)
            alpha *= saturate((0.5 - max(abs(local.x), abs(local.y))) / decal.projection.z);
        if (decal.projection.w > 0.0)
            alpha *= saturate((0.5 - abs(local.z)) / decal.projection.w);
        alpha *= decal.color.a * decal.factors.w;
        if (alpha <= 0.0) continue;
        float2 uv = float2(local.x + 0.5, 0.5 - local.y);
        float2 uvDx = mul((float3x3)decal.worldToLocal, positionDx).xy * float2(1.0, -1.0);
        float2 uvDy = mul((float3x3)decal.worldToLocal, positionDy).xy * float2(1.0, -1.0);
        float layer = decal.atlas.x;
        float4 color = decalAtlas.SampleGrad(decalSampler, float3(uv, layer), uvDx, uvDy);
        alpha *= saturate(color.a);
        if (alpha <= 0.0) continue;
        // Color mips are premultiplied before filtering so transparent texel RGB cannot bleed.
        float3 straightColor = color.rgb / max(color.a, 1e-6);
        baseColor.rgb = lerp(baseColor.rgb, straightColor * decal.color.rgb, alpha * decal.weights.x);
        emissive = lerp(emissive, straightColor * decal.emission.rgb, alpha * decal.weights.w);
        if (decal.weights.z > 0.0)
        {
            float4 packed = decalAtlas.SampleGrad(decalSampler, float3(uv, layer + 2.0), uvDx, uvDy);
            roughness = lerp(roughness, saturate(packed.g * decal.factors.x), alpha * decal.weights.z);
            metallic = lerp(metallic, saturate(packed.b * decal.factors.y), alpha * decal.weights.z);
        }
        if (decal.weights.y > 0.0)
        {
            float2 xy = (decalAtlas.SampleGrad(decalSampler, float3(uv, layer + 1.0), uvDx, uvDy).xy * 2.0 - 1.0) * decal.factors.z;
            float3 detail = normalize(float3(xy, sqrt(max(1.0 - dot(xy, xy), 1e-4))));
            float3 tangent = decal.tangent.xyz - N * dot(N, decal.tangent.xyz);
            float lengthSquared = dot(tangent, tangent);
            if (lengthSquared > 1e-8)
            {
                float3 T = tangent * rsqrt(lengthSquared);
                float3 B = cross(N, T) * decal.tangent.w;
                float3 mapped = normalize(T * detail.x + B * detail.y + N * detail.z);
                N = normalize(lerp(N, mapped, alpha * decal.weights.y));
            }
        }
    }
}

#endif
