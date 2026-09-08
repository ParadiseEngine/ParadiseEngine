using System.Reflection;
using System.Runtime.Loader;

using Paradise.Assets.Pipeline;
using Paradise.Assets.Project;

using Zio;

namespace Paradise.Cli;

/// <summary>Loads importer assemblies declared by a project manifest.</summary>
/// <remarks>
/// Reflection loading prevents CLI trimming and NativeAOT publishing.
/// A game-owned console entry point using <see cref="BuildHost.Run(string[], IReadOnlyList{IAssetImporter})"/>
/// lets MSBuild verify the dependency graph and is preferred for CI.
/// </remarks>
internal static class ExtensionLoader
{
    /// <summary>
    /// <paramref name="importers"/> with the manifest's extension importers appended, so they
    /// shadow the built-ins they replace. Problems are written to stderr and the built-ins are
    /// still returned: a broken extension must not make every verb unusable, and the message names
    /// the file.
    /// </summary>
    public static IReadOnlyList<IAssetImporter> Extend(
        IFileSystem fileSystem, AssetProjectLayout layout, IReadOnlyList<IAssetImporter> importers)
    {
        ProjectManifest manifest;
        try
        {
            manifest = ProjectManifest.Load(fileSystem, layout.Manifest);
        }
        catch (ProjectManifestException)
        {
            return importers;   // every verb reports this against the manifest itself
        }

        if (manifest.Extensions.Count == 0) return importers;

        var found = new List<IAssetImporter>();
        foreach (var relative in manifest.Extensions)
        {
            var path = (layout.Root / relative).ToAbsolute();
            if (!fileSystem.FileExists(path))
            {
                Console.Error.WriteLine($"error: [extensions] names '{relative}', which does not exist — build the project that produces it first");
                continue;
            }

            foreach (var importer in Load(fileSystem.ConvertPathToInternal(path), relative)) found.Add(importer);
        }

        return found.Count == 0 ? importers : [.. importers, .. found];
    }

    private static IReadOnlyList<IAssetImporter> Load(string assemblyPath, string relative)
    {
        Assembly assembly;
        try
        {
            assembly = new ExtensionContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception error) when (error is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            Console.Error.WriteLine($"error: [extensions] '{relative}' could not be loaded: {error.Message}");
            return [];
        }

        var found = new List<IAssetImporter>();
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            // The usual cause is version skew: the extension was compiled against a different
            // Paradise.Assets.Pipeline than this tool carries. Named as such, because the loader's
            // own message ("could not load type") sends people looking in the wrong place.
            var reason = error.LoaderExceptions.FirstOrDefault()?.Message ?? error.Message;
            Console.Error.WriteLine(
                $"error: [extensions] '{relative}' does not load against this build of the pipeline — rebuild it against the same Paradise version, or run its own asset tool instead ({reason})");
            return [];
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || !type.IsPublic || !typeof(IAssetImporter).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                Console.Error.WriteLine($"error: [extensions] '{relative}' has importer '{type.Name}' with no public parameterless constructor; it was skipped");
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is IAssetImporter importer) found.Add(importer);
            }
            catch (Exception error) when (error is TargetInvocationException or MissingMethodException or TypeLoadException)
            {
                Console.Error.WriteLine($"error: [extensions] '{relative}' importer '{type.Name}' would not construct: {error.InnerException?.Message ?? error.Message}");
            }
        }

        if (found.Count == 0) Console.Error.WriteLine($"warning: [extensions] '{relative}' holds no public IAssetImporter; nothing was added");
        return found;
    }

    /// <summary>
    /// The extension's own dependencies come from beside it; anything the HOST already has comes
    /// from the host.
    /// </summary>
    /// <remarks>
    /// That second half is the whole point. An extension implements the host's
    /// <see cref="IAssetImporter"/>, so the contract assemblies must be the same instances — load
    /// a second copy of Paradise.Assets.Pipeline beside the extension and its importers implement
    /// a DIFFERENT interface of the same name, which fails as "does not implement" with no clue
    /// why. Returning null defers to the default context, which is the host's.
    /// </remarks>
    private sealed class ExtensionContext(string assemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (Default.Assemblies.Any(loaded => string.Equals(loaded.GetName().Name, name.Name, StringComparison.Ordinal))) return null;
            return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string name)
            => _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
