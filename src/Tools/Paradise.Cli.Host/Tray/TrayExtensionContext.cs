namespace Paradise.Cli;

/// <summary>Routes extension process actions through the same cancellable runner used by the CLI.</summary>
internal sealed class TrayExtensionContext(string projectDirectory, IProcessRunner runner, Action<string> log)
    : ITrayExtensionContext
{
    public string ProjectDirectory { get; } = projectDirectory;

    public Task<int> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(argument => argument is null)) throw new ArgumentException("Process arguments cannot contain null.", nameof(arguments));
        var fileName = executable == "dotnet" ? DotnetLocator.Find()
            ?? throw new FileNotFoundException("dotnet could not be located for the tray task.") : executable;
        var spec = new ProcessSpec(fileName, arguments.ToArray(), ProjectDirectory);
        return Task.Run(() => runner.Run(spec, cancellationToken), cancellationToken);
    }

    public void Log(string message) => log(message);
}
