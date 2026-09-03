namespace Paradise.Editor.Core.Operators;

/// <summary>Resolves an operator id and runs it against the current context.</summary>
public interface IOperatorDispatcher
{
    IOperator? Find(string id);

    OperatorResult Dispatch(string id, OperatorArgs args);

    /// <summary>Whether <paramref name="id"/> would run right now.</summary>
    /// <remarks>Here rather than on the caller because availability needs the CONTEXT, and a menu
    /// or a palette has only an id. Without it every such caller would need the context too, which
    /// is the coupling operators exist to avoid.</remarks>
    bool IsAvailable(string id);
}
