#ifndef PARADISE_PROBE_UPDATE_WINDOW_SLANG
#define PARADISE_PROBE_UPDATE_WINDOW_SLANG
#include "probes.slang"

// x maps an update slot to a physical probe; y maps a physical probe back to its slot, or -1.
[[vk::binding(12, 3)]]
StructuredBuffer<int2> probeUpdates;

int probeUpdateIndex(int slot)
{
    return probeUpdates[slot].x;
}

bool probeTracedThisFrame(int probe, out int slot)
{
    slot = probeUpdates[probe].y;
    return slot >= 0;
}

#endif
