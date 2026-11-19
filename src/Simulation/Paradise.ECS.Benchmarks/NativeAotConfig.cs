using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.NativeAot;

namespace Paradise.ECS.Concurrent.Benchmarks;

/// <summary>
/// Configuration for running benchmarks with NativeAOT toolchain.
/// </summary>
/// <remarks>Apply [Config(typeof(NativeAotConfig))] to select the .NET 11 NativeAOT short-run job.</remarks>
public class NativeAotConfig : ManualConfig
{
    public NativeAotConfig()
    {
        // Create NativeAOT toolchain for .NET 11
        var job = Job.ShortRun
            .WithToolchain(CsProjNativeAotToolchain.Net11_0)
            .WithIterationCount(10)
            .WithWarmupCount(3)
            .WithEvaluateOverhead(true)
            .WithId("NativeAOT");

        AddJob(job);
        AddColumnProvider(DefaultColumnProviders.Instance);
    }
}

/// <summary>Configuration comparing both runtime and NativeAOT toolchains.</summary>
public class MultiToolchainConfig : ManualConfig
{
    public MultiToolchainConfig()
    {
        // Default runtime (JIT)
        AddJob(Job.ShortRun.WithId("JIT"));

        // NativeAOT (.NET 11)
        AddJob(Job.ShortRun
            .WithToolchain(CsProjNativeAotToolchain.Net11_0)
            .WithId("NativeAOT"));

        AddColumnProvider(DefaultColumnProviders.Instance);
    }
}
