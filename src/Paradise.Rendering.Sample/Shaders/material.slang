// Authoring source-of-truth for the M2 textured quad. Compiled to material.wgsl +
// material.reflection.json at build time by src/Slang.targets. The runtime sample loads the
// embedded outputs and derives the bind-group layout from the reflection record (NOT hand-coded)
// — that's the M2 design contract's load-bearing piece on the non-trivial binding shape.
//
// Group 0 — frame uniforms (float Time).
// Group 1 — sampled texture + sampler. Explicit [[vk::binding(...)]] on the texture and sampler
// disambiguates WGSL bindings within the group (D3D register spaces collapse on WGSL: t0 and s0
// would both map to @binding(0) otherwise).

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
