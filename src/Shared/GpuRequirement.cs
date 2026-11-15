namespace Paradise.Testing;

/// <summary>Skips a GPU test when no WebGPU device is available, unless the run requires one.</summary>
/// <remarks>Linked into each GPU test project. CI sets <c>PARADISE_REQUIRE_GPU=1</c> on the lane that
/// installs a (software) Vulkan driver, so a missing Dawn dependency or adapter fails that lane instead
/// of turning the GPU suite into skips. Local runs without a GPU keep skipping.</remarks>
internal static class GpuRequirement
{
    public const string EnvironmentVariable = "PARADISE_REQUIRE_GPU";

    public static bool Required => Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true";

    public static void Skip(string reason)
    {
        if (Required)
        {
            throw new InvalidOperationException($"{EnvironmentVariable} is set, but the GPU test could not run: {reason}");
        }

        global::TUnit.Core.Skip.Test(reason);
    }
}
