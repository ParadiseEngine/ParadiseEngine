using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paradise.Editor.Core.Extensibility;

namespace Paradise.Editor.Core.Operators;

/// <summary>The reference <see cref="IOperatorDispatcher"/>: resolve an id, refuse what is not
/// available, run the rest, and report what happened.</summary>
/// <remarks>
/// <para>
/// Reporting belongs here rather than in each operator because every menu click, keybind and
/// palette entry passes through this one method. A panel that dispatches an id has nothing useful
/// to say about the outcome, and an operator that reported its own would word it differently in
/// every extension.
/// </para>
/// <para>
/// An operator that throws is caught and reported as <see cref="OperatorResult.Failed"/>. The
/// alternative is worse than it looks: an exception escaping here unwinds through the host's
/// in-progress ImGui frame with <c>Begin</c>/<c>End</c> unbalanced, so a bad operator in one panel
/// corrupts the NEXT frame rather than its own, and the editor dies somewhere that does not name
/// it. The log line is what a game's stack sees when the same extension misbehaves in-game.
/// </para>
/// </remarks>
public sealed partial class OperatorDispatcher(
    IOperatorContext context, IRegistry<IOperator> operators, ILogger? logger = null)
    : IOperatorDispatcher
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

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

        if (!operatorInstance.IsAvailable(context))
        {
            LogUnavailable(_logger, id);
            return OperatorResult.Unavailable;
        }

        try
        {
            OperatorResult result = operatorInstance.Execute(context, args);
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
