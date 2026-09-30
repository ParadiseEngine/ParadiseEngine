using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;

namespace Paradise.ECS.Jobs.CoyoteTest;

/// <summary>Runs rewritten Jobs interleavings with deadlock detection enabled.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        int iterations = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 200;
        var tests = new (string Name, Func<Task> Action)[]
        {
            (nameof(JobWorkerPoolTests.DelayedClaim_CannotCrossWaveBoundary), JobWorkerPoolTests.DelayedClaim_CannotCrossWaveBoundary),
            (nameof(JobWorkerPoolTests.UnequalWavesAndExceptions_CompleteExactlyOnce), JobWorkerPoolTests.UnequalWavesAndExceptions_CompleteExactlyOnce),
            (nameof(JobWorkerPoolTests.Dispose_RacingCallback_WaitsForCompletion), JobWorkerPoolTests.Dispose_RacingCallback_WaitsForCompletion),
            (nameof(JobWorkerPoolTests.Dispose_FromCallbacks_IsRejected), JobWorkerPoolTests.Dispose_FromCallbacks_IsRejected),
        };

        int failed = 0;
        foreach (var (name, action) in tests)
        {
            var configuration = Configuration.Create().WithTestingIterations((uint)iterations);
            using var engine = TestingEngine.Create(configuration, action);
            engine.Run();
            if (engine.TestReport.NumOfFoundBugs == 0)
            {
                Console.WriteLine($"passed  {name}  ({iterations} iterations explored)");
                continue;
            }

            failed++;
            Console.WriteLine($"FAILED  {name}");
            foreach (string bug in engine.TestReport.BugReports)
                Console.WriteLine($"        {bug}");
        }

        return failed == 0 ? 0 : 1;
    }
}
