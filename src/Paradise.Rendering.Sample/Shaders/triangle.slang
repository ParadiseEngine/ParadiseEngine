// Authoring source-of-truth for the M1 triangle. Compiled to triangle.wgsl + triangle.reflection.json
// at build time by src/Slang.targets. The runtime sample loads the embedded outputs and derives the
// vertex layout from the reflection record (NOT hand-coded) — that's the M1 design contract.

struct VsIn
{
    float2 pos : POSITION;
    float3 color : COLOR;
};

struct VsOut
{
    float4 pos : SV_Position;
    float3 color : COLOR;
};

[shader("vertex")]
VsOut vs_main(VsIn input)
{
    VsOut o;
    o.pos = float4(input.pos, 0.0, 1.0);
    o.color = input.color;
    return o;
}

[shader("fragment")]
float4 fs_main(VsOut input) : SV_Target
{
    return float4(input.color, 1.0);
}
