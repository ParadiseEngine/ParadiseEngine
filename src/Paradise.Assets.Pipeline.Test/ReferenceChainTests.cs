using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// References are an importer's to declare, so a game's own asset kind joins the graph, `mv`,
/// `rm` and `verify` by implementing two methods — nothing in the pipeline lists formats.
/// </summary>
public class ReferenceChainTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    /// <summary>A one-line text format: the file holds the assets-relative path of what it references; its sidecar records the identity under <c>[recipe]</c>.</summary>
    private sealed class RecipeImporter : IAssetImporter
    {
        public string Name => "recipe";

        public bool RecordsIdentity => true;

        public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".recipe");

        public bool Import(ImportContext context, List<string> errors) => context.HasExtension(".recipe");

        public IReadOnlyList<IImportSettingsDomain> SettingsDomains => [new RecipeDomain()];

        private sealed class RecipeDomain : IImportSettingsDomain
        {
            public string Name => "recipe";

            public string? Problem(CanonicalTomlTable settings)
                => settings.Value("ingredient") is CanonicalInlineTable ? null : "holds no ingredient in [recipe]";
        }

        public AssetReferences References(ReferenceContext context, UPath asset)
        {
            if (!string.Equals(asset.GetExtensionWithDot(), ".recipe", StringComparison.Ordinal)) return AssetReferences.None;

            var spelled = context.FileSystem.ReadAllText(asset).Trim();
            var recorded = Recorded(context.FileSystem, asset);
            return new AssetReferences([new ReferenceSite("ingredient", recorded, spelled, spelled)]);
        }

        public RepairedDocument? Rewrite(ReferenceContext context, UPath asset)
        {
            var spelled = context.FileSystem.ReadAllText(asset).Trim();
            var reference = Recorded(context.FileSystem, asset) is { } recorded
                ? context.Index.Resolve(recorded).Current
                : context.Index.IdentityOf(context.Index.Root / spelled) is { } guid ? new AssetReference(guid, spelled) : null;
            if (reference is null) return null;

            var changed = false;
            var sidecar = SidecarMeta.PathFor(asset);
            var meta = SidecarMeta.Load(context.FileSystem, sidecar);
            if (Recorded(context.FileSystem, asset) != reference)
            {
                meta.SetSetting("recipe", new CanonicalTomlTable { { "ingredient", AssetReferenceCodec.Write(reference) } });
                meta.Save(context.FileSystem, sidecar);
                changed = true;
            }

            if (context.RewriteSources && spelled != reference.Path)
            {
                context.FileSystem.WriteAllText(asset, reference.Path + "\n");
                changed = true;
            }

            return changed ? new RepairedDocument(asset, [$"{spelled} -> {reference.Path}"]) : null;
        }

        private static AssetReference? Recorded(IFileSystem fileSystem, UPath asset)
        {
            var sidecar = SidecarMeta.PathFor(asset);
            if (!fileSystem.FileExists(sidecar)) return null;
            var table = SidecarMeta.Load(fileSystem, sidecar).Setting("recipe");
            return table?.Value("ingredient") is CanonicalInlineTable inline && AssetReferenceCodec.TryRead(inline, out var reference) ? reference : null;
        }
    }

    private static readonly IReadOnlyList<IAssetImporter> s_chain = [.. AssetImporters.All, new RecipeImporter()];

    [Test]
    public async Task a_games_own_asset_kind_joins_the_graph_by_declaring_its_references()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var flour = Asset(fileSystem, "/game/assets/pantry/flour.png", "png");
        Asset(fileSystem, "/game/assets/recipes/bread.recipe", "pantry/flour.png\n");

        // Unrecorded: the site is path-only until the importer records it.
        var before = ReferenceGraph.Build(fileSystem, s_layout, AssetIndex.Scan(fileSystem, s_layout.Assets), importers: s_chain);
        await Assert.That(before.DependentsOf(flour)).IsEmpty();
        await Assert.That(before.PathOnly.Select(entry => entry.Site.Hint ?? "")).IsEquivalentTo(new[] { "pantry/flour.png" });

        ReferenceRepair.Fix(fileSystem, s_layout, s_chain);

        var after = ReferenceGraph.Build(fileSystem, s_layout, AssetIndex.Scan(fileSystem, s_layout.Assets), importers: s_chain);
        await Assert.That(after.DependentsOf(flour).Select(edge => edge.Where)).IsEquivalentTo(new[] { "ingredient" });
        await Assert.That(after.PathOnly).IsEmpty();
    }

    [Test]
    public async Task mv_follows_a_games_own_reference_and_rm_refuses_over_it()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        Asset(fileSystem, "/game/assets/pantry/flour.png", "png");
        Asset(fileSystem, "/game/assets/recipes/bread.recipe", "pantry/flour.png\n");
        ReferenceRepair.Fix(fileSystem, s_layout, s_chain);

        var moved = AssetMover.Move(fileSystem, s_layout, "/game/assets/pantry/flour.png", "/game/assets/pantry/grains/flour.png", importers: s_chain);

        await Assert.That(moved.Errors).IsEmpty();
        await Assert.That(moved.Rewritten).IsEquivalentTo(new[] { "recipes/bread.recipe" });
        await Assert.That(fileSystem.ReadAllText("/game/assets/recipes/bread.recipe").Trim()).IsEqualTo("pantry/grains/flour.png");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout, AssetIndex.Scan(fileSystem, s_layout.Assets), s_chain)).IsEmpty();

        var removed = AssetRemover.Remove(fileSystem, s_layout, "/game/assets/pantry/grains/flour.png", importers: s_chain);
        await Assert.That(removed.Succeeded).IsFalse();
        await Assert.That(removed.Dangling[0].ReferrerPath).IsEqualTo(new UPath("/game/assets/recipes/bread.recipe"));
    }

    [Test]
    public async Task without_the_importer_the_same_file_is_nobodys_and_names_nothing()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        var flour = Asset(fileSystem, "/game/assets/pantry/flour.png", "png");
        Asset(fileSystem, "/game/assets/recipes/bread.recipe", "pantry/flour.png\n");

        var graph = ReferenceGraph.Build(fileSystem, s_layout, AssetIndex.Scan(fileSystem, s_layout.Assets));

        await Assert.That(graph.DependentsOf(flour)).IsEmpty();
        await Assert.That(graph.PathOnly).IsEmpty();
        await Assert.That(graph.Unreadable).IsEmpty();
    }

    private static Guid Asset(MemoryFileSystem fileSystem, UPath path, string text)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllText(path, text);
        return ProjectVerifierTests.Mint(fileSystem, path);
    }
}
