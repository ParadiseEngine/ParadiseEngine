using Microsoft.Coyote.SystematicTesting;

namespace Paradise.Rendering.WebGPU.CoyoteTest;

/// <summary>
/// Entry point for the renderer's Coyote tests. Run with: <c>dotnet run [iterations]</c>.
///
/// Not a <c>dotnet test</c> project on purpose — see the csproj. For real systematic exploration
/// build Release first so the <c>coyote rewrite</c> target runs; without rewriting these still
/// execute, but as ordinary concurrent code rather than scheduled interleavings.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var iterations = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 200;

        Console.WriteLine($"Running Coyote renderer tests with {iterations} iterations...");
        Console.WriteLine();

        var tests = new (string Name, Func<Task> Action)[]
        {
            ("EnqueueRacingClose_LeavesNothingPending", CaptureQueueTests.EnqueueRacingClose_LeavesNothingPending),
            ("DrainRacingEnqueueAndClose_ServesOrFaultsEveryRequest", CaptureQueueTests.DrainRacingEnqueueAndClose_ServesOrFaultsEveryRequest),
            ("ConcurrentCloses_AreIdempotent", CaptureQueueTests.ConcurrentCloses_AreIdempotent),
        };

        var failed = 0;
        foreach (var (name, action) in tests)
        {
            var configuration = Microsoft.Coyote.Configuration.Create()
                .WithTestingIterations((uint)iterations)
                // No deadlock-detection workaround: these tests AWAIT their joins rather than
                // blocking on them, so Coyote schedules the wait instead of seeing a parked thread
                // it must guess about. Hang detection stays ON and is a signal here, not noise.
                ;
            var engine = TestingEngine.Create(configuration, action);
            engine.Run();

            var report = engine.TestReport;
            if (report.NumOfFoundBugs > 0)
            {
                failed++;
                Console.WriteLine($"FAILED  {name}");
                foreach (var bug in report.BugReports)
                {
                    Console.WriteLine($"        {bug}");
                }
            }
            else
            {
                Console.WriteLine($"passed  {name}  ({iterations} iterations explored)");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "All Coyote tests passed." : $"{failed} Coyote test(s) FAILED.");
        return failed == 0 ? 0 : 1;
    }
}
