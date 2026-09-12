using System.ComponentModel;

using Paradise.Assets.Project;

using Zio;

namespace Paradise.Cli;

/// <summary>Publishes a declared extension before its assembly or dependencies enter the loader.</summary>
internal static class ExtensionProjectBuilder
{
    public static int Build(IFileSystem fileSystem, AssetProjectLayout layout, string project, UPath assembly,
        IProcessRunner processes, CancellationToken stop, Action<string> error)
    {
        if (stop.IsCancellationRequested) return ConsoleProcessRunner.Interrupted;
        var source = (layout.Root / project).ToAbsolute();
        if (!fileSystem.FileExists(source))
        {
            error($"error: [extensions] project names missing project '{project}'; its DLL was not loaded");
            return 1;
        }
        var staging = assembly.GetDirectory() / $".publish-{Guid.NewGuid():N}";
        try
        {
            fileSystem.CreateDirectory(staging);
            var spec = new ProcessSpec("dotnet",
                ["publish", ProjectPaths.Internal(fileSystem, source), "--configuration", "Release",
                    "--output", ProjectPaths.Internal(fileSystem, staging), "--nologo",
                    "--disable-build-servers", "-p:PublishAot=false", "-p:PublishTrimmed=false"],
                ProjectPaths.Internal(fileSystem, layout.Root));
            Console.WriteLine($"extensions: publishing {project}");
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
            foreach (var file in fileSystem.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var destination = assembly.GetDirectory() / file.FullName[(staging.FullName.Length + 1)..];
                fileSystem.CreateDirectory(destination.GetDirectory());
                fileSystem.CopyFile(file, destination, overwrite: true);
            }
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
}
