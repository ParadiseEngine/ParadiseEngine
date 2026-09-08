struct MotionDraw
{
    float4x4 currentMvp;
    float4x4 previousMvp;
    float4 params; // current joint base, previous joint base, history valid
};

[[vk::binding(0, 0)]] ConstantBuffer<MotionDraw> motion;
[[vk::binding(0, 1)]] StructuredBuffer<float4x4> currentJoints;
[[vk::binding(1, 1)]] StructuredBuffer<float4x4> previousJoints;

// Reflection determines the stride, so both inputs declare the complete uploaded streams.
struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent : TANGENT;
};

struct VertexInputSkinned
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
    [[vk::location(2)]] float2 texcoord : TEXCOORD0;
    [[vk::location(3)]] float4 tangent : TANGENT;
    [[vk::location(4)]] float4 joints : JOINTS;
    [[vk::location(5)]] float4 weights : WEIGHTS;
};

struct Varyings
{
    float4 position : SV_Position;
    float4 currentClip : TEXCOORD0;
    float4 previousClip : TEXCOORD1;
};

Varyings project(float3 currentPosition, float3 previousPosition)
{
    Varyings output;
    output.currentClip = mul(motion.currentMvp, float4(currentPosition, 1.0));
    output.previousClip = mul(motion.previousMvp, float4(previousPosition, 1.0));
    output.position = output.currentClip;
    return output;
}

[shader("vertex")]
Varyings vertexMain(VertexInput input)
{
    return project(input.position, input.position);
}

[shader("vertex")]
Varyings vertexMainSkinned(VertexInputSkinned input)
{
    float3 currentPosition = input.position;
    float3 previousPosition = input.position;
    if (dot(input.weights, float4(1.0)) > 0.0001)
    {
        uint currentBase = (uint)motion.params.x;
        uint previousBase = (uint)motion.params.y;
        float4x4 currentSkin = currentJoints[currentBase + (uint)input.joints.x] * input.weights.x
                            + currentJoints[currentBase + (uint)input.joints.y] * input.weights.y
                            + currentJoints[currentBase + (uint)input.joints.z] * input.weights.z
                            + currentJoints[currentBase + (uint)input.joints.w] * input.weights.w;
        currentPosition = mul(currentSkin, float4(input.position, 1.0)).xyz;
        previousPosition = currentPosition;
        if (motion.params.z > 0.5)
        {
            float4x4 previousSkin = previousJoints[previousBase + (uint)input.joints.x] * input.weights.x
                                 + previousJoints[previousBase + (uint)input.joints.y] * input.weights.y
                                 + previousJoints[previousBase + (uint)input.joints.z] * input.weights.z
                                 + previousJoints[previousBase + (uint)input.joints.w] * input.weights.w;
            previousPosition = mul(previousSkin, float4(input.position, 1.0)).xyz;
        }
    }
    return project(currentPosition, previousPosition);
}

[shader("fragment")]
float4 fragmentMain(Varyings input) : SV_Target
{
    // Divide after interpolation. Interpolating divided vertex UVs breaks perspective motion.
    if (motion.params.z < 0.5 || input.previousClip.w <= 0.00001 || input.currentClip.w <= 0.00001)
        return float4(0.0);
    float3 currentNdc = input.currentClip.xyz / input.currentClip.w;
    float3 previousNdc = input.previousClip.xyz / input.previousClip.w;
    if (previousNdc.z < 0.0 || previousNdc.z > 1.0) return float4(0.0);
    return float4((currentNdc.xy - previousNdc.xy) * float2(0.5, -0.5), previousNdc.z, 1.0);
}
