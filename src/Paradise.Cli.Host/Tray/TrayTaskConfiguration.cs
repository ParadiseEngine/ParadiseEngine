using System.IO.Enumeration;

namespace Paradise.Cli;

/// <summary>Validates and snapshots C# contributions before exposing them to native menus and watchers.</summary>
internal sealed class TrayTaskConfiguration
{
    public TrayTaskGroup[] Groups { get; }

    public TrayTaskConfiguration(IEnumerable<TrayTaskGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        Groups = groups.Select(Snapshot).ToArray();
        if (Groups.Length > 32) throw new InvalidDataException("Expected at most 32 tray task groups.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Groups)
        {
            if (!ids.Add(group.Id)) throw new InvalidDataException($"Duplicate tray task group '{group.Id}'.");
        }
    }

    public static TrayTaskConfiguration Register(IEnumerable<ITrayExtension> extensions,
        ITrayExtensionContext context, Action<string> error)
    {
        var groups = new List<TrayTaskGroup>();
        foreach (var extension in extensions)
        {
            try
            {
                var contribution = extension.CreateTaskGroups(context)
                    ?? throw new InvalidDataException("CreateTaskGroups returned null.");
                // Registration is all-or-nothing per extension, including duplicate IDs.
                var candidate = new TrayTaskConfiguration(groups.Concat(contribution));
                groups = [.. candidate.Groups];
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error($"watch: tray extension '{extension.GetType().FullName}' was skipped: {ex.Message}");
            }
        }
        return new(groups);
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':')
            || path.StartsWith('/') || path.Split('/').Any(part => part is ".." or "." or ""))
            throw new InvalidDataException($"Tray task path '{path}' must be a project-relative '/'-separated path without traversal.");
    }

    private static TrayTaskGroup Snapshot(TrayTaskGroup group)
    {
        if (group is null || string.IsNullOrWhiteSpace(group.Id) || string.IsNullOrWhiteSpace(group.Label))
            throw new InvalidDataException("Tray task groups require id and label.");
        if (group.DebounceMilliseconds is < 50 or > 60000)
            throw new InvalidDataException($"'{group.Id}' debounce must be 50–60000 milliseconds.");
        if (group.Tasks is null || group.Tasks.Count is 0 or > 32 || group.Inputs is null || group.Outputs is null)
            throw new InvalidDataException($"'{group.Id}' requires tasks, inputs and outputs collections.");
        var tasks = group.Tasks.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Label) || task.Execute is null)
                throw new InvalidDataException($"Invalid task in '{group.Id}'. Supply id, label and an Execute callback.");
            if (!ids.Add(task.Id)) throw new InvalidDataException($"Duplicate task '{task.Id}' in '{group.Id}'.");
        }
        if (!ids.Contains(group.AutoTask))
            throw new InvalidDataException($"'{group.Id}' autoTask '{group.AutoTask}' does not name a task.");
        var inputs = group.Inputs.Select(input =>
        {
            if (input is null) throw new InvalidDataException($"Invalid input in '{group.Id}'.");
            ValidatePath(input.Path);
            var patterns = input.Patterns?.ToArray() ?? [];
            if (patterns.Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Contains('/') || pattern.Contains('\\')))
                throw new InvalidDataException("Input patterns match filenames; use path and recursive to select directories.");
            return input with { Patterns = patterns };
        }).ToArray();
        var outputs = group.Outputs.ToArray();
        foreach (var output in outputs) ValidatePath(output);
        if (group.OpenDirectory is not null) ValidatePath(group.OpenDirectory);
        if (string.IsNullOrWhiteSpace(group.AutoWatchLabel) || string.IsNullOrWhiteSpace(group.OpenDirectoryLabel))
            throw new InvalidDataException("Auto-watch and open-directory labels must not be empty.");
        return group with { Inputs = inputs, Outputs = outputs, Tasks = tasks };
    }
}

internal static class TrayTaskInputs
{
    /// <summary>Structural events include both sides of directory renames and deleted directories.</summary>
    public static bool Observes(this TrayTaskGroup group, string relative, bool structural = false)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (relative.Split('/').Any(part => part is ".git" or ".editor" or "bin" or "obj")) return false;
        if (group.Outputs.Any(output => string.Equals(output, relative, comparison))) return false;
        return group.Inputs.Any(input => Observes(input, relative, structural, comparison));
    }

    private static bool Observes(TrayTaskInput input, string relative, bool structural, StringComparison comparison)
    {
        if (string.Equals(relative, input.Path, comparison)) return true;
        if (structural && input.Path.StartsWith(relative + "/", comparison)) return true;
        if (input.Patterns is not { Count: > 0 } || !relative.StartsWith(input.Path + "/", comparison)) return false;
        var within = relative[(input.Path.Length + 1)..];
        if (!input.Recursive && within.Contains('/')) return false;
        return structural || input.Patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern,
            Path.GetFileName(within), comparison == StringComparison.OrdinalIgnoreCase));
    }
}
