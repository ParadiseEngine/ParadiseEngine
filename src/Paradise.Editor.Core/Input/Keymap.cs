using System.Collections.Immutable;
using Paradise.Editor.Core.Shell;

namespace Paradise.Editor.Core.Input;

/// <summary>Two bindings that cannot both apply: same chord, same context.</summary>
/// <remarks>Reported rather than thrown, because the shipped preset is not the user's fault and a
/// keymap that refuses to load leaves the editor with no keyboard at all. The later binding wins —
/// which is what makes a user override an override — and the Console says what it displaced.</remarks>
public sealed record KeymapConflict(Chord Chord, string? Context, string Displaced, string Winner)
{
    public override string ToString() =>
        $"'{Chord}'{(Context is null ? "" : $" in '{Context}'")}: '{Winner}' displaces '{Displaced}'";
}

/// <summary>Chord plus input context to operator id.</summary>
/// <remarks>
/// <para>
/// Built by layering: the shipped preset first, then the user's override file, then anything an
/// extension registered. Later wins, and every displacement is reported — see
/// <see cref="KeymapConflict"/>.
/// </para>
/// <para>
/// A binding with no context is global. One with a context applies only while the shell reports
/// that context active, and BEATS a global binding on the same chord, so a panel can take a chord
/// over without the preset having to know the panel exists.
/// </para>
/// </remarks>
public sealed class Keymap
{
    private readonly ImmutableDictionary<(Chord Chord, string? Context), string> _bindings;

    private Keymap(ImmutableDictionary<(Chord, string?), string> bindings) => _bindings = bindings;

    public static Keymap Empty { get; } =
        new(ImmutableDictionary<(Chord, string?), string>.Empty);

    public IEnumerable<KeyBinding> Bindings =>
        _bindings.Select(entry => new KeyBinding(entry.Key.Chord, entry.Value, entry.Key.Context));

    /// <summary>This keymap with <paramref name="bindings"/> layered on top.</summary>
    public Keymap With(IEnumerable<KeyBinding> bindings, out IReadOnlyList<KeymapConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var builder = _bindings.ToBuilder();
        var reported = new List<KeymapConflict>();
        foreach (var binding in bindings)
        {
            var key = (binding.Chord, binding.Context);
            if (builder.TryGetValue(key, out var displaced) && displaced != binding.OperatorId)
            {
                reported.Add(new KeymapConflict(binding.Chord, binding.Context, displaced, binding.OperatorId));
            }
            builder[key] = binding.OperatorId;
        }

        conflicts = reported;
        return new Keymap(builder.ToImmutable());
    }

    /// <summary>The operator <paramref name="chord"/> runs in <paramref name="context"/>, or null.
    /// A context binding beats the global one.</summary>
    public string? Resolve(Chord chord, string? context = null)
    {
        if (context is not null && _bindings.TryGetValue((chord, context), out var scoped)) return scoped;
        return _bindings.TryGetValue((chord, null), out var global) ? global : null;
    }
}
