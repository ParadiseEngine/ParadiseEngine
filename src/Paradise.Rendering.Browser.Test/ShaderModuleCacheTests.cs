using Paradise.Rendering.Browser.Internal;

namespace Paradise.Rendering.Browser.Test;

public class ShaderModuleCacheTests
{
    [Test]
    public async Task identical_wgsl_shares_a_module_until_the_last_pipeline_releases_it()
    {
        var created = 0;
        List<int> destroyed = [];
        using var cache = new ShaderModuleCache((_, _, _) => created++, destroyed.Add);
        var first = cache.Acquire("shared wgsl", "vertex");
        var second = cache.Acquire("shared wgsl", "fragment");
        var slot = first.Slot;

        await Assert.That(created).IsEqualTo(1);
        await Assert.That(second.Slot).IsEqualTo(slot);
        first.Dispose();
        await Assert.That(() => first.Slot).Throws<ObjectDisposedException>();
        await Assert.That(second.Slot).IsEqualTo(slot);
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(destroyed.Count).IsEqualTo(0);
        second.Dispose();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(destroyed.Single()).IsEqualTo(slot);
    }

    [Test]
    public async Task old_lease_cannot_access_or_release_a_replacement_in_its_recycled_slot()
    {
        List<int> destroyed = [];
        using var cache = new ShaderModuleCache((_, _, _) => { }, destroyed.Add);
        var original = cache.Acquire("first", "first");
        var slot = original.Slot;
        original.Dispose();
        await Assert.That(() => original.Slot).Throws<ObjectDisposedException>();
        var replacement = cache.Acquire("replacement", "replacement");
        await Assert.That(replacement.Slot).IsEqualTo(slot);
        await Assert.That(() => original.Slot).Throws<ObjectDisposedException>();

        original.Dispose();

        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(destroyed.Count).IsEqualTo(1);
        replacement.Dispose();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(destroyed.Count).IsEqualTo(2);
    }

    [Test]
    public async Task failed_creation_preserves_other_modules_and_recycles_the_failed_slot()
    {
        var fail = false;
        List<int> destroyed = [];
        using var cache = new ShaderModuleCache((_, _, _) =>
        {
            if (fail) throw new InvalidOperationException("JS creation failed.");
        }, destroyed.Add);
        using var existing = cache.Acquire("existing", "existing");
        fail = true;
        await Assert.That(() => cache.Acquire("retry", "retry")).Throws<InvalidOperationException>();
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(destroyed.Count).IsEqualTo(0);

        fail = false;
        using var retry = cache.Acquire("retry", "retry");
        await Assert.That(retry.Slot).IsEqualTo(existing.Slot + 1);
        await Assert.That(cache.Count).IsEqualTo(2);
    }

    [Test]
    public async Task dispose_releases_each_module_once_despite_outstanding_pipeline_leases()
    {
        List<int> destroyed = [];
        using var cache = new ShaderModuleCache((_, _, _) => { }, destroyed.Add);
        var first = cache.Acquire("shared", "first");
        var second = cache.Acquire("shared", "second");
        var other = cache.Acquire("other", "other");

        cache.Dispose();
        cache.Dispose();
        await Assert.That(() => first.Slot).Throws<ObjectDisposedException>();
        await Assert.That(() => second.Slot).Throws<ObjectDisposedException>();
        await Assert.That(() => other.Slot).Throws<ObjectDisposedException>();
        first.Dispose();
        second.Dispose();
        other.Dispose();

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(destroyed.Count).IsEqualTo(2);
        await Assert.That(destroyed.Distinct().Count()).IsEqualTo(2);
        await Assert.That(() => cache.Acquire("after dispose", "after dispose")).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task repeated_program_replacement_reuses_shader_slots()
    {
        var created = 0;
        var destroyed = 0;
        using var cache = new ShaderModuleCache((slot, _, _) => created++, _ => destroyed++);
        for (var program = 0; program < 32; program++)
        {
            var lease = cache.Acquire($"program {program}", "program");
            await Assert.That(lease.Slot).IsEqualTo(0);
            lease.Dispose();
            await Assert.That(cache.Count).IsEqualTo(0);
        }
        await Assert.That(created).IsEqualTo(32);
        await Assert.That(destroyed).IsEqualTo(created);
    }
}
