// waterFixture.slang — the canonical PBR extension shader, compiled by this test project exactly
// the way a game compiles one: #include of the engine core resolved through a SlangIncludeDir
// (-I), an extra group-2 binding from slot 7 up, and its own entry points over the two seams.
// Its assertions live in ShaderExtensionTests.cs; its very compilation in CI proves the
// include-reuse mechanism on every PR.
#include "Common/pbrCore.slang"

// The extension's own binding: a heightfield sampled in the VERTEX stage (legal on both
// backends — the loader binds textures and plain samplers Vertex | Fragment).
[[vk::binding(7, 2)]]
Texture2D<float4> heightfieldTexture;

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    float2 uv = input.position.xz + 0.5;
    float h = heightfieldTexture.SampleLevel(baseColorSampler, uv, 0.0).r;
    return pbrTransformVertex(input, float3(0.0, h, 0.0));
}

[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target
{
    // A resolved-surface path: geometric normal, factor-driven material, full lighting core.
    float3 geometricNormal = input.isFrontFace ? input.worldNormal : -input.worldNormal;
    float3 N = normalize(geometricNormal);
    return shadePbrCore(input, N, material.baseColorFactor, material.metallicFactor,
        material.roughnessFactor, 1.0, float3(0.0, 0.0, 0.0));
}
