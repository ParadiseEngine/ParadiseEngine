// pbrCore.slang — the reusable body of the PBR shader, as an UMBRELLA over the four files it was
// split into. Including this still gives a shader everything it ever gave: `#include
// "Common/pbrCore.slang"` is unchanged for every existing engine and game shader.
//
// What the split buys is being able to include LESS, and to enter the path in the middle:
//
//   noise.slang       hash/value-noise/fbm/worley — no bindings, no lighting
//   procedural.slang  the per-kind recipes evaluated from that noise
//   shading.slang     the maths and the whole lighting path (shadePbrCore)
//   surface.slang     the stock texture-sampling path (resolvePbrSurface) + PbrSurface
//
// The three extension seams, in the order a frame uses them:
//
//   pbrTransformVertex(...)  — the vertex transform, taking an object-space position a custom
//                              vertex entry may displace first (water waves, wind sway).
//   resolvePbrSurface(...)   — the stock surface as an addressable PbrSurface value, so a shader
//                              can take it, change one field, and light it (surface.slang).
//   shadePbrCore(...)        — the whole lighting path given a RESOLVED surface; shadeSurface is
//                              its struct-shaped twin (shading.slang).
//
// A game shader does `#include "Common/pbrCore.slang"` (resolved via the -I dir the NuGet's build
// targets add), declares its own group-2 bindings from slot 7 up, and defines its own entry
// points. See Paradise.Rendering.Pbr.Test/Shaders/waterFixture.slang for the canonical example,
// and surfaceFixture.slang for the resolve-modify-shade one.
#ifndef PARADISE_PBR_CORE_SLANG
#define PARADISE_PBR_CORE_SLANG

#include "uniforms.slang"
#include "probes.slang"
#include "noise.slang"
#include "procedural.slang"
#include "shading.slang"
#include "surface.slang"

// --- extension seams + the stock surface path ---

/// The rigid vertex transform from explicit object-space components. Extensions displace the
/// position (and adjust the normal/tangent if they can) before calling; the skinned entry point
/// feeds its blended components through the same path.
VertexOutput pbrTransformVertex(float3 localPosition, float3 localNormal, float2 texcoord, float4 localTangent)
{
    VertexOutput output;
    output.position = mul(draw.mvp, float4(localPosition, 1.0));
    output.worldPos = mul(draw.model, float4(localPosition, 1.0)).xyz;
    output.objectPos = localPosition;
    output.worldNormal = mul((float3x3)draw.normalMatrix, localNormal);
    output.texcoord = texcoord;
    output.worldTangent = float4(mul((float3x3)draw.normalMatrix, localTangent.xyz), localTangent.w);
    return output;
}

/// The displacement seam: the standard vertex stream plus an object-space offset. float3(0,0,0)
/// is the stock rigid path.
VertexOutput pbrTransformVertex(VertexInput input, float3 localOffset)
{
    return pbrTransformVertex(input.position + localOffset, input.normal, input.texcoord, input.tangent);
}

/// The stock fragment path: the stock surface, lit the stock way. The built-in fragment entry
/// points call this unchanged — it is now the composition of the two seams beneath it rather than
/// a body of its own, which is what makes "the stock surface with one change" expressible without
/// copying anything.
float4 shadePbr(FragmentInput input)
{
    return shadeSurface(input, resolvePbrSurface(input));
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

#endif // PARADISE_PBR_CORE_SLANG
