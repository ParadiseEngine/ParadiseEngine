// Draws a fullscreen triangle at one device depth in a flat color. Depth-tested against the SDF
// path's depth target, it survives exactly where the traced scene is not nearer.

struct PlaneUniforms
{
    float4 color;
    float4 depth; // x: device depth of the plane
};

[[vk::binding(0, 0)]] ConstantBuffer<PlaneUniforms> plane;

[shader("vertex")]
float4 vertexMain(uint vertexId : SV_VertexID) : SV_Position
{
    float2 p = float2((vertexId << 1) & 2, vertexId & 2);
    return float4(p * 2.0 - 1.0, plane.depth.x, 1.0);
}

[shader("fragment")]
float4 fragmentMain() : SV_Target
{
    return plane.color;
}
