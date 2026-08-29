using System.Text;

namespace Paradise.Assets.Project.Test;

public class ArtifactCacheTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    [Test]
    public async Task a_stored_artifact_comes_back_byte_for_byte()
    {
        using var fileSystem = new MemoryFileSystem();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache);
        var key = ArtifactDigest.Compute("source bytes", "ktx create --encode uastc");

        fileSystem.CreateDirectory("/work");
        fileSystem.WriteAllBytes("/work/crate.ktx2", [1, 2, 3, 4]);
        cache.Store("ktx2", key, fileSystem, "/work/crate.ktx2");
        fileSystem.DeleteFile("/work/crate.ktx2");

        await Assert.That(cache.TryFetch("ktx2", key, fileSystem, "/work/crate.ktx2")).IsTrue();
        await Assert.That(fileSystem.ReadAllBytes("/work/crate.ktx2")).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
    }

    [Test]
    public async Task entries_live_at_kind_slash_digest_plus_extension()
    {
        // This layout is a cross-tool contract: the Blender addon writes and reads the very same
        // paths, so a change here silently splits one cache into two.
        using var fileSystem = new MemoryFileSystem();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache);
        var key = ArtifactDigest.Compute("payload");

        fileSystem.CreateDirectory("/work");
        fileSystem.WriteAllBytes("/work/mesh.ktx2", [7]);
        cache.Store("ktx2", key, fileSystem, "/work/mesh.ktx2");

        await Assert.That(fileSystem.FileExists($"/game/.editor/cache/ktx2/{key}.ktx2")).IsTrue();
    }

    [Test]
    public async Task the_extension_is_part_of_the_entry_name()
    {
        // Documented consequence: a kind must use ONE extension. Storing .ktx2 and fetching with
        // a .png destination misses forever rather than failing, which is why the doc comment
        // says so and this test proves it is really the behaviour.
        using var fileSystem = new MemoryFileSystem();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache);
        var key = ArtifactDigest.Compute("payload");

        fileSystem.CreateDirectory("/work");
        fileSystem.WriteAllBytes("/work/mesh.ktx2", [7]);
        cache.Store("ktx2", key, fileSystem, "/work/mesh.ktx2");

        await Assert.That(cache.TryFetch("ktx2", key, fileSystem, "/work/other.png")).IsFalse();
        await Assert.That(cache.TryFetch("ktx2", key, fileSystem, "/work/other.ktx2")).IsTrue();
    }

    [Test]
    public async Task an_extensionless_destination_yields_an_extensionless_entry()
    {
        using var fileSystem = new MemoryFileSystem();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache);
        var key = ArtifactDigest.Compute("payload");

        fileSystem.CreateDirectory("/work");
        fileSystem.WriteAllBytes("/work/navmesh", [7]);
        cache.Store("navmesh", key, fileSystem, "/work/navmesh");

        await Assert.That(fileSystem.FileExists($"/game/.editor/cache/navmesh/{key}")).IsTrue();
    }

    [Test]
    public async Task a_missing_entry_is_a_miss_and_leaves_the_destination_alone()
    {
        using var fileSystem = new MemoryFileSystem();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache);

        await Assert.That(cache.TryFetch("ktx2", ArtifactDigest.Compute("nothing"), fileSystem, "/work/x.ktx2")).IsFalse();
        await Assert.That(fileSystem.FileExists("/work/x.ktx2")).IsFalse();
    }

    [Test]
    public async Task fetch_creates_the_destination_directory()
    {
        using var fileSystem = new MemoryFileSystem();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache);
        var key = ArtifactDigest.Compute("payload");

        fileSystem.CreateDirectory("/work");
        fileSystem.WriteAllBytes("/work/a.ktx2", [9]);
        cache.Store("ktx2", key, fileSystem, "/work/a.ktx2");

        await Assert.That(cache.TryFetch("ktx2", key, fileSystem, "/build/models/deep/a.ktx2")).IsTrue();
        await Assert.That(fileSystem.ReadAllBytes("/build/models/deep/a.ktx2")).IsEquivalentTo(new byte[] { 9 });
    }

    [Test]
    public async Task the_cache_works_across_filesystems()
    {
        // The real pipeline stores from one mount and fetches into another; nothing about the
        // cache assumes source, destination and cache share a filesystem.
        using var cacheFileSystem = new MemoryFileSystem();
        using var workFileSystem = new MemoryFileSystem();
        var cache = new ArtifactCache(cacheFileSystem, "/cache");
        var key = ArtifactDigest.Compute("payload");

        workFileSystem.CreateDirectory("/out");
        workFileSystem.WriteAllBytes("/out/a.ktx2", [5, 6]);
        cache.Store("ktx2", key, workFileSystem, "/out/a.ktx2");
        workFileSystem.DeleteFile("/out/a.ktx2");

        await Assert.That(cache.TryFetch("ktx2", key, workFileSystem, "/out/a.ktx2")).IsTrue();
        await Assert.That(workFileSystem.ReadAllBytes("/out/a.ktx2")).IsEquivalentTo(new byte[] { 5, 6 });
    }

    [Test]
    public async Task storing_a_missing_source_is_a_no_op()
    {
        using var fileSystem = new MemoryFileSystem();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache);

        cache.Store("ktx2", ArtifactDigest.Compute("x"), fileSystem, "/work/absent.ktx2");

        await Assert.That(EntryNames(fileSystem, "/game/.editor/cache/ktx2").Count).IsEqualTo(0);
    }

    [Test]
    public async Task a_store_leaves_no_partial_file_behind()
    {
        // Entries are complete or absent: the copy lands on a temporary sibling and is renamed
        // into place, so a kill mid-write cannot leave a truncated entry that fetch would trust.
        using var fileSystem = new MemoryFileSystem();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache);
        var key = ArtifactDigest.Compute("payload");

        fileSystem.CreateDirectory("/work");
        fileSystem.WriteAllBytes("/work/a.ktx2", [1]);
        cache.Store("ktx2", key, fileSystem, "/work/a.ktx2");
        cache.Store("ktx2", key, fileSystem, "/work/a.ktx2");

        var entries = EntryNames(fileSystem, "/game/.editor/cache/ktx2");
        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0]).IsEqualTo($"{key}.ktx2");
    }

    [Test]
    public async Task the_cache_root_ignores_itself()
    {
        // A "*" that ignores the cache AND the .gitignore, so no consuming repo needs a rule for
        // this directory. A build cache that dirties the working tree gets committed by accident
        // exactly once.
        using var fileSystem = new MemoryFileSystem();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache);

        cache.TryFetch("ktx2", ArtifactDigest.Compute("x"), fileSystem, "/work/x.ktx2");

        var marker = s_layout.EditorCache / ".gitignore";
        await Assert.That(fileSystem.FileExists(marker)).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(fileSystem.ReadAllBytes(marker))).IsEqualTo("*\n");
    }

    [Test]
    public async Task an_existing_gitignore_is_left_as_the_author_wrote_it()
    {
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory(s_layout.EditorCache);
        fileSystem.WriteAllText(s_layout.EditorCache / ".gitignore", "# mine\n*\n");
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache);

        cache.TryFetch("ktx2", ArtifactDigest.Compute("x"), fileSystem, "/work/x.ktx2");

        await Assert.That(fileSystem.ReadAllText(s_layout.EditorCache / ".gitignore")).IsEqualTo("# mine\n*\n");
    }

    [Test]
    public async Task a_disabled_cache_no_ops_every_method()
    {
        // Callers need no "if (cache is enabled)" branches, which is the whole reason the
        // disabled state is a cache rather than a null.
        using var fileSystem = new MemoryFileSystem();
        var cache = ArtifactCache.Disabled;

        fileSystem.CreateDirectory("/work");
        fileSystem.WriteAllBytes("/work/a.ktx2", [1]);
        cache.Store("ktx2", ArtifactDigest.Compute("x"), fileSystem, "/work/a.ktx2");

        await Assert.That(cache.IsEnabled).IsFalse();
        await Assert.That(cache.TryFetch("ktx2", ArtifactDigest.Compute("x"), fileSystem, "/work/b.ktx2")).IsFalse();
    }

    [Test]
    [Arguments("0")]
    [Arguments("off")]
    [Arguments("false")]
    [Arguments("no")]
    [Arguments("none")]
    [Arguments("OFF")]
    [Arguments("  None  ")]
    public async Task a_falsey_environment_value_turns_the_cache_off(string configured)
    {
        using var fileSystem = new MemoryFileSystem();

        var cache = ArtifactCache.ForProject(fileSystem, s_layout, configured, warn: null);

        await Assert.That(cache.IsEnabled).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task an_unset_environment_value_puts_the_cache_beside_the_project(string? configured)
    {
        using var fileSystem = new MemoryFileSystem();

        var cache = ArtifactCache.ForProject(fileSystem, s_layout, configured, warn: null);

        await Assert.That(cache.IsEnabled).IsTrue();
        await Assert.That(cache.Root).IsEqualTo(s_layout.EditorCache);
    }

    [Test]
    public async Task an_environment_path_relocates_the_cache()
    {
        using var fileSystem = new MemoryFileSystem();

        var cache = ArtifactCache.ForProject(fileSystem, s_layout, "/elsewhere/shared-cache", warn: null);

        await Assert.That(cache.Root).IsEqualTo(new UPath("/elsewhere/shared-cache"));
    }

    [Test]
    public async Task an_unusable_root_disables_the_cache_and_says_so()
    {
        // Non-fatal by design: a cache that cannot be created costs time, not correctness. But it
        // must SAY so, or the tool merely looks mysteriously slow.
        using var fileSystem = new MemoryFileSystem();
        fileSystem.CreateDirectory("/game/.editor");
        fileSystem.WriteAllText("/game/.editor/cache", "not a directory");

        var warnings = new List<string>();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache, warnings.Add);
        cache.Store("ktx2", ArtifactDigest.Compute("x"), fileSystem, "/game/.editor/cache");

        await Assert.That(cache.IsEnabled).IsFalse();
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0]).Contains("caching is off");
    }

    [Test]
    public async Task a_copy_failure_is_reported_and_treated_as_a_miss()
    {
        // The one outcome forbidden here is a destination that exists holding the wrong bytes.
        // A miss makes the caller redo the work: slow, but right.
        using var fileSystem = new MemoryFileSystem();
        var warnings = new List<string>();
        var cache = new ArtifactCache(fileSystem, s_layout.EditorCache, warnings.Add);
        var key = ArtifactDigest.Compute("payload");

        fileSystem.CreateDirectory("/work");
        fileSystem.WriteAllBytes("/work/a.ktx2", [3]);
        cache.Store("ktx2", key, fileSystem, "/work/a.ktx2");
        await Assert.That(warnings.Count).IsEqualTo(0);

        // A ReadOnlyFileSystem cannot inject this failure: CopyFileCross resolves both ends
        // through ComposeFileSystem.ResolvePath, which unwraps the wrapper and copies against
        // the filesystem underneath — the guard never runs. Hence a filesystem whose write
        // opens fail intrinsically.
        using var destination = new WriteFailingFileSystem();
        destination.CreateDirectory("/out");

        await Assert.That(cache.TryFetch("ktx2", key, destination, "/out/a.ktx2")).IsFalse();
        await Assert.That(destination.FileExists("/out/a.ktx2")).IsFalse();
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0]).Contains("regenerating");
    }

    private sealed class WriteFailingFileSystem : MemoryFileSystem
    {
        protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
        {
            if ((access & FileAccess.Write) != 0) throw new IOException("injected write failure");
            return base.OpenFileImpl(path, mode, access, share);
        }
    }

    private static List<string> EntryNames(IFileSystem fileSystem, UPath directory)
    {
        if (!fileSystem.DirectoryExists(directory)) return [];
        return fileSystem.EnumerateFiles(directory).Select(path => path.GetName()).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }
}
