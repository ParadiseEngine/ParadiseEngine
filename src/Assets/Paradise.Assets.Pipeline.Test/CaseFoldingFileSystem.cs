using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// A filesystem that reads two spellings of one name as one path, the way APFS and NTFS do and
/// <see cref="MemoryFileSystem"/> does not.
/// </summary>
/// <remarks>
/// The behaviour worth reproducing is not "lookups ignore case" but what follows from it: an
/// existing entry KEEPS its spelling. Creating <c>models/</c> beside an existing <c>Models/</c>
/// finds the old one and writes into it, so enumeration keeps reporting <c>Models/</c> while the
/// caller believes it wrote <c>models/</c> — which is how a case-only rename in <c>assets/</c> used
/// to make a build delete its own output. Each incoming segment is therefore resolved against what
/// the tree already holds, and only a segment nothing matches is taken as spelled.
/// </remarks>
internal sealed class CaseFoldingFileSystem(IFileSystem fallback) : ComposeFileSystem(fallback, owned: false)
{
    protected override UPath ConvertPathToDelegate(UPath path)
    {
        var resolved = UPath.Root;
        foreach (var segment in path.Split())
        {
            resolved /= Existing(resolved, segment);
        }

        return resolved;
    }

    /// <summary>Paths come back exactly as the tree spells them — that is the whole point.</summary>
    protected override UPath ConvertPathFromDelegate(UPath path) => path;

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        var source = ConvertPathToDelegate(srcPath);
        FallbackSafe.MoveDirectory(source, Destination(source, destPath));
    }

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        var source = ConvertPathToDelegate(srcPath);
        FallbackSafe.MoveFile(source, Destination(source, destPath));
    }

    /// <summary>
    /// Where a move lands. A destination that resolves onto its own SOURCE is a case-only rename —
    /// the one move whose entire purpose is to change the stored spelling — so it keeps the name it
    /// was given and only its parents resolve. Anything else resolves like any other path.
    /// </summary>
    /// <remarks>
    /// Without this the double would fold such a move onto the entry already there, turning it into
    /// a move onto itself, and a test would pass over a rename that never happened. That is the
    /// divergence System.IO.Abstractions#1138 records: a mock that refuses what the real filesystem
    /// does is worse than no mock, because it decides the shape of the code that trusts it.
    /// </remarks>
    private UPath Destination(UPath source, UPath destination)
    {
        var resolved = ConvertPathToDelegate(destination);
        return resolved == source ? resolved.GetDirectory() / destination.GetName() : resolved;
    }

    private string Existing(UPath parent, string segment)
    {
        if (!FallbackSafe.DirectoryExists(parent)) return segment;

        foreach (var candidate in FallbackSafe.EnumeratePaths(parent))
        {
            var name = candidate.GetName();
            if (string.Equals(name, segment, StringComparison.OrdinalIgnoreCase)) return name;
        }

        return segment;
    }
}
