namespace Paradise.Features.Test;

/// <summary>The rules a subsystem relies on: a declaration supplies the default, an override
/// beats it whichever order the two arrive in, and a name nobody claims stays visible instead of
/// reading as a feature that is off.</summary>
public class FeatureSwitchesTests
{
    private static readonly FeatureDefinition Bloom = new("rendering.bloom", true, "The HDR bloom chain.");
    private static readonly FeatureDefinition Capture = new("rendering.sceneColorCapture", false, "Opt-in.");

    [Test]
    public async Task a_declared_feature_takes_its_declaration_default()
    {
        var switches = new FeatureSwitches();
        switches.Declare(Bloom);
        switches.Declare(Capture);

        await Assert.That(switches.IsEnabled(Bloom.Id)).IsTrue();
        await Assert.That(switches.IsEnabled(Capture.Id)).IsFalse();
    }

    /// <summary>A build that does not have the feature and a build that turned it off answer the
    /// same, because that is the only thing a caller can act on.</summary>
    [Test]
    public async Task an_undeclared_feature_nobody_configured_is_off()
    {
        var switches = new FeatureSwitches();

        await Assert.That(switches.IsEnabled(new FeatureId("gameplay.weather"))).IsFalse();
        await Assert.That(switches.IsEnabled(default)).IsFalse();
    }

    /// <summary>The config file is read before the renderer exists, so an override has to survive
    /// the wait — in both orders, because a host may also configure something after it built the
    /// subsystem that owns it.</summary>
    [Test]
    public async Task an_override_wins_whichever_side_of_the_declaration_it_arrives()
    {
        var before = new FeatureSwitches(FeatureOverrides.Parse("-rendering.bloom"));
        before.Declare(Bloom);

        var after = new FeatureSwitches();
        after.Declare(Bloom);
        after.Apply(FeatureOverrides.Parse("-rendering.bloom"));

        await Assert.That(before.IsEnabled(Bloom.Id)).IsFalse();
        await Assert.That(after.IsEnabled(Bloom.Id)).IsFalse();
    }

    [Test]
    public async Task reset_returns_a_feature_to_its_declared_default()
    {
        var switches = new FeatureSwitches();
        switches.Declare(Bloom);
        switches.Set(Bloom.Id, false);

        var whileOverridden = switches.IsEnabled(Bloom.Id);
        switches.Reset(Bloom.Id);

        await Assert.That(whileOverridden).IsFalse();
        await Assert.That(switches.IsEnabled(Bloom.Id)).IsTrue();
    }

    /// <summary>What makes a runtime toggle a toggle: the change is announced, once, and only
    /// when the effective answer actually moved.</summary>
    [Test]
    public async Task changed_fires_on_a_real_transition_only()
    {
        var switches = new FeatureSwitches();
        switches.Declare(Bloom);
        var seen = new List<(string Name, bool Enabled)>();
        switches.Changed += (id, enabled) => seen.Add((id.Value, enabled));

        switches.Set(Bloom.Id, true);   // already the default: no change
        switches.Set(Bloom.Id, false);
        switches.Set(Bloom.Id, false);  // same again: no change
        switches.Reset(Bloom.Id);

        await Assert.That(seen).IsEquivalentTo([("rendering.bloom", false), ("rendering.bloom", true)]);
    }

    /// <summary>A stale line in a config file is the one thing this can get wrong silently, so it
    /// is reported as data — the host decides whether a leftover switch is worth a warning or a
    /// refusal to start.</summary>
    [Test]
    public async Task a_name_no_declaration_claims_is_reported_rather_than_dropped()
    {
        var switches = new FeatureSwitches(FeatureOverrides.Parse("-rendering.bloom,-rendering.blom,-rendering/bloom"));

        var beforeDeclaring = switches.Unknown;
        switches.Declare(Bloom);

        await Assert.That(beforeDeclaring).Contains("rendering.bloom");
        await Assert.That(switches.Unknown).IsEquivalentTo(["rendering.blom", "rendering/bloom"]);
    }

    [Test]
    public async Task declaring_the_same_feature_twice_is_fine_and_disagreeing_is_not()
    {
        var switches = new FeatureSwitches();
        switches.Declare(Bloom);
        switches.Declare(new FeatureDefinition("rendering.bloom", true, "The HDR bloom chain."));

        await Assert.That(() => switches.Declare(new FeatureDefinition("rendering.bloom", false)))
            .Throws<InvalidOperationException>().WithMessageContaining("rendering.bloom");
    }

    /// <summary>The other half of the contract is a file a person types into.</summary>
    [Test]
    public async Task a_name_is_matched_without_regard_to_case()
    {
        var switches = new FeatureSwitches(FeatureOverrides.Parse("-Rendering.Bloom"));
        switches.Declare(Bloom);

        await Assert.That(switches.IsEnabled(Bloom.Id)).IsFalse();
        await Assert.That(switches.Unknown).IsEmpty();
    }

    [Test]
    public async Task definitions_lists_what_the_build_declared()
    {
        var switches = new FeatureSwitches();
        switches.Declare(Bloom);
        switches.Declare(Capture);

        await Assert.That(switches.Definitions.Select(d => d.Name).Order())
            .IsEquivalentTo(["rendering.bloom", "rendering.sceneColorCapture"]);
    }

    /// <summary>What a debug UI writes back, or a second process is started with, so it renders
    /// the frame you are looking at.</summary>
    [Test]
    public async Task a_snapshot_carries_every_effective_state_and_the_stale_names_too()
    {
        var switches = new FeatureSwitches(FeatureOverrides.Parse("-rendering.gone"));
        switches.Declare(Bloom);
        switches.Declare(Capture);
        switches.Set(Bloom.Id, false);

        var snapshot = switches.Snapshot();

        await Assert.That(snapshot.TryGet(Bloom.Id, out var bloom) && !bloom).IsTrue();
        await Assert.That(snapshot.TryGet(Capture.Id, out var capture) && !capture).IsTrue();
        await Assert.That(snapshot.TryGet("rendering.gone", out var gone) && !gone).IsTrue();
    }

    /// <summary>A render thread asks per frame while a debug panel flips switches: every read
    /// returns, nothing throws, and the state the single writer left is the state everyone reads.
    ///
    /// <para>Counting the reads that saw TRUE and asserting some did was the first version of
    /// this, and it is not something the test can promise — under a loaded machine the writer can
    /// finish all its iterations before a reader is scheduled at all, leaving the last written
    /// value (false) for every read. It went red once in a full-solution run and never alone.
    /// What IS guaranteed is that every read completed and the last write stands; the
    /// interleavings themselves belong to Paradise.Features.CoyoteTest, which explores them
    /// deliberately rather than hoping for them.</para></summary>
    [Test]
    public async Task reads_and_writes_from_many_threads_do_not_break_it()
    {
        const int Reads = 10_000;
        const int Writes = 10_000;
        var switches = new FeatureSwitches();
        switches.Declare(Bloom);
        var completed = 0;

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < Reads; i++)
            {
                switches.IsEnabled(Bloom.Id);
                Interlocked.Increment(ref completed);
            }
        }));
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < Writes; i++) switches.Set(Bloom.Id, i % 2 == 0);
        });
        await Task.WhenAll(readers.Append(writer)).ConfigureAwait(false);

        await Assert.That(completed).IsEqualTo(4 * Reads);
        // The writer's last iteration wrote (Writes - 1) % 2 == 0, which is false.
        await Assert.That(switches.IsEnabled(Bloom.Id)).IsFalse();
    }
}
