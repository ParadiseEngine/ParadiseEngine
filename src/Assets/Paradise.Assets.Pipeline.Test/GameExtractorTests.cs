using System.Security.Cryptography;
using System.Text;

using Paradise.Assets.Documents;
using Paradise.Assets.Project;
using Paradise.Authoring;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// A container format the engine has never heard of, extracted by a game's own extractor, with no
/// engine change at all. This is the seam's real test: not that the chain dispatches, but that
/// everything downstream — routing by a kind the game invented, guid identity, the keep-in-step
/// rule and its conflicts, the sidecar record — works for a format whose bytes the engine cannot
/// read.
/// </summary>
/// <remarks>
/// <see cref="CrateExtractor"/> is deliberately written the way a game would write one, out of the
/// public surface only, and is short enough to read as the worked example it is.
/// </remarks>
public class GameExtractorTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private const string Crate = "/game/assets/models/village.crate";

    /// <summary>Kind ids: one the game invented, one it shares with the built-ins.</summary>
    private const string Tilesets = "tilesets";

    /// <summary>
    /// A <c>.crate</c> is one part per line: <c>kind:name:payload</c>. A tileset is tool-owned (the
    /// container is its only side); a material is two-sided (the author edits it, and the container
    /// can change under them).
    /// </summary>
    private sealed class CrateExtractor : IAssetImporter
    {
        public string Name => "crate";

        public bool RecordsIdentity => true;

        public IReadOnlyList<ExtractKindDeclaration> ExtractKinds { get; } = [new(Tilesets), new(ExtractKind.Materials)];

        public bool Claims(ImportCandidate candidate)
            => string.Equals(candidate.Asset.GetExtensionWithDot(), ".crate", StringComparison.OrdinalIgnoreCase);

        /// <summary>A `.crate` ships nothing of its own — what the build reads is the parts it was extracted into.</summary>
        public bool Import(ImportContext context, List<string> errors) => true;

        public bool HasParts(IFileSystem fileSystem, UPath source) => fileSystem.FileExists(source);

        public bool HasAuthoredParts(IFileSystem fileSystem, UPath source)
            => Lines(fileSystem, source).Any(line => line.Kind == ExtractKind.Materials);

        public bool IsExtracted(IFileSystem fileSystem, UPath source)
        {
            var sidecar = SidecarMeta.PathFor(source);
            return fileSystem.FileExists(sidecar) && ExtractionRecord.Read(SidecarMeta.Load(fileSystem, sidecar)).Authored;
        }

        public ExtractResult MintReferences(ExtractRequest request) => Run(request, toolOwnedOnly: true);

        public ExtractResult Extract(ExtractRequest request) => Run(request, toolOwnedOnly: false);

        private static IEnumerable<(string Kind, string Name, string Payload)> Lines(IFileSystem fileSystem, UPath source)
        {
            if (!fileSystem.FileExists(source)) yield break;
            foreach (var line in fileSystem.ReadAllText(source).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(':');
                if (parts.Length == 3) yield return (parts[0], parts[1], parts[2]);
            }
        }

        private static string Fingerprint(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        private ExtractResult Run(ExtractRequest request, bool toolOwnedOnly)
        {
            var fileSystem = request.FileSystem;
            var errors = new List<string>();
            var written = new List<ExtractedFile>();
            var kept = new List<string>();
            var minted = new List<UPath>();

            var manifest = ProjectManifest.Load(fileSystem, request.Layout.Manifest);
            var index = AssetIndex.Scan(fileSystem, request.Layout.Assets, manifest.Ignore);
            var sidecarPath = SidecarMeta.PathFor(request.Source);
            var recorded = ExtractionRecord.Read(SidecarMeta.Load(fileSystem, sidecarPath));
            var stem = Path.GetFileNameWithoutExtension(request.Source.GetName());
            var parts = new List<ExtractedPart>();
            var order = 0;

            foreach (var (kind, name, payload) in Lines(fileSystem, request.Source))
            {
                var slot = order++;
                var toolOwned = kind == Tilesets;
                if (toolOwnedOnly && !toolOwned) continue;

                // Routing: the project's directory for this kind, the kind's declared fallback, then
                // `directory`, then beside the container — all of it the engine's, for a kind the
                // engine has never heard of.
                var relative = recorded.Directory ?? manifest.Extract.DirectoryFor(kind, ExtractKinds);
                var directory = relative is null ? request.Source.GetDirectory() : (request.Layout.Assets / relative).ToAbsolute();

                // A recorded part is found by GUID, so a file an author moved is re-synced where it
                // now lives rather than written again at the default path.
                var previous = recorded.Find(kind, slot);
                var fallback = directory / $"{stem}.{name}{(toolOwned ? ".tileset" : ".material")}";
                var path = previous is not null && index.Resolve(previous.Reference) is { Found: true } found ? found.Asset : fallback;

                var fresh = Encoding.UTF8.GetBytes(payload);
                var source = Fingerprint(fresh);
                var onDisk = fileSystem.FileExists(path) ? Fingerprint(fileSystem.ReadAllBytes(path)) : null;

                void Write()
                {
                    fileSystem.CreateDirectory(path.GetDirectory());
                    fileSystem.WriteAllBytes(path, fresh);
                    minted.Add(path);
                }

                if (toolOwned)
                {
                    // No second side: the container is the only author, so it is written whenever
                    // it does not already say what the container says.
                    if (onDisk != source) { Write(); written.Add(new ExtractedFile(index.Relative(path))); }
                    else kept.Add(index.Relative(path));
                    parts.Add(new ExtractedPart(kind, PartOwnership.ToolOwned, slot, name, new AssetReference(Guid.Empty, index.Relative(path))));
                    continue;
                }

                var outcome = ExtractionSync.Decide(source, onDisk, previous, request.Resolution, "tile material");
                switch (outcome.Action)
                {
                    case SyncAction.Create or SyncAction.TakeSource:
                        Write();
                        written.Add(new ExtractedFile(index.Relative(path), outcome.Note));
                        parts.Add(new ExtractedPart(kind, PartOwnership.TwoSided, slot, name, new AssetReference(Guid.Empty, index.Relative(path)), source, source));
                        break;

                    case SyncAction.Adopt:
                        kept.Add($"{index.Relative(path)} ({outcome.Note})");
                        parts.Add(new ExtractedPart(kind, PartOwnership.TwoSided, slot, name, new AssetReference(Guid.Empty, index.Relative(path)), source, source));
                        break;

                    case SyncAction.Unchanged:
                        kept.Add(index.Relative(path));
                        // The guid decides and the path is a hint, so an unchanged part still has
                        // its path caught up: the file may have been filed somewhere else since.
                        parts.Add(previous! with { Reference = previous.Reference with { Path = index.Relative(path) } });
                        break;

                    case SyncAction.Refuse:
                        errors.Add($"{index.Relative(path)}: {outcome.Problem}");
                        if (previous is not null) parts.Add(previous);
                        break;

                    default:
                        // This format cannot write an edit back into a .crate, so the record keeps
                        // its last-synced pair and the divergence stays visible.
                        kept.Add(index.Relative(path));
                        parts.Add(previous! with { Reference = previous.Reference with { Path = index.Relative(path) } });
                        break;
                }
            }

            // Identity: the maintainer mints a sidecar for each written file, then a rescan resolves
            // every path to the guid that is now on it.
            var maintainer = request.Maintainer ?? new SidecarMaintainer(fileSystem, request.Layout, request.Logger, ignore: manifest.Ignore, importers: request.Importers);
            foreach (var path in minted) maintainer.Ensure(path);
            index = AssetIndex.Scan(fileSystem, request.Layout.Assets, manifest.Ignore);

            var identified = parts
                .Select(part => part.Reference.Guid != Guid.Empty
                    ? part
                    : part with { Reference = new AssetReference(index.IdentityOf(index.Root / part.Reference.Path) ?? Guid.Empty, part.Reference.Path) })
                .ToList();

            var meta = SidecarMeta.Load(fileSystem, sidecarPath);
            ExtractionRecord.Write(meta, Name, new Extraction(recorded.Directory, identified));
            meta.Save(fileSystem, sidecarPath);

            return new ExtractResult(errors.Count == 0, errors, written, kept, [], HasAuthoredParts: true);
        }
    }

    private static MemoryFileSystem Project(string container = "tilesets:grass:GRASS-1\nmaterials:stone:STONE-1\n", string? extract = null)
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        fileSystem.WriteAllText("/game/assets/project.toml", $"name = \"x\"\nschema_version = 1\n{extract ?? ""}");
        fileSystem.CreateDirectory("/game/assets/models");
        fileSystem.WriteAllText(Crate, container);
        ProjectVerifierTests.Mint(fileSystem, Crate);
        return fileSystem;
    }

    private static ExtractRequest Request(IFileSystem fileSystem, ConflictResolution resolution = ConflictResolution.Refuse)
        => new(fileSystem, s_layout, Crate, AssetImporters.All, resolution);

    [Test]
    public async Task a_games_container_routes_its_own_kind_and_gets_identities()
    {
        using var fileSystem = Project(extract: "\n[extract]\ndirectory = \"cooked\"\ntilesets = \"tiles\"\nmaterials = \"materials\"\n");

        var result = new CrateExtractor().Extract(Request(fileSystem));

        await Assert.That(result.Errors).IsEmpty();

        // The game's own kind routes exactly like a built-in one, and so does the kind it shares.
        await Assert.That(fileSystem.FileExists("/game/assets/tiles/village.grass.tileset")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/assets/materials/village.stone.material")).IsTrue();

        // Each extracted file carries a minted identity, and the record names it.
        var extraction = ExtractionRecord.Read(SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(Crate)));
        await Assert.That(ExtractionRecord.WrittenBy(SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(Crate)))).IsEqualTo("crate");
        await Assert.That(extraction.Parts.Count).IsEqualTo(2);
        await Assert.That(extraction.Parts.All(part => part.Reference.Guid != Guid.Empty)).IsTrue();
        await Assert.That(extraction.OfKind(Tilesets).Single().Reference.Path).IsEqualTo("tiles/village.grass.tileset");
        await Assert.That(extraction.Authored).IsTrue();

        var tileset = extraction.OfKind(Tilesets).Single().Reference.Guid;
        await Assert.That(SidecarMeta.Load(fileSystem, "/game/assets/tiles/village.grass.tileset.meta").Guid).IsEqualTo(tileset);
    }

    [Test]
    public async Task an_unnamed_kind_falls_back_and_a_second_run_writes_nothing()
    {
        // `tilesets` names no directory, so it takes the section's fallback — the engine's rule,
        // applied to a kind only the game knows.
        using var fileSystem = Project(extract: "\n[extract]\ndirectory = \"cooked\"\n");
        var extractor = new CrateExtractor();

        await Assert.That(extractor.Extract(Request(fileSystem)).Errors).IsEmpty();
        await Assert.That(fileSystem.FileExists("/game/assets/cooked/village.grass.tileset")).IsTrue();
        await Assert.That(fileSystem.FileExists("/game/assets/cooked/village.stone.material")).IsTrue();

        var again = extractor.Extract(Request(fileSystem));

        await Assert.That(again.Errors).IsEmpty();
        await Assert.That(again.Written).IsEmpty();
        await Assert.That(again.Kept.Count).IsEqualTo(2);
    }

    [Test]
    public async Task an_edit_and_a_re_export_are_told_apart_and_a_clash_is_refused()
    {
        using var fileSystem = Project();
        var extractor = new CrateExtractor();
        await Assert.That(extractor.Extract(Request(fileSystem)).Errors).IsEmpty();
        var material = (UPath)"/game/assets/models/village.stone.material";

        // The container changed and the file did not: re-extracted.
        fileSystem.WriteAllText(Crate, "tilesets:grass:GRASS-1\nmaterials:stone:STONE-2\n");
        var reExported = extractor.Extract(Request(fileSystem));
        await Assert.That(reExported.Errors).IsEmpty();
        await Assert.That(fileSystem.ReadAllText(material)).IsEqualTo("STONE-2");

        // Both changed: refused, and the message names the flags rather than guessing.
        fileSystem.WriteAllText(material, "HAND-EDITED");
        fileSystem.WriteAllText(Crate, "tilesets:grass:GRASS-1\nmaterials:stone:STONE-3\n");
        var clash = extractor.Extract(Request(fileSystem));
        await Assert.That(clash.Succeeded).IsFalse();
        await Assert.That(clash.Errors.Single()).Contains("tile material");
        await Assert.That(fileSystem.ReadAllText(material)).IsEqualTo("HAND-EDITED");

        // The flag resolves it, and the edit is gone because the container won.
        await Assert.That(extractor.Extract(Request(fileSystem, ConflictResolution.TakeGlb)).Succeeded).IsTrue();
        await Assert.That(fileSystem.ReadAllText(material)).IsEqualTo("STONE-3");
    }

    [Test]
    public async Task a_moved_extracted_file_is_followed_by_guid_not_written_again()
    {
        using var fileSystem = Project(extract: "\n[extract]\nmaterials = \"materials\"\ntilesets = \"tiles\"\n");
        var extractor = new CrateExtractor();
        await Assert.That(extractor.Extract(Request(fileSystem)).Errors).IsEmpty();

        // The author files it somewhere else, sidecar and all — the guid is the identity.
        fileSystem.CreateDirectory("/game/assets/materials/tiles");
        foreach (var suffix in new[] { "", ".meta" })
        {
            fileSystem.MoveFile($"/game/assets/materials/village.stone.material{suffix}", $"/game/assets/materials/tiles/stone.material{suffix}");
        }

        var again = extractor.Extract(Request(fileSystem));

        await Assert.That(again.Errors).IsEmpty();
        await Assert.That(again.Written).IsEmpty();
        await Assert.That(fileSystem.FileExists("/game/assets/materials/village.stone.material")).IsFalse();

        var extraction = ExtractionRecord.Read(SidecarMeta.Load(fileSystem, SidecarMeta.PathFor(Crate)));
        await Assert.That(extraction.OfKind(ExtractKind.Materials).Single().Reference.Path).IsEqualTo("materials/tiles/stone.material");
    }
}
