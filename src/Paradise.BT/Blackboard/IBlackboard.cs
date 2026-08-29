using System.Diagnostics.CodeAnalysis;

namespace Paradise.BT;

/// <summary>
/// What a node may ask of the world: typed struct data, by value or by reference.
///
/// Deliberately three members. The EntitiesBT original also carried <c>HasData(Type)</c>,
/// <c>GetDataPtrRO/RW(Type)</c> and <c>GetObject&lt;T&gt;</c> — reflection- and pointer-shaped
/// members that no node ever called, and that an implementation storing its data as ref fields
/// cannot honour. <c>Paradise.BT.Nodes.Blackboard</c> still offers them as its own methods.
/// </summary>
public interface IBlackboard
{
    /// <summary>Whether <typeparamref name="T"/> is reachable — false for an optional component
    /// this entity lacks.</summary>
    bool HasData<T>() where T : struct;

    T GetData<T>() where T : struct;

    /// <summary>
    /// A reference to the stored value, so a node can write through it.
    ///
    /// <c>[UnscopedRef]</c> is what allows an INLINE implementation. One storing its data as its
    /// own fields returns a ref to itself, which a struct member may not do by default (CS8170)
    /// and cannot opt into unless the interface has (CS9102). The reference implementation keeps
    /// its data behind a class and is unaffected.
    /// </summary>
    [UnscopedRef]
    ref T GetDataRef<T>() where T : struct;
}
