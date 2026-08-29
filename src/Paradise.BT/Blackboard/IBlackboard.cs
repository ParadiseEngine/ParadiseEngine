namespace Paradise.BT;

/// <summary>
/// What a node may ask of the world: typed struct data, read by value and written back whole.
///
/// Three members, and none of them returns a <c>ref</c>. That is deliberate on both counts. The
/// EntitiesBT original also carried <c>HasData(Type)</c>, <c>GetDataPtrRO/RW(Type)</c> and
/// <c>GetObject&lt;T&gt;</c> — reflection- and pointer-shaped members no node ever called;
/// <c>Paradise.BT.Nodes.Blackboard</c> still offers them as its own methods.
///
/// The ref-returning <c>GetDataRef&lt;T&gt;</c> is gone too, and it took two problems with it.
/// It needed <c>[UnscopedRef]</c> so an implementation storing its data in its own fields could
/// return a ref to itself (CS8170/CS9102); nothing here returns a ref now, so that is moot. And it
/// could not say whether a caller meant to READ or to WRITE — taking a ref to avoid a copy looks
/// exactly like taking one to mutate. <see cref="GetData{T}"/> reads and <see cref="SetData{T}"/>
/// writes, which is what lets a node's declared access be checked against what it actually does.
/// </summary>
public interface IBlackboard
{
    /// <summary>Whether <typeparamref name="T"/> is reachable — false for an optional component
    /// this entity lacks.</summary>
    bool HasData<T>() where T : struct;

    T GetData<T>() where T : struct;

    /// <summary>
    /// Store <typeparamref name="T"/> back.
    ///
    /// Read-modify-write rather than mutation in place. For a struct of settable members a
    /// <c>with</c> expression says it in one line:
    /// <code>
    /// bb.SetData(bb.GetData&lt;Decision&gt;() with { Strike = true });
    /// </code>
    /// </summary>
    void SetData<T>(T value) where T : struct;
}
