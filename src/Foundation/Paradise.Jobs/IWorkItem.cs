namespace Paradise.Jobs;

/// <summary>A self-contained unit of work that a pool can run on any of its threads.</summary>
/// <remarks>Implement it on a struct carrying everything the work needs, so dispatch allocates nothing.</remarks>
public interface IWorkItem
{
    /// <summary>Executes this work item.</summary>
    void Invoke();
}
