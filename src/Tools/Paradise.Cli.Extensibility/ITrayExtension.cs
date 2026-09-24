namespace Paradise.Cli;

/// <summary>Contributes C# task groups to the existing development tray when a watch session starts.</summary>
/// <remarks>
/// Implementations must be public, concrete and have a public parameterless constructor.
/// Registration is synchronous and must not start background work. The host owns watches,
/// task scheduling and cancellation. An implementation may also implement IDisposable;
/// the host disposes it after its tasks and native menus have stopped. Restart the watcher
/// to load a rebuilt extension DLL; this contract does not promise assembly hot reload.
/// </remarks>
public interface ITrayExtension
{
    IReadOnlyList<TrayTaskGroup> CreateTaskGroups(ITrayExtensionContext context);
}

/// <summary>Services provided to a tray extension for the lifetime of one watch session.</summary>
public interface ITrayExtensionContext
{
    string ProjectDirectory { get; }

    /// <summary>Runs a child outside the menu thread; cancellation stops and joins its process tree.</summary>
    /// <remarks>Arguments are passed individually, not interpreted by a shell.</remarks>
    Task<int> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    void Log(string message);
}
