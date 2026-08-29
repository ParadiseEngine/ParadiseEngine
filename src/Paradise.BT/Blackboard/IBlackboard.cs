namespace Paradise.BT;

/// <summary>
/// What a node may ask of the world: typed struct data, read by value and written back whole.
///
/// No member returns a <c>ref</c>, which is what makes a caller's INTENT legible — taking a ref to
/// avoid a copy and taking one to mutate looked identical, so nothing could tell a read from a
/// write. That split is what the access declarations and PBT0009 rest on.
///
/// <b>An implementation must be handle-shaped.</b> Blackboards travel BY VALUE through the VM
/// (the ref-safety rules force it — see <see cref="VirtualMachine"/>), so a copy must write to
/// the same storage: hold a class reference, refs, or a pointer — never the data itself. A struct
/// storing its data inline loses every <see cref="SetData{T}"/> silently. The same rule
/// <see cref="INodeBlob"/> documents for blobs.
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
