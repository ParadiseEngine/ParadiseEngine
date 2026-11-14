// Binding-free signed distance primitives and smooth CSG. SdfMath.cs mirrors every formula term
// for term as the CPU oracle; change both together.
#ifndef PARADISE_SDF_SLANG
#define PARADISE_SDF_SLANG

// The accumulated distance before any shape; exactly representable as a float.
static const float SDF_FAR = 1e9;

float sdfSphere(float3 p, float radius)
{
    return length(p) - radius;
}

float sdfBox(float3 p, float3 halfExtents, float cornerRadius)
{
    float3 q = abs(p) - (halfExtents - cornerRadius);
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0) - cornerRadius;
}

float sdfCapsule(float3 p, float radius, float halfLength)
{
    p.y -= clamp(p.y, -halfLength, halfLength);
    return length(p) - radius;
}

float sdfCylinder(float3 p, float radius, float halfHeight, float edgeRadius)
{
    float radial = length(p.xz) - (radius - edgeRadius);
    float axial = abs(p.y) - (halfHeight - edgeRadius);
    float outside = length(float2(max(radial, 0.0), max(axial, 0.0)));
    return min(max(radial, axial), 0.0) + outside - edgeRadius;
}

float sdfTorus(float3 p, float ringRadius, float tubeRadius)
{
    return length(float2(length(p.xz) - ringRadius, p.y)) - tubeRadius;
}

float sdfPlane(float3 p)
{
    return p.y;
}

// kind follows SdfShapeKind: 0 sphere, 1 box, 2 capsule, 3 cylinder, 4 torus, 5 plane.
float sdfPrimitive(float kind, float3 p, float4 size)
{
    if (kind < 0.5) return sdfSphere(p, size.x);
    if (kind < 1.5) return sdfBox(p, size.xyz, size.w);
    if (kind < 2.5) return sdfCapsule(p, size.x, size.y);
    if (kind < 3.5) return sdfCylinder(p, size.x, size.y, size.w);
    if (kind < 4.5) return sdfTorus(p, size.x, size.y);
    return sdfPlane(p);
}

// Written out rather than lerp(): a rearranged a + (b - a) * h loses b next to SDF_FAR.
float sdfMix(float a, float b, float h)
{
    return a * (1.0 - h) + b * h;
}

// Folds one shape into the accumulated field. operation follows SdfOperation: 0 union,
// 1 subtraction, 2 intersection. shapeWeight is the shape's share of the blended material.
// Outside the blend radius the polynomial forms equal the hard operation exactly.
float sdfCombine(float operation, float accumulated, float shape, float smoothness, out float shapeWeight)
{
    bool hard = smoothness <= 0.0;
    if (operation > 0.5 && operation < 1.5)
    {
        shapeWeight = 0.0;
        if (hard) return max(accumulated, -shape);
        float h = saturate(0.5 - 0.5 * (accumulated + shape) / smoothness);
        return sdfMix(accumulated, -shape, h) + smoothness * h * (1.0 - h);
    }
    if (operation >= 1.5)
    {
        if (hard)
        {
            shapeWeight = shape > accumulated ? 1.0 : 0.0;
            return max(accumulated, shape);
        }
        float h = saturate(0.5 + 0.5 * (shape - accumulated) / smoothness);
        shapeWeight = h;
        return sdfMix(accumulated, shape, h) + smoothness * h * (1.0 - h);
    }
    if (hard)
    {
        shapeWeight = shape < accumulated ? 1.0 : 0.0;
        return min(accumulated, shape);
    }
    float h = saturate(0.5 + 0.5 * (accumulated - shape) / smoothness);
    shapeWeight = h;
    return sdfMix(accumulated, shape, h) - smoothness * h * (1.0 - h);
}

#endif // PARADISE_SDF_SLANG
