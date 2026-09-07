using System;

namespace Paradise.Features;

/// <summary>What a build knows about one feature before anybody configures it: its name, whether
/// it is on when nothing says otherwise, and a line explaining what turning it off costs.
///
/// <para>Declared by the code that OWNS the feature, not by the config file — a file lists
/// overrides, and a name it holds that no build declares is a stale line rather than a feature
/// (<see cref="FeatureSwitches.Unknown"/>). The default is part of the declaration for the same
/// reason: shipping with a feature off is the owner's decision, and a config that has to name
/// every feature to get the intended frame is a config nobody keeps current.</para></summary>
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
