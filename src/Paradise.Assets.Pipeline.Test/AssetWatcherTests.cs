using Paradise.Assets.Documents;
using Paradise.Assets.Project;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Pipeline.Test;

/// <summary>
/// The watcher's own rules, driven without a disk: <see cref="AssetWatcher.Observe"/> and friends
/// are public for exactly this, and the clock is injected, so the debounce is data rather than a
/// sleep.
/// </summary>
public class AssetWatcherTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");
    private static readonly DateTimeOffset s_start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A watcher whose clock the test moves by assigning to the returned box.</summary>
    private static (AssetWatcher Watcher, MemoryFileSystem FileSystem, Clock Clock) Watching()
    {
        var fileSystem = ProjectVerifierTests.CreateProject();
        var clock = new Clock { Now = s_start };
        var maintainer = new SidecarMaintainer(fileSystem, s_layout);
        return (new AssetWatcher(fileSystem, s_layout, maintainer, now: () => clock.Now), fileSystem, clock);
    }

    private sealed class Clock
    {
        public DateTimeOffset Now;
    }

    private static void WriteAsset(MemoryFileSystem fileSystem, UPath path, byte[] bytes)
    {
        fileSystem.CreateDirectory(path.GetDirectory());
        fileSystem.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// The loop guard: the maintainer's own sidecar writes must not come back as work.
    /// </summary>
    /// <remarks>
    /// A mint fires a Created and a hash refresh fires a Changed. If either were queued, draining
    /// it would write again and the watcher would run forever on one edit. Sidecar deletes are
    /// the other case — see <see cref="deleting_a_sidecar_remints_it"/>.
    /// </remarks>
    [Test]
    public async Task a_sidecar_write_is_never_queued()
    {
        var (watcher, _, _) = Watching();
        using var _guard = watcher;

        watcher.Observe("/game/assets/models/crate.glb.meta");
        watcher.ObserveRename("/game/assets/models/a.glb.meta", "/game/assets/models/b.glb.meta");

        await Assert.That(watcher.HasPending).IsFalse();
    }

    /// <summary>
    /// The same rule stated as the behaviour it exists for: draining the maintainer's own write
    /// leaves nothing to drain again.
    /// </summary>
    [Test]
    public async Task the_maintainers_own_write_does_not_come_back_as_work()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);

        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        var minted = watcher.Drain().SidecarActions;

        // The mint happened, and the sidecar it produced is on disk -- so the Created event a
        // real filesystem would now raise is the one the guard has to swallow.
        await Assert.That(minted).IsEqualTo(1);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.meta")).IsTrue();

        watcher.Observe("/game/assets/models/crate.glb.meta");
        clock.Now += AssetWatcher.Debounce;

        await Assert.That(watcher.HasPending).IsFalse();
        await Assert.That(watcher.Drain().Changes).IsEqualTo(0);
    }

    [Test]
    public async Task an_edit_waits_out_its_debounce()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);

        watcher.Observe("/game/assets/models/crate.glb");

        // Still warm: an atomic write arrives as several events, so acting on the first one would
        // act on a half-written file.
        await Assert.That(watcher.Drain().Changes).IsEqualTo(0);
        await Assert.That(watcher.HasPending).IsTrue();

        clock.Now += AssetWatcher.Debounce;

        await Assert.That(watcher.Drain().Changes).IsEqualTo(1);
        await Assert.That(watcher.HasPending).IsFalse();
    }

    [Test]
    public async Task a_glb_with_geometry_gets_its_reference_documents_on_the_spot()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        var b = new Paradise.Assets.Gltf.Test.GlbTestBuilder();
        var position = b.AddFloatAccessor([0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], "VEC3");
        var node = b.AddNode(mesh: b.AddMesh(Paradise.Assets.Gltf.Test.GlbTestBuilder.Primitive(position)), name: "Crate");
        var times = b.AddFloatAccessor([0f, 1f], "SCALAR");
        var values = b.AddFloatAccessor([0f, 0f, 0f, 0f, 2f, 0f], "VEC3");
        b.AddAnimation("Bob", (node, "translation", times, values, null));
        b.SetSceneRoots(node);
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", b.Build());

        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        watcher.Drain();

        // Mesh and clip documents, with sidecars, and the GLB's record names them; materials and
        // the prefab are the author's and stay the verb's to make.
        foreach (var expected in new[] { "crate.mesh", "crate.skeleton", "crate.Bob.anim" })
        {
            await Assert.That(fileSystem.FileExists("/game/assets/models/" + expected)).IsTrue().Because(expected);
            await Assert.That(fileSystem.FileExists("/game/assets/models/" + expected + ".meta")).IsTrue().Because(expected + ".meta");
        }

        var recorded = GlbImportSettings.ReadExtraction(SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta"));
        await Assert.That(recorded.Extracted).IsTrue();
        await Assert.That(recorded.Authored).IsFalse();
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.prefab")).IsFalse();

        // Draining again is quiet: the documents already say what the GLB says.
        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        await Assert.That(watcher.Drain().SidecarActions).IsEqualTo(0);
    }

    /// <summary>
    /// The case every real project is in: the asset already has a sidecar, so an edit needs no
    /// sidecar work at all — and it still has to reach the build. The watch loop rebuilds on
    /// Changes, never on SidecarActions (issue #195).
    /// </summary>
    [Test]
    public async Task editing_an_identified_asset_is_a_change_even_though_it_touches_no_sidecar()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        watcher.Drain();

        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [4, 5, 6]);
        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        var drained = watcher.Drain();

        await Assert.That(drained.SidecarActions).IsEqualTo(0);
        await Assert.That(drained.Changes).IsEqualTo(1);
    }

    /// <summary>A delete is a change too: the manifest must stop listing what is gone.</summary>
    [Test]
    public async Task deleting_an_asset_is_a_change()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        watcher.Drain();

        fileSystem.DeleteFile("/game/assets/models/crate.glb");
        watcher.ObserveDelete("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;

        await Assert.That(watcher.Drain().Changes).IsEqualTo(1);
    }

    /// <summary>
    /// A move seen as delete-then-add keeps its identity, which is what the delete-before-add
    /// ordering inside one drain exists to produce.
    /// </summary>
    [Test]
    public async Task a_move_drained_in_one_pass_keeps_the_assets_identity()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;

        // Minted through the watcher, not by hand: Ensure remembers the content hash in memory,
        // which is what recognises the asset at its new path later.
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        watcher.Drain();
        var original = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta");

        // The move, as a filesystem reports it when it does not recognise one: the asset is gone
        // from the old path and present at the new one. The old sidecar stays behind — that is
        // what a plain `mv` of the asset leaves, and it is where the identity is read from.
        fileSystem.DeleteFile("/game/assets/models/crate.glb");
        WriteAsset(fileSystem, "/game/assets/props/crate.glb", [1, 2, 3]);

        watcher.Observe("/game/assets/props/crate.glb");
        watcher.ObserveDelete("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        watcher.Drain();

        var carried = SidecarMeta.Load(fileSystem, "/game/assets/props/crate.glb.meta");
        await Assert.That(carried.Guid).IsEqualTo(original.Guid);
    }

    /// <summary>
    /// A rename seen as one: the sidecar is carried, and then every reference to the identity has
    /// its path caught up, so a Finder rename leaves the tree as tidy as `mv` would.
    /// </summary>
    [Test]
    public async Task a_rename_catches_every_reference_to_it_up()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1]);
        var crate = SidecarMeta.Mint();
        crate.Importer = "glb";
        crate.Save(fileSystem, "/game/assets/models/crate.glb.meta");
        Level(fileSystem, "/game/assets/levels/district.prefab", new Paradise.Authoring.AssetReference(crate.Guid, "models/crate.glb"));

        fileSystem.MoveFile("/game/assets/models/crate.glb", "/game/assets/models/box.glb");
        watcher.ObserveRename("/game/assets/models/crate.glb", "/game/assets/models/box.glb");
        clock.Now += AssetWatcher.Debounce;
        var drained = watcher.Drain();

        await Assert.That(drained.Rewritten).IsEqualTo(1);
        var document = PrefabDocumentSerializer.Load(fileSystem, "/game/assets/levels/district.prefab");
        var mesh = (CanonicalInlineTable)document.Objects[0].Components[1].Data.Value("Mesh")!;
        await Assert.That(mesh.Value("path")).IsEqualTo("models/box.glb");
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    /// <summary>A document the author is mid-saving is not rewritten under them; the next drain does it.</summary>
    [Test]
    public async Task a_dependent_still_in_its_debounce_is_caught_up_on_the_next_drain()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1]);
        var crate = SidecarMeta.Mint();
        crate.Importer = "glb";
        crate.Save(fileSystem, "/game/assets/models/crate.glb.meta");
        Level(fileSystem, "/game/assets/levels/district.prefab", new Paradise.Authoring.AssetReference(crate.Guid, "models/crate.glb"));

        fileSystem.MoveFile("/game/assets/models/crate.glb", "/game/assets/models/box.glb");
        watcher.ObserveRename("/game/assets/models/crate.glb", "/game/assets/models/box.glb");
        clock.Now += AssetWatcher.Debounce;
        watcher.Observe("/game/assets/levels/district.prefab");   // still being written
        var first = watcher.Drain();

        await Assert.That(first.Rewritten).IsEqualTo(0);
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout).Count).IsEqualTo(1);

        clock.Now += AssetWatcher.Debounce;
        var second = watcher.Drain();

        await Assert.That(second.Rewritten).IsEqualTo(1);
        await Assert.That(ProjectVerifier.Verify(fileSystem, s_layout)).IsEmpty();
    }

    /// <summary>A delete that outlived the quarantine names every reference it left dangling.</summary>
    [Test]
    public async Task an_expired_delete_reports_what_still_references_it()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        watcher.Drain();
        var crate = SidecarMeta.Load(fileSystem, "/game/assets/models/crate.glb.meta").Guid;
        Level(fileSystem, "/game/assets/levels/district.prefab", new Paradise.Authoring.AssetReference(crate, "models/crate.glb"));

        fileSystem.DeleteFile("/game/assets/models/crate.glb");
        watcher.ObserveDelete("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        var quarantined = watcher.Drain();
        clock.Now += AssetWatcher.QuarantineWindow + TimeSpan.FromSeconds(1);
        var expired = watcher.Drain();

        await Assert.That(quarantined.Dangling).IsEmpty();
        await Assert.That(expired.Dangling.Count).IsEqualTo(1);
        await Assert.That(expired.Dangling[0]).Contains("levels/district.prefab");
        await Assert.That(expired.Dangling[0]).Contains("models/crate.glb");
    }

    private static void Level(MemoryFileSystem fileSystem, UPath path, Paradise.Authoring.AssetReference reference)
    {
        var root = PrefabObject.WithMeta(Guid.NewGuid(), "object");
        root.Components.Add(new PrefabComponent(Guid.NewGuid(), "game.Mesh", new CanonicalTomlTable { { "Mesh", AssetReferenceCodec.Write(reference) } }));
        var document = new PrefabDocument();
        document.Objects.Add(root);
        fileSystem.CreateDirectory(path.GetDirectory());
        PrefabDocumentSerializer.Save(fileSystem, path, document);
        ProjectVerifierTests.Mint(fileSystem, path);
    }

    /// <summary>
    /// Wiping a sidecar while the asset stays is an identity spent, not a loop event. Drain
    /// mints a replacement so the next rebuild is not a verify failure.
    /// </summary>
    [Test]
    public async Task deleting_a_sidecar_remints_it()
    {
        var (watcher, fileSystem, clock) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/models/crate.glb", [1, 2, 3]);
        watcher.Observe("/game/assets/models/crate.glb");
        clock.Now += AssetWatcher.Debounce;
        watcher.Drain();
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.meta")).IsTrue();

        fileSystem.DeleteFile("/game/assets/models/crate.glb.meta");
        watcher.ObserveDelete("/game/assets/models/crate.glb.meta");
        clock.Now += AssetWatcher.Debounce;

        await Assert.That(watcher.Drain().SidecarActions).IsEqualTo(1);
        await Assert.That(fileSystem.FileExists("/game/assets/models/crate.glb.meta")).IsTrue();
    }

    /// <summary>
    /// Rebuild-now does not wait out the sidecar-delete debounce, so it must mint first or
    /// verify refuses the tree the author just asked to rebuild.
    /// </summary>
    [Test]
    public async Task a_rebuild_mints_missing_sidecars_before_verifying()
    {
        var (watcher, fileSystem, _) = Watching();
        using var _guard = watcher;
        WriteAsset(fileSystem, "/game/assets/audio/init.bnk", [1, 2, 3]);

        var result = watcher.Rebuild(null, ProjectOutputTarget.Build, encoder: null);

        await Assert.That(fileSystem.FileExists("/game/assets/audio/init.bnk.meta")).IsTrue();
        await Assert.That(result.Succeeded).IsTrue();
    }
}
