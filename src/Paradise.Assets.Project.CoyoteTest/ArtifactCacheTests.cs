using Paradise.Diagnostics;

using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;

using Zio;
using Zio.FileSystems;

namespace Paradise.Assets.Project.CoyoteTest;

/// <summary>
/// <see cref="ArtifactCache"/> under systematic exploration.
///
/// One instance serves a whole build, and the class promises callers may share it: the root is
/// prepared once behind a lock, stores land whole through a temp-then-rename, and two stores of
/// one key settle on one entry. Those are claims about interleavings, so they get the systematic
/// test the repo asks for (CLAUDE.md), not a stress loop.
///
/// What each would catch, with the guard removed:
/// <list type="bullet">
/// <item>Prepare without its lock: two first-users both write the <c>.gitignore</c>; on a
/// filesystem that refuses the second open, the IOException is "recoverable", and the cache
/// turns itself OFF for the rest of the build — a slow build with nothing in the log but one
/// warning.</item>
/// <item>Store without its existing-entry check: the loser of the rename race throws, is
/// warned about, and the winner's entry is fine — but a later change that deleted-then-moved
/// would open a window with no entry at all, which the fetch racing it sees as a miss.</item>
/// </list>
/// </summary>
public static class ArtifactCacheTests
{
    private static readonly AssetProjectLayout s_layout = new("/game");

    private static (ArtifactCache Cache, ExclusiveMarkerFileSystem FileSystem, CollectingLogger Warnings) Fresh()
    {
        var fileSystem = new ExclusiveMarkerFileSystem();
        fileSystem.CreateDirectory("/work");
        // CollectingLogger locks on an `object`, so Coyote can schedule around it the way it does
        // the cache's own gate; a List<string> behind a delegate had no lock to schedule at all.
        var warnings = new CollectingLogger();
        return (new ArtifactCache(fileSystem, s_layout.EditorCache, warnings), fileSystem, warnings);
    }

    /// <summary>
    /// A memory filesystem with Windows' sharing rule on the cache's <c>.gitignore</c>: a second
    /// writer while one is open is an <see cref="IOException"/>. <see cref="MemoryFileSystem"/>
    /// alone lets both racing preparers write the same one line and nothing observable goes
    /// wrong; this is the filesystem on which the missing lock costs something.
    /// </summary>
    private sealed class ExclusiveMarkerFileSystem : MemoryFileSystem
    {
        private int _markerWriters;

        public int MarkerWrites;

        protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
        {
            if ((access & FileAccess.Write) == 0 || path.GetName() != ".gitignore") return base.OpenFileImpl(path, mode, access, share);

            if (Interlocked.Increment(ref _markerWriters) > 1)
            {
                Interlocked.Decrement(ref _markerWriters);
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            Interlocked.Increment(ref MarkerWrites);
            return new ReleasingStream(base.OpenFileImpl(path, mode, access, share), () => Interlocked.Decrement(ref _markerWriters));
        }

        private sealed class ReleasingStream(Stream inner, Action onClose) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                    onClose();
                }

                base.Dispose(disposing);
            }
        }
    }

    /// <summary>Two callers hit an unprepared cache at once: it prepares once and stays on.</summary>
    [Test]
    public static async Task ConcurrentFirstUse_PreparesOnceAndStaysEnabled()
    {
        var (cache, fileSystem, warnings) = Fresh();
        fileSystem.WriteAllBytes("/work/a.ktx2", [1]);
        var key = ArtifactDigest.Compute("a");

        await Task.WhenAll(
            Task.Run(() => cache.Store("ktx2", key, fileSystem, "/work/a.ktx2")),
            Task.Run(() => cache.TryFetch("ktx2", ArtifactDigest.Compute("other"), fileSystem, "/work/b.ktx2"))).ConfigureAwait(false);

        Specification.Assert(cache.IsEnabled, "A racing first use turned the cache off.");
        Specification.Assert(warnings.Messages.Count == 0, $"A racing first use warned: {string.Join(" | ", warnings.Messages)}");
        Specification.Assert(fileSystem.MarkerWrites == 1, $"The cache root was prepared {fileSystem.MarkerWrites} times.");
        Specification.Assert(
            fileSystem.ReadAllText(s_layout.EditorCache / ".gitignore") == "*\n",
            "The cache root's .gitignore is not the one line it must be.");
    }

    /// <summary>Two stores of the same key, same bytes: one entry, no partial left behind, either store's bytes.</summary>
    [Test]
    public static async Task TwoStoresOfOneKey_LandOneWholeEntry()
    {
        var (cache, fileSystem, warnings) = Fresh();
        fileSystem.WriteAllBytes("/work/a.ktx2", [1, 2, 3]);
        fileSystem.WriteAllBytes("/work/b.ktx2", [1, 2, 3]);
        var key = ArtifactDigest.Compute("same");

        await Task.WhenAll(
            Task.Run(() => cache.Store("ktx2", key, fileSystem, "/work/a.ktx2")),
            Task.Run(() => cache.Store("ktx2", key, fileSystem, "/work/b.ktx2"))).ConfigureAwait(false);

        var entries = fileSystem.EnumerateFiles(s_layout.EditorCache / "ktx2").Select(p => p.GetName()).ToList();
        Specification.Assert(
            entries.Count == 1 && entries[0] == $"{key}.ktx2",
            $"Expected exactly the entry, found: {string.Join(", ", entries)}");
        Specification.Assert(
            fileSystem.ReadAllBytes(s_layout.EditorCache / "ktx2" / $"{key}.ktx2").AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }),
            "The entry does not hold the stored bytes.");
        Specification.Assert(warnings.Messages.Count == 0, $"A store warned: {string.Join(" | ", warnings.Messages)}");
    }

    /// <summary>A fetch racing the store of its key sees either a miss or the whole entry, never a partial.</summary>
    [Test]
    public static async Task StoreRacingFetch_NeverServesAPartial()
    {
        var (cache, fileSystem, _) = Fresh();
        fileSystem.WriteAllBytes("/work/a.ktx2", [1, 2, 3, 4]);
        var key = ArtifactDigest.Compute("racing");

        var fetched = false;
        await Task.WhenAll(
            Task.Run(() => cache.Store("ktx2", key, fileSystem, "/work/a.ktx2")),
            Task.Run(() => fetched = cache.TryFetch("ktx2", key, fileSystem, "/work/out.ktx2"))).ConfigureAwait(false);

        if (fetched)
        {
            Specification.Assert(
                fileSystem.ReadAllBytes("/work/out.ktx2").AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "A fetch that reported a hit delivered bytes other than the stored ones.");
        }
        else
        {
            Specification.Assert(!fileSystem.FileExists("/work/out.ktx2"), "A miss left a destination behind.");
        }

        Specification.Assert(cache.TryFetch("ktx2", key, fileSystem, "/work/after.ktx2"), "The entry is not there after the store completed.");
    }
}
