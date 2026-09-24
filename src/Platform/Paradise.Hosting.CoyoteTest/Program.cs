using Microsoft.Coyote.SystematicTesting;

namespace Paradise.Hosting.CoyoteTest;

/// <summary>Runs systematic host coordination and snapshot ownership tests.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var iterations = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 200;
        var tests = new (string Name, Func<Task> Action)[]
        {
            (nameof(HeadlessWindowTests.ConcurrentInputAndClosePreserveEachTransition),
                HeadlessWindowTests.ConcurrentInputAndClosePreserveEachTransition),
            (nameof(HostLifetimeTests.FailedStartup_ReleasesPresentationWithoutConstructingIt),
                HostLifetimeTests.FailedStartup_ReleasesPresentationWithoutConstructingIt),
            (nameof(HostLifetimeTests.CloseRacingPresentationStartup_KeepsWorldAliveUntilConsumerExits),
                HostLifetimeTests.CloseRacingPresentationStartup_KeepsWorldAliveUntilConsumerExits),
            (nameof(HostLifetimeTests.RenderFailureRacingClose_PreservesFailureAndConsumerDisposalOrder),
                HostLifetimeTests.RenderFailureRacingClose_PreservesFailureAndConsumerDisposalOrder),
            (nameof(HostLifetimeTests.ResizeRacingConsumer_PreservesWholeSizeAndZeroDimensions),
                HostLifetimeTests.ResizeRacingConsumer_PreservesWholeSizeAndZeroDimensions),
            (nameof(SnapshotStreamConcurrencyTests.LatestReturnRacingPoolRead_KeepsSimulationInputReserved),
                SnapshotStreamConcurrencyTests.LatestReturnRacingPoolRead_KeepsSimulationInputReserved),
            (nameof(SnapshotStreamConcurrencyTests.PublishRacingReturn_ReleasesThePreviousWorldExactlyOnce),
                SnapshotStreamConcurrencyTests.PublishRacingReturn_ReleasesThePreviousWorldExactlyOnce),
            (nameof(SnapshotStreamConcurrencyTests.OverflowRacingBorrow_NeverRecyclesAWorldBeingRead),
                SnapshotStreamConcurrencyTests.OverflowRacingBorrow_NeverRecyclesAWorldBeingRead),
            (nameof(SnapshotStreamConcurrencyTests.OverflowRacingReturn_PreservesEachWorldExactlyOnce),
                SnapshotStreamConcurrencyTests.OverflowRacingReturn_PreservesEachWorldExactlyOnce),
        };

        var failed = 0;
        foreach (var (name, action) in tests)
        {
            var configuration = Microsoft.Coyote.Configuration.Create().WithTestingIterations((uint)iterations);
            var engine = TestingEngine.Create(configuration, action);
            engine.Run();
            var report = engine.TestReport;
            if (report.NumOfFoundBugs == 0)
            {
                Console.WriteLine($"passed  {name}  ({iterations} iterations explored)");
                continue;
            }

            failed++;
            Console.WriteLine($"FAILED  {name}");
            foreach (var bug in report.BugReports)
            {
                Console.WriteLine($"        {bug}");
            }
        }

        return failed == 0 ? 0 : 1;
    }
}
