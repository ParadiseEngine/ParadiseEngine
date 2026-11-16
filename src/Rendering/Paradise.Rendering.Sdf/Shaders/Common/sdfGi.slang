// Binding-free layout of the SDF path's irradiance probes, shared by the passes that trace,
// blend and sample them. SdfGiUniformsGpu mirrors the struct and SdfGiMath.cs the ray set.
#ifndef PARADISE_SDF_GI_SLANG
#define PARADISE_SDF_GI_SLANG

// Texels a probe owns in its atlas row: mean radiance, its first moment along x, y and z, validity.
static const int SDF_PROBE_TEXELS = 5;

struct SdfGiUniforms
{
    float4 origin;        // xyz world position of probe (0, 0, 0), w spacing
    float4 counts;        // xyz probes along each axis, w probes per atlas row
    float4 params;        // x intensity (0 = no probes), y 1 = weigh probes by field visibility, z normal bias, w rays per probe
    float4 update;        // x blend weight of this frame's rays, y longest ray, z clearance at which a probe is fully valid, w bounce gain
    float4x4 rayRotation; // this frame's rotation of the ray set
};

// One direction of a spherical Fibonacci set, an even cover of the sphere, turned by the frame's
// rotation so successive frames sample between each other's rays.
float3 sdfGiRayDirection(int ray, int count, float4x4 rotation)
{
    float z = 1.0 - (2.0 * (float)ray + 1.0) / (float)count;
    float radius = sqrt(max(1.0 - z * z, 0.0));
    float phi = 2.39996323 * (float)ray; // the golden angle
    return normalize(mul(rotation, float4(radius * cos(phi), radius * sin(phi), z, 0.0)).xyz);
}

int2 sdfProbeTexel(int probe, int coefficient, int perRow)
{
    return int2(SDF_PROBE_TEXELS * (probe % perRow) + coefficient, probe / perRow);
}

#endif // PARADISE_SDF_GI_SLANG
