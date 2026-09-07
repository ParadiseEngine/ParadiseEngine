using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Paradise.Features;

/// <summary>What one configuration layer says about features: names mapped to on or off, and
/// nothing else. Immutable, so a layer can be read once and merged into as many switchboards as
/// a process has.
///
/// <para><b>Keyed by the raw name, not by <see cref="FeatureId"/>, on purpose.</b> A layer is
/// read before the features exist — the config file is parsed at startup, the renderer declares
/// its features when it is constructed — so a name here cannot be checked against a declaration
/// yet, and a name that turns out to be malformed or stale must survive as far as
/// <see cref="FeatureSwitches.Unknown"/> to be reported. Dropping it at parse time would make a
/// typo in a config file look exactly like a feature that is off.</para></summary>
public sealed class FeatureOverrides : IReadOnlyCollection<KeyValuePair<string, bool>>
{
    /// <summary>A layer that says nothing. Shared: a caller with no overrides iterates this
    /// rather than branching on null.</summary>
    public static FeatureOverrides None { get; } = new(ImmutableDictionary<string, bool>.Empty
        .WithComparers(StringComparer.OrdinalIgnoreCase));

    private readonly ImmutableDictionary<string, bool> _states;

    private FeatureOverrides(ImmutableDictionary<string, bool> states) => _states = states;

    /// <summary>A layer holding exactly <paramref name="states"/>. A name repeated with a
    /// different value keeps the LAST, matching how a later layer wins over an earlier one.</summary>
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

    /// <summary>This layer with <paramref name="later"/> applied over it: every name
    /// <paramref name="later"/> mentions takes its value, the rest keep this layer's.
    ///
    /// <para>The order the engine layers in is defaults (the declarations themselves), then the
    /// config file, then the environment, then the command line — nearest to the person running
    /// the build wins, which is what makes <c>--features -rendering.bloom</c> a thing you can
    /// type without editing a file you will forget to change back.</para></summary>
    public FeatureOverrides Merge(FeatureOverrides later)
    {
        ArgumentNullException.ThrowIfNull(later);
        if (later.Count == 0) return this;
        if (Count == 0) return later;
        var builder = _states.ToBuilder();
        foreach (var (name, enabled) in later._states) builder[name] = enabled;
        return new FeatureOverrides(builder.ToImmutable());
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
