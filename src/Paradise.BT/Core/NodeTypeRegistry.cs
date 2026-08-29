using System.Runtime.CompilerServices;

namespace Paradise.BT;

/// <summary>
/// Maps a node type's <c>[Guid]</c> to a dense id, and that id to an invoker that ticks the node
/// from a <c>ref byte</c>. This is what replaces <c>IRuntimeNode</c> for byte-backed blobs, and so
/// what lets an instance be unmanaged.
///
/// The table is static and managed on purpose: it is a vtable, one entry per node TYPE, so it
/// never becomes per-instance state. Append-only and idempotent.
///
/// Ids are assigned in registration order and are NOT stable across processes — the GUID is the
/// identity a blob carries. Do not persist an id.
/// </summary>
public static class NodeTypeRegistry
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<Guid, int> Ids = new();
    private static INodeInvoker[] _invokers = [];
    private static int _count;

    /// <summary>How many node types have been registered so far.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Register a node type and return its dense id, or the id it already has.
    ///
    /// <c>unmanaged</c> is the contract: a node holding a reference cannot be stored as bytes.
    /// The delegate-backed nodes therefore cannot be registered here, nor for serialization.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another type already claims this GUID.</exception>
    public static int Register<TNodeData>()
        where TNodeData : unmanaged, INodeData
    {
        Guid guid = typeof(TNodeData).GetNodeGuid();
        lock (Gate)
        {
            if (Ids.TryGetValue(guid, out int existing))
            {
                if (_invokers[existing].NodeType != typeof(TNodeData))
                {
                    throw new InvalidOperationException(
                        $"Node GUID '{guid}' is already registered for "
                        + $"'{_invokers[existing].NodeType.FullName}' and cannot also name "
                        + $"'{typeof(TNodeData).FullName}'.");
                }

                return existing;
            }

            if (_count == _invokers.Length)
            {
                Array.Resize(ref _invokers, _count == 0 ? 8 : _count * 2);
            }

            int id = _count++;
            _invokers[id] = NodeInvoker<TNodeData>.Instance;
            Ids[guid] = id;
            return id;
        }
    }

    /// <summary>The id a GUID was registered under, or false if nobody registered it.</summary>
    public static bool TryGetId(Guid guid, out int id)
    {
        lock (Gate)
        {
            return Ids.TryGetValue(guid, out id);
        }
    }

    /// <summary>How many bytes one node of this type occupies in a blob's runtime data.</summary>
    public static int SizeOf(int id) => Invoker(id).Size;

    /// <summary>The CLR type behind an id — for diagnostics, never for dispatch.</summary>
    public static Type TypeOf(int id) => Invoker(id).NodeType;

    internal static INodeInvoker Invoker(int id)
    {
        // No lock: the array is append-only, so a slot for an id in hand is already populated.
        // Volatile.Read covers a concurrent resize — Array.Resize publishes a new array.
        INodeInvoker[] invokers = Volatile.Read(ref _invokers);
        if ((uint)id >= (uint)invokers.Length || invokers[id] is not { } invoker)
        {
            throw new InvalidOperationException(
                $"Node type id {id} is not registered. Every node type in a tree must be passed "
                + $"to {nameof(NodeTypeRegistry)}.{nameof(Register)}<T>() before a "
                + $"{nameof(BehaviorTreeLayout)} is built from it.");
        }

        return invoker;
    }
}

/// <summary>Ticks one node type from its data as bytes. One instance per node type, process-wide
/// — see <see cref="NodeTypeRegistry"/>.</summary>
internal interface INodeInvoker
{
    Type NodeType { get; }

    int Size { get; }

    NodeState Tick<TNodeBlob, TBlackboard>(
        scoped ref byte data, int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct;

    void Reset<TNodeBlob, TBlackboard>(
        scoped ref byte data, int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct;
}

internal sealed class NodeInvoker<TNodeData> : INodeInvoker
    where TNodeData : unmanaged, INodeData
{
    public static readonly NodeInvoker<TNodeData> Instance = new();

    public Type NodeType => typeof(TNodeData);

    public int Size => Unsafe.SizeOf<TNodeData>();

    /// <summary>Ticks THROUGH the bytes: <c>Unsafe.As</c> reinterprets the blob's storage in
    /// place, so a node writing its own fields (every timer) persists to the next tick. Binding to
    /// a local first would compile and silently reset every timer each frame.</summary>
    public NodeState Tick<TNodeBlob, TBlackboard>(
        scoped ref byte data, int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => Unsafe.As<byte, TNodeData>(ref data).Tick(index, ref blob, ref bb);

    /// <summary>Through the TYPE: <c>Reset</c> is static, so no receiver is needed. The
    /// <c>ref byte</c> stays only because <see cref="Tick"/> shares the signature.</summary>
    public void Reset<TNodeBlob, TBlackboard>(
        scoped ref byte data, int index, scoped ref TNodeBlob blob, scoped ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => TNodeData.Reset(index, ref blob, ref bb);
}
