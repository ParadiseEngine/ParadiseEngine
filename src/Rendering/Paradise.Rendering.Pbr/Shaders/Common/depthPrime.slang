// Depth priming (SceneFeature.DepthPrime): the vertex transform of a shading draw with its depth
// pushed back by DepthPrimeSlack. A shading fragment more than the slack behind the nearest opaque
// surface fails the main pass's depth test before it is shaded; the visible surface passes, and the
// shading draws' own Less test against what they write decides each pixel as without priming. The
// slack also absorbs rounding differences between the two pipelines' compiled transforms.
#ifndef PARADISE_DEPTH_PRIME_SLANG
#define PARADISE_DEPTH_PRIME_SLANG

#include "uniforms.slang"

// About 16 ulps of a far depth, a few millimetres of view distance at typical ranges.
static const float DepthPrimeSlack = 1e-6;

VertexOutput depthPrimed(VertexOutput output)
{
    output.position.z += DepthPrimeSlack * output.position.w;
    return output;
}

#endif // PARADISE_DEPTH_PRIME_SLANG
