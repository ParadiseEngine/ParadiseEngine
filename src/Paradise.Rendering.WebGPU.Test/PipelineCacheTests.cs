using System;
using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

/// <summary>Checks the ownership cache used beneath native resource handles without a GPU.</summary>
public class PipelineCacheTests
{
    [Test]
    public async Task leases_share_content_and_last_release_evicts_once()
    {
        var releases = 0;
        var cache = new RefCountedCache<string, object>(_ => releases++);
        var first = cache.Acquire("same", static () => new object());
        var second = cache.Acquire("same", static () => throw new InvalidOperationException("Rebuilt live resource."));
        var retained = first.Retain();
        await Assert.That(ReferenceEquals(first.Value, second.Value)).IsTrue();
        first.Dispose();
        first.Dispose();
        second.Dispose();
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(releases).IsEqualTo(0);
        retained.Dispose();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(releases).IsEqualTo(1);
        await Assert.That(() => first.Retain()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task failed_creation_does_not_leave_a_cache_entry()
    {
        var cache = new RefCountedCache<string, object>();
        await Assert.That(() => cache.Acquire("failed", static () => throw new InvalidOperationException()))
            .Throws<InvalidOperationException>();
        await Assert.That(cache.Count).IsEqualTo(0);
        using var retry = cache.Acquire("failed", static () => new object());
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    public async Task clearing_with_live_leases_releases_once_and_cannot_remove_a_new_entry()
    {
        var releases = 0;
        var cache = new RefCountedCache<string, object>(_ => releases++);
        var old = cache.Acquire("same", static () => new object());
        cache.Clear();
        await Assert.That(releases).IsEqualTo(1);
        using var current = cache.Acquire("same", static () => new object());
        old.Dispose();
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(releases).IsEqualTo(1);
        current.Dispose();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(releases).IsEqualTo(2);
    }
}
