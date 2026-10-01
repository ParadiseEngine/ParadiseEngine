// Self-contained triangle fixture compiled into WGSL and reflection resources for backend tests.
// Its float2 position and float3 color pin the loader's 20-byte vertex-layout contract.

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
