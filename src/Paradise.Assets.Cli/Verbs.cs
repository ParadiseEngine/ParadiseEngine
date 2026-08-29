using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Cli;

/// <summary>The verbs' console rendering. Logic lives in the pipeline library; this prints.</summary>
internal static class Verbs
{
    public static int Verify(IFileSystem fileSystem, AssetProjectLayout layout)
    {
        var findings = ProjectVerifier.Verify(fileSystem, layout);
        foreach (var finding in findings)
        {
            var severity = finding.Severity == VerifySeverity.Error ? "error" : "warning";
            Console.WriteLine($"{severity}: {Display(fileSystem, finding.Path)}: {finding.Message}");
        }

        var errors = findings.Count(finding => finding.Severity == VerifySeverity.Error);
        Console.WriteLine($"verify: {errors} error(s), {findings.Count - errors} warning(s)");
        return errors == 0 ? 0 : 1;
    }

    public static int SceneCheck(IFileSystem fileSystem, AssetProjectLayout layout, bool fix)
    {
        var results = Pipeline.SceneCheck.Run(fileSystem, layout, fix);
        var failed = 0;
        foreach (var result in results)
        {
            switch (result.Outcome)
            {
                case SceneCheckOutcome.Invalid:
                    Console.WriteLine($"error: {Display(fileSystem, result.Path)}: {result.Message}");
                    failed++;
                    break;

                case SceneCheckOutcome.NotCanonical:
                    Console.WriteLine($"error: {Display(fileSystem, result.Path)}: not in canonical form (run scene-check --fix)");
                    failed++;
                    break;

                case SceneCheckOutcome.Rewritten:
                    Console.WriteLine($"fixed: {Display(fileSystem, result.Path)}");
                    break;
            }
        }

        Console.WriteLine($"scene-check: {results.Count} document(s), {failed} problem(s)");
        return failed == 0 ? 0 : 1;
    }

    public static int Build(IFileSystem fileSystem, AssetProjectLayout layout, string profile, bool play)
    {
        // A vendored third_party/tools/KTX-Software under the project root wins; PATH and
        // PARADISE_KTX_PATH are the fallbacks — the same probe order as KtxCreate itself.
        KtxTextureEncoder.TryCreate(fileSystem.ConvertPathToInternal(layout.Root), out var encoder);

        var runner = new BuildRunner(
            fileSystem, layout, encoder,
            log: Console.WriteLine,
            warn: message => Console.Error.WriteLine($"warning: {message}"));
        var result = runner.Run(profile, play ? Paradise.Assets.Project.ProjectOutputTarget.Play : Paradise.Assets.Project.ProjectOutputTarget.Build);

        foreach (var error in result.Errors)
        {
            Console.Error.WriteLine($"error: {error}");
        }

        Console.WriteLine(result.Succeeded
            ? $"build: {result.AssetCount} asset(s) into {Display(fileSystem, result.Output)}"
            : $"build: FAILED with {result.Errors.Count} error(s)");
        return result.Succeeded ? 0 : 1;
    }

    public static int Clean(IFileSystem fileSystem, AssetProjectLayout layout, bool keepEditor)
    {
        foreach (var removed in ProjectCleaner.Clean(fileSystem, layout, keepEditor))
        {
            Console.WriteLine($"removed: {Display(fileSystem, removed)}");
        }

        return 0;
    }

    /// <summary>
    /// A path as the user's shell knows it. Findings print OS paths (<c>C:\…</c>, not Zio's
    /// internal form) because they exist to be opened, copied, and pasted.
    /// </summary>
    private static string Display(IFileSystem fileSystem, UPath path) => fileSystem.ConvertPathToInternal(path);
}
