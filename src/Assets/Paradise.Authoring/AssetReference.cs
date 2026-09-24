using System.Runtime.InteropServices;

namespace Paradise.Authoring;

/// <summary>An authored asset reference identified by GUID, with an assets-relative path hint.</summary>
/// <remarks>
/// Resolve through <c>AssetIndex</c>: stale paths are verify warnings, missing GUIDs are errors.
/// The path names authored source; the build replaces the reference with the importer's built path.
/// Documents write <c>{ guid = "…", path = "…" }</c>; optional null references use <c>{}</c>
/// to preserve array slots.
/// </remarks>
public sealed record AssetReference
{
    /// <summary>Creates a reference.</summary>
    /// <param name="guid">The asset's authoring identity, from its sidecar or its own document.</param>
    /// <param name="path">The assets-relative authoring path, '/'-separated.</param>
    public AssetReference(Guid guid, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Guid = guid;
        Path = path;
    }

    /// <summary>The asset's authoring identity. What resolution uses.</summary>
    public Guid Guid { get; init; }

    /// <summary>
    /// The assets-relative authoring path, '/'-separated. Readable, and a hint only: a rename can
    /// leave it stale without breaking anything that reads this reference.
    /// </summary>
    public string Path { get; init; } = "";

    /// <summary>
    /// Whether this reference carries nothing usable. A document should spell that <c>{}</c> and
    /// a caller should hold <see langword="null"/>, so this is a guard against a half-built one
    /// rather than a value anybody constructs deliberately.
    /// </summary>
    public bool IsEmpty => Guid == Guid.Empty && Path.Length == 0;

    /// <inheritdoc />
    public override string ToString() => Path.Length > 0 ? $"{Path} ({Guid:D})" : Guid.ToString("D");
}
