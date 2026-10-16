// surfaceFixture.slang — the RESOLVE-MODIFY-SHADE seam, compiled by this test project exactly the
// way a game compiles one. Where waterFixture.slang exercises the vertex seam, this exercises the
// fragment one that surface.slang exists for: take the stock surface as a value, change one field,
// and hand it to the stock lighting.
//
// ShaderExtensionTests.cs checks both seams: with no modification this reproduces the
// built-in path byte for byte, and a modification changes the image.
//
// material.procColorA carries the injected emissive, so the fixture needs no group-2 binding of
// its own (the same free-lane trick refractionFixture.slang uses for its screen size).
#include "Common/pbrCore.slang"

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    return pbrTransformVertex(input, float3(0.0, 0.0, 0.0));
}

[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    PbrSurface surface = resolvePbrSurface(input);
    surface.emissive += material.procColorA.rgb;
    return shadeSurface(input, surface);
}
