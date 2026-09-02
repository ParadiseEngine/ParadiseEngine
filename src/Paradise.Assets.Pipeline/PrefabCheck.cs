using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

public enum PrefabCheckOutcome
{
    Canonical,

    NotCanonical,

    /// <summary>Fix mode only.</summary>
    Rewritten,

    Invalid,
}

public readonly record struct PrefabCheckResult(UPath Path, PrefabCheckOutcome Outcome, string Message = "");

/// <summary>The <c>prefab-check</c> verb: parse, rewrite canonically, compare bytes. The Python mirror runs the identical check, so the two writers cannot drift without one side's CI going red.</summary>
public static class PrefabCheck
{
    public static IReadOnlyList<PrefabCheckResult> Run(IFileSystem fileSystem, AssetProjectLayout layout, bool fix = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        var results = new List<PrefabCheckResult>();
        if (!fileSystem.DirectoryExists(layout.Assets)) return results;

        foreach (var path in fileSystem
            .EnumerateFiles(layout.Assets, "*", SearchOption.AllDirectories)
            .Where(path => AssetClassifier.Classify(layout.Assets, path) == AssetClass.Prefab)
            .OrderBy(path => path.FullName, StringComparer.Ordinal))
        {
            PrefabDocument document;
            try
            {
                document = PrefabDocumentSerializer.Load(fileSystem, path);
            }
            catch (PrefabDocumentException error)
            {
                results.Add(new PrefabCheckResult(path, PrefabCheckOutcome.Invalid, error.Message));
                continue;
            }

            var canonical = PrefabDocumentSerializer.Write(document);
            if (fileSystem.ReadAllText(path) == canonical)
            {
                results.Add(new PrefabCheckResult(path, PrefabCheckOutcome.Canonical));
            }
            else if (fix)
            {
                PrefabDocumentSerializer.Save(fileSystem, path, document);
                results.Add(new PrefabCheckResult(path, PrefabCheckOutcome.Rewritten));
            }
            else
            {
                results.Add(new PrefabCheckResult(path, PrefabCheckOutcome.NotCanonical));
            }
        }

        return results;
    }
}
