// Copy of src/Paradise.Rendering.Sample/Shaders/triangle.slang. Lives under the test project so
// the reflection round-trip test can load triangle.reflection.json + triangle.wgsl out of the
// test assembly's embedded resources without taking a dependency on the sample binary. If the
// sample's shader changes shape, this fixture must be updated to match.

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
