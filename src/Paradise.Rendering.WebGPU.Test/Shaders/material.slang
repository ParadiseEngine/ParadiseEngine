// Copy of src/Paradise.Rendering.Sample/Shaders/material.slang. Lives under the test project so
// the reflection round-trip test can load material.reflection.json + material.wgsl out of the
// test assembly's embedded resources without taking a dependency on the sample binary. If the
// sample's shader changes shape, this fixture must be updated to match.

struct VsIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; };
struct VsOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

cbuffer FrameUniforms : register(b0, space0) { float Time; }
[[vk::binding(0, 1)]] Texture2D    AlbedoTex  : register(t0, space1);
[[vk::binding(1, 1)]] SamplerState AlbedoSamp : register(s0, space1);

[shader("vertex")]
VsOut vs_main(VsIn input)
{
    VsOut o;
    o.pos = float4(input.pos, 0.0, 1.0);
    o.uv  = input.uv + float2(Time * 0.1, 0.0);
    return o;
}

[shader("fragment")]
float4 fs_main(VsOut input) : SV_Target
{
    return AlbedoTex.Sample(AlbedoSamp, input.uv);
}
