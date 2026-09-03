using System.Collections.Immutable;

namespace Paradise.Editor.Core.Operators;

public enum OperatorResult
{
    Finished,
    Cancelled,
    Unavailable,

    /// <summary>The operator threw. Distinct from <see cref="Cancelled"/>, which is a decision the
    /// operator made; this is one it never got to make.</summary>
    Failed,
}

/// <summary>Named arguments for one invocation; a menu entry or a keybind passes none, a panel
/// passes what it knows.</summary>
public sealed record OperatorArgs(ImmutableDictionary<string, object?> Values)
{
    public static OperatorArgs None { get; } = new(ImmutableDictionary<string, object?>.Empty);

    public T? Get<T>(string key) => TryGet<T>(key, out var value) ? value : default;

    /// <summary>Distinguishes absent and wrong-typed from present-and-default, which
    /// <see cref="Get{T}"/> cannot: an operator invoked from a menu with no arguments should
    /// report <see cref="OperatorResult.Cancelled"/>, not apply <see langword="default"/>.</summary>
    public bool TryGet<T>(string key, out T value)
    {
        if (Values.TryGetValue(key, out var held) && held is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }
}

/// <summary>A named, dispatchable editor action: the only way anything is done.</summary>
/// <remarks>Menus, toolbars, keybinds and the command palette are data naming an
/// <see cref="Id"/>; none of them holds code. An operator that changes the document commits a
/// new version through the context and is undoable for free; one that only changes editor UI
/// state does not touch the history and is therefore not undoable, by design.</remarks>
public interface IOperator
{
    /// <summary>Stable, dotted, lower-case: <c>editor.layout.reset</c>.</summary>
    string Id { get; }

    string Label { get; }

    string Description { get; }

    bool IsAvailable(IOperatorContext context);

    OperatorResult Execute(IOperatorContext context, OperatorArgs args);
}
