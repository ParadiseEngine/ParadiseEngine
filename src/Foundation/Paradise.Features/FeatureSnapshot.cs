using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Paradise.Features;

/// <summary>One immutable feature configuration: the declarations, switches and settings one write left.</summary>
/// <remarks>
/// <para><see cref="FeatureSwitches"/> publishes a new snapshot for every write, and one for a whole
/// layer applied through <see cref="FeatureSwitches.Apply(EngineConfiguration)"/> or
/// <see cref="FeatureSwitches.Apply(FeatureOverrides)"/>. A reader holding a snapshot therefore
/// sees all of a layer or none of it, its switches and settings together, and nothing written
/// after the capture.</para>
/// <para>Capture once per frame or schedule run and make every decision of that run from it, so
/// two consumers of one feature can never disagree within the run. Capturing allocates nothing;
/// a write copies only what it changes.</para>
/// </remarks>
public sealed class FeatureSnapshot
{
    internal static FeatureSnapshot Empty { get; } = new(
        0,
        ImmutableDictionary<FeatureId, FeatureDefinition>.Empty,
        ImmutableDictionary<string, bool>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<string, FeatureSettings>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

    internal FeatureSnapshot(
        long version,
        ImmutableDictionary<FeatureId, FeatureDefinition> declared,
        ImmutableDictionary<string, bool> overrides,
        ImmutableDictionary<string, FeatureSettings> settings)
    {
        Version = version;
        Declared = declared;
        Overrides = overrides;
        Settings = settings;
    }

    /// <summary>Counts the publications of the switchboard that produced this snapshot.</summary>
    /// <remarks>Switches and settings share this one counter: a snapshot never pairs one layer's
    /// switches with another layer's settings. A larger version from the same switchboard is a
    /// later configuration.</remarks>
    public long Version { get; }

    internal ImmutableDictionary<FeatureId, FeatureDefinition> Declared { get; }

    internal ImmutableDictionary<string, bool> Overrides { get; }

    internal ImmutableDictionary<string, FeatureSettings> Settings { get; }

    /// <summary>All declared features.</summary>
    public IReadOnlyCollection<FeatureDefinition> Definitions => [.. Declared.Values];

    /// <summary>Returns whether <paramref name="id"/> is enabled; undeclared, unoverridden features are off.</summary>
    public bool IsEnabled(FeatureId id)
    {
        if (id.IsEmpty) return false;
        if (Overrides.TryGetValue(id.Value, out var overridden)) return overridden;
        return Declared.TryGetValue(id, out var definition) && definition.EnabledByDefault;
    }

    /// <summary>What the build declared about <paramref name="id"/>, if anything.</summary>
    public bool TryGetDefinition(FeatureId id, [MaybeNullWhen(false)] out FeatureDefinition definition) =>
        Declared.TryGetValue(id, out definition);

    /// <summary>Gets settings for <paramref name="id"/>, or <see cref="FeatureSettings.None"/> if absent.</summary>
    public FeatureSettings SettingsFor(FeatureId id) =>
        id.IsEmpty ? FeatureSettings.None
            : Settings.TryGetValue(id.Value, out var settings) ? settings : FeatureSettings.None;

    internal FeatureSnapshot With(
        ImmutableDictionary<FeatureId, FeatureDefinition>? declared = null,
        ImmutableDictionary<string, bool>? overrides = null,
        ImmutableDictionary<string, FeatureSettings>? settings = null) =>
        new(Version + 1, declared ?? Declared, overrides ?? Overrides, settings ?? Settings);
}
