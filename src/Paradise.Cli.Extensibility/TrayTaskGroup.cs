namespace Paradise.Cli;

/// <summary>A parent submenu with an opt-in watch, tasks, cancellation, status and an optional folder action.</summary>
/// <remarks>Paths are project-relative and '/'-separated. The host snapshots all collections at registration.</remarks>
public sealed record TrayTaskGroup
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public bool AutoWatch { get; init; }
    public string AutoWatchLabel { get; init; } = "Auto-watch";
    public required string AutoTask { get; init; }
    public int DebounceMilliseconds { get; init; } = 300;
    public IReadOnlyList<TrayTaskInput> Inputs { get; init; } = [];
    /// <summary>Output files or directories excluded from automatic task triggers, including all descendants.</summary>
    /// <remarks>Exclusions use path boundaries and apply even when the output does not exist.</remarks>
    public IReadOnlyList<string> Outputs { get; init; } = [];
    public required IReadOnlyList<TrayTaskDefinition> Tasks { get; init; }
    public string? OpenDirectory { get; init; }
    public string OpenDirectoryLabel { get; init; } = "Open Source Folder";
}

/// <summary>A file input, or a directory whose filename patterns are watched recursively by default.</summary>
public sealed record TrayTaskInput(string Path, IReadOnlyList<string>? Patterns = null, bool Recursive = true);

/// <summary>A cancellable C# action, always executed off the native menu thread.</summary>
/// <remarks>Return zero for success. Cooperatively observe cancellation; the host cannot forcibly abort managed callbacks.</remarks>
public sealed record TrayTaskDefinition(string Id, string Label, Func<CancellationToken, Task<int>> Execute);
