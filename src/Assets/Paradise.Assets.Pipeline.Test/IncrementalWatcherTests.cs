using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

public class IncrementalWatcherTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task a_backup_rename_during_save_does_not_carry_away_the_live_identity()
    {
        using var files = ProjectVerifierTests.CreateProject(ignore: ["*.bak"]);
        files.WriteAllBytes("/game/assets/models/one.glb", Triangle(1));
        var now = DateTimeOffset.UtcNow;
        using var watcher = new AssetWatcher(files, s_layout, new SidecarMaintainer(files, s_layout), now: () => now);
        watcher.MintReferences();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Errors).IsEmpty();
        var identity = SidecarMeta.Load(files, "/game/assets/models/one.glb.meta").Guid;
        var previous = files.ReadAllBytes("/game/build/models/one.mesh");
        files.MoveFile("/game/assets/models/one.glb", "/game/assets/models/one.glb.bak");
        files.WriteAllBytes("/game/assets/models/one.glb", Triangle(2));
        watcher.ObserveRename("/game/assets/models/one.glb", "/game/assets/models/one.glb.bak");
        watcher.Observe("/game/assets/models/one.glb");
        now += AssetWatcher.Debounce;
        watcher.Drain();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Errors).IsEmpty();
        await Assert.That(SidecarMeta.Load(files, "/game/assets/models/one.glb.meta").Guid).IsEqualTo(identity);
        await Assert.That(files.FileExists("/game/assets/models/one.glb.bak.meta")).IsFalse();
        await Assert.That(files.ReadAllBytes("/game/build/models/one.mesh").SequenceEqual(previous)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_case_only_rename_preserves_identity_and_uses_only_the_new_output_spelling(bool announced)
    {
        using var storage = ProjectVerifierTests.CreateProject();
        using var files = new CaseFoldingFileSystem(storage);
        files.CreateDirectory("/game/assets/audio");
        files.WriteAllBytes("/game/assets/audio/one.bnk", [1]);
        var now = DateTimeOffset.UtcNow;
        using var watcher = new AssetWatcher(files, s_layout, new SidecarMaintainer(files, s_layout), now: () => now);
        watcher.MintReferences();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Errors).IsEmpty();
        var identity = SidecarMeta.Load(files, "/game/assets/audio/one.bnk.meta").Guid;
        files.MoveFile("/game/assets/audio/one.bnk", "/game/assets/audio/temporary");
        files.MoveFile("/game/assets/audio/temporary", "/game/assets/audio/One.bnk");
        if (announced) watcher.ObserveRename("/game/assets/audio/one.bnk", "/game/assets/audio/One.bnk");
        else
        {
            watcher.Observe("/game/assets/audio/one.bnk");
            watcher.Observe("/game/assets/audio/One.bnk");
        }
        now += AssetWatcher.Debounce;
        watcher.Drain();
        var result = watcher.Rebuild(null, ProjectOutputTarget.Build, null);
        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.AssetCount).IsEqualTo(1);
        await Assert.That(SidecarMeta.Load(files, "/game/assets/audio/One.bnk.meta").Guid).IsEqualTo(identity);
        await Assert.That(files.ReadAllText("/game/build/manifest.json")).Contains("\"path\": \"audio/One.bnk\"");
        await Assert.That(files.EnumerateFiles("/game/build/audio").Select(path => path.GetName())).IsEquivalentTo(new[] { "One.bnk" });

        files.WriteAllBytes("/game/assets/audio/One.bnk", [2, 3]);
        watcher.Observe("/game/assets/audio/one.bnk");
        now += AssetWatcher.Debounce;
        watcher.Drain();
        var edited = watcher.Rebuild(null, ProjectOutputTarget.Build, null);
        await Assert.That(edited.Errors).IsEmpty();
        await Assert.That(edited.AssetCount).IsEqualTo(1);
        await Assert.That(files.ReadAllBytes("/game/build/audio/One.bnk").SequenceEqual(new byte[] { 2, 3 })).IsTrue();
        await Assert.That(SidecarMeta.Load(files, "/game/assets/audio/One.bnk.meta").Guid).IsEqualTo(identity);
        await Assert.That(files.EnumerateFiles("/game/build/audio").Select(path => path.GetName())).IsEquivalentTo(new[] { "One.bnk" });
    }

    [Test]
    public async Task case_distinct_assets_on_a_case_sensitive_filesystem_keep_separate_identities()
    {
        using var files = new ZipArchiveFileSystem(new MemoryStream(), System.IO.Compression.ZipArchiveMode.Update, isCaseSensitive: true);
        files.CreateDirectory("/game/assets/audio");
        files.WriteAllText(s_layout.Manifest, "name = \"case-sensitive\"\nschema_version = 1\n");
        files.WriteAllBytes("/game/assets/audio/one.bnk", [1]);
        var now = DateTimeOffset.UtcNow;
        using var watcher = new AssetWatcher(files, s_layout, new SidecarMaintainer(files, s_layout), now: () => now);
        watcher.MintReferences();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Errors).IsEmpty();
        var identity = SidecarMeta.Load(files, "/game/assets/audio/one.bnk.meta").Guid;

        files.WriteAllBytes("/game/assets/audio/One.bnk", [2]);
        watcher.Observe("/game/assets/audio/One.bnk");
        now += AssetWatcher.Debounce;
        watcher.Drain();
        var result = watcher.Rebuild(null, ProjectOutputTarget.Build, null);
        await Assert.That(result.Errors).IsEmpty();
        await Assert.That(result.AssetCount).IsEqualTo(2);
        await Assert.That(SidecarMeta.Load(files, "/game/assets/audio/one.bnk.meta").Guid).IsEqualTo(identity);
        await Assert.That(SidecarMeta.Load(files, "/game/assets/audio/One.bnk.meta").Guid).IsNotEqualTo(identity);
        await Assert.That(files.ReadAllBytes("/game/build/audio/one.bnk").SequenceEqual(new byte[] { 1 })).IsTrue();
        await Assert.That(files.ReadAllBytes("/game/build/audio/One.bnk").SequenceEqual(new byte[] { 2 })).IsTrue();
    }

    [Test]
    public async Task a_warm_model_edit_keeps_clean_outputs_without_reading_their_sources_or_sidecars()
    {
        using var storage = ProjectVerifierTests.CreateProject();
        using var files = new CountingFileSystem(storage);
        files.CreateDirectory("/game/assets/models");
        for (var i = 0; i < 8; i++) files.WriteAllBytes($"/game/assets/models/model{i}.glb", Triangle(1));
        var now = DateTimeOffset.UtcNow;
        using var watcher = new AssetWatcher(files, s_layout, new SidecarMaintainer(files, s_layout), now: () => now);
        watcher.MintReferences();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Succeeded).IsTrue();
        var before = Enumerable.Range(0, 8).Select(i => storage.ReadAllBytes($"/game/build/models/model{i}.mesh")).ToArray();

        files.WriteAllBytes("/game/assets/models/model0.glb", Triangle(2));
        watcher.Observe("/game/assets/models/model0.glb");
        now += AssetWatcher.Debounce;
        files.Reset();
        watcher.Drain();
        var built = watcher.Rebuild(null, ProjectOutputTarget.Build, null);

        await Assert.That(built.Errors).IsEmpty();
        await Assert.That(built.AssetCount).IsEqualTo(8);
        await Assert.That(files.Enumerations).IsEqualTo(0);
        for (var i = 1; i < 8; i++)
        {
            var name = $"model{i}";
            await Assert.That(files.Reads.Any(path => path.GetName().StartsWith(name, StringComparison.Ordinal))).IsFalse();
            await Assert.That(storage.ReadAllBytes($"/game/build/models/model{i}.mesh").SequenceEqual(before[i])).IsTrue();
        }
        await Assert.That(storage.ReadAllBytes("/game/build/models/model0.mesh").SequenceEqual(before[0])).IsFalse();
    }

    [Test]
    public async Task external_sidecar_identity_changes_reach_the_manifest_but_owned_echoes_do_not_rebuild()
    {
        using var files = ProjectVerifierTests.CreateProject();
        files.CreateDirectory("/game/assets/audio");
        files.WriteAllBytes("/game/assets/audio/one.bnk", [1]);
        var now = DateTimeOffset.UtcNow;
        using var watcher = new AssetWatcher(files, s_layout, new SidecarMaintainer(files, s_layout), now: () => now);
        watcher.MintReferences();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Succeeded).IsTrue();
        watcher.Observe("/game/assets/audio/one.bnk.meta");
        now += AssetWatcher.Debounce;
        await Assert.That(watcher.Drain().Changes).IsEqualTo(0);

        var sidecar = SidecarMeta.Load(files, "/game/assets/audio/one.bnk.meta");
        var replacement = new SidecarMeta(Guid.NewGuid()) { Importer = sidecar.Importer };
        replacement.Save(files, "/game/assets/audio/one.bnk.meta");
        watcher.Observe("/game/assets/audio/one.bnk.meta");
        now += AssetWatcher.Debounce;
        watcher.Drain();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Succeeded).IsTrue();
        var manifest = files.ReadAllText("/game/build/manifest.json");
        await Assert.That(manifest).Contains(replacement.Guid.ToString());
        await Assert.That(manifest.Contains(sidecar.Guid.ToString(), StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task renames_and_deletes_update_output_ownership_without_dropping_clean_assets()
    {
        using var files = ProjectVerifierTests.CreateProject();
        files.CreateDirectory("/game/assets/audio");
        files.WriteAllBytes("/game/assets/audio/one.bnk", [1]);
        files.WriteAllBytes("/game/assets/audio/two.bnk", [2]);
        var now = DateTimeOffset.UtcNow;
        using var watcher = new AssetWatcher(files, s_layout, new SidecarMaintainer(files, s_layout), now: () => now);
        watcher.MintReferences();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Succeeded).IsTrue();
        var identity = SidecarMeta.Load(files, "/game/assets/audio/one.bnk.meta").Guid;
        files.MoveFile("/game/assets/audio/one.bnk", "/game/assets/audio/moved.bnk");
        watcher.ObserveRename("/game/assets/audio/one.bnk", "/game/assets/audio/moved.bnk");
        now += AssetWatcher.Debounce;
        watcher.Drain();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Errors).IsEmpty();
        await Assert.That(files.FileExists("/game/build/audio/one.bnk")).IsFalse();
        await Assert.That(files.ReadAllBytes("/game/build/audio/moved.bnk").SequenceEqual(new byte[] { 1 })).IsTrue();
        await Assert.That(SidecarMeta.Load(files, "/game/assets/audio/moved.bnk.meta").Guid).IsEqualTo(identity);

        files.DeleteFile("/game/assets/audio/moved.bnk");
        files.DeleteFile("/game/assets/audio/moved.bnk.meta");
        watcher.ObserveDelete("/game/assets/audio/moved.bnk");
        watcher.ObserveDelete("/game/assets/audio/moved.bnk.meta");
        now += AssetWatcher.Debounce;
        watcher.Drain();
        var built = watcher.Rebuild(null, ProjectOutputTarget.Build, null);
        await Assert.That(built.Errors).IsEmpty();
        await Assert.That(built.AssetCount).IsEqualTo(1);
        await Assert.That(files.FileExists("/game/build/audio/moved.bnk")).IsFalse();
        await Assert.That(files.ReadAllBytes("/game/build/audio/two.bnk").SequenceEqual(new byte[] { 2 })).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task recovery_discovers_unreported_files_and_reapplies_ignore_rules(bool explicitRebuild)
    {
        using var files = ProjectVerifierTests.CreateProject();
        files.CreateDirectory("/game/assets/audio");
        files.WriteAllBytes("/game/assets/audio/one.bnk", [1]);
        using var watcher = new AssetWatcher(files, s_layout, new SidecarMaintainer(files, s_layout));
        watcher.MintReferences();
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Succeeded).IsTrue();
        files.WriteAllBytes("/game/assets/audio/unreported.bnk", [2]);
        if (explicitRebuild) await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null, full: true).Succeeded).IsTrue();
        else
        {
            watcher.Invalidate();
            watcher.Drain();
            await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Succeeded).IsTrue();
        }
        await Assert.That(files.ReadAllBytes("/game/build/audio/unreported.bnk").SequenceEqual(new byte[] { 2 })).IsTrue();
        files.WriteAllText(s_layout.Manifest, "name = \"probe\"\nschema_version = 1\n[assets]\nignore = [\"audio/one.bnk\"]\n");
        await Assert.That(watcher.Rebuild(null, ProjectOutputTarget.Build, null).Succeeded).IsTrue();
        await Assert.That(files.FileExists("/game/build/audio/one.bnk")).IsFalse();
        await Assert.That(files.FileExists("/game/assets/audio/one.bnk.meta")).IsFalse();
        await Assert.That(files.FileExists("/game/build/audio/unreported.bnk")).IsTrue();
    }

    private static byte[] Triangle(float scale)
    {
        var builder = new Paradise.Assets.Gltf.Test.GlbTestBuilder();
        var positions = builder.AddFloatAccessor([0f, 0f, 0f, scale, 0f, 0f, 0f, scale, 0f], "VEC3");
        var node = builder.AddNode(mesh: builder.AddMesh(Paradise.Assets.Gltf.Test.GlbTestBuilder.Primitive(positions)), name: "Triangle");
        builder.SetSceneRoots(node);
        return builder.Build();
    }

    private sealed class CountingFileSystem(IFileSystem fallback) : ComposeFileSystem(fallback, owned: false)
    {
        public HashSet<UPath> Reads { get; } = [];
        public int Enumerations { get; private set; }
        public void Reset()
        {
            Reads.Clear();
            Enumerations = 0;
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
            Enumerations++;
            return base.EnumeratePathsImpl(path, searchPattern, searchOption, searchTarget);
        }
    }
}
