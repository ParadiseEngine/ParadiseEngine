// Procedural material recipes, kept in parity with Godot GDShader. Input is scaled object position
// and flow-adjusted time; output overrides PBR factors. HDR emissive passes through lighting and
// bloom.
#ifndef PARADISE_PBR_PROCEDURAL_SLANG
#define PARADISE_PBR_PROCEDURAL_SLANG

#include "noise.slang"

struct ProcResult { float3 albedo; float3 emissive; float roughness; float metallic; };

ProcResult evalProcedural(int kind, float3 p, float t, float3 colorA, float3 colorB)
{
    ProcResult r;
    r.albedo = colorA;
    r.emissive = float3(0.0, 0.0, 0.0);
    r.roughness = 0.4;
    r.metallic = 0.0;

    if (kind == 1) // lava — dark crust with glowing, flowing molten cracks
    {
        float3 q = p + float3(0.0, t, 0.0) * 0.35;
        float n = fbm(q * 1.5);
        float cracks = fbm(q * 3.0 + n);
        float hot = smoothstep(0.42, 0.72, cracks);
        r.albedo = lerp(float3(0.05, 0.02, 0.01), float3(0.14, 0.06, 0.03), n);
        float3 glow = lerp(float3(0.95, 0.16, 0.02), float3(1.0, 0.75, 0.12), smoothstep(0.5, 0.95, cracks));
        r.emissive = glow * hot;
        r.roughness = 0.65;
    }
    else if (kind == 2) // marble — veined stone
    {
        float n = fbm(p * 2.0);
        float veins = abs(sin((p.x + p.y * 0.6 + n * 3.5) * 3.2));
        veins = pow(1.0 - veins, 2.2);
        r.albedo = lerp(colorA, colorB, veins);
        r.roughness = 0.28;
    }
    else if (kind == 3) // jade — layered translucent green with inner depth
    {
        float n = fbm(p * 2.5 + t * 0.12);
        float depth = fbm(p * 5.0 + n * 2.2);
        r.albedo = lerp(float3(0.05, 0.32, 0.18), float3(0.22, 0.72, 0.46), depth);
        r.emissive = float3(0.02, 0.11, 0.07) * smoothstep(0.4, 0.85, depth);
        r.roughness = 0.2;
    }
    else if (kind == 4) // ice — frosted worley cells + fine frost
    {
        float cells = worley(p * 3.0);
        float frost = fbm(p * 6.0 + t * 0.06);
        r.albedo = lerp(float3(0.58, 0.78, 0.95), float3(0.88, 0.94, 1.0), cells) + frost * 0.08;
        r.emissive = float3(0.05, 0.08, 0.13) * (1.0 - cells);
        r.roughness = 0.07 + cells * 0.1;
    }
    else if (kind == 5) // molten metal — flowing brushed metallic
    {
        float n = fbm(p * 3.0 + t * 0.25);
        r.albedo = colorA * (0.78 + n * 0.44);
        r.roughness = 0.14 + n * 0.16;
        r.metallic = 1.0;
    }
    else if (kind == 6) // obsidian — near-black volcanic glass, subtle swirl
    {
        float n = fbm(p * 4.0);
        r.albedo = lerp(float3(0.01, 0.01, 0.02), float3(0.06, 0.04, 0.09), n);
        r.roughness = 0.05 + n * 0.05;
    }
    else if (kind == 7) // gem — faceted translucent, tinted, faint internal glow
    {
        float facets = worley(p * 4.0);
        r.albedo = lerp(colorA * 0.45, colorA, facets);
        r.emissive = colorA * 0.18 * facets;
        r.roughness = 0.1;
    }
    else if (kind == 8) // amber — warm translucent resin with glowing flecks
    {
        float n = fbm(p * 3.0);
        float flecks = smoothstep(0.68, 0.74, worley(p * 8.0));
        r.albedo = lerp(float3(0.55, 0.28, 0.05), float3(0.9, 0.55, 0.16), n);
        r.emissive = float3(0.3, 0.12, 0.02) * n + float3(1.0, 0.6, 0.2) * flecks * 0.6;
        r.roughness = 0.16;
    }
    else if (kind == 9) // nebula/quartz — swirling two-tone with soft glow
    {
        float ang = fbm(p * 2.0 + t * 0.15);
        float swirl = fbm(p * 4.0 + ang * 3.0);
        r.albedo = lerp(colorA, colorB, swirl);
        r.emissive = colorB * 0.12 * smoothstep(0.5, 0.92, swirl);
        r.roughness = 0.15;
    }
    return r;
}

#endif // PARADISE_PBR_PROCEDURAL_SLANG
