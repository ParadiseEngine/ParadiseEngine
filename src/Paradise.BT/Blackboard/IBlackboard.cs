namespace Paradise.BT;

/// <summary>
/// What a node may ask of the world: typed struct data, read by value and written back whole.
///
/// No member returns a <c>ref</c>, which is what makes a caller's INTENT legible — taking a ref to
/// avoid a copy and taking one to mutate looked identical, so nothing could tell a read from a
/// write. That split is what the access declarations and PBT0009 rest on.
/// </summary>
public interface IBlackboard
{
    /// <summary>False for an optional component this entity lacks.</summary>
    bool HasData<T>() where T : struct;

    T GetData<T>() where T : struct;

    /// <summary>
    /// Read-modify-write. For a struct of settable members a <c>with</c> expression says it in one
    /// line: <c>bb.SetData(bb.GetData&lt;Decision&gt;() with { Strike = true });</c>
    /// </summary>
    void SetData<T>(T value) where T : struct;
}
