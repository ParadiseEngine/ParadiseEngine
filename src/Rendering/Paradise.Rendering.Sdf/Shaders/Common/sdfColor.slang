// Binding-free tone operator and output transfer for the SDF path, which shares no shader
// includes with the PBR renderer.
#ifndef PARADISE_SDF_COLOR_SLANG
#define PARADISE_SDF_COLOR_SLANG

// ACES filmic (Narkowicz fit).
float3 sdfTonemapAces(float3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

// mode follows SdfTonemapMode: 0 linear, 1 ACES.
float3 sdfApplyTonemap(float3 color, float exposure, float mode)
{
    color = max(color * exposure, 0.0);
    if (mode < 0.5) return color;
    return sdfTonemapAces(color);
}

// IEC 61966-2-1 OETF; call only when the output target does not encode sRGB.
float3 sdfLinearToSrgb(float3 linearColor)
{
    float3 lo = linearColor * 12.92;
    float3 hi = 1.055 * pow(max(linearColor, 0.0), 1.0 / 2.4) - 0.055;
    return float3(
        linearColor.x <= 0.0031308 ? lo.x : hi.x,
        linearColor.y <= 0.0031308 ? lo.y : hi.y,
        linearColor.z <= 0.0031308 ? lo.z : hi.z);
}

#endif // PARADISE_SDF_COLOR_SLANG
