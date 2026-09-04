using Paradise.Authoring;

using Zio;

namespace Paradise.Assets.Documents;

/// <summary>Which part of a GLB a <see cref="MeshReferenceDocument"/> stands for.</summary>
public enum MeshSlot
{
    Mesh,
    Skeleton,
    Clip,
}

/// <summary>
/// The authored <c>*.mesh</c>, <c>*.skeleton</c> or <c>*.anim</c> document: a name for one part
/// of a GLB — its geometry, its rig, or one animation clip — that the build cooks into the blob
/// the runtime reads, at the document's own path. The GLB stays the one source of the geometry;
/// the document is what a prefab references and what carries the identity, so a re-export in the
/// DCC changes nothing an author has to keep in step.
/// </summary>
/// <remarks>
/// A clip is named by its glTF animation name, with the animation index as the tiebreak when the
/// name is missing, duplicated, or changed by a re-export: the name is what an animator means,
/// the index is what the file guarantees. Tool-written and never hand-edited, which is why
/// <c>extract</c> may overwrite one that disagrees with the GLB without a conflict rule.
/// </remarks>
public sealed record MeshReferenceDocument(AssetReference Source, MeshSlot Slot, string? Name = null, int? Index = null)
{
    public const int SchemaVersion = 1;

    public const string MeshSuffix = ".mesh";
    public const string SkeletonSuffix = ".skeleton";
    public const string ClipSuffix = ".anim";

    public static readonly IReadOnlyList<string> Suffixes = [MeshSuffix, SkeletonSuffix, ClipSuffix];

    private static readonly string[] s_knownKeys = ["schema_version", "source", "slot", "name", "index"];

    public static bool IsMeshReferencePath(UPath path)
        => SlotOf(path) is not null;

    /// <summary>The slot a path's extension stands for, or null for a path that is not a mesh reference.</summary>
    public static MeshSlot? SlotOf(UPath path) => path.GetExtensionWithDot()?.ToLowerInvariant() switch
    {
        MeshSuffix => MeshSlot.Mesh,
        SkeletonSuffix => MeshSlot.Skeleton,
        ClipSuffix => MeshSlot.Clip,
        _ => null,
    };

    public static string SuffixFor(MeshSlot slot) => slot switch
    {
        MeshSlot.Mesh => MeshSuffix,
        MeshSlot.Skeleton => SkeletonSuffix,
        MeshSlot.Clip => ClipSuffix,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <exception cref="FormatException">Not a readable mesh reference; the message names the problem.</exception>
    public static MeshReferenceDocument Parse(string toml, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(toml);
        ArgumentNullException.ThrowIfNull(sourceName);

        Exception Fail(string problem) => new FormatException($"{sourceName}: {problem}");
        var table = TomlDocumentReader.Parse(toml, Fail);
        TomlDocumentReader.RejectUnknownKeys(table, "at the document root", s_knownKeys, Fail);

        var version = TomlDocumentReader.RequireInteger(table, "schema_version", "at the document root", Fail);
        if (version != SchemaVersion) throw Fail($"declares schema_version = {version}, which this build cannot read (supports {SchemaVersion})");

        var source = TomlDocumentReader.OptionalTable(table, "source", "at the document root", Fail)
            ?? throw Fail("must declare 'source', the GLB it names, as { guid, path }");
        var reference = AssetReferenceCodec.Read(TomlDocumentReader.ToCanonicalValue(source, "in 'source'", Fail), "'source'", Fail)
            ?? throw Fail("'source' must be an asset reference { guid, path }");

        var slotText = TomlDocumentReader.RequireString(table, "slot", "at the document root", Fail);
        var slot = slotText switch
        {
            "mesh" => MeshSlot.Mesh,
            "skeleton" => MeshSlot.Skeleton,
            "clip" => MeshSlot.Clip,
            _ => throw Fail($"names slot '{slotText}', which is not one of mesh, skeleton, clip"),
        };

        var name = TomlDocumentReader.OptionalString(table, "name", "at the document root", Fail);
        int? index = null;
        if (table.ContainsKey("index"))
        {
            var value = TomlDocumentReader.RequireInteger(table, "index", "at the document root", Fail);
            if (value < 0 || value > int.MaxValue) throw Fail($"'index' is {value}, which is not a glTF index");
            index = (int)value;
        }

        if (slot == MeshSlot.Clip && name is null && index is null) throw Fail("a clip names its animation by 'name' or 'index'");
        if (slot != MeshSlot.Clip && (name is not null || index is not null)) throw Fail($"a {slotText} carries no 'name' or 'index'; a GLB has one of each");

        return new MeshReferenceDocument(reference, slot, name, index);
    }

    public static MeshReferenceDocument Load(IFileSystem fileSystem, UPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return Parse(fileSystem.ReadAllText(path), path.FullName);
    }

    public string Write() => CanonicalTomlWriter.WriteString(ToTable());

    public byte[] WriteBytes() => CanonicalTomlWriter.WriteBytes(ToTable());

    private CanonicalTomlTable ToTable()
    {
        var table = new CanonicalTomlTable
        {
            { "schema_version", (long)SchemaVersion },
            { "source", AssetReferenceCodec.Write(Source) },
            { "slot", Slot switch { MeshSlot.Mesh => "mesh", MeshSlot.Skeleton => "skeleton", _ => "clip" } },
        };
        if (Name is not null) table.Add("name", Name);
        if (Index is { } index) table.Add("index", (long)index);
        return table;
    }
}
