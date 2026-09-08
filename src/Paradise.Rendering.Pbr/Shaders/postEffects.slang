struct PostUniforms
{
    float4 screen;
    float4 a;
    float4 b;
    float4 c;
    float4 d;
    float4 e;
    column_major float4x4 inverseProjection;
};

[[vk::binding(0, 0)]] Texture2D<float4> sourceColor;
[[vk::binding(1, 0)]] SamplerState linearSampler;
[[vk::binding(2, 0)]] ConstantBuffer<PostUniforms> post;
[[vk::binding(3, 0)]] Texture2D<float> prepassDepthTexture;
[[vk::binding(4, 0)]] Texture2D<float4> motionTexture;
[[vk::binding(5, 0)]] Texture2D<float4> colorLut;

struct Varyings
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
Varyings postVertex(uint id : SV_VertexID)
{
    float2 p = float2((id << 1) & 2, id & 2);
    Varyings output;
    output.position = float4(p * 2.0 - 1.0, 0.0, 1.0);
    output.uv = float2(p.x, 1.0 - p.y);
    return output;
}

float3 colorAt(float2 uv)
{
    return sourceColor.SampleLevel(linearSampler, uv, 0.0).rgb;
}

int2 pixelAt(float2 uv)
{
    return clamp(int2(uv * post.screen.xy), int2(0), int2(post.screen.xy) - 1);
}

float depthAt(float2 uv)
{
    float d = prepassDepthTexture.Load(int3(pixelAt(uv), 0));
    float4 view = mul(post.inverseProjection, float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, d, 1.0));
    return abs(view.z / max(abs(view.w), 1e-7));
}

float cocAt(float2 uv)
{
    return clamp(post.a.y * (1.0 - post.a.x / max(depthAt(uv), 0.001)), -post.a.z, post.a.z);
}

[shader("fragment")]
float4 depthOfFieldFragment(Varyings input) : SV_Target
{
    float centerCoc = cocAt(input.uv);
    float3 sum = colorAt(input.uv);
    float total = 1.0;
    // A fixed aperture disk allows near CoCs to spread over in-focus backgrounds as well as
    // gathering the center's far blur. It cannot reconstruct color hidden behind an occluder.
    for (int i = 0; i < 64; i++)
    {
        float radius = sqrt((float(i) + 0.5) / 64.0) * post.a.z;
        float angle = float(i) * 2.39996323;
        float2 uv = input.uv + float2(cos(angle), sin(angle)) * radius * post.screen.zw;
        if (any(uv < 0.0) || any(uv > 1.0)) continue;
        float sampleCoc = cocAt(uv);
        float cover = sampleCoc < -0.5 ? -sampleCoc : max(centerCoc, 0.0);
        // A zero CoC has no aperture coverage, even inside the one-pixel filter fringe.
        float weight = cover > 0.0001 ? saturate(cover - radius + 1.0) : 0.0;
        // A sharply focused foreground must not be pulled across a defocused background.
        if (sampleCoc >= -0.5 && sampleCoc < centerCoc - 1.0) weight = 0.0;
        sum += colorAt(uv) * weight;
        total += weight;
    }
    return float4(sum / total, 1.0);
}

[shader("fragment")]
float4 motionBlurFragment(Varyings input) : SV_Target
{
    float4 motion = motionTexture.Load(int3(pixelAt(input.uv), 0));
    float3 center = colorAt(input.uv);
    if (motion.w < 0.5 || post.a.x == 0.0) return float4(center, 1.0);
    float2 velocity = (motion.xy - post.b.xy) * post.a.x;
    float pixels = length(velocity * post.screen.xy);
    velocity *= min(1.0, post.a.y / max(pixels, 1e-5));
    if (pixels < 0.25) return float4(center, 1.0);
    float centerDepth = depthAt(input.uv);
    float3 sum = center;
    float total = 1.0;
    for (int i = 0; i < int(post.a.z); i++)
    {
        // Trailing shutter: motion is current UV minus previous UV in top-left coordinates.
        float2 uv = input.uv - velocity * ((float(i) + 0.5) / post.a.z);
        if (any(uv < 0.0) || any(uv > 1.0)) continue;
        float4 sampleMotion = motionTexture.Load(int3(pixelAt(uv), 0));
        if (sampleMotion.w < 0.5 || depthAt(uv) < centerDepth - post.a.w) continue;
        sum += colorAt(uv);
        total += 1.0;
    }
    return float4(sum / total, 1.0);
}

float3 lookupColor(float3 color)
{
    float size = post.e.x;
    float3 p = saturate(color) * (size - 1.0);
    float slice = floor(p.z);
    float2 uv0 = float2((p.x + slice * size + 0.5) / (size * size), (p.y + 0.5) / size);
    float2 uv1 = float2((p.x + min(slice + 1.0, size - 1.0) * size + 0.5) / (size * size), uv0.y);
    return lerp(colorLut.SampleLevel(linearSampler, uv0, 0.0).rgb,
        colorLut.SampleLevel(linearSampler, uv1, 0.0).rgb, frac(p.z));
}

[shader("fragment")]
float4 colorGradingFragment(Varyings input) : SV_Target
{
    float3 color = colorAt(input.uv);
    // Diagonal LMS adaptation: neutral settings are exactly identity.
    const float3x3 toLms = float3x3(0.390405, 0.549941, 0.00892632,
        0.0708416, 0.963172, 0.00135775, 0.0231082, 0.128021, 0.936245);
    const float3x3 fromLms = float3x3(2.85847, -1.62879, -0.024891,
        -0.210182, 1.15820, 0.000324281, -0.041812, -0.118169, 1.06867);
    if (post.a.x != 0.0 || post.a.y != 0.0)
    {
        float3 balance = exp2(float3(post.a.x * 0.5 - post.a.y * 0.2, post.a.y * 0.25, -post.a.x * 0.5 - post.a.y * 0.2));
        color = mul(fromLms, mul(toLms, color) * balance);
    }
    color = max((color - 0.18) * post.a.z + 0.18, 0.0);
    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
    color = max(lerp(float3(luma), color, post.a.w), 0.0);
    // Bound the base before exponentiation: clamping an overflowing pow afterward is too late.
    // Gamma is constrained to [0.01, 8], keeping this upper base within float32 range.
    float3 upperBase = exp2(log2(65504.0) * post.c.xyz);
    float3 lifted = max(color * post.d.xyz + post.b.xyz * (1.0 - color), 0.0);
    color = min(pow(min(lifted, upperBase), 1.0 / post.c.xyz), 65504.0);
    if (post.e.x >= 2.0) color = lerp(color, lookupColor(color), post.e.y);
    return float4(clamp(color, 0.0, 65504.0), 1.0);
}

[shader("fragment")]
float4 lensDistortionFragment(Varyings input) : SV_Target
{
    float2 p = (input.uv - 0.5) * 2.0 / post.a.z;
    float r2 = dot(p, p);
    float2 uv = 0.5 + p * (1.0 + post.a.x * r2 + post.a.y * r2 * r2) * 0.5;
    if (any(uv < 0.0) || any(uv > 1.0)) return float4(0.0, 0.0, 0.0, 1.0);
    return float4(colorAt(uv), 1.0);
}

[shader("fragment")]
float4 chromaticAberrationFragment(Varyings input) : SV_Target
{
    float2 p = (input.uv - 0.5) * 2.0;
    float2 delta = p * dot(p, p) * post.a.x * post.screen.zw;
    return float4(colorAt(input.uv + delta).r, colorAt(input.uv).g, colorAt(input.uv - delta).b, 1.0);
}

[shader("fragment")]
float4 vignetteFragment(Varyings input) : SV_Target
{
    float radius = length((input.uv - 0.5) * 2.0);
    float weight = smoothstep(post.a.y, post.a.y + post.a.z, radius) * post.a.x;
    return float4(lerp(colorAt(input.uv), post.b.xyz, weight), 1.0);
}

uint hash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    return value ^ (value >> 16);
}

[shader("fragment")]
float4 filmGrainFragment(Varyings input) : SV_Target
{
    uint2 p = uint2(input.position.xy);
    uint seed = hash(p.x + p.y * uint(post.screen.x) + uint(post.a.z) * 374761393u + asuint(post.a.w));
    float noise = float(seed & 0x00ffffffu) / 16777215.0 - 0.5;
    float3 color = colorAt(input.uv);
    float luma = saturate(dot(color, float3(0.2126, 0.7152, 0.0722)));
    float amplitude = lerp(1.0, 4.0 * luma * (1.0 - luma), post.a.y);
    return float4(max(color + noise * post.a.x * amplitude, 0.0), 1.0);
}

[shader("fragment")]
float4 sharpeningFragment(Varyings input) : SV_Target
{
    float3 center = colorAt(input.uv);
    float3 north = colorAt(input.uv - float2(0.0, post.screen.w));
    float3 south = colorAt(input.uv + float2(0.0, post.screen.w));
    float3 east = colorAt(input.uv + float2(post.screen.z, 0.0));
    float3 west = colorAt(input.uv - float2(post.screen.z, 0.0));
    float3 low = min(center, min(min(north, south), min(east, west)));
    float3 high = max(center, max(max(north, south), max(east, west)));
    float3 color = center + (center - (north + south + east + west) * 0.25) * post.a.x;
    return float4(clamp(color, low, high), 1.0);
}
