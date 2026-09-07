using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Paradise.Features;

/// <summary>One configuration layer: which features are on, and what each of them is configured
/// with. Format-neutral — <c>Paradise.Features.Toml</c> reads <c>engine.toml</c> into this, and a
/// host that gets its configuration from somewhere else (a command line, a server, a save file)
/// builds one directly.
///
/// <para><b>The parsing is not here, and that is the whole layering.</b> This assembly has no
/// package references because <c>Paradise.ECS</c> references it, so it cannot hold a TOML parser;
/// the reader is a package the host picks, the way a logging provider is. What crosses the seam is
/// this record, which is a bag of names and values and depends on nothing.</para>
///
/// <para>A record with sections rather than a bare <see cref="FeatureOverrides"/> because the FILE
/// is the thing being versioned. A third section is a property here and a table in the same file,
/// and every host that already reads its configuration keeps reading it.</para></summary>
public sealed record EngineConfiguration
{
    /// <summary>Declared BEFORE <see cref="Empty"/> and load-bearing there: static initializers
    /// run in declaration order, so an <see cref="Empty"/> built above this line would read it as
    /// null and hand out a layer whose <see cref="Settings"/> is null — which nothing catches
    /// until something iterates it.</summary>
    internal static readonly ImmutableDictionary<string, FeatureSettings> EmptySettings =
        ImmutableDictionary<string, FeatureSettings>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>A layer that configures nothing.</summary>
    public static EngineConfiguration Empty { get; } = new();

    /// <summary>What this layer says about which features are on.</summary>
    public FeatureOverrides Features { get; init; } = FeatureOverrides.None;

    /// <summary>What this layer configures each feature with, by feature name. A name here need
    /// not appear in <see cref="Features"/>: settings and the switch are written independently,
    /// and a feature that ships on only ever needs the settings half.</summary>
    public IReadOnlyDictionary<string, FeatureSettings> Settings { get; init; } = EmptySettings;

    /// <summary>A layer holding exactly these settings, for a host building one by hand.</summary>
    public static EngineConfiguration FromSettings(IEnumerable<KeyValuePair<string, FeatureSettings>> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var builder = EmptySettings.ToBuilder();
        foreach (var (name, value) in settings) builder[name] = value;
        return new EngineConfiguration { Settings = builder.ToImmutable() };
    }

    /// <summary>This layer with <paramref name="later"/> applied over it, section by section.
    ///
    /// <para>A feature's settings are REPLACED wholesale, not deep-merged. A deep merge reads
    /// well in the two-file case and stops being predictable the moment a list or a nested table
    /// is involved — "which layer owns element 3" has no answer anyone wants to reason about at
    /// two in the morning. A later file that means to change one field writes the table it
    /// wants.</para></summary>
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
