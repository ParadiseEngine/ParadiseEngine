using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The outcome of checking one scene document.</summary>
public enum PrefabCheckOutcome
{
    /// <summary>Parses and is byte-canonical.</summary>
    Canonical,

    /// <summary>Parses, but its bytes differ from the canonical write.</summary>
    NotCanonical,

    /// <summary>Was rewritten into canonical form (fix mode).</summary>
    Rewritten,

    /// <summary>Does not parse; the message says why.</summary>
    Invalid,
}

/// <summary>One scene document's check result.</summary>
/// <param name="Path">The document.</param>
/// <param name="Outcome">What was found (or done).</param>
/// <param name="Message">The parse error for <see cref="PrefabCheckOutcome.Invalid"/>; empty otherwise.</param>
public readonly record struct PrefabCheckResult(UPath Path, PrefabCheckOutcome Outcome, string Message = "");

/// <summary>
/// The <c>scene-check</c> verb: the canonical-form drift guard over every <c>*.scene</c>.
/// </summary>
/// <remarks>
/// This is the same mechanism <c>contract-check</c> proves out for the JSON contract, pointed at
/// the authoring format: parse with the strict reader, re-write canonically, compare bytes. The
/// Python mirror runs the identical check, so the two writers cannot drift without one side's
/// CI going red. Fix mode rewrites in place — safe because the rewrite is parse-then-write of
/// the same data, and only a document that already parses is touched.
/// </remarks>
public static class PrefabCheck
{
    /// <summary>Checks (and with <paramref name="fix"/>, rewrites) every scene document under <c>assets/</c>.</summary>
    /// <param name="fileSystem">The filesystem holding the project.</param>
    /// <param name="layout">The located project.</param>
    /// <param name="fix">Rewrite non-canonical documents in place instead of just reporting them.</param>
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
