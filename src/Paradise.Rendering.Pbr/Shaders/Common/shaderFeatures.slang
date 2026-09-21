#ifndef PARADISE_SHADER_FEATURES_SLANG
#define PARADISE_SHADER_FEATURES_SLANG

// Pipeline specialization removes inactive code without changing the shared binding/UBO layout.
// Defaults preserve old callers. The ID is the contract with PbrShaderSpecialization, independent
// of Slang's emitted identifier suffixes; games including the PBR core inherit the same contract.
[[vk::constant_id(19000)]] const uint pbrEnabledFeatures = 32767u;
static const uint PbrShadows = 1u;
static const uint PbrContactShadows = 2u;
static const uint PbrSsao = 4u;
static const uint PbrRayTracedAo = 8u;
static const uint PbrSsr = 16u;
static const uint PbrProbes = 32u;
static const uint PbrDecals = 64u;
static const uint PbrLightGrid = 128u;
static const uint PbrProcedural = 256u;
static const uint PbrBaseColorMap = 512u;
static const uint PbrMetallicRoughnessMap = 1024u;
static const uint PbrNormalMap = 2048u;
static const uint PbrOcclusionMap = 4096u;
static const uint PbrEmissiveMap = 8192u;
static const uint PbrSoftShadows = 16384u;

bool pbrFeatureEnabled(uint feature) { return (pbrEnabledFeatures & feature) != 0u; }
#endif
