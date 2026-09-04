using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace Paradise.Editor.ImGui.Shell;

/// <summary>Extensions discovered in a directory and loaded into their own assembly context.
/// </summary>
/// <remarks>
/// <para>
/// This is why the editor does not publish ahead-of-time. NativeAOT has no JIT, so
/// <c>LoadFromAssemblyPath</c> cannot exist in an AOT binary; giving that up is what buys
/// drop-in extensions and lets an extension author use reflection freely in their own code.
/// The engine's own packages are unaffected — <c>Paradise.Ui.ImGui</c> is still AOT-clean, so a
/// game shipping an AOT launcher with a debug overlay is not caught by this.
/// </para>
/// <para>
/// HOST PATHS, not a Zio mount, and this is the same exception <c>Paradise.Audio.Wwise</c> makes:
/// the runtime's assembly loader opens the file itself, so no mount can back it, and an
/// abstraction that lies at that layer is worse than none.
/// </para>
/// <para>
/// A bad extension is REPORTED AND SKIPPED, never fatal. An editor that refuses to start because
/// somebody left a stale DLL in a folder is worse than one that starts with a message about it —
/// and the message is the only way to find out, since the alternative is a panel that silently
/// never appears.
/// </para>
/// </remarks>
public sealed partial class ExtensionCatalog : IDisposable
{
    private readonly List<PluginLoadContext> _contexts = [];

    private ExtensionCatalog(
        IReadOnlyList<IShellExtension> extensions,
        IReadOnlyList<string> problems,
        IEnumerable<PluginLoadContext>? contexts = null)
    {
        Extensions = extensions;
        Problems = problems;
        if (contexts is not null) _contexts.AddRange(contexts);
    }

    public IReadOnlyList<IShellExtension> Extensions { get; }

    /// <summary>One line per assembly or type that could not be loaded.</summary>
    public IReadOnlyList<string> Problems { get; }

    public static ExtensionCatalog Empty { get; } = new([], []);

    /// <summary>Load every <see cref="IShellExtension"/> in the <c>*.dll</c> files under
    /// <paramref name="directory"/>. A missing directory yields an empty catalog, not an error —
    /// having no extensions is the normal case.</summary>
    public static ExtensionCatalog Discover(string directory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        // Resolved to an absolute path, because AssemblyLoadContext.LoadFromAssemblyPath REFUSES a
        // relative one — and a relative --extensions is the obvious thing to type.
        directory = Path.GetFullPath(directory);

        if (!Directory.Exists(directory))
        {
            LogNoDirectory(log, directory);
            return Empty;
        }

        var contexts = new List<PluginLoadContext>();
        var found = new List<IShellExtension>();
        var problems = new List<string>();

        foreach (var file in Directory.GetFiles(directory, "*.dll").OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var context = new PluginLoadContext(file);
                contexts.Add(context);
                var assembly = context.LoadFromAssemblyPath(file);
                found.AddRange(Instantiate(assembly, file, problems, log));
            }
            catch (Exception exception)
            {
                // Broad on purpose, and for the same reason OperatorDispatcher is: the contract
                // this type states is that a bad extension is reported and skipped, NEVER fatal.
                // A narrower catch is a promise to have anticipated every way a file in a folder
                // can fail to be an assembly — and the first thing it missed was ArgumentException
                // from a relative path, which took the whole editor down on startup.
                problems.Add($"'{file}' could not be loaded: {exception.Message}");
                LogNotLoadable(log, file, exception);
            }
        }

        LogDiscovered(log, found.Count, directory);
        return new ExtensionCatalog(found, problems, contexts);
    }

    private static IEnumerable<IShellExtension> Instantiate(
        Assembly assembly, string file, List<string> problems, ILogger log)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // Partially loadable: take what resolved rather than losing the whole assembly over
            // one type whose dependency is missing.
            types = exception.Types.OfType<Type>().ToArray();
            problems.Add($"'{file}': some types could not be loaded: {exception.Message}");
        }

        foreach (var type in types)
        {
            if (!typeof(IShellExtension).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface) continue;

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                problems.Add($"'{type.FullName}' implements IShellExtension but has no parameterless constructor; skipped.");
                continue;
            }

            IShellExtension? extension = null;
            try
            {
                extension = (IShellExtension?)Activator.CreateInstance(type);
            }
            catch (Exception exception) when (exception is TargetInvocationException or MissingMethodException)
            {
                problems.Add($"'{type.FullName}' threw while being constructed: {exception.Message}");
                LogConstructionFailed(log, type.FullName ?? type.Name, exception);
            }

            if (extension is not null) yield return extension;
        }
    }

    /// <summary>Drop the loaded assemblies.</summary>
    /// <remarks>Best effort, and the caller's part is the larger one: a collectible context unloads
    /// only once EVERY reference into it is gone, and a panel the shell is still holding is exactly
    /// such a reference. Call <c>EditorShell.Unregister</c> for each extension first.</remarks>
    public void Dispose()
    {
        foreach (var context in _contexts) context.Unload();
        _contexts.Clear();
    }

    /// <summary>Collectible, and deliberately deferring to the DEFAULT context for anything it can
    /// already resolve.</summary>
    /// <remarks>Returning null from <see cref="Load"/> is what makes that happen, and it is the
    /// whole trick: without it the plugin gets its own copy of <c>Paradise.Editor.ImGui</c>, its
    /// <c>IShellExtension</c> is a different type from the host's, and the cast fails for reasons
    /// that read like nonsense. The plugin's OWN dependencies still resolve beside it.</remarks>
    private sealed class PluginLoadContext(string pluginPath)
        : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName) =>
            _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;

        protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "no extension directory at '{Directory}'")]
    private static partial void LogNoDirectory(ILogger logger, string directory);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "loaded {Count} extension(s) from '{Directory}'")]
    private static partial void LogDiscovered(ILogger logger, int count, string directory);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "'{File}' is not a loadable assembly")]
    private static partial void LogNotLoadable(ILogger logger, string file, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "'{Type}' could not be constructed")]
    private static partial void LogConstructionFailed(ILogger logger, string type, Exception exception);
}
