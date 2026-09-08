// Binding-free object-space hash, value-noise, fbm and Worley helpers with [0,1] output. Include
// directly for effects that need noise without lighting resources.
#ifndef PARADISE_PBR_NOISE_SLANG
#define PARADISE_PBR_NOISE_SLANG

float hash31(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float3 hash33(float3 p)
{
    p = float3(dot(p, float3(127.1, 311.7, 74.7)),
               dot(p, float3(269.5, 183.3, 246.1)),
               dot(p, float3(113.5, 271.9, 124.6)));
    return frac(sin(p) * 43758.5453);
}

float valueNoise(float3 x)
{
    float3 i = floor(x);
    float3 f = frac(x);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(lerp(hash31(i + float3(0,0,0)), hash31(i + float3(1,0,0)), f.x),
                     lerp(hash31(i + float3(0,1,0)), hash31(i + float3(1,1,0)), f.x), f.y),
                lerp(lerp(hash31(i + float3(0,0,1)), hash31(i + float3(1,0,1)), f.x),
                     lerp(hash31(i + float3(0,1,1)), hash31(i + float3(1,1,1)), f.x), f.y), f.z);
}

float fbm(float3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; i++) { v += a * valueNoise(p); p *= 2.02; a *= 0.5; }
    return v;
}

// Worley (cellular) F1 distance — facets, frost cells, flecks.
float worley(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float d = 1.0;
    for (int x = -1; x <= 1; x++)
    for (int y = -1; y <= 1; y++)
    for (int z = -1; z <= 1; z++)
    {
        float3 g = float3(x, y, z);
        float3 o = hash33(i + g);
        float3 r = g + o - f;
        d = min(d, dot(r, r));
    }
    return sqrt(d);
}

#endif // PARADISE_PBR_NOISE_SLANG
