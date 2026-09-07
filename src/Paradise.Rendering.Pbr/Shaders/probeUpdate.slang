// probeUpdate.slang — probe relocation and classification, one thread per probe. A probe with
// many back-face hits sits inside geometry: it is pushed out along the direction that reaches open
// space, within a fraction of the spacing so the grid stays regular; one that cannot get out is
// switched off, and a lookup then weights it to nothing. Writes the NEXT frame's state buffer in
// full — probes outside this frame's trace window are copied — so the trace and blend that ran
// this frame saw one consistent set and the next frame sees another.
#include "Common/probes.slang"

[[vk::binding(0, 0)]]
StructuredBuffer<float4> probeRays;

[[vk::binding(1, 0)]]
RWStructuredBuffer<float4> probeStatesOut;

static const float BackFaceFraction = 0.25; // above this share of back faces the probe is inside something
static const float MaxOffsetFraction = 0.45; // of the spacing, so neighbours stay ordered
static const float MinFrontDistanceFraction = 0.05; // of the spacing: closer than this is "against a wall"

[shader("compute")]
[numthreads(64, 1, 1)]
void updateMain(uint3 id: SV_DispatchThreadID)
{
    int probe = (int)id.x;
    if (probe >= probeVolume.counts.w) return;
    float4 state = probeStates[probe];
    int windowProbe = probe - probeVolume.window.x;
    if (windowProbe < 0) windowProbe += probeVolume.counts.w; // the window wraps past the last probe
    // An inactive probe was not traced: its rays are the trace's placeholder misses, which would
    // read here as open space and reactivate it at the grid point inside the wall it could not
    // leave — then it is traced, sees back faces, and is switched off again, a flicker with the
    // period of a window lap. Static geometry does not move, and a refit resets every state, so
    // once off a probe stays off.
    if (windowProbe >= probeVolume.window.y || state.w < 0.5)
    {
        probeStatesOut[probe] = state;
        return;
    }

    int rays = (int)probeVolume.params.y;
    float3 spacing = probeVolume.spacing.xyz;

    int backFaces = 0;
    float closestBack = 1e30;
    float3 closestBackDir = float3(0.0, 0.0, 0.0);
    float closestFront = 1e30;
    float3 closestFrontDir = float3(0.0, 0.0, 0.0);
    float farthestFront = 0.0;
    float3 farthestFrontDir = float3(0.0, 1.0, 0.0);
    for (int ray = 0; ray < rays; ray++)
    {
        float4 sample = probeRays[windowProbe * rays + ray];
        float3 dir = probeRayDirection(ray, rays);
        if (sample.w < 0.0)
        {
            backFaces++;
            if (-sample.w < closestBack) { closestBack = -sample.w; closestBackDir = dir; }
        }
        else
        {
            if (sample.w < closestFront) { closestFront = sample.w; closestFrontDir = dir; }
            if (sample.w > farthestFront) { farthestFront = sample.w; farthestFrontDir = dir; }
        }
    }

    float3 offset = state.xyz;
    float active = 1.0;
    float minSpacing = min(spacing.x, min(spacing.y, spacing.z));
    if ((float)backFaces > BackFaceFraction * (float)rays)
    {
        // Inside geometry: step out through the nearest back face, a little past it. Give up
        // and switch off once the offset has nowhere left to go.
        float3 push = closestBackDir * (closestBack + 0.05 * minSpacing);
        float3 candidate = offset + push;
        float3 limit = spacing * MaxOffsetFraction;
        if (any(abs(candidate) > limit))
        {
            offset = clamp(candidate, -limit, limit);
            active = 0.0;
        }
        else
        {
            offset = candidate;
        }
    }
    else if (closestFront < MinFrontDistanceFraction * minSpacing)
    {
        // Pressed against a surface: nudge away from it so the rays that matter clear it.
        float3 push = -closestFrontDir * (MinFrontDistanceFraction * minSpacing);
        offset = clamp(offset + push, -spacing * MaxOffsetFraction, spacing * MaxOffsetFraction);
    }
    else if (dot(offset, offset) > 0.0)
    {
        // Nothing wrong here: relax toward the grid point so a probe freed by a moved prop
        // returns home — but never further than the nearest surface allows, or a probe pushed
        // out of a wall walks back into it and is pushed out again, a two-frame oscillation
        // that flickers everything shaded from it.
        float room = closestFront - MinFrontDistanceFraction * minSpacing;
        float back = min(room, length(offset));
        if (back > 0.0) offset -= normalize(offset) * back;
    }
    probeStatesOut[probe] = float4(offset, active);
}
