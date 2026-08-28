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
    /// <b><c>[UnscopedRef]</c> is what makes an INLINE blackboard possible at all.</b> The
    /// reference implementation keeps its data in a class behind a struct facade, so the ref it
    /// returns points at the heap and no escape rule is troubled. An implementation that stores
    /// its data as its own fields — the whole point of a blackboard for an unmanaged tree — is
    /// returning a ref to itself, which a struct member may not do by default (CS8170), and it
    /// cannot opt in unless the interface member has already done so (CS9102). Declaring it here
    /// costs existing implementations nothing: they simply do not use it.
    /// </summary>
    [UnscopedRef]
    ref T GetDataRef<T>() where T : struct;

    bool HasData(Type type);

    IntPtr GetDataPtrRO(Type type);

    IntPtr GetDataPtrRW(Type type);

    T GetObject<T>() where T : class;
}
