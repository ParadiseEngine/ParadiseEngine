namespace Paradise.Editor.Core.Operators;

/// <summary>Undo one step.</summary>
/// <remarks>Stateless: everything it needs is on the context, which is what lets one instance
/// serve every project a session opens. Its availability is the history's own <c>CanUndo</c>, so
/// the menu item greys out because there is nothing to undo rather than because a panel remembered
/// to check.</remarks>
public sealed class UndoOperator : IOperator
{
    public const string OperatorId = "editor.undo";

    public string Id => OperatorId;
    public string Label => "Undo";
    public string Description => "Step back through the edits made in this session.";

    public bool IsAvailable(IOperatorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.History.CanUndo;
    }

    public OperatorResult Execute(IOperatorContext context, OperatorArgs args)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.History.Undo() is not null ? OperatorResult.Finished : OperatorResult.Cancelled;
    }
}

/// <summary>Redo the step undo last took back.</summary>
public sealed class RedoOperator : IOperator
{
    public const string OperatorId = "editor.redo";

    public string Id => OperatorId;
    public string Label => "Redo";
    public string Description => "Step forward again through edits that were undone.";

    public bool IsAvailable(IOperatorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.History.CanRedo;
    }

    public OperatorResult Execute(IOperatorContext context, OperatorArgs args)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.History.Redo() is not null ? OperatorResult.Finished : OperatorResult.Cancelled;
    }
}
