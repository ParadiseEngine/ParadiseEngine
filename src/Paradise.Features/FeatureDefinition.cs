using System;

namespace Paradise.Features;

/// <summary>Declares a feature's identity, default state and description.</summary>
/// <remarks>The feature's owner declares these values; configuration supplies overrides.
/// Unclaimed configuration names appear in <see cref="FeatureSwitches.Unknown"/>.</remarks>
/// <param name="Id">The name this feature is switched by.</param>
/// <param name="EnabledByDefault">Its state when no override applies.</param>
/// <param name="Summary">One line, for a listing or a debug UI: what it does, or what is lost
/// without it.</param>
public sealed record FeatureDefinition(FeatureId Id, bool EnabledByDefault = true, string Summary = "")
{
    /// <summary>The name this feature is switched by.</summary>
    public FeatureId Id { get; } = Id.IsEmpty
        ? throw new ArgumentException("A feature declaration needs a name.", nameof(Id))
        : Id;

    /// <summary>Declares a feature from its name.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a valid feature id.</exception>
    public FeatureDefinition(string id, bool enabledByDefault = true, string summary = "")
        : this(new FeatureId(id), enabledByDefault, summary)
    {
    }

    /// <summary>The name, for a message or a config line.</summary>
    public string Name => Id.Value;
}
