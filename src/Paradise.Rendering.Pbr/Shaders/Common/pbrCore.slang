// PBR shader extension API. Individual includes expose noise, procedural recipes, lighting and
// surface resolution separately.
// Extension steps: displace vertices before pbrTransformVertex; modify resolvePbrSurface's
// PbrSurface; then call shadeSurface (or shadePbrCore for individual values).
// Games include Common/pbrCore.slang through the packaged Slang include path, add group-2 bindings
// from slot 7 and define entry points. See waterFixture.slang and surfaceFixture.slang for
// examples.
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

#endif // PARADISE_PBR_CORE_SLANG
