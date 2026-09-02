using System.Collections;

using Paradise.Authoring;

namespace Paradise.Assets.Documents;

/// <summary>
/// A table written on one line (spec item 11). A distinct TYPE so the model, not the data, decides
/// the written form in both writers. Exists because an <see cref="AssetReference"/> must sit inside
/// an array, where <c>[[header]]</c> form has no spelling for a null slot. Never nests a table: an
/// arbitrarily deep one-line table is neither readable nor diffable.
/// </summary>
public sealed class CanonicalInlineTable : IEnumerable<KeyValuePair<string, object>>
{
    private readonly List<KeyValuePair<string, object>> _pairs = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public int Count => _pairs.Count;

    /// <summary>Same widening as <see cref="CanonicalTomlTable.Add"/>, through <see cref="CanonicalFloat"/>.</summary>
    /// <exception cref="ArgumentException">The key is duplicated, or the value is not writable inline.</exception>
    public void Add(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value switch
        {
            bool or long or double or string => value,
            int widened => (long)widened,
            float widened => CanonicalFloat.Widen(widened),
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

    public bool ContainsKey(string key) => _keys.Contains(key);

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
                float widened => CanonicalFloat.Widen(widened),
                _ => throw new ArgumentException(
                    $"Array '{key}' inside an inline table holds scalars only.", nameof(array)),
            });
        }

        return elements;
    }
}
