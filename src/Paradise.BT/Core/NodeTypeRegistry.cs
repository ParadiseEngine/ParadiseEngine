using System.Runtime.CompilerServices;

namespace Paradise.BT;

/// <summary>
/// Maps a node type's <c>[Guid]</c> to an invoker that ticks the node from a <c>ref byte</c>. This
/// is what replaces <c>IRuntimeNode</c> for byte-backed blobs, and so what lets an instance be
/// unmanaged.
///
/// The table is static and managed on purpose: it is a vtable, one entry per node TYPE, so it
/// never becomes per-instance state. Append-only and idempotent.
///
/// The GUID is the whole identity — the same one a blob carries — so dispatch resolves a node
/// straight from its serialized form, with no process-local id in between. Readers never lock:
/// registration copies the map and publishes the new one, so a map a reader holds is immutable.
/// </summary>
public static class NodeTypeRegistry
{
    private static readonly Lock Gate = new();
    private static Dictionary<Guid, INodeInvoker> _invokers = new();

    /// <summary>How many node types have been registered so far.</summary>
    public static int Count => Volatile.Read(ref _invokers).Count;

    /// <summary>
    /// Register a node type. Idempotent — registering the same type again is a no-op.
    ///
    /// <c>unmanaged</c> is the contract: a node holding a reference cannot be stored as bytes.
    /// The delegate-backed nodes therefore cannot be registered here, nor for serialization.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another type already claims this GUID.</exception>
    public static void Register<TNodeData>()
        where TNodeData : unmanaged, INodeData
    {
        Guid guid = typeof(TNodeData).GetNodeGuid();
        lock (Gate)
        {
            if (_invokers.TryGetValue(guid, out INodeInvoker? existing))
            {
                if (existing.NodeType != typeof(TNodeData))
                {
                    throw new InvalidOperationException(
                        $"Node GUID '{guid}' is already registered for "
                        + $"'{existing.NodeType.FullName}' and cannot also name "
                        + $"'{typeof(TNodeData).FullName}'.");
                }

                return;
            }

            // Copy-on-write: readers dispatch against whatever map they loaded, which is never
            // mutated after publish — the lock covers writers only.
            var next = new Dictionary<Guid, INodeInvoker>(_invokers)
            {
                [guid] = NodeInvoker<TNodeData>.Instance,
            };
            Volatile.Write(ref _invokers, next);
        }
    }

    /// <summary>Whether anybody registered this GUID.</summary>
    public static bool IsRegistered(Guid guid) => Volatile.Read(ref _invokers).ContainsKey(guid);

    /// <summary>How many bytes one node of this type occupies in a blob's runtime data.</summary>
    public static int SizeOf(Guid guid) => Invoker(guid).Size;

    /// <summary>
    /// A managed factory over a serialized node's default data — what
    /// <see cref="BehaviorTreeBlobSerializer"/> rebuilds a <see cref="BehaviorTree"/> from. Lives
    /// here because this table already knows every node type by GUID.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nobody registered this GUID.</exception>
    internal static IRuntimeNodeFactory CreateFactory(Guid guid, ref BehaviorTreeBlobNode node)
    {
        if (!Volatile.Read(ref _invokers).TryGetValue(guid, out INodeInvoker? invoker))
        {
            throw new InvalidOperationException(
                $"Node GUID '{guid}' is not registered for behavior tree deserialization. Node "
                + "types register themselves through the module initializer the BT generator "
                + $"emits; call {nameof(NodeTypeRegistry)}.{nameof(Register)}<T>() explicitly "
                + "for a node type the generator cannot see.");
        }

        return invoker.CreateFactory(ref node);
    }

    /// <summary>The CLR type behind a GUID — for diagnostics, never for dispatch.</summary>
    public static Type TypeOf(Guid guid) => Invoker(guid).NodeType;

    internal static INodeInvoker Invoker(Guid guid)
    {
        if (!Volatile.Read(ref _invokers).TryGetValue(guid, out INodeInvoker? invoker))
        {
            throw new InvalidOperationException(
                $"Node GUID '{guid}' is not registered. Every node type in a tree must be passed "
                + $"to {nameof(NodeTypeRegistry)}.{nameof(Register)}<T>() before a "
                + $"{nameof(BehaviorTreeLayout)} is built or loaded from it.");
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
        scoped ref byte data, int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct;

    void Reset<TNodeBlob, TBlackboard>(
        scoped ref byte data, int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct;

    /// <summary>See <see cref="NodeTypeRegistry.CreateFactory"/>.</summary>
    IRuntimeNodeFactory CreateFactory(ref BehaviorTreeBlobNode node);
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
        scoped ref byte data, int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => Unsafe.As<byte, TNodeData>(ref data).Tick(index, blob, bb);

    /// <summary>Through the TYPE: <c>Reset</c> is static, so no receiver is needed. The
    /// <c>ref byte</c> stays only because <see cref="Tick"/> shares the signature.</summary>
    public void Reset<TNodeBlob, TBlackboard>(
        scoped ref byte data, int index, TNodeBlob blob, TBlackboard bb)
        where TNodeBlob : struct, INodeBlob, allows ref struct
        where TBlackboard : struct, IBlackboard, allows ref struct
        => TNodeData.Reset(index, blob, bb);

    public IRuntimeNodeFactory CreateFactory(ref BehaviorTreeBlobNode node)
    {
        TNodeData defaultData = node.DefaultData.GetValue<TNodeData>();
        return new RuntimeNodeFactory<TNodeData>(
            defaultData, new BehaviorNodeMetadata(typeof(TNodeData)));
    }
}
