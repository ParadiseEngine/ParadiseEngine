using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Paradise.Features;

/// <summary>Stores an immutable layer of feature names and enabled states.</summary>
/// <remarks>Raw names are retained before features are declared, including malformed or stale
/// names that <see cref="FeatureSwitches.Unknown"/> must report. Layers can be reused across
/// switchboards.</remarks>
public sealed class FeatureOverrides : IReadOnlyCollection<KeyValuePair<string, bool>>
{
    /// <summary>Provides a shared empty layer for callers without overrides.</summary>
    public static FeatureOverrides None { get; } = new(ImmutableDictionary<string, bool>.Empty
        .WithComparers(StringComparer.OrdinalIgnoreCase));

    private readonly ImmutableDictionary<string, bool> _states;

    private FeatureOverrides(ImmutableDictionary<string, bool> states) => _states = states;

    /// <summary>Builds a layer from <paramref name="states"/>, keeping the last value for duplicate names.</summary>
    public static FeatureOverrides From(IEnumerable<KeyValuePair<string, bool>> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var builder = None._states.ToBuilder();
        foreach (var (name, enabled) in states) builder[name] = enabled;
        return new FeatureOverrides(builder.ToImmutable());
    }

    /// <summary>This layer plus one more entry.</summary>
    public FeatureOverrides With(string name, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new FeatureOverrides(_states.SetItem(name.Trim(), enabled));
    }

    /// <inheritdoc cref="With(string, bool)"/>
    public FeatureOverrides With(FeatureId id, bool enabled) => With(id.Value, enabled);

    /// <summary>Applies <paramref name="later"/> over this layer, preserving names it does not mention.</summary>
    /// <remarks>Engine precedence is declarations, config file, environment, then command line;
    /// for example, <c>--features -rendering.bloom</c> overrides the configured default.</remarks>
    public FeatureOverrides Merge(FeatureOverrides later)
    {
        ArgumentNullException.ThrowIfNull(later);
        if (later.Count == 0) return this;
        if (Count == 0) return later;
        return new FeatureOverrides(_states.SetItems(later._states));
    }

    /// <summary>Reads a list a person typed: <c>+rendering.ssr,-rendering.bloom</c>. A bare name
    /// turns the feature ON; a <c>-</c> or <c>!</c> prefix, or an explicit
    /// <c>name=false</c>/<c>off</c>/<c>0</c>, turns it off. Separators are commas, semicolons and
    /// whitespace, so a shell that splits the argument and one that does not both work.</summary>
    /// <exception cref="FormatException">An entry names a value that is not a boolean.</exception>
    public static FeatureOverrides Parse(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) return None;
        var builder = None._states.ToBuilder();
        foreach (var raw in list.Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw;
            var enabled = true;
            if (entry[0] is '-' or '!')
            {
                enabled = false;
                entry = entry[1..];
            }
            else if (entry[0] == '+')
            {
                entry = entry[1..];
            }
            var equals = entry.IndexOf('=');
            if (equals >= 0)
            {
                var value = entry[(equals + 1)..].Trim();
                entry = entry[..equals].Trim();
                enabled = ParseBoolean(value, raw);
            }
            if (entry.Length == 0) throw new FormatException($"'{raw}' names no feature.");
            builder[entry] = enabled;
        }
        return new FeatureOverrides(builder.ToImmutable());
    }

    /// <summary>The list in <paramref name="variable"/>, in the syntax of
    /// <see cref="Parse"/>, or <see cref="None"/> when it is unset.</summary>
    public static FeatureOverrides FromEnvironment(string variable = "PARADISE_FEATURES")
    {
        ArgumentException.ThrowIfNullOrEmpty(variable);
        return Parse(Environment.GetEnvironmentVariable(variable));
    }

    private static bool ParseBoolean(string value, string entry) => value.ToLowerInvariant() switch
    {
        "true" or "on" or "yes" or "1" => true,
        "false" or "off" or "no" or "0" => false,
        _ => throw new FormatException($"'{entry}' sets a feature to '{value}'; write true/false, on/off, yes/no or 1/0."),
    };

    public int Count => _states.Count;

    /// <summary>What this layer says about <paramref name="name"/>, if anything.</summary>
    public bool TryGet(string name, out bool enabled) => _states.TryGetValue(name, out enabled);

    /// <inheritdoc cref="TryGet(string, out bool)"/>
    public bool TryGet(FeatureId id, out bool enabled) => _states.TryGetValue(id.Value, out enabled);

    public IEnumerator<KeyValuePair<string, bool>> GetEnumerator() => _states.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
