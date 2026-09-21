using Microsoft.Coyote.SystematicTesting;

namespace Paradise.Features.CoyoteTest;

/// <summary>Runs the feature switchboard's Coyote tests.</summary>
/// <remarks>Build Release to rewrite binaries, then run with dotnet run -- [iterations].
/// Without rewriting, execution is ordinary concurrency, not systematic exploration.</remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        var iterations = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 200;

        Console.WriteLine($"Running Coyote feature-switch tests with {iterations} iterations...");
        Console.WriteLine();

        var tests = new (string Name, Func<Task> Action)[]
        {
            ("TwoWritersOfOneFeature_LeaveTheAnnouncementAgreeingWithTheState",
                FeatureSwitchesTests.TwoWritersOfOneFeature_LeaveTheAnnouncementAgreeingWithTheState),
            ("SetRacingReset_LeavesTheAnnouncementAgreeingWithTheState",
                FeatureSwitchesTests.SetRacingReset_LeavesTheAnnouncementAgreeingWithTheState),
            ("ApplyRacingSet_NeverLeavesALayerHalfApplied",
                FeatureSwitchesTests.ApplyRacingSet_NeverLeavesALayerHalfApplied),
            ("DeclareRacingApply_LeavesTheOverrideInForce",
                FeatureSwitchesTests.DeclareRacingApply_LeavesTheOverrideInForce),
            ("AReaderRacingAWriter_AlwaysSeesOneOfTheTwoStates",
                FeatureSwitchesTests.AReaderRacingAWriter_AlwaysSeesOneOfTheTwoStates),
        };

        var failed = 0;
        foreach (var (name, action) in tests)
        {
            // These tests AWAIT their joins rather than blocking on them, so hang detection stays
            // ON and is a signal here.
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
