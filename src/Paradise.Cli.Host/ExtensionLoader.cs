using System.Reflection;
using System.Runtime.Loader;

using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Cli;

/// <summary>Loads importer and tray extensions from the existing manifest assembly list.</summary>
/// <remarks>The development CLI is managed and untrimmed; none of this enters the shipped game.</remarks>
internal static class ExtensionLoader
{
    public static LoadedExtensions Load(IFileSystem fileSystem, AssetProjectLayout layout,
        IReadOnlyList<IAssetImporter> importers, bool includeTray = false, Action<string>? error = null,
        bool buildProjects = true, IProcessRunner? processes = null, CancellationToken stop = default)
    {
        error ??= Console.Error.WriteLine;
        var result = new LoadedExtensions(importers, error);
        ProjectManifest manifest;
        try { manifest = ProjectManifest.Load(fileSystem, layout.Manifest); }
        catch (ProjectManifestException) { return result; }

        var seen = new HashSet<string>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var projects = manifest.ExtensionProjects.ToDictionary(
            project => $".editor/extensions/{Path.GetFileNameWithoutExtension(project)}.dll",
            project => project, seen.Comparer);
        if (buildProjects)
        {
            // Publish everything before any dependency is mapped by the loader, especially on Windows.
            foreach (var (relative, project) in projects)
            {
                var exit = ExtensionProjectBuilder.Build(fileSystem, layout, project, (layout.Root / relative).ToAbsolute(),
                    processes ?? new ConsoleProcessRunner(), stop, error);
                if (exit == 0) continue;
                result.BuildExitCode = exit;
                return result;
            }
        }
        foreach (var relative in manifest.Extensions.Concat(projects.Keys))
        {
            var path = (layout.Root / relative).ToAbsolute();
            if (!seen.Add(path.FullName)) continue;
            if (!fileSystem.FileExists(path))
            {
                error($"error: [extensions] names '{relative}', which does not exist - build the project that produces it first");
                continue;
            }
            LoadAssembly(fileSystem.ConvertPathToInternal(path), relative, includeTray, result, error);
        }
        return result;
    }

    private static void LoadAssembly(string path, string relative, bool includeTray,
        LoadedExtensions result, Action<string> error)
    {
        Type[] types;
        try
        {
            var assembly = new ExtensionContext(path).LoadFromAssemblyPath(path);
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            error($"error: [extensions] '{relative}' has missing or incompatible dependencies: {ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message}");
            types = ex.Types.OfType<Type>().ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error($"error: [extensions] '{relative}' could not be loaded: {ex.Message}");
            return;
        }

        var recognized = false;
        foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (!type.IsVisible || type.IsAbstract) continue;
            var importer = typeof(IAssetImporter).IsAssignableFrom(type);
            var tray = typeof(ITrayExtension).IsAssignableFrom(type);
            if (!importer && !tray) continue;
            recognized = true;
            // Build/verify/play must not construct a tray-only extension or start its services.
            if (!importer && !includeTray) continue;
            if (type.ContainsGenericParameters || type.GetConstructor(Type.EmptyTypes) is null)
            {
                error($"error: [extensions] '{relative}' type '{type.FullName}' requires a public parameterless constructor and closed type parameters");
                continue;
            }
            try
            {
                var instance = Activator.CreateInstance(type)!;
                // A dual-role class is constructed and owned once, not once per interface.
                result.Add(instance, includeTray);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error($"error: [extensions] '{relative}' type '{type.FullName}' would not construct: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
        if (!recognized)
            error($"warning: [extensions] '{relative}' holds no public IAssetImporter or ITrayExtension; nothing was added");
    }

    private sealed class ExtensionContext(string assemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Share contracts explicitly even if this is the first extension to need one.
            // A plugin-private copy would give identically named interfaces different identities.
            foreach (var contract in new[] { typeof(ITrayExtension).Assembly, typeof(IAssetImporter).Assembly })
            {
                var identity = contract.GetName();
                if (!string.Equals(identity.Name, name.Name, StringComparison.Ordinal)) continue;
                if (name.Version is { } requested && identity.Version is { } available && requested > available)
                    throw new FileLoadException($"Extension requires {name}; host provides {identity}. Upgrade the CLI or rebuild the extension against its SDK.");
                return contract;
            }
            // Preserve the established importer sharing policy for host/pipeline dependencies.
            if (Default.Assemblies.Any(loaded => string.Equals(loaded.GetName().Name, name.Name, StringComparison.Ordinal))) return null;
            return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string name)
            => _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}

/// <summary>Owns dynamically constructed extensions until the command and its watch session have ended.</summary>
internal sealed class LoadedExtensions(IReadOnlyList<IAssetImporter> builtIns, Action<string> error) : IDisposable
{
    private readonly List<IAssetImporter> _importers = [.. builtIns];
    private readonly List<ITrayExtension> _tray = [];
    private readonly List<object> _owned = [];
    private bool _disposed;

    public IReadOnlyList<IAssetImporter> Importers => _importers;
    public IReadOnlyList<ITrayExtension> TrayExtensions => _tray;
    public int BuildExitCode { get; set; }

    public void Add(object instance, bool includeTray)
    {
        _owned.Add(instance);
        if (instance is IAssetImporter importer) _importers.Add(importer);
        if (includeTray && instance is ITrayExtension tray) _tray.Add(tray);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var index = _owned.Count - 1; index >= 0; index--)
        {
            if (_owned[index] is not IDisposable disposable) continue;
            try { disposable.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error($"error: extension '{disposable.GetType().FullName}' failed to dispose: {ex.Message}");
            }
        }
        _owned.Clear();
        _tray.Clear();
        _importers.Clear();
    }
}
