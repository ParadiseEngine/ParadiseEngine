using System.Runtime.InteropServices;

namespace Paradise.Authoring;

/// <summary>
/// A reference from an authored document to an asset: the asset's GUID, plus its path.
/// </summary>
/// <remarks>
/// <para>
/// <b>The GUID is authoritative and the path is the fallback.</b> Resolution tries the GUID first,
/// so renaming or moving an asset never touches a document that references it. The path is kept
/// because a GUID alone is unreadable in a diff and, more importantly, because it is the recovery
/// route: a sidecar that gets lost or clobbered would otherwise break every reference to its
/// asset, and with the path present it degrades to something a person can fix by hand.
/// </para>
/// <para>
/// Both are written, and <c>verify</c> refuses a document where the two name DIFFERENT assets.
/// That check is the point of carrying both: a half-finished move is then a named error rather
/// than a reference that silently resolves to the wrong file.
/// </para>
/// <para>
/// <see cref="Path"/> is always the <b>authoring</b> path — <c>materials/x.toml</c>, the file that
/// exists under <c>assets/</c> — never the built one. The build flattens a reference to whatever
/// value the runtime resolves (<c>materials/x.json</c>), which is the asymmetry the export
/// contract already lives by: <i>authored as a REFERENCE, exported as a VALUE</i>.
/// </para>
/// <para>
/// In a document this is written as an inline table, <c>{ guid = "…", path = "…" }</c>. Where a
/// reference is optional — a material slot, where absent means "keep the GLB's own" — the C# side
/// is a <see langword="null"/> <see cref="AssetReference"/> and the document side is <c>{}</c>.
/// </para>
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
    /// The assets-relative authoring path, '/'-separated. Readable, and the fallback when the
    /// GUID resolves to nothing.
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
