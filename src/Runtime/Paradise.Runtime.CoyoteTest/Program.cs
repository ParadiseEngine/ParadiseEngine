using Microsoft.Coyote.SystematicTesting;

namespace Paradise.Runtime.CoyoteTest;

/// <summary>Runs systematic tests of the asset loader's worker-to-owner handoff.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var iterations = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 200;
        var tests = new (string Name, Func<Task> Action)[]
        {
            (nameof(AssetLoaderConcurrencyTests.UpdatesRacingDecodes_UploadEachAssetOnceWithinBudget),
                AssetLoaderConcurrencyTests.UpdatesRacingDecodes_UploadEachAssetOnceWithinBudget),
            (nameof(AssetLoaderConcurrencyTests.ReleaseRacingDecode_DiscardsWithoutUploading),
                AssetLoaderConcurrencyTests.ReleaseRacingDecode_DiscardsWithoutUploading),
            (nameof(AssetLoaderConcurrencyTests.DisposeRacingDecodes_DisposesEveryDecodeOnce),
                AssetLoaderConcurrencyTests.DisposeRacingDecodes_DisposesEveryDecodeOnce),
            (nameof(AssetLoaderConcurrencyTests.ReloadRacingDecode_SharesOneDecode),
                AssetLoaderConcurrencyTests.ReloadRacingDecode_SharesOneDecode),
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
