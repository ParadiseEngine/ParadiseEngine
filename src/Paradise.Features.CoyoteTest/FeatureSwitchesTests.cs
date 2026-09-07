using Microsoft.Coyote.Specifications;

namespace Paradise.Features.CoyoteTest;

/// <summary>The switchboard's cross-thread rule, systematically: <b>the last thing
/// <see cref="FeatureSwitches.Changed"/> said about a feature is what
/// <see cref="FeatureSwitches.IsEnabled"/> now answers for it.</b>
///
/// <para>That is not a nicety. A subscriber ACTS on the announcement — a render feature releases
/// a target, retracts a shadow plan, or zeroes a uniform buffer another feature binds every
/// frame. If the last announcement can contradict the state everyone reads, the feature is left
/// switched on with its state retracted (a black picture) or switched off with a target still
/// bound. The window is a check-then-act: read the current state, write the new one, decide
/// whether that was a change. Two writers can interleave inside it.</para>
///
/// <para>Written as awaited joins rather than <c>Task.WaitAll</c>, so Coyote's hang detection
/// stays on and means something.</para></summary>
public static class FeatureSwitchesTests
{
    private static readonly FeatureDefinition s_bloom = new("rendering.bloom", true, "The HDR bloom chain.");

    /// <summary>Watches every announcement and, at the end, checks the last one against the state
    /// the switchboard now reports. Subscribing costs nothing until a change actually
    /// happens.</summary>
    private sealed class LastAnnouncement
    {
        private bool _seen;
        private bool _last;

        public void Observe(FeatureId id, bool enabled)
        {
            _seen = true;
            _last = enabled;
        }

        public void AssertAgreesWith(FeatureSwitches switches, FeatureId id)
        {
            if (!_seen) return; // nothing was announced; nothing to contradict
            Specification.Assert(
                _last == switches.IsEnabled(id),
                "The last Changed said {0} but IsEnabled now answers {1}: a subscriber that acted on " +
                "the announcement is left disagreeing with the switch.",
                _last, switches.IsEnabled(id));
        }
    }

    /// <summary>Two threads driving one feature in opposite directions. Whoever wrote last, the
    /// announcement everybody heard last must describe that state.</summary>
    public static async Task TwoWritersOfOneFeature_LeaveTheAnnouncementAgreeingWithTheState()
    {
        var switches = new FeatureSwitches();
        switches.Declare(s_bloom);
        var watcher = new LastAnnouncement();
        switches.Changed += watcher.Observe;

        var off = Task.Run(() => switches.Set(s_bloom.Id, false));
        var on = Task.Run(() => switches.Set(s_bloom.Id, true));
        await off.ConfigureAwait(false);
        await on.ConfigureAwait(false);

        watcher.AssertAgreesWith(switches, s_bloom.Id);
    }

    /// <summary>The same, with the two ways a state is retracted: an explicit off and a reset back
    /// to the declaration's default.</summary>
    public static async Task SetRacingReset_LeavesTheAnnouncementAgreeingWithTheState()
    {
        var switches = new FeatureSwitches();
        switches.Declare(s_bloom);
        switches.Set(s_bloom.Id, false);
        var watcher = new LastAnnouncement();
        switches.Changed += watcher.Observe;

        var reset = Task.Run(() => switches.Reset(s_bloom.Id));
        var set = Task.Run(() => switches.Set(s_bloom.Id, false));
        await reset.ConfigureAwait(false);
        await set.ConfigureAwait(false);

        watcher.AssertAgreesWith(switches, s_bloom.Id);
    }

    /// <summary>A config layer applied while another thread flips one of its features: the layer
    /// is one write, so no reader may see it half applied, and the last announcement still
    /// describes the state.</summary>
    public static async Task ApplyRacingSet_NeverLeavesALayerHalfApplied()
    {
        var switches = new FeatureSwitches();
        switches.Declare(s_bloom);
        switches.Declare(new FeatureDefinition("rendering.shadows", true, "Shadow maps."));
        var shadows = new FeatureId("rendering.shadows");
        var watcher = new LastAnnouncement();
        switches.Changed += watcher.Observe;

        var apply = Task.Run(() => switches.Apply(FeatureOverrides.Parse("-rendering.bloom,-rendering.shadows")));
        var set = Task.Run(() => switches.Set(s_bloom.Id, true));
        await apply.ConfigureAwait(false);
        await set.ConfigureAwait(false);

        // The layer turned shadows off and nothing else touched them, so its half of the write
        // must have landed whatever order the two threads were scheduled in.
        Specification.Assert(!switches.IsEnabled(shadows), "Apply left a layer half applied.");
        watcher.AssertAgreesWith(switches, s_bloom.Id);
    }

    /// <summary>A subsystem declaring its features while a config layer is being applied to names
    /// it has not claimed yet — the startup order the switchboard promises to be indifferent to.
    /// The override must win, and no read may throw or see a torn declaration.</summary>
    public static async Task DeclareRacingApply_LeavesTheOverrideInForce()
    {
        var switches = new FeatureSwitches();

        var declare = Task.Run(() => switches.Declare(s_bloom));
        var apply = Task.Run(() => switches.Apply(FeatureOverrides.Parse("-rendering.bloom")));
        var read = Task.Run(() => switches.IsEnabled(s_bloom.Id));
        await declare.ConfigureAwait(false);
        await apply.ConfigureAwait(false);
        await read.ConfigureAwait(false);

        Specification.Assert(!switches.IsEnabled(s_bloom.Id),
            "The override lost to a declaration that arrived after it.");
        Specification.Assert(switches.Unknown.Count == 0,
            "A declared feature was still reported as an unknown name.");
    }

    /// <summary>What the renderer actually does: a frame reads the switch while a debug panel
    /// flips it. The read must never throw or answer with something that was never a state.</summary>
    public static async Task AReaderRacingAWriter_AlwaysSeesOneOfTheTwoStates()
    {
        var switches = new FeatureSwitches();
        switches.Declare(s_bloom);
        switches.Set(s_bloom.Id, false);

        var writer = Task.Run(() => switches.Set(s_bloom.Id, true));
        var reader = Task.Run(() =>
        {
            var first = switches.IsEnabled(s_bloom.Id);
            var second = switches.IsEnabled(s_bloom.Id);
            // Only one writer, and it only ever moves false → true: a reader that saw true can
            // never see false again.
            Specification.Assert(!first || second, "A switch went backwards under a single writer.");
        });
        await writer.ConfigureAwait(false);
        await reader.ConfigureAwait(false);
    }
}
