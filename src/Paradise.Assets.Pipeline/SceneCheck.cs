using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Assets.Pipeline;

/// <summary>The outcome of checking one scene document.</summary>
public enum SceneCheckOutcome
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
/// <param name="Message">The parse error for <see cref="SceneCheckOutcome.Invalid"/>; empty otherwise.</param>
public readonly record struct SceneCheckResult(UPath Path, SceneCheckOutcome Outcome, string Message = "");

/// <summary>
/// The <c>scene-check</c> verb: the canonical-form drift guard over every <c>*.scene.toml</c>.
/// </summary>
/// <remarks>
/// This is the same mechanism <c>contract-check</c> proves out for the JSON contract, pointed at
/// the authoring format: parse with the strict reader, re-write canonically, compare bytes. The
/// Python mirror runs the identical check, so the two writers cannot drift without one side's
/// CI going red. Fix mode rewrites in place — safe because the rewrite is parse-then-write of
/// the same data, and only a document that already parses is touched.
/// </remarks>
public static class SceneCheck
{
    /// <summary>Checks (and with <paramref name="fix"/>, rewrites) every scene document under <c>assets/</c>.</summary>
    /// <param name="fileSystem">The filesystem holding the project.</param>
    /// <param name="layout">The located project.</param>
    /// <param name="fix">Rewrite non-canonical documents in place instead of just reporting them.</param>
    public static IReadOnlyList<SceneCheckResult> Run(IFileSystem fileSystem, AssetProjectLayout layout, bool fix = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        var results = new List<SceneCheckResult>();
        if (!fileSystem.DirectoryExists(layout.Assets)) return results;

        foreach (var path in fileSystem
            .EnumerateFiles(layout.Assets, "*", SearchOption.AllDirectories)
            .Where(path => AssetClassifier.Classify(layout.Assets, path) == AssetClass.Scene)
            .OrderBy(path => path.FullName, StringComparer.Ordinal))
        {
            SceneDocument document;
            try
            {
                document = SceneDocumentSerializer.Load(fileSystem, path);
            }
            catch (SceneDocumentException error)
            {
                results.Add(new SceneCheckResult(path, SceneCheckOutcome.Invalid, error.Message));
                continue;
            }

            var canonical = SceneDocumentSerializer.Write(document);
            if (fileSystem.ReadAllText(path) == canonical)
            {
                results.Add(new SceneCheckResult(path, SceneCheckOutcome.Canonical));
            }
            else if (fix)
            {
                SceneDocumentSerializer.Save(fileSystem, path, document);
                results.Add(new SceneCheckResult(path, SceneCheckOutcome.Rewritten));
            }
            else
            {
                results.Add(new SceneCheckResult(path, SceneCheckOutcome.NotCanonical));
            }
        }

        return results;
    }
}
