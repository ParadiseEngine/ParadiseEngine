using Paradise.Assets.Packing;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

public class PackedBuildTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task packed_build_keeps_loose_outputs_and_packs_manifest_paths_only()
    {
        using var files = Project();
        ProjectVerifierTests.WriteDocument(files, "/game/assets/models/data.toml", "value = 1\n");
        ProjectVerifierTests.WriteCanonicalDocument(files, "/game/assets/levels/a.prefab");
        ProjectVerifierTests.AddAssetWithSidecar(files, "/game/assets/audio/init.bnk");
        ProjectVerifierTests.AddAssetWithSidecar(files, "/game/assets/audio/voice.wem");
        files.CreateDirectory("/game/build");
        files.WriteAllText("/game/build/stale.partial", "stale");
        var result = new BuildRunner(files, s_layout, null).Run("release");
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.PackPath).IsEqualTo((UPath?)"/game/build/assets.pak");
        using var pack = new AssetPackFileSystem(files, result.PackPath!.Value);
        await Assert.That(pack.ReadAllBytes("/models/data.toml").SequenceEqual(files.ReadAllBytes("/game/build/models/data.toml"))).IsTrue();
        await Assert.That(pack.ReadAllText("/manifest.json")).IsEqualTo(files.ReadAllText("/game/build/manifest.json"));
        await Assert.That(pack.FileExists("/audio/init.bnk")).IsFalse();
        await Assert.That(pack.FileExists("/audio/voice.wem")).IsFalse();
        await Assert.That(files.FileExists("/game/build/audio/init.bnk")).IsTrue();
        await Assert.That(files.FileExists("/game/build/audio/voice.wem")).IsTrue();
        await Assert.That(pack.FileExists("/.build-index.json")).IsFalse();
        await Assert.That(pack.FileExists("/assets.pak")).IsFalse();
        await Assert.That(pack.EnumerateFiles("/", "*", SearchOption.AllDirectories).Any(path => path.FullName.EndsWith(".meta", StringComparison.Ordinal))).IsFalse();
        await Assert.That(files.FileExists("/game/build/stale.partial")).IsFalse();
        var manifest = BuildManifest.Load(pack, "/manifest.json");
        await Assert.That(manifest.ByGuid.Count).IsEqualTo(3);
        var prefab = manifest.Assets.Single(asset => asset.Source == "levels/a.prefab");
        byte[] prefabBytes = pack.ReadAllBytes("/" + prefab.Path);
        await Assert.That(prefab.Size).IsEqualTo(prefabBytes.LongLength);
        await Assert.That(prefab.Sha256).IsEqualTo(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(prefabBytes)));
        await Assert.That(manifest.ByGuid[prefab.Guid!].Path).IsEqualTo(prefab.Path);
    }

    [Test]
    public async Task repeated_build_reuses_cooking_and_produces_identical_pack_bytes()
    {
        using var files = Project();
        ProjectVerifierTests.AddAssetWithSidecar(files, "/game/assets/textures/t.png");
        var encoder = new BuildRunnerTests.FakeEncoder();
        var runner = new BuildRunner(files, s_layout, encoder);
        await Assert.That(runner.Run("release").Succeeded).IsTrue();
        byte[] before = files.ReadAllBytes("/game/build/assets.pak");
        await Assert.That(runner.Run("release").Succeeded).IsTrue();
        await Assert.That(encoder.Encodes).IsEqualTo(1);
        await Assert.That(files.ReadAllBytes("/game/build/assets.pak").SequenceEqual(before)).IsTrue();
    }

    [Test]
    public async Task warm_builds_republish_complete_packs_after_changes_removals_and_profile_switches()
    {
        using var files = Project();
        ProjectVerifierTests.WriteDocument(files, "/game/assets/a.toml", "value = 1\n");
        ProjectVerifierTests.WriteDocument(files, "/game/assets/b.toml", "value = 10\n");
        var index = AssetIndex.Scan(files, s_layout.Assets);
        var runner = new BuildRunner(files, s_layout, null);
        await Assert.That(runner.Run("release", sources: index).Succeeded).IsTrue();
        byte[] original = files.ReadAllBytes("/game/build/assets.pak");
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath>()).Succeeded).IsTrue();
        await Assert.That(files.ReadAllBytes("/game/build/assets.pak").SequenceEqual(original)).IsTrue();

        files.WriteAllText("/game/assets/a.toml", "value = 2\n");
        index.Refresh(files, "/game/assets/a.toml");
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath> { "/game/assets/a.toml" }).Succeeded).IsTrue();
        using (var pack = new AssetPackFileSystem(files.ReadAllBytes("/game/build/assets.pak")))
        {
            await Assert.That(pack.ReadAllText("/a.toml")).Contains("value = 2");
            await Assert.That(pack.ReadAllText("/b.toml")).Contains("value = 10");
            await Assert.That(pack.ReadAllText("/manifest.json")).IsEqualTo(files.ReadAllText("/game/build/manifest.json"));
        }

        files.DeleteFile("/game/assets/a.toml");
        files.DeleteFile("/game/assets/a.toml.meta");
        index.Refresh(files, "/game/assets/a.toml");
        index.Refresh(files, "/game/assets/a.toml.meta");
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath> { "/game/assets/a.toml", "/game/assets/a.toml.meta" }).Succeeded).IsTrue();
        using (var pack = new AssetPackFileSystem(files.ReadAllBytes("/game/build/assets.pak")))
        {
            await Assert.That(pack.FileExists("/a.toml")).IsFalse();
            await Assert.That(pack.ReadAllText("/b.toml")).Contains("value = 10");
        }

        await Assert.That(runner.Run(sources: index, changedPaths: new HashSet<UPath>()).Succeeded).IsTrue();
        await Assert.That(files.FileExists("/game/build/assets.pak")).IsFalse();
    }

    [Test]
    public async Task failed_warm_publication_recovers_without_a_new_asset_event()
    {
        using var files = Project();
        ProjectVerifierTests.WriteDocument(files, "/game/assets/a.toml", "value = 1\n");
        using var failing = new FailingPublication(files);
        var layout = new AssetProjectLayout(UPath.Root);
        var index = AssetIndex.Scan(failing, layout.Assets);
        var runner = new BuildRunner(failing, layout, null);
        await Assert.That(runner.Run("release", sources: index).Succeeded).IsTrue();
        byte[] original = files.ReadAllBytes("/game/build/assets.pak");
        files.WriteAllText("/game/assets/a.toml", "value = 2\n");
        index.Refresh(failing, "/assets/a.toml");
        failing.FailedName = "assets.pak";
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath> { "/assets/a.toml" }).Succeeded).IsFalse();
        await Assert.That(files.ReadAllBytes("/game/build/assets.pak").SequenceEqual(original)).IsTrue();
        await Assert.That(files.FileExists("/game/build/manifest.json")).IsFalse();

        failing.FailedName = null;
        await Assert.That(runner.Run("release", sources: index, changedPaths: new HashSet<UPath>()).Succeeded).IsTrue();
        using var pack = new AssetPackFileSystem(files.ReadAllBytes("/game/build/assets.pak"));
        await Assert.That(pack.ReadAllText("/a.toml")).Contains("value = 2");
        await Assert.That(pack.ReadAllText("/manifest.json")).IsEqualTo(files.ReadAllText("/game/build/manifest.json"));
    }

    [Test]
    public async Task changing_and_removing_assets_updates_the_pack_and_unpacked_profile_removes_it()
    {
        using var files = Project();
        ProjectVerifierTests.WriteDocument(files, "/game/assets/models/data.toml", "value = 1\n");
        var runner = new BuildRunner(files, s_layout, null);
        await Assert.That(runner.Run("release").Succeeded).IsTrue();
        files.WriteAllText("/game/assets/models/data.toml", "value = 2\n");
        await Assert.That(runner.Run("release").Succeeded).IsTrue();
        using (var pack = new AssetPackFileSystem(files.ReadAllBytes("/game/build/assets.pak")))
            await Assert.That(pack.ReadAllText("/models/data.toml")).Contains("value = 2");
        files.DeleteFile("/game/assets/models/data.toml");
        files.DeleteFile("/game/assets/models/data.toml.meta");
        await Assert.That(runner.Run("release").Succeeded).IsTrue();
        using (var pack = new AssetPackFileSystem(files.ReadAllBytes("/game/build/assets.pak")))
            await Assert.That(pack.FileExists("/models/data.toml")).IsFalse();
        await Assert.That(runner.Run().Succeeded).IsTrue();
        await Assert.That(files.FileExists("/game/build/assets.pak")).IsFalse();
    }

    [Test]
    [Arguments("assets.pak")]
    [Arguments("manifest.json")]
    public async Task failed_publication_does_not_publish_a_completion_manifest(string failedName)
    {
        using var files = Project();
        ProjectVerifierTests.WriteDocument(files, "/game/assets/models/data.toml", "value = 1\n");
        using var failing = new FailingPublication(files);
        var runner = new BuildRunner(failing, new AssetProjectLayout(UPath.Root), null);
        await Assert.That(runner.Run("release").Succeeded).IsTrue();
        byte[] originalPack = files.ReadAllBytes("/game/build/assets.pak");
        files.WriteAllText("/game/assets/models/data.toml", "value = 3\n");
        failing.FailedName = failedName;
        var result = runner.Run("release");
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(files.FileExists("/game/build/manifest.json")).IsFalse();
        if (failedName == "assets.pak")
            await Assert.That(files.ReadAllBytes("/game/build/assets.pak").SequenceEqual(originalPack)).IsTrue();
        await Assert.That(files.EnumerateFiles("/game/build", "*.partial").Any()).IsFalse();
        failing.FailedName = null;
        await Assert.That(runner.Run("release").Succeeded).IsTrue();
    }

    private static MemoryFileSystem Project()
    {
        var files = ProjectVerifierTests.CreateProject();
        files.WriteAllText("/game/assets/project.toml", "name = \"packed\"\nschema_version = 1\n[build.profiles.release]\npack = true\n");
        return files;
    }

    [Test]
    [Arguments("assets.pak")]
    [Arguments(".build-index.json")]
    [Arguments("manifest.json")]
    [Arguments("leaked.meta")]
    [Arguments("a.partial")]
    [Arguments("a.tmp")]
    public async Task importer_cannot_publish_reserved_metadata_or_overwrite_the_previous_pack(string path)
    {
        using var files = Project();
        await Assert.That(new BuildRunner(files, s_layout, null).Run("release").Succeeded).IsTrue();
        byte[] original = files.ReadAllBytes("/game/build/assets.pak");
        IAssetImporter[] chain = [.. AssetImporters.All, new ReservedOutputImporter(path)];
        ProjectVerifierTests.AddAssetWithSidecar(files, "/game/assets/data.bin", chain);
        var result = new BuildRunner(files, s_layout, null, importers: chain).Run("release");
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(string.Join("\n", result.Errors)).Contains("reserved");
        await Assert.That(files.ReadAllBytes("/game/build/assets.pak").SequenceEqual(original)).IsTrue();
        await Assert.That(files.FileExists("/game/build/manifest.json")).IsFalse();
    }

    private sealed class ReservedOutputImporter(string path) : IAssetImporter
    {
        public string Name => "reserved";
        public bool RecordsIdentity => false;
        public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".bin");
        public bool Import(ImportContext context, List<string> errors)
        {
            context.Output.WriteAllBytes("/" + path, [1]);
            return true;
        }
    }

    [Test]
    public async Task physical_publication_replaces_the_pack_and_publishes_the_matching_manifest()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "paradise-packed-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            using var source = Project();
            ProjectVerifierTests.WriteDocument(source, "/game/assets/settings.toml", "value = 1\n");
            using var disk = new PhysicalFileSystem();
            using var files = new SubFileSystem(disk, disk.ConvertPathFromInternal(temporary), owned: false);
            foreach (var path in source.EnumerateFiles("/", "*", SearchOption.AllDirectories))
            {
                files.CreateDirectory(path.GetDirectory());
                source.CopyFileCross(path, files, path, overwrite: false, copyAttributes: false);
            }

            var runner = new BuildRunner(files, s_layout, null);
            await Assert.That(runner.Run("release").Succeeded).IsTrue();
            byte[] before = files.ReadAllBytes("/game/build/assets.pak");
            await Assert.That(runner.Run("release").Succeeded).IsTrue();
            await Assert.That(files.ReadAllBytes("/game/build/assets.pak").SequenceEqual(before)).IsTrue();
            files.WriteAllText("/game/assets/settings.toml", "value = 2\n");
            await Assert.That(runner.Run("release").Succeeded).IsTrue();
            using var pack = new AssetPackFileSystem(files, "/game/build/assets.pak");
            await Assert.That(pack.ReadAllText("/settings.toml")).Contains("value = 2");
            await Assert.That(pack.ReadAllText("/manifest.json")).IsEqualTo(files.ReadAllText("/game/build/manifest.json"));
            await Assert.That(files.EnumerateFiles("/game/build", "*.partial").Any()).IsFalse();
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private sealed class FailingPublication(IFileSystem files) : SubFileSystem(files, "/game", owned: false)
    {
        public string? FailedName { get; set; }

        protected override void MoveFileImpl(UPath srcPath, UPath destPath)
        {
            if (destPath.GetName() == FailedName) throw new IOException("injected publication failure");
            base.MoveFileImpl(srcPath, destPath);
        }

        protected override void ReplaceFileImpl(UPath srcPath, UPath destPath, UPath destBackupPath, bool ignoreMetadataErrors)
        {
            if (destPath.GetName() == FailedName) throw new IOException("injected publication failure");
            base.ReplaceFileImpl(srcPath, destPath, destBackupPath, ignoreMetadataErrors);
        }
    }
}
