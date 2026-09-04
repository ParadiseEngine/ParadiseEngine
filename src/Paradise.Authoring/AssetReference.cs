using System.Runtime.InteropServices;

namespace Paradise.Authoring;

/// <summary>
/// A reference from an authored document to an asset: the asset's GUID, plus its path.
/// </summary>
/// <remarks>
/// <para>
/// <b>The GUID is the identity; the path is a HINT.</b> Every consumer resolves through the one
/// scan of <c>assets/</c> that reads the sidecars (<c>Paradise.Assets.Pipeline.AssetIndex</c>,
/// which holds what exists and which asset carries which guid), so a rename done
/// outside <c>paradise assets mv</c> — Finder, <c>git mv</c> — cannot break a reference: the
/// sidecar travels with the file, and <c>watch</c> relinks an identity a delete-then-add split.
/// </para>
/// <para>
/// The path is written anyway because a GUID alone is unreadable in a diff and unsearchable in a
/// grep. When it falls out of date <c>verify</c> reports a WARNING naming where the asset now
/// lives, and <c>verify --fix</c> rewrites it; a GUID no asset carries stays an error, because
/// nothing can stand in for an identity that is gone. Where the two disagree the GUID wins —
/// resolving by path would repoint every reference at the wrong asset the first time two
/// filenames were swapped.
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
