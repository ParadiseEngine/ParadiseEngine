using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Paradise.Features;

/// <summary>Holds one format-neutral configuration layer of feature switches and settings.</summary>
/// <remarks>
/// <para><c>Paradise.Features.Toml</c> reads <c>engine.toml</c>; hosts can also build layers from
/// command-line, server or saved data. Parsing stays outside this dependency-free assembly
/// because <c>Paradise.ECS</c> references it.</para>
/// <para>The record represents the versioned document, allowing new sections without changing
/// how hosts load configuration.</para>
/// </remarks>
public sealed record EngineConfiguration
{
    /// <summary>Provides shared empty settings before <see cref="Empty"/> is initialized.</summary>
    /// <remarks>Static initializers run in declaration order; moving this below <see cref="Empty"/>
    /// would give that layer null <see cref="Settings"/>.</remarks>
    internal static readonly ImmutableDictionary<string, FeatureSettings> EmptySettings =
        ImmutableDictionary<string, FeatureSettings>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>A layer that configures nothing.</summary>
    public static EngineConfiguration Empty { get; } = new();

    /// <summary>What this layer says about which features are on.</summary>
    public FeatureOverrides Features { get; init; } = FeatureOverrides.None;

    /// <summary>Maps feature names to settings independently of <see cref="Features"/> overrides.</summary>
    public IReadOnlyDictionary<string, FeatureSettings> Settings { get; init; } = EmptySettings;

    /// <summary>Builds a settings layer for readers, which own <see cref="FeatureSettings"/> construction.</summary>
    internal static EngineConfiguration FromSettings(IEnumerable<KeyValuePair<string, FeatureSettings>> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var builder = EmptySettings.ToBuilder();
        foreach (var (name, value) in settings) builder[name] = value;
        return new EngineConfiguration { Settings = builder.ToImmutable() };
    }

    /// <summary>Applies <paramref name="later"/> over this layer, section by section.</summary>
    /// <remarks>Each feature's settings are replaced wholesale. Deep merging would make ownership
    /// of nested tables and list elements ambiguous; later layers supply complete settings tables.</remarks>
    public EngineConfiguration Merge(EngineConfiguration later)
    {
        ArgumentNullException.ThrowIfNull(later);
        return new EngineConfiguration
        {
            Features = Features.Merge(later.Features),
            Settings = later.Settings.Count == 0
                ? Settings
                : Settings.Count == 0
                    ? later.Settings
                    : EmptySettings.SetItems(Settings).SetItems(later.Settings),
        };
    }
}
