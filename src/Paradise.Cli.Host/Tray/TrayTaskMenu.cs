namespace Paradise.Cli;

/// <summary>A generic parent menu and its second-level entries, rendered by the existing native tray.</summary>
internal sealed record TrayTaskMenu(string Label, IReadOnlyList<TrayTaskMenuItem> Items);

internal sealed record TrayTaskMenuItem(Func<string> Label, Action? Click = null,
    Func<bool>? Enabled = null, Func<bool>? Checked = null, bool Separator = false)
{
    public bool IsEnabled => Click is not null && (Enabled?.Invoke() ?? true);

    public void Invoke()
    {
        // A popup can stay open while a task starts on the worker. Recheck before dispatch.
        if (IsEnabled) Click?.Invoke();
    }

    public static IReadOnlyList<TrayTaskMenuItem> For(TrayTaskGroup group, TrayTaskState state, Action? open)
    {
        var items = new List<TrayTaskMenuItem>
        {
            new(() => group.AutoWatchLabel, state.ToggleAutoWatch, () => state.Snapshot.WatchAvailable, () => state.Snapshot.AutoWatch),
        };
        items.AddRange(group.Tasks.Select(task => new TrayTaskMenuItem(() => task.Label,
            () => state.Request(task.Id), () => !state.Snapshot.Running)));
        items.Add(new(() => "Cancel Running Task", state.Cancel, () => state.Snapshot.Running && !state.Snapshot.CancelRequested));
        items.Add(new(() => "", Separator: true));
        items.Add(new(() => state.Snapshot.Status));
        if (open is not null) items.Add(new(() => group.OpenDirectoryLabel, open));
        return items;
    }
}
