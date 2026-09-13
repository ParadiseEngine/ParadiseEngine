using System.ComponentModel;

using Paradise.Assets.Project;

using Zio;

namespace Paradise.Cli;

/// <summary>Publishes a declared extension before its assembly or dependencies enter the loader.</summary>
internal static class ExtensionProjectBuilder
{
    public static int Build(IFileSystem fileSystem, AssetProjectLayout layout, string project, UPath assembly,
        IProcessRunner processes, CancellationToken stop, Action<string> error, Action<string> log)
    {
        if (stop.IsCancellationRequested) return ConsoleProcessRunner.Interrupted;
        var source = (layout.Root / project).ToAbsolute();
        if (!fileSystem.FileExists(source))
        {
            error($"error: [extensions] project names missing project '{project}'; its DLL was not loaded");
            return 1;
        }
        var staging = assembly.GetDirectory() / $".publish-{Guid.NewGuid():N}";
        var published = assembly.GetDirectory() / $".{assembly.GetName()}.published";
        try
        {
            fileSystem.CreateDirectory(staging);
            var spec = new ProcessSpec("dotnet",
                ["publish", ProjectPaths.Internal(fileSystem, source), "--configuration", "Release",
                    "--output", ProjectPaths.Internal(fileSystem, staging), "--nologo",
                    "--disable-build-servers", "-p:PublishAot=false", "-p:PublishTrimmed=false"],
                ProjectPaths.Internal(fileSystem, layout.Root));
            log($"extensions: publishing {project}");
            var exit = processes.Run(spec, stop);
            if (exit != 0)
            {
                error($"error: [extensions] project '{project}' publish failed (exit {exit}); its DLL was not loaded");
                return exit;
            }
            // Validate fresh output before touching the previous publish. An AssemblyName change
            // must not make a successful build appear to have refreshed an unrelated old DLL.
            if (!fileSystem.FileExists(staging / assembly.GetName()))
            {
                error($"error: [extensions] project '{project}' did not produce '{assembly.GetName()}'; check AssemblyName");
                return 1;
            }
            var previous = Published(fileSystem, published);
            var fresh = fileSystem.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Select(file => file.FullName[(staging.FullName.Length + 1)..])
                .Order(StringComparer.Ordinal).ToArray();
            foreach (var relative in fresh)
            {
                var destination = assembly.GetDirectory() / relative;
                fileSystem.CreateDirectory(destination.GetDirectory());
                fileSystem.CopyFile(staging / relative, destination, overwrite: true);
            }
            // Retract what this project's previous publish produced and this one no longer does:
            // AssemblyDependencyResolver would otherwise still find a dependency that was dropped
            // or renamed. The record is per project because sibling projects and prebuilt
            // `assemblies` entries share `.editor/extensions` and are none of this publish's to remove.
            foreach (var stale in previous.Except(fresh, StringComparer.Ordinal))
            {
                var path = assembly.GetDirectory() / stale;
                if (fileSystem.FileExists(path)) fileSystem.DeleteFile(path);
            }
            // Written last: a publish interrupted before this leaves the previous list in place, so
            // the next attempt still retracts what this one stopped producing rather than stranding it.
            fileSystem.WriteAllText(published, string.Join('\n', fresh) + "\n");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            error($"error: [extensions] project '{project}' could not be published: {exception.Message}; its DLL was not loaded");
            return 1;
        }
        finally
        {
            try
            {
                if (fileSystem.DirectoryExists(staging)) fileSystem.DeleteDirectory(staging, isRecursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error($"warning: extension publish staging could not be removed: {exception.Message}");
            }
        }
    }

    /// <summary>The files this project produced last time, so its next publish can retract the ones it stops producing.</summary>
    private static HashSet<string> Published(IFileSystem fileSystem, UPath record)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        if (!fileSystem.FileExists(record)) return files;
        foreach (var line in fileSystem.ReadAllText(record).Split('\n'))
        {
            if (line.Trim() is { Length: > 0 } name) files.Add(name);
        }

        return files;
    }
}
