using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The extractor chain is the extension point a game reaches through <c>BuildHost.Run</c>: what
/// reads a source container is a list, not a hardcoded extension. These cover the dispatch itself —
/// that a format the engine has never heard of is claimed, and that an appended extractor outranks
/// the built-in — rather than what any one extractor produces.
/// </summary>
public class AssetExtractorSeamTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    /// <summary>A game's importer that also reads a container: claims an extension the engine does not know, and reports its parts as authored so verify has something to say.</summary>
    private sealed class CrateExtractor(bool extracted = false) : IAssetImporter
    {
        public string Name => "crate";

        public bool RecordsIdentity => true;

        public bool Claims(ImportCandidate candidate)
            => string.Equals(candidate.Asset.GetExtensionWithDot(), ".crate", StringComparison.OrdinalIgnoreCase);

        public bool Import(ImportContext context, List<string> errors) => true;

        /// <summary>A kind of its own, and one it shares with the built-ins: both must route.</summary>
        public IReadOnlyList<ExtractKindDeclaration> ExtractKinds { get; } =
            [new("tilesets"), new("tilemaps", FallsBackTo: "tilesets"), new(ExtractKind.Materials)];

        public bool HasParts(IFileSystem fileSystem, UPath source) => true;

        public bool HasAuthoredParts(IFileSystem fileSystem, UPath source) => true;

        public bool IsExtracted(IFileSystem fileSystem, UPath source) => extracted;

        public ExtractResult Extract(ExtractRequest request) => new(true, [], [], [], []);

        public ExtractResult MintReferences(ExtractRequest request) => new(true, [], [], [], []);
    }

    /// <summary>Claims everything, to prove precedence rather than to be useful.</summary>
    private sealed class GreedyExtractor : IAssetImporter
    {
        public string Name => "greedy";

        public bool RecordsIdentity => true;

        public IReadOnlyList<ExtractKindDeclaration> ExtractKinds { get; } = [new("greed")];

        public bool Claims(ImportCandidate candidate) => true;

        public bool Import(ImportContext context, List<string> errors) => true;

        public bool HasParts(IFileSystem fileSystem, UPath source) => false;

        public bool HasAuthoredParts(IFileSystem fileSystem, UPath source) => false;

        public bool IsExtracted(IFileSystem fileSystem, UPath source) => true;

        public ExtractResult Extract(ExtractRequest request) => new(true, [], [], [], []);

        public ExtractResult MintReferences(ExtractRequest request) => new(true, [], [], [], []);
    }

    [Test]
    public async Task the_chain_claims_by_extractor_and_an_appended_one_shadows_a_built_in()
    {
        using var fileSystem = ProjectVerifierTests.CreateProject();
        IReadOnlyList<IAssetImporter> extended = [.. AssetImporters.All, new CrateExtractor()];

        // Each format goes to the extractor that claims it, and nothing claims a plain document.
        await Assert.That(ImporterChain.Extractor(extended, fileSystem, s_layout, "/game/assets/models/crate.glb")?.Name).IsEqualTo("glb");
        await Assert.That(ImporterChain.Extractor(extended, fileSystem, s_layout, "/game/assets/models/crate.crate")?.Name).IsEqualTo("crate");
        await Assert.That(ImporterChain.Extractor(extended, fileSystem, s_layout, "/game/assets/levels/main.prefab")).IsNull();

        // The default chain has never heard of the game's format.
        await Assert.That(ImporterChain.Extractor(AssetImporters.All, fileSystem, s_layout, "/game/assets/models/crate.crate")).IsNull();

        // Walked backwards, so what a game appends outranks the built-in for the SAME container.
        IReadOnlyList<IAssetImporter> shadowed = [.. AssetImporters.All, new GreedyExtractor()];
        await Assert.That(ImporterChain.Extractor(shadowed, fileSystem, s_layout, "/game/assets/models/crate.glb")?.Name).IsEqualTo("greedy");
    }

    [Test]
    public async Task verify_reaches_a_games_container_through_the_chain()
    {
        // Dispatch, not a lookup helper: verify only knows "something claims this", so a format the
        // engine cannot read still gets the un-extracted finding — and does not when the chain that
        // claims it is absent.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fileSystem, "/game/assets/models/box.crate");

        var withoutIt = ProjectVerifier.Verify(fileSystem, s_layout);
        await Assert.That(withoutIt.Any(finding => finding.Path == "/game/assets/models/box.crate")).IsFalse();

        var withIt = ProjectVerifier.Verify(fileSystem, s_layout, [.. AssetImporters.All, new CrateExtractor()]);

        var found = withIt.Single(finding => finding.Path == "/game/assets/models/box.crate");
        await Assert.That(found.Severity).IsEqualTo(VerifySeverity.Warning);
        await Assert.That(found.Message).Contains("has not been extracted");

        // An extractor that says it is already extracted has nothing to report.
        var done = ProjectVerifier.Verify(fileSystem, s_layout, [.. AssetImporters.All, new CrateExtractor(extracted: true)]);
        await Assert.That(done.Any(finding => finding.Path == "/game/assets/models/box.crate")).IsFalse();
    }

    [Test]
    public async Task an_extract_key_no_extractor_declares_is_a_verify_error_naming_what_is_available()
    {
        // The open key set moves this check from the manifest to here, where the chain is known.
        // A game's kind is fine when its extractor is in the build, and a typo is still caught.
        using var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllText(
            "/game/assets/project.toml",
            "name = \"x\"\nschema_version = 1\n\n[extract]\nmaterials = \"materials\"\ntilesets = \"tilesets\"\n");

        var withGame = ProjectVerifier.Verify(fileSystem, s_layout, [.. AssetImporters.All, new CrateExtractor()]);
        await Assert.That(withGame.Any(finding => finding.Message.Contains("[extract]"))).IsFalse();

        var without = ProjectVerifier.Verify(fileSystem, s_layout);

        var found = without.Single(finding => finding.Message.Contains("[extract]"));
        await Assert.That(found.Severity).IsEqualTo(VerifySeverity.Error);
        await Assert.That(found.Message).Contains("tilesets");
        await Assert.That(found.Message).Contains("meshes");   // names what IS declared
    }

    [Test]
    public async Task the_chain_declares_its_kinds_nearest_first()
    {
        var kinds = ImporterChain.ExtractKinds([.. AssetImporters.All, new CrateExtractor()]);

        // The appended extractor's declarations come first, so its redeclaration of a shared kind
        // is the one a lookup finds.
        await Assert.That(kinds[0].Id).IsEqualTo("tilesets");
        await Assert.That(kinds.Count(kind => kind.Id == ExtractKind.Materials)).IsEqualTo(2);
        await Assert.That(kinds.Select(kind => kind.Id)).Contains(ExtractKind.Prefabs);
    }

    [Test]
    public async Task the_chain_names_what_it_can_read()
    {
        await Assert.That(ImporterChain.KnownExtractors(AssetImporters.All)).IsEqualTo("glb");
        await Assert.That(ImporterChain.KnownExtractors([.. AssetImporters.All, new CrateExtractor()])).IsEqualTo("glb, crate");
        await Assert.That(ImporterChain.KnownExtractors([])).IsEqualTo("none");
    }
}
