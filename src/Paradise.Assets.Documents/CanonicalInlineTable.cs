using System.Collections;

using Paradise.Authoring;

namespace Paradise.Assets.Documents;

/// <summary>
/// A table written on ONE line — <c>{ key = value, … }</c> — rather than under a
/// <c>[header]</c>. Spec item 11 of <see cref="CanonicalTomlWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// A distinct TYPE rather than a property of the data, and that is the whole design. The obvious
/// alternative — "write a table inline when all its values are scalars" — makes the output depend
/// on what the table happens to contain, so the C# and Python writers would agree until the first
/// document where they read the rule differently, and the disagreement would surface as a
/// <c>prefab-check</c> byte failure nobody could trace back to a formatting rule. With a type, the
/// model says which form it wants and both writers obey it.
/// </para>
/// <para>
/// This exists because an <see cref="AssetReference"/> is table-shaped and has to sit INSIDE an
/// array — <c>Slots = [{ … }, { … }]</c> — which an array-of-tables cannot express, because
/// <c>[[header]]</c> per element has no spelling for the null slot the materials contract
/// requires ("a null entry keeps the GLB's own"). An empty inline table, <c>{}</c>, is that null.
/// </para>
/// <para>
/// Inline tables hold scalars and arrays only. Nesting one inside another is refused: TOML allows
/// it, but the value vocabulary here is deliberately the intersection both toolchains handle
/// losslessly, and an arbitrarily deep one-line table is neither readable nor diffable.
/// </para>
/// </remarks>
public sealed class CanonicalInlineTable : IEnumerable<KeyValuePair<string, object>>
{
    private readonly List<KeyValuePair<string, object>> _pairs = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>Number of keys in this table.</summary>
    public int Count => _pairs.Count;

    /// <summary>
    /// Adds a value under <paramref name="key"/>. Accepts the scalar vocabulary and arrays of it —
    /// never a table of either kind.
    /// </summary>
    /// <exception cref="ArgumentException">The key is duplicated, or the value is not writable inline.</exception>
    public void Add(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value switch
        {
            bool or long or double or string => value,
            int widened => (long)widened,
            float widened => (double)widened,
            CanonicalInlineTable or CanonicalTomlTable or IReadOnlyList<CanonicalTomlTable> =>
                throw new ArgumentException(
                    $"Value for '{key}' is a table. An inline table holds scalars and arrays of " +
                    "scalars only — a nested one-line table is neither readable nor diffable.",
                    nameof(value)),
            IEnumerable<object> array => NormalizeArray(array, key),
            _ => throw new ArgumentException(
                $"Value for '{key}' is a {value.GetType().Name}; an inline table holds bool, " +
                "long, double, string and arrays of those.", nameof(value)),
        };

        if (!_keys.Add(key)) throw new ArgumentException($"Duplicate key '{key}'.", nameof(key));
        _pairs.Add(new KeyValuePair<string, object>(key, normalized));
    }

    /// <summary>Whether <paramref name="key"/> exists in this table.</summary>
    public bool ContainsKey(string key) => _keys.Contains(key);

    /// <summary>The value under <paramref name="key"/>, or <see langword="null"/> when absent.</summary>
    public object? Value(string key)
    {
        foreach (var (candidate, value) in _pairs)
        {
            if (string.Equals(candidate, key, StringComparison.Ordinal)) return value;
        }

        return null;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _pairs.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static object NormalizeArray(IEnumerable<object> array, string key)
    {
        var elements = new List<object>();
        foreach (var element in array)
        {
            ArgumentNullException.ThrowIfNull(element, key);
            elements.Add(element switch
            {
                bool or long or double or string => element,
                int widened => (long)widened,
                float widened => (double)widened,
                _ => throw new ArgumentException(
                    $"Array '{key}' inside an inline table holds scalars only.", nameof(array)),
            });
        }

        return elements;
    }
}
