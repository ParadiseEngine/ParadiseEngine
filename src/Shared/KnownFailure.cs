namespace Paradise.Testing;

/// <summary>Quarantines a test that is known to fail in CI, without hiding it anywhere else.</summary>
/// <remarks>Only skips when <c>CI</c> is set (GitHub Actions sets it), so a developer still sees the
/// failure locally and CI's skip budget counts it. Every call names the issue tracking the fix; remove
/// the call when that issue closes.</remarks>
internal static class KnownFailure
{
    public static void SkipInCi(int issue)
    {
        if (Environment.GetEnvironmentVariable("CI") is "true" or "1")
        {
            global::TUnit.Core.Skip.Test($"Known failure in CI, tracked in https://github.com/ParadiseEngine/ParadiseEngine/issues/{issue}");
        }
    }
}
