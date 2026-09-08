using System;
using System.Diagnostics.CodeAnalysis;

namespace Paradise.Features;

/// <summary>Identifies a feature by a validated dotted name, with the subsystem first.</summary>
/// <remarks>
/// <para>Examples: <c>rendering.bloom</c>, <c>rendering.globalIllumination</c>,
/// <c>ui.debugPanels</c>, <c>gameplay.weather</c>. Validation rejects malformed names shared
/// between declarations, configuration and consumers.</para>
/// <para>Comparison is ordinal and case-insensitive, so hand-edited casing does not change
/// identity. <see cref="Value"/> and <see cref="ToString"/> preserve the declared spelling.</para>
/// </remarks>
public readonly record struct FeatureId
{
    private readonly string? _value;

    /// <summary>Parses and validates <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentException">The name is empty, or holds a segment that is empty or
    /// not made of ASCII letters, digits, <c>_</c> or <c>-</c>.</exception>
    public FeatureId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Validate(value) is { } error) throw new ArgumentException(error, nameof(value));
        _value = value;
    }

    private FeatureId(string value, bool _) => _value = value;

    /// <summary>The declared spelling. Empty for <c>default(FeatureId)</c>, which names nothing
    /// and is never enabled.</summary>
    public string Value => _value ?? "";

    /// <summary>True for <c>default(FeatureId)</c> — a field nobody assigned, not a feature.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>The first segment: the subsystem a listing groups by.</summary>
    public ReadOnlySpan<char> Subsystem
    {
        get
        {
            var value = Value.AsSpan();
            var dot = value.IndexOf('.');
            return dot < 0 ? value : value[..dot];
        }
    }

    /// <summary>The id, or an empty id when <paramref name="value"/> is not a valid name.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out FeatureId id)
    {
        if (value is null || Validate(value) is not null)
        {
            id = default;
            return false;
        }
        id = new FeatureId(value, true);
        return true;
    }

    private static string? Validate(string value)
    {
        if (value.Length == 0) return "A feature id may not be empty.";
        var segmentLength = 0;
        foreach (var c in value)
        {
            if (c == '.')
            {
                if (segmentLength == 0) return $"'{value}' has an empty segment; feature ids read like 'rendering.bloom'.";
                segmentLength = 0;
                continue;
            }
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
                return $"'{value}' holds '{c}'; a feature id segment is ASCII letters, digits, '_' or '-'.";
            segmentLength++;
        }
        return segmentLength == 0 ? $"'{value}' ends with a '.'; feature ids read like 'rendering.bloom'." : null;
    }

    /// <summary>Compares names with ordinal, case-insensitive equality.</summary>
    /// <remarks>Overrides the record's case-sensitive string comparison; synthesized <c>==</c> and
    /// <c>Equals(object)</c> also use this method.</remarks>
    public bool Equals(FeatureId other) => StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    /// <inheritdoc cref="Equals(FeatureId)"/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <summary>Returns the feature name for messages and listings.</summary>
    public override string ToString() => Value;
}
