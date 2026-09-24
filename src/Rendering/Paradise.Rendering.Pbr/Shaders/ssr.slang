// Trace reflections in screen space using projected pixels and reciprocal w (McGuire/Mara),
// avoiding gaps from world-space stepping. Reproject hits into prior-frame HDR. Misses and
// back-face hits return zero confidence for probe/sky fallback. Keep lighting bindings out of this
// compute program.

struct SsrUniforms
{
    float4 params;   // x: max steps, y: max distance (world), z: thickness (world), w: intensity
    float4 screen;   // xy: reflection target size (pixels), zw: pre-pass size it samples
    float4 camera;   // xyz: world camera position, w: frame index (jitter)
    float4x4 viewProj;
    float4x4 invViewProj;
    float4x4 prevViewProj;
};

[[vk::binding(0, 0)]]
ConstantBuffer<SsrUniforms> ssrUniforms;

[[vk::binding(1, 0)]]
Texture2D<float4> prepassNormalTexture;

// Same name as the scene's declaration: the loader forces UnfilterableFloat by it, which is what
// lets the pre-pass depth bind here.
[[vk::binding(2, 0)]]
Texture2D<float> prepassDepthTexture;

// The previous frame's HDR scene, linear, target-sized.
[[vk::binding(3, 0)]]
Texture2D<float4> ssrHistoryTexture;

[[vk::binding(4, 0)]]
SamplerState ssrHistorySampler;

[[vk::binding(5, 0)]]
[format("rgba16f")]
WTexture2D<float4> ssrTexture;

float hashToUnit(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return float(((word >> 22u) ^ word) & 0x00FFFFFFu) / 16777216.0;
}

float3 worldAt(float2 uv, float depth)
{
    float2 ndc = uv * float2(2.0, -2.0) + float2(-1.0, 1.0);
    float4 world = mul(ssrUniforms.invViewProj, float4(ndc, depth, 1.0));
    return world.xyz / world.w;
}

/// The pre-pass surface's distance along the view axis under `uv`, or -1 where it wrote nothing.
float sceneViewDepth(float2 uv)
{
    int2 source = int2(clamp(uv * ssrUniforms.screen.zw, float2(0.0, 0.0), ssrUniforms.screen.zw - 1.0));
    float depth = prepassDepthTexture.Load(int3(source, 0)).r;
    if (depth >= 1.0) return -1.0;
    return mul(ssrUniforms.viewProj, float4(worldAt(uv, depth), 1.0)).w;
}

bool onScreen(float2 uv)
{
    return all(uv >= 0.0) && all(uv <= 1.0);
}

/// How much a hit near the screen border counts: reflections fade out over the last 10 % so the
/// edge of what the screen knows does not cut a hard line across a floor.
float edgeFade(float2 uv)
{
    float2 border = smoothstep(0.0, 0.1, uv) * smoothstep(0.0, 0.1, 1.0 - uv);
    return border.x * border.y;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ssrMain(uint3 id: SV_DispatchThreadID)
{
    int2 pixel = int2(id.xy);
    float2 screen = ssrUniforms.screen.xy;
    if (pixel.x >= (int)screen.x || pixel.y >= (int)screen.y) return;

    float2 uv = (float2(pixel) + 0.5) / screen;
    int2 source = int2(uv * ssrUniforms.screen.zw);
    float4 normalSample = prepassNormalTexture.Load(int3(source, 0));
    float depth = prepassDepthTexture.Load(int3(source, 0)).r;
    if (normalSample.w < 0.5 || depth >= 1.0)
    {
        ssrTexture.Store(pixel, float4(0.0, 0.0, 0.0, 0.0));
        return;
    }

    float3 position = worldAt(uv, depth);
    float3 normal = normalize(normalSample.xyz);
    float3 view = normalize(ssrUniforms.camera.xyz - position);
    float3 dir = reflect(-view, normal);

    int steps = max((int)ssrUniforms.params.x, 1);
    float maxDistance = ssrUniforms.params.y;
    float thickness = ssrUniforms.params.z;

    // The ray is walked in SCREEN space: its two ends are projected once and the walk
    // interpolates between them, so a step is a whole number of pre-pass texels and no
    // surface the ray crosses on screen is skipped, however thin. Depth along the walk is
    // 1/w interpolated, which is exact under perspective.
    float rayLength = maxDistance;
    float4 clip0 = mul(ssrUniforms.viewProj, float4(position, 1.0));
    float4 clip1 = mul(ssrUniforms.viewProj, float4(position + dir * rayLength, 1.0));
    const float nearW = 0.05;
    if (clip1.w < nearW)
    {
        // The far end crosses the camera plane; shorten the ray to stop just in front of it. A
        // surface already closer than that has no ray to walk.
        rayLength *= max(clip0.w - nearW, 0.0) / max(clip0.w - clip1.w, 1e-5);
        if (rayLength <= 0.0)
        {
            ssrTexture.Store(pixel, float4(0.0, 0.0, 0.0, 0.0));
            return;
        }
        clip1 = mul(ssrUniforms.viewProj, float4(position + dir * rayLength, 1.0));
    }
    float2 prepassSize = ssrUniforms.screen.zw;
    float2 pixel0 = (clip0.xy / clip0.w * float2(0.5, -0.5) + 0.5) * prepassSize;
    float2 pixel1 = (clip1.xy / clip1.w * float2(0.5, -0.5) + 0.5) * prepassSize;
    float invW0 = 1.0 / clip0.w;
    float invW1 = 1.0 / clip1.w;
    float2 delta = pixel1 - pixel0;
    float length2d = max(length(delta), 1e-3);
    float stride = max(length2d / (float)steps, 1.0);
    // A per-pixel, per-frame offset into the first step turns the banding a fixed stride
    // leaves on a plane into noise. The walk starts a whole texel out: a sample inside the
    // origin's own texel reads the origin's own depth, sits a hair behind it, and is a hit on
    // the surface the ray just left.
    uint seed = (uint)pixel.x * 1973u + (uint)pixel.y * 9277u + (uint)ssrUniforms.camera.w * 26699u;
    float jitter = 1.0 + hashToUnit(seed);

    float sPrevious = 0.0;
    float scenePrevious = clip0.w;
    // Whether the previous sample was in front of what the screen holds there. A crossing is
    // the transition from in front to behind; a sample that is behind because the ray already
    // passed behind an occluder is not one, and must not re-hit that occluder's front face.
    bool wasInFront = true;
    float sHit = -1.0;
    float behind = 0.0;
    for (int i = 0; i < steps; i++)
    {
        float s = ((float)i + jitter) * stride / length2d;
        if (s > 1.0) break;
        float2 sampleUv = (pixel0 + delta * s) / prepassSize;
        if (!onScreen(sampleUv)) break;
        float rayDepth = 1.0 / lerp(invW0, invW1, s);
        float sceneDepth = sceneViewDepth(sampleUv);
        bool inFront = !(sceneDepth > 0.0 && rayDepth > sceneDepth);
        if (!inFront && wasInFront)
        {
            // Behind the surface this step: bisect back to the crossing, then ask how far
            // behind it the ray is there. A ray that passed BEHIND something keeps walking,
            // where testing thickness at the coarse step would have lost the surface after it.
            float lo = sPrevious;
            float hi = s;
            float sceneLo = scenePrevious;
            float sceneHi = sceneDepth;
            behind = rayDepth - sceneDepth;
            for (int b = 0; b < 5; b++)
            {
                float mid = 0.5 * (lo + hi);
                float sceneMid = sceneViewDepth((pixel0 + delta * mid) / prepassSize);
                float midBehind = 1.0 / lerp(invW0, invW1, mid) - sceneMid;
                if (midBehind > 0.0)
                {
                    hi = mid;
                    sceneHi = sceneMid;
                    behind = midBehind;
                }
                else
                {
                    lo = mid;
                    sceneLo = sceneMid;
                }
            }
            // A real crossing lands on a surface the sample before already looked at; the
            // depth on the two sides of it agrees. A jump means the ray's screen path entered
            // a SILHOUETTE — it passed behind something floating in front of the far scene —
            // which used to paint a second copy of a floating object just below it.
            bool silhouette = sceneLo <= 0.0 || abs(sceneHi - sceneLo) > thickness;
            if (behind < thickness && !silhouette)
            {
                sHit = hi;
                break;
            }
        }
        sPrevious = s;
        scenePrevious = sceneDepth;
        wasInFront = inFront;
    }

    if (sHit < 0.0)
    {
        ssrTexture.Store(pixel, float4(0.0, 0.0, 0.0, 0.0));
        return;
    }
    float2 hitUv = (pixel0 + delta * sHit) / prepassSize;
    // The screen fraction back to a world fraction (the inverse of the perspective map), for the fade.
    float travelled = sHit * clip0.w / max(clip1.w - sHit * (clip1.w - clip0.w), 1e-5) * rayLength;

    // A ray arriving at a surface from behind found the back of something, not a reflection.
    int2 hitSource = int2(clamp(hitUv * ssrUniforms.screen.zw, float2(0.0, 0.0), ssrUniforms.screen.zw - 1.0));
    float3 hitNormal = prepassNormalTexture.Load(int3(hitSource, 0)).xyz;
    if (dot(hitNormal, dir) > 0.0)
    {
        ssrTexture.Store(pixel, float4(0.0, 0.0, 0.0, 0.0));
        return;
    }

    // The color comes from LAST frame, at wherever the hit point was on screen then.
    float hitDepth = prepassDepthTexture.Load(int3(hitSource, 0)).r;
    float3 hitWorld = worldAt(hitUv, hitDepth);
    float4 previous = mul(ssrUniforms.prevViewProj, float4(hitWorld, 1.0));
    if (previous.w <= 1e-4)
    {
        ssrTexture.Store(pixel, float4(0.0, 0.0, 0.0, 0.0));
        return;
    }
    float2 previousUv = (previous.xy / previous.w) * float2(0.5, -0.5) + 0.5;
    if (!onScreen(previousUv))
    {
        ssrTexture.Store(pixel, float4(0.0, 0.0, 0.0, 0.0));
        return;
    }

    float3 color = ssrHistoryTexture.SampleLevel(ssrHistorySampler, previousUv, 0.0).rgb * ssrUniforms.params.w;
    // Fade with the hit's distance too, so the far end of a long reflection dissolves into the
    // environment fallback instead of stopping at the marching limit.
    float confidence = edgeFade(hitUv) * (1.0 - smoothstep(0.75, 1.0, travelled / maxDistance));
    ssrTexture.Store(pixel, float4(color, confidence));
}
