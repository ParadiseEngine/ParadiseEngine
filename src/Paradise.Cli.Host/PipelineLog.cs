using Microsoft.Extensions.Logging;

using Paradise.Assets.Project;
using Paradise.Diagnostics;

using Zio;

namespace Paradise.Cli;

/// <summary>Builds the logger the CLI hands to the asset pipeline, and the path renderer that goes with it.</summary>
/// <remarks>
/// This is the host half of issue #232's seam, and the whole of what the seam is for. The pipeline
/// logs a <see cref="UPath"/> — <c>/</c>-separated, rooted at whatever it was mounted on — because
/// it does not know what that is mounted over and must not guess: <c>ConvertPathToInternal</c>
/// throws on a <c>MemoryFileSystem</c>, so a reader that translated its own paths would be a
/// try/catch in every type whose point is not caring. The CLI mounted the filesystem, so the CLI
/// is what can translate, and this is where it does it once for every message.
/// </remarks>
internal static class PipelineLog
{
    /// <summary>The pipeline's logger: bare lines on the console, with paths rendered for a person.</summary>
    public static ILogger For(IFileSystem fileSystem, AssetProjectLayout layout) =>
        ParadiseConsole.CreateLogger(
            category: string.Empty,
            new ParadiseConsoleOptions
            {
                // The pipeline's lines are the program talking to the author — "minted: …",
                // "swept: …" — and were bare Console.WriteLine before the seam. A category prefix
                // on every one of them would be new noise, not new information.
                IncludeCategory = false,
                RenderValue = value => value is UPath path ? Render(fileSystem, layout, path) : null,
            });

    /// <summary>
    /// Project-relative under <c>assets/</c>, a host path anywhere else.
    /// </summary>
    /// <remarks>
    /// Relative is what the watch log wants: <c>props/lamp.glb</c> is what the author typed into
    /// their DCC and is unambiguous inside a project. A path OUTSIDE the assets tree has no such
    /// short form, and the absolute <c>UPath</c> for it (<c>/mnt/c/proj/game/build/x</c>) cannot
    /// be pasted into Explorer or a shell, so it becomes a host path instead.
    ///
    /// The pipeline used to hold the first half of this rule itself, in a `Display` helper on
    /// SidecarMaintainer. It never had the information for the second half.
    /// </remarks>
    internal static string Render(IFileSystem fileSystem, AssetProjectLayout layout, UPath path)
    {
        var full = path.FullName;
        var root = layout.Assets.FullName;

        // The separator check is the whole point of the third clause: without it a SIBLING whose
        // name merely starts with the root's — `/game/assets-backup/x` against `/game/assets` —
        // matches, and renders as `backup/x`, which reads like a path inside the project and is
        // not one. A UPath is `/`-separated on every platform, so this is the only separator
        // involved; Path.DirectorySeparatorChar here would be the bug AGENTS.md warns about.
        if (full.Length > root.Length && full.StartsWith(root, StringComparison.Ordinal) && full[root.Length] == '/')
        {
            return full[(root.Length + 1)..];
        }

        try
        {
            return fileSystem.ConvertPathToInternal(path);
        }
        catch (NotSupportedException)
        {
            // A filesystem with no host form — memory, an archive. The UPath is then the only
            // name the path has, and printing it is better than dropping the message.
            return full;
        }
    }
}
