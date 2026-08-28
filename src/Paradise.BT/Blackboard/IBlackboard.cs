using System.Diagnostics.CodeAnalysis;

namespace Paradise.BT;

/// <summary>
/// Exact blackboard contract used by EntitiesBT-style nodes.
/// </summary>
public interface IBlackboard
{
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

    bool HasData(Type type);

    IntPtr GetDataPtrRO(Type type);

    IntPtr GetDataPtrRW(Type type);

    T GetObject<T>() where T : class;
}
