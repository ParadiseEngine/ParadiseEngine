using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Paradise.Features;

/// <summary>Exposes declared features, current switches and settings to engine subsystems.</summary>
/// <remarks>Consumers need not know whether values came from defaults, files, environment,
/// command-line flags or runtime changes. <see cref="Capture"/> once at each frame or schedule
/// boundary and decide the whole run from that snapshot; separate live reads can straddle a
/// concurrent write, and caching permanently prevents runtime toggles.</remarks>
public interface IFeatureSwitches
{
    /// <summary>Returns the current configuration as one immutable snapshot.</summary>
    /// <remarks>Later writes do not change a captured snapshot; they apply to the next capture.
    /// <see cref="FeatureSnapshot"/> has no public constructor: another implementation wraps a
    /// <see cref="FeatureSwitches"/> and returns its capture.</remarks>
    FeatureSnapshot Capture();

    /// <summary>Returns whether <paramref name="id"/> is enabled; undeclared, unoverridden features are off.</summary>
    bool IsEnabled(FeatureId id);

    /// <summary>What the build declared about <paramref name="id"/>, if anything.</summary>
    bool TryGetDefinition(FeatureId id, [MaybeNullWhen(false)] out FeatureDefinition definition);

    /// <summary>Gets settings for <paramref name="id"/>, or <see cref="FeatureSettings.None"/> if absent.</summary>
    /// <remarks>Bind with the producing reader, such as <c>FeatureSettingsToml.Read</c> for
    /// <c>engine.toml</c>; absent settings bind to the caller type's defaults.</remarks>
    FeatureSettings SettingsFor(FeatureId id);

    /// <summary>Gets all declared features for listings, debug UIs and configuration generation.</summary>
    IReadOnlyCollection<FeatureDefinition> Definitions { get; }

    /// <summary>Reports a feature's changed effective state.</summary>
    /// <remarks>
    /// <para>Handlers run on the writer's thread under the write lock, serializing concurrent writers.
    /// Reentrant writes raise nested notifications.</para>
    /// <para>GPU and per-frame state owners must poll at frame start instead. <c>RenderPipeline</c>
    /// announces transitions there and uses one snapshot throughout the frame, preventing cross-thread
    /// resource disposal and partial setup/submission.</para>
    /// </remarks>
    event Action<FeatureId, bool>? Changed;

    /// <summary>Reports replacement settings, allowing consumers to respond to configuration reloads.</summary>
    /// <remarks>Uses the thread and ordering rules of <see cref="Changed"/>; unchanged settings text
    /// does not raise a notification.</remarks>
    event Action<FeatureId, FeatureSettings>? SettingsChanged;
}
