namespace Paradise.ECS.Jobs.Test;

/// <summary>
/// Work item that wraps a delegate for testing.
/// </summary>
internal readonly struct DelegateWorkItem(Action action) : IWorkItem
{
    public void Invoke() => action();

    /// <summary>
    /// Creates a list of work items that call <paramref name="action"/> with indices 0 to <paramref name="count"/>-1.
    /// </summary>
    public static List<DelegateWorkItem> Create(int count, Action<int> action)
    {
        var items = new List<DelegateWorkItem>(count);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            items.Add(new DelegateWorkItem(() => action(index)));
        }
        return items;
    }
}
