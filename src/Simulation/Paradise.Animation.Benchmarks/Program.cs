using BenchmarkDotNet.Running;

using Paradise.Animation.Benchmarks;

if (args.Length > 0 && args[0] == "verify")
{
    Verify.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
