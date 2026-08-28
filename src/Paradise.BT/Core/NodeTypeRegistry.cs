using System.Runtime.CompilerServices;

namespace Paradise.BT;

/// <summary>
/// Maps a node type's <c>[Guid]</c> onto a dense id, and that id onto something that can tick a
/// node of that type given nothing but a <c>ref byte</c> to its data.
///
/// <b>This is what lets a behavior tree instance be UNMANAGED.</b> The managed
/// <see cref="NodeBlob"/> stores one boxed <c>RuntimeNode&lt;TNodeData&gt;</c> per node and
/// dispatches through <c>IRuntimeNode</c> — so its per-instance state is an object graph, which
/// cannot live in an ECS component, be memcpy'd into a snapshot, or be held by a ref struct.
/// <see cref="UnmanagedNodeBlob"/> stores the same node data as plain bytes and asks this table
/// which type those bytes are, which moves the only managed thing left — the knowledge of how to
/// call <c>T.Tick</c> — OUT of the instance and into one process-wide table.
///
/// <b>The table being managed and static is not a contradiction.</b> The constraint an ECS
/// component (or a <c>ref partial struct</c> system) imposes is on its own FIELDS; static managed
/// data is reachable from both. This is a vtable, in the same sense the CLR's own is: one entry
/// per node TYPE, never per node instance and never per entity.
///
/// <b>Append-only and idempotent.</b> Registering the same type twice returns the same id, so
/// registration can live wherever it is convenient — a static constructor, a game's startup, a
/// test — without callers coordinating. Two different types claiming one GUID is a programming
/// error and throws, exactly as <see cref="BehaviorTreeSerializationRegistry.Register{T}"/>
/// already does.
///
/// Ids are dense and assigned in registration ORDER, which is deliberately NOT a stable identity:
/// the same tree registered in a different order gets different ids in a different process. The
/// stable identity is the GUID, and it is what a serialized blob carries — an id is resolved
/// through <see cref="TryGetId"/> when a <see cref="BehaviorTreeLayout"/> is built, and is
/// meaningful only for the lifetime of that process. Do not persist one.
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
    /// Register a node type and return its dense id, or return the id it already has.
    ///
    /// The <c>unmanaged</c> constraint is the whole contract: a node holding a reference cannot
    /// have its data stored as bytes, and would drag the instance back into the managed world.
    /// It is the same constraint <see cref="BehaviorTreeSerializationRegistry.Register{T}"/>
    /// carries, and for the same underlying reason — which is why the delegate-backed helper
    /// nodes are registrable with neither.
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
        // Read without the lock: the array is append-only and an id in hand was already assigned,
        // so the slot is populated and never rewritten. The only hazard is reading _invokers
        // mid-resize, which Volatile.Read of the reference rules out — Array.Resize publishes a
        // new array, it does not mutate the old one.
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

/// <summary>
/// Ticks one node type given its data as bytes. One implementation per node type, one INSTANCE
/// per node type for the whole process — see <see cref="NodeTypeRegistry"/> for why a managed
/// object here does not make the tree instance managed.
/// </summary>
internal interface INodeInvoker
{
    Type NodeType { get; }

    int Size { get; }

    NodeState Tick<TNodeBlob, TBlackboard>(
        ref byte data, int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard;

    void Reset<TNodeBlob, TBlackboard>(
        ref byte data, int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard;
}

internal sealed class NodeInvoker<TNodeData> : INodeInvoker
    where TNodeData : unmanaged, INodeData
{
    public static readonly NodeInvoker<TNodeData> Instance = new();

    public Type NodeType => typeof(TNodeData);

    public int Size => Unsafe.SizeOf<TNodeData>();

    /// <summary>
    /// <b>The node is ticked THROUGH the bytes, not on a copy of them.</b>
    /// <see cref="Unsafe.As{TFrom,TTo}(ref TFrom)"/> reinterprets the blob's storage in place, so
    /// a node that writes its own fields — <c>DelayTimerNode.TimerSeconds -=</c> is the one every
    /// timer is built on — writes into the instance's runtime data and the value is there on the
    /// next tick. Binding it to a local first would compile, run, and silently reset every timer
    /// in the tree every frame.
    /// </summary>
    public NodeState Tick<TNodeBlob, TBlackboard>(
        ref byte data, int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => Unsafe.As<byte, TNodeData>(ref data).Tick(index, ref blob, ref bb);

    /// <summary>
    /// Through the TYPE. <c>Reset</c> is static on <see cref="INodeData"/>, so this needs no
    /// receiver at all — the <c>ref byte</c> is still in the signature because the interface is
    /// shared with <see cref="Tick"/>, which very much does need it.
    /// </summary>
    public void Reset<TNodeBlob, TBlackboard>(
        ref byte data, int index, ref TNodeBlob blob, ref TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard
        => TNodeData.Reset(index, ref blob, ref bb);
}
