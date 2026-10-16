using Paradise.Assets.Project;

namespace Paradise.Assets.Pipeline.Test;

public class IncrementalBuildTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private sealed class DependencyImporter : IAssetImporter
    {
        public string Name => "dependency";
        public bool RecordsIdentity => true;
        public string? Collision { get; set; }
        public bool Claims(ImportCandidate candidate) => candidate.HasExtension(".bnk");

        public bool Import(ImportContext context, List<string> errors)
        {
            var input = context.FileSystem.ReadAllText(context.Asset);
            var dependency = input.StartsWith('/') ? new UPath(input) : context.Asset.GetDirectory() / input;
            var value = context.FileSystem.FileExists(dependency) ? context.FileSystem.ReadAllText(dependency) : "absent";
            context.Output.WriteAllText(Collision is null ? new UPath("/" + context.Source) : new UPath(Collision), value);
            if (input == "fail") errors.Add("injected failure after writing output");
            return true;
        }
    }

    private sealed class ObservingFileSystem(IFileSystem inner) : ComposeFileSystem(inner, owned: false)
    {
        public HashSet<UPath> Reads { get; } = [];
        public int RecursiveWalks { get; private set; }
        public void Reset()
        {
            Reads.Clear();
            RecursiveWalks = 0;
        }

        protected override UPath ConvertPathToDelegate(UPath path) => path;
        protected override UPath ConvertPathFromDelegate(UPath path) => path;
        protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
        {
            if ((access & FileAccess.Read) != 0) Reads.Add(path);
            return base.OpenFileImpl(path, mode, access, share);
        }

        protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
        {
            if (searchOption == SearchOption.AllDirectories) RecursiveWalks++;
            return base.EnumeratePathsImpl(path, searchPattern, searchOption, searchTarget);
        }
    }

    private static void Add(MemoryFileSystem fs, UPath path, string content, IReadOnlyList<IAssetImporter>? chain = null)
    {
        ProjectVerifierTests.AddAssetWithSidecar(fs, path, chain);
        fs.WriteAllText(path, content);
    }

    private static BuildResult Change(BuildRunner runner, IFileSystem fs, AssetIndex index, params UPath[] paths)
    {
        foreach (var path in paths) index.Refresh(fs, path);
        return runner.Run(sources: index, changedPaths: paths.ToHashSet());
    }

    [Test]
    public async Task warm_edit_preserves_clean_manifest_outputs_without_reading_clean_sources_or_walking_trees()
    {
        using var fs = ProjectVerifierTests.CreateProject();
        var importer = new DependencyImporter();
        IReadOnlyList<IAssetImporter> chain = [.. AssetImporters.All, importer];
        Add(fs, "/game/assets/audio/a.bnk", "a.txt", chain);
        Add(fs, "/game/assets/audio/b.bnk", "b.txt", chain);
        Add(fs, "/game/assets/audio/a.txt", "first");
        Add(fs, "/game/assets/audio/b.txt", "clean");
        using var observed = new ObservingFileSystem(fs);
        var index = AssetIndex.Scan(observed, s_layout.Assets);
        var runner = new BuildRunner(observed, s_layout, null, importers: chain);
        await Assert.That(runner.Run(sources: index).Succeeded).IsTrue();
        var cleanStamp = fs.GetLastWriteTime("/game/build/audio/b.bnk");
        fs.WriteAllText("/game/assets/audio/a.txt", "changed-value");
        index.Refresh(observed, "/game/assets/audio/a.txt");
        observed.Reset();

        await Assert.That(runner.Run(sources: index, changedPaths: new HashSet<UPath> { "/game/assets/audio/a.txt" }).Succeeded).IsTrue();

        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("changed-value");
        await Assert.That(fs.ReadAllText("/game/build/audio/b.bnk")).IsEqualTo("clean");
        await Assert.That(fs.GetLastWriteTime("/game/build/audio/b.bnk")).IsEqualTo(cleanStamp);
        await Assert.That(fs.ReadAllText("/game/build/manifest.json")).Contains("audio/b.bnk");
        await Assert.That(observed.Reads.Contains("/game/assets/audio/b.bnk")).IsFalse();
        await Assert.That(observed.Reads.Contains("/game/assets/audio/b.bnk.meta")).IsFalse();
        await Assert.That(observed.Reads.Contains("/game/assets/audio/b.txt")).IsFalse();
        await Assert.That(observed.RecursiveWalks).IsEqualTo(0);
    }

    [Test]
    public async Task presence_creation_deletion_and_retargeting_replace_dependency_ownership()
    {
        using var fs = ProjectVerifierTests.CreateProject();
        IReadOnlyList<IAssetImporter> chain = [.. AssetImporters.All, new DependencyImporter()];
        Add(fs, "/game/assets/audio/a.bnk", "first.txt", chain);
        var index = AssetIndex.Scan(fs, s_layout.Assets);
        using var observed = new ObservingFileSystem(fs);
        var runner = new BuildRunner(observed, s_layout, null, importers: chain);
        await Assert.That(runner.Run(sources: index).Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("absent");
        Add(fs, "/game/assets/audio/first.txt", "first");
        await Assert.That(Change(runner, fs, index, "/game/assets/audio/first.txt").Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("first");
        Add(fs, "/game/assets/audio/second.txt", "second");
        fs.WriteAllText("/game/assets/audio/a.bnk", "second.txt");
        await Assert.That(Change(runner, fs, index, "/game/assets/audio/second.txt", "/game/assets/audio/a.bnk").Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("second");
        fs.WriteAllText("/game/assets/audio/first.txt", "retired");
        observed.Reset();
        await Assert.That(Change(runner, fs, index, "/game/assets/audio/first.txt").Succeeded).IsTrue();
        await Assert.That(observed.Reads.Contains("/game/assets/audio/a.bnk")).IsFalse();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("second");
        fs.DeleteFile("/game/assets/audio/second.txt");
        fs.DeleteFile("/game/assets/audio/second.txt.meta");
        await Assert.That(Change(runner, fs, index, "/game/assets/audio/second.txt").Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("absent");
    }

    [Test]
    public async Task moved_and_deleted_sources_remove_only_their_old_outputs()
    {
        using var fs = ProjectVerifierTests.CreateProject();
        IReadOnlyList<IAssetImporter> chain = [.. AssetImporters.All, new DependencyImporter()];
        Add(fs, "/game/assets/audio/a.bnk", "missing", chain);
        Add(fs, "/game/assets/audio/clean.bnk", "missing", chain);
        var index = AssetIndex.Scan(fs, s_layout.Assets);
        var runner = new BuildRunner(fs, s_layout, null, importers: chain);
        await Assert.That(runner.Run(sources: index).Succeeded).IsTrue();
        fs.MoveFile("/game/assets/audio/a.bnk", "/game/assets/audio/moved.bnk");
        fs.MoveFile("/game/assets/audio/a.bnk.meta", "/game/assets/audio/moved.bnk.meta");
        await Assert.That(Change(runner, fs, index, "/game/assets/audio/a.bnk", "/game/assets/audio/moved.bnk").Succeeded).IsTrue();
        await Assert.That(fs.FileExists("/game/build/audio/a.bnk")).IsFalse();
        await Assert.That(fs.ReadAllText("/game/build/audio/moved.bnk")).IsEqualTo("absent");
        fs.DeleteFile("/game/assets/audio/moved.bnk");
        fs.DeleteFile("/game/assets/audio/moved.bnk.meta");
        await Assert.That(Change(runner, fs, index, "/game/assets/audio/moved.bnk").Succeeded).IsTrue();
        await Assert.That(fs.FileExists("/game/build/audio/moved.bnk")).IsFalse();
        await Assert.That(fs.ReadAllText("/game/build/audio/clean.bnk")).IsEqualTo("absent");
        await Assert.That(fs.ReadAllText("/game/build/manifest.json")).DoesNotContain("moved.bnk");
    }

    [Test]
    public async Task duplicate_guid_errors_survive_subsequent_unrelated_events()
    {
        using var fs = ProjectVerifierTests.CreateProject();
        Add(fs, "/game/assets/audio/a.bnk", "one");
        Add(fs, "/game/assets/audio/b.bnk", "two");
        var index = AssetIndex.Scan(fs, s_layout.Assets);
        var runner = new BuildRunner(fs, s_layout, null);
        await Assert.That(runner.Run(sources: index).Succeeded).IsTrue();
        fs.WriteAllText("/game/assets/audio/b.bnk.meta", fs.ReadAllText("/game/assets/audio/a.bnk.meta"));
        var conflict = Change(runner, fs, index, "/game/assets/audio/b.bnk.meta");
        await Assert.That(conflict.Succeeded).IsFalse();
        await Assert.That(string.Join("\n", conflict.Errors)).Contains("duplicates GUID");
        fs.WriteAllText("/game/assets/audio/a.bnk", "updated");
        await Assert.That(Change(runner, fs, index, "/game/assets/audio/a.bnk").Succeeded).IsFalse();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("one");
    }

    [Test]
    public async Task failed_write_then_reverted_input_cannot_reuse_partial_output()
    {
        using var fs = ProjectVerifierTests.CreateProject();
        IReadOnlyList<IAssetImporter> chain = [.. AssetImporters.All, new DependencyImporter()];
        Add(fs, "/game/assets/audio/a.bnk", "good.txt", chain);
        Add(fs, "/game/assets/audio/good.txt", "original");
        var index = AssetIndex.Scan(fs, s_layout.Assets);
        var runner = new BuildRunner(fs, s_layout, null, importers: chain);
        await Assert.That(runner.Run(sources: index).Succeeded).IsTrue();
        fs.WriteAllText("/game/assets/audio/a.bnk", "fail");
        await Assert.That(Change(runner, fs, index, "/game/assets/audio/a.bnk").Succeeded).IsFalse();
        await Assert.That(fs.FileExists("/game/build/manifest.json")).IsFalse();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("absent");
        fs.WriteAllText("/game/assets/audio/a.bnk", "good.txt");
        await Assert.That(Change(runner, fs, index, "/game/assets/audio/a.bnk").Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("original");
    }

    [Test]
    public async Task output_corruption_external_input_changes_and_explicit_recovery_are_not_trusted()
    {
        using var fs = ProjectVerifierTests.CreateProject();
        fs.WriteAllText("/game/external.txt", "external");
        IReadOnlyList<IAssetImporter> chain = [.. AssetImporters.All, new DependencyImporter()];
        Add(fs, "/game/assets/audio/a.bnk", "/game/external.txt", chain);
        var index = AssetIndex.Scan(fs, s_layout.Assets);
        var runner = new BuildRunner(fs, s_layout, null, importers: chain);
        await Assert.That(runner.Run(sources: index).Succeeded).IsTrue();
        var outputWriteTime = fs.GetLastWriteTime("/game/build/audio/a.bnk");
        fs.WriteAllText("/game/build/audio/a.bnk", "corrupt!");
        fs.SetLastWriteTime("/game/build/audio/a.bnk", outputWriteTime.AddMinutes(1));
        await Assert.That(Change(runner, fs, index).Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("external");
        fs.WriteAllText("/game/external.txt", "new external");
        await Assert.That(Change(runner, fs, index).Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("new external");
        fs.DeleteFile("/game/build/audio/a.bnk");
        await Assert.That(Change(runner, fs, index).Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("new external");
        fs.WriteAllText("/game/build/stray.txt", "stale");
        fs.WriteAllText("/game/assets/audio/a.bnk", "missing");
        await Assert.That(runner.Run().Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/audio/a.bnk")).IsEqualTo("absent");
        await Assert.That(fs.FileExists("/game/build/stray.txt")).IsFalse();
    }

    [Test]
    public async Task configuration_and_profile_changes_replace_the_entire_output_contract()
    {
        using var fs = ProjectVerifierTests.CreateProject("json");
        ProjectVerifierTests.WriteDocument(fs, "/game/assets/config/settings.toml", "value = 1\n");
        var index = AssetIndex.Scan(fs, s_layout.Assets);
        var runner = new BuildRunner(fs, s_layout, null);
        await Assert.That(runner.Run(sources: index).Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/config/settings.toml")).Contains("value = 1");
        await Assert.That(runner.Run("dev", sources: index, changedPaths: new HashSet<UPath>()).Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/config/settings.json")).Contains("\"value\": 1");
        await Assert.That(fs.FileExists("/game/build/config/settings.toml")).IsFalse();
        fs.WriteAllText(s_layout.Manifest, fs.ReadAllText(s_layout.Manifest).Replace("\"json\"", "\"toml\"", StringComparison.Ordinal));
        index.Refresh(fs, s_layout.Manifest);
        await Assert.That(runner.Run("dev", sources: index, changedPaths: new HashSet<UPath> { s_layout.Manifest }).Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/config/settings.toml")).Contains("value = 1");
        await Assert.That(fs.FileExists("/game/build/config/settings.json")).IsFalse();
    }

    [Test]
    public async Task selected_importer_cannot_overwrite_a_clean_output_claim_silently()
    {
        using var fs = ProjectVerifierTests.CreateProject();
        var importer = new DependencyImporter();
        IReadOnlyList<IAssetImporter> chain = [.. AssetImporters.All, importer];
        Add(fs, "/game/assets/audio/a.bnk", "missing", chain);
        Add(fs, "/game/assets/audio/b.bnk", "missing", chain);
        var index = AssetIndex.Scan(fs, s_layout.Assets);
        var runner = new BuildRunner(fs, s_layout, null, importers: chain);
        await Assert.That(runner.Run(sources: index).Succeeded).IsTrue();
        importer.Collision = "/audio/b.bnk";
        fs.WriteAllText("/game/assets/audio/a.bnk", "new missing");
        var result = Change(runner, fs, index, "/game/assets/audio/a.bnk");
        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(string.Join("\n", result.Errors)).Contains("also builds to");
        await Assert.That(fs.FileExists("/game/build/manifest.json")).IsFalse();
    }

    private sealed class VersionedEncoder : ITextureEncoder
    {
        public string Identity { get; set; } = "encoder-one";
        public string CacheKey(byte[] source, string sourceExtension, TexturePreset preset, TextureQuality quality)
            => ArtifactDigest.Compute(source, Identity);

        public bool TryEncode(byte[] source, string sourceExtension, TexturePreset preset, TextureQuality quality, out byte[] ktx2, out string error)
        {
            ktx2 = System.Text.Encoding.UTF8.GetBytes(Identity);
            error = "";
            return true;
        }
    }

    [Test]
    public async Task encoder_and_target_changes_invalidate_a_warm_session_without_asset_events()
    {
        using var fs = ProjectVerifierTests.CreateProject();
        ProjectVerifierTests.AddAssetWithSidecar(fs, "/game/assets/textures/fire.png");
        var index = AssetIndex.Scan(fs, s_layout.Assets);
        var encoder = new VersionedEncoder();
        var runner = new BuildRunner(fs, s_layout, encoder);
        await Assert.That(runner.Run(sources: index).Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/textures/fire.ktx2")).IsEqualTo("encoder-one");
        encoder.Identity = "encoder-two";
        await Assert.That(Change(runner, fs, index).Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/build/textures/fire.ktx2")).IsEqualTo("encoder-two");
        await Assert.That(runner.Run(target: ProjectOutputTarget.Play, sources: index, changedPaths: new HashSet<UPath>()).Succeeded).IsTrue();
        await Assert.That(fs.ReadAllText("/game/.editor/play/textures/fire.ktx2")).IsEqualTo("encoder-two");
        await Assert.That(fs.ReadAllText("/game/build/textures/fire.ktx2")).IsEqualTo("encoder-two");
    }
}
