namespace Paradise.Editor.Core.Operators;

/// <summary>Resolves an operator id and runs it against the current context.</summary>
public interface IOperatorDispatcher
{
    IOperator? Find(string id);

    OperatorResult Dispatch(string id, OperatorArgs args);
}
