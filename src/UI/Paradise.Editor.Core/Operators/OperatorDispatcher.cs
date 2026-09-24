using Microsoft.Extensions.Logging;
using Paradise.Editor.Core.Extensibility;

namespace Paradise.Editor.Core.Operators;

/// <summary>Resolves, runs and reports operators for all editor entry points.</summary>
/// <remarks>Catches operator failures so exceptions cannot unwind an unbalanced ImGui frame.</remarks>
public sealed partial class OperatorDispatcher(
    IOperatorContext context, IRegistry<IOperator> operators, ILogger? logger = null)
    : IOperatorDispatcher
{
    // Use the field to avoid primary-constructor double capture (CS9124).
    private readonly IOperatorContext _context = context;

    // Use the host's logger unless the caller overrides it.
    private readonly ILogger _logger = logger ?? context.Log;

    /// <summary>The LAST registration wins, so a game overrides a built-in operator by id — the
    /// same rule the inspector's field renderers follow.</summary>
    public IOperator? Find(string id) =>
        operators.Entries.LastOrDefault(candidate => candidate.Id == id);

    public OperatorResult Dispatch(string id, OperatorArgs args)
    {
        if (Find(id) is not { } operatorInstance)
        {
            LogUnknown(_logger, id);
            return OperatorResult.Unavailable;
        }

        if (!operatorInstance.IsAvailable(_context))
        {
            LogUnavailable(_logger, id);
            return OperatorResult.Unavailable;
        }

        try
        {
            OperatorResult result = operatorInstance.Execute(_context, args);
            LogFinished(_logger, id, result);
            return result;
        }
        catch (Exception exception)
        {
            LogThrew(_logger, id, exception);
            return OperatorResult.Failed;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "operator '{Id}' finished as {Result}")]
    private static partial void LogFinished(ILogger logger, string id, OperatorResult result);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "operator '{Id}' is not available here")]
    private static partial void LogUnavailable(ILogger logger, string id);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "no operator is registered as '{Id}'")]
    private static partial void LogUnknown(ILogger logger, string id);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "operator '{Id}' threw")]
    private static partial void LogThrew(ILogger logger, string id, Exception exception);
}
