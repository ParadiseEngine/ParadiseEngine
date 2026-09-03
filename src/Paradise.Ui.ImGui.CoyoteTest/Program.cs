using Microsoft.Coyote.SystematicTesting;

namespace Paradise.Ui.ImGui.CoyoteTest;

/// <summary>
/// Entry point for the ImGui texture-op queue's Coyote tests. Run with: <c>dotnet run [iterations]</c>.
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

        Console.WriteLine($"Running Coyote ImGui frame-handoff tests with {iterations} iterations...");
        Console.WriteLine();

        var tests = new (string Name, Func<Task> Action)[]
        {
            ("DrawnSnapshotsNeverNameAnUncreatedTexture", FrameExchangeTests.DrawnSnapshotsNeverNameAnUncreatedTexture),
            ("RecycledSnapshotsAreNeverHandedOutWhileBeingWritten", FrameExchangeTests.RecycledSnapshotsAreNeverHandedOutWhileBeingWritten),
            ("DrainRacingEnqueue_LosesNothingAndKeepsOrder", TextureOpsTests.DrainRacingEnqueue_LosesNothingAndKeepsOrder),
            ("ConcurrentProducers_KeepEachSequenceInOrder", TextureOpsTests.ConcurrentProducers_KeepEachSequenceInOrder),
            ("DrainAndPendingCount_AlwaysAccountForEveryOp", TextureOpsTests.DrainAndPendingCount_AlwaysAccountForEveryOp),
        };

        var failed = 0;
        foreach (var (name, action) in tests)
        {
            var configuration = Microsoft.Coyote.Configuration.Create()
                .WithTestingIterations((uint)iterations);
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
