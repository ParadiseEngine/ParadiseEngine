using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Serialization;

using Zio;

namespace Paradise.Cli;

/// <summary>Project-authored task groups for the existing watch tray, independent of any compiler.</summary>
internal sealed class TrayTaskConfiguration
{
    public const string RelativePath = "authoring/tray-tasks.json";
    public int Version { get; set; }
    public TrayTaskGroupConfiguration[] Groups { get; set; } = [];

    public static TrayTaskConfiguration Load(IFileSystem fileSystem, UPath root)
    {
        var path = root / RelativePath;
        return fileSystem.FileExists(path) ? Parse(fileSystem.ReadAllText(path)) : new() { Version = 1 };
    }

    public static TrayTaskConfiguration Parse(string json)
    {
        var config = JsonSerializer.Deserialize(json, TrayTaskJsonContext.Default.TrayTaskConfiguration)
            ?? throw new InvalidDataException("Tray task configuration is null.");
        if (config.Version != 1) throw new InvalidDataException("Unsupported tray task configuration version; expected 1.");
        if (config.Groups is null || config.Groups.Length > 32) throw new InvalidDataException("Expected at most 32 tray task groups.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in config.Groups)
        {
            if (group is null) throw new InvalidDataException("A tray task group is null.");
            group.Validate();
            if (!ids.Add(group.Id)) throw new InvalidDataException($"Duplicate tray task group '{group.Id}'.");
        }
        return config;
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':')
            || new UPath(path).IsAbsolute || path.Split('/').Any(part => part is ".." or "." or ""))
        {
            throw new InvalidDataException($"Tray task path '{path}' must be a project-relative '/'-separated path without traversal.");
        }
    }
}

internal sealed class TrayTaskGroupConfiguration
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool AutoWatch { get; set; }
    public string AutoWatchLabel { get; set; } = "Auto-watch";
    public string AutoTask { get; set; } = "";
    public int DebounceMilliseconds { get; set; } = 300;
    public TrayTaskInput[] Inputs { get; set; } = [];
    public string[] Outputs { get; set; } = [];
    public TrayTaskDefinition[] Tasks { get; set; } = [];
    public string? OpenDirectory { get; set; }
    public string OpenDirectoryLabel { get; set; } = "Open Source Folder";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Label)) throw new InvalidDataException("Tray task groups require id and label.");
        if (DebounceMilliseconds is < 50 or > 60000) throw new InvalidDataException($"'{Id}' debounce must be 50–60000 milliseconds.");
        if (Tasks is null || Tasks.Length is 0 or > 32 || Inputs is null || Outputs is null)
            throw new InvalidDataException($"'{Id}' requires tasks, inputs and outputs arrays.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in Tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Label)
                || string.IsNullOrWhiteSpace(task.Executable) || task.Arguments is null || task.Arguments.Any(arg => arg is null))
                throw new InvalidDataException($"Invalid task in '{Id}'. Supply id, label, executable and an arguments array.");
            if (!ids.Add(task.Id)) throw new InvalidDataException($"Duplicate task '{task.Id}' in '{Id}'.");
        }
        if (!ids.Contains(AutoTask)) throw new InvalidDataException($"'{Id}' autoTask '{AutoTask}' does not name a task.");
        foreach (var input in Inputs)
        {
            if (input is null || input.Patterns is null) throw new InvalidDataException($"Invalid input in '{Id}'.");
            TrayTaskConfiguration.ValidatePath(input.Path);
            if (input.Patterns.Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Contains('/') || pattern.Contains('\\')))
                throw new InvalidDataException("Input patterns match filenames; use path and recursive to select directories.");
        }
        foreach (var output in Outputs) TrayTaskConfiguration.ValidatePath(output);
        if (OpenDirectory is not null) TrayTaskConfiguration.ValidatePath(OpenDirectory);
        if (string.IsNullOrWhiteSpace(AutoWatchLabel)) throw new InvalidDataException("Auto-watch label must not be empty.");
        if (string.IsNullOrWhiteSpace(OpenDirectoryLabel)) throw new InvalidDataException("Open directory label must not be empty.");
    }

    /// <summary>Structural events include both sides of directory renames and deleted directories.</summary>
    public bool Observes(string relative, bool structural = false)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (relative.Split('/').Any(part => part is ".git" or ".editor" or "bin" or "obj")) return false;
        if (Outputs.Any(output => string.Equals(output, relative, comparison))) return false;
        return Inputs.Any(input => input.Observes(relative, structural, comparison));
    }
}

internal sealed class TrayTaskInput
{
    public string Path { get; set; } = "";
    public string[] Patterns { get; set; } = [];
    public bool Recursive { get; set; } = true;

    public bool Observes(string relative, bool structural, StringComparison comparison)
    {
        if (string.Equals(relative, Path, comparison)) return true;
        if (structural && Path.StartsWith(relative + "/", comparison)) return true;
        if (Patterns.Length == 0 || !relative.StartsWith(Path + "/", comparison)) return false;
        var within = relative[(Path.Length + 1)..];
        if (!Recursive && within.Contains('/')) return false;
        return structural || Patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern,
            System.IO.Path.GetFileName(within), comparison == StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class TrayTaskDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Executable { get; set; } = "";
    public string[] Arguments { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TrayTaskConfiguration))]
internal sealed partial class TrayTaskJsonContext : JsonSerializerContext;
