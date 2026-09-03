using System.Collections.Immutable;

namespace Paradise.Editor.Core.Operators;

public enum OperatorResult
{
    Finished,
    Cancelled,
    Unavailable,
}

/// <summary>Named arguments for one invocation; a menu entry or a keybind passes none, a panel
/// passes what it knows.</summary>
public sealed record OperatorArgs(ImmutableDictionary<string, object?> Values)
{
    public static OperatorArgs None { get; } = new(ImmutableDictionary<string, object?>.Empty);

    public T? Get<T>(string key) => Values.TryGetValue(key, out var value) && value is T typed ? typed : default;
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
