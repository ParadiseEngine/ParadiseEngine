using Paradise.Rendering.Internal;

namespace Paradise.Rendering.Test;

public class RefCountedCacheTests
{
    [Test]
    public async Task leases_share_content_and_last_release_evicts_once()
    {
        var releases = 0;
        using var cache = new RefCountedCache<string, object>(_ => releases++);
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
        await Assert.That(() => first.Value).Throws<ObjectDisposedException>();
        await Assert.That(() => first.Retain()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task value_payloads_share_using_the_supplied_key_comparer()
    {
        var released = new List<int>();
        using var cache = new RefCountedCache<string, int>(released.Add, StringComparer.OrdinalIgnoreCase);
        using var first = cache.Acquire("shader", static () => 7);
        using var second = cache.Acquire("SHADER", static () => throw new InvalidOperationException("Rebuilt live resource."));
        using var retained = second.Retain();

        await Assert.That(first.Value).IsEqualTo(7);
        await Assert.That(second.Value).IsEqualTo(7);
        first.Dispose();
        second.Dispose();
        await Assert.That(retained.Value).IsEqualTo(7);
        await Assert.That(released.Count).IsEqualTo(0);
        retained.Dispose();
        await Assert.That(released.SequenceEqual([7])).IsTrue();
    }

    [Test]
    public async Task failed_creation_does_not_leave_a_cache_entry()
    {
        using var cache = new RefCountedCache<string, object>();
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
        using var cache = new RefCountedCache<string, object>(_ => releases++);
        var old = cache.Acquire("same", static () => new object());
        cache.Clear();
        await Assert.That(releases).IsEqualTo(1);
        await Assert.That(() => old.Value).Throws<ObjectDisposedException>();
        await Assert.That(() => old.Retain()).Throws<ObjectDisposedException>();
        using var current = cache.Acquire("same", static () => new object());
        old.Dispose();
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(releases).IsEqualTo(1);
        current.Dispose();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(releases).IsEqualTo(2);
    }

    [Test]
    public async Task clear_retires_all_leases_before_callbacks_and_preserves_callback_acquisitions()
    {
        RefCountedCache<string, int>? cache = null;
        RefCountedCache<string, int>.Lease? replacement = null;
        var oldLeases = new List<RefCountedCache<string, int>.Lease>();
        var retiredChecks = 0;
        var released = new List<int>();
        cache = new RefCountedCache<string, int>(value =>
        {
            released.Add(value);
            if (value != 1) return;
            foreach (var lease in oldLeases)
            {
                try { lease.Retain().Dispose(); }
                catch (ObjectDisposedException) { retiredChecks++; }
            }
            replacement = cache!.Acquire("first", static () => 3);
            foreach (var lease in oldLeases) lease.Dispose();
        });
        using (cache)
        {
            oldLeases.Add(cache.Acquire("first", static () => 1));
            oldLeases.Add(cache.Acquire("second", static () => 2));

            cache.Clear();

            await Assert.That(retiredChecks).IsEqualTo(2);
            await Assert.That(released.SequenceEqual([1, 2])).IsTrue();
            await Assert.That(cache.Count).IsEqualTo(1);
            await Assert.That(replacement!.Value).IsEqualTo(3);
            replacement.Dispose();
            await Assert.That(released.SequenceEqual([1, 2, 3])).IsTrue();
        }
    }

    [Test]
    public async Task failed_clear_drains_cleanup_and_allows_reuse_without_stale_lease_interference()
    {
        var released = new List<int>();
        Exception[] failures = [new InvalidOperationException("first"), new InvalidOperationException("second")];
        using var cache = new RefCountedCache<string, int>(value =>
        {
            released.Add(value);
            if (value <= failures.Length) throw failures[value - 1];
        });
        var first = cache.Acquire("first", static () => 1);
        var second = cache.Acquire("second", static () => 2);
        var third = cache.Acquire("third", static () => 3);

        var error = await Assert.That(cache.Clear).Throws<AggregateException>();

        await Assert.That(error!.InnerExceptions.SequenceEqual(failures)).IsTrue();
        await Assert.That(released.SequenceEqual([1, 2, 3])).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(() => first.Value).Throws<ObjectDisposedException>();
        await Assert.That(() => second.Retain()).Throws<ObjectDisposedException>();
        using var replacement = cache.Acquire("first", static () => 4);
        first.Dispose();
        second.Dispose();
        third.Dispose();
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(replacement.Value).IsEqualTo(4);
        cache.Clear();
        await Assert.That(released.SequenceEqual([1, 2, 3, 4])).IsTrue();
    }

    [Test]
    public async Task dispose_is_terminal_idempotent_and_rejects_acquisition_during_cleanup()
    {
        RefCountedCache<string, int>? cache = null;
        var rejectedAcquisitions = 0;
        var factoryCalls = 0;
        var releases = 0;
        cache = new RefCountedCache<string, int>(_ =>
        {
            releases++;
            try { cache!.Acquire("replacement", () => ++factoryCalls).Dispose(); }
            catch (ObjectDisposedException) { rejectedAcquisitions++; }
            cache!.Dispose();
        });
        var lease = cache.Acquire("same", static () => 1);

        cache.Dispose();
        cache.Dispose();
        cache.Clear();
        lease.Dispose();

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(releases).IsEqualTo(1);
        await Assert.That(rejectedAcquisitions).IsEqualTo(1);
        await Assert.That(factoryCalls).IsEqualTo(0);
        await Assert.That(() => lease.Value).Throws<ObjectDisposedException>();
        await Assert.That(() => lease.Retain()).Throws<ObjectDisposedException>();
        await Assert.That(() => cache.Acquire("new", static () => 2)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task failed_dispose_drains_cleanup_and_remains_terminal()
    {
        var released = new List<int>();
        var cache = new RefCountedCache<string, int>(value =>
        {
            released.Add(value);
            throw new InvalidOperationException($"release {value}");
        });
        var first = cache.Acquire("first", static () => 1);
        var second = cache.Acquire("second", static () => 2);

        var error = await Assert.That(cache.Dispose).Throws<AggregateException>();

        await Assert.That(error!.InnerExceptions.Count).IsEqualTo(2);
        await Assert.That(released.SequenceEqual([1, 2])).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(() => first.Value).Throws<ObjectDisposedException>();
        await Assert.That(() => second.Retain()).Throws<ObjectDisposedException>();
        await Assert.That(() => cache.Acquire("new", static () => 3)).Throws<ObjectDisposedException>();
        cache.Dispose();
        cache.Clear();
        first.Dispose();
        second.Dispose();
        await Assert.That(released.Count).IsEqualTo(2);
    }

    [Test]
    public async Task failed_last_release_retires_the_entry_without_repeating_cleanup()
    {
        var released = new List<int>();
        using var cache = new RefCountedCache<string, int>(value =>
        {
            released.Add(value);
            if (value == 1) throw new InvalidOperationException("release failed");
        });
        var old = cache.Acquire("same", static () => 1);

        await Assert.That(old.Dispose).Throws<InvalidOperationException>();

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(() => old.Value).Throws<ObjectDisposedException>();
        await Assert.That(() => old.Retain()).Throws<ObjectDisposedException>();
        using var replacement = cache.Acquire("same", static () => 2);
        old.Dispose();
        await Assert.That(cache.Count).IsEqualTo(1);
        replacement.Dispose();
        await Assert.That(released.SequenceEqual([1, 2])).IsTrue();
    }
}
