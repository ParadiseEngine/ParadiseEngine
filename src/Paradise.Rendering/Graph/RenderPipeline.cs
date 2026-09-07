using System;
using System.Collections.Generic;
using Paradise.Features;

namespace Paradise.Rendering.Graph;

/// <summary>The ordered features that make up a frame, the switches that decide which of them
/// run, and the driver that runs their setup.
///
/// <para><b>Order is a spaced integer, not a list position.</b> A feature that consumes another's
/// blackboard result must set up after it, and expressing that as "call Add in the right
/// sequence" works only while ONE piece of code does all the adding. A game adding a feature that
/// must publish before the scene reads it would otherwise have no way to say so, and the engine
/// adding a built-in between two others would have to be edited in the same place every time. The
/// slots are spaced for the same reason <see cref="RenderPassEvent"/>'s are: write
/// <c>PbrFeatureOrder.Scene - 1</c> and land there. Equal orders keep insertion order.</para>
///
/// <para><b>Whether a feature runs is configuration, not position.</b> Every feature declares
/// itself into <see cref="Switches"/> when it is added, and the pipeline reads that switch each
/// frame — so a built-in and a game's own feature are turned off the same way, from the same
/// config file, at runtime. A feature that needs to KNOW it was switched off (because something
/// else still reads state it owns) overrides
/// <see cref="IRenderFeature.OnEnabledChanged"/>.</para>
///
/// <para>Not a GPU pipeline: the name is the pipeline of features a frame is, in the sense
/// Unity's scriptable render pipeline uses the word.</para></summary>
public sealed class RenderPipeline : IDisposable
{
    /// <summary>Where a feature lands when its composer names no slot: after every built-in, in
    /// the order the features were added. What a host feature that consumes the finished frame
    /// wants, and the only sensible answer for one that says nothing.</summary>
    public const int DefaultOrder = int.MaxValue;

    private sealed class Entry(IRenderFeature feature, int order, long sequence)
    {
        public IRenderFeature Feature { get; } = feature;
        public int Order { get; } = order;
        public long Sequence { get; } = sequence;

        /// <summary>Whether this feature runs in the frame being built. Read from the switchboard
        /// ONCE per frame, by <see cref="BeginFrame"/>, and used by every phase after it.</summary>
        public bool EnabledThisFrame;

        /// <summary>The state the feature has been told about through
        /// <see cref="IRenderFeature.OnEnabledChanged"/>. Kept apart from
        /// <see cref="EnabledThisFrame"/> so the notification happens exactly on a transition,
        /// on the thread that begins the frame, however many times the switch was flipped in
        /// between.</summary>
        public bool Applied;
    }

    /// <summary>The features of <see cref="_entries"/>, without a second list to keep in step
    /// with it.</summary>
    private sealed class FeatureView(List<Entry> entries) : IReadOnlyList<IRenderFeature>
    {
        public IRenderFeature this[int index] => entries[index].Feature;
        public int Count => entries.Count;
        public IEnumerator<IRenderFeature> GetEnumerator()
        {
            foreach (var entry in entries) yield return entry.Feature;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private readonly List<Entry> _entries = [];
    private readonly FrameBlackboard _blackboard = new();
    private long _added;
    private bool _snapshotFresh;
    private bool _disposed;

    /// <param name="width">The frame size features are told on <see cref="Add"/>, before any
    /// resize, so a feature declares its targets once through the same call either way.</param>
    /// <param name="height">The frame height, as <paramref name="width"/>.</param>
    /// <param name="switches">The engine's feature configuration — REQUIRED, and required to be
    /// the one the rest of the process reads. A default here would mean a pipeline that quietly
    /// got a private switchboard, ran every feature at its declared default and ignored the config
    /// file with nothing to notice: the frame just renders, wrongly. A caller that genuinely
    /// configures nothing writes <c>new FeatureSwitches()</c> and has said so.</param>
    public RenderPipeline(uint width, uint height, FeatureSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(switches);
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Switches = switches;
        Features = new FeatureView(_entries);
    }

    public uint Width { get; private set; }
    public uint Height { get; private set; }

    /// <summary>The engine's feature configuration, which this pipeline's features are declared
    /// into and read from. Flip a switch here and the next frame is different.</summary>
    public FeatureSwitches Switches { get; }

    /// <summary>Every feature, in setup order, including the ones currently switched off.</summary>
    public IReadOnlyList<IRenderFeature> Features { get; }

    /// <summary>Adds a feature at <paramref name="order"/> and tells it the current frame size.
    /// Its declaration is registered with <see cref="Switches"/>, so it is listable and
    /// switchable from that moment — including by a config layer that was applied before the
    /// feature existed.</summary>
    public RenderPipeline Add(IRenderFeature feature, int order = DefaultOrder)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var definition = Switches.Declare(feature.Definition);
        var entry = new Entry(feature, order, _added++);
        var index = _entries.Count;
        while (index > 0 && IsAfter(_entries[index - 1], entry)) index--;
        _entries.Insert(index, entry);

        feature.Resize(Width, Height);
        // ONE read, feeding both the notification and this frame's answer. The switch may already
        // be off — a config file read at startup names features that are constructed later — and
        // a feature is entitled to hear that exactly once, here, rather than discovering it by
        // never being called.
        var enabled = Switches.IsEnabled(definition.Id);
        entry.EnabledThisFrame = enabled;
        entry.Applied = enabled;
        if (enabled != definition.EnabledByDefault) feature.OnEnabledChanged(enabled);
        return this;
    }

    private static bool IsAfter(Entry existing, Entry inserted) =>
        existing.Order > inserted.Order || (existing.Order == inserted.Order && existing.Sequence > inserted.Sequence);

    /// <summary>The first feature of type <typeparamref name="T"/>, for a host that configures
    /// one it did not construct.</summary>
    public T? Find<T>() where T : class, IRenderFeature
    {
        foreach (var entry in _entries)
            if (entry.Feature is T match) return match;
        return null;
    }

    /// <summary>Whether <paramref name="feature"/> runs in the frame being built — this frame's
    /// answer, not the switchboard's current one. <see cref="Switches"/> is where to ask what a
    /// switch says right now.</summary>
    public bool IsEnabled(IRenderFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return IsEnabled(feature.Definition.Id);
    }

    /// <inheritdoc cref="IsEnabled(IRenderFeature)"/>
    public bool IsEnabled(FeatureId id)
    {
        foreach (var entry in _entries)
            if (entry.Feature.Definition.Id == id) return entry.EnabledThisFrame;
        return false;
    }

    /// <summary>Adopt every switch change since the last frame and fix which features run in the
    /// next one. Called by <see cref="Setup"/>; call it yourself before that when something in
    /// the frame depends on the answer — the renderer decides whether to build the trace
    /// hierarchy before any feature sets up — or when you have flipped a switch and need the
    /// feature's own state to have caught up before you touch it.
    ///
    /// <para><b>This is the ONE place a switch is read per frame, and the one place a transition
    /// is announced.</b> Every phase after it — requirements, setup, recording, submit — reads
    /// the answer this took, so a frame cannot be half-configured: a feature that set up is a
    /// feature that gets its <see cref="IRenderFeature.BeforeSubmit"/>, whatever a debug panel on
    /// another thread did in between. It is also why the pipeline does not subscribe to
    /// <see cref="FeatureSwitches.Changed"/>: a handler on the flipping thread would release
    /// targets and retract plans while this thread was recording with them.</para></summary>
    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var entry in _entries)
        {
            entry.EnabledThisFrame = Switches.IsEnabled(entry.Feature.Definition.Id);
            if (entry.EnabledThisFrame == entry.Applied) continue;
            entry.Applied = entry.EnabledThisFrame;
            entry.Feature.OnEnabledChanged(entry.EnabledThisFrame);
        }
        _snapshotFresh = true;
    }

    /// <summary>The union of the requirements of the features running this frame.</summary>
    public FrameRequirements Requirements()
    {
        var requirements = FrameRequirements.None;
        foreach (var entry in _entries)
            if (entry.EnabledThisFrame) requirements |= entry.Feature.Requires;
        return requirements;
    }

    public void Resize(uint width, uint height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        // Every feature, switched off ones included: a feature that skipped the resize would
        // hand out a stale target the frame it is switched back on.
        foreach (var entry in _entries) entry.Feature.Resize(Width, Height);
    }

    /// <summary>Run every enabled feature's setup in order against <paramref name="graph"/>,
    /// which the caller has already reset for this frame.</summary>
    public void Setup(FrameGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A caller that began the frame itself keeps that snapshot; one that did not gets it
        // here. Either way the flag is consumed, so the NEXT setup takes a fresh one.
        if (!_snapshotFresh) BeginFrame();
        _snapshotFresh = false;

        _blackboard.Clear();
        var frame = new FrameContext(graph, Width, Height, Requirements(), _blackboard);
        foreach (var entry in _entries)
            if (entry.EnabledThisFrame) entry.Feature.Setup(in frame);
    }

    /// <summary>Tell every enabled feature the frame is compiled and about to be submitted, in
    /// the same order they set up. The caller runs this between its compile and its submit; a
    /// feature that filled a buffer while recording uploads it here.</summary>
    public void BeforeSubmit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // This frame's answer, not the switchboard's: a feature that staged a buffer during
        // recording uploads it even if its switch went off while the graph was compiling.
        foreach (var entry in _entries)
            if (entry.EnabledThisFrame) entry.Feature.BeforeSubmit();
    }

    /// <summary>Features are disposed in reverse order, so a consumer goes before what it consumed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var i = _entries.Count - 1; i >= 0; i--) _entries[i].Feature.Dispose();
        _entries.Clear();
    }
}
