using System;
using System.Diagnostics.CodeAnalysis;

namespace Paradise.Features;

/// <summary>The name of one switchable engine feature: dotted segments, subsystem first —
/// <c>rendering.bloom</c>, <c>rendering.globalIllumination</c>, <c>ui.debugPanels</c>,
/// <c>gameplay.weather</c>.
///
/// <para>A type rather than a bare string because the name travels between three places that
/// never see each other — the feature that declares it, the config file that overrides it, and
/// the subsystem that asks whether it is on — and a typo in any of them is otherwise a silent
/// "off". Validating once, here, turns it into an exception naming the bad name.</para>
///
/// <para><b>Comparison ignores case</b> (ordinal), because the other half of this contract is a
/// file a person edits by hand and <c>Rendering.Bloom</c> meaning something different from
/// <c>rendering.bloom</c> is a trap with no upside. The declared spelling is what
/// <see cref="Value"/> and <see cref="ToString"/> give back, so a listing still reads the way the
/// feature's author wrote it.</para></summary>
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

    /// <summary>Written out rather than left to the record, which would compare the wrapped
    /// string ordinally and make <c>Rendering.Bloom</c> a different feature from
    /// <c>rendering.bloom</c> — see the type's own remarks. The synthesized <c>==</c> and
    /// <c>Equals(object)</c> route through this one, so the whole type stays consistent.</summary>
    public bool Equals(FeatureId other) => StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    /// <inheritdoc cref="Equals(FeatureId)"/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <summary>The name, not the record's <c>FeatureId { Value = … }</c>: this goes into error
    /// messages and listings, where the name alone is the useful part.</summary>
    public override string ToString() => Value;
}
