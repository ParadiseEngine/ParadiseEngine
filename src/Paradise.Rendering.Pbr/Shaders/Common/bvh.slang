// bvh.slang — the compute ray tracer's scene: the wide quantized BVH nodes Paradise.Geometry
// builds, the merged triangle and vertex buffers, the per-frame instance table, and a two-level
// closest-hit traversal over them. Included by every tracing shader; the buffers bind in the
// group PARADISE_TRACE_GROUP names (default 1), so a shader keeps group 0 for its own uniforms.
//
// Layouts mirror the C# side byte for byte: BvhNode is Paradise.Geometry.BvhNode (96 bytes),
// TraceInstance / TraceMaterial / TraceUniforms are TraceScene's records. A change on either side
// without the other is a silently wrong traversal, so the C# tests pin the sizes.
#ifndef PARADISE_BVH_SLANG
#define PARADISE_BVH_SLANG

#ifndef PARADISE_TRACE_GROUP
#define PARADISE_TRACE_GROUP 1
#endif

struct BvhNode
{
    float3 origin;     // quantization origin: the node's own minimum corner
    uint exponents;    // per-axis biased float exponent of the quantization step, one byte each
    uint childBase;    // first internal child node; internal children are contiguous
    uint leafBase;     // first leaf item; leaf children's items are contiguous
    uint pad0;
    uint pad1;
    uint4 meta;        // eight 16-bit child words: 0 empty, 0x4000|slot internal, 0x8000|count<<8|offset leaf
    uint4 loXY;        // quantized child minima: x in .xy (child 0 low byte), y in .zw
    uint4 hiXY;
    uint2 loZ;
    uint2 hiZ;
};

struct TraceInstance
{
    float4x4 worldToObject; // the ray moves into the mesh's space; t is preserved (no renormalizing)
    float4x4 normalMatrix;  // inverse-transpose of the model matrix, for object-space normals
    uint rootNode;          // this mesh's BVH root in traceNodes
    uint material;          // index into traceMaterials
    uint flags;
    uint pad;
};

struct TraceMaterial
{
    float4 baseColor; // rgb albedo factor (textures are not read at a hit), a: metallic factor
    float4 emissive;  // rgb emissive factor, a unused
};

struct TraceUniforms
{
    uint tlasRoot;      // the instance hierarchy's root node in traceNodes
    uint instanceCount; // 0 disables tracing: every ray misses
    uint pad0;
    uint pad1;
};

[[vk::binding(0, PARADISE_TRACE_GROUP)]]
StructuredBuffer<BvhNode> traceNodes;

[[vk::binding(1, PARADISE_TRACE_GROUP)]]
StructuredBuffer<uint4> traceTriangles; // xyz: vertex indices into traceVertices, w unused

// Two float4 per vertex: [2i] = position (w unused), [2i+1] = unit normal (w unused).
[[vk::binding(2, PARADISE_TRACE_GROUP)]]
StructuredBuffer<float4> traceVertices;

[[vk::binding(3, PARADISE_TRACE_GROUP)]]
StructuredBuffer<TraceInstance> traceInstances;

[[vk::binding(4, PARADISE_TRACE_GROUP)]]
StructuredBuffer<TraceMaterial> traceMaterials;

[[vk::binding(5, PARADISE_TRACE_GROUP)]]
ConstantBuffer<TraceUniforms> trace;

static const uint BvhLeafFlag = 0x8000u;
static const uint BvhInternalFlag = 0x4000u;
static const int BvhStackDepth = 32;

struct TraceHit
{
    float t;
    uint instance;
    uint triangle;   // index into traceTriangles
    float2 bary;     // (u, v) of vertices 1 and 2
};

uint bvhChildMeta(BvhNode node, int child)
{
    return (node.meta[child >> 1] >> ((child & 1) * 16)) & 0xFFFFu;
}

uint bvhByte(uint4 xy, uint2 z, int axis, int child)
{
    uint word = axis == 2 ? z[child >> 2] : xy[axis * 2 + (child >> 2)];
    return (word >> ((child & 3) * 8)) & 0xFFu;
}

// 1/d with a zero component replaced by a huge finite value: infinity times a zero offset is NaN,
// and NaN's comparisons differ between GPUs. Paradise.Geometry.Aabb.InverseDirection is the twin.
float3 safeInverse(float3 d)
{
    float3 inv;
    inv.x = abs(d.x) < 1e-20 ? (d.x < 0.0 ? -1e20 : 1e20) : 1.0 / d.x;
    inv.y = abs(d.y) < 1e-20 ? (d.y < 0.0 ? -1e20 : 1e20) : 1.0 / d.y;
    inv.z = abs(d.z) < 1e-20 ? (d.z < 0.0 ? -1e20 : 1e20) : 1.0 / d.z;
    return inv;
}

// Slab test of one child's decoded box; the entry distance or +inf on a miss within (0, tMax).
float bvhChildEntry(BvhNode node, int child, float3 origin, float3 invDir, float tMax)
{
    float3 scale = float3(
        asfloat((node.exponents & 0xFFu) << 23),
        asfloat(((node.exponents >> 8) & 0xFFu) << 23),
        asfloat(((node.exponents >> 16) & 0xFFu) << 23));
    float3 lo = float3(
        bvhByte(node.loXY, node.loZ, 0, child),
        bvhByte(node.loXY, node.loZ, 1, child),
        bvhByte(node.loXY, node.loZ, 2, child));
    float3 hi = float3(
        bvhByte(node.hiXY, node.hiZ, 0, child),
        bvhByte(node.hiXY, node.hiZ, 1, child),
        bvhByte(node.hiXY, node.hiZ, 2, child));
    float3 bmin = node.origin + lo * scale;
    float3 bmax = node.origin + hi * scale;
    float3 t0 = (bmin - origin) * invDir;
    float3 t1 = (bmax - origin) * invDir;
    float3 near = min(t0, t1);
    float3 far = max(t0, t1);
    float enter = max(max(near.x, near.y), max(near.z, 0.0));
    float exitT = min(min(far.x, far.y), min(far.z, tMax));
    return enter <= exitT ? enter : 1.0 / 0.0;
}

// Möller–Trumbore, two-sided. Paradise.Geometry.RayTriangle is the twin.
bool rayTriangle(float3 origin, float3 dir, float3 a, float3 b, float3 c, float tMax, out float t, out float2 bary)
{
    t = 0.0;
    bary = float2(0.0, 0.0);
    float3 e1 = b - a;
    float3 e2 = c - a;
    float3 p = cross(dir, e2);
    float det = dot(e1, p);
    if (abs(det) < 1e-12) return false;
    float invDet = 1.0 / det;
    float3 s = origin - a;
    float u = dot(s, p) * invDet;
    if (u < 0.0 || u > 1.0) return false;
    float3 q = cross(s, e1);
    float v = dot(dir, q) * invDet;
    if (v < 0.0 || u + v > 1.0) return false;
    t = dot(e2, q) * invDet;
    bary = float2(u, v);
    return t > 0.0 && t < tMax;
}

/// Closest hit against one mesh's hierarchy, in that mesh's object space. Shrinks tMax.
bool traceMesh(uint rootNode, float3 origin, float3 dir, inout float tMax, out uint triangle, out float2 bary)
{
    triangle = 0;
    bary = float2(0.0, 0.0);
    float3 invDir = safeInverse(dir);
    uint stack[BvhStackDepth];
    int depth = 0;
    stack[depth++] = rootNode;
    bool hit = false;

    while (depth > 0)
    {
        BvhNode node = traceNodes[stack[--depth]];
        for (int child = 0; child < 8; child++)
        {
            uint meta = bvhChildMeta(node, child);
            if (meta == 0u) continue;
            if (isinf(bvhChildEntry(node, child, origin, invDir, tMax))) continue;

            if ((meta & BvhLeafFlag) != 0u)
            {
                uint first = node.leafBase + (meta & 0xFFu);
                uint count = (meta >> 8) & 0x3Fu;
                for (uint i = first; i < first + count; i++)
                {
                    uint4 tri = traceTriangles[i];
                    float t;
                    float2 uv;
                    if (rayTriangle(origin, dir,
                            traceVertices[tri.x * 2].xyz, traceVertices[tri.y * 2].xyz, traceVertices[tri.z * 2].xyz,
                            tMax, t, uv))
                    {
                        tMax = t;
                        triangle = i;
                        bary = uv;
                        hit = true;
                    }
                }
            }
            else if (depth < BvhStackDepth)
            {
                stack[depth++] = node.childBase + (meta & 7u);
            }
        }
    }
    return hit;
}

/// Closest hit through the instance hierarchy, in world space. A world-space t: the ray is moved
/// into each instance's object space without renormalizing its direction, so distances agree.
bool traceClosest(float3 origin, float3 dir, float tMax, out TraceHit hit)
{
    hit.t = tMax;
    hit.instance = 0;
    hit.triangle = 0;
    hit.bary = float2(0.0, 0.0);
    if (trace.instanceCount == 0u) return false;

    float3 invDir = safeInverse(dir);
    uint stack[BvhStackDepth];
    int depth = 0;
    stack[depth++] = trace.tlasRoot;
    bool any = false;

    while (depth > 0)
    {
        BvhNode node = traceNodes[stack[--depth]];
        for (int child = 0; child < 8; child++)
        {
            uint meta = bvhChildMeta(node, child);
            if (meta == 0u) continue;
            if (isinf(bvhChildEntry(node, child, origin, invDir, hit.t))) continue;

            if ((meta & BvhLeafFlag) != 0u)
            {
                uint first = node.leafBase + (meta & 0xFFu);
                uint count = (meta >> 8) & 0x3Fu;
                for (uint i = first; i < first + count; i++)
                {
                    TraceInstance inst = traceInstances[i];
                    float3 o = mul(inst.worldToObject, float4(origin, 1.0)).xyz;
                    float3 d = mul((float3x3)inst.worldToObject, dir);
                    uint triangle;
                    float2 bary;
                    float t = hit.t;
                    if (traceMesh(inst.rootNode, o, d, t, triangle, bary))
                    {
                        hit.t = t;
                        hit.instance = i;
                        hit.triangle = triangle;
                        hit.bary = bary;
                        any = true;
                    }
                }
            }
            else if (depth < BvhStackDepth)
            {
                stack[depth++] = node.childBase + (meta & 7u);
            }
        }
    }
    return any;
}

/// Any hit within tMax — for shadow and occlusion rays, where the first hit is enough.
bool traceAny(float3 origin, float3 dir, float tMax)
{
    TraceHit hit;
    return traceClosest(origin, dir, tMax, hit);
}

/// The interpolated, world-space unit normal at a hit, flipped to face the ray.
float3 traceHitNormal(TraceHit hit, float3 dir)
{
    uint4 tri = traceTriangles[hit.triangle];
    float3 n0 = traceVertices[tri.x * 2 + 1].xyz;
    float3 n1 = traceVertices[tri.y * 2 + 1].xyz;
    float3 n2 = traceVertices[tri.z * 2 + 1].xyz;
    float3 n = n0 * (1.0 - hit.bary.x - hit.bary.y) + n1 * hit.bary.x + n2 * hit.bary.y;
    TraceInstance inst = traceInstances[hit.instance];
    float3 world = normalize(mul((float3x3)inst.normalMatrix, n));
    return dot(world, dir) > 0.0 ? -world : world;
}

#endif // PARADISE_BVH_SLANG
