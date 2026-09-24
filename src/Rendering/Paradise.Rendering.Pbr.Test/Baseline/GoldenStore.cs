using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paradise.Rendering.Pbr.Test.Baseline;

/// <summary>Where the frame-graph baseline lives on disk, and how it is refreshed.
///
/// Goldens are written next to the SOURCE, not into the build output, because their entire job is
/// to show up in a diff. Regenerate with <c>PARADISE_UPDATE_GOLDEN=1 dotnet test …</c>, then read
/// what git shows you: during the frame-graph migration the expected diff is empty, and a non-empty
/// one is the finding.</summary>
internal static class GoldenStore
{
    /// <summary>True when the run should overwrite goldens instead of asserting against them.</summary>
    internal static bool UpdateMode =>
        Environment.GetEnvironmentVariable("PARADISE_UPDATE_GOLDEN") is "1" or "true";

    /// <summary>Pass-structure goldens. Adapter-independent — one set, committed once.</summary>
    internal static string SignatureDirectory { get; } = Path.Combine(BaselineDirectory(), "Golden", "signatures");

    /// <summary>Pixel goldens, keyed by runtime identifier.
    ///
    /// Rasterization is not bit-identical across adapters, so a single committed image would be
    /// permanently red on whichever machine did not produce it. Keying by RID keeps the comparison
    /// exact where a baseline exists and silent where none does — which is the honest behaviour,
    /// since a tolerance loose enough to span Metal and lavapipe would not catch the regressions
    /// this is here to catch. The pass-structure golden is the guard that runs everywhere.</summary>
    internal static string PixelDirectory { get; } =
        Path.Combine(BaselineDirectory(), "Golden", "pixels", RuntimeInformation.RuntimeIdentifier);

    /// <summary>Where a mismatched frame is dropped so it can be opened and compared by eye.</summary>
    internal static string FailureDirectory { get; } = Path.Combine(Path.GetTempPath(), "paradise-golden-actual");

    internal static bool HasPixelBaseline => Directory.Exists(PixelDirectory);

    internal static string SignaturePath(string caseName) =>
        Path.Combine(SignatureDirectory, caseName + ".txt");

    internal static string PixelPath(string caseName) =>
        Path.Combine(PixelDirectory, caseName + ".png");

    internal static void WriteSignature(string caseName, string signature)
    {
        Directory.CreateDirectory(SignatureDirectory);
        // Normalised newlines: the goldens are committed, and a CRLF checkout on Windows must not
        // read as a whole-file drift.
        File.WriteAllText(SignaturePath(caseName), signature.ReplaceLineEndings("\n"));
    }

    internal static string? ReadSignature(string caseName)
    {
        var path = SignaturePath(caseName);
        return File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
    }

    internal static void WritePixels(string caseName, ReadOnlySpan<byte> png)
    {
        Directory.CreateDirectory(PixelDirectory);
        File.WriteAllBytes(PixelPath(caseName), png.ToArray());
    }

    internal static byte[]? ReadPixels(string caseName)
    {
        var path = PixelPath(caseName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Drop an actual-vs-expected pair somewhere a human can open them.</summary>
    internal static string WriteFailureArtifact(string caseName, string suffix, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(FailureDirectory);
        var path = Path.Combine(FailureDirectory, $"{caseName}.{suffix}");
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    // The test assembly's own source location. Walking up from the build output would have to guess
    // the configuration and TFM segments; CallerFilePath is exact and survives both.
    private static string BaselineDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile)!;
}
